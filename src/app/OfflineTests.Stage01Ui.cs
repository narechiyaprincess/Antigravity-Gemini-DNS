using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 public static partial class OfflineTests {
  static void RunStage02ProfileNetworkTests(){
   using(var form=new MainForm(true)){
    form.Show();Application.DoEvents();form.SmokeState("idle");
    form.SpeedSource=(template,servers,ct)=>Task.FromResult(new SpeedResult{Ms=37,Utc=DateTime.UtcNow});
    Pump(form.MeasureProfilesForTest(false),4000);
    Check(Enumerable.Range(0,form.ProfileCount).Any(i=>form.ProfilePingText(i).Contains("37")),"profile network: initial network produces measured list latency");
    form.InvalidateNetworkEvidence();
    Check(Enumerable.Range(0,form.ProfileCount).All(i=>form.ProfilePingText(i)==""),"profile network: network invalidation clears cached list latency");
    var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);int started=0;
    form.SpeedSource=async(template,servers,ct)=>{started++;await release.Task;return new SpeedResult{Ms=91,Utc=DateTime.UtcNow};};
    var pending=form.MeasureProfilesForTest(false);
    Check(PumpUntil(()=>started>0,1000),"profile network: controlled old-network measurement is in flight");
    form.InvalidateNetworkEvidence();release.TrySetResult(true);Pump(pending,4000);
    Check(pending.IsCompleted&&Enumerable.Range(0,form.ProfileCount).All(i=>form.ProfilePingText(i)==""),"profile network: late old-network latency cannot repopulate the list");
    form.SpeedSource=(template,servers,ct)=>Task.FromResult(new SpeedResult{Ms=52,Utc=DateTime.UtcNow});
    Pump(form.MeasureProfilesForTest(false),4000);
    Check(Enumerable.Range(0,form.ProfileCount).Any(i=>form.ProfilePingText(i).Contains("52")),"profile network: new-network measurement resumes without force or restart");
   }
  }
  static void RunStage01UiTests(){
   bool english=L.En;
   try{foreach(bool en in new[]{false,true}){
    L.En=en;
    using(var form=new MainForm(true)){
     form.Show();Application.DoEvents();form.JournalReader=()=>"{\"Phase\":\"Active\"}";form.PresentForTest(HealthyStatus);
     var profile=form.PrecisionProfileForTest;
     form.PrecisionMetricsForTest(new BenchRun{StartedUtc=DateTime.UtcNow.AddDays(-1).ToString("O"),Results=new[]{new ProfileResult{Id=profile.Id,Template=profile.Template,Final=true,Attempts=6,Successes=6,MedianMs=75,P95Ms=320,TlsMs=256,Verdict="Good",DomainsOk=true,Samples=new long[]{68,70,75,80,110,320}}}});
     Check(form.Stage01ProviderStatusForTest.dot!=Theme.Green&&form.Stage01TelemetryForTest.Verdict==(en?"DoH benchmark":"Замер DoH"),"stored 6/6 never supplies live Working provider status "+en);
     form.ApplyHealth(new HealthReport{Kind=HealthKind.Healthy,Code=HealthMonitor.Ok,Utc=DateTime.UtcNow});
     Check(form.Stage01ProviderStatusForTest.dot==Theme.Green,"current provider health can show Working "+en);
     var other=System.Linq.Enumerable.First(ProfileCatalog.BuiltIn.Profiles,p=>p.Id!=profile.Id);
     form.PresentForTest(System.Text.Json.JsonSerializer.Serialize(new{State="DNS active",Active=true,JournalPhase="Active",Servers=other.Servers,Template=other.Template},Program.JsonOptions));
     Check(form.Stage01ProviderStatusForTest.dot!=Theme.Green,"silent system profile readback cannot reuse the previous endpoint's healthy status "+en);
     form.PresentForTest(System.Text.Json.JsonSerializer.Serialize(new{State="DNS active",Active=true,JournalPhase="Active",Servers=profile.Servers,Template=profile.Template},Program.JsonOptions));
     form.ApplyHealth(new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.ProviderDown,CanFix=true,Utc=DateTime.UtcNow});
     Check(form.BridgeActive&&form.StatusText==(en?"Bridge needs repair":"Мост требует исправления")&&form.Stage01ProviderStatusForTest.dot==Theme.Amber&&form.Stage01OnlyOneBridgeActionForTest,"failed endpoint is local, config remains applied, repair has one primary action "+en);
     form.InvalidateNetworkEvidence();
     Check(form.Stage01ProviderStatusForTest.dot!=Theme.Green,"network change invalidates provider health "+en);
     form.SmokeState("idle");form.SelectTab(0);form.Stage01ClickMetricsForTest();
     Check(form.Stage01TabForTest==0&&form.Stage01NeutralCardForTest,"metric click is read-only and card has neutral default outline "+en);
     form.Stage01ClickBenchmarkForTest();Check(form.Stage01TabForTest==1&&!form.OpenBenchmarkLinkForTest&&form.Stage01SingleDiagnosticEntryForTest,"one diagnostic entry in Benchmark; no Bridge/Logs duplicates "+en);
     form.SelectTab(2);form.ClickBrowserRowForTest();
     Check(!form.BrowserPanelVisible&&form.Stage01BrowserReadOnlyForTest&&form.Stage01AutostartFileHiddenForTest,"settings browser summary is inert and startup file row is removed "+en);
     form.ClickBrowserChangeForTest();Check(form.BrowserPanelVisible,"explicit Change opens browser picker "+en);form.BrowserCancelForTest();
     form.ShowBrowserPanelForTest(false);Pump(form.BrowserWork);form.SelectBrowserForTest("edge");Pump(form.BrowserWork);form.BrowserContinueForTest();
     form.SelectTab(0);form.SetScopeForTest(MainForm.BridgeScope.Core);form.ClickScopeForTest(MainForm.BridgeScope.Core|MainForm.BridgeScope.Google);
     bool settled=PumpUntil(()=>Program.Google&&!form.ScopeBusy||form.BrowserPanelVisible,4000);
     Check(settled&&!form.BrowserPanelVisible&&form.SettingsBrowser=="edge"&&Program.Google,"saved verified browser enables scope without reopening the picker "+en+" settled="+settled+" panel="+form.BrowserPanelVisible+" saved="+form.SettingsBrowser+" google="+Program.Google+" state="+form.ButtonStateForTest);
     form.SelectTab(0);
     foreach(int dpi in new[]{96,120,144}){UiSurfaceCheck.ChangeDpi(form,dpi);Application.DoEvents();Check(form.Stage01MenuFitsForTest(),"popup remains inside content viewport at "+dpi+" DPI "+en);}
     form.SmokeState("active");var winner=ProfileCatalog.BuiltIn.Profiles.First(p=>p.Id!=form.PrecisionProfileForTest.Id);
     var fresh=new BenchRun{StartedUtc=DateTime.UtcNow.ToString("O"),CatalogVersion=ProfileCatalog.BuiltIn.Version,Targets=Program.Hosts,Winner=winner.Id,Results=new[]{new ProfileResult{Id=winner.Id,Template=winner.Template,Servers=winner.Servers,Final=true,Selectable=true,Attempts=6,Successes=6,MedianMs=75,DomainsOk=true}}};
     int applies=0;form.ApplyProfileHook=()=>{applies++;return Task.CompletedTask;};form.Stage01SetBenchmarkForTest(fresh);form.Stage01SelectBestForTest();Application.DoEvents();
     Check(form.PrecisionProfileForTest.Id==winner.Id&&applies==1,"fresh Best uses stored winner and applies selection once "+en);
     form.Stage01SelectBestForTest();Check(applies==1,"selecting the same winner is a no-op "+en);
     var selected=form.PrecisionProfileForTest.Id;fresh.Winner=ProfileCatalog.BuiltIn.Profiles.First(p=>p.Id!=selected).Id;fresh.StartedUtc=DateTime.UtcNow.AddMinutes(-3).ToString("O");form.Stage01SetBenchmarkForTest(fresh);form.Stage01SelectBestForTest();
     Check(form.PrecisionProfileForTest.Id==selected&&applies==1,"expired result cannot select or apply a profile "+en);
     fresh.StartedUtc=DateTime.UtcNow.ToString("O");form.Stage01SetBenchmarkForTest(fresh);form.InvalidateNetworkEvidence();form.Stage01SelectBestForTest();
     Check(form.PrecisionProfileForTest.Id==selected&&applies==1,"network invalidation prevents stale best selection "+en);
    }
   }RunStage02ProfileNetworkTests();}finally{L.En=english;}
  }
 }
}
