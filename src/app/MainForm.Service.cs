using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 public sealed partial class MainForm {
  readonly Label connectionEvidence=new Label(),readinessEvidence=new Label(),trustEvidence=new Label(),checkedEvidence=new Label(),modeEvidence=new Label();
  readonly Font serviceTitleNormal=Theme.F(16,true),serviceTitleCompact=Theme.F(12.5f,true);
  readonly Font serviceContextNormal=Theme.F(9f),serviceContextCompact=Theme.F(8.5f),serviceContextDense=Theme.F(7.5f);
  void FitServiceTitle(){
   statusLabel.Font=TextRenderer.MeasureText(statusLabel.Text,serviceTitleNormal).Width>statusLabel.Width?serviceTitleCompact:serviceTitleNormal;
   subLabel.Font=serviceContextNormal;
   foreach(var font in new[]{serviceContextNormal,serviceContextCompact,serviceContextDense}){
    subLabel.Font=font;if(ContextTextHeight(font)<=subLabel.Height)break;
   }
  }
  int ContextTextHeight(Font font)=>subLabel.GetPreferredSize(new Size(Math.Max(1,subLabel.Width),0)).Height;
  internal bool ContextFitsForTest=>ContextTextHeight(subLabel.Font)<=subLabel.Height;
  internal bool MainEdgesForTest=>evidenceCard.Left==profileSelect.Left&&evidenceCard.Right==profileSelect.Right&&Math.Abs(bridge.Left+bridge.Width/2-profileSelect.Left-profileSelect.Width/2)<=2;

  readonly PillButton serviceDoctor=new PillButton(),serviceRecheck=new PillButton();
  ToolStripMenuItem trayDoctor,trayRecheck,trayRestore;
  DoctorReport serviceReport;string serviceContext;long serviceRevision;
  bool directVerified,manualPause,doctorRunning;CancellationTokenSource doctorCts;
  string observedCatalogRevision;
  internal Func<string> CatalogRevisionSource;
  string CatalogEvidenceRevision()=>CatalogRevisionSource!=null?CatalogRevisionSource():RuntimeCatalog.Refresh().Fingerprint;
  [StructLayout(LayoutKind.Sequential)]struct ServiceClientRect{public int Left,Top,Right,Bottom;}
  [DllImport("user32.dll")]static extern bool GetClientRect(IntPtr window,out ServiceClientRect rectangle);
  internal Func<CancellationToken,Task<DoctorReport>> DoctorSource;
  readonly System.Collections.Generic.Dictionary<Control,int> pageDesignWidths=new System.Collections.Generic.Dictionary<Control,int>();
  void RememberPageWidths(){for(int i=1;i<pages.Length;i++)foreach(Control child in pages[i].Controls)pageDesignWidths[child]=child.Width;}
  bool CurrentServiceReport=>serviceReport!=null&&serviceRevision==Volatile.Read(ref evidenceSerial)&&serviceContext==HealthContext()&&serviceReport.Utc<=DateTime.UtcNow&&DateTime.UtcNow-serviceReport.Utc<Doctor.Freshness;
  bool ServiceReady=>!busy&&!recoveryNeeded&&!evidenceUncertain&&CurrentServiceReport&&serviceReport.Readiness.State==ReadinessState.Ready&&serviceReport.Readiness.Completeness==EvidenceCompleteness.Complete&&serviceReport.Readiness.Required>0&&serviceReport.Readiness.Confirmed==serviceReport.Readiness.Required;
  RoundPanel evidenceCard;
  const int EvidenceTop=350,EvidenceHeight=134;
  void BuildServiceStatus(Panel page){
   // The detailed evidence remains available to diagnostics and reports, never a permanent bridge card.
   evidenceCard=new RoundPanel{Visible=false,Bounds=new Rectangle(14,EvidenceTop,W-28,EvidenceHeight)};
   foreach(var label in new[]{connectionEvidence,readinessEvidence,trustEvidence,checkedEvidence,modeEvidence})evidenceCard.Controls.Add(label);
   var diagnostics=pages[TabBench];
   BtnAsync(diagnostics,serviceDoctor,L.T("Диагностика"),new Rectangle(14,PageBottom-HeaderH-40,W-28,32),Theme.Soft,()=>DoctorAsync());serviceDoctor.Glyph="";
   serviceRecheck.Visible=false;
   bridge.AccessibleName=L.T("Основная команда");scopeSel.AccessibleName=L.T("Сервисы");profileSelect.AccessibleName=L.T("Провайдер");
   bridge.AccessibleRole=AccessibleRole.PushButton;serviceDoctor.AccessibleRole=AccessibleRole.PushButton;serviceRecheck.AccessibleRole=AccessibleRole.PushButton;
   scopeSel.AccessibleRole=AccessibleRole.Grouping;profileSelect.AccessibleRole=AccessibleRole.ComboBox;
   bridge.TabIndex=0;scopeSel.TabIndex=1;profileSelect.TabIndex=2;serviceDoctor.TabIndex=3;serviceRecheck.TabIndex=4;
   serviceDoctor.Font=serviceRecheck.Font=Theme.F(8.5f,true);
   UpdateServiceStatus();
   FitServiceViewport();
  }
  // Windows can cap a fixed form to the monitor while WinForms still caches the
  // requested DPI-scaled ClientSize. Use the actual client rectangle and keep
  // every page scrollable; the navigation bar always remains reachable.
  void FitServiceViewport(){
   if(!IsHandleCreated||nav==null||!GetClientRect(Handle,out var rect))return;
   float scale=LayoutScale;int contentWidth=(int)Math.Round(W*scale);int left=Math.Max(0,(rect.Right-contentWidth)/2);
   nav.SetBounds(0,nav.Top,rect.Right,(int)Math.Round(NavH*scale));
   int bottom=Math.Max(100,rect.Bottom-nav.Height);
   nav.Top=bottom;nav.BringToFront();LayoutChrome(scale,rect.Right);
   for(int i=0;i<pages.Length;i++){
    var page=pages[i];if(page==null)continue;
    page.AutoScroll=true;
    int top=(int)Math.Round((ChromeH+(i==TabBridge?0:HeaderH))*scale);
    page.SetBounds(left,top,Math.Min(rect.Right,contentWidth),Math.Max(80,bottom-top-(int)Math.Round(10*scale)));
    int extent=i==TabBridge?SecondY+SecondH+2:i==TabSettings?466+ElevRow+CardBExtra+(prevCopy.Visible?PrevH:0):i==TabInfo?0:PageBottom-HeaderH;
    page.AutoScrollMinSize=new Size(0,(int)Math.Round(extent*scale));
    // Reserve the actual vertical scrollbar width so a tall secondary page
    // never acquires a second, horizontal scrollbar at its right edge.
    if(i!=TabBridge){
     void FitWidths(){
      int edge=page.ClientSize.Width-(int)Math.Round(14*scale);
      foreach(Control child in page.Controls)if(pageDesignWidths.TryGetValue(child,out var width))child.Width=Math.Max(1,Math.Min((int)Math.Round(width*scale),edge-child.Left));
     }
     FitWidths();
     if(i==TabInfo){   // the page that is measured from its text: lay it out, give the page the height it needs, then fit the widths again (a scroll bar takes some)
      page.AutoScrollMinSize=Size.Empty;FitWidths();int need=LayoutInfo();
      if(need>page.ClientSize.Height){page.AutoScrollMinSize=new Size(0,need);FitWidths();LayoutInfo();}
     }
    }
    if(i==TabBridge){
     int R(int value)=>(int)Math.Round(value*scale);
     int available=page.ClientSize.Width;
     int scroll=page.AutoScrollPosition.Y;
     statusLabel.Left=R(20);statusLabel.Width=Math.Max(1,available-R(40));
     subLabel.Left=R(20);subLabel.Width=Math.Max(1,available-R(40));
     bigLogo.Left=(available-bigLogo.Width)/2;
     scopeSel.SetBounds(R(14),R(ScopeY)+scroll,Math.Max(1,available-R(28)),R(ScopeH));
     profileSelect.SetBounds(R(14),R(ProfileY)+scroll,Math.Max(1,available-R(28)),R(CardUnits));
     profileTelemetry.SetBounds(R(14),R(CardHeaderH+8),Math.Max(1,profileSelect.Width-R(28)),Math.Max(1,profileSelect.Height-R(CardHeaderH+16)));
     foreach(var caption in new[]{scopeCaption,profileCaption}){caption.Left=R(28);caption.Width=Math.Max(1,available-R(56));}
     if(detailsLink!=null)detailsLink.SetBounds(R(14),R(SecondY)+scroll,Math.Max(1,available-R(28)),R(SecondH));
    }
   }
   foreach(var overlay in new[]{detailsPanel,customPanel,browserPanel})if(overlay!=null){
    overlay.Left=left+(int)Math.Round(14*scale);overlay.Top=(int)Math.Round((ChromeH+10)*scale);
    if(overlay!=detailsPanel){
     overlay.AutoScroll=true;
     overlay.AutoScrollMinSize=new Size(0,(int)Math.Round((overlay==customPanel?350:PageBottom-20)*scale));
     overlay.Height=Math.Max((int)Math.Round(220*scale),bottom-(int)Math.Round((ChromeH+20)*scale));
    }
   }
   FitDiagnostic();
   if(detailsPanel!=null&&detailsBox!=null){
    int R(int value)=>(int)Math.Round(value*scale);
    detailsPanel.Height=Math.Max(R(220),bottom-R(ChromeH+20));
    detailsCopy.Top=detailsPanel.Height-R(48);
    detailsBox.Height=Math.Max(R(40),detailsCopy.Top-R(10)-detailsBox.Top);
   }
  }
  protected override void OnSizeChanged(EventArgs e){
   base.OnSizeChanged(e);bigLogo?.RefreshMotion();FitServiceViewport();
   if(IsHandleCreated&&!IsDisposed)BeginInvoke(new Action(()=>{if(!IsDisposed)UpdateButtons();}));
  }
  protected override void OnShown(EventArgs e){PerfLog.Mark("form shown");base.OnShown(e);FixWindowSize();FitServiceViewport();UpdateButtons();if(PerfLog.On){EventHandler idle=null;idle=(_,__)=>{Application.Idle-=idle;PerfLog.Mark("first idle after shown");};Application.Idle+=idle;}}
  protected override void OnDpiChanged(DpiChangedEventArgs e){
   base.OnDpiChanged(e);
   if(IsHandleCreated&&!IsDisposed)BeginInvoke(new Action(()=>{if(!IsDisposed){FixWindowSize();FitServiceViewport();UpdateButtons();}}));
  }
  void UpdateServiceStatus(){
   string revision=CatalogEvidenceRevision();
   if(observedCatalogRevision!=null&&observedCatalogRevision!=revision){
    Interlocked.Increment(ref evidenceSerial);opSerial++;checksPassed=false;networkEvidenceUtc=default;speed=null;evidenceUncertain=needsRestore;
    directVerified=false;serviceReport=null;offer=null;
   }
   observedCatalogRevision=revision;
   bool current=CurrentServiceReport;
   string connection=recoveryNeeded?L.T("восстановление требуется"):!known?L.T("неизвестно"):needsRestore?(isActive?L.T("native policy включена"):L.T("частичный результат / конфликт")):directVerified&&!evidenceUncertain&&networkEvidenceUtc<=DateTime.UtcNow&&DateTime.UtcNow-networkEvidenceUtc<Doctor.Freshness?"Direct":manualPause?L.T("ручная пауза"):L.T("выключено");
   connectionEvidence.Text=L.T("Соединение: ")+connection;
   readinessEvidence.Text=L.T("Готовность: ")+(current?ServiceReadinessText(serviceReport.Readiness.State):serviceReport==null?L.T("неизвестна — откройте Doctor"):L.T("устарела — повторите Doctor"));
   readinessEvidence.ForeColor=ServiceReady?Theme.Green:Theme.Amber;
   trustEvidence.Text=L.T("Доверие: подтверждение перед изменением");
   checkedEvidence.Text=L.T("Проверено: ")+(current?serviceReport.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):L.T("нет актуального полного результата"));
   modeEvidence.Text=L.T("Native Assisted · каталог: ")+RuntimeCatalog.Refresh().Pack.Revision;
   modeEvidence.AutoEllipsis=true;modeEvidence.AccessibleDescription=RuntimeCatalog.DisplayRevision;
   scopeSel.AccessibleName=L.T("Сервисы");profileSelect.AccessibleName=L.T("Провайдер");
   serviceDoctor.Enabled=!busy&&!doctorRunning;serviceRecheck.Enabled=!busy&&!refreshing;
   if(trayDoctor!=null){trayDoctor.Text=L.T("Диагностика (F6)");trayDoctor.Enabled=serviceDoctor.Enabled;trayRecheck.Text=L.T("Проверить (F5)");trayRecheck.Enabled=serviceRecheck.Enabled;trayRestore.Text=L.T("Восстановить сеть");trayRestore.Enabled=!busy&&known&&needsRestore;}
  }
  static string ServiceReadinessText(ReadinessState state)=>state==ReadinessState.LikelyReady?L.T("сеть: вероятно; приложение: неизвестно"):state==ReadinessState.Ready?L.T("подтверждена"):state==ReadinessState.Off?L.T("выключено"):state==ReadinessState.Failed?L.T("сбой"):state==ReadinessState.Unknown?L.T("неизвестно"):L.T("частичная / требуется проверка");
  void AddServiceTray(ContextMenuStrip menu){
   trayDoctor=new ToolStripMenuItem();trayDoctor.Click+=async(_,__)=>{ShowWindow();await DoctorAsync();};
   trayRecheck=new ToolStripMenuItem();trayRecheck.Click+=async(_,__)=>{if(!busy)await RefreshStatus();};
   trayRestore=new ToolStripMenuItem();trayRestore.Click+=async(_,__)=>{try{if(!busy&&known&&needsRestore)await Mutate("restore");}catch(Exception e){Log(e.Message);}};
   menu.Items.AddRange(new ToolStripItem[]{trayDoctor,trayRecheck,trayRestore});
  }
  async Task DoctorAsync(){
   if(busy||doctorRunning||IsDisposed)return;
   nav.Selected=TabBench;
   doctorRunning=true;UpdateButtons();if(!smoke)ShowDiagnostic();
   var captured=CaptureDiagnosticContext();
   using(var cts=CancellationTokenSource.CreateLinkedTokenSource(lifeCts.Token)){
    doctorCts=cts;cts.CancelAfter(TimeSpan.FromSeconds(45));
    try{
     var report=DoctorSource!=null?await DoctorSource(cts.Token):await Doctor.RunAsync(new LiveDoctorProbe(),cts.Token);
     if(IsDisposed)return;
     if(cts.IsCancellationRequested||captured.Revision!=Volatile.Read(ref evidenceSerial)||captured.Context!=HealthContext()){
      if(!smoke&&diagnosticPanel.Visible)ShowDiagnostic(null,L.En?"Context changed · run the check again":"Условия изменились · повторите проверку");
      return;
     }
     serviceReport=report;serviceContext=captured.Context;serviceRevision=captured.Revision;
     if(!smoke&&diagnosticPanel.Visible)ShowDiagnostic(report);
    }catch(OperationCanceledException){if(!smoke&&!IsDisposed&&diagnosticPanel.Visible)ShowDiagnostic(null,L.En?"Check cancelled":"Проверка отменена");}catch(Exception e){if(!IsDisposed){Log("Doctor: "+e.Message);if(!smoke&&diagnosticPanel.Visible)ShowDiagnostic(null,L.En?"Check failed · see Logs":"Проверка не завершена · см. Логи");}}
    finally{doctorCts=null;doctorRunning=false;if(!IsDisposed){UpdateButtons();if(diagnosticView!=null)diagnosticView.SetRunning(false);}}
   }
  }
  internal Task DoctorForTest()=>DoctorAsync();
  internal bool DoctorRunningForTest=>doctorRunning;
  internal string[] ServiceEvidenceForTest=>new[]{connectionEvidence.Text,readinessEvidence.Text,trustEvidence.Text,checkedEvidence.Text,modeEvidence.Text};
  internal bool ServiceReadyForTest=>ServiceReady;
  internal string ServiceKeyboardDiagnosticsForTest{get;private set;}
  internal bool ServiceKeyboardForTest(){
   Activate();bridge.Select();Application.DoEvents();
   // Native foreground focus can move to another verification window. The
   // form's ActiveControl still records the actual WinForms keyboard route.
   // When this form owns native focus, also require the destination to own it.
   bool At(Control control)=>ActiveControl==control&&(!ContainsFocus||control.Focused);
   bool primary=At(bridge);string start=$"primary={primary}, active={ActiveControl?.GetType().Name}, native={ContainsFocus}/{bridge.Focused}";
   bool next=SelectNextControl(bridge,true,true,true,false);bool services=At(scopeSel);
   bool profileNext=SelectNextControl(scopeSel,true,true,true,false);bool profile=At(profileSelect);
   bool previous=SelectNextControl(profileSelect,false,true,true,false);bool back=At(scopeSel);
   ServiceKeyboardDiagnosticsForTest=$"{start}; next={next}/{services}; profile={profileNext}/{profile}; reverse={previous}/{back}; active={ActiveControl?.GetType().Name}, native={ContainsFocus}/{scopeSel.Focused}; tabs={bridge.TabIndex}/{scopeSel.TabIndex}/{profileSelect.TabIndex}, stops={bridge.TabStop}/{scopeSel.TabStop}/{profileSelect.TabStop}";
   return primary&&next&&services&&profileNext&&profile&&previous&&back&&scopeSel.AccessibleName==L.T("Сервисы")&&serviceDoctor.Parent==pages[TabBench];
  }
  internal bool ServiceScrollReachableForTest(){
   FitServiceViewport();var page=pages[TabBridge];page.ScrollControlIntoView(profileSelect);Application.DoEvents();
   var actual=page.RectangleToClient(profileSelect.RectangleToScreen(profileSelect.ClientRectangle));
   bool accessible=actual.Bottom<=page.ClientRectangle.Bottom&&actual.Top>=0&&nav.Bottom<=Height;
   page.AutoScrollPosition=Point.Empty;return accessible;
  }
  internal bool DetailsFooterReachableForTest(){
   ShowDetails(null);FitServiceViewport();
   bool visible=GetClientRect(Handle,out var rect)&&detailsPanel.Bottom<=rect.Bottom&&detailsCopy.Bottom<=detailsPanel.ClientRectangle.Bottom&&detailsBox.Bottom<=detailsCopy.Top;
   detailsPanel.Visible=false;return visible;
  }
 }
}
