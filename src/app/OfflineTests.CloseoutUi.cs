using System;
using System.Diagnostics;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace AntigravityGeminiDns {
 public static partial class OfflineTests {
  static void CloseoutPump(Task task){var sw=Stopwatch.StartNew();while(!task.IsCompleted&&sw.Elapsed<TimeSpan.FromSeconds(5)){MainForm.PumpBrowserMessages();Thread.Sleep(5);}Check(task.IsCompleted,"closeout report finishes within bounded test deadline");if(!task.IsCompleted)throw new TimeoutException("Closeout report task exceeded its deadline");task.GetAwaiter().GetResult();}
  static void RunCloseoutUiTests(){
   bool previous=L.En;bool gemini=Program.Gemini,google=Program.Google;
   try{
    foreach(string alias in new[]{"dns.malw.link"," DNS.MALW.LINK ","P1"," p1 "}){
     var old=new AppSettings{Profile=alias,CustomName=alias,CustomServers="8.8.8.8",CustomTemplate="https://dns.google/dns-query"};
     var loaded=SettingsRecovery.Parse(JsonSerializer.Serialize(old,Program.JsonOptions)).Settings;
     Check(loaded.Profile==AppSettings.CustomProfileId&&loaded.CustomTemplate==old.CustomTemplate,"closeout identity: legacy colliding name retains custom endpoint "+alias);
     var migrated=LegacyScenarioAdapter.Migrate(ProfileCatalog.BuiltIn,loaded);
     Check(migrated.Providers.Any(p=>p.Id==migrated.SelectedProvider&&p.EndpointIdentity==old.CustomTemplate),"closeout identity: migration resolves explicit custom endpoint "+alias);
     loaded.Profile=ProfileCatalog.BuiltIn.Default;
     var again=SettingsRecovery.Parse(JsonSerializer.Serialize(loaded,Program.JsonOptions)).Settings;
     Check(again.Profile==ProfileCatalog.BuiltIn.Default,"closeout identity: explicit builtin remains builtin after restart "+alias);
     var imported=ConfigFile.Import(ConfigFile.Export(again),new AppSettings());
     Check(imported.Ok&&imported.Applied.Profile==again.Profile,"closeout identity: new export/import retains explicit builtin "+alias);
    }
    using(var customForm=new MainForm(true)){
     var preferences=(AppSettings)typeof(MainForm).GetField("settings",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(customForm);
     preferences.CustomName="dns.malw.link";preferences.CustomServers="8.8.8.8";preferences.CustomTemplate="https://dns.google/dns-query";preferences.Profile=AppSettings.CustomProfileId;preferences.ProfileIdentityVersion=1;
     customForm.ApplyLanguage(false);Check(customForm.CollectReportInput().CustomProfile,"closeout identity: actual form selects colliding custom profile");
     var measurement=new BenchRun{StartedUtc=DateTime.UtcNow.ToString("o"),Synthetic=true,Results=new[]{new ProfileResult{Id=AppSettings.CustomProfileId,Template=preferences.CustomTemplate,Final=true,MedianMs=11,P95Ms=12,TlsMs=13,Successes=3,Attempts=3},new ProfileResult{Id="dns.malw.link",Template=preferences.CustomTemplate,Final=true,MedianMs=999,P95Ms=999,TlsMs=999}}};
     string metrics=customForm.PrecisionMetricsForTest(measurement);Check(metrics.Contains("11")&&!metrics.Contains("999"),"closeout identity: custom benchmark metrics use explicit selection ID");
     customForm.ApplyLanguage(true);Check(customForm.CollectReportInput().CustomProfile,"closeout identity: language refresh preserves custom selection without duplicate benchmark IDs");
     preferences.Profile=ProfileCatalog.BuiltIn.Default;customForm.ApplyLanguage(false);Check(!customForm.CollectReportInput().CustomProfile,"closeout identity: actual form can select ordinary builtin with colliding custom retained");
     preferences.CustomName="edited";preferences.Profile=AppSettings.CustomProfileId;customForm.ApplyLanguage(true);Check(customForm.CollectReportInput().CustomProfile,"closeout identity: editing display name retains custom selection");
     preferences.CustomName=preferences.CustomTemplate=preferences.CustomServers="";preferences.Profile=ProfileCatalog.BuiltIn.Default;customForm.ApplyLanguage(false);Check(!customForm.CollectReportInput().CustomProfile,"closeout identity: deleted custom safely selects builtin");
    }
    var builtin=ProfileCatalog.BuiltIn.DefaultProfile;
    var builtinRequest=new AssistedRequest(AssistedMode.Native,builtin.Id,builtin.Template,builtin.Servers.ToImmutableArray(),Program.Targets.ToImmutableArray(),Program.Gemini,Program.Google);
    var builtIdentity=LiveAssistedBackend.PrepareProviderIdentity(builtinRequest,builtinRequest.Servers);
    var customRequest=builtinRequest with{Profile=AppSettings.CustomProfileId,Template="https://dns.google/dns-query",Servers=ImmutableArray.Create("8.8.8.8")};
    var customIdentity=LiveAssistedBackend.PrepareProviderIdentity(customRequest,customRequest.Servers);
    Check(builtIdentity.Migration.SelectedProvider==builtin.Id&&builtIdentity.Provider.Id==builtin.Id&&builtIdentity.Provider.EndpointIdentity==builtin.Template,"closeout native identity: builtin preparation retains builtin provider metadata");
    Check(customIdentity.Provider.Id.StartsWith("custom.")&&customIdentity.Provider.OperatorIdentity!=builtIdentity.Provider.OperatorIdentity&&customIdentity.Provider.EndpointIdentity==customRequest.Template&&customIdentity.Provider.ObservedCapabilities.IsEmpty,"closeout native identity: custom endpoint receives separate unverified operator and no inherited observations");
    Reject(()=>LiveAssistedBackend.PrepareProviderIdentity(builtinRequest with{Template=customRequest.Template},customRequest.Servers),"closeout native identity: builtin ID cannot substitute another endpoint");
    Check(ConnectionTrust.RequestKey(builtinRequest)!=ConnectionTrust.RequestKey(customRequest),"closeout native identity: custom selection cannot reuse builtin request consent");
    var fixture=PolicyFixture();var binding=fixture.Bindings[0] with{ScenarioId=builtIdentity.Migration.SelectedScenario,ProviderId=builtIdentity.Provider.Id,AuthorizedScopes=builtinRequest.Targets};
    var pack=RuntimeCatalog.Select(builtIdentity.Migration.Pack,DateTimeOffset.UtcNow).Pack;
    var prepared=new AssistedPrepared(builtinRequest,new AssistedSnapshot("fixture-network","fixture-revision","fixture","Absent",false,false,"{}"),fixture with{Pack=pack,Bindings=ImmutableArray.Create(binding),Providers=ImmutableArray.Create(builtIdentity.Provider)},null,DateTimeOffset.UtcNow.AddMinutes(5));
    Check(ConnectionTrust.CoveredPins(prepared),"closeout native identity: canonical builtin prepared metadata agrees with request consent");
    string scratch=Path.Combine(Path.GetTempPath(),"agdns-closeout-settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
    try{
     string path=Path.Combine(scratch,"settings.json");var settings=new AppSettings{Language="ru"};Check(settings.TrySaveTo(path),"closeout save: initial atomic save");string original=File.ReadAllText(path);settings.Language="en";
     using(var locked=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Check(!settings.TrySaveTo(path)&&settings.LastSaveError=="IOException","closeout save: locked target reports explicit failure");
     Check(File.ReadAllText(path)==original&&Directory.GetFiles(scratch,"*.tmp").Length==0,"closeout save: failed replace preserves previous bytes and removes owned temporary file");
     File.SetAttributes(path,FileAttributes.ReadOnly);try{Check(!settings.TrySaveTo(path),"closeout save: read-only target reports failure");}finally{File.SetAttributes(path,FileAttributes.Normal);}
     Check(File.ReadAllText(path)==original,"closeout save: read-only failure preserves previous bytes");
     Check(!settings.TrySaveTo(scratch),"closeout save: invalid directory target fails honestly");
     Check(settings.TrySaveTo(path)&&settings.LastSaveError==null&&SettingsRecovery.Parse(File.ReadAllText(path)).Settings.Language=="en","closeout save: retry succeeds and survives reload");
    }finally{Directory.Delete(scratch,true);}
    using(var toggle=new ToggleRow{Text="Notify"}){
     toggle.CreateControl();Check((toggle.AccessibilityObject.State&AccessibleStates.Checked)==0,"closeout accessibility: toggle initially unchecked");
     toggle.Checked=true;Check((toggle.AccessibilityObject.State&AccessibleStates.Checked)!=0&&toggle.AccessibilityObject.Value=="On","closeout accessibility: programmatic setting exposes checked/value");
     toggle.AccessibilityObject.DoDefaultAction();Check(!toggle.Checked,"closeout accessibility: default action toggles");
     toggle.Enabled=false;toggle.AccessibilityObject.DoDefaultAction();Check(!toggle.Checked&&(toggle.AccessibilityObject.State&AccessibleStates.Unavailable)!=0,"closeout accessibility: disabled state rejects action");
    }
    using(var segmented=new Segmented{Items=new[]{"Core","Gemini","Browser"}}){segmented.CreateControl();segmented.SelectedIndex=2;Check(segmented.AccessibilityObject.Value=="Browser"&&segmented.AccessibilityObject.GetChildCount()==3&&(segmented.AccessibilityObject.GetSelected().State&AccessibleStates.Selected)!=0,"closeout accessibility: segmented exposes selected child/value");segmented.AccessibilityObject.GetChild(1).DoDefaultAction();Check(segmented.SelectedIndex==1,"closeout accessibility: segmented accessible selection works");segmented.Busy=true;segmented.AccessibilityObject.GetChild(2).DoDefaultAction();Check(segmented.SelectedIndex==1&&(segmented.AccessibilityObject.GetChild(2).State&AccessibleStates.Unavailable)!=0,"closeout accessibility: busy selector cannot be changed through accessible action");segmented.Busy=false;segmented.Items=new[]{"Основной","Gemini","Браузер"};Check(segmented.AccessibilityObject.Value=="Gemini","closeout accessibility: language refresh keeps selected value");}
    using(var select=new PillSelect()){select.Items.AddRange(new[]{"First","Second"});select.CreateControl();select.SetSelectedSilently(1);Check(select.AccessibilityObject.Value=="Second"&&select.AccessibilityObject.GetSelected().Name=="Second","closeout accessibility: silent settings restoration exposes selector value/selection");select.AccessibilityObject.GetChild(0).DoDefaultAction();Check(select.SelectedIndex==0,"closeout accessibility: selector accessible action works");}
    for(int scenario=0;scenario<7;scenario++){
     using(var form=new MainForm(true)){
      form.JournalReader=()=>null;int diagnoses=0,previews=0;CancellationToken observed=default;
      var gate=new TaskCompletionSource<DoctorReport>(TaskCreationOptions.RunContinuationsAsynchronously);
      form.ReportDiagnosisSource=ct=>{diagnoses++;return Task.FromResult((0,"{\"Code\":\"OK\"}"));};
      form.DoctorSource=ct=>{observed=ct;return gate.Task;};form.ReportRedactionSource=ct=>Task.FromResult(new RedactionContext());form.ReportPreviewSource=text=>previews++;
      if(scenario==3)form.ReportDeadline=TimeSpan.FromMilliseconds(30);
      var running=form.SupportReportForTest();
      Check(form.Busy&&form.ReportRunningForTest,"closeout report: busy covers Doctor stage "+scenario);
      form.SupportReportForTest().GetAwaiter().GetResult();Check(diagnoses==1,"closeout report: repeated click does not start duplicate "+scenario);
      if(scenario==1)form.InvalidateNetworkEvidence();
      if(scenario==6){var prefs=(AppSettings)typeof(MainForm).GetField("settings",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);prefs.Profile=ProfileCatalog.BuiltIn.Profiles[1].Id;form.ApplyLanguage(L.En);}
      if(scenario==2)form.Dispose();
      if(scenario==4)gate.SetException(new IOException("fixture"));
      else if(scenario==5){typeof(MainForm).GetMethod("ProcessCmdKey",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,new object[]{new Message(),Keys.Escape});}
      else if(scenario!=3)gate.SetResult(new DoctorReport{Verdict="fixture",Utc=DateTime.UtcNow});
      CloseoutPump(running);
      Check(previews==(scenario==0?1:0),"closeout report: only current successful context previews "+scenario);
      Check(!form.ReportRunningForTest&&(form.IsDisposed||!form.Busy),"closeout report: all outcomes clear running/busy "+scenario);
      if(scenario==0){CloseoutPump(form.SupportReportForTest());Check(previews==2&&diagnoses==2,"closeout report: successful completion permits a new independent report");}
      if(scenario==2||scenario==3||scenario==5)Check(observed.IsCancellationRequested,"closeout report: lifetime/deadline/Esc cancel token "+scenario);
     }
    }
    using(var failed=new MainForm(true)){failed.JournalReader=()=>throw new IOException("fixture snapshot failure");CloseoutPump(failed.SupportReportForTest());Check(!failed.Busy&&!failed.ReportRunningForTest,"closeout report: snapshot exception releases operation guard");}
    L.En=true;
    var input=new ReportInput{GeneratedUtc=DateTime.UtcNow};string en=SupportReport.Build(input);
    Check(!en.Any(c=>c>='А'&&c<='я')&&en.Contains("Support report")&&en.Contains("not run"),"closeout localization: English support headings and critical status are translated");
    string recovery=Recovery.BuildInstruction("{\"Owner\":\""+new string('a',32)+"\",\"Phase\":\"Restoring\",\"Rules\":[],\"Doh\":[]}");
    Check(recovery!=null&&!recovery.Any(c=>c>='А'&&c<='я')&&recovery.Contains("Comment = AgDns:")&&recovery.Contains("Restart"),"closeout localization: English recovery includes complete ownership/restart instructions");
    using(var preview=new ReportPreviewForm(en))Check(preview.Box.TabStop&&preview.Box.ReadOnly,"closeout accessibility: support preview can be reached and scrolled by keyboard");
   }finally{L.En=previous;Program.Gemini=gemini;Program.Google=google;}
  }
 }
}
