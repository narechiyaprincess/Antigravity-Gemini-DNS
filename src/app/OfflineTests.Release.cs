using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace AntigravityGeminiDns {
 // v1.4 Final: tray colour, the configuration file and unusable settings. Clock and paths are passed in; no host state is read or written except a scratch folder.
 public static partial class OfflineTests {
  static void RunReleaseTests() {
   // D1 tray colour: the table, and that every combination lands on one of the four states
   Check(TrayColor.From(false,false,false,false,false)==TrayState.Gray,"tray: bridge off and quiet is grey");
   Check(TrayColor.From(true,false,false,false,false)==TrayState.Green,"tray: bridge on and quiet is green");
   Check(TrayColor.From(true,true,false,false,false)==TrayState.Yellow&&TrayColor.From(false,false,true,false,false)==TrayState.Yellow,"tray: a check or an operation in progress is yellow, whether the bridge is up or not");
   Check(TrayColor.From(false,false,false,true,false)==TrayState.Red&&TrayColor.From(true,false,false,false,true)==TrayState.Red,"tray: a recovery need or a repair offer is red, also while the bridge is up");
   Check(TrayColor.From(true,true,true,true,true)==TrayState.Red,"tray: a recovery need beats a running check");
   int combos=0;var seen=new HashSet<TrayState>();
   foreach(bool a in new[]{false,true})foreach(bool b in new[]{false,true})foreach(bool c in new[]{false,true})foreach(bool d in new[]{false,true})foreach(bool e in new[]{false,true}){combos++;seen.Add(TrayColor.From(a,b,c,d,e));}
   Check(combos==32&&seen.Count==4,"tray: all 32 input combinations map to the four states");
   using(var baseIcon=Branding.IconSet(true)){
    foreach(TrayState st in new[]{TrayState.Green,TrayState.Yellow,TrayState.Red,TrayState.Gray}){
     using(var icon=TrayColor.Compose(baseIcon,st,16))using(var bmp=icon.ToBitmap()){
      var px=bmp.GetPixel(12,12);var want=TrayColor.DotOf(st);
      Check(Math.Abs(px.R-want.R)<=24&&Math.Abs(px.G-want.G)<=24&&Math.Abs(px.B-want.B)<=24,"tray: the "+st+" dot is drawn in its colour at the lower right corner");
     }
    }
   }

   // D2 configuration file: what travels, what never does, and an import that either applies completely or changes nothing
   var cur=new AppSettings{Language="ru",Notify=false,CloseToTray=false,StartHidden=false,Gemini=true,Google=false,Profile="dns.comss.one",
    CustomName="Мой DoH",CustomServers="8.8.4.4",CustomTemplate="https://dns.example.test/dns-query",AutoFailover=true,
    Browser="chrome",TrayHintShown=true,PreviousCopy="C:\\old\\AntigravityGeminiDns.exe",WinX=120,WinY=80,LastAddrServers="8.8.4.4",LastAddrTemplate="https://dns.example.test/dns-query",
    SavedAddrs=new List<SavedAddr>{new SavedAddr()},Pool=new List<PoolEntry>{new PoolEntry{Id="x",Utc=DateTime.UtcNow.ToString("o")}}};
   var file=ConfigFile.Export(cur);
   foreach(var secret in new[]{"SavedAddrs","Pool","WinX","WinY","PreviousCopy","LastAddr","Browser","TrayHintShown","SettingsVersion"})
    Check(!file.Contains("\""+secret),"config: "+secret+" never leaves this computer");
   Check(file.Contains("\"Schema\": \"agdns-config\"")&&file.Contains("\"Version\": 1")&&file.Contains("\"CustomTemplate\": \"https://dns.example.test/dns-query\""),"config: the file names its schema and version and carries the own DoH");
   var fresh=new AppSettings{Language="en",Notify=true,WinX=500,WinY=300,SavedAddrs=new List<SavedAddr>{new SavedAddr()},Pool=new List<PoolEntry>(),Browser="edge",PreviousCopy="D:\\mine.exe"};
   var round=ConfigFile.Import(file,fresh);
   Check(round.Ok&&round.Applied.Language=="ru"&&!round.Applied.Notify&&round.Applied.Gemini&&round.Applied.AutoFailover&&round.Applied.Profile=="dns.comss.one"&&round.Applied.CustomTemplate=="https://dns.example.test/dns-query","config: round-trip brings the preferences back");
   Check(round.Applied.WinX==500&&round.Applied.Browser=="edge"&&round.Applied.PreviousCopy=="D:\\mine.exe"&&round.Applied.SavedAddrs.Count==1,"config: this computer's own data is kept on import, never overwritten by the file");
   Check(fresh.Language=="en"&&fresh.Notify,"config: a successful import works on a copy; the settings passed in are not changed");

   void Refused(string text,string code,string label){var r=ConfigFile.Import(text,fresh);Check(!r.Ok&&r.Code==code&&r.Applied==null&&fresh.Language=="en","config: "+label);}
   Refused("{ not json","NOT_JSON","broken JSON is refused and changes nothing");
   Refused("{\"Schema\":\"other\",\"Version\":1,\"Settings\":{}}","NOT_A_CONFIG","a file of another schema is refused");
   Refused("{\"Schema\":\"agdns-config\",\"Version\":2,\"Settings\":{\"Language\":\"ru\"}}","FUTURE_VERSION","a file from a newer version is refused, nothing is guessed");
   Refused("{\"Schema\":\"agdns-config\",\"Settings\":{}}","BAD_VERSION","a file without a version is refused");
   Refused("{\"Schema\":\"agdns-config\",\"Version\":1}","BAD_SETTINGS","a file without a settings block is refused");
   Refused("{\"Schema\":\"agdns-config\",\"Version\":1,\"Settings\":{\"Notify\":\"yes\"}}","BAD_SETTINGS","a wrong value type is refused");
   foreach(var field in new[]{"Language","Profile","CustomName","CustomServers","CustomTemplate"})foreach(var invalid in new[]{"123","true","{}","[]"}){
    Refused("{\"Schema\":\"agdns-config\",\"Version\":1,\"Settings\":{\"Notify\":false,\""+field+"\":"+invalid+"}}","BAD_SETTINGS","wrong string type in "+field+" refuses the entire import");
    Check(fresh.Notify,"config: malformed string field leaves existing preferences intact");
   }
   Check(ConfigFile.Import("{\"Schema\":\"agdns-config\",\"Version\":1,\"Settings\":{\"Language\":null}}",fresh).Ok,"config: legacy null optional string retains compatibility");
   var nullCustom=new AppSettings{CustomName=null,CustomServers=null,CustomTemplate=null};
   var partialNull=ConfigFile.Import("{\"Schema\":\"agdns-config\",\"Version\":1,\"Settings\":{}}",nullCustom);
   Check(partialNull.Ok&&partialNull.Applied.CustomTemplate==""&&nullCustom.CustomTemplate==null,"config: partial import normalizes inherited null custom fields without changing current settings");
   var templateOnly=ConfigFile.Import("{\"Schema\":\"agdns-config\",\"Version\":1,\"Settings\":{\"CustomTemplate\":\"https://dns.example.test/dns-query\"}}",nullCustom);
   Check(templateOnly.Ok&&templateOnly.Applied.CustomServers=="","config: template import accepts null inherited server list as empty");
   Refused("{\"Schema\":\"agdns-config\",\"Version\":1,\"Settings\":{\"Language\":\"fr\"}}","BAD_LANGUAGE","an unknown language is refused");
   Refused("{\"Schema\":\"agdns-config\",\"Version\":1,\"Settings\":{\"CustomTemplate\":\"http://dns.example.test/dns-query\"}}","BAD_TEMPLATE","an own DoH without https is refused by the same check as the window");
   Refused("{\"Schema\":\"agdns-config\",\"Version\":1,\"Settings\":{\"CustomTemplate\":\"https://192.0.2.5/dns-query\"}}","BAD_TEMPLATE","an own DoH given as an IP address is refused");
   Refused("{\"Schema\":\"agdns-config\",\"Version\":1,\"Settings\":{\"CustomTemplate\":\"https://dns.example.test/dns-query\",\"CustomServers\":\"10.0.0.5\"}}","BAD_TEMPLATE","a private address in the own DoH is refused");

   // D3 unusable settings: defaults, the reason named, nothing thrown
   var okParse=SettingsRecovery.Parse("{\"SettingsVersion\":1,\"Notify\":false,\"Pool\":[{\"Id\":\"a\",\"Utc\":\"2026-10-14T12:00:00.0000000Z\"}]}");
   Check(okParse.Settings!=null&&okParse.Reason==null&&!okParse.Settings.Notify&&okParse.Settings.Pool.Count==1,"settings: a valid file is read as it is");
   Check(SettingsRecovery.Parse("{ broken").Reason=="BROKEN"&&SettingsRecovery.Parse("[1,2]").Reason=="BROKEN"&&SettingsRecovery.Parse("{\"Notify\":\"yes\"}").Reason=="BROKEN","settings: broken JSON, a non-object and a wrong value type are BROKEN");
   Check(SettingsRecovery.Parse("{\"SettingsVersion\":2,\"Notify\":false}").Reason=="FUTURE_VERSION"&&SettingsRecovery.Parse("{\"SettingsVersion\":2}").Settings==null,"settings: a file from a newer version gives no settings and says FUTURE_VERSION");
   Check(SettingsRecovery.Parse("").Reason=="EMPTY"&&SettingsRecovery.Parse(null).Settings==null,"settings: an empty file gives no settings");
   var scratch=Path.Combine(Path.GetTempPath(),"agdns-release-"+Guid.NewGuid().ToString("N"));
   try{
    Directory.CreateDirectory(scratch);
    var path=Path.Combine(scratch,"settings.json");File.WriteAllText(path,"{ broken");
    string blocked=Path.Combine(scratch,"blocked");File.WriteAllText(blocked,"preserve");
    var failedImport=ConfigFile.ImportAndSave(file,fresh,s=>s.SaveTo(Path.Combine(blocked,"settings.json")));
    Check(!failedImport.Ok&&failedImport.Code=="SAVE_FAILED"&&failedImport.Applied==null&&File.ReadAllText(blocked)=="preserve"&&fresh.Notify,"config: actual filesystem persistence failure refuses success and preserves existing data");
    string imported=Path.Combine(scratch,"imported.json");
    var savedImport=ConfigFile.ImportAndSave(file,fresh,s=>s.SaveTo(imported));
    Check(savedImport.Ok&&SettingsRecovery.Parse(File.ReadAllText(imported)).Settings.Language=="ru","config: success follows confirmed persistence");
    SettingsRecovery.Quarantine(path,"BROKEN",new DateTime(2026,10,15,12,0,0,DateTimeKind.Utc));
    var copy=Path.Combine(scratch,"settings.json.broken-20261015-120000");
    Check(File.Exists(copy)&&File.ReadAllText(copy)=="{ broken"&&File.Exists(path),"settings: the unusable file is kept as a dated copy and stays in place");
    Check(File.ReadAllText(Path.Combine(scratch,"settings-recovery.log")).Contains("BROKEN"),"settings: the reason is written to settings-recovery.log");
   }finally{try{Directory.Delete(scratch,true);}catch{}}
  }
 }
}
