using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 // The profile card of the bridge in the layout of the reference (status dot, age + refresh mark, «Задержка» with its trend and the share of answers,
 // three fixed labels under the ruler) and the status of the profile in its header.
 public static partial class OfflineTests {
  static void RunStage07Tests(){
   bool english=L.En;
   try{
    // the trend: a whole percent against the previous real measurement, with its arrow and tone
    Check(MainForm.TrendPercent(79,90)==-12&&MainForm.TrendPercent(108,100)==8&&MainForm.TrendPercent(100,100)==0&&MainForm.TrendPercent(50,0)==0,"trend percent is the change of the median against the previous one (79 vs 90 → −12, 108 vs 100 → +8)");
    Check(ProfileTelemetry.TrendText(-12)=="↓ 12 %"&&ProfileTelemetry.TrendText(8)=="↑ 8 %"&&ProfileTelemetry.TrendText(0)=="→ 0 %","trend text: «↓ 12 %», «↑ 8 %», «→ 0 %»");
    Check(ProfileTelemetry.TrendTone(-12,79)==Theme.Green&&ProfileTelemetry.TrendTone(8,120)==Theme.Amber&&ProfileTelemetry.TrendTone(8,900)==Theme.Red&&ProfileTelemetry.TrendTone(0,79)==Theme.Muted,"faster is green, slower amber (red once the median is in the red zone), the same is muted");
    foreach(bool en in new[]{false,true}){
     L.En=en;
     Check(ProfileTelemetry.LatencyLabel==(en?"Latency":"Задержка"),"the first column is «Задержка» / «Latency» "+en);
     using(var form=new MainForm(true)){
      form.Show();Application.DoEvents();form.ApplyLanguage(en);
      var spec=form.PrecisionProfileForTest;
      BenchRun Run(DateTime at,long median,int ok,int attempts,bool synthetic=false,string id=null){
       var r=SampleRun(spec,new long[]{median-10,median,median+30},median,median+60,median+120,ok,attempts);
       if(id!=null)r.Results[0].Id=id;
       r.StartedUtc=at.ToString("o");r.Synthetic=synthetic;return r;
      }
      var now=DateTime.UtcNow;
      var prev=Run(now.AddHours(-3),90,6,6);var cur=Run(now.AddMinutes(-2),79,6,6);
      form.PrecisionTrendForTest(new[]{prev,cur});form.PrecisionMetricsForTest(cur);
      var d=form.Stage01TelemetryForTest;
      Check(d.HasTrend&&d.TrendPct==-12&&d.PrevMedianMs==90,"the trend compares with the previous real measurement of this profile, whatever its age "+en+" ("+d.HasTrend+" "+d.TrendPct+")");
      Check(d.Share=="100 %","the share of answers is successes / attempts (6/6 → 100 %) "+en+" ("+d.Share+")");
      var five=Run(now.AddMinutes(-1),79,5,6);form.PrecisionMetricsForTest(five);
      Check(form.Stage01TelemetryForTest.Share=="83 %","5 of 6 answers is 83 % "+en);
      // nothing to compare with: no trend is invented
      form.PrecisionTrendForTest(new[]{cur});form.PrecisionMetricsForTest(cur);
      Check(!form.Stage01TelemetryForTest.HasTrend,"a single measurement shows no trend "+en);
      var synthetic=Run(now.AddHours(-1),200,6,6,true);var dead=Run(now.AddHours(-2),150,0,6);var other=Run(now.AddHours(-3),400,6,6,false,"another-profile");var later=Run(now.AddMinutes(1),60,6,6);
      form.PrecisionTrendForTest(new[]{synthetic,dead,other,later,cur});form.PrecisionMetricsForTest(cur);
      Check(!form.Stage01TelemetryForTest.HasTrend,"synthetic runs, runs in which the profile did not answer, other profiles and later runs are never the «previous» one "+en);
      var olderGood=Run(now.AddHours(-5),100,6,6);
      form.PrecisionTrendForTest(new[]{olderGood,synthetic,dead,other,cur});form.PrecisionMetricsForTest(cur);
      Check(form.Stage01TelemetryForTest.HasTrend&&form.Stage01TelemetryForTest.PrevMedianMs==100&&form.Stage01TelemetryForTest.TrendPct==-21,"the nearest earlier real measurement is used, the unsuitable ones in between are skipped "+en);
      var slower=Run(now.AddMinutes(-30),70,6,6);form.PrecisionTrendForTest(new[]{slower,cur});form.PrecisionMetricsForTest(cur);
      Check(form.Stage01TelemetryForTest.TrendPct==13,"a slower median gives a positive percent "+en);
      // the summary read by screen readers uses the same name
      Check(form.PrecisionMetricsForTest(cur).Contains(ProfileTelemetry.LatencyLabel.ToLowerInvariant()+" "),"the line for screen readers says «"+ProfileTelemetry.LatencyLabel.ToLowerInvariant()+"» "+en);

      // the status of the profile: «Активен» only after a real health check
      form.JournalReader=()=>"{\"Phase\":\"Active\"}";form.PresentForTest(HealthyStatus);
      form.PrecisionMetricsForTest(cur);
      Check(form.Stage01ProviderStatusForTest.text==(en?"Unknown":"Не проверен")&&form.Stage01ProviderStatusForTest.dot!=Theme.Green,"a connected bridge with only a stored benchmark is «Не проверен», not «Активен» "+en);
      form.ApplyHealth(new HealthReport{Kind=HealthKind.Healthy,Code=HealthMonitor.Ok,Utc=DateTime.UtcNow});
      Check(form.Stage01ProviderStatusForTest.text==(en?"Active":"Активен")&&form.Stage01ProviderStatusForTest.dot==Theme.Green,"after a real healthy check the card says «Активен» "+en);

      // the dot before the profile line: green when connected and checked, muted otherwise, none under a failure text
      form.SmokeState("active");Application.DoEvents();
      Check(form.ProfileLineDotForTest==Theme.Green,"connected and checked → a green dot before the profile line "+en);
      form.SmokeState("active-unchecked");Application.DoEvents();
      Check(form.ProfileLineDotForTest==Theme.Muted,"connected but not checked → a muted dot "+en);
      form.SmokeState("idle");Application.DoEvents();
      Check(form.ProfileLineDotForTest==Theme.Muted,"disconnected → a muted dot "+en);
      form.SmokeState("recovery");Application.DoEvents();
      Check(form.ProfileLineDotForTest==Color.Empty,"a failure text under the title has no dot "+en);

      // both card heights hold the board with its three bands
      Check(ProfileTelemetry.MinBoardUnits<=134-38-16,"the three bands fit the card at its smaller height (134 units) "+en+" ("+ProfileTelemetry.MinBoardUnits+" of "+(134-38-16)+")");
      foreach(string mode in new[]{"active","recovery"}){
       form.SmokeState(mode);form.PrecisionTrendForTest(new[]{slower,cur});form.PrecisionMetricsForTest(cur);Application.DoEvents();
       using(var bmp=new Bitmap(form.ProfileBounds.Width,form.ProfileBounds.Height)){
        form.ProfileControl.DrawToBitmap(bmp,new Rectangle(Point.Empty,bmp.Size));
        Check(bmp.Width>0&&bmp.GetPixel(bmp.Width/2,bmp.Height/2).A==255&&form.MainGeometryProblems().Count==0,"the card with trend, share, age and refresh mark paints in state "+mode+" "+en);
       }
      }
     }
    }
    // the three labels under the ruler stand on three anchors and never meet, at every scale and in both languages
    var samples=new[]{new long[]{38,57,296},new long[]{7,8,9},new long[]{1200,1500,3000},new long[]{67,79,194},new long[]{799,800,9999}};
    foreach(bool en in new[]{false,true})foreach(int dpi in new[]{96,120,144,192}){
     L.En=en;
     using(var form=new MainForm(true)){
      form.Show();Application.DoEvents();UiSurfaceCheck.ChangeDpi(form,dpi);form.ApplyLanguage(en);Application.DoEvents();
      float k=dpi/96f;int width=form.TelemetryWidthForTest;
      using(var g=form.CreateGraphics())
      foreach(var v in samples){
       var rects=ProfileTelemetry.SpreadLabelRects(g,width,k,v[0],v[1],v[2],out int variant);
       bool apart=rects[0].Right+4*k<=rects[1].Left&&rects[1].Right+4*k<=rects[2].Left;
       Check(apart,"min / median / max labels do not touch "+(en?"EN":"RU")+" @"+dpi+" "+string.Join("/",v)+" (variant "+variant+")");
       Check(Math.Abs(rects[0].Left)<.5f&&Math.Abs(rects[2].Right-width)<.5f&&Math.Abs((rects[1].Left+rects[1].Right)/2f-width/2f)<.5f,"left at the left edge, right at the right edge, the median label in the middle of the card (not at the marker) "+(en?"EN":"RU")+" @"+dpi+" "+string.Join("/",v));
       if(v[2]<10000&&v[0]!=1200)Check(variant==0,"ordinary figures keep the full words «"+(en?"median":"медиана")+"» "+(en?"EN":"RU")+" @"+dpi+" "+string.Join("/",v)+" (variant "+variant+")");
      }
      // a band much narrower than the card: the words give way, the labels still do not overlap
      using(var g=form.CreateGraphics()){
       var rects=ProfileTelemetry.SpreadLabelRects(g,130,1f,38,57,296,out int variant);
       Check(variant>=1&&rects[0].Right<=rects[1].Left&&rects[1].Right<=rects[2].Left,"in a narrow band «медиана» becomes «мед.» before anything can overlap "+(en?"EN":"RU")+" @"+dpi+" (variant "+variant+")");
      }
     }
    }
   }finally{L.En=english;}
  }
 }
}
