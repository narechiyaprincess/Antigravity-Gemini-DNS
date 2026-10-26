using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 public static partial class OfflineTests {
  static void RunStage01TrustTests(){
   string original=ConnectionTrust.StorePath;
   string originalRun=AutostartHelper.KeyName;
   string isolated=@"Software\AgDnsTest\Stage01Trust-"+Guid.NewGuid().ToString("N");
   ConnectionTrust.StorePath=isolated;
   AutostartHelper.KeyName=isolated+@"\Run";
   try {
    var request=AssistedRequestFixture();
    string key=ConnectionTrust.RequestKey(request);
    using(var store=Registry.CurrentUser.CreateSubKey(isolated))store.SetValue("AcceptedOnce",1,RegistryValueKind.DWord);
    Check(!ConnectionTrust.Has(key),"trust: legacy blanket consent cannot authorize a scoped request");
    ConnectionTrust.Remember(key);
    Check(ConnectionTrust.Has(ConnectionTrust.RequestKey(request)),"trust: unchanged endpoint and service scope retain consent across reads");
    var custom=request with{Profile=AppSettings.CustomProfileId,Template="https://custom.example.test/dns-query"};
    string customKey=ConnectionTrust.RequestKey(custom);ConnectionTrust.Remember(customKey);
    Check(!ConnectionTrust.Has(ConnectionTrust.RequestKey(custom with{Template=custom.Template+"?changed"})),"trust: changed custom endpoint requires new consent");
    Check(!ConnectionTrust.Has(ConnectionTrust.RequestKey(request with{Servers=ImmutableArray.Create("8.8.8.8")})),"trust: changed bootstrap pins require new consent");
    Check(!ConnectionTrust.Has(ConnectionTrust.RequestKey(request with{Targets=ImmutableArray.Create("different.example")})),"trust: changed service scope requires new consent");
    var prepared=new AssistedFixture().Prepare(request,new AssistedFixture().State,CancellationToken.None).GetAwaiter().GetResult();
    string preparedKey=ConnectionTrust.PreparedKey(prepared);ConnectionTrust.Remember(preparedKey);
    var provider=prepared.Compile.Providers.Single();
    Check(!ConnectionTrust.Has(ConnectionTrust.PreparedKey(prepared with{Compile=prepared.Compile with{Providers=ImmutableArray.Create(provider with{EndpointIdentity=provider.EndpointIdentity+"?changed"})}})),"trust: a prepared endpoint cannot inherit another endpoint's grant");
    {
     var cat=ProfileCatalog.BuiltIn;var builtin=request with{Profile=cat.Default,Template=cat.DefaultProfile.Template,Servers=cat.DefaultProfile.Servers.ToImmutableArray(),Targets=Program.Targets.ToImmutableArray()};
     var other=builtin with{Profile="dns.comss.one",Template=cat.Find("dns.comss.one").Template,Servers=ImmutableArray.Create(cat.Find("dns.comss.one").Servers[0])};
     Check(ConnectionTrust.IsCatalogProvider(builtin)&&ConnectionTrust.IsCatalogProvider(other)&&!ConnectionTrust.IsCatalogProvider(custom)&&!ConnectionTrust.IsCatalogProvider(builtin with{Template="https://evil.example.test/dns-query"}),"trust: only an exact catalog profile (id + template + known scope) counts as a catalog provider");
     Check(!ConnectionTrust.Covers(builtin with{Servers=ImmutableArray.Create(cat.DefaultProfile.Servers.Last())})&&!ConnectionTrust.Covers(other),"trust: without the one-time consent no catalog provider is covered");
     ConnectionTrust.RememberCatalog();
     Check(ConnectionTrust.Covers(builtin)&&ConnectionTrust.Covers(other)&&ConnectionTrust.Covers(other with{Servers=ImmutableArray.Create(cat.Find("dns.comss.one").Servers.Last())}),"trust: one consent covers every catalog profile and the pins it finds (no dialog on each switch)");
     Check(!ConnectionTrust.Covers(custom with{Template="https://other.example.test/dns-query"}),"trust: the catalog consent never covers a user's own DoH endpoint");
    }
    ConnectionTrust.Revoke();
    Check(!ConnectionTrust.Has(key)&&!ConnectionTrust.Has(preparedKey)&&!ConnectionTrust.CatalogConsent,"trust: revoke clears request, prepared and catalog grants");
    using(var form=new MainForm(true)){
     int restores=0;
     form.PresentForTest(HealthyStatus);
     form.MutateHook=(action,repair)=>{if(action=="restore")restores++;form.PresentForTest("{\"State\":\"Not configured\",\"JournalPhase\":\"Restored\"}");return Task.CompletedTask;};
     ConnectionTrust.Remember(key);form.RevokeAssistedTrust().GetAwaiter().GetResult();
     Check(restores==1&&!ConnectionTrust.Has(key),"trust: revoke of an applied route requests journalled restore exactly once");
     form.RevokeAssistedTrust().GetAwaiter().GetResult();
     Check(restores==1,"trust: repeated revoke of an off bridge performs no system operation");
    }
    using(var form=new MainForm(true)){
     int restores=0;
     form.PresentForTest(HealthyStatus);
     form.MutateHook=(action,repair)=>{if(action=="restore")restores++;return Task.CompletedTask;};
     ConnectionTrust.Remember(key);form.RevokeAssistedTrust().GetAwaiter().GetResult();
     Check(restores==1&&!ConnectionTrust.Has(key),"trust: refused restore still revokes future provider admission");
     form.RevokeAssistedTrust().GetAwaiter().GetResult();
     Check(restores==2,"trust: unconfirmed route removal retains recovery action for retry");
    }
    Check(!AutostartHelper.SetEnabled(true,"relative.exe")&&!AutostartHelper.SetEnabled(true,@"C:\missing-stage01.exe"),"startup: invalid or missing executable cannot be registered");
   } finally {
    Registry.CurrentUser.DeleteSubKeyTree(isolated,false);ConnectionTrust.StorePath=original;AutostartHelper.KeyName=originalRun;
    using var root=Registry.CurrentUser.OpenSubKey(@"Software\AgDnsTest");
    if(root!=null&&root.SubKeyCount==0&&root.ValueCount==0){root.Dispose();Registry.CurrentUser.DeleteSubKey(@"Software\AgDnsTest",false);}
   }
  }
 }
}
