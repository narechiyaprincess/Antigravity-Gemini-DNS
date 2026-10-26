using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AntigravityGeminiDns {
 // Consent belongs to the effective provider, its addresses, capabilities and exact service scope.
 // Old name-only Trust_* values intentionally do not authorize anything here.
 public static class ConnectionTrust {
  const string Path=@"Software\"+AppIdentity.StorageName+@"\ConnectionTrustV2";
  internal static string StorePath=Path;
  static string Hash(object value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
  static string[] Sorted(System.Collections.Generic.IEnumerable<string> values)=>values.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
  static LegacyScenarioMigration Expected(AssistedRequest r)=>LiveAssistedBackend.PrepareProviderIdentity(r,r.Servers).Migration;
  static object ProviderIdentity(ProviderRecord p)=>new{p.Id,p.OperatorIdentity,p.EndpointIdentity,Bootstrap=Sorted(p.Bootstrap),Capabilities=Sorted(p.ClaimedCapabilities),p.Revoked,p.Provenance};
  public static string RequestKey(AssistedRequest r) {
   var expected=Expected(r);var provider=expected.Providers.Single(p=>p.Id==expected.SelectedProvider);
   var pack=RuntimeCatalog.Select(expected.Pack,DateTimeOffset.UtcNow).Pack;
   return Hash(new{r.Profile,r.Template,RequestedAddresses=Sorted(r.Servers),Provider=ProviderIdentity(provider),Pack=PolicyJson.Write(pack),expected.SelectedScenario,Targets=Sorted(r.Targets),r.Gemini,r.Google});
  }
  public static string PreparedKey(AssistedPrepared p) {
   var provider=p.Compile.Providers.Single();return Hash(new{Provider=ProviderIdentity(provider),Pack=PolicyJson.Write(p.Compile.Pack),
    Bindings=p.Compile.Bindings.Select(b=>new{b.ScenarioId,b.Route,Scopes=Sorted(b.AuthorizedScopes)}).ToArray()});
  }
  // Only an exact endpoint/scope grant authorizes a connection. Legacy AcceptedOnce
  // records are retained until revoke but never authorize a new provider or scope.
  // One consent covers every provider of the program's own catalog, with any pins it finds and any service scope it supports: target TLS is
  // validated end to end, a pin is used only after TLS on the provider name and DoH answers, and the user is asked once instead of on every switch.
  // A user's own DoH endpoint keeps its exact endpoint/scope grant. Revoke clears both.
  const string CatalogConsentName="CatalogProvidersV1";
  public static bool IsCatalogProvider(AssistedRequest r){var p=ProfileCatalog.BuiltIn.Find(r.Profile??"");return p!=null&&p.Id==r.Profile&&string.Equals(p.Template,r.Template,StringComparison.Ordinal)&&!r.Targets.IsDefault&&Program.IsKnownScope(r.Targets);}
  public static bool CatalogConsent=>Has(CatalogConsentName);
  public static void RememberCatalog()=>Remember(CatalogConsentName);
  public static bool Covers(AssistedRequest r)=>Has(RequestKey(r))||IsCatalogProvider(r)&&CatalogConsent;
  public static bool Has(string key){try{using var store=Registry.CurrentUser.OpenSubKey(StorePath);return store?.GetValue(key) is int value&&value==1;}catch{return false;}}
  public static void Remember(string key){using var store=Registry.CurrentUser.CreateSubKey(StorePath);store.SetValue(key,1,RegistryValueKind.DWord);}
  public static void Revoke(){Registry.CurrentUser.DeleteSubKeyTree(StorePath,false);}
  public static bool CoveredPins(AssistedPrepared p) {
   var actual=p.Compile.Providers.Single();var expected=Expected(p.Request);var provider=expected.Providers.Single(x=>x.Id==expected.SelectedProvider);
   var pack=RuntimeCatalog.Select(expected.Pack,DateTimeOffset.UtcNow).Pack;
   return actual.Bootstrap.All(ip=>p.Request.Servers.Contains(ip,StringComparer.Ordinal))&&
    Hash(ProviderIdentity(actual with{Bootstrap=provider.Bootstrap}))==Hash(ProviderIdentity(provider))&&
    PolicyJson.Write(pack)==PolicyJson.Write(p.Compile.Pack)&&p.Compile.Bindings.All(b=>b.ScenarioId==expected.SelectedScenario&&b.Route==RouteKind.NativeDoh&&
     Sorted(b.AuthorizedScopes).SequenceEqual(Sorted(p.Request.Targets)));
  }
 }
}
