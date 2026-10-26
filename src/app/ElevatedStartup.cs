using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AntigravityGeminiDns {
 // Optional startup with administrator rights: ONE Windows (UAC) confirmation creates a logon task for the current user that runs a protected copy of this exe
 // with the highest available privileges, so connecting and disconnecting need no further prompts. UAC itself is never touched. Switching it off deletes the task.
 // The task never points at the user-writable exe: a same-user program could replace that file and be run elevated at the next logon. The copy lives in
 // %ProgramFiles%, which only administrators can write; the elevated step refuses a folder that ordinary users can write to.
 public enum ElevatedStartupState { Off, On, Stale, Unknown }
 public sealed record ElevatedStartupInfo(ElevatedStartupState State,string Exe,string Detail);
 public interface IElevatedStartupBackend {
  Task<ElevatedStartupInfo> QueryAsync(CancellationToken token);
  Task<int> EnableAsync(CancellationToken token);
  Task<int> DisableAsync(CancellationToken token);
 }
 public static class ElevatedStartup {
  public const string TaskName="Antigravity-Gemini DNS Elevated Logon";
  public const int Ok=0,Failed=1,NotAdmin=2,AccountMismatch=3,BadSource=4,CopyFailed=5,UnsafeFolder=6,TaskFailed=7,VerifyFailed=8,Cancelled=1223;
  public static string InstallDir=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Antigravity-Gemini DNS");
  public static string InstalledExe=>Path.Combine(InstallDir,"AntigravityGeminiDns.exe");
  static string SchTasks=>Path.Combine(Environment.SystemDirectory,"schtasks.exe");
  public static string Sha256(string path){using var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);return Convert.ToHexString(SHA256.HashData(s));}
  static bool ValidSid(string sid){try{var s=new SecurityIdentifier(sid);return s.Value==sid&&sid.StartsWith("S-1-5-21-",StringComparison.Ordinal);}catch{return false;}}
  // The exe path goes into an XML element and a command line: absolute, an .exe, no quotes or control characters.
  internal static bool ValidExePath(string exe)=>!string.IsNullOrWhiteSpace(exe)&&Path.IsPathFullyQualified(exe)&&string.Equals(Path.GetExtension(exe),".exe",StringComparison.OrdinalIgnoreCase)&&!exe.Any(c=>c=='"'||c=='<'||c=='>'||char.IsControl(c));
  internal static string BuildTaskXml(string sid,string exe) {
   if(!ValidSid(sid))throw new ArgumentException("SID");if(!ValidExePath(exe))throw new ArgumentException("EXE");
   XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
   var doc=new XDocument(new XDeclaration("1.0","UTF-16",null),new XElement(ns+"Task",new XAttribute("version","1.4"),
    new XElement(ns+"RegistrationInfo",new XElement(ns+"Description","Antigravity-Gemini DNS: starts the protected copy with administrator rights at logon (created once with one Windows confirmation; remove it in the program's settings).")),
    new XElement(ns+"Triggers",new XElement(ns+"LogonTrigger",new XElement(ns+"Enabled","true"),new XElement(ns+"UserId",sid),new XElement(ns+"Delay","PT5S"))),
    new XElement(ns+"Principals",new XElement(ns+"Principal",new XAttribute("id","Author"),new XElement(ns+"UserId",sid),new XElement(ns+"LogonType","InteractiveToken"),new XElement(ns+"RunLevel","HighestAvailable"))),
    new XElement(ns+"Settings",new XElement(ns+"MultipleInstancesPolicy","IgnoreNew"),new XElement(ns+"DisallowStartIfOnBatteries","false"),new XElement(ns+"StopIfGoingOnBatteries","false"),
     new XElement(ns+"AllowHardTerminate","true"),new XElement(ns+"StartWhenAvailable","false"),new XElement(ns+"ExecutionTimeLimit","PT0S"),new XElement(ns+"Enabled","true")),
    new XElement(ns+"Actions",new XAttribute("Context","Author"),new XElement(ns+"Exec",new XElement(ns+"Command",exe),new XElement(ns+"Arguments","--minimized")))));
   using var sw=new Utf16StringWriter();doc.Save(sw);return sw.ToString();
  }
  sealed class Utf16StringWriter:StringWriter{public override Encoding Encoding=>Encoding.Unicode;}
  // What a registered task really does, read back from its XML: the program, the privilege level and the account.
  internal static (string Exe,bool Highest,string Sid,bool Enabled)? ParseTaskXml(string xml) {
   try {
    var root=XDocument.Parse(xml).Root;XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
    if(root?.Name!=ns+"Task")return null;
    XElement One(XElement parent,string name)=>parent?.Elements(ns+name).SingleOrDefault();
    bool Fields(XElement element,params string[] names)=>element!=null&&element.Elements().All(e=>names.Contains(e.Name.LocalName)&&e.Name.Namespace==ns)&&element.Elements().GroupBy(e=>e.Name).All(g=>g.Count()==1);
    var actions=One(root,"Actions");var principals=One(root,"Principals");var triggers=One(root,"Triggers");var settings=One(root,"Settings");
    if(actions?.Elements().Count()!=1||principals?.Elements().Count()!=1||triggers?.Elements().Count()!=1||settings==null)return null;
    var exec=One(actions,"Exec");var principal=One(principals,"Principal");var trigger=One(triggers,"LogonTrigger");
    if(!Fields(exec,"Command","Arguments","WorkingDirectory")||!Fields(principal,"UserId","LogonType","RunLevel")||!Fields(trigger,"Enabled","UserId","Delay"))return null;
    string sid=One(principal,"UserId")?.Value;
    if(!ValidSid(sid)||One(principal,"LogonType")?.Value!="InteractiveToken"||One(trigger,"UserId")?.Value!=sid||One(trigger,"Delay")?.Value!="PT5S"||
     string.Equals(One(trigger,"Enabled")?.Value,"false",StringComparison.OrdinalIgnoreCase)||actions.Attribute("Context")?.Value!=principal.Attribute("id")?.Value||principal.Attribute("id")==null)return null;
    string command=One(exec,"Command")?.Value?.Trim().Trim('"');string args=One(exec,"Arguments")?.Value?.Trim()??"";
    if(!ValidExePath(command)||!string.IsNullOrWhiteSpace(One(exec,"WorkingDirectory")?.Value))return null;
    bool highest=string.Equals(principal?.Element(ns+"RunLevel")?.Value,"HighestAvailable",StringComparison.OrdinalIgnoreCase);
    bool enabled=!string.Equals(One(settings,"Enabled")?.Value,"false",StringComparison.OrdinalIgnoreCase);
    if(args!="--minimized")return null;   // not the task this program creates
    return (command,highest,sid,enabled);
   }catch{return null;}
  }
  internal readonly record struct TaskLookup(bool Success,string Xml);
  // schtasks uses the same nonzero exit for missing tasks and query failures. The COM
  // lookup distinguishes only FILE_NOT_FOUND; access/service/timeout errors stay unknown.
  static TaskLookup ReadTask() {
   string script="$ErrorActionPreference='Stop';[Console]::OutputEncoding=New-Object Text.UTF8Encoding $false;try{$s=New-Object -ComObject 'Schedule.Service';$s.Connect();$f=$s.GetFolder('\\');try{$t=$f.GetTask('"+TaskName+"');[Console]::Out.Write($t.Xml);exit 0}catch{$e=$_.Exception;while($e.InnerException){$e=$e.InnerException};if($e.HResult -eq -2147024894){exit 2};throw}}catch{exit 1}";
   string encoded=Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
   var r=Run(Program.PowerShell,"-NoProfile -NonInteractive -EncodedCommand "+encoded,20000);
   return r.Code==0&&!string.IsNullOrWhiteSpace(r.Out)?new(true,r.Out):r.Code==2?new(true,null):new(false,null);
  }
  static bool OwnedTask(string xml,string sid) {
   var task=ParseTaskXml(xml);return task!=null&&task.Value.Highest&&task.Value.Sid==sid&&string.Equals(task.Value.Exe,InstalledExe,StringComparison.OrdinalIgnoreCase);
  }
  // Both the protected-copy write and task mutation sit behind this gate. Tests inject
  // only the lookup and mutation callback, without touching Windows tasks or files.
  internal static int WithOwnedTask(string sid,Func<TaskLookup> query,Func<bool,int> mutate) {
   if(!ValidSid(sid))return AccountMismatch;
   TaskLookup read;try{read=query();}catch{return TaskFailed;}
   if(!read.Success)return TaskFailed;
   if(read.Xml!=null&&!OwnedTask(read.Xml,sid))return VerifyFailed;
   return mutate(read.Xml!=null);
  }
  // Pure decision: given the registered task (or none) and the hash of the protected copy and of the running exe, what is the state.
  internal static ElevatedStartupInfo Decide(string sid,(string Exe,bool Highest,string Sid,bool Enabled)? task,string installedHash,string ownHash,bool queryFailed) {
   if(queryFailed)return new(ElevatedStartupState.Unknown,null,"QUERY_FAILED");
   if(task==null)return new(ElevatedStartupState.Off,null,"NO_TASK");
   var t=task.Value;
   if(!t.Highest||!t.Enabled||t.Sid!=sid||!string.Equals(t.Exe,InstalledExe,StringComparison.OrdinalIgnoreCase))return new(ElevatedStartupState.Off,t.Exe,"FOREIGN_OR_INACTIVE_TASK");
   if(installedHash==null)return new(ElevatedStartupState.Stale,t.Exe,"COPY_MISSING");
   if(ownHash!=null&&!string.Equals(installedHash,ownHash,StringComparison.OrdinalIgnoreCase))return new(ElevatedStartupState.Stale,t.Exe,"COPY_DIFFERS");
   return new(ElevatedStartupState.On,t.Exe,"OK");
  }
  static (int Code,string Out) Run(string file,string args,int timeoutMs) {
   var info=Program.SystemModulesOnly(new ProcessStartInfo(file,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8});
   using var p=Process.Start(info)??throw new IOException("START");var o=p.StandardOutput.ReadToEndAsync();var e=p.StandardError.ReadToEndAsync();
   if(!p.WaitForExit(timeoutMs)){try{p.Kill(true);}catch{}return (-1,"");}
   return (p.ExitCode,o.GetAwaiter().GetResult());
  }
  public static ElevatedStartupInfo Query(string ownExe) {
   string sid;try{sid=WindowsIdentity.GetCurrent().User.Value;}catch{return new(ElevatedStartupState.Unknown,null,"IDENTITY");}
   try {
    var r=ReadTask();
    if(!r.Success)return Decide(sid,null,null,null,true);
    if(r.Xml==null)return Decide(sid,null,null,null,false);
    var task=ParseTaskXml(r.Xml);if(task==null)return new(ElevatedStartupState.Off,null,"UNRECOGNISED_TASK");
    string installed=File.Exists(InstalledExe)?Sha256(InstalledExe):null;string own=string.IsNullOrEmpty(ownExe)||!File.Exists(ownExe)?null:Sha256(ownExe);
    return Decide(sid,task,installed,own,false);
   }catch{return Decide(sid,null,null,null,true);}
  }
  // the elevated step (runs in a copy started with "runas": the single UAC confirmation)
  static bool UsersCanWrite(string dir) {
   var rules=new DirectoryInfo(dir).GetAccessControl().GetAccessRules(true,true,typeof(SecurityIdentifier));
   var risky=new[]{"S-1-1-0","S-1-5-11","S-1-5-32-545"};
   const FileSystemRights write=FileSystemRights.WriteData|FileSystemRights.AppendData|FileSystemRights.CreateFiles|FileSystemRights.CreateDirectories|FileSystemRights.Delete|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership|FileSystemRights.DeleteSubdirectoriesAndFiles;
   foreach(FileSystemAccessRule r in rules)if(r.AccessControlType==AccessControlType.Allow&&(r.FileSystemRights&write)!=0&&risky.Contains(r.IdentityReference.Value))return true;
   return false;
  }
  internal static int WithExclusiveFile(string path,Func<int> mutation) {
   FileStream held;
   try {
    if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)return UnsafeFolder;
    held=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
   }catch(IOException){return TaskFailed;}catch(UnauthorizedAccessException){return UnsafeFolder;}
   using(held)return mutation();
  }
  static int WithMutationLock(Func<int> mutation) {
   Directory.CreateDirectory(InstallDir);
   if((File.GetAttributes(InstallDir)&FileAttributes.ReparsePoint)!=0||UsersCanWrite(InstallDir))return UnsafeFolder;
   // The protected lock file stays in place. Deleting a held lock would let a
   // second process open another file and overlap the task/copy transaction.
   return WithExclusiveFile(Path.Combine(InstallDir,"startup.lock"),mutation);
  }
  internal static bool CopyMatches(string path,string expectedHash) {
   try{return !string.IsNullOrEmpty(expectedHash)&&File.Exists(path)&&Sha256(path)==expectedHash;}catch{return false;}
  }
  public static int RunCli(string[] args) {
   try {
    if(args.Length==3&&args[0]=="--elevated-startup-xml"){Console.Out.Write(BuildTaskXml(args[1],args[2]));return Ok;}   // prints the task text only: no system change, used to check it against Windows
    if(args.Length!=2)return Failed;
    bool enable=args[0]=="--elevated-startup-enable";if(!enable&&args[0]!="--elevated-startup-disable")return Failed;
    if(!Program.IsAdmin())return NotAdmin;
    string sid=args[1];string me=WindowsIdentity.GetCurrent().User.Value;
    if(!ValidSid(sid)||sid!=me)return AccountMismatch;   // the confirmation must come from the same account that asked
    return WithOwnedTask(sid,ReadTask,_=>WithMutationLock(()=>enable?Enable(sid):Disable(sid)));
   }catch(Exception ex){Console.Error.WriteLine("ELEVATED_STARTUP_FAILED: "+ex.GetType().Name);return Failed;}
  }
  static int Enable(string sid)=>WithOwnedTask(sid,ReadTask,_=>EnableOwned(sid));
  static int EnableOwned(string sid) {
   string src=Environment.ProcessPath;
   if(!ValidExePath(src)||!File.Exists(src)||!AppIdentity.ProductNames.Contains(AutostartHelper.ProductOf(src)??"",StringComparer.Ordinal))return BadSource;
   string dir=InstallDir;string dest=InstalledExe;
   Directory.CreateDirectory(dir);
   if((File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0||UsersCanWrite(dir))return UnsafeFolder;
   bool selfIsCopy=string.Equals(Path.GetFullPath(src),Path.GetFullPath(dest),StringComparison.OrdinalIgnoreCase);
   string expectedHash=Sha256(src);
   if(!selfIsCopy) {
    string hash=expectedHash;string temp=dest+".new";
    try {
     File.Delete(temp);
     using(var from=new FileStream(src,FileMode.Open,FileAccess.Read,FileShare.Read))using(var to=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){from.CopyTo(to);to.Flush(true);}
     if(Sha256(temp)!=hash)throw new IOException("HASH");
     File.Move(temp,dest,true);   // fails while the protected copy itself is running: reported, nothing else has changed yet
    }catch{try{File.Delete(temp);}catch{}return CopyFailed;}
   }
   string xmlFile=Path.Combine(dir,"logon-task.xml");
   try {
    File.WriteAllText(xmlFile,BuildTaskXml(sid,dest),Encoding.Unicode);   // written inside the protected folder: nobody else can alter it before schtasks reads it
    int create=WithOwnedTask(sid,ReadTask,exists=>Run(SchTasks,"/Create /TN \""+TaskName+"\" /XML \""+xmlFile+"\""+(exists?" /F":""),30000).Code==0?Ok:TaskFailed);
    if(create!=Ok)return create;
   }finally{try{File.Delete(xmlFile);}catch{}}
   var back=ReadTask();
   // An unexpected readback is unconfirmed. Do not delete a task that may have
   // changed independently after creation, especially when the read itself failed.
   return back.Success&&back.Xml!=null&&OwnedTask(back.Xml,sid)&&ParseTaskXml(back.Xml).Value.Enabled&&CopyMatches(dest,expectedHash)?Ok:VerifyFailed;
  }
  static int Disable(string sid)=>WithOwnedTask(sid,ReadTask,exists=>DisableOwned(sid,exists));
  static int DisableOwned(string sid,bool exists) {
   if(!exists)return Ok;   // no task proves no ownership of a leftover protected copy
   int deleted=WithOwnedTask(sid,ReadTask,present=>!present?Ok:Run(SchTasks,"/Delete /TN \""+TaskName+"\" /F",20000).Code==0?Ok:TaskFailed);
   if(deleted!=Ok)return deleted;
   var after=ReadTask();if(!after.Success||after.Xml!=null)return VerifyFailed;
   try{if(File.Exists(InstalledExe))File.Delete(InstalledExe);}catch{/* in use: left for the next enable or a manual cleanup */}
   return Ok;
  }
 }
 // The window's side: asks Windows for the confirmation (runas), waits for the elevated step and reads the task back.
 public sealed class LiveElevatedStartupBackend:IElevatedStartupBackend {
  public Task<ElevatedStartupInfo> QueryAsync(CancellationToken token)=>Task.Run(()=>ElevatedStartup.Query(Environment.ProcessPath),token);
  static async Task<int> Elevated(string verb,CancellationToken token) {
   string sid=WindowsIdentity.GetCurrent().User.Value;
   var info=new ProcessStartInfo(Environment.ProcessPath){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,Arguments=verb+" "+sid};
   try {
    using var p=Process.Start(info);if(p==null)return ElevatedStartup.Failed;
    using var cts=CancellationTokenSource.CreateLinkedTokenSource(token);cts.CancelAfter(TimeSpan.FromSeconds(90));
    try{await p.WaitForExitAsync(cts.Token);}catch(OperationCanceledException){return 1460;}
    return p.ExitCode;
   }catch(System.ComponentModel.Win32Exception ex)when(ex.NativeErrorCode==1223){return ElevatedStartup.Cancelled;}
  }
  public Task<int> EnableAsync(CancellationToken token)=>Elevated("--elevated-startup-enable",token);
  public Task<int> DisableAsync(CancellationToken token)=>Elevated("--elevated-startup-disable",token);
 }
}
