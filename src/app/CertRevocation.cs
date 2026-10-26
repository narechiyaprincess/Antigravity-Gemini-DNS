using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace AntigravityGeminiDns {
 public enum RevocationVerdict{Good,Revoked,Unknown}

 // Revocation of the certificates of a provider and of Google, the soft way: a certificate that its issuer has EXPLICITLY revoked is refused; when the answer cannot be had
 // (no OCSP/CRL server reachable, which is usual behind blocking) the check does not decide and the ordinary validation (chain, name, dates) stands alone.
 // One look per certificate per six hours (a result is remembered by thumbprint), with a short limit for the lookup, so a blocked network costs one delay, not one per probe.
 public static class CertRevocation {
  public static readonly TimeSpan Remember=TimeSpan.FromHours(6);
  public static readonly TimeSpan Limit=TimeSpan.FromMilliseconds(1200);
  internal static Func<X509Certificate2,RevocationVerdict> Checker=Live;      // test seam
  internal static Func<DateTime> Now=()=>DateTime.UtcNow;                     // test seam
  static readonly ConcurrentDictionary<string,(RevocationVerdict V,DateTime At)> seen=new ConcurrentDictionary<string,(RevocationVerdict,DateTime)>();
  internal static void Forget()=>seen.Clear();

  // Only an explicit revocation counts; "could not find out" (no server reachable, offline) is a separate answer that refuses nothing; other chain problems are not this check's business.
  public static RevocationVerdict Decide(System.Collections.Generic.IEnumerable<X509ChainStatusFlags> statuses) {
   var flags=statuses.ToList();
   if(flags.Any(f=>(f&X509ChainStatusFlags.Revoked)!=0))return RevocationVerdict.Revoked;
   if(flags.Any(f=>(f&(X509ChainStatusFlags.RevocationStatusUnknown|X509ChainStatusFlags.OfflineRevocation))!=0))return RevocationVerdict.Unknown;
   return RevocationVerdict.Good;
  }

  internal static RevocationVerdict Live(X509Certificate2 cert) {
   try{
    using(var chain=new X509Chain()){
     chain.ChainPolicy.RevocationMode=X509RevocationMode.Online;
     chain.ChainPolicy.RevocationFlag=X509RevocationFlag.ExcludeRoot;
     chain.ChainPolicy.UrlRetrievalTimeout=Limit;
     chain.ChainPolicy.VerificationFlags=X509VerificationFlags.NoFlag;
     chain.Build(cert);   // the result of the build is not used: only the status of the revocation lookup is
     return Decide(chain.ChainStatus.Select(s=>s.Status));
    }
   }catch(Exception){return RevocationVerdict.Unknown;}
  }

  public static RevocationVerdict Check(System.Security.Cryptography.X509Certificates.X509Certificate cert) {
   if(cert==null)return RevocationVerdict.Unknown;
   X509Certificate2 c2;try{c2=new X509Certificate2(cert);}catch(Exception){return RevocationVerdict.Unknown;}
   using(c2){
    string key=c2.Thumbprint??"";
    if(key.Length>0&&seen.TryGetValue(key,out var hit)&&Now()-hit.At<Remember)return hit.V;
    var v=Checker(c2);
    if(key.Length>0)seen[key]=(v,Now());
    return v;
   }
  }
  public static bool IsRevoked(System.Security.Cryptography.X509Certificates.X509Certificate cert)=>Check(cert)==RevocationVerdict.Revoked;

  // For HTTP handlers and SslStream: the policy errors must be none (as before) and the certificate must not be explicitly revoked.
  public static bool Callback(object sender,System.Security.Cryptography.X509Certificates.X509Certificate cert,X509Chain chain,SslPolicyErrors errors)=>errors==SslPolicyErrors.None&&!IsRevoked(cert);
 }
}
