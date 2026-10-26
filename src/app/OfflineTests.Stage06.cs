using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 // UI polish of the profile card, the selected benchmark row and the Info page, and the waits of the background pings.
 public static partial class OfflineTests {
  [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
  sealed class SilentProbe:ISpeedProbe {
   readonly Func<string,CancellationToken,Task<Reply>> doh;public int DohCalls;
   public SilentProbe(Func<string,CancellationToken,Task<Reply>> doh){this.doh=doh;}
   public Task<Reply> Doh(string template,string server,string host,int timeoutMs,CancellationToken ct){Interlocked.Increment(ref DohCalls);return doh(server,ct);}
   public Task<TlsInfo> Tls(string ip,string host,int timeoutMs,CancellationToken ct)=>Task.FromResult(new TlsInfo{Ok=true,ConnectMs=40});
  }
  static Reply Answer(string ip)=>new Reply{Code="DNS_OK",Addresses=new[]{ip}};
  static async Task<Reply> After(int ms,Reply r,CancellationToken ct){await Task.Delay(ms,ct);return r;}
  static BenchRun SampleRun(ProfileSpec profile,long[] samples,long median,long p95,long tls,int ok,int attempts,bool good=true){
   var result=new ProfileResult{Id=profile.Id,Template=profile.Template,Servers=profile.Servers,Final=true,Attempts=attempts,Successes=ok,Availability=(double)ok/attempts,MedianMs=median,P95Ms=p95,TlsMs=tls,Samples=samples,Verdict=good?"Good":"Flaky",DomainsOk=true,Alive=true,Selectable=true,Recommendable=true,Rank=1};
   return new BenchRun{StartedUtc=DateTime.UtcNow.ToString("o"),Attempts=attempts,CatalogVersion=ProfileCatalog.BuiltIn.Version,Targets=Program.Hosts.ToArray(),Results=new[]{result}};
  }
  sealed class TimeoutProbeTransport:IBenchTransport {
   public readonly System.Collections.Concurrent.ConcurrentBag<(string Server,int Timeout)> Queries=new System.Collections.Concurrent.ConcurrentBag<(string,int)>();
   readonly Func<string,bool> alive;public TimeoutProbeTransport(Func<string,bool> alive){this.alive=alive;}
   public Task<TlsInfo> HandshakeAsync(ProfileSpec p,string server,int timeoutMs,CancellationToken ct)=>Task.FromResult(alive(server)?new TlsInfo{Ok=true,ConnectMs=10,HandshakeMs=20}:new TlsInfo{Ok=false,Error="DOH_TIMEOUT"});
   public Task<Reply> QueryAsync(ProfileSpec p,string server,string host,int type,int timeoutMs,CancellationToken ct){Queries.Add((server,timeoutMs));return Task.FromResult(alive(server)?new Reply{Code="DNS_OK",Addresses=new[]{"142.250.80.1"},Milliseconds=20}:new Reply{Code="DNS_TIMEOUT_OR_UNREACHABLE",Addresses=new string[0]});}
   public Task<TlsInfo> AnswerTlsAsync(ProfileSpec p,string ip,string host,int timeoutMs,CancellationToken ct)=>Task.FromResult(new TlsInfo{Ok=true});
  }
  static void RunStage06BenchTimeoutTests(){
   ProfileSpec Spec(string id,params string[] servers)=>new ProfileSpec{Id=id,Template="https://"+id+".test/dns-query",Servers=servers};
   var o=new BenchOptions{AttemptGapMs=0};
   // a profile whose first round failed completely: the next rounds wait RetryTimeoutMs, the first one the full time
   var dead=new TimeoutProbeTransport(s=>false);
   var run=Benchmark.RunAsync(new[]{Spec("dead","192.0.2.1","192.0.2.2","192.0.2.3")},o,dead).GetAwaiter().GetResult();
   var times=dead.Queries.Select(q=>q.Timeout).OrderByDescending(x=>x).ToArray();
   Check(run.Results[0].Verdict=="Dead"&&run.Results[0].Attempts==3&&times.Length>=3&&times[0]==o.RequestTimeoutMs&&times.Skip(1).All(x=>x==o.RetryTimeoutMs)&&times.Count(x=>x==o.RequestTimeoutMs)==1,"a silent provider: round 1 waits "+o.RequestTimeoutMs+" ms, the next rounds "+o.RetryTimeoutMs+" ms, verdict and attempts unchanged ("+string.Join(",",times)+")");
   // a provider that answered keeps the full time in every round (also after one failed server)
   var mixed=new TimeoutProbeTransport(s=>s!="192.0.2.1");
   var run2=Benchmark.RunAsync(new[]{Spec("mixed","192.0.2.1","192.0.2.2")},o,mixed).GetAwaiter().GetResult();
   var t2=mixed.Queries.Where(q=>q.Server=="192.0.2.2").Select(q=>q.Timeout).ToArray();
   Check(run2.Results[0].Successes>0&&t2.Count(x=>x==o.RetryTimeoutMs)==Program.Hosts.Length&&t2.Count(x=>x==o.RequestTimeoutMs)==t2.Length-Program.Hosts.Length,"only the one round right after the failed first one is short; once something answered, every round keeps the full timeout ("+string.Join(",",t2)+")");
   // a healthy provider never sees the short time
   var good=new TimeoutProbeTransport(s=>true);
   Benchmark.RunAsync(new[]{Spec("good","192.0.2.9")},o,good).GetAwaiter().GetResult();
   Check(good.Queries.All(q=>q.Timeout==o.RequestTimeoutMs),"a healthy provider is always asked with the full timeout");
   // the short time can never exceed the full one
   var odd=new TimeoutProbeTransport(s=>false);
   Benchmark.RunAsync(new[]{Spec("odd","192.0.2.1","192.0.2.2")},new BenchOptions{AttemptGapMs=0,RequestTimeoutMs=800,RetryTimeoutMs=5000},odd).GetAwaiter().GetResult();
   Check(odd.Queries.All(q=>q.Timeout<=800),"the retry timeout is capped by the request timeout");
  }
  static void RunStage06Tests(){RunStage06BenchTimeoutTests();RunStage06UiTests();}
  static void RunStage06UiTests(){
   bool english=L.En;
   try{
    // one colour scale for every measured time
    Check(Latency.Of(299)==Theme.Green&&Latency.Of(300)==Theme.Amber&&Latency.Of(799)==Theme.Amber&&Latency.Of(800)==Theme.Red&&Latency.Of(-1)==Theme.Muted,"one latency scale: green below 300 ms, amber below 800 ms, red from there, muted without a value");
    Check(Latency.OfTls(599)==Theme.Green&&Latency.OfTls(600)==Theme.Amber&&Latency.OfTls(1600)==Theme.Red&&Latency.OfTls(-1)==Theme.Muted,"TLS limits are twice the DoH limits (the benchmark's rule, now written once)");
    Check(Latency.OfAvailability(.9)==Theme.Green&&Latency.OfAvailability(.6)==Theme.Amber&&Latency.OfAvailability(.59)==Theme.Red,"availability colours: 90 % and 60 % limits");
    // the rows of the benchmark: four different states, the chosen one in the colour of the active tab
    var chosen=BenchTable.RowTint(true,false,false);var chosenHover=BenchTable.RowTint(true,true,false);var hoverOnly=BenchTable.RowTint(false,true,false);var plain=BenchTable.RowTint(false,false,false);
    Check(chosen==Theme.NavSel&&chosen!=chosenHover&&chosen!=hoverOnly&&hoverOnly!=plain&&chosenHover!=hoverOnly&&chosen!=plain,"row states differ: plain, hover, chosen (= fill of the active tab), chosen + hover");
    foreach(bool en in new[]{false,true}){
     L.En=en;
     using(var form=new MainForm(true)){
      form.Show();Application.DoEvents();form.ApplyLanguage(en);
      // chosen row: one flat fill, no bar at its left edge
      form.SelectTab(1);Application.DoEvents();
      var table=form.BenchForTest;float k=form.LayoutScale;
      int idx=Array.FindIndex(table.DisplayRows,r=>r.Selected);
      using(var bmp=new Bitmap(table.Width,table.Height)){
       table.DrawToBitmap(bmp,new Rectangle(Point.Empty,bmp.Size));
       var rect=table.RowRectForTest(Math.Max(0,idx));int my=(int)(rect.Y+rect.Height/2);
       Color edge=bmp.GetPixel((int)(rect.X+1.5f*k),my),inner=bmp.GetPixel((int)(rect.X+5.5f*k),my);
       int dist(Color a,Color b)=>Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
       Check(idx>=0&&dist(edge,inner)<=6&&dist(edge,Theme.NavSel)<=10,"the chosen row is one flat fill of the active tab's colour, no pin at its left edge "+en+" (edge "+edge+", inside "+inner+")");
      }
      // the card: no «Открыть замер»; «Подробнее» only with something to read; the card takes the strip the link leaves
      foreach(string mode in new[]{"idle","checking","busy","active","active-unchecked","recovery","conflict","wmi","drift","switch","noprovider"}){
       form.SmokeState(mode);Application.DoEvents();
       Check(!form.OpenBenchmarkLinkForTest,"no «Открыть замер» link in state "+mode+" "+en);
       bool link=form.DetailsLinkVisible;float s=form.LayoutScale;
       Check(form.ProfileCardUnitsForTest==(link?134:166)&&Math.Abs(form.ProfileBounds.Height-(int)Math.Round(form.ProfileCardUnitsForTest*s))<=2,"the profile card is "+(link?"134":"166")+" units while «Подробнее» is "+(link?"shown":"hidden")+" ("+mode+" "+en+")");
       var problems=form.MainGeometryProblems();
       Check(problems.Count==0,"main page geometry holds in state "+mode+" "+en+(problems.Count==0?"":" — "+string.Join("; ",problems)));
      }
      form.SmokeState("idle");Check(!form.DetailsLinkVisible,"a quiet bridge shows no «Подробнее» "+en);
      form.SmokeState("recovery");Check(form.DetailsLinkVisible&&form.DetailsFooterReachableForTest(),"recovery keeps «Подробнее» and its panel (manual steps, support report) reachable "+en);
      // what «Подробнее» used to carry besides its notes has a home of its own
      Check(form.FailoverInSettingsForTest&&form.ReportInLogsForTest&&form.DetailsPanelHoldsOnlyNotesForTest,"«Предлагать резерв» is in Settings → Program, «Отчёт для поддержки» in Logs; the details panel keeps notes, steps and Copy "+en);
      form.SetFailoverForTest(true);Check(form.FailoverSettingForTest,"the failover switch in Settings still changes the setting "+en);form.SetFailoverForTest(false);Check(!form.FailoverSettingForTest,"and switches it off again "+en);
      // the profile's notes are in the log when the profile is chosen
      form.SmokeState("idle");var note=ProfileCatalog.BuiltIn.Find("dns.zapret.moe");
      form.ChooseProfile(note.Id);Application.DoEvents();
      Check(form.LogTextForTest.Contains(note.Note.Substring(0,24))&&form.LogTextForTest.Contains(note.Template),"choosing a profile writes its notes and DoH address to the log "+en);
      // the board of the card lets the mouse through: hover and click belong to the card
      var card=form.ProfileSelectForTest;
      Check((long)SendMessage(form.TelemetryHandleForTest,0x84,IntPtr.Zero,IntPtr.Zero)==-1,"the board is transparent to hit tests, so the card keeps hover while the mouse is over the numbers "+en);
      card.HoverForTestSet(true);Check(card.HoverForTest>=.999f,"hover lifts the card");
      card.HoverForTestSet(false);Check(card.HoverForTest<=.001f,"leaving puts it back");
      // the board with data: numbers are carried for the tints, and both card heights paint
      var spec=form.PrecisionProfileForTest;
      var run=SampleRun(spec,new long[]{38,44,52,57,57,61,88,296},57,174,225,6,6);
      form.PrecisionMetricsForTest(run);var data=form.Stage01TelemetryForTest;
      Check(data.HasMetrics&&data.MedianMs==57&&data.PeakMs==174&&data.TlsMs==225&&Math.Abs(data.Availability-1)<1e-9,"the card carries the figures as numbers for its tints "+en);
      foreach(string mode in new[]{"idle","recovery"}){
       form.SmokeState(mode);Application.DoEvents();
       using(var bmp=new Bitmap(form.ProfileBounds.Width,form.ProfileBounds.Height)){
        form.ProfileControl.DrawToBitmap(bmp,new Rectangle(Point.Empty,bmp.Size));
        Check(bmp.Width>0&&bmp.GetPixel(bmp.Width/2,bmp.Height/2).A==255,"the card paints with its figures in state "+mode+" "+en);
       }
      }
     }
    }
    L.En=english;
    // the Info page: measured from its text at every scale and in both languages; no clipped paragraph, no gap left under the cards
    foreach(bool en in new[]{false,true})foreach(int dpi in new[]{96,120,144,192}){
     L.En=en;
     using(var form=new MainForm(true)){
      form.Show();Application.DoEvents();UiSurfaceCheck.ChangeDpi(form,dpi);form.ApplyLanguage(en);form.SelectTab(4);Application.DoEvents();
      var bad=form.InfoLayoutProblems();
      Check(bad.Count==0,"Info page: every paragraph shows all its text, cards do not overlap or leave the page "+en+" @"+dpi+(bad.Count==0?"":" — "+string.Join("; ",bad)));
      float s=form.LayoutScale;int pageH=form.InfoPageClientHeightForTest;
      Check(form.InfoContentBottom<=pageH||form.InfoPageScrollsForTest,"Info page: the content ends inside the page, or the page scrolls "+en+" @"+dpi);
      if(dpi>=120)Check(!form.InfoPageScrollsForTest&&form.InfoContentBottom<=pageH,"Info page: at 125 % and above all three cards fit without a scroll bar, both languages "+en+" @"+dpi+" (bottom "+form.InfoContentBottom+" of "+pageH+")");
      Check(form.InfoContentBottom>=pageH-(int)Math.Round(40*s)||form.InfoPageScrollsForTest,"Info page: no empty band under the last card (≤ 40 units) "+en+" @"+dpi+" (bottom "+form.InfoContentBottom+" of "+pageH+")");
     }
    }
    L.En=english;
    // pings: a silent server no longer holds the next one back for its whole timeout, and an answering one is the only one asked
    var sw=Stopwatch.StartNew();
    var fast=new SilentProbe((server,ct)=>Task.FromResult(Answer("142.250.80.9")));
    var quick=BridgeSpeed.MeasureAsync(fast,"https://x.test/dns-query",new[]{"192.0.2.1","192.0.2.2","192.0.2.3"},"daily-cloudcode-pa.googleapis.com",CancellationToken.None).GetAwaiter().GetResult();
    Check(fast.DohCalls==1&&quick.Proxy=="142.250.80.9","a server that answers at once is the only one asked");
    var hang=new SilentProbe((server,ct)=>server=="192.0.2.1"?After(60000,Answer("142.250.80.1"),ct):Task.FromResult(Answer("142.250.80.2")));
    sw.Restart();var second=BridgeSpeed.MeasureAsync(hang,"https://x.test/dns-query",new[]{"192.0.2.1","192.0.2.2"},"daily-cloudcode-pa.googleapis.com",CancellationToken.None).GetAwaiter().GetResult();sw.Stop();
    Check(second.Proxy=="142.250.80.2"&&sw.ElapsedMilliseconds<BridgeSpeed.HedgeMs+1500,"a silent first server is joined by the next after "+BridgeSpeed.HedgeMs+" ms and the answer of the second is used ("+sw.ElapsedMilliseconds+" ms, not the 2500 ms timeout)");
    var deadAll=new SilentProbe((server,ct)=>After(700,new Reply{Code="DOH_TIMEOUT",Addresses=new string[0]},ct));
    sw.Restart();var none=BridgeSpeed.MeasureAsync(deadAll,"https://x.test/dns-query",new[]{"192.0.2.1","192.0.2.2","192.0.2.3","192.0.2.4"},"daily-cloudcode-pa.googleapis.com",CancellationToken.None).GetAwaiter().GetResult();sw.Stop();
    Check(none.Proxy==null&&none.Error=="NO_ANSWER"&&deadAll.DohCalls==4&&sw.ElapsedMilliseconds<4*700-300,"four silent servers cost about one timeout plus the hedge steps, not four timeouts ("+sw.ElapsedMilliseconds+" ms of the sequential "+4*700+" ms)");
    var order=new List<string>();
    var chain=new SilentProbe((server,ct)=>{lock(order)order.Add(server);return Task.FromResult(server=="192.0.2.3"?Answer("142.250.80.3"):new Reply{Code="DNS_TIMEOUT_OR_UNREACHABLE",Addresses=new string[0]});});
    var third=BridgeSpeed.MeasureAsync(chain,"https://x.test/dns-query",new[]{"192.0.2.1","192.0.2.2","192.0.2.3","192.0.2.4"},"daily-cloudcode-pa.googleapis.com",CancellationToken.None).GetAwaiter().GetResult();
    Check(third.Proxy=="142.250.80.3"&&order.SequenceEqual(new[]{"192.0.2.1","192.0.2.2","192.0.2.3"}),"servers that fail at once are tried in order, one after another, until one answers");
    using(var cts=new CancellationTokenSource()){
     var held=new SilentProbe((server,ct)=>After(60000,Answer("142.250.80.5"),ct));
     var run=BridgeSpeed.MeasureAsync(held,"https://x.test/dns-query",new[]{"192.0.2.1","192.0.2.2"},"daily-cloudcode-pa.googleapis.com",cts.Token);
     cts.CancelAfter(200);
     bool cancelled=false;try{run.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
     Check(cancelled,"cancelling stops the pending asks (no hang)");
    }
   }finally{L.En=english;}
  }
 }
}
