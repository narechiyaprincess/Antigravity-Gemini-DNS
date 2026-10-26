using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 // The ping shown on the main page while the bridge works: the round trip of a TCP connect to the provider's intermediary server for an Antigravity host.
 // Read-only, like the benchmark: one DoH query to the active provider for the proxy address, then a few TLS handshakes with full certificate
 // validation (SNI = the Google host). A sample counts only if TLS passed, so the number always belongs to a proxy that really serves the host.
 public interface ISpeedProbe {
  Task<Reply> Doh(string template,string server,string host,int timeoutMs,CancellationToken ct);
  Task<TlsInfo> Tls(string ip,string host,int timeoutMs,CancellationToken ct);
 }

 public sealed class SpeedResult {
  public long Ms=-1;          // median TCP connect time to the proxy; -1 = no valid sample
  public string Proxy;        // the address that was measured
  public string Error;        // NO_BRIDGE, NO_ANSWER (provider gave no public IPv4), TLS_FAILED
  public DateTime Utc;
 }

 public static class BridgeSpeed {
  public const int Samples=3,TimeoutMs=2500;
  public static readonly TimeSpan Fresh=TimeSpan.FromMinutes(2);   // a newer result is shown as is; an older one is measured again on the next look

  public const int HedgeMs=500;   // a server that has not answered within this time is joined by the next one; the first answer wins
  static async Task<string> AskAsync(ISpeedProbe probe,string template,string server,string host,CancellationToken ct) {
   Reply r;try{r=await probe.Doh(template,server,host,TimeoutMs,ct);}catch(OperationCanceledException){throw;}catch{return null;}
   return r?.Addresses?.FirstOrDefault(a=>IPAddress.TryParse(a,out var ip)&&ip.AddressFamily==AddressFamily.InterNetwork&&Program.PublicIp(a));
  }
  // The servers are tried in order, as before; but one that is silent does not hold the next back for its whole timeout: after HedgeMs the next server is asked too
  // (a provider whose servers are all down then costs about one timeout, not one per server). A server that answers at once is the only one ever asked.
  static async Task<string> FindProxyAsync(ISpeedProbe probe,string template,string[] servers,string host,CancellationToken ct) {
   var running=new List<Task<string>>();int next=0;string proxy=null;
   using(var race=CancellationTokenSource.CreateLinkedTokenSource(ct)) {
    try {
     while(proxy==null) {
      ct.ThrowIfCancellationRequested();
      if(next<servers.Length)running.Add(AskAsync(probe,template,servers[next++],host,race.Token));
      if(running.Count==0)break;
      Task hedge=next<servers.Length?Task.Delay(HedgeMs,race.Token):null;
      while(proxy==null&&running.Count>0) {
       var waits=new List<Task>(running);if(hedge!=null)waits.Add(hedge);
       var done=await Task.WhenAny(waits);
       if(done==hedge)break;
       var finished=(Task<string>)done;running.Remove(finished);
       proxy=await finished;
       if(proxy==null&&next<servers.Length)break;   // this one gave nothing: the next starts at once
      }
      if(next>=servers.Length&&running.Count==0)break;
     }
    } finally { race.Cancel(); }
   }
   return proxy;
  }

  public static async Task<SpeedResult> MeasureAsync(ISpeedProbe probe,string template,string[] servers,string host,CancellationToken ct) {
   var res=new SpeedResult{Utc=DateTime.UtcNow};
   if(string.IsNullOrWhiteSpace(template)||servers==null||servers.Length==0||string.IsNullOrWhiteSpace(host)){res.Error="NO_BRIDGE";return res;}
   res.Proxy=await FindProxyAsync(probe,template,servers,host,ct);
   if(res.Proxy==null){res.Error="NO_ANSWER";return res;}
   var times=new List<long>();
   for(int i=0;i<Samples;i++) {
    ct.ThrowIfCancellationRequested();
    TlsInfo t;try{t=await probe.Tls(res.Proxy,host,TimeoutMs,ct);}catch(OperationCanceledException){throw;}catch{t=null;}
    if(t!=null&&t.Ok)times.Add(t.ConnectMs);
   }
   ct.ThrowIfCancellationRequested();
   if(times.Count==0){res.Error="TLS_FAILED";return res;}
   times.Sort();res.Ms=times[times.Count/2];res.Utc=DateTime.UtcNow;return res;
  }

  // Plain words for the ping row: up to 80 ms is fast, up to 200 ms is normal for a provider server abroad, more is slow.
  public static string Grade(long ms)=>ms<0?"":L.T(ms<=80?"быстро":ms<=200?"нормально":"медленно");
  public static string ValueText(SpeedResult r)=>r==null?L.T("измеряю…"):r.Ms>=0?r.Ms+L.T(" мс")+" · "+Grade(r.Ms):L.T("не ответил");

  public sealed class Live:ISpeedProbe,IDisposable {
   readonly LiveTransport t=new LiveTransport();
   public Task<Reply> Doh(string template,string server,string host,int timeoutMs,CancellationToken ct)=>t.QueryAsync(new ProfileSpec{Id="speed",Template=template},server,host,1,timeoutMs,ct);
   public Task<TlsInfo> Tls(string ip,string host,int timeoutMs,CancellationToken ct)=>Probing.TlsAsync(ip,443,host,timeoutMs,ct);
   public void Dispose()=>t.Dispose();
  }
 }
}
