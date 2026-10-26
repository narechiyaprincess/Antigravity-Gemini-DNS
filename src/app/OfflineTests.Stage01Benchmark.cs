using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 public static partial class OfflineTests {
  sealed class BenchmarkClock:TimeProvider {
   readonly List<DeadlineTimer> timers=new List<DeadlineTimer>();
   public override ITimer CreateTimer(TimerCallback callback,object state,TimeSpan dueTime,TimeSpan period) {
    var timer=new DeadlineTimer(callback,state);lock(timers)timers.Add(timer);return timer;
   }
   public void Expire(){DeadlineTimer[] copy;lock(timers)copy=timers.ToArray();foreach(var timer in copy)timer.Fire();}
   sealed class DeadlineTimer:ITimer {
    readonly TimerCallback callback;readonly object state;bool disposed;
    public DeadlineTimer(TimerCallback callback,object state){this.callback=callback;this.state=state;}
    public bool Change(TimeSpan dueTime,TimeSpan period)=>!disposed;
    public void Fire(){if(!disposed)callback(state);}
    public void Dispose(){disposed=true;}
    public ValueTask DisposeAsync(){Dispose();return default;}
   }
  }
  sealed class WaitingBenchTransport:IBenchTransport {
   public readonly TaskCompletionSource<bool> Started=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
   public bool ObservedCancellation;
   public Task<TlsInfo> HandshakeAsync(ProfileSpec p,string server,int timeout,CancellationToken ct)=>Task.FromResult(new TlsInfo{Ok=true});
   public async Task<Reply> QueryAsync(ProfileSpec p,string server,string host,int type,int timeout,CancellationToken ct) {
    using var registration=ct.Register(()=>ObservedCancellation=true);
    Started.TrySetResult(true);
    try{await Task.Delay(System.Threading.Timeout.Infinite,ct);}catch(OperationCanceledException){ObservedCancellation=true;throw;}
    return null;
   }
   public Task<TlsInfo> AnswerTlsAsync(ProfileSpec p,string ip,string host,int timeout,CancellationToken ct)=>Task.FromResult(new TlsInfo{Ok=true});
  }
  sealed class WaitingHttpHandler:HttpMessageHandler {
   protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
    await Task.Delay(System.Threading.Timeout.Infinite,ct);return null;
   }
  }
  static void RunStage01BenchmarkTests() {
   RunStage02BenchmarkTests();
   Task.Run(async()=>{
    var clock=new BenchmarkClock();var transport=new WaitingBenchTransport();
    var p=new ProfileSpec{Id="waiting",Template="https://waiting.test/dns-query",Servers=new[]{"1.1.1.1"}};
    var pending=Benchmark.RunAsync(new[]{p},new BenchOptions{Clock=clock},transport,null,default,true);
    await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));clock.Expire();
    var result=await pending.WaitAsync(TimeSpan.FromSeconds(2));
    Check(result.Truncated&&!result.Cancelled&&result.Results.Single().Verdict=="NotMeasured"&&result.Winner==null,"benchmark: fake deadline cancels a never-answering probe without a real 10 s wait");
    Check(result.FirstResultMs>=0&&result.FirstResultMs<=result.LastResultMs&&result.LastResultMs<=result.DurationMs,"benchmark: monotonic first/last result timings fit within total run duration");
    Check(transport.ObservedCancellation,"benchmark: the deadline reaches the actual transport operation");
    var empty=await Benchmark.RunAsync(Array.Empty<ProfileSpec>(),new BenchOptions(),transport,null,default,true);
    Check(empty.Winner==null&&empty.Results.Length==0,"benchmark: an empty ranking has no winner");
    var bad=await Benchmark.RunAsync(new[]{new ProfileSpec{Id="bad",Template="http://bad.test",Servers=Array.Empty<string>()}},new BenchOptions(),transport,null,default,true);
    Check(bad.Winner==null&&bad.Results.Single().Detail=="INVALID_CONFIG"&&!bad.Results.Single().Selectable,"benchmark: invalid provider configuration ends immediately with a local reason");
    using(var cancelled=new CancellationTokenSource()) {
     cancelled.Cancel();var r=await Benchmark.RunAsync(new[]{p},new BenchOptions(),transport,null,cancelled.Token,true);
     Check(r.Cancelled&&!r.Truncated&&!r.Results.Single().Selectable,"benchmark: explicit cancellation stays distinct from deadline exhaustion");
    }
    using(var client=new HttpClient(new WaitingHttpHandler()){Timeout=System.Threading.Timeout.InfiniteTimeSpan}) {
     var timeout=await Wire.DohAsync(client,"https://waiting.test/dns-query",Program.Target,1,80,default);
     Check(timeout.Code=="DOH_TIMEOUT","benchmark: a non-answering HTTP transport keeps its request-timeout classification");
     using(var stop=new CancellationTokenSource(80)) {
      bool stopped=false;try{await Wire.DohAsync(client,"https://waiting.test/dns-query",Program.Target,1,5000,stop.Token);}catch(OperationCanceledException){stopped=true;}
      Check(stopped,"benchmark: explicit cancellation reaches HTTP send and is not reported as timeout");
     }
    }
    var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
    try {
     var accept=listener.AcceptTcpClientAsync();var sw=Stopwatch.StartNew();
     using(var stop=new CancellationTokenSource(120)) {
      var stalled=await Probing.TlsAsync("127.0.0.1",((IPEndPoint)listener.LocalEndpoint).Port,"waiting.test",5000,stop.Token);
      using var peer=await accept.WaitAsync(TimeSpan.FromSeconds(2));
      Check(!stalled.Ok&&stalled.Error=="CANCELLED"&&sw.ElapsedMilliseconds<1500,"benchmark: a real loopback TCP peer that stalls TLS is cancelled at socket I/O");
     }
    }finally{listener.Stop();}
   }).GetAwaiter().GetResult();
   var now=DateTime.UtcNow;var fresh=new BenchRun{StartedUtc=now.ToString("o"),CatalogVersion=ProfileCatalog.BuiltIn.Version,Targets=Program.Hosts.ToArray()};
   Check(Benchmark.IsFresh(fresh,now)&&!Benchmark.IsFresh(fresh,now+Benchmark.Fresh),"benchmark: cache freshness uses the injected clock and expires at its boundary");
   Check(!Benchmark.IsFresh(fresh,now-TimeSpan.FromSeconds(1))&&!Benchmark.IsFresh(fresh,now,new[]{"another.test"}),"benchmark: future and different-scope results cannot be reused");
   fresh.CatalogVersion="obsolete";Check(!Benchmark.IsFresh(fresh,now),"benchmark: catalog changes invalidate measured evidence");
   var partial=new ProfileResult{Final=true,Truncated=true,Selectable=true,Attempts=6,Successes=6,MedianMs=20,DomainsOk=true};
   Check(!Benchmark.CanSelect(partial),"benchmark: a truncated result cannot become selectable even with copied success flags");
   Check(Wire.HttpFailure(new HttpRequestException(HttpRequestError.SecureConnectionError))=="DOH_TLS_FAILURE"&&
    Wire.HttpFailure(new HttpRequestException(HttpRequestError.ConnectionError))=="DOH_UNREACHABLE"&&
    Wire.HttpFailure(new HttpRequestException(HttpRequestError.HttpProtocolError))=="DOH_PROTOCOL_FAILURE","benchmark: TLS, network and HTTP protocol errors retain distinct classifications");
   bool bootstrap=false;
   try{AddressResolver.ResolveAsync("https://waiting.test/dns-query",new[]{"1.1.1.1"},(_,__)=>throw new OperationCanceledException(),_=>{bootstrap=true;return Task.FromResult(Array.Empty<string>());}).GetAwaiter().GetResult();}
   catch(OperationCanceledException){}
   Check(!bootstrap,"connect: cancelled preflight never launches bootstrap or fallback");
   var filtered=AddressResolver.ResolveAsync("https://waiting.test/dns-query",new[]{"1.1.1.1","8.8.8.8"},(_,__)=>Task.FromResult(new[]{"1.1.1.1","9.9.9.9"}),_=>Task.FromResult(Array.Empty<string>())).GetAwaiter().GetResult();
   Check(filtered.Servers.SequenceEqual(new[]{"1.1.1.1"}),"connect: only exact pins that passed preflight enter the apply plan");
  }
 }
}
