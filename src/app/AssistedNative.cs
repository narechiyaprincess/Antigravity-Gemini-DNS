using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 // A finite bridge from the coordinator into the existing protected native backend.
 // It accepts one legacy catalog scope and one DoH provider; never arbitrary compiler graphs or autonomous leases.
 public sealed class LiveAssistedBackend:IAssistedBackend,IAssistedRecoveryBackend,IDisposable {
  readonly ScenarioNetworkEpoch epoch=new();public AddressPlan AddressPlan{get;private set;}
  public AssistedSnapshot LastSnapshot{get;private set;}AssistedPrepared lastPrepared;
  (AssistedPrepared Prepared,string Output) lastApplied;   // the elevated engine's receipt of the last successful apply
  public Func<string,SavedAddr> SavedAddressSource;
  public Action<string> Progress;
  static string Text(JsonElement e,string name)=>e.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
  static bool Bool(JsonElement e,string name)=>e.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.True;
  static string[] Strings(JsonElement e,string name)=>e.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Array?v.EnumerateArray().Select(x=>x.GetString()).ToArray():Array.Empty<string>();
  static bool Same(IEnumerable<string> a,IEnumerable<string> b)=>a.OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(b.OrderBy(x=>x,StringComparer.Ordinal));
  static string ProfileName(AssistedRequest r){
   var builtIn=ProfileCatalog.BuiltIn.Find(r.Profile);
   if(builtIn!=null&&!string.Equals(builtIn.Template,r.Template,StringComparison.Ordinal))throw new FormatException("ASSISTED_PROVIDER_IDENTITY_MISMATCH");
   return builtIn?.Id;
  }
  internal static (LegacyScenarioMigration Migration,ProviderRecord Provider) PrepareProviderIdentity(AssistedRequest request,ImmutableArray<string> pins){
   string builtin=ProfileName(request);
   var preferences=new AppSettings{NoSave=true,ProfileIdentityVersion=1,Core=request.Core,Gemini=request.Gemini,Google=request.Google,Profile=builtin??AppSettings.CustomProfileId,
    CustomName=builtin==null?(request.Profile==AppSettings.CustomProfileId?"Custom":request.Profile):"",CustomTemplate=builtin==null?request.Template:"",CustomServers=builtin==null?string.Join(",",pins):""};
   var migration=LegacyScenarioAdapter.Migrate(ProfileCatalog.BuiltIn,preferences);
   var provider=migration.Providers.Single(p=>p.Id==migration.SelectedProvider);
   if(!string.Equals(provider.EndpointIdentity,request.Template,StringComparison.Ordinal))throw new FormatException("ASSISTED_PROVIDER_IDENTITY_MISMATCH");
   return (migration,provider with{Bootstrap=pins});
  }
  internal static void Validate(AssistedRequest r) {
   if(r.Targets.IsDefault||r.Servers.IsDefault||!Same(r.Targets,Program.Targets)||r.Gemini!=Program.Gemini||r.Google!=Program.Google||r.Core!=Program.Core)throw new FormatException("ASSISTED_SCOPE_CHANGED");
   var scopes=ProfileCatalog.BuiltIn;bool known=Program.IsKnownScope(r.Targets);
   if(!known||r.Targets.Length==0||r.Servers.Length>4)throw new FormatException("ASSISTED_UNSUPPORTED_SCOPE");
   if(r.Mode==AssistedMode.Disable||r.Mode==AssistedMode.Observe||r.Mode==AssistedMode.DoNothing)return;
   if(!Uri.TryCreate(r.Template,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||uri.Port<1||uri.Port>65535)throw new FormatException("ASSISTED_INVALID_PROVIDER");
   if(r.Servers.Any(s=>!System.Net.IPAddress.TryParse(s,out var ip)||!ScenarioDestination.Public(ip)))throw new FormatException("ASSISTED_NONPUBLIC_PROVIDER");
  }
  string Network()=>RuntimeCatalog.BindContext(epoch.Capture("assisted-local-context").Network);
  internal SavedAddr SavedFor(string template) {
   var saved=SavedAddressSource?.Invoke(template);
   return saved!=null&&saved.Template==template?new SavedAddr{Template=saved.Template,Servers=(saved.Servers??Array.Empty<string>()).ToArray(),Utc=saved.Utc}:null;
  }
  // A network that settles within a few seconds (an adapter reconnecting) is read again instead of failing the whole connect or disconnect.
  public async Task<AssistedSnapshot> Capture(AssistedRequest request,CancellationToken token) {
   for(int attempt=1;;attempt++) {
    try{return await CaptureOnce(request,token);}
    catch(FormatException e)when(e.Message=="ASSISTED_NETWORK_CHANGED"&&attempt<3){await Task.Delay(1000,token);}
   }
  }
  async Task<AssistedSnapshot> CaptureOnce(AssistedRequest request,CancellationToken token) {
   Validate(request);string network=Network();
   bool ownershipOnly=request.Mode==AssistedMode.Disable||request.Mode==AssistedMode.Observe;
   var result=await Program.Engine("prepare",false,false,ownershipOnly?null:request.Servers.ToArray(),ownershipOnly?null:request.Template,ownershipOnly?null:ProfileName(request),token,Program.GuardDeadline);
   if(result.Code!=0||result.Output.Length>262144)throw new FormatException(result.Code==1460?Recovery.WmiNotResponding:"ASSISTED_NATIVE_PREPARE_FAILED");
   using var doc=JsonDocument.Parse(result.Output);var root=doc.RootElement;string revision=Text(root,"Revision"),baseline=Text(root,"Baseline"),phase=Text(root,"Phase");
   if(network!=Network()||network.StartsWith("unknown:",StringComparison.Ordinal))throw new FormatException("ASSISTED_NETWORK_CHANGED");
   if(revision==null||baseline==null||phase==null||!root.TryGetProperty("Status",out var status))throw new FormatException("ASSISTED_PREIMAGE_UNKNOWN");
   LastSnapshot=new(network,revision,baseline,phase,Bool(status,"Active"),Bool(root,"ForeignConflict"),result.Output);return LastSnapshot;
  }
  static AssistedProof Proof(bool passed,string level,AssistedSnapshot context,string reason,string path)=>new(passed,level,path,context.Network,context.Revision,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(5),reason);
  public async Task<AssistedProof> Direct(AssistedRequest request,AssistedSnapshot before,CancellationToken token) {
   Progress?.Invoke("direct");Validate(request);
   var transport=new LiveScenarioTransport();var context=ScenarioProbes.Context(transport.ProviderRevision) with{Network=before.Network};
   var receipt=await ScenarioProbes.Run(transport,context,()=>ScenarioProbes.Context(transport.ProviderRevision) with{Network=Network()},false,token);
   // IPv4 transport is the declared level. IPv6 and HTTP refusal metadata remain independently reported.
   string[] hosts=request.Targets.Where(x=>!x.StartsWith(".",StringComparison.Ordinal)).ToArray();
   bool passed=receipt.ContextUnchanged&&hosts.All(h=>new[]{"dns","tcp","tls"}.All(layer=>receipt.Signals.Any(s=>s.Host==h&&s.Family=="IPv4"&&s.Layer==layer&&s.Outcome=="PASSED"))&&
    receipt.Signals.Any(s=>s.Host==h&&s.Family=="IPv4"&&s.Layer=="http"&&s.Outcome=="OBSERVED"));
   bool redirect=receipt.Signals.Any(s=>s.Layer=="http"&&s.Detail.Contains("REDIRECT_NOT_FOLLOWED",StringComparison.Ordinal));
   return Proof(passed&&!redirect,"TRANSPORT_ONLY",before,redirect?"CAPTIVE_PORTAL_OR_NETWORK_UNCERTAIN":passed?"IPV4_TRANSPORT_ONLY_APPLICATION_UNKNOWN":"DIRECT_TRANSPORT_UNCONFIRMED",receipt.Path);
  }
  public async Task<AssistedPrepared> Prepare(AssistedRequest request,AssistedSnapshot before,CancellationToken token) {
   Progress?.Invoke("prepare");Validate(request);DateTimeOffset observed=DateTimeOffset.UtcNow;
   string[] alive=Array.Empty<string>();
   var saved=SavedFor(request.Template);
   using var budget=CancellationTokenSource.CreateLinkedTokenSource(token);budget.CancelAfter(TimeSpan.FromSeconds(10));
   var task=AddressResolver.ResolveAsync(request.Template,request.Servers.ToArray(),async(pins,template)=>{
    var candidates=await Program.NetworkCandidates(pins,template,false,budget.Token);alive=candidates.Where(c=>c.DohValidated).Select(c=>c.Server).ToArray();return alive;
   },template=>Bootstrap.PinnedAsync(template,budget.Token),saved:saved);
   _=task.ContinueWith(t=>{_=t.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
   AddressPlan=await task.WaitAsync(budget.Token);
   if(AddressPlan.Error!=null)throw new FormatException(AddressPlan.Error);
   var pins=(AddressPlan.Source=="pinned"?AddressPlan.Servers.Where(alive.Contains):AddressPlan.Servers).ToImmutableArray();
   if(pins.Length==0||pins.Any(s=>!System.Net.IPAddress.TryParse(s,out var ip)||!ScenarioDestination.Public(ip)))throw new FormatException("ASSISTED_NO_PUBLIC_VALIDATED_PINS");
   if(before.Network!=Network())throw new FormatException("ASSISTED_NETWORK_CHANGED");
   var identity=PrepareProviderIdentity(request,pins);var migration=identity.Migration;
   var runtime=RuntimeCatalog.Select(migration.Pack,DateTimeOffset.UtcNow);
   var selectedPack=RuntimeCatalog.RequireNativePack(runtime.Pack,migration.SelectedScenario,request.Targets);
   if(before.Network!=Network())throw new FormatException("ASSISTED_CATALOG_OR_NETWORK_CHANGED");
   var provider=identity.Provider;
   string route=PolicyCompiler.ProviderRouteRevision(provider);
   provider=provider with{ObservedCapabilities=ImmutableArray.Create(new CapabilityObservation("dns.doh",provider.EndpointIdentity,before.Network,observed,observed.AddMinutes(5),ObservationOutcome.Passed,"AddressResolver + current DoH/TLS preflight",route))};
   var resources=ProjectLegacyPreimage(before);
   var binding=new BindingRequest(migration.SelectedScenario,RouteKind.NativeDoh,provider.Id,"assisted.session",request.Targets,ImmutableArray<string>.Empty);
   var compile=new CompileRequest(1,selectedPack,ImmutableArray.Create(binding),ImmutableArray.Create(provider),ImmutableArray<TrustGrant>.Empty,SharedDomainPolicy.Reject,
    new PolicyContext(before.Network,before.Revision,resources),false);
   lastPrepared=new(request,before,compile,PolicyCompiler.Compile(compile,DateTimeOffset.UtcNow),observed.AddMinutes(5));return lastPrepared;
  }
  internal static ImmutableArray<PolicyResource> ProjectLegacyPreimage(AssistedSnapshot before) {
   var resources=ImmutableArray.CreateBuilder<PolicyResource>();
   using var native=JsonDocument.Parse(before.Output);var data=native.RootElement;
   if(data.TryGetProperty("Journal",out var journal)&&journal.ValueKind==JsonValueKind.Object&&before.Phase!="Restored") {
    var catalog=ProfileCatalog.BuiltIn;var targets=Strings(journal,"Targets");
    if(!journal.TryGetProperty("Schema",out var schema)||!schema.TryGetInt32(out int version)||version<1||version>2||!Program.IsKnownScope(targets))throw new FormatException("ASSISTED_UNSUPPORTED_LEGACY_JOURNAL");
    if(version==2) {
     if(!journal.TryGetProperty("LeaseVersion",out var leaseVersion)||!leaseVersion.TryGetInt32(out int lv)||lv!=1||!journal.TryGetProperty("LeaseManaged",out var managed)||managed.ValueKind!=JsonValueKind.False||!journal.TryGetProperty("Leases",out var leases)||leases.ValueKind!=JsonValueKind.Array||leases.GetArrayLength()!=0)
      throw new FormatException("ASSISTED_DURABLE_LEASE_REQUIRES_BROKER");
    }
    if(Text(journal,"Protocol")!="DoH")throw new FormatException("ASSISTED_PLAIN_POLICY_REQUIRES_RESTORE");
    string endpoint=Text(journal,"Template");var bootstrap=Strings(journal,"Servers").ToImmutableArray();string revision=PolicyCompiler.RouteRevision(RouteKind.NativeDoh,endpoint,bootstrap);
    foreach(var row in data.GetProperty("Rules").EnumerateArray().Where(x=>Bool(x,"Owned"))) {
     string[] namespaces=Strings(row,"Namespaces");if(namespaces.Length!=1)throw new FormatException("ASSISTED_MULTINAMESPACE_RULE");
     string fingerprint=Text(row,"Fingerprint"),expected=Text(row,"ExpectedFingerprint");if(fingerprint!=expected)throw new FormatException("ASSISTED_OWNED_RULE_CHANGED");
     // Explicit release of a manual selection only; durable broker leases cannot be replaced here.
     // Preserve native owner/GUID/fingerprint in Before; do not invent lease owners that Engine cannot retain.
     resources.Add(new PolicyResource(namespaces[0],RouteKind.NativeDoh,endpoint,ImmutableArray<string>.Empty,true,fingerprint,expected,revision,bootstrap));
    }
    if(!Same(resources.Select(x=>x.Namespace),targets))throw new FormatException("ASSISTED_INCOMPLETE_OWNED_PREIMAGE");
   }
   return resources.ToImmutable();
  }
  internal static bool NoChangeRefusal(int code,string output) {
   if(code==1223)return true;
   try{using var doc=JsonDocument.Parse(output);string value=Text(doc.RootElement,"Code")?.Split(':')[0];return new[]{"UAC_CANCELLED","ELEVATION_START_FAILED","DOH_NO_VALIDATED_ENDPOINT","ASSISTED_PREVIEW_STALE","ASSISTED_PROVIDER_CHANGED","ASSISTED_PREVIEW_INVALID","ASSISTED_PREVIEW_EXPIRED","TRUST_DECISION_REQUIRED","ADMIN_REQUIRED","OPERATION_IN_PROGRESS"}.Contains(value,StringComparer.Ordinal);}catch{return false;}
  }
  static bool Quiescent(int code,string output)=>code!=1460&&!output.Contains("NO_MATCHING_OPERATION_RECEIPT",StringComparison.Ordinal);
  public async Task<AssistedChild> Apply(AssistedPrepared prepared,CancellationToken token) {
   Progress?.Invoke("apply");Validate(prepared.Request);
   try {
    var runtime=RuntimeCatalog.Refresh();
    RuntimeCatalog.RequireNativePack(runtime.Pack,prepared.Compile.Bindings.Single().ScenarioId,prepared.Request.Targets);
    if(prepared.Before.Network!=Network()||PolicyJson.Write(runtime.Pack)!=PolicyJson.Write(prepared.Compile.Pack))throw new FormatException("ASSISTED_CATALOG_PREVIEW_STALE");
   }catch(FormatException) {
    // This guard runs before any elevated child exists. Preserve the coordinator's
    // no-change path instead of latching uncertain recovery for a rejected preview.
    return new(1,"{\"Code\":\"ASSISTED_PREVIEW_STALE\"}",true,false);
   }
   var provider=prepared.Compile.Providers.Single();bool launched=false;
   try {
    lastApplied=default;
    var result=await Program.Elevate("enable",true,false,provider.Bootstrap.ToArray(),provider.EndpointIdentity,ProfileName(prepared.Request),token,prepared.Request.Repair,()=>launched=true,prepared.Before.Revision,prepared.ExpiresUtc.ToUnixTimeMilliseconds());
    if(result.Code==0)lastApplied=(prepared,result.Output);
    return new(result.Code,result.Output,Quiescent(result.Code,result.Output),launched&&!NoChangeRefusal(result.Code,result.Output));
   }catch(OperationCanceledException) when(!launched){return new(995,"{\"Code\":\"CANCELLED_BEFORE_ELEVATION\"}",true,false);}
  }
  // The elevated engine proves a fresh apply itself (Enable-State: effective NRPT, cache flush, Windows answers equal the provider's, read-back);
  // only that path reports DnsCacheFlushed. Its receipt for this exact preview stands in for a second diagnose (6-8 s per connect).
  internal static bool EngineProvedApply(string output,string template,IEnumerable<string> targets,IEnumerable<string> servers) {
   try {
    using var doc=JsonDocument.Parse(output??"");var r=doc.RootElement;
    return r.ValueKind==JsonValueKind.Object&&Text(r,"State")=="DNS active"&&Bool(r,"Active")&&Text(r,"JournalPhase")=="Active"&&r.TryGetProperty("DnsCacheFlushed",out var flushed)&&flushed.ValueKind==JsonValueKind.True&&
     Text(r,"Template")==template&&Same(Strings(r,"Targets"),targets)&&Same(Strings(r,"Servers"),servers);
   }catch(JsonException){return false;}
  }
  public async Task<AssistedChild> Disable(AssistedRequest request,AssistedSnapshot before,CancellationToken token) {
   Progress?.Invoke("disable");Validate(request with{Mode=AssistedMode.Disable});bool launched=false;
   try {
    var result=await Program.Elevate("restore",false,false,null,null,null,token,false,()=>launched=true,before.Revision,DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds());
    return new(result.Code,result.Output,Quiescent(result.Code,result.Output),launched&&!NoChangeRefusal(result.Code,result.Output));
   }catch(OperationCanceledException) when(!launched){return new(995,"{\"Code\":\"CANCELLED_BEFORE_ELEVATION\"}",true,false);}
  }
  public async Task<AssistedProof> Verify(AssistedRequest request,AssistedSnapshot current,CancellationToken token) {
   Progress?.Invoke("verify");Validate(request);
   if(request.Mode==AssistedMode.Disable)return Proof((current.Phase=="Absent"||current.Phase=="Restored")&&!current.Active,"NATIVE_OFF",current,"OWNED_NATIVE_POLICY_OFF","native journal + complete policy snapshot");
   using var doc=JsonDocument.Parse(current.Output);var status=doc.RootElement.GetProperty("Status");
   bool configured=current.Phase=="Active"&&current.Active&&!current.ForeignConflict&&Text(status,"Template")==request.Template&&Same(Strings(status,"Targets"),request.Targets)&&
    lastPrepared!=null&&lastPrepared.Request==request&&Same(Strings(status,"Servers"),lastPrepared.Compile.Providers.Single().Bootstrap);
   if(!configured)return Proof(false,"NATIVE_POLICY_AND_RESOLVER",current,"NATIVE_POLICY_UNCONFIRMED","native status");
   if(lastApplied.Prepared==lastPrepared&&EngineProvedApply(lastApplied.Output,request.Template,request.Targets,lastPrepared.Compile.Providers.Single().Bootstrap))
    return Proof(true,"NATIVE_POLICY_AND_RESOLVER",current,"WINDOWS_POLICY_AND_RESOLVER_ONLY_APPLICATION_UNKNOWN","elevated engine apply read-back + Windows system resolver; wire encryption/application not verified");
   var result=await Program.Engine("diagnose",false,false,Strings(status,"Servers"),request.Template,ProfileName(request),token);
   bool passed=result.Code==0&&MainForm.DiagnosisPassed(result.Output);var after=await Capture(request,token);
   return Proof(passed&&current.Network==after.Network&&current.Revision==after.Revision,"NATIVE_POLICY_AND_RESOLVER",current,"WINDOWS_POLICY_AND_RESOLVER_ONLY_APPLICATION_UNKNOWN","current native diagnose + Windows system resolver; wire encryption/application not verified");
  }
  public async Task<AssistedProof> Recover(AssistedPrepared prepared,CancellationToken token) {
   Progress?.Invoke("recover");var current=await Capture(prepared.Request,token);
   // Existing Engine compensates profile switches. Never destroy a previous active profile to imitate a clean rollback.
   if(current.Baseline!=prepared.Before.Baseline&&(prepared.Before.Phase=="Absent"||prepared.Before.Phase=="Restored")) {
    if(current.ForeignConflict)return Proof(false,"BASELINE_RESTORED",current,"FOREIGN_EDIT_RETAINED","native preimage reconciliation");
    var result=await Disable(prepared.Request,current,token);if(!result.Quiescent||result.Code!=0)return Proof(false,"BASELINE_RESTORED",current,"RESTORE_UNCONFIRMED","protected native restore");
    current=await Capture(prepared.Request,token);
   }
   bool baseline=current.Baseline==prepared.Before.Baseline;
   if(baseline&&prepared.Before.Active) {
    using var doc=JsonDocument.Parse(current.Output);var status=doc.RootElement.GetProperty("Status");
    var diagnose=await Program.Engine("diagnose",false,false,Strings(status,"Servers"),Text(status,"Template"),Text(status,"ProfileName"),token,targetScope:Strings(status,"Targets"));
    var after=await Capture(prepared.Request,token);baseline=diagnose.Code==0&&MainForm.DiagnosisPassed(diagnose.Output)&&after.Network==current.Network&&after.Revision==current.Revision;
   }
   return Proof(baseline,"BASELINE_RESTORED",current,baseline?"ORIGINAL_POLICY_AND_FOREIGN_RESOURCES_VERIFIED":"RECOVERY_REQUIRED","native semantic preimage + original foreign rule fingerprints and DoH registrations");
  }
  public Task<bool> RecoveryQuiescent(CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(Recovery.ReleaseCompletedChildren());}
  public bool RecoveryAuthority(AssistedSnapshot current)=>current!=null&&!current.ForeignConflict&&Recovery.RetryJournalSafe(current);
  public void Dispose()=>epoch.Dispose();
 }
}
