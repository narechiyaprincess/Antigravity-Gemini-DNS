using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityGeminiDns {
 public static partial class Broker {
  static readonly SemaphoreSlim DispatchGate=new(1,1);
  static CancellationTokenSource stopping;
  static BrokerNative.ServiceMain serviceMain;
  static BrokerNative.Handler handler;
  static IntPtr serviceHandle;
  static long Now=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
  static string Receipts=>Path.Combine(BrokerNative.Root,"receipts");
  public static bool TryRun(string[] args,out int code) {
   code=0;if(!args.Any(a=>a.StartsWith("--broker-",StringComparison.Ordinal)))return false;
   try{
    if(args.Length==1&&args[0]=="--broker-service"){RunService();return true;}
    if(args.Length==1&&args[0]=="--broker-contract-test"){code=ContractTests();return true;}
    if(args.Length==1&&args[0]=="--broker-scm-read-test"){code=ScmReadTest();return true;}
    if(args.Length==1&&args[0]=="--broker-native-test"){code=NativeTests().GetAwaiter().GetResult();return true;}
    if(args.Length==1&&args[0]=="--broker-install"){Install();Console.WriteLine("BROKER_INSTALLED");return true;}
    if(args.Length==1&&args[0]=="--broker-uninstall"){Uninstall();Console.WriteLine("BROKER_UNINSTALLED_RECOVERY_CONFIRMED");return true;}
    if(args.Length==1&&(args[0]=="--broker-recover-local"||args[0]=="--broker-status-local")){
     BrokerNative.Admin();var result=Engine(args[0]=="--broker-status-local"?"status":"recover",null,null).GetAwaiter().GetResult();Console.WriteLine(result.Output);code=result.ExitCode;return true;
    }
    string op=args[0] switch{"--broker-status"=>"status","--broker-recover"=>"recover","--broker-release"=>"release","--broker-acquire"=>"acquire",_=>throw new ArgumentException("BROKER_ARGUMENTS")};
    string profile="",scope="",lease="";long until=0;
    if(op=="acquire"){
     if(args.Length!=5||args[4]!="--trust-proxy"||!int.TryParse(args[3],NumberStyles.None,CultureInfo.InvariantCulture,out int mins)||mins<1||mins>1440)throw new ArgumentException("BROKER_ACQUIRE: exact-profile core|gemini|google minutes --trust-proxy");
     profile=args[1];scope=args[2];lease=Guid.NewGuid().ToString("N");until=Now+mins*60000L;
    }else if(op=="release"){if(args.Length!=2)throw new ArgumentException("BROKER_RELEASE: lease-guid-n");lease=args[1];}
    else if(args.Length!=1)throw new ArgumentException("BROKER_ARGUMENTS");
    var request=new BrokerRequest(1,Guid.NewGuid().ToString("N"),"",0,op,profile,scope,lease,until);
    var reply=Send(request).GetAwaiter().GetResult();Console.WriteLine(JsonSerializer.Serialize(new{reply.Version,reply.RequestId,reply.Code,reply.ExitCode,LeaseId=lease,reply.Output}));code=reply.ExitCode;
   }catch(Exception ex){Console.Error.WriteLine("BROKER_REFUSED: "+ex.Message);code=1;}return true;
  }
  static async Task<BrokerReply> Engine(string operation,BrokerRequest request,BrokerIdentity who) {
   if(!new[]{"status","acquire","release","recover"}.Contains(operation))throw new InvalidOperationException("FINITE_ENGINE_OPERATION");
   var staged=ScriptStore.Prepare(true);ScriptStore.Verify(staged);
   var info=Program.SystemModulesOnly(new ProcessStartInfo(Program.PowerShell){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=staged.Directory});
   void Add(params string[] a){foreach(string v in a)info.ArgumentList.Add(v);}
   Add("-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",staged.Script("Engine.ps1"),"-Action","lease-"+operation,"-ExePath",staged.ExePath);
   if(operation=="acquire"){
    var p=ProfileCatalog.BuiltIn.Profiles.Single(p=>p.Id==request.Profile);
    var targets=request.Scope switch{"core"=>ProfileCatalog.BuiltIn.Targets,"gemini"=>ProfileCatalog.BuiltIn.TargetsFor(true),"google"=>ProfileCatalog.BuiltIn.TargetsForGoogle(),_=>throw new InvalidOperationException("FINITE_SCOPE")};
    Add("-TrustProxy","-Servers",string.Join(",",p.Servers),"-Template",p.Template,"-ProfileName",p.Id,"-Targets",string.Join(",",targets));
   }
   if(operation=="acquire"||operation=="release"){
    if(request==null||who==null||!BrokerProtocol.GuidN(request.LeaseId))throw new InvalidOperationException("LEASE_IDENTITY");
    Add("-LeaseId",request.LeaseId,"-LeaseOwner",who.Owner);
    if(operation=="acquire")Add("-LeaseUntilMs",request.LeaseUntilMs.ToString(CultureInfo.InvariantCulture));
   }
   using var process=Process.Start(info)??throw new IOException("ENGINE_START_FAILED");
   // Drain concurrently to avoid pipe deadlock; native engine output is bounded by finite catalog/journal size.
   var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
   using var timeout=new CancellationTokenSource(Program.Deadline(operation=="acquire"?"enable":"restore"));
   try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){try{process.Kill(true);await process.WaitForExitAsync();}catch{}throw new IOException("ENGINE_UNCERTAIN_RECOVERY_REQUIRED");}
   return EngineReply(request?.RequestId??"",process.ExitCode,await stdout,await stderr);
  }
  internal static BrokerReply EngineReply(string requestId,int exitCode,string output,string error) {
   if(exitCode==0){
    try{using var native=JsonDocument.Parse(output,new JsonDocumentOptions{MaxDepth=16});
     if(native.RootElement.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("ENGINE_OUTPUT_UNCERTAIN_RECOVERY_REQUIRED");
     output=JsonSerializer.Serialize(native.RootElement); // full valid native JSON; no successful-output truncation
    }catch(JsonException ex){throw new InvalidDataException("ENGINE_OUTPUT_UNCERTAIN_RECOVERY_REQUIRED",ex);}
   }else{output=output+"\n"+error;if(output.Length>1800)output=output.Substring(0,1800)+" [OUTPUT_TRUNCATED]";}
   var reply=new BrokerReply(1,requestId,exitCode==0?"COMPLETE":"ENGINE_SAFE_FAILURE",exitCode,output);
   // Failure here leaves an existing durable InProgress receipt intact; recovery retains journal authority.
   BrokerProtocol.EnsureReplySize(reply);return reply;
  }
  static async Task RecoverBackground() {
   await DispatchGate.WaitAsync();try{
    var reply=await Engine("recover",null,null);WriteHealth(reply.Code,reply.Output);
   }catch(Exception ex){WriteHealth("RECOVERY_REQUIRED",ex.Message);}finally{DispatchGate.Release();}
  }
  static void WriteHealth(string code,string detail){try{File.WriteAllText(Path.Combine(BrokerNative.Root,"recovery-health.json"),JsonSerializer.Serialize(new{Version=1,UtcMs=Now,Code=code,Detail=detail}));}catch{/* State/receipt persistence still fails closed before any requested mutation. */}}
  static async Task Serve(CancellationToken token) {
   BrokerNative.VerifyInstalled();if(!string.Equals(ScriptStore.RunningImagePath(),BrokerNative.Image,StringComparison.OrdinalIgnoreCase)||WindowsIdentity.GetCurrent().User.Value!="S-1-5-18")throw new UnauthorizedAccessException("SERVICE_IDENTITY");
   ScriptStore.EnsureProtectedDirectory(Receipts);
   using var pipe=BrokerNative.Pipe(); // retain the first-instance handle for the complete service lifetime
   await RecoverBackground();
   Status(4);
   var recovery=Task.Run(async()=>{try{while(!token.IsCancellationRequested){await Task.Delay(TimeSpan.FromSeconds(5),token);await RecoverBackground();}}catch(OperationCanceledException){}});
   try{while(!token.IsCancellationRequested){
    await pipe.WaitForConnectionAsync(token);
    try{await ServeConnection(pipe,token);}catch(Exception ex){try{using var errorTimeout=new CancellationTokenSource(2000);await BrokerProtocol.WriteReply(pipe,new BrokerReply(1,"","DENIED",1,ex.Message.Length>100?ex.Message.Substring(0,100):ex.Message),errorTimeout.Token);}catch{}}
    // A client EOF puts PipeStream into Broken state and IsConnected becomes false.
    // Disconnect must still reset this first-instance handle before the next WaitForConnection.
    finally{pipe.Disconnect();}
   }}finally{stopping.Cancel();await recovery;await DispatchGate.WaitAsync();DispatchGate.Release();}
  }
  static async Task ServeConnection(NamedPipeServerStream pipe,CancellationToken token) {
   using var helloTimeout=CancellationTokenSource.CreateLinkedTokenSource(token);helloTimeout.CancelAfter(5000);
   BrokerIdentity who;
   using(var hello=BrokerProtocol.Object(await BrokerProtocol.Read(pipe,helloTimeout.Token),"Version","Sid","Session")){
    who=BrokerNative.Client(pipe); // impersonation authenticates the token of a message already read
    var h=hello.RootElement;if(h.GetProperty("Version").GetInt32()!=1)throw new InvalidDataException("PROTOCOL_VERSION");
    BrokerProtocol.Authorize(who,h.GetProperty("Sid").GetString(),h.GetProperty("Session").GetInt32(),false);
   }
   string nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
   await BrokerProtocol.Write(pipe,new{Version=1,Nonce=nonce,who.Sid,who.Session},helloTimeout.Token);
   for(int i=0;i<16&&!token.IsCancellationRequested;i++){
    using var readTimeout=CancellationTokenSource.CreateLinkedTokenSource(token);readTimeout.CancelAfter(5000);
    var r=BrokerProtocol.Parse(await BrokerProtocol.Read(pipe,readTimeout.Token),nonce,Now);
    var current=BrokerNative.Client(pipe);BrokerProtocol.Authorize(current,who.Sid,who.Session,r.Operation!="status");
    await DispatchGate.WaitAsync(token);BrokerReply reply;
    try{
     BrokerProtocol.Validate(r,nonce,Now); // reject requests which expired waiting for a prior operation
     reply=r.Operation=="status"&&!who.Elevated?await Engine(r.Operation,r,who):new BrokerReceipts(Receipts).Execute(r,who,()=>Engine(r.Operation,r,who).GetAwaiter().GetResult());
    }finally{DispatchGate.Release();}
    using var writeTimeout=CancellationTokenSource.CreateLinkedTokenSource(token);writeTimeout.CancelAfter(5000);await BrokerProtocol.WriteReply(pipe,reply,writeTimeout.Token);
   }
  }
  static async Task<(NamedPipeClientStream Pipe,string Nonce)> Connect(string sid=null,int? session=null) {
   var pipe=new NamedPipeClientStream(".",BrokerNative.PipeName,PipeAccessRights.ReadData|PipeAccessRights.WriteData|PipeAccessRights.ReadAttributes|PipeAccessRights.Synchronize,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification,HandleInheritability.None);
   try{
    using var timeout=new CancellationTokenSource(5000);await pipe.ConnectAsync(timeout.Token);BrokerNative.Server(pipe);
    using var identity=WindowsIdentity.GetCurrent();int ownSession=Process.GetCurrentProcess().SessionId;
    await BrokerProtocol.Write(pipe,new{Version=1,Sid=sid??identity.User.Value,Session=session??ownSession},timeout.Token);
    byte[] bytes=await BrokerProtocol.Read(pipe,timeout.Token);
    using var hello=BrokerProtocol.Object(bytes,"Version","Nonce","Sid","Session");var h=hello.RootElement;
    if(h.GetProperty("Version").GetInt32()!=1||h.GetProperty("Sid").GetString()!=identity.User.Value||h.GetProperty("Session").GetInt32()!=ownSession)throw new UnauthorizedAccessException("SERVER_HELLO_IDENTITY");
    string nonce=h.GetProperty("Nonce").GetString();if(nonce==null||nonce.Length!=64)throw new InvalidDataException("SERVER_NONCE");return(pipe,nonce);
   }catch{pipe.Dispose();throw;}
  }
  static async Task<BrokerReply> Send(BrokerRequest request) {
   var connection=await Connect();using var pipe=connection.Pipe;using var timeout=new CancellationTokenSource(Program.Deadline(request.Operation=="acquire"?"enable":"restore")+TimeSpan.FromSeconds(15));
   var r=request with{Nonce=connection.Nonce,DeadlineUtcMs=Now+30000};BrokerProtocol.Validate(r,connection.Nonce,Now);
   await BrokerProtocol.Write(pipe,r,timeout.Token);
   using var d=BrokerProtocol.ReplyObject(await BrokerProtocol.ReadReply(pipe,timeout.Token));var e=d.RootElement;
   var reply=new BrokerReply(e.GetProperty("Version").GetInt32(),e.GetProperty("RequestId").GetString(),e.GetProperty("Code").GetString(),e.GetProperty("ExitCode").GetInt32(),e.GetProperty("Output").GetString());
   if(reply.Version!=1||reply.Output==null||!new[]{"COMPLETE","ENGINE_SAFE_FAILURE","UNCERTAIN_RECOVERY_REQUIRED","DENIED"}.Contains(reply.Code)||(reply.Code!="DENIED"&&reply.RequestId!=r.RequestId))throw new InvalidDataException("SERVER_REPLY_CONTRACT");return reply;
  }
  static void Status(uint state,uint error=0){var s=new BrokerNative.ServiceStatus{ServiceType=0x10,CurrentState=state,ControlsAccepted=state==4?5u:0u,Win32ExitCode=error,WaitHint=state==2||state==3?100000u:0u};BrokerNative.Check(BrokerNative.SetServiceStatus(serviceHandle,ref s));}
  static void RunService() {
   serviceMain=(count,args)=>{
    uint error=0;stopping=new CancellationTokenSource();handler=control=>{if(control==1||control==5){Status(3);stopping.Cancel();}};
    serviceHandle=BrokerNative.RegisterServiceCtrlHandler(BrokerNative.ServiceName,handler);
    if(serviceHandle==IntPtr.Zero)return;
    try{Status(2);BrokerNative.VerifyInstalled();Serve(stopping.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){}catch(Exception ex){WriteHealth("SERVICE_FAILED",ex.Message);error=1;}finally{try{Status(1,error);}catch{}}
   };
   BrokerNative.Check(BrokerNative.StartServiceCtrlDispatcher(new[]{new BrokerNative.Table{Name=BrokerNative.ServiceName,Main=serviceMain},new BrokerNative.Table()}));
   GC.KeepAlive(serviceMain);GC.KeepAlive(handler);
  }
  static FileStream InstallLock(){BrokerNative.Protect();return new FileStream(Path.Combine(BrokerNative.Root,"install.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
  static void TraceInstall(string step){Console.Error.WriteLine("BROKER_INSTALL_STEP "+step);Console.Error.Flush();}
  static int ScmReadTest() {
   Console.Error.WriteLine("BROKER_SCM_READ_STEP before-open-scm");Console.Error.Flush();
   using(var scm=BrokerNative.Scm(1)){
    Console.Error.WriteLine("BROKER_SCM_READ_STEP opened-scm");Console.Error.Flush();
    try{using var service=BrokerNative.Open(scm,4);Console.Error.WriteLine("BROKER_SCM_READ_STEP opened-service");Console.Error.Flush();var status=BrokerNative.Status(service);Console.WriteLine("BROKER_SCM_READ_PASS state="+status.CurrentState);}
    catch(Win32Exception ex)when(ex.NativeErrorCode==1060){Console.WriteLine("BROKER_SCM_READ_PASS service-absent");}
   }
   Console.Error.WriteLine("BROKER_SCM_READ_STEP handles-closed");Console.Error.Flush();BrokerNative.ReadOnlyAbiProbe();Console.WriteLine("BROKER_ABI_READ_PASS");return 0;
  }
  internal static bool ReusableCandidate(string sourceHash,string retainedHash,string phase)=>sourceHash!=null&&sourceHash.Length==64&&sourceHash==retainedHash&&(phase=="Absent"||phase=="Restored");
  static string RetainedPhase() {
   var info=new ProcessStartInfo(BrokerNative.Image){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add("--broker-status-local");
   using var process=Process.Start(info)??throw new IOException("RETAINED_STATUS_START_FAILED");var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
   if(!process.WaitForExit(45000)){try{process.Kill(true);}catch{}throw new IOException("RETAINED_STATUS_UNCONFIRMED");}
   string text=output.GetAwaiter().GetResult();_=errors.GetAwaiter().GetResult();if(process.ExitCode!=0)throw new IOException("RETAINED_STATUS_UNCONFIRMED");
   using var doc=JsonDocument.Parse(text);return doc.RootElement.GetProperty("Phase").GetString();
  }
  static void Install() {
   TraceInstall("before-admin");BrokerNative.Admin();TraceInstall("before-protected-lock");using var installLock=InstallLock();TraceInstall("protected-lock-held");
#pragma warning disable IL3000   // deliberate: an empty Location is the proof that this is the published single-file exe (see src/BROKER.md)
   if(!string.IsNullOrEmpty(typeof(Broker).Assembly.Location))throw new InvalidOperationException("INSTALL_REQUIRES_PUBLISHED_SINGLE_FILE");
#pragma warning restore IL3000
   TraceInstall("before-open-scm");using var scm=BrokerNative.Scm(3);TraceInstall("opened-scm");
   // Never overwrite a running or existing installation, or its retained recovery candidate.
   try{using var existing=BrokerNative.Open(scm,4);throw new InvalidOperationException("SERVICE_ALREADY_INSTALLED");}catch(Win32Exception ex)when(ex.NativeErrorCode==1060){}
   string source=ScriptStore.RunningImagePath()??throw new IOException("RUNNING_IMAGE_UNKNOWN");byte[] bytes=File.ReadAllBytes(source);ScriptStore.CheckRunningImage(bytes,source);
   if(File.Exists(BrokerNative.Image)||File.Exists(BrokerNative.Manifest)){
    BrokerNative.VerifyInstalled();string retained=File.ReadAllText(BrokerNative.Manifest).Trim();
    if(ScriptStore.Sha256(bytes)!=retained||!ReusableCandidate(ScriptStore.Sha256(bytes),retained,RetainedPhase()))throw new IOException("RETAINED_CANDIDATE_REQUIRES_REVIEW");
   }else{
    using(var file=new FileStream(BrokerNative.Image,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){file.Write(bytes);file.Flush(true);}
    File.WriteAllText(BrokerNative.Manifest,ScriptStore.Sha256(bytes));BrokerNative.VerifyInstalled();
   }
   TraceInstall("before-create-service");using var service=BrokerNative.CreateService(scm,BrokerNative.ServiceName,"Antigravity-Gemini DNS optional recovery broker",0xF01FF,0x10,2,1,BrokerNative.ServiceCommand,null,IntPtr.Zero,null,"LocalSystem",null);TraceInstall("create-service-returned");
   if(service.IsInvalid)throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
   TraceInstall("before-protect-service");BrokerNative.ProtectService(service);
   TraceInstall("before-configure-recovery");BrokerNative.ConfigureRecovery(service);
   TraceInstall("before-start-service");BrokerNative.Check(BrokerNative.StartService(service,0,IntPtr.Zero));TraceInstall("start-service-returned");WaitState(service,4);TraceInstall("running-confirmed");
  }
  static void WaitState(BrokerNative.ScHandle service,uint state){var clock=Stopwatch.StartNew();while(clock.Elapsed<TimeSpan.FromSeconds(100)){if(BrokerNative.Status(service).CurrentState==state)return;Thread.Sleep(100);}throw new IOException("SERVICE_TRANSITION_TIMEOUT");}
  static void Uninstall() {
   BrokerNative.Admin();using var installLock=InstallLock();BrokerNative.VerifyInstalled();using var scm=BrokerNative.Scm(1);using var service=BrokerNative.Open(scm,0x10034);
   var before=BrokerNative.Status(service);if(before.CurrentState!=1){BrokerNative.Check(BrokerNative.ControlService(service,1,out _));WaitState(service,1);}
   try {
   // Execute this installed candidate's embedded recovery code, even when the invoking client is newer.
   var info=new ProcessStartInfo(BrokerNative.Image){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add("--broker-recover-local");
   using var process=Process.Start(info)??throw new IOException("RECOVERY_START_FAILED");var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
   if(!process.WaitForExit(100000)){try{process.Kill(true);}catch{}throw new IOException("UNINSTALL_RECOVERY_UNCERTAIN_CANDIDATE_RETAINED");}
   string text=output.GetAwaiter().GetResult();_=errors.GetAwaiter().GetResult();if(process.ExitCode!=0)throw new IOException("UNINSTALL_RECOVERY_FAILED_CANDIDATE_RETAINED");
   using var doc=JsonDocument.Parse(text);string phase=doc.RootElement.GetProperty("Phase").GetString();
   if(phase!="Absent"&&phase!="Restored")throw new IOException("UNINSTALL_ACTIVE_OR_UNCONFIRMED_LEASES_CANDIDATE_RETAINED");
   BrokerNative.Check(BrokerNative.DeleteService(service));
   }catch{
    // An active lease or failed restore must keep independent recovery available after refusal.
    try{if(BrokerNative.Status(service).CurrentState==1){BrokerNative.Check(BrokerNative.StartService(service,0,IntPtr.Zero));WaitState(service,4);}}catch(Exception restart){throw new IOException("UNINSTALL_REFUSED_RECOVERY_RESTART_FAILED: "+restart.Message);}
    throw;
   }
   // Recovery binary, journals and durable request receipts are deliberately retained for audit/manual recovery.
  }
 }
}
