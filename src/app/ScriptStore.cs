using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;

namespace AntigravityGeminiDns {
 // The PowerShell engine ships inside the exe as resources. Before every launch it is materialised into a directory that
 // ordinary users cannot write to (elevated runs) and its SHA-256 is re-verified immediately before PowerShell starts.
 public static class ScriptStore {
  public static readonly string[] ScriptNames={"Engine.ps1","EmergencyRestore.ps1","Integration.ps1","profiles.json"};
  public const string ExeName="AntigravityGeminiDns.exe";
  const string SystemSid="S-1-5-18",AdminsSid="S-1-5-32-544",UsersSid="S-1-5-32-545";

  public sealed class Prepared {
   public string Directory;public string ExePath;public string Fingerprint;public bool Elevated;public string ExeSha256;
   public Dictionary<string,string> Hashes=new Dictionary<string,string>();
   public string Script(string name)=>Path.Combine(Directory,name);
  }

  // The exe that runs is the file the kernel mapped, whatever its name is now; Environment.ProcessPath is the name it was started under. A program of the user can rename the
  // running exe and put another file under the old name before the elevated copy is made: then the bytes read by name are not the program that was approved in the UAC prompt.
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageNameW(IntPtr process,int flags,StringBuilder name,ref int size);
  public static string RunningImagePath(){
   try{var sb=new StringBuilder(2048);int n=sb.Capacity;return QueryFullProcessImageNameW(System.Diagnostics.Process.GetCurrentProcess().Handle,0,sb,ref n)?sb.ToString(0,n):null;}
   catch{return null;}
  }
  internal static Func<string> ImagePathProvider=RunningImagePath;
  // Throws SCRIPT_TAMPERED when the file the kernel mapped does not hold the bytes that were read by name. Unknown image path or an unreadable file: nothing to compare, no refusal.
  public static void CheckRunningImage(byte[] readByName,string imagePath) {
   if(string.IsNullOrEmpty(imagePath))return;
   byte[] image;
   try{using(var fs=new FileStream(imagePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var ms=new MemoryStream()){fs.CopyTo(ms);image=ms.ToArray();}}
   catch(Exception){return;}
   if(!string.Equals(Sha256(image),Sha256(readByName),StringComparison.OrdinalIgnoreCase))
    throw new IOException("SCRIPT_TAMPERED: the program file was replaced after it was started; nothing was staged. Start the program again.");
  }

  public static byte[] Embedded(string name) {
   using(var s=typeof(ScriptStore).Assembly.GetManifestResourceStream(name))
   using(var ms=new MemoryStream()) {
    if(s==null)throw new FileNotFoundException("Embedded resource missing: "+name);
    s.CopyTo(ms);return ms.ToArray();
   }
  }
  public static string Sha256(byte[] data)=>Convert.ToHexString(SHA256.HashData(data));
  public static string Sha256File(string path){using(var fs=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))return Convert.ToHexString(SHA256.HashData(fs));}
  public static Dictionary<string,string> EmbeddedHashes()=>ScriptNames.ToDictionary(n=>n,n=>Sha256(Embedded(n)));
  public static string Fingerprint(Dictionary<string,string> hashes)=>Sha256(Encoding.UTF8.GetBytes(string.Join("\n",ScriptNames.Select(n=>n+":"+hashes[n]))));

