using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AntigravityGeminiDns {
 // v1.4 configuration in one file. Only preferences travel: what this computer did (journal, history, logs, saved addresses, the measured pool,
 // the crash marker, window position, the browser found here, the previous autostart copy) never leaves it and is never imported.
 // Import is all or nothing: a broken file, an unknown schema, a future version or a template the UI would refuse changes nothing.
 public static class ConfigFile {
  public const string Schema="agdns-config";
  public const int Version=1;

  public sealed class ImportResult {
   public bool Ok;public string Code;public string Text;
   public AppSettings Applied;   // the settings after the import (only when Ok)
  }

  // the preferences as a file: {"Schema":"agdns-config","Version":1,"Settings":{...}}
  public static string Export(AppSettings s) {
   string selection=s.ProfileIdentityVersion<1&&!string.IsNullOrWhiteSpace(s.CustomTemplate)&&string.Equals((s.Profile??" ").Trim(),(s.CustomName??" ").Trim(),StringComparison.OrdinalIgnoreCase)?AppSettings.CustomProfileId:s.Profile;
   var prefs=new {Language=s.Language,Notify=s.Notify,CloseToTray=s.CloseToTray,StartHidden=s.StartHidden,Core=s.Core,Gemini=s.Gemini,Google=s.Google,
    Profile=selection,ProfileIdentityVersion=1,CustomName=s.CustomName,CustomServers=s.CustomServers,CustomTemplate=s.CustomTemplate,AutoFailover=s.AutoFailover};
   return JsonSerializer.Serialize(new{Schema,Version,Settings=prefs},Program.JsonOptions);
  }

  // json: the file's text. current: the settings this computer has now; they are copied, never changed in place.
  public static ImportResult Import(string json,AppSettings current) {
   JsonElement root;
   try{using(var doc=JsonDocument.Parse(json??"")){root=doc.RootElement.Clone();}}
   catch{return Fail("NOT_JSON",L.T("Файл не читается: это не JSON."));}
   if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("Schema",out var sc)||sc.ValueKind!=JsonValueKind.String||sc.GetString()!=Schema)
    return Fail("NOT_A_CONFIG",L.T("Это не файл настроек Antigravity-Gemini DNS."));
   if(!root.TryGetProperty("Version",out var vv)||vv.ValueKind!=JsonValueKind.Number||!vv.TryGetInt32(out int version)||version<1)
    return Fail("BAD_VERSION",L.T("Не удалось определить версию файла настроек."));
   if(version>Version)return Fail("FUTURE_VERSION",L.T("Файл создан более новой версией программы. Настройки не изменены."));
   if(!root.TryGetProperty("Settings",out var st)||st.ValueKind!=JsonValueKind.Object)return Fail("BAD_SETTINGS",L.T("В файле нет блока настроек."));

   foreach(var field in new[]{"Language","Profile","CustomName","CustomServers","CustomTemplate"})
    if(st.TryGetProperty(field,out var value)&&value.ValueKind!=JsonValueKind.String&&value.ValueKind!=JsonValueKind.Null)
     return Fail("BAD_SETTINGS",L.T("Неверный тип значения в файле: ")+field);
   var next=Clone(current);
   string lang=Str(st,"Language");
   if(lang!=null){if(lang!="ru"&&lang!="en")return Fail("BAD_LANGUAGE",L.T("Неизвестный язык в файле."));next.Language=lang;}
   foreach(var flag in new[]{"Notify","CloseToTray","StartHidden","Core","Gemini","Google","AutoFailover"}){
    var v=st.TryGetProperty(flag,out var fv)?fv:default;
    if(v.ValueKind==JsonValueKind.True||v.ValueKind==JsonValueKind.False)Set(next,flag,v.GetBoolean());
    else if(v.ValueKind!=JsonValueKind.Undefined)return Fail("BAD_SETTINGS",L.T("Неверный тип значения в файле: ")+flag);
   }
   if(!st.TryGetProperty("Core",out _)&&next.Google)next.Gemini=true;   // files written before the services were independent: the browser level always carried Gemini
   if(!next.Core&&!next.Gemini&&!next.Google)next.Core=true;   // at least one service stays selected
   string profile=Str(st,"Profile");
   if(profile!=null){
    // a catalog name or the own profile's name (which is not in the catalog): any short name is kept, the window falls back to the default when it is unknown
    if(profile.Length>64||profile.Any(char.IsControl))return Fail("BAD_PROFILE",L.T("Неверное имя профиля в файле."));
    next.Profile=profile.Length==0?ProfileCatalog.BuiltIn.Default:profile;
   }
   string name=Str(st,"CustomName")??next.CustomName??"",servers=Str(st,"CustomServers")??next.CustomServers??"",template=Str(st,"CustomTemplate")??next.CustomTemplate??"";
   if(template.Length>0){
    // the same check as the own-provider panel: https:// with a host name, a short name, at most four public addresses
    if(!MainForm.TryParseCustom(name,servers,template,out _,out var tmpl,out var err,out var finalName))return Fail("BAD_TEMPLATE",err);
    next.CustomName=finalName;next.CustomServers=servers.Trim();next.CustomTemplate=tmpl;
   }else{next.CustomName="";next.CustomServers="";next.CustomTemplate="";}
   next.ProfileIdentityVersion=st.TryGetProperty("ProfileIdentityVersion",out var identity)&&identity.ValueKind==JsonValueKind.Number&&identity.TryGetInt32(out var iv)?iv:0;
   next.NormalizeProfileIdentity();
   return new ImportResult{Ok=true,Code="OK",Text=L.T("Настройки импортированы. Мост не подключён, автозапуск не изменён."),Applied=next};
  }

  internal static ImportResult ImportAndSave(string json,AppSettings current,Action<AppSettings> save) {
   var result=Import(json,current);
   if(!result.Ok)return result;
   try{save(result.Applied);return result;}
   catch(IOException){return Fail("SAVE_FAILED",L.T("Не удалось сохранить настройки."));}
   catch(UnauthorizedAccessException){return Fail("SAVE_FAILED",L.T("Не удалось сохранить настройки."));}
  }
  static ImportResult Fail(string code,string text)=>new ImportResult{Ok=false,Code=code,Text=text};
  static string Str(JsonElement e,string name)=>e.TryGetProperty(name,out var v)?(v.ValueKind==JsonValueKind.String?v.GetString():null):null;

  static void Set(AppSettings s,string name,bool value) {
   switch(name){
    case "Notify":s.Notify=value;break;case "CloseToTray":s.CloseToTray=value;break;case "StartHidden":s.StartHidden=value;break;
    case "Core":s.Core=value;break;case "Gemini":s.Gemini=value;break;case "Google":s.Google=value;break;case "AutoFailover":s.AutoFailover=value;break;
   }
  }

  // a copy with the same preferences; the fields that never travel are left at their defaults here and kept by the caller
  static AppSettings Clone(AppSettings c)=>new AppSettings{
   Language=c.Language,Notify=c.Notify,CloseToTray=c.CloseToTray,StartHidden=c.StartHidden,Core=c.Core,Gemini=c.Gemini,Google=c.Google,Profile=c.Profile,ProfileIdentityVersion=c.ProfileIdentityVersion,
   CustomName=c.CustomName,CustomServers=c.CustomServers,CustomTemplate=c.CustomTemplate,AutoFailover=c.AutoFailover,
   Browser=c.Browser,TrayHintShown=c.TrayHintShown,PreviousCopy=c.PreviousCopy,WinX=c.WinX,WinY=c.WinY,LastAddrSource=c.LastAddrSource,
   LastAddrTime=c.LastAddrTime,LastAddrServers=c.LastAddrServers,LastAddrTemplate=c.LastAddrTemplate,SavedAddrs=c.SavedAddrs,Pool=c.Pool};
 }
}

