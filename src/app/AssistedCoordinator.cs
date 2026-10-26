using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 public enum AssistedMode { Auto, Native, Direct, DoNothing, Disable, Observe }
 public enum AssistedOutcome { DirectVerified, NativeVerified, DisabledVerified, NoChange, CancelledNoChange, Refused, StalePreview, Suspended, RecoveredVerified, RecoveryRequired }
 public sealed record AssistedRequest(AssistedMode Mode,string Profile,string Template,ImmutableArray<string> Servers,
  ImmutableArray<string> Targets,bool Gemini=false,bool Google=false,bool Repair=false,bool Core=true);
 public sealed record AssistedSnapshot(string Network,string Revision,string Baseline,string Phase,bool Active,bool ForeignConflict,string Output);
 public sealed record AssistedProof(bool Passed,string Level,string Path,string Network,string Revision,DateTimeOffset ObservedUtc,DateTimeOffset ExpiresUtc,string Reason);
 public sealed record AssistedPrepared(AssistedRequest Request,AssistedSnapshot Before,CompileRequest Compile,EffectivePolicy Preview,DateTimeOffset ExpiresUtc);
 public sealed record AssistedChild(int Code,string Output,bool Quiescent,bool MayHaveChanged);
 public sealed record AssistedAttempt(string Profile,string Code,string Detail);
 public sealed record AssistedResult(AssistedOutcome Outcome,string Code,bool Changed,string ClaimedLevel,string Output,AssistedProof Proof=null,ImmutableArray<AssistedAttempt> Attempts=default);
 public interface IAssistedBackend {
  Task<AssistedSnapshot> Capture(AssistedRequest request,CancellationToken token);
  Task<AssistedProof> Direct(AssistedRequest request,AssistedSnapshot before,CancellationToken token);
  Task<AssistedPrepared> Prepare(AssistedRequest request,AssistedSnapshot before,CancellationToken token);
  Task<AssistedChild> Apply(AssistedPrepared prepared,CancellationToken token);
  Task<AssistedChild> Disable(AssistedRequest request,AssistedSnapshot before,CancellationToken token);
  Task<AssistedProof> Verify(AssistedRequest request,AssistedSnapshot current,CancellationToken token);
  Task<AssistedProof> Recover(AssistedPrepared prepared,CancellationToken token);
 }
 public interface IAssistedRecoveryBackend {
  // Read-only: exact own process completion plus protected, current journal authority.
  Task<bool> RecoveryQuiescent(CancellationToken token);
  bool RecoveryAuthority(AssistedSnapshot current);
 }
 // Assisted only: this same coordinator serves real callers and fault fixtures. Native ownership stays in Engine.
 public sealed class AssistedCoordinator {
  readonly IAssistedBackend backend;readonly SemaphoreSlim serial=new(1,1);readonly Func<DateTimeOffset> clock;
  long intent;int manualOff,uncertain;bool recoveryVerified;AssistedSnapshot mutationBefore;
  public AssistedCoordinator(IAssistedBackend backend,Func<DateTimeOffset> clock=null){this.backend=backend??throw new ArgumentNullException(nameof(backend));this.clock=clock??(()=>DateTimeOffset.UtcNow);}
  public bool ManualOff=>Volatile.Read(ref manualOff)!=0;
  public bool RecoveryPending=>Volatile.Read(ref uncertain)!=0;
  static bool Known(AssistedSnapshot s)=>s!=null&&!string.IsNullOrWhiteSpace(s.Network)&&!string.IsNullOrWhiteSpace(s.Revision)&&!string.IsNullOrWhiteSpace(s.Baseline);
  bool Fresh(AssistedProof proof,AssistedSnapshot s,string level)=>proof!=null&&proof.Passed&&proof.Level==level&&
   !string.IsNullOrWhiteSpace(proof.Path)&&Known(s)&&proof.Network==s.Network&&proof.Revision==s.Revision&&
   proof.ObservedUtc<=clock()&&clock()<proof.ExpiresUtc&&proof.ExpiresUtc>proof.ObservedUtc;
  static bool SameContext(AssistedSnapshot a,AssistedSnapshot b)=>Known(a)&&Known(b)&&a.Network==b.Network&&a.Revision==b.Revision;
  static bool IsConnect(AssistedMode mode)=>mode==AssistedMode.Auto||mode==AssistedMode.Native||mode==AssistedMode.Direct;
  bool Superseded(long generation)=>generation!=Interlocked.Read(ref intent);
  AssistedResult Result(AssistedOutcome outcome,string code,bool changed=false,string output=null,AssistedProof proof=null)=>new(outcome,code,changed,proof?.Level??"NONE",output,proof);
  AssistedResult Uncertain(string code,string output=null){Interlocked.Exchange(ref uncertain,1);return Result(AssistedOutcome.RecoveryRequired,code,true,output);}
  public static AssistedPrepared Grant(AssistedPrepared p,DateTimeOffset now) {
   var provider=p.Compile.Providers.Single(x=>p.Compile.Bindings.Any(b=>b.ProviderId==x.Id));
   var bindings=p.Compile.Bindings;var scopes=p.Compile.Pack.Dependencies.Where(d=>bindings.Any(b=>b.AuthorizedScopes.Contains(d.Namespace))).Select(d=>d.Namespace).Distinct().ToImmutableArray();
   var grant=new TrustGrant("assisted.explicit",provider.OperatorIdentity,provider.EndpointIdentity,p.Compile.Pack.Id,p.Compile.Pack.Revision,
    bindings.Select(b=>b.ScenarioId).ToImmutableArray(),scopes,false,p.ExpiresUtc,false);
   var request=p.Compile with{Grants=ImmutableArray.Create(grant),Autonomous=false};
   return p with{Compile=request,Preview=PolicyCompiler.Compile(request,now)};
  }
  async Task<AssistedResult> Reconcile(AssistedPrepared prepared,string reason,string output) {
   // A separate bounded recovery token survives caller cancellation. Backend must never race a possibly live child.
   using var recovery=new CancellationTokenSource(TimeSpan.FromSeconds(90));
   try {
    var proof=await backend.Recover(prepared,recovery.Token);
    var current=await backend.Capture(prepared.Request,recovery.Token);
    if(current.Baseline==prepared.Before.Baseline&&Fresh(proof,current,"BASELINE_RESTORED"))return Result(AssistedOutcome.RecoveredVerified,reason,true,output,proof);
   }catch{}
   return Uncertain(reason,output);
  }
  public Task<AssistedResult> Run(AssistedRequest request,Func<AssistedPrepared,CancellationToken,Task<bool>> trust,CancellationToken token=default)=>RunSingle(request,trust,token,null);
  // One user intent across every candidate. Disable supersedes the entire operation, not only its current child.
  public async Task<AssistedResult> RunCandidates(IEnumerable<AssistedRequest> candidates,Func<AssistedPrepared,CancellationToken,Task<bool>> trust,
   Func<AssistedRequest,bool> authorized,Action<AssistedRequest,int> progress=null,CancellationToken token=default,
   TimeSpan? deadline=null,TimeSpan? candidateTimeout=null) {
   var list=candidates?.Take(4).ToArray()??Array.Empty<AssistedRequest>();
   if(list.Length==0)throw new ArgumentException("No connection candidates");
   if(list.Any(r=>r.Mode!=AssistedMode.Auto&&r.Mode!=AssistedMode.Native))throw new ArgumentException("Only connection candidates are supported");
   long generation=Interlocked.Increment(ref intent);Interlocked.Exchange(ref manualOff,0);
   using var total=CancellationTokenSource.CreateLinkedTokenSource(token);total.CancelAfter(deadline??TimeSpan.FromMinutes(4));
   AssistedResult last=Result(AssistedOutcome.Refused,"NO_AUTHORIZED_PROVIDER");
   var attempts=ImmutableArray.CreateBuilder<AssistedAttempt>();
   for(int i=0;i<list.Length;i++) {
    if(token.IsCancellationRequested||Superseded(generation)||ManualOff)return Result(AssistedOutcome.CancelledNoChange,"CANCELLED_BEFORE_MUTATION") with{Attempts=attempts.ToImmutable()};
    if(total.IsCancellationRequested)return Result(AssistedOutcome.Refused,"CONNECTION_SELECTION_TIMEOUT") with{Attempts=attempts.ToImmutable()};
    var request=list[i];if(i>0&&(authorized==null||!authorized(request)))continue;
    progress?.Invoke(request,i);
    using var attempt=CancellationTokenSource.CreateLinkedTokenSource(total.Token);attempt.CancelAfter(candidateTimeout??TimeSpan.FromSeconds(70));
    last=await RunSingle(request,trust,attempt.Token,generation,token);
    attempts.Add(new(request.Profile,FailureCode(last)??last.Code,last.Output));
    last=last with{Attempts=attempts.ToImmutable()};
    if(last.Outcome==AssistedOutcome.CancelledNoChange&&!last.Changed&&!token.IsCancellationRequested&&!Superseded(generation)&&!ManualOff&&attempt.IsCancellationRequested)
     return last with{Outcome=AssistedOutcome.Refused,Code="CONNECTION_SELECTION_TIMEOUT"};
    // Cancellation, uncertainty, rollback, conflicts and UAC refusal all stop here. Only the existing proven no-write codes advance.
    if(last.Outcome!=AssistedOutcome.Refused||last.Changed||!ProviderSwitch.IsTrigger(FailureCode(last)))return last;
   }
   return last with{Code=attempts.Count>0?"ALL_PROVIDERS_UNAVAILABLE":last.Code,Attempts=attempts.ToImmutable()};
  }
  internal static string FailureCode(AssistedResult result) {
   if(ProviderSwitch.IsTrigger(result.Code))return result.Code;
   try{using var json=System.Text.Json.JsonDocument.Parse(result.Output??"{}");return json.RootElement.GetProperty("Code").GetString();}catch{return result.Output;}
  }
  async Task<AssistedResult> RunSingle(AssistedRequest request,Func<AssistedPrepared,CancellationToken,Task<bool>> trust,CancellationToken token,long? operationGeneration,CancellationToken? mutationToken=null) {
   if(request==null||!Enum.IsDefined(request.Mode))throw new ArgumentException("Invalid assisted request");
   long generation;
   if(operationGeneration.HasValue)generation=operationGeneration.Value;
   else if(request.Mode==AssistedMode.Disable){Interlocked.Exchange(ref manualOff,1);generation=Interlocked.Increment(ref intent);}
   else if(IsConnect(request.Mode)){generation=Interlocked.Increment(ref intent);Interlocked.Exchange(ref manualOff,0);}
   else generation=Interlocked.Read(ref intent);
   bool entered=false,started=false;AssistedPrepared prepared;
   try {
    await serial.WaitAsync(token);entered=true;
    if(request.Mode==AssistedMode.DoNothing)return Result(AssistedOutcome.NoChange,"DO_NOTHING");
    if(Superseded(generation))return Result(AssistedOutcome.CancelledNoChange,"SUPERSEDED_BEFORE_MUTATION");
    AssistedSnapshot recoveryAdmission=null;
    if(RecoveryPending&&request.Mode!=AssistedMode.Observe) {
     if(request.Mode!=AssistedMode.Disable||backend is not IAssistedRecoveryBackend recoveryBackend||!await recoveryBackend.RecoveryQuiescent(token))
      return Uncertain("PREVIOUS_CHILD_OR_RECOVERY_UNCONFIRMED");
     var recoveryBefore=await backend.Capture(request,token);
     if(!Known(recoveryBefore)||mutationBefore==null||recoveryBefore.Network!=mutationBefore.Network||!recoveryBackend.RecoveryAuthority(recoveryBefore))
      return Uncertain("RECOVERY_AUTHORITY_OR_NETWORK_UNCONFIRMED",recoveryBefore?.Output);
     var recoveryFresh=await backend.Capture(request,token);
     if(!SameContext(recoveryBefore,recoveryFresh)||recoveryBefore.Baseline!=recoveryFresh.Baseline||!recoveryBackend.RecoveryAuthority(recoveryFresh)||!await recoveryBackend.RecoveryQuiescent(token))
      return Uncertain("RECOVERY_CONTEXT_CHANGED",recoveryFresh?.Output);
     // Only an explicit Restore can clear the latch; Connect never acquires permission here.
     if((recoveryFresh.Phase=="Restored"||recoveryFresh.Phase=="Absent")&&!recoveryFresh.Active) {
      var off=await backend.Verify(request,recoveryFresh,token);var confirmed=await backend.Capture(request,token);
      if(SameContext(recoveryFresh,confirmed)&&recoveryBackend.RecoveryAuthority(confirmed)&&Fresh(off,confirmed,"NATIVE_OFF")) {
       recoveryVerified=true;Interlocked.Exchange(ref uncertain,0);return Result(AssistedOutcome.DisabledVerified,"RECOVERY_ALREADY_RESTORED_VERIFIED",false,confirmed.Output,off);
      }
      return Uncertain("RESTORE_UNCONFIRMED",confirmed?.Output);
     }
     recoveryAdmission=recoveryFresh;
     // Keep uncertainty latched until the guarded Restore and post-check actually succeed.
    }
    var before=recoveryAdmission??await backend.Capture(request,token);
    if(!Known(before))return Result(AssistedOutcome.Suspended,"CONTEXT_UNKNOWN");
    if(request.Mode==AssistedMode.Observe)return Result(AssistedOutcome.NoChange,ManualOff?"MANUAL_OFF":"OBSERVE_ONLY",false,before.Output);
    if(request.Mode==AssistedMode.Disable) {
     token.ThrowIfCancellationRequested();if(Superseded(generation))return Result(AssistedOutcome.CancelledNoChange,"SUPERSEDED_BEFORE_MUTATION");
     var fresh=await backend.Capture(request,token);if(!SameContext(before,fresh))return Result(AssistedOutcome.StalePreview,"DISABLE_PREIMAGE_CHANGED");
     token.ThrowIfCancellationRequested();if(Superseded(generation))return Result(AssistedOutcome.CancelledNoChange,"SUPERSEDED_BEFORE_MUTATION");
     if(recoveryVerified&&(before.Phase=="Restored"||before.Phase=="Absent")&&!before.Active) {
      var off=await backend.Verify(request,before,token);var current=await backend.Capture(request,token);
      if(SameContext(before,current)&&Fresh(off,current,"NATIVE_OFF"))return Result(AssistedOutcome.DisabledVerified,"ALREADY_DISABLED_VERIFIED",false,current.Output,off);
      return Uncertain("RESTORE_UNCONFIRMED",current.Output);
     }
     mutationBefore=before;started=true;var child=await backend.Disable(request,before,token);
     if(!child.Quiescent)return Uncertain("DISABLE_CHILD_UNCONFIRMED",child.Output);
     if(child.Code!=0&&!child.MayHaveChanged)return Result(child.Code==995?AssistedOutcome.CancelledNoChange:AssistedOutcome.Refused,"DISABLE_REFUSED_NO_CHANGE",false,child.Output);
     var after=await backend.Capture(request,CancellationToken.None);var proof=await backend.Verify(request,after,CancellationToken.None);
     if(child.Code==0&&after.Network==before.Network&&Fresh(proof,after,"NATIVE_OFF")) {
      recoveryVerified=RecoveryPending;Interlocked.Exchange(ref uncertain,0);return Result(AssistedOutcome.DisabledVerified,"DISABLED_VERIFIED",child.MayHaveChanged,child.Output,proof);
     }
     return Uncertain("RESTORE_UNCONFIRMED",child.Output);
    }
    if(before.Phase!="Absent"&&before.Phase!="Restored"&&before.Phase!="Active")return Result(AssistedOutcome.Refused,"UNFINISHED_NATIVE_JOURNAL",false,before.Output);
    // An active bridge is not an unconfigured Direct route. Native explicitly requests policy even if transport works already.
    if((request.Mode==AssistedMode.Auto||request.Mode==AssistedMode.Direct)&&!before.Active&&!before.ForeignConflict&&(before.Phase=="Absent"||before.Phase=="Restored")) {
     var direct=await backend.Direct(request,before,token);var current=await backend.Capture(request,token);
     if(Superseded(generation))return Result(AssistedOutcome.CancelledNoChange,"SUPERSEDED_BEFORE_MUTATION");
     if(!SameContext(before,current))return Result(AssistedOutcome.Suspended,"DIRECT_CONTEXT_CHANGED");
     if(Fresh(direct,current,"TRANSPORT_ONLY"))return Result(AssistedOutcome.DirectVerified,"DIRECT_TRANSPORT_VERIFIED_APPLICATION_UNKNOWN",false,current.Output,direct);
     if(request.Mode==AssistedMode.Direct)return Result(AssistedOutcome.NoChange,"DIRECT_UNVERIFIED_APPLICATION_UNKNOWN",false,current.Output,direct);
     // A timeout/refusal is not a portal diagnosis. Only explicit uncertain network evidence suspends fallback.
     if(direct?.Reason=="CAPTIVE_PORTAL_OR_NETWORK_UNCERTAIN")return Result(AssistedOutcome.Suspended,direct.Reason,false,current.Output,direct);
    }
    if(request.Mode==AssistedMode.Direct)return Result(AssistedOutcome.NoChange,"ACTIVE_POLICY_IS_NOT_DIRECT",false,before.Output);
    if(before.ForeignConflict)return Result(AssistedOutcome.Refused,"FOREIGN_POLICY_CONFLICT",false,before.Output);
    prepared=await backend.Prepare(request,before,token);
    if(prepared==null||prepared.Before!=before||prepared.Request!=request||prepared.ExpiresUtc<=clock())return Result(AssistedOutcome.StalePreview,"INVALID_OR_EXPIRED_PREPARE");
    if(trust==null||!await trust(prepared,token))return Result(AssistedOutcome.Refused,"EXPLICIT_TRUST_DECLINED");
    prepared=Grant(prepared,clock());
    token.ThrowIfCancellationRequested();
    if(Superseded(generation)||ManualOff)return Result(AssistedOutcome.CancelledNoChange,"MANUAL_OFF_OR_SUPERSEDED");
    var ready=await backend.Capture(request,token);
    if(prepared.ExpiresUtc<=clock()||!SameContext(before,ready))return Result(AssistedOutcome.StalePreview,"PREVIEW_CONTEXT_CHANGED");
    PolicyCompiler.ValidatePreview(prepared.Preview,prepared.Compile,clock());
    token.ThrowIfCancellationRequested();
    if(Superseded(generation)||ManualOff)return Result(AssistedOutcome.CancelledNoChange,"MANUAL_OFF_OR_SUPERSEDED");
    // After this point cancellation cannot claim no mutation. Native must persist its protected journal before the first Windows write.
    // Candidate/selection deadlines gate admission only. Once admitted, retain native's 150 s engine deadline plus its receipt grace;
    // a short preflight timer must never interrupt a compensated switch. Explicit user cancellation still reaches the native backend.
    token=mutationToken??token;token.ThrowIfCancellationRequested();
    recoveryVerified=false;mutationBefore=before;started=true;var applied=await backend.Apply(prepared,token);
    if(!applied.Quiescent)return Uncertain("CHILD_COMPLETION_UNCONFIRMED",applied.Output);
    if(applied.Code!=0) {
     if(!applied.MayHaveChanged)return Result(applied.Code==995?AssistedOutcome.CancelledNoChange:AssistedOutcome.Refused,"NATIVE_REFUSED_NO_CHANGE",false,applied.Output);
     return await Reconcile(prepared,"APPLY_FAILED",applied.Output);
    }
    using var verify=new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var observed=await backend.Capture(request,verify.Token);var post=await backend.Verify(request,observed,verify.Token);
    if(!token.IsCancellationRequested&&!Superseded(generation)&&!ManualOff&&before.Network==observed.Network&&Fresh(post,observed,"NATIVE_POLICY_AND_RESOLVER"))return Result(AssistedOutcome.NativeVerified,"NATIVE_VERIFIED_APPLICATION_UNKNOWN",applied.MayHaveChanged,applied.Output,post);
    return await Reconcile(prepared,"POSTCHECK_FAILED_OR_CONTEXT_CHANGED",applied.Output);
   }catch(OperationCanceledException) {
    if(!started&&!RecoveryPending)return Result(AssistedOutcome.CancelledNoChange,"CANCELLED_BEFORE_MUTATION");
    return Uncertain("CANCELLED_AFTER_CHILD_OR_MUTATION");
   }catch(Exception e) {
    if(started||RecoveryPending)return Uncertain("ASSISTED_OPERATION_UNCONFIRMED",e.Message);
    return Result(AssistedOutcome.Refused,"PREPARE_OR_PREVIEW_REFUSED",false,e.Message);
   }finally{if(entered)serial.Release();}
  }
 }
}
