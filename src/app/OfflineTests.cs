using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 public static partial class OfflineTests {
  static int passed;
  static void Check(bool value,string label){if(!value)throw new Exception("FAIL "+label);Console.WriteLine("PASS "+label);passed++;}
  static void Reject(Action action,string label){try{action();}catch(ArgumentException){Check(true,label);return;}catch(FormatException){Check(true,label);return;}throw new Exception("FAIL accepted "+label);}
  static byte[] Answer(string host,int type,byte[] ip){var q=Wire.Query(host,type,1234);q[2]=129;q[3]=128;q[7]=1;var b=new List<byte>(q);b.AddRange(new byte[]{192,12,0,(byte)type,0,1,0,0,0,60,0,(byte)ip.Length});b.AddRange(ip);return b.ToArray();}
  static void AppendName(List<byte> b,string name){foreach(var part in name.Split('.')){b.Add((byte)part.Length);b.AddRange(Encoding.ASCII.GetBytes(part));}b.Add(0);}
  static byte[] EncodeName(string name){var b=new List<byte>();AppendName(b,name);return b.ToArray();}
  static void AppendCnameRr(List<byte> b,string owner,string target){AppendName(b,owner);b.AddRange(new byte[]{0,5,0,1,0,0,0,60});byte[] tb=EncodeName(target);b.AddRange(new byte[]{0,(byte)tb.Length});b.AddRange(tb);}
  static void AppendARr(List<byte> b,string owner,string ipStr){AppendName(b,owner);b.AddRange(new byte[]{0,1,0,1,0,0,0,60});byte[] ipBytes=IPAddress.Parse(ipStr).GetAddressBytes();b.AddRange(new byte[]{0,4});b.AddRange(ipBytes);}
  static byte[] MakePacket(string host,int id,Action<List<byte>> addRecords,ushort ancount,int type=1){var q=Wire.Query(host,type,id);q[2]=129;q[3]=128;q[6]=(byte)(ancount>>8);q[7]=(byte)(ancount&255);var b=new List<byte>(q);addRecords(b);return b.ToArray();}
  // provider catalog (versioned data)
  static void RunCatalogTests() {
   var cat=ProfileCatalog.BuiltIn;
   Check(cat.Schema==1&&!string.IsNullOrWhiteSpace(cat.Version)&&cat.Profiles.Length>=6,"catalog: embedded profiles.json loads with a version and at least six profiles");
   Check(new[]{"dns.malw.link","dns.comss.one","geohide.ru","dns.bezmezhau.com","dns.zapret.moe","eu.geohide.ru"}.All(id=>cat.Find(id)!=null),"catalog: the two anchors (malw, comss) plus GeoHide, BezMezhau, Zapret and GeoHide EU are present");
   Check(new[]{"xbox-dns.ru","dns.dns-ai.ru","dns-ai","P2","P5"}.All(n=>cat.Find(n)==null&&cat.FindRetired(n)!=null)&&!Program.KnownProfiles.ContainsKey("xbox-dns.ru")&&!Program.KnownProfiles.ContainsKey("P5"),"catalog: xbox-dns.ru and dns.dns-ai.ru are retired: no built-in, slot or alias resolves to them, and the retired list still knows the old names");
   Check(cat.Default=="dns.malw.link"&&!cat.Find("dns.malw.link").Experimental&&!cat.Find("dns.comss.one").Experimental&&new[]{"dns.zapret.moe","eu.geohide.ru","geohide.ru"}.All(id=>cat.Find(id).Experimental),"catalog: the anchors stay the default and recommendable; new and degraded profiles are experimental (never recommended before a Good verdict)");
   Check(cat.Profiles.All(p=>Uri.TryCreate(p.Template,UriKind.Absolute,out var u)&&u.Scheme==Uri.UriSchemeHttps&&u.AbsolutePath=="/dns-query"&&u.Query.Length==0&&u.UserInfo.Length==0)&&cat.Profiles.Select(p=>p.Slot).Distinct().Count()==cat.Profiles.Length,"catalog: every DoH template is a plain https URL on /dns-query and every slot is unique");
   Check(cat.Find("dns.bezmezhau.ru")==null&&cat.Pending.Any(p=>p.Id=="dns.bezmezhau.ru"),"catalog: the legacy dns.bezmezhau.ru is kept as a pending note, not confused with dns.bezmezhau.com");
   string liveJson=System.Text.Encoding.UTF8.GetString(ScriptStore.Embedded("profiles.json"));
   Reject(()=>ProfileCatalog.Parse(liveJson.Replace("\"id\": \"xbox-dns.ru\", \"slot\": \"P2\"","\"id\": \"xbox-dns.ru\", \"slot\": \"P3\"")),"catalog: a retired slot that collides with a live profile is refused");
   Reject(()=>ProfileCatalog.Parse(liveJson.Replace("\"id\": \"dns.dns-ai.ru\", \"slot\": \"P5\", \"aliases\": [\"dns-ai\"]","\"id\": \"dns.dns-ai.ru\", \"slot\": \"P5\", \"aliases\": [\"zapret\"]")),"catalog: a retired alias that collides with a live alias is refused");
   foreach(var (saved,custom,expected,label) in new[]{("xbox-dns.ru","","dns.malw.link","retired id"),("P5","","dns.malw.link","retired slot"),("dns-ai","","dns.malw.link","retired alias"),("dns.comss.one","","dns.comss.one","live id"),("P3","","P3","live slot"),("","","dns.malw.link","empty name"),(AppSettings.CustomProfileId,"https://xbox-dns.ru/dns-query",AppSettings.CustomProfileId,"own profile on a retired host")}) {
    var st=new AppSettings{NoSave=true,ProfileIdentityVersion=1,Profile=saved,CustomName=custom.Length>0?"mine":"",CustomTemplate=custom};st.NormalizeProfileIdentity();
    Check(st.Profile==expected&&st.CustomTemplate==custom,"settings migration: "+label+" '"+saved+"' -> "+expected+", own profile fields untouched");
   }
   var legacyOwn=new AppSettings{NoSave=true,Profile="xbox-dns.ru",CustomName="xbox-dns.ru",CustomTemplate="https://own.example.test/dns-query"};legacyOwn.NormalizeProfileIdentity();
   Check(legacyOwn.Profile==AppSettings.CustomProfileId&&legacyOwn.CustomName=="xbox-dns.ru"&&legacyOwn.CustomTemplate=="https://own.example.test/dns-query","settings migration: an old own profile that happens to carry a retired name stays the own profile");
   var importedRetired=ConfigFile.Import("{\"Schema\":\"agdns-config\",\"Version\":1,\"Settings\":{\"Profile\":\"xbox-dns.ru\"}}",new AppSettings{NoSave=true});
   Check(importedRetired.Ok&&importedRetired.Applied.Profile=="dns.malw.link","settings import: a file naming a retired profile imports and selects the default");
   {
    // network revision: only the route counts (owner's PC 2026-10-26: Ethernet + blinking Wi-Fi + Radmin VPN + Teredo + a VPN TUN)
    EvidenceContext.NicView Nic(string id,System.Net.NetworkInformation.NetworkInterfaceType t,bool up,int ix,string addr,bool gw)=>new(id,t,up,ix,new[]{addr},new[]{"192.168.0.1"},gw?new[]{"192.168.0.1"}:new string[0],gw);
    var E=System.Net.NetworkInformation.NetworkInterfaceType.Ethernet;var W=System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211;var T=System.Net.NetworkInformation.NetworkInterfaceType.Tunnel;var V=(System.Net.NetworkInformation.NetworkInterfaceType)53;
    EvidenceContext.NicView[] Pc(bool wifiUp,string teredo="2001:0:1::1",string eth="192.168.0.10",string tun="172.19.0.1",bool radmin=true)=>new[]{Nic("eth",E,true,5,eth,true),Nic("wifi",W,wifiUp,11,"192.168.0.11",true),Nic("teredo",T,true,9,teredo,false),Nic("tun",V,true,3,tun,false)}.Concat(radmin?new[]{Nic("radmin",E,true,7,"26.1.2.3",true)}:new EvidenceContext.NicView[0]).ToArray();
    string r0=EvidenceContext.RevisionOf(Pc(true),5);
    Check(EvidenceContext.RevisionOf(Pc(false),5)==r0&&EvidenceContext.RevisionOf(Pc(true,teredo:"2001:0:9::9"),5)==r0&&EvidenceContext.RevisionOf(Pc(true,radmin:false),5)==r0,"network revision: a blinking second Wi-Fi, a Teredo address and an overlay LAN adapter with its own gateway do not change the network");
    Check(EvidenceContext.RevisionOf(Pc(true,eth:"192.168.0.20"),5)!=r0&&EvidenceContext.RevisionOf(Pc(true,tun:"172.19.0.9"),5)!=r0&&EvidenceContext.RevisionOf(Pc(true),11)!=r0,"network revision: the primary adapter's address, the VPN tunnel and a move of the primary route do change it");
    var epoch=new ScenarioNetworkEpoch();var c1=epoch.Capture("x").Network;epoch.Observe();epoch.Observe();
    Check(epoch.Capture("x").Network==c1,"network epoch: address events without a real revision change do not move the epoch");epoch.Dispose();
   }
   {
    string ok="{\"State\":\"DNS active\",\"Active\":true,\"JournalPhase\":\"Active\",\"DnsCacheFlushed\":true,\"Template\":\"https://dns.malw.link/dns-query\",\"Targets\":[\"a.example\",\"b.example\"],\"Servers\":[\"1.2.3.4\"]}";
    var tg=new[]{"b.example","a.example"};var sv=new[]{"1.2.3.4"};const string tp="https://dns.malw.link/dns-query";
    Check(LiveAssistedBackend.EngineProvedApply(ok,tp,tg,sv),"connect speed: the elevated engine's verified apply receipt proves the exact policy (no second 6-8 s diagnose)");
    Check(!LiveAssistedBackend.EngineProvedApply(ok.Replace("\"DnsCacheFlushed\":true,",""),tp,tg,sv)&&!LiveAssistedBackend.EngineProvedApply(ok.Replace("1.2.3.4","5.6.7.8"),tp,tg,sv)&&
     !LiveAssistedBackend.EngineProvedApply(ok,"https://dns.comss.one/dns-query",tg,sv)&&!LiveAssistedBackend.EngineProvedApply(ok.Replace("\"Active\":true","\"Active\":false"),tp,tg,sv)&&
     !LiveAssistedBackend.EngineProvedApply("{}",tp,tg,sv)&&!LiveAssistedBackend.EngineProvedApply("not json",tp,tg,sv),"connect speed: a read-back without the resolver check, other pins, template or state falls back to the full diagnose");
   }
   string retiredError=null;try{Program.ParseNetworkArgs(new[]{"--network","--profile","xbox-dns.ru"});}catch(ArgumentException e){retiredError=e.Message;}
   Check(retiredError!=null&&retiredError.Contains("retired"),"CLI: --profile with a retired name is refused and says it was retired");
   Check(cat.Find("P1").Id=="dns.malw.link"&&cat.Find("P4").Id=="geohide.ru"&&cat.Find("bezmezhau").Id=="dns.bezmezhau.com"&&Program.KnownProfiles["P6"].Template=="https://dns.bezmezhau.com/dns-query","catalog: slots and aliases resolve to the same pinned profile");
   Check(cat.Profiles.All(p=>p.Servers.All(Program.PublicIp)&&p.Template.StartsWith("https://")),"catalog: every pinned server is a public IP and every template is https");
   Check(cat.Pending.Any(p=>p.Id=="dns.astracat.ru"&&p.Template.Contains(":8443/")&&!string.IsNullOrWhiteSpace(p.Reason))&&cat.Find("dns.astracat.ru")==null,"catalog: Astracat stays pending with a recorded reason until an IP is verified");
   string good="{\"schema\":1,\"default\":\"a.test\",\"targets\":[\"one.example.test\",\"two.example.test\"],\"version\":\"t\",\"profiles\":[{\"id\":\"a.test\",\"slot\":\"P1\",\"template\":\"https://a.test/dns-query\",\"servers\":[\"193.233.112.67\"]}]}";
   Check(ProfileCatalog.Parse(good).Profiles.Length==1,"catalog: minimal valid document parses");
   Check(cat.Targets.Length==2&&cat.Targets.All(h=>h.EndsWith(".googleapis.com"))&&Program.Targets.SequenceEqual(cat.Targets)&&Program.Target==cat.Targets[0]&&cat.DefaultProfile!=null&&Program.Servers.SequenceEqual(cat.DefaultProfile.Servers)&&Program.DefaultTemplate==cat.DefaultProfile.Template,"catalog: protected domains and the default profile are read from profiles.json only");
   Reject(()=>ProfileCatalog.Parse(good.Replace("\"default\":\"a.test\",","")),"catalog: a catalog without a default profile is rejected");
   Reject(()=>ProfileCatalog.Parse(good.Replace("\"default\":\"a.test\"","\"default\":\"nope.test\"")),"catalog: an unknown default profile is rejected");
   Reject(()=>ProfileCatalog.Parse(good.Replace("\"targets\":[\"one.example.test\",\"two.example.test\"],","")),"catalog: a catalog without target domains is rejected");
   Reject(()=>ProfileCatalog.Parse(good.Replace("two.example.test","*.example.test")),"catalog: a wildcard target is rejected");
   Reject(()=>ProfileCatalog.Parse(good.Replace("two.example.test","one.example.test")),"catalog: duplicate target domains are rejected");
   Reject(()=>ProfileCatalog.Parse(good.Replace("\"schema\":1","\"schema\":2")),"catalog: unknown schema rejected");
   Reject(()=>ProfileCatalog.Parse(good.Replace("https://a.test","http://a.test")),"catalog: http template rejected");
   Reject(()=>ProfileCatalog.Parse(good.Replace("193.233.112.67","192.168.1.1")),"catalog: private pinned IP rejected");
   Reject(()=>ProfileCatalog.Parse(good.Replace("https://a.test/","https://193.233.112.67/")),"catalog: IP-literal template host rejected");
   Reject(()=>ProfileCatalog.Parse(good.Replace("\"servers\":[\"193.233.112.67\"]","\"servers\":[]")),"catalog: empty server list rejected");
   Reject(()=>ProfileCatalog.Parse(good.Replace("}]}","},{\"id\":\"b.test\",\"slot\":\"P1\",\"template\":\"https://b.test/dns-query\",\"servers\":[\"193.233.112.68\"]}]}")),"catalog: duplicate slot rejected");
   Check(ProfileCatalog.Parse(good.Replace("https://a.test/dns-query","https://a.test:8443/dns-query")).Profiles[0].Port==8443,"catalog: explicit :8443 in a template is kept");
   Check(Bootstrap.TryParse("geohide.ru",out var bu1)&&bu1.AbsoluteUri=="https://geohide.ru/dns-query"&&Bootstrap.TryParse("https://dns.astracat.ru:8443/dns-query",out var bu2)&&bu2.Port==8443&&!Bootstrap.TryParse("http://x.test/dns-query",out _)&&!Bootstrap.TryParse("1.2.3.4",out _)&&!Bootstrap.TryParse("",out _),"bootstrap: a host name or https URL is accepted; IP literals and http are rejected");
  }

  // embedded scripts
  static void RunScriptStoreTests() {
   string root=Path.Combine(Path.GetTempPath(),"agdns-scripts-"+Guid.NewGuid().ToString("N"));
   try {
    var hashes=ScriptStore.EmbeddedHashes();
    Check(ScriptStore.ScriptNames.All(n=>hashes[n].Length==64),"scripts: Engine, Integration and EmergencyRestore are embedded in the exe");
    var prep=ScriptStore.Prepare(false,root);
    Check(ScriptStore.ScriptNames.All(n=>File.Exists(prep.Script(n))&&ScriptStore.Sha256File(prep.Script(n))==hashes[n]),"scripts: staged copies match the embedded SHA-256");
    ScriptStore.Verify(prep);Check(true,"scripts: verification passes on an untouched stage");
    File.AppendAllText(prep.Script("Engine.ps1"),"\n# tampered");
    bool tampered=false;try{ScriptStore.Verify(prep);}catch(IOException e){tampered=e.Message.StartsWith("SCRIPT_TAMPERED");}
    Check(tampered,"scripts: a modified staged script is detected before launch");
    var healed=ScriptStore.Prepare(false,root);ScriptStore.Verify(healed);
    Check(ScriptStore.Sha256File(healed.Script("Engine.ps1"))==hashes["Engine.ps1"],"scripts: re-staging replaces the tampered file with the embedded one");
    File.Delete(healed.Script("Integration.ps1"));
    bool missing=false;try{ScriptStore.Verify(healed);}catch(IOException){missing=true;}
    Check(missing,"scripts: a deleted staged script fails verification");
    string export=Path.Combine(root,"export");ScriptStore.Export(export);
    Check(File.ReadAllText(Path.Combine(export,"SHA256SUMS.txt")).Contains(hashes["Engine.ps1"])&&File.Exists(Path.Combine(export,"EmergencyRestore.ps1")),"scripts: --export-scripts writes the scripts and a SHA256SUMS list");
    var acl=ScriptStore.BuildProtectedAcl();
    var rules=acl.GetAccessRules(true,false,typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
    bool Has(string sid,System.Security.AccessControl.FileSystemRights r)=>rules.Any(x=>x.IdentityReference.Value==sid&&(x.FileSystemRights&r)==r&&x.AccessControlType==System.Security.AccessControl.AccessControlType.Allow);
    var writeBits=System.Security.AccessControl.FileSystemRights.WriteData|System.Security.AccessControl.FileSystemRights.AppendData|System.Security.AccessControl.FileSystemRights.Delete|System.Security.AccessControl.FileSystemRights.ChangePermissions|System.Security.AccessControl.FileSystemRights.TakeOwnership;
    Check(Has("S-1-5-18",System.Security.AccessControl.FileSystemRights.FullControl)&&Has("S-1-5-32-544",System.Security.AccessControl.FileSystemRights.FullControl),"scripts: protected ACL gives SYSTEM and Administrators full control");
    Check(Has("S-1-5-32-545",System.Security.AccessControl.FileSystemRights.ReadAndExecute)&&!rules.Any(x=>x.IdentityReference.Value=="S-1-5-32-545"&&(x.FileSystemRights&writeBits)!=0)&&rules.All(x=>x.IdentityReference.Value=="S-1-5-18"||x.IdentityReference.Value=="S-1-5-32-544"||x.IdentityReference.Value=="S-1-5-32-545"),"scripts: protected ACL gives Users read/execute only and nobody else any access");
    Check(acl.AreAccessRulesProtected,"scripts: protected ACL does not inherit from the parent directory");
   }finally{try{Directory.Delete(root,true);}catch{}}
  }

  // autostart without a question or a copy, and the elevated staging flow
  static void RunInstallTests() {
   string dir=Path.Combine(Path.GetTempPath(),"agdns-stable-"+Guid.NewGuid().ToString("N"));
   try {
    Directory.CreateDirectory(dir);
    string src=Path.Combine(dir,"src.exe");File.WriteAllBytes(src,Enumerable.Range(0,4096).Select(i=>(byte)(i*7)).ToArray());
    // autostart: no question and no copy; it names the running exe; only a file that exists and is this program's is ever offered for deletion
    const string isoKey=@"Software\AgDnsTest\RunInstall";string realKey=AutostartHelper.KeyName;
    Microsoft.Win32.Registry.CurrentUser.CreateSubKey(isoKey).Dispose();AutostartHelper.KeyName=isoKey;
    try {
     string self=Environment.ProcessPath;
     string ours1=Path.Combine(dir,"ours1.exe");File.Copy(self,ours1,true);
     string gone=Path.Combine(dir,"gone","AntigravityGeminiDns.exe");
     void SeedEntry(string target){using var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(isoKey);key.SetValue(AutostartHelper.ValueName,"\""+target+"\" --minimized");}
     Check(AutostartHelper.SamePath(src,src.ToUpperInvariant())&&!AutostartHelper.SamePath(src,ours1)&&AutostartHelper.SamePath("\""+src+"\"",src),"autostart: path comparison ignores case and quotes");
     // the entry names a file that is gone: quietly moved to this exe
     SeedEntry(gone);
     Check(AutostartHelper.RepointIfMissing(self)&&AutostartHelper.IsEnabled(self)&&AutostartHelper.CurrentTarget()==self,"autostart: an entry whose file is gone is quietly pointed at the running exe");
     string absentRoot=Enumerable.Range('D',23).Select(c=>((char)c)+":\\").FirstOrDefault(r=>!Directory.Exists(r));
     if(absentRoot!=null){string onStick=Path.Combine(absentRoot,"Apps","AntigravityGeminiDns.exe");SeedEntry(onStick);
      Check(!AutostartHelper.RepointIfMissing(self)&&AutostartHelper.SamePath(AutostartHelper.CurrentTarget(),onStick),"autostart: an entry on a drive that is not there now (USB stick, offline share) is left alone");}
     AutostartHelper.SetEnabled(true,ours1);
     Check(!AutostartHelper.RepointIfMissing(self)&&AutostartHelper.SamePath(AutostartHelper.CurrentTarget(),ours1),"autostart: an entry whose file exists (another copy) is left alone at start");
     AutostartHelper.SetEnabled(false);
     Check(!AutostartHelper.RepointIfMissing(self)&&AutostartHelper.CurrentTarget()==null,"autostart: with no entry nothing is ever turned on by itself");
     AutostartHelper.SetEnabled(true,self);
     Check(!AutostartHelper.RepointIfMissing(self)&&AutostartHelper.IsEnabled(self),"autostart: an entry that already names this exe is not touched");
     AutostartHelper.SetEnabled(false);
     // turning it on: the running exe, and the other copy is remembered only if it still exists
     AutostartHelper.SetEnabled(true,ours1);
     bool ok1=AutostartHelper.EnableForCurrent(self,out string prev1);
     Check(ok1&&prev1==ours1&&AutostartHelper.IsEnabled(self),"autostart: turning it on moves the entry to the running exe and names the other copy it had");
     bool ok2=AutostartHelper.EnableForCurrent(self,out string prev2);
     Check(ok2&&prev2==null,"autostart: turning it on again names no previous copy (it already names this exe)");
     SeedEntry(gone);
     bool ok3=AutostartHelper.EnableForCurrent(self,out string prev3);
     Check(ok3&&prev3==null&&AutostartHelper.IsEnabled(self),"autostart: a previous entry whose file is gone is not offered");
     AutostartHelper.SetEnabled(false);
     bool ok4=AutostartHelper.EnableForCurrent(self,out string prev4);
     Check(ok4&&prev4==null&&AutostartHelper.IsEnabled(self),"autostart: turning it on with no entry names no previous copy");
     AutostartHelper.SetEnabled(false);
     // deleting a previous copy
     string product=AutostartHelper.ProductOf(self);
     Check(!string.IsNullOrEmpty(product)&&AutostartHelper.ProductOf(ours1)==product&&AutostartHelper.ProductOf(Environment.SystemDirectory+@"\PING.EXE")!=product,"autostart: a copy of this program carries the same ProductName; another program does not ("+product+")");
     Check(AutostartHelper.IsRunning(self)&&!AutostartHelper.IsRunning(ours1),"autostart: the running exe counts as running, a copy that nobody started does not");
     Check(AutostartHelper.RemovePreviousCopy(ours1,self,p=>true)==AutostartHelper.CopyRemoval.Running&&File.Exists(ours1),"autostart: a previous copy that is running is not deleted");
     Check(AutostartHelper.RemovePreviousCopy(self,self,p=>false)==AutostartHelper.CopyRemoval.ThisCopy&&File.Exists(self),"autostart: the running exe is never deleted");
     string foreign=Path.Combine(dir,"PING.EXE");File.Copy(Environment.SystemDirectory+@"\PING.EXE",foreign,true);
     Check(AutostartHelper.RemovePreviousCopy(foreign,self,p=>false)==AutostartHelper.CopyRemoval.NotOurs&&File.Exists(foreign),"autostart: a file of another program is never deleted, whatever it is called");
     string alien=Path.Combine(dir,"AntigravityDnsUnlocker-looks-ours.exe");File.WriteAllBytes(alien,new byte[]{1,2,3});
     Check(AutostartHelper.RemovePreviousCopy(alien,self,p=>false)==AutostartHelper.CopyRemoval.NotOurs&&File.Exists(alien),"autostart: a file that only has our name is not deleted (the ProductName decides)");
     Check(AutostartHelper.RemovePreviousCopy(Path.Combine(dir,"nothing.exe"),self,p=>false)==AutostartHelper.CopyRemoval.AlreadyGone,"autostart: a copy that is already gone is simply forgotten");
     Check(AutostartHelper.RemovePreviousCopy(ours1,self,p=>false)==AutostartHelper.CopyRemoval.Removed&&!File.Exists(ours1)&&File.Exists(self),"autostart: an idle copy of this program is deleted, and only that file");
    } finally { AutostartHelper.KeyName=realKey;try{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\AgDnsTest",false);}catch{} }
    // elevated staging without the ACL step (setting the protected ACL needs an admin token)
    string root=Path.Combine(dir,"elevated");
    var prep=ScriptStore.Prepare(true,root,src);
    Check(File.Exists(prep.ExePath)&&prep.ExeSha256==ScriptStore.Sha256File(src)&&ScriptStore.Sha256File(prep.ExePath)==prep.ExeSha256&&ScriptStore.ScriptNames.All(n=>File.Exists(prep.Script(n))),"elevated staging: scripts and a hash-verified exe copy are placed together");
    File.AppendAllText(prep.ExePath,"x");bool caught=false;try{ScriptStore.Verify(prep);}catch(IOException e){caught=e.Message.Contains("AntigravityGeminiDns.exe");}
    Check(caught,"elevated staging: a modified exe copy is detected before launch");
    var again=ScriptStore.Prepare(true,root,src);ScriptStore.Verify(again);
    Check(ScriptStore.Sha256File(again.ExePath)==again.ExeSha256,"elevated staging: re-staging replaces a modified exe copy");
   }finally{try{Directory.Delete(dir,true);}catch{}}
  }

  // benchmark on synthetic wire
  static string B64(string host,string ip)=>Convert.ToBase64String(Answer(host,1,IPAddress.Parse(ip).GetAddressBytes()));
  static object Ok(string host,int ms,string ip)=>new{ms,packet=B64(host,ip)};
  static object Timeout()=>new{timeout=true};
  static object Nx(string host){var q=Wire.Query(host,1,1234);q[2]=129;q[3]=131;return new{ms=40,packet=Convert.ToBase64String(q)};}
  static object Prof(string id,string[] servers,object[] tls,object[] q1,object[] q2,Dictionary<string,bool> answerTls=null)=>new{id,template="https://"+id+".test/dns-query",servers,tls,queries=new Dictionary<string,object[]>{[Program.Targets[0]]=q1,[Program.Targets[1]]=q2},answerTls=answerTls??new Dictionary<string,bool>()};
  // WinForms tests above install a UI SynchronizationContext on this thread; run the async benchmark off it so blocking on the result cannot deadlock.
  static BenchRun Bench(string scenario,CancellationToken ct=default,Action<SyntheticTransport> tweak=null)=>System.Threading.Tasks.Task.Run(()=>BenchAsync(scenario,ct,tweak)).GetAwaiter().GetResult();
  static async System.Threading.Tasks.Task<BenchRun> BenchAsync(string scenario,CancellationToken ct,Action<SyntheticTransport> tweak) {
   var t=SyntheticTransport.Load(scenario);tweak?.Invoke(t);
   var run=await Benchmark.RunAsync(t.Profiles,t.Options,t,null,ct,true);
   lastRequests=t.Requests;return run;
  }
  static int lastRequests;
  // «Замер» budget (10 s for the whole run, all profiles in parallel). Runs on the real default options, not a shortened test budget.
  static void RunBenchmarkBudgetTests() {
   string d1=Program.Targets[0],d2=Program.Targets[1];
   var sv=new[]{"193.233.112.67","193.233.112.68"};
   Check(new BenchOptions().TotalBudgetMs==10000&&new BenchOptions().RequestTimeoutMs==2500&&new BenchOptions().MaxParallel==6,"benchmark budget: the user's run is limited to 10 s; per-request timeout and parallelism are unchanged");
   object Hang(string id)=>Prof(id,sv,new object[]{new{ok=true,ms=40,delay=60000}},new object[]{new{ms=40,delay=60000,packet=B64(d1,"193.233.112.67")}},new object[]{new{ms=40,delay=60000,packet=B64(d2,"193.233.112.88")}});
   object Healthy(string id)=>Prof(id,sv,new object[]{new{ok=true,ms=40}},new[]{Ok(d1,40,"193.233.112.67")},new[]{Ok(d2,40,"193.233.112.88")});
   // one healthy profile and five hanging ones: six profiles, so all of them hold a slot from the start (MaxParallel = 6)
   var sw=Stopwatch.StartNew();
   var run=Bench(JsonSerializer.Serialize(new{attempts=6,profiles=new object[]{Healthy("healthy"),Hang("h1"),Hang("h2"),Hang("h3"),Hang("h4"),Hang("h5")}}));
   sw.Stop();
   ProfileResult R(string id)=>run.Results.Single(r=>r.Id==id);
   long ms=sw.ElapsedMilliseconds;
   Check(ms>=9500&&ms<13000,"benchmark budget: the run ends at its 10 s budget even with hanging probes (wall-clock "+ms+" ms)");
   Check(run.Truncated&&!run.Cancelled,"benchmark budget: a run cut by the budget is reported as truncated, not as cancelled");
   Check(R("healthy").Attempts==6&&R("healthy").Verdict=="Good"&&R("healthy").Selectable&&R("healthy").MedianMs==40,"benchmark budget: a profile that finished before the deadline keeps its full result");
   Check(run.Results.Where(r=>r.Id!="healthy").All(r=>r.Attempts==0&&r.Verdict=="NotMeasured"&&r.Detail=="time budget exhausted"&&!r.Selectable&&!r.Recommendable&&r.MedianMs<0),"benchmark budget: hanging probes are NotMeasured (time budget exhausted), never a false success or a latency");
   Check(run.Winner=="healthy"&&R("healthy").Best,"benchmark budget: the winner is chosen only among the profiles that were actually measured");
   // a new run after a truncated one starts clean: nothing from the cut run leaks into it
   var again=Bench(JsonSerializer.Serialize(new{attempts=6,profiles=new object[]{Healthy("healthy")}}));
   Check(!again.Truncated&&!again.Cancelled&&again.Results.Single().Attempts==6&&again.Results.Single().Verdict=="Good","benchmark budget: a fresh run after a truncated one is not contaminated by it");
   bool en0=L.En;
   try{
    L.En=true;
    Check(L.T(" с (лимит ")==" s (limit "&&L.T(" с). «Отмена» остановит замер.")==" s). “Cancel” stops it.","benchmark budget: the EN status text uses the same budget number (limit 10 s)");
   }finally{L.En=en0;}
   Check(!L.Has(" с (лимит 30 с). «Отмена» остановит замер."),"benchmark budget: the old 30 s status phrase is gone from the dictionary");
   // A profile cut mid-way by the budget keeps its real counters but is never selectable or recommended on that partial sample (short budget: the logic does not depend on its length).
   var tarpit=Prof("tarpit",sv,new object[]{new{ok=true,ms=10}},new object[]{new{ms=10,delay=600,packet=B64(d1,"193.233.112.67")}},new object[]{new{ms=10,delay=600,packet=B64(d2,"193.233.112.88")}});
   var part=Bench(JsonSerializer.Serialize(new{attempts=6,profiles=new object[]{Healthy("healthy"),tarpit}}),default,t=>t.Options.TotalBudgetMs=2000);
   ProfileResult PR(string id)=>part.Results.Single(r=>r.Id==id);
   Check(part.Truncated&&PR("tarpit").Truncated&&PR("tarpit").Attempts>=1&&PR("tarpit").Attempts<6&&PR("tarpit").Verdict=="Partial"&&!PR("tarpit").Selectable&&!PR("tarpit").Recommendable,"benchmark budget: a profile cut after some attempts is Partial and never selectable on a partial sample");
   Check(part.Winner=="healthy"&&!PR("tarpit").Best,"benchmark budget: a partially measured profile does not win over a fully measured one");
   // The answer-address TLS check ignores the budget token on the live transport (shared cache): the run must still end at the budget, not wait for it.
   var late=SyntheticTransport.Load(JsonSerializer.Serialize(new{attempts=6,totalBudgetMs=800,profiles=new object[]{Healthy("healthy")}}));
   var lateT=new LateAnswerTransport(late);
   var swLate=Stopwatch.StartNew();
   var lateRun=System.Threading.Tasks.Task.Run(()=>Benchmark.RunAsync(late.Profiles,late.Options,lateT,null,default,true)).GetAwaiter().GetResult();
   swLate.Stop();
   Check(swLate.ElapsedMilliseconds<1600,"benchmark budget: a late answer-address TLS check cannot extend the run past its budget (wall-clock "+swLate.ElapsedMilliseconds+" ms)");
   Check(lateRun.Truncated&&!lateRun.Results.Single().Selectable&&lateRun.Results.Single().Verdict=="Partial","benchmark budget: an unfinished answer-address check leaves the profile Partial and not selectable");
  }

  // Delegates to the scripted transport, but its answer-address TLS check ignores the token and takes 2 s, like a cached live handshake would.
  sealed class LateAnswerTransport:IBenchTransport {
   readonly SyntheticTransport inner;
   public LateAnswerTransport(SyntheticTransport t){inner=t;}
   public System.Threading.Tasks.Task<TlsInfo> HandshakeAsync(ProfileSpec p,string server,int timeoutMs,CancellationToken ct)=>inner.HandshakeAsync(p,server,timeoutMs,ct);
   public System.Threading.Tasks.Task<Reply> QueryAsync(ProfileSpec p,string server,string host,int type,int timeoutMs,CancellationToken ct)=>inner.QueryAsync(p,server,host,type,timeoutMs,ct);
   public async System.Threading.Tasks.Task<TlsInfo> AnswerTlsAsync(ProfileSpec p,string ip,string host,int timeoutMs,CancellationToken ct){await System.Threading.Tasks.Task.Delay(2000);return new TlsInfo{Ok=true};}
  }

  static void RunBenchmarkTests() {
   string d1=Program.Targets[0],d2=Program.Targets[1];
   var sv=new[]{"193.233.112.67","193.233.112.68"};
   object[] Stable(string host,int ms,string ip)=>new[]{Ok(host,ms,ip)};
   var scenario=JsonSerializer.Serialize(new{attempts=6,profiles=new object[]{
    Prof("good",sv,new object[]{new{ok=true,ms=40}},new[]{Ok(d1,2000,"193.233.112.67"),Ok(d1,2000,"193.233.112.67"),Ok(d1,60,"193.233.112.67"),Ok(d1,60,"193.233.112.67"),Ok(d1,60,"193.233.112.67"),Ok(d1,60,"193.233.112.67")},Stable(d2,70,"193.233.112.88")),
    Prof("slow",sv,new object[]{new{ok=true,ms=500}},Stable(d1,900,"193.233.112.67"),Stable(d2,1100,"193.233.112.88")),
    Prof("flaky",sv,new object[]{new{ok=true,ms=60}},new[]{Ok(d1,80,"193.233.112.67"),Timeout()},new[]{Ok(d2,90,"193.233.112.88"),Timeout()}),
    Prof("partial",sv,new object[]{new{ok=true,ms=50}},Stable(d1,70,"193.233.112.67"),new[]{Nx(d2)}),
    Prof("dead",new[]{"193.233.112.69"},new object[]{new{ok=false}},new[]{Timeout()},new[]{Timeout()}),
    Prof("shifty",sv,new object[]{new{ok=true,ms=45}},new[]{Ok(d1,70,"193.233.112.67"),Ok(d1,70,"193.233.112.68")},new[]{Ok(d2,70,"193.233.112.88"),Ok(d2,70,"193.233.112.89")}),
    Prof("blockedtls",sv,new object[]{new{ok=true,ms=45}},Stable(d1,70,"193.233.112.67"),Stable(d2,70,"193.233.112.88"),new Dictionary<string,bool>{[d2]=false}),
   }});
   var run=Bench(scenario);
   ProfileResult R(string id)=>run.Results.Single(r=>r.Id==id);
   Check(R("good").Verdict=="Good"&&R("good").Availability==1&&R("good").Stability==1&&R("good").DomainsOk&&R("good").Selectable,"benchmark: healthy profile is Good, fully available, stable, both Google domains verified");
   Check(R("good").MedianMs>=60&&R("good").MedianMs<=70&&R("good").P95Ms==70&&R("good").TlsMs==40,"benchmark: median/p95 latency exclude each server's cold first query (2000 ms) and TLS time is measured");
   Check(R("slow").Verdict=="Slow"&&R("slow").Selectable&&R("slow").MedianMs>=900,"benchmark: slow profile is flagged Slow but still selectable");
   Check(R("flaky").Verdict=="Flaky"&&R("flaky").Alive&&!R("flaky").Selectable&&R("flaky").Availability<0.6,"benchmark: unreliable profile is Flaky and not selectable");
   Check(R("partial").Verdict=="Partial"&&R("partial").Alive&&!R("partial").DomainsOk&&!R("partial").Selectable&&R("partial").Domains.Single(x=>x.Host==d2).Error!=null,"benchmark: profile that fails one Google domain is Partial");
   Check(R("dead").Verdict=="Dead"&&!R("dead").Alive&&R("dead").Score==0&&R("dead").Attempts==1,"benchmark: dead profile is detected after a single round (circuit breaker)");
   // ranking: two "Good" profiles, the faster one leads even when the other scores a few points more (pool rotation lowers stability, not quality)
   {var fast=new ProfileResult{Id="fast",Verdict="Good",Selectable=true,Recommendable=true,Score=97,MedianMs=50,P95Ms=90};var slow=new ProfileResult{Id="slow",Verdict="Good",Selectable=true,Recommendable=true,Score=100,MedianMs=86,P95Ms=106};var flaky=new ProfileResult{Id="flaky",Verdict="Flaky",Selectable=false,Recommendable=false,Score=99,MedianMs=10};
    var ranked=new[]{slow,flaky,fast};Benchmark.RankForTest(ranked);
    Check(fast.Rank==1&&fast.Best&&slow.Rank==2&&flaky.Rank==3,"benchmark ranking: among profiles that passed every check the faster one is best; a failing profile stays behind whatever its latency");}
   Check(R("shifty").Verdict=="Flaky"&&R("shifty").Stability<=0.5+1e-9&&R("shifty").Availability==1&&!R("shifty").Selectable,"benchmark: unstable answers lower stability, mark Flaky and block selection");
   Check(R("blockedtls").Verdict=="Partial"&&!R("blockedtls").Domains.Single(x=>x.Host==d2).TlsOk,"benchmark: a Google domain whose answer fails TLS makes the profile Partial");
   Check(run.Winner=="good"&&R("good").Rank==1&&R("good").Best&&run.Results.Count(r=>r.Best)==1,"benchmark: ranking puts the best selectable profile first and highlights exactly one");
   Check(R("slow").Rank<R("flaky").Rank&&R("dead").Rank==run.Results.Length,"benchmark: slow-but-working outranks flaky, dead is last");
   Check(lastRequests<=7*6*4,"benchmark: bounded request count (no flood): "+lastRequests);
   // cancellation and time budget
   var slowScenario=JsonSerializer.Serialize(new{attempts=6,profiles=new object[]{Prof("tarpit",sv,new object[]{new{ok=true,ms=10,delay=400}},new object[]{new{ms=10,delay=400,packet=B64(d1,"193.233.112.67")}},new object[]{new{ms=10,delay=400,packet=B64(d2,"193.233.112.88")}})}});
   using(var cts=new CancellationTokenSource(250)) {
    var sw=Stopwatch.StartNew();var cancelled=Bench(slowScenario,cts.Token);sw.Stop();
    Check(cancelled.Cancelled&&sw.ElapsedMilliseconds<2500,"benchmark: user cancellation stops the run promptly with partial results");
   }
   {
    var sw=Stopwatch.StartNew();var limited=Bench(slowScenario,default,t=>t.Options.TotalBudgetMs=300);sw.Stop();
    Check(limited.Truncated&&!limited.Cancelled&&sw.ElapsedMilliseconds<2500,"benchmark: total time budget truncates the run");
   }
   // helpers
   var samples=new List<long>{10,20,30,40,50,60,70,80,90,100};
   Check(Benchmark.Percentile(samples,0.5)==50&&Benchmark.Percentile(samples,0.95)==100&&Benchmark.Percentile(new List<long>(),0.5)==-1,"benchmark: nearest-rank percentile");
   // history
   string histOld=Benchmark.HistoryPath,histTmp=Path.Combine(Path.GetTempPath(),"agdns-bench-"+Guid.NewGuid().ToString("N"),"history.json");
   try {
    Benchmark.HistoryPath=histTmp;
    for(int i=0;i<Benchmark.HistoryLimit+5;i++)Benchmark.AppendHistory(new BenchRun{Id=i.ToString("D32"),StartedUtc=DateTime.UtcNow.ToString("o"),Winner=i==Benchmark.HistoryLimit+4?"good":null,Results=run.Results});
    var loaded=Benchmark.LoadHistory();
    Check(loaded.Count==Benchmark.HistoryLimit&&loaded.Last().Winner=="good"&&loaded.Last().Results.Length==run.Results.Length,"benchmark: history is capped and round-trips");
    File.WriteAllText(histTmp,"{ not json");
    Check(Benchmark.LoadHistory().Count==0&&Benchmark.AppendHistory(run)&&Benchmark.LoadHistory().Count==1,"benchmark: corrupt history is ignored and replaced");
    foreach(string invalid in new[]{"null","{\"Results\":null}","{\"Results\":[null]}","{\"Results\":[{\"Domains\":null}]}","{\"Results\":[{\"Domains\":[null]}]}"}){
     string damaged="{\"Runs\":["+invalid+"]}";File.WriteAllText(histTmp,damaged);
     Check(Benchmark.LoadHistory().Count==0&&File.ReadAllText(histTmp)==damaged,"benchmark: invalid persisted run is ignored without overwriting history: "+invalid);
     Check(Benchmark.AppendHistory(run)&&Benchmark.LoadHistory().Count==1,"benchmark: valid measurement recovers after invalid persisted run");
    }
    File.WriteAllText(histTmp,"{\"Runs\":[null,"+JsonSerializer.Serialize(run,Program.JsonOptions)+",{\"Results\":[null]}]}");
    var recovered=Benchmark.LoadHistory();
    Check(recovered.Count==1&&recovered[0].Id==run.Id,"benchmark: valid history survives adjacent invalid entries");
    using(var historyForm=new MainForm(true)){
     historyForm.PrecisionTrendForTest(recovered);historyForm.Show();Application.DoEvents();
     Check(historyForm.IsHandleCreated,"benchmark: recovered persisted history renders in the window");
    }
   }finally{Benchmark.HistoryPath=histOld;try{Directory.Delete(Path.GetDirectoryName(histTmp),true);}catch{}}
  }

  // plain messages and one action per code
  static void RunMessageTests() {
   var all=CodeTable.All.ToList();
   Check(all.Count>=40&&all.All(i=>!string.IsNullOrWhiteSpace(i.Title)&&!string.IsNullOrWhiteSpace(i.Text)&&!string.IsNullOrWhiteSpace(i.Action)),"messages: every table entry has a title, a text and an action");
   Check(all.All(i=>System.Text.RegularExpressions.Regex.IsMatch(i.Text,"[Ѐ-ӿ]")&&System.Text.RegularExpressions.Regex.IsMatch(i.Action,"[Ѐ-ӿ]")),"messages: texts and actions are Russian");
   Check(all.Where(i=>i.Kind==ActionKind.Restore).All(i=>i.Action.Contains("Восстановить"))&&all.Count(i=>i.Kind==ActionKind.Restore)>=4,"messages: codes that need a restore point at the single \"Восстановить\" action");
   Check(CodeTable.Lookup("APPLY_FAILED_ROLLED_BACK: DOH_ADD_VERIFICATION_FAILED").Code=="APPLY_FAILED_ROLLED_BACK"&&CodeTable.Lookup("NRPT_NOT_EFFECTIVE: Windows resolver differs").Code=="NRPT_NOT_EFFECTIVE"&&CodeTable.TokenOf("STATE_INVALID: journal unreadable")=="STATE_INVALID","messages: only the leading code token of an engine message selects the entry");
   var unknown=CodeTable.Lookup("SOMETHING_NEW: x");var empty=CodeTable.Lookup("");var native=CodeTable.Lookup("Cannot validate argument on parameter");
   Check(unknown.Kind==ActionKind.Details&&!string.IsNullOrWhiteSpace(unknown.Text)&&empty.Kind==ActionKind.Details&&native.Kind==ActionKind.Details&&!CodeTable.Known("SOMETHING_NEW")&&CodeTable.Known("WMI_NOT_RESPONDING: x"),"messages: an unknown code still gets a sentence and the Details action");
   Check(new[]{"NRPT_OR_HOSTS_CONFLICT","DOH_NO_VALIDATED_ENDPOINT","ENGINE_TIMEOUT","ELEVATION_TIMEOUT","UAC_CANCELLED","RECOVERY_REQUIRED","RESTORE_UNCONFIRMED","WMI_NOT_RESPONDING","BOOTSTRAP_FAILED","BOOTSTRAP_NO_ADDRESS","PROFILE_SWITCH_FAILED_PREVIOUS_RESTORED"}.All(CodeTable.Known),"messages: operation error codes have messages");
   Check(CodeTable.Lookup("RECOVERY_REQUIRED").Title=="Требуется восстановление"&&CodeTable.Lookup("WMI_NOT_RESPONDING").Text.Contains("изменения не начаты"),"messages: recovery and WMI messages describe the required action");
  }

  // text fits its place in the window: measured with pixel fonts at 96/120/144/192 dpi, so the check does not depend on the screen of the machine
  static int TextHeight(string text,string family,float pt,float dpi,int widthUnits){
   using(var f=new Font(family,pt*dpi/72f,GraphicsUnit.Pixel))return TextRenderer.MeasureText(text,f,new Size((int)Math.Round(widthUnits*dpi/96f),int.MaxValue/4),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding).Height;
  }
  static int TextWidth(string text,string family,float pt,float dpi){
   using(var f=new Font(family,pt*dpi/72f,GraphicsUnit.Pixel))return TextRenderer.MeasureText(text,f,new Size(int.MaxValue/4,int.MaxValue/4),TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width;
  }
  static void RunTextFitTests() {
   var shown=new List<(string Name,string Title,string Body)>();
   // REPORT_SAVED is shown in a message box (no line limit), every other entry in the status area of the main page
   foreach(var i in CodeTable.All.Where(x=>x.Code!="REPORT_SAVED"))shown.Add((i.Code,i.Title,i.Text+"\n"+i.Action));
   // the two sentences the window composes itself, with the longest profile names it can show
   string a="dns.bezmezhau.com",b2="eu.geohide.ru";
   shown.Add(("switch offer",CodeTable.Lookup("PROVIDER_SWITCH_OFFERED").Title,CodeTable.SwitchOfferText(a,b2)+"\n"+CodeTable.Lookup("PROVIDER_SWITCH_OFFERED").Action));
   shown.Add(("profile not applied",CodeTable.ProfileNotAppliedTitle,CodeTable.ProfileNotAppliedText+"\n"+CodeTable.ProfileNotAppliedAction));
   foreach(string code in new[]{"BOOTSTRAP_NO_ADDRESS","DOH_NO_VALIDATED_ENDPOINT","DNS_TIMEOUT_OR_UNREACHABLE_OR_TLS_FAILURE"}){var gi=CodeTable.Lookup(code);shown.Add(("gemini "+code,gi.Title,gi.Text+"\n"+CodeTable.GeminiAction));}
   var bad=new List<string>();
   foreach(float dpi in new[]{96f,120f,144f,192f})foreach(var s in shown) {
    // main page: title 16 pt semibold on one line (380 units, 20 units of margin), status text 9 pt in 332 x 50 units (three lines at most)
    if(TextWidth(s.Title,Theme.Family(16,true),16,dpi)>(380-20)*dpi/96f)bad.Add(s.Name+" title@"+dpi);
    if(TextHeight(s.Body,Theme.Family(9f,false),9f,dpi,332)>50*dpi/96f+0.5f)bad.Add(s.Name+" body@"+dpi);
   }
   // info page: the description is laid out by its measured height, but stays within six lines of 9 pt at the card's text width (316 units)
   foreach(float dpi in new[]{96f,120f,144f,192f})foreach(var sc0 in new[]{MainForm.BridgeScope.Core,MainForm.BridgeScope.All})if(TextHeight(MainForm.InfoTextFor(sc0),Theme.Family(9f,false),9f,dpi,316)>MainForm.InfoTextMaxH*dpi/96f+0.5f)bad.Add("info "+sc0+"@"+dpi);
   Check(bad.Count==0,"text fit: every status title is one line and every status text is at most three lines at 100/125/150/200 % DPI"+(bad.Count==0?"":" — "+string.Join("; ",bad)));
   // buttons of the main page in every state: one main button, on top, nothing overlapping
   using(var form=new MainForm(true)) {
    form.Show();Application.DoEvents();var broken=new List<string>();
    foreach(string mode in new[]{"idle","active","active-unchecked","recovery","conflict","wmi","drift","switch","noprovider"}) {
     form.SmokeState(mode);
     if(form.PrimaryButtons!=1||!form.PrimaryButtonOnTop||form.MainButtonsOverlap)broken.Add(mode+" (main "+form.PrimaryButtons+", top "+form.PrimaryButtonOnTop+", overlap "+form.MainButtonsOverlap+")");
    }
    Check(broken.Count==0,"text fit: in every state of the main page there is exactly one main button, it is on top and no buttons overlap"+(broken.Count==0?"":" — "+string.Join("; ",broken)));
    form.Close();
   }
  }

  // Test transport that moves the Gemini toggle during the first call, the way a user can while a measurement runs.
  sealed class FlipTransport:IBenchTransport {
   readonly IBenchTransport inner;bool flipped;
   public FlipTransport(IBenchTransport i){inner=i;}
   void Flip(){if(!flipped){flipped=true;Program.Gemini=!Program.Gemini;}}
   public System.Threading.Tasks.Task<TlsInfo> HandshakeAsync(ProfileSpec p,string server,int timeoutMs,CancellationToken ct){Flip();return inner.HandshakeAsync(p,server,timeoutMs,ct);}
   public System.Threading.Tasks.Task<Reply> QueryAsync(ProfileSpec p,string server,string host,int type,int timeoutMs,CancellationToken ct){Flip();return inner.QueryAsync(p,server,host,type,timeoutMs,ct);}
   public System.Threading.Tasks.Task<TlsInfo> AnswerTlsAsync(ProfileSpec p,string ip,string host,int timeoutMs,CancellationToken ct){Flip();return inner.AnswerTlsAsync(p,ip,host,timeoutMs,ct);}
  }
  // "Также для Gemini" (night): the catalog's second host set, the one switch, nothing the command line can add
  static void RunGeminiTests() {
   var cat=ProfileCatalog.BuiltIn;
   Check(cat.GeminiTargets.Length==2&&cat.GeminiTargets.All(h=>h.Contains(".")&&!h.Contains("*")&&!cat.Targets.Contains(h)),"gemini: the catalog has two exact Gemini hosts, no wildcard, none of the core hosts");
   Check(cat.TargetsFor(false).SequenceEqual(cat.Targets)&&cat.TargetsFor(true).Length==4&&cat.TargetsFor(true).Take(2).SequenceEqual(cat.Targets)&&cat.TargetsFor(true).Skip(2).SequenceEqual(cat.GeminiTargets),"gemini: off = the core hosts exactly as before; on = the core hosts plus the Gemini hosts");
   string good="{\"schema\":1,\"default\":\"a.test\",\"targets\":[\"one.example.test\",\"two.example.test\"],\"version\":\"t\",\"profiles\":[{\"id\":\"a.test\",\"slot\":\"P1\",\"template\":\"https://a.test/dns-query\",\"servers\":[\"193.233.112.67\"]}]}";
   Func<string,string> withG=g=>good.Replace("\"version\":\"t\"","\"geminiTargets\":["+g+"],\"version\":\"t\"");
   Check(ProfileCatalog.Parse(withG("\"g1.example.test\",\"g2.example.test\"")).TargetsFor(true).Length==4&&ProfileCatalog.Parse(good).GeminiTargets.Length==0,"gemini: a catalog with two Gemini hosts parses (four hosts in all); without the key the Gemini set is empty");
   Reject(()=>ProfileCatalog.Parse(withG("\"*.example.test\"")),"gemini: a wildcard among the Gemini hosts is refused");
   Reject(()=>ProfileCatalog.Parse(withG("\"g1.example.test\",\"g2.example.test\",\"g3.example.test\"")),"gemini: more than four hosts in all (core plus Gemini) are refused");
   Reject(()=>ProfileCatalog.Parse(withG("\"one.example.test\"")),"gemini: a Gemini host that repeats a core host is refused");
   // the flag and the host set move together; the host set never comes from the command line
   bool was=Program.Gemini;
   try {
    Program.Gemini=false;Check(Program.Targets.Length==2&&!Program.HasGemini(Program.Targets),"gemini: flag off -> two hosts");
    Program.Gemini=true;Check(Program.Targets.Length==4&&Program.HasGemini(Program.Targets)&&Program.Targets.Take(2).SequenceEqual(cat.Targets),"gemini: flag on -> four hosts, core first");
    Program.ValidateCliArgs(new[]{"--enable","--gemini"});Program.ValidateCliArgs(new[]{"--network","--gemini","--profile","P1"});
    Check(true,"gemini: --gemini is accepted next to the bridge commands");
    Reject(()=>Program.ValidateCliArgs(new[]{"--enable","--targets","evil.example"}),"gemini: --targets <hosts> is an unknown argument");
    Reject(()=>Program.ValidateCliArgs(new[]{"--network","--targets","evil.example"}),"gemini: --network --targets <hosts> is refused");
    Reject(()=>Program.ValidateCliArgs(new[]{"--enable","--gemini-targets","evil.example"}),"gemini: no flag names hosts, even under a Gemini-like name");
    bool refused=false;try{Wire.SystemQuery("evil.example",1);}catch(ArgumentException){refused=true;}
    Check(refused,"gemini: the exe's system-resolver query refuses a host that is not in the (Gemini-widened) catalog set");
    Check(Program.HasGemini(new[]{"daily-cloudcode-pa.googleapis.com"})==false&&Program.HasGemini(null)==false,"gemini: a core-only (older) scope is not a Gemini scope");
   } finally { Program.Gemini=was; }
   // the way out when a provider has no address for the Gemini hosts
   var nv=CodeTable.Lookup("BOOTSTRAP_NO_ADDRESS");
   Check(CodeTable.ActionFor(nv,true)==CodeTable.GeminiAction&&CodeTable.ActionFor(nv,false)==nv.Action&&CodeTable.GeminiAction.Contains("«Antigravity»")&&CodeTable.ActionFor(CodeTable.Lookup("ENGINE_TIMEOUT"),true)==CodeTable.Lookup("ENGINE_TIMEOUT").Action,"gemini: 'no address' offers the switch only while Gemini is on; unrelated failures keep their own action");
   Check(!new AppSettings().Gemini,"gemini: the setting is off by default");
   // review P2-4: toggling Gemini while a measurement runs must not turn every result into "Partial" (the result is judged against the hosts it measured)
   {
    var sv=new[]{"193.233.112.67","193.233.112.68"};bool saved=Program.Gemini;Program.Gemini=false;
    try {
     string d1=Program.Targets[0],d2=Program.Targets[1];
     string sc=JsonSerializer.Serialize(new{attempts=2,profiles=new object[]{Prof("flip",sv,new object[]{new{ok=true,ms=40}},new[]{Ok(d1,60,"193.233.112.67")},new[]{Ok(d2,70,"193.233.112.88")})}});
     var fr=System.Threading.Tasks.Task.Run(async()=>{var t=SyntheticTransport.Load(sc);return await Benchmark.RunAsync(t.Profiles,t.Options,new FlipTransport(t),null,default,true);}).GetAwaiter().GetResult();
     Check(fr.Results.Length==1&&fr.Results[0].DomainsOk&&fr.Results[0].Verdict=="Good","gemini review: a toggle flipped in the middle of a measurement does not make the profile Partial");
    } finally { Program.Gemini=saved; }
   }
   // review P2-1: a reconnect that is refused before the engine (trust, WMI, no address) leaves the bridge alone; the toggle follows the journal, not the wish
   using(var form=new MainForm(true)) {
    form.Show();Application.DoEvents();bool saved=Program.Gemini;
    try {
     string core="[\""+string.Join("\",\"",cat.Targets)+"\"]";
     form.JournalReader=()=>"{\"Phase\":\"Active\",\"Targets\":"+core+"}";
     form.SmokeState("active");form.SetGeminiForTest(true);
     Check(form.GeminiOn&&Program.Targets.Length==4,"gemini review: the person turned Gemini on over a working core bridge");
     form.SyncGeminiFromJournalForTest();
     Check(!form.GeminiOn&&!Program.Gemini&&Program.Targets.Length==2,"gemini review: the refused change is undone by the journal of the bridge that exists (toggle, flag and hosts)");
     form.JournalReader=()=>"{\"Phase\":\"Restored\",\"Targets\":"+core+"}";
     form.SetGeminiForTest(true);form.SyncGeminiFromJournalForTest();
     Check(form.GeminiOn,"gemini review: with no bridge (journal Restored) the person's choice stays");
    } finally { Program.Gemini=saved;form.Close(); }
   }
   // review P2-3: the elevated failure text keeps the wording of the scope that was asked for
   Check(CodeTable.ActionFor(CodeTable.Lookup("DOH_NO_VALIDATED_ENDPOINT"),true)==CodeTable.GeminiAction&&CodeTable.Lookup("PROFILE_SWITCH_FAILED_PREVIOUS_RESTORED").Title!="Профиль не сменился","gemini review: a rolled-back change is no longer reported as a profile that did not switch");
   // window
   using(var form=new MainForm(true)) {
    form.Show();Application.DoEvents();
    try {
     Check(!form.GeminiOn&&form.GeminiToggleEnabled&&!Program.Gemini,"gemini window: the toggle is off by default and usable"+(form.GeminiToggleEnabled&&!Program.Gemini?"":" (on="+form.GeminiOn+" enabled="+form.GeminiToggleEnabled+" flag="+Program.Gemini+" "+form.ButtonStateForTest+")"));
     form.SetGeminiForTest(true);
     Check(form.GeminiOn&&Program.Targets.Length==4&&form.ScopeSelected==MainForm.BridgeScope.CoreGemini&&form.ScopePicker.IsOn(0)&&form.ScopePicker.IsOn(1)&&!form.ScopePicker.IsOn(2)&&form.ScopePicker.Items[1]=="Gemini","gemini window: on -> four hosts everywhere and «Antigravity/Gemini» under the status");
     form.SetGeminiForTest(false);
     Check(!form.GeminiOn&&Program.Targets.Length==2&&form.ScopeSelected==MainForm.BridgeScope.Core&&form.ScopePicker.Items[0]=="Antigravity","gemini window: off again -> two hosts, «Antigravity» only");
     form.SmokeState("drift");Check(!form.GeminiToggleEnabled,"gemini window: while a bridge needs repair the toggle waits");
     form.SmokeState("active");Check(form.GeminiToggleEnabled,"gemini window: with a working bridge the toggle is usable (the change is a reconnect)");
     // the bridge that exists decides: a status with the Gemini hosts switches the toggle on, one without switches it off
     form.JournalReader=()=>"{\"Phase\":\"Active\"}";
     string scope4="[\""+string.Join("\",\"",cat.TargetsFor(true))+"\"]",scope2="[\""+string.Join("\",\"",cat.Targets)+"\"]";
     form.PresentForTest("{\"State\":\"DNS active\",\"Active\":true,\"JournalPhase\":\"Active\",\"Targets\":"+scope4+",\"Servers\":[],\"Template\":\"x\"}");
     Check(form.GeminiOn&&Program.Gemini&&Program.Targets.Length==4,"gemini window: a bridge that was applied with the Gemini hosts shows the toggle on");
     form.PresentForTest("{\"State\":\"DNS active\",\"Active\":true,\"JournalPhase\":\"Active\",\"Targets\":"+scope2+",\"Servers\":[],\"Template\":\"x\"}");
     Check(!form.GeminiOn&&!Program.Gemini&&Program.Targets.Length==2,"gemini window: after a failed or cancelled change the toggle returns to the scope the bridge really has");
    } finally { Program.Gemini=was;form.Close(); }
   }
  }

  // main page, bridge latency, settings, info page, icons and version display
  sealed class FakeSpeedProbe:ISpeedProbe {
   public Func<string,Reply> Answer=s=>new Reply{Addresses=new[]{"157.22.177.90"}};
   public readonly Queue<TlsInfo> Replies=new Queue<TlsInfo>();public int DohCalls,TlsCalls;public readonly List<string> TlsTo=new List<string>();
   public System.Threading.Tasks.Task<Reply> Doh(string template,string server,string host,int timeoutMs,CancellationToken ct){DohCalls++;return System.Threading.Tasks.Task.FromResult(Answer(server));}
   public System.Threading.Tasks.Task<TlsInfo> Tls(string ip,string host,int timeoutMs,CancellationToken ct){TlsCalls++;TlsTo.Add(ip+"|"+host);return System.Threading.Tasks.Task.FromResult(Replies.Count>0?Replies.Dequeue():new TlsInfo{Ok=false,Error="X"});}
  }
  static TlsInfo Ok(long ms)=>new TlsInfo{Ok=true,ConnectMs=ms};
  // A label fits when its text, measured the way a Label draws it (with GDI padding), fits its box at the given scale: one line for short labels, wrapped otherwise.
  static bool LabelFits(Label l,float layoutScale,float dpi){
   float wu=l.Width/layoutScale,hu=l.Height/layoutScale;
   using(var f=new Font(l.Font.FontFamily.Name,l.Font.SizeInPoints*dpi/72f,GraphicsUnit.Pixel)){
    if(hu<=20)return TextRenderer.MeasureText(l.Text,f,new Size(int.MaxValue/4,int.MaxValue/4),TextFormatFlags.SingleLine).Width<=wu*dpi/96f;
    return TextRenderer.MeasureText(l.Text,f,new Size((int)(wu*dpi/96f),int.MaxValue/4),TextFormatFlags.WordBreak).Height<=hu*dpi/96f+0.5f;
   }
  }
  static void RunFinalUiTests() {
   // the ping: one DoH answer from the active provider, the first public IPv4 in it, three validated TLS handshakes, the median TCP connect time
   var host=Program.Targets[0];var srv=new[]{"83.220.169.155","195.133.25.16"};const string tmpl="https://dns.comss.one/dns-query";
   var fp=new FakeSpeedProbe{Answer=s=>new Reply{Addresses=new[]{"0.0.0.0","10.1.2.3","157.22.177.90","132.243.112.221"}}};
   foreach(var t in new[]{Ok(40),Ok(90),Ok(60)})fp.Replies.Enqueue(t);
   var r=BridgeSpeed.MeasureAsync(fp,tmpl,srv,host,CancellationToken.None).GetAwaiter().GetResult();
   Check(r.Ms==60&&r.Proxy=="157.22.177.90"&&r.Error==null&&fp.DohCalls==1&&fp.TlsCalls==3&&fp.TlsTo.All(x=>x=="157.22.177.90|"+host),"ping: the first public IPv4 of the provider's answer, three TLS handshakes with the Google host as SNI, the median connect time");
   fp=new FakeSpeedProbe();fp.Replies.Enqueue(Ok(30));fp.Replies.Enqueue(new TlsInfo{Ok=false,ConnectMs=5,Error="RemoteCertificateNameMismatch"});fp.Replies.Enqueue(Ok(50));
   r=BridgeSpeed.MeasureAsync(fp,tmpl,srv,host,CancellationToken.None).GetAwaiter().GetResult();
   Check(r.Ms==50,"ping: a handshake that failed TLS validation is not a sample (its fast connect does not count)");
   fp=new FakeSpeedProbe();int n=0;fp.Answer=s=>{if(n++==0)throw new InvalidOperationException("down");return new Reply{Addresses=new[]{"132.243.112.221"}};};fp.Replies.Enqueue(Ok(70));
   r=BridgeSpeed.MeasureAsync(fp,tmpl,srv,host,CancellationToken.None).GetAwaiter().GetResult();
   Check(r.Ms==70&&r.Proxy=="132.243.112.221"&&fp.DohCalls==2,"ping: a provider address that does not answer is skipped, the next one is asked");
   fp=new FakeSpeedProbe();
   r=BridgeSpeed.MeasureAsync(fp,tmpl,srv,host,CancellationToken.None).GetAwaiter().GetResult();
   Check(r.Ms==-1&&r.Error=="TLS_FAILED"&&BridgeSpeed.ValueText(r)=="не ответил","ping: no valid handshake -> no number, an honest 'не ответил'");
   fp=new FakeSpeedProbe{Answer=s=>new Reply{Addresses=new[]{"0.0.0.0","192.168.1.1"}}};
   r=BridgeSpeed.MeasureAsync(fp,tmpl,srv,host,CancellationToken.None).GetAwaiter().GetResult();
   Check(r.Error=="NO_ANSWER"&&fp.TlsCalls==0,"ping: a blocked (0.0.0.0) or private answer is never measured");
   r=BridgeSpeed.MeasureAsync(new FakeSpeedProbe(),tmpl,new string[0],host,CancellationToken.None).GetAwaiter().GetResult();
   Check(r.Error=="NO_BRIDGE","ping: no active addresses -> nothing is sent");
   Check(BridgeSpeed.Grade(80)=="быстро"&&BridgeSpeed.Grade(81)=="нормально"&&BridgeSpeed.Grade(200)=="нормально"&&BridgeSpeed.Grade(201)=="медленно"&&BridgeSpeed.ValueText(new SpeedResult{Ms=42})=="42 мс · быстро","ping: plain grades (≤80 быстро, ≤200 нормально, more медленно)");
   // version: 1.0 for people, three parts for the tools
   Check(AppInfo.DisplayOf("1.0.0")=="1.0"&&AppInfo.DisplayOf("1.2.3")=="1.2.3"&&AppInfo.DisplayOf("2.1.0")=="2.1"&&AppInfo.Display==AppInfo.DisplayOf(AppInfo.Version),"version: the window shows 1.0 for 1.0.0, a patch release in full");
   // icons: every size Windows asks for, real alpha, distinct on/off
   byte[] ico=Branding.IcoBytes(true);int count=BitConverter.ToInt16(ico,4);var dirSizes=Enumerable.Range(0,count).Select(i=>ico[6+16*i]==0?256:ico[6+16*i]).ToArray();
   Check(count==8&&dirSizes.SequenceEqual(new[]{16,20,24,32,40,48,64,256})&&BitConverter.ToInt32(ico,6+16*0+12)>0&&BitConverter.ToInt32(ico,BitConverter.ToInt32(ico,6+12))==40,"icons: the .ico carries 16, 20, 24, 32, 40, 48, 64 and 256 px (DIB frames up to 64 px)");
   using(var on=Branding.Render(16,true))using(var off=Branding.Render(16,false))using(var big=Branding.Render(256,true)){
    Check(on.GetPixel(0,0).A==0&&on.GetPixel(8,10).A==255&&big.GetPixel(2,2).A==0&&big.GetPixel(128,140).A==255,"icons: transparent corners, opaque tile, at 16 and 256 px");
    bool differ=false;for(int x=0;x<16&&!differ;x++)for(int y=0;y<16&&!differ;y++)if(on.GetPixel(x,y)!=off.GetPixel(x,y))differ=true;
    Check(differ,"icons: the connected and the disconnected icon differ");
   }
   using(var set=Branding.IconSet(true))using(var i20=new Icon(set,20,20))using(var i48=new Icon(set,48,48))Check(i20.Width==20&&i48.Width==48,"icons: the window icon set hands Windows the exact 20 and 48 px frames (no stretching at 125 %)");
   using(var form=new MainForm(true)) {
    form.Show();Application.DoEvents();
    try {
     float sc=form.LayoutScale;
     // main page: no card of rows. The profile field carries the ping of the working bridge (a coloured dot and «42 мс»); every other state says the rest in its title and text
     form.SmokeState("active");
     Check(form.ProfilePingTextNow=="42 мс"&&form.ProfilePingDotNow==Theme.Green,"main: a working bridge shows its ping in the profile field: a green dot and «42 мс»");
     Check(!form.MainTexts.Any(t=>t=="Пинг"||t=="DNS"||t.StartsWith("Адреса")||t.Contains("Сетевые проверки")||t.Contains("не проверена")),"main: no «Пинг» and «DNS» rows, no address source and no 'not checked' rows on the main page");
     var bb=form.BridgeBounds;int cw=form.ClientSize.Width;
     Check(form.BridgeSubtle&&bb.Width<=form.ProfileBounds.Width&&Math.Abs(bb.Left+bb.Width/2-cw/2)<=2,"main: disconnect keeps the quiet treatment and aligns with the provider field");
     Check(!form.DetailsLinkVisible,"main: «Подробнее» is not a permanent footer: a working bridge has nothing to read there");
     foreach(string mode in new[]{"idle","checking","busy","recovery","conflict","wmi","drift","switch","noprovider","active-unchecked"}){
      form.SmokeState(mode);
      bool details=mode=="recovery"||mode=="conflict"||mode=="wmi"||mode=="noprovider";   // only a state with a message to read, and no other main action in its place
      Check(form.ProfilePingTextNow==""&&form.DetailsLinkVisible==details,"main: state "+mode+" -> no ping in the field, «Подробнее» "+(details?"shown":"hidden"));
     }
     form.SmokeState("idle");
     Check(!form.BridgeSubtle&&form.PrimaryButtons==1,"main: «Подключить мост» is the one solid main button");
     Check(form.BridgeText=="Подключить мост"&&form.BridgeFill==Theme.GreenSolid&&form.BridgeEnabled,"main: «Подключить мост» is green and enabled");
     form.SmokeState("active");
     Check(form.BridgeText=="Отключить мост"&&form.BridgeFill==Theme.Red&&form.BridgeSubtle&&form.BridgeEnabled&&form.PrimaryButtons==1,"main: «Отключить мост» is red (quiet) and enabled");
     form.SmokeState("busy");
     Check(!form.BridgeEnabled,"main: an operation in progress disables the bridge button (neutral disabled fill)");
     form.SmokeState("idle");
     Check(form.BridgeFill==Theme.GreenSolid&&form.BridgeEnabled,"main: connected -> busy -> disconnected returns to the green connect button");
     Check(MainForm.BridgeFillFor(false,false,false)==Theme.GreenSolid&&MainForm.BridgeFillFor(false,false,true)==Theme.Red&&MainForm.BridgeFillFor(false,true,true)==Theme.Danger&&MainForm.BridgeFillFor(true,false,true)==Theme.Soft,"main: bridge colour follows one state source (connect green, disconnect red, recovery solid red)");
     form.SmokeState("recovery");
     Check(!form.BridgeSubtle&&form.BridgeText=="Восстановить","main: «Восстановить» keeps the solid colour (it has to be done)");
     form.SmokeState("drift");
     Check(form.FixVisible&&form.FixFill==Theme.AmberSolid&&form.PrimaryButtons==1&&form.PrimaryButtonOnTop,"main: a bridge that needs repair shows the yellow «Починить» in the main slot instead of «Отключить мост»");
     form.SmokeState("active");form.ShowConnectionUnavailable(new AssistedResult(AssistedOutcome.Refused,"CONNECTION_SELECTION_TIMEOUT",false,"NONE",null));
     Check(form.StatusText=="Мост подключён"&&form.BridgeText=="Отключить мост"&&!form.FixVisible,"main: a switch that timed out keeps the working bridge and says so, not «требует исправления»");
     {
      int repairs=0;var hookBefore=form.MutateHook;form.MutateHook=(a,rep)=>{if(a=="enable"&&rep)repairs++;return System.Threading.Tasks.Task.CompletedTask;};
      var drift=new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.ProviderDown,CanFix=true,Utc=DateTime.UtcNow};
      form.CanRepairSilently=()=>false;form.SmokeState("active");
      Check(!form.AutoRepairAsync(drift).GetAwaiter().GetResult()&&repairs==0,"self-repair: without administrator rights nothing starts by itself (Windows would ask); the yellow «Починить» waits for one click");
      form.CanRepairSilently=()=>true;
      Check(form.AutoRepairAsync(drift).GetAwaiter().GetResult()&&repairs==1,"self-repair: with administrator rights a confirmed drift is repaired without a click");
      Check(!form.AutoRepairAsync(drift).GetAwaiter().GetResult()&&repairs==1,"self-repair: not repeated within 5 minutes");
      form.SmokeState("idle");Check(!form.AutoRepairAsync(drift).GetAwaiter().GetResult()&&repairs==1,"self-repair: never connects a bridge that is off");
      form.MutateHook=hookBefore;form.CanRepairSilently=()=>Program.IsAdmin();
     }
     // the ping in the window: measured after a status that says the bridge works, cached, re-measured on demand, never while busy or when off
     int calls=0;string[] got=null;long next=37;
     form.SpeedSource=(t,s,ct)=>{calls++;got=s;return System.Threading.Tasks.Task.FromResult(next<0?new SpeedResult{Error="TLS_FAILED",Utc=DateTime.UtcNow}:new SpeedResult{Ms=next,Proxy="157.22.177.90",Utc=DateTime.UtcNow});};
     {
      string retired="{\"State\":\"DNS active\",\"Active\":true,\"JournalPhase\":\"Active\",\"ProfileName\":\"xbox-dns.ru\",\"Servers\":[\"111.88.96.56\"],\"Template\":\"https://xbox-dns.ru/dns-query\"}";
      int before=form.ProfileCount;form.PresentForTest(retired);Application.DoEvents();
      Check(form.CurrentProfileId=="xbox-dns.ru"&&form.ProfileCount==before+1&&form.BridgeText=="Отключить мост"&&form.BridgeEnabled,"main: a bridge still applied through a retired provider names that provider and can be disconnected");
      form.PresentForTest(retired);Application.DoEvents();Check(form.ProfileCount==before+1,"main: the retired provider is listed once");
      form.PresentForTest("{\"State\":\"Not configured\",\"Code\":\"ROLLED_BACK\",\"JournalPhase\":\"Restored\",\"Active\":false}");Application.DoEvents();
      Check(form.ProfileCount==before&&form.CurrentProfileId!="xbox-dns.ru","main: once the bridge is off the retired provider leaves the list");
      calls=0;got=null;
     }
     form.SmokeState("idle");
     string active="{\"State\":\"DNS active\",\"Active\":true,\"JournalPhase\":\"Active\",\"Servers\":[\"83.220.169.155\"],\"Template\":\"https://dns.comss.one/dns-query\"}";
     form.PresentForTest(active);Application.DoEvents();
     Check(calls==1&&got!=null&&got.SequenceEqual(new[]{"83.220.169.155"})&&form.ProfilePingTextNow=="37 мс"&&form.ProfilePingDotNow==Theme.Green,"main: the status of a working bridge starts one ping of its active addresses, shown in the profile field");
     form.PresentForTest(active);Application.DoEvents();
     Check(calls==1,"main: a fresh ping is not repeated on every status");
     next=260;Pump(form.MeasureSpeedForTest(true));
     Check(calls==2&&form.ProfilePingTextNow=="260 мс"&&form.ProfilePingDotNow==Theme.Amber,"main: measuring again shows the new number, slow ones in amber");
     form.ShowDetails(null);
     Check(form.DetailsText.Contains("Пинг до сервера-посредника провайдера: 260 мс")&&form.DetailsText.Contains("Сетевые проверки:"),"main: «Подробнее» keeps the detail (ping, network checks) for those who want it");
     next=-1;Pump(form.MeasureSpeedForTest(true));
     Check(form.ProfilePingTextNow=="не ответил"&&form.ProfilePingDotNow==Theme.Amber,"main: a provider server that does not answer is said plainly");
     form.SetBusyForTest(true);Pump(form.MeasureSpeedForTest(true));int busyCalls=calls;form.SetBusyForTest(false);
     Check(busyCalls==3,"main: no ping while an operation runs");
     form.PresentForTest("{\"State\":\"Not configured\",\"JournalPhase\":\"Restored\"}");Application.DoEvents();
     Check(calls==3&&form.CurrentSpeed==null&&form.ProfilePingTextNow=="","main: a disconnected bridge is not pinged and forgets the old number");
     form.SpeedSource=null;
     // the list of profiles: every row carries a ping («—» until known); opening it measures in the background (three at a time, none while busy), and the window stays usable
     {
      int listCalls=0,running=0,maxRunning=0;var asked=new List<string>();
      form.SpeedSource=async(tmpl,servers,ct)=>{lock(asked){listCalls++;running++;maxRunning=Math.Max(maxRunning,running);asked.Add(tmpl);}await System.Threading.Tasks.Task.Delay(120);lock(asked)running--;return new SpeedResult{Ms=tmpl.Contains("comss")?300:tmpl.Contains("geohide")?1:55,Proxy="157.22.177.90",Utc=DateTime.UtcNow};};
      form.SmokeState("idle");form.PresentForTest("{\"State\":\"Not configured\",\"JournalPhase\":\"Restored\"}");Application.DoEvents();
      var withServers=Enumerable.Range(0,form.ProfileCount).Where(i=>ProfileCatalog.BuiltIn.Profiles.ElementAtOrDefault(i)?.Servers.Length>0).ToList();
      Check(Enumerable.Range(0,form.ProfileCount).All(i=>form.ProfilePingText(i)==""),"profile list: before anything is measured every row is «—» (no number is invented)");
      var sw=Stopwatch.StartNew();var task=form.MeasureProfilesForTest(false);long startedIn=sw.ElapsedMilliseconds;
      Check(startedIn<80&&!task.IsCompleted,"profile list: measuring starts in the background and does not hold the window ("+startedIn+" ms)");
      Pump(task,8000);
      Check(task.IsCompleted&&listCalls==withServers.Count&&maxRunning>=2&&maxRunning<=3,"profile list: every profile with an address is measured once, at most three at a time ("+listCalls+" of "+withServers.Count+", "+maxRunning+" at once)");
      int comssI=Enumerable.Range(0,form.ProfileCount).First(i=>ProfileCatalog.BuiltIn.Profiles[i].Template.Contains("comss"));
      Check(form.ProfilePingText(comssI)=="300 мс"&&form.ProfilePingDot(comssI)==Theme.Amber&&withServers.Where(i=>!ProfileCatalog.BuiltIn.Profiles[i].Template.Contains("comss")).All(i=>form.ProfilePingText(i).EndsWith(" мс")&&form.ProfilePingDot(i)==Theme.Green),"profile list: each row shows its own number, green up to 200 ms and amber above");
      int before=listCalls;Pump(form.MeasureProfilesForTest(false),3000);
      Check(listCalls==before,"profile list: opening the list again within two minutes measures nothing again");
      Pump(form.MeasureProfilesForTest(true),8000);
      Check(listCalls==before+withServers.Count,"profile list: a forced look measures everything again");
      form.SetBusyForTest(true);before=listCalls;Pump(form.MeasureProfilesForTest(true),2000);form.SetBusyForTest(false);
      Check(listCalls==before,"profile list: nothing is measured while an operation runs");
      // the working bridge's own profile keeps its own measurement and is left out of the list's
      form.PresentForTest(active);Application.DoEvents();lock(asked)asked.Clear();Pump(form.MeasureProfilesForTest(true),8000);
      Check(!asked.Any(a=>a.Contains("comss")),"profile list: the profile of the working bridge is not measured twice");
      form.PresentForTest("{\"State\":\"Not configured\",\"JournalPhase\":\"Restored\"}");Application.DoEvents();
      form.SpeedSource=null;
     }
     // main page: the profile field and «Через мост» (Antigravity / + Gemini Desktop / + Браузер) sit under the button, each under its own caption
     Check(form.ProfileControl.Parent==form.ScopeControl.Parent&&form.ScopeBounds.Top>=form.BridgeBounds.Bottom&&form.ProfileBounds.Top>=form.ScopeBounds.Bottom&&form.ProfileCaptionText=="DNS-профиль"&&form.MainTexts.Contains("DNS выбранных сервисов использует мост"),"main: service selection precedes provider card; caption accurately describes DNS scope");
     {var geo=form.MainGeometryProblems();Check(geo.Count==0,"main: the button, the profile field and the levels are one even block: equal gaps, equal fields, equal margins"+(geo.Count==0?"":" — "+string.Join("; ",geo)));}
     form.SelectTab(2);Application.DoEvents();
     Check(form.ToggleTexts.SequenceEqual(new[]{"Запускать при входе в Windows","Запуск от администратора","При автозапуске — сразу в трей","Крестик сворачивает в трей","Уведомлять о сбоях моста"})&&form.Toggles.All(t=>!string.IsNullOrEmpty(t.Subtitle)&&t.Icon!=IconKind.None),"settings: five switches with an icon and a line saying what each does");
     Check(form.LanguageControl.Visible&&form.LanguageControl.Parent!=null&&form.LanguageControl.Parent.Visible&&form.LanguageControl.Width>0&&form.CustomRow.Text=="Свой DoH-провайдер"&&!form.CustomPanelVisible,"settings: language and the own provider are rows; the provider editor is closed");
     form.ShowCustomPanelForTest();Check(form.CustomPanelVisible,"settings: the own provider opens its editor over the window");form.SelectTab(3);Check(!form.CustomPanelVisible,"settings: changing tab closes the editor");form.SelectTab(2);
     // info: credit, version 1.0, platform; patcher download and its author; the contact row with the Telegram mark
     form.SelectTab(4);Application.DoEvents();
     var info=form.LabelsOf(4).Select(l=>l.Text).ToList();
     Check(info.Any(t=>t=="Версия "+AppInfo.Version+"  ·  Windows x64")&&info.Contains("by @narechiyaprincess")&&!info.Any(t=>t.Contains("один файл")||t.Contains("откроется приложение")),"info: «by @narechiyaprincess», full feature version and Windows x64; no 'один файл' and no hint about how Telegram opens");
     var rows=form.InfoRows;
     Check(rows[0].Text=="Скачать Antigravity Patcher"&&rows[1].Text=="@avencoresyt"&&rows[1].Kind==LinkRow.Mark.Telegram&&rows[2].Text=="@your_sister_loves_me"&&rows[2].Kind==LinkRow.Mark.Telegram&&rows.All(r=>r.Width>=rows[0].Parent.Width-(int)(40*sc)),"info: patcher download, the patcher author and the contact are full-width rows with the Telegram mark (no lone button in a corner)");
     // every label of the settings and info pages fits its box at 100/125/150/200 %
     var bad=new List<string>();
     foreach(int tab in new[]{2,4})foreach(var l in form.LabelsOf(tab))foreach(float dpi in new[]{96f,120f,144f,192f})if(l.Text.Length>0&&!l.AutoEllipsis&&!LabelFits(l,sc,dpi))bad.Add("tab"+tab+" «"+(l.Text.Length>30?l.Text.Substring(0,30)+"…":l.Text)+"»@"+dpi);
     foreach(float dpi in new[]{96f,120f,144f,192f})if(!LabelFits(form.InfoLabel,sc,dpi))bad.Add("info text@"+dpi);
     // a switch row: icon tile 40, switch 58; title 10 pt and the second line 7.5 pt on one line each
     foreach(var t in form.Toggles)foreach(float dpi in new[]{96f,120f,144f,192f}){if(TextWidth(t.Text,Theme.Family(10,false),10,dpi)>(316-98)*dpi/96f)bad.Add("toggle «"+t.Text+"»@"+dpi);if(TextWidth(t.Subtitle,Theme.Family(7.5f,false),7.5f,dpi)>(316-98)*dpi/96f)bad.Add("toggle sub «"+t.Subtitle+"»@"+dpi);}
     foreach(var ir in form.InfoRows)foreach(float dpi in new[]{96f,120f,144f,192f}){if(TextWidth(ir.Text,Theme.Family(9.5f,true),9.5f,dpi)>(316-66)*dpi/96f)bad.Add("row «"+ir.Text+"»@"+dpi);if(TextWidth(ir.Subtitle,Theme.Family(7.5f,false),7.5f,dpi)>(316-66)*dpi/96f)bad.Add("row sub «"+ir.Subtitle+"»@"+dpi);}
     // the two chips side by side, with the longest ping text, and the longest main button
     foreach(float dpi in new[]{96f,120f,144f,192f}){
      // the profile field: the longest name, the ping beside it (dot, gap, words) and the chevron must fit the field (332 units)
      foreach(string ping in new[]{"2500 мс","не ответил","2500 ms","no answer"})foreach(string name in form.ProfileLabels){
       float need=TextWidth(name,Theme.Family(9.5f,false),9.5f,dpi)+TextWidth(ping,Theme.Family(9f,false),9f,dpi)+(14+11+10+36)*dpi/96f;
       if(name.Length<24&&need>332*dpi/96f)bad.Add("field «"+name+"» + «"+ping+"»@"+dpi);
      }
      if(TextWidth("Подключить через dns.bezmezhau.com",Theme.Family(10.5f,true),10.5f,dpi)+(int)((18+9+24)*dpi/96f)>(380-28)*dpi/96f)bad.Add("offer button@"+dpi);   // the actual maximum CTA width in UpdateButtons.Main
     }
     Check(bad.Count==0,"text fit: settings and info labels, toggles, chips and the main button fit at 100/125/150/200 % DPI"+(bad.Count==0?"":" — "+string.Join("; ",bad)));
    } finally { form.Close(); }
   }
  }

  // all of Google, the scope selector, English, the Telegram mark
  static readonly System.Text.RegularExpressions.Regex Cyr=new System.Text.RegularExpressions.Regex("[А-Яа-яЁё]");
  static void RunScopeLangTests() {
   var cat=ProfileCatalog.BuiltIn;
   Check(cat.GoogleTargets.Length>=4&&cat.GoogleTargets.All(t=>t.StartsWith(".")&&!t.Contains("*"))&&cat.GoogleTargets.Contains(".google.com")&&cat.GoogleTargets.Contains(".googleapis.com"),"google: the catalog lists Google suffixes (.google.com, .googleapis.com, …), no wildcard");
   Check(cat.TargetsForGoogle().SequenceEqual(cat.Targets.Concat(cat.GeminiTargets).Concat(cat.GoogleTargets)),"google: all Google = the core hosts, the Gemini hosts and the suffixes");
   string good="{\"schema\":1,\"default\":\"a.test\",\"targets\":[\"one.example.test\"],\"version\":\"t\",\"profiles\":[{\"id\":\"a.test\",\"slot\":\"P1\",\"template\":\"https://a.test/dns-query\",\"servers\":[\"193.233.112.67\"]}]}";
   Func<string,string> withS=g=>good.Replace("\"version\":\"t\"","\"googleTargets\":["+g+"],\"version\":\"t\"");
   Check(ProfileCatalog.Parse(withS("\".example.test\"")).GoogleTargets.Length==1,"google: a suffix entry parses");
   Reject(()=>ProfileCatalog.Parse(withS("\"*.example.test\"")),"google: a wildcard suffix is refused");
   Reject(()=>ProfileCatalog.Parse(withS("\"example.test\"")),"google: an entry without the leading dot is refused (suffixes only)");
   Reject(()=>ProfileCatalog.Parse(withS(string.Join(",",Enumerable.Range(0,9).Select(i=>"\".s"+i+".test\"")))),"google: more than eight suffixes are refused");
   bool g0=Program.Gemini,g1=Program.Google;
   try {
    Program.Gemini=true;Program.Google=true;
    Check(Program.Targets.Length==cat.Targets.Length+cat.GeminiTargets.Length+cat.GoogleTargets.Length&&Program.Hosts.SequenceEqual(cat.TargetsFor(true))&&Program.Target==cat.Targets[0],"google: on -> rules for every host and suffix; every check (DoH, TLS, Windows answers) runs on the exact hosts only");
    Check(Program.HasGoogle(Program.Targets)&&!Program.HasGoogle(cat.TargetsFor(true))&&MainForm.ScopeOf(Program.Targets)==MainForm.BridgeScope.All&&MainForm.ScopeOf(cat.TargetsFor(true))==MainForm.BridgeScope.CoreGemini&&MainForm.ScopeOf(cat.Targets)==MainForm.BridgeScope.Core,"google: a journal's scope is recognised (core, + Gemini, all Google)");
    bool refused=false;try{Wire.SystemQuery(".google.com",1);}catch(ArgumentException){refused=true;}
    Check(refused,"google: a suffix is never queried as a host");
    Program.ValidateCliArgs(new[]{"--enable","--google"});Program.ValidateCliArgs(new[]{"--network","--google","--profile","P1"});
    Check(true,"google: --google is accepted next to the bridge commands");
   } finally { Program.Gemini=g0;Program.Google=g1; }
   // the selector on the main page
   using(var form=new MainForm(true)) {
    form.Show();Application.DoEvents();bool sg=Program.Gemini,sgo=Program.Google;
    try {
     form.SetScopeForTest(MainForm.BridgeScope.Core);
     form.ClickScopeForTest(MainForm.BridgeScope.All);
     Check(form.BrowserPanelVisible&&!Program.Google&&form.BrowserBackVisible&&!form.BrowserContinueEnabled,"google window: «+ Браузер» first asks which browser is used; nothing changes yet and the question cannot be passed without an answer");
     form.BrowserCancelForTest();
     Check(!form.BrowserPanelVisible&&!Program.Google&&!Program.Gemini&&form.ScopeSelected==MainForm.BridgeScope.Core,"google window: backing out of the browser question puts the selector back where it was");
     form.ClickScopeForTest(MainForm.BridgeScope.All);Pump(form.BrowserWork);form.SelectBrowserForTest("edge");Pump(form.BrowserWork);form.BrowserContinueForTest();
     Check(Program.Google&&Program.Gemini&&form.ScopeSelected==MainForm.BridgeScope.All&&!form.BrowserPanelVisible&&form.ScopePicker.IsOn(2)&&form.ScopePicker.Items.Length==3,"google window: «+ Браузер» with no bridge, after the browser answer -> the next connect covers all of Google; the line under the status says so");
     form.ClickScopeForTest(MainForm.BridgeScope.CoreGemini);
     Check(!Program.Google&&Program.Gemini&&form.ScopeSelected==MainForm.BridgeScope.CoreGemini&&!form.ScopePicker.IsOn(2)&&form.ScopePicker.Items[1]=="Gemini","google window: «+ Gemini» -> Antigravity/Gemini");
     form.JournalReader=()=>"{\"Phase\":\"Active\"}";
     string all="[\""+string.Join("\",\"",cat.TargetsForGoogle())+"\"]";
     form.PresentForTest("{\"State\":\"DNS active\",\"Active\":true,\"JournalPhase\":\"Active\",\"Targets\":"+all+",\"Servers\":[],\"Template\":\"x\"}");
     Check(form.ScopeSelected==MainForm.BridgeScope.All&&Program.Google,"google window: a bridge applied for all of Google moves the selector to «+ Браузер»");
     form.SmokeState("drift");Check(!form.ScopeControl.Enabled,"google window: while a bridge needs repair the selector waits");
     form.SmokeState("busy");Check(!form.ScopeControl.Enabled,"google window: no change of scope during an operation");
    } finally { Program.Gemini=sg;Program.Google=sgo;form.Close(); }
   }
   // a disabled button (an operation in progress) is still clearly a button
   using(var b=new PillButton{Text="Отключить мост",Bounds=new Rectangle(0,0,200,44)}) {
    b.SetFill(Theme.Danger);b.Subtle=true;b.Enabled=false;
    using(var bmp=new Bitmap(200,44)){b.DrawToBitmap(bmp,new Rectangle(0,0,200,44));var c=bmp.GetPixel(30,22);int dist=Math.Abs(c.R-Theme.Bg.R)+Math.Abs(c.G-Theme.Bg.G)+Math.Abs(c.B-Theme.Bg.B);
     Check(dist>=40,"buttons: a disabled quiet button stays visible against the background (distance "+dist+")");}
   }
   // the Telegram mark: a blue disc with a white plane
   using(var bmp=new Bitmap(56,56)){using(var g=Graphics.FromImage(bmp)){g.Clear(Color.Black);Branding.DrawTelegram(g,new RectangleF(0,0,56,56));}
    var disc=bmp.GetPixel(28,48);var plane=bmp.GetPixel(35,31);
    Check(disc.B>180&&disc.R<90&&plane.R>220&&plane.G>220&&plane.B>220,"icons: the Telegram mark is a blue disc with a white plane");}
   // English: every message has a translation that fits; the whole window has no Russian left
   bool en0=L.En;
   try {
    L.En=true;
    var missing=CodeTable.All.Where(i=>!L.HasCode(i.Code)).Select(i=>i.Code).ToList();
    Check(missing.Count==0,"english: every message code has an English title, text and action"+(missing.Count==0?"":" — "+string.Join(",",missing)));
    var bad=new List<string>();
    foreach(float dpi in new[]{96f,120f,144f,192f})foreach(var i in CodeTable.All.Where(x=>x.Code!="REPORT_SAVED")){var e=CodeTable.Lookup(i.Code);
     if(TextWidth(e.Title,Theme.Family(16,true),16,dpi)>(380-20)*dpi/96f)bad.Add(i.Code+" title@"+dpi);
     if(TextHeight(e.Text+"\n"+e.Action,Theme.Family(9f,false),9f,dpi,332)>48*dpi/96f+0.5f)bad.Add(i.Code+" body@"+dpi);}
    Check(bad.Count==0,"english: message titles are one line and texts at most three lines at 100–200 % DPI"+(bad.Count==0?"":" — "+string.Join("; ",bad)));
    Check(NavBar.Labels.Select(L.T).SequenceEqual(new[]{"Bridge","Test","Settings","Logs","Info"})&&BenchTable.VerdictText("Good")=="Works"&&BridgeSpeed.ValueText(new SpeedResult{Ms=42})=="42 ms · fast","english: tabs, verdicts and the ping value");
    using(var form=new MainForm(true)) {
     form.Show();Application.DoEvents();var left=new List<string>();
     void Scan(string where){foreach(var t in form.AllVisibleTexts())if(Cyr.IsMatch(t)&&t!="Русский"&&t!="Русский · English")   /* language names are written in their own language */left.Add(where+": «"+t+"»");}
     foreach(string mode in new[]{"idle","checking","busy","active","active-unchecked","recovery","conflict","wmi","drift","switch","noprovider"}){form.SmokeState(mode);Scan(mode);}
     form.SetScopeForTest(MainForm.BridgeScope.CoreGemini);Scan("scope gemini");form.SetScopeForTest(MainForm.BridgeScope.Core);
     for(int tab=1;tab<5;tab++){form.SelectTab(tab);Application.DoEvents();Scan("tab"+tab);}
     form.SelectTab(2);form.ShowCustomPanelForTest();Scan("custom");
     foreach(string bid in new string[]{null,"chrome","edge","firefox","brave","other"}){form.SmokeBrowserPanel(bid,true);Scan("browser "+(bid??"none"));}
     form.BrowserCancelForTest();
     Check(left.Count==0,"english: no Russian text anywhere in the window (every state, every tab, the provider editor)"+(left.Count==0?"":" — "+string.Join("; ",left.Distinct().Take(12))));
     form.Close();
    }
   } finally { L.En=en0; }
  }

  // speed and comfort (premium pass): instant state from the journal, background status, status taken from the operation, keys, window position
  static void Pump(System.Threading.Tasks.Task t,int maxMs=5000){var sw=Stopwatch.StartNew();while(!t.IsCompleted&&sw.ElapsedMilliseconds<maxMs){MainForm.PumpBrowserMessages();Thread.Sleep(5);}MainForm.PumpBrowserMessages();if(!t.IsCompleted)throw new TimeoutException("Offline UI task exceeded its deadline");t.GetAwaiter().GetResult();}
  static void RunUxTests() {
   // the elevated answer already carries the status after the operation
   string st="{\"State\":\"DNS active\",\"Code\":\"DNS_CONFIGURED_APPLICATION_NOT_TESTED\",\"JournalPhase\":\"Active\",\"Active\":true}";
   Check(MainForm.StatusFromOperation("enable",0,st)!=null&&MainForm.StatusFromOperation("enable",0,st).Contains("DNS active"),"ux: a successful connect brings its own status (no second engine start)");
   string rs="{\"State\":\"Not configured\",\"Code\":\"ROLLED_BACK\",\"Status\":{\"State\":\"Not configured\",\"Code\":\"NOT_CONFIGURED\",\"JournalPhase\":\"Restored\"}}";
   Check(MainForm.StatusFromOperation("restore",0,rs)!=null&&MainForm.StatusFromOperation("restore",0,rs).Contains("Restored"),"ux: a successful disconnect brings the status from its Status field");
   Check(MainForm.StatusFromOperation("enable",1,st)==null&&MainForm.StatusFromOperation("restore",0,"{\"State\":\"Not configured\",\"Code\":\"ROLLED_BACK\"}")==null&&MainForm.StatusFromOperation("enable",0,"{ broken")==null&&MainForm.StatusFromOperation("enable",0,"{\"State\":\"x\"}")==null,"ux: a failure, a missing Status or an answer that is not a status -> the window asks the engine as before");
   // the screen check for a remembered window position
   var scr=new[]{new Rectangle(0,0,1536,824)};
   Check(MainForm.TitleBarOnScreen(new Rectangle(100,100,475,730),scr)&&!MainForm.TitleBarOnScreen(new Rectangle(1400,100,475,730),scr)&&!MainForm.TitleBarOnScreen(new Rectangle(100,-200,475,730),scr)&&MainForm.TitleBarOnScreen(new Rectangle(1700,50,475,730),new[]{scr[0],new Rectangle(1536,0,1920,1040)}),"ux: a remembered position is used only while the whole title bar is on an existing screen");
   Check(new AppSettings().WinX==int.MinValue,"ux: no remembered position by default (Windows centres the first window)");
   Check(Theme.Has(Theme.Family(16,true))&&Theme.Has(Theme.Family(9,false))&&Theme.Has(Theme.IconFont)&&Theme.Has(Theme.MonoFont),"ux: every font the window asks for is installed (Windows 11 cut or the Windows 10 fallback)");
   using(var form=new MainForm(true)) {
    form.Show();Application.DoEvents();
    try {
     // start-up: the journal decides the first picture at once; a configured bridge is "checking", never green before the engine has confirmed it
     form.JournalReader=()=>"{\"Phase\":\"Active\"}";form.ShowJournalHintForTest();
     Check(form.BridgeEnabled&&form.BridgeText=="Отключить мост"&&form.StatusText=="Мост подключён"&&!form.BridgeActive&&form.CheckingShown,"ux start: a configured bridge is shown at once with «Отключить мост» usable, as 'checking' and not green");
     form.JournalReader=()=>"{\"Phase\":\"Restored\"}";form.ShowJournalHintForTest();
     Check(form.BridgeEnabled&&form.BridgeText=="Подключить мост"&&form.StatusText=="Мост отключён"&&!form.CheckingShown,"ux start: no bridge -> «Подключить мост» at once");
     form.JournalReader=()=>null;form.ShowJournalHintForTest();
     Check(form.BridgeEnabled&&form.BridgeText=="Подключить мост"&&form.StatusText=="Мост отключён","ux start: no journal at all -> «Подключить мост» at once");
     form.JournalReader=()=>"{\"Phase\":\"Restoring\"}";form.ShowJournalHintForTest();
     Check(form.BridgeEnabled&&form.BridgeText=="Восстановить"&&form.RecoveryNeeded&&form.StatusText=="Мост требует исправления","ux start: an unfinished operation -> repair state and Restore action at once");
     // the background status confirms without locking the window
     form.JournalReader=()=>"{\"Phase\":\"Active\"}";form.ShowJournalHintForTest();
     var gate=new System.Threading.Tasks.TaskCompletionSource<(int Code,string Output)>();
     form.StatusSource=_=>gate.Task;
     var r1=form.RefreshForTest();Application.DoEvents();
     Check(form.LogoSpinning&&form.BridgeEnabled&&!form.Busy,"ux start: while the status is read the logo works and the buttons stay usable");
     gate.SetResult((0,st));Pump(r1);
     Check(form.BridgeActive&&!form.CheckingShown&&!form.LogoSpinning&&form.StatusText=="Мост подключён","ux start: the engine's answer turns the page green and stops the logo");
     // a background answer that started before a user operation is dropped (the operation's own result is newer)
     form.JournalReader=()=>"{\"Phase\":\"Restored\"}";form.ShowJournalHintForTest();
     var gate2=new System.Threading.Tasks.TaskCompletionSource<(int Code,string Output)>();
     form.StatusSource=_=>gate2.Task;
     var r2=form.RefreshForTest();Application.DoEvents();
     form.SetBusyForTest(true);form.SetBusyForTest(false);
     gate2.SetResult((0,st));Pump(r2);
     Check(!form.BridgeActive,"ux: a stale background status does not overwrite what a user operation started after it");
     // keys
     form.SmokeState("idle");form.ShowDetails(null);
     Check(form.DetailsVisible&&form.KeyForTest(Keys.Escape)&&!form.DetailsVisible,"ux keys: Esc closes «Подробнее»");
     Check(form.KeyForTest(Keys.Control|Keys.D2)&&form.SelectedTabIndex==1&&form.KeyForTest(Keys.Control|Keys.D1)&&form.SelectedTabIndex==0,"ux keys: Ctrl+1…5 switch tabs");
     form.SmokeState("busy");
     Check(form.LogoSpinning&&!form.BridgeEnabled,"ux: an operation in progress shows the working logo and locks the buttons");
     form.SmokeState("idle");
     Check(!form.LogoSpinning,"ux: idle again, the logo is still");
    } finally { form.Close(); }
   }
  }

  // restore that cannot hang silently: manual instruction from the journal, text only
  static void RunRecoveryTests() {
   string owner=new string('a',32),g1="{82c33f7e-1111-4222-8333-444455556666}",g2="{a99b6302-aaaa-4bbb-8ccc-ddddeeeeffff}";
   string journal="{\"Schema\":1,\"Owner\":\""+owner+"\",\"Phase\":\"Restoring\",\"Targets\":[\"one.example.test\",\"two.example.test\"],\"Servers\":[\"179.254.166.171\",\"164.5.224.81\"],"+
    "\"Rules\":[{\"Namespace\":\"one.example.test\",\"Name\":\""+g1+"\",\"Fingerprint\":\"x\"},{\"Namespace\":\"two.example.test\",\"Name\":\""+g2+"\",\"Fingerprint\":\"y\"}],"+
    "\"Doh\":[{\"Server\":\"179.254.166.171\",\"Template\":\"t\",\"Created\":true,\"Ownership\":\"Owned\"},{\"Server\":\"164.5.224.81\",\"Template\":\"t\",\"Created\":true,\"Ownership\":\"Owned\"},{\"Server\":\"9.9.9.9\",\"Template\":\"t\",\"Created\":false,\"Ownership\":\"Shared\"},{\"Server\":\"8.8.4.4\",\"Template\":\"t\",\"Created\":true,\"Ownership\":\"Pending\"},{\"Server\":\"1.0.0.1\",\"Template\":\"t\",\"Created\":true,\"Ownership\":\"Planned\"}]}";
   string text=Recovery.BuildInstruction(journal);
   const string pk=@"HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig\",dk=@"HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DohWellKnownServers\";
   Check(text!=null&&text.Contains(pk+g1)&&text.Contains(pk+g2),"recovery text: names the exact NRPT keys recorded in the journal");
   Check(text.Contains(dk+"179.254.166.171")&&text.Contains(dk+"164.5.224.81"),"recovery text: names the DoH keys the program owned");
   Check(!text.Contains(dk+"9.9.9.9")&&!text.Contains(dk+"1.0.0.1")&&text.Contains(dk+"8.8.4.4")&&text.IndexOf("Pending",StringComparison.Ordinal)>text.IndexOf(dk+"164.5.224.81",StringComparison.Ordinal),"recovery text: shared and merely planned registrations are never listed; a pending one is flagged as uncertain");
   Check(text.Contains("AgDns:"+owner)&&text.Contains("AgDns "+owner)&&text.Contains("перезагрузк"),"recovery text: tells to verify the owner marker before deleting and to reboot");
   Check(Recovery.BuildInstruction(journal.Replace("Restoring","Restored"))==null&&Recovery.BuildInstruction("{ not json")==null&&Recovery.BuildInstruction(null)==null&&Recovery.BuildInstruction(journal.Replace(owner,"zz"))==null,"recovery text: nothing for a restored, unreadable or foreign-owner journal");
   string tampered=Recovery.BuildInstruction(journal.Replace(g1,@"..\\..\\SYSTEM\\Setup").Replace("179.254.166.171","179.254.166.171\\\\..\\\\x"));
   Check(tampered!=null&&!tampered.Contains(@"..\..")&&!tampered.Contains(@"\..\x"),"recovery text: a name or address in the journal that is not a GUID / canonical IP never becomes a registry path");
   string noName=Recovery.BuildInstruction(journal.Replace("\"Name\":\""+g1+"\"","\"Name\":null"));
   Check(noName!=null&&noName.Contains("имя в журнале не записано")&&noName.Contains("Comment = AgDns:"+owner),"recovery text: a rule whose name was never journaled is described by its owner marker");
   using(var doc=JsonDocument.Parse(Recovery.UnconfirmedJson("ENGINE_TIMEOUT",journal))) {
    var r=doc.RootElement;Check(r.GetProperty("Code").GetString()=="RESTORE_UNCONFIRMED"&&r.GetProperty("Instruction").GetString().Contains(pk+g1)&&r.GetProperty("State").GetString()=="Failed (restorable)","recovery: an unfinished restore is reported as RESTORE_UNCONFIRMED with the instruction");
   }
   var hung=new GuardResult{Allowed=false,Code=Recovery.WmiNotResponding,Detail="x"};
   using(var d1=JsonDocument.Parse(Recovery.BlockedJson("restore",hung,journal)))using(var d2=JsonDocument.Parse(Recovery.BlockedJson("enable",hung,journal))) {
    Check(d1.RootElement.GetProperty("Code").GetString()=="RESTORE_UNCONFIRMED"&&d2.RootElement.GetProperty("Code").GetString()=="WMI_NOT_RESPONDING"&&!d2.RootElement.TryGetProperty("Instruction",out _),"recovery: a blocked restore gives the manual steps, a blocked connect only says that WMI does not answer");
   }
   Check(Program.GuardDeadline<=TimeSpan.FromSeconds(10)&&Program.GuardDeadline>=TimeSpan.FromSeconds(5),"recovery: the pre-check deadline is about ten seconds");
  }

  // review of the 1.4 code (session 1.5): defects found by reading Enable/Apply/Restore, Recovery, Connect, MainForm
  static void RunReviewTests() {
   // R1: a profile switch makes two preflights; the engine and the waiting window need room for them, and the window waits longer than the child's own deadline.
   Check(Program.Deadline("enable")>=TimeSpan.FromSeconds(150)&&Program.Deadline("enable")>Program.Deadline("diagnose")&&Program.Deadline("restore")<=TimeSpan.FromSeconds(30),"review: enable has room for two preflights; restore stays short");
   Check(new[]{"enable","restore","diagnose"}.All(a=>Program.ElevationWait(a)>Program.Deadline(a)),"review: the window waits for the elevated child longer than the child's own engine deadline");
   // R2: the caller's cancellation is not "WMI does not answer"
   bool threw=false;
   using(var cts=new CancellationTokenSource()) {
    cts.Cancel();
    try{Sync(()=>Recovery.CheckCimAsync(null,null,null,cts.Token));}catch(OperationCanceledException){threw=true;}
    Check(threw,"review: a cancelled pre-check throws OperationCanceledException instead of reporting WMI_NOT_RESPONDING");
    threw=false;try{Sync(()=>Program.Engine("status",false,false,null,null,null,cts.Token));}catch(OperationCanceledException){threw=true;}
    Check(threw,"review: Program.Engine propagates a caller's cancellation (no fake ENGINE_TIMEOUT)");
   }
   // R3: the pre-check tolerates one slow answer (cold CIM provider) but not a hang; other failures are not retried
   string timeoutJson="{\"State\":\"Failed (restorable)\",\"Code\":\"ENGINE_TIMEOUT\"}",okJson="{\"State\":\"Not configured\",\"Code\":\"NOT_CONFIGURED\"}";
   int calls=0;var seen=new List<TimeSpan>();
   Func<(int,string)[],Func<TimeSpan,System.Threading.Tasks.Task<(int Code,string Output)>>> script=answers=>d=>{seen.Add(d);var a=answers[Math.Min(calls,answers.Length-1)];calls++;return System.Threading.Tasks.Task.FromResult((a.Item1,a.Item2));};
   calls=0;var slowOnce=Sync(()=>Recovery.CheckCimAsync(null,null,null,default,null,script(new[]{(1460,timeoutJson),(0,okJson)})));
   Check(slowOnce.Allowed&&calls==2,"review: one slow pre-check answer followed by a normal one is not a hung WMI");
   calls=0;var hungTwice=Sync(()=>Recovery.CheckCimAsync(null,null,null,default,null,script(new[]{(1460,timeoutJson),(1460,timeoutJson)})));
   Check(!hungTwice.Allowed&&hungTwice.Code==Recovery.WmiNotResponding&&calls==Recovery.GuardAttempts&&seen.All(d=>d==Program.GuardDeadline),"review: two timeouts in a row are WMI_NOT_RESPONDING, each attempt keeps the ten-second deadline");
   calls=0;var broken=Sync(()=>Recovery.CheckCimAsync(null,null,null,default,null,script(new[]{(1,"{\"Code\":\"STATE_INVALID\"}"),(0,okJson)})));
   Check(!broken.Allowed&&broken.Code=="STATE_INVALID"&&calls==1,"review: a fast engine failure is reported as it is and not retried");
   calls=0;var fine=Sync(()=>Recovery.CheckCimAsync(null,null,null,default,null,script(new[]{(0,okJson)})));
   Check(fine.Allowed&&calls==1,"review: a normal answer costs one attempt");
   // R4: when the engine cannot answer, the journal file still keeps "Восстановить" available
   string owner=new string('b',32),g1="{82c33f7e-1111-4222-8333-444455556666}";
   string journal="{\"Schema\":1,\"Owner\":\""+owner+"\",\"Phase\":\"Restoring\",\"Rules\":[{\"Namespace\":\"one.example.test\",\"Name\":\""+g1+"\"}],\"Doh\":[]}";
   Check(Recovery.PhaseOfJournal(journal)=="Restoring"&&Recovery.PhaseOfJournal(journal.Replace("Restoring","Restored"))=="Restored"&&Recovery.PhaseOfJournal("{ broken")==null&&Recovery.PhaseOfJournal(null)==null&&Recovery.PhaseOfJournal(journal.Replace("Restoring","Nonsense"))==null,"review: the journal phase is read from state.json text; garbage gives null");
   using(var form=new MainForm(true)) {
    form.Show();Application.DoEvents();
    form.JournalReader=()=>journal;
    form.PresentForTest(timeoutJson);
    Check(form.BridgeEnabled&&form.BridgeText=="Восстановить"&&form.RecoveryNeeded&&form.LastInstruction!=null&&form.LastInstruction.Contains(g1.Trim('{','}')),"review: engine timeout + journal in Restoring -> the restore button stays enabled and the manual steps are ready");
    form.JournalReader=()=>null;
    form.PresentForTest(timeoutJson);
    Check(!form.BridgeEnabled,"review: engine timeout without any journal -> nothing is claimed, the button stays disabled");
    form.Close();
   }
   // R6: --repair is a known flag; the live redaction context knows this machine's own names
   bool repairOk=true;try{Program.ValidateCliArgs(new[]{"--enable","--repair"});}catch(Exception){repairOk=false;}
   Check(repairOk,"review: the CLI accepts --enable --repair");
   var live=RedactionContext.Live(()=>new string[0]);
   string own=(Environment.GetEnvironmentVariable("USERPROFILE")??"")+@"\Desktop\a.txt by "+Environment.UserName+" on "+Environment.MachineName;
   string cleaned=SupportReport.Sanitize(own,live);
   Check(Environment.UserName.Length<2||new[]{"user","computer","ssid","profile","personal-path"}.Any(t=>t.Contains(Environment.UserName.ToLowerInvariant())||t.Contains(Environment.MachineName.ToLowerInvariant()))||(cleaned.IndexOf(Environment.UserName,StringComparison.OrdinalIgnoreCase)<0&&cleaned.IndexOf(Environment.MachineName,StringComparison.OrdinalIgnoreCase)<0),"review: the live redaction context removes this machine's real user name, computer name and profile folder");
   // R5: the manual steps name the button that exists in that state
   string steps=Recovery.BuildInstruction(journal);
   Check(steps!=null&&steps.Contains("«Восстановить»")&&!steps.Contains("«Отключить мост»"),"review: the manual steps tell to press «Восстановить» (the button of the recovery state)");
  }

  // 1.5-1: tray health check (fake probe / fake clock; nothing touches the system)
  sealed class FakeProbe:IHealthProbe {
   public string JournalText="{\"Schema\":1,\"Phase\":\"Active\"}";public Func<(int Code,string Output)> StatusFn;public Action OnStatus;
   public int StatusCalls,ProviderCalls,SystemCalls;public string[] LastServers;public string LastTemplate;public TimeSpan? LastDeadline;public ProviderProbe Prov;public bool ProviderThrows;public Dictionary<string,string[]> Sys=new Dictionary<string,string[]>();
   public string Journal()=>JournalText;
   public System.Threading.Tasks.Task<(int Code,string Output)> Status(TimeSpan deadline,CancellationToken ct){StatusCalls++;LastDeadline=deadline;ct.ThrowIfCancellationRequested();OnStatus?.Invoke();return System.Threading.Tasks.Task.FromResult(StatusFn());}
   public System.Threading.Tasks.Task<ProviderProbe> Provider(string[] servers,string template,CancellationToken ct){ProviderCalls++;LastServers=servers;LastTemplate=template;if(ProviderThrows)throw new InvalidOperationException("offline");return System.Threading.Tasks.Task.FromResult(Prov);}
   public System.Threading.Tasks.Task<string[]> SystemAnswers(string host,CancellationToken ct){SystemCalls++;return System.Threading.Tasks.Task.FromResult(Sys.TryGetValue(host,out var v)?v:new string[0]);}
  }
  const string HealthyStatus="{\"State\":\"DNS active\",\"Code\":\"DNS_CONFIGURED_APPLICATION_NOT_TESTED\",\"Active\":true,\"JournalPhase\":\"Active\",\"Servers\":[\"203.0.113.9\",\"203.0.113.10\"],\"Template\":\"https://x.example.test/dns-query\"}";
  static string DriftStatus(string code)=>"{\"State\":\"Failed (restorable)\",\"Code\":\""+code+"\",\"Active\":false,\"JournalPhase\":\"Active\",\"Servers\":[\"203.0.113.9\"],\"Template\":\"https://x.example.test/dns-query\"}";
  static FakeProbe HealthyProbe(HealthOptions o) {
   var p=new FakeProbe{StatusFn=()=>(0,HealthyStatus)};
   p.Prov=new ProviderProbe{Validated=new[]{"203.0.113.9"}};
   foreach(string t in o.Targets){p.Prov.Answers[t]=new[]{"198.51.100.1","198.51.100.2"};p.Sys[t]=new[]{"198.51.100.1"};}
   return p;
  }
  static HealthReport Hc(HealthMonitor m)=>Sync(()=>m.CheckAsync(HealthTrigger.Manual,default));

  static void RunHealthTests() {
   var o=new HealthOptions{Targets=new[]{"a.example.test","b.example.test"}};
   // monitor: what is checked, in which order, and what it never does
   var p=HealthyProbe(o);var m=new HealthMonitor(p,o);var r=Hc(m);
   Check(r.Kind==HealthKind.Healthy&&r.Code==HealthMonitor.Ok&&!r.CanFix&&p.StatusCalls==1&&p.LastDeadline==o.CimDeadline&&p.ProviderCalls==1&&p.SystemCalls==2,"health: rules, DoH and the Windows resolver all fine -> Healthy; CIM is asked once, with the short deadline");
   Check(o.CimDeadline<=TimeSpan.FromSeconds(20)&&o.Period>=TimeSpan.FromMinutes(30)&&o.Period<=TimeSpan.FromMinutes(60)&&o.MinInterval>=TimeSpan.FromSeconds(60),"health: short CIM deadline, 30-60 minute period, at most one check per minute");
   foreach(string j in new[]{null,"","{ not json","{\"Phase\":\"Restored\"}"}) {
    var pj=HealthyProbe(o);pj.JournalText=j;var rj=Hc(new HealthMonitor(pj,o));
    Check(rj.Kind==HealthKind.Off&&!rj.CanFix&&pj.StatusCalls==0&&pj.ProviderCalls==0&&pj.SystemCalls==0,"health: no journal or a switched-off bridge -> Off, with no CIM and no network at all ("+(j??"null")+")");
   }
   var prec=HealthyProbe(o);prec.JournalText="{\"Phase\":\"Restoring\"}";var rrec=Hc(new HealthMonitor(prec,o));
   Check(rrec.Kind==HealthKind.Drift&&rrec.Code=="RECOVERY_REQUIRED"&&!rrec.CanFix&&prec.StatusCalls==0,"health: an unfinished operation is reported as RECOVERY_REQUIRED without any CIM call (the main button 'Восстановить' handles it)");
   var pd=HealthyProbe(o);pd.StatusFn=()=>(0,DriftStatus("RECOVERY_REQUIRED"));var rd=Hc(new HealthMonitor(pd,o));
   Check(rd.Kind==HealthKind.Drift&&rd.Code==HealthMonitor.DriftRules&&rd.CanFix&&rd.Detail=="RECOVERY_REQUIRED"&&pd.ProviderCalls==0,"health: a rule removed behind our back (engine status says RECOVERY_REQUIRED) -> Drift with 'Починить'");
   pd.StatusFn=()=>(0,DriftStatus("NRPT_NOT_EFFECTIVE"));var rn=Hc(new HealthMonitor(pd,o));
   Check(rn.Kind==HealthKind.Drift&&rn.Code==HealthMonitor.ResolverMismatch&&rn.CanFix,"health: rules present but not effective -> HEALTH_RESOLVER_MISMATCH with 'Починить'");
   foreach(string manual in new[]{"OWNED_RULE_CHANGED","NRPT_OR_HOSTS_CONFLICT","DOH_CHANGED_INDEPENDENTLY"}) {
    pd.StatusFn=()=>(0,DriftStatus(manual));var rm=Hc(new HealthMonitor(pd,o));
    Check(rm.Kind==HealthKind.Drift&&rm.Code==HealthMonitor.NeedsManual&&!rm.CanFix,"health: "+manual+" is never offered for automatic repair (the program does not overwrite what it does not own)");
   }
   // the real engine reports "CODE: explanation"; the whole message must not defeat the manual-care list (review 3)
   foreach(string manual in new[]{"OWNED_RULE_CHANGED: retained, no automatic overwrite","OWNED_RULE_CHANGED: security/encoding/auxiliary policy edited; retained","DOH_REGISTRATION_CONFLICT","DOH_REGISTRATION_CONFLICT: template differs"}) {
    pd.StatusFn=()=>(0,DriftStatus(manual));var rm=Hc(new HealthMonitor(pd,o));
    Check(rm.Kind==HealthKind.Drift&&rm.Code==HealthMonitor.NeedsManual&&!rm.CanFix,"health: the engine's full message '"+manual+"' is manual care, never offered for repair");
   }
   pd.StatusFn=()=>(0,DriftStatus("NRPT_NOT_EFFECTIVE: Windows resolver differs"));var rnf=Hc(new HealthMonitor(pd,o));
   Check(rnf.Code==HealthMonitor.ResolverMismatch,"health: 'NRPT_NOT_EFFECTIVE: …' with an explanation is still the resolver mismatch");
   // a hung CIM: Unknown, not drift; the provider and the resolver are not probed; no claim either way
   var ph=HealthyProbe(o);ph.StatusFn=()=>(1460,"{\"State\":\"Failed (restorable)\",\"Code\":\"ENGINE_TIMEOUT\"}");var rh=Hc(new HealthMonitor(ph,o));
   Check(rh.Kind==HealthKind.Unknown&&rh.Code==HealthMonitor.Unknown&&!rh.CanFix&&ph.ProviderCalls==0&&ph.SystemCalls==0&&ph.StatusCalls==1,"health: a hung CIM is 'could not check' (Unknown), asked once, never turned into drift or a retry loop");
   ph.StatusFn=()=>(1,"{\"Code\":\"STATE_INVALID\"}");
   Check(Hc(new HealthMonitor(ph,o)).Kind==HealthKind.Unknown,"health: an engine failure of the check itself is Unknown, not drift");
   // soft failures (network-dependent) are confirmed by a second check before they are called drift
   var pn=HealthyProbe(o);pn.Prov=new ProviderProbe();var mn=new HealthMonitor(pn,o);
   var n1=Hc(mn);var n2=Hc(mn);
   Check(n1.Kind==HealthKind.Suspect&&n1.Code==HealthMonitor.ProviderDown&&n2.Kind==HealthKind.Drift&&n2.Code==HealthMonitor.ProviderDown&&n2.CanFix,"health: provider addresses not answering -> first Suspect, second consecutive time Drift with 'Починить'");
   pn.Prov=new ProviderProbe{Validated=new[]{"203.0.113.9"}};foreach(string t in o.Targets){pn.Prov.Answers[t]=new[]{"198.51.100.1"};pn.Sys[t]=new[]{"198.51.100.1"};}
   Check(Hc(mn).Kind==HealthKind.Healthy,"health: recovery of the provider clears the suspicion");
   pn.Prov=new ProviderProbe();Check(Hc(mn).Kind==HealthKind.Suspect,"health: after a healthy check a new failure starts the confirmation again");
   var pt=new FakeProbe{StatusFn=()=>(0,HealthyStatus),Prov=new ProviderProbe{Validated=new[]{"203.0.113.9"}}};foreach(string t in o.Targets){pt.Prov.Answers[t]=new[]{"198.51.100.1"};pt.Sys[t]=new[]{"192.0.2.55"};}
   var mt=new HealthMonitor(pt,o);Check(mt.CheckAsync(HealthTrigger.Manual,default).GetAwaiter().GetResult().Kind==HealthKind.Suspect,"health: Windows answering with addresses the provider did not give -> Suspect");
   var rt=Hc(mt);Check(rt.Kind==HealthKind.Drift&&rt.Code==HealthMonitor.ResolverMismatch&&!rt.CanFix,"health: the same mismatch twice -> Drift (HEALTH_RESOLVER_MISMATCH), never offered for 'Починить' (-Repair changes nothing when the addresses match)");
   var pe=HealthyProbe(o);pe.Sys[o.Targets[0]]=new string[0];Check(Hc(new HealthMonitor(pe,o)).Kind==HealthKind.Suspect,"health: an empty Windows answer is not a match");
   var px=HealthyProbe(o);px.ProviderThrows=true;Check(Hc(new HealthMonitor(px,o)).Kind==HealthKind.Suspect,"health: a failing provider probe is a soft failure, not a crash");
   // the user switches the bridge off while the check runs: the automation must not call it drift
   var pu=HealthyProbe(o);pu.StatusFn=()=>(0,DriftStatus("RECOVERY_REQUIRED"));pu.OnStatus=()=>pu.JournalText="{\"Phase\":\"Restored\"}";
   var ru=Hc(new HealthMonitor(pu,o));
   Check(ru.Kind==HealthKind.Off&&!ru.CanFix,"health: 'Отключить' pressed during a check -> Off, nothing is offered or undone");
   var pu2=HealthyProbe(o);pu2.Prov=new ProviderProbe();var mu2=new HealthMonitor(pu2,o);Hc(mu2);
   pu2.OnStatus=()=>pu2.JournalText="{\"Phase\":\"Restored\"}";Check(Hc(mu2).Kind==HealthKind.Off,"health: a pending suspicion does not survive the user's disconnect");
   bool cancelled=false;using(var cts=new CancellationTokenSource()){cts.Cancel();try{Sync(()=>new HealthMonitor(HealthyProbe(o),o).CheckAsync(HealthTrigger.Manual,cts.Token));}catch(OperationCanceledException){cancelled=true;}}
   Check(cancelled,"health: a cancelled check throws OperationCanceledException");
   Check(!string.IsNullOrEmpty(CodeTable.Lookup(HealthMonitor.DriftRules).Action)&&new[]{HealthMonitor.Ok,HealthMonitor.DriftRules,HealthMonitor.ProviderDown,HealthMonitor.ResolverMismatch,HealthMonitor.NeedsManual,HealthMonitor.Unknown}.All(c=>CodeTable.Known(c)&&CodeTable.Lookup(c).Action.Length>0),"health: every health code has a message and one action in the table");
   Check(new[]{HealthMonitor.DriftRules,HealthMonitor.ProviderDown}.All(c=>CodeTable.Lookup(c).Kind==ActionKind.Fix&&CodeTable.Lookup(c).Action.Contains("Починить")),"health: the repairable codes carry the 'Починить' action");
   Check(CodeTable.Lookup(HealthMonitor.ResolverMismatch).Kind==ActionKind.Details&&CodeTable.Lookup(HealthMonitor.ResolverMismatch).Action.Contains("Диагностика")&&!CodeTable.Lookup(HealthMonitor.ResolverMismatch).Action.Contains("Починить"),"health: the resolver mismatch points to 'Диагностика', not to 'Починить' (the soft case has no repair)");

   // scheduler: debounce, no overlap, one check per minute, no retry loop on a hung CIM
   DateTime now=new DateTime(2026,10,12,12,0,0,DateTimeKind.Utc);
   // the tests of the full check run with the light check switched off, so they isolate the 30-60 minute logic (the light cadence has its own tests)
   var so=new HealthOptions{LightPeriod=TimeSpan.FromDays(365)};var sch=new HealthScheduler(()=>now,so);
   sch.Trigger(HealthTrigger.Startup);now=now.AddSeconds(4);
   Check(!sch.TryStart(out _),"health scheduler: the startup check waits for its short delay");
   now=now.AddSeconds(1);HealthTrigger got;
   Check(sch.TryStart(out got)&&got==HealthTrigger.Startup&&sch.Running,"health scheduler: the startup check starts after the delay");
   now=now.AddSeconds(30);Check(!sch.TryStart(out _),"health scheduler: no second check while one is running");
   sch.Finished(HealthKind.Healthy);
   DateTime stormStart=now.AddSeconds(40);now=stormStart;int starts=0;DateTime firstStorm=DateTime.MinValue;
   for(int i=0;i<10;i++){sch.Trigger(HealthTrigger.NetworkChange);now=now.AddSeconds(5);if(sch.TryStart(out _)){starts++;if(firstStorm==DateTime.MinValue)firstStorm=now;sch.Finished(HealthKind.Healthy);}}
   for(int i=0;i<30;i++){now=now.AddSeconds(5);if(sch.TryStart(out _)){starts++;if(firstStorm==DateTime.MinValue)firstStorm=now;sch.Finished(HealthKind.Healthy);}}
   Check(starts==1&&firstStorm>stormStart.AddSeconds(14),"health scheduler: a burst of network-change events (every 5 s for 45 s) produces exactly one check, after the quiet period");
   // at most one check per MinInterval, however many triggers arrive
   DateTime t0=new DateTime(2026,10,12,13,0,0,DateTimeKind.Utc);now=t0;var s2=new HealthScheduler(()=>now,so);var starts2=new List<DateTime>();
   for(int sec=0;sec<1800;sec+=2){now=t0.AddSeconds(sec);s2.Trigger(HealthTrigger.Manual);if(s2.TryStart(out _)){starts2.Add(now);s2.Finished(HealthKind.Healthy);}}
   bool spaced=true;for(int i=1;i<starts2.Count;i++)if((starts2[i]-starts2[i-1])<so.MinInterval)spaced=false;
   Check(starts2.Count>=2&&spaced,"health scheduler: with a trigger every 2 seconds for 30 minutes no two checks are closer than a minute");
   now=t0;var s8=new HealthScheduler(()=>now,so);var starts8=new List<DateTime>();
   for(int sec=0;sec<600;sec+=5){now=t0.AddSeconds(sec);s8.Trigger(HealthTrigger.NetworkChange);if(s8.TryStart(out _)){starts8.Add(now);s8.Finished(HealthKind.Healthy);}}
   bool spaced8=true;for(int i=1;i<starts8.Count;i++)if((starts8[i]-starts8[i-1])<so.MinInterval)spaced8=false;
   Check(starts8.Count>=2&&starts8.Count<=10&&spaced8&&(starts8[0]-t0)<=so.MaxDebounce+TimeSpan.FromSeconds(5),"health scheduler: a network that flaps for 10 minutes still gets checked (the debounce is capped) and never more than once a minute");
   // periodic
   now=t0;var s3=new HealthScheduler(()=>now,so);Check(!s3.TryStart(out _),"health scheduler: nothing to do at the start without a trigger");
   now=t0.AddMinutes(44);Check(!s3.TryStart(out _),"health scheduler: not before the period");
   now=t0.AddMinutes(45).AddSeconds(1);Check(s3.TryStart(out got)&&got==HealthTrigger.Periodic,"health scheduler: a periodic check after the period");
   s3.Finished(HealthKind.Healthy);now=now.AddMinutes(44);Check(!s3.TryStart(out _),"health scheduler: the period restarts after each check");
   // an overlapping trigger is remembered and honoured once, after the minimum interval
   now=t0;var s4=new HealthScheduler(()=>now,so);s4.Trigger(HealthTrigger.Manual);Check(s4.TryStart(out _),"health scheduler: manual trigger starts at once");
   now=t0.AddSeconds(10);s4.Trigger(HealthTrigger.NetworkChange);now=t0.AddSeconds(20);s4.Finished(HealthKind.Healthy);
   now=t0.AddSeconds(59);Check(!s4.TryStart(out _),"health scheduler: a trigger that arrived during a check waits for the minimum interval");
   now=t0.AddSeconds(61);Check(s4.TryStart(out got)&&got==HealthTrigger.NetworkChange,"health scheduler: ...and then runs once");
   s4.Finished(HealthKind.Healthy);now=now.AddMinutes(1);Check(!s4.TryStart(out _),"health scheduler: ...and not again");
   // a hung CIM: Unknown backs off 5, 10, 20, 40 minutes (cap = period); triggers piling up meanwhile do not create a loop
   now=t0;var s5=new HealthScheduler(()=>now,so);s5.Trigger(HealthTrigger.Manual);var hungStarts=new List<DateTime>();
   for(int sec=0;sec<3*3600;sec+=5){now=t0.AddSeconds(sec);if(sec%10==0)s5.Trigger(HealthTrigger.NetworkChange);if(s5.TryStart(out _)){hungStarts.Add(now);s5.Finished(HealthKind.Unknown);}}
   bool growing=true;for(int i=2;i<hungStarts.Count;i++)if((hungStarts[i]-hungStarts[i-1])<(hungStarts[i-1]-hungStarts[i-2]))growing=false;
   Check(hungStarts.Count>=4&&hungStarts.Count<=9&&growing&&(hungStarts[1]-hungStarts[0])>=so.BackoffStart,"health scheduler: a CIM that never answers gets at most "+hungStarts.Count+" checks in 3 hours, the gaps only grow (no retry loop)");
   now=t0;var s6=new HealthScheduler(()=>now,so);s6.Trigger(HealthTrigger.Manual);s6.TryStart(out _);now=t0.AddSeconds(5);s6.Trigger(HealthTrigger.Resume);now=t0.AddSeconds(15);s6.Finished(HealthKind.Unknown);
   now=t0.AddMinutes(5).AddSeconds(30);Check(!s6.TryStart(out _),"health scheduler: triggers that arrived during a failed check are dropped, not retried");
   // one confirmation re-check after a suspicion, then back to normal
   now=t0;var s7=new HealthScheduler(()=>now,so);s7.Trigger(HealthTrigger.Manual);s7.TryStart(out _);now=t0.AddSeconds(3);s7.Finished(HealthKind.Suspect,true);
   now=t0.AddSeconds(3+so.MinInterval.TotalSeconds-1);Check(!s7.TryStart(out _),"health scheduler: the confirmation re-check waits a minute");
   now=t0.AddSeconds(3+so.MinInterval.TotalSeconds+1);Check(s7.TryStart(out _),"health scheduler: the confirmation re-check runs");
   s7.Finished(HealthKind.Healthy);now=now.AddMinutes(10);Check(!s7.TryStart(out _),"health scheduler: and only once");

   // window: a fixable drift shows 'Починить'; the user's own disconnect removes it
   using(var form=new MainForm(true)) {
    form.Show();Application.DoEvents();
    form.JournalReader=()=>"{\"Phase\":\"Active\"}";
    Check(!form.FixVisible,"health window: no repair button while everything is fine");
    form.SmokeHealth(new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.ProviderDown,CanFix=true,Utc=DateTime.UtcNow});
    Check(form.FixVisible&&form.CurrentHealth!=null,"health window: a fixable drift shows 'Починить'");
    form.PresentForTest("{\"State\":\"Not configured\",\"Code\":\"ROLLED_BACK\",\"JournalPhase\":\"Restored\",\"Active\":false}");
    Check(!form.FixVisible&&form.CurrentHealth==null,"health window: after the user disconnected, no repair is offered");
    form.SmokeHealth(new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.ProviderDown,CanFix=true,Utc=DateTime.UtcNow});
    form.PresentForTest("{\"State\":\"DNS active\",\"Code\":\"DNS_CONFIGURED_APPLICATION_NOT_TESTED\",\"Active\":true,\"JournalPhase\":\"Active\",\"Servers\":[],\"Template\":\"x\"}");
    Check(form.FixVisible&&form.CurrentHealth!=null&&form.StatusText=="Мост требует исправления","health window: a routine active status keeps bridge repair visible while endpoint failure stays local");
    form.SmokeHealth(new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.NeedsManual,CanFix=false,Utc=DateTime.UtcNow});
    Check(!form.FixVisible,"health window: a drift that needs manual care does not offer 'Починить'");
    form.SmokeHealth(new HealthReport{Kind=HealthKind.Healthy,Code=HealthMonitor.Ok,Utc=DateTime.UtcNow});
    Check(form.CurrentHealth==null,"health window: a healthy report clears the drift");
    form.Close();
   }
   // review 3: a drift found while the page shows "connected" must not leave the tray/colours/state rows on "connected"
   using(var form=new MainForm(true)) {
    form.Show();Application.DoEvents();
    form.JournalReader=()=>"{\"Phase\":\"Active\"}";
    form.PresentForTest(HealthyStatus);
    Check(form.BridgeActive,"review: precondition, the page shows a connected bridge");
    form.ApplyHealth(new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.ProviderDown,CanFix=true,Utc=DateTime.UtcNow});
    Check(form.BridgeActive&&form.FixVisible,"review: endpoint failure preserves applied bridge configuration and offers repair");
    form.ApplyHealth(new HealthReport{Kind=HealthKind.Healthy,Code=HealthMonitor.Ok,Utc=DateTime.UtcNow});
    Check(form.CurrentHealth==null&&form.BridgeActive&&form.StatusText=="Мост подключён"&&!form.FixVisible,"review: a later healthy report repaints the page as connected instead of leaving the drift text");
    form.ApplyHealth(new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.ProviderDown,CanFix=true,Utc=DateTime.UtcNow});
    form.SettleForTest(HealthyStatus);
    Check(form.CurrentHealth==null&&form.BridgeActive&&form.StatusText=="Мост подключён","review: after a successful repair the page and tray say 'connected', not the stale drift");
    form.ApplyHealth(new HealthReport{Kind=HealthKind.Unknown,Code="HEALTH_UNKNOWN",Detail="WMI_NOT_RESPONDING",Utc=DateTime.UtcNow});
    Check(form.CollectReportInput().HealthKind=="Unknown","review: the report says the check failed (Unknown), not 'Healthy/Off'");
    Check(form.EvidenceUncertain&&form.StatusInkForTest!=Theme.Green,"doctor evidence: unknown health invalidates earlier green presentation without disconnecting policy");
    form.ApplyHealth(new HealthReport{Kind=HealthKind.Healthy,Code=HealthMonitor.Ok,Utc=DateTime.UtcNow.AddMinutes(-6)});
    Check(form.EvidenceUncertain,"doctor evidence: a stale healthy report cannot clear unknown evidence");
    form.ApplyHealth(new HealthReport{Kind=HealthKind.Healthy,Code=HealthMonitor.Ok,Utc=DateTime.UtcNow});
    Check(!form.EvidenceUncertain&&form.StatusInkForTest!=Theme.Green,"doctor evidence: fresh full health clears network uncertainty without implying application readiness");
    form.InvalidateNetworkEvidence();
    Check(form.EvidenceUncertain&&form.BridgeActive,"doctor evidence: network invalidation withdraws proof while keeping the configured bridge and restore action");
    form.ApplyHealth(new HealthReport{Kind=HealthKind.Healthy,Code=HealthMonitor.Ok,Light=true,Utc=DateTime.UtcNow});
    Check(form.EvidenceUncertain,"doctor evidence: provider-only light success cannot restore full readiness");
    var diagnosisContext=form.CaptureDiagnosticContext();
    form.InvalidateNetworkEvidence();
    const string diagnosticSuccess="{\"NrptEffective\":\"Policy + Windows resolver answers match\"}";
    form.ApplyDiagnosticEvidence(diagnosticSuccess,diagnosisContext);
    Check(form.EvidenceUncertain,"doctor evidence: delayed successful diagnosis cannot undo network invalidation");
    form.ApplyDiagnosticEvidence(diagnosticSuccess,form.CaptureDiagnosticContext());
    Check(!form.EvidenceUncertain,"doctor evidence: diagnosis from the current context can confirm fresh network evidence");
    var changedPolicy=form.CaptureDiagnosticContext();form.JournalReader=()=>"{\"Phase\":\"Restored\"}";
    form.ApplyDiagnosticEvidence(diagnosticSuccess,changedPolicy);
    Check(form.EvidenceUncertain,"doctor evidence: diagnosis from an old policy cannot confirm the replacement context");
    var mutationContext=form.CaptureDiagnosticContext();form.InvalidateNetworkEvidence();
    form.SettleForTest(HealthyStatus,mutationContext.Revision,"synthetic-network");
    Check(form.EvidenceUncertain,"doctor evidence: delayed mutation completion cannot restore evidence invalidated by a network change");

    form.Close();
   }
  }

  // 1.5-2: support report
  static RedactionContext Ctx()=>new RedactionContext{UserName="ivan",MachineName="DESKTOP-IVAN01",DomainName="DESKTOP-IVAN01",ProfileDir=@"C:\Users\ivan",ExtraPaths=new[]{@"D:\Docs\ivan-docs"},Ssids=new[]{"HomeWiFi-5G","Café 5G"}};
  // Quick provider check every 10 minutes: no engine, no CIM; Suspect and Drift unchanged; the full check stays at 45 minutes
  static void RunLightHealthTests() {
   var o=new HealthOptions{Targets=new[]{"a.example.test","b.example.test"}};
   const string ActiveJournal="{\"Schema\":1,\"Phase\":\"Active\",\"Servers\":[\"212.109.195.91\",\"83.220.169.155\",\"10.0.0.7\"],\"Template\":\"https://x.example.test/dns-query\"}";
   FakeProbe Light(){var p=HealthyProbe(o);p.JournalText=ActiveJournal;p.StatusFn=()=>throw new InvalidOperationException("the light check must never ask the engine");return p;}
   HealthReport Lc(HealthMonitor m)=>Sync(()=>m.CheckAsync(HealthTrigger.Light,default));
   var p1=Light();var m1=new HealthMonitor(p1,o);var r1=Lc(m1);
   Check(r1.Kind==HealthKind.Healthy&&r1.Light&&r1.Trigger==HealthTrigger.Light&&p1.StatusCalls==0&&p1.SystemCalls==0&&p1.ProviderCalls==1,"light health: a working provider -> Healthy, with no engine status (no CIM) and no resolver comparison, one provider probe");
   Check(p1.LastServers.SequenceEqual(new[]{"212.109.195.91","83.220.169.155"})&&p1.LastTemplate=="https://x.example.test/dns-query","light health: the addresses and template come from the journal; a private address in it is never contacted");
   Check(new HealthOptions().LightPeriod==TimeSpan.FromMinutes(10)&&new HealthOptions().Period==TimeSpan.FromMinutes(45),"light health: the light period is 10 minutes, the full period stays 45");
   foreach(string j in new[]{null,"","{ not json","{\"Phase\":\"Restored\"}"}) {
    var pj=Light();pj.JournalText=j;var rj=Lc(new HealthMonitor(pj,o));
    Check(rj.Kind==HealthKind.Off&&rj.Light&&pj.ProviderCalls==0&&pj.StatusCalls==0,"light health: no journal or a switched-off bridge -> Off with no network at all ("+(j??"null")+")");
   }
   var prec=Light();prec.JournalText="{\"Phase\":\"Restoring\",\"Servers\":[\"212.109.195.91\"],\"Template\":\"https://x.example.test/dns-query\"}";var rrec=Lc(new HealthMonitor(prec,o));
   Check(rrec.Kind==HealthKind.Drift&&rrec.Code=="RECOVERY_REQUIRED"&&prec.ProviderCalls==0&&prec.StatusCalls==0,"light health: an unfinished operation is RECOVERY_REQUIRED without touching the network or the engine");
   var pnoaddr=Light();pnoaddr.JournalText="{\"Phase\":\"Active\",\"Servers\":[\"10.0.0.7\"],\"Template\":\"https://x.example.test/dns-query\"}";
   var rna=Lc(new HealthMonitor(pnoaddr,o));
   Check(rna.Kind==HealthKind.Unknown&&pnoaddr.ProviderCalls==0,"light health: a journal without a usable public address cannot be checked (Unknown), nothing is probed");
   // Suspect -> Drift: one failure is never reported, the repeat is
   var pf=Light();pf.Prov=new ProviderProbe();var mf=new HealthMonitor(pf,o);
   var f1=Lc(mf);var f2=Lc(mf);
   Check(f1.Kind==HealthKind.Suspect&&f1.Code==HealthMonitor.ProviderDown&&f1.Light&&!f1.CanFix&&f2.Kind==HealthKind.Drift&&f2.Code==HealthMonitor.ProviderDown&&f2.CanFix&&f2.Light,"light health: a provider that does not answer is Suspect first and Drift (with 'Починить') only on the repeat");
   pf.Prov=new ProviderProbe{Validated=new[]{"212.109.195.91"}};
   Check(Lc(mf).Kind==HealthKind.Healthy,"light health: the provider answering again is Healthy");
   pf.Prov=new ProviderProbe();Check(Lc(mf).Kind==HealthKind.Suspect,"light health: after a healthy check a new failure starts the confirmation from the beginning");
   var px=Light();px.ProviderThrows=true;Check(Lc(new HealthMonitor(px,o)).Kind==HealthKind.Suspect,"light health: a failing probe is a soft failure, not a crash and not an instant drift");
   // the two checks share one suspicion for the provider, but a light success does not wipe a pending resolver suspicion of the full check
   var pm=HealthyProbe(o);pm.JournalText=ActiveJournal;foreach(string t in o.Targets)pm.Sys[t]=new[]{"192.0.2.55"};var mm=new HealthMonitor(pm,o);
   var full1=Sync(()=>mm.CheckAsync(HealthTrigger.Periodic,default));var lightOk=Lc(mm);var full2=Sync(()=>mm.CheckAsync(HealthTrigger.Periodic,default));
   Check(full1.Kind==HealthKind.Suspect&&full1.Code==HealthMonitor.ResolverMismatch&&lightOk.Kind==HealthKind.Healthy&&full2.Kind==HealthKind.Drift&&full2.Code==HealthMonitor.ResolverMismatch,"light health: a healthy light check does not erase the full check's pending resolver suspicion");
   var pOff=Light();pOff.Prov=new ProviderProbe();var mOff=new HealthMonitor(new SwitchingProbe(pOff),o);
   Check(Lc(mOff).Kind==HealthKind.Off,"light health: 'Отключить' pressed during the check -> Off, never a drift");
   bool cancelled=false;using(var cts=new CancellationTokenSource()){cts.Cancel();try{Sync(()=>new HealthMonitor(Light(),o).CheckAsync(HealthTrigger.Light,cts.Token));}catch(OperationCanceledException){cancelled=true;}}
   Check(cancelled,"light health: a cancelled check throws OperationCanceledException");

   // scheduler: a light check every 10 minutes, the full check still every 45, the full one wins when both are due
   DateTime t0=new DateTime(2026,10,12,9,0,0,DateTimeKind.Utc);DateTime now=t0;var sch=new HealthScheduler(()=>now,new HealthOptions());
   var seq=new List<string>();
   for(int s=0;s<=90*60;s+=30){now=t0.AddSeconds(s);if(sch.TryStart(out var tr)){seq.Add(((int)(s/60))+":"+tr);sch.Finished(HealthKind.Healthy);}}
   Check(string.Join(",",seq)=="10:Light,20:Light,30:Light,40:Light,45:Periodic,55:Light,65:Light,75:Light,85:Light,90:Periodic","health scheduler: a light check every 10 minutes, the full check at 45 and 90 minutes, and each full check restarts the light cadence ("+string.Join(",",seq)+")");
   now=t0;var s2=new HealthScheduler(()=>now,new HealthOptions());now=t0.AddHours(2);
   HealthTrigger first;Check(s2.TryStart(out first)&&first==HealthTrigger.Periodic,"health scheduler: when the light and the full check are both due, the full one runs");
   s2.Finished(HealthKind.Healthy);Check(!s2.TryStart(out _),"health scheduler: ...and the full check counts as the light one too");
   now=t0;var s3=new HealthScheduler(()=>now,new HealthOptions());now=t0.AddMinutes(10);Check(s3.TryStart(out first)&&first==HealthTrigger.Light,"health scheduler: the first light check comes 10 minutes after the start");
   now=t0.AddMinutes(10).AddSeconds(30);Check(!s3.TryStart(out _),"health scheduler: no second check while the light one runs");
   // every check takes two minutes: a light check must not push the full one back, and no check starts while another runs
   now=t0;var s6=new HealthScheduler(()=>now,new HealthOptions());DateTime? finishAt=null;var slow=new List<string>();bool overlap=false;
   for(int s=0;s<=50*60;s+=30){now=t0.AddSeconds(s);
    if(finishAt!=null&&now>=finishAt){s6.Finished(HealthKind.Healthy);finishAt=null;}
    if(s6.TryStart(out var tr6)){if(finishAt!=null)overlap=true;slow.Add(((int)(s/60))+":"+tr6);finishAt=now.AddMinutes(2);}}
   Check(!overlap&&slow.Contains("45:Periodic")&&slow.Count(x=>x.EndsWith("Periodic"))==1&&slow.Where(x=>x.EndsWith("Light")).Count()>=3,"health scheduler: with checks that take two minutes the full check still starts at 45 minutes (a light check does not postpone it) and nothing overlaps ("+string.Join(",",slow)+")");
   // a suspicion found by a light check is confirmed by a light check
   now=t0;var s4=new HealthScheduler(()=>now,new HealthOptions());now=t0.AddMinutes(10);s4.TryStart(out _);now=t0.AddMinutes(10).AddSeconds(3);s4.Finished(HealthKind.Suspect,true);
   now=t0.AddMinutes(10).AddSeconds(3+59);Check(!s4.TryStart(out _),"health scheduler: the confirmation of a light suspicion waits a minute");
   now=t0.AddMinutes(10).AddSeconds(3+61);Check(s4.TryStart(out first)&&first==HealthTrigger.Light,"health scheduler: ...and is a light check again");
   // the WMI backoff is not wiped by a light check (it never touches CIM): full Unknown, light Healthy, full Unknown again: the second pause is 10 minutes, not 5
   now=t0;var s7=new HealthScheduler(()=>now,new HealthOptions());s7.Trigger(HealthTrigger.Manual);s7.TryStart(out _);s7.Finished(HealthKind.Unknown);
   now=t0.AddMinutes(10);s7.TryStart(out first);bool wasLightRun=first==HealthTrigger.Light;s7.Finished(HealthKind.Healthy);
   now=t0.AddMinutes(12);s7.Trigger(HealthTrigger.Manual);s7.TryStart(out _);s7.Finished(HealthKind.Unknown);
   Check(wasLightRun&&s7.NotBefore==t0.AddMinutes(22),"health scheduler: a healthy light check does not reset the backoff of a CIM that does not answer (second pause 10 minutes)");
   // a light check that could not finish backs off like any other
   now=t0;var s5=new HealthScheduler(()=>now,new HealthOptions());now=t0.AddMinutes(10);s5.TryStart(out _);s5.Finished(HealthKind.Unknown);
   now=t0.AddMinutes(14);var early=s5.TryStart(out _);now=t0.AddMinutes(20).AddSeconds(5);Check(!early&&s5.TryStart(out first)&&first==HealthTrigger.Light,"health scheduler: an unfinished light check is not retried at once (backoff), the next one comes after the pause");

   // window: a light report touches only what it can verify
   using(var form=new MainForm(true)) {
    form.Show();Application.DoEvents();
    form.SmokeHealth(new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.ProviderDown,CanFix=true,Utc=DateTime.UtcNow});
    Check(form.FixVisible&&form.CurrentHealth!=null,"light window: a confirmed provider failure shows 'Починить'");
    form.ApplyHealth(new HealthReport{Kind=HealthKind.Healthy,Code=HealthMonitor.Ok,Light=true,Utc=DateTime.UtcNow});
    Check(form.CurrentHealth==null&&!form.FixVisible,"light window: a healthy light report clears the provider failure");
    form.SmokeHealth(new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.DriftRules,CanFix=true,Utc=DateTime.UtcNow});
    form.ApplyHealth(new HealthReport{Kind=HealthKind.Healthy,Code=HealthMonitor.Ok,Light=true,Utc=DateTime.UtcNow});
    Check(form.CurrentHealth!=null&&form.CurrentHealth.Code==HealthMonitor.DriftRules&&form.FixVisible,"light window: a healthy light report never clears a rules drift it cannot see");
    form.ApplyHealth(new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.ProviderDown,CanFix=true,Light=true,Utc=DateTime.UtcNow});
    Check(form.CurrentHealth.Code==HealthMonitor.DriftRules,"light window: a light provider failure does not replace the rules drift already shown");
    form.SmokeHealth(new HealthReport{Kind=HealthKind.Healthy,Code=HealthMonitor.Ok,Utc=DateTime.UtcNow});
    form.ApplyHealth(new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.ProviderDown,CanFix=true,Light=true,Utc=DateTime.UtcNow});
    Check(form.CurrentHealth!=null&&form.CurrentHealth.Code==HealthMonitor.ProviderDown&&form.FixVisible,"light window: a confirmed light failure on a quiet page offers 'Починить'");
    form.Close();
   }
  }
  // Probe that turns the bridge off while the provider is being asked (the user pressed "Отключить" during the check).
  sealed class SwitchingProbe:IHealthProbe {
   readonly FakeProbe inner;public SwitchingProbe(FakeProbe p){inner=p;}
   public string Journal()=>inner.Journal();
   public System.Threading.Tasks.Task<(int Code,string Output)> Status(TimeSpan d,CancellationToken ct)=>inner.Status(d,ct);
   public System.Threading.Tasks.Task<ProviderProbe> Provider(string[] s,string t,CancellationToken ct){inner.JournalText="{\"Phase\":\"Restored\"}";return inner.Provider(s,t,ct);}
   public System.Threading.Tasks.Task<string[]> SystemAnswers(string h,CancellationToken ct)=>inner.SystemAnswers(h,ct);
  }

  static void RunReportTests() {
   var c=Ctx();string S(string t)=>SupportReport.Sanitize(t,c);
   Check(S(@"C:\Users\ivan\AppData\Local\AntigravityDnsUnlocker\x.json")==@"<profile>\AppData\Local\AntigravityDnsUnlocker\x.json","report: the profile folder is cut, the file name stays");
   Check(S(@"c:\users\IVAN\Desktop\a.txt")==@"<profile>\Desktop\a.txt"&&S("C:/Users/ivan/Desktop/a.txt")=="<profile>/Desktop/a.txt"&&S(@"C:\\Users\\ivan\\a.txt")==@"<profile>\\a.txt","report: upper/lower case, forward slashes and JSON-escaped backslashes are all cut");
   Check(S(@"C:\Users\ivanov\a.txt")==@"C:\Users\<user>\a.txt"&&S("ivanov wrote to ivan")=="ivanov wrote to <user>"&&S("DESKTOP-IVAN012 and DESKTOP-IVAN01")=="DESKTOP-IVAN012 and <computer>","report: a longer name is not cut inside (profile, user and computer names are whole tokens); another account folder is still hidden");
   Check(S("HomeWiFi-5G, HomeWiFi-5GHz, homewifi-5g")=="<ssid>, HomeWiFi-5GHz, <ssid>"&&S("Café 5G / CAFÉ 5G")=="<ssid> / <ssid>","report: network names are cut as whole tokens, case-insensitively, with accents");
   Check(S(@"D:\Docs\ivan-docs\x.txt")==@"<personal-path>\x.txt","report: other personal folders are cut");
   Check(S(@"C:\Users\Public\x C:\Users\PublicSomeone\x")==@"C:\Users\Public\x C:\Users\<user>\x","report: shared folders stay, a similar-looking account folder does not");
   var cu=new RedactionContext{UserName="Иван",MachineName="ПК-ИВАН",ProfileDir=@"C:\Users\Иван",Ssids=new[]{"Дом 5G"}};
   Check(SupportReport.Sanitize("Привет, иван! C:\\Users\\Иван\\Desktop\\о.txt ИВАНОВ ПК-иван дом 5g",cu)=="Привет, <user>! <profile>\\Desktop\\о.txt ИВАНОВ <computer> <ssid>","report: Cyrillic names, folders and network names are cut regardless of case; a longer word is left alone");
   Check(SupportReport.Sanitize("table ab abc",new RedactionContext{UserName="ab"})=="table <user> abc"&&SupportReport.Sanitize("a b",new RedactionContext{UserName="a"})=="a b","report: a name is never cut out of another word; a one-character name is ignored");
   foreach(var ctx in new[]{c,cu,new RedactionContext{UserName="user",MachineName="profile",ProfileDir=@"C:\Users\user",Ssids=new[]{"ssid","computer"}}}) {
    string sample=@"C:\Users\ivan\x user profile computer ssid <user> <profile> <computer> <ssid> <personal-path> ivan DESKTOP-IVAN01 HomeWiFi-5G Иван ПК-ИВАН Дом 5G C:\Users\user\a C:\Users\<user>\b "+"C:/Users/ivan/q C:\\\\Users\\\\ivan";
    string once=SupportReport.Sanitize(sample,ctx),twice=SupportReport.Sanitize(once,ctx);
    Check(once==twice,"report: redaction is idempotent ("+ctx.UserName+")");
   }
   var odd=new RedactionContext{UserName="user",MachineName="profile",ProfileDir=@"C:\Users\user",Ssids=new[]{"ssid","computer"}};
   Check(SupportReport.Sanitize(@"C:\Users\user\a",odd)==@"<profile>\a"&&SupportReport.Sanitize("profile and user and ssid",odd)=="<computer> and <user> and <ssid>"&&SupportReport.Sanitize(@"C:\Users\user\a profile",odd)==@"<profile>\a <computer>","report: a placeholder inserted by the pass is never cut again, even when a name equals a placeholder word");
   Check(SupportReport.Sanitize("<user> stays",new RedactionContext{UserName="user"})=="<user> stays"&&SupportReport.Sanitize("",c)==""&&SupportReport.Sanitize(null,c)==""&&SupportReport.Sanitize("ivan",null)=="ivan","report: placeholders already in the text survive; empty input and a missing context are harmless");
   Check(SupportReport.ParseSsids("    Name                   : Wi-Fi\n    SSID                   : My Net 5G\n    BSSID                  : aa:bb:cc:dd:ee:ff\n    SSID                   : My Net 5G\n")[0]=="My Net 5G"&&SupportReport.ParseSsids("    BSSID : aa").Length==0&&SupportReport.ParseSsids(null).Length==0,"report: the SSID is read from 'netsh wlan show interfaces' (BSSID is not an SSID)");
   // content
   var ops=Enumerable.Range(1,30).Select(i=>new OperationEntry{Utc="2026-10-12T10:"+i.ToString("00")+":00Z",Action=i%2==0?"enable":"restore",Code="CODE_"+i,Ok=i%3!=0,Profile="dns.malw.link"}).ToArray();
   var input=new ReportInput{GeneratedUtc=new DateTime(2026,10,12,12,0,0,DateTimeKind.Utc),ExeVersion="1.3.0",CatalogVersion="2026.10.12.1",CatalogUpdated="2026-10-12",Os="Microsoft Windows NT 10.0.26100.0 x64",JournalPhase="Active",Profile="dns.malw.link",HealthKind="Drift",HealthCode="HEALTH_PROVIDER_DOWN",HealthUtc="2026-10-12 11:50",Operations=ops,DiagnosisJson=null,DiagnosisCode="NOT_RUN"};
   string text=SupportReport.Build(input);
   Check(text.Contains("1.3.0")&&text.Contains("2026.10.12.1")&&text.Contains("HEALTH_PROVIDER_DOWN")&&text.Contains("CODE_30")&&text.Contains("CODE_11")&&!text.Contains("CODE_10 ")&&!text.Contains("CODE_1 "),"report: EXE and catalog version, health result and only the last 20 operations with their codes");
   string diag="{\"State\":\"DNS active\",\"Code\":\"DNS_CONFIGURED_APPLICATION_NOT_TESTED\",\"Status\":{\"Resolver\":\"custom-host.example.test\",\"Protocol\":\"DoH\",\"JournalPhase\":\"Active\",\"Servers\":[\"203.0.113.9\"]},\"NrptEffective\":\"Policy + Windows resolver answers match (wire destination unverified)\",\"SystemResolver\":[{\"Host\":\"a.example.test\",\"A\":[\"198.51.100.1\"]}],\"Network\":{\"Candidates\":[{\"Server\":\"203.0.113.9\",\"DohValidated\":true,\"DohTls\":\"TLS_OK_HTTP_404\"}]},\"VpnPresent\":[]}";
   input.DiagnosisJson=diag;string withDiag=SupportReport.Build(input);
   Check(withDiag.Contains("203.0.113.9")&&withDiag.Contains("198.51.100.1")&&withDiag.Contains("Policy + Windows resolver answers match")&&withDiag.Contains("TLS_OK_HTTP_404"),"report: the diagnosis result is summarised (state, addresses, resolver answers, provider checks)");
   // review 3: a custom DoH host in the journal must not reach the report even if the combo currently shows a catalog profile
   input.DiagnosisJson=diag.Replace("custom-host.example.test","https://ivanov-home.example.test/dns-query");string leak=SupportReport.Build(input);
   Check(!leak.Contains("ivanov-home")&&leak.Contains("свой профиль"),"report: a resolver that is not a catalog profile id is reported as a custom profile, whatever the selection");
   input.DiagnosisJson=diag;
   input.CustomProfile=true;string customText=SupportReport.Build(input);input.CustomProfile=false;
   Check(customText.Contains("свой профиль")&&!customText.Contains("203.0.113.9")&&!customText.Contains("198.51.100.1")&&!customText.Contains("custom-host.example.test")&&withDiag.Contains("custom-host.example.test"),"report: a custom profile's name and every provider address are kept out of the diagnosis summary");
   input.DiagnosisJson="{ broken";Check(SupportReport.Build(input).Contains("не удалось разобрать"),"report: an unreadable diagnosis is said so, not dropped silently");
   // end to end: personal data in every field never reaches the text the user sees
   var dirty=new ReportInput{GeneratedUtc=DateTime.UtcNow,ExeVersion="1.3.0",CatalogVersion="v",Os="Windows on DESKTOP-IVAN01",JournalPhase="Active",Profile="HomeWiFi-5G",HealthKind="Drift",HealthCode="X",
    Operations=new[]{new OperationEntry{Utc="u",Action="enable",Code="ERR at C:\\Users\\ivan\\Desktop\\a.txt",Ok=false,Profile="ivan"}},
    DiagnosisJson="{\"State\":\"s\",\"Code\":\"c\",\"Status\":{\"Resolver\":\"ivan on DESKTOP-IVAN01\",\"Protocol\":\"C:\\\\Users\\\\ivan\\\\x\",\"JournalPhase\":\"Active\",\"Servers\":[]},\"NrptEffective\":\"HomeWiFi-5G Café 5G\"}"};
   string final=SupportReport.Prepare(dirty,c);
   Check(!final.Contains("ivan",StringComparison.OrdinalIgnoreCase)&&!final.Contains("DESKTOP-IVAN01",StringComparison.OrdinalIgnoreCase)&&!final.Contains("HomeWiFi",StringComparison.OrdinalIgnoreCase)&&!final.Contains("Café")&&final.Contains("<user>")&&SupportReport.Sanitize(final,c)==final,"report: the prepared text contains no user, computer, folder or network name, and preparing it again changes nothing");
   // operation history: bounded, local, never throws
   string hist=Path.Combine(Path.GetTempPath(),"agdns-hist-"+Guid.NewGuid().ToString("N")+".json");
   try {
    var h=new OperationHistory(hist);for(int i=1;i<=60;i++)h.Append("enable","C"+i,true,"p",new DateTime(2026,10,12,0,0,0,DateTimeKind.Utc).AddMinutes(i));
    var loaded=h.Load();
    Check(loaded.Length==OperationHistory.MaxEntries&&loaded.Last().Code=="C60"&&loaded.First().Code=="C11","report: the operation history keeps the last 50 entries");
    File.WriteAllText(hist,"{ corrupt");Check(h.Load().Length==0,"report: a corrupt history file reads as empty");
    h.Append("restore","OK",true,"p");Check(h.Load().Length==1,"report: ...and the next entry starts a clean one");
    new OperationHistory(Path.Combine(hist,"no","such","dir"+new string('x',300))).Append("a","b",true,"c");Check(true,"report: an unwritable history location never throws");
   }finally{try{File.Delete(hist);}catch{}}
   Check(!typeof(SupportReport).GetMethods().Any(m=>m.Name.IndexOf("Send",StringComparison.OrdinalIgnoreCase)>=0||m.Name.IndexOf("Upload",StringComparison.OrdinalIgnoreCase)>=0),"report: there is no sending code path");
   using(var form=new MainForm(true)){
    var ri=form.CollectReportInput();
    Check(ri.ExeVersion==AppInfo.Version&&ri.CatalogVersion==ProfileCatalog.BuiltIn.Version&&!string.IsNullOrEmpty(ri.Os),"report: the window fills the report from the real EXE and catalog versions");
   }
  }

  // addresses without manual upkeep: bootstrap at connect time
  static T Sync<T>(Func<System.Threading.Tasks.Task<T>> f)=>System.Threading.Tasks.Task.Run(f).GetAwaiter().GetResult();
  sealed class FakeBootstrapNet:IBootstrapNet {
   public string[] SysIps=new string[0];public CrossResult[] Cross=new CrossResult[0];public HashSet<string> TlsOk=new HashSet<string>();public bool CrossThrows,SysThrows,CrossHang,DohHang;public string[] DohAsked=new string[0];public HashSet<string> DohOk=new HashSet<string>();
   // "Hang" = a task that never completes and ignores the token, the worst case for a time limit.
   public System.Threading.Tasks.Task<string[]> SystemResolve(string host,CancellationToken ct){if(SysThrows)throw new System.Net.Sockets.SocketException(11001);return System.Threading.Tasks.Task.FromResult(SysIps);}
   public System.Threading.Tasks.Task<CrossResult[]> CrossCheck(string host,CancellationToken ct){if(CrossThrows)throw new InvalidOperationException("offline");if(CrossHang)return new System.Threading.Tasks.TaskCompletionSource<CrossResult[]>().Task;return System.Threading.Tasks.Task.FromResult(Cross);}
   public System.Threading.Tasks.Task<TlsInfo> TlsOnName(string ip,int port,string host,CancellationToken ct)=>System.Threading.Tasks.Task.FromResult(TlsOk.Contains(ip)?new TlsInfo{Ok=true,PolicyErrors="None"}:new TlsInfo{Ok=false,Error="AuthenticationException: certificate name mismatch",PolicyErrors="RemoteCertificateNameMismatch"});
   public System.Threading.Tasks.Task<Candidate[]> DohCandidates(string[] ips,string template,CancellationToken ct){DohAsked=ips;if(DohHang)return new System.Threading.Tasks.TaskCompletionSource<Candidate[]>().Task;return System.Threading.Tasks.Task.FromResult(ips.Select(i=>new Candidate{Server=i,DohValidated=DohOk.Contains(i),DohTls="TLS_OK_HTTP_404"}).ToArray());}
  }
  static void RunConnectTests() {
   const string tmpl="https://dns.example.test/dns-query";
   var pins=new[]{"193.233.112.67","193.233.112.68"};var found3=new[]{"83.220.169.155","195.133.25.16","212.109.195.93"};
   var stamp=new DateTime(2026,10,12,12,30,0,DateTimeKind.Utc);int bootCalls=0;
   Func<string[],string,System.Threading.Tasks.Task<string[]>> allDead=(s,t)=>System.Threading.Tasks.Task.FromResult(new string[0]);
   Func<string[],string,System.Threading.Tasks.Task<string[]>> allAlive=(s,t)=>System.Threading.Tasks.Task.FromResult(s);
   Func<string,System.Threading.Tasks.Task<string[]>> boot=t=>{bootCalls++;return System.Threading.Tasks.Task.FromResult(found3);};
   var p1=Sync(()=>AddressResolver.ResolveAsync(tmpl,pins,allDead,boot,()=>stamp));
   Check(p1.Error==null&&p1.Source=="found"&&p1.Servers.SequenceEqual(found3)&&p1.FoundUtc==stamp&&bootCalls==1,"connect: every pin dead, provider name alive -> addresses are found by bootstrap and used (no new exe)");
   bootCalls=0;var p2=Sync(()=>AddressResolver.ResolveAsync(tmpl,pins,allAlive,boot,()=>stamp));
   Check(p2.Source=="pinned"&&p2.Servers.SequenceEqual(pins)&&bootCalls==0,"connect: when a pin passes preflight the pins are used and the network is not bootstrapped");
   bootCalls=0;var p3=Sync(()=>AddressResolver.ResolveAsync(tmpl,new string[0],allAlive,boot,()=>stamp));
   Check(p3.Source=="found"&&bootCalls==1,"connect: a profile without pins goes straight to bootstrap");
   var p4=Sync(()=>AddressResolver.ResolveAsync(tmpl,pins,allDead,t=>System.Threading.Tasks.Task.FromResult(Enumerable.Range(1,7).Select(i=>"212.109.195."+(90+i)).ToArray()),()=>stamp));
   Check(p4.Servers.Length==AddressResolver.MaxFound&&AddressResolver.MaxFound==4,"connect: at most four found addresses are used");
   var p5=Sync(()=>AddressResolver.ResolveAsync(tmpl,pins,(s,t)=>throw new System.Net.Http.HttpRequestException("no network"),t=>throw new System.Net.Http.HttpRequestException("no network"),()=>stamp));
   Check(p5.Error=="BOOTSTRAP_FAILED"&&p5.Servers.Length==0,"connect: without internet nothing is chosen, the caller starts no mutation and an existing bridge is untouched");
   var p6=Sync(()=>AddressResolver.ResolveAsync(tmpl,new string[0],allAlive,t=>System.Threading.Tasks.Task.FromResult(new[]{"10.0.0.5","192.168.1.1","127.0.0.1"}),()=>stamp));
   Check(p6.Error=="BOOTSTRAP_NO_ADDRESS"&&p6.Servers.Length==0,"connect: private or loopback answers are never accepted as provider addresses");
   var p7=Sync(()=>AddressResolver.ResolveAsync(tmpl,pins,allDead,t=>System.Threading.Tasks.Task.FromResult(new string[0]),()=>stamp));
   Check(p7.Error=="BOOTSTRAP_NO_ADDRESS","connect: dead pins and a provider name that yields nothing give a clear error");
   Check(AddressResolver.SourceText("pinned",stamp)=="закреплённые"&&AddressResolver.SourceText("found",stamp).StartsWith("найдены сейчас, ")&&AddressResolver.SourceText("found",stamp).EndsWith(stamp.ToLocalTime().ToString("dd.MM HH:mm")),"connect: the address source is shown as pinned or found-now with a timestamp");
   // bootstrap trust: a resolver-supplied address without a valid certificate for the provider NAME is never used
   var net=new FakeBootstrapNet{SysIps=new[]{"195.133.25.16","45.9.9.9"},Cross=new[]{new CrossResult{Via="x",Server="1.1.1.1",Code="DNS_OK",Addresses=new[]{"195.133.25.16","83.220.169.155"}}}};
   net.TlsOk.Add("195.133.25.16");net.TlsOk.Add("83.220.169.155");net.DohOk.Add("195.133.25.16");net.DohOk.Add("83.220.169.155");
   var pinned=Sync(()=>Bootstrap.PinnedAsync(tmpl,default,net));
   Check(pinned.OrderBy(x=>x).SequenceEqual(new[]{"195.133.25.16","83.220.169.155"})&&!net.DohAsked.Contains("45.9.9.9"),"bootstrap: an address without a valid certificate for the provider name is dropped before DoH is even asked");
   var spoof=new FakeBootstrapNet{SysIps=new[]{"195.133.25.16"}};spoof.DohOk.Add("195.133.25.16");
   Check(Sync(()=>Bootstrap.PinnedAsync(tmpl,default,spoof)).Length==0,"bootstrap: if no address passes TLS on the name nothing is pinned, even when the DoH side would answer");
   var halfOffline=new FakeBootstrapNet{SysIps=new[]{"195.133.25.16"},CrossThrows=true};halfOffline.TlsOk.Add("195.133.25.16");halfOffline.DohOk.Add("195.133.25.16");
   Check(Sync(()=>Bootstrap.PinnedAsync(tmpl,default,halfOffline)).SequenceEqual(new[]{"195.133.25.16"}),"bootstrap: a failing cross-check does not break it (system resolver result is still validated by TLS on the name)");
   // custom profile: the DoH URL is enough
   Check(MainForm.TryParseCustom("","","https://dns.example.test/dns-query",out var cs,out var ct,out _,out var cn)&&cs.Length==0&&ct=="https://dns.example.test/dns-query"&&cn=="dns.example.test","custom profile: only a DoH URL is needed, the name defaults to the host and IPs are optional");
   Check(MainForm.TryParseCustom("mine","185.185.185.185","https://dns.example.test/dns-query",out var cs2,out _,out _,out var cn2)&&cs2.Length==1&&cn2=="mine","custom profile: optional IPs are still accepted");
   Check(!MainForm.TryParseCustom("","10.0.0.1","https://dns.example.test/dns-query",out _,out _,out _,out _)&&!MainForm.TryParseCustom("","","https://1.2.3.4/dns-query",out _,out _,out _,out _)&&!MainForm.TryParseCustom("","","http://dns.example.test/dns-query",out _,out _,out _,out _),"custom profile: private IPs, IP-literal hosts and plain http are rejected");
  }

  // resilient bootstrap: more resolvers, parallel, one budget
  static void RunResilientBootstrapTests() {
   const string tmpl="https://dns.example.test/dns-query";
   var fast=new BootstrapOptions{Budget=TimeSpan.FromMilliseconds(900),Discovery=TimeSpan.FromMilliseconds(300)};
   CrossResult Silent(string via,string server)=>new CrossResult{Via=via,Server=server,Code="DNS_TIMEOUT_OR_UNREACHABLE",Addresses=new string[0]};
   // the system resolver and the three foreign resolvers are unreachable; only another provider of the catalog answers -> the address is found
   var cat=new FakeBootstrapNet{SysThrows=true,Cross=new[]{Silent("https://cloudflare-dns.com/dns-query","1.1.1.1"),Silent("https://dns.google/dns-query","8.8.8.8"),Silent("https://dns.quad9.net/dns-query","9.9.9.9"),
    new CrossResult{Via="https://dns.comss.one/dns-query",Server="83.220.169.155",Code="DNS_OK",Addresses=new[]{"212.109.195.91"}}}};
   cat.TlsOk.Add("212.109.195.91");cat.DohOk.Add("212.109.195.91");
   Check(Sync(()=>Bootstrap.PinnedAsync(tmpl,default,cat)).SequenceEqual(new[]{"212.109.195.91"}),"bootstrap: system resolver and the three foreign resolvers down, another catalog provider answered -> the address is found and validated");
   // a resolver (any) that hands out an address without a valid certificate for the NAME is not believed
   var liar=new FakeBootstrapNet{SysThrows=true,Cross=new[]{new CrossResult{Via="https://dns.comss.one/dns-query",Server="83.220.169.155",Code="DNS_OK",Addresses=new[]{"212.109.195.77"}}}};
   liar.DohOk.Add("212.109.195.77");
   Check(Sync(()=>Bootstrap.PinnedAsync(tmpl,default,liar)).Length==0&&liar.DohAsked.Length==0,"bootstrap: an address from a catalog provider resolver without valid TLS for the name is not taken (DoH is not even asked)");
   // budget: the check of the names hangs and ignores its token : the search ends at the budget with a timeout, not "no address passed"
   var hang=new FakeBootstrapNet{SysIps=new[]{"212.109.195.91"},DohHang=true};hang.TlsOk.Add("212.109.195.91");
   var sw=System.Diagnostics.Stopwatch.StartNew();
   Exception caught=null;try{Sync(()=>Bootstrap.PinnedAsync(tmpl,default,hang,fast));}catch(BootstrapTimeoutException e){caught=e;}
   Check(caught!=null&&sw.ElapsedMilliseconds<4000,"bootstrap: a step that never answers ends the search at the budget with a timeout, not a hang");
   var viaResolver=Sync(()=>AddressResolver.ResolveAsync(tmpl,new string[0],(s,t)=>System.Threading.Tasks.Task.FromResult(new string[0]),t=>Bootstrap.PinnedAsync(t,default,hang,fast)));
   Check(viaResolver.Error=="BOOTSTRAP_TIMEOUT"&&viaResolver.Servers.Length==0&&CodeTable.Known("BOOTSTRAP_TIMEOUT"),"bootstrap: the connect flow reports BOOTSTRAP_TIMEOUT, starts nothing and has a message for it");
   // silent resolvers must not hold up addresses that were found: system resolver answered, the cross-check hangs forever
   var partial=new FakeBootstrapNet{SysIps=new[]{"212.109.195.91"},CrossHang=true};partial.TlsOk.Add("212.109.195.91");partial.DohOk.Add("212.109.195.91");
   sw.Restart();
   Check(Sync(()=>Bootstrap.PinnedAsync(tmpl,default,partial,fast)).SequenceEqual(new[]{"212.109.195.91"})&&sw.ElapsedMilliseconds<2500,"bootstrap: a hanging cross-check is dropped after the discovery time, what the system resolver found is still validated and used");
   // nothing answers at all: a timeout, not "no address passed the checks"
   var mute=new FakeBootstrapNet{SysIps=new string[0],CrossHang=true};
   Exception mc=null;try{Sync(()=>Bootstrap.PinnedAsync(tmpl,default,mute,fast));}catch(BootstrapTimeoutException e){mc=e;}
   Check(mc!=null,"bootstrap: when no resolver answers in time the result is a timeout");
   // an ordinary empty answer (resolvers answered, nothing usable) stays 'no address'
   var empty=new FakeBootstrapNet();
   Check(Sync(()=>Bootstrap.PinnedAsync(tmpl,default,empty,fast)).Length==0,"bootstrap: resolvers that answered with nothing usable are not a timeout");
   // the user's own cancellation is neither an empty result nor a timeout, all the way up to the connect flow
   using(var cts=new CancellationTokenSource()){
    cts.Cancel();Exception ce=null;try{Sync(()=>Bootstrap.PinnedAsync(tmpl,cts.Token,hang,fast));}catch(Exception e){ce=e;}
    Check(ce!=null&&ce is OperationCanceledException&&!(ce is BootstrapTimeoutException),"bootstrap: a cancelled search throws a cancellation, not a timeout and not 'no address'");
    Exception re=null;try{Sync(()=>AddressResolver.ResolveAsync(tmpl,new string[0],(s,t)=>System.Threading.Tasks.Task.FromResult(new string[0]),t=>Bootstrap.PinnedAsync(t,cts.Token,hang,fast)));}catch(Exception e){re=e;}
    Check(re is OperationCanceledException,"connect: a cancelled search is a cancelled operation (no BOOTSTRAP_* error is recorded)");
   }
   Check(new BootstrapOptions().Budget==TimeSpan.FromSeconds(20)&&new BootstrapOptions().Discovery<new BootstrapOptions().Budget,"bootstrap: the default budget is 20 seconds and the resolvers get only part of it");

   // the resolver list: documented public DoH plus the catalog providers by their pins (not the provider being searched), public addresses only
   var list=LiveBootstrapNet.Resolvers("dns.comss.one");
   var comssPins=ProfileCatalog.BuiltIn.Find("dns.comss.one").Servers;var malwPins=ProfileCatalog.BuiltIn.Find("dns.malw.link").Servers;
   Check(list.Any(r=>r.Template=="https://dns.adguard-dns.com/dns-query")&&list.Any(r=>r.Template=="https://unfiltered.adguard-dns.com/dns-query")&&list.Any(r=>r.Template=="https://dns.sb/dns-query")&&list.Any(r=>r.Server=="1.1.1.1")&&list.Any(r=>r.Server=="8.8.8.8")&&list.Any(r=>r.Server=="9.9.9.9"),"resolvers: the three earlier ones plus the AdGuard DNS and DNS.SB entries of their official pages");
   Check(list.Any(r=>r.Server==malwPins[0]&&r.Template==ProfileCatalog.BuiltIn.Find("dns.malw.link").Template)&&!list.Any(r=>comssPins.Contains(r.Server)),"resolvers: other catalog providers ask by their pins; the provider being searched does not ask itself");
   Check(list.All(r=>Program.PublicIp(r.Server)&&r.Template.StartsWith("https://"))&&list.Select(r=>r.Server+"|"+r.Template).Distinct().Count()==list.Length&&list.Length>=10&&!list.Any(r=>r.Template.Contains("yandex")),"resolvers: public addresses, https templates, no duplicates; Yandex stays out because its official page names no DoH host");
   var catalogOnly=LiveBootstrapNet.Resolvers("nothing.example.test");
   Check(ProfileCatalog.BuiltIn.Profiles.All(p=>p.Servers.Take(LiveBootstrapNet.PinsPerCatalogProvider).All(ip=>catalogOnly.Any(r=>r.Server==ip))),"resolvers: every catalog provider is represented by up to two pins");
  }

  // last working addresses: pins -> saved (not older than 30 days, same preflight) -> bootstrap
  static void RunSavedAddressTests() {
   const string tmpl="https://dns.example.test/dns-query";
   var pins=new[]{"193.233.112.67","193.233.112.68"};var savedIps=new[]{"83.220.169.155","195.133.25.16","212.109.195.93"};
   var now=new DateTime(2026,10,12,12,30,0,DateTimeKind.Utc);
   var asked=new List<string[]>();int bootCalls=0;
   Func<string[],string,System.Threading.Tasks.Task<string[]>> Alive(params string[] ok)=>(s,t)=>{asked.Add(s);return System.Threading.Tasks.Task.FromResult(s.Where(ok.Contains).ToArray());};
   Func<string,System.Threading.Tasks.Task<string[]>> bootDown=t=>{bootCalls++;throw new System.Net.Http.HttpRequestException("bootstrap unreachable");};
   Func<string,System.Threading.Tasks.Task<string[]>> bootOk=t=>{bootCalls++;return System.Threading.Tasks.Task.FromResult(new[]{"212.109.195.91"});};
   SavedAddr Rec(int ageDays,params string[] ips)=>new SavedAddr{Template=tmpl,Servers=ips,Utc=now.AddDays(-ageDays).ToString("o")};
   AddressPlan Plan(Func<string[],string,System.Threading.Tasks.Task<string[]>> pre,Func<string,System.Threading.Tasks.Task<string[]>> boot,SavedAddr saved){asked.Clear();bootCalls=0;return Sync(()=>AddressResolver.ResolveAsync(tmpl,pins,pre,boot,()=>now,saved));}

   var a=Plan(Alive(savedIps),bootDown,Rec(3,savedIps));
   Check(a.Error==null&&a.Source=="saved"&&a.Servers.SequenceEqual(savedIps)&&bootCalls==0&&a.FoundUtc==now.AddDays(-3),"saved: dead pins and a failing bootstrap, live saved addresses -> the connection goes ahead on them");
   var b=Plan(Alive(),bootOk,Rec(3,savedIps));
   Check(b.Source=="found"&&bootCalls==1&&asked.Count==2&&asked[1].SequenceEqual(savedIps),"saved: dead saved addresses are tested once and then the provider name is bootstrapped");
   var c=Plan(Alive(savedIps),bootDown,Rec(31,savedIps));
   Check(c.Error=="BOOTSTRAP_FAILED"&&asked.Count==1&&asked[0].SequenceEqual(pins),"saved: an entry older than 30 days is ignored (never even tested)");
   Check(Plan(Alive(savedIps),bootDown,Rec(30,savedIps)).Source=="saved"&&Plan(Alive(savedIps),bootDown,Rec(29,savedIps)).Source=="saved","saved: an entry of 29 and exactly 30 days is still used");
   Check(Plan(Alive(savedIps),bootDown,Rec(-2,savedIps)).Error=="BOOTSTRAP_FAILED","saved: an entry dated in the future (clock error or tampering) is ignored");
   var d=Plan(Alive(savedIps[1]),bootDown,Rec(3,savedIps));
   Check(d.Source=="saved"&&d.Servers.SequenceEqual(new[]{savedIps[1]}),"saved: only the saved addresses that pass preflight are used, the others are dropped");
   var e=Plan(Alive(),bootDown,Rec(3,savedIps));
   Check(e.Error=="BOOTSTRAP_FAILED"&&e.Servers.Length==0,"saved: an address that does not pass preflight is never used, even when nothing else is left");
   var f=Plan((s,t)=>{asked.Add(s);if(s.Length>0&&s[0]==pins[0])return System.Threading.Tasks.Task.FromResult(new string[0]);throw new InvalidOperationException("probe crashed");},bootOk,Rec(3,savedIps));
   Check(f.Source=="found"&&bootCalls==1,"saved: a crashing preflight of the saved addresses falls through to the bootstrap");
   var g=Plan(Alive(pins),bootDown,Rec(3,savedIps));
   Check(g.Source=="pinned"&&asked.Count==1&&bootCalls==0,"saved: when a pin works the saved addresses are not consulted");
   var h=Plan(Alive(savedIps[0]),bootDown,Rec(3,pins[0],savedIps[0]));
   Check(h.Source=="saved"&&asked.Count==2&&asked[1].SequenceEqual(new[]{savedIps[0]}),"saved: an address that is also a pin is not tested a second time");
   var i=Plan(Alive("10.0.0.5","83.220.169.155"),bootDown,Rec(3,"10.0.0.5","83.220.169.155"));
   Check(i.Source=="saved"&&i.Servers.SequenceEqual(new[]{"83.220.169.155"})&&asked[1].SequenceEqual(new[]{"83.220.169.155"}),"saved: private or loopback saved addresses are never tried");
   var j=Plan(Alive(savedIps),bootDown,null);
   Check(j.Error=="BOOTSTRAP_FAILED","saved: no entry -> behaviour of 1.4 (bootstrap decides)");
   var jj=Plan(Alive(savedIps),bootDown,new SavedAddr{Template=tmpl,Servers=savedIps,Utc="not a date"});
   Check(jj.Error=="BOOTSTRAP_FAILED","saved: an entry with an unreadable date is ignored");
   Check(AddressResolver.SourceText("saved",now)=="сохранённые, "+now.ToLocalTime().ToString("dd.MM"),"saved: the source is shown as saved addresses with the date");

   // the store: one entry per profile, at most four addresses, newest wins, public addresses only
   var list=new List<SavedAddr>();
   SavedAddresses.Put(list,tmpl,new[]{"83.220.169.155","83.220.169.155","195.133.25.16","212.109.195.93","212.109.195.94","212.109.195.95","10.1.1.1"},now);
   Check(list.Count==1&&list[0].Servers.Length==4&&!list[0].Servers.Contains("10.1.1.1"),"saved store: at most four distinct public addresses per profile");
   SavedAddresses.Put(list,tmpl.ToUpperInvariant().Replace("HTTPS","https"),new[]{"212.109.195.91"},now.AddDays(1));
   Check(list.Count==1&&list[0].Servers.SequenceEqual(new[]{"212.109.195.91"})&&SavedAddresses.FoundAt(list[0])==now.AddDays(1),"saved store: a newer find replaces the old entry of the same profile");
   SavedAddresses.Put(list,tmpl,new string[0],now);SavedAddresses.Put(list,tmpl,new[]{"192.168.0.1"},now);
   Check(list.Count==1&&list[0].Servers.SequenceEqual(new[]{"212.109.195.91"}),"saved store: an empty or private-only result never overwrites a good entry");
   for(int k=0;k<20;k++)SavedAddresses.Put(list,"https://p"+k+".example.test/dns-query",new[]{"212.109.195.91"},now);
   Check(list.Count==SavedAddresses.MaxEntries&&SavedAddresses.Find(list,"https://p19.example.test/dns-query")!=null&&SavedAddresses.Find(list,"https://p0.example.test/dns-query")==null,"saved store: the list is bounded, the oldest entries go first");
   // settings round trip: public fields are serialised only with IncludeFields (the pitfall of the project)
   var st=new AppSettings();SavedAddresses.Put(st.SavedAddrs,tmpl,savedIps,now);
   var back=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(st,Program.JsonOptions),Program.JsonOptions);
   Check(back.SavedAddrs.Count==1&&back.SavedAddrs[0].Servers.SequenceEqual(savedIps)&&SavedAddresses.FoundAt(back.SavedAddrs[0])==now&&JsonSerializer.Deserialize<AppSettings>("{\"SavedAddrs\":null}",Program.JsonOptions)!=null,"saved store: survives the settings file round trip");
  }

  // best-profile selection
  static object OkMulti(string host,int ms,string[] ips)=>new{ms,packet=Convert.ToBase64String(MakePacket(host,1234,b=>{foreach(string ip in ips)AppendARr(b,host,ip);},(ushort)ips.Length))};
  static Dictionary<string,object> ProfX(string id,string[] servers,object[] q1,object[] q2,Dictionary<string,bool> answerTls=null,bool experimental=false)=>new Dictionary<string,object>{["id"]=id,["template"]="https://"+id+".test/dns-query",["servers"]=servers,["tls"]=new object[]{new{ok=true,ms=40}},["queries"]=new Dictionary<string,object[]>{[Program.Targets[0]]=q1,[Program.Targets[1]]=q2},["answerTls"]=answerTls??new Dictionary<string,bool>(),["experimental"]=experimental};
  static void RunBestChoiceTests() {
   string d1=Program.Targets[0],d2=Program.Targets[1];
   var sv=new[]{"193.233.112.67","193.233.112.68"};
   var pool=new[]{"193.233.112.71","193.233.112.72","193.233.112.73","193.233.112.74"};
   object[] One(string host,int ms,string ip)=>new[]{Ok(host,ms,ip)};
   var healthy=ProfX("healthy",sv,One(d1,80,"193.233.112.67"),One(d2,80,"193.233.112.88"));
   var halfdead=ProfX("halfdead",sv,new[]{OkMulti(d1,20,pool)},One(d2,20,"193.233.112.88"),new Dictionary<string,bool>{[pool[1]]=false,[pool[2]]=false,[pool[3]]=false});
   var expSlow=ProfX("expslow",sv,One(d1,900,"193.233.112.67"),One(d2,900,"193.233.112.88"),null,true);
   var expGood=ProfX("expgood",sv,One(d1,30,"193.233.112.67"),One(d2,30,"193.233.112.88"),null,true);
   var run=Bench(JsonSerializer.Serialize(new{attempts=6,profiles=new object[]{halfdead,healthy,expSlow}}));
   ProfileResult R(string id)=>run.Results.Single(r=>r.Id==id);
   Check(R("halfdead").MedianMs<R("healthy").MedianMs&&R("halfdead").DomainsOk&&R("halfdead").Alive&&R("halfdead").Reach<Benchmark.ReachThreshold,"best choice: a pool with dead answer addresses has the better latency but reach below 75%");
   Check(!R("halfdead").Selectable&&!R("halfdead").Recommendable&&R("halfdead").Verdict=="Flaky","best choice: low reach makes the profile not selectable (Flaky)");
   Check(run.Winner=="healthy"&&R("healthy").Rank==1&&R("halfdead").Rank>R("healthy").Rank,"best choice: the partly dead pool with good latency ranks below the healthy profile");
   Check(R("expslow").Experimental&&R("expslow").Selectable&&!R("expslow").Recommendable&&!R("expslow").Best&&R("expslow").Rank<R("halfdead").Rank,"best choice: an experimental profile that is not Good stays selectable by hand but is never the recommended one");
   var onlyWeak=Bench(JsonSerializer.Serialize(new{attempts=6,profiles=new object[]{halfdead,expSlow}}));
   Check(onlyWeak.Winner==null&&!onlyWeak.Results.Any(r=>r.Best),"best choice: when only weak profiles remain there is no recommendation");
   var withExp=Bench(JsonSerializer.Serialize(new{attempts=6,profiles=new object[]{healthy,expGood}}));
   Check(withExp.Results.Single(r=>r.Id=="expgood").Verdict=="Good"&&withExp.Results.Single(r=>r.Id=="expgood").Recommendable&&withExp.Winner=="healthy","best choice: a faster Good experimental profile is recommendable but does not displace a Good confirmed one");
   var expOnly=Bench(JsonSerializer.Serialize(new{attempts=6,profiles=new object[]{halfdead,expGood}}));
   Check(expOnly.Winner=="expgood","best choice: an experimental profile that earns the Good verdict is recommended when no confirmed profile is Good");
   Check(ProfileCatalog.BuiltIn.Profiles.Where(p=>p.Experimental).Select(p=>p.Slot).OrderBy(x=>x).SequenceEqual(new[]{"P4","P7","P8"}),"catalog: geohide.ru (degraded), dns.zapret.moe and eu.geohide.ru (new) carry the experimental flag, nothing else does");
  }

  // switch to a working provider: offered, never silent, trust first, experimental never offered
  static void RunSwitchTests() {
   string d1=Program.Targets[0],d2=Program.Targets[1];
   var sv=new[]{"193.233.112.67","193.233.112.68"};
   var poolIps=new[]{"193.233.112.71","193.233.112.72","193.233.112.73","193.233.112.74"};
   object[] One(string host,int ms,string ip)=>new[]{Ok(host,ms,ip)};
   object[] Dead()=>new object[]{Timeout()};
   Dictionary<string,object> Good(string id,int ms,bool exp=false)=>ProfX(id,sv,One(d1,ms,"193.233.112.67"),One(d2,ms,"193.233.112.88"),null,exp);
   SwitchOutcome Find(string current,CancellationToken ct,params Dictionary<string,object>[] profiles){
    var t=SyntheticTransport.Load(JsonSerializer.Serialize(new{attempts=6,profiles=profiles.Cast<object>().ToArray()}));
    return Sync(()=>ProviderSwitch.FindAsync(t.Profiles,current,t,t.Options,ct));
   }
   var dead=ProfX("deadone",sv,Dead(),Dead());
   var a=Find("deadone",default,dead,Good("alive",80));
   Check(a.Kind==SwitchKind.Offer&&a.Offer.ProfileId=="alive"&&a.Offer.FromId=="deadone"&&a.Checked==1,"switch: the current provider is dead, another one is alive -> the offer names that profile");
   var b=Find("deadone",default,dead,Good("slow",600),Good("fast",30),Good("mid",200));
   Check(b.Kind==SwitchKind.Offer&&b.Offer.ProfileId=="fast","switch: of several working profiles the best one by the benchmark ranking is offered");
   var c=Find("alive",default,Good("alive",20),Good("other",300));
   Check(c.Kind==SwitchKind.Offer&&c.Offer.ProfileId=="other"&&c.Checked==1,"switch: the profile that has just failed is never offered back, even if it measures best");
   var e1=Find("deadone",default,dead,Good("expfast",20,true));
   Check(e1.Kind==SwitchKind.NoWorking&&e1.Offer==null&&e1.Checked==0,"switch: an experimental profile is not offered (and not even measured) when it is the only one that works");
   var e2=Find("deadone",default,dead,Good("expfast",10,true),Good("plain",500));
   Check(e2.Kind==SwitchKind.Offer&&e2.Offer.ProfileId=="plain","switch: an experimental profile never wins against a non-experimental one, however fast");
   var allDead=Find("deadone",default,dead,ProfX("dead2",sv,Dead(),Dead()),ProfX("dead3",sv,Dead(),Dead()));
   Check(allDead.Kind==SwitchKind.NoWorking&&allDead.Offer==null&&allDead.Checked==2,"switch: every other profile is dead -> NoWorking (the caller says so and removes nothing)");
   var half=ProfX("halfdead",sv,new[]{OkMulti(d1,20,poolIps)},One(d2,20,"193.233.112.88"),new Dictionary<string,bool>{[poolIps[1]]=false,[poolIps[2]]=false,[poolIps[3]]=false});
   var h=Find("deadone",default,dead,half);
   Check(h.Kind==SwitchKind.NoWorking,"switch: a profile with fewer than 75 % reachable answer addresses is not offered");
   using(var cts=new CancellationTokenSource()){cts.Cancel();Check(Find("deadone",cts.Token,dead,Good("alive",80)).Kind==SwitchKind.Cancelled,"switch: a cancelled search offers nothing");}
   Check(Find("deadone",default).Kind==SwitchKind.NoWorking,"switch: an empty pool is NoWorking, not a crash");

   // the pick itself (not only the pool in front of it): selectable, not experimental, not the failed profile, best rank first
   ProfileResult PR(string id,int rank,bool selectable,bool exp)=>new ProfileResult{Id=id,Rank=rank,Selectable=selectable,Experimental=exp};
   var handmade=new BenchRun{Results=new[]{PR("expfirst",1,true,true),PR("failed",2,true,false),PR("unfit",3,false,false),PR("late",5,true,false),PR("early",4,true,false)}};
   Check(ProviderSwitch.Pick(handmade,"failed").Id=="early"&&ProviderSwitch.Pick(new BenchRun{Results=new[]{PR("a",1,false,false),PR("b",2,true,true)}},"x")==null,"switch pick: skips experimental, the failed profile and unselectable ones, then takes the best rank");

   // pool: pins plus fresh saved addresses, no experimental profile
   var cat=ProfileCatalog.BuiltIn;var now=new DateTime(2026,10,12,12,0,0,DateTimeKind.Utc);
   var geo=cat.Find("dns.bezmezhau.com");var saved=new List<SavedAddr>();
   SavedAddresses.Put(saved,geo.Template,new[]{"212.109.195.91",geo.Servers[0]},now.AddDays(-2));
   SavedAddresses.Put(saved,cat.Find("dns.comss.one").Template,new[]{"212.109.195.92"},now.AddDays(-40));
   var pool=ProviderSwitch.Pool(cat,saved,now);
   Check(pool.All(p=>!p.Experimental)&&!pool.Any(p=>p.Id=="geohide.ru"||p.Id=="dns.zapret.moe"||p.Id=="eu.geohide.ru")&&pool.Count==cat.Profiles.Count(x=>!x.Experimental),"switch pool: the experimental profiles of the catalog are left out");
   var geoPool=pool.Single(p=>p.Id=="dns.bezmezhau.com");
   Check(geoPool.Servers.Take(geo.Servers.Length).SequenceEqual(geo.Servers)&&geoPool.Servers.Contains("212.109.195.91")&&geoPool.Servers.Distinct().Count()==geoPool.Servers.Length,"switch pool: pins first, then the saved addresses of the profile, without duplicates");
   Check(!pool.Single(p=>p.Id=="dns.comss.one").Servers.Contains("212.109.195.92"),"switch pool: a saved entry older than 30 days is not measured");

   // which failures start a search for another provider
   Check(new[]{"BOOTSTRAP_NO_ADDRESS","BOOTSTRAP_TIMEOUT","DOH_NO_VALIDATED_ENDPOINT","DOH_NO_VALIDATED_ENDPOINT: preflight failed"}.All(ProviderSwitch.IsTrigger)&&!new[]{"BOOTSTRAP_FAILED","NRPT_CONFLICT","WMI_NOT_RESPONDING","UAC_CANCELLED","ENGINE_TIMEOUT","RECOVERY_REQUIRED","",null}.Any(ProviderSwitch.IsTrigger),"switch: only 'no usable address' failures start the search; a conflict, a dead WMI, a cancelled UAC or a missing connection do not");

   // carrying it out: trust first, then the ordinary enable; a refusal applies nothing
   var order=new List<string>();var offer=new SwitchOffer{ProfileId="geohide.ru",FromId="dns.malw.link"};
   bool done=Sync(()=>ProviderSwitch.ApplyAsync(offer,id=>{order.Add("trust:"+id);return false;},id=>{order.Add("enable:"+id);return System.Threading.Tasks.Task.CompletedTask;}));
   Check(!done&&order.SequenceEqual(new[]{"trust:geohide.ru"}),"switch apply: when the person declines the trust dialog nothing is enabled");
   order.Clear();done=Sync(()=>ProviderSwitch.ApplyAsync(offer,id=>{order.Add("trust:"+id);return true;},id=>{order.Add("enable:"+id);return System.Threading.Tasks.Task.CompletedTask;}));
   Check(done&&order.SequenceEqual(new[]{"trust:geohide.ru","enable:geohide.ru"}),"switch apply: trust is asked first, then the profile is enabled once");
   order.Clear();Check(!Sync(()=>ProviderSwitch.ApplyAsync(null,id=>true,id=>{order.Add("x");return System.Threading.Tasks.Task.CompletedTask;}))&&order.Count==0,"switch apply: no offer, nothing happens");

   // messages
   var offered=CodeTable.Lookup("PROVIDER_SWITCH_OFFERED");var none=CodeTable.Lookup("NO_WORKING_PROVIDER");
   Check(CodeTable.Known("PROVIDER_SWITCH_OFFERED")&&CodeTable.Known("NO_WORKING_PROVIDER")&&offered.Kind==ActionKind.Switch&&none.Text.Contains("Мост не менялся"),"switch: both new codes are in the message table with an action; 'no working provider' says the bridge is untouched");

   // window: the offer is a button in the place of 'Починить', and goes away with the user's own actions
   using(var form=new MainForm(true)) {
    form.Show();Application.DoEvents();
    form.SmokeState("switch");
    Check(form.AltVisible&&!form.FixVisible&&form.AltText.StartsWith("Подключить через ")&&form.StatusText=="Мост отключён"&&!form.BridgeActive,"switch window: the offer stays under a disconnected bridge with one explicit apply action");
    form.SettleForTest("{\"State\":\"x\",\"Status\":{\"JournalPhase\":\"Restored\"}}");
    Check(!form.AltVisible,"switch window: the person's own operation clears the offer");
    form.SmokeState("noprovider");
    Check(!form.AltVisible&&form.StatusText=="Мост отключён"&&!form.BridgeActive&&form.SubText.Contains(none.Text),"switch window: unavailable provider stays subordinate to the disconnected bridge state");
    form.SmokeState("switch");form.SmokeState("idle");
    Check(!form.AltVisible&&!form.FixVisible,"switch window: back to the plain state, no stale button");
    // the lifecycle of an offer made by a failed repair on a bridge that is still configured (review: a stale offer hid 'Починить')
    var other=ProfileCatalog.BuiltIn.Profiles.First(p=>!p.Experimental).Id;
    form.SmokeHealth(new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.ProviderDown,CanFix=true,Utc=DateTime.UtcNow});
    form.SetOfferForTest(other);
    Check(form.AltVisible&&!form.FixVisible,"switch window: after a failed repair the offer takes the place of 'Починить'");
    form.ApplyHealth(new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.ProviderDown,CanFix=true,Light=true,Utc=DateTime.UtcNow});
    Check(form.OfferActive&&form.AltVisible,"switch window: a repeated provider-down report keeps the offer");
    form.ApplyHealth(new HealthReport{Kind=HealthKind.Healthy,Code=HealthMonitor.Ok,Light=true,Utc=DateTime.UtcNow});
    Check(!form.OfferActive&&!form.AltVisible,"switch window: the provider answering again (light check) withdraws the offer");
    form.SmokeHealth(new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.ProviderDown,CanFix=true,Utc=DateTime.UtcNow});form.SetOfferForTest(other);
    form.ApplyHealth(new HealthReport{Kind=HealthKind.Healthy,Code=HealthMonitor.Ok,Utc=DateTime.UtcNow});
    Check(!form.OfferActive,"switch window: a healthy full check withdraws the offer too");
    form.SmokeHealth(new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.ProviderDown,CanFix=true,Utc=DateTime.UtcNow});form.SetOfferForTest(other);
    form.ApplyHealth(new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.DriftRules,CanFix=true,Utc=DateTime.UtcNow});
    Check(!form.OfferActive&&form.FixVisible&&!form.AltVisible,"switch window: a later rules drift withdraws the offer and shows 'Починить' again, on the page");
    form.SmokeState("idle");form.SetOfferForTest(other);
    Check(form.OfferActive&&form.AltVisible,"switch window: an offer after a failed first connect stays while the bridge is off (an 'off' health report does not withdraw it)");
    form.ApplyHealth(new HealthReport{Kind=HealthKind.Off,Code="NOT_CONFIGURED",Utc=DateTime.UtcNow});
    Check(form.OfferActive,"switch window: a report that nothing is configured keeps the offer made for the failed connect");
    form.JournalReader=()=>"{\"Phase\":\"Active\"}";
    form.PresentForTest("{\"State\":\"DNS active\",\"Active\":true,\"JournalPhase\":\"Active\"}");
    Check(form.BridgeActive&&!form.OfferActive&&!form.AltVisible,"switch window: a status that shows a working bridge withdraws the offer");
    form.Close();
   }
  }

  // non-443 DoH port
  static void RunPortTests() {
   var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();
   try {
    int port=((IPEndPoint)listener.LocalEndpoint).Port;
    var accept=listener.AcceptTcpClientAsync();
    var reply=System.Threading.Tasks.Task.Run(()=>Wire.Doh("doh.test",1,"127.0.0.1",2,"https://doh.test:"+port+"/dns-query"));
    bool connected=accept.Wait(3000);
    if(connected)accept.Result.Dispose();
    reply.Wait(5000);
    Check(connected&&reply.IsCompleted&&reply.Result.Code!="DNS_OK","DoH probe dials the port from the template (:"+port+"), not 443");
    var accept2=listener.AcceptTcpClientAsync();
    using(var live=new LiveTransport()) {
     var spec=new ProfileSpec{Id="port.test",Template="https://doh.test:"+port+"/dns-query",Servers=new[]{"127.0.0.1"}};
     var q=live.QueryAsync(spec,"127.0.0.1",Program.Targets[0],1,2000,CancellationToken.None);
     bool connected2=accept2.Wait(3000);if(connected2)accept2.Result.Dispose();
     q.Wait(5000);
     Check(connected2&&spec.Port==port,"benchmark transport dials the port from the template (:"+port+")");
    }
   }finally{listener.Stop();}
  }

  public static int Run(){try {

   string host=Program.Target;
   Reject(()=>Wire.Query("",1,1),"empty domain");Reject(()=>Wire.Query("a..b",1,1),"empty DNS label");Reject(()=>Wire.Query("bad;host",1,1),"domain injection");Reject(()=>Wire.Query(new string('a',64)+".com",1,1),"label length");Reject(()=>Wire.Query("example.com",15,1),"unsupported RRtype");
   var a=Answer(host,1,new byte[]{1,2,3,4});Check(Wire.Parse(a,host,1,1234).Addresses.Single()=="1.2.3.4","A wire reply");
   var aaaa=Answer(host,28,IPAddress.Parse("2606:4700::1111").GetAddressBytes());Check(Wire.Parse(aaaa,host,28,1234).Addresses.Single()=="2606:4700::1111","AAAA wire reply");
   Reject(()=>Wire.Parse(a,host,1,999),"transaction mismatch");Reject(()=>Wire.Parse(a,"other.google.com",1,1234),"question mismatch");Reject(()=>Wire.Parse(a.Take(a.Length-1).ToArray(),host,1,1234),"truncated rdata");
   var truncated=(byte[])a.Clone();truncated[2]|=2;Reject(()=>Wire.Parse(truncated,host,1,1234),"TC flag rejects partial response");
   var loop=(byte[])a.Clone();loop[12]=192;loop[13]=12;Reject(()=>Wire.Parse(loop,host,1,1234),"compression pointer loop");
   var nodata=Wire.Query(host,28,1234);nodata[2]=129;nodata[3]=128;Check(Wire.Parse(nodata,host,28,1234).Code=="DNS_NODATA","AAAA NODATA valid");
   var nx=(byte[])nodata.Clone();nx[3]=131;Check(Wire.Parse(nx,host,28,1234).Code=="DNS_NXDOMAIN","NXDOMAIN interpreted");
   Check(Program.SystemModulesOnly(new ProcessStartInfo("x")).Environment["PSModulePath"]==Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","Modules"),"engine: every PowerShell the exe starts searches only the system module folder");
   Check(!Program.PublicIp("127.0.0.1")&&!Program.PublicIp("0.0.0.0")&&!Program.PublicIp("192.168.1.1")&&!Program.PublicIp("100.64.0.1")&&!Program.PublicIp("invalid"),"private/invalid answers rejected");Check(Program.PublicIp("179.254.166.171"),"candidate public IPv4 accepted");
   // A foreign owner in an otherwise valid packet cannot supply the target answer.
   var foreign=new List<byte>(Wire.Query(host,1,1234));foreign[2]=129;foreign[3]=128;foreign[7]=1;foreign.AddRange(new byte[]{4,101,118,105,108,0,0,1,0,1,0,0,0,1,0,4,1,2,3,4});
   Check(Wire.Parse(foreign.ToArray(),host,1,1234).Addresses.Length==0,"unrelated answer owner ignored");

   // CNAME & A record ordering and multi-hop resolution
   var cnameFirst=MakePacket(host,1234,b=>{AppendCnameRr(b,host,"canonical.example.com");AppendARr(b,"canonical.example.com","1.2.3.4");},2);
   var repCnameFirst=Wire.Parse(cnameFirst,host,1,1234);
   Check(repCnameFirst.Code=="DNS_OK"&&repCnameFirst.Addresses.Single()=="1.2.3.4"&&repCnameFirst.Cnames.Single()=="canonical.example.com","CNAME-first answer resolved");

   var aFirst=MakePacket(host,1234,b=>{AppendARr(b,"canonical.example.com","1.2.3.4");AppendCnameRr(b,host,"canonical.example.com");},2);
   var repAFirst=Wire.Parse(aFirst,host,1,1234);
   Check(repAFirst.Code=="DNS_OK"&&repAFirst.Addresses.Single()=="1.2.3.4"&&repAFirst.Cnames.Single()=="canonical.example.com","A-first preceding CNAME resolved");
   Check(repCnameFirst.Addresses.SequenceEqual(repAFirst.Addresses),"Order independence identical address");

   var multiHop=MakePacket(host,1234,b=>{AppendCnameRr(b,"cdn.example.com","edge.example.com");AppendARr(b,"edge.example.com","1.2.3.4");AppendCnameRr(b,host,"cdn.example.com");},3);
   var repMulti=Wire.Parse(multiHop,host,1,1234);
   Check(repMulti.Code=="DNS_OK"&&repMulti.Addresses.Single()=="1.2.3.4","Multi-hop CNAME address resolved");
   Check(repMulti.Cnames.SequenceEqual(new[]{"cdn.example.com","edge.example.com"}),"Multi-hop CNAME aliases in traversal order");

   var cyclical=MakePacket(host,1234,b=>{AppendCnameRr(b,host,"loop.example.com");AppendCnameRr(b,"loop.example.com",host);AppendARr(b,"loop.example.com","1.2.3.4");},3);
   var repCycle=Wire.Parse(cyclical,host,1,1234);
   Check(repCycle.Code=="DNS_NODATA"&&repCycle.Addresses.Length==0,"Cyclical CNAME loop detected with DNS_NODATA");

   var allowed8=MakePacket(host,1234,b=>{string prev=host;for(int i=1;i<=8;i++){string next="hop"+i+".example.com";AppendCnameRr(b,prev,next);prev=next;}AppendARr(b,prev,"1.2.3.4");},9);
   var repAllowed8=Wire.Parse(allowed8,host,1,1234);
   Check(repAllowed8.Code=="DNS_OK"&&repAllowed8.Addresses.Single()=="1.2.3.4"&&repAllowed8.Cnames.Length==8,"8-hop CNAME chain resolves within limit");

   var exceeded9=MakePacket(host,1234,b=>{string prev=host;for(int i=1;i<=9;i++){string next="hop"+i+".example.com";AppendCnameRr(b,prev,next);prev=next;}AppendARr(b,prev,"1.2.3.4");},10);
   var repExceeded9=Wire.Parse(exceeded9,host,1,1234);
   Check(repExceeded9.Code=="DNS_NODATA"&&repExceeded9.Addresses.Length==0,"9-hop CNAME chain exceeds limit and yields DNS_NODATA");

   // IPv6 public address validation & bogon rejection
   Check(Program.PublicIp("2a12:bec4:1bb0:2a30::2")&&Program.PublicIp("2606:4700:4700::1111")&&Program.PublicIp("2001:4860:4860::8888"),"IPv6 public address validation");
   Check(!Program.PublicIp("::1")&&!Program.PublicIp("::")&&!Program.PublicIp("fd00::1")&&!Program.PublicIp("fc00::1")&&!Program.PublicIp("fe80::1")&&!Program.PublicIp("fec0::1")&&!Program.PublicIp("ff02::1")&&!Program.PublicIp("2001:db8::1")&&!Program.PublicIp("::ffff:127.0.0.1"),"IPv6 bogon rejection");

   // Mixed A and AAAA wire reply parsing
   var qMixed=Wire.Query(host,1,4321);qMixed[2]=129;qMixed[3]=128;qMixed[7]=2;
   var bMixed=new List<byte>(qMixed);
   bMixed.AddRange(new byte[]{192,12,0,1,0,1,0,0,0,60,0,4,1,2,3,4});
   var ipv6Bytes=IPAddress.Parse("2606:4700::1111").GetAddressBytes();
   bMixed.AddRange(new byte[]{192,12,0,28,0,1,0,0,0,60,0,16});
   bMixed.AddRange(ipv6Bytes);
   var parsedA=Wire.Parse(bMixed.ToArray(),host,1,4321);
   Check(parsedA.Addresses.Length==1&&parsedA.Addresses[0]=="1.2.3.4","mixed packet parses only Type 1 A record");

   var qMixed28=Wire.Query(host,28,4322);qMixed28[2]=129;qMixed28[3]=128;qMixed28[7]=2;
   var bMixed28=new List<byte>(qMixed28);
   bMixed28.AddRange(new byte[]{192,12,0,1,0,1,0,0,0,60,0,4,1,2,3,4});
   bMixed28.AddRange(new byte[]{192,12,0,28,0,1,0,0,0,60,0,16});
   bMixed28.AddRange(ipv6Bytes);
   var parsedAAAA=Wire.Parse(bMixed28.ToArray(),host,28,4322);
   Check(parsedAAAA.Addresses.Length==1&&parsedAAAA.Addresses[0]=="2606:4700::1111","mixed packet parses only Type 28 AAAA record");

   // CLI Argument Parsing and Validation
   var pNet=Program.ParseNetworkArgs(new[]{"--network","--servers","185.185.185.185,185.185.185.186","--template","https://custom.test/dns-query"});
   Check(pNet.Servers!=null&&pNet.Servers.Length==2&&pNet.Servers[0]=="185.185.185.185"&&pNet.Servers[1]=="185.185.185.186"&&pNet.Template=="https://custom.test/dns-query","CLI parses custom IPv4 servers and template");

   var pNet6=Program.ParseNetworkArgs(new[]{"--network","--servers","2606:4700::1111,2001:4860::8888","--plain"});
   Check(pNet6.Servers!=null&&pNet6.Servers.Length==2&&pNet6.Servers[0]=="2606:4700::1111"&&pNet6.Plain,"CLI parses IPv6 servers and --plain");

   var pProf=Program.ParseNetworkArgs(new[]{"--network","--profile","P6"});
   Check(pProf.Servers!=null&&pProf.Servers.SequenceEqual(ProfileCatalog.BuiltIn.Find("P6").Servers)&&pProf.Template=="https://dns.bezmezhau.com/dns-query","CLI binds profile P6 servers and template");

   var pProfOvr=Program.ParseNetworkArgs(new[]{"--network","--profile","P6","--template","https://custom.test/dns-query"});
   Check(pProfOvr.Template=="https://custom.test/dns-query"&&pProfOvr.Servers.SequenceEqual(ProfileCatalog.BuiltIn.Find("P6").Servers),"CLI profile allows explicit template override");

   // Argument Rejection on Invalid Inputs
   Reject(()=>Program.ParseNetworkArgs(new[]{"--network","--servers"}),"missing value for --servers");
   Reject(()=>Program.ParseNetworkArgs(new[]{"--network","--servers",""}),"empty --servers string");
   Reject(()=>Program.ParseNetworkArgs(new[]{"--network","--servers","invalid_ip"}),"invalid IP in --servers");
   Reject(()=>Program.ParseNetworkArgs(new[]{"--network","--template"}),"missing value for --template");
   Reject(()=>Program.ParseNetworkArgs(new[]{"--network","--template","ftp://bad-url.example/dns-query"}),"non-HTTPS template URL");
   Reject(()=>Program.ParseNetworkArgs(new[]{"--network","--template","http://insecure.example/dns-query"}),"HTTP insecure template URL");
   Reject(()=>Program.ParseNetworkArgs(new[]{"--network","--unknown-flag"}),"unknown flag for --network");
   Reject(()=>Program.ValidateCliArgs(new[]{"--invalid-command"}),"unknown top-level command");
   Reject(()=>Program.ParseNetworkArgs(new[]{"--network","--profile"}),"missing value for --profile");
   Reject(()=>Program.ParseNetworkArgs(new[]{"--network","--profile","NonexistentProfile"}),"unknown profile name");

   // Process Execution Timeout and Cancellation
   {
    var sw=Stopwatch.StartNew();
    using var cts=new CancellationTokenSource();
    cts.Cancel();
    var psi=new ProcessStartInfo("cmd.exe","/c "+Path.Combine(Environment.SystemDirectory,"PING.EXE")+" 127.0.0.1 -n 5"){CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
    bool caughtCancel=false;
    using var p=Process.Start(psi);
    try{
     p.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
    }catch(OperationCanceledException){
     caughtCancel=true;
     try{if(!p.HasExited)p.Kill(entireProcessTree:true);}catch{}
    }
    sw.Stop();
    Check(caughtCancel&&sw.ElapsedMilliseconds<1000,"Process WaitForExitAsync aborts immediately upon cancelled token");
   }
   {
    var sw=Stopwatch.StartNew();
    using var cts=new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
    // Full path: the long-running child must not depend on System32 being on PATH of the process that runs the self-test.
    var psi=new ProcessStartInfo("cmd.exe","/c "+Path.Combine(Environment.SystemDirectory,"PING.EXE")+" 127.0.0.1 -n 5"){CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
    bool timedOut=false;
    using var p=Process.Start(psi);
    try{
     p.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
    }catch(OperationCanceledException){
     timedOut=true;
     try{if(!p.HasExited)p.Kill(entireProcessTree:true);}catch{}
     // Kill returns before the OS reports the exit; wait a bounded time instead of reading HasExited at once.
     try{p.WaitForExit(2000);}catch{}
    }
    sw.Stop();
    Check(timedOut&&sw.ElapsedMilliseconds<2500&&p.HasExited,"Process execution terminates and kills process tree on deadline expiry");
   }

   // Non-Blocking Form Closing Behavior
   {
    using(var form=new MainForm(true)){
     form.Show();
     Application.DoEvents();
     form.SetBusyForTest(true);
     Check(form.Busy,"MainForm busy state verified");
     form.Close();
     Application.DoEvents();
     Check(!form.Visible||form.IsDisposed,"MainForm close succeeds without blocking while busy");
    }
   }

   // Autostart Configuration and Non-Elevated Behavior (isolated registry key; user's real Run entry is never touched)
   string realKey=AutostartHelper.KeyName;const string isoKey=@"Software\AgDnsTest\Run";
   Microsoft.Win32.Registry.CurrentUser.CreateSubKey(isoKey).Dispose();AutostartHelper.KeyName=isoKey;
   try {
    string testExe=Environment.ProcessPath;
    AutostartHelper.SetEnabled(true,testExe);
    Check(AutostartHelper.IsEnabled(testExe),"Autostart enabled detects valid target path");
    Check(!AutostartHelper.IsEnabled(@"C:\OtherPath\DifferentApp.exe"),"Autostart rejects non-matching executable path");
    AutostartHelper.SetEnabled(false,testExe);
    Check(!AutostartHelper.IsEnabled(testExe),"Autostart disabled cleanly removes entry");

    using(var form=new MainForm(true)){
     Check(form.AutostartControl!=null&&form.AutostartControl.Text=="Запускать при входе в Windows","MainForm embeds autostart checkbox control");
     Check(form.AutostartControl.Enabled,"MainForm autostart checkbox is initially enabled");
    }

    Program.ValidateCliArgs(new[]{"--autostart-status"});
    Program.ValidateCliArgs(new[]{"--autostart-enable"});
    Program.ValidateCliArgs(new[]{"--autostart-disable"});
    Check(true,"CLI argument validation accepts autostart flags");

   // Mutex abandonment, robustness and quoted arguments
   {
    var parsedQuoted=Program.ParseNetworkArgs(new[]{"--network","--template","\"https://dns.zapret.moe/dns-query\"","--profile","\"P7\""});
    Check(parsedQuoted.Template=="https://dns.zapret.moe/dns-query"&&parsedQuoted.Servers!=null&&parsedQuoted.Servers.Contains("83.217.211.149"),"CLI accepts safely quoted template and profile arguments");

    string mutName="Local\\TestSingleInstance_"+Guid.NewGuid().ToString("N");
    using(var first=new Mutex(true,mutName,out bool firstNew))
    using(var second=new Mutex(true,mutName,out bool secondNew)){
     Check(firstNew&&!secondNew,"Single-instance mutex: second instance detects the first");
    }

    var st=AutostartHelper.GetStatus();
    Check(st!=null,"Autostart status returns populated non-null descriptor");
   }
   }finally{AutostartHelper.KeyName=realKey;try{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\AgDnsTest",false);}catch{}}

   RunSignedCatalogContracts();RunAssistedContracts();RunScenarioContracts();RunReliabilityTests();RunCatalogTests();RunContractTests();RunScriptStoreTests();RunInstallTests();RunBenchmarkTests();RunBenchmarkBudgetTests();RunBestChoiceTests();RunSwitchTests();RunConnectTests();RunResilientBootstrapTests();RunSavedAddressTests();RunRecoveryTests();RunReviewTests();RunHealthTests();RunLightHealthTests();RunReportTests();RunMessageTests();RunTextFitTests();RunGeminiTests();RunUxTests();RunFinalUiTests();RunScopeLangTests();RunBrowserTests();RunUiV3Tests();RunUiV4Tests();RunPortTests();RunDoctorTests();RunAutonomyTests();RunReleaseTests();

   RunCloseoutUiTests();
   RunStage01TrustTests();RunStage01SystemTests();RunStage01BenchmarkTests();RunStage01UiTests();RunStage06Tests();RunStage07Tests();RunElevatedStartupTests();
   Console.WriteLine("OFFLINE_WIRE_PASS "+passed);return 0;
  }catch(Exception e){Console.WriteLine(e.Message);return 1;}}
 }
}