namespace AntigravityGeminiDns {
 // v1.4 settings that cannot be used (broken JSON, wrong types, a version this program does not know): the defaults are used, the file is kept
 // as a copy next to the settings and the reason goes to settings-recovery.log. Nothing here throws into the program.
 public static class SettingsRecovery {
  public const int CurrentVersion=1;
  public sealed class Parsed {
   public AppSettings Settings;public string Reason;   // Settings is null when the file cannot be used; Reason says why (BROKEN, FUTURE_VERSION, EMPTY)
  }

  public static Parsed Parse(string json) {
   if(string.IsNullOrWhiteSpace(json))return new Parsed{Reason="EMPTY"};
   try{
    using(var doc=JsonDocument.Parse(json)){
     var root=doc.RootElement;
     if(root.ValueKind!=JsonValueKind.Object)return new Parsed{Reason="BROKEN"};
     if(root.TryGetProperty("SettingsVersion",out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out int ver)&&ver>CurrentVersion)return new Parsed{Reason="FUTURE_VERSION"};
    }
    var s=JsonSerializer.Deserialize<AppSettings>(json,Program.JsonOptions)??new AppSettings();
    if(s.SavedAddrs==null)s.SavedAddrs=new System.Collections.Generic.List<SavedAddr>();
    if(s.Pool==null)s.Pool=new System.Collections.Generic.List<PoolEntry>();
    s.NormalizeProfileIdentity();
    return new Parsed{Settings=s};
   }catch{return new Parsed{Reason="BROKEN"};}
  }

  // the unusable file stays where it is and gets a dated copy; the reason is appended to settings-recovery.log next to it
  public static void Quarantine(string path,string reason,DateTime nowUtc) {
   try{
    if(!File.Exists(path))return;
    string stamp=nowUtc.ToString("yyyyMMdd-HHmmss");
    string copy=path+".broken-"+stamp;
    File.Copy(path,copy,true);
    File.AppendAllText(Path.Combine(Path.GetDirectoryName(path),"settings-recovery.log"),stamp+" "+reason+" -> "+Path.GetFileName(copy)+Environment.NewLine);
   }catch{}
  }
 }
}
