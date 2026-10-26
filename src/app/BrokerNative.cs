using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AntigravityGeminiDns {
 internal static class BrokerNative {
  internal const string ServiceName="AntigravityDnsBrokerV1",PipeName=AppIdentity.StorageName+".Broker.v1";
  internal static string Root=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),AppIdentity.StorageName,"broker-v1");
  internal static string Image=>Path.Combine(Root,"AntigravityDnsBroker.exe");
  internal static string ServiceCommand=>"\""+Image+"\" --broker-service";
  internal static string Manifest=>Path.Combine(Root,"image.sha256");
  internal static void Check(bool ok){if(!ok)throw new Win32Exception(Marshal.GetLastWin32Error());}
  internal static void Admin(){using var i=WindowsIdentity.GetCurrent();if(!new WindowsPrincipal(i).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("ELEVATED_ADMIN_REQUIRED");}
  internal static void Protect(){ScriptStore.EnsureProtectedDirectory(Path.GetDirectoryName(Root));ScriptStore.EnsureProtectedDirectory(Root);}
  internal static void VerifyProtected(string path,bool directory) {
   var item=directory?(FileSystemInfo)new DirectoryInfo(path):new FileInfo(path);
   if(!item.Exists||(item.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("BROKER_PATH_UNTRUSTED");
   var acl=directory?(FileSystemSecurity)((DirectoryInfo)item).GetAccessControl():((FileInfo)item).GetAccessControl();
   string owner=((SecurityIdentifier)acl.GetOwner(typeof(SecurityIdentifier))).Value;
   if(owner!="S-1-5-18"&&owner!="S-1-5-32-544")throw new IOException("BROKER_OWNER_UNTRUSTED");
   foreach(FileSystemAccessRule r in acl.GetAccessRules(true,true,typeof(SecurityIdentifier))){
    string sid=((SecurityIdentifier)r.IdentityReference).Value;
    const FileSystemRights writes=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
    if(r.AccessControlType==AccessControlType.Allow&&((r.FileSystemRights&writes)!=0||((uint)r.FileSystemRights&0x50000000u)!=0)&&sid!="S-1-5-18"&&sid!="S-1-5-32-544")throw new IOException("BROKER_ACL_UNTRUSTED");
   }
  }
  internal static void VerifyInstalled() {
   VerifyProtected(Path.GetDirectoryName(Root),true);VerifyProtected(Root,true);VerifyProtected(Image,false);VerifyProtected(Manifest,false);
   string hash=File.ReadAllText(Manifest).Trim();if(hash.Length!=64||!string.Equals(hash,ScriptStore.Sha256File(Image),StringComparison.Ordinal))throw new IOException("BROKER_HASH_MISMATCH");
  }
  internal static NamedPipeServerStream Pipe() {
   var acl=new PipeSecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(new SecurityIdentifier("S-1-5-18"));
   foreach(string sid in new[]{"S-1-5-18","S-1-5-32-544"})acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid),PipeAccessRights.FullControl,AccessControlType.Allow));
   // GenericWrite includes CreateNewInstance. Grant only data/attribute/synchronization rights to clients.
   acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier("S-1-5-32-545"),PipeAccessRights.ReadData|PipeAccessRights.WriteData|PipeAccessRights.ReadAttributes|PipeAccessRights.Synchronize,AccessControlType.Allow));
   return NamedPipeServerStreamAcl.Create(PipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.FirstPipeInstance,16384,16384,acl,HandleInheritability.None);
  }
  internal static BrokerIdentity Client(NamedPipeServerStream pipe) {
   Check(GetNamedPipeClientProcessId(pipe.SafePipeHandle,out uint pid));Check(GetNamedPipeClientSessionId(pipe.SafePipeHandle,out uint session));
   using var process=Process.GetProcessById(checked((int)pid));if(process.SessionId!=(int)session)throw new UnauthorizedAccessException("CLIENT_SESSION");
   BrokerIdentity identity=null;
   pipe.RunAsClient(()=>{using var token=WindowsIdentity.GetCurrent(true);if(token==null||token.ImpersonationLevel<TokenImpersonationLevel.Identification||!TokenSession(token.AccessToken,out int tokenSession)||tokenSession!=(int)session)throw new UnauthorizedAccessException("CLIENT_TOKEN");
    if(token.Groups!=null&&token.Groups.Any(g=>g.Value=="S-1-5-2"))throw new UnauthorizedAccessException("REMOTE_CLIENT_DENIED");
    bool admin=new WindowsPrincipal(token).IsInRole(WindowsBuiltInRole.Administrator);identity=new(token.User.Value,tokenSession,admin&&Elevated(token.AccessToken));
   });return identity??throw new UnauthorizedAccessException("CLIENT_TOKEN");
  }
  static bool Elevated(SafeAccessTokenHandle token){Check(GetTokenInformation(token,20,out int elevation,4,out _));return elevation!=0;}
  static bool TokenSession(SafeAccessTokenHandle token,out int session)=>GetTokenInformation(token,12,out session,4,out _);
  internal static void Server(NamedPipeClientStream pipe) {
   VerifyInstalled();Check(GetNamedPipeServerProcessId(pipe.SafePipeHandle,out uint pid));using var scm=Scm(1);using var service=Open(scm,5);var status=Status(service);
   if(status.CurrentState!=4||status.ProcessId!=pid||pid==0)throw new UnauthorizedAccessException("SERVER_SCM_PID");
   VerifyServiceConfiguration(service);
   VerifyProcessIfAccessible(pid);
   // SCM config is authoritative for the named own-process service. Ordinary users may query that
   // config/status but cannot query a SYSTEM process/token. Only ACCESS_DENIED permits this alternative;
   // image/token mismatch or any other query error still fails. Admin/SYSTEM tampering is outside scope.
   Check(GetNamedPipeServerProcessId(pipe.SafePipeHandle,out uint afterPid));var after=Status(service);VerifyServiceConfiguration(service);
   if(after.CurrentState!=4||after.ProcessId!=pid||afterPid!=pid)throw new UnauthorizedAccessException("SERVER_SCM_PID");
  }
  internal static bool TrustedServiceConfiguration(uint type,string command,string account)=>type==0x10&&string.Equals(command,ServiceCommand,StringComparison.OrdinalIgnoreCase)&&string.Equals(account,"LocalSystem",StringComparison.OrdinalIgnoreCase);
  static void VerifyServiceConfiguration(ScHandle service) {
   bool initial=QueryServiceConfig(service,IntPtr.Zero,0,out int needed);int error=Marshal.GetLastWin32Error();
   if(initial||error!=122||needed<Marshal.SizeOf<ServiceConfiguration>()||needed>8192)throw new IOException("SERVER_SCM_CONFIG_SIZE");
   IntPtr memory=Marshal.AllocHGlobal(needed);
   try{Check(QueryServiceConfig(service,memory,needed,out _));var c=Marshal.PtrToStructure<ServiceConfiguration>(memory);
    if(!TrustedServiceConfiguration(c.ServiceType,Marshal.PtrToStringUni(c.BinaryPath),Marshal.PtrToStringUni(c.Account)))throw new UnauthorizedAccessException("SERVER_SCM_CONFIG");
   }finally{Marshal.FreeHGlobal(memory);}
  }
  static void VerifyProcessIfAccessible(uint pid) {
   using var process=OpenProcess(0x1000,false,pid);
   if(process.IsInvalid){int error=Marshal.GetLastWin32Error();if(error==5)return;throw new Win32Exception(error);}
   var image=new StringBuilder(32768);int size=image.Capacity;
   if(!QueryFullProcessImageName(process.DangerousGetHandle(),0,image,ref size)){int error=Marshal.GetLastWin32Error();if(error==5)return;throw new Win32Exception(error);}
   if(!string.Equals(Path.GetFullPath(image.ToString()),Image,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("SERVER_IMAGE");
   if(!OpenProcessToken(process.DangerousGetHandle(),8,out var raw)){int error=Marshal.GetLastWin32Error();if(error==5)return;throw new Win32Exception(error);}
   using var token=new SafeAccessTokenHandle(raw);using var identity=new WindowsIdentity(token.DangerousGetHandle());if(identity.User.Value!="S-1-5-18")throw new UnauthorizedAccessException("SERVER_NOT_SYSTEM");
  }
  internal sealed class ScHandle:SafeHandleZeroOrMinusOneIsInvalid {public ScHandle():base(true){}public ScHandle(IntPtr h):base(true){SetHandle(h);}protected override bool ReleaseHandle()=>CloseServiceHandle(handle);}
  internal static ScHandle Scm(uint access){var h=OpenSCManager(null,null,access);if(h.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());return h;}
  internal static ScHandle Open(ScHandle scm,uint access){var h=OpenService(scm,ServiceName,access);if(h.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());return h;}
  [StructLayout(LayoutKind.Sequential)]internal struct ServiceStatus {public uint ServiceType,CurrentState,ControlsAccepted,Win32ExitCode,ServiceSpecificExitCode,CheckPoint,WaitHint;}
  [StructLayout(LayoutKind.Sequential)]internal struct ServiceStatusProcess {public uint ServiceType,CurrentState,ControlsAccepted,Win32ExitCode,ServiceSpecificExitCode,CheckPoint,WaitHint,ProcessId,Flags;}
  [StructLayout(LayoutKind.Sequential)]struct ServiceConfiguration {public uint ServiceType,StartType,ErrorControl;public IntPtr BinaryPath,LoadOrderGroup;public uint Tag;public IntPtr Dependencies,Account,DisplayName;}
  internal static ServiceStatusProcess Status(ScHandle service){Check(QueryServiceStatusEx(service,0,out var s,Marshal.SizeOf<ServiceStatusProcess>(),out _));return s;}
  internal delegate void ServiceMain(int count,IntPtr args);
  internal delegate void Handler(uint control);
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct Table { [MarshalAs(UnmanagedType.LPWStr)]public string Name;public ServiceMain Main; }
  [StructLayout(LayoutKind.Sequential)]struct ScAction {public uint Type,Delay;}
  [StructLayout(LayoutKind.Sequential)]struct FailureActions {public uint Reset;public IntPtr Reboot,Command;public uint Count;public IntPtr Actions;}
  internal static void ConfigureRecovery(ScHandle service) {
   IntPtr memory=Marshal.AllocHGlobal(Marshal.SizeOf<ScAction>()*3);
   try{
    for(int i=0;i<3;i++)Marshal.StructureToPtr(new ScAction{Type=1,Delay=5000},IntPtr.Add(memory,i*Marshal.SizeOf<ScAction>()),false);
    var actions=new FailureActions{Reset=86400,Count=3,Actions=memory};Check(ChangeServiceFailureActions(service,2,ref actions));
    int onNonCrash=1;Check(ChangeServiceFailureFlag(service,4,ref onNonCrash));
   }finally{Marshal.FreeHGlobal(memory);}
  }
  internal static void ProtectService(ScHandle service) {
   // Service-specific access masks: SYSTEM/Admin service-all; Users query-config/query-status/read-control.
   // In particular local users receive neither START/STOP nor CHANGE_CONFIG/WRITE_DAC/DELETE.
   Console.Error.WriteLine("BROKER_ACL_STEP before-sddl");Console.Error.Flush();
   Check(ConvertStringSecurityDescriptorToSecurityDescriptor("D:(A;;0xF01FF;;;SY)(A;;0xF01FF;;;BA)(A;;0x20005;;;BU)",1,out var descriptor,out _));
   try{
    Console.Error.WriteLine("BROKER_ACL_STEP sddl-converted");Console.Error.Flush();
    Check(GetSecurityDescriptorDacl(descriptor,out bool present,out var dacl,out _));if(!present||dacl==IntPtr.Zero)throw new IOException("SERVICE_DACL_MISSING");
    Console.Error.WriteLine("BROKER_ACL_STEP dacl-validated-before-service-api");Console.Error.Flush();
    // Use the documented service-specific API. Pass only its supported DACL flag; a service
    // descriptor is checked below for the exact explicit ACEs rather than assuming D:P was applied.
    Check(SetServiceObjectSecurity(service,4,descriptor));
    Console.Error.WriteLine("BROKER_ACL_STEP service-api-returned");Console.Error.Flush();
    VerifyServiceDacl(service);
    Console.Error.WriteLine("BROKER_ACL_STEP effective-dacl-confirmed");Console.Error.Flush();
   }finally{LocalFree(descriptor);}
  }
  internal static bool ExactServiceDacl(string sddl) {
   var acl=new RawSecurityDescriptor(sddl).DiscretionaryAcl;if(acl==null||acl.Count!=3)return false;
   var wanted=new System.Collections.Generic.Dictionary<string,int>{{"S-1-5-18",0xF01FF},{"S-1-5-32-544",0xF01FF},{"S-1-5-32-545",0x20005}};
   foreach(GenericAce entry in acl){if(entry is not CommonAce a||a.AceFlags!=AceFlags.None||a.AceQualifier!=AceQualifier.AccessAllowed||a.IsCallback||!wanted.Remove(a.SecurityIdentifier.Value,out int mask)||mask!=a.AccessMask)return false;}return wanted.Count==0;
  }
  static void VerifyServiceDacl(ScHandle service) {
   bool initial=QueryServiceObjectSecurity(service,4,null,0,out uint needed);int error=Marshal.GetLastWin32Error();if(initial||error!=122||needed<20||needed>8192)throw new IOException("SERVICE_DACL_QUERY_SIZE");
   byte[] descriptor=new byte[needed];Check(QueryServiceObjectSecurity(service,4,descriptor,needed,out _));
   string sddl=new RawSecurityDescriptor(descriptor,0).GetSddlForm(AccessControlSections.Access);if(!ExactServiceDacl(sddl))throw new IOException("SERVICE_EFFECTIVE_DACL_MISMATCH");
  }
  internal static void ReadOnlyAbiProbe() {
   void Step(string name){Console.Error.WriteLine("BROKER_ABI_READ_STEP "+name);Console.Error.Flush();}
   void ExpectedFailure(Action call,string name){bool failed=false;try{call();}catch(Win32Exception ex)when(ex.NativeErrorCode==6){failed=true;Step(name+" invalid-handle-confirmed");}if(!failed)throw new IOException("READ_PROBE_EXPECTED_INVALID_HANDLE "+name);}
   // The literal NULL SCM/service handle cannot identify a service or acquire mutation rights.
   // No real handle is passed to any mutation API in this diagnostic.
   using var invalid=new ScHandle(IntPtr.Zero);
   Step("before-create-service-null-scm");
   using(var created=CreateService(invalid,"AntigravityDnsReadProbeNeverCreated","invalid-handle diagnostic",0xF01FF,0x10,3,1,"invalid",null,IntPtr.Zero,null,"LocalSystem",null)){
    if(!created.IsInvalid)throw new IOException("READ_PROBE_UNEXPECTED_HANDLE");int error=Marshal.GetLastWin32Error();if(error!=6)throw new Win32Exception(error);Step("create-service invalid-handle-confirmed");
   }
   Step("before-configure-recovery-null-service");ExpectedFailure(()=>ConfigureRecovery(invalid),"configure-recovery");
   Step("before-security-descriptor-memory-only");
   Check(ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;0xF01FF;;;SY)(A;;0xF01FF;;;BA)(A;;0x20005;;;BU)",1,out var descriptor,out _));
   try{
    Check(GetSecurityDescriptorDacl(descriptor,out bool present,out var dacl,out _));if(!present||dacl==IntPtr.Zero)throw new IOException("READ_PROBE_DACL_MISSING");
    Step("descriptor-memory-valid-before-setsecurityinfo-null");uint error=SetSecurityInfo(IntPtr.Zero,5,0x80000004,IntPtr.Zero,IntPtr.Zero,dacl,IntPtr.Zero);if(error!=6)throw new Win32Exception((int)error);Step("setsecurityinfo invalid-handle-confirmed");
    ExpectedFailure(()=>Check(SetServiceObjectSecurity(invalid,4,descriptor)),"service-specific-security");
   }finally{LocalFree(descriptor);}
   Step("abi-memory-released");
  }
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern bool StartServiceCtrlDispatcher([In]Table[] table);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern IntPtr RegisterServiceCtrlHandler(string name,Handler handler);
  [DllImport("advapi32.dll",SetLastError=true)]internal static extern bool SetServiceStatus(IntPtr handle,ref ServiceStatus status);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern ScHandle OpenSCManager(string machine,string database,uint access);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern ScHandle OpenService(ScHandle scm,string name,uint access);
  [DllImport("advapi32.dll",SetLastError=true)]static extern bool CloseServiceHandle(IntPtr handle);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern ScHandle CreateService(ScHandle scm,string name,string display,uint access,uint type,uint start,uint error,string binary,string group,IntPtr tag,string dependencies,string account,string password);
  [DllImport("advapi32.dll",SetLastError=true)]internal static extern bool DeleteService(ScHandle service);
  [DllImport("advapi32.dll",SetLastError=true)]internal static extern bool StartService(ScHandle service,int count,IntPtr args);
  [DllImport("advapi32.dll",SetLastError=true)]internal static extern bool ControlService(ScHandle service,uint control,out ServiceStatus status);
  [DllImport("advapi32.dll",EntryPoint="ChangeServiceConfig2W",SetLastError=true)]static extern bool ChangeServiceFailureActions(ScHandle service,uint level,ref FailureActions value);
  [DllImport("advapi32.dll",EntryPoint="ChangeServiceConfig2W",SetLastError=true)]static extern bool ChangeServiceFailureFlag(ScHandle service,uint level,ref int value);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl,uint revision,out IntPtr descriptor,out uint size);
  [DllImport("advapi32.dll",SetLastError=true)]static extern bool GetSecurityDescriptorDacl(IntPtr descriptor,out bool present,out IntPtr dacl,out bool defaulted);
  [DllImport("advapi32.dll")]static extern uint SetSecurityInfo(IntPtr handle,int type,uint info,IntPtr owner,IntPtr group,IntPtr dacl,IntPtr sacl);
  [DllImport("advapi32.dll",SetLastError=true)]static extern bool SetServiceObjectSecurity(ScHandle service,uint info,IntPtr descriptor);
  [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceObjectSecurity(ScHandle service,uint info,[Out]byte[] descriptor,uint size,out uint needed);
  [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr memory);
  [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceStatusEx(ScHandle service,int info,out ServiceStatusProcess status,int bytes,out int needed);
  [DllImport("advapi32.dll",EntryPoint="QueryServiceConfigW",SetLastError=true)]static extern bool QueryServiceConfig(ScHandle service,IntPtr configuration,int bytes,out int needed);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe,out uint id);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
  [DllImport("kernel32.dll",SetLastError=true)]static extern SafeProcessHandle OpenProcess(uint access,bool inherit,uint pid);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageName(IntPtr process,int flags,StringBuilder path,ref int size);
  [DllImport("advapi32.dll",SetLastError=true)]static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
  [DllImport("advapi32.dll",SetLastError=true)]static extern bool GetTokenInformation(SafeAccessTokenHandle token,int type,out int info,int bytes,out int needed);
 }
}
