using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;

namespace AntigravityGeminiDns {
 public sealed class ProfileSpec {
  public string Id{get;set;}public string Slot{get;set;}public string[] Aliases{get;set;}=new string[0];
  public string Template{get;set;}public string[] Servers{get;set;}=new string[0];
  public string Origin{get;set;}public string Note{get;set;}public string KnownIssue{get;set;}
  public bool Custom{get;set;}
  // Listed in the catalog but not recommended: "Select best" skips it until a benchmark gives it the "Good" verdict.
  public bool Experimental{get;set;}
  public string Label=>Id;   // the provider is named by its DNS name only; the slot (P1..) stays in the catalog data
  public string Host=>new Uri(Template).IdnHost;
  public int Port=>new Uri(Template).Port;
 }
 public sealed class PendingProfile {
  public string Id{get;set;}public string Template{get;set;}public string[] Resolved{get;set;}=new string[0];public string Reason{get;set;}
 }
 // A built-in that was removed. Its names are never reused, so an old setting, journal or --profile cannot silently mean another provider.
 public sealed class RetiredProfile {
  public string Id{get;set;}public string Slot{get;set;}public string[] Aliases{get;set;}=new string[0];public string Date{get;set;}public string Reason{get;set;}
 }
 // Versioned provider data (embedded profiles.json). Providers are data, not code: add or change entries there.
 public sealed class ProfileCatalog {
  public int Schema{get;set;}public string Version{get;set;}public string Updated{get;set;}public string Policy{get;set;}
  // The only place the two Google domains and the default profile are defined; Engine.ps1 receives them as parameters (or reads the staged copy of this file).
  public string Default{get;set;}public string[] Targets{get;set;}=new string[0];
  // Optional second set of exact host names for the Gemini web app (measured 2026-10-13 at every catalog provider). Used only when the user picks the «+ Gemini Desktop» level; never a wildcard.
  public string[] GeminiTargets{get;set;}=new string[0];
  public string[] TargetsFor(bool gemini)=>gemini?Targets.Concat(GeminiTargets??new string[0]).ToArray():Targets;
  // "+ Браузер": suffix namespaces (".google.com" = every name under google.com) on top of the core and Gemini hosts. They become NRPT rules only;
  // the provider is still checked (DoH + TLS) on the exact hosts, so the safety rule is the same as for the narrower scopes.
  public string[] GoogleTargets{get;set;}=new string[0];
  public string[] TargetsForGoogle()=>TargetsFor(true).Concat(GoogleTargets??new string[0]).ToArray();
  public static bool IsSuffix(string t)=>t!=null&&t.StartsWith(".");
  public ProfileSpec DefaultProfile=>Find(Default);
  public ProfileSpec[] Profiles{get;set;}=new ProfileSpec[0];
  public PendingProfile[] Pending{get;set;}=new PendingProfile[0];
  public RetiredProfile[] Retired{get;set;}=new RetiredProfile[0];
  static readonly JsonSerializerOptions Options=new JsonSerializerOptions{PropertyNameCaseInsensitive=true,ReadCommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true};
  static ProfileCatalog builtIn;
  public static ProfileCatalog BuiltIn{get{if(builtIn==null){using(var s=typeof(ProfileCatalog).Assembly.GetManifestResourceStream("profiles.json"))using(var r=new StreamReader(s??throw new FileNotFoundException("Embedded resource missing: profiles.json")))builtIn=Parse(r.ReadToEnd());}return builtIn;}}
  public static ProfileCatalog Parse(string json) {
   var c=JsonSerializer.Deserialize<ProfileCatalog>(json,Options)??throw new FormatException("Empty profile catalog");
   c.Validate();return c;
  }
  public void Validate() {
   if(Schema!=1)throw new FormatException("Unsupported profile catalog schema: "+Schema);
   if(string.IsNullOrWhiteSpace(Version))throw new FormatException("Catalog version missing");
   if(Profiles==null||Profiles.Length==0)throw new FormatException("Catalog has no profiles");
   var every=(Targets??new string[0]).Concat(GeminiTargets??new string[0]).ToArray();
   if(Targets==null||Targets.Length<1||every.Length>4||every.Any(t=>string.IsNullOrWhiteSpace(t)||Uri.CheckHostName(t)!=UriHostNameType.Dns||t.Contains("*")||every.Count(x=>string.Equals(x,t,StringComparison.OrdinalIgnoreCase))>1))throw new FormatException("Catalog targets (with the Gemini set) must list 1..4 distinct DNS host names");
   var g=GoogleTargets??new string[0];
   if(g.Length>8||g.Any(t=>!IsSuffix(t)||t.Length<3||t.Contains("*")||Uri.CheckHostName(t.Substring(1))!=UriHostNameType.Dns||g.Count(x=>string.Equals(x,t,StringComparison.OrdinalIgnoreCase))>1))throw new FormatException("Catalog googleTargets must be 0..8 distinct suffixes like .google.com");
   var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(var p in Profiles) {
    if(string.IsNullOrWhiteSpace(p.Id)||p.Id.Length>40)throw new FormatException("Profile id invalid");
    if(!Uri.TryCreate(p.Template??"",UriKind.Absolute,out var u)||u.Scheme!=Uri.UriSchemeHttps||IPAddress.TryParse(u.IdnHost,out _))throw new FormatException("Profile "+p.Id+": DoH template must be an https:// URL with a host name");
    if(p.Servers==null||p.Servers.Length<1||p.Servers.Length>4)throw new FormatException("Profile "+p.Id+": 1..4 pinned server IPs required");
    foreach(string s in p.Servers)if(!IPAddress.TryParse(s,out var ip)||ip.ToString()!=s||!Program.PublicIp(s))throw new FormatException("Profile "+p.Id+": pinned IP is not a canonical public address: "+s);
    if(p.Servers.Distinct().Count()!=p.Servers.Length)throw new FormatException("Profile "+p.Id+": duplicate server");
    foreach(string n in new[]{p.Id}.Concat(p.Aliases??new string[0]).Concat(string.IsNullOrEmpty(p.Slot)?new string[0]:new[]{p.Slot}))if(!names.Add(n))throw new FormatException("Duplicate profile name or alias: "+n);
   }
   if(string.IsNullOrWhiteSpace(Default)||Find(Default)==null)throw new FormatException("Catalog default profile is missing or unknown");
   foreach(var p in Pending??new PendingProfile[0])if(string.IsNullOrWhiteSpace(p.Id)||string.IsNullOrWhiteSpace(p.Reason))throw new FormatException("Pending entry needs id and reason");
   foreach(var r in Retired??new RetiredProfile[0]) {
    if(string.IsNullOrWhiteSpace(r.Id)||string.IsNullOrWhiteSpace(r.Reason))throw new FormatException("Retired entry needs id and reason");
    foreach(string n in new[]{r.Id}.Concat(r.Aliases??new string[0]).Concat(string.IsNullOrEmpty(r.Slot)?new string[0]:new[]{r.Slot}))if(!names.Add(n))throw new FormatException("Retired name is still in use: "+n);
   }
  }
  public RetiredProfile FindRetired(string name)=>(Retired??new RetiredProfile[0]).FirstOrDefault(r=>string.Equals(r.Id,name,StringComparison.OrdinalIgnoreCase)||string.Equals(r.Slot,name,StringComparison.OrdinalIgnoreCase)||(r.Aliases??new string[0]).Any(a=>string.Equals(a,name,StringComparison.OrdinalIgnoreCase)));
  public ProfileSpec Find(string name)=>Profiles.FirstOrDefault(p=>string.Equals(p.Id,name,StringComparison.OrdinalIgnoreCase)||string.Equals(p.Slot,name,StringComparison.OrdinalIgnoreCase)||(p.Aliases??new string[0]).Any(a=>string.Equals(a,name,StringComparison.OrdinalIgnoreCase)));
 }
}
