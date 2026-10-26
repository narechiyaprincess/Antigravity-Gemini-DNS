using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 // Light = the cheap provider-only check every 10 minutes: journal + DoH/TLS to the active addresses, no engine and no CIM.
 public enum HealthTrigger { Startup, NetworkChange, Resume, Periodic, Manual, Light }
 // Off = no bridge is configured or the user switched it off (nothing to check, nothing to fix). Suspect = a soft failure seen once, confirmed by one more check
 // before anyone is told. Drift = the bridge differs from what was configured. Unknown = the check itself could not be completed (e.g. WMI did not answer).
 public enum HealthKind { Off, Healthy, Suspect, Drift, Unknown }

 public sealed class HealthReport {
  public HealthKind Kind;public string Code;public string Detail;public DateTime Utc;public HealthTrigger Trigger;
  public bool Light;    // produced by the light check: it says nothing about the rules, so a healthy light report never clears a failure of the full check
  public bool CanFix;   // Tray repair: one UAC request, address resolution and compensated profile switch.
 }

 public sealed class ProviderProbe {
  public Dictionary<string,string> TransportDetails=new Dictionary<string,string>();
  public string[] Validated=new string[0];                                   // active servers that passed DoH + TLS for both domains right now
  public Dictionary<string,string[]> Answers=new Dictionary<string,string[]>();  // per protected domain: what those servers answer
 }

 // Everything the health check touches, so tests replace it. The live implementation reaches CIM only through Program.Engine("status") with a short deadline.
 public interface IHealthProbe {
  string Journal();
  Task<(int Code,string Output)> Status(TimeSpan deadline,CancellationToken ct);
  Task<ProviderProbe> Provider(string[] servers,string template,CancellationToken ct);
  Task<string[]> SystemAnswers(string host,CancellationToken ct);
 }

 public sealed class HealthOptions {
  public TimeSpan CimDeadline=TimeSpan.FromSeconds(15);     // the engine's read-only status; a hung CIM is cut off here
  public TimeSpan MinInterval=TimeSpan.FromSeconds(60);     // never two checks closer than this (also the delay of the single confirmation re-check)
  public TimeSpan Period=TimeSpan.FromMinutes(45);          // 30-60 minutes: the full check (rules, CIM status, provider, Windows resolver)
  public TimeSpan LightPeriod=TimeSpan.FromMinutes(10);     // Provider-only check between full checks.
  public TimeSpan StartupDelay=TimeSpan.FromSeconds(5);
  public TimeSpan NetworkDebounce=TimeSpan.FromSeconds(15);
  public TimeSpan ResumeDelay=TimeSpan.FromSeconds(20);     // the network is usually not up yet right after wake-up
  public TimeSpan MaxDebounce=TimeSpan.FromSeconds(60);     // a storm of network events cannot postpone the check forever
  public TimeSpan BackoffStart=TimeSpan.FromMinutes(5);     // after a check that could not finish: 5, 10, 20 ... minutes, capped at Period
  public string[] Targets=new string[0];
 }

 // When to run a check. Pure logic over an injected clock: no overlap, debounce, at most one check per MinInterval, and a check that could not finish
 // (hung CIM) backs off exponentially instead of being retried in a loop.
 public sealed class HealthScheduler {
  readonly object gate=new object();readonly Func<DateTime> clock;readonly HealthOptions o;
  bool running,queued;HealthTrigger queuedTrigger;DateTime? due;HealthTrigger dueTrigger;DateTime firstRequest;
  DateTime lastStart=DateTime.MinValue,notBefore=DateTime.MinValue,nextPeriodic,nextLight;int unknownStreak;HealthTrigger runningTrigger;

  public HealthScheduler(Func<DateTime> clock,HealthOptions options){this.clock=clock;o=options;nextPeriodic=clock()+o.Period;nextLight=clock()+o.LightPeriod;}
  public bool Running{get{lock(gate)return running;}}

  TimeSpan Delay(HealthTrigger t)=>t==HealthTrigger.Startup?o.StartupDelay:t==HealthTrigger.NetworkChange?o.NetworkDebounce:t==HealthTrigger.Resume?o.ResumeDelay:TimeSpan.Zero;

  public void Trigger(HealthTrigger t) {
   lock(gate) {
    var n=clock();
    if(running){queued=true;queuedTrigger=t;return;}
    var want=n+Delay(t);
    if(due==null){firstRequest=n;due=want;}
    else{var cap=firstRequest+o.MaxDebounce;due=want<cap?want:cap;}   // trailing edge, bounded
    dueTrigger=t;
   }
  }

  // Called from a UI timer. True = start one check now (the caller must call Finished when it ends).
  public bool TryStart(out HealthTrigger trigger) {
   lock(gate) {
    trigger=default;var n=clock();
    if(running)return false;
    if(due==null&&n>=nextPeriodic){due=n;dueTrigger=HealthTrigger.Periodic;firstRequest=n;}
    if(due==null&&n>=nextLight){due=n;dueTrigger=HealthTrigger.Light;firstRequest=n;}   // the full check wins when both are due
    if(due==null||n<due.Value)return false;
    var earliest=lastStart+o.MinInterval;if(notBefore>earliest)earliest=notBefore;
    if(n<earliest)return false;
    running=true;lastStart=n;due=null;trigger=dueTrigger;runningTrigger=trigger;queued=false;return true;
   }
  }

  public void Finished(HealthKind kind,bool confirmSoon=false) {
   lock(gate) {
    var n=clock();running=false;bool wasLight=runningTrigger==HealthTrigger.Light;
    if(!wasLight)nextPeriodic=n+o.Period;   // a light check does not postpone the full one
    nextLight=n+o.LightPeriod;
    if(kind==HealthKind.Unknown) {
     // The check could not complete. Whatever piled up meanwhile came from the same trouble: drop it and wait longer each time.
     unknownStreak++;var wait=TimeSpan.FromTicks(o.BackoffStart.Ticks<<Math.Min(unknownStreak-1,16));if(wait>o.Period)wait=o.Period;
     notBefore=n+wait;queued=false;due=null;return;
    }
    if(!wasLight){unknownStreak=0;notBefore=DateTime.MinValue;}   // only a check that exercised CIM can say that the WMI trouble is over
    if(confirmSoon){due=n+o.MinInterval;dueTrigger=wasLight?HealthTrigger.Light:HealthTrigger.Periodic;firstRequest=n;queued=false;return;}   // a suspicion is confirmed by the same kind of check
    if(queued){due=n;dueTrigger=queuedTrigger;firstRequest=n;queued=false;}
   }
  }
  public DateTime NotBefore{get{lock(gate)return notBefore;}}
 }

 // One read-only health check. It never writes, never asks for UAC, never starts a benchmark; "Починить" is a separate, user-started action.
 public sealed class HealthMonitor {
  public const string Ok="HEALTH_OK",DriftRules="HEALTH_DRIFT_RULES",ProviderDown="HEALTH_PROVIDER_DOWN",ResolverMismatch="HEALTH_RESOLVER_MISMATCH",NeedsManual="HEALTH_NEEDS_MANUAL",Unknown="HEALTH_UNKNOWN";
  readonly IHealthProbe probe;readonly HealthOptions o;readonly Func<DateTime> clock;string softCode,softJournal;

  public HealthMonitor(IHealthProbe probe,HealthOptions options,Func<DateTime> clock=null){this.probe=probe;o=options;this.clock=clock??(()=>DateTime.UtcNow);}

  static readonly HashSet<string> ManualCodes=new HashSet<string>{"OWNED_RULE_CHANGED","DOH_CHANGED_INDEPENDENTLY","NRPT_OR_HOSTS_CONFLICT","NRPT_CONFLICT","STATE_INVALID","DOH_OWNERSHIP_UNCERTAIN","DOH_REGISTRATION_CONFLICT"};

  HealthReport R(HealthKind kind,string code,string detail,HealthTrigger t,bool canFix=false)=>new HealthReport{Kind=kind,Code=code,Detail=detail,Utc=clock(),Trigger=t,CanFix=canFix,Light=t==HealthTrigger.Light};
  HealthReport ChangedContext(string journal,HealthTrigger trigger) {
   string current=probe.Journal();
   if(string.Equals(current,journal,StringComparison.Ordinal))return null;
   softCode=null;softJournal=null;
   string phase=Recovery.PhaseOfJournal(current);
   if(phase==null||phase=="Restored")return R(HealthKind.Off,"NOT_CONFIGURED","switched off during the check",trigger);
   return phase=="Active"?R(HealthKind.Unknown,Unknown,"ACTIVE_CONFIGURATION_CHANGED",trigger)
    :R(HealthKind.Drift,"RECOVERY_REQUIRED","journal phase "+phase,trigger);
  }

  // The light check: the same journal gate as the full one, then only the provider: the addresses the journal says are active get DoH + TLS for both domains.
  // No engine, no CIM, no Windows-resolver comparison. A failure goes through the same Suspect and Drift confirmation; a success only withdraws an earlier provider failure.
  async Task<HealthReport> LightAsync(CancellationToken ct) {
   const HealthTrigger t=HealthTrigger.Light;
   string journal=probe.Journal();string phase=Recovery.PhaseOfJournal(journal);
   if(phase==null||phase=="Restored"){softCode=null;return R(HealthKind.Off,"NOT_CONFIGURED",null,t);}
   if(phase!="Active"){softCode=null;return R(HealthKind.Drift,"RECOVERY_REQUIRED","journal phase "+phase,t);}
   string[] servers=new string[0];string template=null;
   try{using(var doc=JsonDocument.Parse(journal)){
    var root=doc.RootElement;
    if(root.TryGetProperty("Servers",out var sv)&&sv.ValueKind==JsonValueKind.Array)servers=sv.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.String).Select(x=>x.GetString()).Where(Program.PublicIp).Distinct().ToArray();
    if(root.TryGetProperty("Template",out var tp)&&tp.ValueKind==JsonValueKind.String)template=tp.GetString();
   }}catch{}
   if(servers.Length==0||string.IsNullOrEmpty(template))return R(HealthKind.Unknown,Unknown,"JOURNAL_WITHOUT_ADDRESSES",t);
   ProviderProbe pp=null;
   try{pp=await probe.Provider(servers,template,ct);}catch(OperationCanceledException){throw;}catch{pp=null;}
   ct.ThrowIfCancellationRequested();
   var changed=ChangedContext(journal,t);if(changed!=null)return changed;
   if(pp==null||pp.Validated==null||pp.Validated.Length==0)return Soft(ProviderDown,"no active address passed DoH + TLS (light check)",t,journal);
   if(softCode==ProviderDown)softCode=null;   // only the failure this check can verify; a pending resolver suspicion of the full check stays
   return R(HealthKind.Healthy,Ok,null,t);
  }

  public async Task<HealthReport> CheckAsync(HealthTrigger trigger,CancellationToken ct) {
   ct.ThrowIfCancellationRequested();
   if(trigger==HealthTrigger.Light)return await LightAsync(ct);
   string journal=probe.Journal();string phase=Recovery.PhaseOfJournal(journal);
   // Nothing configured, or the user switched the bridge off: this is the user's decision and nothing here may suggest otherwise. No CIM, no network.
   if(phase==null||phase=="Restored"){softCode=null;return R(HealthKind.Off,"NOT_CONFIGURED",null,trigger);}
   // An unfinished operation is the recovery state of the main page (its own "Восстановить" button); no CIM call is needed to know it.
   if(phase!="Active"){softCode=null;return R(HealthKind.Drift,"RECOVERY_REQUIRED","journal phase "+phase,trigger);}

   (int Code,string Output) st=await probe.Status(o.CimDeadline,ct);
   ct.ThrowIfCancellationRequested();
   string code=null;JsonElement status=default;bool parsed=false;
   try{using(var doc=JsonDocument.Parse(st.Output)){status=doc.RootElement.Clone();parsed=true;}}catch{}
   if(parsed&&status.ValueKind==JsonValueKind.Object&&status.TryGetProperty("Code",out var cp)&&cp.ValueKind==JsonValueKind.String)code=cp.GetString();
   if(st.Code==1460||code=="ENGINE_TIMEOUT"){softCode=null;return R(HealthKind.Unknown,Unknown,"WMI_NOT_RESPONDING",trigger);}
   if(st.Code!=0||!parsed){softCode=null;return R(HealthKind.Unknown,Unknown,code??"ENGINE_ERROR",trigger);}
   var changed=ChangedContext(journal,trigger);if(changed!=null)return changed;

   bool active=status.TryGetProperty("Active",out var a)&&a.ValueKind==JsonValueKind.True&&status.TryGetProperty("State",out var sp)&&sp.GetString()=="DNS active";
   if(!active) {
    softCode=null;
    string why=code??"UNKNOWN";
    string token=CodeTable.TokenOf(why);   // the engine says "CODE: explanation"
    if(ManualCodes.Contains(token))return R(HealthKind.Drift,NeedsManual,why,trigger);
    return R(HealthKind.Drift,token=="NRPT_NOT_EFFECTIVE"?ResolverMismatch:DriftRules,why,trigger,true);
   }

   var servers=status.TryGetProperty("Servers",out var sv)&&sv.ValueKind==JsonValueKind.Array?sv.EnumerateArray().Select(x=>x.GetString()).Where(x=>!string.IsNullOrEmpty(x)).ToArray():new string[0];
   string template=status.TryGetProperty("Template",out var tp)&&tp.ValueKind==JsonValueKind.String?tp.GetString():null;
   ProviderProbe pp=null;
   try{pp=await probe.Provider(servers,template,ct);}catch(OperationCanceledException){throw;}catch{pp=null;}
   ct.ThrowIfCancellationRequested();
   changed=ChangedContext(journal,trigger);if(changed!=null)return changed;
   if(pp==null||pp.Validated==null||pp.Validated.Length==0)return Soft(ProviderDown,"no active address passed DoH + TLS",trigger,journal);
   foreach(string host in o.Targets) {
    string[] sys;try{sys=await probe.SystemAnswers(host,ct)??new string[0];}catch(OperationCanceledException){throw;}catch{sys=new string[0];}
    pp.Answers.TryGetValue(host,out var expected);expected=expected??new string[0];
    if(sys.Length==0||sys.Any(x=>!expected.Contains(x)))return Soft(ResolverMismatch,"Windows resolver answer for "+host+" is not from the provider",trigger,journal);
   }
   changed=ChangedContext(journal,trigger);if(changed!=null)return changed;
   softCode=null;return R(HealthKind.Healthy,Ok,null,trigger);
  }

  // Network-dependent failures are confirmed by one more check a minute later before they are reported as drift (waking up, Wi-Fi reconnecting).
  HealthReport Soft(string code,string detail,HealthTrigger t,string journal) {
   var changed=ChangedContext(journal,t);if(changed!=null)return changed;
   if(!string.Equals(softJournal,journal,StringComparison.Ordinal))softCode=null;
   softJournal=journal;
   // ResolverMismatch is not repairable here: when the addresses match, Engine -Repair changes nothing and still reports success.
   if(softCode==code){softCode=null;return R(HealthKind.Drift,code,detail,t,code==ProviderDown);}
   softCode=code;return R(HealthKind.Suspect,code,detail,t);
  }

  // The live probe: unelevated, read-only. CIM only through the engine's status with the short deadline; DoH and resolver checks are plain network reads.
  public sealed class Live:IHealthProbe {
   public string Journal()=>Recovery.ReadJournal();
   public Task<(int Code,string Output)> Status(TimeSpan deadline,CancellationToken ct)=>Program.Engine("status",false,false,null,null,null,ct,deadline);
   public async Task<ProviderProbe> Provider(string[] servers,string template,CancellationToken ct) {
    var res=new ProviderProbe();if(servers==null||servers.Length==0)return res;
    var cands=await Program.NetworkCandidates(servers,template,false,ct);
    // One lost handshake must not count as a failed check: a provider that answers nothing is asked once more after a short pause.
    if(!cands.Any(c=>c.DohValidated)){await Task.Delay(1500,ct);cands=await Program.NetworkCandidates(servers,template,false,ct);}
    var ok=cands.Where(c=>c.DohValidated).ToArray();res.Validated=ok.Select(c=>c.Server).ToArray();
    foreach(string host in Program.Hosts)res.Answers[host]=ok.SelectMany(c=>c.TargetAnswers!=null&&c.TargetAnswers.TryGetValue(host,out var v)?v:new string[0]).Distinct().ToArray();
    foreach(string host in Program.Hosts)res.TransportDetails[host]=string.Join("; ",cands.Select(c=>c.TargetTls!=null&&c.TargetTls.TryGetValue(host,out var tls)?tls:host==Program.Target?c.DohTls:null).Where(x=>!string.IsNullOrEmpty(x)).Distinct());
    return res;
   }
   public async Task<string[]> SystemAnswers(string host,CancellationToken ct) {
    ct.ThrowIfCancellationRequested();
    var work=Task.Run(()=>{using(var doc=JsonDocument.Parse(JsonSerializer.Serialize(Wire.SystemQuery(host,1)))){return doc.RootElement.TryGetProperty("Addresses",out var ad)&&ad.ValueKind==JsonValueKind.Array?ad.EnumerateArray().Select(x=>x.GetString()).ToArray():new string[0];}});
    var done=await Task.WhenAny(work,Task.Delay(TimeSpan.FromSeconds(8),ct));
    ct.ThrowIfCancellationRequested();
    return done==work?await work:new string[0];
   }
  }
 }
}
