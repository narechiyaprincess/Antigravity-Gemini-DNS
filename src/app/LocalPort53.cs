using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;

namespace AntigravityGeminiDns {
 // Who listens on local port 53 (v1.1). Read-only: the iphlpapi owner-PID tables, no CIM/WMI, so it answers even when WMI does not.
 // The bridge itself does not bind port 53 (it is NRPT + DoH); a foreign listener is reported before the bridge starts, never treated as fatal.
 public static class LocalPort53 {
  public sealed class Holder {
   public string Protocol{get;set;}
   public string Address{get;set;}
   public int Port{get;set;}
   public int Pid{get;set;}
   public string Process{get;set;}
  }

  const int AF_INET=2,AF_INET6=23;
  const int TCP_TABLE_OWNER_PID_LISTENER=3,UDP_TABLE_OWNER_PID=1;
  const uint ERROR_INSUFFICIENT_BUFFER=122;

  [DllImport("iphlpapi.dll",SetLastError=true)]
  static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool order,int af,int tableClass,uint reserved);
  [DllImport("iphlpapi.dll",SetLastError=true)]
  static extern uint GetExtendedUdpTable(IntPtr table,ref int size,bool order,int af,int tableClass,uint reserved);

  // Everything listening on the port (IPv4 and IPv6, TCP and UDP). Empty = the port is free as far as this user can see.
  public static Holder[] Find(int port=53) {
   var found=new List<Holder>();
   foreach(int af in new[]{AF_INET,AF_INET6}){
    found.AddRange(Parse(ReadTable(true,af),true,af,port));
    found.AddRange(Parse(ReadTable(false,af),false,af,port));
   }
   var names=new Dictionary<int,string>();
   foreach(var h in found){
    if(!names.TryGetValue(h.Pid,out var name)){name=ProcessName(h.Pid);names[h.Pid]=name;}
    h.Process=name;
   }
   return found.Distinct(new HolderComparer()).ToArray();
  }

  // One line for the log: "example.exe (PID 1234), TCP 127.0.0.1:53".
  public static string Describe(Holder[] holders) =>string.Join("; ",holders.Select(h=>h.Process+" (PID "+h.Pid+"), "+h.Protocol+" "+(h.Address.Contains(":")?"["+h.Address+"]":h.Address)+":"+h.Port));

  // Owner-PID table in the raw form the OS returns. Null when the table cannot be read (for example IPv6 switched off).
  static byte[] ReadTable(bool tcp,int af) {
   int size=0;IntPtr buf=IntPtr.Zero;
   try{
    for(int attempt=0;attempt<3;attempt++){
     if(buf!=IntPtr.Zero)Marshal.FreeHGlobal(buf);
     buf=Marshal.AllocHGlobal(Math.Max(size,4));
     uint rc=tcp?GetExtendedTcpTable(buf,ref size,false,af,TCP_TABLE_OWNER_PID_LISTENER,0):GetExtendedUdpTable(buf,ref size,false,af,UDP_TABLE_OWNER_PID,0);
     if(rc==0){var bytes=new byte[size];Marshal.Copy(buf,bytes,0,size);return bytes;}
     if(rc!=ERROR_INSUFFICIENT_BUFFER)return null;
    }
    return null;
   }catch{return null;}
   finally{if(buf!=IntPtr.Zero)Marshal.FreeHGlobal(buf);}
  }

  // Row layouts (all little-endian DWORDs; a port is the low 16 bits of its DWORD in network byte order):
  //   TCP4 24 bytes: state, localAddr, localPort, remoteAddr, remotePort, pid      (addr at 4, port at 8, pid at 20)
  //   TCP6 56 bytes: localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid   (addr 0, port 20, pid 52)
  //   UDP4 12 bytes: localAddr, localPort, pid                                     (addr 0, port 4, pid 8)
  //   UDP6 28 bytes: localAddr[16], scope, localPort, pid                          (addr 0, port 20, pid 24)
  // Pure function: the self-test feeds it hand-made tables.
  public static Holder[] Parse(byte[] table,bool tcp,int af,int port) {
   var result=new List<Holder>();
   if(table==null||table.Length<4)return result.ToArray();
   int count=BitConverter.ToInt32(table,0);
   bool v4=af==AF_INET;
   int rowSize=tcp?(v4?24:56):(v4?12:28);
   int addrOff=tcp?(v4?4:0):0;
   int portOff=tcp?(v4?8:20):(v4?4:20);
   int pidOff=tcp?(v4?20:52):(v4?8:24);
   int addrLen=v4?4:16;
   for(int i=0;i<count;i++){
    int off=4+i*rowSize;
    if(off+rowSize>table.Length)break;
    int raw=BitConverter.ToInt32(table,off+portOff);
    int p=((raw&0xFF)<<8)|((raw>>8)&0xFF);
    if(p!=port)continue;
    var addr=new byte[addrLen];Array.Copy(table,off+addrOff,addr,0,addrLen);
    result.Add(new Holder{Protocol=tcp?"TCP":"UDP",Address=new IPAddress(addr).ToString(),Port=p,Pid=BitConverter.ToInt32(table,off+pidOff)});
   }
   return result.ToArray();
  }

  static string ProcessName(int pid) {
   if(pid==0)return "System Idle Process";
   if(pid==4)return "System";
   try{
    using(var p=Process.GetProcessById(pid)){
     try{return p.MainModule?.ModuleName??p.ProcessName;}catch{return p.ProcessName;}   // protected processes do not expose their module
    }
   }catch{return "?";}
  }

  sealed class HolderComparer:IEqualityComparer<Holder> {
   public bool Equals(Holder x,Holder y)=>x.Protocol==y.Protocol&&x.Address==y.Address&&x.Port==y.Port&&x.Pid==y.Pid;
   public int GetHashCode(Holder h)=>HashCode.Combine(h.Protocol,h.Address,h.Port,h.Pid);
  }
 }
}
