using System;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 public static partial class OfflineTests {
  sealed class FailingQueryTransport:IBenchTransport {
   public readonly TaskCompletionSource<bool> Stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
   public async Task<TlsInfo> HandshakeAsync(ProfileSpec p,string server,int timeout,CancellationToken ct) {
    try { await Task.Delay(System.Threading.Timeout.Infinite,ct);return new TlsInfo(); }
    finally { Stopped.TrySetResult(true); }
   }
   public Task<Reply> QueryAsync(ProfileSpec p,string server,string host,int type,int timeout,CancellationToken ct)=>throw new InvalidOperationException("injected query failure");
   public Task<TlsInfo> AnswerTlsAsync(ProfileSpec p,string ip,string host,int timeout,CancellationToken ct)=>Task.FromResult(new TlsInfo{Ok=true});
  }
  sealed class ControlledBenchTransport:IBenchTransport {
   public int Active,Started,Completed;
   readonly string failure;
   public ControlledBenchTransport(string failure=null){this.failure=failure;}
   async Task Wait(ProfileSpec p,CancellationToken ct) {
    Interlocked.Increment(ref Active);Interlocked.Increment(ref Started);
    try { if(p.Id.StartsWith("hang"))await Task.Delay(System.Threading.Timeout.Infinite,ct); }
    finally { Interlocked.Increment(ref Completed);Interlocked.Decrement(ref Active); }
   }
   public async Task<TlsInfo> HandshakeAsync(ProfileSpec p,string server,int timeout,CancellationToken ct) {
    await Wait(p,ct);return new TlsInfo{Ok=failure==null,Error=failure};
   }
   public async Task<Reply> QueryAsync(ProfileSpec p,string server,string host,int type,int timeout,CancellationToken ct) {
    await Wait(p,ct);return new Reply{Code=failure??"DNS_OK",Addresses=failure==null?new[]{"1.1.1.1"}:Array.Empty<string>(),Milliseconds=20};
   }
   public async Task<TlsInfo> AnswerTlsAsync(ProfileSpec p,string ip,string host,int timeout,CancellationToken ct) {
    await Wait(p,ct);return new TlsInfo{Ok=true};
   }
  }
  sealed class MixedServerTransport:IBenchTransport {
   public Task<TlsInfo> HandshakeAsync(ProfileSpec p,string server,int timeout,CancellationToken ct)=>Task.FromResult(server=="9.9.9.9"?new TlsInfo{Ok=false,Error="DOH_TLS_FAILURE"}:new TlsInfo{Ok=true});
   public Task<Reply> QueryAsync(ProfileSpec p,string server,string host,int type,int timeout,CancellationToken ct)=>Task.FromResult(server=="9.9.9.9"?new Reply{Code="DOH_TLS_FAILURE",Addresses=Array.Empty<string>()}:new Reply{Code="DNS_OK",Addresses=new[]{"1.1.1.1"},Milliseconds=20});
   public Task<TlsInfo> AnswerTlsAsync(ProfileSpec p,string ip,string host,int timeout,CancellationToken ct)=>Task.FromResult(new TlsInfo{Ok=true});
  }
  static void RunStage02BenchmarkTests() {
   Task.Run(async()=>{
    var transport=new FailingQueryTransport();
    var profile=new ProfileSpec{Id="failure",Template="https://failure.test/dns-query",Servers=new[]{"1.1.1.1"}};
    var result=await Benchmark.RunAsync(new[]{profile},new BenchOptions(),transport,null,default,true).WaitAsync(TimeSpan.FromSeconds(20));
    Check(result.Winner==null&&!result.Results.Single().Selectable,"benchmark: an unexpected query failure never becomes eligible");
    Check(transport.Stopped.Task.IsCompleted,"benchmark: failed query cancels its parallel TLS probe before the run finishes");
    ProfileSpec Spec(string id)=>new ProfileSpec{Id=id,Template="https://"+id+".test/dns-query",Servers=new[]{"1.1.1.1"}};
    foreach(string failure in new[]{"DOH_TLS_FAILURE","DOH_INVALID_HTTP_RESPONSE_403","DOH_UNREACHABLE"}) {
     var fast=new ControlledBenchTransport(failure);var timer=Stopwatch.StartNew();
     var failed=await Benchmark.RunAsync(Enumerable.Range(0,11).Select(i=>Spec("failed"+i)),new BenchOptions{AttemptGapMs=0},fast,null,default,true).WaitAsync(TimeSpan.FromSeconds(20));
     Check(failed.Winner==null&&failed.Results.All(r=>!r.Selectable&&r.Detail.Contains(failure))&&fast.Active==0&&timer.ElapsedMilliseconds<2000,"benchmark: all fast failures preserve "+failure+" and drain transport work ("+timer.ElapsedMilliseconds+" ms)");
    }
    var mixedProfile=new ProfileSpec{Id="mixed",Template="https://mixed.test/dns-query",Servers=new[]{"9.9.9.9","1.1.1.1","8.8.8.8"}};
    var mixedRun=await Benchmark.RunAsync(new[]{mixedProfile},new BenchOptions{AttemptGapMs=0},new MixedServerTransport(),null,default,true).WaitAsync(TimeSpan.FromSeconds(20));
    Check(mixedRun.Results.Single().Selectable&&mixedRun.Winner=="mixed","benchmark: one rejected server does not reject the profile's healthy servers (selectable="+mixedRun.Results.Single().Selectable+")");
    for(int sample=0;sample<3;sample++) {
     var tracked=new ControlledBenchTransport();var timer=Stopwatch.StartNew();
     var mixed=await Benchmark.RunAsync(new[]{Spec("healthy")}.Concat(Enumerable.Range(0,10).Select(i=>Spec("hang"+i))),new BenchOptions{AttemptGapMs=0},tracked,null,default,true).WaitAsync(TimeSpan.FromSeconds(20));
     // Cancellation continuations may finish in the same scheduler turn as the wait; verify they all drain.
     for(int drain=0;drain<100&&Volatile.Read(ref tracked.Active)>0;drain++)await Task.Delay(5);
     Check(mixed.Winner=="healthy"&&mixed.Truncated&&mixed.Results.Count(r=>Benchmark.CanSelect(r))==1&&timer.ElapsedMilliseconds>=9500&&timer.ElapsedMilliseconds<11500&&tracked.Active==0&&tracked.Started==tracked.Completed,"benchmark: 1 healthy + 10 stalled, sample "+sample+", wall="+timer.ElapsedMilliseconds+" ms; active="+tracked.Active+" started="+tracked.Started+" completed="+tracked.Completed+" winner="+mixed.Winner+" eligible="+mixed.Results.Count(r=>Benchmark.CanSelect(r))+" truncated="+mixed.Truncated);
    }
    var cancelledTransport=new ControlledBenchTransport();using var stop=new CancellationTokenSource(3000);var cancelTime=Stopwatch.StartNew();
    var cancelled=await Benchmark.RunAsync(new[]{Spec("hang-cancel")},new BenchOptions(),cancelledTransport,null,stop.Token,true).WaitAsync(TimeSpan.FromSeconds(20));
    await Task.Delay(20);
    Check(cancelled.Cancelled&&!cancelled.Truncated&&cancelled.Winner==null&&cancelTime.ElapsedMilliseconds>=2900&&cancelTime.ElapsedMilliseconds<4000&&cancelledTransport.Active==0,"benchmark: explicit cancellation at 3 s drains owned probes ("+cancelTime.ElapsedMilliseconds+" ms)");
   }).GetAwaiter().GetResult();
  }
 }
}
