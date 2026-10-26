using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
namespace AntigravityGeminiDns {
 public sealed class ProcessReceipt {
  public int ExitCode = -1; public bool TimedOut, Cancelled, OutputComplete;
  public int Pid; public long ElapsedMilliseconds; public string Stdout="", Stderr="", Error="";
 }
 public static class ToolProcess {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a,string name);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateJobObject(IntPtr job,uint code);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  public static ProcessReceipt Run(string file,string args,string cwd,int timeout,string cancel,string modules) {
   if(timeout<1)throw new ArgumentOutOfRangeException("timeout");
   var r=new ProcessReceipt();var watch=Stopwatch.StartNew();IntPtr job=IntPtr.Zero;
   using(var p=new Process())try {
    job=CreateJobObject(IntPtr.Zero,null);if(job==IntPtr.Zero)throw new Exception("Cannot create owned process job");
    p.StartInfo=new ProcessStartInfo(file,args){WorkingDirectory=cwd,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    if(!String.IsNullOrEmpty(modules))p.StartInfo.EnvironmentVariables["PSModulePath"]=modules;
    p.Start();r.Pid=p.Id;
    if(!AssignProcessToJobObject(job,p.Handle)){try{p.Kill();}catch{}throw new Exception("Cannot assign child to owned process job: "+Marshal.GetLastWin32Error());}
    Task<string> output=p.StandardOutput.ReadToEndAsync(),error=p.StandardError.ReadToEndAsync();
    while(!(p.HasExited&&output.IsCompleted&&error.IsCompleted)) {
     if(!String.IsNullOrEmpty(cancel)&&File.Exists(cancel)){r.Cancelled=true;break;}
     if(watch.ElapsedMilliseconds>=timeout){r.TimedOut=true;break;}
     Thread.Sleep(20);
    }
    // The deadline covers BOTH process exit and pipe closure, including inherited child pipes.
    if(r.TimedOut||r.Cancelled)TerminateJobObject(job,124);
    // Always terminate remaining descendants, even when the root exited successfully.
    TerminateJobObject(job,124);
    p.WaitForExit(1000);
    try{Task.WaitAll(new Task[]{output,error},1000);}catch{}
    r.OutputComplete=output.Status==TaskStatus.RanToCompletion&&error.Status==TaskStatus.RanToCompletion;
    if(output.Status==TaskStatus.RanToCompletion)r.Stdout=output.Result;
    if(error.Status==TaskStatus.RanToCompletion)r.Stderr=error.Result;
    r.ExitCode=r.Cancelled?125:r.TimedOut?124:!r.OutputComplete?126:p.HasExited?p.ExitCode:127;
   }catch(Exception e){r.Error=e.GetType().Name+": "+e.Message;r.ExitCode=127;}
   finally{if(job!=IntPtr.Zero){TerminateJobObject(job,124);CloseHandle(job);}r.ElapsedMilliseconds=watch.ElapsedMilliseconds;}
   return r;
  }
 }
}
