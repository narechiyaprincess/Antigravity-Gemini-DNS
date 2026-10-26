using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace AntigravityGeminiDns {
 public enum ObservationOutcome { Passed, Failed, Unknown, Skipped }
 public enum ReadinessState { Off, Ready, LikelyReady, Partial, Degraded, Unknown, Failed }
 public enum EvidenceCompleteness { Missing, Partial, Complete }

 // One observation describes one method and client path. Transport proof never fills an application check.
 public sealed class Observation {
  public string Id;public string Layer;public string[] Scope=Array.Empty<string>();
  public string ClientPath;public string Source;public string Method;
  public DateTime ObservedUtc;public DateTime ExpiresUtc;public string ContextRevision;
  public ObservationOutcome Outcome;public string Reason;public bool Critical;
  public bool HasProvenance=>!string.IsNullOrWhiteSpace(Source)&&!string.IsNullOrWhiteSpace(Method)&&!string.IsNullOrWhiteSpace(ClientPath);
  public bool Fresh(DateTime now,string context)=>ObservedUtc!=default&&ExpiresUtc>ObservedUtc&&ObservedUtc<=now&&now<ExpiresUtc&&
   !string.IsNullOrWhiteSpace(context)&&string.Equals(ContextRevision,context,StringComparison.Ordinal);
 }
 public sealed class ReadinessResult {
  public ReadinessState State;public EvidenceCompleteness Completeness;
  public int Required;public int Confirmed;public string[] Missing=Array.Empty<string>();public string Reason;
 }
 public static class EvidenceReducer {
  public static ReadinessResult Reduce(IEnumerable<Observation> source,string context,DateTime now,bool enabled,bool configurationKnown) {
   var all=(source??Array.Empty<Observation>()).ToArray();
   var required=all.Where(x=>x.Critical).ToArray();
   var failed=required.FirstOrDefault(x=>x.HasProvenance&&x.Fresh(now,context)&&x.Outcome==ObservationOutcome.Failed);
   var confirmed=required.Count(x=>x.HasProvenance&&x.Fresh(now,context)&&x.Outcome==ObservationOutcome.Passed);
   var missing=required.Where(x=>!x.HasProvenance||!x.Fresh(now,context)||x.Outcome==ObservationOutcome.Unknown||x.Outcome==ObservationOutcome.Skipped).Select(x=>x.Id).Distinct().ToArray();
   var complete=required.Length>0&&confirmed==required.Length;
   var stale=required.Any(x=>x.HasProvenance&&x.ObservedUtc!=default&&!x.Fresh(now,context));
   bool offProven=configurationKnown&&all.Any(x=>x.Layer=="journal"&&x.HasProvenance&&x.Fresh(now,context)&&x.Outcome==ObservationOutcome.Passed);
   return new ReadinessResult{Required=required.Length,Confirmed=confirmed,Missing=missing,
    Completeness=complete?EvidenceCompleteness.Complete:confirmed>0?EvidenceCompleteness.Partial:EvidenceCompleteness.Missing,
    State=failed!=null?ReadinessState.Failed:!enabled?(offProven?ReadinessState.Off:ReadinessState.Unknown):stale?ReadinessState.Degraded:complete?ReadinessState.LikelyReady:confirmed>0?ReadinessState.Partial:ReadinessState.Unknown,
    Reason=failed?.Reason??(!enabled?(offProven?"BRIDGE_OFF":"CONFIGURATION_UNKNOWN"):stale?"STALE_OR_CHANGED_CONTEXT":complete?"NETWORK_ONLY_APPLICATION_NOT_TESTED":"CRITICAL_EVIDENCE_INCOMPLETE")};
  }
 }
 public static class EvidenceContext {
  public static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text??"")));
  // No traffic: hash only local interface state. A caller also tracks events to catch a network that changes and changes back.
  // Only what decides the route counts. A second physical adapter with its own default gateway (Wi-Fi beside a primary Ethernet) blinking,
  // Teredo, loopback, down adapters and rotating IPv6 link-local/temporary addresses used to make every operation "change network" and fail.
  public static string NetworkRevision() {
   try{
    int primary=GetBestInterface(BitConverter.ToUInt32(new byte[]{1,1,1,1},0),out uint best)==0?(int)best:-1;   // a route lookup, no packet is sent
    return RevisionOf(NetworkInterface.GetAllNetworkInterfaces().Select(n=>{
     var p=n.GetIPProperties();int index=-1;try{index=p.GetIPv4Properties()?.Index??-1;}catch(NetworkInformationException){}
     return new NicView(n.Id,n.NetworkInterfaceType,n.OperationalStatus==OperationalStatus.Up,index,
      p.UnicastAddresses.Where(a=>!a.Address.IsIPv6LinkLocal&&!(a.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6&&a.SuffixOrigin==SuffixOrigin.Random)).Select(a=>a.Address.ToString()).ToArray(),
      p.DnsAddresses.Select(a=>a.ToString()).ToArray(),p.GatewayAddresses.Select(a=>a.Address.ToString()).ToArray(),
      p.GatewayAddresses.Any(g=>g.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&!g.Address.Equals(System.Net.IPAddress.Any)));
    }),primary);
   }catch{return null;}
  }
  public sealed record NicView(string Id,NetworkInterfaceType Type,bool Up,int Index,string[] Addresses,string[] Dns,string[] Gateways,bool DefaultGateway);
  internal static bool Counts(NicView n,int primary) {
   if(!n.Up||n.Type==NetworkInterfaceType.Loopback||n.Type==NetworkInterfaceType.Tunnel)return false;
   bool physical=n.Type==NetworkInterfaceType.Ethernet||n.Type==NetworkInterfaceType.GigabitEthernet||n.Type==NetworkInterfaceType.FastEthernetT||n.Type==NetworkInterfaceType.FastEthernetFx||n.Type==NetworkInterfaceType.Wireless80211;
   return !physical||!n.DefaultGateway||n.Index==primary;   // VPN/TUN adapters always count: they carry the provider's traffic
  }
  internal static string RevisionOf(IEnumerable<NicView> nics,int primary) {
   static string Set(IEnumerable<string> xs)=>string.Join(",",(xs??Array.Empty<string>()).OrderBy(x=>x,StringComparer.Ordinal));
   return Hash("primary:"+primary+"|"+string.Join("|",nics.Where(n=>Counts(n,primary)).OrderBy(n=>n.Id,StringComparer.Ordinal).Select(n=>n.Id+":"+Set(n.Addresses)+":"+Set(n.Dns)+":"+Set(n.Gateways))));
  }
  [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]static extern int GetBestInterface(uint destination,out uint index);
 }
}
