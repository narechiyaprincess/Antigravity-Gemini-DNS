using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 // the current provider has no usable address. The program looks for another catalog profile that works on this network and OFFERS it.
 // Nothing is switched silently: the offer becomes a connection only through the trust dialog and the ordinary enable (which handles a running bridge
 // as a compensated profile switch). A bridge that exists is never removed by the search.
 public sealed class SwitchOffer {
  public string ProfileId;   // the profile that passed the check
  public string FromId;      // the profile that had no usable address
  public string Reason;      // the code that triggered the search
 }

 public enum SwitchKind { Offer, NoWorking, Cancelled }
 public sealed class SwitchOutcome {
  public SwitchKind Kind;public SwitchOffer Offer;public ProfileResult Result;public int Checked;
 }

 public sealed class PoolEntry {
  public string Id;public string Utc;   // the profile and the time of the measurement that found it working (round-trip UTC)
 }

 // v1.3 Top-3 pool: the best fully working profiles of the last real measurement. A switch checks them first; pure, with the clock passed in.
 public static class ProviderPool {
  public const int Size=3;
  public static readonly TimeSpan MaxAge=TimeSpan.FromDays(7);

  // the best selectable, non-experimental profiles of a run, by rank; a synthetic (self-test) or cancelled run gives nothing
  public static List<PoolEntry> From(BenchRun run,DateTime nowUtc) {
   if(run==null||run.Cancelled||run.Synthetic)return new List<PoolEntry>();
   return run.Results.Where(r=>r.Selectable&&!r.Experimental&&!r.BogusAnswer).OrderBy(r=>r.Rank==0?int.MaxValue:r.Rank).Take(Size)
    .Select(r=>new PoolEntry{Id=r.Id,Utc=nowUtc.ToUniversalTime().ToString("o")}).ToList();
  }

  // ids still usable: measured within MaxAge (not in the future), not the profile that just failed, in the saved order
  public static string[] Usable(List<PoolEntry> pool,DateTime nowUtc,string excludeId) {
   if(pool==null)return new string[0];
   var ids=new List<string>();
   foreach(var e in pool) {
    if(e==null||string.IsNullOrEmpty(e.Id)||string.Equals(e.Id,excludeId,StringComparison.OrdinalIgnoreCase))continue;
    if(!DateTime.TryParse(e.Utc,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out var t))continue;
    var age=nowUtc.ToUniversalTime()-t.ToUniversalTime();
    if(age<TimeSpan.Zero||age>MaxAge||ids.Contains(e.Id,StringComparer.OrdinalIgnoreCase))continue;
    ids.Add(e.Id);
   }
   return ids.ToArray();
  }

  public static bool Contains(IEnumerable<string> ids,string id)=>ids!=null&&ids.Contains(id,StringComparer.OrdinalIgnoreCase);

  // the check order: candidates named by the pool first (in pool order), then the others in their catalog order
  public static List<T> CheckOrder<T>(IEnumerable<T> candidates,Func<T,string> id,IEnumerable<string> preferred) where T:class {
   var list=candidates.ToList();
   var first=(preferred??Enumerable.Empty<string>()).Select(p=>list.FirstOrDefault(c=>string.Equals(id(c),p,StringComparison.OrdinalIgnoreCase))).Where(c=>c!=null).Distinct().ToList();
   return first.Concat(list.Where(c=>!first.Contains(c))).ToList();
  }
 }

 // v1.3 failover (opt-in, off by default). It only decides whether the program may OFFER a working profile after a confirmed provider failure;
 // the offer is carried out by the ordinary trust dialog and enable, never by itself. Clock passed in; no system access.
 public sealed class FailoverGuard {
  public const int CooldownMinutes=10,PingPongMinutes=30,MaxOffersPerHour=3;
  DateTime? lastSwitchUtc;
  readonly Dictionary<string,DateTime> failedAt=new Dictionary<string,DateTime>(StringComparer.OrdinalIgnoreCase);
  readonly List<DateTime> offers=new List<DateTime>();

  // only a confirmed provider failure (Health: Suspect, then the same Drift again). One timeout is Suspect and never a trigger.
  public static bool Triggered(bool enabled,HealthReport r)=>enabled&&r!=null&&r.Kind==HealthKind.Drift&&r.Code==HealthMonitor.ProviderDown;

  // null = a switch may be offered now; otherwise the reason code for the operation history
  public string Refusal(string candidateId,DateTime nowUtc) {
   if(lastSwitchUtc!=null&&nowUtc-lastSwitchUtc.Value<TimeSpan.FromMinutes(CooldownMinutes))return "FAILOVER_COOLDOWN";
   if(candidateId!=null&&failedAt.TryGetValue(candidateId,out var f)&&nowUtc-f<TimeSpan.FromMinutes(PingPongMinutes))return "FAILOVER_PING_PONG";
   offers.RemoveAll(t=>nowUtc-t>=TimeSpan.FromHours(1));
   if(offers.Count>=MaxOffersPerHour)return "FAILOVER_LIMIT";
   return null;
  }
  public void Failed(string profileId,DateTime nowUtc)=>failedAt[profileId]=nowUtc;
  public void Offered(DateTime nowUtc)=>offers.Add(nowUtc);
  public void Switched(DateTime nowUtc)=>lastSwitchUtc=nowUtc;
 }

 public static class ProviderSwitch {
  // The failures that mean "this provider has no address that works right now". Anything else (a conflict, a missing right, a dead WMI) is not a reason to change provider.
  public static readonly string[] Triggers={"BOOTSTRAP_NO_ADDRESS","BOOTSTRAP_TIMEOUT","DOH_NO_VALIDATED_ENDPOINT"};
  public static bool IsTrigger(string code)=>Triggers.Contains(CodeTable.TokenOf(code));

  // What is measured: the non-experimental profiles of the catalog, each with its pins and the addresses an earlier bootstrap found for it (not older than 30 days).
  public static List<ProfileSpec> Pool(ProfileCatalog catalog,List<SavedAddr> saved,DateTime nowUtc) {
   var pool=new List<ProfileSpec>();
   foreach(var p in catalog.Profiles.Where(x=>!x.Experimental)) {
    var servers=p.Servers.Concat(SavedAddresses.Usable(SavedAddresses.Find(saved,p.Template),nowUtc)).Distinct().ToArray();
    pool.Add(new ProfileSpec{Id=p.Id,Slot=p.Slot,Template=p.Template,Servers=servers,Experimental=false,Origin=p.Origin});
   }
   return pool;
  }

  // The benchmark's own idea of "works": Selectable (alive, both domains, availability, stability and at least 75 % of the answer addresses reachable).
  // An experimental profile is never offered, even when it measures well; the profile that just failed is not offered back.
  public static ProfileResult Pick(BenchRun run,string currentId)=>run.Results
   .Where(r=>r.Selectable&&!r.Experimental&&!string.Equals(r.Id,currentId,StringComparison.OrdinalIgnoreCase))
   .OrderBy(r=>r.Rank==0?int.MaxValue:r.Rank).FirstOrDefault();

  // Read-only: DoH queries and TLS handshakes, like the benchmark tab. No state, no NRPT, no elevation.
  // The Top-3 pool of the last measurement (preferredIds) is measured first; the rest of the catalog only when no pool member works.
  public static async Task<SwitchOutcome> FindAsync(IEnumerable<ProfileSpec> pool,string currentId,IBenchTransport transport,BenchOptions options,CancellationToken ct,IEnumerable<string> preferredIds=null) {
   var candidates=pool.Where(p=>!p.Experimental&&p.Servers!=null&&p.Servers.Length>0&&!string.Equals(p.Id,currentId,StringComparison.OrdinalIgnoreCase)).ToList();
   var outcome=new SwitchOutcome{Kind=SwitchKind.NoWorking,Checked=candidates.Count};
   if(candidates.Count==0)return outcome;
   var ordered=ProviderPool.CheckOrder(candidates,p=>p.Id,preferredIds);
   var pooled=ordered.Where(p=>ProviderPool.Contains(preferredIds,p.Id)).ToList();
   foreach(var stage in new[]{pooled,ordered.Except(pooled).ToList()}){
    if(stage.Count==0)continue;
    var run=await Benchmark.RunAsync(stage,options,transport,null,ct);
    if(run.Cancelled||ct.IsCancellationRequested){outcome.Kind=SwitchKind.Cancelled;return outcome;}
    var best=Pick(run,currentId);
    if(best==null)continue;
    outcome.Kind=SwitchKind.Offer;outcome.Result=best;outcome.Offer=new SwitchOffer{ProfileId=best.Id,FromId=currentId};
    return outcome;
   }
   return outcome;
  }

  // The only way an offer is carried out: first the user's trust decision for that provider, then the ordinary enable. A refusal applies nothing.
  public static async Task<bool> ApplyAsync(SwitchOffer offer,Func<string,bool> trust,Func<string,Task> enable) {
   if(offer==null||string.IsNullOrEmpty(offer.ProfileId))return false;
   if(!trust(offer.ProfileId))return false;
   await enable(offer.ProfileId);
   return true;
  }
 }
}
