using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 public static partial class OfflineTests {
  sealed class ScenarioFixture:IScenarioTransport {
   public string Answer="8.8.8.8",V6=null;public ScenarioExchange Exchange=new(true,true,200,null);public int Heads;public bool Timeout;
   public Task<string[]> Resolve(string h,int type,CancellationToken t)=>Timeout?Task.FromCanceled<string[]>(new CancellationToken(true)):Task.FromResult(type==1?new[]{Answer}:V6==null?Array.Empty<string>():new[]{V6});
   public Task<byte[]> Modern(string h,int type,CancellationToken t)=>Task.FromResult(ModernPacket(h,type,new byte[]{0,1,0}));
   public Task<ScenarioExchange> Head(string h,IPAddress ip,CancellationToken t){Heads++;return Task.FromResult(Exchange);}
  }
  static byte[] ModernPacket(string host,int type,byte[] rdata) {
   var b=ScenarioDns.Query(host,type).ToList();b[2]=0x81;b[3]=0x80;b[7]=1;
   b.AddRange(new byte[]{0xc0,0x0c,0,(byte)type,0,1,0,0,0,60,(byte)(rdata.Length>>8),(byte)rdata.Length});b.AddRange(rdata);return b.ToArray();
  }
  static byte[] Svc(params (int Key,byte[] Value)[] values) {
   var b=new List<byte>{0,1,0};foreach(var v in values){b.Add((byte)(v.Key>>8));b.Add((byte)v.Key);b.Add((byte)(v.Value.Length>>8));b.Add((byte)v.Value.Length);b.AddRange(v.Value);}return b.ToArray();
  }
  static void RunScenarioContracts() {
   foreach(var ip in new[]{"0.1.2.3","10.0.0.1","100.64.0.1","100.127.255.255","127.0.0.1","169.254.169.254","172.16.0.1","172.31.255.255","192.0.0.1","192.0.2.1","192.88.99.1","192.168.1.1","198.18.0.1","198.19.0.1","198.51.100.1","203.0.113.1","224.0.0.1","255.255.255.255","::","::1","::ffff:8.8.8.8","64:ff9b::808:808","2001::1","2001:1ff::1","2001:db8::1","2002::1","3fff::1","3fff:fff::1","fc00::1","fe80::1","ff02::1"})Check(!ScenarioDestination.Public(IPAddress.Parse(ip)),"scenario SSRF blocked "+ip);
   foreach(var ip in new[]{"1.1.1.1","8.8.8.8","100.63.255.255","100.128.0.0","172.15.0.1","172.32.0.1","192.0.3.1","198.51.99.1","198.51.101.1","223.255.255.255","2001:200::1","2001:4860:4860::8888","2606:4700:4700::1111","3ffe::1","3fff:1000::1"})Check(ScenarioDestination.Public(IPAddress.Parse(ip)),"scenario public boundary "+ip);
   foreach(var bad in new[]{"localhost","127.0.0.1","gemini.google.com.evil.test","GEMINI.GOOGLE.COM","https://gemini.google.com/","gemini.google.com\r\nX: evil"})Reject(()=>ScenarioProbes.RequireHost(bad),"scenario exact host "+bad);
   foreach(var flag in new[]{"--scenario-contract-test","--scenario-probe-system"}) {
    Program.ValidateCliArgs(new[]{flag});Check(true,"scenario standalone");
    Reject(()=>Program.ValidateCliArgs(new[]{flag,"--enable"}),"scenario mixed mutation");
    Reject(()=>Program.ValidateCliArgs(new[]{flag.ToUpperInvariant(),"--restore"}),"scenario mixed case mutation");
   }
   Reject(()=>new LiveScenarioTransport("https://127.0.0.1/"),"scenario provider allowlist");
   using(var request=LiveScenarioTransport.DohRequest(new Uri("https://example.com/dns-query"),new byte[12]))Check(request.Version==HttpVersion.Version20&&request.VersionPolicy==System.Net.Http.HttpVersionPolicy.RequestVersionExact&&request.Content.Headers.ContentType.MediaType=="application/dns-message","scenario existing HTTP2 DoH contract preserved");
   var ctx=new ScenarioContext("client-sha","AG-STREAM","network","pack","provider","policy");var now=DateTimeOffset.UtcNow;
   var fixture=new ScenarioFixture();var receipt=ScenarioProbes.Run(fixture,ctx,()=>ctx,false).GetAwaiter().GetResult();
   Check(fixture.Heads==4&&receipt.Readiness=="UNKNOWN"&&receipt.Signals.Count(x=>x.Layer=="modern-rr"&&x.Outcome=="UNKNOWN")==8,"scenario network success never fills app/browser");
   var dual=new ScenarioFixture{V6="2606:4700:4700::1111"};ScenarioProbes.Run(dual,ctx,()=>ctx,true).GetAwaiter().GetResult();Check(dual.Heads==8,"scenario dual stack measured independently");
   var badV6=new ScenarioFixture{V6="fe80::1"};var broken=ScenarioProbes.Run(badV6,ctx,()=>ctx,false).GetAwaiter().GetResult();Check(badV6.Heads==4&&broken.Signals.Any(x=>x.Family=="IPv6"&&x.Outcome=="FAILED"),"scenario broken IPv6 cannot erase IPv4 evidence");
   Check(ScenarioProbes.Fresh(receipt,ctx,now.AddSeconds(1)),"scenario fresh context");
   Check(!ScenarioProbes.Fresh(receipt,ctx with{Provider="edited-pins"},now.AddSeconds(1)),"scenario provider revision invalidates");
   Check(!ScenarioProbes.Fresh(receipt,ctx,now.AddMinutes(6)),"scenario expiry");
   Check(!ScenarioProbes.Fresh(receipt,ctx with{Network=""},now.AddSeconds(1)),"scenario missing context unknown");
   foreach(var changed in new[]{ctx with{Network="other"},ctx with{Policy="other"},ctx with{Client="other"},ctx with{Scenario="other"},ctx with{Pack="other"}})Check(!ScenarioProbes.Fresh(receipt,changed,now.AddSeconds(1)),"scenario dimension invalidates");
   var stale=ScenarioProbes.Run(fixture,ctx,()=>ctx with{Network="changed"},false).GetAwaiter().GetResult();Check(!stale.ContextUnchanged&&!ScenarioProbes.Fresh(stale,ctx,now.AddSeconds(1)),"scenario context changed during probe");
   foreach(var poison in new[]{"127.0.0.1","169.254.169.254","::1","not-an-ip"}) {
    var f=new ScenarioFixture{Answer=poison};var r=ScenarioProbes.Run(f,ctx,()=>ctx,false).GetAwaiter().GetResult();Check(f.Heads==0&&r.Signals.Any(x=>x.Detail=="NONPUBLIC_OR_INVALID_ANSWER_POSSIBLE_POISON"),"scenario poison never connected");
   }
   var timeout=ScenarioProbes.Run(new ScenarioFixture{Timeout=true},ctx,()=>ctx,false).GetAwaiter().GetResult();Check(timeout.Signals.Any(x=>x.Detail=="TIMEOUT_OR_CANCELLED"),"scenario DNS timeout");
   foreach(var x in new[]{new ScenarioExchange(false,false,0,"TCP_REFUSED"),new ScenarioExchange(true,false,0,"TLS_VALIDATION_OR_HANDSHAKE_FAILURE")}) {
    var r=ScenarioProbes.Run(new ScenarioFixture{Exchange=x},ctx,()=>ctx,false).GetAwaiter().GetResult();Check(r.Signals.Any(s=>s.Detail==x.Failure&&s.Outcome=="FAILED"),"scenario TCP/TLS failure stays distinct");
   }
   foreach(var pair in new[]{(200,"RESOURCE_REACHABLE_OPERATION_UNKNOWN"),(302,"REDIRECT_NOT_FOLLOWED"),(401,"RESOURCE_AUTH_OR_PERMISSION_REFUSAL"),(403,"RESOURCE_AUTH_OR_PERMISSION_REFUSAL"),(404,"RESOURCE_OR_METHOD_REFUSAL"),(405,"RESOURCE_OR_METHOD_REFUSAL"),(429,"RESOURCE_RATE_LIMITED"),(503,"RESOURCE_SERVER_REFUSAL")}) {
    using var stream=new MemoryStream(Encoding.ASCII.GetBytes("HTTP/1.1 "+pair.Item1+" Status\r\nContent-Length: 100000000\r\nLocation: http://127.0.0.1/\r\n\r\n"));
    int status=ScenarioProbes.ReadHead(stream,default).GetAwaiter().GetResult();Check(status==pair.Item1&&ScenarioProbes.HttpMeaning(status)==pair.Item2,"scenario status meaning "+status);
   }
   Reject(()=>ScenarioProbes.ReadHead(new MemoryStream(new byte[ScenarioProbes.HeaderBytes+1]),default).GetAwaiter().GetResult(),"scenario header limit");
   Check(ScenarioProbes.ReadHead(new MemoryStream(Encoding.ASCII.GetBytes("HTTP/1.1 103 Early Hints\r\n\r\nHTTP/1.1 200 OK\r\n\r\n")),default).GetAwaiter().GetResult()==200,"scenario bounded interim then final status");
   Reject(()=>ScenarioProbes.ReadHead(new MemoryStream(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("HTTP/1.1 103 Early Hints\r\n\r\n",9)))),default).GetAwaiter().GetResult(),"scenario interim limit");
   Reject(()=>ScenarioProbes.ReadHead(new MemoryStream(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\n\r\n")),default).GetAwaiter().GetResult(),"scenario no upgrade");
   Reject(()=>ScenarioProbes.ReadHead(new MemoryStream(Encoding.ASCII.GetBytes("HTTP/1.1 200evil\r\n\r\n")),default).GetAwaiter().GetResult(),"scenario malformed status");
   Reject(()=>ScenarioProbes.ReadBody(new MemoryStream(new byte[65536]),65535,default).GetAwaiter().GetResult(),"scenario body limit");
   Check(ScenarioProbes.ReadBody(new MemoryStream(new byte[65535]),65535,default).GetAwaiter().GetResult().Length==65535,"scenario exact body boundary");
   var app=new UserScenarioEvidence(ctx,now.AddSeconds(-1),now.AddMinutes(5),"VOLUNTARY_USER_ATTESTATION",ObservationOutcome.Passed,ObservationOutcome.Passed,ObservationOutcome.Passed,null);
   Check(ScenarioProbes.AssessApplication(app,ctx,now)=="PASSED_USER_ATTESTED_SELECTED_SCENARIO","scenario voluntary selected app proof");
   foreach(var altered in new[]{app with{Stream=ObservationOutcome.Unknown},app with{Stream=ObservationOutcome.Skipped},app with{Source="HTTP_200"},app with{Context=ctx with{Client="browser"}},app with{ExpiresUtc=now},app with{ObservedUtc=now.AddSeconds(1)}})Check(ScenarioProbes.AssessApplication(altered,ctx,now)=="UNKNOWN","scenario missing/stale/wrong app proof");
   Check(ScenarioProbes.AssessApplication(app with{Request=ObservationOutcome.Failed,Failure="AUTH_REFUSED"},ctx,now)=="FAILED:AUTH_REFUSED","scenario account refusal");
   Check(ScenarioProbes.AssessApplication(app with{Stream=ObservationOutcome.Failed,Failure="STREAM_INTERRUPTED"},ctx,now)=="FAILED:STREAM_INTERRUPTED","scenario stream failure");
   var host=ScenarioProbes.Hosts[0];var simple=ScenarioDns.Parse(ModernPacket(host,65,new byte[]{0,1,0}),host,65);
   Check(simple.Records.Single().Meaning=="SERVICE_OWNER_NAME","scenario service root semantics");
   Check(ScenarioDns.ParseRecord(host,new byte[]{0,0,0}).Meaning=="ALIAS_SERVICE_UNAVAILABLE","scenario alias root semantics");
   var svc=ScenarioDns.ParseRecord(host,Svc((0,new byte[]{0,1,0,3}),(1,new byte[]{2,(byte)'h',(byte)'2'}),(3,new byte[]{1,187}),(4,new byte[]{8,8,8,8}),(5,new byte[]{0,0}),(6,new byte[16]),(123,new byte[]{9})));
   Check(svc.Compatible&&svc.Port==443&&svc.Ipv4Hints.Single()=="8.8.8.8"&&svc.Ech=="ECH_CONFIG_PRESENT_NOT_VALIDATED"&&svc.Parameters.ContainsKey(123),"scenario modern metadata preserved without routing");
   Check(!ScenarioDns.ParseRecord(host,Svc((0,new byte[]{0,123}),(123,Array.Empty<byte>()))).Compatible,"scenario unknown mandatory incompatible");
   Check(!ScenarioDns.ParseRecord(host,Svc((0,new byte[]{0,5}),(5,new byte[]{0,0}))).Compatible,"scenario opaque ECH not mandatory implementation");
   foreach(var bad in new[]{new byte[]{0,1,0xc0,0},Svc((0,Array.Empty<byte>())),Svc((0,new byte[]{0,0})),Svc((0,new byte[]{0,3})),Svc((1,new byte[]{0})),Svc((1,new byte[]{2,1})),Svc((2,new byte[]{1})),Svc((2,Array.Empty<byte>())),Svc((3,new byte[]{1})),Svc((4,new byte[3])),Svc((6,new byte[15])),Svc((1,new byte[]{1,2}),(1,new byte[]{1,2})),Svc((3,new byte[]{1,187}),(1,new byte[]{1,2}))})Reject(()=>ScenarioDns.ParseRecord(host,bad),"scenario malformed modern RDATA");
   byte[] valid=ModernPacket(host,64,new byte[]{0,1,0});
   for(int i=0;i<valid.Length;i++) {byte[] shortPacket=valid.Take(i).ToArray();Reject(()=>ScenarioDns.Parse(shortPacket,host,64),"scenario DNS truncation "+i);}
   byte[] loop=(byte[])valid.Clone();loop[12]=0xc0;loop[13]=12;Reject(()=>ScenarioDns.Parse(loop,host,64),"scenario compression loop");
   byte[] tc=(byte[])valid.Clone();tc[2]|=2;Reject(()=>ScenarioDns.Parse(tc,host,64),"scenario DNS TC rejected");
  }
  public static int RunScenarioTests(){try{RunScenarioContracts();Console.WriteLine("SCENARIO_CONTRACT_PASS "+passed);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 }
}
