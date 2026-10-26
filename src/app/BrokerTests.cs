using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 public static partial class Broker {
  static int ContractTests() {
   int count=0;void Check(bool ok,string name){if(!ok)throw new InvalidOperationException("BROKER_CONTRACT_FAIL: "+name);count++;}
   void Reject(Action action,string name){try{action();}catch{count++;return;}throw new InvalidOperationException("BROKER_CONTRACT_FAIL accepted: "+name);}
   long now=Now;string nonce=new string('A',64);var who=new BrokerIdentity("S-1-5-21-100",1,true);
   var status=new BrokerRequest(1,Guid.NewGuid().ToString("N"),nonce,now+30000,"status","","","",0);
   Check(BrokerProtocol.Parse(BrokerProtocol.Serialize(status),nonce,now)==status,"status roundtrip");
   Reject(()=>BrokerProtocol.Parse(Encoding.UTF8.GetBytes("{}"),nonce,now),"missing fields");
   string json=JsonSerializer.Serialize(status);
   Reject(()=>BrokerProtocol.Parse(Encoding.UTF8.GetBytes(json.Replace("\"Version\":1","\"Version\":1,\"Version\":1")),nonce,now),"duplicate");
   Reject(()=>BrokerProtocol.Parse(Encoding.UTF8.GetBytes(json.Replace("\"Version\":1","\"Version\":1,\"Command\":\"cmd.exe\"")),nonce,now),"unknown command field");
   Reject(()=>BrokerProtocol.Parse(new byte[16385],nonce,now),"oversized JSON");
   Reject(()=>BrokerProtocol.Parse(new byte[]{0xff},nonce,now),"invalid UTF8");
   foreach(var bad in new[]{status with{Version=2},status with{Nonce=new string('B',64)},status with{DeadlineUtcMs=now},status with{DeadlineUtcMs=now+30001},status with{Operation="powershell"},status with{RequestId="../arbitrary"},status with{Scope="google"},status with{Profile="C:\\evil"},status with{LeaseUntilMs=now+10},status with{LeaseId=Guid.NewGuid().ToString("N")}})Reject(()=>BrokerProtocol.Validate(bad,nonce,now),"invalid request field");
   Reject(()=>BrokerProtocol.Authorize(who,"S-1-5-21-101",1,false),"wrong user");
   Reject(()=>BrokerProtocol.Authorize(who,who.Sid,2,false),"wrong session");
   Reject(()=>BrokerProtocol.Authorize(who with{Elevated=false},who.Sid,1,true),"unelevated mutation");
   BrokerProtocol.Authorize(who with{Elevated=false},who.Sid,1,false);Check(true,"unelevated status");
   var acquire=status with{Operation="acquire",Profile=ProfileCatalog.BuiltIn.DefaultProfile.Id,Scope="core",LeaseId=Guid.NewGuid().ToString("N"),LeaseUntilMs=now+60000};
   BrokerProtocol.Validate(acquire,nonce,now);Check(true,"finite acquisition");
   foreach(var bad in new[]{acquire with{Profile="https://evil.example"},acquire with{Scope="*.example"},acquire with{LeaseUntilMs=now+86400001},acquire with{LeaseUntilMs=now},acquire with{LeaseId="../../file"}})Reject(()=>BrokerProtocol.Validate(bad,nonce,now),"acquire boundary");
   Check(BrokerProtocol.Digest(status,who)==BrokerProtocol.Digest(status with{Nonce=new string('B',64),DeadlineUtcMs=now+10000},who),"transport renewal stable digest");
   Check(BrokerProtocol.Digest(status,who)!=BrokerProtocol.Digest(status,who with{Session=2}),"digest identity binding");
   Check(ReusableCandidate(new string('A',64),new string('A',64),"Absent"),"identical absent candidate reinstall");
   Check(ReusableCandidate(new string('A',64),new string('A',64),"Restored"),"identical restored candidate reinstall");
   Check(!ReusableCandidate(new string('A',64),new string('B',64),"Restored"),"different candidate refused");
   Check(!ReusableCandidate(new string('A',64),new string('A',64),"Active"),"active retained candidate refused");
   Check(!ReusableCandidate(new string('A',64),new string('A',64),"Failed"),"failed retained candidate refused");
   Check(BrokerNative.TrustedServiceConfiguration(0x10,BrokerNative.ServiceCommand,"LocalSystem"),"authoritative SCM own-process SYSTEM config");
   Check(!BrokerNative.TrustedServiceConfiguration(0x20,BrokerNative.ServiceCommand,"LocalSystem"),"shared-process service refused");
   Check(!BrokerNative.TrustedServiceConfiguration(0x10,BrokerNative.ServiceCommand,"LocalService"),"wrong service account refused");
   Check(!BrokerNative.TrustedServiceConfiguration(0x10,BrokerNative.ServiceCommand+" --extra","LocalSystem"),"extra service arguments refused");
   Check(!BrokerNative.TrustedServiceConfiguration(0x10,"C:\\evil.exe --broker-service","LocalSystem"),"wrong service image refused");
   string serviceDacl="D:(A;;0xF01FF;;;SY)(A;;0xF01FF;;;BA)(A;;0x20005;;;BU)";
   Check(BrokerNative.ExactServiceDacl(serviceDacl),"exact effective service DACL");
   Check(BrokerNative.ExactServiceDacl(serviceDacl.Replace("D:","D:P")),"DACL protection flag independent of exact rights");
   Check(!BrokerNative.ExactServiceDacl(serviceDacl.Replace("0x20005","0xF01FF")),"user service-all refused");
   Check(!BrokerNative.ExactServiceDacl(serviceDacl+"(A;;0x20005;;;AU)"),"extra service ACE refused");
   Check(!BrokerNative.ExactServiceDacl(serviceDacl.Replace(";;;BU",";;;AU")),"wrong client service SID refused");
   Check(PipeReuseRegression().GetAwaiter().GetResult(),"first-instance pipe reused after client EOF");
   var maxLeases=Enumerable.Range(1,128).Select(i=>new{Id=i.ToString("x32"),Owner=new string('A',192),ExpiresUtcMs=long.MaxValue,Policy="RestoreWhenUnreferenced"}).ToArray();
   string fullStatus=JsonSerializer.Serialize(new{Code="LEASE_STATUS",LeaseVersion=1,Managed=true,Phase="Active",Leases=maxLeases});
   var maxReply=EngineReply(status.RequestId,0,fullStatus,"");
   using(var native=JsonDocument.Parse(maxReply.Output))Check(native.RootElement.GetProperty("Leases").GetArrayLength()==128&&native.RootElement.GetProperty("Leases")[127].GetProperty("Owner").GetString().Length==192,"full max128 leases with max192 owner remain valid");
   byte[] maxWire=BrokerProtocol.Serialize(maxReply);Check(maxWire.Length>BrokerProtocol.MaxFrame&&maxWire.Length<=BrokerProtocol.MaxReplyFrame,"max lease reply bounded separately from request");
   using(var transport=new MemoryStream()){
    BrokerProtocol.WriteReply(transport,maxReply,CancellationToken.None).GetAwaiter().GetResult();transport.Position=0;
    using var envelope=BrokerProtocol.ReplyObject(BrokerProtocol.ReadReply(transport,CancellationToken.None).GetAwaiter().GetResult());
    Check(envelope.RootElement.GetProperty("Output").GetString()==maxReply.Output,"full response frame roundtrip");
   }
   Check(ResponsePipeRoundtrip(maxReply).GetAwaiter().GetResult(),"actual local pipe transports full128 lease reply");
   Reject(()=>BrokerProtocol.Object(maxWire,"Version","RequestId","Code","ExitCode","Output"),"reply allowance does not widen request object");
   var tooBigReply=maxReply with{Output=new string('A',BrokerProtocol.MaxReplyFrame)};
   Reject(()=>BrokerProtocol.WriteReply(new MemoryStream(),tooBigReply,CancellationToken.None).GetAwaiter().GetResult(),"oversize reply write refused");
   using(var oversizedResponse=new MemoryStream()){
    byte[] header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,BrokerProtocol.MaxReplyFrame+1);oversizedResponse.Write(header);oversizedResponse.Position=0;
    Reject(()=>BrokerProtocol.ReadReply(oversizedResponse,CancellationToken.None).GetAwaiter().GetResult(),"oversize reply read refused before allocation");
   }
   string root=Path.Combine(Path.GetTempPath(),"AgDnsBrokerContracts-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
   try{
    var receipts=new BrokerReceipts(root);int calls=0;BrokerReply Run(){calls++;return new(1,status.RequestId,"COMPLETE",0,"fixture");}
    var first=receipts.Execute(status,who,Run);var retry=receipts.Execute(status,who,Run);Check(first==retry&&calls==1,"duplicate retry no dispatch");
    Reject(()=>receipts.Execute(status with{Operation="recover"},who,Run),"same id payload mismatch");
    Reject(()=>receipts.Execute(status,who with{Sid="S-1-5-21-101"},Run),"same id other user");
    var interrupted=status with{RequestId=Guid.NewGuid().ToString("N")};Reject(()=>receipts.Execute(interrupted,who,()=>throw new IOException("fixture crash")),"interrupted dispatch");
    Check(receipts.Execute(interrupted,who,Run).Code=="UNCERTAIN_RECOVERY_REQUIRED"&&calls==1,"durable uncertainty no repeat");
    var invalidOutput=status with{RequestId=Guid.NewGuid().ToString("N")};Reject(()=>receipts.Execute(invalidOutput,who,()=>EngineReply(invalidOutput.RequestId,0,"{", "")),"successful invalid output never complete");
    Check(receipts.Execute(invalidOutput,who,Run).Code=="UNCERTAIN_RECOVERY_REQUIRED"&&calls==1,"invalid output retains durable uncertainty");
    var excessOutput=status with{RequestId=Guid.NewGuid().ToString("N")};Reject(()=>receipts.Execute(excessOutput,who,()=>EngineReply(excessOutput.RequestId,0,JsonSerializer.Serialize(new{Data=new string('A',BrokerProtocol.MaxReplyFrame)}),"")),"oversize successful output never complete");
    Check(receipts.Execute(excessOutput,who,Run).Code=="UNCERTAIN_RECOVERY_REQUIRED"&&calls==1,"oversize output retains durable uncertainty");
    using var stream=new MemoryStream();byte[] size=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(size,16385);stream.Write(size);stream.Position=0;
    Reject(()=>BrokerProtocol.Read(stream,CancellationToken.None).GetAwaiter().GetResult(),"oversized frame before allocation");
    Console.WriteLine("BROKER_CONTRACT_PASS "+count+" fixture="+root);return 0;
   }finally{/* Preserve fixture receipts as inspectable evidence. */}
  }
  static async Task<bool> PipeReuseRegression() {
   string name="AgDnsBrokerContract-"+Guid.NewGuid().ToString("N");
   using var server=new System.IO.Pipes.NamedPipeServerStream(name,System.IO.Pipes.PipeDirection.InOut,1,System.IO.Pipes.PipeTransmissionMode.Byte,System.IO.Pipes.PipeOptions.Asynchronous|System.IO.Pipes.PipeOptions.FirstPipeInstance);
   using var timeout=new CancellationTokenSource(10000);
   for(byte iteration=1;iteration<=2;iteration++){
    var waiting=server.WaitForConnectionAsync(timeout.Token);
    using(var client=new System.IO.Pipes.NamedPipeClientStream(".",name,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous)){
     await client.ConnectAsync(timeout.Token);await waiting;byte[] data=new byte[1];var read=server.ReadExactlyAsync(data,timeout.Token);await client.WriteAsync(new byte[]{iteration},timeout.Token);await read;if(data[0]!=iteration)return false;
    }
    bool eof=false;try{await server.ReadExactlyAsync(new byte[1],timeout.Token);}catch(EndOfStreamException){eof=true;}
    if(!eof||server.IsConnected)return false;
    server.Disconnect(); // Broken is disconnected even though IsConnected is false.
   }
   return true;
  }
  static async Task<bool> ResponsePipeRoundtrip(BrokerReply reply) {
   string name="AgDnsBrokerResponseContract-"+Guid.NewGuid().ToString("N");
   using var server=new System.IO.Pipes.NamedPipeServerStream(name,System.IO.Pipes.PipeDirection.InOut,1,System.IO.Pipes.PipeTransmissionMode.Byte,System.IO.Pipes.PipeOptions.Asynchronous|System.IO.Pipes.PipeOptions.FirstPipeInstance,16384,16384);
   using var client=new System.IO.Pipes.NamedPipeClientStream(".",name,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous);
   using var timeout=new CancellationTokenSource(10000);var connected=server.WaitForConnectionAsync(timeout.Token);await client.ConnectAsync(timeout.Token);await connected;
   var reading=BrokerProtocol.ReadReply(client,timeout.Token);await BrokerProtocol.WriteReply(server,reply,timeout.Token);
   using var envelope=BrokerProtocol.ReplyObject(await reading);string output=envelope.RootElement.GetProperty("Output").GetString();
   using var native=JsonDocument.Parse(output);var leases=native.RootElement.GetProperty("Leases");
   return output==reply.Output&&leases.GetArrayLength()==128&&leases[127].GetProperty("Owner").GetString().Length==192;
  }
  // Lab-only native acceptance: requires an empty/restored journal and elevated admin.
  // Temporarily stops/restarts the installed service to test an actual hostile pipe server.
  static async Task<int> NativeTests() {
   BrokerNative.Admin();int count=0;
   var status=new BrokerRequest(1,Guid.NewGuid().ToString("N"),"",0,"status","","","",0);
   var baseline=await Send(status);if(baseline.ExitCode!=0)throw new IOException("NATIVE_FIXTURE_STATUS_FAILED");
   using(var state=JsonDocument.Parse(baseline.Output)){string phase=state.RootElement.GetProperty("Phase").GetString();if(phase!="Absent"&&phase!="Restored")throw new IOException("NATIVE_TEST_REQUIRES_ABSENT_OR_RESTORED_JOURNAL");}
   using(var scm=BrokerNative.Scm(1))using(var service=BrokerNative.Open(scm,0x34)){
    var beforeStop=BrokerNative.Status(service);
    Console.Error.WriteLine("BROKER_NATIVE_STOP_BEFORE "+JsonSerializer.Serialize(new{beforeStop.CurrentState,beforeStop.ControlsAccepted,beforeStop.ProcessId,beforeStop.Win32ExitCode}));
    if(!BrokerNative.ControlService(service,1,out _)){
     int error=System.Runtime.InteropServices.Marshal.GetLastWin32Error();var afterStop=BrokerNative.Status(service);
     throw new IOException("NATIVE_STOP_FAILED error="+error+" state="+afterStop.CurrentState+" controls="+afterStop.ControlsAccepted+" pid="+afterStop.ProcessId);
    }
    WaitState(service,1);
    try{
     using var fake=new System.IO.Pipes.NamedPipeServerStream(BrokerNative.PipeName,System.IO.Pipes.PipeDirection.InOut,1,System.IO.Pipes.PipeTransmissionMode.Byte,System.IO.Pipes.PipeOptions.Asynchronous|System.IO.Pipes.PipeOptions.FirstPipeInstance);
     using var timeout=new CancellationTokenSource(5000);var accepted=fake.WaitForConnectionAsync(timeout.Token);
     bool refused=false;try{await Send(status with{RequestId=Guid.NewGuid().ToString("N")});}catch(UnauthorizedAccessException ex)when(ex.Message=="SERVER_SCM_PID"){refused=true;}
     await accepted;if(!refused)throw new IOException("WRONG_SERVER_NOT_REJECTED");count++;Console.WriteLine("BROKER_NATIVE_NEGATIVE_PASS wrong-server actual-pipe-connected SCM-stopped");
    }finally{BrokerNative.Check(BrokerNative.StartService(service,0,IntPtr.Zero));WaitState(service,4);}
   }
   async Task BadHello(string sid,int session,string expected){
    using var pipe=new System.IO.Pipes.NamedPipeClientStream(".",BrokerNative.PipeName,System.IO.Pipes.PipeAccessRights.ReadData|System.IO.Pipes.PipeAccessRights.WriteData|System.IO.Pipes.PipeAccessRights.ReadAttributes|System.IO.Pipes.PipeAccessRights.Synchronize,System.IO.Pipes.PipeOptions.Asynchronous,System.Security.Principal.TokenImpersonationLevel.Identification,System.IO.HandleInheritability.None);
    using var timeout=new CancellationTokenSource(10000);await pipe.ConnectAsync(timeout.Token);BrokerNative.Server(pipe);await BrokerProtocol.Write(pipe,new{Version=1,Sid=sid,Session=session},timeout.Token);
    var reply=JsonSerializer.Deserialize<BrokerReply>(await BrokerProtocol.Read(pipe,timeout.Token));if(reply?.Code!="DENIED"||reply.Output!="CLIENT_IDENTITY")throw new IOException("HELLO_EXPECTED_IDENTITY_DENIAL "+expected);
    count++;Console.WriteLine("BROKER_NATIVE_NEGATIVE_PASS "+expected);
   }
   using(var identity=System.Security.Principal.WindowsIdentity.GetCurrent()){
    await BadHello("S-1-5-21-0-0-0-999",System.Diagnostics.Process.GetCurrentProcess().SessionId,"wrong-user");
    await BadHello(identity.User.Value,int.MaxValue,"wrong-session");
   }
   status=status with{RequestId=Guid.NewGuid().ToString("N")};
   var first=await Send(status);if(first.ExitCode!=0)throw new IOException("STATUS_FAILED "+first.Output);
   var retry=await Send(status);if(first!=retry)throw new IOException("DUPLICATE_RECEIPT_CHANGED");count++;Console.WriteLine("BROKER_NATIVE_NEGATIVE_PASS duplicate-identical-retry");
   var mismatch=await Send(status with{Operation="recover"});if(mismatch.Code!="DENIED")throw new IOException("PAYLOAD_MISMATCH_ACCEPTED");count++;Console.WriteLine("BROKER_NATIVE_NEGATIVE_PASS payload-mismatch");
   async Task Raw(Func<string,byte[]> make,bool prefixOnly=false){var c=await Connect();using var pipe=c.Pipe;using var timeout=new CancellationTokenSource(10000);byte[] data=make(c.Nonce);byte[] prefix=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(prefix,prefixOnly?16385:data.Length);await pipe.WriteAsync(prefix,timeout.Token);if(!prefixOnly)await pipe.WriteAsync(data,timeout.Token);await pipe.FlushAsync(timeout.Token);var reply=JsonSerializer.Deserialize<BrokerReply>(await BrokerProtocol.Read(pipe,timeout.Token));if(reply?.Code!="DENIED")throw new IOException("TEST_EXPECTED_DENIAL_NOT_EXCEPTION");}
   // Raw validates actual structured denial; connection failures are not mistaken for parser passes.
   await Raw(n=>Encoding.UTF8.GetBytes("{"));count++;Console.WriteLine("BROKER_NATIVE_NEGATIVE_PASS malformed");
   await Raw(n=>Array.Empty<byte>(),true);count++;Console.WriteLine("BROKER_NATIVE_NEGATIVE_PASS oversized");
   await Raw(n=>BrokerProtocol.Serialize(status with{RequestId=Guid.NewGuid().ToString("N"),Nonce=n,DeadlineUtcMs=Now+30000,Version=2}));count++;Console.WriteLine("BROKER_NATIVE_NEGATIVE_PASS protocol-skew");
   await Raw(n=>BrokerProtocol.Serialize(status with{RequestId=Guid.NewGuid().ToString("N"),Nonce=new string('F',64),DeadlineUtcMs=Now+30000}));count++;Console.WriteLine("BROKER_NATIVE_NEGATIVE_PASS replay-old-nonce");
   await Raw(n=>BrokerProtocol.Serialize(status with{RequestId=Guid.NewGuid().ToString("N"),Nonce=n,DeadlineUtcMs=Now-1}));count++;Console.WriteLine("BROKER_NATIVE_NEGATIVE_PASS expired-deadline");
   await Raw(n=>Encoding.UTF8.GetBytes(JsonSerializer.Serialize(status with{RequestId=Guid.NewGuid().ToString("N"),Nonce=n,DeadlineUtcMs=Now+30000}).Replace("\"Version\":1","\"Version\":1,\"Version\":1")));count++;Console.WriteLine("BROKER_NATIVE_NEGATIVE_PASS duplicate-field");
   await Raw(n=>Encoding.UTF8.GetBytes(JsonSerializer.Serialize(status with{RequestId=Guid.NewGuid().ToString("N"),Nonce=n,DeadlineUtcMs=Now+30000}).Replace("\"Version\":1","\"Version\":1,\"Path\":\"C:\\\\evil.exe\"")));count++;Console.WriteLine("BROKER_NATIVE_NEGATIVE_PASS arbitrary-path");
   await Raw(n=>Encoding.UTF8.GetBytes(JsonSerializer.Serialize(status with{RequestId=Guid.NewGuid().ToString("N"),Nonce=n,DeadlineUtcMs=Now+30000}).Replace("\"Version\":1","\"Version\":1,\"Command\":\"cmd.exe /c echo forbidden\"")));count++;Console.WriteLine("BROKER_NATIVE_NEGATIVE_PASS arbitrary-command");
   Console.WriteLine("BROKER_NATIVE_PASS "+count);return 0;
  }
 }
}
