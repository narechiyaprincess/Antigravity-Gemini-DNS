using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 // ui-surfaces: the Windows surfaces the window uses that the self-test does not open (tray menu, tray icon, message box, save dialog, tooltip,
 // clipboard). Each surface is opened once for real in the canonical untrimmed release.
 // Dialogs are closed by a timer; the clipboard text is put back. Nothing else is touched.
 public static partial class UiSurfaceCheck {
  delegate bool EnumProc(IntPtr hwnd,IntPtr lParam);
  [DllImport("user32.dll")]static extern bool EnumThreadWindows(uint threadId,EnumProc proc,IntPtr lParam);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr hwnd,System.Text.StringBuilder name,int max);
  [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr hwnd);
  [DllImport("user32.dll")]static extern bool PostMessage(IntPtr hwnd,uint msg,IntPtr w,IntPtr l);
  [DllImport("kernel32.dll")]static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")]static extern int GetGuiResources(IntPtr process,int flags);

  static int failed,skipped;
  sealed class SurfaceSkip:Exception{public SurfaceSkip(string reason):base(reason){}}
  static void Step(string name,Action a){try{a();Console.WriteLine("PASS ui surface: "+name);}catch(SurfaceSkip e){skipped++;Console.WriteLine("SKIP ui surface: "+name+" — "+e.Message);}catch(Exception e){failed++;Console.WriteLine("FAIL ui surface: "+name+" — "+e.GetType().Name+": "+e.Message);}}

  // Closes the first visible dialog (#32770) of this thread a moment after it opened.
  static Timer Closer(){
   uint tid=GetCurrentThreadId();
   var t=new Timer{Interval=400};
   t.Tick+=(_,__)=>EnumThreadWindows(tid,(h,l)=>{var sb=new System.Text.StringBuilder(64);GetClassName(h,sb,64);if(sb.ToString()=="#32770"&&IsWindowVisible(h)){PostMessage(h,0x0010,IntPtr.Zero,IntPtr.Zero);return false;}return true;},IntPtr.Zero);
   t.Start();return t;
  }

  // ui-stress: the real paint path under the conditions of a live window that the snapshots do not have: per-monitor DPI changes (a window restored on another
  // monitor), animation, mouse messages, repeated repaints. Works on defaults (nothing is read from or written to the owner's files). Prints UI_STRESS_PASS or _FAIL.
  [StructLayout(LayoutKind.Sequential)]struct Rc{public int L,T,R,B;}
  [DllImport("user32.dll")]static extern bool GetClientRect(IntPtr window,out Rc rectangle);
  [DllImport("user32.dll")]static extern bool AdjustWindowRectExForDpi(ref Rc rc,uint style,bool menu,uint exStyle,uint dpi);
  [DllImport("user32.dll")]static extern int GetWindowLong(IntPtr hwnd,int index);
  [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr hwnd,uint msg,IntPtr w,IntPtr l);
  // Gives a window another scale as Windows does when it is dragged to a monitor of another density (WM_DPICHANGED); also used by --ui-smoke --dpi.
  public static void ChangeDpi(Form form,int dpi){
   var b=form.Bounds;int cur=Math.Max(1,form.DeviceDpi);if(dpi==cur)return;   // already at this scale
   // the suggested window rectangle holds the client area scaled by the ratio and the frame of the new scale, as Windows works it out
   var rc=new Rc{L=0,T=0,R=(int)Math.Round(form.ClientSize.Width*(double)dpi/cur),B=(int)Math.Round(form.ClientSize.Height*(double)dpi/cur)};
   AdjustWindowRectExForDpi(ref rc,(uint)GetWindowLong(form.Handle,-16),false,(uint)GetWindowLong(form.Handle,-20),(uint)dpi);
   rc=new Rc{L=b.Left,T=b.Top,R=b.Left+(rc.R-rc.L),B=b.Top+(rc.B-rc.T)};
   IntPtr mem=Marshal.AllocHGlobal(Marshal.SizeOf<Rc>());Marshal.StructureToPtr(rc,mem,false);
   var want=new Size((int)Math.Round(form.ClientSize.Width*(double)dpi/cur),(int)Math.Round(form.ClientSize.Height*(double)dpi/cur));
   SendMessage(form.Handle,0x02E0,(IntPtr)((dpi<<16)|dpi),mem);Marshal.FreeHGlobal(mem);Application.DoEvents();
    // MainForm has its own monitor-capped fixed viewport in WM_DPICHANGED. Forcing
    // the uncapped design back afterward defeats its size constraints on short monitors.
    if(form is not MainForm&&form.ClientSize!=want){form.ClientSize=want;Application.DoEvents();}   // framed auxiliary forms may need a one-pixel client correction
  }
  static void Pump(int ms){var sw=System.Diagnostics.Stopwatch.StartNew();while(sw.ElapsedMilliseconds<ms){Application.DoEvents();System.Threading.Thread.Sleep(8);}}
  public static int Stress(){
   int exceptions=0;string first=null;
   Application.ThreadException+=(s,e)=>{exceptions++;if(first==null)first=e.Exception.GetType().Name+": "+e.Exception.Message+Environment.NewLine+e.Exception.StackTrace;};
   using(var form=new MainForm(true)){
    form.SetScopeForTest(MainForm.BridgeScope.All);   // as the owner's window starts: the saved scope is «+ Браузер», so the levels begin fully lit
    form.Show();Pump(200);Motion.Enabled=true;var seg=form.ScopePicker;
    int initialGdi=GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle,0);
    // shown from the tray the way the program does it: hidden, then a fade-in (the window is layered while its opacity changes)
    for(int i=0;i<3;i++){form.Hide();Pump(150);form.ShowWindow();for(int j=0;j<8;j++){seg.Invalidate();seg.Update();Pump(40);}}
    Pump(300);
    foreach(int dpi in new[]{144,192,96,120,168,96,120}){
     ChangeDpi(form,dpi);
     foreach(int lvl in new[]{0,1,2,1}){
      int x=(int)(seg.Width*(lvl*2+1)/6f),y=seg.Height/2;IntPtr lp=(IntPtr)((y<<16)|(x&0xFFFF));
      SendMessage(seg.Handle,0x0200,IntPtr.Zero,lp);Pump(90);seg.Invalidate();seg.Update();form.ProfileControl.Invalidate();form.ProfileControl.Update();form.NavControl.Invalidate();form.NavControl.Update();
      if(lvl<2){form.SmokeState("idle");SendMessage(seg.Handle,0x0201,IntPtr.Zero,lp);SendMessage(seg.Handle,0x0202,IntPtr.Zero,lp);}
      Pump(180);
     }
     Console.WriteLine("dpi "+dpi+" -> form "+form.DeviceDpi+" client "+form.ClientSize+" paint failures "+seg.PaintFailures+" exceptions "+exceptions);
    }
    // hover long enough for the tips to appear (the levels, the ping row)
    form.SmokeState("active");Pump(100);
    foreach(int lvl in new[]{0,1,2}){int x=(int)(seg.Width*(lvl*2+1)/6f),y=seg.Height/2;SendMessage(seg.Handle,0x0200,IntPtr.Zero,(IntPtr)((y<<16)|(x&0xFFFF)));Pump(900);}
    {var sm=form.ProfileControl;SendMessage(sm.Handle,0x0200,IntPtr.Zero,(IntPtr)(((int)(sm.Height*.5)<<16)|(sm.Width/2)));Pump(900);SendMessage(sm.Handle,0x0200,IntPtr.Zero,(IntPtr)((2<<16)|2));Pump(300);}
    // the question of «+ Браузер»: open by a real click on the third level, choose a browser, go on or back out, with animation on
    for(int round=0;round<8;round++){
     form.SmokeState("idle");form.SetScopeForTest(MainForm.BridgeScope.Core);Pump(120);
     int x=(int)(seg.Width*5/6f),y=seg.Height/2;IntPtr lp=(IntPtr)((y<<16)|(x&0xFFFF));
     SendMessage(seg.Handle,0x0200,IntPtr.Zero,lp);Pump(60);SendMessage(seg.Handle,0x0201,IntPtr.Zero,lp);SendMessage(seg.Handle,0x0202,IntPtr.Zero,lp);Pump(350);
     form.SelectBrowserForTest(round%2==0?"chrome":"edge");Pump(250);
     if(round%3==0)form.KeyForTest(Keys.Escape);else if(round%2==0)form.BrowserCancelForTest();else form.BrowserContinueForTest();
     for(int i=0;i<6;i++){seg.Invalidate();seg.Update();Pump(60);}
    }
    Console.WriteLine("question rounds done: paint failures "+seg.PaintFailures+" exceptions "+exceptions);
    // the language switch in the live window, at several scales, with the levels busy in between: every text is written again, nothing restarts, nothing throws
    form.SmokeState("active");
    foreach(int dpi in new[]{120,192,96}){
     ChangeDpi(form,dpi);
     for(int r=0;r<4;r++){form.ApplyLanguage(r%2==0);form.SelectTab(r%5);Pump(120);form.NavControl.Invalidate();form.NavControl.Update();seg.Busy=r%2==0;seg.Invalidate();seg.Update();Pump(90);}
     seg.Busy=false;form.ApplyLanguage(false);form.SelectTab(0);Pump(100);
    }
    Console.WriteLine("language rounds done: paint failures "+seg.PaintFailures+" exceptions "+exceptions);
    foreach(var units in new[]{new Size(420,540),new Size(620,900),new Size(420,600)}){
     var fixedSize=form.Size;float k=form.DeviceDpi/96f;form.ClientSize=new Size((int)(units.Width*k),(int)(units.Height*k));Pump(120);
     form.SystemCommandForTest(0xF030);Pump(30);
     if(form.Size!=fixedSize||!form.FixedWindowForTest){exceptions++;Console.WriteLine("FIXED_WINDOW_FAIL "+units+" actual="+form.Size+" expected="+fixedSize);}
     for(int tab=0;tab<form.TabCount;tab++){
      form.SelectTab(tab);Pump(30);
      var nav=form.NavControl;
      GetClientRect(form.Handle,out var client);
      if(nav.Bottom>client.B||nav.Left<0||nav.Right>client.R){exceptions++;Console.WriteLine("RESIZE_FAIL navigation "+units+" tab "+tab+" client="+form.ClientSize+" bounds="+form.Bounds+" minimum="+form.MinimumSize+" nav="+nav.Bounds+" dpi="+form.DeviceDpi);}
     }
     form.SelectTab(0);form.SmokeState("idle");Pump(50);
     string captures=Environment.GetEnvironmentVariable("AGDNS_UI_CAPTURE");
     void Capture(string name){if(string.IsNullOrEmpty(captures))return;System.IO.Directory.CreateDirectory(captures);using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));form.OverlayDetails(bitmap);bitmap.Save(System.IO.Path.Combine(captures,name+"-"+units.Width+"x"+units.Height+".png"));}}
     Capture("resize");
     form.ShowCustomPanelForTest();Pump(40);Capture("custom");
     foreach(Control control in form.Controls)if(control is RoundPanel&&control.Visible&&control.Bottom>form.NavControl.Top){exceptions++;Console.WriteLine("RESIZE_FAIL custom overlay covers navigation "+units);}
     form.KeyForTest(Keys.Escape);
     form.ShowBrowserPanelForTest(false);Pump(120);Capture("browser");
     var browser=form.BrowserPanelControls.OfType<RoundPanel>().First();
     var list=form.BrowserPanelControls.First(c=>c is BrowserList);
     browser.AutoScrollPosition=Point.Empty;int listTop=list.Top;
     browser.AutoScrollPosition=new Point(0,100);
     foreach(string id in new[]{"chrome","edge"}){form.SelectBrowserForTest(id);Pump(250);}
     browser.AutoScrollPosition=Point.Empty;Pump(40);
     if(list.Top!=listTop){exceptions++;Console.WriteLine("RESIZE_FAIL browser list moved after scrolled selection "+listTop+" -> "+list.Top);}
     browser.AutoScrollPosition=new Point(0,100);Capture("browser-scrolled");
     foreach(Control control in form.Controls)if(control is RoundPanel&&control.Visible&&control.Bottom>form.NavControl.Top){exceptions++;Console.WriteLine("RESIZE_FAIL browser overlay covers navigation "+units);}
     form.BrowserCancelForTest();
    }
    Console.WriteLine("resize rounds done: exceptions "+exceptions);
    Console.WriteLine("UI_GDI_HANDLES initial="+initialGdi+" after_interactions="+GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle,0));
    form.Close();
    bool ok=exceptions==0&&seg.PaintFailures==0;
    ok=ResourceSoak()&&ok;
    if(first!=null)Console.WriteLine(first);
    Console.WriteLine(ok?"UI_STRESS_PASS":"UI_STRESS_FAIL");return ok?0:1;
   }
  }

  public static int Run(){
   failed=skipped=0;
   using(var form=new Form{StartPosition=FormStartPosition.Manual,Location=new Point(-3000,-3000),ShowInTaskbar=false,Size=new Size(300,200)}){
    form.Show();Application.DoEvents();
    Step("tray menu (dark renderer) opens and closes",()=>{
     using(var menu=new ContextMenuStrip{Renderer=new ToolStripProfessionalRenderer(new DarkColors()){RoundedEdges=false},ShowImageMargin=false,BackColor=Theme.Card,ForeColor=Theme.Text,Font=Theme.F(9.5f)}){
      menu.Items.Add(new ToolStripMenuItem("Открыть окно"));menu.Items.Add(new ToolStripSeparator());menu.Items.Add(new ToolStripMenuItem("Выход"){Enabled=false});
      menu.Show(form,new Point(10,10));Application.DoEvents();
      if(!menu.Visible)throw new InvalidOperationException("menu not shown");
      menu.Close();Application.DoEvents();
     }
    });
    Step("profile drop-down opens with a ping in its rows and takes a new one while open",()=>{
     // A native drop-down requires an active on-screen owner; an off-screen owner can close it during activation.
     // Give all three rows room here; short-view geometry/scrolling is covered by the UI tests.
     form.ClientSize=new Size(400,400);
     form.Location=Screen.PrimaryScreen.WorkingArea.Location+new Size(30,30);form.Activate();Pump(100);
     using(var ps=new PillSelect{Bounds=new Rectangle(10,10,270,36)}){
      ps.Items.AddRange(new[]{"geohide.ru · P4","dns.example · P2","other.example · P3"});ps.SetSelectedSilently(0);int opened=0;ps.DropDownOpening+=(_,__)=>opened++;
      ps.Detail=i=>i==0?("12 мс",Theme.Green):("",Color.Empty);form.Controls.Add(ps);Application.DoEvents();
      ps.OpenForTest();Application.DoEvents();var m=ps.OpenMenu;
      try{
       if(m==null||!m.Visible||m.Items.Count!=3||opened!=1)throw new InvalidOperationException("the list did not open: menu="+(m!=null)+" visible="+(m?.Visible??false)+" items="+(m?.Items.Count??0)+" opening="+opened);
       int Colour(Color c,ToolStripItem it){using(var bmp=new Bitmap(Math.Max(1,m.Width),Math.Max(1,m.Height))){m.DrawToBitmap(bmp,new Rectangle(0,0,bmp.Width,bmp.Height));int n=0;for(int y=it.Bounds.Top;y<it.Bounds.Bottom&&y<bmp.Height;y++)for(int x=it.Bounds.Left;x<it.Bounds.Right&&x<bmp.Width;x++){var p=bmp.GetPixel(x,y);if(Math.Abs(p.R-c.R)+Math.Abs(p.G-c.G)+Math.Abs(p.B-c.B)<40)n++;}return n;}}
       if(Colour(Theme.Green,m.Items[0])<15)throw new InvalidOperationException("no dot in the row that has a ping");
       if(Colour(Theme.Green,m.Items[1])!=0)throw new InvalidOperationException("a dot in a row without a ping");
       ps.Detail=i=>i==1?("320 мс",Theme.Amber):("",Color.Empty);ps.RefreshDetails();Application.DoEvents();
       int amber=Colour(Theme.Amber,m.Items[1]),green=Colour(Theme.Green,m.Items[0]);
       if(amber<15||green!=0)throw new InvalidOperationException("the list did not take the new ping while open: amber="+amber+" oldGreen="+green+" menu="+m.Size+" row="+m.Items[1].Bounds);
      }finally{m?.Close();Application.DoEvents();form.Controls.Remove(ps);}
     }
    });
    Step("tray icon with its menu is created and removed",()=>{
     using(var icon=Branding.ToIcon(32,true))using(var menu=new ContextMenuStrip())using(var tray=new NotifyIcon{Icon=icon,Text="Antigravity-Gemini DNS",Visible=false,ContextMenuStrip=menu}){menu.Items.Add("x");Application.DoEvents();}
    });
    Step("tooltip shows and hides",()=>{using(var tip=new ToolTip()){tip.Show("Проверка",form,5,5,300);Application.DoEvents();tip.Hide(form);}});
    Step("message box opens and closes",()=>{using(Closer()){var r=MessageBox.Show(form,"Проверка окна сообщения.","Antigravity-Gemini DNS",MessageBoxButtons.OK,MessageBoxIcon.Information);}});
    Step("save dialog opens and closes",()=>{using(Closer())using(var sfd=new SaveFileDialog{Title="Проверка",Filter="Текстовый файл (*.txt)|*.txt",FileName="check.txt",OverwritePrompt=true}){var r=sfd.ShowDialog(form);if(r!=DialogResult.Cancel)throw new InvalidOperationException("unexpected result "+r);}});
    Step("clipboard text round-trip (previous content put back)",()=>{
     // Copy whatever is on the clipboard into a private data object first; if any format cannot be read, nothing is touched.
     DataObject saved=null;
     var current=Clipboard.GetDataObject();
     if(current!=null){
      saved=new DataObject();
      foreach(string f in current.GetFormats(false)){object v;try{v=current.GetData(f,false);}catch(Exception){throw new SurfaceSkip("clipboard format cannot be copied; nothing touched");}if(v!=null)saved.SetData(f,false,v);}
     }
     Clipboard.SetText("agdns-ui-check");
     string got=Clipboard.GetText();
     if(saved!=null&&saved.GetFormats().Length>0)Clipboard.SetDataObject(saved,true);else Clipboard.Clear();
     if(got!="agdns-ui-check")throw new InvalidOperationException("clipboard returned '"+got+"'");
    });
    Step("support report preview opens and closes",()=>{using(var dlg=new ReportPreviewForm("Проверка")){dlg.StartPosition=FormStartPosition.Manual;dlg.Location=new Point(-3000,-3000);dlg.Show();Application.DoEvents();dlg.Close();}});
    form.Close();
   }
   Console.WriteLine("UI_SURFACES_COUNTS failed="+failed+" skipped="+skipped);
   Console.WriteLine(failed==0?"UI_SURFACES_PASS":"UI_SURFACES_FAIL "+failed);
   return failed==0?0:1;
  }
 }
}
