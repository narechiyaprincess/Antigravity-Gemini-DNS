using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 // The browsers installed here, one per row: the program's own icon, the name and a radio mark. The last row is always "Другой / не знаю".
 internal sealed class BrowserList:Surface {
  public sealed class Item{public string Id,Name;public Bitmap Icon;}
  public readonly List<Item> Items=new List<Item>();int sel=-1,hot=-1;
  public event EventHandler SelectedChanged;   // raised by the user's click only
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public int SelectedIndex{get=>sel;set{if(value<-1||value>=Items.Count)return;sel=value;Invalidate();}}
  public string SelectedId=>sel>=0&&sel<Items.Count?Items[sel].Id:null;
  public BrowserList(){Cursor=Cursors.Hand;Font=Theme.F(9.5f);AccessibleRole=AccessibleRole.List;AccessibleName=L.En?"Browser":"Браузер";}
  protected override bool IsInputKey(Keys key)=>key==Keys.Up||key==Keys.Down||key==Keys.Home||key==Keys.End||base.IsInputKey(key);
  protected override void OnKeyDown(KeyEventArgs e){
   if(Enabled&&Items.Count>0&&(e.KeyCode==Keys.Up||e.KeyCode==Keys.Down||e.KeyCode==Keys.Home||e.KeyCode==Keys.End)){
    int next=e.KeyCode==Keys.Home?0:e.KeyCode==Keys.End?Items.Count-1:Math.Clamp(sel+(e.KeyCode==Keys.Up?-1:1),0,Items.Count-1);
    if(next!=sel){sel=next;Invalidate();SelectedChanged?.Invoke(this,EventArgs.Empty);}e.Handled=true;
   }base.OnKeyDown(e);
  }
  protected override void Dispose(bool disposing){if(disposing){foreach(var it in Items)it.Icon?.Dispose();Items.Clear();}base.Dispose(disposing);}
  float RowH=>Items.Count==0?Height:Height/(float)Items.Count;
  int At(int y)=>Items.Count==0?-1:Math.Max(0,Math.Min(Items.Count-1,(int)(y/RowH)));
  protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);int i=At(e.Y);if(i!=hot){hot=i;Invalidate();}}
  protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hot=-1;Invalidate();}
  protected override void OnMouseClick(MouseEventArgs e){base.OnMouseClick(e);if(!Enabled)return;int i=At(e.Y);if(i>=0&&i!=sel){sel=i;Invalidate();SelectedChanged?.Invoke(this,EventArgs.Empty);}}
  internal void ClickForTest(int i){if(i>=0&&i<Items.Count&&i!=sel){sel=i;Invalidate();SelectedChanged?.Invoke(this,EventArgs.Empty);}}
  protected override void OnPaint(PaintEventArgs e){
   var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;float k=K,rh=RowH;
   for(int i=0;i<Items.Count;i++){
    var row=new RectangleF(0,i*rh+1,Width-1,rh-2);bool on=i==sel;
    using(var p=Theme.Round(row,10*k)){
     using(var b=new SolidBrush(on?Theme.NavSel:i==hot?Color.FromArgb(22,22,28):Theme.Field))g.FillPath(b,p);
     using(var pen=new Pen(on?Color.FromArgb(60,90,160):Theme.Line))g.DrawPath(pen,p);
    }
    float icon=20*k,ix=12*k,iy=row.Y+(row.Height-icon)/2;
    var it=Items[i];
    if(it.Icon!=null){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(it.Icon,new RectangleF(ix,iy,icon,icon));}
    else using(var p=Theme.Round(new RectangleF(ix-4*k,iy-4*k,icon+8*k,icon+8*k),7*k))using(var b=new SolidBrush(RowKit.TileFill)){g.FillPath(b,p);Icons.Draw(g,IconKind.Compass,new RectangleF(ix,iy,icon,icon),Color.FromArgb(170,190,235));}
    TextRenderer.DrawText(g,it.Name,Font,new Rectangle((int)(44*k),(int)row.Y,(int)(Width-44*k-40*k),(int)row.Height),Enabled?Theme.Text:Theme.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis);
    float rr=8*k,rx=Width-22*k,ry=row.Y+row.Height/2;
    using(var pen=new Pen(on?Theme.AccentHover:Theme.Muted,1.5f*k))g.DrawEllipse(pen,rx-rr,ry-rr,rr*2,rr*2);
    if(on)using(var b=new SolidBrush(Theme.AccentHover))g.FillEllipse(b,rx-rr/2f,ry-rr/2f,rr,rr);
   }
   if(Focused&&ShowFocusCues)Theme.FocusRing(g,this,10);
  }
 }

 // The answer of the browser check: one sentence behind a coloured dot, in the place of the sub-title (at most two lines).
 internal sealed class VerdictLine:Surface{
  public Color Tone=Theme.Muted;public string Headline="";
  public VerdictLine(){Font=Theme.F(8.5f);}
  public void Show(Color tone,string headline){if(Tone==tone&&Headline==headline)return;Tone=tone;Headline=headline;Invalidate();}
  protected override void OnPaint(PaintEventArgs e){
   var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;float k=K;
   const TextFormatFlags f=TextFormatFlags.WordBreak|TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis;
   int x=(int)(16*k),w=Math.Max(1,Width-x);
   int lh=TextRenderer.MeasureText(g,"Ag",Font,new Size(w,int.MaxValue/4),f).Height;
   using(var br=new SolidBrush(Tone))g.FillEllipse(br,1*k,(lh-8*k)/2f+1*k,8*k,8*k);   // the dot sits on the middle of the first line
   TextRenderer.DrawText(g,Headline,Font,new Rectangle(x,0,w,Height),Theme.Text,f);
  }
 }

 public sealed partial class MainForm {
  RoundPanel browserPanel;Label browserTitle,browserNote;BrowserList browserList;VerdictLine verdictLine;Func<string> verdictFn;
  readonly PillButton browserOpen=new PillButton(),browserRecheck=new PillButton(),browserBack=new PillButton(),browserContinue=new PillButton();
  List<BrowserInfo> browsers=new List<BrowserInfo>();BrowserInfo browserInfo;
  System.Threading.Tasks.TaskCompletionSource<bool> browserTcs;bool browserForScope,browserWarned;int browserSerial;Task browserWork=Task.CompletedTask;
  internal IBrowserEnv BrowserEnv=new LiveBrowserEnv();                 // test seam: where the browser check reads from
  internal Action<BrowserInfo> BrowserLauncher;                         // test seam: null = start the browser's program with its settings address
  internal Func<bool> BrowserElevated;                                  // test seam: null = ask Windows
  internal Func<string,Bitmap> IconSource;                              // test seam: where the list gets a program's icon (null = Windows; none in a render run)
  internal Action<string> BrowserClipboard;                             // test seam: null = the real clipboard
  const int BrowserRowH=32,BrowserMaxShown=5;

  // Top to bottom: «Назад», the question, the verdict (one sentence), the list, the button to the browser's own settings (only when it bypasses the bridge),
  // the note about the reconnect (only in the «+ Браузер» question with a working bridge), and at the bottom «Проверить снова» and «Продолжить» / «Готово».
  const int BackY=12,BackH=28,TitleY=52,VerdictY=80,VerdictH=36,ListY=126,BottomH=34;
  void BuildBrowserPanel(){
   int pw=W-28,iw=pw-44;
   browserPanel=new RoundPanel{Visible=false,Fill=Theme.Card,Radius=16};browserPanel.Bounds=new Rectangle(14,10,pw,PageBottom-20);Controls.Add(browserPanel);
   browserPanel.HandleCreated+=(_,__)=>{try{SetWindowTheme(browserPanel.Handle,"DarkMode_Explorer",null);}catch{}};
   browserBack.Glyph="\uE72B";browserBack.Font=Theme.F(9f,true);
   Btn(browserPanel,browserBack,L.T("Назад"),new Rectangle(18,BackY,94,BackH),Theme.Soft,()=>CloseBrowserPanel(false));
   browserTitle=MakeLabel(L.T("Какой браузер для Gemini и Google?"),Theme.Text,Theme.F(11.5f,true),new Rectangle(18,TitleY,iw,22));browserPanel.Controls.Add(browserTitle);
   verdictLine=new VerdictLine{Bounds=new Rectangle(18,VerdictY,iw,VerdictH)};browserPanel.Controls.Add(verdictLine);
   browserList=new BrowserList{Bounds=new Rectangle(18,ListY,iw,BrowserRowH*2)};browserPanel.Controls.Add(browserList);
   browserList.SelectedChanged+=(_,__)=>{browserWork=EvaluateBrowserAsync();};
   Btn(browserPanel,browserOpen,L.T("Открыть настройку браузера"),new Rectangle(18,ListY+BrowserRowH*2+8,iw,26),Theme.Soft,OpenBrowserSettings);
   browserNote=MakeLabel("",Theme.Muted,Theme.F(8f),new Rectangle(18,ListY+BrowserRowH*2+8,iw,30));browserNote.UseMnemonic=false;browserPanel.Controls.Add(browserNote);
   int by=PageBottom-20-BottomH-6,gap=8,wRe=132;
   Btn(browserPanel,browserRecheck,L.T("Проверить снова"),new Rectangle(18,by,wRe,BottomH),Theme.Soft,()=>{browserWork=LoadBrowsersAsync();});
   Btn(browserPanel,browserContinue,L.T("Продолжить"),new Rectangle(18+wRe+gap,by,iw-wRe-gap,BottomH),Theme.Accent,()=>{if(browserList.SelectedIndex>=0)CloseBrowserPanel(true);});
   browserOpen.Font=Theme.F(9f,true);browserRecheck.Font=browserContinue.Font=Theme.F(9f,true);
   browserOpen.Visible=false;
  }
  // The list's height depends on how many browsers there are: the settings button and the note follow it.
  void LayoutBrowserPanel(){
   float s=LayoutScale;int R(int v)=>(int)Math.Round(v*s);
   var scroll=browserPanel.AutoScrollPosition;
   browserList.Bounds=new Rectangle(R(18)+scroll.X,R(ListY)+scroll.Y,R(W-28-44),(int)Math.Round(browserList.Items.Count*BrowserRowH*s));
   browserOpen.Top=browserList.Bottom+R(8);
   browserNote.Top=(browserOpen.Visible?browserOpen.Bottom:browserList.Bottom)+R(8);
   int footer=Math.Max(R(PageBottom-20-BottomH-6)+scroll.Y,browserNote.Bottom+R(10));
   browserRecheck.Top=browserContinue.Top=footer;
   browserPanel.AutoScrollMinSize=new Size(0,footer-scroll.Y+R(BottomH+12));
   int contentWidth=browserPanel.ClientSize.Width-R(36);
   browserList.Width=browserOpen.Width=browserNote.Width=Math.Max(1,contentWidth);
   browserContinue.Left=browserRecheck.Right+R(8);browserContinue.Width=Math.Max(R(80),contentWidth-browserRecheck.Width-R(8));
  }

  // «+ Браузер»: ask which browser first. true = continue with the scope, false = the user backed out.
  async Task<bool> AskBrowserAsync(){
   var ready=new TaskCompletionSource<bool>();browserWork=ready.Task;
   try{
   int tabAtStart=nav.Selected;
   string chosen=settings.Browser;var env=BrowserEnv;
   if(chosen==BrowserCatalog.OtherId)return true; // the user already chose the explicit unknown-browser option
   if(!string.IsNullOrEmpty(chosen)){
    bool follows=await OffUi(()=>{
     var saved=BrowserCatalog.Detect(env).FirstOrDefault(b=>b.Id==chosen);
     return saved!=null&&BrowserCatalog.Evaluate(saved,env).Status==BypassStatus.Follows;
    });
    if(IsDisposed||exiting||nav.Selected!=tabAtStart)return false;
    if(follows&&settings.Browser==chosen)return true;
   }
   // Missing/unconfirmed/bypassing browsers need a useful decision; simply viewing
   // Settings never gets here. An unchanged verified choice does not open a picker.
   var decision=new TaskCompletionSource<bool>();browserTcs=decision;ShowBrowserPanel(true);
   _=browserWork.ContinueWith(t=>{_=t.Exception;ready.TrySetResult(true);},System.Threading.CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
   return await decision.Task;
   }finally{ready.TrySetResult(true);}
  }
  void ShowBrowserPanel(bool forScope){
   if(detailsPanel!=null)detailsPanel.Visible=false;if(customPanel!=null)customPanel.Visible=false;
   browserForScope=forScope;browserWarned=forScope&&needsRestore&&isActive;
   browserNote.Text=browserWarned?L.T("Мост подключится заново: один запрос Windows. Если не получится, прежний мост останется."):"";
   browserContinue.Text=forScope?L.T("Продолжить"):L.T("Готово");
   float sc=LayoutScale;
   browserPanel.Bounds=new Rectangle((int)Math.Round(14*sc),(int)Math.Round(10*sc),(int)Math.Round((W-28)*sc),(int)Math.Round((PageBottom-20)*sc));
   LayoutBrowserPanel();
   if(!browserOpen.IsHandleCreated)_=browserOpen.Handle;   // the settings button appears later: its window is created here, on this thread, while nothing else is going on
   browserPanel.AutoScrollPosition=Point.Empty;browserPanel.Visible=true;browserPanel.BringToFront();FitServiceViewport();
   browserWork=LoadBrowsersAsync();
  }
  void CloseBrowserPanel(bool ok){
   if(browserPanel==null||!browserPanel.Visible&&browserTcs==null)return;
   if(ok&&browserForScope&&browserList.SelectedId!=null&&settings.Browser!=browserList.SelectedId){settings.Browser=browserList.SelectedId;if(!smoke)SaveSettings();UpdateBrowserRow();}
   browserPanel.Visible=false;browserSerial++;
   var t=browserTcs;browserTcs=null;t?.TrySetResult(ok);
  }
  // The language changed while the panel is open: its own texts are written again (the buttons and the title are exchanged with the other phrases).
  void RelabelBrowserPanel(){
   if(verdictFn!=null)verdictLine.Show(verdictLine.Tone,verdictFn());
   if(browserList.Items.Count>0&&browserList.Items[browserList.Items.Count-1].Id==BrowserCatalog.OtherId){browserList.Items[browserList.Items.Count-1].Name=L.T("Другой / не знаю");browserList.Invalidate();}
   browserNote.Text=browserWarned?L.T("Мост подключится заново: один запрос Windows. Если не получится, прежний мост останется."):"";
  }

  // Reads on a pool thread; the result arrives on the window's thread by an explicit hand-over, so the code after the "await" may touch controls even where the
  // thread has no WinForms SynchronizationContext (a render run and the self-test have no Application.Run).
  Task<T> OffUi<T>(Func<T> work){
   var tcs=new TaskCompletionSource<T>();
   Task.Run(work).ContinueWith(t=>{
    Action done=()=>{if(t.IsFaulted)tcs.TrySetException(t.Exception.InnerException);else if(t.IsCanceled)tcs.TrySetCanceled();else tcs.TrySetResult(t.Result);};
    try{if(IsHandleCreated&&!IsDisposed)BeginInvoke(done);else done();}catch{done();}
   },TaskScheduler.Default);
   return tcs.Task;
  }
  // Finds the installed browsers (a read of the registry), fills the list, selects the remembered one and checks it.
  async Task LoadBrowsersAsync(){
   int serial=++browserSerial;var env=BrowserEnv;
   browserList.Enabled=false;Verdict(Theme.Muted,()=>L.T("Ищу браузеры…"));browserOpen.Visible=false;UpdateBrowserButtons();
   List<BrowserInfo> found;List<Bitmap> icons;
   try{
    var r=await OffUi(()=>{var f=BrowserCatalog.Detect(env);var shown=f.Take(BrowserMaxShown).ToList();
     string keep=settings.Browser;if(!string.IsNullOrEmpty(keep)&&shown.All(b=>b.Id!=keep)){var s=f.FirstOrDefault(b=>b.Id==keep);if(s!=null)shown[shown.Count-1]=s;}
     return (shown,shown.Select(b=>IconSource!=null?IconSource(b.Exe):smoke?null:BrowserCatalog.IconOf(b.Exe)).ToList());});
    found=r.shown;icons=r.Item2;
   }catch{found=new List<BrowserInfo>();icons=new List<Bitmap>();}
   if(serial!=browserSerial||IsDisposed){foreach(var ic in icons)ic?.Dispose();return;}
   browsers=found;browserList.Enabled=true;
   foreach(var old in browserList.Items)old.Icon?.Dispose();
   browserList.Items.Clear();
   for(int i=0;i<found.Count;i++)browserList.Items.Add(new BrowserList.Item{Id=found[i].Id,Name=found[i].Name,Icon=icons[i]});
   browserList.Items.Add(new BrowserList.Item{Id=BrowserCatalog.OtherId,Name=L.T("Другой / не знаю")});
   browserList.SelectedIndex=browserList.Items.FindIndex(i=>i.Id==settings.Browser);LayoutBrowserPanel();
   browserList.Invalidate();
   await EvaluateBrowserAsync();
  }
  async Task EvaluateBrowserAsync(){
   int serial=++browserSerial;int i=browserList.SelectedIndex;
   if(i<0){browserInfo=null;Verdict(Theme.Muted,()=>L.T("Выберите браузер — проверю, обходит ли он мост"));browserOpen.Visible=false;LayoutBrowserPanel();UpdateBrowserButtons();return;}
   var item=browserList.Items[i];var info=browsers.FirstOrDefault(b=>b.Id==item.Id);var env=BrowserEnv;
   if(!browserForScope&&settings.Browser!=item.Id){settings.Browser=item.Id;if(!smoke)SaveSettings();UpdateBrowserRow();}   // in the «+ Браузер» question the choice is kept when the user continues
   var v=info==null?new BrowserVerdict{Status=BypassStatus.Unknown,Code="OTHER"}:await OffUi(()=>BrowserCatalog.Evaluate(info,env));
   if(serial!=browserSerial||IsDisposed||!browserPanel.Visible)return;
   browserInfo=info;
   Log("Браузер для Google: "+item.Name+" — "+v.Status+" ("+v.Code+")");
   ShowBrowserVerdict(info,v);UpdateBrowserButtons();
  }
  void Verdict(Color tone,Func<string> text){verdictFn=text;verdictLine.Show(tone,text());}
  // One sentence: what the browser does with the bridge, and when that is not clear, why.
  void ShowBrowserVerdict(BrowserInfo info,BrowserVerdict v) {
   switch(v.Status){
    case BypassStatus.Follows:Verdict(Theme.Green,()=>L.T("Google в браузере пойдёт через мост"));break;
    case BypassStatus.Bypasses:{bool pol=v.ByPolicy;Verdict(Theme.Amber,()=>pol?L.T("«Безопасный DNS» задан политикой и обходит мост"):L.T("Свой «Безопасный DNS» браузера обходит мост"));break;}
    default:{string code=v.Code;Verdict(Theme.Muted,()=>UnknownLine(code));break;}
   }
   browserOpen.Visible=v.Status!=BypassStatus.Follows&&BrowserCatalog.CanOpenSettings(info,BrowserEnv);
   LayoutBrowserPanel();
  }
  static string UnknownLine(string code){
   switch(code){
    case "NO_PROFILE":return L.T("Не удалось определить: файл настроек не найден");
    case "DEFAULT_UNKNOWN":return L.T("Не удалось определить: Firefox мог включить DoH сам");
    case "SECURE_NO_TEMPLATES":return L.T("Не удалось определить: безопасный DNS без адреса");
    case "NOT_VERIFIED":return L.T("Не удалось определить: надёжной проверки нет");
    case "OTHER":return L.T("Проверьте «Безопасный DNS» в своём браузере");
    default:return L.T("Не удалось определить: браузер не проверяется");
   }
  }
  void UpdateBrowserButtons(){browserContinue.Enabled=browserList.SelectedIndex>=0;browserRecheck.Enabled=true;}
  string serviceBrowserIdentity;
  Bitmap serviceBrowserIcon;
  void UpdateBrowserRow(){
   browserRow.Subtitle=string.IsNullOrEmpty(settings.Browser)?L.T("Не выбран"):BrowserCatalog.NameOf(settings.Browser);
   scopeSel.Items=ScopeItems();
   if(serviceBrowserIdentity==settings.Browser)return;
   serviceBrowserIdentity=settings.Browser;serviceBrowserIcon?.Dispose();serviceBrowserIcon=null;scopeSel.BrowserIcon=null;
   try{
    var chosen=BrowserCatalog.Detect(BrowserEnv).FirstOrDefault(b=>b.Id==settings.Browser);
    if(chosen!=null&&!smoke)serviceBrowserIcon=IconSource!=null?IconSource(chosen.Exe):BrowserCatalog.IconOf(chosen.Exe);
   }catch{}
   scopeSel.BrowserIcon=serviceBrowserIcon;scopeSel.Invalidate();
  }

  // The only thing the program starts here: the browser's own program with its settings address, when the user presses the button.
  void OpenBrowserSettings(){
   var info=browserInfo;if(info==null||!BrowserCatalog.CanOpenSettings(info,BrowserEnv))return;
   Log("Открываю настройку браузера: "+info.Name);
   if(BrowserElevated?.Invoke()??Program.IsAdmin()){Log("Окно запущено с правами администратора: браузер не запускаю, адрес настройки только копирую.");}
   else if(BrowserLauncher!=null)BrowserLauncher(info);
   else{
    try{var psi=new ProcessStartInfo(info.Exe){UseShellExecute=false};if(!string.IsNullOrEmpty(info.SettingsUrl))psi.ArgumentList.Add(info.SettingsUrl);Process.Start(psi);}
    catch(Exception e){Log("Не удалось открыть настройку браузера: "+e.Message);return;}
   }
   // Some browsers do not take an internal address (edge://, chrome://) from the command line; the address is also put on the clipboard for the address bar.
   if(!string.IsNullOrEmpty(info.SettingsUrl)){
    try{if(BrowserClipboard!=null)BrowserClipboard(info.SettingsUrl);else if(!smoke)Clipboard.SetText(info.SettingsUrl);}catch(Exception e){Log("Адрес не скопирован: "+e.Message);return;}
    Flash(browserOpen,L.T("Адрес скопирован: вставьте его в адресную строку"),3500);
   }
  }

  // Smoke renders and self-tests read these invented browsers, never this computer: Chrome with its own secure DNS (yellow), Edge with none (green),
  // Firefox with nothing to go by (grey), Brave that never ran (grey).
  internal static MemoryBrowserEnv SmokeBrowsers() {
   var e=new MemoryBrowserEnv();
   void Install(string exe,string path)=>e.AddFile(path).SetReg(true,@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\"+exe,null,path);
   Install("chrome.exe",@"C:\Program Files\Google\Chrome\Application\chrome.exe");
   Install("msedge.exe",@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe");
   Install("firefox.exe",@"C:\Program Files\Mozilla Firefox\firefox.exe");
   Install("brave.exe",@"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe");
   e.SetText(e.LocalAppData+@"\Google\Chrome\User Data\Local State","{\"dns_over_https\":{\"mode\":\"secure\",\"templates\":\"https://dns.example/dns-query\"}}");
   e.SetText(e.LocalAppData+@"\Microsoft\Edge\User Data\Local State","{\"browser\":{}}");
   e.SetText(e.AppData+@"\Mozilla\Firefox\profiles.ini","[Profile0]\nName=default\nIsRelative=1\nPath=Profiles/abc.default\nDefault=1\n");
   e.SetText(e.AppData+@"\Mozilla\Firefox\Profiles\abc.default\prefs.js","user_pref(\"browser.startup.homepage\", \"about:home\");\n");
   return e;
  }
  // Smoke only: the browser panel, optionally with a browser chosen, drawn after the invented browsers are read.
  internal void SmokeBrowserPanel(string id,bool forScope) {
   known=true;detailsPanel.Visible=false;nav.Selected=TabBridge;ShowBrowserPanel(forScope);PumpBrowser();
   if(id!=null){SelectBrowserForTest(id);PumpBrowser();}
   PumpBrowserMessages();
  }
  void PumpBrowser(){
   var sw=Stopwatch.StartNew();
   while(!browserWork.IsCompleted&&sw.ElapsedMilliseconds<5000){PumpBrowserMessages();System.Threading.Thread.Sleep(5);}
   if(!browserWork.IsCompleted)throw new TimeoutException("Browser smoke work did not finish within five seconds");
   browserWork.GetAwaiter().GetResult();
   PumpBrowserMessages();
  }
  // Smoke only: DoEvents can block inside WinForms GetMessage after its PeekMessage check.
  // Remove messages nonblocking and bound each slice so the task deadline remains effective.
  [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
  struct BrowserSmokeMessage {public IntPtr Window;public uint Id;public UIntPtr WParam;public IntPtr LParam;public uint Time;public int X,Y;public uint Private;}
  [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="PeekMessageW")]
  static extern bool PeekBrowserSmokeMessage(out BrowserSmokeMessage message,IntPtr window,uint min,uint max,uint remove);
  [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="TranslateMessage")]
  static extern bool TranslateBrowserSmokeMessage(ref BrowserSmokeMessage message);
  [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="DispatchMessageW")]
  static extern IntPtr DispatchBrowserSmokeMessage(ref BrowserSmokeMessage message);
  [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="PostQuitMessage")]
  static extern void PostBrowserSmokeQuit(int code);
  internal static void PumpBrowserMessages(){
   var sw=Stopwatch.StartNew();
   for(int count=0;count<128&&sw.ElapsedMilliseconds<10;count++){
    if(!PeekBrowserSmokeMessage(out var message,IntPtr.Zero,0,0,1))return;
    if(message.Id==0x0012){PostBrowserSmokeQuit(unchecked((int)message.WParam.ToUInt64()));throw new InvalidOperationException("Browser smoke received WM_QUIT");}
    TranslateBrowserSmokeMessage(ref message);DispatchBrowserSmokeMessage(ref message);
   }
  }

  // seams for the self-test and the smoke renders
  internal bool BrowserPanelVisible=>browserPanel.Visible;
  internal Bitmap[] BrowserListIcons=>browserList.Items.Select(i=>i.Icon).Where(b=>b!=null).ToArray();
  internal int BrowserPanelHeight=>browserPanel.Height;
  internal int BrowserContentHeight=>Math.Max(browserPanel.ClientSize.Height,browserPanel.AutoScrollMinSize.Height);
  internal bool BrowserFooterReachableForTest(){browserPanel.ScrollControlIntoView(browserContinue);Application.DoEvents();var r=browserPanel.RectangleToClient(browserContinue.RectangleToScreen(browserContinue.ClientRectangle));bool fits=r.Top>=0&&r.Bottom<=browserPanel.ClientSize.Height;browserPanel.AutoScrollPosition=Point.Empty;return fits;}
  internal Rectangle[] BrowserLayout()=>new[]{browserList.Bounds,verdictLine.Bounds,browserOpen.Bounds,browserRecheck.Bounds,browserContinue.Bounds,browserBack.Bounds,browserTitle.Bounds,browserNote.Bounds};
  internal Task BrowserWork=>browserWork;
  internal string[] BrowserNames=>browserList.Items.Select(i=>i.Name).ToArray();
  internal string BrowserHeadline=>verdictLine.Headline;
  internal Color BrowserTone=>verdictLine.Tone;
  internal Control VerdictControl=>verdictLine;
  internal bool BrowserOpenVisible=>browserOpen.Visible;
  internal bool BrowserContinueEnabled=>browserContinue.Enabled;
  internal bool BrowserBackVisible=>browserBack.Visible&&browserBack.Parent==browserPanel;
  internal string BrowserBackText=>browserBack.Text;
  internal void BrowserBackForTest()=>browserBack.PerformClickForTest();
  internal bool BrowserForScope=>browserForScope;
  internal string BrowserRowText=>browserRow.Subtitle;
  internal string SettingsBrowser=>settings.Browser;
  internal void SelectBrowserForTest(string id){int i=browserList.Items.FindIndex(x=>x.Id==id);if(i>=0)browserList.ClickForTest(i);}
  internal void BrowserContinueForTest(){if(browserList.SelectedIndex>=0)CloseBrowserPanel(true);}
  internal void BrowserCancelForTest()=>CloseBrowserPanel(false);   // the same as Esc
  internal void OpenBrowserSettingsForTest()=>OpenBrowserSettings();
  internal void ShowBrowserPanelForTest(bool forScope)=>ShowBrowserPanel(forScope);
  internal void ClickBrowserRowForTest()=>browserRow.ClickForTest();
  internal IEnumerable<Control> BrowserPanelControls{get{var s=new Stack<Control>();s.Push(browserPanel);while(s.Count>0){var c=s.Pop();yield return c;foreach(Control ch in c.Controls)s.Push(ch);}}}
 }
}
