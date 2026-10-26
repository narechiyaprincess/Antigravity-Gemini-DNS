using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;

namespace AntigravityGeminiDns {
 public sealed record LocalCatalogRoots(int Schema,string Channel,ImmutableArray<CatalogRoot> Roots);
 public static class CatalogCli {
  public static bool IsCommand(string[] args)=>args.Any(x=>x.StartsWith("--catalog-",StringComparison.OrdinalIgnoreCase));
  public static void Validate(string[] args) {
   bool valid=args.Length==1&&args[0]=="--catalog-contract-test"||
    args.Length==3&&(args[0]=="--catalog-local-status"||args[0]=="--catalog-local-rollback"||args[0]=="--catalog-local-ui")||
    args.Length==4&&args[0]=="--catalog-local-import";
   if(!valid||args.Skip(1).Any(x=>string.IsNullOrWhiteSpace(x)||x.StartsWith("--",StringComparison.Ordinal)))throw new ArgumentException("Catalog commands require standalone canonical spelling and exact file arguments");
  }
  public static LocalCatalogRoots ReadRoots(string path) {
   using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(f.Length>32768)throw new FormatException("CATALOG_ROOT_FILE_SIZE");
   var bytes=new byte[(int)f.Length];f.ReadExactly(bytes);var config=PolicyJson.Read<LocalCatalogRoots>(new UTF8Encoding(false,true).GetString(bytes));
   if(config.Schema!=1||config.Channel!="local-test")throw new FormatException("LOCAL_TEST_ROOTS_ONLY");
   PolicyNames.List(config.Roots,8);if(config.Roots.Length==0||config.Roots.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=config.Roots.Length)throw new FormatException("CATALOG_ROOTS_EMPTY_OR_DUPLICATE");
   foreach(var r in config.Roots) {
    PolicyNames.Id(r.Id);if(r.PublicKey==null||r.PublicKey.Length>1024||r.NotBeforeUtc>=r.NotAfterUtc)throw new FormatException("CATALOG_ROOT_INVALID");
    try{using var k=System.Security.Cryptography.ECDsa.Create();byte[] spki=Convert.FromBase64String(r.PublicKey);k.ImportSubjectPublicKeyInfo(spki,out int used);if(used!=spki.Length||k.ExportParameters(false).Curve.Oid.Value!="1.2.840.10045.3.1.7")throw new FormatException("CATALOG_ROOT_CURVE");}catch(System.Security.Cryptography.CryptographicException){throw new FormatException("CATALOG_ROOT_KEY");}
   }
   return config;
  }
  public static bool DryRun(ScenarioPack pack,DateTimeOffset now) {
   // Every scenario is checked against the real compiler as Direct, with empty trust/provider/resource sets.
   // Native activation remains exclusively the coordinator's job and requires independent grants.
   foreach(var s in pack.Scenarios) {
    var binding=new BindingRequest(s.Id,RouteKind.Direct,"","catalog-preview",pack.Dependencies.Select(d=>d.Namespace).ToImmutableArray(),ImmutableArray<string>.Empty);
    var request=new CompileRequest(1,pack,ImmutableArray.Create(binding),ImmutableArray<ProviderRecord>.Empty,ImmutableArray<TrustGrant>.Empty,SharedDomainPolicy.Reject,new PolicyContext("catalog-local-preview","catalog-data-only",ImmutableArray<PolicyResource>.Empty));
    var plan=PolicyCompiler.Compile(request,now);if(plan.Blocked||plan.AllowedWrites.Length!=0)return false;
   }
   return true;
  }
  public static bool TryRun(string[] args,out int exit) {
   exit=0;if(!IsCommand(args)||args.Length>0&&args[0]=="--catalog-local-ui")return false;
   try {
    Validate(args);if(args[0]=="--catalog-contract-test"){exit=OfflineTests.RunSignedCatalog();return true;}
    var config=ReadRoots(args[2]);var verifier=new CatalogVerifier(config.Roots,config.Channel);
    var seed=LegacyScenarioAdapter.Migrate(ProfileCatalog.BuiltIn,new AppSettings{NoSave=true}).Pack;
    var store=new CatalogStore(args[1],verifier,seed);var now=DateTimeOffset.UtcNow;CatalogSelection selection;
    if(args[0]=="--catalog-local-import")selection=store.Install(CatalogStore.ReadLocal(args[3]),now,p=>DryRun(p,now),p=>PackValidator.Validate(p)!=null);
    else if(args[0]=="--catalog-local-rollback")selection=store.Rollback(now);
    else selection=store.Select(now);
    Console.WriteLine(PolicyJson.Write(selection));
   }catch(Exception e)when(e is ArgumentException||e is FormatException||e is IOException||e is UnauthorizedAccessException){Console.Error.WriteLine("CATALOG_REJECTED: "+e.Message);exit=1;}
   return true;
  }
 }
}
