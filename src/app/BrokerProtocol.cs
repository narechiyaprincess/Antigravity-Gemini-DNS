using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 // This wire format intentionally has no extensible command, path, endpoint or script field.
 internal sealed record BrokerRequest(int Version,string RequestId,string Nonce,long DeadlineUtcMs,string Operation,string Profile,string Scope,string LeaseId,long LeaseUntilMs);
 internal sealed record BrokerIdentity(string Sid,int Session,bool Elevated) { public string Owner=>Sid+":"+Session; }
 internal sealed record BrokerReply(int Version,string RequestId,string Code,int ExitCode,string Output);
 internal static class BrokerProtocol {
  internal const int MaxFrame=16384;
  internal const int MaxReplyFrame=131072;
  static readonly UTF8Encoding Utf8=new(false,true);
  internal static JsonDocument Object(byte[] bytes,params string[] fields)=>ObjectBounded(bytes,MaxFrame,fields);
  internal static JsonDocument ReplyObject(byte[] bytes)=>ObjectBounded(bytes,MaxReplyFrame,new[]{"Version","RequestId","Code","ExitCode","Output"});
  static JsonDocument ObjectBounded(byte[] bytes,int max, string[] fields) {
   if(bytes.Length==0||bytes.Length>max)throw new InvalidDataException("FRAME_SIZE");
   _=Utf8.GetString(bytes);
   var d=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=4});
   try{if(d.RootElement.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("OBJECT_REQUIRED");
    var seen=new HashSet<string>(StringComparer.Ordinal);
    foreach(var p in d.RootElement.EnumerateObject())if(!fields.Contains(p.Name,StringComparer.Ordinal)||!seen.Add(p.Name))throw new InvalidDataException("UNKNOWN_OR_DUPLICATE_FIELD");
    if(seen.Count!=fields.Length)throw new InvalidDataException("MISSING_FIELD");return d;
   }catch{d.Dispose();throw;}
  }
  internal static BrokerRequest Parse(byte[] data,string nonce,long now) {
   using var d=Object(data,"Version","RequestId","Nonce","DeadlineUtcMs","Operation","Profile","Scope","LeaseId","LeaseUntilMs");var e=d.RootElement;
   var r=new BrokerRequest(e.GetProperty("Version").GetInt32(),e.GetProperty("RequestId").GetString(),e.GetProperty("Nonce").GetString(),e.GetProperty("DeadlineUtcMs").GetInt64(),e.GetProperty("Operation").GetString(),e.GetProperty("Profile").GetString(),e.GetProperty("Scope").GetString(),e.GetProperty("LeaseId").GetString(),e.GetProperty("LeaseUntilMs").GetInt64());
   Validate(r,nonce,now);return r;
  }
  internal static bool GuidN(string s)=>s!=null&&s.Length==32&&Guid.TryParseExact(s,"N",out var g)&&g.ToString("N")==s;
  internal static void Validate(BrokerRequest r,string nonce,long now) {
   if(r.Version!=1)throw new InvalidDataException("PROTOCOL_VERSION");
   if(!GuidN(r.RequestId)||nonce==null||r.Nonce!=nonce||nonce.Length!=64)throw new InvalidDataException("REQUEST_OR_NONCE");
   if(r.DeadlineUtcMs<=now||r.DeadlineUtcMs>now+30000)throw new InvalidDataException("DEADLINE");
   if(!new[]{"status","acquire","release","recover"}.Contains(r.Operation))throw new InvalidDataException("OPERATION");
   if(r.Operation=="acquire"){
    if(!ProfileCatalog.BuiltIn.Profiles.Any(p=>p.Id==r.Profile)||!new[]{"core","gemini","google"}.Contains(r.Scope)||!GuidN(r.LeaseId)||r.LeaseUntilMs<=now||r.LeaseUntilMs>now+86400000)throw new InvalidDataException("LEASE_CONFIGURATION");
   }else if(r.Profile!=""||r.Scope!=""||r.LeaseUntilMs!=0||(r.Operation=="release"?!GuidN(r.LeaseId):r.LeaseId!=""))throw new InvalidDataException("UNEXPECTED_ARGUMENT");
  }
  internal static void Authorize(BrokerIdentity actual,string sid,int session,bool mutation) {
   if(actual.Sid!=sid||actual.Session!=session||session<0||actual.Sid=="S-1-5-7")throw new UnauthorizedAccessException("CLIENT_IDENTITY");
   if(mutation&&!actual.Elevated)throw new UnauthorizedAccessException("ELEVATED_ADMIN_REQUIRED");
  }
  // Transport nonce/deadline are validated on every connection, but excluded from the durable semantic digest.
  // Thus reconnecting with a fresh challenge can retrieve an identical operation receipt after a lost reply.
  internal static string Digest(BrokerRequest r,BrokerIdentity who)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{r.Version,r.RequestId,r.Operation,r.Profile,r.Scope,r.LeaseId,r.LeaseUntilMs,who.Sid,who.Session})));
  internal static byte[] Serialize<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value);
  internal static Task<byte[]> Read(Stream s,CancellationToken token)=>ReadBounded(s,MaxFrame,token);
  internal static Task<byte[]> ReadReply(Stream s,CancellationToken token)=>ReadBounded(s,MaxReplyFrame,token);
  static async Task<byte[]> ReadBounded(Stream s,int max,CancellationToken token) {
   byte[] h=new byte[4];await s.ReadExactlyAsync(h,token);int n=BinaryPrimitives.ReadInt32LittleEndian(h);
   if(n<=0||n>max)throw new InvalidDataException("FRAME_SIZE");byte[] b=new byte[n];await s.ReadExactlyAsync(b,token);return b;
  }
  internal static Task Write<T>(Stream s,T value,CancellationToken token)=>WriteBounded(s,Serialize(value),MaxFrame,token);
  internal static void EnsureReplySize(BrokerReply reply){if(Serialize(reply).Length>MaxReplyFrame)throw new InvalidDataException("RESPONSE_SIZE_UNCERTAIN_RECOVERY_REQUIRED");}
  internal static Task WriteReply(Stream s,BrokerReply value,CancellationToken token)=>WriteBounded(s,Serialize(value),MaxReplyFrame,token);
  static async Task WriteBounded(Stream s,byte[] b,int max,CancellationToken token) {
   if(b.Length>max)throw new InvalidDataException("RESPONSE_SIZE_UNCERTAIN_RECOVERY_REQUIRED");byte[] h=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(h,b.Length);await s.WriteAsync(h,token);await s.WriteAsync(b,token);await s.FlushAsync(token);
  }
 }
 internal sealed record BrokerReceipt(int Version,string Digest,string State,BrokerReply Reply);
 internal sealed class BrokerReceipts {
  readonly string root;
  internal BrokerReceipts(string root){this.root=root;}
  internal BrokerReply Execute(BrokerRequest r,BrokerIdentity who,Func<BrokerReply> run) {
   string digest=BrokerProtocol.Digest(r,who),path=Path.Combine(root,r.RequestId+".json");
   if(File.Exists(path)){
    var old=JsonSerializer.Deserialize<BrokerReceipt>(File.ReadAllBytes(path));
    if(old==null||old.Version!=1||old.Digest!=digest)throw new InvalidDataException("REPLAY_PAYLOAD_MISMATCH");
    return old.State=="Complete"&&old.Reply!=null?old.Reply:new(1,r.RequestId,"UNCERTAIN_RECOVERY_REQUIRED",1460,"");
   }
   // Bounded receipt storage: refuse new dispatch rather than prune crash/retry evidence.
   if(Directory.EnumerateFiles(root,"*.json").Take(4096).Count()>=4096)throw new IOException("RECEIPT_CAPACITY_RECOVERY_REQUIRED");
   Save(path,new(1,digest,"InProgress",null));
   BrokerReply reply=run();Save(path,new(1,digest,"Complete",reply));return reply;
  }
  static void Save(string path,BrokerReceipt receipt) {
   string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
   using(var f=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){byte[] b=BrokerProtocol.Serialize(receipt);f.Write(b);f.Flush(true);}
   File.Move(tmp,path,true);
  }
 }
}
