using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 public sealed class Candidate {
  public string Server; public bool DohValidated; public bool PlainValidated;
  public string[] TargetA; public string[] TargetAAAA;
  public Dictionary<string,string[]> TargetAnswers; public Dictionary<string,string[]> TargetAnswersV6;
  public Dictionary<string,string> TargetTls;
  public string PlainAssessment;
  public string DohTls; public string DohTlsV6; public string PlainTls;
  public string Ipv6Status;
  public Reply[] Probes;
 }
 // One version number: <Version> in the .csproj, read back from the assembly (window, --version, diagnostics, archive name).
 // Product identity. The display name, executable and assembly follow the product name. StorageName keeps the first releases' identifier
 // (Antigravity DNS Unlocker): data folders, the restore journal, the Run value, the trust key and the window mutex are shared with copies
 // already installed, so this version reads and cleans up what they wrote. See docs/COMPATIBILITY-IDENTIFIERS.md.
 public static class AppIdentity {
  public const string Name="Antigravity-Gemini DNS";
  public const string StorageName="AntigravityDnsUnlocker";
  // ProductName of this build and of the older builds: "delete previous copy" accepts both.
  public static readonly string[] ProductNames={Name,StorageName};
 }
 public static class AppInfo {
  public static string Version{get{
   var asm=System.Reflection.Assembly.GetEntryAssembly()??typeof(AppInfo).Assembly;
   string v=asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().Select(a=>a.InformationalVersion).FirstOrDefault()??asm.GetName().Version.ToString(3);
   int plus=v.IndexOf('+');return plus>0?v.Substring(0,plus):v;
  }}
  // For people (the "Инфо" page): "1.0" for 1.0.0; a patch release shows all three parts.
  public static string Display=>DisplayOf(Version);
  public static string DisplayOf(string v){var p=(v??"").Split('.');return p.Length==3&&p[2]=="0"?p[0]+"."+p[1]:v;}
 }
 public static class AutostartHelper {
  const string RealKeyName=@"Software\Microsoft\Windows\CurrentVersion\Run";
  // Test hook: AGDNS_TEST_RUN_KEY may redirect the Run key only into HKCU\Software\AgDnsTest\..., so automated runs never touch the real autostart entry.
  public static string KeyName=ResolveKeyName();
  static string ResolveKeyName() {
   string v=Environment.GetEnvironmentVariable("AGDNS_TEST_RUN_KEY");
   return !string.IsNullOrWhiteSpace(v)&&v.StartsWith(@"Software\AgDnsTest\",StringComparison.OrdinalIgnoreCase)&&!v.Contains("..")?v.Trim():RealKeyName;
  }
  public const string ValueName=AppIdentity.StorageName;   // the Run value keeps the first releases' name: an older copy's entry is recognised and replaced, not duplicated
  public static string ExtractPath(string v){
   v=v.Trim();
   if(v.StartsWith("\"")){int i=v.IndexOf('"',1);return i>0?v.Substring(1,i-1):v.Trim('"');}
   int sp=v.IndexOf(" --",StringComparison.Ordinal);return sp>0?v.Substring(0,sp):v;
  }
  // Path the autostart entry points at (null when there is no entry), whatever it is.
  public static string CurrentTarget() {
   try {
    using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyName,false);
    var val=key?.GetValue(ValueName) as string;
    return string.IsNullOrWhiteSpace(val)?null:ExtractPath(val);
   }catch{return null;}
  }
  public static bool IsEnabled(string expectedPath=null) {
   try {
    using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyName,false);
    if(key==null)return false;
    var val=key.GetValue(ValueName) as string;
    if(string.IsNullOrWhiteSpace(val))return false;
    string target=expectedPath??Environment.ProcessPath;
    if(string.IsNullOrWhiteSpace(target))return false;
    return SamePath(ExtractPath(val),target);   // the same comparison as everywhere else: normalised, case and quotes ignored
   }catch{return false;}
  }
  public static bool SetEnabled(bool enable,string targetPath=null) {
   try {
    using var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyName);
    if(key==null)return false;
    if(enable){
     string exe=targetPath??Environment.ProcessPath;
     if(string.IsNullOrWhiteSpace(exe)||!Path.IsPathFullyQualified(exe)||exe.Contains('"')||!File.Exists(exe)||!string.Equals(Path.GetExtension(exe),".exe",StringComparison.OrdinalIgnoreCase))return false;
     key.SetValue(ValueName,$"\"{exe}\" --minimized",Microsoft.Win32.RegistryValueKind.String);
    }else{
     key.DeleteValue(ValueName,false);
    }
    return true;
   }catch{return false;}
  }
  public static bool SamePath(string a,string b) {
   try{return string.Equals(Path.GetFullPath(a.Trim().Trim('"')),Path.GetFullPath(b.Trim().Trim('"')),StringComparison.OrdinalIgnoreCase);}catch{return false;}
  }
  // The entry exists but its file is gone (the exe was moved or deleted): quietly point it at this copy. An entry whose file exists is another copy's and is left alone.
  public static bool RepointIfMissing(string currentExe) {
   try {
    string target=CurrentTarget();
    if(target==null||string.IsNullOrWhiteSpace(currentExe)||!File.Exists(currentExe)||File.Exists(target)||SamePath(target,currentExe))return false;
    // a drive or share that is not there right now (a USB stick, an offline share) does not mean the file is gone: the entry is left alone
    string root=Path.GetPathRoot(Path.GetFullPath(target));
    if(string.IsNullOrEmpty(root)||!Directory.Exists(root))return false;
    return SetEnabled(true,currentExe);
   }catch{return false;}
  }
  // A new build was started while the entry names another copy of this same program that still exists: the entry follows the copy the user runs now, silently.
  // Only a file whose ProductName is this program's is replaced; a foreign exe, a missing one (RepointIfMissing) and a switched-off autostart (no entry) are left alone.
  public static bool RebindOwnCopy(string currentExe) {
   try {
    string target=CurrentTarget();
    if(target==null||string.IsNullOrWhiteSpace(currentExe)||!File.Exists(currentExe)||!File.Exists(target)||SamePath(target,currentExe))return false;
    if(!AppIdentity.ProductNames.Contains(ProductOf(currentExe)??"",StringComparer.Ordinal)||!AppIdentity.ProductNames.Contains(ProductOf(target)??"",StringComparer.Ordinal))return false;
    return EnableForCurrent(currentExe,out _);
   }catch{return false;}
  }
  // Turning autostart on always names the running exe. Returns the file the entry named before when that is another copy that still exists (shown as «Прежняя копия»), else null.
  public static bool EnableForCurrent(string currentExe,out string previousCopy) {
   previousCopy=null;
   string old=CurrentTarget();
   if(!SetEnabled(true,currentExe))return false;
   if(old!=null&&!SamePath(old,currentExe)&&File.Exists(old))previousCopy=old;
   return true;
  }
  public static string ProductOf(string exe){try{return FileVersionInfo.GetVersionInfo(exe).ProductName;}catch{return null;}}
  public static bool IsRunning(string exe) {
   try {
    string want=Path.GetFullPath(exe);string name=Path.GetFileNameWithoutExtension(want);
    foreach(var p in Process.GetProcessesByName(name))using(p){try{if(string.Equals(p.MainModule?.FileName,want,StringComparison.OrdinalIgnoreCase))return true;}catch{return true;}}   // a process whose path cannot be read counts as running: nothing is deleted on a guess
    return false;
   }catch{return true;}
  }
  public enum CopyRemoval{Removed,AlreadyGone,NotOurs,Running,ThisCopy,Failed}
  // Deletes a previous copy of this program, and only that: the file must exist, carry this program's ProductName, not be the running exe and not be running.
  public static CopyRemoval RemovePreviousCopy(string path,string currentExe,Func<string,bool> isRunning=null) {
   try {
    if(string.IsNullOrWhiteSpace(path))return CopyRemoval.AlreadyGone;
    if(!File.Exists(path))return CopyRemoval.AlreadyGone;
    if(SamePath(path,currentExe))return CopyRemoval.ThisCopy;
    string product=ProductOf(currentExe);
    if(string.IsNullOrEmpty(product)||!AppIdentity.ProductNames.Contains(product,StringComparer.Ordinal)||!AppIdentity.ProductNames.Contains(ProductOf(path)??"",StringComparer.Ordinal))return CopyRemoval.NotOurs;
    if((isRunning??IsRunning)(path))return CopyRemoval.Running;
    File.Delete(path);
    return File.Exists(path)?CopyRemoval.Failed:CopyRemoval.Removed;
   }catch{return CopyRemoval.Failed;}
  }
  public static object GetStatus(string targetPath=null) {
   string exe=targetPath??Environment.ProcessPath??Path.Combine(AppContext.BaseDirectory,"AntigravityGeminiDns.exe");
   bool enabled=IsEnabled(exe);
   string rawVal=null;
   try{using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyName,false);rawVal=key?.GetValue(ValueName) as string;}catch{}
   return new{Enabled=enabled,NonElevated=true,EntryName=ValueName,RegistryKey=@"HKCU\"+KeyName,Target=exe,CurrentValue=rawVal};
  }
 }
 public static class Program {
  // Short deadline of the unelevated CIM pre-check that runs before any UAC prompt.
  public static readonly TimeSpan GuardDeadline=TimeSpan.FromSeconds(10);
  // enable can be a profile switch: preflight of the new profile, removal of the old one, apply, and on failure a second preflight plus re-apply of the previous profile.
  // 90 s covered one preflight; with two a slow network ran into the kill in the middle of the switch, so enable gets more room.
  public static TimeSpan Deadline(string action)=>action=="enable"?TimeSpan.FromSeconds(150):action=="diagnose"?TimeSpan.FromSeconds(90):TimeSpan.FromSeconds(30);
  // The window waits for the elevated child a little longer than the child's own engine deadline, so the child's timeout receipt is read instead of a bare "may still be running".
  public static readonly TimeSpan ElevationGrace=TimeSpan.FromSeconds(15);
  public static TimeSpan ElevationWait(string action)=>Deadline(action)+ElevationGrace;
  // Target domains and the default profile come from the embedded profiles.json only.
  // "Также для Gemini": the only way the target set grows. There is no way to name a host on the command line; the extra hosts come from the catalog (geminiTargets).
  public static volatile bool Gemini;
  // "Браузер" (--browser): the catalog's Google suffixes. Suffixes are NRPT namespaces only; every check runs on Hosts. Independent of the other two services.
  public static volatile bool Google;
  // "Antigravity": the core hosts. On unless --no-core; at least one of the three services is always selected.
  public static volatile bool Core=true;
  public static string[] TargetsOf(bool core,bool gemini,bool google){
   var catalog=ProfileCatalog.BuiltIn;var list=new List<string>();
   if(core)list.AddRange(catalog.Targets);
   if(gemini)list.AddRange(catalog.GeminiTargets??new string[0]);
   if(google)list.AddRange(catalog.GoogleTargets??new string[0]);
   return list.ToArray();
  }
  public static string[] Targets=>TargetsOf(Core,Gemini,Google);
  // Exact host names of the selection. With only the browser selected there is no exact name, so the provider is checked on the catalog's own hosts (all under the suffixes).
  public static string[] Hosts{get{var hosts=Targets.Where(t=>!ProfileCatalog.IsSuffix(t)).ToArray();return hosts.Length>0?hosts:TargetsOf(true,true,false);}}
  public static bool HasCore(IEnumerable<string> targets)=>targets!=null&&ProfileCatalog.BuiltIn.Targets.Length>0&&ProfileCatalog.BuiltIn.Targets.All(g=>targets.Contains(g,StringComparer.OrdinalIgnoreCase));
  // A scope the engine and the native policy accept: a non-empty union of the three catalog groups.
  public static bool IsKnownScope(IEnumerable<string> targets){
   if(targets==null)return false;var list=targets.ToArray();if(list.Length==0||list.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=list.Length)return false;
   var catalog=ProfileCatalog.BuiltIn;var all=new List<string>();
   foreach(var group in new[]{catalog.Targets,catalog.GeminiTargets??new string[0],catalog.GoogleTargets??new string[0]}){
    if(group.Length==0)continue;int present=group.Count(x=>list.Contains(x,StringComparer.OrdinalIgnoreCase));
    if(present!=0&&present!=group.Length)return false;all.AddRange(group);
   }
   return list.All(t=>all.Contains(t,StringComparer.OrdinalIgnoreCase));
  }
  public static bool HasGoogle(IEnumerable<string> targets)=>targets!=null&&ProfileCatalog.BuiltIn.GoogleTargets.Length>0&&ProfileCatalog.BuiltIn.GoogleTargets.All(g=>targets.Contains(g,StringComparer.OrdinalIgnoreCase));
  public static bool HasGemini(IEnumerable<string> targets)=>targets!=null&&ProfileCatalog.BuiltIn.GeminiTargets.Length>0&&ProfileCatalog.BuiltIn.GeminiTargets.All(g=>targets.Contains(g,StringComparer.OrdinalIgnoreCase));
  public static string Target=>Hosts[0];
  public static string[] Servers=>ProfileCatalog.BuiltIn.DefaultProfile.Servers;
  public static string DefaultTemplate=>ProfileCatalog.BuiltIn.DefaultProfile.Template;
  public static readonly JsonSerializerOptions JsonOptions=new JsonSerializerOptions{WriteIndented=true,IncludeFields=true};
  public static string PowerShell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
  // Every PowerShell this exe starts (the engine runs elevated) searches only the system's module folder: the inherited PSModulePath begins with the user's own
  // Documents folder (or whatever HKCU\Environment says), and a module there would be loaded the first time the script names a cmdlet of it.
  public static string SystemModulePath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\Modules");
  public static ProcessStartInfo SystemModulesOnly(ProcessStartInfo info){info.Environment["PSModulePath"]=SystemModulePath;return info;}
  [DllImport("kernel32.dll")]static extern bool FreeConsole();
  public static bool PublicIp(string value) {
   if(string.IsNullOrWhiteSpace(value)||!IPAddress.TryParse(value,out var ip))return false;
   if(ip.AddressFamily==AddressFamily.InterNetwork){
    var b=ip.GetAddressBytes();
    return b[0]!=0&&b[0]!=10&&b[0]!=127&&b[0]<224&&!(b[0]==169&&b[1]==254)&&!(b[0]==172&&b[1]>=16&&b[1]<=31)&&!(b[0]==192&&b[1]==168)&&!(b[0]==100&&b[1]>=64&&b[1]<=127)&&!(b[0]==192&&b[1]==0&&b[2]==2)&&!(b[0]==192&&b[1]==0&&b[2]==0)&&!(b[0]==192&&b[1]==88&&b[2]==99)&&!(b[0]==198&&(b[1]==18||b[1]==19||b[1]==51))&&!(b[0]==203&&b[1]==0&&b[2]==113);
   }
   if(ip.AddressFamily==AddressFamily.InterNetworkV6){
    if(ip.IsIPv6LinkLocal||ip.IsIPv6SiteLocal||ip.IsIPv6Multicast||IPAddress.IsLoopback(ip)||ip.IsIPv6Teredo||ip.IsIPv4MappedToIPv6)return false;
    var b=ip.GetAddressBytes();
    bool allZero=true;for(int i=0;i<16;i++)if(b[i]!=0){allZero=false;break;}
    if(allZero)return false;
    bool first12Zero=true;for(int i=0;i<12;i++)if(b[i]!=0){first12Zero=false;break;}
    if(first12Zero)return false;   // ::a.b.c.d (IPv4-compatible)
    if(b[0]==0x00&&b[1]==0x64&&b[2]==0xff&&b[3]==0x9b)return false;   // 64:ff9b::/96 and 64:ff9b:1::/48 (NAT64: may point at a private IPv4)
    if((b[0]&0xfe)==0xfc)return false;
    if(b[0]==0x20&&b[1]==0x01&&b[2]==0x0d&&b[3]==0xb8)return false;
    if(b[0]==0x20&&b[1]==0x01&&b[2]==0x00&&(b[3]&0xfe)==0x02)return false;
    if(b[0]==0x20&&b[1]==0x01&&b[2]==0x00&&(b[3]&0xf0)==0x20)return false;
    if(b[0]==0x20&&b[1]==0x02)return false;
    if(b[0]==0x01&&b[1]==0x00&&b[2]==0&&b[3]==0)return false;
    return true;
   }
   return false;
  }
  static async Task<string> Tls(string ip,string host=null,CancellationToken ct=default) {
   host=host??Target;
   if(!PublicIp(ip))return AnswerSanity.IsBogus(ip)?AnswerSanity.Code:"DNS_UNEXPECTED_ANSWER";
   try {
    var addr=IPAddress.Parse(ip);
    using(var handler=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false}) {
     handler.SslOptions.RemoteCertificateValidationCallback=CertRevocation.Callback;
     handler.ConnectCallback=async (ctx,ct)=>{var s=new Socket(addr.AddressFamily,SocketType.Stream,ProtocolType.Tcp);try{await s.ConnectAsync(addr,443,ct);return new NetworkStream(s,true);}catch{s.Dispose();throw;}};
     using(var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(6)})using(var request=new HttpRequestMessage(HttpMethod.Head,"https://"+host+"/"))using(var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct))return "TLS_OK_HTTP_"+(int)response.StatusCode;
    }
   }catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}catch{return "HTTPS_CONNECTIVITY_FAILURE";}
  }
  // A provider's answer for a Google host can list several proxy addresses and one of them may be down (GeoHide's pool did on 2026-10-11).
  // Validation passes when at least one of the first four answer addresses completes a certificate-valid TLS handshake for that host;
  // checking only the first address made enabling fail at random. All answer addresses must still be public (checked by the caller).
  static async Task<string> FirstTls(string[] addresses,string host,CancellationToken ct) {
   var tested=addresses.Take(4).ToArray();
   var results=await Task.WhenAll(tested.Select(a=>Tls(a,host,ct)));
   return results.FirstOrDefault(r=>r.StartsWith("TLS_OK"))??results[0];
  }  static async Task<Candidate> CheckCandidate(string ip,string template=null,bool includePlain=false,CancellationToken ct=default) {
   using var live=new LiveTransport();
   var spec=new ProfileSpec{Id="preflight",Template=template??DefaultTemplate,Servers=new[]{ip}};
   var tasks=new List<Task<Reply>>();
   var allHosts=Hosts.Concat(new[]{"www.microsoft.com"}).ToArray();
   foreach(string h in allHosts)foreach(int type in new[]{1,28}) {
    if(includePlain) {
     tasks.Add(Wire.ProbeAsync(h,type,ip,false,1800,ct));tasks.Add(Wire.ProbeAsync(h,type,ip,true,1800,ct));
    }
    tasks.Add(live.QueryAsync(spec,ip,h,type,6000,ct));
   }
   var replies=await Task.WhenAll(tasks);
   var d=replies.FirstOrDefault(r=>r.Host==Target&&r.Type==1&&r.Transport.StartsWith("DoH"));
   var tlsByHost=new Dictionary<string,Task<string>>();
   foreach(string host in Hosts){
    var h0=replies.FirstOrDefault(r=>r.Host==host&&r.Type==1&&r.Transport.StartsWith("DoH"));
    tlsByHost[host]=h0!=null&&h0.Code=="DNS_OK"&&h0.Addresses!=null&&h0.Addresses.Length>0&&h0.Addresses.All(PublicIp)?FirstTls(h0.Addresses,host,ct):Task.FromResult("Not tested: "+(h0?.Code??"NO_DOH_REPLY"));
   }
   var d6pre=replies.FirstOrDefault(r=>r.Host==Target&&r.Type==28&&r.Transport.StartsWith("DoH"));
   Task<string> tls6=d6pre!=null&&d6pre.Code=="DNS_OK"&&d6pre.Addresses!=null&&d6pre.Addresses.Length>0&&d6pre.Addresses.All(PublicIp)?Tls(d6pre.Addresses[0],Target,ct):null;
   string dt=d!=null&&d.Code=="DNS_OK"&&d.Addresses!=null&&d.Addresses.Length>0&&d.Addresses.All(PublicIp)?await tlsByHost[Target]:"Not tested: "+(d?.Code??"NO_DOH_REPLY");
   bool dValid=d!=null&&d.Code=="DNS_OK"&&dt.StartsWith("TLS_OK")&&replies.Where(r=>r.Transport.StartsWith("DoH")).All(r=>r.Type==1?r.Code=="DNS_OK":r.Code=="DNS_OK"||r.Code=="DNS_NODATA");
   var targetAnswers=new Dictionary<string,string[]>();
   foreach(string host in Hosts){
    var hd=replies.FirstOrDefault(r=>r.Host==host&&r.Type==1&&r.Transport.StartsWith("DoH"));
    bool tlsD=hd!=null&&hd.Code=="DNS_OK"&&hd.Addresses!=null&&hd.Addresses.Length>0&&hd.Addresses.All(PublicIp)&&(await tlsByHost[host]).StartsWith("TLS_OK");
    dValid&=tlsD;
    targetAnswers[host]=dValid&&hd!=null&&hd.Addresses!=null?hd.Addresses:Array.Empty<string>();
   }
   string[] targetA=dValid&&d!=null&&d.Addresses!=null?d.Addresses:Array.Empty<string>();

   var d6=replies.FirstOrDefault(r=>r.Host==Target&&r.Type==28&&r.Transport.StartsWith("DoH"));
   var targetAnswersV6=new Dictionary<string,string[]>();
   foreach(string host in Hosts){
    var hd6=replies.FirstOrDefault(r=>r.Host==host&&r.Type==28&&r.Transport.StartsWith("DoH"));
    targetAnswersV6[host]=hd6!=null&&hd6.Addresses!=null?hd6.Addresses:Array.Empty<string>();
   }
   string[] targetAAAA=d6!=null&&d6.Addresses!=null?d6.Addresses:Array.Empty<string>();
   string dt6="Not tested: NODATA";
   string ipv6Status="NODATA (dual-stack clients fall back to validated IPv4)";
   if(d6!=null&&d6.Code=="DNS_OK"&&d6.Addresses!=null&&d6.Addresses.Length>0){
    if(d6.Addresses.All(PublicIp)){
     try{
      dt6=await tls6;
      ipv6Status=dt6.StartsWith("TLS_OK")?"Validated (IPv6 TLS passed)":"Warning: AAAA present but IPv6 TLS returned "+dt6;
     }catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}catch{
      dt6="HTTPS_CONNECTIVITY_FAILURE";
      ipv6Status="Warning: AAAA present but IPv6 TLS unreachable";
     }
    }else{
     dt6="DNS_UNEXPECTED_ANSWER: Bogon/private IPv6";
     ipv6Status="Warning: AAAA contains non-public IPv6 addresses";
    }
   }

   bool pValid=false;
   string pt="Not tested: standard preflight enforces encrypted DoH";
   string plainAssessment="Plaintext probes disabled in standard DoH preflight; explicit opt-in required";
   if(includePlain) {
    var u=replies.FirstOrDefault(r=>r.Host==Target&&r.Type==1&&r.Transport=="UDP");
    var tcp=replies.FirstOrDefault(r=>r.Host==Target&&r.Type==1&&r.Transport=="TCP");
    pt=u!=null&&u.Code=="DNS_OK"&&u.Addresses!=null&&u.Addresses.Length>0&&u.Addresses.All(PublicIp)?await Tls(u.Addresses[0],ct:ct):"Not tested: "+(u?.Code??"NO_UDP_REPLY");
    pValid=u!=null&&u.Code=="DNS_OK"&&tcp!=null&&tcp.Code=="DNS_OK"&&pt.StartsWith("TLS_OK")&&u.Addresses!=null&&tcp.Addresses!=null&&u.Addresses.Order().SequenceEqual(tcp.Addresses.Order())&&replies.Where(r=>r.Transport=="UDP"||r.Transport=="TCP").All(r=>r.Type==1?r.Code=="DNS_OK":r.Code=="DNS_OK"||r.Code=="DNS_NODATA");
    bool same=d!=null&&u!=null&&d.Addresses!=null&&u.Addresses!=null&&d.Addresses.Order().SequenceEqual(u.Addresses.Order());
    foreach(string host in Hosts){
     var hd=replies.FirstOrDefault(r=>r.Host==host&&r.Type==1&&r.Transport.StartsWith("DoH"));
     var hu=replies.FirstOrDefault(r=>r.Host==host&&r.Type==1&&r.Transport=="UDP");
     var ht=replies.FirstOrDefault(r=>r.Host==host&&r.Type==1&&r.Transport=="TCP");
     bool tlsP=hu!=null&&hu.Code=="DNS_OK"&&hu.Addresses!=null&&hu.Addresses.Length>0&&hu.Addresses.All(PublicIp)&&(await Tls(hu.Addresses[0],host,ct)).StartsWith("TLS_OK");
     pValid&=tlsP&&ht!=null&&ht.Code=="DNS_OK"&&hu.Addresses!=null&&ht.Addresses!=null&&hu.Addresses.Order().SequenceEqual(ht.Addresses.Order());
     same&=hd!=null&&hu!=null&&hd.Addresses!=null&&hu.Addresses!=null&&hd.Addresses.Order().SequenceEqual(hu.Addresses.Order());
    }
    if(dValid&&!same)pValid=false;
    plainAssessment=dValid&&!same?"DNS_UNEXPECTED_ANSWER: UDP/TCP differ from pinned encrypted DoH; possible DNS interception/TUN":pValid?"Plaintext probes validated (diagnostic mode only)":"Plaintext validation failed";
   }

   return new Candidate{Server=ip,DohValidated=dValid,PlainValidated=pValid,TargetA=targetA,TargetAAAA=targetAAAA,TargetAnswers=targetAnswers,TargetAnswersV6=targetAnswersV6,TargetTls=tlsByHost.ToDictionary(kv=>kv.Key,kv=>kv.Value.GetAwaiter().GetResult()),DohTls=dt,DohTlsV6=dt6,PlainTls=pt,Ipv6Status=ipv6Status,Probes=replies,PlainAssessment=plainAssessment};
  }
  public static async Task<Candidate[]> NetworkCandidates(string[] servers=null,string template=null,bool includePlain=false,CancellationToken ct=default) {
   string[] targetServers=servers!=null&&servers.Length>0?servers:Servers;
   var results=await Task.WhenAll(targetServers.Select(ip=>CheckCandidate(ip,template,includePlain,ct)));
   ct.ThrowIfCancellationRequested();return results;
  }
  public static async Task<object> Network(string[] servers=null,string template=null,bool includePlain=false) {
   var candidates=await NetworkCandidates(servers,template,includePlain);
   bool allAaaaNoData=candidates.All(c=>c.TargetAAAA==null||c.TargetAAAA.Length==0);
   string ipv6Report;
   if(allAaaaNoData) {
    ipv6Report="DNS_NODATA: No AAAA records returned by resolver; dual-stack clients fall back to validated IPv4 path.";
   } else {
    var v6Addrs=candidates.SelectMany(c=>c.TargetAAAA??Array.Empty<string>()).Distinct();
    ipv6Report="DUAL_STACK_ACTIVE: Resolver returned AAAA records ("+string.Join(", ",v6Addrs)+"); IPv6 destination transport is active on dual-stack hosts and not restricted to IPv4 SNI proxy.";
   }
   return new {Version=AppInfo.Version,TimestampUtc=DateTime.UtcNow,Resolver=!string.IsNullOrWhiteSpace(template)?template:DefaultTemplate,Target,Protocol="RFC8484 HTTP/2 with default TLS validation",Candidates=candidates,Ipv6=ipv6Report,Application="AUTH_REQUIRED_FOR_E2E_TEST",Trust="Provider can route selected TLS through its SNI proxy; deployed infrastructure not independently audited"};
  }
  public static async Task<(int Code,string Output)> Engine(string action,bool trust=false,bool plain=false,string[] servers=null,string template=null,string profileName=null,CancellationToken cancellationToken=default,TimeSpan? deadline=null,bool repair=false,string expectedRevision=null,string[] targetScope=null,long? expectedUntilMs=null) {
   TimeSpan limit=deadline??Deadline(action);
   cancellationToken.ThrowIfCancellationRequested();
   ScriptStore.Prepared prepared;
   try{bool admin=IsAdmin();prepared=await Task.Run(()=>ScriptStore.Prepare(admin));ScriptStore.Verify(prepared);}
   catch(Exception ex){return(1,JsonSerializer.Serialize(new{State="Failed (restorable)",Code=ex.Message.StartsWith("SCRIPT_TAMPERED")?"SCRIPT_TAMPERED":"SCRIPT_STORE_FAILED",Detail=ex.Message},JsonOptions));}
   var info=SystemModulesOnly(new ProcessStartInfo(PowerShell){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8});
   foreach(string a in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",prepared.Script("Engine.ps1"),"-Action",action,"-ExePath",prepared.ExePath})info.ArgumentList.Add(a);
   if(trust)info.ArgumentList.Add("-TrustProxy");if(plain)info.ArgumentList.Add("-Plain");if(repair&&action=="enable")info.ArgumentList.Add("-Repair");
   // Targets and the default profile are handed to the engine from the one catalog; the engine holds no copy of its own.
   if(targetScope!=null&&!IsKnownScope(targetScope))throw new ArgumentException("Unsupported native scope");
   info.ArgumentList.Add("-Targets");info.ArgumentList.Add(string.Join(",",targetScope??Targets));
   if((servers==null||servers.Length==0)&&string.IsNullOrWhiteSpace(template))servers=Servers;
   if(string.IsNullOrWhiteSpace(template))template=DefaultTemplate;
   if(servers!=null&&servers.Length>0){info.ArgumentList.Add("-Servers");info.ArgumentList.Add(string.Join(",",servers));}
   info.ArgumentList.Add("-Template");info.ArgumentList.Add(template);
   if(!string.IsNullOrWhiteSpace(profileName)){info.ArgumentList.Add("-ProfileName");info.ArgumentList.Add(profileName);}
   if(expectedRevision!=null){if(!System.Text.RegularExpressions.Regex.IsMatch(expectedRevision,"^[A-F0-9]{64}$")||expectedUntilMs==null)throw new ArgumentException("Invalid prepared native revision/window");info.ArgumentList.Add("-ExpectedRevision");info.ArgumentList.Add(expectedRevision);info.ArgumentList.Add("-ExpectedUntilMs");info.ArgumentList.Add(expectedUntilMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));}
   using(var p=Process.Start(info)){
    if(p==null)return(1,JsonSerializer.Serialize(new{State="Failed (restorable)",Code="ENGINE_START_FAILED",Detail="Could not start PowerShell process"},JsonOptions));
    var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
    using(var cts=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)){
     cts.CancelAfter(limit);
     try{
      await p.WaitForExitAsync(cts.Token);
      string output=await stdout;string err=await stderr;
      if(string.IsNullOrWhiteSpace(output))output=JsonSerializer.Serialize(new{State="Failed (restorable)",Code="ENGINE_ERROR",Detail=err},JsonOptions);
      return(p.ExitCode,output);
     }catch(OperationCanceledException){
      try{p.Kill(entireProcessTree:true);}catch{}
      // The caller cancelled (window closed / operation replaced): that is not a Windows timeout and must never read as "WMI does not answer".
      if(cancellationToken.IsCancellationRequested)throw;
      return(1460,JsonSerializer.Serialize(new{State="Failed (restorable)",Code="ENGINE_TIMEOUT",Detail="Engine execution exceeded deadline ("+(int)limit.TotalSeconds+"s) and was terminated"},JsonOptions));
     }
    }
   }
  }
  internal static bool IsAdmin()=>new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
  static string MutationReceipt(string id)=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),AppIdentity.StorageName,"operation-"+id+".json");
  // `started` runs right after Windows created the elevated process, i.e. after the user confirmed the UAC prompt (the window says what is happening now).
  public static async Task<(int Code,string Output)> Elevate(string action,bool trust=false,bool plain=false,string[] servers=null,string template=null,string profileName=null,CancellationToken cancellationToken=default,bool repair=false,Action started=null,string expectedRevision=null,long? expectedUntilMs=null) {
   cancellationToken.ThrowIfCancellationRequested();
   if(expectedRevision!=null&&(!System.Text.RegularExpressions.Regex.IsMatch(expectedRevision,"^[A-F0-9]{64}$")||expectedUntilMs==null))throw new ArgumentException("Invalid prepared native revision/window");
   string id=Guid.NewGuid().ToString("N");
   string srvArg=servers!=null&&servers.Length>0?" --servers "+string.Join(",",servers):"";
   string tmplArg=!string.IsNullOrWhiteSpace(template)?" --template \""+template+"\"":"";
   string profArg=!string.IsNullOrWhiteSpace(profileName)?" --profile \""+profileName+"\"":"";
   var info=new ProcessStartInfo(Environment.ProcessPath){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,Arguments="--"+action+(trust?" --trust-proxy":"")+(plain?" --plain":"")+(repair&&action=="enable"?" --repair":"")+(Gemini?" --gemini":"")+(Google?" --browser":"")+(Core?"":" --no-core")+srvArg+tmplArg+profArg+(expectedRevision!=null?" --prepared-revision "+expectedRevision+" --prepared-until-ms "+expectedUntilMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture):"")+" --operation-id "+id};
   try {
    cancellationToken.ThrowIfCancellationRequested();
    var p=Process.Start(info);
    try{
     if(p!=null)Recovery.TrackChild(p);
     if(p!=null)started?.Invoke();
     if(p==null)return(1,JsonSerializer.Serialize(new{State="Failed (restorable)",Code="ELEVATION_START_FAILED",Note="Could not start elevated process"},JsonOptions));
     using(var cts=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)){
      cts.CancelAfter(ElevationWait(action));
      try{
       await p.WaitForExitAsync(cts.Token);
      }catch(OperationCanceledException){
       // A non-elevated parent cannot terminate an elevated child; it may still be finishing. Never claim otherwise.
       return(1460,JsonSerializer.Serialize(new{State="Failed (restorable)",Code="ELEVATION_TIMEOUT",Note="Waiting deadline ("+(int)ElevationWait(action).TotalSeconds+"s) elapsed; the elevated operation may still be running. Re-check status."},JsonOptions));
      }
     }
     string file=MutationReceipt(id);
     if(File.Exists(file)){
      string output=File.ReadAllText(file);
      using(var doc=JsonDocument.Parse(output)){
       if(doc.RootElement.TryGetProperty("OperationId",out var nonce)&&nonce.GetString()==id&&doc.RootElement.TryGetProperty("ExitCode",out var exit)&&exit.GetInt32()==p.ExitCode)
        return(p.ExitCode,output);
      }
     }
     return(p.ExitCode==0?1:p.ExitCode,JsonSerializer.Serialize(new{State="Failed (restorable)",Code="NO_MATCHING_OPERATION_RECEIPT",ChildExitCode=p.ExitCode,Note="Check actual status; this action's result is unknown"},JsonOptions));
    }finally{Recovery.ReleaseCompletedChildren();}
   }catch(System.ComponentModel.Win32Exception ex) when(ex.NativeErrorCode==1223){return(1223,JsonSerializer.Serialize(new{State="Operation cancelled",Code="UAC_CANCELLED",Note="No elevated operation was started"},JsonOptions));}
  }
  public static string Receipt=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),AppIdentity.StorageName,"last-operation.json");
  static void SaveReport(string output) {Directory.CreateDirectory(Path.GetDirectoryName(Receipt));if(output.Length<=262144)File.WriteAllText(Receipt,output,new UTF8Encoding(false));}
  static string SaveMutation(string output,string id,int code) {
   if(!Guid.TryParseExact(id,"N",out _))throw new ArgumentException("Invalid operation ID");
   var data=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(output);data["OperationId"]=JsonSerializer.SerializeToElement(id);data["ExitCode"]=JsonSerializer.SerializeToElement(code);string text=JsonSerializer.Serialize(data,JsonOptions);
   string file=MutationReceipt(id);string directory=Path.GetDirectoryName(file);
   if(!Directory.Exists(directory))throw new IOException("Protected journal directory not initialized; operation receipt unavailable");
   File.WriteAllText(file,text,new UTF8Encoding(false));
   // Bounded metadata receipts in the app's protected directory; never prune journals.
   var old=Directory.GetFiles(directory,"operation-*.json").Where(p=>System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p),@"^operation-[a-f0-9]{32}\.json$")).OrderByDescending(File.GetLastWriteTimeUtc).Skip(8);
   foreach(string path in old)try{File.Delete(path);}catch{}
   return text;
  }
  // Built from the embedded, versioned profiles.json (ids, P-slots and aliases all resolve to the same pinned servers + DoH template).
  public static readonly Dictionary<string,(string[] Servers,string Template)> KnownProfiles=BuildKnownProfiles();
  static Dictionary<string,(string[] Servers,string Template)> BuildKnownProfiles() {
   var map=new Dictionary<string,(string[],string)>(StringComparer.OrdinalIgnoreCase);
   foreach(var p in ProfileCatalog.BuiltIn.Profiles) {
    var value=(p.Servers,p.Template);
    map[p.Id]=value;if(!string.IsNullOrEmpty(p.Slot))map[p.Slot]=value;
    foreach(string alias in p.Aliases??new string[0])map[alias]=value;
   }
   return map;
  }
  public static void ValidateCliArgs(string[] args) {
   if(args==null||args.Length==0)return;
   if(args.Any(a=>a.StartsWith("--prepared-",StringComparison.OrdinalIgnoreCase))) {
    int revision=Array.IndexOf(args,"--prepared-revision"),until=Array.IndexOf(args,"--prepared-until-ms");
    if(revision<0||until<0||revision+1>=args.Length||until+1>=args.Length||args.Count(a=>a.StartsWith("--prepared-",StringComparison.OrdinalIgnoreCase))!=2||
     !System.Text.RegularExpressions.Regex.IsMatch(args[revision+1],"^[A-F0-9]{64}$")||!long.TryParse(args[until+1],out _)||
     args.Count(a=>a=="--enable"||a=="--restore")!=1)throw new ArgumentException("Prepared native mutation requires canonical revision and expiry");
    var allowed=new[]{"--enable","--restore","--trust-proxy","--plain","--repair","--servers","--template","--profile","--operation-id","--prepared-revision","--prepared-until-ms"};
    for(int i=0;i<args.Length;i++) {
     if(!allowed.Contains(args[i],StringComparer.Ordinal))throw new ArgumentException("Prepared native helper cannot mix other commands");
     if(new[]{"--servers","--template","--profile","--operation-id","--prepared-revision","--prepared-until-ms"}.Contains(args[i],StringComparer.Ordinal))i++;
    }
   }
   if(args.Any(a=>a.StartsWith("--assisted-",StringComparison.OrdinalIgnoreCase))) {
    var commands=new[]{"--assisted-connect","--assisted-native","--assisted-direct","--assisted-disable","--assisted-do-nothing","--assisted-observe","--assisted-inspect","--assisted-contract-test"};
    if(!commands.Contains(args[0],StringComparer.Ordinal)||args.Count(a=>a.StartsWith("--assisted-",StringComparison.OrdinalIgnoreCase))!=1)throw new ArgumentException("Assisted commands must be standalone with canonical spelling");
    if(args.Count(a=>a=="--profile")>1||args.Count(a=>a=="--trust-proxy")>1||args.Count(a=>a=="--repair")>1)throw new ArgumentException("Duplicate assisted option");
    for(int i=1;i<args.Length;i++) {
     if(args[i]=="--profile"&&i+1<args.Length&&!args[i+1].StartsWith("--",StringComparison.Ordinal)){i++;continue;}
     if(args[i]=="--trust-proxy"&&(args[0]=="--assisted-connect"||args[0]=="--assisted-native"))continue;
     if(args[i]=="--repair"&&args[0]=="--assisted-native")continue;
     throw new ArgumentException("Unsupported argument for assisted command");
    }
    if((args[0]=="--assisted-contract-test"||args[0]=="--assisted-do-nothing")&&args.Length!=1)throw new ArgumentException("Pure assisted command takes no options");
    return;
   }
   if(CatalogCli.IsCommand(args)){CatalogCli.Validate(args);return;}
   if(args.Any(a=>a.StartsWith("--scenario-",StringComparison.OrdinalIgnoreCase))) {
    bool standalone=args.Length==1&&(args[0]=="--scenario-contract-test"||args[0]=="--scenario-probe-system")||args.Length==2&&args[0]=="--scenario-probe-provider"&&!args[1].StartsWith("--",StringComparison.Ordinal);
    if(!standalone)throw new ArgumentException("Scenario probes must be standalone with canonical spelling");
    return;
   }
   if(args.Any(a=>a.StartsWith("--policy-",StringComparison.OrdinalIgnoreCase))) {
    bool standalone=args.Length==1&&(args[0]=="--policy-contract-test"||args[0]=="--policy-legacy-preview"||args[0]=="--policy-direct-preview")||args.Length==2&&args[0]=="--policy-dry-run"&&!args[1].StartsWith("--",StringComparison.Ordinal);
    if(!standalone)throw new ArgumentException("Policy preview commands must be used standalone with canonical spelling");
   }
   if(args.Contains("--help")||args.Contains("-h")||args.Contains("/?"))return;
   var knownFlags=new HashSet<string>(StringComparer.OrdinalIgnoreCase){
    "--network","--plain","--trust-proxy","--self-test","--integration","--port53","--doctor","--policy-contract-test","--policy-legacy-preview","--policy-direct-preview",
    "--diagnose","--export-report","--enable","--restore","--status",
    "--autostart-status","--autostart-enable","--autostart-disable",
    "--benchmark","--no-history","--profiles-list","--verify-scripts","--bench","--version","--preflight","--states","--codes-list","--repair","--gemini","--google","--browser","--no-core","--ui-surfaces","--ui-stress","--en"
   };
   if(args.Length==4&&args[0]=="--tls-probe")return;
   var knownValuedFlags=new HashSet<string>(StringComparer.OrdinalIgnoreCase){
    "--servers","--template","--profile","--ui-smoke","--dpi","--probe-domain","--export-icon","--bootstrap","--policy-dry-run","--prepared-revision","--prepared-until-ms",
    "--attempts","--budget","--only","--benchmark-synthetic","--connect-synthetic","--bootstrap-synthetic","--bootstrap-resolvers","--alternative-synthetic","--health-synthetic","--export-scripts","--config-export","--config-import","--recovery-instructions"
   };
   for(int i=0;i<args.Length;i++){
    string arg=args[i];
    if(arg=="--system-dns"||arg=="--system-dns-batch"){
     if(i+2>=args.Length||args[i+1].StartsWith("--")||args[i+2].StartsWith("--"))throw new ArgumentException("--system-dns requires domain and record type");
     i+=2;continue;
    }
    if(arg=="--operation-id"){
     if(i+1<args.Length&&!args[i+1].StartsWith("--"))i++;
     continue;
    }
    if(knownValuedFlags.Contains(arg)){
     if(i+1>=args.Length||args[i+1].StartsWith("--"))throw new ArgumentException("Missing value for "+arg+" argument.");
     i++;continue;
    }
    if(!knownFlags.Contains(arg))throw new ArgumentException("Unknown argument: "+arg);
   }
   if(args.Contains("--network")){
    var allowedInNetwork=new HashSet<string>(StringComparer.OrdinalIgnoreCase){
     "--network","--plain","--servers","--template","--profile","--operation-id","--gemini","--google","--browser","--no-core"
    };
    for(int i=0;i<args.Length;i++){
     string a=args[i];
     if(a.StartsWith("--")&&!allowedInNetwork.Contains(a))throw new ArgumentException("Unrecognized argument for --network: "+a);
     if(a=="--servers"||a=="--template"||a=="--profile")i++;
     else if(a=="--operation-id"&&i+1<args.Length&&!args[i+1].StartsWith("--"))i++;
    }
   }
  }
  public static (string[] Servers,string Template,bool Plain) ParseNetworkArgs(string[] args) {
   ValidateCliArgs(args);
   bool hasServers=args.Contains("--servers");
   bool hasTemplate=args.Contains("--template");
   bool hasProfile=args.Contains("--profile");
   bool plain=args.Contains("--plain");
   string[] profServers=null;
   string profTemplate=null;
   if(hasProfile){
    int pIdx=Array.IndexOf(args,"--profile");
    if(pIdx+1>=args.Length||args[pIdx+1].StartsWith("--"))throw new ArgumentException("Missing value for --profile argument.");
    string pName=args[pIdx+1].Trim().Trim('"', '\'');
    if(string.IsNullOrWhiteSpace(pName))throw new ArgumentException("Empty profile name provided to --profile.");
    if(!KnownProfiles.TryGetValue(pName,out var pInfo))throw new ArgumentException((ProfileCatalog.BuiltIn.FindRetired(pName)!=null?"Profile retired from the catalog: ":"Unknown profile: ")+pName);
    profServers=pInfo.Servers;profTemplate=pInfo.Template;
   }
   string[] servers=null;
   if(hasServers){
    int sIdx=Array.IndexOf(args,"--servers");
    if(sIdx+1>=args.Length||args[sIdx+1].StartsWith("--"))throw new ArgumentException("Missing value for --servers argument.");
    string raw=args[sIdx+1].Trim().Trim('"', '\'');
    if(string.IsNullOrWhiteSpace(raw))throw new ArgumentException("Empty server list provided to --servers.");
    string[] parts=raw.Split(new[]{',',';',' '},StringSplitOptions.RemoveEmptyEntries);
    if(parts.Length==0)throw new ArgumentException("Empty server list provided to --servers.");
    var list=new List<string>();
    foreach(var p in parts){
     string clean=p.Trim().Trim('[',']','"', '\'');
     if(!IPAddress.TryParse(clean,out var ip)||(ip.AddressFamily!=AddressFamily.InterNetwork&&ip.AddressFamily!=AddressFamily.InterNetworkV6))
      throw new ArgumentException("Invalid IP address in --servers: "+p);
     list.Add(ip.ToString());
    }
    servers=list.ToArray();
   }else if(hasProfile){
    servers=profServers;
   }
   string template=null;
   if(hasTemplate){
    int tIdx=Array.IndexOf(args,"--template");
    if(tIdx+1>=args.Length||args[tIdx+1].StartsWith("--"))throw new ArgumentException("Missing value for --template argument.");
    string raw=args[tIdx+1].Trim().Trim('"', '\'');
    if(string.IsNullOrWhiteSpace(raw))throw new ArgumentException("Empty template URL provided to --template.");
    if(!Uri.TryCreate(raw,UriKind.Absolute,out var uri)||uri.Scheme!=Uri.UriSchemeHttps)
     throw new ArgumentException("Invalid DoH template URL in --template. Must be an absolute https:// URL: "+raw);
    template=uri.AbsoluteUri;
   }else if(hasProfile){
    template=profTemplate;
   }
   return(servers,template,plain);
  }
  [STAThread]public static int Main(string[] args) {
   PerfLog.Mark("main");Console.OutputEncoding=Encoding.UTF8;
   if(Broker.TryRun(args,out int brokerExit))return brokerExit;
   if(args.Length>=1&&(args[0]=="--elevated-startup-enable"||args[0]=="--elevated-startup-disable"||args[0]=="--elevated-startup-xml"))return ElevatedStartup.RunCli(args);
   string catalogUiIdentity=null;
   bool minimized=args.Contains("--minimized");if(minimized)args=args.Where(x=>x!="--minimized").ToArray();
   // gemini (also handed to the elevated copy) adds the catalog's Gemini hosts to every operation of this process; it takes no value.
   if(args.Any(x=>string.Equals(x,"--gemini",StringComparison.OrdinalIgnoreCase))){Gemini=true;args=args.Where(x=>!string.Equals(x,"--gemini",StringComparison.OrdinalIgnoreCase)).ToArray();}
   // browser adds the catalog's Google suffixes; --no-core drops the core hosts (Antigravity). Both take no value.
   if(args.Any(x=>string.Equals(x,"--browser",StringComparison.OrdinalIgnoreCase))){Google=true;args=args.Where(x=>!string.Equals(x,"--browser",StringComparison.OrdinalIgnoreCase)).ToArray();}
   if(args.Any(x=>string.Equals(x,"--no-core",StringComparison.OrdinalIgnoreCase))){Core=false;args=args.Where(x=>!string.Equals(x,"--no-core",StringComparison.OrdinalIgnoreCase)).ToArray();}
   // google is the older spelling of "everything": the Gemini hosts and the Google suffixes together.
   if(args.Any(x=>string.Equals(x,"--google",StringComparison.OrdinalIgnoreCase))){Gemini=true;Google=true;args=args.Where(x=>!string.Equals(x,"--google",StringComparison.OrdinalIgnoreCase)).ToArray();}
   try {
    if(args.Length>0&&(args.Contains("--help")||args.Contains("-h")||args.Contains("/?"))){
     Console.WriteLine("Antigravity-Gemini DNS: командная строка (без аргументов открывается окно)\n\nМост (--enable и --restore просят права администратора):\n  --status | --diagnose | --enable [--repair] | --restore\n      --repair: заново применить мост, который перестал проходить проверку\n  --network [--servers <csv>] [--template <url>] [--profile <имя>] [--plain]   проверка провайдера, только чтение\n\nАвтозапуск:\n  --autostart-status | --autostart-enable | --autostart-disable\n\nПрофили (только чтение):\n  --profiles-list\n  --benchmark [--attempts N] [--budget СЕКУНДЫ] [--only <профили>] [--no-history]   замер каталога\n  --bootstrap <хост|https-ссылка>   найти адреса и проверить: TLS по имени, DoH для всех доменов\n  --bootstrap-resolvers <хост>   какие резолверы и провайдеры опросят для хоста (без сети)\n\nСлужебное:\n  --version | --self-test | --verify-scripts | --export-scripts <папка>\n  --gemini   добавить к любой команде моста домены Gemini из каталога (сами домены задать нельзя)\n  --google   «+ Браузер»: домены Gemini и суффиксы Google из каталога (.google.com и др.)\n  --preflight   проверка WMI перед запросом UAC; код 1 значит, что WMI не отвечает\n  --recovery-instructions <state.json|->   шаги ручного восстановления по журналу (- = текущий журнал)\n  --codes-list   таблица сообщений: код, текст, действие\n  --doctor   самодиагностика по слоям (только чтение, JSON; код 1 — отказ, 2 — неполная или устаревшая проверка)\n  --ui-smoke <output.png> [--bench] [--states] [--en] [--dpi N]   снимки окна (--en: на английском, --dpi: другой масштаб)\n  --ui-surfaces   открыть и закрыть меню трея, окно сообщения, диалог сохранения, подсказку и проверить буфер обмена");
     Console.WriteLine("\nS02, только предварительный план (без применения и сетевых запросов):\n  --policy-legacy-preview [--gemini | --google]   совместимость каталога; trust/capabilities пока не подтверждены\n  --policy-direct-preview [--gemini | --google]   Direct без изменений; готовность сценария не проверяется\n  --policy-dry-run <request.json>   ограниченный контракт JSON, только моделирование\n  --policy-contract-test   чистые offline проверки компилятора");
     Console.WriteLine("\nЛокальный тестовый подписанный каталог (без изменения native policy и TrustGrant):\n  --catalog-local-ui <папка> <roots.json>   окно с локальными проверенными сценариями\n  --catalog-local-status <папка> <roots.json>\n  --catalog-local-import <папка> <roots.json> <signed.json>\n  --catalog-local-rollback <папка> <roots.json>\n  --catalog-contract-test   offline проверки подписи и lifecycle; roots только local-test");
     return 0;
    }
    ValidateCliArgs(args);
    if(args.Length==3&&args[0]=="--catalog-local-ui"){RuntimeCatalog.Configure(args[1],args[2]);catalogUiIdentity=EvidenceContext.Hash(Path.GetFullPath(args[1])+"|"+Path.GetFullPath(args[2]));args=Array.Empty<string>();}
    if(CatalogCli.TryRun(args,out int catalogExit))return catalogExit;
    if(args.Length==1&&args[0]=="--assisted-contract-test")return OfflineTests.RunAssistedTests();
    if(args.Length>0&&args[0].StartsWith("--assisted-",StringComparison.Ordinal)) {
     using var backend=new LiveAssistedBackend();var coordinator=new AssistedCoordinator(backend);
     string profile=ProfileCatalog.BuiltIn.Default;int at=Array.IndexOf(args,"--profile");if(at>=0)profile=args[at+1];
     var p=ProfileCatalog.BuiltIn.Find(profile)??throw new ArgumentException("Assisted CLI requires an embedded catalog profile");
     var mode=args[0] switch{"--assisted-connect"=>AssistedMode.Auto,"--assisted-native"=>AssistedMode.Native,"--assisted-direct"=>AssistedMode.Direct,
      "--assisted-disable"=>AssistedMode.Disable,"--assisted-do-nothing"=>AssistedMode.DoNothing,_=>AssistedMode.Observe};
     var request=new AssistedRequest(mode,p.Id,p.Template,p.Servers.ToImmutableArray(),Targets.ToImmutableArray(),Gemini,Google,args.Contains("--repair"),Core);
     if(args[0]=="--assisted-inspect"){Console.WriteLine(PolicyJson.Write(backend.Capture(request,default).GetAwaiter().GetResult()));return 2;}
     var result=coordinator.Run(request,(preview,ct)=>Task.FromResult(args.Contains("--trust-proxy")&&!ct.IsCancellationRequested)).GetAwaiter().GetResult();
     Console.WriteLine(PolicyJson.Write(result));return result.Outcome==AssistedOutcome.NativeVerified||result.Outcome==AssistedOutcome.DisabledVerified||result.Outcome==AssistedOutcome.NoChange?0:result.Outcome==AssistedOutcome.DirectVerified?2:1;
    }
    // Bounded diagnostics: no settings write, engine staging, elevation, or repair.
    if(args.Length==1&&args[0]=="--scenario-contract-test")return OfflineTests.RunScenarioTests();
    if(args.Length>0&&(args[0]=="--scenario-probe-system"||args[0]=="--scenario-probe-provider")) {
     var transport=new LiveScenarioTransport(args.Length==2?args[1]:null);
     using var epoch=new ScenarioNetworkEpoch();var context=epoch.Capture(transport.ProviderRevision);
     var receipt=ScenarioProbes.Run(transport,context,()=>epoch.Capture(transport.ProviderRevision),args.Length==2).GetAwaiter().GetResult();
     Console.WriteLine(PolicyJson.Write(receipt));return 2; // Transport evidence alone cannot establish application readiness.
    }
    // Pure entry points: no settings load/save, probes, engine staging or elevation.
    if(args.Any(a=>a.StartsWith("--policy-",StringComparison.OrdinalIgnoreCase))) {
     if(args.Length==1&&args[0]=="--policy-contract-test")return OfflineTests.RunPolicyContracts();
     CompileRequest previewRequest;
     if(args.Length==2&&args[0]=="--policy-dry-run") {
      if(new FileInfo(args[1]).Length>262144)throw new FormatException("Policy document exceeds 256 KiB");
      previewRequest=PolicyJson.Read<CompileRequest>(File.ReadAllText(args[1]));
     }else if(args.Length==1&&(args[0]=="--policy-legacy-preview"||args[0]=="--policy-direct-preview")) {
      var migration=LegacyScenarioAdapter.Migrate(ProfileCatalog.BuiltIn,new AppSettings{NoSave=true,Core=Core,Gemini=Gemini,Google=Google});
      var scenario=migration.Pack.Scenarios.Single(x=>x.Id==migration.SelectedScenario);
      var scope=migration.Pack.Dependencies.Where(x=>scenario.Critical.Contains(x.Id)).Select(x=>x.Namespace).ToImmutableArray();
      bool directPreview=args[0]=="--policy-direct-preview";
      previewRequest=new CompileRequest(1,migration.Pack,System.Collections.Immutable.ImmutableArray.Create(new BindingRequest(scenario.Id,directPreview?RouteKind.Direct:RouteKind.NativeDoh,directPreview?"":ProfileCatalog.BuiltIn.Default,"preview",scope,System.Collections.Immutable.ImmutableArray<string>.Empty)),migration.Providers,System.Collections.Immutable.ImmutableArray<TrustGrant>.Empty,SharedDomainPolicy.Reject,new PolicyContext("unobserved-preview","unobserved-preview",System.Collections.Immutable.ImmutableArray<PolicyResource>.Empty));
     }else throw new ArgumentException("Policy preview commands must be used standalone");
     var preview=PolicyCompiler.Compile(previewRequest,DateTimeOffset.UtcNow);Console.WriteLine(PolicyJson.Write(preview));return preview.Blocked?2:0;
    }
    if(args.Length==1&&args[0]=="--version"){Console.WriteLine(AppInfo.Version);return 0;}
    if(args.Length==1&&args[0]=="--codes-list"){Console.WriteLine(JsonSerializer.Serialize(CodeTable.All.Select(i=>new{i.Code,i.Title,i.Text,i.Action,Kind=i.Kind.ToString()}).ToArray(),JsonOptions));return 0;}
    // Read-only: who listens on local port 53 (owner PID, process name). Exit 0 = port free, 1 = busy; the JSON lists the holders.
    if(args.Length==1&&args[0]=="--port53"){var holders=LocalPort53.Find();Console.WriteLine(JsonSerializer.Serialize(holders,JsonOptions));return holders.Length==0?0:1;}
    // Read-only, unelevated: Doctor's layers as JSON. Exit 0 = off or complete bounded network evidence; 1 = measured failure; 2 = incomplete/stale evidence.
    if(args.Length==1&&args[0]=="--doctor"){var rep=Doctor.RunAsync(new LiveDoctorProbe(),default).GetAwaiter().GetResult();Console.WriteLine(rep.ToJson());return rep.ExitCode;}
    // Preferences in one file (v1.4). Touches only settings.json: the bridge, autostart and the journal are not changed. A running window keeps its own copy until it saves.
    if(args.Length==2&&args[0]=="--config-export"){File.WriteAllText(args[1],ConfigFile.Export(AppSettings.Load()));Console.WriteLine("CONFIG_EXPORTED "+args[1]);return 0;}
    if(args.Length==2&&args[0]=="--config-import"){var res=ConfigFile.ImportAndSave(File.ReadAllText(args[1]),AppSettings.Load(),s=>s.SaveChecked());Console.WriteLine(JsonSerializer.Serialize(new{res.Ok,res.Code,res.Text},JsonOptions));return res.Ok?0:1;}
    // Read-only: the same pre-check that gates every UAC prompt. Exit 0 = CIM answered, 1 = blocked (nothing is changed either way).
    if(args.Length==1&&args[0]=="--preflight"){var guard=Recovery.CheckCimAsync(null,null,null,default).GetAwaiter().GetResult();Console.WriteLine(JsonSerializer.Serialize(guard,JsonOptions));return guard.Allowed?0:1;}
    // Read-only: the manual recovery text generated from an ownership journal (default: the live journal path; nothing is executed or written).
    if(args.Length==2&&args[0]=="--recovery-instructions"){string text=Recovery.BuildInstruction(Recovery.ReadJournal(args[1]=="-"?null:args[1]));Console.WriteLine(text??"NOTHING_TO_RECOVER");return text==null?1:0;}
    int idIndex=Array.IndexOf(args,"--operation-id");string operationId=idIndex>=0&&idIndex+1<args.Length?args[idIndex+1]:Guid.NewGuid().ToString("N");
    if(idIndex>=0&&!Guid.TryParseExact(operationId,"N",out _)){Console.Error.WriteLine("Invalid operation ID");return 1;}   // before anything is started: a mutation must not run and then lose its receipt
    bool hasServers=args.Contains("--servers");int serversIdx=Array.IndexOf(args,"--servers");
    bool hasTemplate=args.Contains("--template");int tmplIdx=Array.IndexOf(args,"--template");
    if(args.Contains("--network")){
     var netArgs=ParseNetworkArgs(args);
     Console.WriteLine(JsonSerializer.Serialize(Network(netArgs.Servers,netArgs.Template,netArgs.Plain).GetAwaiter().GetResult(),JsonOptions));
     return 0;
    }
    // Engine helper: every host and record type of one check in a single start of the exe, queried in parallel. Hosts are limited as for --system-dns.
    if(args.Length==3&&args[0]=="--system-dns-batch"){
     var hosts=args[1].Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
     var types=args[2].Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(t=>t=="AAAA"?28:t=="A"?1:throw new ArgumentException("type")).Distinct().ToArray();
     if(hosts.Length<1||hosts.Length>8||types.Length<1)throw new ArgumentException("--system-dns-batch <host,host...> <A|AAAA|A,AAAA>");
     var jobs=hosts.SelectMany(h=>types.Select(t=>Task.Run(()=>Wire.SystemQuery(h,t)))).ToArray();
     Console.WriteLine(JsonSerializer.Serialize(Task.WhenAll(jobs).GetAwaiter().GetResult(),JsonOptions));return 0;
    }
    if(args.Length==3&&args[0]=="--system-dns"){Console.WriteLine(JsonSerializer.Serialize(Wire.SystemQuery(args[1],args[2]=="AAAA"?28:1),JsonOptions));return 0;}
    if(args.Length==2&&args[0]=="--probe-domain"){Console.WriteLine(JsonSerializer.Serialize(new{Host=args[1],DoH=Servers.Select(ip=>Wire.Doh(args[1],1,ip,6)).ToArray(),System=(args[1]=="www.microsoft.com"||Hosts.Contains(args[1])?Wire.SystemQuery(args[1],1):(object)"SKIPPED: system query limited to known hosts")},JsonOptions));return 0;}
    if(args.Length==4&&args[0]=="--tls-probe"){if(!int.TryParse(args[2],out int tp)||!IPAddress.TryParse(args[1],out _))throw new ArgumentException("--tls-probe <ip> <port> <sni>");Console.WriteLine(JsonSerializer.Serialize(Probing.TlsAsync(args[1],tp,args[3],6000).GetAwaiter().GetResult(),JsonOptions));return 0;}
    if(args.Contains("--profiles-list")){var cat=ProfileCatalog.BuiltIn;Console.WriteLine(JsonSerializer.Serialize(new{cat.Schema,cat.Version,cat.Updated,Targets=Targets,cat.GeminiTargets,cat.GoogleTargets,Profiles=cat.Profiles.Select(p=>new{p.Id,p.Slot,p.Template,p.Servers,p.Origin,p.KnownIssue,p.Experimental}).ToArray(),cat.Pending},JsonOptions));return 0;}
    if(args.Contains("--verify-scripts")){var prep=ScriptStore.Prepare(false);ScriptStore.Verify(prep);Console.WriteLine(JsonSerializer.Serialize(new{Verified=true,prep.Directory,prep.Fingerprint,prep.Hashes,Exe=prep.ExePath},JsonOptions));return 0;}
    if(args.Length==2&&args[0]=="--export-scripts"){ScriptStore.Export(args[1]);Console.WriteLine("SCRIPTS_EXPORTED "+args[1]);return 0;}
    if(args.Length==2&&args[0]=="--benchmark-synthetic"){
     var synthetic=SyntheticTransport.Load(File.ReadAllText(args[1]));
     Console.WriteLine(JsonSerializer.Serialize(Benchmark.RunAsync(synthetic.Profiles,synthetic.Options,synthetic,null,default,true).GetAwaiter().GetResult(),JsonOptions));return 0;
    }
    if(args.Length==2&&args[0]=="--bootstrap-synthetic"){Console.WriteLine(JsonSerializer.Serialize(SyntheticScenarios.BootstrapScenario(File.ReadAllText(args[1])),JsonOptions));return 0;}
    if(args.Length==2&&args[0]=="--bootstrap-resolvers"){Console.WriteLine(JsonSerializer.Serialize(LiveBootstrapNet.Resolvers(args[1]),JsonOptions));return 0;}
    if(args.Length==2&&args[0]=="--alternative-synthetic"){Console.WriteLine(JsonSerializer.Serialize(SyntheticScenarios.Alternative(File.ReadAllText(args[1])),JsonOptions));return 0;}
    if(args.Length==2&&args[0]=="--health-synthetic"){Console.WriteLine(JsonSerializer.Serialize(SyntheticScenarios.Health(File.ReadAllText(args[1])),JsonOptions));return 0;}
    if(args.Length==2&&args[0]=="--connect-synthetic"){Console.WriteLine(JsonSerializer.Serialize(SyntheticScenarios.Connect(File.ReadAllText(args[1])),JsonOptions));return 0;}
    if(args.Contains("--benchmark")){
     var bo=new BenchOptions();
     int ai=Array.IndexOf(args,"--attempts");if(ai>=0){if(!int.TryParse(args[ai+1],out bo.Attempts)||bo.Attempts<1||bo.Attempts>20)throw new ArgumentException("--attempts must be 1..20");}
     int bi=Array.IndexOf(args,"--budget");if(bi>=0){if(!int.TryParse(args[bi+1],out int sec)||sec<5||sec>120)throw new ArgumentException("--budget must be 5..120 seconds");bo.TotalBudgetMs=sec*1000;}
     var specs=ProfileCatalog.BuiltIn.Profiles.AsEnumerable();
     int oi=Array.IndexOf(args,"--only");if(oi>=0){var wanted=args[oi+1].Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);specs=wanted.Select(w=>ProfileCatalog.BuiltIn.Find(w)??throw new ArgumentException("Unknown profile: "+w)).Distinct();}
     BenchRun benchRun;
     using(var live=new LiveTransport())benchRun=Benchmark.RunAsync(specs.ToList(),bo,live).GetAwaiter().GetResult();
     if(!args.Contains("--no-history"))Benchmark.AppendHistory(benchRun);
     Console.WriteLine(JsonSerializer.Serialize(benchRun,JsonOptions));return 0;
    }
    if(args.Length==2&&args[0]=="--bootstrap"){Console.WriteLine(JsonSerializer.Serialize(Bootstrap.RunAsync(args[1]).GetAwaiter().GetResult(),JsonOptions));return 0;}
    if(args.Contains("--autostart-status")){Console.WriteLine(JsonSerializer.Serialize(AutostartHelper.GetStatus(),JsonOptions));return 0;}
    if(args.Contains("--autostart-enable")){bool ok=AutostartHelper.SetEnabled(true);Console.WriteLine(JsonSerializer.Serialize(new{Success=ok,Action="Enable",Status=AutostartHelper.GetStatus()},JsonOptions));return ok?0:1;}
    if(args.Contains("--autostart-disable")){bool ok=AutostartHelper.SetEnabled(false);Console.WriteLine(JsonSerializer.Serialize(new{Success=ok,Action="Disable",Status=AutostartHelper.GetStatus()},JsonOptions));return ok?0:1;}
    if(args.Contains("--self-test"))return OfflineTests.Run();
    if(args.Length==1&&args[0]=="--ui-stress"){Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();return UiSurfaceCheck.Stress();}
    if(args.Length==1&&args[0]=="--ui-surfaces"){Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();return UiSurfaceCheck.Run();}
    if(args.Length==2&&args[0]=="--export-icon"){File.WriteAllBytes(args[1],Branding.IcoBytes());Console.WriteLine("ICON_EXPORTED "+args[1]);return 0;}
    if(args.Length>=2&&args[0]=="--ui-smoke") {
     // ui-smoke <png> [--bench]: renders every tab; with --bench also runs the real (read-only) benchmark through the UI and captures it mid-run and at the end.
     Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
     if(args.Contains("--en"))L.En=true;   // en: the same shots in English
     int dpiAt=Array.IndexOf(args,"--dpi");int smokeDpi=0;if(dpiAt>=0&&(dpiAt+1>=args.Length||!int.TryParse(args[dpiAt+1],out smokeDpi)||smokeDpi<96||smokeDpi>480))throw new ArgumentException("--dpi needs a whole number from 96 to 480");   // dpi N: the window at another scale (96, 120, 144, 192)
     string baseName=Path.ChangeExtension(args[1],null);
     using(var form=new MainForm(true)){
      form.Show();Application.DoEvents();if(smokeDpi>0)UiSurfaceCheck.ChangeDpi(form,smokeDpi);
      using(var bitmap=new Bitmap(form.Width,form.Height)){
       Action<string> shot=path=>{form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));form.OverlayDetails(bitmap);bitmap.Save(path);};
       shot(args[1]);
       for(int tab=1;tab<form.TabCount;tab++){form.SelectTab(tab);Application.DoEvents();shot(baseName+"_tab"+tab+".png");}
       form.SelectTab(0);
       if(args.Contains("--states")){
        // Every main-page state and the details panel, rendered without touching the system (the 5 default shots stay unchanged).
        foreach(string mode in new[]{"idle","checking","busy","active","active-unchecked","recovery","conflict","wmi","drift","switch","noprovider","fallback-active","allfailed","selection-timeout"}){form.SmokeState(mode);shot(baseName+"_state_"+mode+".png");}
        // The levels of «Через мост» (each highlights from the left) and the browser panel with every kind of answer.
        form.SmokeState("active");form.SetScopeForTest(MainForm.BridgeScope.CoreGemini);shot(baseName+"_state_active_gemini.png");
        form.SetScopeForTest(MainForm.BridgeScope.All);shot(baseName+"_state_active_google.png");form.SetScopeForTest(MainForm.BridgeScope.Core);
        form.SmokeState("idle");form.SmokeBrowserPanel(null,true);shot(baseName+"_browser_none.png");
        foreach(string bid in new[]{"chrome","edge","firefox","brave","other"}){form.SmokeBrowserPanel(bid,true);shot(baseName+"_browser_"+bid+".png");}
        form.SmokeBrowserPanel("chrome",false);shot(baseName+"_browser_settings_mode.png");form.BrowserCancelForTest();
        form.SelectTab(2);Application.DoEvents();shot(baseName+"_tab2_browser_chosen.png");form.SelectTab(0);
        form.SmokeState("idle");form.SmokeDetails("dns.zapret.moe",false);shot(baseName+"_details_provider.png");
        form.SmokeState("recovery");form.SmokeDetails(null,true);shot(baseName+"_details_recovery.png");
        form.SmokeState("idle");
        // The support report preview, rendered from sample personal data that must come out redacted.
        var sampleCtx=new RedactionContext{UserName="ivan",MachineName="DESKTOP-IVAN01",ProfileDir=@"C:\Users\ivan",Ssids=new[]{"HomeWiFi-5G"}};
        var sampleInput=new ReportInput{GeneratedUtc=DateTime.UtcNow,ExeVersion=AppInfo.Version,CatalogVersion=ProfileCatalog.BuiltIn.Version,CatalogUpdated=ProfileCatalog.BuiltIn.Updated,Os=Environment.OSVersion.VersionString,JournalPhase="Active",Profile=ProfileCatalog.BuiltIn.Default,HealthKind="Drift",HealthCode="HEALTH_PROVIDER_DOWN",HealthUtc="12.10 18:00",
         Operations=new[]{new OperationEntry{Utc="2026-10-12T15:30:00Z",Action="enable",Code="DNS_CONFIGURED_APPLICATION_NOT_TESTED",Ok=true,Profile=ProfileCatalog.BuiltIn.Default},new OperationEntry{Utc="2026-10-12T17:10:00Z",Action="health",Code="HEALTH_PROVIDER_DOWN",Ok=false,Profile=ProfileCatalog.BuiltIn.Default}},
         DiagnosisJson=null,DiagnosisCode=null};
        using(var preview=form.SmokePreview(SupportReport.Sanitize(SupportReport.Build(sampleInput)+"\nC:\\Users\\ivan\\Desktop\\x.txt  DESKTOP-IVAN01  HomeWiFi-5G (sample, redacted)\n",sampleCtx),smokeDpi))preview.Save(baseName+"_report_preview.png");
        // Doctor's window from invented layer inputs: a warning in conflicts and port 53, a warning for a browser's Secure DNS, the rest ok or skipped.
        var sampleDoctorInput=new DoctorInput{ContextRevision="synthetic-doctor",ConfigurationKnown=true,NetworkUp=true,JournalPhase="Active",BridgeOn=true,RulesOk=true,ForeignConflicts=new[]{"sample.example"},Port53=new[]{new LocalPort53.Holder{Protocol="UDP",Address="127.0.0.1",Port=53,Pid=4242,Process="sample-dns"}},Servers=new[]{"192.0.2.10","192.0.2.11"},Targets=new[]{"protected.example.test"},Provider=new ProviderProbe{Validated=new[]{"192.0.2.10","192.0.2.11"},Answers=new Dictionary<string,string[]>{{"protected.example.test",new[]{"192.0.2.20"}}}},BrowsersWithSecureDns=new[]{"Chrome"}};
        foreach(string id in new[]{Doctor.IdNetwork,Doctor.IdJournal,Doctor.IdRules,Doctor.IdConflicts,Doctor.IdPort53,Doctor.IdProvider,Doctor.IdAnswers,Doctor.IdTls,Doctor.IdVpn,Doctor.IdBrowser})Doctor.Record(sampleDoctorInput,id,"synthetic fixture","fixed local test data","synthetic",DateTime.UtcNow);
        var sampleDoctor=Doctor.Evaluate(sampleDoctorInput,DateTime.UtcNow);
        using(var doctorShot=form.SmokeDoctor(sampleDoctor,smokeDpi))doctorShot.Save(baseName+"_state_doctor.png");
        using(var consent=new ProviderConsentForm(L.T("Разрешённые провайдеры"),
         L.T("Разрешить этим провайдерам обработку запросов выбранных сервисов? Если выбранный профиль недоступен, мост проверит разрешённые альтернативы. TLS-сертификаты проверяются. Доверие можно отозвать в настройках.")+"\r\n\r\n"+
         string.Join("\r\n\r\n",ProfileCatalog.BuiltIn.Profiles.Where(p=>!p.Experimental).Take(4).Select(p=>p.Id+"\r\n"+p.Template+"\r\n"+string.Join(", ",p.Servers)))+"\r\n\r\n"+string.Join("\r\n",ProfileCatalog.BuiltIn.TargetsForGoogle()))){
         consent.Show();Application.DoEvents();if(smokeDpi>0)UiSurfaceCheck.ChangeDpi(consent,smokeDpi);consent.FitToWorkArea();Application.DoEvents();
         using(var consentShot=new Bitmap(consent.Width,consent.Height)){consent.DrawToBitmap(consentShot,new Rectangle(Point.Empty,consentShot.Size));consentShot.Save(baseName+"_provider_consent.png");}consent.Close();
        }
        // the tray icon in each colour, as the tray shows it (the cached icons stay owned by the window)
        foreach(var trayState in new[]{TrayState.Gray,TrayState.Green,TrayState.Yellow,TrayState.Red})using(var trayShot=form.TrayIconFor(true,trayState).ToBitmap())trayShot.Save(baseName+"_tray_"+trayState.ToString().ToLowerInvariant()+".png");
       }
       if(args.Contains("--bench")){form.RunBenchmarkForSmoke(tag=>{shot(baseName+"_bench_"+tag+".png");return true;},2500);}
      }
      Console.WriteLine("UI_SMOKE_PASS: native form created and rendered; tabs "+form.TabCount+"; client "+form.ClientSize+"; dpi "+form.DeviceDpi);
      if(smokeDpi>0){var problems=form.ScaleProblems();foreach(var p in problems)Console.WriteLine("SCALE_PROBLEM "+p);Console.WriteLine(problems.Count==0?"SCALE_PASS "+form.DeviceDpi+" dpi":"SCALE_FAIL "+form.DeviceDpi+" dpi");form.Close();return problems.Count==0&&form.DeviceDpi==smokeDpi?0:1;}
      form.Close();
     }
     return 0;
    }
    if(args.Contains("--integration")) {
     if(!IsAdmin()){var elevated=Elevate("integration").GetAwaiter().GetResult();Console.WriteLine(elevated.Output);return elevated.Code;}
     var integrationScripts=ScriptStore.Prepare(true);ScriptStore.Verify(integrationScripts);
     var psi=SystemModulesOnly(new ProcessStartInfo(PowerShell){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8});
     foreach(string a in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",integrationScripts.Script("Integration.ps1")})psi.ArgumentList.Add(a);
     using(var p=Process.Start(psi)){
      if(p==null)return 1;
      var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
      using(var cts=new CancellationTokenSource(TimeSpan.FromSeconds(30))){
       try{
        p.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
        string output=stdout.GetAwaiter().GetResult();string err=stderr.GetAwaiter().GetResult();
        output=SaveMutation(output,operationId,p.ExitCode);
        Console.WriteLine(output);Console.Error.WriteLine(err);return p.ExitCode;
       }catch(OperationCanceledException){
        try{p.Kill(entireProcessTree:true);}catch{}
        Console.Error.WriteLine("Integration process exceeded deadline (30s) and was terminated");
        return 1460;
       }
      }
     }
    }
    string action=args.Contains("--diagnose")||args.Contains("--export-report")?"diagnose":args.Contains("--enable")?"enable":args.Contains("--restore")?"restore":args.Contains("--status")?"status":null;
    if(action!=null) {
     var netArgs=ParseNetworkArgs(args);
     string profName=args.Contains("--profile")&&Array.IndexOf(args,"--profile")+1<args.Length?args[Array.IndexOf(args,"--profile")+1]:null;
     bool mutation=action=="enable"||action=="restore";
     string expectedNative=null;long? expectedUntil=null;int preparedAt=Array.IndexOf(args,"--prepared-revision");
     if(preparedAt>=0){expectedNative=args[preparedAt+1];expectedUntil=long.Parse(args[Array.IndexOf(args,"--prepared-until-ms")+1],System.Globalization.CultureInfo.InvariantCulture);if(!mutation)throw new ArgumentException("Prepared revision requires a canonical native mutation");}
     if(mutation&&!IsAdmin()){
      // no UAC prompt and no mutation while CIM/WMI does not answer.
      var guard=Recovery.CheckCimAsync(netArgs.Servers,netArgs.Template,profName,default).GetAwaiter().GetResult();
      if(!guard.Allowed){Console.WriteLine(Recovery.BlockedJson(action,guard,Recovery.ReadJournal()));return 1;}
      var elevated=Elevate(action,args.Contains("--trust-proxy"),args.Contains("--plain"),netArgs.Servers,netArgs.Template,profName,default,args.Contains("--repair"),null,expectedNative,expectedUntil).GetAwaiter().GetResult();
      if(action=="restore"&&elevated.Code==1460){Console.WriteLine(Recovery.UnconfirmedJson("ELEVATION_TIMEOUT",Recovery.ReadJournal()));return 1;}
      Console.WriteLine(elevated.Output);return elevated.Code;
     }
     var result=Engine(action,args.Contains("--trust-proxy"),args.Contains("--plain"),netArgs.Servers,netArgs.Template,profName,default,null,args.Contains("--repair"),expectedNative,expectedUntilMs:expectedUntil).GetAwaiter().GetResult();
     // An engine restore that ran out of time did not finish: say so with the manual steps instead of a bare timeout.
     if(action=="restore"&&result.Code==1460)result=(1,Recovery.UnconfirmedJson("ENGINE_TIMEOUT",Recovery.ReadJournal()));
     string output=result.Output;if(mutation)output=SaveMutation(output,operationId,result.Code);else SaveReport(output);Console.WriteLine(output);return result.Code;
    }
    if(args.Length>0){throw new ArgumentException("Unknown CLI command or arguments: "+string.Join(" ",args));}
    FreeConsole();
    // A separately versioned candidate must open its own window instead of silently focusing the older release.
    string copyIdentity=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Environment.ProcessPath??AppContext.BaseDirectory).ToUpperInvariant()))).Substring(0,16);
    string uiInstance=AppInfo.Version+"-"+copyIdentity+(catalogUiIdentity==null?"":"-catalog-"+catalogUiIdentity);
    Mutex uiMutex=null;bool createdNew=false;
    try{uiMutex=new Mutex(true,@"Local\"+AppIdentity.StorageName+"UI-"+uiInstance,out createdNew);}catch(AbandonedMutexException){createdNew=true;}
    catch(UnauthorizedAccessException){return 0;}
    if(uiMutex!=null){
     using(uiMutex){
      if(!createdNew){if(!minimized)try{using(var existing=EventWaitHandle.OpenExisting(@"Local\"+AppIdentity.StorageName+"Show-"+uiInstance))existing.Set();}catch{}return 0;}
      using(var showEvent=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\"+AppIdentity.StorageName+"Show-"+uiInstance)){
       Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
       // an exception in the window's own handlers or painting is written down with its stack (ui-errors.log) instead of a framework dialog: the cause can then be read
       Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);Application.ThreadException+=(s,e)=>UiDiag.Thread(e.Exception);
       // Crash Guard: judge the previous window session, then mark this one as running; only a normal exit marks it clean.
       CrashGuard.Start();
       using(var form=new MainForm(false,minimized)){form.RegisterShowEvent(showEvent);Application.Run(form);}
       CrashGuard.MarkClean();
       return 0;
      }
     }
    }
    return 0;
   }catch(Exception e){Console.Error.WriteLine(e.GetType().Name+": "+e.Message);return 1;}
  }
 }
}
