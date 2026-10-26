using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 // Which provider addresses a connect attempt will hand to the (elevated) engine, and where they came from.
 public sealed class AddressPlan {
  public string[] Servers=new string[0];
  public string Source="none";          // "pinned" | "saved" | "found" | "none"
  public DateTime FoundUtc;             // set when Source == "found" (found now) or "saved" (when they were found)
  public string Error;                  // null = ready to connect; otherwise a Code from the UI message table
  public string Detail;
 }

 // The last addresses a bootstrap found for one profile. User settings, never the engine's ownership journal.
 public sealed class SavedAddr {
  public string Template="";public string[] Servers=new string[0];public string Utc="";   // Utc: round-trip format, when the addresses were found
 }

 public static class SavedAddresses {
  public const int MaxServers=4,MaxEntries=16;
  public static readonly TimeSpan MaxAge=TimeSpan.FromDays(30);
  static bool Parse(string utc,out DateTime t)=>DateTime.TryParse(utc,null,System.Globalization.DateTimeStyles.RoundtripKind,out t);
  public static SavedAddr Find(List<SavedAddr> list,string template)=>list==null?null:list.FirstOrDefault(s=>s!=null&&string.Equals(s.Template,template,StringComparison.OrdinalIgnoreCase));
  public static DateTime? FoundAt(SavedAddr s)=>s!=null&&Parse(s.Utc,out var t)?(DateTime?)t.ToUniversalTime():null;
  // Addresses that may be tried again: not older than MaxAge (and not from the future), public, distinct, at most four.
  public static string[] Usable(SavedAddr s,DateTime nowUtc) {
   var at=FoundAt(s);
   if(at==null||s.Servers==null)return new string[0];
   var age=nowUtc-at.Value;
   if(age>MaxAge||age<TimeSpan.FromDays(-1))return new string[0];
   return s.Servers.Where(Program.PublicIp).Distinct().Take(MaxServers).ToArray();
  }
  // One entry per profile template, the newest wins; the list never grows beyond MaxEntries (oldest dropped).
  public static void Put(List<SavedAddr> list,string template,string[] servers,DateTime foundUtc) {
   if(list==null||string.IsNullOrWhiteSpace(template))return;
   var clean=(servers??new string[0]).Where(Program.PublicIp).Distinct().Take(MaxServers).ToArray();
   if(clean.Length==0)return;
   list.RemoveAll(s=>s==null||string.Equals(s.Template,template,StringComparison.OrdinalIgnoreCase));
   list.Add(new SavedAddr{Template=template,Servers=clean,Utc=foundUtc.ToUniversalTime().ToString("o")});
   if(list.Count>MaxEntries)list.RemoveRange(0,list.Count-MaxEntries);
  }
 }

 // Order: pins -> saved addresses of an earlier bootstrap (not older than 30 days) -> bootstrap of the provider name.
 // Pins and saved addresses are only a hint. If at least one passes the full preflight (DoH for both Google domains + TLS on the answers) it is used.
 // If none does, or the profile has no pins, the provider name is bootstrapped: only addresses with a TLS certificate valid for the provider NAME
 // and a working DoH for both domains are taken, at most four. An address a resolver merely claims is never used on its own.
 // The elevated engine repeats its own preflight on whatever list it is given; this plan is a proposal, not an authorisation.
 public static class AddressResolver {
  public const int MaxFound=4;

  public static async Task<AddressPlan> ResolveAsync(string template,string[] pins,Func<string[],string,Task<string[]>> preflight,Func<string,Task<string[]>> bootstrap,Func<DateTime> clock=null,SavedAddr saved=null) {
   var plan=new AddressPlan();
   var now=(clock??(()=>DateTime.UtcNow));
   pins=(pins??new string[0]).Where(s=>!string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
   if(pins.Length>0) {
    string[] alive;
    try{alive=await preflight(pins,template)??new string[0];}catch(OperationCanceledException){throw;}catch{alive=new string[0];}
    alive=alive.Where(s=>pins.Contains(s)&&Program.PublicIp(s)).Distinct().ToArray();
    if(alive.Length>0){plan.Servers=alive;plan.Source="pinned";return plan;}
   }
   // the pins are dead (or absent). What the last bootstrap found is tried before the network is asked again, so a bootstrap that fails right now
   // (no cross-check reachable, name resolver down) does not cost the connection. Same preflight as for pins; an address that does not pass it is not used.
   var remembered=SavedAddresses.Usable(saved,now()).Except(pins).ToArray();
   if(remembered.Length>0) {
    string[] alive;
    try{alive=await preflight(remembered,template)??new string[0];}catch(OperationCanceledException){throw;}catch{alive=new string[0];}
    alive=alive.Where(remembered.Contains).Distinct().ToArray();
    if(alive.Length>0){plan.Servers=alive;plan.Source="saved";plan.FoundUtc=SavedAddresses.FoundAt(saved).Value;return plan;}
   }
   string[] found;
   try{found=await bootstrap(template)??new string[0];}
   catch(OperationCanceledException){throw;}   // the user closed the window: that is a cancelled operation, not a failed search
   catch(BootstrapTimeoutException e){plan.Error="BOOTSTRAP_TIMEOUT";plan.Detail=e.Message;return plan;}
   catch(Exception e){plan.Error="BOOTSTRAP_FAILED";plan.Detail=e.Message;return plan;}
   found=found.Where(Program.PublicIp).Distinct().Take(MaxFound).ToArray();
   if(found.Length==0){plan.Error="BOOTSTRAP_NO_ADDRESS";plan.Detail="No address passed TLS on the provider name and DoH for both Google domains.";return plan;}
   plan.Servers=found;plan.Source="found";plan.FoundUtc=now();
   return plan;
  }

  // The real network: the pins go through the same candidate check the engine uses, the name through Bootstrap.
  public static Task<AddressPlan> ResolveLiveAsync(string template,string[] pins,CancellationToken ct=default,SavedAddr saved=null)=>ResolveAsync(template,pins,
   async(servers,tmpl)=>(await Program.NetworkCandidates(servers,tmpl,false,ct)).Where(c=>c.DohValidated).Select(c=>c.Server).ToArray(),
   tmpl=>Bootstrap.PinnedAsync(tmpl,ct),null,saved);

  // Human-readable origin of the addresses for the main page.
  public static string SourceText(string source,DateTime foundUtc)=>source=="found"?L.T("найдены сейчас, ")+foundUtc.ToLocalTime().ToString("dd.MM HH:mm"):source=="saved"?L.T("сохранённые, ")+foundUtc.ToLocalTime().ToString("dd.MM"):source=="pinned"?L.T("закреплённые"):"";
 }
}
