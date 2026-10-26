using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace AntigravityGeminiDns {
 public static class PolicyCompiler {
  static ImmutableArray<string> Sort(IEnumerable<string> values)=>PackValidator.Sort(values);
  static ImmutableArray<string> Scopes(ImmutableArray<string> values){PolicyNames.List(values,128);return Sort(values.Select(PolicyNames.Normalize));}
  static string Endpoint(string value) {
   if(!Uri.TryCreate(value,UriKind.Absolute,out var u)||u.Scheme!="https"||u.UserInfo.Length!=0||u.Fragment.Length!=0||u.AbsoluteUri.Length>2048)throw new FormatException("Invalid provider endpoint");
   string host=PolicyNames.Normalize(u.IdnHost);if(host.StartsWith(".",StringComparison.Ordinal))throw new FormatException("Invalid endpoint host");
   var builder=new UriBuilder(u){Host=host};return builder.Uri.AbsoluteUri;
  }
  // Technical proof follows the actual pinned route. Operator trust remains a separate grant.
  public static string RouteRevision(RouteKind route,string endpoint,System.Collections.Generic.IEnumerable<string> bootstrap) {
   if(route==RouteKind.Direct)return "direct";
   if(route!=RouteKind.NativeDoh)throw new FormatException("Unsupported route revision");
   var pins=Sort(bootstrap.Select(ip=>System.Net.IPAddress.TryParse(ip,out var parsed)?parsed.ToString():throw new FormatException("Invalid bootstrap address")));
   return EvidenceContext.Hash(PolicyJson.Write(new{Route=route,Endpoint=Endpoint(endpoint),Bootstrap=pins}));
  }
  public static string ProviderRouteRevision(ProviderRecord provider)=>RouteRevision(RouteKind.NativeDoh,provider.EndpointIdentity,provider.Bootstrap);
  static ProviderRecord Provider(ProviderRecord p) {
   PolicyNames.Id(p.Id);PolicyNames.Text(p.OperatorIdentity);PolicyNames.Text(p.Provenance);
   PolicyNames.List(p.Bootstrap,4);PolicyNames.List(p.ClaimedCapabilities,32);PolicyNames.List(p.ObservedCapabilities,64);
   if(p.Bootstrap.Any(ip=>!Program.PublicIp(ip)))throw new FormatException("Provider bootstrap requires public addresses");
   foreach(var c in p.ClaimedCapabilities)PolicyNames.Id(c);
   foreach(var c in p.ObservedCapabilities){PolicyNames.Id(c.Capability);PolicyNames.Text(c.Source);PolicyNames.Text(c.NetworkRevision);Endpoint(c.EndpointIdentity);if(!Enum.IsDefined(c.Outcome)||c.ExpiresUtc<=c.ObservedUtc)throw new FormatException("Invalid capability observation");}
   return p with{EndpointIdentity=Endpoint(p.EndpointIdentity),Bootstrap=Sort(p.Bootstrap.Select(ip=>System.Net.IPAddress.Parse(ip).ToString())),ClaimedCapabilities=Sort(p.ClaimedCapabilities),ObservedCapabilities=p.ObservedCapabilities.Select(o=>o with{EndpointIdentity=Endpoint(o.EndpointIdentity),ObservedUtc=o.ObservedUtc.ToUniversalTime(),ExpiresUtc=o.ExpiresUtc.ToUniversalTime()}).OrderBy(PolicyJson.Write,StringComparer.Ordinal).ToImmutableArray()};
  }
  static TrustGrant Grant(TrustGrant g) {
   PolicyNames.Id(g.Id);PolicyNames.Text(g.OperatorIdentity);PolicyNames.Id(g.PackId);PolicyNames.Id(g.PackRevision);PolicyNames.List(g.Scenarios,64);foreach(var id in g.Scenarios)PolicyNames.Id(id);
   return g with{EndpointIdentity=Endpoint(g.EndpointIdentity),Scenarios=Sort(g.Scenarios),Scopes=Scopes(g.Scopes),ExpiresUtc=g.ExpiresUtc.ToUniversalTime()};
  }
  static CompileRequest Normalize(CompileRequest r) {
   if(r==null||r.Schema!=1||r.Context==null||!Enum.IsDefined(r.SharedDomains))throw new FormatException("Unsupported policy schema/context");
   var pack=PackValidator.Validate(r.Pack);PolicyNames.Text(r.Context.NetworkRevision);PolicyNames.Text(r.Context.StateRevision);
   PolicyNames.List(r.Bindings,64);PolicyNames.List(r.Providers,32);PolicyNames.List(r.Grants,128);PolicyNames.List(r.Context.Resources,256);
   if(r.Bindings.Select(x=>x.ScenarioId).Distinct(StringComparer.Ordinal).Count()!=r.Bindings.Length||r.Providers.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=r.Providers.Length||r.Grants.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=r.Grants.Length)throw new FormatException("Duplicate binding/provider/grant");
   var bindings=r.Bindings.Select(b=>{PolicyNames.Id(b.ScenarioId);PolicyNames.Id(b.LeaseId);if(!Enum.IsDefined(b.Route)||b.ProviderId==null||b.Route==RouteKind.Direct&&b.ProviderId.Length!=0)throw new FormatException("Invalid route binding");if(b.Route==RouteKind.NativeDoh)PolicyNames.Id(b.ProviderId);return b with{AuthorizedScopes=Scopes(b.AuthorizedScopes),Exclusions=Scopes(b.Exclusions)};}).OrderBy(x=>x.ScenarioId,StringComparer.Ordinal).ToImmutableArray();
   var resources=r.Context.Resources.Select(x=>{if(!Enum.IsDefined(x.Route))throw new FormatException("Invalid resource route");PolicyNames.List(x.Owners,64);PolicyNames.List(x.Bootstrap,4);foreach(string owner in x.Owners)PolicyNames.Text(owner);PolicyNames.Text(x.Fingerprint);PolicyNames.Text(x.ExpectedFingerprint);var pins=Sort(x.Bootstrap.Select(ip=>System.Net.IPAddress.TryParse(ip,out var parsed)?parsed.ToString():throw new FormatException("Invalid resource bootstrap")));if(x.Route==RouteKind.Direct&&pins.Length!=0||x.Owned&&x.Route==RouteKind.NativeDoh&&pins.Length==0)throw new FormatException("Incomplete resource route snapshot");if(x.RouteRevision!=RouteRevision(x.Route,x.EndpointIdentity,pins))throw new FormatException("Resource route revision mismatch");return x with{Namespace=PolicyNames.Normalize(x.Namespace),EndpointIdentity=x.Route==RouteKind.Direct?(x.EndpointIdentity==""?"":throw new FormatException("Direct resource has provider")):Endpoint(x.EndpointIdentity),Owners=Sort(x.Owners),Bootstrap=pins};}).OrderBy(x=>x.Namespace,StringComparer.Ordinal).ToImmutableArray();
   if(resources.Select(x=>x.Namespace).Distinct(StringComparer.Ordinal).Count()!=resources.Length)throw new FormatException("Duplicate policy resource");
   return r with{Pack=pack,Bindings=bindings,Providers=r.Providers.Select(Provider).OrderBy(x=>x.Id,StringComparer.Ordinal).ToImmutableArray(),Grants=r.Grants.Select(Grant).OrderBy(x=>x.Id,StringComparer.Ordinal).ToImmutableArray(),Context=r.Context with{Resources=resources}};
  }
  public static EffectivePolicy Compile(CompileRequest request,DateTimeOffset now) {
   try{return CompileCore(Normalize(request),now);}
   catch(NullReferenceException e){throw new FormatException("Null policy member",e);}
  }
  static EffectivePolicy CompileCore(CompileRequest r,DateTimeOffset now) {
   var reasons=new List<string>();var trust=new List<string>();var proposed=new List<PolicyBinding>();
   var deps=r.Pack.Dependencies.ToDictionary(x=>x.Id,StringComparer.Ordinal);
   foreach(var binding in r.Bindings) {
    var scenario=r.Pack.Scenarios.SingleOrDefault(s=>s.Id==binding.ScenarioId)??throw new FormatException("Unknown selected scenario");
    var selected=new HashSet<string>(StringComparer.Ordinal);
    void Add(string id){if(selected.Add(id))foreach(string child in deps[id].Requires)Add(child);}
    foreach(string id in scenario.Critical.Concat(scenario.Optional))Add(id);
    string endpoint="",routeRevision="direct";ProviderRecord provider=null;
    if(binding.Route==RouteKind.NativeDoh) {
     provider=r.Providers.SingleOrDefault(p=>p.Id==binding.ProviderId)??throw new FormatException("Unknown selected provider");endpoint=provider.EndpointIdentity;routeRevision=ProviderRouteRevision(provider);
     if(provider.Revoked)reasons.Add("PROVIDER_REVOKED:"+provider.Id);
     if(provider.Bootstrap.Length==0)reasons.Add("BOOTSTRAP_UNRESOLVED:"+provider.Id);
     foreach(string cap in scenario.RequiredCapabilities.Concat(new[]{"dns.doh"}).Distinct(StringComparer.Ordinal)) {
      if(!provider.ClaimedCapabilities.Contains(cap))reasons.Add("CAPABILITY_NOT_CLAIMED:"+scenario.Id+":"+cap);
      if(!provider.ObservedCapabilities.Any(o=>o.Capability==cap&&o.EndpointIdentity==endpoint&&o.RouteRevision==routeRevision&&o.NetworkRevision==r.Context.NetworkRevision&&o.Outcome==ObservationOutcome.Passed&&o.ObservedUtc<=now&&o.ExpiresUtc>now))reasons.Add("CAPABILITY_NOT_OBSERVED:"+scenario.Id+":"+cap);
     }
    }
    else reasons.Add("DIRECT_UNVERIFIED:"+scenario.Id);
    foreach(string name in Sort(selected.Select(id=>deps[id].Namespace))) {
     if(!binding.AuthorizedScopes.Any(allowed=>PolicyNames.Contains(allowed,name)))throw new FormatException("Namespace widening outside authorized scope: "+name);
     if(binding.Exclusions.Any(ex=>PolicyNames.Overlaps(ex,name)))throw new FormatException("Dependency overlaps explicit exclusion: "+name);
     if(provider!=null&&!r.Grants.Any(g=>!g.Revoked&&g.ExpiresUtc>now&&g.OperatorIdentity==provider.OperatorIdentity&&g.EndpointIdentity==endpoint&&g.PackId==r.Pack.Id&&g.PackRevision==r.Pack.Revision&&g.Scenarios.Contains(scenario.Id)&&g.Scopes.Any(s=>PolicyNames.Contains(s,name))&&(!r.Autonomous||g.Autonomous)))trust.Add("TRUST_REQUIRED:"+scenario.Id+":"+name+":"+provider.OperatorIdentity+":"+endpoint);
     proposed.Add(new PolicyBinding(name,binding.Route,endpoint,ImmutableArray.Create(scenario.Id+"/"+binding.LeaseId),routeRevision,provider?.Bootstrap??ImmutableArray<string>.Empty));
    }
   }
   // Collapse repeated graph references before overlap comparisons: at most 128 namespaces,
   // rather than a quadratic scan of 64 scenarios x 128 dependencies.
   var bindings=proposed.GroupBy(x=>x.Namespace,StringComparer.Ordinal).OrderBy(x=>x.Key,StringComparer.Ordinal).Select(g=>{
    var first=g.First();if(g.Any(b=>b.Route!=first.Route||b.EndpointIdentity!=first.EndpointIdentity||b.RouteRevision!=first.RouteRevision))throw new FormatException("Conflicting providers/routes for shared namespace: "+g.Key);
    var owners=Sort(g.SelectMany(x=>x.Owners));if(owners.Length>1&&r.SharedDomains!=SharedDomainPolicy.CompatibleBinding)throw new FormatException("Shared namespace requires explicit compatible-binding policy");
    return first with{Owners=owners};
   }).ToImmutableArray();
   for(int i=0;i<bindings.Length;i++)for(int j=i+1;j<bindings.Length;j++) {
    var a=bindings[i];var b=bindings[j];if(!PolicyNames.Overlaps(a.Namespace,b.Namespace))continue;
    // A single scenario can contain exact rules nested in its suffix scope. Sharing between owners is explicit.
    if(a.Route!=b.Route||a.EndpointIdentity!=b.EndpointIdentity||a.RouteRevision!=b.RouteRevision)throw new FormatException("Conflicting providers/routes for shared namespace: "+a.Namespace+" / "+b.Namespace);
    if(!a.Owners.SequenceEqual(b.Owners)&&r.SharedDomains!=SharedDomainPolicy.CompatibleBinding)throw new FormatException("Shared namespace requires explicit compatible-binding policy");
   }
   var diff=new List<PolicyChange>();
   foreach(var b in bindings) {
    foreach(var foreign in r.Context.Resources.Where(x=>!x.Owned&&PolicyNames.Overlaps(x.Namespace,b.Namespace)))throw new FormatException("Foreign effective policy overlaps selected namespace: "+foreign.Namespace);
    foreach(var overlap in r.Context.Resources.Where(x=>x.Owned&&x.Namespace!=b.Namespace&&PolicyNames.Overlaps(x.Namespace,b.Namespace))) {
     if(overlap.Fingerprint!=overlap.ExpectedFingerprint)throw new FormatException("Overlapping ownership fingerprint changed: "+overlap.Namespace);
     if(overlap.Owners.Length!=0&&(overlap.Route!=b.Route||overlap.EndpointIdentity!=b.EndpointIdentity||overlap.RouteRevision!=b.RouteRevision))throw new FormatException("Conflicting route held by overlapping lease: "+overlap.Namespace);
     if(overlap.Owners.Except(b.Owners,StringComparer.Ordinal).Any()&&r.SharedDomains!=SharedDomainPolicy.CompatibleBinding)throw new FormatException("Overlapping shared lease requires explicit compatible-binding policy");
    }
    var old=r.Context.Resources.SingleOrDefault(x=>x.Namespace==b.Namespace);
    if(old!=null&&old.Fingerprint!=old.ExpectedFingerprint)throw new FormatException("Ownership fingerprint changed: "+b.Namespace);
    // Direct never creates a rule. Removing an existing owned resource still requires the future native executor.
    if(b.Route==RouteKind.Direct){if(old!=null)diff.Add(new PolicyChange("Remove",b.Namespace,old.Route,old.EndpointIdentity,ImmutableArray<string>.Empty,old.Fingerprint,"Selected Direct; preserve preimage before removal",old.RouteRevision,old.Bootstrap));continue;}
    string action=old==null?"Add":old.Route!=b.Route||old.EndpointIdentity!=b.EndpointIdentity||old.RouteRevision!=b.RouteRevision?"Replace":!old.Owners.SequenceEqual(b.Owners)?"Owners":"Keep";
    if(action!="Keep")diff.Add(new PolicyChange(action,b.Namespace,b.Route,b.EndpointIdentity,b.Owners,old?.Fingerprint??"ABSENT",action=="Owners"?"Shared resource remains while any selected owner needs it":"Bounded selected scenario dependency",b.RouteRevision,b.Bootstrap));
   }
   foreach(var old in r.Context.Resources.Where(x=>x.Owned&&!bindings.Any(b=>b.Namespace==x.Namespace))) {
    if(old.Fingerprint!=old.ExpectedFingerprint)throw new FormatException("Ownership fingerprint changed: "+old.Namespace);
    // Missing selections cannot silently remove another lease. Caller must release ownership in a later durable transaction.
    if(old.Owners.Length!=0){reasons.Add("UNSELECTED_OWNERS_PRESERVED:"+old.Namespace);continue;}
    diff.Add(new PolicyChange("Remove",old.Namespace,old.Route,old.EndpointIdentity,ImmutableArray<string>.Empty,old.Fingerprint,"No remaining owners",old.RouteRevision,old.Bootstrap));
   }
   // Existing owners outside the requested selection are retained, even when their namespace remains selected.
   for(int i=0;i<diff.Count;i++) {
    var d=diff[i];var old=r.Context.Resources.SingleOrDefault(x=>x.Namespace==d.Namespace);if(old==null||old.Owners.Length==0)continue;
    var incoming=bindings.SingleOrDefault(x=>x.Namespace==d.Namespace);
    var activeOwners=new HashSet<string>(r.Bindings.Select(x=>x.ScenarioId+"/"+x.LeaseId),StringComparer.Ordinal);
    var retained=old.Owners.Where(o=>!activeOwners.Contains(o)).ToArray();
    if(retained.Length==0)continue;
    if(incoming==null||incoming.Route!=old.Route||incoming.EndpointIdentity!=old.EndpointIdentity||incoming.RouteRevision!=old.RouteRevision)throw new FormatException("Cannot replace/remove resource held by another lease");
    if(r.SharedDomains!=SharedDomainPolicy.CompatibleBinding)throw new FormatException("Retaining another lease requires explicit compatible-binding policy");
    var owners=Sort(incoming.Owners.Concat(retained));diff[i]=d with{Owners=owners};
    bindings=bindings.Select(b=>b.Namespace==d.Namespace?b with{Owners=owners}:b).ToImmutableArray();
   }
   var changes=diff.OrderBy(x=>x.Namespace,StringComparer.Ordinal).ThenBy(x=>x.Action,StringComparer.Ordinal).ToImmutableArray();
   var recovery=changes.Select(x=>new RecoveryStep(x.Namespace,x.Action=="Add"?"Remove created resource":"Restore captured preimage","Only if owned, unchanged since apply, and no remaining lease; otherwise preserve and require review")).ToImmutableArray();
   bool blocked=trust.Count>0||reasons.Any(x=>x.StartsWith("CAPABILITY_",StringComparison.Ordinal)||x.StartsWith("PROVIDER_REVOKED:",StringComparison.Ordinal)||x.StartsWith("BOOTSTRAP_UNRESOLVED:",StringComparison.Ordinal));
   var plan=new EffectivePolicy("",r.Context.NetworkRevision,r.Context.StateRevision,true,blocked,bindings,changes,Sort(reasons),Sort(trust),recovery,r.Context.Resources,blocked?ImmutableArray<PolicyChange>.Empty:changes);
   // Canonical input plus exact output detects context changes, expired proof/grants and edited preview payloads.
   return plan with{Generation=EvidenceContext.Hash(PolicyJson.Write(r)+"\n"+PolicyJson.Write(plan))};
  }
  public static void ValidatePreview(EffectivePolicy preview,CompileRequest current,DateTimeOffset now) {
   var fresh=Compile(current,now);
   if(preview==null||PolicyJson.Write(preview)!=PolicyJson.Write(fresh))throw new FormatException("Stale or modified policy preview");
   if(preview.Blocked)throw new FormatException("Policy preview requires capabilities or trust");
  }
 }
}
