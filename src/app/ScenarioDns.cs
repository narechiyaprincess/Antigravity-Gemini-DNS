using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace AntigravityGeminiDns {
 public sealed record ScenarioSvcb(string Owner,int Priority,string Target,string Meaning,bool Compatible,
  string[] Alpn,int? Port,string[] Ipv4Hints,string[] Ipv6Hints,string Ech,Dictionary<int,string> Parameters);
 public sealed record ScenarioDnsResult(int Rcode,ScenarioSvcb[] Records,string Limit);
 // Observation only. No alias/hint following, port changes, ECH execution or route changes.
 public static class ScenarioDns {
  static int U16(byte[] b,int p) {if(p<0||p+2>b.Length)throw new FormatException("DNS_BOUNDS");return b[p]*256+b[p+1];}
  static string Name(byte[] b,ref int pos,int end,bool compressed) {
   int p=pos;bool jumped=false;int length=0;var seen=new HashSet<int>();var labels=new List<string>();
   while(true) {
    if(p<0||p>=end||!seen.Add(p)||seen.Count>128)throw new FormatException("DNS_NAME_BOUNDS_OR_LOOP");int n=b[p++];
    if(n==0){if(!jumped)pos=p;return labels.Count==0?".":string.Join(".",labels);}
    if((n&0xc0)==0xc0) {
     if(!compressed||p>=end)throw new FormatException("DNS_COMPRESSION_FORBIDDEN_OR_TRUNCATED");
     int target=((n&63)<<8)|b[p++];if(!jumped)pos=p;jumped=true;p=target;continue;
    }
    if(n>63||p+n>end||(length+=n+1)>254)throw new FormatException("DNS_NAME_LENGTH");
    for(int i=0;i<n;i++)if(b[p+i]<33||b[p+i]>126||b[p+i]=='.')throw new FormatException("DNS_NAME_ENCODING");
    labels.Add(Encoding.ASCII.GetString(b,p,n));p+=n;
   }
  }
  public static byte[] Query(string host,int type) {
   ScenarioProbes.RequireHost(host);if(type!=1&&type!=28&&type!=64&&type!=65)throw new ArgumentException("DNS type");
   var q=Wire.Query(host,type==28?28:1,System.Security.Cryptography.RandomNumberGenerator.GetInt32(65536));q[q.Length-3]=(byte)type;return q;
  }
  public static ScenarioSvcb ParseRecord(string owner,byte[] rdata) {
   if(rdata==null||rdata.Length<3||rdata.Length>65535)throw new FormatException("SVCB_RDATA_LENGTH");
   int priority=U16(rdata,0),p=2;string target=Name(rdata,ref p,rdata.Length,false);var values=new Dictionary<int,byte[]>();int last=-1;
   while(p<rdata.Length) {
    int key=U16(rdata,p),len=U16(rdata,p+2);p+=4;
    if(key<=last||p+len>rdata.Length)throw new FormatException("SVCB_KEY_ORDER_OR_LENGTH");last=key;
    values.Add(key,rdata.AsSpan(p,len).ToArray());p+=len;
   }
   var opaque=values.ToDictionary(v=>v.Key,v=>Convert.ToBase64String(v.Value));
   if(priority==0)return new(owner,priority,target,target=="."?"ALIAS_SERVICE_UNAVAILABLE":"ALIAS_NOT_FOLLOWED",false,Array.Empty<string>(),null,Array.Empty<string>(),Array.Empty<string>(),"PARAMETERS_IGNORED_ALIAS_MODE",opaque);
   bool compatible=true;var alpn=new List<string>();int? port=null;var v4=new List<string>();var v6=new List<string>();
   foreach(var entry in values) {
    byte[] b=entry.Value;
    switch(entry.Key) {
     case 0:
      if(b.Length==0||b.Length%2!=0)throw new FormatException("SVCB_MANDATORY_LENGTH");int previous=0;
      for(int i=0;i<b.Length;i+=2) {int key=U16(b,i);if(key<=previous||!values.ContainsKey(key))throw new FormatException("SVCB_MANDATORY_MISSING_OR_ORDER");previous=key;
       // key 5 is observed opaquely; implementing its mandatory semantics is not claimed.
       if(key>6||key==5)compatible=false;
      }break;
     case 1:
      if(b.Length==0)throw new FormatException("SVCB_ALPN_EMPTY");
      for(int i=0;i<b.Length;) {int n=b[i++];if(n==0||i+n>b.Length)throw new FormatException("SVCB_ALPN_LENGTH");alpn.Add(Convert.ToHexString(b.AsSpan(i,n)));i+=n;}break;
     case 2:if(b.Length!=0||!values.ContainsKey(1))throw new FormatException("SVCB_NO_DEFAULT_ALPN");break;
     case 3:if(b.Length!=2)throw new FormatException("SVCB_PORT_LENGTH");port=U16(b,0);break;
     case 4:case 6:
      int size=entry.Key==4?4:16;if(b.Length==0||b.Length%size!=0)throw new FormatException("SVCB_HINT_LENGTH");
      for(int i=0;i<b.Length;i+=size)(size==4?v4:v6).Add(new IPAddress(b.AsSpan(i,size)).ToString());break;
    }
   }
   return new(owner,priority,target,target=="."?"SERVICE_OWNER_NAME":"SERVICE_TARGET_NOT_FOLLOWED",compatible,alpn.ToArray(),port,v4.ToArray(),v6.ToArray(),values.ContainsKey(5)?"ECH_CONFIG_PRESENT_NOT_VALIDATED":"NOT_PRESENT",opaque);
  }
  public static ScenarioDnsResult Parse(byte[] packet,string host,int type) {
   ScenarioProbes.RequireHost(host);if(type!=64&&type!=65)throw new ArgumentException("DNS type");
   if(packet==null||packet.Length<12||packet.Length>65535||(packet[2]&0xfa)!=0x80||U16(packet,4)!=1)throw new FormatException("DNS_INVALID_RESPONSE");
   int answers=U16(packet,6),authority=U16(packet,8),additional=U16(packet,10);if(answers+authority+additional>256)throw new FormatException("DNS_RECORD_LIMIT");
   int p=12;string name=Name(packet,ref p,packet.Length,true);
   if(!string.Equals(name,host,StringComparison.OrdinalIgnoreCase)||U16(packet,p)!=type||U16(packet,p+2)!=1)throw new FormatException("DNS_QUESTION");p+=4;
   var result=new List<ScenarioSvcb>();
   for(int i=0;i<answers+authority+additional;i++) {
    string owner=Name(packet,ref p,packet.Length,true);int t=U16(packet,p),cl=U16(packet,p+2),len=U16(packet,p+8);p+=10;
    if(p+len>packet.Length)throw new FormatException("DNS_RDATA_BOUNDS");
    if(i<answers&&cl==1&&t==type)result.Add(ParseRecord(owner,packet.AsSpan(p,len).ToArray()));p+=len;
   }
   if(p!=packet.Length)throw new FormatException("DNS_TRAILING_BYTES");
   return new(packet[3]&15,result.ToArray(),"Record metadata only; owner/CNAME dependency and aliases/hints not followed; no routing or ECH support inferred");
  }
 }
}
