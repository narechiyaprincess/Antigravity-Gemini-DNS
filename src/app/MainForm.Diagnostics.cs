using System;
using System.Drawing;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 public sealed partial class MainForm {
  Panel diagnosticPanel;
  DoctorView diagnosticView;
  void ShowDiagnostic(DoctorReport report=null,string message=null){
   if(diagnosticPanel==null){
    diagnosticPanel=new Panel{BackColor=Theme.Bg,Visible=false};
    diagnosticView=new DoctorView{Dock=DockStyle.Fill};
    diagnosticView.CloseRequested+=()=>{doctorCts?.Cancel();diagnosticPanel.Visible=false;};
    diagnosticView.RecheckRequested+=async()=>await DoctorAsync();
    diagnosticPanel.Controls.Add(diagnosticView);Controls.Add(diagnosticPanel);
   }
   diagnosticView.ShowReport(report,message,doctorRunning);
   diagnosticPanel.Visible=true;FitDiagnostic();diagnosticPanel.BringToFront();
  }
  void FitDiagnostic(){
   if(diagnosticPanel==null||!IsHandleCreated)return;
   int top=(int)Math.Round(ChromeH*LayoutScale);
   diagnosticPanel.SetBounds(0,top,nav.Width,Math.Max(1,nav.Top-top));
  }
  internal bool DiagnosticEmbeddedForTest=>diagnosticPanel!=null&&diagnosticPanel.Visible&&diagnosticPanel.Parent==this&&diagnosticPanel.Bottom<=nav.Top;
  internal void ShowDiagnosticForTest(DoctorReport report)=>ShowDiagnostic(report);
 }

 // One compact surface shared by the live main window and diagnostic snapshots.
 internal sealed class DoctorView:UserControl {
  readonly Label title=new Label(),verdict=new Label(),stamp=new Label();
  readonly Panel rows=new Panel();
  readonly PillButton close=new PillButton(),copy=new PillButton(),recheck=new PillButton();
  DoctorReport report;
  public event Action CloseRequested,RecheckRequested;
  public DoctorView(){
   AutoScaleMode=AutoScaleMode.Dpi;AutoScaleDimensions=new SizeF(96,96);
   BackColor=Theme.Bg;ForeColor=Theme.Text;Font=Theme.F(9);
   title.Text=L.T("Диагностика");title.Font=Theme.F(11,true);
   verdict.Font=Theme.F(9,true);verdict.AutoEllipsis=true;
   stamp.Font=Theme.F(8);stamp.ForeColor=Theme.Muted;stamp.AutoEllipsis=true;
   rows.AutoScroll=true;rows.BackColor=Theme.Card;
   rows.HandleCreated+=(_,__)=>{try{SetWindowTheme(rows.Handle,"DarkMode_Explorer",null);}catch{}};
   close.Text=L.En?"Back":"Назад";close.SetFill(Theme.Soft);close.Click+=(_,__)=>CloseRequested?.Invoke();
   copy.Text=L.T("Копировать");copy.SetFill(Theme.Soft);copy.Click+=(_,__)=>{if(report!=null)Clipboard.SetText(report.ToText());};
   recheck.Text=L.En?"Recheck":"Повторить";recheck.SetFill(Theme.Accent);recheck.Click+=(_,__)=>RecheckRequested?.Invoke();
   foreach(var button in new[]{close,copy,recheck}){button.AccessibleRole=AccessibleRole.PushButton;button.AccessibleName=button.Text;}
   close.TabIndex=0;rows.TabIndex=1;copy.TabIndex=2;recheck.TabIndex=3;
   Controls.AddRange(new Control[]{title,verdict,stamp,rows,close,copy,recheck});
   Resize+=(_,__)=>Arrange();
  }
  int R(int v)=>(int)Math.Round(v*(FindForm()?.DeviceDpi??DeviceDpi)/96f);
  protected override void OnDpiChangedAfterParent(EventArgs e){base.OnDpiChangedAfterParent(e);Arrange();}
  void Arrange(){
   if(rows==null)return;
   int pad=R(14),width=Math.Max(1,ClientSize.Width-2*pad),footer=Math.Max(R(100),ClientSize.Height-R(46));
   title.SetBounds(pad,R(10),Math.Max(1,width-R(82)),R(25));close.SetBounds(ClientSize.Width-pad-R(76),R(8),R(76),R(28));
   verdict.SetBounds(pad,R(44),width,R(34));stamp.SetBounds(pad,R(80),width,R(18));
   rows.SetBounds(pad,R(106),width,Math.Max(1,footer-R(116)));
   int half=(width-R(8))/2;copy.SetBounds(pad,footer,half,R(32));recheck.SetBounds(pad+half+R(8),footer,width-half-R(8),R(32));
   foreach(Control row in rows.Controls)row.Width=Math.Max(1,rows.ClientSize.Width-R(16));
  }
  public void SetRunning(bool running){recheck.Enabled=!running;}
  public void ShowReport(DoctorReport value,string message,bool running){
   report=value;verdict.Text=message??value?.Verdict??(L.En?"Checking layers…":"Проверяю слои…");
   verdict.ForeColor=value?.Readiness.State==ReadinessState.Failed?Theme.Red:value?.Readiness.State==ReadinessState.Off?Theme.Text:Theme.Amber;
   stamp.Text=value==null?(L.En?"Read only · Esc to cancel":"Только чтение · Esc — отмена"):(L.En?"Confirmed: ":"Подтверждено: ")+value.Readiness.Confirmed+"/"+value.Readiness.Required+" · "+value.Utc.ToLocalTime().ToString("HH:mm:ss");
   copy.Enabled=value!=null;recheck.Enabled=!running;
   rows.SuspendLayout();while(rows.Controls.Count>0){var c=rows.Controls[0];rows.Controls.RemoveAt(0);c.Dispose();}
   int y=R(6);
   foreach(var layer in value?.Layers??Array.Empty<DoctorLayer>()){
    var row=new Panel{BackColor=Theme.Card,Location=new Point(R(8),y),Size=new Size(Math.Max(1,rows.ClientSize.Width-R(16)),R(53))};
    var name=new Label{Text=DoctorPresentation.NameOf(layer.Id),Font=Theme.F(8.5f,true),ForeColor=Theme.Text,Bounds=new Rectangle(0,0,Math.Max(1,row.Width-R(90)),R(21)),AutoEllipsis=true,Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right};
    var level=new Label{Text=Doctor.Word(layer.Level),ForeColor=DoctorPresentation.LevelInk(layer.Level),Font=Theme.F(8),TextAlign=ContentAlignment.MiddleRight,Bounds=new Rectangle(row.Width-R(90),0,R(90),R(21)),Anchor=AnchorStyles.Top|AnchorStyles.Right};
    var summary=new LinkLabel{Text=layer.Summary??"",Font=Theme.F(8),LinkColor=Theme.Muted,ActiveLinkColor=Theme.Text,VisitedLinkColor=Theme.Muted,LinkBehavior=LinkBehavior.HoverUnderline,AutoEllipsis=true,Bounds=new Rectangle(0,R(22),row.Width,R(25)),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right};
    var observation=layer.Observation;
    string detail=(layer.Detail??layer.Summary??"")+(observation==null?"":"\r\n"+observation.Source+" · "+observation.ClientPath+" · "+observation.ObservedUtc.ToLocalTime().ToString("HH:mm:ss"));
    summary.AccessibleDescription=detail;
    summary.LinkClicked+=(_,__)=>{
     bool opening=row.Controls.Count==3;
     int delta;
     if(opening){var box=new TextBox{Multiline=true,ReadOnly=true,WordWrap=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,BackColor=Theme.Field,ForeColor=Theme.Muted,Font=Theme.F(8),Text=detail,Bounds=new Rectangle(0,R(51),row.Width,R(90)),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right};row.Controls.Add(box);delta=R(96);}
     else{var box=row.Controls[3];row.Controls.Remove(box);box.Dispose();delta=-R(96);}
     row.Height+=delta;foreach(Control next in rows.Controls)if(next!=row&&next.Top>row.Top)next.Top+=delta;
     Arrange();
    };
    row.Controls.AddRange(new Control[]{name,level,summary});rows.Controls.Add(row);y+=R(55);
   }
   rows.AutoScrollPosition=Point.Empty;rows.ResumeLayout();Arrange();
  }
  [System.Runtime.InteropServices.DllImport("uxtheme.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]static extern int SetWindowTheme(IntPtr hwnd,string app,string ids);
 }
}
