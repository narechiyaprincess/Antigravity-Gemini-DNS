using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AntigravityGeminiDns {
 // Crash Guard (v1.1): a per-user marker that says whether the last window session ended cleanly. It changes no system state.
 // What it adds is the knowledge that a session died in the middle of a DNS change, which the window then shows with the journal's restore path.
 // The marker lives under %LOCALAPPDATA%: the window runs unelevated and cannot write the protected %ProgramData% state directory.
 public static class CrashGuard {
  public enum Kind { None, BridgeActive, InterruptedChange }

  public sealed class Report {
   public bool PreviousUnclean;
   public string JournalPhase;
   public Kind Kind;
  }

  // Set once by Program.Main before the window opens; null when the guard did not run (tests, CLI).
  public static Report Current;

  public static string MarkerPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),AppIdentity.StorageName,"session.json");

  // Pure decision. markerJson = session.json (null when absent), journalJson = state.json (null when absent).
  // An unreadable marker counts as unclean: the window then tells the user what the journal says instead of staying silent.
  public static Report Evaluate(string markerJson,string journalJson) {
   var r=new Report{JournalPhase=Recovery.PhaseOfJournal(journalJson),PreviousUnclean=IsUnclean(markerJson)};
   if(!r.PreviousUnclean){r.Kind=Kind.None;return r;}
   switch(r.JournalPhase){
    case "Active": r.Kind=Kind.BridgeActive;break;
    case "Applying": case "Restoring": case "Failed": r.Kind=Kind.InterruptedChange;break;
    default: r.Kind=Kind.None;break;   // no journal, or already Restored: nothing to recover
   }
   return r;
  }

  public static bool IsUnclean(string markerJson) {
   if(markerJson==null)return false;   // first start on this account
   try{
    using(var doc=JsonDocument.Parse(markerJson)){
     return !(doc.RootElement.TryGetProperty("CleanShutdown",out var c)&&c.ValueKind==JsonValueKind.True);
    }
   }catch{return true;}
  }

  // Called once at startup: judges the previous session, then marks this one as running (unclean until MarkClean).
  public static Report Start() {
   var report=Evaluate(ReadText(MarkerPath),Recovery.ReadJournal());
   Current=report;
   WriteMarker(false);
   return report;
  }

  // Called after the window has closed normally (exit from the tray menu, Windows shutdown). A crash never reaches it.
  public static void MarkClean()=>WriteMarker(true);

  static void WriteMarker(bool clean) {
   try{
    string path=MarkerPath;
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    string json=JsonSerializer.Serialize(new{CleanShutdown=clean,Version=AppInfo.Version,UpdatedUtc=DateTime.UtcNow.ToString("o")});
    string temp=path+".tmp";
    File.WriteAllText(temp,json,new UTF8Encoding(false));
    File.Move(temp,path,true);
   }catch{}   // a marker that cannot be written must never stop the window; the next start then simply sees no marker
  }

  static string ReadText(string path) {
   if(!File.Exists(path))return null;
   try{return File.ReadAllText(path,Encoding.UTF8);}catch{return "unreadable";}   // not JSON, so IsUnclean reads it as a dirty exit
  }
 }
}
