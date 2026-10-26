using System;
using System.Drawing;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 public static partial class OfflineTests {
  static void RunS11Tests(){
   bool previous=L.En;bool google=Program.Google,gemini=Program.Gemini;
   try{
    foreach(bool en in new[]{false,true}){
     L.En=en;
     using(var f=new MainForm(true)){
      f.Show();Application.DoEvents();
      foreach(string state in new[]{"idle","active","active-unchecked","recovery","conflict","drift","wmi"}){
       f.SmokeState(state);
       Check(!f.ServiceReadyForTest&&f.StatusInkForTest!=Theme.Green,en+" "+state+" has no app readiness from policy alone");
       Check(f.ServiceEvidenceForTest.Length==5&&f.ServiceEvidenceForTest.All(x=>!string.IsNullOrEmpty(x)),"separate connection/readiness/trust/time/mode "+state);
      }
      var replacement=new AssistedResult(AssistedOutcome.NativeVerified,"DNS_CONFIGURED_APPLICATION_NOT_TESTED",true,"NATIVE",null,Attempts:ImmutableArray.Create(new AssistedAttempt("selected.example","BOOTSTRAP_TIMEOUT","fixture"),new AssistedAttempt("applied.example","DNS_CONFIGURED_APPLICATION_NOT_TESTED","fixture")));
      f.SmokeState("active");
      f.ConnectionOutcome(()=>MainForm.ConnectionReplacementNote(replacement,"selected.example",L.En));
      Check(f.SubText.Contains("selected.example")&&f.SubText.Contains("applied.example")&&f.SubText.Contains(en?"unavailable":"недоступен"),"successful fallback explains the selected and applied profiles inline "+en);
      f.ApplyLanguage(!en);Check(f.SubText.Contains(!en?"unavailable":"недоступен"),"fallback explanation follows language changes "+en);f.ApplyLanguage(en);
      f.SetBusyForTest(true);f.SetBusyForTest(false);Check(!f.SubText.Contains("selected.example"),"a new operation clears previous fallback explanation "+en);
      foreach(string code in new[]{"ALL_PROVIDERS_UNAVAILABLE","CONNECTION_SELECTION_TIMEOUT"}){
       f.SmokeState("idle");f.ShowConnectionUnavailable(new AssistedResult(AssistedOutcome.Refused,code,false,"NONE",null));
       Check(f.StatusText==(en?"Bridge disconnected":"Мост отключён")&&f.BridgeText==(en?"Retry":"Повторить")&&!f.OpenBenchmarkLinkForTest&&f.SubText.Contains(code=="CONNECTION_SELECTION_TIMEOUT"?(en?"too long":"слишком много"):(en?"allowed profiles":"Разрешённые профили")),code+" keeps disconnected bridge with compact reason and Retry; diagnostics stays in Benchmark "+en);
      }
      f.SmokeState("idle");
      bool keyboard=f.ServiceKeyboardForTest();
      Check(keyboard,"keyboard primary/services/profile forward and reverse focus order and accessible labels "+en+(keyboard?"":" — "+f.ServiceKeyboardDiagnosticsForTest));
      foreach(int dpi in new[]{120,144,192,96}){UiSurfaceCheck.ChangeDpi(f,dpi);Check(f.ServiceScrollReachableForTest(),"navigation and Doctor accessible by scroll at "+dpi+" dpi "+en);Check(f.DetailsFooterReachableForTest(),"details copy/report/failover footer fits native client at "+dpi+" dpi "+en);}
      var completion=new TaskCompletionSource<DoctorReport>();f.DoctorSource=ct=>completion.Task;
      var task=f.DoctorForTest();Check(f.DoctorRunningForTest&&f.KeyForTest(Keys.Escape),"Esc cancels a running Doctor");
      completion.SetResult(new DoctorReport{Utc=DateTime.UtcNow,Readiness=new ReadinessResult{State=ReadinessState.Ready}});
      Check(PumpUntil(()=>task.IsCompleted)&&!f.ServiceReadyForTest,"cancelled late Doctor cannot turn readiness green");
      f.DoctorSource=ct=>Task.FromResult(new DoctorReport{Utc=DateTime.UtcNow.AddHours(-1),Readiness=new ReadinessResult{State=ReadinessState.Ready}});
      Check(PumpUntil(()=>f.DoctorForTest().IsCompleted)&&!f.ServiceReadyForTest,"expired Doctor cannot turn readiness green");
      completion=new TaskCompletionSource<DoctorReport>();f.DoctorSource=ct=>completion.Task;task=f.DoctorForTest();
      f.SetScopeForTest(MainForm.ScopeNow==MainForm.BridgeScope.Core?MainForm.BridgeScope.CoreGemini:MainForm.BridgeScope.Core);
      completion.SetResult(new DoctorReport{Utc=DateTime.UtcNow,Readiness=new ReadinessResult{State=ReadinessState.Ready,Completeness=EvidenceCompleteness.Complete,Required=1,Confirmed=1}});
      Check(PumpUntil(()=>task.IsCompleted)&&!f.ServiceReadyForTest,"context change rejects late Doctor readiness");
      string pack="pack-a";f.CatalogRevisionSource=()=>pack;f.SetBusyForTest(false);
      completion=new TaskCompletionSource<DoctorReport>();f.DoctorSource=ct=>completion.Task;task=f.DoctorForTest();pack="pack-b";
      completion.SetResult(new DoctorReport{Utc=DateTime.UtcNow,Readiness=new ReadinessResult{State=ReadinessState.Ready,Completeness=EvidenceCompleteness.Complete,Required=1,Confirmed=1}});
      Check(PumpUntil(()=>task.IsCompleted)&&!f.ServiceReadyForTest,"selected catalog generation change rejects late Doctor readiness");
      Check(f.ServiceEvidenceForTest.Last().Contains(RuntimeCatalog.Refresh().Pack.Revision),"selected catalog revision visible in main mode row");
      completion=new TaskCompletionSource<DoctorReport>();f.DoctorSource=ct=>completion.Task;task=f.DoctorForTest();
      f.Hide();f.ShowWindow();Application.DoEvents();Check(f.Visible&&f.DoctorRunningForTest,"hide/reopen preserves running Doctor");
      f.Close();completion.SetResult(new DoctorReport{Utc=DateTime.UtcNow,Readiness=new ReadinessResult{State=ReadinessState.Ready}});
      Check(PumpUntil(()=>task.IsCompleted)&&f.IsDisposed,"close tolerates late Doctor completion");
     }
    }
   }finally{L.En=previous;Program.Google=google;Program.Gemini=gemini;}
  }
 }
}
