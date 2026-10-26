using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace AntigravityGeminiDns {
 public sealed class Reply {
  public string Code; public int Rcode; public string[] Addresses; public string[] Cnames;
  public long Milliseconds; public string Transport; public string Server; public string Host; public int Type;
 }
 public static class Wire {
  [StructLayout(LayoutKind.Sequential)]struct NativeRecord {public IntPtr Next,Name;public ushort Type,DataLength;public uint Flags,Ttl,Reserved;}
  [DllImport("dnsapi.dll",CharSet=CharSet.Unicode,EntryPoint="DnsQuery_W")]static extern int DnsQuery(string name,ushort type,uint options,IntPtr extra,out IntPtr records,IntPtr reserved);
  [DllImport("dnsapi.dll")]static extern void DnsRecordListFree(IntPtr records,int freeType);
  public static object SystemQuery(string host,int type) {
   if(!Program.Hosts.Contains(host)&&host!="www.microsoft.com")throw new ArgumentException("domain");
   if(type!=1&&type!=28)throw new ArgumentException("type");
   IntPtr first;int status=DnsQuery(host,(ushort)type,0x8|0x40,IntPtr.Zero,out first,IntPtr.Zero);var addresses=new List<string>();
   try {for(IntPtr p=first;p!=IntPtr.Zero;) {var r=Marshal.PtrToStructure<NativeRecord>(p);if(r.Type==type&&(r.Flags&3)==1){int length=type==1?4:16;if(r.DataLength<length)throw new FormatException();var bytes=new byte[length];Marshal.Copy(IntPtr.Add(p,Marshal.SizeOf<NativeRecord>()),bytes,0,length);addresses.Add(new IPAddress(bytes).ToString());}p=r.Next;}}
   finally{if(first!=IntPtr.Zero)DnsRecordListFree(first,1);}
   return new {Host=host,Type=type,Code=status==0?"DNS_OK":"WINDOWS_DNS_STATUS_"+status,Addresses=addresses.ToArray(),Path="Windows DnsQuery_W; BYPASS_CACHE + NO_HOSTS_FILE; no explicit server"};
  }
  static int U16(byte[] b,int p) { if(p<0 || p+2>b.Length) throw new FormatException(); return (b[p]<<8)|b[p+1]; }
  static string Name(byte[] b, ref int pos) {
   int p=pos, steps=0; bool jumped=false; var labels=new List<string>(); var seen=new HashSet<int>();
   while(true) {
    if(++steps>128 || p>=b.Length || !seen.Add(p)) throw new FormatException("DNS compression loop");
    int n=b[p++]; if(n==0){if(!jumped)pos=p;break;}
    if((n&192)==192) { if(p>=b.Length)throw new FormatException(); int target=((n&63)<<8)|b[p++]; if(!jumped)pos=p; jumped=true;p=target;continue; }
    if(n>63 || p+n>b.Length)throw new FormatException(); labels.Add(Encoding.ASCII.GetString(b,p,n));p+=n;
   }
   return string.Join(".",labels.ToArray());
  }
  public static byte[] Query(string host,int type,int id) {
   if(type!=1 && type!=28)throw new ArgumentException("type");
   if(string.IsNullOrWhiteSpace(host)||host.Length>253)throw new ArgumentException("domain");
   var b=new List<byte>(new byte[]{(byte)(id>>8),(byte)id,1,0,0,1,0,0,0,0,0,0});
   foreach(string s in host.Split('.')) { if(s.Length<1||s.Length>63)throw new ArgumentException("label");foreach(char c in s)if(!(char.IsLetterOrDigit(c)&&c<128)&&c!='-')throw new ArgumentException("label"); b.Add((byte)s.Length);b.AddRange(Encoding.ASCII.GetBytes(s)); }
   b.AddRange(new byte[]{0,0,(byte)type,0,1});return b.ToArray();
  }
  public static Reply Parse(byte[] b,string host,int type,int id) {
   if(b==null||b.Length<12 || U16(b,0)!=id || (b[2]&128)==0 || (b[2]&120)!=0 || (b[2]&2)!=0 || U16(b,4)!=1)throw new FormatException("DNS_INVALID_RESPONSE");
   int p=12;string q=Name(b,ref p);if(!string.Equals(q,host,StringComparison.OrdinalIgnoreCase)||U16(b,p)!=type||U16(b,p+2)!=1)throw new FormatException("question");p+=4;
   int count=U16(b,6),rcode=b[3]&15;
   var cnameMap=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   var addressMap=new Dictionary<string,List<string>>(StringComparer.OrdinalIgnoreCase);
   for(int i=0;i<count;i++) {
    string owner=Name(b,ref p);int t=U16(b,p),cl=U16(b,p+2),len=U16(b,p+8);p+=10;
    if(p+len>b.Length)throw new FormatException("rdata");
    if(cl==1) {
     if(t==5){int cp=p;string alias=Name(b,ref cp);if(cp!=p+len)throw new FormatException("cname length");if(!cnameMap.ContainsKey(owner))cnameMap[owner]=alias;}
     else if(t==type && ((t==1&&len==4)||(t==28&&len==16))){var raw=new byte[len];Array.Copy(b,p,raw,0,len);string ip=new IPAddress(raw).ToString();if(!addressMap.TryGetValue(owner,out var list)){list=new List<string>();addressMap[owner]=list;}list.Add(ip);}
    }
    p+=len;
   }
   var names=new List<string>();var addresses=new List<string>();string current=host;
   var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);const int MaxHops=8;bool loopDetected=false;
   while(cnameMap.TryGetValue(current,out string alias)) {
    if(!visited.Add(current)||visited.Count>MaxHops||visited.Contains(alias)){loopDetected=true;break;}
    names.Add(alias);current=alias;
   }
   if(!loopDetected&&addressMap.TryGetValue(current,out var addrs))addresses.AddRange(addrs);
   return new Reply{Code=rcode==0?(addresses.Count>0?"DNS_OK":"DNS_NODATA"):(rcode==3?"DNS_NXDOMAIN":"DNS_RCODE_"+rcode),Rcode=rcode,Addresses=addresses.ToArray(),Cnames=names.ToArray()};
  }
  static byte[] ReadExact(Stream s,int n){var b=new byte[n];int p=0;while(p<n){int k=s.Read(b,p,n-p);if(k<=0)throw new IOException("DNS short TCP");p+=k;}return b;}
  public static Reply Probe(string host,int type,string server,bool tcp,int timeout) {
   var sw=Stopwatch.StartNew();int id=System.Security.Cryptography.RandomNumberGenerator.GetInt32(1,65536);byte[] q=Query(host,type,id);Reply r;
   try {
    var ip=IPAddress.Parse(server);byte[] b;
    if(tcp) {
     using(var c=new TcpClient(ip.AddressFamily)){var a=c.BeginConnect(ip,53,null,null);if(!a.AsyncWaitHandle.WaitOne(timeout))throw new TimeoutException();c.EndConnect(a);c.ReceiveTimeout=timeout;c.SendTimeout=timeout;using(var s=c.GetStream()){s.WriteByte((byte)(q.Length>>8));s.WriteByte((byte)q.Length);s.Write(q,0,q.Length);byte[] h=ReadExact(s,2);int n=U16(h,0);if(n<12)throw new FormatException();b=ReadExact(s,n);}}
    }else{using(var c=new UdpClient(ip.AddressFamily)){c.Client.ReceiveTimeout=timeout;c.Connect(ip,53);c.Send(q,q.Length);var peer=new IPEndPoint(ip,53);b=c.Receive(ref peer);}}
    r=Parse(b,host,type,id);
   }catch(FormatException){r=new Reply{Code="DNS_INVALID_RESPONSE",Addresses=new string[0],Cnames=new string[0]};}
   catch(Exception){r=new Reply{Code="DNS_TIMEOUT_OR_UNREACHABLE",Addresses=new string[0],Cnames=new string[0]};}
   r.Milliseconds=sw.ElapsedMilliseconds;r.Transport=tcp?"TCP":"UDP";r.Server=server;r.Host=host;r.Type=type;return r;
  }
  public static async Task<Reply> ProbeAsync(string host,int type,string server,bool tcp,int timeout,CancellationToken ct) {
   var sw=Stopwatch.StartNew();int id=System.Security.Cryptography.RandomNumberGenerator.GetInt32(1,65536);byte[] q=Query(host,type,id);Reply r;
   using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct)) {
    deadline.CancelAfter(timeout);
    try {
     var ip=IPAddress.Parse(server);byte[] data;
     if(tcp) {
      using(var c=new TcpClient(ip.AddressFamily)) {
       await c.ConnectAsync(ip,53,deadline.Token);
       using(var stream=c.GetStream()) {
        byte[] frame=new byte[q.Length+2];frame[0]=(byte)(q.Length>>8);frame[1]=(byte)q.Length;q.CopyTo(frame,2);
        await stream.WriteAsync(frame,deadline.Token);
        byte[] prefix=new byte[2];await stream.ReadExactlyAsync(prefix,deadline.Token);
        int size=U16(prefix,0);if(size<12)throw new FormatException();data=new byte[size];await stream.ReadExactlyAsync(data,deadline.Token);
       }
      }
     } else {
      using(var c=new UdpClient(ip.AddressFamily)){c.Connect(ip,53);await c.SendAsync(q,deadline.Token);data=(await c.ReceiveAsync(deadline.Token)).Buffer;}
     }
     r=Parse(data,host,type,id);
    }catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
    catch(OperationCanceledException){r=new Reply{Code="DNS_TIMEOUT"};}
    catch(FormatException){r=new Reply{Code="DNS_INVALID_RESPONSE"};}
    catch(SocketException){r=new Reply{Code="DNS_UNREACHABLE"};}
    catch(IOException){r=new Reply{Code="DNS_INVALID_RESPONSE"};}
   }
   r.Addresses=r.Addresses??Array.Empty<string>();r.Cnames=r.Cnames??Array.Empty<string>();r.Milliseconds=sw.ElapsedMilliseconds;r.Transport=tcp?"TCP":"UDP";r.Server=server;r.Host=host;r.Type=type;return r;
  }
  // Cancellable variant over a caller-owned HttpClient whose ConnectCallback pins the provider IP (the port comes from the URI, so :8443 works).
  public static async Task<Reply> DohAsync(HttpClient client,string uri,string host,int type,int timeoutMs,CancellationToken ct) {
   int id=System.Security.Cryptography.RandomNumberGenerator.GetInt32(1,65536);byte[] q=Query(host,type,id);var sw=Stopwatch.StartNew();
   Reply r;
   using(var cts=CancellationTokenSource.CreateLinkedTokenSource(ct)) {
    cts.CancelAfter(timeoutMs);
    try {
     using(var request=new HttpRequestMessage(HttpMethod.Post,uri)) {
      request.Version=HttpVersion.Version20;request.VersionPolicy=HttpVersionPolicy.RequestVersionExact;request.Content=new ByteArrayContent(q);
      request.Content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message");request.Headers.Accept.ParseAdd("application/dns-message");
      using(var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cts.Token)) {
       if(response.StatusCode!=HttpStatusCode.OK||response.Content.Headers.ContentType?.MediaType!="application/dns-message")r=new Reply{Code="DOH_INVALID_HTTP_RESPONSE_"+(int)response.StatusCode};
       else using(var stream=await response.Content.ReadAsStreamAsync(cts.Token))using(var data=new MemoryStream()) {
        byte[] buffer=new byte[4096];int n;
        while((n=await stream.ReadAsync(buffer,cts.Token))>0){data.Write(buffer,0,n);if(data.Length>65535)throw new FormatException();}
        r=Parse(data.ToArray(),host,type,id);
       }
      }
     }
    }
    catch(FormatException){r=new Reply{Code="DNS_INVALID_RESPONSE"};}
    catch(HttpRequestException e){r=new Reply{Code=HttpFailure(e)};}
    catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
    catch(OperationCanceledException){r=new Reply{Code="DOH_TIMEOUT"};}
    catch(Exception){r=new Reply{Code="DNS_INVALID_RESPONSE"};}
   }
   r.Addresses=r.Addresses??new string[0];r.Cnames=r.Cnames??new string[0];
   r.Transport="DoH HTTP/2 (benchmark, pinned IP)";r.Host=host;r.Type=type;r.Milliseconds=sw.ElapsedMilliseconds;return r;
  }
  internal static string HttpFailure(HttpRequestException e)=>e.HttpRequestError switch {
   HttpRequestError.SecureConnectionError=>"DOH_TLS_FAILURE",
   HttpRequestError.HttpProtocolError or HttpRequestError.VersionNegotiationError or HttpRequestError.InvalidResponse=>"DOH_PROTOCOL_FAILURE",
   HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError=>"DOH_UNREACHABLE",
   _=>"DOH_TRANSPORT_FAILURE"
  };
  public static Reply Doh(string host,int type,string server,int timeout,string template=null) {
   int id=System.Security.Cryptography.RandomNumberGenerator.GetInt32(1,65536);byte[] q=Query(host,type,id);var sw=Stopwatch.StartNew();
   Reply r;
   string uri=string.IsNullOrWhiteSpace(template)?Program.DefaultTemplate:template;
   try {
    using(var handler=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false}) {
     handler.SslOptions.RemoteCertificateValidationCallback=CertRevocation.Callback;
     handler.ConnectCallback=async (context,ct)=> { var ip=IPAddress.Parse(server);var socket=new Socket(ip.AddressFamily,SocketType.Stream,ProtocolType.Tcp);try{await socket.ConnectAsync(ip,context.DnsEndPoint.Port,ct);return new NetworkStream(socket,true);}catch{socket.Dispose();throw;} };
     using(var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(timeout)}) {
      using(var request=new HttpRequestMessage(HttpMethod.Post,uri)) {
       request.Version=HttpVersion.Version20;request.VersionPolicy=HttpVersionPolicy.RequestVersionExact;request.Content=new ByteArrayContent(q);request.Content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message");request.Headers.Accept.ParseAdd("application/dns-message");
       using(var response=client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult()) {
        if(response.StatusCode!=HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType!="application/dns-message")r=new Reply{Code="DOH_INVALID_HTTP_RESPONSE_"+(int)response.StatusCode};
        else {using(var ct=new CancellationTokenSource(TimeSpan.FromSeconds(timeout))){using(var stream=response.Content.ReadAsStream(ct.Token)){using(var data=new MemoryStream()){byte[] buffer=new byte[4096];int n;while((n=stream.ReadAsync(buffer,ct.Token).AsTask().GetAwaiter().GetResult())>0){data.Write(buffer,0,n);if(data.Length>65535)throw new FormatException();}r=Parse(data.ToArray(),host,type,id);}}}}
       }
      }
     }
    }
   }catch(FormatException){r=new Reply{Code="DNS_INVALID_RESPONSE"};}
   catch(HttpRequestException){r=new Reply{Code="DOH_TLS_FAILURE_OR_UNREACHABLE"};}
   catch(OperationCanceledException){r=new Reply{Code="DNS_TIMEOUT_OR_UNREACHABLE"};}
   catch(Exception){r=new Reply{Code="DNS_INVALID_RESPONSE"};}
   r.Addresses=r.Addresses??new string[0];r.Cnames=r.Cnames??new string[0];r.Transport="DoH HTTP/2 (TLS validated on success, pinned IP)";r.Server=server;r.Host=host;r.Type=type;r.Milliseconds=sw.ElapsedMilliseconds;return r;
  }
 }
}
