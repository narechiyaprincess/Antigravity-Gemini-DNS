using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 public sealed class TlsInfo {
  public bool Ok; public string Error; public string Protocol; public string Alpn; public string Subject; public string Issuer; public string NotAfter;
  public string PolicyErrors="None"; public long ConnectMs; public long HandshakeMs;
 }

 // Read-only network probes: TCP connect + TLS handshake with full certificate/name validation. Nothing here changes system state.
 public static class Probing {
  public static async Task<TlsInfo> TlsAsync(string ip,int port,string sni,int timeoutMs,CancellationToken ct=default) {
   var info=new TlsInfo();
   using(var cts=CancellationTokenSource.CreateLinkedTokenSource(ct)) {
    cts.CancelAfter(timeoutMs);
    var sw=Stopwatch.StartNew();
    try {
     var addr=IPAddress.Parse(ip);
     using(var sock=new Socket(addr.AddressFamily,SocketType.Stream,ProtocolType.Tcp){NoDelay=true}) {
      await sock.ConnectAsync(addr,port,cts.Token);
      info.ConnectMs=sw.ElapsedMilliseconds;sw.Restart();
      using(var ns=new NetworkStream(sock,false))
      using(var ssl=new SslStream(ns,false,(s,cert,chain,errors)=>{
       info.PolicyErrors=errors.ToString();
       if(cert!=null){try{using var c2=new X509Certificate2(cert);info.Subject=c2.Subject;info.Issuer=c2.Issuer;info.NotAfter=c2.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd");}catch{}}
       return errors==SslPolicyErrors.None;
      })) {
       var options=new SslClientAuthenticationOptions{TargetHost=sni,ApplicationProtocols=new List<SslApplicationProtocol>{SslApplicationProtocol.Http2,SslApplicationProtocol.Http11}};
       await ssl.AuthenticateAsClientAsync(options,cts.Token);
       info.HandshakeMs=sw.ElapsedMilliseconds;info.Ok=true;
       // The OS revocation lookup is synchronous with a per-retrieval CertRevocation.Limit.
       // Keep the probe's deadline while waiting and own the certificate copy if that lookup finishes after cancellation.
       var rc=ssl.RemoteCertificate;
       if(rc!=null) {
        var copy=new X509Certificate2(rc);
        bool revoked=await Task.Run(()=>{using(copy)return CertRevocation.IsRevoked(copy);}).WaitAsync(cts.Token);
        if(revoked){info.Ok=false;info.Error="CERT_REVOKED";}
       }
       info.Protocol=ssl.SslProtocol.ToString();info.Alpn=ssl.NegotiatedApplicationProtocol.ToString();
      }
     }
    }
    catch(OperationCanceledException) when(!ct.IsCancellationRequested){info.Ok=false;info.Error="TIMEOUT_"+timeoutMs+"ms";}
    catch(OperationCanceledException){info.Ok=false;info.Error="CANCELLED";}
    catch(Exception e){info.Ok=false;var inner=e;while(inner.InnerException!=null)inner=inner.InnerException;info.Error=inner.GetType().Name+": "+inner.Message;}
   }
   return info;
  }
 }

 public sealed class CrossResult {public string Via;public string Server;public string Code;public string[] Addresses;}
 // Network access of the bootstrap, injectable so the offline tests can simulate resolvers, spoofed addresses and a missing connection.
 public interface IBootstrapNet {
  Task<string[]> SystemResolve(string host,CancellationToken ct);
  Task<CrossResult[]> CrossCheck(string host,CancellationToken ct);
  Task<TlsInfo> TlsOnName(string ip,int port,string host,CancellationToken ct);
  Task<Candidate[]> DohCandidates(string[] ips,string template,CancellationToken ct);
 }

 // A public DoH resolver used only to find out which addresses a provider NAME has. It is never trusted: every address it returns still has to pass TLS on the name.
 public sealed class DohResolver {public string Name,Server,Template;}

 public sealed class LiveBootstrapNet:IBootstrapNet {
  // Only what the operators publish on their own documentation pages (AdGuard, DNS.SB and Yandex read on 2026-10-12; Cloudflare, Google and Quad9 are the 1.3 entries):
  //  Cloudflare  https://developers.cloudflare.com/1.1.1.1/encryption/dns-over-https/        https://cloudflare-dns.com/dns-query @ 1.1.1.1
  //  Google      https://developers.google.com/speed/public-dns/docs/doh                      https://dns.google/dns-query @ 8.8.8.8
  //  Quad9       https://docs.quad9.net/services/                                              https://dns.quad9.net/dns-query @ 9.9.9.9
  //  AdGuard DNS https://adguard-dns.io/kb/general/dns-providers/                              "Default" https://dns.adguard-dns.com/dns-query @ 94.140.14.14; "Non-filtering" https://unfiltered.adguard-dns.com/dns-query @ 94.140.14.140
  //  DNS.SB      https://dns.sb/doh/ (updated 2026-01-17)                                      https://dns.sb/dns-query @ 185.222.222.222 and 45.11.45.11
  // Yandex DNS (https://yandex.ru/support/dns/ru/ya-dns-modes.md) documents DoH only as "77.88.8.8:443" with no host name or URL template, so a name-validated
  // DoH query cannot be built from the official page; it is deliberately not used. Mullvad announced the end of its public DNS on 2026-11-02; not used.
  static readonly DohResolver[] Fixed={
   new DohResolver{Name="Cloudflare",Server="1.1.1.1",Template="https://cloudflare-dns.com/dns-query"},
   new DohResolver{Name="Google",Server="8.8.8.8",Template="https://dns.google/dns-query"},
   new DohResolver{Name="Quad9",Server="9.9.9.9",Template="https://dns.quad9.net/dns-query"},
   new DohResolver{Name="AdGuard unfiltered",Server="94.140.14.140",Template="https://unfiltered.adguard-dns.com/dns-query"},
   new DohResolver{Name="AdGuard default",Server="94.140.14.14",Template="https://dns.adguard-dns.com/dns-query"},
   new DohResolver{Name="DNS.SB",Server="185.222.222.222",Template="https://dns.sb/dns-query"},
   new DohResolver{Name="DNS.SB 2",Server="45.11.45.11",Template="https://dns.sb/dns-query"},
  };
  public const int PinsPerCatalogProvider=2;
  // Foreign resolvers are often cut off from Russia; the providers of the catalog are reachable by definition of the catalog, so they ask too (by their pins).
  // The provider being bootstrapped is left out: its own pins are the ones that are dead.
  public static DohResolver[] Resolvers(string host,ProfileCatalog catalog=null) {
   var list=new List<DohResolver>(Fixed);
   foreach(var p in (catalog??ProfileCatalog.BuiltIn).Profiles) {
    string h;try{h=p.Host;}catch{continue;}
    if(string.Equals(h,host,StringComparison.OrdinalIgnoreCase))continue;
    foreach(string ip in p.Servers.Take(PinsPerCatalogProvider))list.Add(new DohResolver{Name=p.Id,Server=ip,Template=p.Template});
   }
   return list.GroupBy(r=>r.Server+"|"+r.Template).Select(g=>g.First()).ToArray();
  }
  public async Task<string[]> SystemResolve(string host,CancellationToken ct)=>(await Dns.GetHostAddressesAsync(host,ct)).Where(a=>a.AddressFamily==AddressFamily.InterNetwork).Select(a=>a.ToString()).Distinct().ToArray();
  // All resolvers ask at the same time, each with its own short timeout; one that does not answer costs nothing but its own slot.
  public async Task<CrossResult[]> CrossCheck(string host,CancellationToken ct) {
   using(var live=new LiveTransport()) {
    var asks=Resolvers(host).Select(async r=>{
     try{
      var spec=new ProfileSpec{Id=r.Name,Template=r.Template,Servers=new[]{r.Server}};
      var reply=await live.QueryAsync(spec,r.Server,host,1,4500,ct);
      return new CrossResult{Via=r.Template,Server=r.Server,Code=reply.Code,Addresses=reply.Addresses??new string[0]};
     }catch(Exception e){return new CrossResult{Via=r.Template,Server=r.Server,Code=e is OperationCanceledException?"CANCELLED":"ERROR",Addresses=new string[0]};}
    }).ToArray();
    return await Task.WhenAll(asks);
   }
  }
  public Task<TlsInfo> TlsOnName(string ip,int port,string host,CancellationToken ct)=>Probing.TlsAsync(ip,port,host,6000,ct);
  public Task<Candidate[]> DohCandidates(string[] ips,string template,CancellationToken ct)=>Program.NetworkCandidates(ips,template,false,ct);
 }

 // Time limits of one bootstrap. The whole search never takes longer than Budget; the resolvers get Discovery of it, the rest is for the certificate and DoH checks.
 public sealed class BootstrapOptions {
  public TimeSpan Budget=TimeSpan.FromSeconds(20);
  public TimeSpan Discovery=TimeSpan.FromSeconds(9);
 }
 // The budget ran out and not a single address had been validated: a refusal, not "no address passed".
 public sealed class BootstrapTimeoutException:Exception {
  public BootstrapTimeoutException(string message):base(message){}
 }

 // Safe bootstrap for a DoH provider that is known only by name: resolve the host, cross-check, validate TLS on the NAME,
 // then validate DoH + TLS for both Google domains. Only validated IPs are returned as pin candidates. Nothing is applied here.
 public static class Bootstrap {
  public static bool TryParse(string input,out Uri uri) {
   uri=null;if(string.IsNullOrWhiteSpace(input))return false;
   string raw=input.Trim().Trim('"','\'');
   if(!raw.Contains("://"))raw="https://"+raw+"/dns-query";
   if(!Uri.TryCreate(raw,UriKind.Absolute,out var u)||u.Scheme!=Uri.UriSchemeHttps||string.IsNullOrEmpty(u.IdnHost))return false;
   if(IPAddress.TryParse(u.IdnHost,out _))return false;
   uri=u;return true;
  }

  // Awaits f within `limit`. Whatever happens (timeout, an exception, a task that ignores its token) the caller gets `fallback` and the reason in `notes`:
  // one slow or broken step never takes the rest of the search down with it.
  static void Note(List<string> notes,string text){lock(notes)notes.Add(text);}
  static async Task<T> Within<T>(Func<CancellationToken,Task<T>> f,CancellationToken ct,TimeSpan limit,T fallback,List<string> notes,string what,Action timedOut=null) {
   if(limit<=TimeSpan.Zero){Note(notes,what+": no time left");timedOut?.Invoke();return fallback;}
   using(var cts=CancellationTokenSource.CreateLinkedTokenSource(ct)) {
    cts.CancelAfter(limit);
    Task<T> work;
    try{work=f(cts.Token);}catch(Exception e){Note(notes,what+" failed: "+e.Message);return fallback;}
    var gate=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    using(cts.Token.Register(()=>gate.TrySetResult(true))) {
     var first=await Task.WhenAny(work,gate.Task);
     if(first==work) {
      try{return await work;}
      catch(OperationCanceledException){Note(notes,what+": no answer in time");timedOut?.Invoke();return fallback;}
      catch(Exception e){Note(notes,what+" failed: "+e.Message);return fallback;}
     }
    }
    var _=work.ContinueWith(t=>{var ignored=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);   // a late failure of the abandoned task is not an unobserved exception
    Note(notes,what+": no answer in time");timedOut?.Invoke();return fallback;
   }
  }
  static TimeSpan Left(Stopwatch sw,BootstrapOptions o)=>o.Budget-sw.Elapsed;

  public static async Task<object> RunAsync(string input,CancellationToken ct=default,IBootstrapNet net=null,BootstrapOptions options=null) {
   net=net??new LiveBootstrapNet();options=options??new BootstrapOptions();
   if(!TryParse(input,out var uri))throw new ArgumentException("Bootstrap needs a hostname or an absolute https:// DoH URL: "+input);
   string host=uri.IdnHost;int port=uri.Port;string template=uri.AbsoluteUri;
   var notes=new List<string>();var sw=Stopwatch.StartNew();bool anyTimeout=false,lateTimeout=false;Action slow=()=>anyTimeout=true;Action late=()=>lateTimeout=true;
   // Everything below runs against one budget. The system resolver and all cross-check resolvers ask in parallel; what has not answered when the
   // discovery time is over is dropped, and the addresses that did come are validated with the time that is left.
   using(var overall=CancellationTokenSource.CreateLinkedTokenSource(ct)) {
    overall.CancelAfter(options.Budget);var tok=overall.Token;
    TimeSpan disc=options.Discovery<Left(sw,options)?options.Discovery:Left(sw,options);
    var systemTask=Within(t=>net.SystemResolve(host,t),tok,disc,new string[0],notes,"System resolver",slow);
    var crossTask=Within(t=>net.CrossCheck(host,t),tok,disc,new CrossResult[0],notes,"Cross-check",slow);
    await Task.WhenAll(systemTask,crossTask);
    string[] system=systemTask.Result;CrossResult[] cross=crossTask.Result??new CrossResult[0];
    var all=system.Concat(cross.SelectMany(c=>c.Addresses??new string[0])).Where(Program.PublicIp).Distinct().Take(12).ToArray();
    if(all.Length==0)notes.Add("No public IPv4 address was resolved for the host; nothing to pin.");
    var tlsTasks=all.Select(ip=>Within(t=>net.TlsOnName(ip,port,host,t),tok,Left(sw,options),new TlsInfo{Ok=false,Error="NOT_CHECKED_IN_TIME"},notes,"TLS on the name "+ip,late)).ToArray();
    var tls=await Task.WhenAll(tlsTasks);
    var nameOk=all.Where((ip,i)=>tls[i].Ok).ToArray();
    Candidate[] candidates=nameOk.Length>0?await Within(t=>net.DohCandidates(nameOk,template,t),tok,Left(sw,options),new Candidate[0],notes,"DoH check",late):new Candidate[0];
    var pinned=candidates.Where(c=>c.DohValidated).Select(c=>c.Server).ToArray();
    if(nameOk.Length>0&&pinned.Length==0&&!tok.IsCancellationRequested)notes.Add("TLS on the provider name succeeded, but DoH for both Google domains did not validate; nothing to pin.");
    ct.ThrowIfCancellationRequested();   // cancelled by the user: neither a result nor a timeout
    bool exceeded=overall.IsCancellationRequested&&!ct.IsCancellationRequested||sw.Elapsed>=options.Budget;
    // Gave up = nothing validated and the time ran out (or the resolvers were silent): that is "did not finish in time", not "no address passed".
    bool gaveUp=pinned.Length==0&&(exceeded||lateTimeout||(anyTimeout&&all.Length==0));
    if(exceeded)notes.Add("Search budget of "+(int)options.Budget.TotalSeconds+" s used up"+(pinned.Length==0?"; no address was validated in time.":"; the addresses validated so far are kept."));
    return new{
     TimestampUtc=DateTime.UtcNow,Host=host,Port=port,Template=template,
     SystemResolver=system,CrossCheck=cross,
     Candidates=all.Select((ip,i)=>new{
      Ip=ip,SeenInSystemResolver=system.Contains(ip),SeenInCrossCheck=cross.Any(c=>(c.Addresses??new string[0]).Contains(ip)),
      TlsOnName=tls[i].Ok,TlsError=tls[i].Error,TlsPolicyErrors=tls[i].PolicyErrors,tls[i].Subject,tls[i].Issuer,tls[i].NotAfter,tls[i].Alpn,
      DohBothDomains=candidates.FirstOrDefault(c=>c.Server==ip)?.DohValidated??false,
      DohTls=candidates.FirstOrDefault(c=>c.Server==ip)?.DohTls
     }).ToArray(),
     Pinned=pinned,BudgetExceeded=exceeded,TimedOut=gaveUp,BudgetSeconds=(int)options.Budget.TotalSeconds,Notes=notes.ToArray(),
     Trust="Review the IPs above before pinning. A pin is accepted only when TLS validates for the provider name and DoH answers both Google domains with TLS-valid public IPs."
    };
   }
  }
  // Just the validated addresses (TLS on the provider name + DoH for both Google domains), for the connect flow.
  // Nothing validated and the budget used up: BootstrapTimeoutException (a "ran out of time", not "no address passed").
  public static async Task<string[]> PinnedAsync(string template,CancellationToken ct=default,IBootstrapNet net=null,BootstrapOptions options=null) {
   var element=JsonSerializer.SerializeToElement(await RunAsync(template,ct,net,options),Program.JsonOptions);
   var pinned=element.GetProperty("Pinned").EnumerateArray().Select(x=>x.GetString()).ToArray();
   if(pinned.Length==0&&element.GetProperty("TimedOut").GetBoolean())throw new BootstrapTimeoutException("The address search used up its "+element.GetProperty("BudgetSeconds").GetInt32()+" s budget.");
   return pinned;
  }
 }
}
