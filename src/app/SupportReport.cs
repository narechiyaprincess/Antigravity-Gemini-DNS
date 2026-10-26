using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AntigravityGeminiDns {
 // What must never leave the computer inside a support report. Values come from the machine (live) or from a test.
 public sealed class RedactionContext {
  public string UserName,MachineName,DomainName,ProfileDir;
  public string[] ExtraPaths=new string[0];     // other personal folders (redirected Documents, Desktop, temp ...)
  public string[] Ssids=new string[0];

  public static RedactionContext Live(Func<string[]> ssids=null) {
   string F(Environment.SpecialFolder f){try{return Environment.GetFolderPath(f);}catch{return null;}}
   string profile=Environment.GetEnvironmentVariable("USERPROFILE")??F(Environment.SpecialFolder.UserProfile);
   var extra=new[]{F(Environment.SpecialFolder.LocalApplicationData),F(Environment.SpecialFolder.ApplicationData),F(Environment.SpecialFolder.MyDocuments),F(Environment.SpecialFolder.DesktopDirectory),F(Environment.SpecialFolder.MyPictures),F(Environment.SpecialFolder.MyMusic),F(Environment.SpecialFolder.MyVideos),Path.GetTempPath(),Environment.GetEnvironmentVariable("TEMP"),Environment.GetEnvironmentVariable("TMP"),Environment.GetEnvironmentVariable("OneDrive")}
    .Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.TrimEnd('\\','/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
   string[] names;try{names=(ssids??SupportReport.ReadSsids)()??new string[0];}catch{names=new string[0];}
   return new RedactionContext{UserName=Environment.UserName,MachineName=Environment.MachineName,DomainName=Environment.UserDomainName,ProfileDir=profile,ExtraPaths=extra,Ssids=names};
  }
 }

 public sealed class OperationEntry {
  public string Utc,Action,Code,Profile;public bool Ok;
 }

 public sealed class ReportInput {
  public DateTime GeneratedUtc;public string ExeVersion,CatalogVersion,CatalogUpdated,Os;
  public string JournalPhase,Profile;public string HealthKind,HealthCode,HealthUtc;
  public OperationEntry[] Operations=new OperationEntry[0];
  public bool CustomProfile;       // a profile the user entered: its name, host and addresses stay out of the report
  public string DiagnosisJson;     // the engine's `diagnose` output, or null when it was not run / failed
  public string DiagnosisCode;
  public string DoctorText;        // Doctor.ToText() of this run, or null when Doctor was not run
 }

 // Bounded local history of operations (code only, never output text): %LocalAppData%\AntigravityDnsUnlocker\operations.json, the last MaxEntries entries.
 public sealed class OperationHistory {
  public const int MaxEntries=50;
  readonly string path;readonly object gate=new object();
  static readonly JsonSerializerOptions Json=new JsonSerializerOptions{WriteIndented=true,IncludeFields=true};
  public OperationHistory(string path=null){this.path=path??DefaultPath;}
  public static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),AppIdentity.StorageName,"operations.json");
  static string Clip(string s,int n)=>s==null?"":(s.Length>n?s.Substring(0,n):s);

  public OperationEntry[] Load() {
   lock(gate) {
    try{if(!File.Exists(path))return new OperationEntry[0];var list=JsonSerializer.Deserialize<List<OperationEntry>>(File.ReadAllText(path,Encoding.UTF8),Json);return (list??new List<OperationEntry>()).Where(e=>e!=null).ToArray();}catch{return new OperationEntry[0];}
   }
  }
  public void Append(string action,string code,bool ok,string profile,DateTime? utc=null) {
   lock(gate) {
    try {
     var list=Load().ToList();
     list.Add(new OperationEntry{Utc=(utc??DateTime.UtcNow).ToString("o"),Action=Clip(action,24),Code=Clip(code,64),Ok=ok,Profile=Clip(profile,64)});
     if(list.Count>MaxEntries)list=list.Skip(list.Count-MaxEntries).ToList();
     Directory.CreateDirectory(Path.GetDirectoryName(path));
     string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
     File.WriteAllText(tmp,JsonSerializer.Serialize(list,Json),new UTF8Encoding(false));
     if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);
    }catch{}   // history is a convenience; it must never break an operation
   }
  }
 }

 public static class SupportReport {
  public const int ReportOperations=20;
  public const string UserTag="<user>",ComputerTag="<computer>",SsidTag="<ssid>",ProfileTag="<profile>",PathTag="<personal-path>";
  static readonly string[] Tags={UserTag,ComputerTag,SsidTag,ProfileTag,PathTag};
  const string TokenL=@"(?<![\p{L}\p{N}_])",TokenR=@"(?![\p{L}\p{N}_])";

  // A literal path whose separators match "\", "\\" (JSON-escaped) or "/" in the text.
  static string PathPattern(string path) {
   var sb=new StringBuilder();
   foreach(char c in path.TrimEnd('\\','/')) sb.Append(c=='\\'||c=='/'?@"(?:\\{1,2}|/)":Regex.Escape(c.ToString()));
   return sb.ToString();
  }

  // Pure and idempotent: Sanitize(Sanitize(x))==Sanitize(x). Placeholders already in the text are set aside first so that a name such as "user" cannot eat "<user>".
  public static string Sanitize(string text,RedactionContext ctx) {
   if(string.IsNullOrEmpty(text)||ctx==null)return text??"";
   string Mask(string t){foreach(string tag in Tags){t=t.Replace(tag,"\uE000"+Array.IndexOf(Tags,tag)+"\uE001");}return t;}
   string Unmask(string t){return Regex.Replace(t,"\uE000(\\d)\uE001",m=>Tags[int.Parse(m.Groups[1].Value)]);}
   // Everything this pass inserts is a sentinel, too, so no later step can match inside a placeholder; the tags are written back at the end.
   string Sent(string tag)=>"\uE000"+Array.IndexOf(Tags,tag)+"\uE001";
   string result=Mask(text);

   // 1. Whole folders first, longest first: the profile folder, redirected personal folders. Whatever follows the folder stays (the file name is useful, the user is not).
   var folders=new List<(string Value,string Tag)>();
   if(!string.IsNullOrWhiteSpace(ctx.ProfileDir))folders.Add((ctx.ProfileDir,ProfileTag));
   foreach(string p in ctx.ExtraPaths??new string[0])if(!string.IsNullOrWhiteSpace(p))folders.Add((p,PathTag));
   foreach(var f in folders.OrderByDescending(x=>x.Value.Length)) {
    // a folder is followed by a separator or the end of the token, so "C:\Users\ivan" does not eat "C:\Users\ivanov"
    result=Regex.Replace(result,"(?<![\\p{L}\\p{N}_])"+PathPattern(f.Value)+"(?![\\p{L}\\p{N}_])",Sent(f.Tag),RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
   }
   // 2. Any other account folder under \Users\ (another profile name inside a log line); the placeholder and the shared folders stay.
   result=Regex.Replace(result,@"(?i)(?<=[A-Za-z]:(?:\\{1,2}|/)Users(?:\\{1,2}|/))(?!(?:Public|Default|Default User|All Users)(?![^\\/""'\s<>|:*?]))[^\\/:*?""<>|\s\uE000\uE001]+",Sent(UserTag));
   // 3. Names as whole tokens (so "ab" is not cut out of "table"), in one pass, longest first; the first kind wins when two names are equal.
   var names=new List<(string Value,string Tag)>();
   void Add(string v,string tag){if(!string.IsNullOrWhiteSpace(v)&&v.Trim().Length>=2)names.Add((v.Trim(),tag));}
   Add(ctx.UserName,UserTag);Add(ctx.MachineName,ComputerTag);Add(ctx.DomainName,ComputerTag);
   foreach(string s in ctx.Ssids??new string[0])Add(s,SsidTag);
   names=names.GroupBy(n=>n.Value,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).OrderByDescending(n=>n.Value.Length).ToList();
   if(names.Count>0) {
    var map=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(var n in names)map[n.Value]=n.Tag;
    // JSON-escaped variants of a name (backslash, quote) are matched as well
    string alt=string.Join("|",names.Select(n=>Regex.Escape(n.Value)));
    result=Regex.Replace(result,TokenL+"(?:"+alt+")"+TokenR,m=>map.TryGetValue(m.Value,out var tag)?Sent(tag):m.Value,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
   }
   return Unmask(result);
  }

  public static string[] ReadSsids() {
   try {
    var psi=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"netsh.exe"),"wlan show interfaces"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8};
    using(var p=Process.Start(psi)) {
     var outText=p.StandardOutput.ReadToEndAsync();p.StandardError.ReadToEndAsync();
     if(!p.WaitForExit(5000)){try{p.Kill();}catch{}return new string[0];}
     return ParseSsids(outText.Result);
    }
   }catch{return new string[0];}
  }
  // "    SSID                   : Name" (not BSSID); the label is "SSID" in every Windows language.
  public static string[] ParseSsids(string netshOutput) {
   var list=new List<string>();
   foreach(string line in (netshOutput??"").Split('\n')) {
    var m=Regex.Match(line,@"^\s*SSID\s*:\s*(.+?)\s*$");
    if(m.Success&&m.Groups[1].Value.Length>0)list.Add(m.Groups[1].Value);
   }
   return list.Distinct().ToArray();
  }

  static string Row(string label,string value)=>label.PadRight(26)+(string.IsNullOrWhiteSpace(value)?"—":value);

  // The report text before redaction. Codes and versions only; the diagnosis is summarised, not pasted.
  public static string Build(ReportInput r) {
   var sb=new StringBuilder();
   sb.AppendLine(L.T("Отчёт для поддержки — Antigravity-Gemini DNS"));
   sb.AppendLine(L.T("Создан локально; никуда не отправляется. Имя пользователя, имя компьютера, личные папки и имена сетей вырезаны."));
   sb.AppendLine();
   sb.AppendLine(L.T("== Программа =="));
   sb.AppendLine(Row(L.T("Версия EXE:"),r.ExeVersion));
   sb.AppendLine(Row(L.T("Версия каталога:"),r.CatalogVersion+(string.IsNullOrWhiteSpace(r.CatalogUpdated)?"":L.T(" (от ")+r.CatalogUpdated+")")));
   sb.AppendLine(Row("Windows:",r.Os));
   sb.AppendLine(Row(L.T("Создан (UTC):"),r.GeneratedUtc.ToString("yyyy-MM-dd HH:mm")));
   sb.AppendLine();
   sb.AppendLine(L.T("== Состояние =="));
   sb.AppendLine(Row(L.T("Журнал:"),r.JournalPhase));
   sb.AppendLine(Row(L.T("Профиль:"),r.Profile));
   sb.AppendLine(Row(L.T("Проверка моста:"),string.IsNullOrWhiteSpace(r.HealthKind)?null:r.HealthKind+(string.IsNullOrWhiteSpace(r.HealthCode)?"":" / "+r.HealthCode)+(string.IsNullOrWhiteSpace(r.HealthUtc)?"":" ("+r.HealthUtc+")")));
   sb.AppendLine();
   sb.AppendLine(L.T("== Последние операции (не больше ")+ReportOperations+L.T(", новые внизу) =="));
   var ops=(r.Operations??new OperationEntry[0]).Skip(Math.Max(0,(r.Operations??new OperationEntry[0]).Length-ReportOperations)).ToArray();
   if(ops.Length==0)sb.AppendLine(L.T("  (нет записей)"));
   foreach(var o in ops)sb.AppendLine("  "+(o.Utc??"?")+"  "+(o.Action??"?").PadRight(10)+(o.Ok?"ok  ":"FAIL")+"  "+(o.Code??"")+(string.IsNullOrWhiteSpace(o.Profile)?"":"  ["+o.Profile+"]"));
   sb.AppendLine();
   sb.AppendLine(L.T("== Диагностика =="));
   AppendDiagnosis(sb,r);
   // Doctor layers (Doctor.ToText): the first failed layer is where the problem is; Prepare() redacts this text like the rest
   sb.AppendLine();
   sb.AppendLine(L.T("== Самодиагностика по слоям =="));
   sb.AppendLine(string.IsNullOrWhiteSpace(r.DoctorText)?L.T("  не выполнена"):r.DoctorText.TrimEnd());
   return sb.ToString();
  }

  static void AppendDiagnosis(StringBuilder sb,ReportInput r) {
   if(string.IsNullOrWhiteSpace(r.DiagnosisJson)){sb.AppendLine(L.T("  Диагностика не выполнена")+(string.IsNullOrWhiteSpace(r.DiagnosisCode)?"":L.T(" (код: ")+r.DiagnosisCode+")")+".");return;}
   try {
    using(var doc=JsonDocument.Parse(r.DiagnosisJson)) {
     var root=doc.RootElement;
     string S(JsonElement e,string n)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(n,out var v)?(v.ValueKind==JsonValueKind.String?v.GetString():v.ToString()):null;
     // A custom profile is recognised by the journal too: its "resolver" is the user's own DoH URL, whatever the profile list shows now.
     bool custom=r.CustomProfile||(root.TryGetProperty("Status",out var st0)&&(S(st0,"Resolver")??"").Contains("/"));
     sb.AppendLine("  "+Row(L.T("Итог:"),S(root,"State")+" / "+S(root,"Code")));
     if(root.TryGetProperty("Status",out var st)&&st.ValueKind==JsonValueKind.Object) {
      sb.AppendLine("  "+Row(L.T("Профиль:"),custom?L.T("свой профиль"):S(st,"Resolver")));
      sb.AppendLine("  "+Row(L.T("Протокол:"),S(st,"Protocol")));
      sb.AppendLine("  "+Row(L.T("Журнал:"),S(st,"JournalPhase")));
      if(st.TryGetProperty("Servers",out var sv)&&sv.ValueKind==JsonValueKind.Array)sb.AppendLine("  "+Row(L.T("Адреса провайдера:"),custom?sv.GetArrayLength()+L.T(" шт. (скрыты: свой профиль)"):string.Join(", ",sv.EnumerateArray().Select(x=>x.GetString()))));
     }
     sb.AppendLine("  "+Row(L.T("NRPT и ответы Windows:"),S(root,"NrptEffective")));
     if(root.TryGetProperty("SystemResolver",out var sr)&&sr.ValueKind==JsonValueKind.Array)
      foreach(var h in sr.EnumerateArray()) {
       string a=h.TryGetProperty("A",out var av)&&av.ValueKind==JsonValueKind.Array?string.Join(", ",av.EnumerateArray().Select(x=>x.GetString())):"";
       sb.AppendLine("  "+Row("Windows → "+S(h,"Host"),custom?(a.Length==0?"":L.T("адреса скрыты: свой профиль")):a));
      }
     if(root.TryGetProperty("Network",out var net)&&net.ValueKind==JsonValueKind.Object&&net.TryGetProperty("Candidates",out var cs)&&cs.ValueKind==JsonValueKind.Array)
      foreach(var c in cs.EnumerateArray())sb.AppendLine("  "+Row(L.T("Провайдер ")+(custom?L.T("(свой профиль)"):S(c,"Server"))+":","DoH "+(c.TryGetProperty("DohValidated",out var dv)&&dv.ValueKind==JsonValueKind.True?"ok":L.T("нет"))+"; TLS "+S(c,"DohTls")));
     if(root.TryGetProperty("VpnPresent",out var vp)&&vp.ValueKind==JsonValueKind.Array)sb.AppendLine("  "+Row(L.T("VPN-адаптеры:"),vp.GetArrayLength().ToString()));
    }
   }catch{sb.AppendLine(L.T("  Ответ диагностики не удалось разобрать."));}
  }

  // The text the user sees and saves: built, then redacted. Applying the redaction a second time changes nothing.
  public static string Prepare(ReportInput input,RedactionContext ctx)=>Sanitize(Build(input),ctx);
 }
}
