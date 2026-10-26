using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 // Offline black-box hooks for the E2E tier (tests\t8_release_1_6.ps1): a scenario file describes what the "network" answers, the real production logic runs on it.
 // Nothing here opens a socket or touches the system; like --benchmark-synthetic these flags exist for tests only.
 public static class SyntheticScenarios {
  static string[] Strs(JsonElement e,string name)=>e.TryGetProperty(name,out var a)&&a.ValueKind==JsonValueKind.Array?a.EnumerateArray().Select(x=>x.GetString()).ToArray():new string[0];
  static string Str(JsonElement e,string name,string d=null)=>e.TryGetProperty(name,out var a)&&a.ValueKind==JsonValueKind.String?a.GetString():d;
  static DateTime Utc(JsonElement e,string name)=>DateTime.Parse(Str(e,name,"2026-10-12T12:00:00Z"),null,System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

  sealed class ScenarioNet:IBootstrapNet {
   public string[] Sys=new string[0];public bool SysFail,SysHang,CrossHang;public int SysDelay,CrossDelay;public CrossResult[] Cross=new CrossResult[0];public HashSet<string> TlsOk=new HashSet<string>(),DohOk=new HashSet<string>();public bool DohHang;public string[] DohAsked=new string[0];
   static async Task<T> Delayed<T>(int ms,CancellationToken ct,T value){if(ms>0)await Task.Delay(ms,ct);return value;}
   public Task<string[]> SystemResolve(string host,CancellationToken ct){if(SysFail)throw new InvalidOperationException("system resolver unreachable");if(SysHang)return new TaskCompletionSource<string[]>().Task;return Delayed(SysDelay,ct,Sys);}
   public Task<CrossResult[]> CrossCheck(string host,CancellationToken ct){if(CrossHang)return new TaskCompletionSource<CrossResult[]>().Task;return Delayed(CrossDelay,ct,Cross);}
   public Task<TlsInfo> TlsOnName(string ip,int port,string host,CancellationToken ct)=>Task.FromResult(TlsOk.Contains(ip)?new TlsInfo{Ok=true,PolicyErrors="None"}:new TlsInfo{Ok=false,Error="AuthenticationException: certificate name mismatch",PolicyErrors="RemoteCertificateNameMismatch"});
   public Task<Candidate[]> DohCandidates(string[] ips,string template,CancellationToken ct){DohAsked=ips;if(DohHang)return new TaskCompletionSource<Candidate[]>().Task;return Task.FromResult(ips.Select(i=>new Candidate{Server=i,DohValidated=DohOk.Contains(i),DohTls="TLS_OK_HTTP_404"}).ToArray());}
  }
  static bool Is(JsonElement e,string name,string word)=>e.TryGetProperty(name,out var a)&&a.ValueKind==JsonValueKind.String&&a.GetString()==word;

  // bootstrap-synthetic: Bootstrap + AddressResolver on scripted resolvers.
  // {template,budgetMs,discoveryMs,system:[ips]|"fail"|"hang",cross:[{via,server,code,addresses}]|"hang",tlsOk:[ips],dohOk:[ips],dohHang:bool}
  public static object BootstrapScenario(string json) {
   using(var doc=JsonDocument.Parse(json)) {
    var r=doc.RootElement;string template=Str(r,"template","https://dns.example.test/dns-query");
    var net=new ScenarioNet{TlsOk=new HashSet<string>(Strs(r,"tlsOk")),DohOk=new HashSet<string>(Strs(r,"dohOk")),DohHang=r.TryGetProperty("dohHang",out var dh)&&dh.ValueKind==JsonValueKind.True};
    if(Is(r,"system","fail"))net.SysFail=true;else if(Is(r,"system","hang"))net.SysHang=true;else net.Sys=Strs(r,"system");
    if(Is(r,"cross","hang"))net.CrossHang=true;
    else if(r.TryGetProperty("cross",out var cr)&&cr.ValueKind==JsonValueKind.Array)net.Cross=cr.EnumerateArray().Select(c=>new CrossResult{Via=Str(c,"via"),Server=Str(c,"server"),Code=Str(c,"code","DNS_OK"),Addresses=Strs(c,"addresses")}).ToArray();
    if(r.TryGetProperty("systemDelayMs",out var sd))net.SysDelay=sd.GetInt32();
    if(r.TryGetProperty("crossDelayMs",out var cd))net.CrossDelay=cd.GetInt32();
    var o=new BootstrapOptions();
    if(r.TryGetProperty("budgetMs",out var b))o.Budget=TimeSpan.FromMilliseconds(b.GetInt32());
    if(r.TryGetProperty("discoveryMs",out var d))o.Discovery=TimeSpan.FromMilliseconds(d.GetInt32());
    var sw=System.Diagnostics.Stopwatch.StartNew();
    var plan=Task.Run(()=>AddressResolver.ResolveAsync(template,new string[0],(s,t)=>Task.FromResult(new string[0]),t=>Bootstrap.PinnedAsync(t,default,net,o))).GetAwaiter().GetResult();
    return new{plan.Source,plan.Servers,plan.Error,DohAsked=net.DohAsked,ElapsedMs=sw.ElapsedMilliseconds,BudgetMs=(int)o.Budget.TotalMilliseconds};
   }
  }

  sealed class ScenarioProbe:IHealthProbe {
   public string JournalText;public bool ProviderDown,ProviderThrows,ResolverMismatch;public int StatusCalls,ProviderCalls,SystemCalls;public string[] LastServers;public string LastTemplate;
   public string Journal()=>JournalText;
   public Task<(int Code,string Output)> Status(TimeSpan deadline,CancellationToken ct){StatusCalls++;return Task.FromResult((0,"{\"State\":\"DNS active\",\"Code\":\"DNS_CONFIGURED_APPLICATION_NOT_TESTED\",\"Active\":true,\"JournalPhase\":\"Active\",\"Servers\":[\"212.109.195.91\"],\"Template\":\"https://x.example.test/dns-query\"}"));}
   public Task<ProviderProbe> Provider(string[] servers,string template,CancellationToken ct){
    ProviderCalls++;LastServers=servers;LastTemplate=template;if(ProviderThrows)throw new InvalidOperationException("offline");
    var p=new ProviderProbe();if(!ProviderDown){p.Validated=servers;foreach(string t in Program.Hosts)p.Answers[t]=new[]{"198.51.100.1"};}
    return Task.FromResult(p);
   }
   public Task<string[]> SystemAnswers(string host,CancellationToken ct){SystemCalls++;return Task.FromResult(new[]{ResolverMismatch?"192.0.2.55":"198.51.100.1"});}
  }

  // health-synthetic: HealthMonitor / HealthScheduler on a scripted probe.
  // {journal:<object>|"none", provider:"ok"|"down"|"throws", steps:["light"|"full"|{kind,provider}], schedule:{minutes,stepSeconds}}
  public static object Health(string json) {
   using(var doc=JsonDocument.Parse(json)) {
    var r=doc.RootElement;
    if(r.TryGetProperty("schedule",out var sc)) {
     // The scheduler alone, with the production options: every check is instantaneous and healthy.
     int minutes=sc.TryGetProperty("minutes",out var mi)?mi.GetInt32():90,step=sc.TryGetProperty("stepSeconds",out var st)?st.GetInt32():30;
     bool suspectFirst=sc.TryGetProperty("suspectFirst",out var sf)&&sf.ValueKind==JsonValueKind.True;
     var t0=new DateTime(2026,10,12,9,0,0,DateTimeKind.Utc);var now=t0;var sched=new HealthScheduler(()=>now,new HealthOptions());var seq=new List<string>();
     for(int s=0;s<=minutes*60;s+=step){now=t0.AddSeconds(s);if(sched.TryStart(out var tr)){seq.Add((s/60)+":"+tr);if(suspectFirst&&seq.Count==1)sched.Finished(HealthKind.Suspect,true);else sched.Finished(HealthKind.Healthy);}}
     return new{Sequence=seq};
    }
    var probe=new ScenarioProbe{ProviderDown=Is(r,"provider","down"),ProviderThrows=Is(r,"provider","throws"),ResolverMismatch=Is(r,"resolver","mismatch")};
    if(r.TryGetProperty("journal",out var j))probe.JournalText=j.ValueKind==JsonValueKind.Object?j.GetRawText():null;
    var options=new HealthOptions{Targets=Program.Hosts};var monitor=new HealthMonitor(probe,options);var reports=new List<object>();
    if(r.TryGetProperty("steps",out var steps))foreach(var stp in steps.EnumerateArray()) {
     string kind=stp.ValueKind==JsonValueKind.String?stp.GetString():Str(stp,"kind","light");
     if(stp.ValueKind==JsonValueKind.Object&&stp.TryGetProperty("provider",out var pv)){probe.ProviderDown=pv.GetString()=="down";probe.ProviderThrows=pv.GetString()=="throws";}
     var rep=Task.Run(()=>monitor.CheckAsync(kind=="full"?HealthTrigger.Periodic:HealthTrigger.Light,default)).GetAwaiter().GetResult();
     reports.Add(new{Kind=rep.Kind.ToString(),rep.Code,rep.Light,rep.CanFix,Trigger=rep.Trigger.ToString()});
    }
    return new{Reports=reports,probe.StatusCalls,probe.ProviderCalls,probe.SystemCalls,ServersSeen=probe.LastServers,TemplateSeen=probe.LastTemplate};
   }
  }

  // alternative-synthetic: ProviderSwitch.FindAsync on a scripted benchmark (the scenario format of --benchmark-synthetic) plus {"current":"<profile id>"}.
  public static object Alternative(string json) {
   string current,trust;using(var doc=JsonDocument.Parse(json)){current=Str(doc.RootElement,"current","");trust=Str(doc.RootElement,"trust",null);}
   var t=SyntheticTransport.Load(json);
   var outcome=Task.Run(()=>ProviderSwitch.FindAsync(t.Profiles,current,t,t.Options,default)).GetAwaiter().GetResult();
   // "trust": "accept"|"decline" also plays the user's answer to the trust dialog through ProviderSwitch.ApplyAsync and records what was called, in order.
   var calls=new List<string>();bool? applied=null;
   if(trust!=null&&outcome.Offer!=null)
    applied=Task.Run(()=>ProviderSwitch.ApplyAsync(outcome.Offer,id=>{calls.Add("trust:"+id);return trust=="accept";},id=>{calls.Add("enable:"+id);return Task.CompletedTask;})).GetAwaiter().GetResult();
   return new{Kind=outcome.Kind.ToString(),ProfileId=outcome.Offer?.ProfileId,From=outcome.Offer?.FromId,outcome.Checked,Requests=t.Requests,Applied=applied,Calls=calls};
  }

  // connect-synthetic: AddressResolver (pins -> saved -> bootstrap). {template,nowUtc,pins,alive,saved:{servers,utc},bootstrap:"fail"|[ips]}
  public static object Connect(string json) {
   using(var doc=JsonDocument.Parse(json)) {
    var r=doc.RootElement;string template=Str(r,"template","https://dns.example.test/dns-query");DateTime now=Utc(r,"nowUtc");
    var alive=new HashSet<string>(Strs(r,"alive"));var asked=new List<string[]>();int bootCalls=0;
    SavedAddr saved=null;
    if(r.TryGetProperty("saved",out var sv)&&sv.ValueKind==JsonValueKind.Object)saved=new SavedAddr{Template=template,Servers=Strs(sv,"servers"),Utc=Str(sv,"utc","")};
    bool bootFails=r.TryGetProperty("bootstrap",out var bs)&&bs.ValueKind==JsonValueKind.String&&bs.GetString()=="fail";
    var bootIps=r.TryGetProperty("bootstrap",out var bs2)&&bs2.ValueKind==JsonValueKind.Array?Strs(r,"bootstrap"):new string[0];
    Func<string[],string,Task<string[]>> pre=(s,t)=>{asked.Add(s);return Task.FromResult(s.Where(alive.Contains).ToArray());};
    Func<string,Task<string[]>> boot=t=>{bootCalls++;if(bootFails)throw new InvalidOperationException("bootstrap unreachable");return Task.FromResult(bootIps);};
    var plan=Task.Run(()=>AddressResolver.ResolveAsync(template,Strs(r,"pins"),pre,boot,()=>now,saved)).GetAwaiter().GetResult();
    return new{plan.Source,plan.Servers,plan.Error,FoundUtc=plan.Source=="none"?null:plan.FoundUtc.ToString("o"),PreflightAsked=asked,BootstrapCalls=bootCalls};
   }
  }
 }
}
