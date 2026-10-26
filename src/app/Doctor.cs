using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 // Doctor: answers «on which layer is the problem», not «here is a log». Evaluate is pure: it reads only DoctorInput, so every layer is testable with fixtures.
 public enum DoctorLevel { Ok, Warning, Fail, Skipped }

 public sealed class DoctorLayer {
  public string Id;public DoctorLevel Level;public string Summary;public string Detail;public Observation Observation;
 }

 public sealed class DoctorReport {
  public DoctorLayer[] Layers=new DoctorLayer[0];
  public string FirstFailedId;   // the first layer in order that failed, null when none did
  public string Verdict;         // The reducer's bounded conclusion; incomplete evidence never implies application readiness.
  public DateTime Utc;
  public string ContextRevision;public Observation[] Observations=Array.Empty<Observation>();
  public ReadinessResult Readiness=new ReadinessResult{State=ReadinessState.Unknown,Reason="MISSING_OBSERVATIONS"};
  public int ExitCode=>Readiness.State==ReadinessState.Failed?1:Readiness.State==ReadinessState.Off||Readiness.State==ReadinessState.LikelyReady?0:2;
  public string ToJson()=>JsonSerializer.Serialize(new{Verdict,FirstFailedId,Utc,ContextRevision,
   Readiness=new{State=Readiness.State.ToString(),Completeness=Readiness.Completeness.ToString(),Readiness.Required,Readiness.Confirmed,Readiness.Missing,Readiness.Reason},
   Layers=Layers.Select(l=>new{l.Id,Level=l.Level.ToString(),l.Summary,l.Detail}).ToArray(),
   Observations=Observations.Select(o=>new{o.Id,o.Layer,o.Scope,o.ClientPath,o.Source,o.Method,o.ObservedUtc,o.ExpiresUtc,o.ContextRevision,Outcome=o.Outcome.ToString(),o.Reason,o.Critical}).ToArray()},Program.JsonOptions);

  // Plain text for the clipboard and the support report: one line per layer, level as a word so it reads without colour.
  public string ToText() {
   var sb=new StringBuilder();
   sb.AppendLine(Verdict);
   sb.AppendLine(Readiness.State+" | "+Readiness.Confirmed+"/"+Readiness.Required+" | "+Utc.ToString("u"));
   foreach(var l in Layers){
    sb.AppendLine("  ["+Doctor.Word(l.Level)+"] "+l.Id+": "+l.Summary);
    if(!string.IsNullOrWhiteSpace(l.Detail))sb.AppendLine("      "+l.Detail);
    if(l.Observation!=null){var o=l.Observation;sb.AppendLine("      "+o.Source+" | "+o.Method+" | "+o.ClientPath+" | "+o.ObservedUtc.ToString("u")+" .. "+o.ExpiresUtc.ToString("u"));}
   }
   foreach(var o in Observations.Where(o=>o.Layer=="http"))sb.AppendLine("  "+o.Id+": "+o.Reason+" | application semantics unknown");
   return sb.ToString();
  }
 }

 // Everything Evaluate reads. Filled from the live system by LiveDoctorProbe, or by hand in tests.
 public sealed class DoctorInput {
  public string ContextRevision;
  public bool ConfigurationKnown;
  public Dictionary<string,Observation> Observations=new Dictionary<string,Observation>(StringComparer.Ordinal);
  public bool? NetworkUp;                                   // null = not asked
  public string JournalPhase;                               // null = no journal (or a journal the program does not know)
  public CrashGuard.Kind Crash;                             // the Crash Guard decision for this start
  public bool BridgeOn;                                     // the bridge is configured: the journal says Active (or the engine reports it active)
  public bool RulesOk;                                      // the engine reports the bridge in effect (Active and «DNS active»)
  public string EngineCode;                                 // the engine's status Code, null when none was given
  public string EngineReason;                               // set when the engine did not answer (e.g. WMI_NOT_RESPONDING): rules, conflicts and answers are then Skipped
  public string[] ForeignConflicts=new string[0];           // NRPT policies for the protected hosts that are not ours
  public LocalPort53.Holder[] Port53=new LocalPort53.Holder[0];
  public string[] Servers=new string[0];                    // the bridge's addresses (from the engine status)
  public string Template;                                   // the bridge's DoH template
  public string[] Targets=new string[0];                    // the protected hosts
  public ProviderProbe Provider;                            // null = the provider was not probed
  public bool? TlsOk;                                       // null = no separate TLS result (the provider probe covers DoH and TLS together)
  public string[] VpnAdapters=new string[0];
  public string[] BrowsersWithSecureDns=new string[0];
 }

 public interface IDoctorProbe {
  Task<DoctorInput> CollectAsync(CancellationToken ct);
 }

 public static class Doctor {
  public const string IdNetwork="network",IdJournal="journal",IdRules="rules",IdConflicts="conflicts",IdPort53="port53",IdProvider="provider",IdAnswers="answers",IdTls="tls",IdVpn="vpn",IdBrowser="browser";

  public static string Word(DoctorLevel l)=>l==DoctorLevel.Ok?L.T("ОК"):l==DoctorLevel.Warning?L.T("Внимание"):l==DoctorLevel.Fail?L.T("Сбой"):L.T("Пропущено");

  static DoctorLayer Layer(string id,DoctorLevel level,string summary,string detail=null)=>new DoctorLayer{Id=id,Level=level,Summary=summary,Detail=detail};
  static DoctorLayer Skip(string id,string why)=>Layer(id,DoctorLevel.Skipped,why);
  const string EngineSilent="Windows не ответил, проверка пропущена";
  const string BridgeOff="Мост выключен, проверка пропущена";

  // The ten layers in their fixed order. Pure: no clock, no system call.
  public static DoctorReport Evaluate(DoctorInput i,DateTime utc) {
   if(i==null)throw new ArgumentNullException(nameof(i));
   var layers=new List<DoctorLayer>();
   bool bridge=i.BridgeOn;
   bool engineSilent=!string.IsNullOrEmpty(i.EngineReason);
   string phase=i.JournalPhase;

   // network: without an adapter that is up nothing below means anything.
   layers.Add(i.NetworkUp==null?Skip(IdNetwork,L.T("Сеть не проверялась"))
     :i.NetworkUp.Value?Layer(IdNetwork,DoctorLevel.Ok,L.T("Сеть есть"))
     :Layer(IdNetwork,DoctorLevel.Fail,L.T("Сети нет: ни один адаптер не подключён")));

   // journal: an interrupted change (a crash in the middle of Apply or Restore) is a failure; the Restore button fixes it.
   bool interrupted=i.Crash==CrashGuard.Kind.InterruptedChange||phase=="Applying"||phase=="Restoring"||phase=="Failed";
   layers.Add(interrupted?Layer(IdJournal,DoctorLevel.Fail,L.T("Прерванное изменение: нажмите «Восстановить»"),L.T("Журнал: ")+(phase??L.T("нет"))+(i.Crash==CrashGuard.Kind.InterruptedChange?L.T(", предыдущий запуск не завершился"):""))
     :Layer(IdJournal,DoctorLevel.Ok,bridge?L.T("Мост активен, журнал в порядке"):L.T("Журнал в порядке")));

   // rules: our own NRPT policies are in place and in effect.
   layers.Add(!bridge?Skip(IdRules,L.T(BridgeOff))
     :engineSilent?Skip(IdRules,L.T(EngineSilent))
     :!i.RulesOk?Layer(IdRules,DoctorLevel.Fail,L.T("Свои правила NRPT отсутствуют или не действуют"),i.EngineCode)
     :Layer(IdRules,DoctorLevel.Ok,L.T("Свои правила NRPT на месте")));

   // conflicts: somebody else's policy for the same hosts takes the answer away from us.
   layers.Add(engineSilent?Skip(IdConflicts,L.T(EngineSilent))
     :i.ForeignConflicts.Length>0?Layer(IdConflicts,DoctorLevel.Warning,L.F("Чужие правила NRPT для тех же доменов: {0}",i.ForeignConflicts.Length),string.Join(", ",i.ForeignConflicts))
     :Layer(IdConflicts,DoctorLevel.Ok,L.T("Чужих правил нет")));

   // port53: a foreign listener never fails the check; the bridge does not use port 53, but the listener can still intercept queries.
   layers.Add(i.Port53.Length>0?Layer(IdPort53,DoctorLevel.Warning,L.F("Порт 53 занят: {0}",LocalPort53.Describe(i.Port53)))
     :Layer(IdPort53,DoctorLevel.Ok,L.T("Порт 53 свободен")));

   // provider: the bridge's addresses answer over DoH and TLS right now.
   ProviderProbe p=i.Provider;
   string probeSkip=!bridge?BridgeOff:p==null?"Провайдер не проверялся":null;
   layers.Add(probeSkip!=null?Skip(IdProvider,L.T(probeSkip))
     :p.Validated.Length==0?Layer(IdProvider,DoctorLevel.Fail,L.T("Ни один адрес провайдера не отвечает по DoH и TLS"))
     :p.Validated.Length<Math.Max(1,i.Servers.Length)?Layer(IdProvider,DoctorLevel.Warning,L.F("Отвечают {0} из {1} адресов провайдера",p.Validated.Length,Math.Max(1,i.Servers.Length)))
     :Layer(IdProvider,DoctorLevel.Ok,L.T("Провайдер отвечает по DoH и TLS")));

   // answers: the protected hosts get an answer from the bridge's addresses.
   // The provider probe measures the catalog's default hosts only; a host of a wider scope (Gemini, Google) that it did not measure is reported, not failed.
   var measured=p==null?new string[0]:i.Targets.Where(t=>p.Answers.ContainsKey(t)).ToArray();
   var unmeasured=p==null?new string[0]:i.Targets.Where(t=>!p.Answers.ContainsKey(t)).ToArray();
   bool answersMissing=measured.Any(t=>p.Answers[t]==null||p.Answers[t].Length==0);
   string notMeasured=unmeasured.Length>0?L.T("Не проверены: ")+string.Join(", ",unmeasured):null;
   layers.Add(probeSkip!=null||engineSilent?Skip(IdAnswers,L.T(probeSkip??EngineSilent))
     :measured.Length==0?Skip(IdAnswers,L.T("Защищённые домены не проверялись"))
     :answersMissing||i.EngineCode=="NRPT_NOT_EFFECTIVE"?Layer(IdAnswers,DoctorLevel.Fail,L.T("Защищённые домены не получили ответа от адресов моста"),notMeasured)
     :unmeasured.Length>0?Layer(IdAnswers,DoctorLevel.Skipped,L.T("Часть защищённых доменов не проверена"),notMeasured)
     :Layer(IdAnswers,DoctorLevel.Ok,L.T("Защищённые домены получают ответ"),notMeasured));

   // tls: only when the probe measured it separately; otherwise the provider layer already says whether TLS works.
   layers.Add(!bridge?Skip(IdTls,L.T(BridgeOff))
     :i.TlsOk==null?Skip(IdTls,L.T("TLS проверяется вместе с провайдером"))
     :i.TlsOk.Value?Layer(IdTls,DoctorLevel.Ok,L.T("TLS к целевым доменам в порядке"))
     :Layer(IdTls,DoctorLevel.Fail,L.T("TLS к целевым доменам не проходит")));

   // vpn and browser: they may bypass the system resolver. Warnings, never failures: the bridge itself may still be fine.
   layers.Add(i.VpnAdapters.Length>0?Layer(IdVpn,DoctorLevel.Warning,L.T("Обнаружен включённый VPN/TUN-адаптер; маршрут не проверен"),string.Join(", ",i.VpnAdapters))
     :Layer(IdVpn,DoctorLevel.Ok,L.T("Включённых VPN/TUN-адаптеров не найдено; маршрут не проверен")));
   layers.Add(i.BrowsersWithSecureDns.Length>0?Layer(IdBrowser,DoctorLevel.Warning,L.T("Secure DNS включён в браузере: браузер может обходить мост"),string.Join(", ",i.BrowsersWithSecureDns))
     :Skip(IdBrowser,L.T("Обход через Secure DNS не подтверждён; запрос браузера не проверен")));

   var arr=layers.ToArray();
   var observations=new List<Observation>();
   foreach(var layer in arr){
    i.Observations.TryGetValue(layer.Id,out var stamp);
    bool critical=layer.Id==IdNetwork||layer.Id==IdJournal||(bridge||!i.ConfigurationKnown)&&(layer.Id==IdRules||layer.Id==IdConflicts||layer.Id==IdProvider||layer.Id==IdAnswers||layer.Id==IdTls&&i.TlsOk.HasValue);
    var o=new Observation{Id=layer.Id,Layer=layer.Id,Scope=(layer.Id==IdProvider||layer.Id==IdAnswers||layer.Id==IdTls)?i.Targets.ToArray():Array.Empty<string>(),
     ClientPath=stamp?.ClientPath,Source=stamp?.Source,Method=stamp?.Method,ObservedUtc=stamp?.ObservedUtc??default,
     ExpiresUtc=stamp?.ExpiresUtc??default,ContextRevision=stamp?.ContextRevision,Critical=critical,
     Outcome=layer.Level==DoctorLevel.Fail?ObservationOutcome.Failed:layer.Level==DoctorLevel.Skipped?ObservationOutcome.Skipped:ObservationOutcome.Passed,
     Reason=layer.Detail??layer.Summary};
    if(stamp==null||stamp.Outcome==ObservationOutcome.Unknown){o.Outcome=ObservationOutcome.Unknown;o.Reason=stamp?.Reason??"MISSING_OBSERVATION";}
    if(!o.HasProvenance||!o.Fresh(utc,i.ContextRevision)||o.Outcome==ObservationOutcome.Unknown){
     layer.Level=DoctorLevel.Skipped;layer.Summary=L.T("Проверка неполна или устарела");layer.Detail=o.Reason;
    }
    layer.Observation=o;observations.Add(o);
   }
   if(i.Provider!=null)foreach(var item in i.Provider.TransportDetails){
    i.Observations.TryGetValue(IdProvider,out var stamp);
    observations.Add(new Observation{Id="http:"+item.Key,Layer="http",Scope=new[]{item.Key},ClientPath="direct-provider",Source="provider HEAD /",Method="TLS-validated HEAD; endpoint application semantics unknown",
     ObservedUtc=stamp?.ObservedUtc??default,ExpiresUtc=stamp?.ExpiresUtc??default,ContextRevision=stamp?.ContextRevision,Outcome=ObservationOutcome.Unknown,Reason=item.Value,Critical=false});
   }
   var readiness=EvidenceReducer.Reduce(observations,i.ContextRevision,utc,bridge,i.ConfigurationKnown);
   var firstFail=arr.FirstOrDefault(l=>l.Level==DoctorLevel.Fail);
   var firstWarn=arr.FirstOrDefault(l=>l.Level==DoctorLevel.Warning);
   bool refused=i.Provider!=null&&i.Provider.TransportDetails.Values.Any(v=>v!=null&&(v.Contains("TLS_OK_HTTP_4")||v.Contains("TLS_OK_HTTP_5")));
   string verdict=firstFail!=null?firstFail.Summary:readiness.State==ReadinessState.Off?L.T("Мост отключён"):
    readiness.State==ReadinessState.Degraded?L.T("Проверка устарела или сеть изменилась"):
    readiness.Completeness!=EvidenceCompleteness.Complete?L.T("Проверка неполна: работа моста не подтверждена"):
    refused?L.T("TLS доступен; HTTP-запрос отклонён. Причина не установлена"):
    firstWarn!=null?firstWarn.Summary:L.T("Сетевые проверки пройдены; работа приложения не проверена");
   return new DoctorReport{Layers=arr,FirstFailedId=firstFail?.Id,Verdict=verdict,Utc=utc,ContextRevision=i.ContextRevision,Observations=observations.ToArray(),Readiness=readiness};
  }

  // Five minutes is a diagnostic snapshot budget, invalidated sooner by network/policy/client-context changes.
  public static readonly TimeSpan Freshness=TimeSpan.FromMinutes(5);
  public static void Record(DoctorInput input,string id,string source,string method,string path,DateTime utc,ObservationOutcome outcome=ObservationOutcome.Passed,string reason=null){
   input.Observations[id]=new Observation{Id=id,Source=source,Method=method,ClientPath=path,ObservedUtc=utc,ExpiresUtc=utc+Freshness,ContextRevision=input.ContextRevision,Outcome=outcome,Reason=reason};
  }

  public static bool ApplyJournal(DoctorInput input,string json,bool present){
   input.JournalPhase=present?Recovery.PhaseOfJournal(json):"Absent";
   input.ConfigurationKnown=!present||input.JournalPhase!=null;
   input.BridgeOn=input.JournalPhase=="Active";
   return input.ConfigurationKnown;
  }

  // Engine status (JSON from `--engine status`) into the input. Pure; a status that is not JSON or a timeout code marks the engine as silent.
  public static void ApplyStatus(DoctorInput i,string statusJson,int exitCode) {
   i.ConfigurationKnown=false;i.EngineReason=null;
   if(exitCode==1460||statusJson!=null&&statusJson.Contains("ENGINE_TIMEOUT")){i.EngineReason="WMI_NOT_RESPONDING";return;}
   JsonElement st;
   try{using(var doc=JsonDocument.Parse(statusJson??"")){st=doc.RootElement.Clone();}}
   catch{i.EngineReason="ENGINE_ERROR";return;}
   if(st.ValueKind!=JsonValueKind.Object){i.EngineReason="ENGINE_ERROR";return;}
   i.EngineCode=Str(st,"Code");
   if(exitCode!=0){
    i.EngineReason=i.EngineCode??"ENGINE_ERROR";
    if(i.EngineReason.StartsWith("STATE_",StringComparison.Ordinal)&&i.Observations.TryGetValue(IdJournal,out var journal)){journal.Outcome=ObservationOutcome.Unknown;journal.Reason=i.EngineReason;}
    return;
   }
   if(!st.TryGetProperty("Active",out var active)||(active.ValueKind!=JsonValueKind.True&&active.ValueKind!=JsonValueKind.False)||string.IsNullOrEmpty(Str(st,"State"))){i.EngineReason="ENGINE_STATUS_INCOMPLETE";return;}
   string phase=Str(st,"JournalPhase");
   if(phase!="Absent"&&phase!="Restored"&&phase!="Applying"&&phase!="Active"&&phase!="Restoring"&&phase!="Failed"){i.EngineReason="ENGINE_STATUS_INCOMPLETE";return;}
   i.ConfigurationKnown=true;
   i.BridgeOn=i.BridgeOn||Str(st,"JournalPhase")=="Active"||Bool(st,"Active");
   i.RulesOk=Bool(st,"Active")&&Str(st,"State")=="DNS active";
   i.Servers=Strings(st,"Servers");
   i.Targets=Strings(st,"Targets");
   i.Template=Str(st,"Template");
   i.ForeignConflicts=Conflicts(st);
  }

  static string Str(JsonElement e,string name)=>e.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
  static bool Bool(JsonElement e,string name)=>e.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.True;
  static string[] Strings(JsonElement e,string name)=>e.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Array?v.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.String).Select(x=>x.GetString()).ToArray():new string[0];
  // a conflict is named by its namespace (the protected host or suffix it covers)
  static string[] Conflicts(JsonElement e)=>!e.TryGetProperty("Conflicts",out var v)||v.ValueKind!=JsonValueKind.Array?new string[0]
   :v.EnumerateArray().Select(c=>c.TryGetProperty("Namespace",out var ns)?(ns.ValueKind==JsonValueKind.Array?string.Join(",",ns.EnumerateArray().Select(x=>x.GetString())):ns.GetString()):null).Where(x=>!string.IsNullOrEmpty(x)).ToArray();

  // Virtual adapters that can take DNS away from the bridge: a tunnel type or a name that says VPN/TUN. Pure over a plain list.
  public static string[] VpnNames(IEnumerable<(string Name,string Description,bool Tunnel,bool Up)> adapters)=>adapters
   .Where(a=>a.Up&&(a.Tunnel||System.Text.RegularExpressions.Regex.IsMatch((a.Name??"")+" "+(a.Description??""),"vpn|tun|tap|wireguard|openvpn|wintun",System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
   .Select(a=>a.Name).ToArray();

  public static async Task<DoctorReport> RunAsync(IDoctorProbe probe,CancellationToken ct) {
   var input=await probe.CollectAsync(ct);
   return Evaluate(input,DateTime.UtcNow);
  }
 }

 // The live data for Doctor. Read-only and unelevated: the engine's status (the same call Health uses), the provider probe of Health, port 53, adapters and browser settings.
 // A source that fails or does not answer leaves its layer Skipped; nothing here throws into the report.
 public sealed class LiveDoctorProbe:IDoctorProbe {
  static string Revision(){
   try{
    var network=EvidenceContext.NetworkRevision();if(network==null)return null;
    var env=new LiveBrowserEnv();
    var browser=string.Join("|",BrowserCatalog.Detect(env).Select(b=>b.Name+":"+BrowserCatalog.Evaluate(b,env).Status).OrderBy(x=>x,StringComparer.Ordinal));
    return EvidenceContext.Hash(network+"|"+Recovery.ReadJournal()+"|"+ProfileCatalog.BuiltIn.Version+"|"+browser+"|native-windows-resolver");
   }catch{return null;}
  }
  public async Task<DoctorInput> CollectAsync(CancellationToken ct) {
   // An address event only counts when the route-relevant network revision really changed (see EvidenceContext.NetworkRevision).
   int changed=0;string startNetwork=EvidenceContext.NetworkRevision();NetworkAddressChangedEventHandler handler=(_,__)=>{if(EvidenceContext.NetworkRevision()!=startNetwork)Interlocked.Increment(ref changed);};
   NetworkChange.NetworkAddressChanged+=handler;
   try{
    var i=new DoctorInput{ContextRevision=Revision()};
    void Record(string id,string source,string method,string path,ObservationOutcome outcome=ObservationOutcome.Passed,string reason=null)=>Doctor.Record(i,id,source,method,path,DateTime.UtcNow,outcome,reason);
    try{i.NetworkUp=NetworkInterface.GetIsNetworkAvailable();Record(Doctor.IdNetwork,"Windows NetworkInterface","GetIsNetworkAvailable","local-host");}
    catch(Exception e){Record(Doctor.IdNetwork,"Windows NetworkInterface","GetIsNetworkAvailable","local-host",ObservationOutcome.Unknown,e.GetType().Name);}
    string journal=null;
    try{journal=File.ReadAllText(Recovery.StatePath);bool known=Doctor.ApplyJournal(i,journal,true);i.Crash=CrashGuard.Current?.Kind??CrashGuard.Kind.None;
     Record(Doctor.IdJournal,"ownership journal","read phase and crash marker","local-host",known?ObservationOutcome.Passed:ObservationOutcome.Unknown,known?null:"JOURNAL_INVALID");}
    catch(FileNotFoundException){Doctor.ApplyJournal(i,null,false);Record(Doctor.IdJournal,"ownership journal","confirmed absent file","local-host");}
    catch(DirectoryNotFoundException){Doctor.ApplyJournal(i,null,false);Record(Doctor.IdJournal,"ownership journal","confirmed absent directory","local-host");}
    catch(Exception e){Doctor.ApplyJournal(i,null,true);Record(Doctor.IdJournal,"ownership journal","read phase","local-host",ObservationOutcome.Unknown,e.GetType().Name);}
    try{
     var (code,output)=await Program.Engine("status",false,false,null,null,null,ct,TimeSpan.FromSeconds(15));
     Doctor.ApplyStatus(i,output,code);
    }catch(OperationCanceledException){throw;}catch{Doctor.ApplyStatus(i,null,1);}
    foreach(string id in new[]{Doctor.IdRules,Doctor.IdConflicts})Record(id,"Windows NRPT engine status","bounded status/readback","windows-system-resolver",i.EngineReason==null?ObservationOutcome.Passed:ObservationOutcome.Unknown,i.EngineReason);
    if(i.BridgeOn&&i.Servers.Length>0&&!string.IsNullOrEmpty(i.Template)){
     try{i.Provider=await new HealthMonitor.Live().Provider(i.Servers,i.Template,ct);}
     catch(OperationCanceledException){throw;}catch{}
    }
    foreach(string id in new[]{Doctor.IdProvider,Doctor.IdAnswers,Doctor.IdTls})Record(id,"provider probe and bounded engine readback","DoH query + certificate-validated target TLS; Windows engine status","direct-provider + windows-policy",i.Provider==null?ObservationOutcome.Unknown:ObservationOutcome.Passed,i.Provider==null?"PROVIDER_NOT_MEASURED":null);
    try{i.Port53=LocalPort53.Find();Record(Doctor.IdPort53,"Windows sockets","local TCP/UDP ownership","local-host");}
    catch(Exception e){Record(Doctor.IdPort53,"Windows sockets","local TCP/UDP ownership","local-host",ObservationOutcome.Unknown,e.GetType().Name);}
    try{
     var nics=NetworkInterface.GetAllNetworkInterfaces().Select(n=>(n.Name,n.Description,n.NetworkInterfaceType==NetworkInterfaceType.Tunnel,n.OperationalStatus==OperationalStatus.Up));
     i.VpnAdapters=Doctor.VpnNames(nics);Record(Doctor.IdVpn,"Windows adapters","active tunnel/name observation","local-host");
    }catch(Exception e){Record(Doctor.IdVpn,"Windows adapters","active tunnel/name observation","local-host",ObservationOutcome.Unknown,e.GetType().Name);}
    try{
     var env=new LiveBrowserEnv();i.BrowsersWithSecureDns=BrowserCatalog.Detect(env).Where(b=>BrowserCatalog.Evaluate(b,env).Status==BypassStatus.Bypasses).Select(b=>b.Name).ToArray();
     Record(Doctor.IdBrowser,"browser settings","installed-browser Secure DNS settings; no browser request proof","browser-settings");
    }catch(Exception e){Record(Doctor.IdBrowser,"browser settings","Secure DNS settings","browser-settings",ObservationOutcome.Unknown,e.GetType().Name);}
    ct.ThrowIfCancellationRequested();
    string after=Revision();if(Volatile.Read(ref changed)!=0||after!=i.ContextRevision)i.ContextRevision=null;
    return i;
   }finally{NetworkChange.NetworkAddressChanged-=handler;}
  }
 }
}
