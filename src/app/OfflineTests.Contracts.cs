using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;

namespace AntigravityGeminiDns {
 public static partial class OfflineTests {
  static readonly DateTimeOffset PolicyNow=new DateTimeOffset(2026,10,17,12,0,0,TimeSpan.Zero);
  static CompileRequest PolicyFixture(bool direct=false) {
   var migration=LegacyScenarioAdapter.Migrate(ProfileCatalog.BuiltIn,new AppSettings{NoSave=true});
   var p=migration.Providers[0];
   p=p with{ObservedCapabilities=ImmutableArray.Create(new CapabilityObservation("dns.doh",p.EndpointIdentity,"network-a",PolicyNow.AddMinutes(-1),PolicyNow.AddMinutes(5),ObservationOutcome.Passed,"synthetic-fixture",PolicyCompiler.ProviderRouteRevision(p)))};
   var scopes=migration.Pack.Dependencies.Where(x=>migration.Pack.Scenarios.Single(s=>s.Id==migration.SelectedScenario).Critical.Contains(x.Id)).Select(x=>x.Namespace).ToImmutableArray();
   var binding=new BindingRequest(migration.SelectedScenario,direct?RouteKind.Direct:RouteKind.NativeDoh,direct?"":p.Id,"lease-a",scopes,ImmutableArray<string>.Empty);
   var grant=new TrustGrant("grant-a",p.OperatorIdentity,p.EndpointIdentity,migration.Pack.Id,migration.Pack.Revision,ImmutableArray.Create(binding.ScenarioId),scopes,false,PolicyNow.AddHours(1),false);
   return new CompileRequest(1,migration.Pack,ImmutableArray.Create(binding),ImmutableArray.Create(p),ImmutableArray.Create(grant),SharedDomainPolicy.Reject,new PolicyContext("network-a","state-a",ImmutableArray<PolicyResource>.Empty));
  }
  public static int RunPolicyContracts(){try{RunContractTests();Console.WriteLine("POLICY_CONTRACT_PASS "+passed);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
  static void RunContractTests() {
   foreach(string mutation in new[]{"--enable","--restore","--config-import","--autostart-enable"}) {
    Reject(()=>Program.ValidateCliArgs(new[]{"--policy-direct-preview",mutation}),"policy: standalone boundary rejects "+mutation);
    Reject(()=>Program.ValidateCliArgs(new[]{"--POLICY-DIRECT-PREVIEW",mutation}),"policy: mixed-case prefix cannot bypass standalone boundary "+mutation);
   }
   var c=ProfileCatalog.BuiltIn;var request=PolicyFixture();var plan=PolicyCompiler.Compile(request,PolicyNow);
   Check(plan.PreviewOnly&&!plan.Blocked&&plan.Bindings.Length==c.Targets.Length&&plan.Diff.All(x=>x.Action=="Add")&&plan.AllowedWrites.Length==c.Targets.Length,"policy: native dry-run matches core; finite permitted writes with preimages/recovery");
   Check(plan.RecoveryPlan.Length==plan.Diff.Length&&plan.Diff.All(x=>x.PreimageFingerprint=="ABSENT"&&x.RouteRevision==PolicyCompiler.ProviderRouteRevision(request.Providers[0])&&x.Bootstrap.SequenceEqual(request.Providers[0].Bootstrap.OrderBy(ip=>ip,StringComparer.Ordinal))),"policy: every proposed mutation has pinned route snapshot and conditional recovery");
   foreach(var pair in new[]{(false,false,c.Targets),(true,false,c.TargetsFor(true)),(false,true,Program.TargetsOf(true,false,true)),(true,true,c.TargetsForGoogle())}) {
    var s=new AppSettings{NoSave=true,Gemini=pair.Item1,Google=pair.Item2,Profile="P4",Language="en",Notify=false,StartHidden=false,CloseToTray=false,AutoFailover=true,Browser="local-only",WinX=123};
    var before=ConfigFile.Export(s);var migrated=LegacyScenarioAdapter.Migrate(c,s);
    var scenario=migrated.Pack.Scenarios.Single(x=>x.Id==migrated.SelectedScenario);
    Check(migrated.Pack.Dependencies.Where(d=>scenario.Critical.Contains(d.Id)).Select(d=>d.Namespace).OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(pair.Item3.OrderBy(x=>x,StringComparer.Ordinal)),"policy: legacy scope equivalence gemini="+pair.Item1+" google="+pair.Item2);
    var restored=LegacyScenarioAdapter.RestorePreferences(PolicyJson.Read<LegacyScenarioMigration>(PolicyJson.Write(migrated)),s);
    Check(ConfigFile.Export(restored)==before&&restored.Browser=="local-only"&&restored.WinX==123&&ConfigFile.Export(s)==before,"policy: migration/config round-trip preserves every exported preference and local state");
    Check(migrated.Providers.All(p=>p.ObservedCapabilities.Length==0)&&migrated.SelectedProvider==c.Find("P4").Id,"policy: alias resolves canonically without creating observations or grants");
   }
   var custom=new AppSettings{NoSave=true,CustomName="mine",CustomTemplate="https://resolver.example.test/dns-query",CustomServers="8.8.8.8",Profile="mine",Google=true};
   var customMigration=LegacyScenarioAdapter.Migrate(c,custom);
   Check(ConfigFile.Export(LegacyScenarioAdapter.RestorePreferences(customMigration,custom))==ConfigFile.Export(custom),"policy: custom provider preference retained without silently becoming trusted");
   Check(customMigration.Providers.Single(p=>p.Id==customMigration.SelectedProvider).EndpointIdentity==custom.CustomTemplate,"policy: custom selection migrates to finite unverified provider record");
   custom.CustomServers="";var urlOnly=LegacyScenarioAdapter.Migrate(c,custom);
   var urlProvider=urlOnly.Providers.Single(p=>p.Id==urlOnly.SelectedProvider);
   var urlRequest=request with{Providers=ImmutableArray.Create(urlProvider),Bindings=request.Bindings.SetItem(0,request.Bindings[0] with{ProviderId=urlProvider.Id})};
   var urlPlan=PolicyCompiler.Compile(urlRequest,PolicyNow);
   Check(urlPlan.Blocked&&urlPlan.AllowedWrites.Length==0&&urlPlan.Reasons.Contains("BOOTSTRAP_UNRESOLVED:"+urlProvider.Id),"policy: valid URL-only custom profile becomes bounded native bootstrap gap");
   Check(!PolicyCompiler.Compile(PolicyFixture(true) with{Providers=ImmutableArray.Create(urlProvider)},PolicyNow).Blocked,"policy: unresolved custom bootstrap cannot block Direct");
   Check(ConfigFile.Export(LegacyScenarioAdapter.RestorePreferences(urlOnly,custom))==ConfigFile.Export(custom),"policy: URL-only custom preferences round-trip unchanged");
   custom.CustomTemplate+="?mode=wire";var queryMigration=PolicyJson.Read<LegacyScenarioMigration>(PolicyJson.Write(LegacyScenarioAdapter.Migrate(c,custom)));
   var queryProvider=queryMigration.Providers.Single(p=>p.Id==queryMigration.SelectedProvider);
   Check(queryProvider.EndpointIdentity==custom.CustomTemplate&&ConfigFile.Export(LegacyScenarioAdapter.RestorePreferences(queryMigration,custom))==ConfigFile.Export(custom),"policy: fixed-query custom endpoint survives migration/JSON/preferences round-trip");
   Check(!PolicyCompiler.Compile(PolicyFixture(true) with{Providers=ImmutableArray.Create(queryProvider)},PolicyNow).Blocked,"policy: fixed-query custom endpoint does not block Direct");
   var queryMismatch=PolicyCompiler.Compile(request with{Providers=request.Providers.Select(p=>p with{EndpointIdentity=p.EndpointIdentity+"?mode=wire"}).ToImmutableArray()},PolicyNow);
   Check(queryMismatch.Blocked&&queryMismatch.TrustChanges.Length>0&&queryMismatch.Reasons.Any(x=>x.StartsWith("CAPABILITY_NOT_OBSERVED:")),"policy: distinct fixed query cannot inherit endpoint trust or observation");
   var secondQueryProvider=request.Providers[0] with{Id="query.provider",EndpointIdentity=request.Providers[0].EndpointIdentity+"?mode=other"};
   Reject(()=>PolicyCompiler.Compile(request with{Providers=request.Providers.Add(secondQueryProvider),SharedDomains=SharedDomainPolicy.CompatibleBinding,Bindings=request.Bindings.Add(request.Bindings[0] with{ScenarioId="legacy.gemini",ProviderId=secondQueryProvider.Id,AuthorizedScopes=c.TargetsFor(true).ToImmutableArray()})},PolicyNow),"policy: distinct query endpoints conflict on a shared hostname");
   Check(PolicyNames.Normalize("BÜCHER.Example.")=="xn--bcher-kva.example"&&PolicyNames.Normalize(".GOOGLE")==".google","policy: IDNA/case/root dot normalization and explicit legacy suffix");
   foreach(string bad in new[]{"*","*.example.test","a..test","-bad.test","a.test:443","a.test/path","127.0.0.1","localhost"," a.test",".127.0.0.1","a_test.example","a.test.."})Reject(()=>PolicyNames.Normalize(bad),"policy: invalid namespace "+bad);
   Check(PolicyNames.Overlaps("a.example.test",".example.test")&&PolicyNames.Overlaps(".sub.example.test",".example.test")&&!PolicyNames.Overlaps("badexample.test",".example.test")&&!PolicyNames.Overlaps("a.test","b.test"),"policy: exact/suffix overlap respects DNS label boundaries");
   var direct=PolicyFixture(true);var dp=new DirectRoute().Plan(direct,PolicyNow);var adapter=new DirectRoute();
   Check(!dp.Blocked&&dp.Diff.Length==0&&dp.AllowedWrites.Length==0&&!adapter.Inspect().Mutates&&!adapter.Prepare(dp).Changed&&!adapter.Apply(dp).Changed&&!adapter.Restore(dp).Changed&&adapter.Verify(dp).Outcome==ObservationOutcome.Unknown,"policy: Direct lifecycle performs zero mutations and gives no application proof");
   Reject(()=>adapter.Apply(plan),"policy: Direct cannot consume native mutation plan");
   PolicyCompiler.ValidatePreview(plan,request,PolicyNow);Check(true,"policy: unchanged complete preview validates");
   var round=PolicyCompiler.Compile(PolicyJson.Read<CompileRequest>(PolicyJson.Write(request)),PolicyNow);
   Check(PolicyJson.Write(plan)==PolicyJson.Write(round),"policy: request/immutable plan JSON round-trip deterministic");
   var reordered=request with{Pack=request.Pack with{Dependencies=request.Pack.Dependencies.Reverse().ToImmutableArray(),Scenarios=request.Pack.Scenarios.Reverse().ToImmutableArray()},Grants=request.Grants.Select(g=>g with{Scopes=g.Scopes.Reverse().ToImmutableArray()}).ToImmutableArray(),Bindings=request.Bindings.Select(b=>b with{AuthorizedScopes=b.AuthorizedScopes.Reverse().ToImmutableArray()}).ToImmutableArray()};
   Check(PolicyCompiler.Compile(reordered,PolicyNow).Generation==plan.Generation,"policy: ordering does not change generation");
   Reject(()=>PolicyCompiler.ValidatePreview(plan,request with{Context=request.Context with{NetworkRevision="network-b"}},PolicyNow),"policy: changed network invalidates preview");
   Reject(()=>PolicyCompiler.ValidatePreview(plan,request with{Context=request.Context with{StateRevision="state-b"}},PolicyNow),"policy: changed policy state invalidates preview");
   Reject(()=>PolicyCompiler.ValidatePreview(plan with{Diff=ImmutableArray<PolicyChange>.Empty},request,PolicyNow),"policy: edited preview rejected despite unchanged generation string");
   Reject(()=>PolicyCompiler.ValidatePreview(plan,request,PolicyNow.AddMinutes(10)),"policy: expired capability invalidates preview");
   Reject(()=>PolicyCompiler.Compile(request with{Schema=2},PolicyNow),"policy: future request schema rejected");
   Reject(()=>PackValidator.Validate(request.Pack with{Schema=2}),"policy: future pack schema rejected");
   Reject(()=>PackValidator.Validate(request.Pack with{MinimumCompiler=2}),"policy: future compiler compatibility rejected");
   Reject(()=>PolicyJson.Read<CompileRequest>(PolicyJson.Write(request).Replace("\"Schema\": 1","\"Execute\": \"powershell evil\", \"Schema\": 1")),"policy: executable/unmapped properties rejected");
   Reject(()=>PolicyJson.Read<CompileRequest>("{\"Schema\":1,\"Schema\":1}"),"policy: duplicate JSON properties rejected");
   Reject(()=>PolicyJson.Read<CompileRequest>("{}"),"policy: missing required constructor fields rejected");
   Reject(()=>PolicyJson.Read<CompileRequest>(new string(' ',262145)),"policy: oversized document rejected");
   var first=request.Pack.Dependencies[0];var second=request.Pack.Dependencies[1];
   ScenarioPack WithNodes(params DependencyContract[] nodes)=>request.Pack with{Dependencies=nodes.ToImmutableArray()};
   Reject(()=>PackValidator.Validate(WithNodes(first with{Requires=ImmutableArray.Create(second.Id)},second with{Requires=ImmutableArray.Create(first.Id)},request.Pack.Dependencies[2],request.Pack.Dependencies[3],request.Pack.Dependencies[4],request.Pack.Dependencies[5],request.Pack.Dependencies[6],request.Pack.Dependencies[7],request.Pack.Dependencies[8])),"policy: selected/unselected dependency cycles rejected");
   Reject(()=>PackValidator.Validate(request.Pack with{Dependencies=request.Pack.Dependencies.SetItem(0,first with{Requires=ImmutableArray.Create("unknown")})}),"policy: unknown dependency rejected");
   var deep=Enumerable.Range(0,17).Select(i=>new DependencyContract("n"+i,"n"+i+".example.test","dns","Any","fixture",i==16?ImmutableArray<string>.Empty:ImmutableArray.Create("n"+(i+1)))).ToImmutableArray();
   Reject(()=>PackValidator.Validate(request.Pack with{Dependencies=request.Pack.Dependencies.AddRange(deep)}),"policy: depth budget enforced");
   Reject(()=>PackValidator.Validate(request.Pack with{Dependencies=Enumerable.Range(0,129).Select(i=>first with{Id="x"+i}).ToImmutableArray()}),"policy: dependency count budget enforced");
   var s0=request.Pack.Scenarios[0];
   foreach(var probe in new[]{new ProbeContract("shell",first.Id,"RUN",1000,1024),new ProbeContract("https",first.Id,"POST",1000,1024),new ProbeContract("dns",first.Id,"QUERY",10001,1024),new ProbeContract("dns",first.Id,"QUERY",1000,65537)})Reject(()=>PackValidator.Validate(request.Pack with{Scenarios=request.Pack.Scenarios.SetItem(0,s0 with{Probes=ImmutableArray.Create(probe)})}),"policy: unsafe or unbounded probe rejected "+probe.Kind+"/"+probe.Method+"/"+probe.TimeoutMs+"/"+probe.MaxBytes);
   var binding=request.Bindings[0];
   Reject(()=>PolicyCompiler.Compile(request with{Bindings=ImmutableArray.Create(binding with{AuthorizedScopes=ImmutableArray.Create("other.example.test")})},PolicyNow),"policy: namespace widening rejected");
   Reject(()=>PolicyCompiler.Compile(request with{Bindings=ImmutableArray.Create(binding with{Exclusions=ImmutableArray.Create(".googleapis.com")})},PolicyNow),"policy: exact/suffix exclusion rejected");
   var noProof=PolicyCompiler.Compile(request with{Providers=request.Providers.Select(p=>p with{ObservedCapabilities=ImmutableArray<CapabilityObservation>.Empty}).ToImmutableArray()},PolicyNow);
   Check(noProof.Blocked&&noProof.AllowedWrites.Length==0&&noProof.Reasons.Any(x=>x.StartsWith("CAPABILITY_NOT_OBSERVED:")),"policy: claims cannot substitute for current observed capability");
   foreach(var grant in new[]{request.Grants[0] with{Revoked=true},request.Grants[0] with{ExpiresUtc=PolicyNow},request.Grants[0] with{OperatorIdentity="another-operator"},request.Grants[0] with{EndpointIdentity="https://other.example.test/dns-query"},request.Grants[0] with{PackRevision="old"},request.Grants[0] with{Scopes=ImmutableArray.Create("other.example.test")}}) {
    var blocked=PolicyCompiler.Compile(request with{Grants=ImmutableArray.Create(grant)},PolicyNow);
    Check(blocked.Blocked&&blocked.AllowedWrites.Length==0&&blocked.TrustChanges.Length>0,"policy: trust boundary/expiry/revocation mismatch blocks writes");
   }
   Check(PolicyCompiler.Compile(request with{Autonomous=true},PolicyNow).Blocked,"policy: manual grant never authorizes autonomous action");
   Check(PolicyCompiler.Compile(request with{Providers=request.Providers.Select(p=>p with{Revoked=true}).ToImmutableArray()},PolicyNow).Blocked,"policy: revoked provider blocks even with proof/grant");
   var shared=request with{Bindings=request.Bindings.Add(binding with{ScenarioId="legacy.gemini",AuthorizedScopes=c.TargetsFor(true).ToImmutableArray()})};
   Reject(()=>PolicyCompiler.Compile(shared,PolicyNow),"policy: shared domain requires explicit policy");
   var sp=PolicyCompiler.Compile(shared with{SharedDomains=SharedDomainPolicy.CompatibleBinding},PolicyNow);
   Check(sp.Bindings.Where(b=>c.Targets.Contains(b.Namespace)).All(b=>b.Owners.Length==2),"policy: compatible shared binding aggregates owners");
   Reject(()=>PolicyCompiler.Compile(shared with{SharedDomains=SharedDomainPolicy.CompatibleBinding,Bindings=shared.Bindings.SetItem(1,shared.Bindings[1] with{Route=RouteKind.Direct,ProviderId=""})},PolicyNow),"policy: conflicting direct/provider same hostname rejected");
   var resource=new PolicyResource(c.Targets[0],RouteKind.NativeDoh,request.Providers[0].EndpointIdentity,ImmutableArray.Create("other/lease-b"),true,"fp","fp",PolicyCompiler.ProviderRouteRevision(request.Providers[0]),request.Providers[0].Bootstrap);
   var retained=PolicyCompiler.Compile(request with{SharedDomains=SharedDomainPolicy.CompatibleBinding,Context=request.Context with{Resources=ImmutableArray.Create(resource)}},PolicyNow);
   Check(retained.Bindings.Single(b=>b.Namespace==resource.Namespace).Owners.Contains("other/lease-b"),"policy: another lease retains shared resource ownership");
   Reject(()=>PolicyCompiler.Compile(direct with{Context=direct.Context with{Resources=ImmutableArray.Create(resource)}},PolicyNow),"policy: direct selection cannot remove another lease resource");
   Reject(()=>PolicyCompiler.Compile(request with{Context=request.Context with{Resources=ImmutableArray.Create(resource with{Owned=false})}},PolicyNow),"policy: foreign effective namespace rejected");
   Reject(()=>PolicyCompiler.Compile(request with{Context=request.Context with{Resources=ImmutableArray.Create(resource with{Fingerprint="changed"})}},PolicyNow),"policy: changed ownership fingerprint rejected");
   var foreignSuffix=resource with{Namespace=".googleapis.com",Owned=false};
   Reject(()=>PolicyCompiler.Compile(request with{Context=request.Context with{Resources=ImmutableArray.Create(foreignSuffix)}},PolicyNow),"policy: foreign suffix overlapping exact selection rejected");
   Reject(()=>PolicyCompiler.Compile(request with{Context=request.Context with{Resources=ImmutableArray.Create(resource with{Namespace=".googleapis.com",EndpointIdentity="https://other.example.test/dns-query",RouteRevision=PolicyCompiler.RouteRevision(RouteKind.NativeDoh,"https://other.example.test/dns-query",resource.Bootstrap)})}},PolicyNow),"policy: existing owned suffix cannot hide conflicting provider for selected exact host");
   Reject(()=>PolicyCompiler.Compile(request with{Context=request.Context with{Resources=ImmutableArray.Create(resource with{Namespace=".googleapis.com"})}},PolicyNow),"policy: overlapping existing suffix lease requires explicit sharing policy");
   Check(!PolicyCompiler.Compile(request with{SharedDomains=SharedDomainPolicy.CompatibleBinding,Context=request.Context with{Resources=ImmutableArray.Create(resource with{Namespace=".googleapis.com"})}},PolicyNow).Blocked,"policy: explicitly compatible existing suffix lease retained");
   Reject(()=>PolicyCompiler.Compile(request with{Context=request.Context with{Resources=ImmutableArray.Create(resource)}},PolicyNow),"policy: existing shared lease requires explicit compatible-binding policy");
   var keep=PolicyCompiler.Compile(request with{Bindings=ImmutableArray<BindingRequest>.Empty,Context=request.Context with{Resources=ImmutableArray.Create(resource)}},PolicyNow);
   Check(keep.Diff.Length==0&&keep.Reasons.Any(x=>x.StartsWith("UNSELECTED_OWNERS_PRESERVED:")),"policy: omitted scenario never discards another lease");
   var own=resource with{Owners=ImmutableArray.Create(binding.ScenarioId+"/"+binding.LeaseId)};
   Check(PolicyCompiler.Compile(request with{Context=request.Context with{Resources=ImmutableArray.Create(own)}},PolicyNow).Diff.All(x=>x.Namespace!=own.Namespace),"policy: unchanged binding yields no resource delta");
   var changedPins=request.Providers[0] with{Bootstrap=ImmutableArray.Create("8.8.8.8")};
   var stalePins=PolicyCompiler.Compile(request with{Providers=ImmutableArray.Create(changedPins)},PolicyNow);
   Check(stalePins.Blocked&&stalePins.AllowedWrites.Length==0&&stalePins.TrustChanges.Length==0&&stalePins.Reasons.Any(x=>x.StartsWith("CAPABILITY_NOT_OBSERVED:")),"policy: changed pins require fresh technical proof independently of operator trust");
   var freshPins=changedPins with{ObservedCapabilities=changedPins.ObservedCapabilities.Select(o=>o with{RouteRevision=PolicyCompiler.ProviderRouteRevision(changedPins)}).ToImmutableArray()};
   var replacement=PolicyCompiler.Compile(request with{Providers=ImmutableArray.Create(freshPins),Context=request.Context with{Resources=ImmutableArray.Create(own)}},PolicyNow);
   Check(!replacement.Blocked&&replacement.Diff.Single(d=>d.Namespace==own.Namespace).Action=="Replace"&&replacement.Diff.Single(d=>d.Namespace==own.Namespace).Bootstrap.SequenceEqual(freshPins.Bootstrap)&&replacement.Preimage.Single().Bootstrap.SequenceEqual(own.Bootstrap.OrderBy(ip=>ip,StringComparer.Ordinal)),"policy: fresh new pins replace owned route with complete old/new pin snapshots");
   var pinProvider=freshPins with{Id="other.pins"};
   Reject(()=>PolicyCompiler.Compile(shared with{SharedDomains=SharedDomainPolicy.CompatibleBinding,Providers=request.Providers.Add(pinProvider),Bindings=shared.Bindings.SetItem(1,shared.Bindings[1] with{ProviderId=pinProvider.Id})},PolicyNow),"policy: same URI with distinct pinned routes cannot share a namespace");
   Reject(()=>PolicyCompiler.Compile(request with{Providers=ImmutableArray.Create(freshPins),SharedDomains=SharedDomainPolicy.CompatibleBinding,Context=request.Context with{Resources=ImmutableArray.Create(resource with{Namespace=".googleapis.com"})}},PolicyNow),"policy: retained suffix lease protects its pinned route even at same URI");
   Check(PolicyCompiler.Compile(request with{Providers=request.Providers.Select(p=>p with{Bootstrap=p.Bootstrap.Reverse().ToImmutableArray()}).ToImmutableArray()},PolicyNow).Generation==plan.Generation,"policy: reordered equivalent pins preserve proof and generation");
   var equivalentProvider=request.Providers[0] with{Id="equivalent.pins",Bootstrap=request.Providers[0].Bootstrap.Reverse().ToImmutableArray()};
   Check(PolicyCompiler.Compile(shared with{SharedDomains=SharedDomainPolicy.CompatibleBinding,Providers=request.Providers.Add(equivalentProvider),Bindings=shared.Bindings.SetItem(1,shared.Bindings[1] with{ProviderId=equivalentProvider.Id})},PolicyNow).Bindings.Where(b=>c.Targets.Contains(b.Namespace)).All(b=>b.Owners.Length==2),"policy: equivalent reordered pinned routes can share explicitly");
   Reject(()=>PolicyCompiler.Compile(request with{Context=request.Context with{Resources=ImmutableArray.Create(own with{Bootstrap=ImmutableArray.Create("8.8.8.8")})}},PolicyNow),"policy: resource revision cannot hide edited preimage pins");
   var wideNodes=Enumerable.Range(0,128).Select(i=>new DependencyContract("wide"+i,"n"+i+".example.test","dns","Any","synthetic fixture",ImmutableArray<string>.Empty)).ToImmutableArray();
   var wideScenarios=Enumerable.Range(0,64).Select(i=>new ScenarioContract("scenario"+i,"wide.service","fixture.client","Unverified Direct fixture",wideNodes.Select(d=>d.Id).ToImmutableArray(),ImmutableArray<string>.Empty,ImmutableArray<string>.Empty,ImmutableArray<ProbeContract>.Empty)).ToImmutableArray();
   var widePack=new ScenarioPack(1,"wide.pack","r1",1,"synthetic bounded stress fixture",ImmutableArray.Create(new ServiceContract("wide.service",ImmutableArray.Create("fixture.client"),wideScenarios.Select(s=>s.Id).ToImmutableArray())),wideScenarios,wideNodes);
   var wideRequest=direct with{Pack=widePack,Bindings=wideScenarios.Select(s=>new BindingRequest(s.Id,RouteKind.Direct,"","fixture.lease",wideNodes.Select(d=>d.Namespace).ToImmutableArray(),ImmutableArray<string>.Empty)).ToImmutableArray(),SharedDomains=SharedDomainPolicy.CompatibleBinding};
   var widePlan=PolicyCompiler.Compile(wideRequest,PolicyNow);
   Check(widePlan.Bindings.Length==128&&widePlan.Bindings.All(b=>b.Owners.Length==64)&&widePlan.Diff.Length==0,"policy: maximum bounded graph compiles shared references without expansion of overlap work");
  }
 }
}
