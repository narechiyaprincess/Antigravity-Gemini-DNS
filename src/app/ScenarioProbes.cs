using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 public sealed record ScenarioContext(string Client,string Scenario,string Network,string Pack,string Provider,string Policy);
 public sealed record ScenarioSignal(string Host,string Family,string Layer,string Outcome,string Detail);
 public sealed record ScenarioReceipt(int Schema,ScenarioContext Context,DateTimeOffset ObservedUtc,DateTimeOffset ExpiresUtc,
  bool ContextUnchanged,string Path,string Readiness,string Limit,ScenarioSignal[] Signals);
 public sealed record ScenarioExchange(bool Tcp,bool Tls,int Status,string Failure);
 public sealed record UserScenarioEvidence(ScenarioContext Context,DateTimeOffset ObservedUtc,DateTimeOffset ExpiresUtc,
  string Source,ObservationOutcome Ui,ObservationOutcome Request,ObservationOutcome Stream,string Failure);
 public interface IScenarioTransport {
  Task<string[]> Resolve(string host,int type,CancellationToken token);
  Task<byte[]> Modern(string host,int type,CancellationToken token);
  Task<ScenarioExchange> Head(string host,IPAddress address,CancellationToken token);
 }
 // A deliberately conservative diagnostic destination policy, independent of legacy routing policy.
 public static class ScenarioDestination {
  public static bool Public(IPAddress ip) {
   if(ip==null||ip.IsIPv4MappedToIPv6)return false;
   var b=ip.GetAddressBytes();
   if(b.Length==4) {
    int a=b[0],c=b[1];return !(a==0||a==10||a==127||a>=224||a==100&&c>=64&&c<=127||a==169&&c==254||
     a==172&&c>=16&&c<=31||a==192&&(c==168||c==0&&(b[2]==0||b[2]==2)||c==88&&b[2]==99)||
     a==198&&(c==18||c==19||c==51&&b[2]==100)||a==203&&c==0&&b[2]==113);
   }
   if(b.Length!=16||b[0]<0x20||b[0]>0x3f)return false;
   return !(b[0]==0x20&&b[1]==1&&(b[2]<2||b[2]==0x0d&&b[3]==0xb8)||
    b[0]==0x20&&b[1]==2||b[0]==0x3f&&b[1]==0xff&&(b[2]&0xf0)==0);
  }
 }
 public static class ScenarioProbes {
  public const int OperationMs=4000,OverallMs=90000,HeaderBytes=16384,BodyBytes=65535;
  public static string[] Hosts=>ProfileCatalog.BuiltIn.TargetsFor(true).ToArray();
  public static void RequireHost(string host) {if(!Hosts.Contains(host,StringComparer.Ordinal))throw new ArgumentException("Scenario endpoint is not an exact catalog host");}
  public static string PolicyRevision() {
   try {if(!File.Exists(Recovery.StatePath))return "absent";using var f=new FileStream(Recovery.StatePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
    if(f.Length>1048576)return "unknown:oversized";return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f));
   }catch{return "unknown:unreadable";}
  }
  public static ScenarioContext Context(string provider)=>new ScenarioContext("transport-control-cli","catalog-head-v1",EvidenceContext.NetworkRevision(),
   EvidenceContext.Hash(PolicyJson.Write(LegacyScenarioAdapter.Migrate(ProfileCatalog.BuiltIn,new AppSettings{NoSave=true,Gemini=true}).Pack)),provider,PolicyRevision());
  public static string HttpMeaning(int status)=>status>=200&&status<300?"RESOURCE_REACHABLE_OPERATION_UNKNOWN":status>=300&&status<400?"REDIRECT_NOT_FOLLOWED":
   status==401||status==403?"RESOURCE_AUTH_OR_PERMISSION_REFUSAL":status==429?"RESOURCE_RATE_LIMITED":status>=500&&status<=599?"RESOURCE_SERVER_REFUSAL":
   status>=400&&status<500?"RESOURCE_OR_METHOD_REFUSAL":"INVALID_HTTP_STATUS";
  static bool CompleteContext(ScenarioContext context)=>context!=null&&new[]{context.Client,context.Scenario,context.Network,context.Pack,context.Provider,context.Policy}.All(x=>!string.IsNullOrWhiteSpace(x)&&!x.StartsWith("unknown:",StringComparison.Ordinal));
  public static bool Fresh(ScenarioReceipt receipt,ScenarioContext context,DateTimeOffset now)=>receipt!=null&&CompleteContext(context)&&receipt.ContextUnchanged&&receipt.Context==context&&
   receipt.ExpiresUtc>receipt.ObservedUtc&&receipt.ObservedUtc<=now&&now<receipt.ExpiresUtc;
  public static string AssessApplication(UserScenarioEvidence evidence,ScenarioContext current,DateTimeOffset now) {
   if(evidence==null||!CompleteContext(current)||evidence.Context!=current||
    evidence.Source!="VOLUNTARY_USER_ATTESTATION"||evidence.ObservedUtc>now||now>=evidence.ExpiresUtc||evidence.ExpiresUtc<=evidence.ObservedUtc)return "UNKNOWN";
   var results=new[]{evidence.Ui,evidence.Request,evidence.Stream};
   return results.Contains(ObservationOutcome.Failed)?"FAILED:"+evidence.Failure:results.All(x=>x==ObservationOutcome.Passed)?"PASSED_USER_ATTESTED_SELECTED_SCENARIO":"UNKNOWN";
  }
  static async Task<T> Bounded<T>(Func<CancellationToken,Task<T>> action,CancellationToken overall) {
   using var op=CancellationTokenSource.CreateLinkedTokenSource(overall);op.CancelAfter(OperationMs);
   Task<T> task=action(op.Token);
   // A noncooperative injectable transport cannot hold up the scenario or fault unobserved later.
   _=task.ContinueWith(t=>{_ = t.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
   return await task.WaitAsync(op.Token).ConfigureAwait(false);
  }
  public static async Task<ScenarioReceipt> Run(IScenarioTransport transport,ScenarioContext context,Func<ScenarioContext> current,bool modern,CancellationToken token=default) {
   if(transport==null||context==null||current==null)throw new ArgumentNullException();
   var start=DateTimeOffset.UtcNow;var signals=new List<ScenarioSignal>();
   using var budget=CancellationTokenSource.CreateLinkedTokenSource(token);budget.CancelAfter(OverallMs);
   foreach(string host in Hosts) {
    foreach(int type in new[]{1,28}) {
     string family=type==1?"IPv4":"IPv6";string[] addresses;
     if(budget.IsCancellationRequested){signals.Add(new(host,family,"dns","UNKNOWN","OVERALL_BUDGET_OR_CANCELLED"));continue;}
     try {addresses=await Bounded(t=>transport.Resolve(host,type,t),budget.Token);
      if(addresses==null||addresses.Length>64)throw new FormatException("DNS_ADDRESS_LIMIT");
      if(addresses.Any(a=>!IPAddress.TryParse(a,out var ip)||!ScenarioDestination.Public(ip)||ip.AddressFamily!=(type==1?AddressFamily.InterNetwork:AddressFamily.InterNetworkV6))) {
       signals.Add(new(host,family,"dns","FAILED","NONPUBLIC_OR_INVALID_ANSWER_POSSIBLE_POISON"));continue;
      }
      signals.Add(new(host,family,"dns",addresses.Length>0?"PASSED":"UNKNOWN",addresses.Length>0?string.Join(",",addresses):"NO_ADDRESS"));
     }catch(Exception e){signals.Add(new(host,family,"dns","FAILED",Failure(e)));continue;}
     if(addresses.Length==0)continue;
     try {
      var exchange=await Bounded(t=>transport.Head(host,IPAddress.Parse(addresses[0]),t),budget.Token);
      signals.Add(new(host,family,"tcp",exchange.Tcp?"PASSED":"FAILED",exchange.Tcp?"CONNECTED":exchange.Failure));
      signals.Add(new(host,family,"tls",exchange.Tls?"PASSED":exchange.Tcp?"FAILED":"UNKNOWN",exchange.Tls?"NAME_AND_CHAIN_VALID_REVOCATION_NOT_CHECKED":exchange.Failure));
      signals.Add(new(host,family,"http",exchange.Status>=100?"OBSERVED":"UNKNOWN",exchange.Status>=100?exchange.Status+":"+HttpMeaning(exchange.Status):exchange.Failure));
     }catch(Exception e){signals.Add(new(host,family,"exchange","FAILED",Failure(e)));}
    }
    foreach(int type in new[]{64,65}) {
     if(!modern){signals.Add(new(host,type.ToString(),"modern-rr","UNKNOWN","SYSTEM_API_NOT_MEASURED"));continue;}
     try {var data=await Bounded(t=>transport.Modern(host,type,t),budget.Token);var parsed=ScenarioDns.Parse(data,host,type);
      signals.Add(new(host,type.ToString(),"modern-rr","OBSERVED",PolicyJson.Write(parsed)));
     }catch(Exception e){signals.Add(new(host,type.ToString(),"modern-rr","UNKNOWN",Failure(e)));}
    }
   }
   bool unchanged=context==current();
   // Transport controls never complete account, operation, stream, browser, or dependency coverage.
   return new ScenarioReceipt(1,context,start,start.AddMinutes(5),unchanged,modern?"Pinned-provider DoH + pinned destination TCP/TLS/HEAD; current OS/VPN/TUN egress":"System resolver (cache/hosts/policy) + pinned destination TCP/TLS/HEAD; current OS/VPN/TUN egress",
    "UNKNOWN","Transport controls only; browser and app resolver paths not inferred; dependency graph incomplete",signals.ToArray());
  }
  public static string Failure(Exception e)=>e is OperationCanceledException?"TIMEOUT_OR_CANCELLED":e is AuthenticationException?"TLS_VALIDATION_OR_HANDSHAKE_FAILURE":
   e is SocketException?"TCP_OR_DNS_SOCKET_FAILURE":e is FormatException?e.Message:"TRANSPORT_FAILURE_"+e.GetType().Name;
  public static async Task<byte[]> ReadBody(Stream stream,int max,CancellationToken token) {
   using var output=new MemoryStream();var buffer=new byte[4096];int n;
   while((n=await stream.ReadAsync(buffer.AsMemory(0,Math.Min(buffer.Length,max+1-(int)output.Length)),token))>0) {
    output.Write(buffer,0,n);if(output.Length>max)throw new FormatException("BODY_LIMIT");
   }return output.ToArray();
  }
  public static async Task<int> ReadHead(Stream stream,CancellationToken token) {
   // HEAD has no response content. Read only the bounded header, never a response body or redirect target.
   var b=new byte[HeaderBytes];int n=0,start=0,interim=0;var one=new byte[1];
   while(n<b.Length) {
    if(await stream.ReadAsync(one,token)==0)throw new FormatException("HTTP_HEADERS_INCOMPLETE");b[n++]=one[0];
    if(n>=4&&b[n-4]==13&&b[n-3]==10&&b[n-2]==13&&b[n-1]==10) {
     string first=Encoding.ASCII.GetString(b,start,n-start).Split(new[]{"\r\n"},StringSplitOptions.None)[0];
     if(first.Length<12||!first.StartsWith("HTTP/1.",StringComparison.Ordinal)||first[7]!='0'&&first[7]!='1'||first[8]!=' '||
      !int.TryParse(first.Substring(9,3),out int status)||status<100||status>599||first.Length>12&&first[12]!=' ')throw new FormatException("HTTP_INVALID_STATUS");
     if(status<200){if(status==101||++interim>8)throw new FormatException("HTTP_UPGRADE_OR_INTERIM_LIMIT");start=n;continue;}
     return status;
    }
   }throw new FormatException("HEADER_LIMIT");
  }
 }
 // Also catches a network that changes and returns to its original snapshot during one probe run.
 public sealed class ScenarioNetworkEpoch:IDisposable {
  // Counts real changes of the network revision only: Windows raises address events for every blinking adapter and IPv6 rotation.
  long epoch;string seen=EvidenceContext.NetworkRevision();readonly object gate=new();
  public ScenarioNetworkEpoch(){NetworkChange.NetworkAddressChanged+=Changed;NetworkChange.NetworkAvailabilityChanged+=Availability;}
  void Changed(object sender,EventArgs args)=>Observe();
  void Availability(object sender,NetworkAvailabilityEventArgs args)=>Observe();
  internal void Observe(){string now=EvidenceContext.NetworkRevision();lock(gate){if(now!=null&&now==seen)return;seen=now;}Interlocked.Increment(ref epoch);}
  public ScenarioContext Capture(string provider) {
   long before=Interlocked.Read(ref epoch);var context=ScenarioProbes.Context(provider);long after=Interlocked.Read(ref epoch);
   return context with{Network=string.IsNullOrWhiteSpace(context.Network)||before!=after?"unknown:network-changing":context.Network+":"+after};
  }
  public void Dispose(){NetworkChange.NetworkAddressChanged-=Changed;NetworkChange.NetworkAvailabilityChanged-=Availability;}
 }
 public sealed class LiveScenarioTransport:IScenarioTransport {
  readonly string endpoint;readonly IPAddress pin;
  public string ProviderRevision{get;}
  public LiveScenarioTransport(string profile=null) {
   if(profile==null){ProviderRevision="system-resolver:route-unobserved";return;}
   var p=ProfileCatalog.BuiltIn.Find(profile)??throw new ArgumentException("Unknown catalog provider");
   var uri=new Uri(p.Template);if(uri.Scheme!="https"||uri.Port!=443||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||p.Servers.Length==0)throw new ArgumentException("Unsafe provider endpoint");
   if(p.Servers.Any(s=>!IPAddress.TryParse(s,out var ip)||!ScenarioDestination.Public(ip)))throw new ArgumentException("Nonpublic provider pin");
   endpoint=uri.AbsoluteUri;pin=IPAddress.Parse(p.Servers[0]);ProviderRevision=PolicyCompiler.RouteRevision(RouteKind.NativeDoh,endpoint,p.Servers);
  }
  static SslClientAuthenticationOptions Tls(string host,bool doh=false)=>new SslClientAuthenticationOptions{TargetHost=host,
   ApplicationProtocols=new List<SslApplicationProtocol>{doh?SslApplicationProtocol.Http2:SslApplicationProtocol.Http11},
   CertificateChainPolicy=new X509ChainPolicy{DisableCertificateDownloads=true,RevocationMode=X509RevocationMode.NoCheck}};
  static async Task<Socket> Connect(IPAddress ip,int port,CancellationToken token) {
   if(port!=443||!ScenarioDestination.Public(ip))throw new FormatException("SSRF_DESTINATION_BLOCKED");
   var s=new Socket(ip.AddressFamily,SocketType.Stream,ProtocolType.Tcp);
   try {await s.ConnectAsync(ip,port,token);if(!Equals(((IPEndPoint)s.RemoteEndPoint).Address,ip))throw new FormatException("CONNECTED_ADDRESS_CHANGED");return s;}catch{s.Dispose();throw;}
  }
  async Task<byte[]> Query(string host,int type,CancellationToken token) {
   ScenarioProbes.RequireHost(host);if(endpoint==null)throw new FormatException("MODERN_SYSTEM_NOT_MEASURED");
   byte[] query=ScenarioDns.Query(host,type);var uri=new Uri(endpoint);
   using var handler=new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false,Credentials=null,MaxResponseHeadersLength=16,MaxResponseDrainSize=0,SslOptions=Tls(uri.IdnHost,true),
    ConnectCallback=async (ctx,ct)=>{if(ctx.DnsEndPoint.Host!=uri.IdnHost||ctx.DnsEndPoint.Port!=443)throw new FormatException("DOH_ENDPOINT_CHANGED");return new NetworkStream(await Connect(pin,443,ct),true);}};
   using var client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
   using var request=DohRequest(uri,query);
   using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
   if((int)response.StatusCode!=200)throw new FormatException("DOH_HTTP_"+(int)response.StatusCode);
   if(response.Content.Headers.ContentType?.MediaType!="application/dns-message")throw new FormatException("DOH_MEDIA_TYPE");
   if(response.Content.Headers.ContentLength>ScenarioProbes.BodyBytes)throw new FormatException("BODY_LIMIT");
   using var stream=await response.Content.ReadAsStreamAsync(token);var body=await ScenarioProbes.ReadBody(stream,ScenarioProbes.BodyBytes,token);
   if(body.Length<2||body[0]!=query[0]||body[1]!=query[1])throw new FormatException("DNS_ID_MISMATCH");return body;
  }
  internal static HttpRequestMessage DohRequest(Uri uri,byte[] query) {
   var request=new HttpRequestMessage(HttpMethod.Post,uri){Content=new ByteArrayContent(query),Version=HttpVersion.Version20,VersionPolicy=HttpVersionPolicy.RequestVersionExact};
   request.Content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message");
   request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-message"));return request;
  }
  public async Task<string[]> Resolve(string host,int type,CancellationToken token) {
   ScenarioProbes.RequireHost(host);if(type!=1&&type!=28)throw new ArgumentException("DNS type");
   if(endpoint==null)return (await Dns.GetHostAddressesAsync(host,type==1?AddressFamily.InterNetwork:AddressFamily.InterNetworkV6,token)).Select(a=>a.ToString()).ToArray();
   var b=await Query(host,type,token);var parsed=Wire.Parse(b,host,type,(b[0]<<8)|b[1]);
   if(parsed.Rcode!=0)throw new FormatException("DNS_RCODE_"+parsed.Rcode);return parsed.Addresses??Array.Empty<string>();
  }
  public Task<byte[]> Modern(string host,int type,CancellationToken token) {if(type!=64&&type!=65)throw new ArgumentException("DNS type");return Query(host,type,token);}
  public async Task<ScenarioExchange> Head(string host,IPAddress address,CancellationToken token) {
   ScenarioProbes.RequireHost(host);bool tcp=false,tls=false;
   try {using var socket=await Connect(address,443,token);tcp=true;using var stream=new SslStream(new NetworkStream(socket,false),false);
    await stream.AuthenticateAsClientAsync(Tls(host),token);tls=true;
    byte[] request=Encoding.ASCII.GetBytes("HEAD / HTTP/1.1\r\nHost: "+host+"\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(request,token);return new(tcp,tls,await ScenarioProbes.ReadHead(stream,token),null);
   }catch(Exception e){return new(tcp,tls,0,ScenarioProbes.Failure(e));}
  }
 }
}
