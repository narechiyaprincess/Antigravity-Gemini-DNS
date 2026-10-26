using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AntigravityGeminiDns {
 // Does a browser take its DNS from Windows (and so go through the bridge for "All Google") or does it bring its own secure DNS (DoH) and skip the
 // bridge's rules? Read-only: the registry, a few settings files of the browser and nothing else. The program never changes a browser's settings.
 // Sources: the Chrome/Edge enterprise policy docs (DnsOverHttpsMode, DnsOverHttpsTemplates), Mozilla policy templates (DNSOverHTTPS), network.trr.mode.
 public enum BrowserFamily{Chromium,Firefox,Other}
 public enum BypassStatus{Follows,Bypasses,Unknown}

 public sealed class BrowserInfo {
  public string Id,Name,Exe,SettingsUrl;
  public BrowserFamily Family;
 }

 public sealed class BrowserVerdict {
  public BypassStatus Status;
  public string Code;       // why, for the log and for the tests: POLICY_ON, POLICY_OFF, USER_ON, USER_OFF, NO_CUSTOM_DNS, ...
  public bool ByPolicy;     // the setting comes from a policy, so the browser's own settings page cannot change it
 }

 // The places the check reads from. Live = this computer; a memory one stands in for tests and for the window's smoke renders.
 public interface IBrowserEnv {
  string RegString(bool machine,string subKey,string name);   // null when the key or value is missing; name null = the default value
  int? RegDword(bool machine,string subKey,string name);
  string[] RegSubKeys(bool machine,string subKey);
  string ReadText(string path);                               // null when the file is missing or cannot be read
  bool FileExists(string path);
  string Dir(string which);                                   // LocalAppData, AppData
 }

 public sealed class LiveBrowserEnv:IBrowserEnv {
  static IEnumerable<RegistryKey> Open(bool machine,string subKey) {
   if(machine){
    foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32}){
     RegistryKey k=null;try{k=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view).OpenSubKey(subKey,false);}catch{}
     if(k!=null)yield return k;
    }
   }else{
    RegistryKey k=null;try{k=Registry.CurrentUser.OpenSubKey(subKey,false);}catch{}
    if(k!=null)yield return k;
   }
  }
  public string RegString(bool machine,string subKey,string name) {
   foreach(var k in Open(machine,subKey))using(k){try{if(k.GetValue(name) is string s)return s;}catch{}}
   return null;
  }
  public int? RegDword(bool machine,string subKey,string name) {
   foreach(var k in Open(machine,subKey))using(k){try{if(k.GetValue(name) is int v)return v;}catch{}}
   return null;
  }
  public string[] RegSubKeys(bool machine,string subKey) {
   var all=new List<string>();
   foreach(var k in Open(machine,subKey))using(k){try{all.AddRange(k.GetSubKeyNames());}catch{}}
   return all.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  }
  public string ReadText(string path) {
   try{
    var fi=new FileInfo(path);if(!fi.Exists||fi.Length>16*1024*1024)return null;
    using(var fs=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var sr=new StreamReader(fs,new UTF8Encoding(false),true))return sr.ReadToEnd();
   }catch{return null;}
  }
  public bool FileExists(string path){try{return File.Exists(path);}catch{return false;}}
  public string Dir(string which)=>which=="AppData"?Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData):Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
 }

 // Test and smoke source: values set in code, nothing read from the computer.
 public sealed class MemoryBrowserEnv:IBrowserEnv {
  readonly Dictionary<string,object> reg=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
  readonly Dictionary<string,string> text=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  readonly HashSet<string> files=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  public string LocalAppData=@"C:\Users\test\AppData\Local",AppData=@"C:\Users\test\AppData\Roaming";
  static string K(bool machine,string key,string name)=>(machine?"M|":"U|")+key.Trim('\\')+"|"+(name??"");
  public MemoryBrowserEnv SetReg(bool machine,string key,string name,object value){reg[K(machine,key,name)]=value;return this;}
  public MemoryBrowserEnv SetText(string path,string content){text[path]=content;files.Add(path);return this;}
  public MemoryBrowserEnv AddFile(string path){files.Add(path);return this;}
  public string RegString(bool machine,string subKey,string name)=>reg.TryGetValue(K(machine,subKey,name),out var v)?v as string:null;
  public int? RegDword(bool machine,string subKey,string name)=>reg.TryGetValue(K(machine,subKey,name),out var v)&&v is int i?i:(int?)null;
  public string[] RegSubKeys(bool machine,string subKey) {
   string prefix=(machine?"M|":"U|")+subKey.Trim('\\')+"\\";
   return reg.Keys.Where(k=>k.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).Select(k=>k.Substring(prefix.Length).Split('|')[0].Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  }
  public string ReadText(string path)=>text.TryGetValue(path,out var t)?t:null;
  public bool FileExists(string path)=>files.Contains(path);
  public string Dir(string which)=>which=="AppData"?AppData:LocalAppData;
 }

 public static class BrowserCatalog {
  sealed class Known {
   public string Id,Name,Exe,Policy,Url,FirefoxRoot;public BrowserFamily Family;public string[] LocalState;public bool Roaming;
   public string PathHint;   // when set, the exe counts as this browser only if its path contains this text (browser.exe is a common name)
   public bool Full;   // policy and settings names confirmed for this browser; false = only a found value is trusted, "nothing found" stays unknown
  }
  static readonly Known[] List={
   new Known{Id="chrome",Name="Google Chrome",Exe="chrome.exe",Family=BrowserFamily.Chromium,LocalState=new[]{@"Google\Chrome\User Data\Local State"},Policy=@"SOFTWARE\Policies\Google\Chrome",Url="chrome://settings/security",Full=true},
   new Known{Id="edge",Name="Microsoft Edge",Exe="msedge.exe",Family=BrowserFamily.Chromium,LocalState=new[]{@"Microsoft\Edge\User Data\Local State"},Policy=@"SOFTWARE\Policies\Microsoft\Edge",Url="edge://settings/privacy",Full=true},
   new Known{Id="brave",Name="Brave",Exe="brave.exe",Family=BrowserFamily.Chromium,LocalState=new[]{@"BraveSoftware\Brave-Browser\User Data\Local State"},Policy=@"SOFTWARE\Policies\BraveSoftware\Brave",Url="brave://settings/security",Full=true},
   new Known{Id="yandex",Name="Yandex Browser",Exe="browser.exe",PathHint=@"\Yandex\",Family=BrowserFamily.Chromium,LocalState=new[]{@"Yandex\YandexBrowser\User Data\Local State"},Policy=@"SOFTWARE\Policies\YandexBrowser"},
   new Known{Id="vivaldi",Name="Vivaldi",Exe="vivaldi.exe",Family=BrowserFamily.Chromium,LocalState=new[]{@"Vivaldi\User Data\Local State"},Policy=@"SOFTWARE\Policies\Vivaldi"},
   new Known{Id="opera",Name="Opera",Exe="opera.exe",Family=BrowserFamily.Chromium,LocalState=new[]{@"Opera Software\Opera Stable\Local State",@"Opera Software\Opera GX Stable\Local State"},Roaming=true},
   new Known{Id="firefox",Name="Mozilla Firefox",Exe="firefox.exe",Family=BrowserFamily.Firefox,Policy=@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS",Url="about:preferences#privacy",FirefoxRoot=@"Mozilla\Firefox",Full=true},
   new Known{Id="waterfox",Name="Waterfox",Exe="waterfox.exe",Family=BrowserFamily.Firefox,Url="about:preferences#privacy",FirefoxRoot="Waterfox"},   // a Firefox build: same settings, its own folder; its policy key is not confirmed
  };
  public const string OtherId="other";
  public static string NameOf(string id)=>id==OtherId?L.T("Другой / не знаю"):List.FirstOrDefault(k=>k.Id==id)?.Name??(id.StartsWith("exe:")?id.Substring(4):id);
  static Known ByExe(string exePath){string f=Path.GetFileName(exePath??"");return List.FirstOrDefault(k=>string.Equals(k.Exe,f,StringComparison.OrdinalIgnoreCase)&&(k.PathHint==null||(exePath??"").IndexOf(k.PathHint,StringComparison.OrdinalIgnoreCase)>=0));}

  // The path of the program in a registry command line: "C:\x\a.exe" --arg  |  C:\x\a.exe  |  C:\x y\a.exe -flag
  public static string ExeOfCommand(string cmd) {
   if(string.IsNullOrWhiteSpace(cmd))return null;
   cmd=Environment.ExpandEnvironmentVariables(cmd.Trim());
   if(cmd.StartsWith("\"")){int q=cmd.IndexOf('"',1);return q>1?cmd.Substring(1,q-1):null;}
   int e=cmd.IndexOf(".exe",StringComparison.OrdinalIgnoreCase);
   return e>0?cmd.Substring(0,e+4):null;
  }

  // Installed browsers, from what Windows registers for "Web browser" and App Paths. Nothing is launched or changed.
  public static List<BrowserInfo> Detect(IBrowserEnv env) {
   var found=new List<BrowserInfo>();
   void Add(string exe,string displayName) {
    if(string.IsNullOrWhiteSpace(exe)||!env.FileExists(exe))return;
    string file=Path.GetFileName(exe);
    if(string.Equals(file,"iexplore.exe",StringComparison.OrdinalIgnoreCase))return;
    if(found.Any(b=>string.Equals(b.Exe,exe,StringComparison.OrdinalIgnoreCase)))return;
    var k=ByExe(exe);
    if(k!=null){if(found.Any(b=>b.Id==k.Id))return;found.Add(new BrowserInfo{Id=k.Id,Name=k.Name,Exe=exe,Family=k.Family,SettingsUrl=k.Url});}
    else found.Add(new BrowserInfo{Id="exe:"+file.ToLowerInvariant(),Name=string.IsNullOrWhiteSpace(displayName)?Path.GetFileNameWithoutExtension(file):displayName,Exe=exe,Family=BrowserFamily.Other});
   }
   foreach(bool machine in new[]{true,false})
    foreach(string name in env.RegSubKeys(machine,@"SOFTWARE\Clients\StartMenuInternet"))
     Add(ExeOfCommand(env.RegString(machine,@"SOFTWARE\Clients\StartMenuInternet\"+name+@"\shell\open\command",null)),env.RegString(machine,@"SOFTWARE\Clients\StartMenuInternet\"+name,null));
   foreach(var k in List)
    foreach(bool machine in new[]{true,false})
     Add(env.RegString(machine,@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\"+k.Exe,null)?.Trim('"'),k.Name);
   int order(BrowserInfo b){int i=Array.FindIndex(List,k=>k.Id==b.Id);return i<0?100:i;}
   return found.OrderBy(order).ThenBy(b=>b.Name,StringComparer.OrdinalIgnoreCase).ToList();
  }

  // The verdict for one browser. "Bypasses" only when the browser itself says it has its own secure DNS servers; "Follows" only when nothing like that is set
  // and the browser's settings are known well enough to say so; everything else is "Unknown", which the window says plainly.
  public static BrowserVerdict Evaluate(BrowserInfo b,IBrowserEnv env) {
   if(b==null||b.Family==BrowserFamily.Other)return V(BypassStatus.Unknown,"OTHER");
   var k=List.FirstOrDefault(x=>x.Id==b.Id);
   if(k==null)return V(BypassStatus.Unknown,"OTHER");
   try{return b.Family==BrowserFamily.Firefox?EvaluateFirefox(b,k,env):EvaluateChromium(k,env);}
   catch(Exception){return V(BypassStatus.Unknown,"NO_PROFILE");}
  }

  // The program that the settings button may start: a rooted path to an existing file whose name is that of a known browser (a registry entry of a program
  // the list does not know is shown in the list, but never started from here).
  public static bool CanOpenSettings(BrowserInfo b,IBrowserEnv env)=>b!=null&&b.Family!=BrowserFamily.Other&&!string.IsNullOrEmpty(b.SettingsUrl)&&ByExe(b.Exe)!=null&&Path.IsPathRooted(b.Exe??"")&&env.FileExists(b.Exe);
  static BrowserVerdict V(BypassStatus s,string code,bool policy=false)=>new BrowserVerdict{Status=s,Code=code,ByPolicy=policy};

  // Chromium: the policy wins over the user's own setting. A built-in secure resolver is used when the mode is "secure" with servers, or "automatic" with servers
  // given explicitly; "automatic" without servers only upgrades the system's resolver, which Chromium does not do while Windows has name-resolution policy rules (NRPT).
  static BrowserVerdict Classify(string mode,string templates,bool policy) {
   mode=(mode??"").Trim().ToLowerInvariant();bool servers=!string.IsNullOrWhiteSpace(templates);
   string src=policy?"POLICY":"USER";
   if(mode=="secure"||mode=="custom")return servers?V(BypassStatus.Bypasses,src+"_ON",policy):V(BypassStatus.Unknown,"SECURE_NO_TEMPLATES",policy);
   if(mode=="automatic")return servers?V(BypassStatus.Bypasses,src+"_ON",policy):V(BypassStatus.Follows,src+"_AUTOMATIC",policy);
   return V(BypassStatus.Follows,src+"_OFF",policy);
  }
  static BrowserVerdict EvaluateChromium(Known k,IBrowserEnv env) {
   if(k.Policy!=null)
    foreach(bool machine in new[]{true,false}){
     string mode=env.RegString(machine,k.Policy,"DnsOverHttpsMode");
     if(mode!=null)return Classify(mode,env.RegString(machine,k.Policy,"DnsOverHttpsTemplates"),true);
    }
   string state=null;
   foreach(string rel in k.LocalState){state=env.ReadText(Path.Combine(env.Dir(k.Roaming?"AppData":"LocalAppData"),rel));if(state!=null)break;}
   if(state==null)return V(BypassStatus.Unknown,"NO_PROFILE");
   try{
    using(var doc=JsonDocument.Parse(state)){
     if(doc.RootElement.ValueKind==JsonValueKind.Object&&doc.RootElement.TryGetProperty("dns_over_https",out var d)&&d.ValueKind==JsonValueKind.Object){
      string mode=d.TryGetProperty("mode",out var m)&&m.ValueKind==JsonValueKind.String?m.GetString():null;
      string tpl=d.TryGetProperty("templates",out var t)&&t.ValueKind==JsonValueKind.String?t.GetString():null;
      if(mode!=null||!string.IsNullOrWhiteSpace(tpl)){var r=Classify(mode,tpl,false);if(r.Status==BypassStatus.Bypasses||k.Full)return r;return V(BypassStatus.Unknown,"NOT_VERIFIED");}
     }
    }
   }catch{return V(BypassStatus.Unknown,"NO_PROFILE");}
   return k.Full?V(BypassStatus.Follows,"NO_CUSTOM_DNS"):V(BypassStatus.Unknown,"NOT_VERIFIED");
  }

  // Firefox: policy (registry, then policies.json next to the program), then the user's prefs.js of the default profile.
  // network.trr.mode: 2 = secure DNS first, 3 = secure DNS only; 0 and 5 = off. Without any of them Firefox may switch it on by itself (a regional roll-out), so that is "unknown".
  static BrowserVerdict EvaluateFirefox(BrowserInfo b,Known k,IBrowserEnv env) {
   if(k.Policy!=null)foreach(bool machine in new[]{true,false}){
    int? en=env.RegDword(machine,k.Policy,"Enabled");
    if(en.HasValue)return en.Value!=0?V(BypassStatus.Bypasses,"POLICY_ON",true):V(BypassStatus.Follows,"POLICY_OFF",true);
   }
   string pj=env.ReadText(Path.Combine(Path.GetDirectoryName(b.Exe)??"","distribution","policies.json"));
   if(pj!=null){
    try{
     using(var doc=JsonDocument.Parse(pj))
      if(doc.RootElement.TryGetProperty("policies",out var ps)&&ps.ValueKind==JsonValueKind.Object&&ps.TryGetProperty("DNSOverHTTPS",out var d)&&d.ValueKind==JsonValueKind.Object&&d.TryGetProperty("Enabled",out var e)&&(e.ValueKind==JsonValueKind.True||e.ValueKind==JsonValueKind.False))
       return e.ValueKind==JsonValueKind.True?V(BypassStatus.Bypasses,"POLICY_ON",true):V(BypassStatus.Follows,"POLICY_OFF",true);
    }catch{}
   }
   string root=Path.Combine(env.Dir("AppData"),k.FirefoxRoot);
   string profile=FirefoxProfileDir(env.ReadText(Path.Combine(root,"profiles.ini")),root);
   if(profile==null)return V(BypassStatus.Unknown,"NO_PROFILE");
   string prefs=env.ReadText(Path.Combine(profile,"prefs.js"));
   if(prefs==null)return V(BypassStatus.Unknown,"NO_PROFILE");
   var m=Regex.Matches(prefs,@"user_pref\(\s*""network\.trr\.mode""\s*,\s*(\d+)\s*\)\s*;");
   if(m.Count==0)return V(BypassStatus.Unknown,"DEFAULT_UNKNOWN");
   if(!int.TryParse(m[m.Count-1].Groups[1].Value,out int mode))return V(BypassStatus.Unknown,"DEFAULT_UNKNOWN");
   if(mode==2||mode==3)return V(BypassStatus.Bypasses,"USER_ON");
   if(mode==0||mode==5)return V(BypassStatus.Follows,"USER_OFF");
   return V(BypassStatus.Unknown,"DEFAULT_UNKNOWN");
  }

  // profiles.ini: an [Install...] section's Default= names the profile that install uses; otherwise the [ProfileN] with Default=1; otherwise the first one.
  public static string FirefoxProfileDir(string ini,string root) {
   if(ini==null)return null;
   var sections=new List<Dictionary<string,string>>();var names=new List<string>();Dictionary<string,string> cur=null;
   foreach(string raw in ini.Replace("\r","").Split('\n')){
    string line=raw.Trim();
    if(line.StartsWith("[")&&line.EndsWith("]")){cur=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);sections.Add(cur);names.Add(line.Substring(1,line.Length-2));}
    else if(cur!=null){int eq=line.IndexOf('=');if(eq>0)cur[line.Substring(0,eq).Trim()]=line.Substring(eq+1).Trim();}
   }
   string Resolve(string path,bool rel)=>rel?Path.Combine(root,path.Replace('/','\\')):path.Replace('/','\\');
   for(int i=0;i<sections.Count;i++)if(names[i].StartsWith("Install",StringComparison.OrdinalIgnoreCase)&&sections[i].TryGetValue("Default",out var d)&&d.Length>0)return Resolve(d,!Path.IsPathRooted(d));
   var profiles=new List<Dictionary<string,string>>();
   for(int i=0;i<sections.Count;i++)if(names[i].StartsWith("Profile",StringComparison.OrdinalIgnoreCase)&&sections[i].ContainsKey("Path"))profiles.Add(sections[i]);
   var pick=profiles.FirstOrDefault(p=>p.TryGetValue("Default",out var df)&&df=="1")??profiles.FirstOrDefault();
   return pick==null?null:Resolve(pick["Path"],!pick.TryGetValue("IsRelative",out var ir)||ir!="0");
  }

  // The browser's own icon for the list; null when Windows cannot give one (the list then draws a plain mark).
  public static Bitmap IconOf(string exe) {
   try{using(var ic=Icon.ExtractAssociatedIcon(exe)){return ic?.ToBitmap();}}catch{return null;}
  }
 }
}
