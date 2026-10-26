using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AntigravityGeminiDns {
 // Contracts are data only. No adapter here invokes the Windows engine or performs probes.
 public enum RouteKind { Direct, NativeDoh }
 public enum SharedDomainPolicy { Reject, CompatibleBinding }
 public sealed record ServiceContract(string Id, ImmutableArray<string> ClientSurfaces, ImmutableArray<string> Scenarios);
 public sealed record ProbeContract(string Kind, string DependencyId, string Method, int TimeoutMs, int MaxBytes);
 public sealed record DependencyContract(string Id, string Namespace, string Protocol, string AddressFamily,
  string Provenance, ImmutableArray<string> Requires);
 public sealed record ScenarioContract(string Id, string ServiceId, string ClientSurface, string Outcome,
  ImmutableArray<string> Critical, ImmutableArray<string> Optional, ImmutableArray<string> RequiredCapabilities,
  ImmutableArray<ProbeContract> Probes);
 public sealed record ScenarioPack(int Schema, string Id, string Revision, int MinimumCompiler,
  string Provenance, ImmutableArray<ServiceContract> Services, ImmutableArray<ScenarioContract> Scenarios,
  ImmutableArray<DependencyContract> Dependencies);
 public sealed record CapabilityObservation(string Capability, string EndpointIdentity, string NetworkRevision,
  DateTimeOffset ObservedUtc, DateTimeOffset ExpiresUtc, ObservationOutcome Outcome, string Source, string RouteRevision="");
 public sealed record ProviderRecord(string Id, string OperatorIdentity, string EndpointIdentity,
  ImmutableArray<string> Bootstrap, ImmutableArray<string> ClaimedCapabilities,
  ImmutableArray<CapabilityObservation> ObservedCapabilities, bool Revoked, string Provenance);
 public sealed record TrustGrant(string Id, string OperatorIdentity, string EndpointIdentity, string PackId,
  string PackRevision, ImmutableArray<string> Scenarios, ImmutableArray<string> Scopes,
  bool Autonomous, DateTimeOffset ExpiresUtc, bool Revoked);
 public sealed record SessionLease(string Id, string Owner, string Mode, ImmutableArray<string> Scopes,
  DateTimeOffset ExpiresUtc, string DurablePhase, ImmutableArray<string> SharedReferences, string RestorePolicy);
 public sealed record BindingRequest(string ScenarioId, RouteKind Route, string ProviderId, string LeaseId,
  ImmutableArray<string> AuthorizedScopes, ImmutableArray<string> Exclusions);
 public sealed record PolicyBinding(string Namespace, RouteKind Route, string EndpointIdentity,
  ImmutableArray<string> Owners, string RouteRevision, ImmutableArray<string> Bootstrap);
 // Fingerprint is the caller's observed resource fingerprint, never a claim made by an imported pack.
 public sealed record PolicyResource(string Namespace, RouteKind Route, string EndpointIdentity,
  ImmutableArray<string> Owners, bool Owned, string Fingerprint, string ExpectedFingerprint, string RouteRevision, ImmutableArray<string> Bootstrap);
 public sealed record PolicyContext(string NetworkRevision, string StateRevision, ImmutableArray<PolicyResource> Resources);
 public sealed record CompileRequest(int Schema, ScenarioPack Pack, ImmutableArray<BindingRequest> Bindings,
  ImmutableArray<ProviderRecord> Providers, ImmutableArray<TrustGrant> Grants,
  SharedDomainPolicy SharedDomains, PolicyContext Context, bool Autonomous=false);
 public sealed record PolicyChange(string Action, string Namespace, RouteKind Route, string EndpointIdentity,
  ImmutableArray<string> Owners, string PreimageFingerprint, string Reason, string RouteRevision, ImmutableArray<string> Bootstrap);
 public sealed record RecoveryStep(string Namespace, string Action, string Condition);
 public sealed record EffectivePolicy(string Generation, string NetworkRevision, string StateRevision,
  bool PreviewOnly, bool Blocked, ImmutableArray<PolicyBinding> Bindings, ImmutableArray<PolicyChange> Diff,
  ImmutableArray<string> Reasons, ImmutableArray<string> TrustChanges, ImmutableArray<RecoveryStep> RecoveryPlan,
  ImmutableArray<PolicyResource> Preimage, ImmutableArray<PolicyChange> AllowedWrites);

 public static class PolicyJson {
  public static readonly JsonSerializerOptions Options=new JsonSerializerOptions {
   UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow, MaxDepth=32, RespectRequiredConstructorParameters=true,
   Converters={new JsonStringEnumConverter(null,false)}, WriteIndented=true
  };
  public static string Write<T>(T value)=>JsonSerializer.Serialize(value,Options);
  public static T Read<T>(string json) {
   if(json==null||json.Length>262144)throw new FormatException("Policy document exceeds 256 KiB");
   try {
    using var doc=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=32});
    Unique(doc.RootElement);
    return JsonSerializer.Deserialize<T>(json,Options)??throw new FormatException("Empty policy document");
   }catch(JsonException e){throw new FormatException("Invalid policy document: "+e.Message,e);}
  }
  static void Unique(JsonElement item) {
   if(item.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var p in item.EnumerateObject()){if(!names.Add(p.Name))throw new FormatException("Duplicate policy property: "+p.Name);Unique(p.Value);}}
   else if(item.ValueKind==JsonValueKind.Array)foreach(var x in item.EnumerateArray())Unique(x);
  }
 }

 public static class PolicyNames {
  public static string Normalize(string name) {
   if(string.IsNullOrWhiteSpace(name)||name!=name.Trim()||name.Any(char.IsControl)||name.Contains('*')||name.Contains('/')||name.Contains(':'))throw new FormatException("Invalid namespace");
   bool suffix=name.StartsWith(".",StringComparison.Ordinal);string raw=suffix?name.Substring(1):name;
   // One trailing DNS root dot is equivalent, but empty labels, IPs and single-label names are refused.
   if(raw.EndsWith(".",StringComparison.Ordinal))raw=raw.Substring(0,raw.Length-1);
   string ascii;try{ascii=new IdnMapping{UseStd3AsciiRules=true}.GetAscii(raw).ToLowerInvariant();}catch(ArgumentException){throw new FormatException("Invalid IDNA namespace");}
   if(ascii.Length>253||!suffix&&!ascii.Contains('.')||IPAddress.TryParse(ascii,out _)||ascii.Split('.').Any(l=>l.Length<1||l.Length>63||l[0]=='-'||l[l.Length-1]=='-'||l.Any(c=>!(c>='a'&&c<='z')&&!(c>='0'&&c<='9')&&c!='-')))throw new FormatException("Invalid DNS namespace");
   return(suffix?".":"")+ascii;
  }
  public static bool Contains(string outer,string inner) {
   outer=Normalize(outer);inner=Normalize(inner);
   if(outer==inner)return true;
   if(!outer.StartsWith(".",StringComparison.Ordinal))return false;
   string root=outer.Substring(1),value=inner.TrimStart('.');
   return value==root||value.EndsWith("."+root,StringComparison.Ordinal);
  }
  public static bool Overlaps(string a,string b)=>Contains(a,b)||Contains(b,a);
  internal static void Id(string value){if(string.IsNullOrWhiteSpace(value)||value.Length>100||value.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c=='.'||c=='-'||c=='_')))throw new FormatException("Invalid contract identity");}
  internal static void Text(string value){if(string.IsNullOrWhiteSpace(value)||value.Length>2048||value.Any(char.IsControl))throw new FormatException("Missing or invalid contract provenance/outcome");}
  internal static void List<T>(ImmutableArray<T> value,int maximum){if(value.IsDefault||value.Length>maximum||value.Any(v=>v is null))throw new FormatException("Missing or oversized contract collection");}
 }

 public static class PackValidator {
  public static ScenarioPack Validate(ScenarioPack pack) {
   if(pack==null||pack.Schema!=1||pack.MinimumCompiler!=1)throw new FormatException("Unsupported scenario schema/compiler");
   PolicyNames.Id(pack.Id);PolicyNames.Id(pack.Revision);PolicyNames.Text(pack.Provenance);
   PolicyNames.List(pack.Services,16);PolicyNames.List(pack.Scenarios,64);PolicyNames.List(pack.Dependencies,128);
   if(pack.Services.Length==0||pack.Scenarios.Length==0)throw new FormatException("Empty scenario pack");
   var deps=new Dictionary<string,DependencyContract>(StringComparer.Ordinal);
   foreach(var d in pack.Dependencies) {
    PolicyNames.Id(d.Id);PolicyNames.Text(d.Provenance);PolicyNames.List(d.Requires,32);
    if(d.Protocol!="dns"&&d.Protocol!="https")throw new FormatException("Unsupported dependency protocol");
    if(d.AddressFamily!="Any"&&d.AddressFamily!="IPv4"&&d.AddressFamily!="IPv6")throw new FormatException("Invalid address family");
    if(!deps.TryAdd(d.Id,d with{Namespace=PolicyNames.Normalize(d.Namespace),Requires=Sort(d.Requires)}))throw new FormatException("Duplicate dependency");
   }
   // Validate all nodes, including unselected/optional graph branches. Depth and fanout are bounded.
   // Avoid exponential expansion of a bounded DAG: memoize after cycle validation using tri-colour DFS.
   var done=new HashSet<string>(StringComparer.Ordinal);var active=new HashSet<string>(StringComparer.Ordinal);var heights=new Dictionary<string,int>(StringComparer.Ordinal);
   int Visit(string id){if(done.Contains(id))return heights[id];if(!deps.TryGetValue(id,out var d))throw new FormatException("Unknown dependency: "+id);if(!active.Add(id))throw new FormatException("Dependency cycle");if(active.Count>16)throw new FormatException("Dependency depth exceeds 16");int h=1;foreach(var child in d.Requires)h=Math.Max(h,1+Visit(child));active.Remove(id);if(h>16)throw new FormatException("Dependency depth exceeds 16");done.Add(id);return heights[id]=h;}
   foreach(string id in deps.Keys)Visit(id);
   var services=new HashSet<string>(StringComparer.Ordinal);var scenarios=new HashSet<string>(StringComparer.Ordinal);
   foreach(var s in pack.Services){PolicyNames.Id(s.Id);PolicyNames.List(s.ClientSurfaces,16);PolicyNames.List(s.Scenarios,64);if(!services.Add(s.Id)||s.ClientSurfaces.Length==0)throw new FormatException("Invalid service");foreach(string c in s.ClientSurfaces)PolicyNames.Id(c);}
   foreach(var s in pack.Scenarios) {
    PolicyNames.Id(s.Id);PolicyNames.Text(s.Outcome);PolicyNames.List(s.Critical,128);PolicyNames.List(s.Optional,128);PolicyNames.List(s.RequiredCapabilities,16);PolicyNames.List(s.Probes,16);
    if(!scenarios.Add(s.Id)||!services.Contains(s.ServiceId)||!pack.Services.Single(x=>x.Id==s.ServiceId).ClientSurfaces.Contains(s.ClientSurface)||s.Critical.Length==0)throw new FormatException("Invalid scenario/service relationship");
    foreach(string id in s.Critical.Concat(s.Optional))if(!deps.ContainsKey(id))throw new FormatException("Unknown scenario dependency");
    if(s.Critical.Intersect(s.Optional,StringComparer.Ordinal).Any())throw new FormatException("Dependency cannot be critical and optional");
    foreach(var cap in s.RequiredCapabilities)PolicyNames.Id(cap);
    var reachable=new HashSet<string>(StringComparer.Ordinal);void Walk(string id){if(reachable.Add(id))foreach(var child in deps[id].Requires)Walk(child);}foreach(string id in s.Critical.Concat(s.Optional))Walk(id);
    foreach(var p in s.Probes) {
     if(!reachable.Contains(p.DependencyId)||p.TimeoutMs<100||p.TimeoutMs>10000||p.MaxBytes<1||p.MaxBytes>65536)throw new FormatException("Unbounded or unrelated probe");
     var dep=deps[p.DependencyId];
     if(dep.Namespace.StartsWith(".",StringComparison.Ordinal)||
       !(p.Kind=="dns"&&p.Method=="QUERY"&&dep.Protocol=="dns"||p.Kind=="tls"&&p.Method=="HANDSHAKE"&&dep.Protocol=="https"||p.Kind=="https"&&p.Method=="HEAD"&&dep.Protocol=="https"))throw new FormatException("Unsafe probe type/method/namespace");
    }
   }
   foreach(var svc in pack.Services){if(svc.Scenarios.Distinct(StringComparer.Ordinal).Count()!=svc.Scenarios.Length||!Sort(svc.Scenarios).SequenceEqual(Sort(pack.Scenarios.Where(s=>s.ServiceId==svc.Id).Select(s=>s.Id))))throw new FormatException("Service scenario membership mismatch");}
   return pack with{Services=pack.Services.OrderBy(x=>x.Id,StringComparer.Ordinal).Select(x=>x with{ClientSurfaces=Sort(x.ClientSurfaces),Scenarios=Sort(x.Scenarios)}).ToImmutableArray(),
    Dependencies=deps.Values.OrderBy(x=>x.Id,StringComparer.Ordinal).ToImmutableArray(),Scenarios=pack.Scenarios.OrderBy(x=>x.Id,StringComparer.Ordinal).Select(s=>s with{Critical=Sort(s.Critical),Optional=Sort(s.Optional),RequiredCapabilities=Sort(s.RequiredCapabilities),Probes=s.Probes.OrderBy(PolicyJson.Write,StringComparer.Ordinal).ToImmutableArray()}).ToImmutableArray()};
  }
  internal static ImmutableArray<string> Sort(System.Collections.Generic.IEnumerable<string> values)=>values.Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToImmutableArray();
 }

 public sealed record LegacyScenarioMigration(int Schema, string PreferencesJson, string SelectedScenario,
  string SelectedProvider, ScenarioPack Pack, ImmutableArray<ProviderRecord> Providers);
 public static class LegacyScenarioAdapter {
  public static LegacyScenarioMigration Migrate(ProfileCatalog catalog,AppSettings settings) {
   catalog.Validate();var dependencies=ImmutableArray.CreateBuilder<DependencyContract>();
   ImmutableArray<string> Nodes(string[] names,string prefix){return names.Select((n,i)=>{string id=prefix+i;dependencies.Add(new DependencyContract(id,n,"dns","Any","embedded-catalog:"+catalog.Version,ImmutableArray<string>.Empty));return id;}).ToImmutableArray();}
   var core=Nodes(catalog.Targets,"core");var gemini=Nodes(catalog.GeminiTargets??Array.Empty<string>(),"gemini");var google=Nodes(catalog.GoogleTargets??Array.Empty<string>(),"google");
   var scenarios=new[]{("legacy.core",core),("legacy.gemini",core.AddRange(gemini)),("legacy.google",core.AddRange(gemini).AddRange(google)),
    ("legacy.core-browser",core.AddRange(google)),("legacy.gemini-only",gemini),("legacy.browser-only",google),("legacy.gemini-browser",gemini.AddRange(google))}.Select(x=>new ScenarioContract(x.Item1,"legacy.scope","legacy.client","Legacy namespace equivalence only; application outcome unverified",x.Item2,ImmutableArray<string>.Empty,ImmutableArray.Create("dns.doh"),ImmutableArray<ProbeContract>.Empty)).ToImmutableArray();
   var pack=PackValidator.Validate(new ScenarioPack(1,"legacy.catalog",catalog.Version,1,"Local compatibility adapter; dependencies incomplete for real application scenarios",ImmutableArray.Create(new ServiceContract("legacy.scope",ImmutableArray.Create("legacy.client"),scenarios.Select(x=>x.Id).ToImmutableArray())),scenarios,dependencies.ToImmutable()));
   var providers=catalog.Profiles.Select(p=>new ProviderRecord(p.Id,"unverified:"+p.Id,p.Template,p.Servers.ToImmutableArray(),ImmutableArray.Create("dns.doh"),ImmutableArray<CapabilityObservation>.Empty,false,"embedded-catalog:"+catalog.Version)).ToImmutableArray();
   string selected=catalog.Find(settings.Profile)?.Id??settings.Profile;
   if((settings.Profile==AppSettings.CustomProfileId||(settings.ProfileIdentityVersion<1&&string.Equals((settings.Profile??"").Trim(),(settings.CustomName??"").Trim(),StringComparison.OrdinalIgnoreCase)))&&!string.IsNullOrEmpty(settings.CustomTemplate)) {
    if(!MainForm.TryParseCustom(settings.CustomName,settings.CustomServers,settings.CustomTemplate,out var customServers,out var customTemplate,out var error,out _))throw new FormatException(error);
    selected="custom."+EvidenceContext.Hash(customTemplate+"|"+string.Join(",",customServers)).Substring(0,16).ToLowerInvariant();
    providers=providers.Add(new ProviderRecord(selected,"unverified:"+selected,customTemplate,customServers.ToImmutableArray(),ImmutableArray.Create("dns.doh"),ImmutableArray<CapabilityObservation>.Empty,false,"local-user-preference; not a trust grant"));
   }
   // Preserve the original preference document. No provider selection, alias, custom config or boolean is rewritten.
   return new LegacyScenarioMigration(1,ConfigFile.Export(settings),LegacyScenarioId(settings),selected,pack,providers);
  }
  // The scenario that names the selection of the three services (a non-empty union; the first three are the older cumulative levels).
  public static string LegacyScenarioId(AppSettings s){
   bool core=s.Core||(!s.Gemini&&!s.Google);
   return core?(s.Google?(s.Gemini?"legacy.google":"legacy.core-browser"):s.Gemini?"legacy.gemini":"legacy.core"):s.Gemini?(s.Google?"legacy.gemini-browser":"legacy.gemini-only"):"legacy.browser-only";
  }
  public static AppSettings RestorePreferences(LegacyScenarioMigration migration,AppSettings current) {
   if(migration.Schema!=1)throw new FormatException("Unknown migration schema");PackValidator.Validate(migration.Pack);
   var imported=ConfigFile.Import(migration.PreferencesJson,current);if(!imported.Ok)throw new FormatException(imported.Code);return imported.Applied;
  }
 }

 public sealed record RouteInspection(string Adapter, bool Mutates, string ProofBoundary);
 public sealed record RouteReceipt(string Adapter, bool Changed, ObservationOutcome Outcome, string Reason);
 public interface IRouteAdapter {
  RouteInspection Inspect(); EffectivePolicy Plan(CompileRequest request,DateTimeOffset now);
  RouteReceipt Prepare(EffectivePolicy policy); RouteReceipt Apply(EffectivePolicy policy);
  RouteReceipt Verify(EffectivePolicy policy); RouteReceipt Restore(EffectivePolicy policy);
 }
 public sealed class DirectRoute : IRouteAdapter {
  public RouteInspection Inspect()=>new RouteInspection("Direct",false,"No application or network proof; no mutation");
  public EffectivePolicy Plan(CompileRequest request,DateTimeOffset now){if(request.Bindings.Any(b=>b.Route!=RouteKind.Direct))throw new FormatException("DirectRoute accepts only Direct bindings");return PolicyCompiler.Compile(request,now);}
  static RouteReceipt NoChange(EffectivePolicy p){if(p==null||p.Blocked||p.Bindings.Any(b=>b.Route!=RouteKind.Direct)||p.Diff.Length!=0)throw new FormatException("DirectRoute cannot mutate or consume a blocked plan");return new RouteReceipt("Direct",false,ObservationOutcome.Unknown,"No changes; scenario readiness requires independent evidence");}
  public RouteReceipt Prepare(EffectivePolicy p)=>NoChange(p);public RouteReceipt Apply(EffectivePolicy p)=>NoChange(p);
  public RouteReceipt Verify(EffectivePolicy p)=>NoChange(p);public RouteReceipt Restore(EffectivePolicy p)=>NoChange(p);
 }
}
