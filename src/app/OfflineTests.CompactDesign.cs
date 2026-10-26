using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 public static partial class OfflineTests {
  static void RunCompactDesignTests(){
   using(var form=new MainForm(true)){
    form.Show();Application.DoEvents();
    Check(form.OffDesktopForTest,"test windows: a render/self-test window opens off the visible desktop, so the cursor and clicks of whoever uses the computer cannot reach it");
    Check(form.ProfileCardForTest,"profile card: metrics and timestamp are inside the selected provider, below services");
    Check(form.PrecisionHitRegionForTest,"compact main: visible brand mark and separate labelled action");
    var profile=form.PrecisionProfileForTest;
    var run=new BenchRun{StartedUtc=DateTime.UtcNow.AddMinutes(-3).ToString("O"),Results=new[]{new ProfileResult{Id=profile.Id,Template=profile.Template,Final=true,Attempts=6,Successes=5,MedianMs=17,P95Ms=41,TlsMs=-1}}};
    Check(form.PrecisionMetricsForTest(null).StartsWith(L.En?"Not measured":"Нет замера"),"precision metrics: missing measurements remain unknown");
    var text=form.PrecisionMetricsForTest(run);
    Check(text.Contains("17")&&text.Contains("41")&&text.Contains("TLS —")&&text.Contains("5/6")&&text.Contains("P95"),"precision metrics: median, measured P95, missing TLS and actual response counts");
    Check(text.Contains(L.En?"min ago":"мин назад"),"precision metrics: freshness is shown as the age of the measurement");
    var many=new BenchRun{StartedUtc=run.StartedUtc,Results=new[]{new ProfileResult{Id=profile.Id,Template=profile.Template,Final=true,Attempts=24,Successes=24,MedianMs=17,P95Ms=41,TlsMs=30,DomainsOk=true,Verdict="Good"}}};
    {var manyText=form.PrecisionMetricsForTest(many);Check(manyText.Contains("P95 41"),"precision metrics: the stored 95th percentile is labelled P95");}
    var dead=new BenchRun{StartedUtc=run.StartedUtc,Results=new[]{new ProfileResult{Id=profile.Id,Template=profile.Template,Final=true,Attempts=2,Successes=0,Verdict="Dead",Domains=new[]{new DomainResult{Host="a.example",Error="DNS_TIMEOUT_OR_UNREACHABLE"},new DomainResult{Host="b.example",Error="SKIPPED_ENDPOINT_DOWN"}}}}};
    text=form.PrecisionMetricsForTest(dead);
    Check(text.Contains("0/2")&&text.Contains(L.En?"no DoH answer":"нет ответа DoH")&&!text.Contains(L.En?"peak":"пик"),"precision metrics: a dead profile explains the failure and shows no latency");
    BenchRun Hist(int minutesAgo,int ok,long median,string template=null)=>new BenchRun{StartedUtc=DateTime.UtcNow.AddMinutes(-minutesAgo).ToString("O"),Results=new[]{new ProfileResult{Id=profile.Id,Template=template??profile.Template,Final=true,Attempts=6,Successes=ok,MedianMs=ok>0?median:-1}}};
    var trend=form.PrecisionTrendForTest(new[]{Hist(50,6,40),Hist(40,0,0),Hist(30,6,55,"https://other.example/dns-query"),new BenchRun{StartedUtc=DateTime.UtcNow.AddMinutes(-20).ToString("O"),Synthetic=true,Results=new[]{new ProfileResult{Id=profile.Id,Template=profile.Template,Final=true,Attempts=6,Successes=6,MedianMs=1}}},Hist(10,6,60)});
    Check(trend.SequenceEqual(new long[]{40,-1,60}),"precision trend: only real runs of this exact profile, failures kept as gaps");
    form.PrecisionTrendForTest(new BenchRun[0]);
    run.StartedUtc=DateTime.UtcNow.AddHours(1).ToString("O");
    Check(form.PrecisionMetricsForTest(run).StartsWith(L.En?"Not measured":"Нет замера"),"precision metrics: future timestamps cannot become current measurements");
    run.StartedUtc=DateTime.UtcNow.ToString("O");run.Results[0].Template="https://other.example/dns-query";
    Check(form.PrecisionMetricsForTest(run).StartsWith(L.En?"Not measured":"Нет замера"),"precision metrics: a different provider template cannot supply the selected profile's metrics");
   }
   foreach(var display in new[]{(Width:640,Height:480,Dpi:96),(Width:600,Height:600,Dpi:192),(Width:2560,Height:1440,Dpi:192),(Width:3840,Height:2160,Dpi:288)}){
    using(var form=new MainForm(true)){
     form.Show();Application.DoEvents();UiSurfaceCheck.ChangeDpi(form,display.Dpi);
     form.FitWorkAreaForTest(new Rectangle(0,0,display.Width,display.Height));Application.DoEvents();
     Check(form.AdaptiveViewportForTest&&form.ProfileCardForTest&&form.AdaptiveActionsReachableForTest(),"adaptive display: "+display.Width+"x"+display.Height+" @ "+display.Dpi+" DPI keeps navigation visible and metrics inside the card without horizontal scrolling");
    }
   }
   bool original=L.En;var problems=new List<string>();int renders=0;
   try{
    foreach(bool english in new[]{false,true}){
     L.En=english;
     using(var form=new MainForm(true)){
      form.Show();Application.DoEvents();
      foreach(int dpi in new[]{96,120,144,192}){
       UiSurfaceCheck.ChangeDpi(form,dpi);Application.DoEvents();
       string at=(english?"en ":"ru ")+dpi;
       if(!form.FixedWindowForTest)problems.Add(at+" native frame/size constraints: size="+form.Size+" min="+form.MinimumSize+" max="+form.MaximumSize+" dpi="+form.DeviceDpi);
       var size=form.Size;
       form.Size=new Size(size.Width+200,size.Height+300);Application.DoEvents();
       form.SystemCommandForTest(0xF030);form.SystemCommandForTest(0xF000);Application.DoEvents();
       if(form.Size!=size||form.WindowState!=FormWindowState.Normal)problems.Add(at+" resizing/maximize accepted");
       form.SmokeState("idle");
       if(dpi<=144&&!form.CompactBodyForTest())problems.Add(at+" main actions require scrolling");
       for(int tab=0;tab<form.TabCount;tab++){
        form.SelectTab(tab);Application.DoEvents();
        if(form.PageTopUnits(tab)<0||form.ChromeBottomForTest<=0||form.NavControl.Bottom>form.ClientSize.Height)problems.Add(at+" chrome/nav overlap tab "+tab);
       }
       var table=form.BenchForTest;table.Rows.Clear();
       long[] values={1,11,111,1111,21,31};
       for(int i=0;i<values.Length;i++)table.Rows.Add(new BenchRow{Id="compact"+i,Name="Provider "+(i+1),Result=new ProfileResult{Final=true,Rank=i+1,Attempts=1,Availability=1,MedianMs=values[i],P95Ms=values[i],TlsMs=values[i],Verdict="Good"}});
       form.SelectTab(1);Application.DoEvents();
       using(var bitmap=new Bitmap(table.Width,table.Height)){
        table.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));renders++;
        float k=form.DeviceDpi/96f,rh=Math.Min(60*k,(table.Height-26*k-6*k)/table.Rows.Count);
        var rightEdges=new List<int>();var availabilityEdges=new List<int>();
        for(int row=0;row<table.Rows.Count;row++){
         int top=(int)Math.Ceiling(26*k+row*rh+4*k),bottom=(int)Math.Floor(26*k+(row+1)*rh-7*k);
         int min=bitmap.Width,max=-1;
         for(int y=top;y<bottom;y++)for(int x=(int)(8*k);x<(int)(30*k);x++){
          var pixel=bitmap.GetPixel(x,y);if(pixel.R>90&&pixel.G>90&&pixel.B>90){min=Math.Min(min,x);max=Math.Max(max,x);}
         }
         if(max<0||Math.Abs((min+max+1)/2f-19*k)>2*k)problems.Add(at+" rank off centre row "+row);
         int edge=-1;
         for(int y=top;y<bottom;y++)for(int x=(int)(234*k);x<(int)(290*k);x++){
          var pixel=bitmap.GetPixel(x,y);if(Math.Max(pixel.R,Math.Max(pixel.G,pixel.B))>100)edge=Math.Max(edge,x);
         }
         if(edge<0)problems.Add(at+" missing median row "+row);else rightEdges.Add(edge);
         edge=-1;for(int y=top;y<bottom;y++)for(int x=(int)(182*k);x<(int)(234*k);x++){var pixel=bitmap.GetPixel(x,y);if(Math.Max(pixel.R,Math.Max(pixel.G,pixel.B))>100)edge=Math.Max(edge,x);}
         if(edge<0)problems.Add(at+" missing availability row "+row);else availabilityEdges.Add(edge);
        }
        if(rightEdges.Count>0&&rightEdges.Max()-rightEdges.Min()>1)problems.Add(at+" median right edges drift");
        if(availabilityEdges.Count>0&&availabilityEdges.Max()-availabilityEdges.Min()>1)problems.Add(at+" availability right edges drift");
       }
      }
      UiSurfaceCheck.ChangeDpi(form,120);form.CaptionMinimizeForTest();Application.DoEvents();
      Check(form.WindowState==FormWindowState.Minimized,"compact chrome: minimize button works");
      form.ShowWindow();Application.DoEvents();Check(form.WindowState==FormWindowState.Normal&&form.FixedWindowForTest,"compact chrome: restore retains fixed size");
      form.CaptionCloseForTest();Application.DoEvents();Check(form.IsDisposed,"compact chrome: close uses the existing form lifecycle in the test window");
     }
    }
   }finally{L.En=original;}
   Check(problems.Count==0,"COMPACT_DESIGN: fixed borderless window, blocked resizing/maximize, visible primary actions, aligned numeric columns — "+renders+" renders"+(problems.Count==0?"":" — "+string.Join("; ",problems.Take(10))));
   double Light(Color c){double C(byte v){double n=v/255d;return n<=.04045?n/12.92:Math.Pow((n+.055)/1.055,2.4);}return .2126*C(c.R)+.7152*C(c.G)+.0722*C(c.B);}
   double Contrast(Color a,Color b)=>(Math.Max(Light(a),Light(b))+.05)/(Math.Min(Light(a),Light(b))+.05);
   Check(Contrast(Theme.Text,Theme.Bg)>=7&&Contrast(Theme.Muted,Theme.Card)>=4.5,"compact theme: tonal surfaces preserve text and muted contrast");
  }
 }
}
