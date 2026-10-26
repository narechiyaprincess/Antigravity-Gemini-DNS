using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 public static partial class OfflineTests {
  sealed class AssistedFixture:IAssistedBackend {
   public AssistedSnapshot State=new("network-a","state-a","baseline-a","Absent",false,false,"{}");
   public bool DirectPass,Portal,VerifyPass=true,RecoveryPass=true,ChangeOnApply=true;
   public int Captures,Prepares,Applies,Disables,Recovers,Directs;public AssistedChild Child=new(0,"{}",true,true);
   public string ProofLevel="NATIVE_POLICY_AND_RESOLVER";public DateTimeOffset Now=PolicyNow;
   public TimeSpan ApplyDelay;
   public Action<AssistedFixture> CaptureHook,PrepareHook,ApplyHook;
   public Action<AssistedRequest> RequestPrepareHook;
   public TaskCompletionSource<bool> PrepareStarted,PrepareRelease,ApplyStarted,ApplyRelease;
   public AssistedProof Proof(bool pass,string level,AssistedSnapshot s,string reason="fixture")=>new(pass,level,"synthetic-native-fixture",s.Network,s.Revision,Now.AddSeconds(-1),Now.AddMinutes(5),reason);
   public Task<AssistedSnapshot> Capture(AssistedRequest r,CancellationToken ct){ct.ThrowIfCancellationRequested();Captures++;CaptureHook?.Invoke(this);return Task.FromResult(State);}
   public Task<AssistedProof> Direct(AssistedRequest r,AssistedSnapshot before,CancellationToken ct){Directs++;return Task.FromResult(Proof(DirectPass,"TRANSPORT_ONLY",before,Portal?"CAPTIVE_PORTAL_OR_NETWORK_UNCERTAIN":"fixture"));}
   public async Task<AssistedPrepared> Prepare(AssistedRequest r,AssistedSnapshot before,CancellationToken ct) {
    Prepares++;RequestPrepareHook?.Invoke(r);PrepareStarted?.TrySetResult(true);if(PrepareRelease!=null)await PrepareRelease.Task.WaitAsync(ct);PrepareHook?.Invoke(this);
    var request=PolicyFixture() with{Grants=ImmutableArray<TrustGrant>.Empty,Context=new PolicyContext(before.Network,before.Revision,ImmutableArray<PolicyResource>.Empty)};
    return new(r,before,request,PolicyCompiler.Compile(request,Now),Now.AddMinutes(5));
   }
   public async Task<AssistedChild> Apply(AssistedPrepared p,CancellationToken ct) {
    Applies++;ApplyStarted?.TrySetResult(true);if(ApplyRelease!=null)await ApplyRelease.Task.WaitAsync(ct);if(ApplyDelay>TimeSpan.Zero)await Task.Delay(ApplyDelay,ct);ApplyHook?.Invoke(this);
    PolicyCompiler.ValidatePreview(p.Preview,p.Compile,Now);
    if(ChangeOnApply&&Child.MayHaveChanged)State=State with{Revision="after",Baseline="changed",Phase="Active",Active=true};return Child;
   }
   public Task<AssistedChild> Disable(AssistedRequest r,AssistedSnapshot before,CancellationToken ct){Disables++;State=State with{Revision="disabled",Phase="Restored",Active=false};return Task.FromResult(Child);}
   public Task<AssistedProof> Verify(AssistedRequest r,AssistedSnapshot current,CancellationToken ct)=>Task.FromResult(Proof(VerifyPass,r.Mode==AssistedMode.Disable?"NATIVE_OFF":ProofLevel,current));
   public Task<AssistedProof> Recover(AssistedPrepared p,CancellationToken ct){Recovers++;if(RecoveryPass)State=p.Before;return Task.FromResult(Proof(RecoveryPass,"BASELINE_RESTORED",State));}
  }
  static AssistedRequest AssistedRequestFixture(AssistedMode mode=AssistedMode.Native) {
   var p=PolicyFixture().Providers.Single();return new(mode,p.Id,p.EndpointIdentity,p.Bootstrap,PolicyFixture().Bindings.Single().AuthorizedScopes);
  }
  static Task<bool> AcceptAssisted(AssistedPrepared p,CancellationToken ct)=>Task.FromResult(true);
  static void RunAssistedContractsCore() {
   RunCloseoutRecoveryContracts();
   RunConnectionCandidatesContracts();
   foreach(var mode in new[]{AssistedMode.DoNothing,AssistedMode.Observe}) {
    var f=new AssistedFixture();var c=new AssistedCoordinator(f,()=>f.Now);var r=c.Run(AssistedRequestFixture(mode),AcceptAssisted).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.NoChange&&!r.Changed&&f.Applies==0&&f.Disables==0&&f.Recovers==0,"assisted no-change path "+mode);
    if(mode==AssistedMode.DoNothing)Check(f.Captures==0,"assisted do nothing has zero backend work");
   }
   foreach(var mode in new[]{AssistedMode.Auto,AssistedMode.Direct}) {
    var f=new AssistedFixture{DirectPass=true};int trust=0;var r=new AssistedCoordinator(f,()=>f.Now).Run(AssistedRequestFixture(mode),(p,t)=>{trust++;return Task.FromResult(true);}).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.DirectVerified&&!r.Changed&&r.ClaimedLevel=="TRANSPORT_ONLY"&&f.Prepares==0&&f.Applies==0&&trust==0,"assisted direct first no trust/elevation "+mode);
   }
   {
    var f=new AssistedFixture{DirectPass=true,State=new("network-a","state-a","baseline-a","Active",true,false,"{}")};
    var r=new AssistedCoordinator(f,()=>f.Now).Run(AssistedRequestFixture(AssistedMode.Direct),AcceptAssisted).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.NoChange&&f.Directs==0&&f.Applies==0,"assisted active policy never labelled Direct");
   }
   {
    var f=new AssistedFixture{Portal=true};var r=new AssistedCoordinator(f,()=>f.Now).Run(AssistedRequestFixture(AssistedMode.Auto),AcceptAssisted).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.Suspended&&f.Prepares==0&&f.Applies==0,"assisted uncertain portal suspends without repair");
   }
   {
    var f=new AssistedFixture();var r=new AssistedCoordinator(f,()=>f.Now).Run(AssistedRequestFixture(),(p,t)=>Task.FromResult(false)).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.Refused&&f.Applies==0&&f.State.Revision=="state-a","assisted explicit trust refusal preserves baseline");
   }
   {
    var f=new AssistedFixture();var c=new AssistedCoordinator(f,()=>f.Now);var r=c.Run(AssistedRequestFixture(),AcceptAssisted).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.NativeVerified&&r.ClaimedLevel=="NATIVE_POLICY_AND_RESOLVER"&&f.Applies==1&&f.Recovers==0,"assisted successful current-path postcheck");
   }
   foreach(var proof in new[]{false,true}) {
    var f=new AssistedFixture{VerifyPass=proof,ProofLevel="TRANSPORT_ONLY"};var r=new AssistedCoordinator(f,()=>f.Now).Run(AssistedRequestFixture(),AcceptAssisted).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.RecoveredVerified&&f.Recovers==1,"assisted transport/failed proof cannot fill native level");
   }
   {
    var f=new AssistedFixture();f.ApplyHook=x=>x.State=x.State with{Network="changed-during-apply"};
    var r=new AssistedCoordinator(f,()=>f.Now).Run(AssistedRequestFixture(),AcceptAssisted).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.RecoveredVerified&&f.Recovers==1,"assisted network transition during apply cannot claim verified success");
   }
   {
    var f=new AssistedFixture{Child=new(1223,"UAC_CANCELLED",true,false)};var c=new AssistedCoordinator(f,()=>f.Now);
    var r=c.Run(AssistedRequestFixture(AssistedMode.Disable),AcceptAssisted).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.Refused&&!r.Changed&&!c.RecoveryPending&&c.ManualOff,"assisted disable UAC refusal no mutation or false recovery claim");
   }
   foreach(var child in new[]{new AssistedChild(1223,"UAC_CANCELLED",true,false),new AssistedChild(1,"STALE_PREPARE",true,false)}) {
    var f=new AssistedFixture{Child=child};var r=new AssistedCoordinator(f,()=>f.Now).Run(AssistedRequestFixture(),AcceptAssisted).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.Refused&&!r.Changed&&f.Recovers==0&&f.State.Revision=="state-a","assisted native refusal without mutation");
   }
   foreach(var recover in new[]{false,true}) {
    var f=new AssistedFixture{Child=new(1,"PARTIAL_APPLY",true,true),RecoveryPass=recover};var r=new AssistedCoordinator(f,()=>f.Now).Run(AssistedRequestFixture(),AcceptAssisted).GetAwaiter().GetResult();
    Check(r.Outcome==(recover?AssistedOutcome.RecoveredVerified:AssistedOutcome.RecoveryRequired)&&f.Recovers==1,"assisted partial apply recovery truth "+recover);
   }
   {
    var f=new AssistedFixture{Child=new(1460,"CHILD_MAY_BE_RUNNING",false,true)};var c=new AssistedCoordinator(f,()=>f.Now);var r=c.Run(AssistedRequestFixture(),AcceptAssisted).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.RecoveryRequired&&f.Recovers==0&&c.RecoveryPending,"assisted timeout never races compensation");
    r=c.Run(AssistedRequestFixture(AssistedMode.Disable),AcceptAssisted).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.RecoveryRequired&&f.Disables==0&&c.ManualOff,"assisted uncertain child blocks subsequent restore and keeps manual-off intent");
   }
   {
    var f=new AssistedFixture();f.PrepareHook=x=>x.State=x.State with{Revision="edited"};var r=new AssistedCoordinator(f,()=>f.Now).Run(AssistedRequestFixture(),AcceptAssisted).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.StalePreview&&f.Applies==0,"assisted native context changed during prepare");
   }
   {
    var f=new AssistedFixture();f.CaptureHook=x=>{if(x.Captures==2)x.State=x.State with{Network="network-b"};};var r=new AssistedCoordinator(f,()=>f.Now).Run(AssistedRequestFixture(),AcceptAssisted).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.StalePreview&&f.Applies==0,"assisted network transition refuses stale preview");
   }
   {
    var f=new AssistedFixture();var r=new AssistedCoordinator(f,()=>f.Now).Run(AssistedRequestFixture(),(p,t)=>{f.Now=f.Now.AddMinutes(6);return Task.FromResult(true);}).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.StalePreview&&f.Applies==0,"assisted preview expires during trust decision");
   }
   {
    var f=new AssistedFixture();using var cancel=new CancellationTokenSource();cancel.Cancel();var r=new AssistedCoordinator(f,()=>f.Now).Run(AssistedRequestFixture(),AcceptAssisted,cancel.Token).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.CancelledNoChange&&f.Captures==0&&f.Applies==0,"assisted cancellation before lock preserves baseline");
   }
   {
    var f=new AssistedFixture();using var cancel=new CancellationTokenSource();var r=new AssistedCoordinator(f,()=>f.Now).Run(AssistedRequestFixture(),(p,t)=>{cancel.Cancel();return Task.FromResult(true);},cancel.Token).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.CancelledNoChange&&f.Applies==0&&f.State.Revision=="state-a","assisted cancellation immediately before mutation");
   }
   foreach(var mode in new[]{AssistedMode.Native,AssistedMode.Disable}) {
    var f=new AssistedFixture();using var cancel=new CancellationTokenSource();f.CaptureHook=x=>{if(x.Captures==2)cancel.Cancel();};
    var r=new AssistedCoordinator(f,()=>f.Now).Run(AssistedRequestFixture(mode),AcceptAssisted,cancel.Token).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.CancelledNoChange&&f.Applies==0&&f.Disables==0,"assisted cancellation at final capture cannot start child "+mode);
   }
   {
    var f=new AssistedFixture{PrepareStarted=new(TaskCreationOptions.RunContinuationsAsynchronously),PrepareRelease=new(TaskCreationOptions.RunContinuationsAsynchronously)};
    var c=new AssistedCoordinator(f,()=>f.Now);var connecting=c.Run(AssistedRequestFixture(),AcceptAssisted);f.PrepareStarted.Task.GetAwaiter().GetResult();
    var disabling=c.Run(AssistedRequestFixture(AssistedMode.Disable),AcceptAssisted);f.PrepareRelease.SetResult(true);
    var first=connecting.GetAwaiter().GetResult();var second=disabling.GetAwaiter().GetResult();
    Check(first.Outcome==AssistedOutcome.CancelledNoChange&&second.Outcome==AssistedOutcome.DisabledVerified&&f.Applies==0&&f.Disables==1,"assisted serialized manual off supersedes queued connect before writes");
    var observed=c.Run(AssistedRequestFixture(AssistedMode.Observe),AcceptAssisted).GetAwaiter().GetResult();
    Check(c.ManualOff&&observed.Code=="MANUAL_OFF"&&f.Applies==0,"assisted health observe cannot undo manual off");
   }
   {
    var f=new AssistedFixture{ApplyStarted=new(TaskCreationOptions.RunContinuationsAsynchronously),ApplyRelease=new(TaskCreationOptions.RunContinuationsAsynchronously)};
    var c=new AssistedCoordinator(f,()=>f.Now);using var cancel=new CancellationTokenSource();var task=c.Run(AssistedRequestFixture(),AcceptAssisted,cancel.Token);
    f.ApplyStarted.Task.GetAwaiter().GetResult();cancel.Cancel();var r=task.GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.RecoveryRequired&&f.Recovers==0,"assisted cancel after child launch is uncertain");
   }
   foreach(var phase in new[]{"Applying","Restoring","Failed"}) {
    var f=new AssistedFixture();f.State=f.State with{Phase=phase};var r=new AssistedCoordinator(f,()=>f.Now).Run(AssistedRequestFixture(),AcceptAssisted).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.Refused&&f.Applies==0,"assisted unfinished journal refuses enable "+phase);
   }
   {
    var f=new AssistedFixture();f.State=f.State with{ForeignConflict=true};var r=new AssistedCoordinator(f,()=>f.Now).Run(AssistedRequestFixture(AssistedMode.Auto),AcceptAssisted).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.Refused&&f.Directs==0&&f.Applies==0,"assisted foreign policy no Direct claim or mutation");
   }
   foreach(string command in new[]{"--assisted-connect","--assisted-native","--assisted-direct","--assisted-disable","--assisted-observe","--assisted-inspect","--assisted-contract-test","--assisted-do-nothing"}) {
    Program.ValidateCliArgs(new[]{command});Check(true,"assisted canonical CLI "+command);
    Reject(()=>Program.ValidateCliArgs(new[]{command,"--enable"}),"assisted mixed mutation rejected");
    Reject(()=>Program.ValidateCliArgs(new[]{command.ToUpperInvariant(),"--restore"}),"assisted case bypass rejected");
   }
   Reject(()=>Program.ValidateCliArgs(new[]{"--assisted-native","--servers","127.0.0.1"}),"assisted arbitrary native arguments rejected");
   Reject(()=>Program.ValidateCliArgs(new[]{"--enable","--PREPARED-REVISION",new string('A',64)}),"assisted prepared proof cannot silently lose guard");
   Reject(()=>Program.ValidateCliArgs(new[]{"--assisted-native","--profile","P1","--profile","P4"}),"assisted ambiguous duplicate profile refused");
   Reject(()=>Program.ValidateCliArgs(new[]{"--enable","--status","--prepared-revision",new string('A',64),"--prepared-until-ms","1"}),"assisted protected helper cannot mix commands");
   foreach(var result in new[]{new AssistedResult(AssistedOutcome.Refused,"NATIVE_REFUSED_NO_CHANGE",false,"NONE","{\"Code\":\"UAC_CANCELLED\"}"),
    new(AssistedOutcome.StalePreview,"PREVIEW_CONTEXT_CHANGED",false,"NONE",null),new(AssistedOutcome.Suspended,"CONTEXT_UNKNOWN",false,"NONE",null),
    new(AssistedOutcome.Refused,"PREPARE_OR_PREVIEW_REFUSED",false,"NONE","BOOTSTRAP_NO_ADDRESS"),new(AssistedOutcome.Refused,"PREPARE_OR_PREVIEW_REFUSED",false,"NONE","compiler rejected preview")}) {
    string display=MainForm.AssistedDisplayCode(result);
    Check(CodeTable.Known(display)&&display!="","assisted expected failure has a known user-facing message");
    Check(L.HasCode(display),"assisted expected failure has English translation");
   }
   Check(LiveAssistedBackend.NoChangeRefusal(1,"{\"Code\":\"DOH_NO_VALIDATED_ENDPOINT: preflight refused\"}"),"assisted native provider preflight refuses before writes");
   Check(!LiveAssistedBackend.NoChangeRefusal(1,"{\"Code\":\"APPLY_FAILED_ROLLED_BACK\"}"),"assisted apply failure is not labelled no change");
   Check(!LiveAssistedBackend.NoChangeRefusal(1,"{\"Code\":\"ENGINE_ERROR\",\"Details\":\"DOH_NO_VALIDATED_ENDPOINT\"}"),"assisted no change classification reads only leading code");
   var recoveredDisplay=MainForm.AssistedDisplayCode(new(AssistedOutcome.RecoveredVerified,"POSTCHECK_FAILED_OR_CONTEXT_CHANGED",true,"BASELINE_RESTORED","{\"Code\":\"DNS_CONFIGURED_APPLICATION_NOT_TESTED\"}"));
   Check(recoveredDisplay=="ASSISTED_BASELINE_RESTORED","assisted recovered outcome cannot reuse obsolete child success label");
   Check(L.HasCode(recoveredDisplay),"assisted baseline restoration message translated");
   var scope=Program.Targets.ToImmutableArray();var liveRequest=AssistedRequestFixture() with{Targets=scope,Gemini=Program.Gemini,Google=Program.Google,Servers=ImmutableArray<string>.Empty,Template="https://custom.example:8443/dns-query"};
   LiveAssistedBackend.Validate(liveRequest);Check(true,"assisted existing custom HTTPS port is preserved");
   LiveAssistedBackend.Validate(liveRequest with{Mode=AssistedMode.Disable,Template="bad-provider",Servers=ImmutableArray.Create("127.0.0.1")});Check(true,"assisted ownership-only disable independent of provider validity");
   Reject(()=>LiveAssistedBackend.Validate(liveRequest with{Servers=ImmutableArray.Create("127.0.0.1")}),"assisted native private provider still rejected");
   var saved=new SavedAddr{Template=liveRequest.Template,Servers=new[]{"8.8.8.8"},Utc=DateTime.UtcNow.ToString("o")};
   using(var live=new LiveAssistedBackend{SavedAddressSource=t=>saved}) {
    var snapshot=live.SavedFor(liveRequest.Template);saved.Servers[0]="9.9.9.9";
    Check(snapshot.Servers.Single()=="8.8.8.8"&&live.SavedFor("https://other.example/dns-query")==null,"assisted saved address input cloned and scoped to exact template");
   }
   var catalog=ProfileCatalog.BuiltIn;
   foreach(var oldScope in new[]{catalog.Targets,catalog.TargetsFor(true),catalog.TargetsForGoogle()})foreach(int newScope in new[]{0,1,2}) {
    var migration=LegacyScenarioAdapter.Migrate(catalog,new AppSettings{NoSave=true,Gemini=newScope>0,Google=newScope==2});
    var oldProvider=migration.Providers[0];var provider=migration.Providers[1];var scopes=migration.Pack.Dependencies.Where(d=>migration.Pack.Scenarios.Single(s=>s.Id==migration.SelectedScenario).Critical.Contains(d.Id)).Select(d=>d.Namespace).ToImmutableArray();
    var native=PolicyJson.Write(new{Journal=new{Schema=1,Protocol="DoH",Targets=oldScope,Template=oldProvider.EndpointIdentity,Servers=oldProvider.Bootstrap},Rules=oldScope.Select(n=>new{Namespaces=new[]{n},Owned=true,Fingerprint="fp:"+n,ExpectedFingerprint="fp:"+n}).ToArray()});
    var before=new AssistedSnapshot("network-a","state-a","baseline","Active",true,false,native);var preimage=LiveAssistedBackend.ProjectLegacyPreimage(before);
    provider=provider with{ObservedCapabilities=ImmutableArray.Create(new CapabilityObservation("dns.doh",provider.EndpointIdentity,before.Network,PolicyNow.AddSeconds(-1),PolicyNow.AddMinutes(5),ObservationOutcome.Passed,"fixture",PolicyCompiler.ProviderRouteRevision(provider)))};
    var binding=new BindingRequest(migration.SelectedScenario,RouteKind.NativeDoh,provider.Id,"assisted.session",scopes,ImmutableArray<string>.Empty);
    var compile=new CompileRequest(1,migration.Pack,ImmutableArray.Create(binding),ImmutableArray.Create(provider),ImmutableArray<TrustGrant>.Empty,SharedDomainPolicy.Reject,new(before.Network,before.Revision,preimage));
    var request=new AssistedRequest(AssistedMode.Native,provider.Id,provider.EndpointIdentity,provider.Bootstrap,scopes);
    var prepared=AssistedCoordinator.Grant(new(request,before,compile,PolicyCompiler.Compile(compile,PolicyNow),PolicyNow.AddMinutes(5)),PolicyNow);
    PolicyCompiler.ValidatePreview(prepared.Preview,prepared.Compile,PolicyNow);
    Check(preimage.All(p=>p.Owners.IsEmpty)&&!prepared.Preview.Blocked,"assisted exclusive legacy selection is explicitly released for bounded migration");
    Check(prepared.Preview.Diff.Count(d=>d.Action=="Remove")==oldScope.Except(scopes).Count()&&prepared.Preview.Diff.Count(d=>d.Action=="Replace")==oldScope.Intersect(scopes).Count()&&prepared.Preview.Diff.Count(d=>d.Action=="Add")==scopes.Except(oldScope).Count(),"assisted compiler diff matches full native replacement/narrowing "+oldScope.Length+"->"+scopes.Length);
    Check(!prepared.Preview.Reasons.Any(x=>x.StartsWith("UNSELECTED_OWNERS_PRESERVED:",StringComparison.Ordinal)),"assisted preview cannot promise preservation of rules native switch removes");
   }
   foreach(int defect in new[]{0,1,2,3}) {
    var p=PolicyFixture().Providers.Single();var targets=catalog.Targets;
    var output=PolicyJson.Write(new{Journal=new{Schema=defect==0?3:1,Protocol="DoH",Targets=targets,Template=p.EndpointIdentity,Servers=p.Bootstrap},
     Rules=targets.Select(n=>new{Namespaces=defect==1?new[]{n,"extra.example"}:new[]{n},Owned=defect!=2,Fingerprint="current",ExpectedFingerprint=defect==3?"changed":"current"}).ToArray()});
    Reject(()=>LiveAssistedBackend.ProjectLegacyPreimage(new("network-a","state-a","baseline","Active",true,false,output)),"assisted unsupported/incomplete/edited preimage refuses release "+defect);
   }
   foreach(bool managed in new[]{false,true}) {
    var p=PolicyFixture().Providers.Single();var targets=catalog.Targets;
    var output=PolicyJson.Write(new{Journal=new{Schema=2,LeaseVersion=1,LeaseManaged=managed,Leases=Array.Empty<object>(),Protocol="DoH",Targets=targets,Template=p.EndpointIdentity,Servers=p.Bootstrap},
     Rules=targets.Select(n=>new{Namespaces=new[]{n},Owned=true,Fingerprint="current",ExpectedFingerprint="current"}).ToArray()});
    var snapshot=new AssistedSnapshot("network-a","state-a","baseline","Active",true,false,output);
    if(managed)Reject(()=>LiveAssistedBackend.ProjectLegacyPreimage(snapshot),"assisted cannot take over durable broker ownership");
    else Check(LiveAssistedBackend.ProjectLegacyPreimage(snapshot).Length==targets.Length,"assisted manual schema2 remains service-free");
   }
  }
  static void RunConnectionCandidatesContracts() {
   var selected=AssistedRequestFixture();var alternative=selected with{Profile="alternative"};
   {
    var f=new AssistedFixture{ApplyDelay=TimeSpan.FromMilliseconds(300)};
    var r=new AssistedCoordinator(f,()=>f.Now).RunCandidates(new[]{selected,alternative},AcceptAssisted,_=>true,
     deadline:TimeSpan.FromMilliseconds(200),candidateTimeout:TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.NativeVerified&&f.Applies==1&&f.Recovers==0,"connect short preflight and selection deadlines never cancel admitted native mutation");
   }
   {
    var f=new AssistedFixture();using var user=new CancellationTokenSource();f.ApplyHook=_=>user.Cancel();
    var r=new AssistedCoordinator(f,()=>f.Now).RunCandidates(new[]{selected,alternative},AcceptAssisted,_=>true,token:user.Token).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.RecoveredVerified&&f.Applies==1&&f.Recovers==1,"connect explicit cancellation after native mutation still compensates and stops fallback");
   }
   {
    var f=new AssistedFixture{RequestPrepareHook=r=>throw new FormatException("BOOTSTRAP_NO_ADDRESS")};
    var candidates=Enumerable.Range(0,10).Select(i=>selected with{Profile="candidate-"+i});
    var r=new AssistedCoordinator(f,()=>f.Now).RunCandidates(candidates,AcceptAssisted,_=>true).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.Refused&&f.Prepares==4&&f.Applies==0,"connect candidate count is hard bounded");
   }
   {
    var f=new AssistedFixture{PrepareStarted=new(),PrepareRelease=new()};var c=new AssistedCoordinator(f,()=>f.Now);
    var first=c.RunCandidates(new[]{selected,alternative},AcceptAssisted,_=>true);f.PrepareStarted.Task.GetAwaiter().GetResult();
    var disconnect=c.Run(selected with{Mode=AssistedMode.Disable},null);f.PrepareRelease.SetResult(true);
    var before=first.GetAwaiter().GetResult();var after=disconnect.GetAwaiter().GetResult();
    Check(before.Outcome==AssistedOutcome.CancelledNoChange&&after.Outcome==AssistedOutcome.DisabledVerified&&f.Applies==0&&f.Disables==1&&c.ManualOff,"connect manual disconnect supersedes whole candidate set without reconnect");
   }
   {
    var f=new AssistedFixture{PrepareStarted=new(),PrepareRelease=new()};var c=new AssistedCoordinator(f,()=>f.Now);
    var first=c.RunCandidates(new[]{selected,alternative},AcceptAssisted,_=>true);f.PrepareStarted.Task.GetAwaiter().GetResult();
    var second=c.RunCandidates(new[]{alternative},AcceptAssisted,_=>true);f.PrepareRelease.SetResult(true);
    var before=first.GetAwaiter().GetResult();var after=second.GetAwaiter().GetResult();
    Check(before.Outcome==AssistedOutcome.CancelledNoChange&&after.Outcome==AssistedOutcome.NativeVerified&&f.Applies==1,"connect repeated intent or changed profile supersedes old callback and applies once");
   }
   {
    var f=new AssistedFixture();var r=new AssistedCoordinator(f,()=>f.Now).RunCandidates(new[]{selected,alternative},AcceptAssisted,_=>true).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.NativeVerified&&f.Prepares==1&&f.Applies==1,"connect selected success never tries another provider");
   }
   {
    var f=new AssistedFixture{RequestPrepareHook=r=>{if(r.Profile==selected.Profile)throw new FormatException("BOOTSTRAP_NO_ADDRESS");}};
    var r=new AssistedCoordinator(f,()=>f.Now).RunCandidates(new[]{selected,alternative},AcceptAssisted,_=>true).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.NativeVerified&&f.Prepares==2&&f.Applies==1,"connect first safe refusal automatically applies authorized alternative once");
    Check(r.Attempts.Length==2&&r.Attempts[0].Code=="BOOTSTRAP_NO_ADDRESS"&&r.Attempts[1].Profile==alternative.Profile&&
     MainForm.ConnectionReplacementNote(r,selected.Profile,false).Contains(alternative.Profile)&&MainForm.ConnectionReplacementNote(r,selected.Profile,true).Contains("unavailable"),"connect replacement note names actual successful profile and retains first refusal evidence");
   }
   {
    var f=new AssistedFixture{RequestPrepareHook=r=>throw new FormatException("BOOTSTRAP_TIMEOUT")};
    var r=new AssistedCoordinator(f,()=>f.Now).RunCandidates(new[]{selected,alternative},AcceptAssisted,_=>true).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.Refused&&r.Code=="ALL_PROVIDERS_UNAVAILABLE"&&r.Attempts.Length==2&&r.Attempts.All(a=>a.Code=="BOOTSTRAP_TIMEOUT"&&a.Detail=="BOOTSTRAP_TIMEOUT")&&f.Prepares==2&&f.Applies==0,"connect all unavailable aggregates outcome retains every technical reason and performs zero mutations");
   }
   {
    var f=new AssistedFixture{RequestPrepareHook=r=>throw new FormatException("BOOTSTRAP_NO_ADDRESS")};
    var r=new AssistedCoordinator(f,()=>f.Now).RunCandidates(new[]{selected,alternative},AcceptAssisted,_=>false).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.Refused&&f.Prepares==1&&f.Applies==0,"connect untrusted alternative excluded before preflight and mutation");
   }
   foreach(var code in new[]{"WMI_NOT_RESPONDING","NRPT_CONFLICT","UAC_CANCELLED"}) {
    var f=new AssistedFixture{RequestPrepareHook=r=>throw new FormatException(code)};
    var r=new AssistedCoordinator(f,()=>f.Now).RunCandidates(new[]{selected,alternative},AcceptAssisted,_=>true).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.Refused&&f.Prepares==1&&f.Applies==0,"connect unsafe refusal never falls back "+code);
   }
   {
    var f=new AssistedFixture{Child=new(1,"PARTIAL_APPLY",true,true)};
    var r=new AssistedCoordinator(f,()=>f.Now).RunCandidates(new[]{selected,alternative},AcceptAssisted,_=>true).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.RecoveredVerified&&f.Prepares==1&&f.Applies==1&&f.Recovers==1,"connect compensation ends operation without fallback");
   }
   {
    var f=new AssistedFixture();using var stop=new CancellationTokenSource();stop.Cancel();
    var r=new AssistedCoordinator(f,()=>f.Now).RunCandidates(new[]{selected,alternative},AcceptAssisted,_=>true,token:stop.Token).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.CancelledNoChange&&f.Captures==0&&f.Applies==0,"connect cancelled intent performs zero backend work");
   }
   {
    var f=new AssistedFixture{PrepareStarted=new(),PrepareRelease=new()};
    var r=new AssistedCoordinator(f,()=>f.Now).RunCandidates(new[]{selected,alternative},AcceptAssisted,_=>true,candidateTimeout:TimeSpan.FromMilliseconds(20)).GetAwaiter().GetResult();
    Check(r.Outcome==AssistedOutcome.Refused&&r.Code=="CONNECTION_SELECTION_TIMEOUT"&&r.Attempts.Length==1&&f.Prepares==1&&f.Applies==0,"connect automatic selection timeout remains actionable and stops without applying or racing next candidate");
   }
   {
    var f=new AssistedFixture();var prepared=f.Prepare(selected,f.State,CancellationToken.None).GetAwaiter().GetResult();
    string original=ConnectionTrust.PreparedKey(prepared);var provider=prepared.Compile.Providers.Single();
    foreach(var changed in new[]{provider with{EndpointIdentity=provider.EndpointIdentity+"?changed"},provider with{Bootstrap=ImmutableArray.Create("8.8.8.8")},provider with{ClaimedCapabilities=ImmutableArray.Create("changed")}})
     Check(original!=ConnectionTrust.PreparedKey(prepared with{Compile=prepared.Compile with{Providers=ImmutableArray.Create(changed)}}),"connect changed provider identity cannot inherit grant");
    Check(ConnectionTrust.RequestKey(selected)!=ConnectionTrust.RequestKey(selected with{Targets=ImmutableArray.Create("different.example")}),"connect changed scope cannot inherit grant");
   }
  }
  static void RunAssistedContracts() {
   Task.Run(RunAssistedContractsCore).GetAwaiter().GetResult();
   RunProviderConsentSurfaces();
   using(var form=new MainForm(true)) {
    var selected=ProfileCatalog.BuiltIn.Profiles.Last(p=>!p.Experimental&&p.Servers.Length>0);
    var request=new AssistedRequest(AssistedMode.Auto,selected.Id,selected.Template,selected.Servers.ToImmutableArray(),Program.Targets.ToImmutableArray(),Program.Gemini,Program.Google);
    var initial=form.ConnectionCandidates(request).Select(r=>r.Profile).ToArray();
    var preferences=(AppSettings)typeof(MainForm).GetField("settings",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(form);
    preferences.Pool=initial.Skip(1).Reverse().Select(id=>new PoolEntry{Id=id,Utc=DateTime.UtcNow.AddDays(-7).AddMinutes(1).ToString("o")}).ToList();
    Check(ProviderPool.Usable(preferences.Pool,DateTime.UtcNow,selected.Id).Length>0,"connect fixture includes historical entries still admitted by legacy seven-day pool");
    var ordered=form.ConnectionCandidates(request).Select(r=>r.Profile).ToArray();
    var expected=new[]{selected.Id}.Concat(ProfileCatalog.BuiltIn.Profiles.Where(p=>!p.Experimental&&p.Id!=selected.Id&&p.Servers.Length>0).Select(p=>p.Id)).Take(4);
    Check(ordered.SequenceEqual(initial)&&ordered.SequenceEqual(expected)&&ordered[0]==selected.Id,"connect ignores unscoped seven-day measurement pool and preserves selected-first deterministic catalog order");
    bool refused=false;
    try{var task=(Task)typeof(MainForm).GetMethod("Mutate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(form,new object[]{"enable",false});task.GetAwaiter().GetResult();}
    catch(InvalidOperationException e){refused=e.Message=="Synthetic UI requires a mutation hook.";}
    Check(refused,"assisted synthetic UI cannot reach live native pipeline");
   }
  }
  static void RunProviderConsentSurfaces() {
   string GrantState(){using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\AntigravityDnsUnlocker\ConnectionTrustV2");return key==null?"absent":string.Join("|",key.GetValueNames().OrderBy(n=>n,StringComparer.Ordinal).Select(n=>n+":"+key.GetValue(n)));}
   string before=GrantState();bool language=L.En;
   try {
    foreach(bool english in new[]{false,true})foreach(int dpi in new[]{96,192}) {
     L.En=english;
     string detail=string.Join("\n\n",Enumerable.Range(0,30).Select(i=>"Provider with a very long readable name "+i+"\nhttps://"+new string('a',180)+".example/dns-query\n8.8.8.8, 1.1.1.1\n"+new string('z',220)+".example"));
     using var form=new ProviderConsentForm(L.En?"Allowed providers":"Разрешённые провайдеры",detail);
     form.StartPosition=System.Windows.Forms.FormStartPosition.Manual;form.Location=new System.Drawing.Point(-2500,-2500);form.Show();System.Windows.Forms.Application.DoEvents();
     UiSurfaceCheck.ChangeDpi(form,dpi);form.ClientSize=new System.Drawing.Size(Math.Min(form.ClientSize.Width,680),Math.Min(form.ClientSize.Height,500));form.PerformLayout();System.Windows.Forms.Application.DoEvents();
     var client=form.ClientRectangle;
     bool Fits(System.Windows.Forms.Control button){var p=form.PointToClient(button.Parent.PointToScreen(button.Location));return client.Contains(new System.Drawing.Rectangle(p,button.Size))&&button.Width>70&&button.Height>=32;}
     using var bitmap=new System.Drawing.Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,new System.Drawing.Rectangle(System.Drawing.Point.Empty,bitmap.Size));
     Check(Fits(form.Allow)&&Fits(form.CancelChoice)&&form.Details.Height>40&&form.Details.WordWrap&&form.Details.ScrollBars==System.Windows.Forms.ScrollBars.Vertical,"consent long content keeps fixed actions visible and only vertical scroll "+(english?"EN":"RU")+" "+dpi);
     form.CancelForTest();Check(form.DialogResult==System.Windows.Forms.DialogResult.Cancel,"consent cancellation never approves "+dpi);
    }
   }finally{L.En=language;}
   Check(GrantState()==before,"consent render and cancellation write no trust grants");
  }
  public static int RunAssistedTests(){try{RunAssistedContracts();Console.WriteLine("ASSISTED_CONTRACT_PASS "+passed);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 }
}
