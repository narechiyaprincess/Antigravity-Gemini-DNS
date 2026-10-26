using System;

namespace AntigravityGeminiDns {
 // v1.1 Reliability: Crash Guard decisions and the local port 53 owner tables. Pure functions only; nothing here touches the network or the system.
 public static partial class OfflineTests {
  static void RunReliabilityTests() {
   const string Applying="{\"Phase\":\"Applying\"}",Active="{\"Phase\":\"Active\"}",Restored="{\"Phase\":\"Restored\"}",Failed="{\"Phase\":\"Failed\"}";
   const string Unclean="{\"CleanShutdown\":false}",Clean="{\"CleanShutdown\":true}";

   // Crash Guard: only an unclean previous session with a journal in the middle of a change is an interrupted change.
   var none=CrashGuard.Evaluate(null,null);
   Check(!none.PreviousUnclean&&none.Kind==CrashGuard.Kind.None,"crash guard: first start on the account has no previous crash");
   Check(CrashGuard.Evaluate(Clean,Applying).Kind==CrashGuard.Kind.None,"crash guard: a clean previous exit never shows a notice, even mid-change");
   Check(CrashGuard.Evaluate(Unclean,Applying).Kind==CrashGuard.Kind.InterruptedChange,"crash guard: a crash during Apply is an interrupted change");
   Check(CrashGuard.Evaluate(Unclean,Failed).Kind==CrashGuard.Kind.InterruptedChange,"crash guard: a crash leaving a Failed journal is an interrupted change");
   Check(CrashGuard.Evaluate(Unclean,Active).Kind==CrashGuard.Kind.BridgeActive,"crash guard: a crash while the bridge is Active is reported as an active bridge, not as a failure");
   Check(CrashGuard.Evaluate(Unclean,Restored).Kind==CrashGuard.Kind.None,"crash guard: a crash after a finished restore needs nothing");
   Check(CrashGuard.Evaluate(Unclean,null).Kind==CrashGuard.Kind.None,"crash guard: a crash without any journal needs nothing");
   var garbled=CrashGuard.Evaluate("unreadable",Applying);
   Check(garbled.PreviousUnclean&&garbled.Kind==CrashGuard.Kind.InterruptedChange,"crash guard: an unreadable marker counts as a dirty exit");
   Check(CrashGuard.IsUnclean(Unclean)&&!CrashGuard.IsUnclean(Clean)&&!CrashGuard.IsUnclean(null),"crash guard: marker reading (false, true, absent)");

   // Port 53 owner tables, built by hand in the OS layout.
   var tcp4=Table(1,24,r=>{Put(r,0,2);Put(r,4,new byte[]{127,0,0,1});Put(r,8,PortBytes(53));Put(r,20,1234);});
   var h=LocalPort53.Parse(tcp4,true,2,53);
   Check(h.Length==1&&h[0].Protocol=="TCP"&&h[0].Address=="127.0.0.1"&&h[0].Pid==1234&&h[0].Port==53,"port 53: a TCP IPv4 listener gives its address, port and owner PID");
   var tcp4Other=Table(1,24,r=>{Put(r,4,new byte[]{0,0,0,0});Put(r,8,PortBytes(80));Put(r,20,99);});
   Check(LocalPort53.Parse(tcp4Other,true,2,53).Length==0,"port 53: a listener on another port is not reported");
   var udp4=Table(1,12,r=>{Put(r,0,new byte[]{0,0,0,0});Put(r,4,PortBytes(53));Put(r,8,4321);});
   var u=LocalPort53.Parse(udp4,false,2,53);
   Check(u.Length==1&&u[0].Protocol=="UDP"&&u[0].Address=="0.0.0.0"&&u[0].Pid==4321,"port 53: a UDP IPv4 socket gives its owner PID");
   var udp6=Table(1,28,r=>{var a=new byte[16];a[15]=1;Put(r,0,a);Put(r,20,PortBytes(53));Put(r,24,77);});
   var u6=LocalPort53.Parse(udp6,false,23,53);
   Check(u6.Length==1&&u6[0].Address=="::1"&&u6[0].Pid==77,"port 53: a UDP IPv6 socket is read with its own row layout");
   var tcp6=Table(1,56,r=>{var a=new byte[16];a[15]=1;Put(r,0,a);Put(r,20,PortBytes(53));Put(r,52,555);});
   var t6=LocalPort53.Parse(tcp6,true,23,53);
   Check(t6.Length==1&&t6[0].Address=="::1"&&t6[0].Pid==555,"port 53: a TCP IPv6 listener is read with its own row layout");
   Check(LocalPort53.Parse(null,true,2,53).Length==0&&LocalPort53.Parse(new byte[]{1,0},false,2,53).Length==0,"port 53: a missing or truncated table reads as free");
   var truncated=new byte[4+24];Array.Copy(BitConverter.GetBytes(3),0,truncated,0,4);Put(truncated,4+8,PortBytes(53));   // header promises 3 rows, the buffer holds 1: no read past the end
   Check(LocalPort53.Parse(truncated,true,2,53).Length==1,"port 53: a row count larger than the buffer stops at the buffer end");
   Check(LocalPort53.Describe(h).Contains("PID 1234")&&LocalPort53.Describe(h).Contains("127.0.0.1:53"),"port 53: the log line names the process and the PID");
  }

  // header (row count) plus rows; fill writes one row, identical for every row, so the tests keep to a single row
  static byte[] Table(int count,int rowSize,Action<byte[]> fill) {
   var b=new byte[4+count*rowSize];
   Array.Copy(BitConverter.GetBytes(count),0,b,0,4);
   for(int i=0;i<count;i++){var row=new byte[rowSize];fill(row);Array.Copy(row,0,b,4+i*rowSize,rowSize);}
   return b;
  }
  static void Put(byte[] row,int off,int value) =>Array.Copy(BitConverter.GetBytes(value),0,row,off,4);
  static void Put(byte[] row,int off,byte[] value) =>Array.Copy(value,0,row,off,value.Length);
  // a port as the OS stores it: the two bytes in network order, low 16 bits of the DWORD
  static byte[] PortBytes(int port) =>new byte[]{(byte)(port>>8),(byte)(port&0xFF),0,0};
 }
}
