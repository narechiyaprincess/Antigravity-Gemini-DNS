using System;
using System.Collections.Generic;
using System.Linq;

namespace AntigravityGeminiDns {
 // v1.2 Doctor: every layer from fixtures with the clock passed in. Nothing here reads the system; the live probe is covered by the --doctor run.
 public static partial class OfflineTests {
  static readonly string[] DoctorOrder={"network","journal","rules","conflicts","port53","provider","answers","tls","vpn","browser"};

  static DoctorInput HealthyDoctorInput() {var input=new DoctorInput{
   NetworkUp=true,JournalPhase="Active",BridgeOn=true,ConfigurationKnown=true,RulesOk=true,EngineCode="DNS_CONFIGURED_APPLICATION_NOT_TESTED",
   Servers=new[]{"192.0.2.10","192.0.2.11"},Template="https://dns.example/dns-query",Targets=new[]{"protected.example.test"},
   Provider=new ProviderProbe{Validated=new[]{"192.0.2.10","192.0.2.11"},Answers=new Dictionary<string,string[]>{{"protected.example.test",new[]{"192.0.2.20"}}}}
  };
   input.ContextRevision="fixture-network-policy-provider-client-v1";
   foreach(string id in DoctorOrder)Doctor.Record(input,id,"fixture","deterministic observation","fixture-system-path",DateTime.UtcNow);
   return input;
  }
  static DoctorReport DoctorWith(Action<DoctorInput> change) {var i=HealthyDoctorInput();change(i);return Doctor.Evaluate(i,DateTime.UtcNow);}
  static DoctorLevel LevelOf(DoctorReport r,string id)=>r.Layers.Single(l=>l.Id==id).Level;

