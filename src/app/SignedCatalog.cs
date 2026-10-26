using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AntigravityGeminiDns {
 // Data provenance only: neither this document nor the updater can contain a TrustGrant.
 public sealed record CatalogManifest(int Schema,string Channel,long Sequence,string KeyId,string Algorithm,
  DateTimeOffset IssuedUtc,DateTimeOffset ExpiresUtc,int MinimumClient,int MaximumClient,int Compiler,string PayloadSha256);
 public sealed record SignedCatalogDocument(CatalogManifest Manifest,string Payload,string Signature);
 public sealed record CatalogRoot(string Id,string PublicKey,DateTimeOffset NotBeforeUtc,DateTimeOffset NotAfterUtc,bool Revoked);
 public sealed record CatalogSnapshot(int Schema,long HighWater,DateTimeOffset LastAcceptedUtc,SignedCatalogDocument Current,SignedCatalogDocument LastKnownGood,bool PendingHealth=false);
 public sealed record CatalogSelection(ScenarioPack Pack,string Source,string Reason,long HighWater);

 public sealed class CatalogVerifier {
  public const int MaximumDocumentBytes=98304;
  readonly ImmutableDictionary<string,CatalogRoot> roots;
  readonly string channel;readonly int client;
  public CatalogVerifier(IEnumerable<CatalogRoot> roots,string channel,int client=1) {
   PolicyNames.Id(channel);if(client<1)throw new ArgumentException("Invalid client version");
   this.roots=roots.ToImmutableDictionary(r=>r.Id,StringComparer.Ordinal);this.channel=channel;this.client=client;
   foreach(var r in this.roots.Values){PolicyNames.Id(r.Id);if(r.NotAfterUtc<=r.NotBeforeUtc)throw new ArgumentException("Invalid root lifetime");}
  }
  public static byte[] SignedBytes(CatalogManifest manifest)=>Encoding.UTF8.GetBytes("AGDNS-SCENARIO-CATALOG-V1\n"+PolicyJson.Write(manifest));
  public static string Hash(string payload)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
  public static SignedCatalogDocument Parse(byte[] bytes) {
   if(bytes==null||bytes.Length>MaximumDocumentBytes)throw new FormatException("CATALOG_SIZE");
   string json;try{json=new UTF8Encoding(false,true).GetString(bytes);}catch(DecoderFallbackException){throw new FormatException("CATALOG_UTF8");}
   return PolicyJson.Read<SignedCatalogDocument>(json);
  }
  public ScenarioPack Verify(SignedCatalogDocument document,DateTimeOffset now) {
   if(document==null||document.Manifest==null||document.Payload==null||Encoding.UTF8.GetByteCount(PolicyJson.Write(document))>MaximumDocumentBytes)throw new FormatException("CATALOG_SIZE_OR_EMPTY");
   var m=document.Manifest;
   if(m.Schema!=1||m.Algorithm!="ECDSA-P256-SHA256-P1363"||m.Channel!=channel)throw new FormatException("CATALOG_SCHEMA_ALGORITHM_CHANNEL");
   if(m.Sequence<1||m.MinimumClient<1||m.MaximumClient<m.MinimumClient||client<m.MinimumClient||client>m.MaximumClient||m.Compiler!=1)throw new FormatException("CATALOG_COMPATIBILITY");
   if(m.IssuedUtc>now||m.ExpiresUtc<=now||m.ExpiresUtc<=m.IssuedUtc||m.ExpiresUtc-m.IssuedUtc>TimeSpan.FromDays(90))throw new FormatException("CATALOG_EXPIRY_OR_CLOCK");
   if(!roots.TryGetValue(m.KeyId??"",out var root)||root.Revoked)throw new FormatException("CATALOG_UNKNOWN_OR_REVOKED_KEY");
   if(m.IssuedUtc<root.NotBeforeUtc||m.ExpiresUtc>root.NotAfterUtc||now<root.NotBeforeUtc||now>=root.NotAfterUtc)throw new FormatException("CATALOG_KEY_LIFETIME");
   if(m.PayloadSha256!=Hash(document.Payload))throw new FormatException("CATALOG_HASH");
   try {
    using var key=ECDsa.Create();key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(root.PublicKey),out int used);
    if(used!=Convert.FromBase64String(root.PublicKey).Length||key.ExportParameters(false).Curve.Oid.Value!="1.2.840.10045.3.1.7")throw new FormatException("CATALOG_KEY_CURVE");
    byte[] signature=Convert.FromBase64String(document.Signature??"");
    if(signature.Length!=64||!key.VerifyData(SignedBytes(m),signature,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))throw new FormatException("CATALOG_SIGNATURE");
   }catch(CryptographicException){throw new FormatException("CATALOG_SIGNATURE_OR_KEY");}
   // The signature covers the exact payload hash. Canonical representation rejects ambiguous encodings.
   var pack=PackValidator.Validate(PolicyJson.Read<ScenarioPack>(document.Payload));
   if(PolicyJson.Write(pack)!=document.Payload)throw new FormatException("CATALOG_NONCANONICAL_PAYLOAD");
   return pack;
  }
 }

 // A single same-directory replacement commits current, LKG, clock floor and anti-rollback together.
 // The store owns catalog data only. It never reads/writes a Windows policy or recovery journal.
 public sealed class CatalogStore {
  readonly string directory,statePath;readonly CatalogVerifier verifier;readonly ScenarioPack seed;
  public CatalogStore(string directory,CatalogVerifier verifier,ScenarioPack seed) {
   this.directory=Path.GetFullPath(directory);statePath=Path.Combine(this.directory,"catalog-state.json");
   this.verifier=verifier??throw new ArgumentNullException(nameof(verifier));this.seed=PackValidator.Validate(seed);
  }
  public string StatePath=>statePath;
  static byte[] ReadBounded(string path,int maximum) {
   using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
   if(f.Length>maximum)throw new FormatException("CATALOG_SIZE");byte[] bytes=new byte[(int)f.Length];f.ReadExactly(bytes);return bytes;
  }
  public static SignedCatalogDocument ReadLocal(string path)=>CatalogVerifier.Parse(ReadBounded(path,CatalogVerifier.MaximumDocumentBytes));
  CatalogSnapshot Load() {
   if(!File.Exists(statePath))return new CatalogSnapshot(1,0,DateTimeOffset.MinValue,null,null);
   var s=PolicyJson.Read<CatalogSnapshot>(new UTF8Encoding(false,true).GetString(ReadBounded(statePath,2*CatalogVerifier.MaximumDocumentBytes+4096)));
   if(s.Schema!=1||s.HighWater<0||s.Current!=null&&(s.Current.Manifest==null||s.Current.Manifest.Sequence>s.HighWater)||s.LastKnownGood!=null&&(s.LastKnownGood.Manifest==null||s.LastKnownGood.Manifest.Sequence>s.HighWater))throw new FormatException("CATALOG_STATE_INVALID");
   return s;
  }
  static bool Invalid(Exception e)=>e is FormatException||e is IOException||e is UnauthorizedAccessException||e is DecoderFallbackException||e is ArgumentException;
  public CatalogSelection Select(DateTimeOffset now) {
   CatalogSnapshot s;try{s=Load();}catch(Exception e)when(Invalid(e)){return new(seed,"SEED","STATE_UNREADABLE_UPDATES_BLOCKED",0);}
   if(now<s.LastAcceptedUtc)return new(seed,"SEED","CLOCK_BEFORE_ACCEPTED_TIME",s.HighWater);
   foreach(var candidate in new[]{(s.PendingHealth?null:s.Current,"CURRENT"),(s.LastKnownGood,"LKG")}) {
    if(candidate.Item1==null)continue;
    try{return new(verifier.Verify(candidate.Item1,now),candidate.Item2,"SIGNED_DATA_ONLY_NO_TRUST_GRANT",s.HighWater);}catch(Exception e)when(Invalid(e)){}
   }
   return new(seed,"SEED","NO_FRESH_TRUSTED_COMPATIBLE_CATALOG",s.HighWater);
  }
  public CatalogSelection Install(SignedCatalogDocument document,DateTimeOffset now,Func<ScenarioPack,bool> dryRun,Func<ScenarioPack,bool> health,Action<string> checkpoint=null) {
   if(dryRun==null||health==null)throw new ArgumentNullException("Explicit policy dry-run and data health checks required");
   Directory.CreateDirectory(directory);
   using var exclusive=new FileStream(Path.Combine(directory,"catalog.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
   var before=Load();if(now<before.LastAcceptedUtc)throw new FormatException("CATALOG_CLOCK_ROLLBACK");
   var pack=verifier.Verify(document,now);
   if(document.Manifest.Sequence<=before.HighWater)throw new FormatException("CATALOG_ROLLBACK");
   if(!dryRun(pack))throw new FormatException("CATALOG_DRY_RUN_REJECTED");
   checkpoint?.Invoke("validated");
   SignedCatalogDocument previous=null;
   foreach(var old in new[]{before.PendingHealth?null:before.Current,before.LastKnownGood})if(old!=null)try{verifier.Verify(old,now);previous=old;break;}catch(Exception e)when(Invalid(e)){}
   var next=new CatalogSnapshot(1,document.Manifest.Sequence,now,document,previous,true);
   Save(next,checkpoint);
   checkpoint?.Invoke("activated-pending-health");
   // Post-activation health is data-only. Failed health restores the former generation while
   // retaining the newly observed sequence, so rejected data cannot be replayed as an update.
   try {if(!health(pack))throw new FormatException("CATALOG_HEALTH_REJECTED");}
   catch {Save(new CatalogSnapshot(1,next.HighWater,now,previous,previous),null);throw;}
   checkpoint?.Invoke("healthy-before-finalize");
   Save(next with{PendingHealth=false,LastKnownGood=previous??document},null);
   return new(pack,"CURRENT","SIGNED_DATA_ONLY_NO_TRUST_GRANT",next.HighWater);
  }
  public CatalogSelection Rollback(DateTimeOffset now) {
   Directory.CreateDirectory(directory);using var exclusive=new FileStream(Path.Combine(directory,"catalog.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
   var s=Load();if(now<s.LastAcceptedUtc||s.LastKnownGood==null)throw new FormatException("CATALOG_NO_ROLLBACK");
   var pack=verifier.Verify(s.LastKnownGood,now);
   Save(s with{Current=s.LastKnownGood,LastAcceptedUtc=now,PendingHealth=false},null);
   return new(pack,"LKG","KNOWN_GOOD_ROLLBACK_HIGH_WATER_RETAINED",s.HighWater);
  }
  void Save(CatalogSnapshot snapshot,Action<string> checkpoint) {
   byte[] bytes=Encoding.UTF8.GetBytes(PolicyJson.Write(snapshot));string temp=Path.Combine(directory,"catalog-"+Guid.NewGuid().ToString("N")+".pending");
   using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(bytes);f.Flush(true);}
   // A killed process may leave .pending data; startup never consumes it. Keep failed preimages for diagnosis.
   checkpoint?.Invoke("flushed");
   if(File.Exists(statePath))File.Replace(temp,statePath,null);else File.Move(temp,statePath);
  }
 }
}
