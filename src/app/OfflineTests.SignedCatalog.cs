using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AntigravityGeminiDns {
 public static partial class OfflineTests {
  public static int RunSignedCatalog(){try{RunSignedCatalogContracts();Console.WriteLine("SIGNED_CATALOG_PASS "+passed);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
  static void RunSignedCatalogContracts() {
   var now=new DateTimeOffset(2026,10,19,12,0,0,TimeSpan.Zero);
   using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);using var rotated=ECDsa.Create(ECCurve.NamedCurves.nistP256);
   var root=new CatalogRoot("explicit-local-test-key",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),now.AddDays(-1),now.AddDays(60),false);
   var root2=root with{Id="explicit-rotated-test-key",PublicKey=Convert.ToBase64String(rotated.ExportSubjectPublicKeyInfo())};
   var verifier=new CatalogVerifier(new[]{root,root2},"local-test");
   var seed=LegacyScenarioAdapter.Migrate(ProfileCatalog.BuiltIn,new AppSettings{NoSave=true}).Pack;
   SignedCatalogDocument Sign(long sequence,string revision="test.1",CatalogManifest manifest=null,ECDsa signingKey=null) {
    string payload=PolicyJson.Write(PackValidator.Validate(seed with{Revision=revision}));
    var m=manifest??new CatalogManifest(1,"local-test",sequence,root.Id,"ECDSA-P256-SHA256-P1363",now.AddHours(-1),now.AddDays(2),1,1,1,CatalogVerifier.Hash(payload));
    return new(m,payload,Convert.ToBase64String((signingKey??key).SignData(CatalogVerifier.SignedBytes(m),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
   }
   var first=Sign(1);Check(verifier.Verify(first,now).Revision=="test.1","signed catalog: explicit test root validates canonical data");
   Check(CatalogVerifier.SignedBytes(first.Manifest).SequenceEqual(CatalogVerifier.SignedBytes(PolicyJson.Read<CatalogManifest>(PolicyJson.Write(first.Manifest)))),"signed catalog: deterministic domain-separated manifest bytes");
   Reject(()=>verifier.Verify(first with{Payload=first.Payload.Replace("test.1","test.9")},now),"signed catalog: payload tampering rejected");
   Reject(()=>verifier.Verify(first with{Manifest=first.Manifest with{Sequence=9}},now),"signed catalog: manifest tampering rejected");
   Reject(()=>verifier.Verify(first with{Signature=Convert.ToBase64String(new byte[64])},now),"signed catalog: invalid signature rejected");
   Reject(()=>verifier.Verify(Sign(1,manifest:first.Manifest with{KeyId="unknown"}),now),"signed catalog: unknown key rejected");
   Reject(()=>new CatalogVerifier(new[]{root with{Revoked=true}},"local-test").Verify(first,now),"signed catalog: revoked key rejected");
   Reject(()=>verifier.Verify(Sign(1,manifest:first.Manifest with{Schema=2}),now),"signed catalog: unknown schema rejected");
   Reject(()=>verifier.Verify(Sign(1,manifest:first.Manifest with{Channel="release"}),now),"signed catalog: channel isolation");
   Reject(()=>verifier.Verify(Sign(1,manifest:first.Manifest with{Algorithm="none"}),now),"signed catalog: algorithm substitution rejected");
   Reject(()=>verifier.Verify(Sign(1,manifest:first.Manifest with{ExpiresUtc=now}),now),"signed catalog: expiry rejected");
   Reject(()=>verifier.Verify(Sign(1,manifest:first.Manifest with{IssuedUtc=now.AddMinutes(1)}),now),"signed catalog: future issuance rejected");
   Reject(()=>verifier.Verify(Sign(1,manifest:first.Manifest with{MinimumClient=2,MaximumClient=3}),now),"signed catalog: incompatible client rejected");
   Reject(()=>verifier.Verify(Sign(1,manifest:first.Manifest with{Compiler=2}),now),"signed catalog: incompatible compiler rejected");
   Reject(()=>CatalogVerifier.Parse(new byte[CatalogVerifier.MaximumDocumentBytes+1]),"signed catalog: bounded bytes before parse");
   Reject(()=>CatalogVerifier.Parse(new byte[]{0xff}),"signed catalog: malformed UTF8 rejected");
   byte[] wire=Encoding.UTF8.GetBytes(PolicyJson.Write(first));Check(PolicyJson.Write(verifier.Verify(CatalogVerifier.Parse(wire),now))==PolicyJson.Write(verifier.Verify(first,now)),"signed catalog: local wire round trip");
   Reject(()=>CatalogVerifier.Parse(Encoding.UTF8.GetBytes(PolicyJson.Write(first).Replace("\"Signature\":","\"Unknown\":1,\"Signature\":"))),"signed catalog: unknown property cannot smuggle trust or executable data");
   Reject(()=>CatalogVerifier.Parse(Encoding.UTF8.GetBytes(PolicyJson.Write(first).Replace("\"Signature\":","\"Signature\":\"x\",\"Signature\":"))),"signed catalog: duplicate fields rejected");
   string directory=Path.Combine(Path.GetTempPath(),"agdns-signed-catalog-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
   try {
    var store=new CatalogStore(directory,verifier,seed);
    Check(store.Select(now).Source=="SEED"&&!File.Exists(store.StatePath),"signed catalog: first offline selection uses embedded seed without writes");
    Check(store.Install(first,now,p=>p.Dependencies.Length<=128,p=>true).HighWater==1,"signed catalog: local data generation commits");
    string baseline=File.ReadAllText(store.StatePath);
    Reject(()=>store.Install(first,now,p=>true,p=>true),"signed catalog: replay rejected");
    Reject(()=>store.Install(Sign(2),now,p=>false,p=>true),"signed catalog: policy dry-run rejection retains generation");
    foreach(string phase in new[]{"validated","flushed"}) {
     try{store.Install(Sign(2,"test.2"),now,p=>true,p=>true,x=>{if(x==phase)throw new IOException("simulated interruption");});throw new Exception("Checkpoint not reached");}catch(IOException e){Check(e.Message=="simulated interruption","signed catalog: interruption injected "+phase);}
     Check(File.ReadAllText(store.StatePath)==baseline&&new CatalogStore(directory,verifier,seed).Select(now).Pack.Revision=="test.1","signed catalog: interrupted update retains exact previous generation "+phase);
    }
    Reject(()=>store.Install(Sign(2),now.AddMinutes(-1),p=>true,p=>true),"signed catalog: wall clock rollback blocks updates");
    Check(store.Select(now.AddMinutes(-1)).Source=="SEED","signed catalog: clock recovery uses seed without accepting expired remote data");
    var second=Sign(2,"test.2");store.Install(second,now,p=>true,p=>true);
    Check(new CatalogStore(directory,verifier,seed).Select(now).Pack.Revision=="test.2","signed catalog: fresh process offline startup uses current");
    var rolled=store.Rollback(now);Check(rolled.Pack.Revision=="test.1"&&rolled.HighWater==2,"signed catalog: explicit LKG rollback retains antirollback watermark");
    Reject(()=>store.Install(second,now,p=>true,p=>true),"signed catalog: replay after LKG rollback rejected");
    var rotatedDoc=Sign(3,"test.1",first.Manifest with{Sequence=3,KeyId=root2.Id},rotated);
    store.Install(rotatedDoc,now,p=>true,p=>true);
    var revokedStore=new CatalogStore(directory,new CatalogVerifier(new[]{root,root2 with{Revoked=true}},"local-test"),seed);
    Check(revokedStore.Select(now).Source=="LKG"&&revokedStore.Select(now).HighWater==3,"signed catalog: locally revoked rotated key falls back to independently valid LKG");
    Check(store.Select(now.AddDays(3)).Source=="SEED","signed catalog: expired current and LKG return seed offline");
    Reject(()=>store.Install(Sign(4,"test.4"),now,p=>true,p=>false),"signed catalog: post-activation failed health rolls back");
    Check(store.Select(now).Pack.Revision=="test.1"&&store.Select(now).HighWater==4,"signed catalog: failed health retains previous data and advances replay floor");
    Reject(()=>store.Install(Sign(4),now,p=>true,p=>true),"signed catalog: unhealthy generation cannot replay");
    foreach(var interrupt in new[]{(5L,"activated-pending-health"),(6L,"healthy-before-finalize")}) {
     try{store.Install(Sign(interrupt.Item1,"test.interrupted"),now,p=>true,p=>true,x=>{if(x==interrupt.Item2)throw new IOException("simulated pending interruption");});throw new Exception("Checkpoint not reached");}catch(IOException e){Check(e.Message=="simulated pending interruption","signed catalog: pending interruption injected "+interrupt.Item2);}
     var restart=new CatalogStore(directory,verifier,seed).Select(now);
     Check(restart.Pack.Revision=="test.1"&&restart.Source=="LKG"&&restart.HighWater==interrupt.Item1,"signed catalog: restart ignores pending generation and retains previous plus replay floor "+interrupt.Item2);
    }
    // A valid signature can supply a pack, but the compiler still needs grants and observations.
    var request=PolicyFixture() with{Pack=verifier.Verify(first,now),Grants=ImmutableArray<TrustGrant>.Empty};
    Check(PolicyCompiler.Compile(request,PolicyNow).Blocked,"signed catalog: signature cannot grant provider route trust");
    Check(!Directory.GetFiles(directory).Any(x=>Path.GetFileName(x).Contains("journal",StringComparison.OrdinalIgnoreCase)),"signed catalog: data store does not create or replace a recovery journal");
    File.WriteAllText(store.StatePath,"corrupt");
    Check(store.Select(now).Reason=="STATE_UNREADABLE_UPDATES_BLOCKED","signed catalog: corrupt state gives seed and explicit blocked update reason");
    Reject(()=>store.Install(Sign(5),now,p=>true,p=>true),"signed catalog: corrupt watermark cannot be reset by import");
    Check(File.ReadAllText(store.StatePath)=="corrupt","signed catalog: corruption diagnostics retained");
    RunRuntimeCatalogContracts(directory,root,verifier,seed,now,Sign);
   } finally {
    // Every path here is created by this test under its unique temporary directory.
    foreach(string path in Directory.GetFiles(directory))File.Delete(path);Directory.Delete(directory);
   }
  }
  static void RunRuntimeCatalogContracts(string directory,CatalogRoot root,CatalogVerifier verifier,ScenarioPack seed,DateTimeOffset now,
   Func<long,string,CatalogManifest,ECDsa,SignedCatalogDocument> sign) {
   string runtime=Path.Combine(directory,"runtime");Directory.CreateDirectory(runtime);string rootPath=Path.Combine(runtime,"roots.json");
   void Roots(bool revoked)=>File.WriteAllText(rootPath,PolicyJson.Write(new LocalCatalogRoots(1,"local-test",ImmutableArray.Create(root with{Revoked=revoked}))));
   Roots(false);var store=new CatalogStore(runtime,verifier,seed);
   try {
    RuntimeCatalog.Configure(runtime,rootPath);
    var initial=RuntimeCatalog.Select(seed,now);Check(initial.Source=="SEED","runtime catalog: explicit opt-in offline seed");
    store.Install(sign(1,"runtime.1",null,null),now,p=>CatalogCli.DryRun(p,now),p=>true);
    var first=RuntimeCatalog.Select(seed,now);Check(first.Source=="CURRENT"&&first.Pack.Revision=="runtime.1"&&first.Fingerprint!=initial.Fingerprint,"runtime catalog: shared selected source hot reloads signed revision and invalidates seed evidence");
    var scopes=ProfileCatalog.BuiltIn.Targets;
    Check(RuntimeCatalog.RequireNativePack(first.Pack,"legacy.core",scopes).Revision=="runtime.1","runtime catalog: compiler bridge consumes selected signed pack");
    Reject(()=>RuntimeCatalog.RequireNativePack(first.Pack,"missing.scenario",scopes),"runtime catalog: no silent fallback for missing selected scenario");
    Reject(()=>RuntimeCatalog.RequireNativePack(first.Pack,"legacy.gemini",scopes),"runtime catalog: signed broader namespace scope refused by finite native bridge");
    Reject(()=>RuntimeCatalog.RequireNativePack(first.Pack,"legacy.core",scopes.Take(1)),"runtime catalog: signed narrower native request refused");
    store.Install(sign(2,"runtime.2",null,null),now,p=>CatalogCli.DryRun(p,now),p=>true);
    var second=RuntimeCatalog.Select(seed,now);Check(second.Pack.Revision=="runtime.2"&&second.Fingerprint!=first.Fingerprint,"runtime catalog: replacement invalidates prior prepared pack evidence");
    var compile=PolicyFixture() with{Pack=first.Pack,Grants=ImmutableArray<TrustGrant>.Empty};
    Check(PolicyCompiler.Compile(compile,PolicyNow).Blocked&&PolicyCompiler.Compile(compile,PolicyNow).Generation!=PolicyCompiler.Compile(compile with{Pack=second.Pack},PolicyNow).Generation,"runtime catalog: selected revision enters real compiler generation without creating trust");
    var migration=LegacyScenarioAdapter.Migrate(ProfileCatalog.BuiltIn,new AppSettings{NoSave=true,Gemini=Program.Gemini,Google=Program.Google});
    var provider=migration.Providers[0];var nativeRequest=new AssistedRequest(AssistedMode.Native,provider.Id,provider.EndpointIdentity,provider.Bootstrap,Program.Targets.ToImmutableArray(),Program.Gemini,Program.Google);
    var stale=new AssistedPrepared(nativeRequest,new AssistedSnapshot("stale-catalog-network","state","baseline","Absent",false,false,"{}"),compile,PolicyCompiler.Compile(compile,PolicyNow),now.AddMinutes(5));
    using(var native=new LiveAssistedBackend()) {
     var child=native.Apply(stale,System.Threading.CancellationToken.None).GetAwaiter().GetResult();
     Check(child.Code!=0&&child.Quiescent&&!child.MayHaveChanged&&child.Output.Contains("ASSISTED_PREVIEW_STALE"),"runtime catalog: live native Apply rejects stale catalog before elevated launch");
     var fixture=new AssistedFixture{Child=child,ChangeOnApply=false};var coordinator=new AssistedCoordinator(fixture,()=>fixture.Now);
     var refused=coordinator.Run(AssistedRequestFixture(),AcceptAssisted).GetAwaiter().GetResult();
     Check(refused.Outcome==AssistedOutcome.Refused&&!refused.Changed&&!coordinator.RecoveryPending&&fixture.Recovers==0,"runtime catalog: actual prelaunch refusal through coordinator cannot latch uncertain recovery");
     fixture.Child=new(0,"{}",true,false);
     var disabled=coordinator.Run(AssistedRequestFixture(AssistedMode.Disable),AcceptAssisted).GetAwaiter().GetResult();
     Check(disabled.Outcome==AssistedOutcome.DisabledVerified&&fixture.Disables==1&&!coordinator.RecoveryPending,"runtime catalog: harmless stale refusal leaves later manual stop available");
    }
    store.Rollback(now);var rollback=RuntimeCatalog.Select(seed,now);Check(rollback.Pack.Revision=="runtime.1"&&rollback.HighWater==2&&rollback.Fingerprint!=second.Fingerprint,"runtime catalog: explicit LKG hot reload retains replay floor and changes evidence");
    Roots(true);var revoked=RuntimeCatalog.Select(seed,now);Check(revoked.Source=="SEED"&&revoked.Fingerprint!=rollback.Fingerprint,"runtime catalog: root revocation reverified on refresh falls back seed and invalidates evidence");
    Roots(false);var expired=RuntimeCatalog.Select(seed,now.AddDays(3));Check(expired.Source=="SEED"&&expired.Fingerprint!=first.Fingerprint,"runtime catalog: expiry reverified without network or native mutation");
    File.WriteAllText(rootPath,"invalid");var invalid=RuntimeCatalog.Select(seed,now);Check(invalid.Source=="SEED"&&invalid.Reason=="LOCAL_ROOTS_UNREADABLE_OR_INVALID","runtime catalog: invalid roots fail closed visibly");
    Check(!Directory.GetFiles(runtime).Any(p=>Path.GetFileName(p).Contains("journal",StringComparison.OrdinalIgnoreCase)),"runtime catalog: refresh and source changes never write native policy journal");
   }finally{RuntimeCatalog.ResetForTests();foreach(string file in Directory.GetFiles(runtime))File.Delete(file);Directory.Delete(runtime);}
  }
 }
}