  static void RunDoctorTests() {
   var healthy=DoctorWith(i=>{});
   Check(healthy.Layers.Select(l=>l.Id).SequenceEqual(DoctorOrder),"doctor: ten layers in the fixed order");
   Check(healthy.FirstFailedId==null&&healthy.Readiness.State==ReadinessState.LikelyReady&&healthy.Readiness.Completeness==EvidenceCompleteness.Complete&&healthy.Verdict==L.T("Сетевые проверки пройдены; работа приложения не проверена"),"doctor: complete network evidence is likely ready, with the application explicitly unverified");
   Check(LevelOf(healthy,"browser")==DoctorLevel.Skipped,"doctor: absent browser settings evidence cannot prove no Secure DNS bypass");
   Check(!healthy.Layers.Single(l=>l.Id=="vpn").Summary.Contains("Активен VPN"),"doctor: adapter inventory does not assert an active VPN route");
   Check(LevelOf(healthy,"tls")==DoctorLevel.Skipped&&healthy.Layers.All(l=>l.Level!=DoctorLevel.Fail),"doctor: TLS is skipped (not measured separately) and nothing fails when healthy");

   // one failure per layer, each on its own input
   Check(LevelOf(DoctorWith(i=>i.NetworkUp=false),"network")==DoctorLevel.Fail&&DoctorWith(i=>i.NetworkUp=false).FirstFailedId=="network","doctor: no network is a failure of the network layer");
   Check(LevelOf(DoctorWith(i=>i.NetworkUp=null),"network")==DoctorLevel.Skipped,"doctor: a network that was not asked is skipped, not failed");
   var crashed=DoctorWith(i=>i.Crash=CrashGuard.Kind.InterruptedChange);
   Check(LevelOf(crashed,"journal")==DoctorLevel.Fail&&crashed.Verdict==L.T("Прерванное изменение: нажмите «Восстановить»"),"doctor: an interrupted change is a journal failure with the Restore instruction");
   Check(LevelOf(DoctorWith(i=>{i.JournalPhase="Applying";i.BridgeOn=false;}),"journal")==DoctorLevel.Fail,"doctor: a journal left in Applying is a failure even with the bridge off");
   Check(LevelOf(DoctorWith(i=>i.RulesOk=false),"rules")==DoctorLevel.Fail,"doctor: own NRPT rules that are not in effect fail the rules layer");
   Check(LevelOf(DoctorWith(i=>i.ForeignConflicts=new[]{"sample.example"}),"conflicts")==DoctorLevel.Warning,"doctor: a foreign NRPT policy is a conflicts warning");
   Check(LevelOf(DoctorWith(i=>i.Provider.Validated=new string[0]),"provider")==DoctorLevel.Fail,"doctor: no provider address answering over DoH and TLS fails the provider layer");
   Check(LevelOf(DoctorWith(i=>i.Provider.Validated=new[]{"192.0.2.10"}),"provider")==DoctorLevel.Warning,"doctor: one of two provider addresses answering is a warning");
   Check(LevelOf(DoctorWith(i=>i.Provider.Answers["protected.example.test"]=new string[0]),"answers")==DoctorLevel.Fail,"doctor: a protected host without any answer fails the answers layer");
   var wider=DoctorWith(i=>i.Targets=new[]{"protected.example.test","wider.example.test"});
   Check(LevelOf(wider,"answers")==DoctorLevel.Skipped&&wider.Readiness.Completeness==EvidenceCompleteness.Partial&&wider.Layers.Single(l=>l.Id=="answers").Detail.Contains("wider.example.test"),"doctor: unmeasured critical scope is partial, never a complete success or measured failure");
   Check(LevelOf(DoctorWith(i=>{i.Targets=new[]{"wider.example.test"};i.Provider.Answers.Clear();}),"answers")==DoctorLevel.Skipped,"doctor: when no protected host was measured the answers layer is skipped");
   Check(LevelOf(DoctorWith(i=>i.TlsOk=false),"tls")==DoctorLevel.Fail&&LevelOf(DoctorWith(i=>i.TlsOk=true),"tls")==DoctorLevel.Ok,"doctor: a separately measured TLS result decides the tls layer");
   Check(LevelOf(DoctorWith(i=>i.VpnAdapters=new[]{"Wintun"}),"vpn")==DoctorLevel.Warning,"doctor: an active VPN/TUN adapter is a warning");
   Check(LevelOf(DoctorWith(i=>i.BrowsersWithSecureDns=new[]{"Chrome"}),"browser")==DoctorLevel.Warning,"doctor: a browser with its own Secure DNS is a warning");

   // a foreign port 53 listener is never a failure
   var port=DoctorWith(i=>i.Port53=new[]{new LocalPort53.Holder{Protocol="UDP",Address="127.0.0.1",Port=53,Pid=4242,Process="sample-dns"}});
   Check(LevelOf(port,"port53")==DoctorLevel.Warning&&port.FirstFailedId==null,"doctor: a listener on port 53 is a warning and never a failure");
   var portAndDown=DoctorWith(i=>{i.Port53=new[]{new LocalPort53.Holder{Protocol="TCP",Address="127.0.0.1",Port=53,Pid=7,Process="x"}};i.Provider.Validated=new string[0];});
   Check(portAndDown.FirstFailedId=="provider","doctor: with a port 53 listener and a dead provider the failure is the provider, not the port");

   // the first failure wins over later ones
   var two=DoctorWith(i=>{i.Crash=CrashGuard.Kind.InterruptedChange;i.Provider.Validated=new string[0];});
   Check(two.FirstFailedId=="journal","doctor: the first failing layer in order is the one named");

   // a bridge that is off: the bridge layers are skipped and the verdict says so, not an error
   var off=DoctorWith(i=>{i.BridgeOn=false;i.JournalPhase=null;i.RulesOk=false;i.Provider=null;i.Targets=new string[0];});
   Check(new[]{"rules","provider","answers","tls"}.All(id=>LevelOf(off,id)==DoctorLevel.Skipped),"doctor: with the bridge off rules, provider, answers and tls are skipped");
   Check(off.FirstFailedId==null&&off.Verdict==L.T("Мост отключён"),"doctor: with the bridge off the verdict is «bridge is off», not a failure");

   // an engine that did not answer: the layers that need it are skipped, not failed
   var silent=DoctorWith(i=>{i.EngineReason="WMI_NOT_RESPONDING";i.RulesOk=false;i.ForeignConflicts=new string[0];i.Provider=null;});
   Check(LevelOf(silent,"rules")==DoctorLevel.Skipped&&LevelOf(silent,"conflicts")==DoctorLevel.Skipped&&LevelOf(silent,"answers")==DoctorLevel.Skipped&&silent.FirstFailedId==null,"doctor: a WMI timeout skips the engine-based layers instead of failing them");

   // the engine status (JSON) into the input, from the shape the engine prints
   var st=new DoctorInput();
   Doctor.ApplyStatus(st,"{\"State\":\"DNS active\",\"Code\":\"DNS_CONFIGURED_APPLICATION_NOT_TESTED\",\"Active\":true,\"JournalPhase\":\"Active\",\"Servers\":[\"192.0.2.10\",\"192.0.2.11\"],\"Template\":\"https://dns.example/dns-query\",\"Targets\":[\"protected.example.test\"],\"Conflicts\":[{\"Namespace\":[\"sample.example\"],\"NameServers\":[\"10.0.0.1\"]}]}",0);
   Check(st.BridgeOn&&st.RulesOk&&st.Servers.Length==2&&st.Targets.Length==1&&st.Template.StartsWith("https://")&&st.ForeignConflicts.SequenceEqual(new[]{"sample.example"})&&st.EngineReason==null,"doctor: the engine status fills bridge, rules, servers, targets and foreign conflicts");
   var drift=new DoctorInput();
   Doctor.ApplyStatus(drift,"{\"State\":\"Failed (restorable)\",\"Code\":\"NRPT_NOT_EFFECTIVE\",\"Active\":false,\"JournalPhase\":\"Active\"}",0);
   Check(drift.BridgeOn&&!drift.RulesOk&&drift.EngineCode=="NRPT_NOT_EFFECTIVE","doctor: a bridge the journal says is active but the status does not confirm is configured and not in effect");
   var none=new DoctorInput();
   Doctor.ApplyStatus(none,"{\"State\":\"Not configured\",\"Code\":\"ROLLED_BACK\",\"Active\":false,\"JournalPhase\":\"Absent\"}",0);
   Check(!none.BridgeOn&&!none.RulesOk,"doctor: no bridge in the status means the bridge layers are off");
   var wmi=new DoctorInput();Doctor.ApplyStatus(wmi,null,1460);
   Check(wmi.EngineReason=="WMI_NOT_RESPONDING","doctor: exit 1460 from the engine is a silent WMI, not an engine error");
   var timeout=new DoctorInput();Doctor.ApplyStatus(timeout,"{\"Code\":\"ENGINE_TIMEOUT\"}",0);
   Check(timeout.EngineReason=="WMI_NOT_RESPONDING","doctor: an ENGINE_TIMEOUT answer is a silent WMI");
   var garbage=new DoctorInput();Doctor.ApplyStatus(garbage,"not json",0);
   Check(garbage.EngineReason=="ENGINE_ERROR"&&!garbage.BridgeOn,"doctor: a status that is not JSON is an engine error, nothing is assumed");

   var missing=DoctorWith(i=>i.Provider=null);
   Check(missing.Readiness.State==ReadinessState.Partial&&missing.ExitCode==2&&missing.Verdict!=L.T("Всё работает"),"doctor evidence: missing provider is incomplete, CLI returns unknown rather than success");
   Check(silent.Readiness.State==ReadinessState.Partial&&silent.ExitCode==2,"doctor evidence: WMI timeout is unknown evidence, never a measured provider failure or success");
   Check(DoctorWith(i=>i.NetworkUp=null).Readiness.Completeness==EvidenceCompleteness.Partial,"doctor evidence: skipped critical network cannot be complete");
   Check(DoctorWith(i=>i.Targets=Array.Empty<string>()).Readiness.State!=ReadinessState.LikelyReady,"doctor evidence: empty scope cannot pass vacuously");
   var now=DateTime.UtcNow;var oldInput=HealthyDoctorInput();
   foreach(var stamp in oldInput.Observations.Values){stamp.ObservedUtc=now-Doctor.Freshness;stamp.ExpiresUtc=now;}
   var expired=Doctor.Evaluate(oldInput,now);
   Check(expired.Readiness.State==ReadinessState.Degraded&&expired.ExitCode==2,"doctor evidence: expiry boundary is stale, not fresh");
   var changedInput=HealthyDoctorInput();changedInput.ContextRevision="different-network";
   Check(Doctor.Evaluate(changedInput,DateTime.UtcNow).Readiness.State==ReadinessState.Degraded,"doctor evidence: old network/policy revision cannot confirm the new context");
   var noMetadata=HealthyDoctorInput();noMetadata.Observations.Clear();
   Check(Doctor.Evaluate(noMetadata,DateTime.UtcNow).Readiness.State==ReadinessState.Unknown,"doctor evidence: missing provenance and time are unknown, not stamped fresh during evaluation");
   var missingSource=HealthyDoctorInput();missingSource.Observations["network"].Source="";
   Check(Doctor.Evaluate(missingSource,DateTime.UtcNow).Readiness.State==ReadinessState.Partial,"doctor evidence: timestamps alone cannot replace the required source/method/client-path provenance");
   var futureInput=HealthyDoctorInput();foreach(var stamp in futureInput.Observations.Values){stamp.ObservedUtc=now.AddMinutes(1);stamp.ExpiresUtc=now.AddMinutes(6);}
   Check(Doctor.Evaluate(futureInput,now).Readiness.State==ReadinessState.Degraded,"doctor evidence: future observations cannot pass after a clock rollback");
   foreach(int status in new[]{401,403,429,503}){
    var refused=DoctorWith(i=>i.Provider.TransportDetails["protected.example.test"]="TLS_OK_HTTP_"+status);
    Check(refused.Readiness.State==ReadinessState.LikelyReady&&refused.Verdict.Contains(L.T("Причина не установлена"))&&refused.Observations.Any(o=>o.Layer=="http"&&o.Outcome==ObservationOutcome.Unknown),"doctor evidence: HTTP "+status+" preserves TLS proof without inventing application or region success");
   }
   var failedAndMissing=DoctorWith(i=>{i.NetworkUp=false;i.Provider=null;});
   Check(failedAndMissing.Readiness.State==ReadinessState.Failed&&failedAndMissing.FirstFailedId=="network"&&failedAndMissing.ExitCode==1,"doctor evidence: measured current failure takes precedence over unknown");
   var localUnknown=DoctorWith(i=>i.Observations["browser"].Outcome=ObservationOutcome.Unknown);
   Check(LevelOf(localUnknown,"browser")==DoctorLevel.Skipped,"doctor evidence: failed source collection never becomes a clean browser observation");
   var projection=missing.ToJson();using(var json=System.Text.Json.JsonDocument.Parse(projection)){
    Check(json.RootElement.GetProperty("Verdict").GetString()==missing.Verdict&&missing.ToText().StartsWith(missing.Verdict)&&json.RootElement.GetProperty("Readiness").GetProperty("State").GetString()==missing.Readiness.State.ToString(),"doctor evidence: CLI JSON, UI report and copied text use one reducer result");
   }
   Check(healthy.Observations.Where(o=>o.Critical).All(o=>o.Fresh(healthy.Utc,healthy.ContextRevision)&&!string.IsNullOrEmpty(o.Source)&&!string.IsNullOrEmpty(o.Method)),"doctor evidence: complete result exposes source, method, path, times and revision for each critical observation");
   foreach(string journal in new[]{"{broken","{\"Phase\":\"future-phase\"}",null}){
    var unknownJournal=HealthyDoctorInput();bool readable=Doctor.ApplyJournal(unknownJournal,journal,true);
    unknownJournal.Observations["journal"].Outcome=ObservationOutcome.Unknown;
    Doctor.ApplyStatus(unknownJournal,"{\"Code\":\"STATE_INVALID\"}",1);
    var r=Doctor.Evaluate(unknownJournal,DateTime.UtcNow);
    Check(!readable&&!unknownJournal.ConfigurationKnown&&r.Readiness.State==ReadinessState.Unknown&&r.ExitCode==2&&r.Verdict!=L.T("Мост отключён")&&unknownJournal.EngineReason=="STATE_INVALID","doctor evidence: existing corrupt/unreadable/future journal is unknown ownership, never confirmed Off");
   }
   var absentJournal=HealthyDoctorInput();Doctor.ApplyJournal(absentJournal,null,false);
   Doctor.ApplyStatus(absentJournal,"{\"State\":\"Not configured\",\"Active\":false,\"JournalPhase\":\"Absent\"}",0);
   Check(Doctor.Evaluate(absentJournal,DateTime.UtcNow).Readiness.State==ReadinessState.Off,"doctor evidence: fresh confirmed journal absence and inactive engine remain Off");
   var absentWmi=HealthyDoctorInput();Doctor.ApplyJournal(absentWmi,null,false);Doctor.ApplyStatus(absentWmi,null,1460);
   Check(Doctor.Evaluate(absentWmi,DateTime.UtcNow).ExitCode==2,"doctor evidence: absent journal plus unavailable Windows status cannot prove Off");
   var absentEngineFailure=HealthyDoctorInput();Doctor.ApplyJournal(absentEngineFailure,null,false);Doctor.ApplyStatus(absentEngineFailure,null,1);
   Check(Doctor.Evaluate(absentEngineFailure,DateTime.UtcNow).ExitCode==2&&!absentEngineFailure.ConfigurationKnown,"doctor evidence: thrown engine startup errors use the same unknown-configuration path");
   var schemaRejected=HealthyDoctorInput();Doctor.ApplyJournal(schemaRejected,"{\"Schema\":2,\"Phase\":\"Active\"}",true);
   Doctor.ApplyStatus(schemaRejected,"{\"Code\":\"STATE_INVALID\"}",1);
   Check(LevelOf(Doctor.Evaluate(schemaRejected,DateTime.UtcNow),"journal")==DoctorLevel.Skipped,"doctor evidence: engine schema rejection invalidates phase-only journal observation");

   // virtual adapters: a tunnel type or a VPN/TUN name that is up; a down adapter never counts
   var vpn=Doctor.VpnNames(new[]{("Ethernet","Intel Ethernet",false,true),("Wintun Userspace Tunnel","WireGuard",true,true),("OpenVPN TAP","TAP-Windows",false,true),("Wi-Fi","VPN-like name",false,false),("vEthernet","Hyper-V virtual",false,true)});
   Check(vpn.SequenceEqual(new[]{"Wintun Userspace Tunnel","OpenVPN TAP"}),"doctor: VPN/TUN adapters are found by tunnel type or name, only when up");

   // the text for the clipboard and the support report: the verdict first, every layer with its word
   var text=DoctorWith(i=>i.Crash=CrashGuard.Kind.InterruptedChange).ToText();
   Check(text.StartsWith(L.T("Прерванное изменение: нажмите «Восстановить»"))&&text.Contains("[")&&DoctorOrder.All(id=>text.Contains(" "+id+": ")),"doctor: the text starts with the verdict and names every layer");
  }
 }
}
