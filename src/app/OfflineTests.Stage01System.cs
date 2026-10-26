using System;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 public static partial class OfflineTests {
  sealed class Stage01ChangingProbe:IHealthProbe {
   public readonly FakeProbe Inner;public Action BeforeProvider,BeforeSystem;
   public Stage01ChangingProbe(FakeProbe inner){Inner=inner;}
   public string Journal()=>Inner.Journal();
   public Task<(int Code,string Output)> Status(TimeSpan deadline,CancellationToken ct)=>Inner.Status(deadline,ct);
   public Task<ProviderProbe> Provider(string[] servers,string template,CancellationToken ct){BeforeProvider?.Invoke();return Inner.Provider(servers,template,ct);}
   public Task<string[]> SystemAnswers(string host,CancellationToken ct){BeforeSystem?.Invoke();return Inner.SystemAnswers(host,ct);}
  }
  static void RunStage01SystemTests() {
   var options=new HealthOptions{Targets=new[]{"a.example.test","b.example.test"}};
   const string journalB="{\"Schema\":1,\"Phase\":\"Active\",\"Owner\":\"profile-b\"}";
   var statusChanged=HealthyProbe(options);
   statusChanged.OnStatus=()=>statusChanged.JournalText=journalB;
   var result=Hc(new HealthMonitor(statusChanged,options));
   Check(result.Kind==HealthKind.Unknown&&result.Detail=="ACTIVE_CONFIGURATION_CHANGED"&&statusChanged.ProviderCalls==0,"health: stale status from profile A cannot validate active profile B");

   var providerChanged=new Stage01ChangingProbe(HealthyProbe(options));
   providerChanged.BeforeProvider=()=>providerChanged.Inner.JournalText=journalB;
   result=Hc(new HealthMonitor(providerChanged,options));
   Check(result.Kind==HealthKind.Unknown&&providerChanged.Inner.SystemCalls==0,"health: provider result is discarded after an active profile switch");

   var resolverChanged=new Stage01ChangingProbe(HealthyProbe(options));
   resolverChanged.BeforeSystem=()=>resolverChanged.Inner.JournalText=journalB;
   result=Hc(new HealthMonitor(resolverChanged,options));
   Check(result.Kind==HealthKind.Unknown&&result.Detail=="ACTIVE_CONFIGURATION_CHANGED","health: resolver result is discarded after an active profile switch");

   var lightChanged=new Stage01ChangingProbe(HealthyProbe(options));lightChanged.Inner.JournalText="{\"Phase\":\"Active\",\"Servers\":[\"212.109.195.91\"],\"Template\":\"https://x.example.test/dns-query\"}";
   lightChanged.BeforeProvider=()=>lightChanged.Inner.JournalText=journalB;
   result=Sync(()=>new HealthMonitor(lightChanged,options).CheckAsync(HealthTrigger.Light,default));
   Check(result.Kind==HealthKind.Unknown&&result.Light,"health: light check of A never reports active B healthy");

   var newFailure=HealthyProbe(options);newFailure.Prov=new ProviderProbe();var monitor=new HealthMonitor(newFailure,options);
   var first=Hc(monitor);newFailure.JournalText=journalB;var second=Hc(monitor);
   Check(first.Kind==HealthKind.Suspect&&second.Kind==HealthKind.Suspect,"health: failures of different profiles do not combine into confirmed drift");
  }
 }
}
