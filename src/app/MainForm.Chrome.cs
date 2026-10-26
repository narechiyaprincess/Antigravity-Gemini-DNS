using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 public sealed partial class MainForm {
  readonly Panel windowBar=new Panel();
  readonly CaptionButton windowMinimize=new CaptionButton(false),windowClose=new CaptionButton(true);
  readonly LogoBox windowMark=new LogoBox();
  readonly Label windowTitle=new Label();
  bool fixingWindow;
  internal Func<Rectangle> WorkAreaForTest;
  internal void FitWorkAreaForTest(Rectangle area){WorkAreaForTest=()=>area;FixWindowSize();FitServiceViewport();UpdateButtons();}
  [DllImport("user32.dll")]static extern bool ReleaseCapture();
  [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam);

  void BuildChrome(){
   windowBar.BackColor=Theme.Bg;windowBar.TabStop=false;Controls.Add(windowBar);
   windowMark.TabStop=false;windowBar.Controls.Add(windowMark);
   windowTitle.Text="Antigravity-Gemini DNS";windowTitle.Font=Theme.F(9f,true);windowTitle.ForeColor=Theme.Muted;
   windowTitle.TextAlign=ContentAlignment.MiddleLeft;windowTitle.BackColor=Theme.Bg;windowBar.Controls.Add(windowTitle);
   windowBar.Controls.Add(windowMinimize);windowBar.Controls.Add(windowClose);
   windowMinimize.TabIndex=100;windowClose.TabIndex=101;
   windowMinimize.Click+=(_,__)=>WindowState=FormWindowState.Minimized;
   windowClose.Click+=(_,__)=>Close();
   void Drag(object sender,MouseEventArgs e){if(e.Button!=MouseButtons.Left)return;ReleaseCapture();SendMessage(Handle,0x00A1,(IntPtr)2,IntPtr.Zero);}
   windowBar.MouseDown+=Drag;windowTitle.MouseDown+=Drag;windowMark.MouseDown+=Drag;
   windowBar.Paint+=(_,e)=>{using(var pen=new Pen(Theme.Line))e.Graphics.DrawLine(pen,0,windowBar.Height-1,windowBar.Width,windowBar.Height-1);};
   LayoutChrome(1f,W);
  }
  void LayoutChrome(float scale,int width){
   if(windowBar==null||windowTitle==null)return;
   int R(int value)=>(int)Math.Round(value*scale);
   windowBar.SetBounds(0,0,width,R(ChromeH));
   windowMark.SetBounds(R(14),R(9),R(18),R(18));
   windowTitle.SetBounds(R(40),0,Math.Max(1,width-R(128)),R(ChromeH)-1);
   windowMinimize.SetBounds(width-R(80),0,R(40),R(ChromeH)-1);windowClose.SetBounds(width-R(40),0,R(40),R(ChromeH)-1);
   windowMinimize.AccessibleName=L.En?"Minimize":"Свернуть";windowClose.AccessibleName=L.En?"Close":"Закрыть";
   windowBar.BringToFront();
  }
  void FixWindowSize(){
   if(fixingWindow||!IsHandleCreated||WindowState==FormWindowState.Minimized)return;
   fixingWindow=true;
   try{
    float scale=DeviceDpi/96f;var work=WorkAreaForTest!=null?WorkAreaForTest():Screen.FromControl(this).WorkingArea;
    var size=new Size(Math.Min(work.Width,(int)Math.Round(W*scale)),Math.Min(work.Height,(int)Math.Round(H*scale)));
    MinimumSize=Size.Empty;MaximumSize=Size.Empty;
    if(WindowState==FormWindowState.Maximized)WindowState=FormWindowState.Normal;
    // WinForms may constrain MinimumSize just inside the monitor work area.
    // Lock the other bounds to the accepted value rather than leaving a resize gap.
    MinimumSize=size;size=MinimumSize;MaximumSize=size;Size=size;
    var fitted=new Point(Math.Clamp(Left,work.Left,Math.Max(work.Left,work.Right-Width)),Math.Clamp(Top,work.Top,Math.Max(work.Top,work.Bottom-Height)));
    Location=smoke?SmokeLocation:fitted;   // a render/self-test window keeps its size logic but stays off the desktop (see the constructor)
   }finally{fixingWindow=false;}
  }
  protected override void WndProc(ref Message m){
   if(m.Msg==0x0112){long command=m.WParam.ToInt64()&0xFFF0;if(command==0xF000||command==0xF030){m.Result=IntPtr.Zero;return;}}
   bool dpi=m.Msg==0x02E0;
   if(dpi){MinimumSize=Size.Empty;MaximumSize=Size.Empty;}
   base.WndProc(ref m);
   if(dpi){FixWindowSize();FitServiceViewport();}
  }
  internal bool FixedWindowForTest=>FormBorderStyle==FormBorderStyle.None&&!MaximizeBox&&MinimumSize==MaximumSize&&MinimumSize==Size;
  internal int ChromeBottomForTest=>windowBar.Bottom;
  internal Size NativeClientSizeForTest=>GetClientRect(Handle,out var client)?new Size(client.Right,client.Bottom):Size.Empty;
  internal void SystemCommandForTest(int command)=>SendMessage(Handle,0x0112,(IntPtr)command,IntPtr.Zero);
  internal void CaptionMinimizeForTest()=>windowMinimize.ClickForTest();
  internal void CaptionCloseForTest()=>windowClose.ClickForTest();
  internal BenchTable BenchForTest=>bench;
  internal bool CompactBodyForTest(){
   FitServiceViewport();var page=pages[TabBridge];
   return page.Top>=windowBar.Bottom&&page.Bottom<=nav.Top&&profileSelect.Bottom<=page.ClientSize.Height&&page.AutoScrollPosition==Point.Empty;
  }
 }

 internal sealed class CaptionButton:Surface {
  readonly bool close;bool hot;
  public CaptionButton(bool close){this.close=close;AccessibleRole=AccessibleRole.PushButton;Cursor=Cursors.Hand;}
  internal void ClickForTest()=>OnClick(EventArgs.Empty);
  protected override void OnMouseEnter(EventArgs e){hot=true;Invalidate();base.OnMouseEnter(e);}
  protected override void OnMouseLeave(EventArgs e){hot=false;Invalidate();base.OnMouseLeave(e);}
  protected override bool IsInputKey(Keys key)=>key==Keys.Space||key==Keys.Enter||base.IsInputKey(key);
  protected override void OnKeyUp(KeyEventArgs e){if(e.KeyCode==Keys.Space||e.KeyCode==Keys.Enter){OnClick(EventArgs.Empty);e.Handled=true;}base.OnKeyUp(e);}
  protected override void OnPaint(PaintEventArgs e){
   var g=e.Graphics;g.Clear(hot?(close?Theme.Danger:Theme.Soft):Theme.Bg);
   float k=DeviceDpi/96f,cx=Width/2f,cy=Height/2f;
   using(var pen=new Pen(hot?Theme.Text:Theme.Muted,1.25f*k)){
    if(close){g.DrawLine(pen,cx-4*k,cy-4*k,cx+4*k,cy+4*k);g.DrawLine(pen,cx+4*k,cy-4*k,cx-4*k,cy+4*k);}
    else g.DrawLine(pen,cx-5*k,cy,cx+5*k,cy);
   }
   if(Focused&&ShowFocusCues)Theme.FocusRing(g,this,3);
  }
 }
}
