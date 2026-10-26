using System;
using System.Diagnostics;
using System.IO;

namespace AntigravityGeminiDns {
 // Timing marks for measuring start-up and operations. Off unless the AGDNS_PERF environment variable names a file; then every mark is one line
 // "<ms since the process started>\t<name>". Nothing is read, sent or kept otherwise.
 internal static class PerfLog {
  static readonly string path=Environment.GetEnvironmentVariable("AGDNS_PERF");
  static readonly DateTime started=Start();
  static readonly object gate=new object();
  static DateTime Start(){if(string.IsNullOrEmpty(path))return default;try{return Process.GetCurrentProcess().StartTime;}catch{return DateTime.Now;}}
  internal static bool On=>!string.IsNullOrEmpty(path);
  internal static void Mark(string name){
   if(!On)return;
   try{lock(gate)File.AppendAllText(path,((long)(DateTime.Now-started).TotalMilliseconds)+"\t"+name+Environment.NewLine);}catch{}
  }
 }
}