  public static string DefaultRoot(bool elevated)=>elevated
   ?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),AppIdentity.StorageName,"bin")
   :Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),AppIdentity.StorageName,"bin");

  // SYSTEM + Administrators: full control. Users: read/execute only. No inheritance from the parent.
  public static DirectorySecurity BuildProtectedAcl(bool withOwner=true) {
   var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
   foreach(string sid in new[]{SystemSid,AdminsSid})acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
   acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(UsersSid),FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
   if(withOwner)acl.SetOwner(new SecurityIdentifier(AdminsSid));
   return acl;
  }
  // The directory is created with its protected rules in the same call: there is no moment when it exists with the parent's inherited, user-writable rights
  // (a program watching %ProgramData% could otherwise slip a file or a link in before the rules were applied). The owner is then set by EnsureProtectedDirectory as before.
  public static void CreateProtected(string path)=>FileSystemAclExtensions.Create(new DirectoryInfo(path),BuildProtectedAcl(false));
  public static void EnsureProtectedDirectory(string path) {
   var di=new DirectoryInfo(path);
   if(!di.Exists)CreateProtected(path);
   di.Refresh();
   if((di.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("BIN_DIR_REPARSE_POINT: "+path);
   var owner=di.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
   if(owner==null||(owner.Value!=SystemSid&&owner.Value!=AdminsSid))throw new IOException("BIN_DIR_UNTRUSTED: owner of "+path+" is not SYSTEM/Administrators; remove it manually");
   di.SetAccessControl(BuildProtectedAcl());
  }

  static void WriteVerified(string path,byte[] data,string expectedHash) {
   string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
   File.WriteAllBytes(tmp,data);
   if(!string.Equals(Sha256File(tmp),expectedHash,StringComparison.OrdinalIgnoreCase)){try{File.Delete(tmp);}catch{}throw new IOException("SCRIPT_WRITE_VERIFY_FAILED: "+Path.GetFileName(path));}
   File.Move(tmp,path,true);
  }

  public static Prepared Prepare(bool elevated,string rootOverride=null,string exeSource=null) {
   string root=rootOverride??DefaultRoot(elevated);
   bool protect=elevated&&rootOverride==null;
   if(protect){
    string state=Path.GetDirectoryName(root);
    EnsureProtectedDirectory(state);EnsureProtectedDirectory(root);
   }else Directory.CreateDirectory(root);
   var p=new Prepared{Elevated=elevated,Hashes=EmbeddedHashes()};
   p.Fingerprint=Fingerprint(p.Hashes);
   string exeSrc=exeSource??Environment.ProcessPath;
   string key=p.Fingerprint;
   byte[] exeBytes=null;
   if(elevated){
    if(string.IsNullOrEmpty(exeSrc)||!File.Exists(exeSrc))throw new FileNotFoundException("Cannot locate the running executable to stage it");
    using(var fs=new FileStream(exeSrc,FileMode.Open,FileAccess.Read,FileShare.Read))using(var ms=new MemoryStream()){fs.CopyTo(ms);exeBytes=ms.ToArray();}
    if(exeSource==null)CheckRunningImage(exeBytes,ImagePathProvider());
    p.ExeSha256=Sha256(exeBytes);key=p.ExeSha256;
   }
   p.Directory=Path.Combine(root,key.Substring(0,16).ToLowerInvariant());
   if(protect)EnsureProtectedDirectory(p.Directory);else Directory.CreateDirectory(p.Directory);
   foreach(string n in ScriptNames) {
    string path=p.Script(n);
    if(!File.Exists(path)||!string.Equals(Sha256File(path),p.Hashes[n],StringComparison.OrdinalIgnoreCase))WriteVerified(path,Embedded(n),p.Hashes[n]);
   }
   if(elevated) {
    // The engine calls back into the exe (--network, --system-dns). An elevated PowerShell must not execute a user-writable file,
    // so the callback target is a hash-verified copy inside the protected directory.
    p.ExePath=Path.Combine(p.Directory,ExeName);
    if(!File.Exists(p.ExePath)||!string.Equals(Sha256File(p.ExePath),p.ExeSha256,StringComparison.OrdinalIgnoreCase)) {
     try{WriteVerified(p.ExePath,exeBytes,p.ExeSha256);}catch(IOException e) when(e.Message.StartsWith("SCRIPT_WRITE_VERIFY_FAILED")){throw new IOException("EXE_COPY_VERIFY_FAILED");}
    }
   } else p.ExePath=exeSrc;
   Verify(p);
   if(protect)Cleanup(root,p.Directory,2);
   return p;
  }

  // Re-hash everything that is about to run. Called immediately before each PowerShell launch.
  public static void Verify(Prepared p) {
   foreach(string n in ScriptNames) {
    string path=p.Script(n);
    if(!File.Exists(path)||!string.Equals(Sha256File(path),p.Hashes[n],StringComparison.OrdinalIgnoreCase))throw new IOException("SCRIPT_TAMPERED: "+n);
   }
   if(p.Elevated&&(!File.Exists(p.ExePath)||!string.Equals(Sha256File(p.ExePath),p.ExeSha256,StringComparison.OrdinalIgnoreCase)))throw new IOException("SCRIPT_TAMPERED: "+ExeName);
  }

  static void Cleanup(string root,string keep,int retain) {
   try {
    var old=new DirectoryInfo(root).GetDirectories().Where(d=>!string.Equals(d.FullName,keep,StringComparison.OrdinalIgnoreCase)&&System.Text.RegularExpressions.Regex.IsMatch(d.Name,"^[0-9a-f]{16}$")).OrderByDescending(d=>d.LastWriteTimeUtc).Skip(retain-1);
    foreach(var d in old)try{d.Delete(true);}catch{}
   }catch{}
  }

  public static void Export(string directory) {
   Directory.CreateDirectory(directory);var hashes=EmbeddedHashes();var sums=new StringBuilder();
   foreach(string n in ScriptNames){File.WriteAllBytes(Path.Combine(directory,n),Embedded(n));sums.AppendLine(hashes[n]+"  "+n);}
   File.WriteAllText(Path.Combine(directory,"SHA256SUMS.txt"),sums.ToString(),new UTF8Encoding(false));
  }
 }
}
