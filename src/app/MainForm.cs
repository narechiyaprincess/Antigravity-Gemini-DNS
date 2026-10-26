using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 public sealed partial class MainForm:Form {
  // Retired: the provider of a bridge that is still applied but is no longer in the catalog; shown so the field names the real provider, never connected again.
  sealed class ProfileItem{public string Name,Label,Template,Slot;public string SelectionId=>Custom?AppSettings.CustomProfileId:Name;public string[] Servers;public bool Custom,Experimental,Retired;
   public ProfileSpec ToSpec()=>new ProfileSpec{Id=SelectionId,Slot=Slot,Template=Template,Servers=Servers,Custom=Custom,Experimental=Experimental};}
  const int W=380,H=584,ChromeH=36,HeaderH=10,NavH=60,PageBottom=H-ChromeH-NavH-10;
  const int TabBridge=0,TabBench=1,TabSettings=2,TabLogs=3,TabInfo=4;
  const string TgUser="your_sister_loves_me",PatcherTgUser="avencoresyt",PatcherUrl="https://sourceforge.net/projects/open-antigravity-patcher/",Credit="@narechiyaprincess";
  [DllImport("uxtheme.dll",CharSet=CharSet.Unicode)]static extern int SetWindowTheme(IntPtr hwnd,string app,string ids);
  [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int value,int size);

  readonly bool smoke;bool hideOnStart;
  readonly AppSettings settings;   // a render run and the self-test start from defaults: the owner's own choices (browser, own provider, profile) must not change what they check
  readonly List<ProfileItem> profiles=new List<ProfileItem>();
  readonly Panel[] pages=new Panel[NavBar.Count];
  readonly ContextLabel subLabel=new ContextLabel();readonly Label statusLabel=new Label(),benchStatus=new Label(),autostartNote=new Label();
  // The texts under the logo are kept as functions, so a change of language writes them again in the new language (no engine call, no state change).
  Func<string> titleFn,detailFn,benchFn,connectionOutcomeFn;
  internal void ConnectionOutcome(Func<string> note){connectionOutcomeFn=note;UpdateDetailsText();}
  string statusDetail=>detailFn?.Invoke();
  string CompactStatusDetail=>statusDetail?.Replace("\r","").Replace("\n"," ");   // when set, replaces the profile line under the status (message text + one action from the code table)
  void Title(Func<string> f){titleFn=f;statusLabel.Text=f();}
  void Detail(Func<string> f){detailFn=f;}
  CodeInfo SwitchOfferStatus(string from,string to){Title(()=>BridgeTitle);Detail(()=>CodeTable.SwitchOfferText(from,to)+"\n"+CodeTable.Lookup("PROVIDER_SWITCH_OFFERED").Action);return CodeTable.Lookup("PROVIDER_SWITCH_OFFERED");}
  string BridgeTitle=>recoveryNeeded||health?.Kind==HealthKind.Drift||needsRestore&&!isActive||!known?(L.En?"Bridge needs repair":"Мост требует исправления"):isActive?L.T("Мост подключён"):L.T("Мост отключён");
  CodeInfo StatusInfo(string code){Title(()=>BridgeTitle);Detail(()=>{var i=CodeTable.Lookup(code);return code==HealthMonitor.ProviderDown?(L.En?"Check the selected DNS profile below.":"Проверьте выбранный DNS-профиль ниже."):i.Text+"\n"+i.Action;});return CodeTable.Lookup(code);}
  void BenchStatus(Func<string> f){benchFn=f;benchStatus.Text=f();}
  string addrSource="";   // Source of the active bridge addresses, shown in «Подробнее».
  readonly LogoBox bigLogo=new LogoBox();
  readonly Font bigFont=Theme.F(11.5f,true),smallFont=Theme.F(10,true);
  readonly PillButton bridge=new PillButton(),diagBtn=new PillButton(),doctorBtn=new PillButton(),copyBtn=new PillButton(),clearBtn=new PillButton(),saveCustom=new PillButton(),delCustom=new PillButton(),findIp=new PillButton(),benchRun=new PillButton(),benchBest=new PillButton(),customClose=new PillButton();
  // Scope of the bridge on the main page (Antigravity / + Gemini / all of Google) and the language switch in the settings.
  readonly ServicePicker scopeSel=new ServicePicker();readonly Segmented langSel=new Segmented();
  readonly LinkRow customRow=new LinkRow(),tgRow=new LinkRow(),patcherRow=new LinkRow(),patcherTgRow=new LinkRow();
  RoundPanel customPanel,settingsCardA,settingsCardB;bool scopeUpdating;readonly PathLabel prevCopy=new PathLabel();readonly PillButton prevDelete=new PillButton();readonly LinkRow browserRow=new LinkRow(),langRow=new LinkRow();
  readonly PillSelect profileSelect=new PillSelect();
  // Ping of the working bridge (shown in the profile field): measured in the background, read-only; never while an operation runs.
  SpeedResult speed;bool speedRunning;string speedKey;
  // Pings of the profiles the list measured (by DoH template): read-only, in the background, at most once per BridgeSpeed.Fresh; shown for a few minutes.
  readonly Dictionary<string,SpeedResult> pings=new Dictionary<string,SpeedResult>(StringComparer.OrdinalIgnoreCase);bool pingsRunning;
  readonly HashSet<string> pingPending=new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // templates being measured now: their rows show an ellipsis instead of a dash
  System.Windows.Forms.Timer pingTimer;
  static readonly TimeSpan PingShown=TimeSpan.FromMinutes(5);string[] activeServers=new string[0];string activeTemplate;
  internal Func<string,string[],CancellationToken,Task<SpeedResult>> SpeedSource;   // test seam; null = the live probe (never in smoke)
  // "Подробнее": one in-window panel for provider notes, the last operation's text/action and the manual recovery steps (instead of tooltips).
  RoundPanel detailsPanel;TextBox detailsBox;Label detailsTitle;readonly PillButton detailsClose=new PillButton(),detailsCopy=new PillButton();
  LinkLabel detailsLink;string lastCode="",lastInstruction;bool checksPassed,recoveryNeeded;bool evidenceUncertain;long evidenceSerial;DateTime networkEvidenceUtc;
  readonly ToggleRow tgFailover=new ToggleRow();   // «Настройки» / «Программа» (formerly in the details panel)
  readonly ToggleRow tgAutostart=new ToggleRow(),tgCloseTray=new ToggleRow(),tgQuiet=new ToggleRow(),tgNotify=new ToggleRow(),tgElevated=new ToggleRow();
  HealthOptions healthOptions;Label infoText;
  readonly TextBox logBox=new TextBox();
  readonly NavBar nav=new NavBar();
  readonly BenchTable bench=new BenchTable();
  readonly System.Windows.Forms.Timer benchTimer=new System.Windows.Forms.Timer{Interval=400};
  NotifyIcon tray;ToolStripMenuItem trayToggle,trayStatus,trayFix,trayOpen,trayExit;Label versionLabel;string detailsProfile,detailsExtra;Icon iconOn,iconOff,trayOn,trayOff;
  readonly Dictionary<(bool Active,TrayState State),Icon> trayDots=new Dictionary<(bool,TrayState),Icon>();   // the tray icon per state, made once and reused (GDI handles are few)
  // 1.5-1 tray health check (read-only, no UAC; "Починить" is always the user's click) and 1.5-2 support report.
  readonly PillButton fixBtn=new PillButton(),reportBtn=new PillButton();
  readonly PillButton revokeTrust=new PillButton(),browserChange=new PillButton();
  // another catalog profile that passed the check when the current one has no usable address; offered, never applied by itself.
  SwitchOffer offer;readonly PillButton altBtn=new PillButton();
  readonly FailoverGuard failover=new FailoverGuard();   // in memory: the cooldown and the offer limit restart with the program
  HealthScheduler healthSched;HealthMonitor healthMon;HealthReport health,lastCheck;System.Windows.Forms.Timer healthTimer;CancellationTokenSource healthCts;bool healthRunning;string lastNotifiedCode;long providerEvidenceRevision=-1;
  OperationHistory history;
  (int Code,string Output) lastDiagnosis=(-1,null);
  CancellationTokenSource currentCts,benchCts;
  string last="";bool busy,needsRestore,known,isActive,exiting,autostartUpdating,benchRunning;DateTime lastStatus=DateTime.MinValue,benchStarted,benchCooldownUntil=DateTime.MinValue;
  BenchRun lastBench;bool lastBenchStale;int benchDone;string benchNetwork;

  internal bool Busy=>busy;
  // Test seams: the journal source and read access to the recovery state of the main page.
  internal Func<string> JournalReader=()=>Recovery.ReadJournal();
  internal void PresentForTest(string engineOutput)=>Present(engineOutput);
  // The user's own operation settles the state: a stale drift (or a disconnect the automation would undo) must not linger.
  void Settle(string engineOutput,long? evidenceRevision=null,string networkRevision=null){
   evidenceUncertain=false;networkEvidenceUtc=DateTime.UtcNow;offer=null;health=null;lastNotifiedCode=null;lastCheck=null;Present(engineOutput);
   if(evidenceRevision.HasValue&&(evidenceRevision.Value!=Volatile.Read(ref evidenceSerial)||networkRevision==null||networkRevision!=NetworkRevisionForEvidence()))InvalidateNetworkEvidence();
  }
  internal void SettleForTest(string engineOutput,long? revision=null,string network=null)=>Settle(engineOutput,revision,network);
  internal bool BridgeActive=>isActive;
  internal bool BridgeEnabled=>bridge.Enabled;
  internal string BridgeText=>bridge.Text;
  internal bool RecoveryNeeded=>recoveryNeeded;
  internal string StatusText=>statusLabel.Text;
  internal Color StatusInkForTest=>statusLabel.ForeColor;
  internal string LastInstruction=>lastInstruction;
  internal void SetBusyForTest(bool value)=>SetBusy(value);
  internal Control AutostartControl=>tgAutostart;
  internal void SelectTab(int i){nav.Selected=i;}
  internal int TabCount=>NavBar.Count;

  ProfileItem Current=>profiles[Math.Max(0,Math.Min(profiles.Count-1,profileSelect.SelectedIndex))];

  string DisplayProfile(string id)=>profiles.FirstOrDefault(p=>p.SelectionId==id)?.Name??id;

  void BuildProfiles(){
   profiles.Clear();
   foreach(var p in ProfileCatalog.BuiltIn.Profiles)profiles.Add(new ProfileItem{Name=p.Id,Label=p.Label+(p.Experimental?L.T("  ·  эксп."):""),Slot=p.Slot,Servers=p.Servers,Template=p.Template,Experimental=p.Experimental});
   if(TryParseCustom(settings.CustomName,settings.CustomServers,settings.CustomTemplate,out var cs,out var ct,out _,out var cn))
    profiles.Add(new ProfileItem{Name=cn,Label="★  "+cn+L.T("  ·  свой"),Servers=cs,Template=ct,Custom=true});
   profileSelect.Items.Clear();foreach(var p in profiles)profileSelect.Items.Add(p.Label);
   RebuildBenchRows();
  }
  int AddRetiredItem(string name,string template){
   int at=profiles.FindIndex(p=>p.Retired&&p.Template==template);if(at>=0)return at;
   profiles.Add(new ProfileItem{Name=name,Label=name+L.T("  ·  вне каталога"),Template=template,Servers=new string[0],Experimental=true,Retired=true});profileSelect.Items.Add(profiles[^1].Label);
   Log("Мост подключён через «"+name+"», которого больше нет в каталоге. Его можно отключить; для нового подключения выберите другой профиль.");
   return profiles.Count-1;
  }
  void DropRetiredItems(){BuildProfiles();int i=profiles.FindIndex(p=>p.SelectionId==settings.Profile);profileSelect.SetSelectedSilently(i<0?0:i);}
  // a custom profile needs only the DoH URL. The name defaults to the host; IPs are an optional advanced field (without them the addresses are found at connect time).
  internal static bool TryParseCustom(string name,string servers,string template,out string[] srv,out string tmpl,out string error,out string finalName){
   srv=null;tmpl=null;error=null;finalName=null;
   if(!Uri.TryCreate((template??"").Trim(),UriKind.Absolute,out var uri)||uri.Scheme!=Uri.UriSchemeHttps||IPAddress.TryParse(uri.IdnHost,out _)){error=L.T("DoH-ссылка должна начинаться с https:// и содержать имя хоста, а не IP.");return false;}
   string nm=string.IsNullOrWhiteSpace(name)?uri.IdnHost:name.Trim();
   if(nm.Length>28){error=L.T("Название не должно быть длиннее 28 символов.");return false;}
   var parts=(servers??"").Split(new[]{',',';',' ','\r','\n'},StringSplitOptions.RemoveEmptyEntries);
   if(parts.Length>4){error=L.T("Можно указать не больше 4 IP-адресов.");return false;}
   var list=new List<string>();
   foreach(var p in parts){if(!IPAddress.TryParse(p.Trim('[',']'),out var ip)||!Program.PublicIp(ip.ToString())){error=L.T("Неверный или непубличный IP-адрес: ")+p;return false;}list.Add(ip.ToString());}
   srv=list.ToArray();tmpl=uri.AbsoluteUri;finalName=nm;return true;
  }

  Label MakeLabel(string text,Color color,Font font,Rectangle r,ContentAlignment a=ContentAlignment.TopLeft){return new Label{Text=text,ForeColor=color,Font=font,BackColor=Color.Transparent,AutoSize=false,Bounds=r,TextAlign=a};}
  RoundPanel Card(Panel page,int x,int y,int w,int h){var c=new RoundPanel{Bounds=new Rectangle(x,y,w,h)};page.Controls.Add(c);return c;}
  TextBox Field(RoundPanel card,string label,int y,string value,int x,int fw){
   card.Controls.Add(MakeLabel(label,Theme.Muted,Theme.F(8f),new Rectangle(20+x,y,fw,15)));
   var box=new RoundPanel{Fill=Theme.Field,Radius=10,Bounds=new Rectangle(20+x,y+16,fw,30),Border=Theme.Line};card.Controls.Add(box);
   var tb=new TextBox{BorderStyle=BorderStyle.None,BackColor=Theme.Field,ForeColor=Theme.Text,Font=Theme.F(9.5f),Bounds=new Rectangle(11,6,fw-22,18),Text=value??""};box.Controls.Add(tb);return tb;
  }
  void Btn(Control parent,PillButton b,string text,Rectangle r,Color fill,Action onClick){b.Text=text;b.Bounds=r;b.SetFill(fill);parent.Controls.Add(b);b.Click+=(_,__)=>{try{onClick();}catch(Exception e){MessageBox.Show(this,e.Message,Text);}};}
  void BtnAsync(Control parent,PillButton b,string text,Rectangle r,Color fill,Func<Task> onClick){b.Text=text;b.Bounds=r;b.SetFill(fill);parent.Controls.Add(b);b.Click+=async(_,__)=>{try{await onClick();}catch(Exception e){MessageBox.Show(this,e.Message,Text);}};}

  public MainForm(bool smoke=false,bool startHidden=false) {
   PerfLog.Mark("form ctor begin");this.smoke=smoke;settings=smoke?new AppSettings{NoSave=true}:AppSettings.Load();string loadedProfile=settings.Profile;settings.NormalizeProfileIdentity();UiDiag.Enabled=!smoke;hideOnStart=startHidden&&!smoke&&settings.StartHidden;
   if(!smoke){Program.Core=settings.Core||!(settings.Gemini||settings.Google);Program.Gemini=settings.Gemini;Program.Google=settings.Google;L.En=settings.Language=="en";}
   Motion.Enabled=!smoke&&SystemInformation.UIEffectsEnabled;   // smoke renders and self-tests see final states; Windows "Show animations" off = no motion
   history=new OperationHistory(smoke?Path.Combine(Path.GetTempPath(),"agdns-smoke-history-"+Guid.NewGuid().ToString("N")+".json"):null);
   SuspendLayout();
   AutoScaleDimensions=new SizeF(96f,96f);AutoScaleMode=AutoScaleMode.Dpi;
   Text="Antigravity-Gemini DNS";FormBorderStyle=FormBorderStyle.None;MaximizeBox=false;SizeGripStyle=SizeGripStyle.Hide;ClientSize=new Size(W,H);BackColor=Theme.Bg;ForeColor=Theme.Text;Font=Theme.F(10);StartPosition=FormStartPosition.CenterScreen;DoubleBuffered=true;
   // A render or self-test window opens off the visible desktop and never activates: the cursor, a click or the focus of whoever works at the
   // computer must not hover, scroll or press anything in it (that made UI checks fail at random). Rendering uses DrawToBitmap, not the screen.
   if(smoke){StartPosition=FormStartPosition.Manual;Location=SmokeLocation;ShowInTaskbar=false;}
   iconOn=Branding.IconSet(true);iconOff=Branding.IconSet(false);Icon=iconOff;

   // No page has a header: the bar below says where you are, and the logo is on the main page and in the window icon.
   for(int i=0;i<pages.Length;i++){pages[i]=new Panel{BackColor=Theme.Bg};pages[i].HandleCreated+=(sender,__)=>{try{SetWindowTheme(((Control)sender).Handle,"DarkMode_Explorer",null);}catch{}};Controls.Add(pages[i]);}
   nav.Bounds=new Rectangle(0,H-NavH,W,NavH);Controls.Add(nav);BuildChrome();
   BuildProfiles();BuildMain(pages[TabBridge]);BuildBench(pages[TabBench]);BuildSettings(pages[TabSettings]);BuildLogs(pages[TabLogs]);BuildInfo(pages[TabInfo]);BuildDetails();BuildCustomPanel();BuildBrowserPanel();BuildServiceStatus(pages[TabBridge]);
   if(smoke)BrowserEnv=SmokeBrowsers();
   RememberPageWidths();ApplyTab();

   int idx=profiles.FindIndex(p=>p.SelectionId==settings.Profile);profileSelect.SetSelectedSilently(idx<0?0:idx);UpdateDetailsText();
   if(settings.Profile!=loadedProfile&&loadedProfile!=AppSettings.CustomProfileId&&!string.IsNullOrWhiteSpace(loadedProfile)&&ProfileCatalog.BuiltIn.Find(loadedProfile)==null)Log("Профиль «"+loadedProfile+"» больше не входит в каталог; выбран «"+settings.Profile+"».");
   profileSelect.SelectedIndexChanged+=async(_,__)=>{offer=null;connectionOutcomeFn=null;lastCheck=null;if(health?.Code==HealthMonitor.ProviderDown)health=null;settings.Profile=Current.SelectionId;SaveSettings();UpdateDetailsText();MarkSelectedRow();LogProfileNotes(Current);await ApplyChosenProfileAsync();};
   nav.SelectedChanged+=(_,__)=>{MarkSelectedRow();ApplyTab();};

   if(!smoke)AutostartHelper.RepointIfMissing(Environment.ProcessPath);
   if(!smoke&&string.IsNullOrWhiteSpace(settings.AutostartPath)){
    if(AutostartHelper.RebindOwnCopy(Environment.ProcessPath))Log("Автозапуск перенесён на эту копию: "+Environment.ProcessPath);
    if(!string.IsNullOrEmpty(settings.PreviousCopy)){settings.PreviousCopy="";SaveSettings();}   // the row «Прежняя копия» is no longer offered: the entry just follows the running copy
   }
   try{autostartUpdating=true;tgAutostart.Checked=AutostartHelper.IsEnabled(AutostartExe);}catch{tgAutostart.Checked=false;}finally{autostartUpdating=false;}
   UpdateAutostartNote();
   tgCloseTray.Checked=settings.CloseToTray;tgQuiet.Checked=settings.StartHidden;tgNotify.Checked=settings.Notify;scopeSel.SetOn(Program.Core,Program.Gemini,Program.Google);langSel.SelectedIndex=L.En?1:0;
   tgAutostart.CheckedChanged+=(_,__)=>OnAutostartToggled();
   ElevatedBackend=smoke?null:new LiveElevatedStartupBackend();tgElevated.CheckedChanged+=(_,__)=>OnElevatedToggled();RefreshElevatedStartup();
   tgCloseTray.CheckedChanged+=(_,__)=>{settings.CloseToTray=tgCloseTray.Checked;SaveSettings();};
   tgQuiet.CheckedChanged+=(_,__)=>{settings.StartHidden=tgQuiet.Checked;SaveSettings();};
   tgNotify.CheckedChanged+=(_,__)=>{settings.Notify=tgNotify.Checked;SaveSettings();};
   scopeSel.SelectionChanged+=(_,__)=>OnScopeSelected();
   langSel.SelectedIndexChanged+=(_,__)=>OnLanguageSelected();
   benchTimer.Tick+=(_,__)=>UpdateBenchStatus();
   bench.RowClicked+=r=>ChooseProfile(r.Id);   // a row is a choice of profile, the same as the list on the main page
   LoadBenchHistory();

   ResumeLayout(false);
   if(smoke)known=true;
   UpdateButtons();
   if(!smoke){
    InitTray();Log("Приложение запущено");ShowJournalHint();ShowCrashNotice();InitHealth();RestoreWindowPosition();
    var t=new System.Windows.Forms.Timer{Interval=50};t.Tick+=async(_,__)=>{t.Stop();t.Dispose();await RefreshStatus();_=MeasureProfilesAsync(false);};t.Start();
    Activated+=async(_,__)=>{UpdatePrecisionProfile();_=MeasureProfilesAsync(false);if(!busy&&(DateTime.UtcNow-lastStatus).TotalSeconds>20)await RefreshStatus();};
    // the pings of the card and the list are kept current while the window is on screen (each one is measured again only once it is older than BridgeSpeed.Fresh)
    pingTimer=new System.Windows.Forms.Timer{Interval=60000};pingTimer.Tick+=(_,__)=>{if(!Visible||WindowState==FormWindowState.Minimized)return;_=MeasureProfilesAsync(false);if(isActive)_=MeasureSpeedAsync(false);};pingTimer.Start();   // the card's "N min ago" is recomputed when the window comes back
   }else{known=true;Title(()=>L.T("Мост отключён"));Log("Готов к работе");Log("Профиль: "+Current.Name);}
   FormClosing+=OnFormClosing;PerfLog.Mark("form ctor end");
  }

  void ApplyTab(){
   var tabClock=PerfLog.On?Stopwatch.StartNew():null;int sel=nav.Selected;if(diagnosticPanel!=null){doctorCts?.Cancel();diagnosticPanel.Visible=false;}if(detailsPanel!=null)detailsPanel.Visible=false;if(customPanel!=null)customPanel.Visible=false;if(browserPanel!=null)CloseBrowserPanel(false);
   float s=LayoutScale;   // layout units were scaled by AutoScale after construction
   for(int i=0;i<pages.Length;i++){
    pages[i].Visible=i==sel;
    pages[i].Bounds=i==TabBridge?new Rectangle(0,0,(int)Math.Round(W*s),(int)Math.Round(PageBottom*s)):new Rectangle(0,(int)Math.Round(HeaderH*s),(int)Math.Round(W*s),(int)Math.Round((PageBottom-HeaderH)*s));
   }
   FitServiceViewport();
   if(tabClock!=null){long ms=tabClock.ElapsedMilliseconds;PerfLog.Mark("tab "+sel+" applied in "+ms+" ms");EventHandler idle=null;idle=(_,__)=>{Application.Idle-=idle;PerfLog.Mark("tab "+sel+" idle after "+tabClock.ElapsedMilliseconds+" ms");};Application.Idle+=idle;}
  }

  // pages
  void BuildMain(Panel pg){
   // Top to bottom: logo, state, one line of context (or the failure text), the one main button, the profile field (with the ping),
   // what goes through the bridge, and the secondary action.
   bigLogo.Bounds=new Rectangle((W-LogoS)/2,LogoY,LogoS,LogoS);bigLogo.Visible=true;bigLogo.Ambient=true;pg.Controls.Add(bigLogo);
   statusLabel.Bounds=new Rectangle(20,StatusY,W-40,32);statusLabel.Font=Theme.F(16,true);statusLabel.ForeColor=Theme.Text;statusLabel.BackColor=Color.Transparent;statusLabel.TextAlign=ContentAlignment.MiddleCenter;Title(()=>L.T("Проверяю мост…"));pg.Controls.Add(statusLabel);
   subLabel.Bounds=new Rectangle(20,132,W-40,38);subLabel.Font=Theme.F(9f);subLabel.ForeColor=Theme.Muted;subLabel.BackColor=Color.Transparent;subLabel.TextAlign=ContentAlignment.TopCenter;subLabel.UseMnemonic=false;subLabel.AutoEllipsis=false;subLabel.DotSource=ProfileLineDot;pg.Controls.Add(subLabel);
   bridge.Font=bigFont;fixBtn.Font=bigFont;altBtn.Font=Theme.F(10.5f,true);   // the offer names a profile: a size smaller so the longest name fits
   bridge.OrbitToggle=false;bridge.Glyph="\uE7E8";fixBtn.Glyph="\uE90F";altBtn.Glyph="\uE8AB";
   BtnAsync(pg,bridge,L.T("Подключить мост"),new Rectangle(14,MainY,W-28,MainH),Theme.Accent,async()=>{if(needsRestore)await Mutate("restore");else await Mutate("enable");});
   pg.Controls.Add(profileCaption);profileCaption.Text=L.En?"DNS profile":"DNS-профиль";profileCaption.Bounds=new Rectangle(28,ProfileCapY,W-56,CapH);profileCaption.ForeColor=Theme.Muted;profileCaption.Font=Theme.F(8f);profileCaption.BackColor=Color.Transparent;profileCaption.AutoSize=false;
   profileSelect.Font=Theme.F(10f,true);profileSelect.Bounds=new Rectangle(14,ProfileY,W-28,ProfileCardFullH);pg.Controls.Add(profileSelect);
   profileSelect.Detail=PingDetail;profileSelect.DropDownOpening+=(_,__)=>{_=MeasureProfilesAsync(false);};
   pg.Controls.Add(scopeCaption);scopeCaption.Text=ScopeNote;scopeCaption.TextAlign=ContentAlignment.MiddleCenter;scopeCaption.Bounds=new Rectangle(14,ScopeCapY,W-28,CapH);scopeCaption.ForeColor=Theme.Muted;scopeCaption.Font=Theme.F(8f);scopeCaption.BackColor=Color.Transparent;scopeCaption.AutoSize=false;
   scopeSel.Items=ScopeItems();scopeSel.Tips=ScopeTips();
   scopeSel.Bounds=new Rectangle(14,ScopeY,W-28,ScopeH);pg.Controls.Add(scopeSel);BuildPrecisionProfile();
   BtnAsync(pg,fixBtn,L.T("Починить"),new Rectangle(14,MainY,W-28,MainH),Theme.AmberSolid,async()=>{if(!busy&&known)await Mutate("enable",true);});fixBtn.Visible=false;fixBtn.ForeColor=Theme.Bg;
   BtnAsync(pg,altBtn,L.T("Подключить через …"),new Rectangle(14,MainY,W-28,MainH),Theme.Accent,async()=>{await SwitchToOfferedAsync();});altBtn.Visible=false;
   // Only when there is something to read: a failure, an unfinished operation or manual steps. A working bridge needs no "more"; its profile notes go to the log when the profile is chosen.
   detailsLink=new LinkLabel{Text=L.T("Подробнее"),AutoSize=false,Bounds=new Rectangle(14,SecondY,W-28,SecondH),TextAlign=ContentAlignment.MiddleCenter,LinkColor=Theme.AccentHover,ActiveLinkColor=Theme.Accent,VisitedLinkColor=Theme.AccentHover,LinkBehavior=LinkBehavior.HoverUnderline,BackColor=Color.Transparent,Font=Theme.F(8.5f),Visible=false};
   detailsLink.LinkClicked+=(_,__)=>ShowDetails(null);pg.Controls.Add(detailsLink);
  }
  // One dense block under the logo: the main button, the profile field, the levels; each field has its caption right above it, with equal gaps.
  // The gap (Theme.SectionGap) is the same above the profile caption, above the levels' caption and above the secondary button; a caption sits CapGap above its field.
  const int LogoY=8,LogoS=88,StatusY=100,MainY=172,MainH=42,MainMinW=236,CapH=14,CapGap=6,FieldH=Theme.FieldHeight,ScopeCapY=222,ScopeY=ScopeCapY+CapH+CapGap,ScopeH=38,ProfileCapY=ScopeY+ScopeH+10,ProfileY=ProfileCapY+CapH+CapGap,SecondY=448,SecondH=28,ProfileCardH=SecondY-4-ProfileY,ProfileCardFullH=SecondY+SecondH-ProfileY,CardHeaderH=38;   // the card ends above the footer link while it shows, and takes the footer strip when it does not
  readonly Label profileCaption=new MetricLabel(),scopeCaption=new MetricLabel();

  // benchmark tab
  readonly List<BenchRow> benchRows=new List<BenchRow>();
  void BuildBench(Panel pg){
   int pageH=PageBottom-HeaderH;
   bench.Bounds=new Rectangle(14,2,W-28,0);pg.Controls.Add(bench);
   // height is fixed after profiles are known (RebuildBenchRows)
   // Under the table: the one line about the measurement, then its two actions side by side; «Диагностика» is a quiet button of its own below (BuildServiceStatus).
   Btn(pg,benchRun,L.T("Измерить"),new Rectangle(14,pageH-86,(W-36)/2,40),Theme.Accent,()=>{var _=ToggleBenchmark();});
   Btn(pg,benchBest,L.T("Выбрать лучший"),new Rectangle(22+(W-36)/2,pageH-86,(W-36)/2,40),Theme.Soft,SelectBestProfile);
   benchRun.Glyph="";
   benchStatus.Bounds=new Rectangle(18,pageH-122,W-36,30);benchStatus.Font=Theme.F(8f);benchStatus.ForeColor=Theme.Muted;benchStatus.BackColor=Color.Transparent;BenchStatus(()=>L.T("Замер ничего не меняет в системе: только DoH-запросы и проверка TLS."));pg.Controls.Add(benchStatus);
   RebuildBenchRows();
  }
  void RebuildBenchRows(){
   var old=benchRows.ToDictionary(r=>r.Id,r=>r.Result);
   benchRows.Clear();
   foreach(var p in profiles){
    var spec=ProfileCatalog.BuiltIn.Find(p.Name);
    string note=spec==null||p.Custom?null:string.Join("\n",new[]{spec.Experimental?L.T("Экспериментальный профиль: не рекомендуется, пока замер не покажет «Работает»."):null,spec.KnownIssue,spec.Note}.Where(x=>!string.IsNullOrWhiteSpace(x)));
    benchRows.Add(new BenchRow{Id=p.SelectionId,Name=p.Name,Slot=p.Slot,CatalogNote=string.IsNullOrEmpty(note)?null:note,Result=old.TryGetValue(p.SelectionId,out var r)?r:null});
   }
   bench.Rows.Clear();bench.Rows.AddRange(benchRows);MarkSelectedRow();
   int pageH=PageBottom-HeaderH;float sc=LayoutScale;int R(int v)=>(int)Math.Round(v*sc);
   bench.Height=R(pageH-130);   // the table takes the page; its rows share the height (layout units, scaled once the window has its DPI)
   benchStatus.Top=bench.Bottom+R(4);benchRun.Top=benchBest.Top=benchStatus.Bottom+R(4);
   bench.Invalidate();
  }
  void LoadBenchHistory(){
   try{
    var runs=Benchmark.LoadHistory();benchHistory=runs;var last=runs.LastOrDefault(r=>!r.Synthetic);
    if(last==null)return;
    // A measurement from an earlier session is shown with its date and never offered as a fresh recommendation.
    lastBench=last;lastBenchStale=true;foreach(var r in last.Results)r.Best=false;ApplyBenchResults(last);
    string when=DateTime.TryParse(last.StartedUtc,null,System.Globalization.DateTimeStyles.RoundtripKind,out var dt)?dt.ToLocalTime().ToString("dd.MM HH:mm"):"?";
    BenchStatus(()=>L.T("Прошлый запуск, замер от ")+when+L.T(". Сеть могла измениться — измерьте заново."));
    benchBest.Enabled=false;
   }catch{}
  }
  // the row of the profile in use gets the accent outline (the same colour as its card on the main page)
  void MarkSelectedRow(){var id=profiles.Count>0?Current.SelectionId:null;foreach(var r in benchRows)r.Selected=r.Id==id;bench.Invalidate();}
  void ApplyBenchResults(BenchRun run){
   foreach(var row in benchRows){var r=run.Results.FirstOrDefault(x=>x.Id==row.Id);row.Result=r;row.Running=false;row.Stale=lastBenchStale;row.Note=r==null?L.T("не измерялся"):null;}
   var ordered=benchRows.OrderBy(r=>r.Result==null?int.MaxValue:(r.Result.Rank==0?int.MaxValue-1:r.Result.Rank)).ToList();
   bench.Rows.Clear();bench.Rows.AddRange(ordered);bench.Invalidate();UpdatePrecisionProfile();profileSelect.Invalidate();
  }
  async Task ToggleBenchmark(){
   if(benchRunning){benchCts?.Cancel();BenchStatus(()=>L.T("Отменяю…"));return;}
   if(DateTime.UtcNow<benchCooldownUntil){int wait=Math.Max(1,(int)(benchCooldownUntil-DateTime.UtcNow).TotalSeconds+1);BenchStatus(()=>L.T("Повторный замер через ")+wait+L.T(" с."));return;}
   PerfLog.Mark("bench begin");var uiClock=Stopwatch.StartNew();benchRunning=true;benchNetwork=BenchmarkContext();benchCts?.Dispose();benchCts=new CancellationTokenSource();benchStarted=DateTime.UtcNow;benchDone=0;var benchmarkToken=benchCts.Token;
   foreach(var row in benchRows){row.Result=null;row.Running=true;row.Stale=false;row.Note=null;}
   bench.Rows.Clear();bench.Rows.AddRange(benchRows);bench.Progress=0;bench.Invalidate();
   benchTimer.Start();UpdateBenchButtons();UpdateBenchStatus();
   Log("Замер профилей: старт ("+profiles.Count+" профилей)");
   var specs=profiles.Where(p=>!p.Retired).Select(p=>p.ToSpec()).ToList();
   var progress=new Progress<BenchProgress>(OnBenchProgress);
   var options=new BenchOptions();BenchRun run=null;
   try{
    using(var live=new LiveTransport())run=await Task.Run(()=>Benchmark.RunAsync(specs,options,live,progress,benchmarkToken));
   }catch(Exception e){if(!IsDisposed)Log("Замер не удался: "+e.Message);}
   finally{benchRunning=false;if(!IsDisposed){benchTimer.Stop();bench.Progress=-1;}benchCts?.Dispose();benchCts=null;benchCooldownUntil=DateTime.UtcNow.AddSeconds(4);}
   if(IsDisposed)return;
   if(run!=null){
    lastBench=run;lastBenchStale=benchNetwork==null||benchNetwork!=BenchmarkContext();ApplyBenchResults(run);
    if(run.Results.Any(r=>r.Attempts>0)){if(!smoke)Benchmark.AppendHistory(run);benchHistory.Add(run);UpdatePrecisionProfile();}
    // the Top-3 working profiles of this measurement: the first ones a switch checks (a synthetic or cancelled run leaves the pool as it was)
    var pool=ProviderPool.From(run,DateTime.UtcNow);if(pool.Count>0){settings.Pool=pool;SaveSettings();}
    var best=run.Results.FirstOrDefault(r=>r.Best);
    BenchStatus(()=>run.Cancelled?L.T("Замер отменён. Показаны результаты до остановки.")
     :best!=null?L.T("Лучший: ")+DisplayProfile(best.Id)+" · "+best.Score+L.T(" баллов · медиана ")+(best.MedianMs<0?"—":best.MedianMs+L.T(" мс"))+(run.Truncated?L.T(" · не все профили успели (лимит ")+(options.TotalBudgetMs/1000)+L.T(" с)"):"")
     :L.T("Подходящего профиля нет: ни один не прошёл проверку всех доменов на этой сети."));
    Log(benchStatus.Text);
   }
   UpdateBenchButtons();UpdateButtons();
   PerfLog.Mark("bench end (network "+(run==null?-1:run.DurationMs)+" ms, first "+(run==null?-1:run.FirstResultMs)+" ms)");if(run!=null)Log("Benchmark firstMs="+run.FirstResultMs+" lastMs="+run.LastResultMs+" networkMs="+run.DurationMs+" uiFinishedMs="+uiClock.ElapsedMilliseconds);
   // brief cooldown so a double click cannot hammer the providers
   var cool=new System.Windows.Forms.Timer{Interval=4000};cool.Tick+=(_,__)=>{cool.Stop();cool.Dispose();if(!IsDisposed)UpdateBenchButtons();};cool.Start();
  }
  void OnBenchProgress(BenchProgress p){
   if(IsDisposed)return;
   var row=benchRows.FirstOrDefault(r=>r.Id==p.ProfileId);if(row==null)return;
   row.Result=p.Partial;row.Running=!p.ProfileFinished;if(p.ProfileFinished)PerfLog.Mark("bench profile done: "+p.ProfileId+" ("+(p.Partial?.Attempts)+" attempts, "+p.Partial?.Verdict+")");
   if(p.ProfileFinished){benchDone=p.Done;bench.Progress=profiles.Count==0?0:(float)benchDone/profiles.Count;}
   bench.Invalidate();
  }
  void UpdateBenchStatus(){
   if(!benchRunning)return;
   BenchStatus(()=>L.T("Идёт замер: ")+benchDone+L.T(" из ")+profiles.Count+L.T(" профилей, ")+(int)(DateTime.UtcNow-benchStarted).TotalSeconds+L.T(" с (лимит ")+(new BenchOptions().TotalBudgetMs/1000)+L.T(" с). «Отмена» остановит замер."));
  }
  void UpdateBenchButtons(){
   benchRun.Text=benchRunning?L.T("Отмена"):L.T("Измерить");benchRun.Glyph=benchRunning?"":"";benchRun.SetFill(benchRunning?Theme.Danger:Theme.Accent);
   benchRun.Enabled=benchRunning||DateTime.UtcNow>=benchCooldownUntil;
   bool hasBest=!lastBenchStale&&Benchmark.IsFresh(lastBench,DateTime.UtcNow,Program.Hosts)&&benchNetwork!=null&&benchNetwork==BenchmarkContext()&&CachedWinnerMatches();
   benchBest.Enabled=!benchRunning&&!busy&&!recoveryNeeded&&hasBest;
  }
  bool CachedWinnerMatches(){
   var selected=profiles.FirstOrDefault(p=>p.SelectionId==lastBench?.Winner);var measured=lastBench?.Results?.FirstOrDefault(r=>r.Id==lastBench.Winner);
   return selected!=null&&Benchmark.CanSelect(measured)&&measured.Template==selected.Template&&(measured.Servers??Array.Empty<string>()).SequenceEqual(selected.Servers??Array.Empty<string>(),StringComparer.OrdinalIgnoreCase);
  }
  void SelectBestProfile(){
   if(lastBenchStale||!Benchmark.IsFresh(lastBench,DateTime.UtcNow,Program.Hosts)||benchNetwork==null||benchNetwork!=BenchmarkContext()||!CachedWinnerMatches())return;
   int i=profiles.FindIndex(p=>p.SelectionId==lastBench.Winner);if(i<0)return;
   Log("Выбран лучший профиль по замеру: "+profiles[i].Name);
   ChooseProfile(profiles[i].SelectionId);
   nav.Selected=TabBridge;
  }
  // One way to pick a profile from anywhere (the list on the main page, a row of the measurement): the choice is saved,
  // and a connected bridge moves to it at once (the same native apply as «Подключить мост»).
  internal bool ChooseProfile(string id){
   if(busy)return false;
   int i=profiles.FindIndex(p=>p.SelectionId==id);if(i<0)return false;
   if(i==profileSelect.SelectedIndex){MarkSelectedRow();return false;}
   profileSelect.SelectedIndex=i;return true;   // SelectedIndexChanged saves it and applies it to a connected bridge
  }
  internal Func<Task> ApplyProfileHook;   // test seam: what a change of profile does to a connected bridge (null = reconnect)
  async Task ApplyChosenProfileAsync(){
   if(smoke&&ApplyProfileHook==null)return;
   if(busy||!known||!needsRestore||!isActive||recoveryNeeded)return;   // only a working bridge follows the choice; an idle one uses it on the next connect
   Log("Смена профиля на подключённом мосте: "+Current.Name);
   try{if(ApplyProfileHook!=null)await ApplyProfileHook();else await Mutate("enable");}catch(Exception e){Log("Смена профиля не удалась: "+e.Message);}
  }
  // Used by --ui-smoke --states: puts the main page into a given state without touching the system, so each state can be rendered and inspected.
  internal void SmokeState(string mode){
   known=true;detailsPanel.Visible=false;nav.Selected=TabBridge;health=null;offer=null;checking=false;refreshing=false;busy=false;connectionOutcomeFn=null;
   switch(mode){
    case "fallback-active":{
     SmokeState("active");
     var selected=profiles.FirstOrDefault(x=>x.Name!=Current.Name)??Current;
     var result=new AssistedResult(AssistedOutcome.NativeVerified,"DNS_CONFIGURED_APPLICATION_NOT_TESTED",true,"NATIVE",null,Attempts:System.Collections.Immutable.ImmutableArray.Create(new AssistedAttempt(selected.Name,"BOOTSTRAP_TIMEOUT","synthetic fixture"),new AssistedAttempt(Current.Name,"DNS_CONFIGURED_APPLICATION_NOT_TESTED","synthetic fixture")));
     ConnectionOutcome(()=>ConnectionReplacementNote(result,selected.Name,L.En));break;
    }
    case "allfailed":case "selection-timeout":{
     SmokeState("idle");ShowConnectionUnavailable(new AssistedResult(AssistedOutcome.Refused,mode=="allfailed"?"ALL_PROVIDERS_UNAVAILABLE":"CONNECTION_SELECTION_TIMEOUT",false,"NONE",null));break;
    }
    case "active":needsRestore=true;isActive=true;recoveryNeeded=false;checksPassed=true;Title(()=>L.T("Мост подключён"));Detail(null);addrSource=L.T("Адреса: найдены сейчас, 12.10 15:30");activeTemplate=Current.Template;speed=new SpeedResult{Ms=42,Proxy="203.0.113.10",Utc=DateTime.UtcNow};break;
    case "active-unchecked":needsRestore=true;isActive=true;recoveryNeeded=false;checksPassed=false;Title(()=>L.T("Мост подключён"));Detail(null);addrSource=L.T("Адреса: закреплённые");activeTemplate=Current.Template;speed=null;break;
    case "recovery":{needsRestore=true;isActive=false;recoveryNeeded=true;checksPassed=false;addrSource="";var ri=StatusInfo("RECOVERY_REQUIRED");break;}
    case "conflict":{needsRestore=false;isActive=false;recoveryNeeded=false;checksPassed=false;addrSource="";var ci=StatusInfo("NRPT_OR_HOSTS_CONFLICT");break;}
    case "drift":{needsRestore=true;isActive=false;recoveryNeeded=false;checksPassed=false;addrSource="";health=new HealthReport{Kind=HealthKind.Drift,Code=HealthMonitor.ProviderDown,CanFix=true,Utc=DateTime.UtcNow};var di=StatusInfo(HealthMonitor.ProviderDown);break;}
    case "switch":{needsRestore=false;isActive=false;recoveryNeeded=false;checksPassed=false;addrSource="";var pi=profiles.FirstOrDefault(x=>!x.Experimental&&x.Name!=Current.Name)??Current;offer=new SwitchOffer{ProfileId=pi.Name,FromId=Current.Name,Reason="BOOTSTRAP_NO_ADDRESS"};var si=SwitchOfferStatus(Current.Name,pi.Name);break;}
    case "noprovider":{needsRestore=false;isActive=false;recoveryNeeded=false;checksPassed=false;addrSource="";var ni=StatusInfo("NO_WORKING_PROVIDER");break;}
    case "wmi":{needsRestore=false;isActive=false;recoveryNeeded=false;checksPassed=false;addrSource="";var wi=StatusInfo("WMI_NOT_RESPONDING");break;}
    case "checking":needsRestore=true;isActive=false;recoveryNeeded=false;checksPassed=false;checking=true;refreshing=true;Title(()=>L.T("Мост подключён"));Detail(null);addrSource="";break;
    case "busy":needsRestore=false;isActive=false;recoveryNeeded=false;checksPassed=false;busy=true;Title(()=>L.T("Подключаю мост…"));Detail(null);addrSource="";break;
    default:health=null;needsRestore=false;isActive=false;recoveryNeeded=false;checksPassed=false;addrSource="";Title(()=>L.T("Мост отключён"));Detail(null);break;
   }
   UpdateDetailsText();UpdateButtons();Application.DoEvents();
  }
  // DrawToBitmap of the form skips the overlay panel; paint it on top of the form snapshot so the details panel can be inspected like every other state.
  internal void OverlayDetails(Bitmap target){
   foreach(var panel in new[]{detailsPanel,browserPanel,customPanel}){
    if(panel==null||!panel.Visible)continue;
    using(var pb=new Bitmap(panel.Width,panel.Height)){
     panel.DrawToBitmap(pb,new Rectangle(Point.Empty,pb.Size));
     var origin=PointToScreen(Point.Empty);
     // a button that became visible after the panel was already on screen comes out blank in the panel's own snapshot: paint every button with its own code
     // (on its own bitmap over the panel colour, because text drawing ignores a graphics transform)
     foreach(Control ch in panel.Controls)if(ch is PillButton btn&&ch.Visible)using(var cb=new Bitmap(ch.Width,ch.Height)){
      using(var gc=Graphics.FromImage(cb)){gc.Clear(panel is RoundPanel rp?rp.Fill:Theme.Card);btn.PaintForSnapshot(gc);}
      using(var gp=Graphics.FromImage(pb))gp.DrawImage(cb,ch.Left,ch.Top);
     }
     using(var g=Graphics.FromImage(target))g.DrawImage(pb,origin.X-Left+panel.Left,origin.Y-Top+panel.Top);
    }
   }
  }
  internal void SmokeDetails(string profile,bool withInstruction){
   lastCode=withInstruction?"RESTORE_UNCONFIRMED":"";
   lastInstruction=withInstruction?Recovery.BuildInstruction("{\"Schema\":1,\"Owner\":\""+new string('a',32)+"\",\"Phase\":\"Restoring\",\"Rules\":[{\"Namespace\":\""+Program.Targets[0]+"\",\"Name\":\"{82c33f7e-1111-4222-8333-444455556666}\"},{\"Namespace\":\""+Program.Targets[1]+"\",\"Name\":\"{a99b6302-aaaa-4bbb-8ccc-ddddeeeeffff}\"}],\"Doh\":[{\"Server\":\""+Program.Servers[0]+"\",\"Created\":true,\"Ownership\":\"Owned\"},{\"Server\":\""+Program.Servers[1]+"\",\"Created\":true,\"Ownership\":\"Owned\"}]}"):null;
   ShowDetails(profile);Application.DoEvents();
  }
  // Used by --ui-smoke --bench: runs the real benchmark through the real UI path and pumps messages until it ends.
  internal void RunBenchmarkForSmoke(Func<string,bool> snapshot,int midRunMs){
   nav.Selected=TabBench;Application.DoEvents();
   var t=ToggleBenchmark();var sw=Stopwatch.StartNew();bool midShot=false;
   while(!t.IsCompleted&&sw.ElapsedMilliseconds<60000){
    Application.DoEvents();Thread.Sleep(30);
    if(!midShot&&sw.ElapsedMilliseconds>=midRunMs){midShot=true;snapshot("running");}
   }
   Application.DoEvents();snapshot("done");
   Console.WriteLine(logBox.Lines.LastOrDefault(line=>line.Contains("Benchmark firstMs=",StringComparison.Ordinal))??"UI_BENCHMARK_NO_TIMING");
  }

  void BuildSettings(Panel pg){
   int cw=W-28,iw=cw-36;
   // Rows in the Windows 11 manner: an icon, what the switch does in one line, a short explanation under it. The own provider and the browser open their own panels.
   var a=settingsCardA=Card(pg,14,4,cw,222+ElevRow);
   pg.ClientSizeChanged+=(_,__)=>LayoutSettings();   // the scroll bar comes and goes with the content: the cards follow the width that is left
   a.Controls.Add(MakeLabel(L.T("ЗАПУСК И ТРЕЙ"),Theme.Muted,Theme.F(7.5f,true),new Rectangle(18,12,iw,14)));
   void T(ToggleRow t,IconKind icon,string text,string sub,int y,bool divider){t.Icon=icon;t.Text=text;t.Subtitle=sub;t.Divider=divider;t.Bounds=new Rectangle(18,y,iw,46);a.Controls.Add(t);}
   T(tgAutostart,IconKind.Power,L.T("Запускать при входе в Windows"),L.T("Программа следит за состоянием моста"),30,true);
   T(tgElevated,IconKind.Shield,L.T("Запуск от администратора"),L.T("Один раз подтвердить в Windows"),76,true);
   T(tgQuiet,IconKind.Tray,L.T("При автозапуске — сразу в трей"),L.T("Без окна, только значок у часов"),76+ElevRow,true);
   T(tgCloseTray,IconKind.Minimize,L.T("Крестик сворачивает в трей"),L.T("Выйти — через меню значка в трее"),122+ElevRow,true);
   T(tgNotify,IconKind.Bell,L.T("Уведомлять о сбоях моста"),L.T("Сообщение, если мост перестал работать"),168+ElevRow,false);
   prevCopy.Divider=true;prevCopy.Visible=false;a.Controls.Add(prevCopy);
   Btn(a,prevDelete,L.T("Удалить"),new Rectangle(18+iw-72,76+(PrevH-26)/2,72,26),Theme.Soft,OnDeletePreviousCopy);prevDelete.Font=Theme.F(8.5f,true);prevDelete.Visible=false;
   var b=settingsCardB=Card(pg,14,238+ElevRow,cw,176+CardBExtra);
   b.Controls.Add(MakeLabel(L.T("ПРОГРАММА"),Theme.Muted,Theme.F(7.5f,true),new Rectangle(18,12,iw,14)));
   langRow.Icon=IconKind.Globe;langRow.Text=L.T("Язык");langRow.Subtitle="Русский · English";langRow.Cursor=Cursors.Default;langRow.Bounds=new Rectangle(18,30,iw-158,46);langRow.ShowChevron=false;b.Controls.Add(langRow);
   b.Controls.Add(new Panel{BackColor=Theme.Divider,Bounds=new Rectangle(58,75,iw-40,1)});   // the row divider, drawn under the selector as well
   langSel.Items=new[]{"Русский","English"};langSel.Bounds=new Rectangle(18+iw-150,38,150,30);b.Controls.Add(langSel);langSel.BringToFront();   // each language named in itself
   customRow.Icon=IconKind.Server;customRow.Text=L.T("Свой DoH-провайдер");customRow.Divider=true;customRow.Bounds=new Rectangle(18,76,iw,46);b.Controls.Add(customRow);UpdateCustomRow();
   customRow.Click+=(_,__)=>ShowCustomPanel();
   browserRow.Icon=IconKind.Compass;browserRow.Text=L.T("Браузер для Google");browserRow.Bounds=new Rectangle(18,122,iw,46);b.Controls.Add(browserRow);UpdateBrowserRow();
   browserRow.ShowChevron=false;browserRow.Cursor=Cursors.Default;browserRow.AccessibleRole=AccessibleRole.StaticText;browserRow.TabStop=false;browserRow.Divider=true;
   // v1.3 opt-in: a confirmed provider failure is offered, never switched
   tgFailover.Icon=IconKind.Globe;tgFailover.Text=L.T("Предлагать резерв");tgFailover.Subtitle=L.T("Только предложение, без переключения");tgFailover.Divider=false;tgFailover.Bounds=new Rectangle(18,168,iw,46);b.Controls.Add(tgFailover);
   tgFailover.Checked=settings.AutoFailover;tgFailover.CheckedChanged+=(_,__)=>{settings.AutoFailover=tgFailover.Checked;SaveSettings();};
   Btn(b,browserChange,L.En?"Change":"Изменить",new Rectangle(18+iw-76,132,76,26),Theme.Soft,()=>ShowBrowserPanel(false));browserChange.Font=Theme.F(8f,true);browserChange.BringToFront();
   revokeTrust.Font=Theme.F(9f,true);
   BtnAsync(pg,revokeTrust,L.T("Отозвать доверие к провайдерам"),new Rectangle(14,426+ElevRow+CardBExtra,cw,36),Theme.Soft,async()=>{if(!busy)await RevokeAssistedTrust();});
  }
  void UpdateCustomRow(){customRow.Subtitle=string.IsNullOrWhiteSpace(settings.CustomTemplate)?L.T("Не задан: нажмите, чтобы добавить"):(string.IsNullOrWhiteSpace(settings.CustomName)?settings.CustomTemplate:settings.CustomName+" · "+settings.CustomTemplate);}
  // own provider: a panel over the window, like «Подробнее»
  void BuildCustomPanel(){
   int pw=W-28,iw=pw-44;
   customPanel=new RoundPanel{Visible=false,Fill=Theme.Card,Radius=16};customPanel.Bounds=new Rectangle(14,10,pw,PageBottom-20);Controls.Add(customPanel);
   customPanel.Controls.Add(MakeLabel(L.T("Свой DoH-провайдер"),Theme.Text,Theme.F(11f,true),new Rectangle(18,14,pw-140,26)));
   Btn(customPanel,customClose,L.T("Закрыть"),new Rectangle(pw-26-96,12,96,28),Theme.Soft,()=>{customPanel.Visible=false;});
   customPanel.Controls.Add(MakeLabel(L.T("Хватит DoH-ссылки: адреса провайдера найдутся при подключении. Название и IP — по желанию."),Theme.Muted,Theme.F(8f),new Rectangle(18,48,iw,32)));
   var t2=Field(customPanel,L.T("DoH-ссылка  https://…/dns-query"),86,settings.CustomTemplate,-2,iw);
   var n=Field(customPanel,L.T("Название (необязательно)"),136,settings.CustomName,-2,iw);
   var s=Field(customPanel,L.T("IP-адреса (необязательно)"),186,settings.CustomServers,-2,iw);
   int bw=(iw-12)/3,by=246;
   BtnAsync(customPanel,findIp,L.T("Найти IP"),new Rectangle(18,by,bw,36),Theme.Soft,async()=>{
    if(!Bootstrap.TryParse(t2.Text,out var boot)){MessageBox.Show(this,L.T("Введите DoH-ссылку с именем хоста, например https://dns.example/dns-query."),Text,MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
    findIp.Enabled=false;findIp.Text=L.T("Ищу…");UseWaitCursor=true;JsonElement res;
    try{res=JsonSerializer.SerializeToElement(await Task.Run(()=>Bootstrap.RunAsync(boot.AbsoluteUri)),Program.JsonOptions);}
    catch(Exception ex){MessageBox.Show(this,L.T("Найти IP не удалось: ")+ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
    finally{UseWaitCursor=false;findIp.Text=L.T("Найти IP");findIp.Enabled=!busy&&!needsRestore;}
    var lines=new List<string>();var pinned=res.GetProperty("Pinned").EnumerateArray().Select(x=>x.GetString()).ToList();
    foreach(var c in res.GetProperty("Candidates").EnumerateArray()){
     bool tlsOk=c.GetProperty("TlsOnName").GetBoolean(),doh=c.GetProperty("DohBothDomains").GetBoolean();
     lines.Add("  "+c.GetProperty("Ip").GetString()+"  —  "+L.T("TLS по имени: ")+(tlsOk?L.T("ок"):L.T("нет"))+"; "+L.T("DoH для всех доменов: ")+(doh?L.T("ок"):L.T("нет")));
    }
    if(pinned.Count==0){MessageBox.Show(this,L.T("Хост ")+res.GetProperty("Host").GetString()+":\n"+(lines.Count==0?L.T("  публичных IPv4 нет"):string.Join("\n",lines))+"\n\n"+L.T("Ни один адрес не прошёл проверку TLS по имени и DoH для всех доменов. Закреплять нечего."),Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
    var take=pinned.Take(4).ToList();
    var answer=MessageBox.Show(this,L.T("Хост ")+res.GetProperty("Host").GetString()+L.T(" (сверка с системным резолвером, публичными DoH-резолверами и провайдерами каталога):")+"\n"+string.Join("\n",lines)+"\n\n"+L.T("Закрепить прошедшие проверку адреса?")+"\n  "+string.Join(", ",take)+"\n\n"+L.T("Адреса попадут в поле IP, а профиль сохранит кнопка «Сохранить»."),Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question);
    if(answer==DialogResult.Yes){s.Text=string.Join(", ",take);if(string.IsNullOrWhiteSpace(n.Text))n.Text=res.GetProperty("Host").GetString();Log("IP найдены и подтверждены: "+string.Join(", ",take));}
   });
   Btn(customPanel,saveCustom,L.T("Сохранить"),new Rectangle(24+bw,by,bw,36),Theme.Accent,()=>{
    if(!TryParseCustom(n.Text,s.Text,t2.Text,out var srv,out var tmpl,out var err,out var finalName)){MessageBox.Show(this,err,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
    n.Text=finalName;
    var oldName=settings.CustomName;var oldServers=settings.CustomServers;var oldTemplate=settings.CustomTemplate;var oldProfile=settings.Profile;
    settings.CustomName=finalName;settings.CustomServers=string.Join(", ",srv);settings.CustomTemplate=tmpl;settings.Profile=AppSettings.CustomProfileId;if(!SaveSettings(false)){settings.CustomName=oldName;settings.CustomServers=oldServers;settings.CustomTemplate=oldTemplate;settings.Profile=oldProfile;return;}RefreshProfiles(AppSettings.CustomProfileId);UpdateCustomRow();Log("Свой профиль сохранён: "+settings.CustomName);Flash(saveCustom,L.T("Сохранено"));
   });
   Btn(customPanel,delCustom,L.T("Удалить"),new Rectangle(30+2*bw,by,iw-12-2*bw,36),Theme.Soft,()=>{
    var oldName=settings.CustomName;var oldServers=settings.CustomServers;var oldTemplate=settings.CustomTemplate;var oldProfile=settings.Profile;
    settings.CustomName=settings.CustomServers=settings.CustomTemplate="";settings.Profile=ProfileCatalog.BuiltIn.Default;if(!SaveSettings(false)){settings.CustomName=oldName;settings.CustomServers=oldServers;settings.CustomTemplate=oldTemplate;settings.Profile=oldProfile;return;}n.Text=s.Text=t2.Text="";RefreshProfiles(ProfileCatalog.BuiltIn.Default);UpdateCustomRow();Log("Свой профиль удалён");
   });
   customPanel.Controls.Add(MakeLabel(L.T("Свой провайдер не входит в проверенные: программа проверит его так же строго (TLS по имени и DoH для всех доменов), но работу не гарантирует."),Theme.Muted,Theme.F(7.5f),new Rectangle(18,by+48,iw,44)));
  }
  void ShowCustomPanel(){
   float sc=LayoutScale;
   customPanel.Bounds=new Rectangle((int)Math.Round(14*sc),(int)Math.Round(10*sc),(int)Math.Round((W-28)*sc),(int)Math.Round((PageBottom-20)*sc));
   customPanel.AutoScrollPosition=Point.Empty;customPanel.Visible=true;customPanel.BringToFront();FitServiceViewport();
  }
  internal bool CustomPanelVisible=>customPanel.Visible;
  internal void ShowCustomPanelForTest()=>ShowCustomPanel();
  void RefreshProfiles(string select){
   BuildProfiles();int i=profiles.FindIndex(p=>p.SelectionId==select);profileSelect.SetSelectedSilently(i<0?0:i);UpdateDetailsText();UpdateButtons();UpdateBenchButtons();
  }

  void BuildLogs(Panel pg){
   int pageH=PageBottom-HeaderH;int cw=W-28;
   var c=Card(pg,14,2,cw,pageH-68-46);c.Fill=Theme.Field;
   logBox.AccessibleName=L.T("Логи");logBox.Multiline=true;logBox.ReadOnly=true;logBox.ScrollBars=ScrollBars.Vertical;logBox.WordWrap=true;logBox.BorderStyle=BorderStyle.None;logBox.BackColor=Theme.Field;logBox.ForeColor=Color.FromArgb(198,203,214);logBox.Font=new Font(Theme.MonoFont,8.5f);logBox.Bounds=new Rectangle(14,14,cw-28,pageH-68-46-28);logBox.HandleCreated+=(_,__)=>{try{SetWindowTheme(logBox.Handle,"DarkMode_Explorer",null);}catch{}};c.Controls.Add(logBox);
   BtnAsync(pg,diagBtn,L.T("Отчёт DNS"),new Rectangle(14,pageH-98,(cw-16)/3,40),Theme.Soft,async()=>{await Run("diagnose");});
   BtnAsync(pg,reportBtn,L.T("Отчёт для поддержки"),new Rectangle(14,pageH-52,cw,40),Theme.Soft,async()=>{await SupportReportAsync();});
   // The raw engine report stays in Logs; the shared diagnostic action lives in Benchmark.
   Btn(pg,copyBtn,L.T("Копировать"),new Rectangle(14+(cw+8)/3,pageH-98,(cw-16)/3,40),Theme.Soft,()=>{if(logBox.Text.Length>0){Clipboard.SetText(logBox.Text);Flash(copyBtn,L.T("Скопировано"));}});
   Btn(pg,clearBtn,L.T("Очистить"),new Rectangle(14+2*((cw+8)/3),pageH-98,cw-2*((cw+8)/3),40),Theme.Soft,()=>{logBox.Clear();});
  }

  // About (who, version, how it works), the patcher Russian accounts need, and one way to reach the author. The cards are not drawn at fixed heights: LayoutInfo
  // measures the paragraphs at the real DPI and language, stacks the three cards with one rhythm and shares what is left of the page between them as air.
  RoundPanel infoAbout,infoPatcher,infoContact;LogoBox infoLogo;Label infoTitle,infoBy,infoNote,patcherCaption,patcherNote,contactCaption;
  void BuildInfo(Panel pg){
   int cw=W-28;
   infoAbout=Card(pg,14,2,cw,InfoAboutMinH);
   infoLogo=new LogoBox();infoAbout.Controls.Add(infoLogo);
   infoTitle=MakeLabel("Antigravity-Gemini DNS",Theme.Text,Theme.F(10.5f,true),Rectangle.Empty);infoAbout.Controls.Add(infoTitle);
   infoBy=MakeLabel("by "+Credit,Theme.AccentHover,Theme.F(8.5f,true),Rectangle.Empty);infoAbout.Controls.Add(infoBy);
   versionLabel=MakeLabel(L.T("Версия")+" "+AppInfo.Version+"  ·  Windows x64",Theme.Muted,Theme.F(8f),Rectangle.Empty);infoAbout.Controls.Add(versionLabel);
   infoText=MakeLabel(InfoTextFor(ScopeNow),Theme.Mix(Theme.Muted,Theme.Text,.3f),Theme.F(9f),Rectangle.Empty);infoText.UseMnemonic=false;infoAbout.Controls.Add(infoText);
   infoNote=MakeLabel(L.T("Остальные сайты и VPN не меняются; при отключении удаляются только правила программы."),Theme.Muted,Theme.F(8f),Rectangle.Empty);infoNote.UseMnemonic=false;infoAbout.Controls.Add(infoNote);
   infoPatcher=Card(pg,14,2,cw,150);
   patcherCaption=MakeLabel("ANTIGRAVITY PATCHER",Theme.Muted,Theme.F(7.5f,true),Rectangle.Empty);infoPatcher.Controls.Add(patcherCaption);
   patcherNote=MakeLabel(L.T("Российским аккаунтам для стабильной работы нужен ещё Antigravity Patcher."),Theme.Muted,Theme.F(8f),Rectangle.Empty);patcherNote.UseMnemonic=false;infoPatcher.Controls.Add(patcherNote);
   patcherRow.Icon=IconKind.Download;patcherRow.Text=L.T("Скачать Antigravity Patcher");patcherRow.Subtitle="sourceforge.net";patcherRow.Divider=true;infoPatcher.Controls.Add(patcherRow);
   patcherRow.Click+=(_,__)=>OpenUrl(PatcherUrl);
   patcherTgRow.Kind=LinkRow.Mark.Telegram;patcherTgRow.Text="@"+PatcherTgUser;patcherTgRow.Subtitle=L.T("Автор патчера — если с ним что-то не так");infoPatcher.Controls.Add(patcherTgRow);
   patcherTgRow.Click+=(_,__)=>OpenTelegram(PatcherTgUser);
   infoContact=Card(pg,14,2,cw,80);
   contactCaption=MakeLabel(L.T("СВЯЗАТЬСЯ"),Theme.Muted,Theme.F(7.5f,true),Rectangle.Empty);infoContact.Controls.Add(contactCaption);
   tgRow.Kind=LinkRow.Mark.Telegram;tgRow.Text="@"+TgUser;tgRow.Subtitle=L.T("Автор Antigravity-Gemini DNS");infoContact.Controls.Add(tgRow);
   tgRow.Click+=(_,__)=>OpenTelegram(TgUser);
   LayoutInfo();
  }
  // layout units
  internal const int InfoAboutMinH=150,InfoPad=18,InfoGap=6,InfoRow=40,InfoTextMaxH=96;
  // Stacks the three cards for the page's real width and scale; returns the height the content needs (pixels). A page too short for it scrolls; a taller one gets air:
  // first between the cards, then inside them (never decoration, never a bigger type).
  string infoLayoutKey;int infoLayoutNeed;   // the measured layout is kept while its inputs (width, height, scale, texts) stay the same: the page is laid out on every state change otherwise
  int LayoutInfo(){
   if(infoAbout==null)return 0;
   float s=LayoutScale;int R(float v)=>(int)Math.Round(v*s);
   int cardW=infoAbout.Width,pad=R(InfoPad),iw=Math.Max(1,cardW-2*pad),tx=pad+R(62),tw=Math.Max(1,cardW-tx-pad);
   string key=cardW+"|"+pages[TabInfo].ClientSize.Height+"|"+s+"|"+infoTitle.Text.GetHashCode()+"|"+infoBy.Text.GetHashCode()+"|"+versionLabel.Text.GetHashCode()+"|"+infoText.Text.GetHashCode()+"|"+infoNote.Text.GetHashCode()+"|"+patcherNote.Text.GetHashCode()+"|"+patcherCaption.Text.GetHashCode()+"|"+contactCaption.Text.GetHashCode();
   if(key==infoLayoutKey)return infoLayoutNeed;
   int Fit(Label l,int width)=>Math.Max(l.Font.Height,l.GetPreferredSize(new Size(width,0)).Height);
   int hTitle=Fit(infoTitle,tw),hBy=Fit(infoBy,tw),hVer=Fit(versionLabel,tw),block=Math.Max(R(46),hTitle+hBy+hVer+R(2));
   int h1=Fit(infoText,iw),h2=Fit(infoNote,iw),h3=Fit(patcherNote,iw),hCap1=Fit(patcherCaption,iw),hCap2=Fit(contactCaption,iw);
   // the height of each card without its share of spare height: top padding, content, bottom padding
   int aboutH=R(12)+block+R(8)+h1+R(4)+h2+R(12),patcherH=R(10)+hCap1+R(2)+h3+R(4)+2*R(InfoRow)+R(6),contactH=R(10)+hCap2+R(2)+R(InfoRow)+R(6);
   int gap=R(InfoGap),top=R(2),used=top+aboutH+patcherH+contactH+2*gap;
   var page=pages[TabInfo];int room=page.ClientSize.Height-R(6);
   int spare=Math.Max(0,room-used);
   int extraGap=Math.Min(R(6),spare/6),extraCard=Math.Min(R(12),(spare-2*extraGap)/3);
   gap+=extraGap;int lift=extraCard/2;
   int y=top;
   // about
   infoAbout.SetBounds(infoAbout.Left,y,cardW,aboutH+extraCard);
   int yy=R(12)+lift;
   infoLogo.SetBounds(pad,yy+(block-R(46))/2,R(46),R(46));
   int ty=yy+(block-(hTitle+hBy+hVer+R(2)))/2;
   infoTitle.SetBounds(tx,ty,tw,hTitle);infoBy.SetBounds(tx,ty+hTitle+R(1),tw,hBy);versionLabel.SetBounds(tx,ty+hTitle+hBy+R(1),tw,hVer);
   yy+=block+R(8);infoText.SetBounds(pad,yy,iw,h1);yy+=h1+R(4);infoNote.SetBounds(pad,yy,iw,h2);
   y+=aboutH+extraCard+gap;
   // patcher
   infoPatcher.SetBounds(infoPatcher.Left,y,cardW,patcherH+extraCard);
   yy=R(10)+lift;patcherCaption.SetBounds(pad,yy,iw,hCap1);yy+=hCap1+R(2);patcherNote.SetBounds(pad,yy,iw,h3);yy+=h3+R(4);
   patcherRow.SetBounds(pad,yy,iw,R(InfoRow));patcherTgRow.SetBounds(pad,yy+R(InfoRow),iw,R(InfoRow));
   y+=patcherH+extraCard+gap;
   // contact
   infoContact.SetBounds(infoContact.Left,y,cardW,contactH+extraCard);
   yy=R(10)+lift;contactCaption.SetBounds(pad,yy,iw,hCap2);tgRow.SetBounds(pad,yy+hCap2+R(2),iw,R(InfoRow));
   y+=contactH+extraCard;
   infoLayoutKey=key;return infoLayoutNeed=y+R(4);
  }
  internal int InfoContentBottom=>infoContact==null?0:infoContact.Bottom;
  internal int InfoPageClientHeightForTest=>pages[TabInfo].ClientSize.Height;
  internal bool InfoPageScrollsForTest=>pages[TabInfo].VerticalScroll.Visible;
  // Every label of the page must show all of its text inside its card, and the cards must neither overlap nor leave the page's width: the list of what does not.
  internal List<string> InfoLayoutProblems(){
   var bad=new List<string>();if(infoAbout==null)return bad;
   FitServiceViewport();
   foreach(var card in new[]{infoAbout,infoPatcher,infoContact}){
    foreach(Label l in card.Controls.OfType<Label>()){
     int need=l.GetPreferredSize(new Size(l.Width,0)).Height;
     if(need>l.Height+1)bad.Add("«"+l.Text.Substring(0,Math.Min(24,l.Text.Length))+"» needs "+need+" px, has "+l.Height);
     if(l.Bottom>card.Height)bad.Add("«"+l.Text.Substring(0,Math.Min(24,l.Text.Length))+"» leaves its card");
    }
    foreach(LinkRow r in card.Controls.OfType<LinkRow>())if(r.Bottom>card.Height||r.Right>card.Width)bad.Add("row «"+r.Text+"» leaves its card");
    if(card.Right>pages[TabInfo].ClientSize.Width)bad.Add("a card leaves the page's width");
   }
   if(infoAbout.Bottom>infoPatcher.Top||infoPatcher.Bottom>infoContact.Top)bad.Add("cards overlap");
   return bad;
  }

  // "Подробнее": provider notes, last operation, manual recovery steps (replaces long tooltips)
  void BuildDetails(){
   detailsPanel=new RoundPanel{Visible=false,Fill=Theme.Card,Radius=16};
   detailsPanel.Bounds=new Rectangle(14,10,W-28,PageBottom-20);Controls.Add(detailsPanel);
   int pw=W-28;
   detailsTitle=MakeLabel(L.T("Подробнее"),Theme.Text,Theme.F(12.5f,true),new Rectangle(18,12,pw-140,26));detailsPanel.Controls.Add(detailsTitle);
   Btn(detailsPanel,detailsClose,L.T("Закрыть"),new Rectangle(pw-18-96,12,96,28),Theme.Soft,()=>{detailsPanel.Visible=false;});
   detailsBox=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,WordWrap=true,BorderStyle=BorderStyle.None,BackColor=Theme.Card,ForeColor=Theme.Text,Font=Theme.F(9f),Bounds=new Rectangle(18,48,pw-36,PageBottom-20-48-58),TabStop=false};
   detailsBox.HandleCreated+=(_,__)=>{try{SetWindowTheme(detailsBox.Handle,"DarkMode_Explorer",null);}catch{}};
   detailsPanel.Controls.Add(detailsBox);
   // The opt-in failover switch lives in «Настройки» and the support report in «Логи»: this panel is only for what a failure says and what to do by hand.
   Btn(detailsPanel,detailsCopy,L.T("Копировать"),new Rectangle(18,PageBottom-20-48,pw-36,36),Theme.Accent,()=>{if(detailsBox.Text.Length>0){Clipboard.SetText(detailsBox.Text);Flash(detailsCopy,L.T("Скопировано"));}});
  }
  // What the footer link used to show for a healthy bridge (the provider's notes and known issues, its DoH address and pinned servers) is written to the log whenever
  // a profile is chosen, so it stays readable without a permanent link on the main page.
  void LogProfileNotes(ProfileItem p){
   var spec=p.Custom?null:ProfileCatalog.BuiltIn.Find(p.Name);
   Log(L.T("Профиль: ")+p.Name+(p.Experimental?L.T(" (экспериментальный: не рекомендуется, пока замер не покажет «Работает»)"):p.Custom?L.T(" (свой: не входит в официально поддерживаемые)"):""));
   Log("DoH: "+p.Template+"; "+L.T("Закреплённые адреса: ")+(p.Servers.Length==0?L.T("нет, адреса ищутся при подключении"):string.Join(", ",p.Servers)));
   if(spec==null)return;
   if(!string.IsNullOrWhiteSpace(spec.KnownIssue))Log(L.T("\r\nИзвестная проблема: ").Trim()+" "+spec.KnownIssue);
   if(!string.IsNullOrWhiteSpace(spec.Note))Log(L.T("\r\nЗаметка: ").Trim()+" "+spec.Note);
  }
  internal void LogProfileNotesForTest()=>LogProfileNotes(Current);
  internal string LogTextForTest=>logBox.Text;
  internal void ShowDetails(string profileName,string extra=null){
   detailsProfile=profileName;detailsExtra=extra;
   var p=profileName==null?Current:(profiles.FirstOrDefault(x=>x.SelectionId==profileName)??profiles.FirstOrDefault(x=>x.Name==profileName)??Current);
   var sb=new System.Text.StringBuilder();
   sb.Append(L.T("Профиль: ")+p.Name+(p.Experimental?L.T(" (экспериментальный: не рекомендуется, пока замер не покажет «Работает»)"):p.Custom?L.T(" (свой: не входит в официально поддерживаемые)"):"")+"\r\n");
   sb.Append("DoH: "+p.Template+"\r\n");
   sb.Append(L.T("Закреплённые адреса: ")+(p.Servers.Length==0?L.T("нет, адреса ищутся при подключении"):string.Join(", ",p.Servers))+"\r\n");
   if(isActive&&addrSource.Length>0&&p==Current)sb.Append(addrSource+"\r\n");
   if(isActive&&p==Current)sb.Append(L.T("Сетевые проверки: ")+(checksPassed&&!evidenceUncertain&&DateTime.UtcNow-networkEvidenceUtc<Doctor.Freshness?L.T("пройдены при подключении"):L.T("не выполнялись (вкладка «Логи» → «Диагностика»)"))+"\r\n");
   if(isActive&&p==Current&&speed!=null&&speed.Ms>=0)sb.Append(L.T("Пинг до сервера-посредника провайдера: ")+speed.Ms+L.T(" мс (")+speed.Proxy+", "+speed.Utc.ToLocalTime().ToString("HH:mm")+")\r\n");
   var spec=p.Custom?null:ProfileCatalog.BuiltIn.Find(p.Name);
   if(spec!=null){
    if(!string.IsNullOrWhiteSpace(spec.KnownIssue))sb.Append(L.T("\r\nИзвестная проблема: ")+spec.KnownIssue+"\r\n");
    if(!string.IsNullOrWhiteSpace(spec.Note))sb.Append(L.T("\r\nЗаметка: ")+spec.Note+"\r\n");
   }
   if(!string.IsNullOrWhiteSpace(extra))sb.Append("\r\n"+extra.Replace("\r\n","\n").Replace("\n","\r\n")+"\r\n");
   if(!string.IsNullOrEmpty(lastCode)){
    var info=CodeTable.Lookup(lastCode);
    sb.Append(L.T("\r\nПоследнее сообщение: ")+info.Text+"\r\n"+(info.Action.Contains(L.T("«Подробнее»"))?"":L.T("Что делать: ")+info.Action+"\r\n")+L.T("Код: ")+lastCode+"\r\n");
   }
   if(!string.IsNullOrWhiteSpace(lastInstruction))sb.Append(L.T("\r\nИнструкция по ручному восстановлению:\r\n")+lastInstruction.Replace("\r\n","\n").Replace("\n","\r\n"));
   detailsBox.Text=sb.ToString();detailsBox.SelectionStart=0;detailsBox.SelectionLength=0;
   float s=LayoutScale;
   detailsPanel.Bounds=new Rectangle((int)Math.Round(14*s),(int)Math.Round(10*s),(int)Math.Round((W-28)*s),(int)Math.Round((PageBottom-20)*s));
   detailsPanel.Visible=true;detailsPanel.BringToFront();
   FitServiceViewport();
  }

  // The Telegram app when it is installed (tg://), the browser otherwise.
  static void OpenTelegram(string user){
   bool desktop=false;try{using(var k=Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"tg\shell\open\command")){desktop=k!=null;}}catch{}
   string url=desktop?"tg://resolve?domain="+user:"https://t.me/"+user;
   try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}
   catch{OpenUrl("https://t.me/"+user);}
  }
  static void OpenUrl(string url){try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}catch{}}

  // What goes through the bridge: any of Antigravity, Gemini and the browser (Google suffixes), in any combination; the hosts and suffixes themselves come from the catalog
  [Flags]internal enum BridgeScope{None=0,Core=1,Gemini=2,Google=4,CoreGemini=Core|Gemini,CoreGoogle=Core|Google,GeminiGoogle=Gemini|Google,All=Core|Gemini|Google}
  internal static BridgeScope ScopeNow=>(Program.Core?BridgeScope.Core:0)|(Program.Gemini?BridgeScope.Gemini:0)|(Program.Google?BridgeScope.Google:0);
  internal static BridgeScope ScopeOf(IEnumerable<string> targets)=>(Program.HasCore(targets)?BridgeScope.Core:0)|(Program.HasGemini(targets)?BridgeScope.Gemini:0)|(Program.HasGoogle(targets)?BridgeScope.Google:0);
  static string ScopeName(BridgeScope v){
   var parts=new List<string>();if(v.HasFlag(BridgeScope.Core))parts.Add("Antigravity");if(v.HasFlag(BridgeScope.Gemini))parts.Add("Gemini");if(v.HasFlag(BridgeScope.Google))parts.Add(L.En?"browser":"браузер");
   return parts.Count==0?"Antigravity":string.Join("/",parts);
  }
  // The names on the picker: the browser carries its own short name once the user has told which one it is.
  string BrowserShortName=>string.IsNullOrEmpty(settings.Browser)||settings.Browser=="other"?(L.En?"Browser":"Браузер"):BrowserCatalog.NameOf(settings.Browser).Replace("Google ","").Replace("Microsoft ","").Replace("Mozilla ","");
  string[] ScopeItems()=>new[]{"Antigravity","Gemini",BrowserShortName};
  static string[] ScopeTips()=>new[]{L.T("Домены Antigravity."),L.T("Gemini Desktop и сайт gemini.google.com."),L.T("Все домены Google: Gemini, NotebookLM, AI Studio, поиск, почта и остальные сервисы.")};
  internal static string InfoTextFor(BridgeScope scope)=>L.T("DNS-запросы выбранных сервисов уходят зашифрованно (DoH) провайдеру. Он отвечает адресом сервера-посредника. Соединение с Google шифруется напрямую; провайдер видит лишь имя сайта.");
  void SetScope(BridgeScope v){
   if(v==BridgeScope.None)v=BridgeScope.Core;
   if(v!=ScopeNow)connectionOutcomeFn=null;
   scopeUpdating=true;try{scopeSel.SetOn(v.HasFlag(BridgeScope.Core),v.HasFlag(BridgeScope.Gemini),v.HasFlag(BridgeScope.Google));}finally{scopeUpdating=false;}
   Program.Core=v.HasFlag(BridgeScope.Core);Program.Gemini=v.HasFlag(BridgeScope.Gemini);Program.Google=v.HasFlag(BridgeScope.Google);
   if(settings.Core!=Program.Core||settings.Gemini!=Program.Gemini||settings.Google!=Program.Google){settings.Core=Program.Core;settings.Gemini=Program.Gemini;settings.Google=Program.Google;if(!smoke)SaveSettings();}
   if(healthOptions!=null)healthOptions.Targets=Program.Hosts;
   UpdateDetailsText();
  }
  void SetGemini(bool value)=>SetScope(value?BridgeScope.CoreGemini:BridgeScope.Core);
  BridgeScope PickerScope=>(scopeSel.IsOn(0)?BridgeScope.Core:0)|(scopeSel.IsOn(1)?BridgeScope.Gemini:0)|(scopeSel.IsOn(2)?BridgeScope.Google:0);
  // A new scope with no bridge: only the next connect changes. With a working bridge the choice is applied at once, as the ordinary connect (compensated switch):
  // the picker shows that it is busy and ignores clicks, a failure puts the picker back and says why in the usual way, and the old bridge stays.
  // Switching the browser on asks first which browser the user uses (a browser with its own secure DNS skips the bridge); backing out puts the picker back.
  bool scopeApplying;
  async void OnScopeSelected(){
   if(scopeUpdating||scopeApplying)return;
   var want=PickerScope;var prev=ScopeNow;
   try{
    if(busy||benchRunning){SetScope(ScopeNow);return;}
    if(want.HasFlag(BridgeScope.Google)&&!prev.HasFlag(BridgeScope.Google)){
     scopeApplying=true;UpdateButtons();
     try{if(!await AskBrowserAsync()){SetScope(ScopeNow);return;}}
     finally{scopeApplying=false;UpdateButtons();}
     if(busy||benchRunning){SetScope(ScopeNow);return;}
    }
    if(needsRestore&&isActive){
     SetScope(want);Log("Через мост: "+ScopeName(want)+" — подключаю заново.");
     scopeApplying=true;UpdateButtons();
     try{await Mutate("enable");}
     finally{scopeApplying=false;}
     if(!SyncGeminiFromJournal()&&!isActive)SetScope(prev);   // refused before the engine and nothing to read: the picker goes back to what it was
     UpdateButtons();
     return;
    }
    SetScope(want);Log("Через мост: "+ScopeName(want)+" (со следующего подключения).");
   }catch(Exception e){scopeApplying=false;SetScope(prev);UpdateButtons();Log("Выбор сервисов: "+e.Message);}
  }
  // The scope of the bridge that exists is the journal's (a plain file, readable without elevation). No journal or a restored one: the selector keeps the user's choice.
  bool SyncGeminiFromJournal(){
   try{
    string j=JournalReader();if(string.IsNullOrWhiteSpace(j))return false;
    using(var doc=JsonDocument.Parse(j)){
     var r=doc.RootElement;if(r.TryGetProperty("Phase",out var ph)&&ph.ValueKind==JsonValueKind.String&&ph.GetString()=="Restored")return false;
     if(!r.TryGetProperty("Targets",out var t)||t.ValueKind!=JsonValueKind.Array)return false;
     var scope=t.EnumerateArray().Select(x=>x.GetString()).Where(x=>x!=null).ToList();
     if(scope.Count==0)return false;
     var sc=ScopeOf(scope);if(sc!=BridgeScope.None&&sc!=ScopeNow)SetScope(sc);return true;
    }
   }catch{return false;}
  }
  // language: the whole window follows it at once; switching rewrites every text of the window, the tray and the tooltips, nothing restarts
  void OnLanguageSelected(){
   string want=langSel.SelectedIndex==1?"en":"ru";
   if(settings.Language==want)return;
   if(busy||benchRunning){langSel.SelectedIndex=L.En?1:0;return;}
   settings.Language=want;SaveSettings();
   ApplyLanguage(want=="en");
  }
  // Static texts are exchanged through the dictionary (every control that holds a plain phrase); the texts that are built from state are made again by their own
  // functions (status, line under it, buttons, tray, the rows of settings, the benchmark note, the open panels). The log keeps its Russian technical lines.
  internal void ApplyLanguage(bool en){
   L.En=en;
   if(!string.IsNullOrWhiteSpace(lastInstruction))lastInstruction=Recovery.BuildInstruction(JournalReader())??lastInstruction;
   SuspendLayout();
   try{
    SwapTexts(this,en);browserChange.Text=L.En?"Change":"Изменить";profileCaption.Text=L.En?"DNS profile":"DNS-профиль";scopeCaption.Text=ScopeNote;scopeSel.AccessibleDescription=ScopeNote;
    BuildProfiles();int pi=profiles.FindIndex(x=>x.SelectionId==settings.Profile);profileSelect.SetSelectedSilently(pi<0?0:pi);
    scopeSel.Items=ScopeItems();
    nav.AccessibleName=L.T(NavBar.Labels[nav.Selected]);
    scopeSel.Tips=ScopeTips();
    if(titleFn!=null)statusLabel.Text=titleFn();
    if(benchFn!=null)benchStatus.Text=benchFn();
    versionLabel.Text=L.T("Версия")+" "+AppInfo.Version+"  ·  Windows x64";
    infoText.Text=InfoTextFor(ScopeNow);
    UpdateDetailsText();UpdateButtons();UpdateAutostartNote();UpdateCustomRow();UpdateBrowserRow();
    if(lastBench!=null)ApplyBenchResults(lastBench);
    if(trayOpen!=null){trayOpen.Text=L.T("Открыть окно");trayFix.Text=L.T("Починить мост");trayExit.Text=L.T("Выход");}
    if(detailsPanel!=null&&detailsPanel.Visible)ShowDetails(detailsProfile,detailsExtra);
    if(browserPanel!=null&&browserPanel.Visible)RelabelBrowserPanel();
   }finally{ResumeLayout(true);}
   Invalidate(true);
  }
  // A control's own phrase, when it is one the dictionary knows, is written in the new language; anything built from state is left to the functions above.
  static void SwapTexts(Control root,bool en){
   foreach(Control c in root.Controls){
    switch(c){
     case PillButton b:b.Text=L.Swap(b.Text,en);break;
     case ToggleRow t:t.Text=L.Swap(t.Text,en);t.Subtitle=L.Swap(t.Subtitle,en);break;
     case LinkRow r:r.Text=L.Swap(r.Text,en);r.Subtitle=L.Swap(r.Subtitle,en);break;
     case Segmented s:s.Items=s.Items.Select(x=>L.Swap(x,en)).ToArray();s.Tips=s.Tips.Select(x=>L.Swap(x,en)).ToArray();break;
     case ServicePicker:break;   // the picker is relabelled by ApplyLanguage (the browser's own name stays)
     case Label l:l.Text=L.Swap(l.Text,en);break;
    }
    if(c.HasChildren)SwapTexts(c,en);
   }
  }
  internal void SyncGeminiFromJournalForTest()=>SyncGeminiFromJournal();
  internal bool GeminiOn=>Program.Gemini&&scopeSel.IsOn(1);
  internal static readonly Point SmokeLocation=new Point(-4000,-4000);
  protected override bool ShowWithoutActivation=>smoke;
  internal bool OffDesktopForTest=>Bounds.Right<0&&Bounds.Bottom<0;
  internal bool GeminiToggleEnabled=>scopeSel.Enabled;
  internal string ButtonStateForTest=>"busy="+busy+" needsRestore="+needsRestore+" active="+isActive+" applying="+scopeApplying+" refreshing="+refreshing+" known="+known;
  internal void SetGeminiForTest(bool value)=>SetGemini(value);
  internal void SetScopeForTest(BridgeScope v)=>SetScope(v);
  // the selection becomes v, as one change of the picker
  internal void ClickScopeForTest(BridgeScope v)=>scopeSel.SelectForTest(v.HasFlag(BridgeScope.Core),v.HasFlag(BridgeScope.Gemini),v.HasFlag(BridgeScope.Google));
  internal bool ScopeIsOn(int index)=>scopeSel.IsOn(index);
  internal BridgeScope ScopeSelected=>PickerScope;
  internal string InfoText=>infoText.Text;

  // Autostart: only the switch turns it on, and it always names the running exe; no question, no copy
  // The switch is on when the entry names this exe. An entry that names another file (a copy elsewhere) shows the switch off with a note; turning it on moves the entry
  // here without asking and remembers the other file, which the row «Прежняя копия» offers to delete (the user confirms; nothing is deleted by itself).
  const int CardBExtra=46,ElevRow=46;   // CardBExtra: the failover switch under the browser row
  readonly LinkRow autostartRow=new LinkRow();
  // The file autostart runs: the one the user chose, else this copy.
  string AutostartExe=>!string.IsNullOrWhiteSpace(settings.AutostartPath)&&File.Exists(settings.AutostartPath)?settings.AutostartPath:Environment.ProcessPath;
  internal Func<string> AutostartFilePicker;   // test seam: the file dialog (null = OpenFileDialog)
  void ChooseAutostartFile(){
   string path=AutostartFilePicker?.Invoke();
   if(AutostartFilePicker==null){
    using var dlg=new OpenFileDialog{Title=L.T("Файл автозапуска"),Filter=L.T("Программа")+" (*.exe)|*.exe",CheckFileExists=true,RestoreDirectory=true};
    try{var cur=AutostartHelper.CurrentTarget()??AutostartExe;dlg.InitialDirectory=Path.GetDirectoryName(cur);dlg.FileName=Path.GetFileName(cur);}catch{}
    if(dlg.ShowDialog(this)!=DialogResult.OK)return;
    path=dlg.FileName;
   }
   if(string.IsNullOrWhiteSpace(path)||!File.Exists(path))return;
   if(!AutostartHelper.SetEnabled(true,path)){Tell(L.T("Не удалось обновить запись автозапуска в реестре."));return;}
   settings.AutostartPath=AutostartHelper.SamePath(path,Environment.ProcessPath??"")?"":path;
   if(!string.IsNullOrEmpty(settings.PreviousCopy)&&AutostartHelper.SamePath(settings.PreviousCopy,path))settings.PreviousCopy="";
   SaveSettings();Log("Автозапуск: "+path);
   autostartUpdating=true;tgAutostart.Checked=true;autostartUpdating=false;
   UpdateAutostartNote();
  }
  void UpdateAutostartNote(){
   string target=AutostartHelper.CurrentTarget();
   autostartRow.Subtitle=target==null||smoke?L.T("Не задан: нажмите, чтобы выбрать .exe"):target;
   bool other=target!=null&&!AutostartHelper.SamePath(target,AutostartExe??"");
   autostartNote.Text=other?L.T("Автозапуск ведёт на другую копию"):"";
   tgAutostart.Subtitle=other?autostartNote.Text:L.T("Программа следит за состоянием моста");tgAutostart.SubtitleColor=other?Theme.Amber:Theme.Muted;
   tgQuiet.Enabled=!busy&&tgAutostart.Checked;
   if(!string.IsNullOrEmpty(settings.PreviousCopy)&&(!File.Exists(settings.PreviousCopy)||AutostartHelper.SamePath(settings.PreviousCopy,Environment.ProcessPath??""))){settings.PreviousCopy="";SaveSettings();}   // gone, or it is this very file: nothing to offer
   prevCopy.Text=L.T("Прежняя копия: ")+settings.PreviousCopy;
   bool show=!string.IsNullOrEmpty(settings.PreviousCopy);
   if(prevCopy.Visible!=show){prevCopy.Visible=show;prevDelete.Visible=show;tgAutostart.Divider=!show;}
   LayoutSettings();
  }
  const int PrevH=36;
  // The row under the switch takes room from the cards below it; positions are set in pixels from the layout units.
  bool settingsLayingOut;
  void LayoutSettings(){
   if(settingsCardA==null||settingsLayingOut)return;
   settingsLayingOut=true;try{LayoutSettingsCore();}finally{settingsLayingOut=false;}
  }
  void LayoutSettingsCore(){
   float s=LayoutScale;int R(int v)=>(int)Math.Round(v*s);int cw=Math.Min(W-28,(int)Math.Floor(pages[TabSettings].ClientSize.Width/s)-28),iw=cw-36,extra=prevCopy.Visible?PrevH:0;
   int scroll=pages[TabSettings].AutoScrollPosition.Y;
   settingsCardA.Bounds=new Rectangle(R(14),R(4)+scroll,R(cw),R(222+ElevRow+extra));
   tgAutostart.Bounds=new Rectangle(R(18),R(30),R(iw),R(46));
   prevCopy.Bounds=new Rectangle(R(18),R(76),R(iw),R(PrevH));
   prevDelete.Bounds=new Rectangle(R(18+iw-72),R(76+(PrevH-26)/2),R(72),R(26));
   tgElevated.Bounds=new Rectangle(R(18),R(76+extra),R(iw),R(46));
   tgQuiet.Bounds=new Rectangle(R(18),R(76+ElevRow+extra),R(iw),R(46));tgCloseTray.Bounds=new Rectangle(R(18),R(122+ElevRow+extra),R(iw),R(46));tgNotify.Bounds=new Rectangle(R(18),R(168+ElevRow+extra),R(iw),R(46));
   settingsCardB.Bounds=new Rectangle(R(14),R(238+ElevRow+extra)+scroll,R(cw),R(176+CardBExtra));
   customRow.Width=autostartRow.Width=R(iw);browserRow.Width=R(iw-82);browserChange.Bounds=new Rectangle(R(18+iw-76),R(132),R(76),R(26));tgFailover.Bounds=new Rectangle(R(18),R(168),R(iw),R(46));
   langSel.Left=R(18+iw-150);
   revokeTrust.Bounds=new Rectangle(R(14),R(426+ElevRow+CardBExtra+extra)+scroll,R(cw),R(36));
   pages[TabSettings].AutoScrollMinSize=new Size(0,R(466+ElevRow+CardBExtra+extra));
  }
  internal Func<string,bool> DeleteConfirm;     // test seam: the question before a previous copy is deleted (null = a message box)
  internal Action<string> Notice;               // test seam: where a plain notice goes (null = a message box)
  internal Func<string,bool> RunningProbe;      // test seam: is that file running (null = look at the processes)
  void Tell(string text){if(Notice!=null)Notice(text);else MessageBox.Show(this,text,Text,MessageBoxButtons.OK,MessageBoxIcon.Information);}
  // startup with administrator rights: one Windows confirmation creates a logon task for a protected copy; off = the task is deleted
  internal IElevatedStartupBackend ElevatedBackend;ElevatedStartupInfo elevatedInfo;bool elevatedUpdating,elevatedBusy;
  static bool UnderInstallDir(string path){try{return path!=null&&Path.GetFullPath(path).StartsWith(Path.GetFullPath(ElevatedStartup.InstallDir)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);}catch{return false;}}
  internal static string ElevatedMessage(int code)=>code switch{
   ElevatedStartup.Cancelled=>L.T("Подтверждение Windows отменено: ничего не изменено."),
   ElevatedStartup.AccountMismatch=>L.T("Подтвердите запрос Windows от той же учётной записи, под которой открыта программа."),
   ElevatedStartup.CopyFailed=>L.T("Не удалось подготовить защищённую копию программы (возможно, она запущена): закройте её и повторите."),
   ElevatedStartup.UnsafeFolder=>L.T("Папка защищённой копии доступна обычным пользователям: задача не создана."),
   ElevatedStartup.TaskFailed=>L.T("Не удалось создать или проверить задачу входа; изменения отменены."),
   ElevatedStartup.VerifyFailed=>L.T("Не удалось создать или проверить задачу входа; изменения отменены."),
   _=>L.T("Не удалось изменить автозапуск с правами администратора.")};
  void RefreshElevatedStartup(){if(ElevatedBackend!=null)_=RefreshElevatedStartupAsync();}
  async Task RefreshElevatedStartupAsync(){
   try{var info=await ElevatedBackend.QueryAsync(default);if(!IsDisposed)ApplyElevatedInfo(info);}catch{if(!IsDisposed)ApplyElevatedInfo(new ElevatedStartupInfo(ElevatedStartupState.Unknown,null,"QUERY_FAILED"));}
  }
  internal void ApplyElevatedInfo(ElevatedStartupInfo info){
   elevatedInfo=info;elevatedUpdating=true;
   try{
    tgElevated.Checked=info.State==ElevatedStartupState.On;
    tgElevated.Subtitle=info.State switch{
     ElevatedStartupState.On=>L.T("Мост без запросов Windows"),
     ElevatedStartupState.Stale=>L.T("Копия устарела: включите, чтобы обновить"),
     ElevatedStartupState.Unknown=>L.T("Не удалось проверить задачу"),
     _=>L.T("Один раз подтвердить в Windows")};
    tgElevated.SubtitleColor=info.State==ElevatedStartupState.Stale||info.State==ElevatedStartupState.Unknown?Theme.Amber:Theme.Muted;
    if(info.State==ElevatedStartupState.On||info.State==ElevatedStartupState.Stale){autostartUpdating=true;try{tgAutostart.Checked=true;}finally{autostartUpdating=false;}}
   }finally{elevatedUpdating=false;}
   tgQuiet.Enabled=!busy&&tgAutostart.Checked;
  }
  void OnElevatedToggled(){
   if(elevatedUpdating)return;
   if(ElevatedBackend==null||busy||elevatedBusy){elevatedUpdating=true;try{tgElevated.Checked=elevatedInfo?.State==ElevatedStartupState.On;}finally{elevatedUpdating=false;}return;}
   _=SwitchElevatedAsync(tgElevated.Checked,false);
  }
  // want = administrator startup on/off. fromAutostartOff: the main autostart switch was turned off, so nothing may start at logon at all.
  internal async Task SwitchElevatedAsync(bool want,bool fromAutostartOff){
   if(ElevatedBackend==null||elevatedBusy)return;
   elevatedBusy=true;UpdateButtons();int code;
   try{code=want?await ElevatedBackend.EnableAsync(default):await ElevatedBackend.DisableAsync(default);}catch(Exception ex){Log("Автозапуск с правами администратора: "+ex.GetType().Name);code=ElevatedStartup.Failed;}
   try{
    if(code==ElevatedStartup.Ok){
     if(want){AutostartHelper.SetEnabled(false);Log("Автозапуск с правами администратора включён (защищённая копия)");}   // the task replaces the plain entry: both would start two copies
     else if(fromAutostartOff){AutostartHelper.SetEnabled(false);Log("Автозапуск выключен (задача удалена)");}
     else if(!UnderInstallDir(Environment.ProcessPath)){AutostartHelper.EnableForCurrent(Environment.ProcessPath,out _);Log("Задача администратора удалена; обычный автозапуск восстановлен");}
     else Log("Задача администратора удалена");
    }else{
     Log("Автозапуск с правами администратора не изменён: код "+code);
     Tell(ElevatedMessage(code));
    }
    try{ApplyElevatedInfo(await ElevatedBackend.QueryAsync(default));}catch{ApplyElevatedInfo(new ElevatedStartupInfo(ElevatedStartupState.Unknown,null,"QUERY_FAILED"));}
    if(code==ElevatedStartup.Ok&&!want){autostartUpdating=true;try{tgAutostart.Checked=AutostartHelper.IsEnabled(AutostartExe);}finally{autostartUpdating=false;}}
    UpdateAutostartNote();
   }finally{elevatedBusy=false;UpdateButtons();}
  }
  void OnAutostartToggled(){
   if(autostartUpdating)return;
   bool want=tgAutostart.Checked;
   if(!want&&elevatedInfo!=null&&(elevatedInfo.State==ElevatedStartupState.On||elevatedInfo.State==ElevatedStartupState.Stale)){_=SwitchElevatedAsync(false,true);return;}
   try{
    if(want){
     string exe=AutostartExe;
     if(!AutostartHelper.EnableForCurrent(exe,out string previous))throw new InvalidOperationException(L.T("Не удалось обновить запись автозапуска в реестре."));
     if(previous!=null){settings.PreviousCopy=previous;SaveSettings();Log("Автозапуск переведён на эту копию: "+exe+"; прежняя: "+previous);}
     else Log("Автозапуск включён: "+exe);
    }else{
     if(!AutostartHelper.SetEnabled(false))throw new InvalidOperationException(L.T("Не удалось обновить запись автозапуска в реестре."));
     Log("Автозапуск выключен");
    }
    UpdateAutostartNote();
   }catch(Exception ex){MessageBox.Show(this,L.T("Автозапуск не изменён: ")+ex.Message,Text);autostartUpdating=true;tgAutostart.Checked=!want;autostartUpdating=false;}
  }
  void OnDeletePreviousCopy(){
   string path=settings.PreviousCopy;if(string.IsNullOrEmpty(path))return;
   bool yes=DeleteConfirm!=null?DeleteConfirm(path):MessageBox.Show(this,L.T("Удалить файл прежней копии?\n")+path+L.T("\n\nУдалится только этот файл программы, больше ничего."),L.T("Прежняя копия"),MessageBoxButtons.YesNo,MessageBoxIcon.Warning)==DialogResult.Yes;
   if(!yes)return;
   var r=AutostartHelper.RemovePreviousCopy(path,Environment.ProcessPath,RunningProbe);
   switch(r){
    case AutostartHelper.CopyRemoval.Removed:Log("Прежняя копия удалена: "+path);settings.PreviousCopy="";SaveSettings();break;
    case AutostartHelper.CopyRemoval.AlreadyGone:settings.PreviousCopy="";SaveSettings();break;
    case AutostartHelper.CopyRemoval.Running:Tell(L.T("Прежняя копия сейчас запущена. Закройте её и повторите."));break;
    case AutostartHelper.CopyRemoval.NotOurs:Tell(L.T("Это не файл этой программы: ничего не удалено."));settings.PreviousCopy="";SaveSettings();break;
    case AutostartHelper.CopyRemoval.ThisCopy:settings.PreviousCopy="";SaveSettings();break;
    default:Tell(L.T("Не удалось удалить файл: ")+path);break;
   }
   UpdateAutostartNote();
  }
  internal bool PreviousCopyVisible=>prevCopy.Visible;
  internal string PreviousCopyText=>prevCopy.Text;
  internal void ClickDeletePreviousCopyForTest()=>prevDelete.PerformClickForTest();
  internal void ToggleAutostartForTest(bool on){tgAutostart.Checked=on;}
  internal void ChooseAutostartFileForTest()=>ChooseAutostartFile();
  internal void ClickBenchRowForTest(string id){var row=benchRows.FirstOrDefault(r=>r.Id==id);if(row!=null)bench.ClickRowForTest(row);}
  internal bool ProfileSelectEnabled=>profileSelect.Enabled;
  internal string CurrentProfileId=>Current.SelectionId;
  internal bool BenchRowSelected(string id)=>benchRows.Any(r=>r.Id==id&&r.Selected);
  internal string AutostartSubtitle=>tgAutostart.Subtitle;
  internal bool AutostartOn=>tgAutostart.Checked;
  internal bool ElevatedOn=>tgElevated.Checked;
  internal string ElevatedSubtitle=>tgElevated.Subtitle;
  internal int PageHeightOf(int tab)=>pages[tab].Height;
  internal int SettingsContentHeight=>Math.Max(pages[TabSettings].ClientSize.Height,pages[TabSettings].AutoScrollMinSize.Height);
  internal bool SettingsFooterReachableForTest(){var page=pages[TabSettings];page.ScrollControlIntoView(revokeTrust);Application.DoEvents();var r=page.RectangleToClient(revokeTrust.RectangleToScreen(revokeTrust.ClientRectangle));bool fits=r.Top>=0&&r.Bottom<=page.ClientSize.Height;page.AutoScrollPosition=Point.Empty;return fits;}
  internal string SettingsPreviousCopy()=>settings.PreviousCopy;
  internal void SetPreviousCopyForTest(string path){settings.PreviousCopy=path;UpdateAutostartNote();}
  // Scrolls the settings page to its footer (or back to the top) and returns the scroll offset the card bounds now include.
  internal int ScrollSettingsForTest(bool toFooter){var page=pages[TabSettings];if(toFooter)page.ScrollControlIntoView(revokeTrust);else page.AutoScrollPosition=Point.Empty;Application.DoEvents();return page.AutoScrollPosition.Y;}
  internal Rectangle[] SettingsRows=>new[]{settingsCardA.Bounds,tgAutostart.Bounds,prevCopy.Bounds,tgQuiet.Bounds,tgCloseTray.Bounds,tgNotify.Bounds,settingsCardB.Bounds,tgElevated.Bounds};

  // tray / lifecycle
  internal void InitTrayForTest(){if(tray==null){InitTray(false);UpdateButtons();}}
  internal string[] TrayTexts=>tray==null?new string[0]:new[]{trayOpen.Text,trayStatus.Text,trayToggle.Text,trayFix.Text,trayExit.Text,tray.Text};
  internal void ShowFailureForTest(string code)=>ShowFailure(code);
  internal string SettingsLanguage=>settings.Language;
  void InitTray(bool visible=true){
   var menu=new ContextMenuStrip{Renderer=new ToolStripProfessionalRenderer(new DarkColors()){RoundedEdges=false},ShowImageMargin=false,BackColor=Theme.Card,ForeColor=Theme.Text,Font=Theme.F(9.5f)};
   var open=trayOpen=new ToolStripMenuItem(L.T("Открыть окно")){ForeColor=Theme.Text,Padding=new Padding(4,5,4,5)};open.Click+=(_,__)=>ShowWindow();
   trayStatus=new ToolStripMenuItem(L.T("Мост: проверяю…")){Enabled=false,ForeColor=Theme.Muted};
   trayToggle=new ToolStripMenuItem(L.T("Подключить мост")){ForeColor=Theme.Text,Padding=new Padding(4,5,4,5)};trayToggle.Click+=async(_,__)=>{try{if(!busy&&known)await Mutate(needsRestore?"restore":"enable");}catch(Exception e){Log("Ошибка: "+e.Message);}};
   trayFix=new ToolStripMenuItem(L.T("Починить мост")){ForeColor=Theme.Amber,Padding=new Padding(4,5,4,5),Visible=false};trayFix.Click+=async(_,__)=>{try{if(!busy&&known&&health!=null&&health.CanFix)await Mutate("enable",true);}catch(Exception e){Log("Ошибка: "+e.Message);}};
   AddServiceTray(menu);
   var exit=trayExit=new ToolStripMenuItem(L.T("Выход")){ForeColor=Theme.Text,Padding=new Padding(4,5,4,5)};exit.Click+=(_,__)=>{exiting=true;Close();};
   menu.Items.AddRange(new ToolStripItem[]{open,trayStatus,new ToolStripSeparator(),trayFix,trayToggle,new ToolStripSeparator(),exit});
   trayOn=new Icon(iconOn,SystemInformation.SmallIconSize);trayOff=new Icon(iconOff,SystemInformation.SmallIconSize);
   tray=new NotifyIcon{Icon=trayOff,Text="Antigravity-Gemini DNS",Visible=visible,ContextMenuStrip=menu};
   tray.MouseClick+=(_,e)=>{if(e.Button==MouseButtons.Left)ToggleWindow();};
  }
  void ToggleWindow(){if(Visible&&WindowState!=FormWindowState.Minimized)Hide();else ShowWindow();}
  public void ShowWindow(){
   bool fade=!Visible&&Motion.Enabled;
   if(fade)Opacity=0;
   if(!Visible)Show();if(WindowState==FormWindowState.Minimized)WindowState=FormWindowState.Normal;Activate();BringToFront();
   if(fade){long st=Stopwatch.GetTimestamp();Motion.Run(()=>{if(IsDisposed)return false;double t=(Stopwatch.GetTimestamp()-st)*1000.0/Stopwatch.Frequency/140;if(t>=1){Opacity=1;return false;}Opacity=Motion.Ease((float)t);return true;});}
  }
  // A short confirmation on the button itself instead of a dialog.
  // The original label is held only while a flash runs, and it comes back in the language the window has then (the language may change meanwhile).
  static void Flash(PillButton b,string text,int ms=1400){
   var prev=b.Tag as Tuple<string,System.Windows.Forms.Timer>;string orig=prev?.Item1??b.Text;
   if(prev!=null){prev.Item2.Stop();prev.Item2.Dispose();}
   var t=new System.Windows.Forms.Timer{Interval=ms};b.Tag=Tuple.Create(orig,t);b.Text=text;
   t.Tick+=(_,__)=>{t.Stop();t.Dispose();if(b.IsDisposed)return;b.Tag=null;b.Text=L.Swap(L.Swap(orig,false),L.En);};t.Start();
  }
  internal static void FlashForTest(PillButton b,string text,int ms)=>Flash(b,text,ms);
  RegisteredWaitHandle showRegistration;
  public void RegisterShowEvent(EventWaitHandle evt){showRegistration?.Unregister(null);showRegistration=ThreadPool.RegisterWaitForSingleObject(evt,(st,to)=>{try{if(IsHandleCreated&&!IsDisposed)BeginInvoke(new Action(ShowWindow));}catch{}},null,-1,false);}
  void Notify(string text){if(tray!=null&&settings.Notify&&!Visible)try{tray.ShowBalloonTip(2500,"Antigravity-Gemini DNS",text,ToolTipIcon.Info);}catch{}}

  protected override void SetVisibleCore(bool value){
   if(hideOnStart){hideOnStart=false;if(!IsHandleCreated)CreateHandle();value=false;}
   base.SetVisibleCore(value);
  }
  protected override void OnHandleCreated(EventArgs e){
   base.OnHandleCreated(e);
   try{int one=1;DwmSetWindowAttribute(Handle,20,ref one,4);int cap=ColorTranslator.ToWin32(Theme.Bg);DwmSetWindowAttribute(Handle,35,ref cap,4);int txt=ColorTranslator.ToWin32(Theme.Text);DwmSetWindowAttribute(Handle,36,ref txt,4);}catch{}
  }
  void OnFormClosing(object s,FormClosingEventArgs e){
   SaveWindowPosition();
   if(!smoke&&!exiting&&e.CloseReason==CloseReason.UserClosing&&settings.CloseToTray&&tray!=null){
    e.Cancel=true;Hide();
    if(!settings.TrayHintShown){settings.TrayHintShown=true;SaveSettings();try{tray.ShowBalloonTip(3000,"Antigravity-Gemini DNS",L.T("Программа осталась в трее. Выход — в меню значка."),ToolTipIcon.Info);}catch{}}
    return;
   }
   // Windows shutdown or log-off is never held by a dialog: the operation is cancelled and its journal is recovered on the next start.
   if(busy||benchRunning){if(!smoke&&e.CloseReason==CloseReason.UserClosing&&MessageBox.Show(this,L.T("Операция ещё идёт. Прервать её и закрыть программу?"),Text,MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes){e.Cancel=true;exiting=false;return;}reportCts?.Cancel();currentCts?.Cancel();benchCts?.Cancel();}
  }
  protected override void Dispose(bool disposing){
   if(disposing){
    try{currentCts?.Cancel();currentCts?.Dispose();}catch(ObjectDisposedException){}
    try{benchCts?.Cancel();benchCts?.Dispose();}catch(ObjectDisposedException){}
    try{lifeCts.Cancel();lifeCts.Dispose();}catch(ObjectDisposedException){}
    showRegistration?.Unregister(null);showRegistration=null;StopHealth();assistedBackend?.Dispose();
    currentCts=null;benchCts=null;benchTimer.Dispose();pingTimer?.Dispose();if(tray!=null){tray.Visible=false;tray.Dispose();}
    serviceBrowserIcon?.Dispose();serviceBrowserIcon=null;scopeSel.BrowserIcon=null;
    Icon=null;iconOn?.Dispose();iconOff?.Dispose();iconOn=iconOff=null;trayOn?.Dispose();trayOff?.Dispose();foreach(var dot in trayDots.Values)dot.Dispose();trayDots.Clear();
   }
   base.Dispose(disposing);
  }

  // state
  void Log(string line){
   if(logBox.IsDisposed)return;
   logBox.AppendText("["+DateTime.Now.ToString("HH:mm:ss")+"] "+line+"\r\n");
   if(logBox.TextLength>200000)logBox.Text=logBox.Text.Substring(logBox.TextLength-100000);
  }
  void LogBlock(string text){if(!logBox.IsDisposed){logBox.AppendText(text.Replace("\r\n","\n").Replace("\n","\r\n")+"\r\n");}}
  string Scope=>ScopeName(ScopeNow);
  // The dot before the profile line under the title: green once the connected bridge has been checked, muted otherwise. Only the plain «profile → services» line has one (not a failure text).
  Color ProfileLineDot(){
   if(busy||Checking||statusDetail!=null||subLabel.Text!=Current.Name+"  →  "+Scope)return Color.Empty;
   return ServiceReady||isActive&&checksPassed&&!evidenceUncertain&&!recoveryNeeded?Theme.Green:Theme.Muted;
  }
  internal Color ProfileLineDotForTest=>ProfileLineDot();
  void UpdateDetailsText(){UpdatePrecisionProfile();subLabel.Text=CompactStatusDetail??(Checking?L.T("Проверяю, что мост работает…"):needsRestore?(connectionOutcomeFn?.Invoke()??(Current.Name+"  →  "+Scope)):(Current.Name+"  →  "+Scope));}
  // The ping of a profile for its field and its row in the list: the working bridge's own measurement for the profile in use, the list's measurements for the rest.
  // Empty text = not known. A failure to answer is said in words.
  (string text,Color dot) PingDetail(int i){
   if(i<0||i>=profiles.Count)return ("",Color.Empty);
   var p=profiles[i];SpeedResult r=null;
   if(isActive&&!busy&&activeTemplate!=null&&string.Equals(p.Template,activeTemplate,StringComparison.OrdinalIgnoreCase)){
    if(Checking)return ("",Color.Empty);
    r=speed;if(r==null)return speedRunning?("…",Color.Empty):("",Color.Empty);
   }else lock(pings){if(!pings.TryGetValue(p.Template??"",out r)||DateTime.UtcNow-r.Utc>PingShown)r=null;if(r==null&&pingPending.Contains(p.Template??""))return ("…",Color.Empty);}
   if(r==null)return ("",Color.Empty);
   if(r.Utc>DateTime.UtcNow||DateTime.UtcNow-r.Utc>PingShown)return (L.T("устарело"),Theme.Muted);
   if(r.Ms>=0)return (r.Ms+L.T(" мс"),r.Ms<=200?Theme.Green:Theme.Amber);
   return (L.T("не ответил"),Theme.Amber);
  }
  void UpdateSummary(){UpdatePrecisionProfile();profileSelect.Tip=string.IsNullOrEmpty(PingDetail(profileSelect.SelectedIndex).text)?null:PingTip;profileSelect.RefreshDetails();}
  // The tooltip of the field names what the number is.
  internal string PingTip=>L.T("Задержка до сервера-посредника провайдера: время TCP-соединения с адресом, который провайдер отдаёт для ")+Program.Target+".";
  internal string ProfilePingText(int i)=>PingDetail(i).text;
  internal Color ProfilePingDot(int i)=>PingDetail(i).dot;
  internal string ProfilePingTextNow=>PingDetail(profileSelect.SelectedIndex).text;
  internal Color ProfilePingDotNow=>PingDetail(profileSelect.SelectedIndex).dot;
  internal int ProfileCount=>profiles.Count;
  internal string[] ProfileLabels=>profileSelect.Items.ToArray();
  // The dense block under the logo, at the current scale: the same gap above each caption, the same gap under each caption, fields of one size and one width,
  // equal margins, everything inside the page. Used by the self-test and by the stress run at every scale.
  // Everything the self-test and the scale runs ask of the main page at the scale the window has now: the dense block in five states, no overlaps, the longest
  // profile name beside a ping fits its field.
  internal List<string> ScaleProblems(){
   var bad=new List<string>();string at=DeviceDpi+" dpi ";
   foreach(string mode in new[]{"idle","active","drift","switch","busy"}){
    SmokeState(mode);foreach(var g in MainGeometryProblems())bad.Add(at+mode+": "+g);
    var rs=MainPageRects.ToList();int ph=MainPageHeight;
    for(int i=0;i<rs.Count;i++){
     if(rs[i].Item2.Bottom>ph+1)bad.Add(at+mode+": "+rs[i].Item1+" leaves the page");
     for(int j=i+1;j<rs.Count;j++)if(rs[i].Item2.IntersectsWith(rs[j].Item2))bad.Add(at+mode+": "+rs[i].Item1+" over "+rs[j].Item1);
    }
   }
   SmokeState("idle");float k=DeviceDpi/96f;
   foreach(var name in profileSelect.Items.Where(n=>n.Length<24)){
    int nameW=TextRenderer.MeasureText(name,Theme.F(9.5f),Size.Empty,PillSelect.Flags).Width,pingW=TextRenderer.MeasureText(L.T("не ответил"),Theme.F(9f),Size.Empty,PillSelect.Flags).Width;
    if(nameW+pingW+(14+11+10+36)*k>profileSelect.Width)bad.Add(at+"«"+name+"» and its ping do not fit the field");
   }
   return bad;
  }
  internal List<string> MainGeometryProblems(){
   var bad=new List<string>();float s=LayoutScale;int tol=Math.Max(2,(int)Math.Ceiling(1.5*s));
   var vis=MainButtons.Where(b=>b.Visible).OrderBy(b=>b.Top).ToList();if(vis.Count==0){bad.Add("no button");return bad;}
   var main=vis[0];Rectangle pc=scopeCaption.Bounds,pf=scopeSel.Bounds,sc=profileCaption.Bounds,sf=profileSelect.Bounds;
   void Eq(string what,int a,int b){if(Math.Abs(a-b)>tol)bad.Add(what+": "+a+" vs "+b);}
   if(pc.Top<subLabel.Bottom)bad.Add("service caption overlaps status context");
   Eq("caption → field (profile, levels)",pf.Top-pc.Bottom,sf.Top-sc.Bottom);
   Eq("profile card height",sf.Height,(int)Math.Round(CardUnits*s));Eq("field left",pf.Left,sf.Left);Eq("field width",pf.Width,sf.Width);Eq("field margins ("+pf.Left+".."+pf.Right+" of "+(int)Math.Round(W*s)+")",pf.Left,pages[TabBridge].ClientSize.Width-pf.Right);   // against the designed width, not the window's (a frame can be a pixel or two off)
   if(pc.Top<main.Bottom||sc.Top<pf.Bottom||sf.Bottom>MainPageHeight)bad.Add("a field leaves its place (button "+main.Top+"–"+main.Bottom+" "+main.Text+", caption "+pc.Top+", field "+pf.Top+"–"+pf.Bottom+", levels caption "+sc.Top+", levels "+sf.Top+"–"+sf.Bottom+", page "+pages[TabBridge].Height+", client "+ClientSize+", dpi "+DeviceDpi+")");
   return bad;
  }
  internal Rectangle ProfileBounds=>profileSelect.Bounds;
  internal Rectangle ScopeBounds=>scopeSel.Bounds;
  internal string ProfileCaptionText=>profileCaption.Text;
  internal bool ScopeBusy=>scopeSel.Busy;
  // The scale the controls have really been multiplied by: the tab bar is placed once in layout units and only ever scaled by WinForms (AutoScale, DPI changes),
  // so its width is right whether the window is shown yet or not (a window started hidden in the tray has a handle long before it is scaled).
  internal float LayoutScale=>IsHandleCreated?DeviceDpi/96f:1f;
  internal Rectangle BridgeBounds=>bridge.Bounds;
  internal bool BridgeSubtle=>bridge.Subtle;
  internal string DetailsText=>detailsBox.Text;
  internal Control ScopeControl=>scopeSel;
  internal Control ProfileControl=>profileSelect;
  internal PillSelect ProfileSelectForTest=>profileSelect;
  internal bool FailoverInSettingsForTest=>tgFailover.Parent==settingsCardB&&settingsCardB.Parent==pages[TabSettings]&&tgFailover.Bottom<=settingsCardB.Height;
  internal bool ReportInLogsForTest=>reportBtn.Parent==pages[TabLogs]&&reportBtn.Bottom<=pages[TabLogs].ClientSize.Height&&diagBtn.Bottom<=reportBtn.Top;
  internal bool DetailsPanelHoldsOnlyNotesForTest=>tgFailover.Parent!=detailsPanel&&reportBtn.Parent!=detailsPanel&&detailsCopy.Parent==detailsPanel;
  internal bool FailoverSettingForTest=>settings.AutoFailover;
  internal void SetFailoverForTest(bool value)=>tgFailover.Checked=value;
  internal IntPtr TelemetryHandleForTest=>profileTelemetry.Handle;
  internal Control NavControl=>nav;
  internal ToggleRow[] Toggles=>new[]{tgAutostart,tgElevated,tgQuiet,tgCloseTray,tgNotify};
  internal string[] ToggleTexts=>Toggles.Select(t=>t.Text).ToArray();
  internal LinkRow[] InfoRows=>new[]{patcherRow,patcherTgRow,tgRow};
  internal Label InfoLabel=>infoText;
  internal LinkRow CustomRow=>customRow;
  internal string SubText=>subLabel.Text;
  // Every text the user can see right now (labels, buttons, switch rows, link rows, selectors, chips, the open overlay): the English check walks it.
  internal IEnumerable<string> AllVisibleTexts(){
   var stack=new Stack<Control>();stack.Push(this);
   while(stack.Count>0){
    var c=stack.Pop();if(c!=this&&!c.Visible)continue;
    if(c is Label l&&!string.IsNullOrEmpty(l.Text))yield return l.Text;
    else if(c is PillButton b&&!string.IsNullOrEmpty(b.Text))yield return b.Text;
    else if(c is ToggleRow t){yield return t.Text;if(!string.IsNullOrEmpty(t.Subtitle))yield return t.Subtitle;}
    else if(c is LinkRow r){yield return r.Text;if(!string.IsNullOrEmpty(r.Subtitle))yield return r.Subtitle;}
    else if(c is Segmented sg){foreach(var i in sg.Items)yield return i;foreach(var tp in sg.Tips)yield return tp;}
    else if(c is VerdictLine vl){yield return vl.Headline;}
    else if(c is PathLabel pl&&!string.IsNullOrEmpty(pl.Text))yield return pl.Text;
    else if(c is BrowserList bl){foreach(var it in bl.Items)yield return it.Name;}
    else if(c is PillSelect ps&&ps.Items.Count>0){yield return ps.Items[Math.Max(0,Math.Min(ps.Items.Count-1,ps.SelectedIndex))];var pd=ps.Detail?.Invoke(ps.SelectedIndex);if(pd!=null&&!string.IsNullOrEmpty(pd.Value.text))yield return pd.Value.text;}
    foreach(Control ch in c.Controls)stack.Push(ch);
   }
  }
  internal Segmented LanguageControl=>langSel;
  internal ServicePicker ScopePicker=>scopeSel;
  internal IconKind LanguageIconForTest=>langRow.Icon;
  internal IconKind BrowserRowIconForTest=>browserRow.Icon;
  internal int MainPageHeight=>Math.Max(pages[TabBridge].Height,pages[TabBridge].AutoScrollMinSize.Height);
  internal IEnumerable<(string,Rectangle)> MainPageRects=>pages[TabBridge].Controls.Cast<Control>().Where(c=>c.Visible).Select(c=>(c.GetType().Name+(c is Label l?":"+l.Text.Length:""),c.Bounds));
  internal bool HeaderControlsVisible=>Controls.Cast<Control>().Any(c=>c.Visible&&(c is Label||c is LogoBox));   // no page has a title or a small logo
  internal int PageTopUnits(int tab)=>(int)Math.Round(pages[tab].Top/LayoutScale)-ChromeH;
  internal IEnumerable<Label> LabelsOf(int tab){var stack=new Stack<Control>();stack.Push(pages[tab]);while(stack.Count>0){var c=stack.Pop();foreach(Control ch in c.Controls){if(ch is Label l&&!(ch is LinkLabel))yield return l;stack.Push(ch);}}}
  internal IEnumerable<string> MainTexts=>LabelsOf(TabBridge).Where(l=>l.Visible).Select(l=>l.Text);
  internal bool DetailsLinkVisible=>detailsLink.Visible;
  // «Открыть замер» was a second way to the tab the bar already shows: no control of the bridge page may offer it
  internal bool OpenBenchmarkLinkForTest=>pages[TabBridge].Controls.OfType<LinkLabel>().Any(l=>l.Text=="Открыть замер"||l.Text=="Open Benchmark");
  internal int ProfileCardUnitsForTest=>CardUnits;
  // «Подробнее» appears only with something to read: a message of a failed or cancelled operation, a state that needs recovery, manual steps.
  bool DetailsWorthShowing(bool other)=>!busy&&!Checking&&!other&&(statusDetail!=null||recoveryNeeded||!string.IsNullOrWhiteSpace(lastInstruction));
  // the profile card: its height follows the footer link (units)
  int CardUnits=>detailsLink!=null&&detailsLink.Visible?ProfileCardH:ProfileCardFullH;
  void UpdateButtons(){
   bool showFix=health!=null&&health.Kind==HealthKind.Drift&&health.CanFix&&!recoveryNeeded&&needsRestore;
   bool showAlt=offer!=null&&!recoveryNeeded;showFix=showFix&&!showAlt;
   // One main button: "Починить" / "Подключить через …" take the big top slot and push the plain connect/disconnect button to the small bottom slot as a secondary one.
   bool other=showFix||showAlt;
   detailsLink.Visible=DetailsWorthShowing(other);
   FitServiceViewport();UpdateSummary();UpdateServiceStatus();
   float sc=LayoutScale;int bodyWidth=Math.Min(W,(int)Math.Round(pages[TabBridge].ClientSize.Width/sc));int scrollY=pages[TabBridge].AutoScrollPosition.Y;
   // The brand CTA and secondary action share one stable content width.
   Rectangle Main(PillButton b){int width=Math.Min(bodyWidth-28,Math.Max(MainMinW,(int)Math.Ceiling(b.PreferredWidth()/sc)));return new Rectangle((int)Math.Round((bodyWidth-width)*sc/2),(int)Math.Round(MainY*sc)+scrollY,(int)Math.Round(width*sc),(int)Math.Round(MainH*sc));}
   bridge.Text=recoveryNeeded?L.T("Восстановить"):needsRestore?L.T("Отключить мост"):L.T("Подключить мост");bridge.AccessibleName=bridge.Text;bridge.Font=other?smallFont:bigFont;bridge.Glyph=recoveryNeeded?"\uE777":"\uE7E8";
   if(!needsRestore&&!other&&statusDetail!=null&&!busy&&!Checking)bridge.Text=L.T("Повторить");
   altBtn.Visible=showAlt;altBtn.Enabled=!busy&&known;if(showAlt)altBtn.Text=L.T("Подключить через ")+offer.ProfileId;
   bridge.Visible=!other;bridge.Bounds=Main(bridge);fixBtn.Bounds=Main(fixBtn);altBtn.Bounds=Main(altBtn);
   // Green connects, red disconnects. Connected and healthy: the red "Отключить мост" stays at hand but quiet (tinted, not a red slab).
   // Recovery keeps the solid colour: that one needs doing. A busy or unknown state draws the neutral disabled fill.
   bridge.Enabled=!busy&&known;bridge.SetFill(BridgeFillFor(other,recoveryNeeded,needsRestore));bridge.Subtle=!other&&needsRestore&&!recoveryNeeded;
   fixBtn.Visible=showFix;fixBtn.Enabled=!busy&&known;if(trayFix!=null)trayFix.Visible=showFix;
   profileSelect.Enabled=!busy&&!recoveryNeeded;diagBtn.Enabled=!busy;doctorBtn.Enabled=!busy;tgAutostart.Enabled=!busy&&!elevatedBusy;tgElevated.Enabled=!busy&&!elevatedBusy;tgQuiet.Enabled=!busy&&tgAutostart.Checked;browserChange.Enabled=!busy;scopeSel.Busy=scopeApplying;scopeSel.Enabled=scopeApplying||(!busy&&!(needsRestore&&!isActive));langSel.Enabled=!busy;
   saveCustom.Enabled=!busy&&!needsRestore;delCustom.Enabled=!busy&&!needsRestore;findIp.Enabled=!busy&&!needsRestore;revokeTrust.Enabled=!busy;
   FitServiceTitle();subLabel.Invalidate();bigLogo.Active=ServiceReady;bigLogo.Spinning=busy||(refreshing&&checking);
   statusLabel.ForeColor=ServiceReady?Theme.Green:showAlt?Theme.AccentHover:(recoveryNeeded||(statusDetail!=null&&!busy))?Theme.Amber:Theme.Text;
   if(evidenceUncertain)statusLabel.ForeColor=Theme.Amber;
   if(tray!=null){var ti=TrayIconFor(isActive,TrayColor.From(isActive,Checking||evidenceUncertain||!ServiceReady,busy,recoveryNeeded,showFix));if(tray.Icon!=ti)tray.Icon=ti;tray.Text=evidenceUncertain?L.T("Antigravity-Gemini DNS — требуется проверка"):isActive&&!ServiceReady?L.T("Antigravity-Gemini DNS — мост подключён; приложение не проверено"):isActive?L.T("Antigravity-Gemini DNS — мост подключён")+(speed!=null&&speed.Ms>=0?L.T(", пинг ")+speed.Ms+L.T(" мс"):""):recoveryNeeded?L.T("Antigravity-Gemini DNS — требуется восстановление"):L.T("Antigravity-Gemini DNS — мост отключён");if(trayToggle!=null){trayToggle.Text=recoveryNeeded?L.T("Восстановить"):needsRestore?L.T("Отключить мост"):L.T("Подключить мост");trayToggle.Enabled=!busy&&known;trayStatus.Text=L.T("Мост: ")+(showFix?L.T("требует починки"):evidenceUncertain?L.T("проверка неполна"):Checking?L.T("проверяю…"):isActive?L.T("подключён"):recoveryNeeded?L.T("требуется восстановление"):needsRestore?L.T("требует внимания"):L.T("отключён"));}}
   var want=ServiceReady?iconOn:iconOff;if(Icon!=want)Icon=want;
   UpdateBenchButtons();
  }
  // Origin of the addresses the active bridge uses: "pinned" or "found now, DD.MM HH:MM".
  string AddressSourceText(JsonElement status,string template){
   if(!status.TryGetProperty("Servers",out var arr)||arr.ValueKind!=JsonValueKind.Array)return "";
   var active=arr.EnumerateArray().Select(x=>x.GetString()).Where(x=>x!=null).ToList();if(active.Count==0)return "";
   var last=(settings.LastAddrServers??"").Split(new[]{','},StringSplitOptions.RemoveEmptyEntries);
   if((settings.LastAddrSource=="found"||settings.LastAddrSource=="saved")&&settings.LastAddrTemplate==template&&active.All(a=>last.Contains(a))&&DateTime.TryParse(settings.LastAddrTime,null,System.Globalization.DateTimeStyles.RoundtripKind,out var when))return L.T("Адреса: ")+AddressResolver.SourceText(settings.LastAddrSource,when);
   var item=profiles.FirstOrDefault(p=>p.Template==template);
   if(item!=null&&item.Servers.Length>0&&active.All(a=>item.Servers.Contains(a)))return L.T("Адреса: закреплённые");
   return L.T("Адреса: найдены ранее");
  }
  // A failed operation: one plain sentence and one action from the code table; the log keeps the detail but is not needed for routine failures.
  void ShowFailure(string code,string instruction=null,bool? gemini=null){
   var info=CodeTable.Lookup(code);
   lastCode=string.IsNullOrEmpty(info.Code)?code:info.Code;lastInstruction=instruction;
   bool gem=gemini??(Program.Gemini||Program.Google||!Program.Core);
   string action=CodeTable.ActionFor(info,gem);
   Detail(()=>{var i=CodeTable.Lookup(code);return i.Text+"\n"+CodeTable.ActionFor(i,gem);});
   Title(()=>BridgeTitle);
   // A profile switch where neither the new nor the previous profile could be applied leaves a clean (Restored) journal: nothing to restore, so do not claim it.
   if(info.Code=="RECOVERY_REQUIRED"&&!recoveryNeeded){Detail(()=>L.T(CodeTable.ProfileNotAppliedText)+"\n"+L.T(CodeTable.ProfileNotAppliedAction));}
   subLabel.Text=CompactStatusDetail;
   detailsLink.LinkColor=info.Kind==ActionKind.Details||instruction!=null?Theme.Amber:Theme.AccentHover;
   Log(info.Title+": "+info.Text+" "+action+" ["+lastCode+"]");
   if(!string.IsNullOrWhiteSpace(instruction)){Log("Что делать вручную:");LogBlock(instruction);ShowDetails(null);}
  }
  void ShowOperationFailure(string json,bool? gemini=null){
   try{using(var doc=JsonDocument.Parse(json)){var r=doc.RootElement;string code=r.TryGetProperty("Code",out var c)&&c.ValueKind==JsonValueKind.String?c.GetString():"";string ins=r.TryGetProperty("Instruction",out var i)&&i.ValueKind==JsonValueKind.String?i.GetString():null;ShowFailure(code,ins,gemini);}}
   catch{ShowFailure("ENGINE_ERROR");}
  }
  void SetBusy(bool value){busy=value;if(value){connectionOutcomeFn=null;opSerial++;checking=false;Title(()=>L.T("Выполняю…"));Detail(null);UpdateDetailsText();}UpdateButtons();}

  void Present(string output){
   last=output;bool wasActive=isActive;
   try{
    using(var doc=JsonDocument.Parse(output)){
     var root=doc.RootElement;var status=root.TryGetProperty("Status",out var st)?st:root;
     bool active=status.TryGetProperty("Active",out var a)&&a.ValueKind==JsonValueKind.True&&status.TryGetProperty("State",out var vs)&&vs.GetString()=="DNS active";
     bool hasPhase=status.TryGetProperty("JournalPhase",out var phase);known=hasPhase;
     string ph=hasPhase?phase.GetString():null;
     // The engine could not answer (WMI hangs, timeout) but the journal file can: a pending restore must keep "Восстановить" available and its manual steps reachable.
     if(!hasPhase){string journal=JournalReader();string fp=Recovery.PhaseOfJournal(journal);if(fp!=null){hasPhase=true;known=true;ph=fp;if(fp!="Active"&&fp!="Restored")lastInstruction=Recovery.BuildInstruction(journal);}}
     if(hasPhase)needsRestore=ph!="Absent"&&ph!="Restored";
     // "Requires recovery": the journal is neither Active nor Restored (an interrupted apply or restore). One button: "Восстановить".
     recoveryNeeded=hasPhase&&(ph=="Applying"||ph=="Restoring"||ph=="Failed");
     if(hasPhase&&!needsRestore)health=null;   // switched off (or never on): there is nothing left to repair and the automation must not suggest otherwise
     string code=(status.TryGetProperty("Code",out var c)&&c.ValueKind==JsonValueKind.String?c.GetString():null)??(root.TryGetProperty("Code",out var rc)&&rc.ValueKind==JsonValueKind.String?rc.GetString():"");
     // A drift the health check found stays visible until a check (or the user's own operation) clears it; a routine status must not repaint it green.
     bool drift=health!=null&&health.Kind==HealthKind.Drift&&hasPhase&&needsRestore&&!recoveryNeeded;
     if(drift&&health.Code!=HealthMonitor.ProviderDown)active=false;
     isActive=active;if(active&&networkEvidenceUtc==default&&!evidenceUncertain)networkEvidenceUtc=DateTime.UtcNow;if(!active)checksPassed=false;else offer=null;   // a bridge that works needs no offer to leave it
     Detail(null);
     if(drift&&health.Code==HealthMonitor.ProviderDown){var hi=StatusInfo(health.Code);lastCode=hi.Code;}
     else if(active)Title(()=>L.T("Мост подключён"));
     else if(drift){var hi=StatusInfo(health.Code);lastCode=hi.Code;}
     else if(recoveryNeeded){var ri=StatusInfo("RECOVERY_REQUIRED");lastCode=ri.Code;}
     else if(hasPhase&&!needsRestore&&code!="NRPT_OR_HOSTS_CONFLICT")Title(()=>L.T("Мост отключён"));
     else{var ci=StatusInfo(code);lastCode=ci.Code;}
     string pName=status.TryGetProperty("ProfileName",out var pe)&&pe.ValueKind==JsonValueKind.String?pe.GetString():null;
     string pTmpl=status.TryGetProperty("Template",out var te)&&te.ValueKind==JsonValueKind.String?te.GetString():null;
     int hit=active||needsRestore?profiles.FindIndex(p=>p.SelectionId==settings.Profile&&pTmpl!=null&&p.Template==pTmpl):-1;
     if(hit<0&&(active||needsRestore)&&pTmpl!=null)hit=profiles.FindIndex(p=>p.Template==pTmpl);
     if(hit<0&&(active||needsRestore)&&pName!=null){var known0=ProfileCatalog.BuiltIn.Find(pName);hit=profiles.FindIndex(p=>p.Name==pName||(known0!=null&&p.Name==known0.Id));}
     if(hit<0&&(active||needsRestore)&&Uri.TryCreate(pTmpl??"",UriKind.Absolute,out var retiredUri))hit=AddRetiredItem(string.IsNullOrWhiteSpace(pName)?retiredUri.IdnHost:pName,pTmpl);
     else if(!active&&!needsRestore&&profiles.Any(p=>p.Retired))DropRetiredItems();
     if(hit>=0)profileSelect.SetSelectedSilently(hit);
     // The scope of a bridge that exists is the journal's, not the toggle's: a cancelled or failed change must not leave the toggle on the wrong side.
     if(needsRestore&&status.TryGetProperty("Targets",out var tgs)&&tgs.ValueKind==JsonValueKind.Array){var scope=tgs.EnumerateArray().Select(x=>x.GetString()).Where(x=>x!=null).ToList();var sc=ScopeOf(scope);if(scope.Count>0&&sc!=BridgeScope.None&&sc!=ScopeNow)SetScope(sc);}
     addrSource=active?AddressSourceText(status,pTmpl):"";
     activeTemplate=active?pTmpl:null;activeServers=active&&status.TryGetProperty("Servers",out var asv)&&asv.ValueKind==JsonValueKind.Array?asv.EnumerateArray().Select(x=>x.GetString()).Where(x=>!string.IsNullOrEmpty(x)).ToArray():new string[0];
     if(!active)speed=null;
     Log("Статус: "+statusLabel.Text+(string.IsNullOrEmpty(code)?"":" ("+code+")"));
    }
   }catch{var ei=StatusInfo("ENGINE_ERROR");Log("Ответ служебного скрипта не удалось разобрать");}
   UpdateDetailsText();UpdateButtons();lastStatus=DateTime.UtcNow;
   if(wasActive!=isActive)Notify(isActive?L.T("Мост подключён"):L.T("Мост отключён"));
   if(isActive)_=MeasureSpeedAsync(false);
  }

  // ping of the working bridge (main page chip)
  // Read-only network work (one DoH query, three validated TLS handshakes), in the background, at most once per BridgeSpeed.Fresh unless the user clicks the chip.
  // Never in smoke renders or self-tests without a test source, never while an operation runs; a result from before an operation is dropped.
  async Task MeasureSpeedAsync(bool force){
   if(!isActive||busy||speedRunning||exiting||IsDisposed)return;
   if(smoke&&SpeedSource==null)return;
   string tmpl=activeTemplate;var servers=activeServers;
   if(string.IsNullOrEmpty(tmpl)||servers.Length==0)return;
   string host=Program.Target,key=tmpl+"|"+string.Join(",",servers)+"|"+host;
   bool fresh=speed!=null&&speedKey==key&&DateTime.UtcNow-speed.Utc<BridgeSpeed.Fresh;
   if(fresh&&!force)return;
   bool announce=force||speedKey!=key;
   speedRunning=true;if(announce)speed=null;speedKey=key;int serial=opSerial;UpdateSummary();
   SpeedResult r=null;
   try{
    r=SpeedSource!=null?await SpeedSource(tmpl,servers,lifeCts.Token)
     :await Task.Run(async()=>{using(var live=new BridgeSpeed.Live())return await BridgeSpeed.MeasureAsync(live,tmpl,servers,host,lifeCts.Token);});
   }catch(OperationCanceledException){}
   catch(Exception e){Log("Пинг не измерен: "+e.Message);}
   finally{speedRunning=false;}
   if(IsDisposed)return;
   if(r!=null&&isActive&&serial==opSerial&&speedKey==key){
    speed=r;
    if(announce||r.Ms<0)Log(r.Ms>=0?"Пинг до сервера-посредника провайдера: "+r.Ms+" мс ("+r.Proxy+")":"Пинг не измерен: "+r.Error+(r.Proxy==null?"":" ("+r.Proxy+")"));
   }
   UpdateButtons();
  }
  internal Task MeasureSpeedForTest(bool force)=>MeasureSpeedAsync(force);
  // Where the addresses of a profile to measure come from: its pins, else what an earlier search found. No address = no number (nothing is searched for here).
  string[] ServersFor(ProfileItem p){
   if(p.Servers!=null&&p.Servers.Length>0)return p.Servers;
   return SavedAddresses.Usable(SavedAddresses.Find(settings.SavedAddrs,p.Template),DateTime.UtcNow);
  }
  // Opening the list measures the profiles whose number is missing or older than BridgeSpeed.Fresh (the working bridge's own profile has its own measurement).
  // Read-only, in the background, three at a time (a silent server is joined by its provider's next one after half a second, so a dead provider frees its place sooner), never while an operation runs; the list stays usable and fills in as the answers come.
  async Task MeasureProfilesAsync(bool force){
   if(busy||benchRunning||pingsRunning||exiting||IsDisposed)return;
   if(smoke&&SpeedSource==null)return;
   var jobs=profiles.Where(p=>!(isActive&&string.Equals(p.Template,activeTemplate,StringComparison.OrdinalIgnoreCase))).Select(p=>(Template:p.Template,Servers:ServersFor(p))).Where(j=>j.Servers.Length>0).ToList();
   lock(pings)jobs=jobs.Where(j=>force||!pings.TryGetValue(j.Template,out var old)||DateTime.UtcNow-old.Utc>=BridgeSpeed.Fresh).ToList();
   if(jobs.Count==0)return;
   pingsRunning=true;PerfLog.Mark("pings begin ("+jobs.Count+")");string host=Program.Target;long revision=Volatile.Read(ref evidenceSerial);var gate=new SemaphoreSlim(3);
   lock(pings)foreach(var j in jobs)pingPending.Add(j.Template);
   UpdateSummary();
   try{
    await Task.WhenAll(jobs.Select(async j=>{
     await gate.WaitAsync(lifeCts.Token);
     try{
      if(revision!=Volatile.Read(ref evidenceSerial)||host!=Program.Target)return;
      SpeedResult r;
      try{r=SpeedSource!=null?await SpeedSource(j.Template,j.Servers,lifeCts.Token):await Task.Run(async()=>{using(var live=new BridgeSpeed.Live())return await BridgeSpeed.MeasureAsync(live,j.Template,j.Servers,host,lifeCts.Token);});}
      catch(OperationCanceledException){return;}
      catch{return;}
      if(IsDisposed||revision!=Volatile.Read(ref evidenceSerial)||host!=Program.Target)return;
      lock(pings){pings[j.Template]=r;pingPending.Remove(j.Template);}
      try{if(IsHandleCreated&&!IsDisposed)BeginInvoke(new Action(()=>{if(!IsDisposed)UpdateSummary();}));}catch{}
     }finally{gate.Release();}
    }));
   }catch(Exception){}   // cancelled, or the window went away while a measurement ran
   finally{pingsRunning=false;PerfLog.Mark("pings end");gate.Dispose();lock(pings)pingPending.Clear();if(!IsDisposed)UpdateSummary();}
  }
  internal Task MeasureProfilesForTest(bool force)=>MeasureProfilesAsync(force);
  internal SpeedResult CurrentSpeed=>speed;

  // "Network checks passed" = the engine's own diagnosis found the policy effective and the Windows resolver answering with the provider's addresses.
  internal static bool DiagnosisPassed(string output){
   try{using(var doc=JsonDocument.Parse(output))return doc.RootElement.TryGetProperty("NrptEffective",out var n)&&n.ValueKind==JsonValueKind.String&&n.GetString().StartsWith("Policy + Windows resolver answers match");}catch{return false;}
  }
  internal (long Revision,string Context) CaptureDiagnosticContext()=>(Volatile.Read(ref evidenceSerial),HealthContext());
  internal void ApplyDiagnosticEvidence(string output,(long Revision,string Context) captured){
   if(captured.Context==null||captured.Revision!=Volatile.Read(ref evidenceSerial)||captured.Context!=HealthContext()){InvalidateNetworkEvidence();return;}
   checksPassed=DiagnosisPassed(output);networkEvidenceUtc=DateTime.UtcNow;evidenceUncertain=!checksPassed;
  }
  async Task Run(string action){
   currentCts?.Cancel();currentCts?.Dispose();currentCts=new CancellationTokenSource();var token=currentCts.Token;SetBusy(true);
   try{
    var p=Current;var captured=CaptureDiagnosticContext();var result=await Program.Engine(action,false,false,p.Servers,p.Template,p.Custom?null:p.Name,token);
    if(action=="diagnose"){Log("Диагностика:");LogBlock(result.Output);ApplyDiagnosticEvidence(result.Output,captured);lastDiagnosis=result;history.Append("diagnose",CodeOfOutput(result.Output),result.Code==0,p.Custom?"custom":p.Name);}
    Present(result.Output);
   }catch(OperationCanceledException){Title(()=>L.T("Операция отменена"));}
   finally{SetBusy(false);}
  }
  internal Func<string,bool,Task> MutateHook;   // test seam: stands in for the whole operation (nothing reaches the engine or UAC)
  // The elevated engine ends with the status after its own work (enable: the status itself; restore: a Status field), so a successful operation needs no
  // second engine start (~1.4 s). Anything unexpected = null, and the window asks the engine as before.
  internal static string StatusFromOperation(string action,int code,string output){
   if(code!=0||string.IsNullOrWhiteSpace(output))return null;
   try{
    using(var doc=JsonDocument.Parse(output)){
     var r=doc.RootElement;
     if(action=="restore")r=r.TryGetProperty("Status",out var st)?st:default;
     if(r.ValueKind!=JsonValueKind.Object||!r.TryGetProperty("State",out var s)||s.ValueKind!=JsonValueKind.String||!r.TryGetProperty("JournalPhase",out _))return null;
     return r.GetRawText();
    }
   }catch{return null;}
  }

  // start-up and background status
  // The journal (a plain file, readable without elevation) says at once what the program has configured; the engine's status confirms it in the background.
  // Until it does, a configured bridge is shown as "checking" in neutral colours: never green before the engine has confirmed it.
  readonly CancellationTokenSource lifeCts=new CancellationTokenSource();
  bool checking,refreshing;int opSerial;
  bool Checking=>checking&&needsRestore&&!recoveryNeeded&&!busy;
  void ShowJournalHint(){
   string journal=JournalReader();string ph=Recovery.PhaseOfJournal(journal);
   known=true;checking=true;Detail(null);isActive=false;checksPassed=false;addrSource="";
   if(ph==null||ph=="Restored"){needsRestore=false;recoveryNeeded=false;Title(()=>L.T("Мост отключён"));}
   else if(ph=="Active"){needsRestore=true;recoveryNeeded=false;Title(()=>L.T("Мост подключён"));}
   else{needsRestore=true;recoveryNeeded=true;var ri=StatusInfo("RECOVERY_REQUIRED");lastCode=ri.Code;lastInstruction=Recovery.BuildInstruction(journal);}
   UpdateDetailsText();UpdateButtons();
  }
  internal void ShowJournalHintForTest()=>ShowJournalHint();
  // Crash Guard: a previous session that died in the middle of a DNS change is said plainly; the journal's own «Восстановить» path is already on screen.
  void ShowCrashNotice(){
   var c=CrashGuard.Current;if(c==null||c.Kind==CrashGuard.Kind.None)return;
   if(c.Kind==CrashGuard.Kind.InterruptedChange){
    Log("Прошлый сеанс завершился во время изменения DNS (фаза журнала: "+c.JournalPhase+"). Нужно «Восстановить».");
    Detail(()=>L.T("Прошлый запуск прервался во время изменения DNS. Нажмите «Восстановить»."));
   }else Log("Прошлый сеанс завершился некорректно. По журналу мост активен; состояние будет проверено.");
  }
  internal void ShowCrashNoticeForTest()=>ShowCrashNotice();
  internal bool CheckingShown=>Checking;
  internal Func<CancellationToken,Task<(int Code,string Output)>> StatusSource;   // test seam: the engine status of RefreshStatus
  internal Task RefreshForTest()=>RefreshStatus();
  internal bool LogoSpinning=>bigLogo.Spinning;
  internal bool KeyForTest(Keys k){var m=new Message();return ProcessCmdKey(ref m,k);}
  internal int SelectedTabIndex=>nav.Selected;
  internal bool DetailsVisible=>detailsPanel.Visible;
  // A status read that does not lock the window. A user operation started meanwhile wins: its result is newer, so this one is dropped.
  async Task RefreshStatus(){
   if(busy||refreshing||exiting||IsDisposed)return;
   refreshing=true;int serial=opSerial;UpdateButtons();PerfLog.Mark("status begin");
   try{
    var p=Current;
    var r=StatusSource!=null?await StatusSource(lifeCts.Token):await Program.Engine("status",false,false,p.Servers,p.Template,p.Custom?null:p.Name,lifeCts.Token);
    if(!busy&&serial==opSerial&&!IsDisposed){checking=false;Present(r.Output);}
   }catch(OperationCanceledException){}
   catch(Exception e){Log("Проверка состояния не удалась: "+e.Message);}
   finally{refreshing=false;PerfLog.Mark("status end");if(!IsDisposed){if(!busy)checking=false;UpdateDetailsText();UpdateButtons();}}
  }

  // comfort
  protected override bool ProcessCmdKey(ref Message msg,Keys keyData){
   if(keyData==Keys.Escape&&diagnosticPanel!=null&&diagnosticPanel.Visible){doctorCts?.Cancel();diagnosticPanel.Visible=false;return true;}
   if(keyData==Keys.Escape&&browserPanel!=null&&browserPanel.Visible){CloseBrowserPanel(false);return true;}
   if(keyData==Keys.Escape&&customPanel!=null&&customPanel.Visible){customPanel.Visible=false;return true;}
   if(keyData==Keys.Escape&&detailsPanel!=null&&detailsPanel.Visible){detailsPanel.Visible=false;return true;}
   if(keyData==Keys.Escape&&doctorRunning){doctorCts?.Cancel();return true;}
   if(keyData==Keys.Escape&&busy){reportCts?.Cancel();currentCts?.Cancel();return true;}
   if(keyData==Keys.F6){var _=DoctorAsync();return true;}
   if(keyData==Keys.F5&&!busy){var _=RefreshStatus();return true;}
   if((keyData&Keys.Control)==Keys.Control){int n=(int)(keyData&Keys.KeyCode)-(int)Keys.D1;if(n>=0&&n<NavBar.Count&&(keyData&~Keys.KeyCode)==Keys.Control){nav.Selected=n;return true;}}
   return base.ProcessCmdKey(ref msg,keyData);
  }
  void SaveWindowPosition(){
   if(smoke||WindowState!=FormWindowState.Normal||!Visible)return;
   if(settings.WinX!=Left||settings.WinY!=Top){settings.WinX=Left;settings.WinY=Top;SaveSettings();}
  }
  // A saved position is used only if both ends of the title bar are on a screen that exists now (monitors change between runs).
  internal static bool TitleBarOnScreen(Rectangle r,Rectangle[] screens)=>screens.Any(s=>s.Contains(new Point(r.Left+40,r.Top+10))&&s.Contains(new Point(r.Right-40,r.Top+10)));
  void RestoreWindowPosition(){
   if(settings.WinX==int.MinValue)return;
   var r=new Rectangle(settings.WinX,settings.WinY,Width,Height);
   if(!TitleBarOnScreen(r,Screen.AllScreens.Select(s=>s.WorkingArea).ToArray()))return;
   StartPosition=FormStartPosition.Manual;Location=r.Location;
  }

  static string CodeOfOutput(string json){
   try{using(var doc=JsonDocument.Parse(json)){var r=doc.RootElement;if(r.TryGetProperty("Code",out var c)&&c.ValueKind==JsonValueKind.String)return c.GetString();if(r.TryGetProperty("Status",out var st)&&st.TryGetProperty("Code",out var c2)&&c2.ValueKind==JsonValueKind.String)return c2.GetString();}}catch{}
   return "";
  }

  // ---- 1.5-1: health check in the tray ----
  // Read-only and unelevated: journal file, the engine's `status` with a short deadline (the only CIM access), DoH and Windows-resolver reads. Never a benchmark,
  // never an automatic repair, and a bridge the user switched off is "Off" (nothing to check, nothing to restore).
  void InitHealth(){
   var options=healthOptions=new HealthOptions{Targets=Program.Hosts};
   healthSched=new HealthScheduler(()=>DateTime.UtcNow,options);healthMon=new HealthMonitor(new HealthMonitor.Live(),options);healthCts=new CancellationTokenSource();
   healthTimer=new System.Windows.Forms.Timer{Interval=5000};healthTimer.Tick+=async(_,__)=>await HealthTick();healthTimer.Start();
   seenNetwork=NetworkRevisionForEvidence();
   try{System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged+=OnNetworkChanged;Microsoft.Win32.SystemEvents.PowerModeChanged+=OnPowerChanged;}catch{}
   healthSched.Trigger(HealthTrigger.Startup);
  }
  void StopHealth(){
   try{System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged-=OnNetworkChanged;Microsoft.Win32.SystemEvents.PowerModeChanged-=OnPowerChanged;}catch{}
   try{healthTimer?.Stop();healthTimer?.Dispose();healthCts?.Cancel();healthCts?.Dispose();}catch(ObjectDisposedException){}
   healthTimer=null;healthCts=null;
  }
  internal void InvalidateNetworkEvidence(){
   Interlocked.Increment(ref evidenceSerial);opSerial++;
   checksPassed=false;networkEvidenceUtc=default;speed=null;evidenceUncertain=needsRestore;lastBenchStale=true;
   lock(pings){pings.Clear();pingPending.Clear();}
   UpdateDetailsText();UpdateButtons();
  }
  void NetworkEvidenceChanged(HealthTrigger trigger){
   Interlocked.Increment(ref evidenceSerial);
   try{if(IsHandleCreated&&!IsDisposed)BeginInvoke(new Action(()=>{if(IsDisposed)return;InvalidateNetworkEvidence();healthSched?.Trigger(trigger);}));}catch(InvalidOperationException){}
  }
  // Only a change of the route-relevant network revision invalidates evidence; the health tick re-reads it in case an event came too early.
  string seenNetwork;
  void OnNetworkChanged(object s,EventArgs e)=>NetworkMaybeChanged();
  internal bool NetworkMaybeChanged(){string now=NetworkRevisionForEvidence();string was=Interlocked.Exchange(ref seenNetwork,now);if(now!=null&&now==was)return false;NetworkEvidenceChanged(HealthTrigger.NetworkChange);return true;}
  void OnPowerChanged(object s,Microsoft.Win32.PowerModeChangedEventArgs e){if(e.Mode==Microsoft.Win32.PowerModes.Resume)NetworkEvidenceChanged(HealthTrigger.Resume);}
  string NetworkRevisionForEvidence()=>smoke?"synthetic-network":EvidenceContext.NetworkRevision();
  string BenchmarkContext(){var network=NetworkRevisionForEvidence();return network==null?null:network+"|"+string.Join("|",Program.Targets);}
  string HealthContext(){var network=NetworkRevisionForEvidence();return network==null?null:EvidenceContext.Hash(network+"|"+Current.Template+"|"+string.Join("|",Program.Targets)+"|"+JournalReader()+"|"+CatalogEvidenceRevision());}
  internal bool EvidenceUncertain=>evidenceUncertain;
  async Task HealthTick(){
   if(healthSched==null||healthRunning||busy||benchRunning||exiting)return;
   if(seenNetwork!=null)NetworkMaybeChanged();
   if(needsRestore&&!evidenceUncertain&&networkEvidenceUtc!=default&&DateTime.UtcNow-networkEvidenceUtc>=Doctor.Freshness){InvalidateNetworkEvidence();healthSched.Trigger(HealthTrigger.Manual);}
   if(!healthSched.TryStart(out var trigger))return;
   int serial=opSerial;long revision=Volatile.Read(ref evidenceSerial);string context=HealthContext();
   healthRunning=true;HealthReport report=null;var token=healthCts.Token;
   try{report=await Task.Run(()=>healthMon.CheckAsync(trigger,token));}
   catch(OperationCanceledException){healthSched.Finished(HealthKind.Unknown);healthRunning=false;return;}
   catch(Exception e){if(!IsDisposed)InvalidateNetworkEvidence();Log("Проверка моста не удалась: "+e.Message);healthSched.Finished(HealthKind.Unknown);healthRunning=false;return;}
   healthRunning=false;
   healthSched.Finished(report.Kind,report.Kind==HealthKind.Suspect);
   if(IsDisposed)return;
   // An operation started during the check: this report describes the state before it, so it is not shown. The check runs again once the operation has settled.
   if(busy){healthSched.Trigger(HealthTrigger.Manual);return;}
   if(context==null||serial!=opSerial||revision!=Volatile.Read(ref evidenceSerial)||context!=HealthContext()){InvalidateNetworkEvidence();healthSched.Trigger(HealthTrigger.Manual);return;}
   ApplyHealth(report);
  }
  // v1.3 failover (opt-in): a confirmed provider failure starts the search for a working profile. The result is an offer behind the button and a tray note;
  // the switch itself still needs the user's trust decision and UAC. Every offer and every refusal goes to the operation history with its reason.
  async Task FailoverOfferAsync(){
   if(assisted!=null&&(assisted.ManualOff||assisted.RecoveryPending))return;
   var now=DateTime.UtcNow;failover.Failed(Current.Name,now);
   var refused=failover.Refusal(null,now);
   if(refused!=null){history.Append("failover-refused",refused,false,Current.Name);return;}
   await OfferAlternativeAsync(Current,"PROVIDER_DOWN",CancellationToken.None);
   var o=offer;if(o==null)return;
   var refusedOffer=failover.Refusal(o.ProfileId,DateTime.UtcNow);
   if(refusedOffer!=null){offer=null;history.Append("failover-refused",refusedOffer,false,o.ProfileId);UpdateButtons();return;}
   failover.Offered(DateTime.UtcNow);
   history.Append("failover-offer","PROVIDER_DOWN",true,o.ProfileId);
   Notify(L.F("Провайдер {0} не отвечает. Переключить на {1}?",Current.Name,o.ProfileId));
  }
  internal void ApplyHealth(HealthReport r){
   if(assisted!=null&&(assisted.RecoveryPending||assisted.ManualOff&&r.Kind!=HealthKind.Off)){offer=null;InvalidateNetworkEvidence();return;}
   if(r.Kind==HealthKind.Healthy&&(r.Utc==default||r.Utc>DateTime.UtcNow||DateTime.UtcNow-r.Utc>=Doctor.Freshness)){InvalidateNetworkEvidence();return;}
   if(FailoverGuard.Triggered(settings.AutoFailover,r)&&!busy&&known&&!recoveryNeeded)_=FailoverOfferAsync();
   // An offer to switch provider belongs to a provider failure. A healthy report (the provider answers again) or a drift that is not about the provider (the rules)
   // makes it stale: it must neither linger nor hide "Починить". A provider-down drift keeps it (the repair has already failed once).
   if(offer!=null&&(r.Kind==HealthKind.Healthy||(r.Kind==HealthKind.Drift&&r.Code!=HealthMonitor.ProviderDown))){offer=null;UpdateButtons();}
   // The light provider check knows only about the provider. A healthy light report clears an earlier provider-down drift and nothing else;
   // a failing one never replaces a different drift the full check has shown (the repair covers both).
   if(r.Light&&r.Kind==HealthKind.Healthy){
    lastCheck=r;providerEvidenceRevision=Volatile.Read(ref evidenceSerial);providerEvidenceIdentity=ProviderHealthIdentity();
    if(health!=null&&health.Code==HealthMonitor.ProviderDown){health=null;lastNotifiedCode=null;if(!string.IsNullOrEmpty(last))Present(last);else{UpdateDetailsText();UpdateButtons();}}
    else UpdateSummary();
    return;
   }
   if(r.Light&&r.Kind==HealthKind.Drift&&health!=null&&health.Code!=r.Code)return;
   lastCheck=r;providerEvidenceRevision=Volatile.Read(ref evidenceSerial);providerEvidenceIdentity=ProviderHealthIdentity();
   if(r.Kind==HealthKind.Unknown){InvalidateNetworkEvidence();Log("Проверка моста не удалась ("+r.Detail+"). Мост не менялся.");return;}
   if(r.Kind==HealthKind.Suspect){InvalidateNetworkEvidence();Log("Проверка моста: "+r.Code+", подтверждаю повторной проверкой.");return;}
   if(r.Kind==HealthKind.Off||r.Kind==HealthKind.Healthy){
    evidenceUncertain=false;networkEvidenceUtc=r.Utc;UpdateButtons();
    if(health!=null){health=null;lastNotifiedCode=null;if(r.Kind==HealthKind.Healthy&&!string.IsNullOrEmpty(last))Present(last);else{UpdateDetailsText();UpdateButtons();}}
    return;
   }
   // Drift
   health=r;var info=CodeTable.Lookup(r.Code);
   if(!recoveryNeeded&&r.Code!="RECOVERY_REQUIRED"){if(r.Code!=HealthMonitor.ProviderDown)isActive=false;checksPassed=false;StatusInfo(r.Code);subLabel.Text=CompactStatusDetail;lastCode=info.Code;}
   UpdateButtons();
   if(r.CanFix)_=AutoRepairAsync(r);
   if(lastNotifiedCode!=r.Code){
    lastNotifiedCode=r.Code;
    Log("Проверка моста: "+info.Title+". "+info.Text+" "+info.Action+" ["+r.Code+(string.IsNullOrEmpty(r.Detail)?"":": "+r.Detail)+"]");
    history.Append("health",r.Code,false,Current.Custom?"custom":Current.Name);
    Notify(info.Title+". "+info.Action);
   }
  }
  internal HealthReport CurrentHealth=>health;
  internal bool FixVisible=>fixBtn.Visible;
    internal bool AltVisible=>altBtn.Visible;
  internal static Color BridgeFillFor(bool other,bool recoveryNeeded,bool needsRestore)=>other?Theme.Soft:recoveryNeeded?Theme.Danger:needsRestore?Theme.Red:Theme.GreenSolid;
  internal Color BridgeFill=>bridge.Fill;
  // Layout checks of the main page: one main (coloured) button, it sits in the top slot, and no two visible buttons overlap.
  PillButton[] MainButtons=>new[]{bridge,fixBtn,altBtn};
  static bool Primary(PillButton b)=>b.Fill==Theme.Accent||b.Fill==Theme.Danger||b.Fill==Theme.GreenSolid||b.Fill==Theme.Red||b.Fill==Theme.AmberSolid;
  internal Color FixFill=>fixBtn.Fill;
  internal int PrimaryButtons=>MainButtons.Count(b=>b.Visible&&Primary(b));
  internal bool PrimaryButtonOnTop{get{var v=MainButtons.Where(b=>b.Visible).ToList();var p=v.Where(Primary).ToList();return p.Count==1&&v.All(b=>b==p[0]||b.Top>p[0].Top);}}
  internal bool MainButtonsOverlap{get{var v=MainButtons.Where(b=>b.Visible).ToList();for(int i=0;i<v.Count;i++)for(int j=i+1;j<v.Count;j++)if(v[i].Bounds.IntersectsWith(v[j].Bounds))return true;return false;}}
  internal bool OfferActive=>offer!=null;
  internal void SetOfferForTest(string profileId){offer=new SwitchOffer{ProfileId=profileId,FromId=Current.Name,Reason="BOOTSTRAP_NO_ADDRESS"};UpdateButtons();}
  internal string AltText=>altBtn.Text;
  // Smoke only: shows a drift on the main page without touching the system.
  internal void SmokeHealth(HealthReport r){known=true;needsRestore=true;isActive=false;recoveryNeeded=false;checksPassed=false;addrSource="";ApplyHealth(r);Application.DoEvents();}

  // ---- 1.5-2: support report (local file, preview first, nothing is sent) ----
  bool reportRunning;CancellationTokenSource reportCts;
  internal Func<CancellationToken,Task<(int Code,string Output)>> ReportDiagnosisSource;
  internal Func<CancellationToken,Task<RedactionContext>> ReportRedactionSource;
  internal Action<string> ReportPreviewSource;
  internal TimeSpan ReportDeadline=TimeSpan.FromSeconds(60);
  internal Task SupportReportForTest()=>SupportReportAsync();
  internal bool ReportRunningForTest=>reportRunning;
  async Task SupportReportAsync(){
   if(busy||reportRunning||doctorRunning||IsDisposed)return;
   reportRunning=true;
   try{
    SetBusy(true);
    var captured=CaptureDiagnosticContext();var selected=Current;var input=CollectReportInput();bool english=L.En;
    using var cts=CancellationTokenSource.CreateLinkedTokenSource(lifeCts.Token);
    reportCts=cts;cts.CancelAfter(ReportDeadline);
    var diagnosis=await (ReportDiagnosisSource!=null?ReportDiagnosisSource(cts.Token):Program.Engine("diagnose",false,false,selected.Servers,selected.Template,selected.Custom?null:selected.Name,cts.Token)).WaitAsync(cts.Token);
    var doctor=await (DoctorSource!=null?DoctorSource(cts.Token):Doctor.RunAsync(new LiveDoctorProbe(),cts.Token)).WaitAsync(cts.Token);
    var ctx=await (ReportRedactionSource!=null?ReportRedactionSource(cts.Token):Task.Run(()=>RedactionContext.Live(),cts.Token)).WaitAsync(cts.Token);
    cts.Token.ThrowIfCancellationRequested();
    if(IsDisposed||captured.Context==null||captured.Revision!=Volatile.Read(ref evidenceSerial)||captured.Context!=HealthContext()||selected.SelectionId!=Current.SelectionId||english!=L.En){if(!IsDisposed)Log(L.En?"Support report discarded: context changed; retry.":"Отчёт отменён: условия изменились; повторите подготовку.");return;}
    input.DiagnosisJson=diagnosis.Code==0?diagnosis.Output:null;input.DiagnosisCode=diagnosis.Code==0?null:CodeOfOutput(diagnosis.Output??"");input.DoctorText=doctor.ToText();
    string text=SupportReport.Prepare(input,ctx);
    if(!IsDisposed){if(ReportPreviewSource!=null)ReportPreviewSource(text);else ShowReportPreview(text);}
   }catch(OperationCanceledException){if(!IsDisposed)Log(L.En?"Support report cancelled or timed out.":"Подготовка отчёта отменена или превысила время ожидания.");}
   catch(Exception e){if(!IsDisposed){Log("Support report: "+e.GetType().Name);if(!smoke)MessageBox.Show(this,L.En?"Could not prepare the report. See Logs and retry.":"Не удалось подготовить отчёт. См. Логи и повторите.",Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);}}
   finally{reportCts=null;reportRunning=false;if(!IsDisposed)SetBusy(false);}
  }
  // Ordinary preferences remain active for this session on write failure. A persistent
  // local status makes that distinction visible; custom edit/delete roll back instead.
  bool SaveSettings(bool retainForSession=true){
   bool ok=settings.Save();if(!ok&&!IsDisposed){string message=L.En?"Settings were not saved. Check folder access and retry.":"Настройки не сохранены. Проверьте доступ к папке и повторите.";
    if(retainForSession)message+=L.En?" Changes apply to this session only.":" Изменения действуют только в этом сеансе.";
    Log(message+" ("+settings.LastSaveError+")");
    if(!smoke)MessageBox.Show(this,message,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);
   }
   return ok;
  }
  // Doctor: read-only checks, one line per layer, in its own window. Nothing here changes the system, so there is no UAC and it runs with the bridge on or off.
  internal ReportInput CollectReportInput(){
   string journal=JournalReader();string phase=Recovery.PhaseOfJournal(journal);
   return new ReportInput{GeneratedUtc=DateTime.UtcNow,ExeVersion=AppInfo.Version,CatalogVersion=ProfileCatalog.BuiltIn.Version,CatalogUpdated=ProfileCatalog.BuiltIn.Updated,
    Os=Environment.OSVersion.VersionString+(Environment.Is64BitOperatingSystem?" x64":" x86"),JournalPhase=phase??L.T("нет"),Profile=Current.Custom?L.T("свой профиль"):Current.Name,
    HealthKind=(health??lastCheck)==null?L.T("не выполнялась"):(health??lastCheck).Kind.ToString(),HealthCode=(health??lastCheck)?.Code,HealthUtc=(health??lastCheck)?.Utc.ToString("yyyy-MM-dd HH:mm"),Operations=history.Load(),
    CustomProfile=Current.Custom,DiagnosisJson=lastDiagnosis.Code==0?lastDiagnosis.Output:null,DiagnosisCode=lastDiagnosis.Code==0?null:CodeOfOutput(lastDiagnosis.Output??"")};
  }
  void ShowReportPreview(string text){
   using(var dlg=new ReportPreviewForm(text)){
    if(dlg.ShowDialog(this)==DialogResult.OK){
     using(var sfd=new SaveFileDialog{Title=L.T("Сохранить отчёт для поддержки"),Filter=L.T("Текстовый файл (*.txt)|*.txt"),FileName="AntigravityGeminiDns-report-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".txt",OverwritePrompt=true}){
      if(sfd.ShowDialog(this)==DialogResult.OK){
       try{File.WriteAllText(sfd.FileName,text,new System.Text.UTF8Encoding(true));Log(CodeTable.Lookup("REPORT_SAVED").Title+": "+sfd.FileName);history.Append("report","REPORT_SAVED",true,"");MessageBox.Show(this,CodeTable.Lookup("REPORT_SAVED").Text+"\n"+CodeTable.Lookup("REPORT_SAVED").Action,Text,MessageBoxButtons.OK,MessageBoxIcon.Information);}
       catch(Exception e){Log("Отчёт не сохранён: "+e.Message);MessageBox.Show(this,CodeTable.Lookup("REPORT_SAVE_FAILED").Text+"\n"+CodeTable.Lookup("REPORT_SAVE_FAILED").Action,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);}
      }
     }
    }
   }
  }
  // ---- another provider when the current one has none ----
  // Starts only after the user's own connect/repair failed with "no usable address". Read-only (DoH queries and TLS handshakes of the benchmark kind).
  // The result is an offer behind a button; the existing bridge is never touched here. Carrying it out = trust dialog + the ordinary enable (compensated profile switch).
  async Task OfferAlternativeAsync(ProfileItem failed,string reason,CancellationToken ct){
   if(assisted!=null&&(assisted.ManualOff||assisted.RecoveryPending))return;
   int capturedSerial=opSerial;
   offer=null;Title(()=>L.T("Ищу рабочего провайдера…"));
   Log("Профиль «"+failed.Name+"» не дал рабочих адресов ("+reason+"). Проверяю остальные профили каталога: только чтение, мост не меняется.");
   SwitchOutcome res;
   var preferred=ProviderPool.Usable(settings.Pool,DateTime.UtcNow,failed.Name);
   try{using(var live=new LiveTransport())res=await Task.Run(()=>ProviderSwitch.FindAsync(ProviderSwitch.Pool(ProfileCatalog.BuiltIn,settings.SavedAddrs,DateTime.UtcNow),failed.Name,live,new BenchOptions(),ct,preferred));}
   catch(OperationCanceledException){return;}
   catch(Exception e){Log("Другого провайдера найти не удалось: "+e.Message);return;}
   if(res.Kind==SwitchKind.Cancelled||IsDisposed||capturedSerial!=opSerial||assisted!=null&&(assisted.ManualOff||assisted.RecoveryPending))return;
   if(res.Kind==SwitchKind.Offer&&profiles.Any(x=>x.Name==res.Offer.ProfileId)){
    res.Offer.Reason=reason;offer=res.Offer;
    var info=CodeTable.Lookup("PROVIDER_SWITCH_OFFERED");lastCode=info.Code;
    SwitchOfferStatus(failed.Name,res.Offer.ProfileId);subLabel.Text=CompactStatusDetail;
    Log(info.Title+": «"+res.Offer.ProfileId+"» (проверено профилей: "+res.Checked+"). "+info.Action);
    history.Append("switch-offer",info.Code,true,res.Offer.ProfileId);
   }else{
    ShowFailure("NO_WORKING_PROVIDER");
    history.Append("switch-offer","NO_WORKING_PROVIDER",false,failed.Custom?"custom":failed.Name);
   }
   UpdateButtons();
  }
  async Task SwitchToOfferedAsync(){
   if(assisted!=null&&(assisted.ManualOff||assisted.RecoveryPending)){offer=null;UpdateButtons();return;}
   var o=offer;if(o==null||busy||!known)return;
   int i=profiles.FindIndex(x=>x.Name==o.ProfileId);if(i<0){offer=null;UpdateButtons();return;}
   await ProviderSwitch.ApplyAsync(o,id=>true,async id=>{
    profileSelect.SetSelectedSilently(i);settings.Profile=profiles[i].SelectionId;SaveSettings();offer=null;UpdateDetailsText();failover.Switched(DateTime.UtcNow);
    Log("Переключение на «"+id+"»: доверие подтверждено, дальше обычное подключение.");
    await Mutate("enable");
   });
  }

  // Smoke only: renders the preview dialog with sample text (no system access) so its layout can be inspected.
  internal Bitmap SmokePreview(string text,int dpi=0){
   using(var dlg=new ReportPreviewForm(text)){dlg.StartPosition=FormStartPosition.Manual;dlg.Location=new Point(-3000,-3000);dlg.Show();Application.DoEvents();if(dpi>0)UiSurfaceCheck.ChangeDpi(dlg,dpi);dlg.FitToWorkArea();Application.DoEvents();var bmp=new Bitmap(dlg.Width,dlg.Height);dlg.DrawToBitmap(bmp,new Rectangle(Point.Empty,bmp.Size));dlg.Close();return bmp;}
  }
  // the tray icon of a state: the base icon of the bridge (on or off) with its colour dot, composed once per state
  internal Icon TrayIconFor(bool active,TrayState state){
   var key=(active,state);
   if(!trayDots.TryGetValue(key,out var icon)){icon=TrayColor.Compose(active?iconOn:iconOff,state,SystemInformation.SmallIconSize.Width);trayDots[key]=icon;}
   return icon;
  }
  internal Bitmap SmokeDoctor(DoctorReport report,int dpi=0){
   ShowDiagnostic(report);Application.DoEvents();var bmp=new Bitmap(Width,Height);DrawToBitmap(bmp,new Rectangle(Point.Empty,bmp.Size));diagnosticPanel.DrawToBitmap(bmp,diagnosticPanel.Bounds);diagnosticPanel.Visible=false;return bmp;
  }
 }
}
