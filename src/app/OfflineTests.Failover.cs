using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace AntigravityGeminiDns {
 // v1.3 Autonomy: the Top-3 pool, the bogus-answer classes and the failover guard. Clock passed in; the health probe is the test fake of RunHealthTests.
 public static partial class OfflineTests {
  static ProfileResult Res(string id,int rank,bool selectable=true,bool experimental=false,bool bogus=false)=>new ProfileResult{Id=id,Rank=rank,Selectable=selectable,Experimental=experimental,BogusAnswer=bogus};
  static BenchRun RunOf(params ProfileResult[] results)=>new BenchRun{Id="t",Results=results,StartedUtc=DateTime.UtcNow.ToString("o")};

  static void RunAutonomyTests() {
   var now=new DateTime(2026,10,15,12,0,0,DateTimeKind.Utc);

   // C1 Top-3 pool: the best three selectable profiles of a real run, in rank order
   var pool=ProviderPool.From(RunOf(Res("d",4),Res("b",2),Res("x",1,experimental:true),Res("e",5),Res("a",1),Res("c",3,selectable:false),Res("f",0),Res("g",6,bogus:true)),now);
   Check(pool.Select(e=>e.Id).SequenceEqual(new[]{"a","b","d"}),"autonomy: the pool keeps the three best selectable profiles by rank (experimental, unselectable, bogus and unranked left out)");
   Check(pool.All(e=>DateTime.Parse(e.Utc,null,System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()==now),"autonomy: every pool entry carries the time of the measurement");
   var synthetic=RunOf(Res("a",1));synthetic.Synthetic=true;
   var cancelled=RunOf(Res("a",1));cancelled.Cancelled=true;
   Check(ProviderPool.From(synthetic,now).Count==0&&ProviderPool.From(cancelled,now).Count==0&&ProviderPool.From(null,now).Count==0,"autonomy: a self-test, a cancelled or a missing run never fills the pool");

   // a stored pool: fresh entries are used, the failed profile is excluded, stale and future entries are ignored, duplicates collapse
   var stored=new List<PoolEntry>{
    new PoolEntry{Id="a",Utc=now.AddDays(-1).ToString("o")},
    new PoolEntry{Id="old",Utc=now.AddDays(-8).ToString("o")},
    new PoolEntry{Id="future",Utc=now.AddHours(1).ToString("o")},
    new PoolEntry{Id="b",Utc=now.AddDays(-6).ToString("o")},
    new PoolEntry{Id="a",Utc=now.ToString("o")},
    new PoolEntry{Id="broken",Utc="not a date"},
    null};
   Check(ProviderPool.Usable(stored,now,null).SequenceEqual(new[]{"a","b"}),"autonomy: the pool skips stale (older than 7 days), future and broken entries and collapses duplicates");
   Check(ProviderPool.Usable(stored,now,"a").SequenceEqual(new[]{"b"}),"autonomy: the profile that just failed is never taken from the pool");
   Check(ProviderPool.Usable(new List<PoolEntry>(),now,null).Length==0&&ProviderPool.Usable(null,now,null).Length==0,"autonomy: an empty or missing pool gives no candidates");
   Check(ProviderPool.Usable(new List<PoolEntry>{new PoolEntry{Id="z",Utc=now.AddDays(-7).ToString("o")}},now,null).SequenceEqual(new[]{"z"}),"autonomy: an entry exactly 7 days old is still usable");

   // the check order: the pool first in pool order, then the rest of the catalog in its order
   var cat=new[]{"A","B","C","D"}.Select(x=>new ProfileSpec{Id=x}).ToList();
   Check(ProviderPool.CheckOrder(cat,p=>p.Id,new[]{"C","A","Z"}).Select(p=>p.Id).SequenceEqual(new[]{"C","A","B","D"}),"autonomy: pool members are checked first in pool order, unknown ids are ignored");
   Check(ProviderPool.CheckOrder(cat,p=>p.Id,null).Select(p=>p.Id).SequenceEqual(new[]{"A","B","C","D"}),"autonomy: with no pool the catalog order stays");

   // C2 bogus answers: every network of the list, the boundary addresses next to them are not bogus
   var bogusList=new[]{"0.0.0.0","0.1.2.3","127.0.0.1","169.254.1.1","10.1.2.3","172.16.0.1","172.31.255.255","192.168.1.1","100.64.0.1","100.127.255.255","224.0.0.1","240.0.0.1","255.255.255.254","::","::1","fe80::1","febf::1","fc00::1","fd00::1","ff02::1"};
   Check(bogusList.All(AnswerSanity.IsBogus),"autonomy: every bogus network of the list is recognised (IPv4 and IPv6)");
   var cleanList=new[]{"172.15.255.255","172.32.0.0","100.63.255.255","100.128.0.0","9.255.255.255","11.0.0.0","223.255.255.255","8.8.8.8","203.0.113.50","2606:4700:4700::1111","2a12:bec4:1bb0:2a30::2"};
   Check(!cleanList.Any(AnswerSanity.IsBogus),"autonomy: the boundary addresses (172.15.255.255, 172.32.0.0, 100.63.255.255, 100.128.0.0) and public addresses are not bogus");
   Check(!AnswerSanity.IsBogus((IPAddress)null)&&!AnswerSanity.IsBogus("not an address")&&!AnswerSanity.IsBogus(""),"autonomy: a missing or unreadable address is not classified as bogus");
   Check(AnswerSanity.Code=="BOGUS_ANSWER"&&CodeTable.TokenOf("BOGUS_ANSWER")=="BOGUS_ANSWER","autonomy: the reason code BOGUS_ANSWER is in the code table");

   // C3 failover: only a confirmed provider failure, only when enabled
   var one=new HealthReport{Kind=HealthKind.Suspect,Code=HealthMonitor.ProviderDown};
   var confirmed=new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.ProviderDown};
   Check(!FailoverGuard.Triggered(true,one)&&FailoverGuard.Triggered(true,confirmed),"autonomy: one failed check is not a trigger, a confirmed provider failure is");
   Check(!FailoverGuard.Triggered(false,confirmed)&&!FailoverGuard.Triggered(true,null)&&!FailoverGuard.Triggered(true,new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.DriftRules}),"autonomy: disabled never triggers; a rule drift is not a provider failure");

   var g=new FailoverGuard();
   Check(g.Refusal(null,now)==null&&g.Refusal("A",now)==null,"autonomy: a fresh guard allows an offer");
   g.Switched(now);
   Check(g.Refusal(null,now.AddMinutes(5))=="FAILOVER_COOLDOWN"&&g.Refusal(null,now.AddMinutes(FailoverGuard.CooldownMinutes))==null,"autonomy: a switch starts a 10 minute cooldown");

   var pp=new FailoverGuard();
   pp.Failed("X",now);
   Check(pp.Refusal("X",now.AddMinutes(10))=="FAILOVER_PING_PONG"&&pp.Refusal("X",now.AddMinutes(30))==null&&pp.Refusal("Y",now.AddMinutes(10))==null,"autonomy: a profile that failed in the last 30 minutes is not offered again (anti ping-pong)");

   var lim=new FailoverGuard();
   lim.Offered(now);lim.Offered(now.AddMinutes(1));lim.Offered(now.AddMinutes(2));
   Check(lim.Refusal(null,now.AddMinutes(3))=="FAILOVER_LIMIT"&&lim.Refusal(null,now.AddMinutes(61))==null,"autonomy: at most three offers an hour; an old offer stops counting after an hour");

   // C4: the health check decides, not one timeout. Two failed checks in a row are the trigger; a working bridge and a disabled setting never are.
   var ho=new HealthOptions{Targets=new[]{"a.example.test"}};
   var hp=HealthyProbe(ho);hp.Prov=new ProviderProbe{Validated=new string[0]};
   var hm=new HealthMonitor(hp,ho);
   var first=Hc(hm);var second=Hc(hm);
   Check(!FailoverGuard.Triggered(true,first)&&FailoverGuard.Triggered(true,second)&&!FailoverGuard.Triggered(false,second),"autonomy: a provider that fails once is not an offer; the same failure confirmed by the next check is, unless the setting is off");
   var hh=HealthyProbe(ho);var healthy=Hc(new HealthMonitor(hh,ho));
   Check(healthy.Kind==HealthKind.Healthy&&!FailoverGuard.Triggered(true,healthy),"autonomy: a working bridge is never touched by the failover");
  }
 }
}
