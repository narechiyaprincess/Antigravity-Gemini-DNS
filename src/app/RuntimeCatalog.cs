using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace AntigravityGeminiDns {
 public sealed record RuntimeCatalogView(ScenarioPack Pack,string Source,string Reason,long HighWater,string Fingerprint);
 // Process-local opt-in only. Refresh never downloads data, writes policy or imports trust.
 public static class RuntimeCatalog {
  static readonly object gate=new();static string directory,rootsPath;static RuntimeCatalogView current;
  public static bool Configured {get{lock(gate)return directory!=null;}}
  public static void Configure(string store,string roots) {
   string path=Path.GetFullPath(store),root=Path.GetFullPath(roots);CatalogCli.ReadRoots(root);
   lock(gate){directory=path;rootsPath=root;current=null;}
   Refresh();
  }
  internal static void ResetForTests(){lock(gate){directory=null;rootsPath=null;current=null;}}
  public static string EvidenceRevision {get{lock(gate)return current?.Fingerprint??"embedded";}}
  public static string DisplayRevision {get{lock(gate)return current==null?"SEED embedded":current.Source+" "+current.Pack.Id+" / "+current.Pack.Revision;}}
  public static RuntimeCatalogView Current {get{lock(gate)return current;}}
  public static RuntimeCatalogView Refresh()=>Select(LegacyScenarioAdapter.Migrate(ProfileCatalog.BuiltIn,new AppSettings{NoSave=true}).Pack,DateTimeOffset.UtcNow);
  public static RuntimeCatalogView Select(ScenarioPack seed,DateTimeOffset now) {
   lock(gate) {
    seed=PackValidator.Validate(seed);CatalogSelection selected;string rootRevision="embedded";
    if(directory==null)selected=new(seed,"SEED","LOCAL_CATALOG_NOT_CONFIGURED",0);
    else try {
     var roots=CatalogCli.ReadRoots(rootsPath);rootRevision=EvidenceContext.Hash(PolicyJson.Write(roots));
     selected=new CatalogStore(directory,new CatalogVerifier(roots.Roots,roots.Channel),seed).Select(now);
    }catch(Exception e)when(e is IOException||e is UnauthorizedAccessException||e is FormatException||e is ArgumentException) {
     selected=new(seed,"SEED","LOCAL_ROOTS_UNREADABLE_OR_INVALID",0);rootRevision="invalid-roots";
    }
    string fingerprint=EvidenceContext.Hash(PolicyJson.Write(selected)+"|"+rootRevision);
    current=new(selected.Pack,selected.Source,selected.Reason,selected.HighWater,fingerprint);return current;
   }
  }
  // The existing native engine can only execute its finite legacy scope. Signed packs may
  // revise metadata/probes, but cannot widen or shrink that scope through this bridge.
  public static ScenarioPack RequireNativePack(ScenarioPack pack,string scenario,IEnumerable<string> targets) {
   pack=PackValidator.Validate(pack);var selected=pack.Scenarios.SingleOrDefault(s=>s.Id==scenario)??throw new FormatException("CATALOG_REQUESTED_SCENARIO_MISSING");
   var dependencies=pack.Dependencies.ToDictionary(d=>d.Id,StringComparer.Ordinal);var visited=new HashSet<string>(StringComparer.Ordinal);
   void Walk(string id){if(visited.Add(id))foreach(string child in dependencies[id].Requires)Walk(child);}
   foreach(string id in selected.Critical.Concat(selected.Optional))Walk(id);
   var actual=visited.Select(id=>dependencies[id].Namespace).Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal);
   var expected=targets.Select(PolicyNames.Normalize).Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal);
   if(!actual.SequenceEqual(expected,StringComparer.Ordinal))throw new FormatException("CATALOG_NATIVE_SCOPE_INCOMPATIBLE");
   if(selected.RequiredCapabilities.Any(c=>c!="dns.doh"))throw new FormatException("CATALOG_NATIVE_CAPABILITY_UNSUPPORTED");
   return pack;
  }
  public static string BindContext(string network) {
   if(!Configured)return network;
   if(string.IsNullOrWhiteSpace(network)||network.StartsWith("unknown:",StringComparison.Ordinal))return network;
   return EvidenceContext.Hash(network+"|catalog:"+Refresh().Fingerprint);
  }
 }
}
