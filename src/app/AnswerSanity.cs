using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace AntigravityGeminiDns {
 // v1.3 answer sanity: an answer for a protected host that points into a network no provider should send a browser to (loopback, link-local,
 // private, shared, multicast, reserved) is BOGUS_ANSWER. Pure and table-driven; Program.PublicIp stays the gate for everything else.
 public static class AnswerSanity {
  public const string Code="BOGUS_ANSWER";

  // network, prefix length. IPv4 and IPv6 are kept apart by address family.
  static readonly (string Net,int Bits)[] V4={("0.0.0.0",8),("127.0.0.0",8),("169.254.0.0",16),("10.0.0.0",8),("172.16.0.0",12),("192.168.0.0",16),("100.64.0.0",10),("224.0.0.0",4),("240.0.0.0",4)};
  static readonly (string Net,int Bits)[] V6={("::",128),("::1",128),("fe80::",10),("fc00::",7),("ff00::",8)};

  public static bool IsBogus(IPAddress ip) {
   if(ip==null)return false;
   var table=ip.AddressFamily==AddressFamily.InterNetwork?V4:ip.AddressFamily==AddressFamily.InterNetworkV6?V6:null;
   if(table==null)return false;
   var b=ip.GetAddressBytes();
   return table.Any(r=>Within(b,IPAddress.Parse(r.Net).GetAddressBytes(),r.Bits));
  }

  public static bool IsBogus(string text)=>IPAddress.TryParse(text??"",out var ip)&&IsBogus(ip);

  // the first `bits` bits of both addresses are equal
  static bool Within(byte[] addr,byte[] net,int bits) {
   if(addr.Length!=net.Length)return false;
   for(int i=0;i<bits/8;i++)if(addr[i]!=net[i])return false;
   int rest=bits%8;
   if(rest==0)return true;
   int mask=(0xFF<<(8-rest))&0xFF;
   return (addr[bits/8]&mask)==(net[bits/8]&mask);
  }
 }
}
