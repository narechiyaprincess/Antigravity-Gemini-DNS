using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 public sealed partial class MainForm {
  LiveAssistedBackend assistedBackend;AssistedCoordinator assisted;
  internal static string ConnectionReplacementNote(AssistedResult outcome,string selected,bool english) {
   if(outcome.Outcome!=AssistedOutcome.NativeVerified||outcome.Attempts.IsDefaultOrEmpty||outcome.Attempts.Length<2)return null;
   string applied=outcome.Attempts.Last().Profile;
   return applied==selected?null:english?selected+" is unavailable. Connected with "+applied+".":selected+" недоступен. Подключено через "+applied+".";
  }
  internal void ShowConnectionUnavailable(AssistedResult result) {
   bool timedOut=result.Code=="CONNECTION_SELECTION_TIMEOUT";lastCode=result.Code;lastInstruction=null;
   if(needsRestore&&isActive&&!recoveryNeeded) {
    // Nothing was changed (both codes are no-write refusals): the bridge that worked keeps working.
    Title(()=>L.T("Мост подключён"));
    Detail(()=>L.En?"Could not switch the profile; the current bridge stays connected.":"Не удалось переключить профиль; текущий мост остался подключён.");
    UpdateDetailsText();UpdateButtons();return;
   }
   Title(()=>needsRestore?(L.En?"Bridge needs repair":"Мост требует исправления"):L.T("Мост отключён"));
   Detail(()=>timedOut?(L.En?"The provider check took too long. Retry or open diagnostics.":"Проверка провайдера заняла слишком много времени. Повторите или откройте диагностику."):
    (L.En?"None of the allowed profiles responded. Retry or open diagnostics.":"Разрешённые профили не ответили. Повторите или откройте диагностику."));
   UpdateDetailsText();UpdateButtons();
  }
  internal static string AssistedDisplayCode(AssistedResult result) {
   if(result.Outcome==AssistedOutcome.RecoveredVerified)return "ASSISTED_BASELINE_RESTORED";
   if(result.Outcome==AssistedOutcome.StalePreview)return "ASSISTED_CONTEXT_CHANGED";
   if(result.Outcome==AssistedOutcome.Suspended)return "ASSISTED_NETWORK_SUSPENDED";
   if(result.Code=="UNFINISHED_NATIVE_JOURNAL")return "RECOVERY_REQUIRED";
   if(result.Code=="FOREIGN_POLICY_CONFLICT")return "NRPT_CONFLICT";
   string underlying=null;
   if(!string.IsNullOrWhiteSpace(result.Output)) {
    try{using var doc=JsonDocument.Parse(result.Output);if(doc.RootElement.ValueKind==JsonValueKind.Object&&doc.RootElement.TryGetProperty("Code",out var code)&&code.ValueKind==JsonValueKind.String)underlying=code.GetString();}catch(JsonException){underlying=result.Output;}
   }
   string token=CodeTable.TokenOf(underlying);
   if(token=="ASSISTED_NETWORK_CHANGED")return "ASSISTED_NETWORK_SUSPENDED";
   if(token=="ASSISTED_PREVIEW_STALE"||token=="ASSISTED_PREVIEW_EXPIRED"||token=="ASSISTED_PROVIDER_CHANGED")return "ASSISTED_CONTEXT_CHANGED";
   if(CodeTable.Known(underlying))return CodeTable.TokenOf(underlying);
   return CodeTable.Known(result.Code)?CodeTable.TokenOf(result.Code):"ASSISTED_PREPARE_REFUSED";
  }
  bool AssistedTrust(AssistedPrepared prepared) {
   string key=ConnectionTrust.PreparedKey(prepared);
   if(ConnectionTrust.Has(key))return true;
   if(ConnectionTrust.IsCatalogProvider(prepared.Request)&&ConnectionTrust.CatalogConsent){ConnectionTrust.Remember(key);return true;}
   if(ConnectionTrust.Has(ConnectionTrust.RequestKey(prepared.Request))&&ConnectionTrust.CoveredPins(prepared)){ConnectionTrust.Remember(key);return true;}
   var provider=prepared.Compile.Providers.Single();
   string detail=L.T("Провайдер получит DNS-запросы выбранных доменов и может направлять HTTPS через свой сервер-посредник. TLS-сертификаты продолжат проверяться. Подключить этот маршрут?")+"\n\n"+
    provider.EndpointIdentity+"\n"+string.Join(", ",provider.Bootstrap)+"\n\n"+string.Join("\n",prepared.Request.Targets);
   if(prepared.Before.Active)detail+="\n\n"+L.T("Текущий маршрут этого моста будет заменён. Чужие правила сохраняются.");
   if(!ProviderConsentForm.Confirm(this,L.T("Подтверждение доверия"),detail))return false;
   ConnectionTrust.Remember(key);return true;
  }
  internal async Task RevokeAssistedTrust(){
   if(busy)return;
   ConnectionTrust.Revoke();offer=null;
   Log(L.T("Доверие к провайдерам отозвано."));
   // Revoking consent also stops an applied route through the ordinary, journalled
   // restore path. A refused UAC/restore must remain visibly unconfirmed.
   if(needsRestore){
    await Mutate("restore");
    if(needsRestore)Log(L.En?"Trust is revoked, but Windows has not confirmed route removal. Disconnect the bridge to finish.":"Доверие отозвано, но Windows не подтвердила удаление маршрута. Завершите отключение моста.");
   }
   UpdateButtons();
  }
  bool AuthorizeConnectionSet(AssistedRequest[] requests,AssistedPrepared prepared=null) {
   // Bind consent to the metadata observed before opening a modal. A catalog change
   // while the choice is open must not acquire a new grant after the user clicks Allow.
   var requestKeys=requests.Select(ConnectionTrust.RequestKey).ToArray();
   if(requests.All(ConnectionTrust.Covers))return true;
   if(requests.All(ConnectionTrust.IsCatalogProvider)) {
    string catalog=L.T("Мост направляет DNS-запросы выбранных сервисов через провайдеров встроенного каталога. Провайдер видит, к каким сервисам вы обращаетесь, и может пропускать соединение через свой сервер-посредник; TLS-сертификаты сервисов проверяются всегда, поэтому подменить сайт он не может.\n\nРазрешение спрашивается один раз и действует для всех профилей каталога. Отозвать его можно в «Настройках».")+
     "\n\n"+string.Join("\n",ProfileCatalog.BuiltIn.Profiles.Select(p=>"• "+p.Id));
    if(!ProviderConsentForm.Confirm(this,L.T("Разрешённые провайдеры"),catalog))return false;
    ConnectionTrust.RememberCatalog();foreach(var requestKey in requestKeys)ConnectionTrust.Remember(requestKey);
    if(prepared!=null)ConnectionTrust.Remember(ConnectionTrust.PreparedKey(prepared));return true;
   }
   string names=string.Join("\n",requests.Select(r=>"• "+DisplayProfile(r.Profile)+" — "+r.Template+"\n  "+string.Join(", ",prepared?.Request.Profile==r.Profile?prepared.Compile.Providers.Single().Bootstrap:r.Servers)));
   string detail=L.T("Разрешить этим провайдерам обработку запросов выбранных сервисов? Если выбранный профиль недоступен, мост проверит разрешённые альтернативы. TLS-сертификаты проверяются. Доверие можно отозвать в настройках.")+"\n\n"+names+"\n\n"+string.Join(", ",requests[0].Targets);
   if(!ProviderConsentForm.Confirm(this,L.T("Разрешённые провайдеры"),detail))return false;
   foreach(var requestKey in requestKeys)ConnectionTrust.Remember(requestKey);
   if(prepared!=null)ConnectionTrust.Remember(ConnectionTrust.PreparedKey(prepared));return true;
  }
  internal AssistedRequest[] ConnectionCandidates(AssistedRequest selected) {
   // Legacy Pool entries have only provider ID and timestamp: no scope, network or catalog fingerprint.
   // They cannot establish comparable evidence. Keep catalog order until measurements carry that context.
   var alternatives=profiles.Where(x=>!x.Custom&&!x.Experimental&&x.Name!=selected.Profile&&x.Servers.Length>0);
   return new[]{selected}.Concat(alternatives.Select(x=>selected with{Profile=x.Name,Template=x.Template,Servers=x.Servers.ToImmutableArray(),Mode=AssistedMode.Native})).Take(4).ToArray();
  }
  // Self-repair: a confirmed drift the repair covers (own rules gone, provider down, resolver mismatch) is repaired without a click whenever
  // Windows will not ask (the program runs with administrator rights, e.g. «Запуск от администратора»); otherwise the yellow «Починить» waits
  // for one click. The repair is the ordinary journalled, compensated enable that may move to another allowed provider. At most once in
  // 5 minutes and three times an hour; never during another operation, after a manual disconnect, with an offer open or while recovery is pending.
  readonly System.Collections.Generic.List<DateTime> autoRepairs=new();
  internal Func<bool> CanRepairSilently=()=>Program.IsAdmin();
  internal async Task<bool> AutoRepairAsync(HealthReport r){
   var now=DateTime.UtcNow;autoRepairs.RemoveAll(t=>now-t>TimeSpan.FromHours(1));
   if(smoke&&MutateHook==null||busy||!known||!needsRestore||recoveryNeeded||offer!=null||assisted!=null&&(assisted.ManualOff||assisted.RecoveryPending))return false;
   if(!CanRepairSilently()||autoRepairs.Count>=3||autoRepairs.Count>0&&now-autoRepairs[^1]<TimeSpan.FromMinutes(5))return false;
   autoRepairs.Add(now);Log("Автопочинка моста ("+r.Code+"): без запроса Windows.");
   try{await Mutate("enable",true);}catch(Exception e){Log("Автопочинка не удалась: "+e.Message);}
   return true;
  }
  async Task Mutate(string action,bool repair=false) {
   if(MutateHook!=null){await MutateHook(action,repair);return;}
   if(smoke)throw new InvalidOperationException("Synthetic UI requires a mutation hook.");
   if(busy)return;
   if(action!="enable"&&action!="restore")throw new ArgumentException("Unsupported assisted action");
   var p=Current;bool geminiAtStart=Program.Gemini;PerfLog.Mark("mutate "+action+" begin");
   if(action=="enable"&&p.Retired){Log("Профиль «"+p.Name+"» больше не входит в каталог: выберите другой профиль.");return;}
   var transition=System.Diagnostics.Stopwatch.StartNew();
   string transitionPhase="snapshot",transitionOutcome="interrupted";
   long phaseStarted=0;
   long evidenceAtStart=Volatile.Read(ref evidenceSerial);string networkAtStart=NetworkRevisionForEvidence();
   if(assistedBackend==null) {
    assistedBackend=new LiveAssistedBackend{SavedAddressSource=template=>SavedAddresses.Find(settings.SavedAddrs,template)};assisted=new AssistedCoordinator(assistedBackend);
   }
   assistedBackend.Progress=phase=>{
    if(IsDisposed)return;
    long elapsed=transition.ElapsedMilliseconds;
    Log("Transition "+transitionPhase+" completed ms="+(elapsed-phaseStarted));
    transitionPhase=phase;phaseStarted=elapsed;
    Title(()=>phase=="apply"||phase=="disable"?L.T("Подтвердите запрос Windows…"):phase=="verify"?L.T("Проверяю результат…"):L.T("Проверяю провайдера…"));
   };
   currentCts?.Cancel();currentCts?.Dispose();currentCts=new CancellationTokenSource();var token=currentCts.Token;
   SetBusy(true);offer=null;directVerified=false;serviceReport=null;
   Log(action=="restore"?"Отключение моста…":repair?"Починка моста: "+p.Name:"Подготовка маршрута: "+p.Name);
   if(action=="enable")Log(L.En?"Validating the selected endpoint for this network; the full benchmark is not repeated.":"Проверяю выбранный endpoint для текущей сети; общий замер не повторяется.");
   try {
    var mode=action=="restore"?AssistedMode.Disable:repair||needsRestore?AssistedMode.Native:AssistedMode.Auto;
    var request=new AssistedRequest(mode,p.SelectionId,p.Template,p.Servers.ToImmutableArray(),Program.Targets.ToImmutableArray(),Program.Gemini,Program.Google,repair,Program.Core);
    AssistedResult outcome;
    if(action=="enable") {
     var requests=ConnectionCandidates(request);
     bool? permission=null;
     bool Permit(AssistedPrepared prepared=null)=>permission??(permission=AuthorizeConnectionSet(requests,prepared)).Value;
     string appliedProfile=p.SelectionId;
     // Consent is asked before the run starts: time spent reading the dialog used to eat the 70 s candidate deadline and end a
     // consented switch in CONNECTION_SELECTION_TIMEOUT.
     if(!Permit())outcome=new AssistedResult(AssistedOutcome.Refused,"EXPLICIT_TRUST_DECLINED",false,"NONE",null);
     else outcome=await assisted.RunCandidates(requests,(preview,ct)=>Task.FromResult(!ct.IsCancellationRequested&&Permit(preview)&&AssistedTrust(preview)),
      candidate=>Permit()&&ConnectionTrust.Covers(candidate),(candidate,index)=>{
       appliedProfile=candidate.Profile;
       if(index>0){Log("Fallback: "+candidate.Profile);Title(()=>L.T("Профиль недоступен. Проверяю другой…"));}
      },token);
     if(outcome.Outcome==AssistedOutcome.NativeVerified&&appliedProfile!=p.SelectionId) {
      int selected=profiles.FindIndex(x=>x.SelectionId==appliedProfile);if(selected>=0){p=profiles[selected];profileSelect.SetSelectedSilently(selected);settings.Profile=p.SelectionId;SaveSettings();UpdateDetailsText();Log("Применён разрешённый альтернативный профиль: "+p.Name);}
     }
    }else outcome=await assisted.Run(request,null,token);
    transitionOutcome=outcome.Code;
    if(IsDisposed)return;
    if(!outcome.Attempts.IsDefaultOrEmpty)foreach(var attempt in outcome.Attempts){Log("Connect candidate "+attempt.Profile+": "+attempt.Code);if(!string.IsNullOrWhiteSpace(attempt.Detail))LogBlock(attempt.Detail);}
    bool verified=outcome.Outcome==AssistedOutcome.NativeVerified||outcome.Outcome==AssistedOutcome.DisabledVerified;
    if(verified&&assistedBackend.LastSnapshot!=null) {
     using var snapshot=JsonDocument.Parse(assistedBackend.LastSnapshot.Output);
     using var operation=JsonDocument.Parse(string.IsNullOrWhiteSpace(outcome.Output)?"{}":outcome.Output);
     var status=snapshot.RootElement.GetProperty("Status");checksPassed=outcome.Outcome==AssistedOutcome.NativeVerified;
     Settle(JsonSerializer.Serialize(new{State=status.GetProperty("State").GetString(),Status=status,Operation=operation.RootElement},Program.JsonOptions),evidenceAtStart,networkAtStart);
     ConnectionOutcome(()=>ConnectionReplacementNote(outcome,request.Profile,L.En)?.Replace(AppSettings.CustomProfileId,settings.CustomName));
     if(outcome.Outcome==AssistedOutcome.NativeVerified&&assistedBackend.AddressPlan!=null) {
      var plan=assistedBackend.AddressPlan;
      if(plan.Source=="found")SavedAddresses.Put(settings.SavedAddrs,p.Template,plan.Servers,plan.FoundUtc);
      settings.LastAddrSource=plan.Source;settings.LastAddrTime=plan.FoundUtc==default?DateTime.UtcNow.ToString("o"):plan.FoundUtc.ToString("o");
      settings.LastAddrServers=string.Join(",",plan.Servers);settings.LastAddrTemplate=p.Template;SaveSettings();
     }
     lastCode="";lastInstruction=null;Log(outcome.Code);
    }else if(outcome.Outcome==AssistedOutcome.DirectVerified) {
     ShowJournalHint();checksPassed=false;directVerified=true;manualPause=false;networkEvidenceUtc=DateTime.UtcNow;Title(()=>L.T("Мост отключён"));
     Detail(()=>L.En?"Direct access is available. Windows DNS was not changed.":"Прямой доступ работает. DNS Windows не менялся.");
     Log("Direct: IPv4 DNS/TCP/TLS/HTTP подтверждены через текущую сеть; приложение и IPv6 этим не проверены. Windows не изменялась.");
    }else if(outcome.Outcome==AssistedOutcome.RecoveryRequired) {
     ShowJournalHint();recoveryNeeded=true;needsRestore=true;checksPassed=false;InvalidateNetworkEvidence();
     string unconfirmed=Recovery.UnconfirmedJson(outcome.Code,Recovery.ReadJournal());ShowOperationFailure(unconfirmed,geminiAtStart);
     LogBlock(unconfirmed);
    }else {
     ShowJournalHint();checksPassed=false;Log(outcome.Code);if(!string.IsNullOrWhiteSpace(outcome.Output))LogBlock(outcome.Output);
     if(outcome.Outcome==AssistedOutcome.CancelledNoChange||outcome.Code=="EXPLICIT_TRUST_DECLINED"){Title(()=>BridgeTitle);Detail(()=>L.T("Операция отменена"));}
     else if(outcome.Outcome!=AssistedOutcome.NoChange) {
      if(outcome.Code=="ALL_PROVIDERS_UNAVAILABLE"||outcome.Code=="CONNECTION_SELECTION_TIMEOUT")ShowConnectionUnavailable(outcome);
      else {string failure=AssistedDisplayCode(outcome);ShowFailure(failure,null,geminiAtStart);}
     }
    }
    if(outcome.Outcome==AssistedOutcome.DisabledVerified){manualPause=true;directVerified=false;Title(()=>L.T("Мост отключён"));}
    history.Append(repair?"repair":action,outcome.Code,verified||outcome.Outcome==AssistedOutcome.DirectVerified,p.Custom?"custom":p.Name);
   }catch(Exception e)when(!(e is OperationCanceledException)&&!IsDisposed&&IsHandleCreated){
    // Windows may already have been changed: once busy is cleared, read the real state back instead of keeping the state from before the operation.
    Log("Операция прервана ошибкой: "+e.GetType().Name);BeginInvoke(new Action(()=>{_=RefreshStatus();}));throw;
   }finally {
    PerfLog.Mark("mutate "+action+" end ("+transitionOutcome+", "+transition.ElapsedMilliseconds+" ms)");if(!IsDisposed)Log("Transition "+transitionPhase+" completed ms="+(transition.ElapsedMilliseconds-phaseStarted)+"; totalMs="+transition.ElapsedMilliseconds+" outcome="+transitionOutcome);
    if(!IsDisposed){SetBusy(false);if(action=="enable"&&healthSched!=null&&!assisted.ManualOff&&!assisted.RecoveryPending)healthSched.Trigger(HealthTrigger.Manual);}
   }
  }
 }
}
