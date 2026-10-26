using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 // UI tests: autostart, immediate scope changes, profile latency,
 // the browser screen (order, one-sentence verdict, «Назад»), the language switched without a restart, the symmetric highlight of the tab bar.
 public static partial class OfflineTests {
  static bool PumpUntil(Func<bool> done,int maxMs=5000){var sw=Stopwatch.StartNew();while(!done()&&sw.ElapsedMilliseconds<maxMs){Application.DoEvents();System.Threading.Thread.Sleep(5);}Application.DoEvents();return done();}
  static bool IsPill(Bitmap b,int x,int y){var c=b.GetPixel(x,y);return Math.Abs(c.R-Theme.NavSel.R)+Math.Abs(c.G-Theme.NavSel.G)+Math.Abs(c.B-Theme.NavSel.B)<=6;}

  static void RunUiV4Tests() {
   RunVisualBrandTests();RunCompactDesignTests();
   RunS11Tests();RunV4NavTests();RunV4ControlTests();RunV4ScaleTests();RunV4AutostartUiTests();RunV4ProfileChoiceTests();RunV4ScopeTests();RunV4LanguageTests();RunV4BrowserOrderTests();RunV4ReviewTests();
  }

  // The selected navigation sector meets the panel edges. Validate actual pixels,
  // including first/last sectors, fractional DPI and both label sets.
  static void RunV4NavTests() {
   bool en0=L.En;var bad=new List<string>();int shots=0;
   try {
    foreach(bool en in new[]{false,true}){
     L.En=en;
     foreach(float k in new[]{1f,1.25f,1.5f,1.75f,2f}){
      int w=(int)Math.Round(420*k),h=(int)Math.Round(60*k);
      for(int sel=0;sel<NavBar.Count;sel++){
       using(var bmp=new Bitmap(w,h)){
        bmp.SetResolution(96*k,96*k);
        using var g=Graphics.FromImage(bmp);g.Clear(Theme.Bg);
        NavBar.Paint(g,w,h,k,sel);shots++;
        var sector=NavBar.Pill(sel,w,h,k,NavBar.Count);
        string who=(en?"en ":"ru ")+(int)(k*96)+"dpi tab"+sel;
        if(sector.Top!=0||sector.Bottom!=h)bad.Add(who+" detached selection");
        foreach(int x in new[]{sector.Left+2,sector.Right-3}){
         if(!IsPill(bmp,x,h-2))bad.Add(who+" bottom corner is inset or rounded");
         if(!IsPill(bmp,x,(int)(4*k)))bad.Add(who+" top corner is inset or rounded");
        }
        var accent=bmp.GetPixel(sector.Left+sector.Width/2,0);
        if(accent.ToArgb()==Theme.AccentHover.ToArgb())bad.Add(who+" hard accent line on top of the selection");
        for(int i=0;i<NavBar.Count;i++)if(i!=sel){
         var other=NavBar.Pill(i,w,h,k,NavBar.Count);
         if(IsPill(bmp,other.Left+other.Width/2,h-2))bad.Add(who+" selection leaks into another sector");
        }
        for(int part=0;part<2;part++){
         int minX=w,maxX=-1;
         int top=part==0?(int)(7*k):h/2,bottom=part==0?h/2:h-(int)(6*k);
         for(int y=top;y<bottom;y++)for(int x=sector.Left+1;x<sector.Right-1;x++){
          var px=bmp.GetPixel(x,y);
          if(px.R>120&&px.G>120&&px.B>120){minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);}
         }
         if(maxX<0)bad.Add(who+" missing "+(part==0?"icon":"label"));
         else if(Math.Abs((minX+maxX+1)/2.0-(sector.Left+sector.Right)/2.0)>2+ k)bad.Add(who+" content off centre");
        }
       }
      }
      var first=NavBar.Pill(0,w,h,k,NavBar.Count);var last=NavBar.Pill(NavBar.Count-1,w,h,k,NavBar.Count);
      if(first.Left!=0||last.Right!=w)bad.Add("navigation does not meet outer edges");
      for(int i=1;i<NavBar.Count;i++)if(NavBar.Pill(i-1,w,h,k,NavBar.Count).Right!=NavBar.Pill(i,w,h,k,NavBar.Count).Left)bad.Add("gap between sectors");
     }
    }
    Check(bad.Count==0,"tab bar: cast full-height selection, square connected corners, no accent line, centred icons and labels, no gaps or leakage — "+shots+" renders"+(bad.Count==0?"":" — "+string.Join("; ",bad.Take(8))));
    using(var nav=new NavBar()){
     int changes=0;nav.SelectedChanged+=(_,__)=>changes++;
     nav.Selected=-1;nav.Selected=NavBar.Count;Check(nav.Selected==0&&changes==0,"tab bar: invalid selection preserves the current page");
     nav.Selected=4;nav.Selected=4;Check(nav.Selected==4&&changes==1&&nav.AccessibleName==L.T(NavBar.Labels[4]),"tab bar: one change event and accessible name for the selected page");
    }
    using(var form=new MainForm(true)){
     form.Show();Application.DoEvents();var nav=form.NavControl;
     Check(nav.Bottom==form.ClientSize.Height&&nav.Left==0&&nav.Width==form.ClientSize.Width,"tab bar: initial panel meets the client bottom and both sides");
     var fixedSize=form.NativeClientSizeForTest;form.ClientSize=new Size(form.ClientSize.Width+160,form.ClientSize.Height+300);Application.DoEvents();
     Check(form.NativeClientSizeForTest==fixedSize&&form.FixedWindowForTest&&nav.Bottom==fixedSize.Height,"compact: attempted resizing preserves the native fixed canvas and bottom navigation");
     form.Close();
    }
   }finally{L.En=en0;}
  }

  // the controls of this pass: levels while busy, the field with its ping
  static void RunV4ControlTests() {
   using(var seg=new ServicePicker{Bounds=new Rectangle(0,0,320,36),Items=new[]{"A","B","C"}}) {
    seg.SetOn(true,false,false);int changes=0;seg.SelectionChanged+=(_,__)=>changes++;
    using(var calm=new Bitmap(320,36))using(var busy=new Bitmap(320,36)) {
     seg.DrawToBitmap(calm,new Rectangle(0,0,320,36));
     seg.Busy=true;seg.DrawToBitmap(busy,new Rectangle(0,0,320,36));
     int diff=0;for(int y=4;y<32;y++)for(int x=8;x<200;x++){var p=calm.GetPixel(x,y);var q=busy.GetPixel(x,y);if(Math.Abs(p.R-q.R)+Math.Abs(p.G-q.G)+Math.Abs(p.B-q.B)>12)diff++;}
     Check(diff>200,"levels: while a choice is being applied the highlight looks different (busy tone), so the control does not look idle or greyed out ("+diff+" px)");
    }
    seg.ClickForTest(1);Check(changes==0&&!seg.IsOn(1),"levels: while busy a click changes nothing");
    seg.Busy=false;seg.ClickForTest(1);Check(changes==1&&seg.IsOn(1),"levels: once the work is done a click works again");
   }
   // the profile field: the name on the left, the ping (dot and words) before the chevron; a long name gives way, the ping never does
   float sysK;using(var probe=new Panel())sysK=probe.DeviceDpi/96f;   // a control paints at the scale of the screen it is on
   foreach(float k in new[]{sysK}) {
    int w=(int)Math.Round(332*k),h=(int)Math.Round(38*k);
    using(var host=new Panel{BackColor=Theme.Bg,Bounds=new Rectangle(0,0,w,h)})using(var ps=new PillSelect{Bounds=new Rectangle(0,0,w,h),Font=Theme.F(9.5f)}) {
     host.Controls.Add(ps);
     ps.Items.Add("dns.a-very-long-provider-name.example · P9  ·  эксп.");ps.SetSelectedSilently(0);
     Color dot=Theme.Green;string text="123 мс";ps.Detail=i=>(text,dot);
     using(var bmp=new Bitmap(w,h)) {
      host.DrawToBitmap(bmp,new Rectangle(0,0,w,h));
      int dotMin=int.MaxValue,dotMax=-1;for(int y=0;y<h;y++)for(int x=0;x<w;x++){var p=bmp.GetPixel(x,y);if(Math.Abs(p.R-dot.R)+Math.Abs(p.G-dot.G)+Math.Abs(p.B-dot.B)<40){dotMin=Math.Min(dotMin,x);dotMax=Math.Max(dotMax,x);}}
      int nameMax=-1,detailMin=int.MaxValue,detailMax=-1;for(int y=0;y<h;y++)for(int x=0;x<w;x++){var p=bmp.GetPixel(x,y);if(Math.Min(p.R,Math.Min(p.G,p.B))>=170){if(dotMax>=0&&x<dotMin)nameMax=Math.Max(nameMax,x);else if(dotMax>=0&&x>dotMax){detailMin=Math.Min(detailMin,x);detailMax=Math.Max(detailMax,x);}}}
      Check(dotMax>=0&&dotMin>w*.55&&dotMax<w-30*k&&nameMax>0&&nameMax<=dotMin-3*k&&detailMin>dotMax&&detailMax<w-30*k,"profile field "+(int)(k*96)+"%: the green dot and «123 мс» sit before the chevron and the long name stops short of them (name ends "+nameMax+", dot "+dotMin+"–"+dotMax+", words "+detailMin+"–"+detailMax+" of "+w+")");
      text="";ps.Invalidate();host.DrawToBitmap(bmp,new Rectangle(0,0,w,h));
      bool anyDot=false;for(int y=0;y<h&&!anyDot;y++)for(int x=0;x<w;x++){var p=bmp.GetPixel(x,y);if(Math.Abs(p.R-dot.R)+Math.Abs(p.G-dot.G)+Math.Abs(p.B-dot.B)<40){anyDot=true;break;}}
      Check(!anyDot,"profile field: no ping known -> no dot and no «—» in the field itself");
     }
    }
   }
   // a row of the list: a known ping has its dot and words, an unknown one is «—» without a dot
   using(var bmp=new Bitmap(120,30))using(var g=Graphics.FromImage(bmp))using(var f=Theme.F(9.5f)) {
    g.Clear(Theme.Card);PillSelect.PaintDetail(g,f,("",Color.Empty),new Rectangle(0,0,120,30),1f,true);
    int ink=0,green=0;for(int y=0;y<30;y++)for(int x=0;x<120;x++){var p=bmp.GetPixel(x,y);if((p.R+p.G+p.B)/3>70)ink++;if(Math.Abs(p.R-Theme.Green.R)+Math.Abs(p.G-Theme.Green.G)+Math.Abs(p.B-Theme.Green.B)<40)green++;}
    Check(ink>5&&green==0,"profile list: a row whose ping is not known shows «—» and no dot");
    g.Clear(Theme.Card);PillSelect.PaintDetail(g,f,("42 мс",Theme.Green),new Rectangle(0,0,120,30),1f,true);
    green=0;for(int y=0;y<30;y++)for(int x=0;x<120;x++){var p=bmp.GetPixel(x,y);if(Math.Abs(p.R-Theme.Green.R)+Math.Abs(p.G-Theme.Green.G)+Math.Abs(p.B-Theme.Green.B)<40)green++;}
    Check(green>=20,"profile list: a row with a known ping shows its dot");
   }
  }

  // 3. the dense block under the logo at the scale of this screen (the other scales: a fresh process per scale, --ui-smoke --dpi N, see verify.ps1)
  static void RunV4ScaleTests() {
   bool en0=L.En;var bad=new List<string>();
   try {
    foreach(bool en in new[]{false,true}){
     L.En=en;
     using(var form=new MainForm(true)) {
      form.Show();Application.DoEvents();
      foreach(var p in form.ScaleProblems())bad.Add((en?"en ":"ru ")+p);
      form.Close();
     }
    }
   } finally { L.En=en0; }
   Check(bad.Count==0,"main block: at this screen's scale, in both languages, the button, the profile field and the levels keep equal gaps, equal fields and equal margins, nothing overlaps, everything stays on the page"+(bad.Count==0?"":" — "+string.Join("; ",bad.Take(6))));
  }

  // profile choice: a row of the measurement picks the profile; a connected bridge follows it
  static void RunV4ProfileChoiceTests() {
   using var form=new MainForm(true);
   form.Show();Application.DoEvents();
   int applied=0;form.ApplyProfileHook=()=>{applied++;return Task.CompletedTask;};
   var ids=ProfileCatalog.BuiltIn.Profiles.Select(p=>p.Id).ToArray();
   form.SmokeState("idle");string first=form.CurrentProfileId,other=ids.First(x=>x!=first);
   form.ClickBenchRowForTest(other);
   Check(form.CurrentProfileId==other&&form.BenchRowSelected(other)&&applied==0,"profile choice: a click on a measurement row selects that profile; an idle bridge is not touched");
   form.SmokeState("active");
   Check(form.ProfileSelectEnabled,"profile choice: the profile list stays open while the bridge is connected");
   string third=ids.First(x=>x!=other);
   form.ClickBenchRowForTest(third);
   Check(form.CurrentProfileId==third&&applied==1,"profile choice: on a connected bridge the chosen profile is applied at once");
   form.ClickBenchRowForTest(third);
   Check(applied==1,"profile choice: the profile already in use is not applied again");
  }

  // 1. autostart in the window
  static void RunV4AutostartUiTests() {
   const string isoKey=@"Software\AgDnsTest\RunUi";string realKey=AutostartHelper.KeyName;
   Microsoft.Win32.Registry.CurrentUser.CreateSubKey(isoKey).Dispose();AutostartHelper.KeyName=isoKey;
   string dir=Path.Combine(Path.GetTempPath(),"agdns-asui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
   bool en0=L.En;
   try {
    string self=Environment.ProcessPath;string other=Path.Combine(dir,"AntigravityGeminiDns.exe");File.Copy(self,other,true);
    string foreign=Path.Combine(dir,"foreign","PING.EXE");Directory.CreateDirectory(Path.GetDirectoryName(foreign));File.Copy(Environment.SystemDirectory+@"\PING.EXE",foreign,true);
    AutostartHelper.SetEnabled(true,other);
    using(var form=new MainForm(true)) {
     form.Show();Application.DoEvents();form.SelectTab(2);Application.DoEvents();
     Check(!form.AutostartOn&&form.AutostartSubtitle=="Автозапуск ведёт на другую копию"&&!form.PreviousCopyVisible&&AutostartHelper.SamePath(AutostartHelper.CurrentTarget(),other),"autostart window: an entry that names another copy shows the switch off and says so; the window asked nothing and changed nothing when it started");
     var rows0=form.SettingsRows;
     form.ToggleAutostartForTest(true);   // a question here would block the test: there is none
     Check(form.AutostartOn&&AutostartHelper.IsEnabled(self)&&form.AutostartSubtitle=="Программа следит за состоянием моста"&&form.PreviousCopyVisible&&form.PreviousCopyText=="Прежняя копия: "+other,"autostart window: turning it on moves the entry to this exe without a word and shows «Прежняя копия: <путь>» under the switch");
     float s=form.LayoutScale;int pageH=form.SettingsContentHeight;
     // rows[1..5] are inside the first card (card coordinates); the cards themselves are page coordinates that include the scroll offset.
     // Each comparison stays in one coordinate space, so the check holds whether or not the page happens to be scrolled (focus can scroll it).
     bool Apart(Rectangle[] r,int scroll)=>r[1].Bottom<=r[2].Top+1&&r[2].Bottom<=r[3].Top+1&&r[3].Bottom<=r[4].Top+1&&r[4].Bottom<=r[5].Top+1&&r[5].Bottom<=r[0].Height&&r[0].Bottom<=r[6].Top&&r[6].Bottom-scroll<=pageH&&r[3].Top>rows0[3].Top&&r[2].Height>=(int)(30*s);
     bool ApartAt(bool footer){int scroll=form.ScrollSettingsForTest(footer);return Apart(form.SettingsRows,scroll);}   // scroll first, then read the rows at that offset
     bool apart=ApartAt(false)&&ApartAt(true)&&form.SettingsFooterReachableForTest();
     form.ScrollSettingsForTest(false);
     Check(apart,"autostart window: the row «Прежняя копия» takes its own room between the switch and the next row; the cards below move down and stay on the page"+(apart?"":" (rows "+string.Join(";",form.SettingsRows.Select(r=>r.Top+"-"+r.Bottom))+", page "+pageH+")"));
     // delete: asked first; refused while running; never a foreign file; only then gone
     string asked=null,notice=null;form.DeleteConfirm=p=>{asked=p;return false;};form.Notice=n=>notice=n;form.RunningProbe=p=>false;
     form.ClickDeletePreviousCopyForTest();
     Check(asked==other&&File.Exists(other)&&form.PreviousCopyVisible,"autostart window: «Удалить» asks first; «Нет» leaves the file and the row");
     form.DeleteConfirm=p=>true;form.RunningProbe=p=>true;form.ClickDeletePreviousCopyForTest();
     Check(File.Exists(other)&&form.PreviousCopyVisible&&notice!=null&&notice.Contains("запущена"),"autostart window: a previous copy that is running is not deleted, the person is told why");
     form.RunningProbe=p=>false;form.ClickDeletePreviousCopyForTest();
     Check(!File.Exists(other)&&File.Exists(self)&&!form.PreviousCopyVisible&&form.SettingsPreviousCopy()=="","autostart window: confirmed and not running -> the file is deleted, the row goes");
     Check(form.SettingsRows[3].Top==rows0[3].Top,"autostart window: the cards go back up when the row is gone");
     notice=null;form.SetPreviousCopyForTest(foreign);Check(form.PreviousCopyVisible,"autostart window: a remembered file that exists is shown");
     form.ClickDeletePreviousCopyForTest();
     Check(File.Exists(foreign)&&notice!=null&&notice.Contains("не файл этой программы")&&!form.PreviousCopyVisible,"autostart window: a file that is not this program is never deleted, even if it was remembered");
     form.SetPreviousCopyForTest(Path.Combine(dir,"vanished.exe"));
     Check(!form.PreviousCopyVisible,"autostart window: a remembered file that has vanished is forgotten");
     form.ToggleAutostartForTest(false);
     Check(!form.AutostartOn&&AutostartHelper.CurrentTarget()==null,"autostart window: turning it off removes the entry");
     form.ToggleAutostartForTest(true);
     Check(form.AutostartOn&&AutostartHelper.IsEnabled(self)&&!form.PreviousCopyVisible,"autostart window: turning it on with no entry names this exe and offers nothing to delete");
     // «Файл автозапуска»: the chosen exe goes into the entry, the switch stays on, and a later off/on keeps the chosen file
     form.AutostartFilePicker=()=>foreign;form.ChooseAutostartFileForTest();
     Check(form.AutostartOn&&AutostartHelper.SamePath(AutostartHelper.CurrentTarget(),foreign)&&form.AutostartSubtitle=="Программа следит за состоянием моста","autostart file: a chosen exe is what autostart runs, the switch shows it on");
     form.ToggleAutostartForTest(false);form.ToggleAutostartForTest(true);
     Check(AutostartHelper.SamePath(AutostartHelper.CurrentTarget(),foreign),"autostart file: switching off and on keeps the chosen file");
     form.AutostartFilePicker=()=>self;form.ChooseAutostartFileForTest();
     Check(form.AutostartOn&&AutostartHelper.IsEnabled(self),"autostart file: choosing this copy again points the entry back here");
    }
    AutostartHelper.SetEnabled(false);
    // the row in English, and a long path still leaves the delete button its room
    L.En=true;
    using(var form=new MainForm(true)) {
     form.Show();Application.DoEvents();form.SelectTab(2);Application.DoEvents();
     string deep=Path.Combine(dir,"a very long folder name that goes on and on","and another one","AntigravityGeminiDns.exe");Directory.CreateDirectory(Path.GetDirectoryName(deep));File.Copy(self,deep,true);
     form.SetPreviousCopyForTest(deep);
     Check(form.PreviousCopyText=="Previous copy: "+deep&&form.AllVisibleTexts().Contains("Previous copy: "+deep)&&form.AllVisibleTexts().Contains("Delete"),"autostart window: the row is in English too (a switch rewrites it)");
     var rr=form.SettingsRows;Check(rr[2].Right<=rr[0].Right,"autostart window: the row stays inside its card with a long path");
    }
   } finally { L.En=en0;AutostartHelper.KeyName=realKey;try{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\AgDnsTest",false);}catch{}try{Directory.Delete(dir,true);}catch{} }
  }

  // 2. the levels are applied at once
  static void RunV4ScopeTests() {
   var cat=ProfileCatalog.BuiltIn;bool g0=Program.Gemini,g1=Program.Google;
   using(var form=new MainForm(true)) {
    form.Show();Application.DoEvents();
    try {
     string J(string[] targets)=>"{\"Phase\":\"Active\",\"Targets\":[\""+string.Join("\",\"",targets)+"\"]}";
     string core=J(cat.Targets),gem=J(cat.TargetsFor(true)),all=J(cat.TargetsForGoogle());string journal=core;form.JournalReader=()=>journal;
     int hooked=0;form.MutateHook=(a,r)=>{hooked++;return Task.CompletedTask;};
     // no bridge: remembered only, no operation, no busy state
     form.SmokeState("idle");form.SetScopeForTest(MainForm.BridgeScope.Core);form.ClickScopeForTest(MainForm.BridgeScope.CoreGemini);
     Check(hooked==0&&Program.Gemini&&!Program.Google&&form.ScopeSelected==MainForm.BridgeScope.CoreGemini&&!form.ScopeBusy&&!form.BrowserPanelVisible,"levels: with no bridge a click is only remembered for the next connect (no operation, no question)");
     // a working bridge: applied at once, busy meanwhile, a second click is ignored, the new level stands
     form.SmokeState("active");form.SetScopeForTest(MainForm.BridgeScope.Core);journal=core;hooked=0;
     bool busyDuring=false,enabledDuring=false;int reentry=0;bool panelDuring=false;
     form.MutateHook=async(a,r)=>{hooked++;busyDuring=form.ScopeBusy;enabledDuring=form.ScopeControl.Enabled;int before=hooked;form.ClickScopeForTest(MainForm.BridgeScope.All);reentry+=hooked-before;panelDuring=form.BrowserPanelVisible;await Task.Delay(80);journal=gem;};
     form.ClickScopeForTest(MainForm.BridgeScope.CoreGemini);
     bool done=PumpUntil(()=>hooked>=1&&!form.ScopeBusy,4000);
     Check(done&&hooked==1&&busyDuring&&enabledDuring&&reentry==0&&!panelDuring,"levels: with a working bridge a click is applied at once (one operation), the levels show they are busy and ignore a second click meanwhile");
     Check(form.ScopeSelected==MainForm.BridgeScope.CoreGemini&&Program.Gemini&&!Program.Google&&!form.ScopeBusy&&form.ScopeControl.Enabled,"levels: when the operation ends the new level stands and the control is free again");
     // a failure: the usual error, the selector goes back, the old bridge stays
     hooked=0;journal=gem;form.SmokeState("active");form.SetScopeForTest(MainForm.BridgeScope.CoreGemini);
     form.MutateHook=async(a,r)=>{hooked++;await Task.Delay(40);form.ShowFailureForTest("UAC_CANCELLED");};
     form.ClickScopeForTest(MainForm.BridgeScope.All);
     Pump(form.BrowserWork);
     if(form.BrowserPanelVisible){Pump(form.BrowserWork);form.SelectBrowserForTest("edge");Pump(form.BrowserWork);form.BrowserContinueForTest();}
     done=PumpUntil(()=>hooked>=1&&!form.ScopeBusy,4000);
     Check(done&&form.ScopeSelected==MainForm.BridgeScope.CoreGemini&&Program.Gemini&&!Program.Google&&form.StatusText=="Мост подключён"&&form.SubText.Contains(CodeTable.Lookup("UAC_CANCELLED").Text)&&form.BridgeActive,"levels: refusal remains in the detail, scope and applied bridge stay connected");
     // an exception inside the operation: nothing is left busy, the selector is back
     hooked=0;form.SmokeState("active");form.SetScopeForTest(MainForm.BridgeScope.Core);journal=core;
     form.MutateHook=(a,r)=>{hooked++;throw new InvalidOperationException("boom");};
     form.ClickScopeForTest(MainForm.BridgeScope.CoreGemini);done=PumpUntil(()=>hooked>=1&&!form.ScopeBusy,4000);
     Check(done&&form.ScopeSelected==MainForm.BridgeScope.Core&&!Program.Gemini&&!form.ScopeBusy,"levels: an exception in the operation leaves nothing busy and puts the selector back");
     // no journal to read and no bridge afterwards: back to the old level
     hooked=0;form.SmokeState("active");form.SetScopeForTest(MainForm.BridgeScope.Core);journal="";
     form.MutateHook=(a,r)=>{hooked++;form.SmokeState("idle");return Task.CompletedTask;};
     form.ClickScopeForTest(MainForm.BridgeScope.CoreGemini);done=PumpUntil(()=>hooked>=1&&!form.ScopeBusy,4000);
     Check(done&&form.ScopeSelected==MainForm.BridgeScope.Core&&!Program.Gemini,"levels: if the bridge is gone after a failed apply and nothing can be read, the selector returns to the old level");
     // «+ Браузер» with a working bridge: the browser panel, then the work at once; the panel's «Продолжить» is the only thing in between
     hooked=0;form.SmokeState("active");form.SetScopeForTest(MainForm.BridgeScope.Core);journal=core;bool busyGoogle=false;
     form.MutateHook=async(a,r)=>{hooked++;busyGoogle=form.ScopeBusy;await Task.Delay(40);journal=all;};
     form.ClickScopeForTest(MainForm.BridgeScope.All);Pump(form.BrowserWork);
     if(form.BrowserPanelVisible){Check(hooked==0,"levels: unconfirmed browser applies nothing before the decision");form.SelectBrowserForTest("edge");Pump(form.BrowserWork);form.BrowserContinueForTest();}
     done=PumpUntil(()=>hooked>=1&&!form.ScopeBusy,4000);
     Check(done&&busyGoogle&&form.ScopeSelected==MainForm.BridgeScope.All&&Program.Google,"levels: after «Продолжить» «+ Браузер» is applied at once and the selector shows it was busy");
     form.MutateHook=null;form.SmokeState("idle");form.SetScopeForTest(MainForm.BridgeScope.Core);
    } finally { Program.Gemini=g0;Program.Google=g1;form.Close(); }
   }
  }

  // 5. language without a restart
  static void RunV4LanguageTests() {
   var shared=L.SharedEnglish().ToList();
   Check(shared.Count==0,"language: no two Russian phrases share one English phrase, so a switch back to Russian is exact"+(shared.Count==0?"":" — "+string.Join(" | ",shared.Take(6))));
   Check(L.Swap("Мост подключён",true)=="Bridge connected"&&L.Swap("Bridge connected",false)=="Мост подключён"&&L.Swap("@your_sister_loves_me",true)=="@your_sister_loves_me"&&L.Swap("",false)==""&&L.Swap(null,true)==null&&L.Swap("42 ms",false)=="42 ms","language: a known phrase swaps both ways; a name, a number or nothing stays");
   bool en0=L.En;
   try {
    L.En=false;
    using(var form=new MainForm(true)) {
     form.Show();Application.DoEvents();var bad=new List<string>();int rounds=0;
     foreach(string mode in new[]{"idle","checking","active","active-unchecked","recovery","conflict","wmi","drift","switch","noprovider"}){
      form.SmokeState(mode);
      for(int tab=0;tab<5;tab++){
       form.SelectTab(tab);Application.DoEvents();rounds++;
       var ru=form.AllVisibleTexts().ToList();
       form.LanguageControl.ClickForTest(1);var en=form.AllVisibleTexts().ToList();
       form.LanguageControl.ClickForTest(0);var ru2=form.AllVisibleTexts().ToList();
       string who=mode+"/tab"+tab;
       var cyr=en.Where(t=>Cyr.IsMatch(t)&&t!="Русский"&&t!="Русский · English").Take(2).ToList();if(cyr.Count>0)bad.Add(who+" still Russian: «"+string.Join("», «",cyr)+"»");
       if(!ru.SequenceEqual(ru2)){int i=0;while(i<ru.Count&&i<ru2.Count&&ru[i]==ru2[i])i++;bad.Add(who+" not restored: «"+(i<ru.Count?ru[i]:"")+"» vs «"+(i<ru2.Count?ru2[i]:"")+"»");}
       if(en.SequenceEqual(ru))bad.Add(who+" did not change");
      }
     }
     Check(bad.Count==0,"language: switching at once rewrites every text of every state and tab in English, and back in Russian, exactly as it was ("+rounds+" screens)"+(bad.Count==0?"":" — "+string.Join("; ",bad.Take(6))));
     // the saved choice, the tooltips, the profile list, nothing restarted
     form.SmokeState("idle");form.LanguageControl.ClickForTest(1);
     Check(L.En&&form.SettingsLanguage=="en"&&form.LanguageControl.SelectedIndex==1,"language: the choice is remembered in the settings and the window stays the same window");
     Check(form.ScopePicker.Tips[0]=="The Antigravity domains."&&form.ScopePicker.Items[2]=="Browser"&&form.ProfileLabels.All(l=>!l.Contains("эксп.")),"language: the tooltips, the levels and the profile list follow at once");
     form.LanguageControl.ClickForTest(0);
     Check(!L.En&&form.SettingsLanguage=="ru"&&form.ScopePicker.Tips[0]=="Домены Antigravity.","language: and back");
     // a message from the table (a failure) is written again in the other language, title and text and action
     form.ShowFailureForTest("UAC_CANCELLED");var ruTitle=form.StatusText;var ruSub=form.SubText;
     form.LanguageControl.ClickForTest(1);
     Check(form.StatusText=="Bridge disconnected"&&form.SubText.Contains("declined")&&form.SubText.Contains("Try again")&&ruTitle=="Мост отключён"&&ruSub.Contains("отклонён"),"language: the stable bridge title and failure detail both follow the selected language");
     form.LanguageControl.ClickForTest(0);Check(form.StatusText=="Мост отключён"&&form.SubText==ruSub,"language: and rewritten back to the same Russian");
     // the open screens: the details panel and the browser panel
     form.SmokeDetails("dns.zapret.moe",false);form.LanguageControl.ClickForTest(1);
     Check(form.DetailsVisible&&form.DetailsText.Contains("Profile: ")&&!Cyr.IsMatch(form.DetailsText.Split('\n')[0]),"language: an open «Подробнее» panel is rewritten");
     form.LanguageControl.ClickForTest(0);form.SelectTab(0);
     form.SmokeState("idle");form.SmokeBrowserPanel("chrome",true);var ruHead=form.BrowserHeadline;form.LanguageControl.ClickForTest(1);
     Check(form.BrowserPanelVisible&&form.BrowserBackText=="Back"&&form.BrowserHeadline.Contains("Secure DNS")&&form.BrowserNames.Last()=="Other / not sure"&&ruHead.Contains("Безопасный DNS"),"language: an open browser panel is rewritten (the back button, the sentence, the last row)");
     form.LanguageControl.ClickForTest(0);Check(form.BrowserBackText=="Назад"&&form.BrowserHeadline==ruHead&&form.BrowserNames.Last()=="Другой / не знаю","language: and back");
     form.BrowserCancelForTest();
     // no switch in the middle of an operation
     form.SetBusyForTest(true);form.LanguageControl.ClickForTest(1);
     Check(!L.En&&form.LanguageControl.SelectedIndex==0,"language: no switch while an operation runs, the selector goes back");
     form.SetBusyForTest(false);
     // the tray
     form.SmokeState("idle");form.InitTrayForTest();var trayRu=form.TrayTexts;
     form.LanguageControl.ClickForTest(1);var trayEn=form.TrayTexts;form.LanguageControl.ClickForTest(0);var trayBack=form.TrayTexts;
     Check(trayEn.Length==6&&trayEn[0]=="Open window"&&trayEn[2]=="Connect bridge"&&trayEn[4]=="Exit"&&trayEn[5]=="Antigravity-Gemini DNS — bridge disconnected"&&trayEn.All(t=>!Cyr.IsMatch(t))&&trayRu.SequenceEqual(trayBack)&&trayRu[0]=="Открыть окно","language: the tray menu and its tooltip follow at once and come back exactly");
     form.SmokeState("active");var trayOnRu=form.TrayTexts;form.LanguageControl.ClickForTest(1);
     Check(form.TrayTexts[1]=="Bridge: connected"&&form.TrayTexts[2]=="Disconnect"&&form.TrayTexts[5].StartsWith("Antigravity-Gemini DNS — bridge connected"),"language: the tray follows the state of the bridge too (connected)");
     form.LanguageControl.ClickForTest(0);
     // the same, from a progress state that holds a function of the language (checking the provider)
     form.SmokeState("busy");string busyRu=form.StatusText;form.SetBusyForTest(false);
     form.Close();
    }
   } finally { L.En=en0; }
  }

  // 4. the browsers: Chrome and Edge first, the rest as found, «Другой» last
  static void RunV4BrowserOrderTests() {
   var env=new MemoryBrowserEnv();
   foreach(var pair in new[]{("waterfox.exe",@"C:\Waterfox\waterfox.exe"),("oddbrowser.exe",@"C:\Odd\oddbrowser.exe"),("firefox.exe",FirefoxExe),("brave.exe",@"C:\b\brave.exe"),("msedge.exe",EdgeExe),("chrome.exe",ChromeExe)})
    env.AddFile(pair.Item2).SetReg(true,@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\"+pair.Item1,null,pair.Item2);
   env.SetReg(true,@"SOFTWARE\Clients\StartMenuInternet\Odd\shell\open\command",null,"\"C:\\Odd\\oddbrowser.exe\"");   // registered first by Windows
   var ids=BrowserCatalog.Detect(env).Select(b=>b.Id).ToList();
   Check(ids.Take(2).SequenceEqual(new[]{"chrome","edge"})&&ids.Count==6&&ids.Contains("exe:oddbrowser.exe"),"browsers: Chrome and Edge come first however Windows lists them, the others follow ("+string.Join(",",ids)+")");
   using(var form=new MainForm(true)) {
    form.Show();Application.DoEvents();
    try {
     form.BrowserEnv=env;form.SelectTab(2);form.ShowBrowserPanelForTest(false);Pump(form.BrowserWork);
     var names=form.BrowserNames;
     Check(names.Length==6&&names[0]=="Google Chrome"&&names[1]=="Microsoft Edge"&&names.Last()=="Другой / не знаю","browser window: the list reads Chrome, Edge, the others found, «Другой / не знаю» last");
     Check(!form.BrowserPanelControls.Any(c=>c.GetType().Name=="VerdictBox"),"browser window: there is no card for the verdict any more");
     // the verdict is one line in the sub-title position: right under the question, above the list, with its dot
     var lay=form.BrowserLayout();
     Check(lay[1].Top>=lay[6].Bottom&&lay[1].Bottom<=lay[0].Top&&form.VerdictControl.Visible&&lay[1].Height<=(int)(40*form.LayoutScale),"browser window: the verdict sits right under the question, above the list, in a line of at most two rows");
     form.BrowserBackForTest();
     Check(!form.BrowserPanelVisible,"browser window: «Назад» closes the screen");
     form.ShowBrowserPanelForTest(false);Pump(form.BrowserWork);form.KeyForTest(Keys.Escape);
     Check(!form.BrowserPanelVisible,"browser window: Esc closes the screen too");
    } finally { form.Close(); }
   }
  }

  // review of 2026-10-14 (evening): a flash on a button and a language switch; the autostart path comparison; a wrong --dpi
  static void RunV4ReviewTests() {
   bool en0=L.En;
   try {
    L.En=false;
    using(var b=new PillButton{Text="Копировать"}) {
     MainForm.FlashForTest(b,"Скопировано",30);PumpUntil(()=>b.Text=="Копировать",2000);
     L.En=true;b.Text=L.Swap(b.Text,true);   // what the switch does to a button
     MainForm.FlashForTest(b,"Copied",30);bool back=PumpUntil(()=>b.Text!="Copied",2000);
     Check(back&&b.Text=="Copy","buttons: after a language switch a flash returns to the label in the new language, not the one of the first flash");
     L.En=false;MainForm.FlashForTest(b,"Скопировано",60);L.En=true;b.Text=L.Swap(b.Text,true);PumpUntil(()=>b.Tag==null,2000);
     Check(b.Text=="Copy","buttons: a switch in the middle of a flash ends with the label in the new language");
    }
   } finally { L.En=en0; }
   const string isoKey=@"Software\AgDnsTest\RunReview";string realKey=AutostartHelper.KeyName;
   Microsoft.Win32.Registry.CurrentUser.CreateSubKey(isoKey).Dispose();AutostartHelper.KeyName=isoKey;
   try {
    string self=Environment.ProcessPath;string dir=Path.GetDirectoryName(self);
    using(var k=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(isoKey))k.SetValue(AutostartHelper.ValueName,"\""+Path.Combine(dir,".",Path.GetFileName(self)).ToUpperInvariant()+"\" --minimized");
    Check(AutostartHelper.IsEnabled(self),"autostart: an entry that names this exe in another spelling (./, case) counts as this exe");
   } finally { AutostartHelper.KeyName=realKey;try{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\AgDnsTest",false);}catch{} }
   int code=Program.Main(new[]{"--ui-smoke",Path.Combine(Path.GetTempPath(),"agdns-dpi-"+Guid.NewGuid().ToString("N")+".png"),"--dpi","abc"});
   Check(code==1,"--ui-smoke: a --dpi that is not a scale is refused, not quietly ignored");
  }
 }
}
