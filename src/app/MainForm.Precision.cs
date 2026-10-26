using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 public sealed partial class MainForm {
  // The selected profile's measured state inside its card (see ProfileTelemetry). Fed only by real benchmark results of this exact profile
  // (selection ID and DoH template); the trend comes from the stored benchmark history, never from a synthetic run.
  readonly ProfileTelemetry profileTelemetry=new ProfileTelemetry();
  List<BenchRun> benchHistory=new List<BenchRun>();
  const int TrendPoints=12;
  string providerEvidenceIdentity;
  string ProviderHealthIdentity()=>Current.SelectionId+"|"+Current.Template+"|"+string.Join(",",Current.Servers??Array.Empty<string>())+"|"+string.Join(",",Program.Targets);
  // The caption above the service selector: what selecting a service does.
  static string ScopeNote=>L.En?"DNS for the selected services uses the bridge":"DNS выбранных сервисов использует мост";
  void BuildPrecisionProfile(){
   scopeSel.AccessibleDescription=ScopeNote;
   profileSelect.HeaderHeight=CardHeaderH;profileSelect.AccentBorder=false;profileSelect.HeaderStatus=ProviderStatus;
   profileTelemetry.SetBounds(14,CardHeaderH+8,W-28-28,ProfileCardFullH-CardHeaderH-8-8);profileTelemetry.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
   profileTelemetry.AccessibleDescription=L.En?"Stored DoH benchmark, not current provider health.":"Сохранённый замер DoH, не текущая доступность провайдера.";
   profileSelect.Controls.Add(profileTelemetry);
   UpdatePrecisionProfile();
  }
  // Provider health comes from the endpoint probe, never from a stored benchmark or TCP latency.
  (string text,Color dot) ProviderStatus(){
   var report=health?.Code==HealthMonitor.ProviderDown?health:lastCheck;
   if(report==null){var fresh=FreshBenchmarkStatus();if(fresh.HasValue)return fresh.Value;}
   if(report==null||providerEvidenceRevision!=System.Threading.Volatile.Read(ref evidenceSerial)||providerEvidenceIdentity!=ProviderHealthIdentity())return (L.En?"Unknown":"Не проверен",Theme.Muted);
   if(report.Utc==default||report.Utc>DateTime.UtcNow||DateTime.UtcNow-report.Utc>=Doctor.Freshness)
    return (L.En?"Stale":"Устарело",Theme.Muted);
   if(report.Code==HealthMonitor.ProviderDown&&(report.Kind==HealthKind.Drift||report.Kind==HealthKind.Suspect))
    return (L.En?"No answer":"Не отвечает",Theme.Amber);
   if(report.Kind==HealthKind.Healthy)return (L.En?"Active":"Активен",Theme.Green);   // only after a real health check: a connected bridge alone does not claim it
   return (L.En?"Unknown":"Не проверен",Theme.Muted);
  }
  // A benchmark run in this session, on this network and scope, within its freshness window, probed this very endpoint (DoH + TLS + answers): that is current evidence for it.
  // A run restored from a previous launch (lastBenchStale) or taken on another network never is.
  (string text,Color dot)? FreshBenchmarkStatus(){
   if(lastBenchStale||benchNetwork==null||benchNetwork!=BenchmarkContext()||!Benchmark.IsFresh(lastBench,DateTime.UtcNow,Program.Hosts))return null;
   var r=ResultOf(lastBench);
   if(r==null||r.Truncated||!(r.Servers??Array.Empty<string>()).SequenceEqual(Current.Servers??Array.Empty<string>(),StringComparer.OrdinalIgnoreCase))return null;
   if(Benchmark.CanSelect(r))return (L.En?"Active":"Активен",Theme.Green);
   return (L.En?"No answer":"Не отвечает",Theme.Amber);
  }
  static bool Measured(BenchRun run,out DateTime stamp)=>DateTime.TryParse(run?.StartedUtc,null,System.Globalization.DateTimeStyles.RoundtripKind,out stamp)&&stamp.ToUniversalTime()<=DateTime.UtcNow.AddMinutes(1);
  ProfileResult ResultOf(BenchRun run){var id=Current.SelectionId;var template=Current.Template;return run?.Results?.FirstOrDefault(r=>r.Id==id&&r.Template==template&&r.Final);}
  static string Age(DateTime utc){
   var local=utc.ToLocalTime();var span=DateTime.Now-local;
   if(span.TotalMinutes<1)return L.En?"just now":"только что";
   if(span.TotalMinutes<60)return (int)span.TotalMinutes+(L.En?" min ago":" мин назад");
   if(local.Date==DateTime.Today)return (L.En?"today ":"сегодня ")+local.ToString("HH:mm");
   if(local.Date==DateTime.Today.AddDays(-1))return (L.En?"yesterday ":"вчера ")+local.ToString("HH:mm");
   return local.ToString("dd.MM HH:mm");
  }
  // The same moment as a small caption and a value for the card's last box.
  static (string label,string value) AgeParts(DateTime utc){
   var local=utc.ToLocalTime();var span=DateTime.Now-local;
   if(span.TotalMinutes<1)return (L.En?"measured":"замер",L.En?"now":"сейчас");
   if(span.TotalMinutes<60)return (L.En?"ago":"назад",(int)span.TotalMinutes+(L.En?" min":" мин"));
   if(local.Date==DateTime.Today)return (L.En?"today":"сегодня",local.ToString("HH:mm"));
   if(local.Date==DateTime.Today.AddDays(-1))return (L.En?"yesterday":"вчера",local.ToString("HH:mm"));
   return (local.ToString("HH:mm"),local.ToString("dd.MM"));
  }
  static string Reason(ProfileResult r){
   string code=r.Domains.Select(d=>d.Error).FirstOrDefault(e=>!string.IsNullOrEmpty(e)&&!e.StartsWith("SKIPPED"));
   if(r.BogusAnswer)return L.En?"answers point to a local network":"ответы ведут в локальную сеть";
   return code=="DNS_TIMEOUT_OR_UNREACHABLE"?(L.En?"no DoH answer":"нет ответа DoH")
    :code=="DOH_TLS_FAILURE_OR_UNREACHABLE"?(L.En?"DoH TLS failed":"ошибка TLS у DoH")
    :code=="DNS_INVALID_RESPONSE"?(L.En?"invalid DNS answer":"неверный ответ DNS")
    :code;
  }
  void UpdatePrecisionProfile(){
   if(profiles.Count==0)return;
   string Ms(long value)=>value<0?"—":value+(L.En?" ms":" мс");
   var result=ResultOf(lastBench);
   if(result==null||!Measured(lastBench,out var stamp)||lastBench.Synthetic&&!smoke){
    string hint=L.En?"Open Benchmark to measure this profile":"Откройте «Замер», чтобы измерить профиль";
    profileTelemetry.Show(new TelemetryData{Verdict=L.En?"Not measured":"Нет замера",Hint=hint},(L.En?"Not measured":"Нет замера")+" · "+hint,Trend());
    return;
   }
   string age=Age(stamp.ToUniversalTime());var parts2=AgeParts(stamp.ToUniversalTime());
   if(result.Attempts==0){
    string hint=(L.En?"did not fit the time limit · ":"не успел за отведённое время · ")+age;
    profileTelemetry.Show(new TelemetryData{Verdict=BenchTable.VerdictText("NotMeasured"),Dot=Theme.Muted,Stale=lastBenchStale,Hint=hint},BenchTable.VerdictText("NotMeasured")+" · "+hint,Trend());
    return;
   }
   string responses=result.Successes+"/"+result.Attempts;string responsesLabel=L.En?"responses":"ответов";
   var previous=PreviousMedian(stamp.ToUniversalTime());
   var data=new TelemetryData{
    Verdict=L.En?"DoH benchmark":"Замер DoH",Dot=Color.Empty,Stale=lastBenchStale,NowUtc=DateTime.UtcNow,
    HasMetrics=result.Successes>0,Median=Ms(result.MedianMs),Peak=Ms(result.P95Ms),Tls=Ms(result.TlsMs),ResponsesValue=responses,ResponsesLabel=responsesLabel,
    MedianMs=result.MedianMs,PeakMs=result.P95Ms,TlsMs=result.TlsMs,Availability=result.Attempts>0?(double)result.Successes/result.Attempts:-1,
    AgeLabel=parts2.label,AgeValue=parts2.value,Age=age,Samples=result.Samples??new long[0],Points=History(),
    Share=result.Attempts>0?(int)Math.Round(100.0*result.Successes/result.Attempts)+" %":""};
   if(result.MedianMs>=0&&previous>0){data.HasTrend=true;data.PrevMedianMs=previous;data.TrendPct=TrendPercent(result.MedianMs,previous);}
   // a text for readers that cannot see the boxes: the same values in one line (the screen reader and the tests read it)
   var parts=new List<string>();
   if(result.MedianMs>=0)parts.Add(ProfileTelemetry.LatencyLabel.ToLowerInvariant()+" "+Ms(result.MedianMs));
   if(result.Successes>0)parts.Add("P95 "+Ms(result.P95Ms));
   if(result.Successes>0)parts.Add("TLS "+Ms(result.TlsMs));
   parts.Add(responses+" "+responsesLabel);
   if(result.Successes==0||!result.DomainsOk){var why=Reason(result);if(!string.IsNullOrEmpty(why))parts.Add(why);}
   parts.Add(age);
   if(!data.HasMetrics){var why=Reason(result);data.Hint=string.IsNullOrEmpty(why)?responses+" "+responsesLabel+" · "+age:why+" · "+responses+" "+responsesLabel+" · "+age;}
   profileTelemetry.Show(data,BenchTable.VerdictText(result.Verdict)+" · "+string.Join(" · ",parts),Trend());
  }
  // The median of this profile in its latest real measurement before the shown one (any age; real runs only, never one in which it did not answer); -1 when there is none.
  long PreviousMedian(DateTime shownUtc){
   long best=-1;DateTime bestAt=DateTime.MinValue;
   foreach(var run in benchHistory.Where(x=>!x.Synthetic)){
    if(!Measured(run,out var at))continue;at=at.ToUniversalTime();if(at>=shownUtc||at<=bestAt)continue;
    var r=ResultOf(run);if(r!=null&&r.Attempts>0&&r.Successes>0&&r.MedianMs>=0){best=r.MedianMs;bestAt=at;}
   }
   return best;
  }
  // the change of the median in whole percent: negative = faster than before
  internal static int TrendPercent(long now,long before)=>before<=0?0:(int)Math.Round((now-before)*100.0/before,MidpointRounding.AwayFromZero);
  // The medians of this profile in the stored real measurements of the last half hour, with their times.
  (DateTime Utc,long Ms)[] History(){
   var cutoff=DateTime.UtcNow.AddMinutes(-ProfileTelemetry.WindowMinutes);var list=new List<(DateTime,long)>();
   foreach(var run in benchHistory.Where(x=>!x.Synthetic)){
    if(!Measured(run,out var at))continue;at=at.ToUniversalTime();if(at<cutoff)continue;
    var r=ResultOf(run);if(r!=null&&r.Attempts>0&&r.Successes>0&&r.MedianMs>=0)list.Add((at,r.MedianMs));
   }
   return list.ToArray();
  }
  // Medians of this profile in the stored real measurements, oldest first; -1 marks a run in which it did not answer.
  long[] Trend()=>benchHistory.Where(run=>!run.Synthetic&&Measured(run,out _)).Select(ResultOf).Where(r=>r!=null&&r.Attempts>0)
   .Select(r=>r.Successes>0?r.MedianMs:-1).TakeLast(TrendPoints).ToArray();
  internal string PrecisionMetricsForTest(BenchRun run){lastBench=run;UpdatePrecisionProfile();return profileTelemetry.Summary;}
  internal long[] PrecisionTrendForTest(IEnumerable<BenchRun> history){benchHistory=history.ToList();UpdatePrecisionProfile();return profileTelemetry.Trend;}
  internal ProfileSpec PrecisionProfileForTest=>Current.ToSpec();
  internal bool ProfileCardForTest=>profileTelemetry.Parent==profileSelect&&profileSelect.ClientRectangle.Contains(profileTelemetry.Bounds)&&scopeSel.Bottom<profileSelect.Top;
  internal bool AdaptiveViewportForTest=>nav.Left>=0&&nav.Right<=NativeClientSizeForTest.Width&&nav.Bottom<=NativeClientSizeForTest.Height&&profileSelect.Right<=pages[TabBridge].ClientSize.Width&&scopeSel.Right<=pages[TabBridge].ClientSize.Width&&!pages[TabBridge].HorizontalScroll.Visible;
  internal bool AdaptiveActionsReachableForTest(){
   var page=pages[TabBridge];bool fits=true;
   foreach(Control control in new Control[]{bridge,scopeSel,profileSelect,profileTelemetry,detailsLink}){
    if(!control.Visible)continue;
    page.ScrollControlIntoView(control);Application.DoEvents();
    var bounds=page.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
    if(control.Height<=page.ClientSize.Height&&(bounds.Top<0||bounds.Bottom>page.ClientSize.Height+1))fits=false;
   }
   page.AutoScrollPosition=Point.Empty;UpdateButtons();return fits;
  }
  internal bool AmbientForTest=>bigLogo.AmbientRunning;

  internal bool PrecisionHitRegionForTest=>!bridge.OrbitToggle&&bigLogo.Visible;
  internal (string text,Color dot) Stage01ProviderStatusForTest=>ProviderStatus();
  internal TelemetryData Stage01TelemetryForTest=>profileTelemetry.Data;
  internal int TelemetryWidthForTest=>profileTelemetry.Width;
  internal void Stage01ClickMetricsForTest()=>profileTelemetry.ClickForTest();
  internal void Stage01ClickBenchmarkForTest()=>nav.Selected=TabBench;
  internal int Stage01TabForTest=>nav.Selected;
  internal bool Stage01NeutralCardForTest=>!profileSelect.AccentBorder;
  internal bool Stage01BrowserReadOnlyForTest=>!browserRow.TabStop&&browserRow.AccessibleRole==AccessibleRole.StaticText;
  internal void ClickBrowserChangeForTest()=>browserChange.PerformClickForTest();
  internal bool Stage01QuietEnabledForTest=>tgQuiet.Enabled;
  internal bool Stage01AutostartFileHiddenForTest=>autostartRow.Parent==null;
  internal bool Stage01OnlyOneBridgeActionForTest=>MainButtons.Count(b=>b.Visible)==1;
  internal bool Stage01SingleDiagnosticEntryForTest=>serviceDoctor.Parent==pages[TabBench]&&doctorBtn.Parent==null&&!OpenBenchmarkLinkForTest;
  internal void Stage01SetBenchmarkForTest(BenchRun run){lastBench=run;lastBenchStale=false;benchNetwork=BenchmarkContext();UpdateBenchButtons();}
  internal void Stage01SelectBestForTest()=>SelectBestProfile();
  internal bool Stage01MenuFitsForTest(){
   var viewport=pages[TabBridge].RectangleToScreen(pages[TabBridge].ClientRectangle);var anchor=profileSelect.RectangleToScreen(profileSelect.ClientRectangle);
   int row=(int)Math.Round(34*LayoutScale),gap=(int)Math.Round(4*LayoutScale),desired=profileSelect.Items.Count*row+(int)Math.Round(8*LayoutScale)+4;
   var popup=PillSelect.PopupBounds(viewport,anchor,profileSelect.Width,desired,gap);
   return viewport.Contains(popup)&&popup.Height<=desired&&popup.Width<=profileSelect.Width;
  }
 }
}
