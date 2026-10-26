using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 public static partial class OfflineTests {
  sealed class FakeElevatedBackend:IElevatedStartupBackend {
   public ElevatedStartupState State=ElevatedStartupState.Off;public int EnableResult=ElevatedStartup.Ok,DisableResult=ElevatedStartup.Ok,Enables,Disables,Queries;
   public Task<ElevatedStartupInfo> QueryAsync(CancellationToken token){Queries++;return Task.FromResult(new ElevatedStartupInfo(State,null,"FAKE"));}
   public Task<int> EnableAsync(CancellationToken token){Enables++;if(EnableResult==ElevatedStartup.Ok)State=ElevatedStartupState.On;return Task.FromResult(EnableResult);}
   public Task<int> DisableAsync(CancellationToken token){Disables++;if(DisableResult==ElevatedStartup.Ok)State=ElevatedStartupState.Off;return Task.FromResult(DisableResult);}
  }
  // The administrator-startup feature: the task XML says what was agreed, the state is decided from what is really registered, the elevated step refuses
  // foreign accounts, and the window never reports a change that Windows did not confirm. Real task creation needs administrator rights: NOT RUN here.
  static void RunElevatedStartupTests() {
   const string sid="S-1-5-21-1111111111-2222222222-3333333333-1001";string exe=ElevatedStartup.InstalledExe;
   string xml=ElevatedStartup.BuildTaskXml(sid,exe);
   var parsed=ElevatedStartup.ParseTaskXml(xml);
   Check(parsed!=null&&parsed.Value.Highest&&parsed.Value.Enabled&&parsed.Value.Sid==sid&&string.Equals(parsed.Value.Exe,exe,StringComparison.OrdinalIgnoreCase),"elevated startup: the task XML round-trips with the highest run level, the account and the protected exe");
   Check(xml.Contains("LogonTrigger")&&xml.Contains("InteractiveToken")&&xml.Contains("--minimized")&&xml.Contains("PT0S")&&!xml.Contains("SYSTEM"),"elevated startup: the task is a logon task of the user, interactive, started minimised, never as SYSTEM");
   var amp=ElevatedStartup.ParseTaskXml(ElevatedStartup.BuildTaskXml(sid,@"C:\Program Files\A&B <x>\AntigravityGeminiDns.exe".Replace("<x>","x")));
   Check(amp!=null&&amp.Value.Exe.Contains("A&B"),"elevated startup: XML special characters in the path are escaped, not interpreted");
   foreach(string bad in new[]{"AntigravityGeminiDns.exe",@"C:\x\a.bat",@"C:\x\a"".exe","",null,"C:\\x\\a\n.exe"})
    Check(!ElevatedStartup.ValidExePath(bad),"elevated startup: path refused: "+(bad??"null").Replace("\n","\\n"));
   foreach(string bad in new[]{"S-1-5-18","S-1-5-32-544","garbage","","S-1-5-21-1-2-3-4\"&calc"}) {
    bool refused;try{ElevatedStartup.BuildTaskXml(bad,exe);refused=false;}catch(ArgumentException){refused=true;}
    Check(refused,"elevated startup: account refused: "+bad);
   }
   Check(ElevatedStartup.ParseTaskXml(xml.Replace("--minimized","--evil"))==null,"elevated startup: a task with other arguments is not recognised as ours");
   Check(ElevatedStartup.ParseTaskXml(xml.Replace("</Exec>","</Exec><Exec><Command>x.exe</Command></Exec>"))==null,"elevated startup: a task with a second action is not recognised as ours");
   Check(ElevatedStartup.ParseTaskXml("not xml")==null,"elevated startup: unreadable task text is unknown, not trusted");
   Check(ElevatedStartup.ParseTaskXml(xml.Replace("HighestAvailable","LeastPrivilege")).Value.Highest==false,"elevated startup: a lower run level is read as such");
   var ok=(exe,true,sid,true);
   Check(ElevatedStartup.Decide(sid,ok,"AA","AA",false).State==ElevatedStartupState.On,"elevated startup: matching task and copy = On");
   Check(ElevatedStartup.Decide(sid,ok,"AA","BB",false).State==ElevatedStartupState.Stale,"elevated startup: a protected copy that differs from this exe = Stale (update needs one confirmation)");
   Check(ElevatedStartup.Decide(sid,ok,null,"BB",false).State==ElevatedStartupState.Stale,"elevated startup: a registered task whose copy is gone = Stale");
   Check(ElevatedStartup.Decide(sid,null,"AA","AA",false).State==ElevatedStartupState.Off,"elevated startup: no task = Off");
   Check(ElevatedStartup.Decide(sid,(@"C:\Users\x\Downloads\AntigravityGeminiDns.exe",true,sid,true),"AA","AA",false).State==ElevatedStartupState.Off,"elevated startup: a task that points at a user-writable exe is never treated as ours");
   Check(ElevatedStartup.Decide(sid,(exe,false,sid,true),"AA","AA",false).State==ElevatedStartupState.Off&&ElevatedStartup.Decide(sid,(exe,true,"S-1-5-21-9-9-9-9",true),"AA","AA",false).State==ElevatedStartupState.Off&&ElevatedStartup.Decide(sid,(exe,true,sid,false),"AA","AA",false).State==ElevatedStartupState.Off,"elevated startup: a lower level, another account or a disabled task = Off");
   Check(ElevatedStartup.Decide(sid,null,null,null,true).State==ElevatedStartupState.Unknown,"elevated startup: a query that failed is Unknown, not Off");
   // Exercise the very gate used before the protected EXE write and again before
   // task creation/deletion. The callback counts mutations; it never reaches Windows.
   string foreignSid="S-1-5-21-9-9-9-9";
   string[] foreignTasks={xml.Replace(sid,foreignSid),xml.Replace(exe,@"C:\Other\foreign.exe"),xml.Replace("HighestAvailable","LeastPrivilege"),
    xml.Replace("</Actions>","<ComHandler><ClassId>{00000000-0000-0000-0000-000000000000}</ClassId></ComHandler></Actions>"),
    xml.Replace("</Principals>","<Principal id=\"Other\"><UserId>"+foreignSid+"</UserId></Principal></Principals>"),
    xml.Replace("</Triggers>","<BootTrigger /></Triggers>"),xml.Replace("<LogonTrigger>","<BootTrigger>").Replace("</LogonTrigger>","</BootTrigger>"),
    xml.Replace("Context=\"Author\"","Context=\"Other\""),xml.Replace("</Exec>","<WorkingDirectory>C:\\Other</WorkingDirectory></Exec>"),
    xml.Replace("--minimized","--evil"),"not xml"};
   foreach(string foreign in foreignTasks){
    int writes=0;int refused=ElevatedStartup.WithOwnedTask(sid,()=>new(true,foreign),_=>{writes++;return ElevatedStartup.Ok;});
    Check(refused==ElevatedStartup.VerifyFailed&&writes==0,"elevated startup: foreign SID/path/actions/principals/triggers/arguments never reach protected copy or task mutation");
   }
   int unknownWrites=0;
   Check(ElevatedStartup.WithOwnedTask(sid,()=>new(false,null),_=>{unknownWrites++;return ElevatedStartup.Ok;})==ElevatedStartup.TaskFailed&&unknownWrites==0,"elevated startup: failed task query is not absence and never reaches mutation");
   Check(ElevatedStartup.WithOwnedTask(sid,()=>throw new System.IO.IOException("query"),_=>{unknownWrites++;return ElevatedStartup.Ok;})==ElevatedStartup.TaskFailed&&unknownWrites==0,"elevated startup: query exception never reaches mutation");
   bool? existed=null;
   Check(ElevatedStartup.WithOwnedTask(sid,()=>new(true,null),present=>{existed=present;return ElevatedStartup.Ok;})==ElevatedStartup.Ok&&existed==false,"elevated startup: only confirmed absence permits a new non-force creation");
   Check(ElevatedStartup.WithOwnedTask(sid,()=>new(true,xml),present=>{existed=present;return ElevatedStartup.Ok;})==ElevatedStartup.Ok&&existed==true,"elevated startup: exact owned task permits replacement or removal");
   int racedWrites=0;var changed=new System.Collections.Generic.Queue<ElevatedStartup.TaskLookup>(new[]{new ElevatedStartup.TaskLookup(true,xml),new ElevatedStartup.TaskLookup(true,xml.Replace(sid,foreignSid))});
   int raced=ElevatedStartup.WithOwnedTask(sid,()=>changed.Dequeue(),_=>ElevatedStartup.WithOwnedTask(sid,()=>changed.Dequeue(),present=>{racedWrites++;return ElevatedStartup.Ok;}));
   Check(raced==ElevatedStartup.VerifyFailed&&racedWrites==0,"elevated startup: a task changed to foreign before create/delete is preserved");
   string lockDirectory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"agdns-startup-lock-"+Guid.NewGuid().ToString("N"));
   System.IO.Directory.CreateDirectory(lockDirectory);
   try {
    string lockPath=System.IO.Path.Combine(lockDirectory,"startup.lock"),copy=System.IO.Path.Combine(lockDirectory,"copy.exe");int overlappingWrites=0;
    int first=ElevatedStartup.WithExclusiveFile(lockPath,()=>{
     int concurrent=Task.Run(()=>ElevatedStartup.WithExclusiveFile(lockPath,()=>{Interlocked.Increment(ref overlappingWrites);return ElevatedStartup.Ok;})).GetAwaiter().GetResult();
     Check(concurrent==ElevatedStartup.TaskFailed&&overlappingWrites==0,"elevated startup: overlapping enable/disable cannot enter the copy/task transaction");
     return ElevatedStartup.Ok;
    });
    Check(first==ElevatedStartup.Ok&&ElevatedStartup.WithExclusiveFile(lockPath,()=>{overlappingWrites++;return ElevatedStartup.Ok;})==ElevatedStartup.Ok&&overlappingWrites==1,"elevated startup: lock releases after transaction and the next operation can enter");
    try{ElevatedStartup.WithExclusiveFile(lockPath,()=>throw new System.IO.IOException("fixture"));}catch(System.IO.IOException){}
    Check(ElevatedStartup.WithExclusiveFile(lockPath,()=>ElevatedStartup.Ok)==ElevatedStartup.Ok,"elevated startup: exception releases exclusive transaction lock");
    System.IO.File.WriteAllText(copy,"candidate");string expected=ElevatedStartup.Sha256(copy);
    Check(ElevatedStartup.CopyMatches(copy,expected),"elevated startup: final copy readback checks exact content hash");
    System.IO.File.WriteAllText(copy,"replaced");Check(!ElevatedStartup.CopyMatches(copy,expected),"elevated startup: replacement cannot pass final copy verification");
    System.IO.File.Delete(copy);Check(!ElevatedStartup.CopyMatches(copy,expected),"elevated startup: missing protected EXE cannot pass final verification");
   }finally{System.IO.Directory.Delete(lockDirectory,true);}
   // the elevated step itself: whatever the test process's rights, a foreign account or a malformed call must change nothing
   int code0=ElevatedStartup.RunCli(new[]{"--elevated-startup-enable","S-1-5-21-1-2-3-4"});int code1=ElevatedStartup.RunCli(new[]{"--elevated-startup-disable","S-1-5-21-1-2-3-4"});
   Check((code0==ElevatedStartup.NotAdmin||code0==ElevatedStartup.AccountMismatch)&&(code1==ElevatedStartup.NotAdmin||code1==ElevatedStartup.AccountMismatch),"elevated startup: the elevated step refuses without administrator rights and for another account");
   Check(ElevatedStartup.RunCli(new[]{"--elevated-startup-enable"})==ElevatedStartup.Failed&&ElevatedStartup.RunCli(new[]{"--elevated-startup-other","x"})==ElevatedStartup.Failed,"elevated startup: malformed calls are refused");
   Check(!ElevatedStartup.InstallDir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),StringComparison.OrdinalIgnoreCase)&&ElevatedStartup.InstalledExe.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),StringComparison.OrdinalIgnoreCase),"elevated startup: the protected copy lives under Program Files, not in a user-writable folder");
   // the window
   const string isoKey=@"Software\AgDnsTest\ElevatedUi";string realKey=AutostartHelper.KeyName;bool en0=L.En;
   Microsoft.Win32.Registry.CurrentUser.CreateSubKey(isoKey).Dispose();AutostartHelper.KeyName=isoKey;
   try {
    foreach(bool en in new[]{false,true}) {
     L.En=en;
     using var form=new MainForm(true);
     form.Show();Application.DoEvents();form.SelectTab(2);Application.DoEvents();
     var fake=new FakeElevatedBackend();string notice=null;form.Notice=t=>notice=t;form.ElevatedBackend=fake;
     form.ApplyElevatedInfo(new ElevatedStartupInfo(ElevatedStartupState.Off,null,"FAKE"));
     Check(!form.ElevatedOn&&form.ElevatedSubtitle==(en?"Confirm once in Windows":"Один раз подтвердить в Windows"),"elevated startup window: off by default, says what one confirmation does "+en);
     var rows=form.SettingsRows;Check(rows.All(r=>r.Width>0&&r.Height>0)&&rows[7].Top>=rows[1].Bottom-1&&rows[3].Top>=rows[7].Bottom-1,"elevated startup window: the new row sits between autostart and the tray row without overlap "+en);
     AutostartHelper.SetEnabled(true,Environment.ProcessPath);
     form.SwitchElevatedAsync(true,false).GetAwaiter().GetResult();
     Check(fake.Enables==1&&form.ElevatedOn&&form.AutostartOn&&notice==null&&!AutostartHelper.IsEnabled(Environment.ProcessPath),"elevated startup window: on = one confirmation, the plain Run entry is removed so two copies cannot start "+en);
     fake.State=ElevatedStartupState.Stale;form.ApplyElevatedInfo(new ElevatedStartupInfo(ElevatedStartupState.Stale,null,"FAKE"));
     Check(!form.ElevatedOn&&form.AutostartOn&&form.ElevatedSubtitle==(en?"Copy is out of date: switch on to update":"Копия устарела: включите, чтобы обновить"),"elevated startup window: a stale copy shows off with the update hint, autostart stays on "+en);
     fake.State=ElevatedStartupState.Off;fake.EnableResult=ElevatedStartup.Cancelled;form.ApplyElevatedInfo(new ElevatedStartupInfo(ElevatedStartupState.Off,null,"FAKE"));
     form.SwitchElevatedAsync(true,false).GetAwaiter().GetResult();
     Check(!form.ElevatedOn&&notice==(en?"The Windows confirmation was cancelled: nothing was changed.":"Подтверждение Windows отменено: ничего не изменено.")&&!AutostartHelper.IsEnabled(Environment.ProcessPath),"elevated startup window: a refused confirmation changes nothing and says so "+en);
     notice=null;fake.EnableResult=ElevatedStartup.TaskFailed;form.SwitchElevatedAsync(true,false).GetAwaiter().GetResult();
     Check(!form.ElevatedOn&&notice!=null&&fake.State==ElevatedStartupState.Off,"elevated startup window: a failed task creation is reported and the switch stays off "+en);
     notice=null;fake.EnableResult=ElevatedStartup.Ok;form.SwitchElevatedAsync(true,false).GetAwaiter().GetResult();
     fake.DisableResult=ElevatedStartup.TaskFailed;notice=null;form.SwitchElevatedAsync(false,false).GetAwaiter().GetResult();
     Check(form.ElevatedOn&&notice!=null,"elevated startup window: a task that could not be deleted keeps the switch on and says so (rollback is retried by the person) "+en);
     fake.DisableResult=ElevatedStartup.Ok;notice=null;form.SwitchElevatedAsync(false,false).GetAwaiter().GetResult();
     Check(!form.ElevatedOn&&fake.Disables==2&&notice==null&&AutostartHelper.IsEnabled(Environment.ProcessPath)&&form.AutostartOn,"elevated startup window: off deletes the task and restores the plain autostart entry "+en);
     fake.State=ElevatedStartupState.On;form.ApplyElevatedInfo(new ElevatedStartupInfo(ElevatedStartupState.On,null,"FAKE"));
     form.ToggleAutostartForTest(false);Application.DoEvents();
     Check(fake.Disables==3&&!form.ElevatedOn&&!form.AutostartOn&&!AutostartHelper.IsEnabled(Environment.ProcessPath),"elevated startup window: switching autostart off removes the task too, nothing starts at logon "+en);
     var slow=new FakeElevatedBackend();form.ElevatedBackend=null;form.ApplyElevatedInfo(new ElevatedStartupInfo(ElevatedStartupState.Off,null,"FAKE"));
     form.SwitchElevatedAsync(true,false).GetAwaiter().GetResult();
     Check(!form.ElevatedOn&&slow.Enables==0,"elevated startup window: without a backend (smoke mode) nothing is attempted "+en);
    }
   }finally{L.En=en0;AutostartHelper.KeyName=realKey;try{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\AgDnsTest",false);}catch{}}
  }
 }
}
