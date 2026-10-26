using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Linq;
using System.Windows.Forms;
namespace AntigravityGeminiDns {
 internal class CompactDialog:Form {
  [DllImport("user32.dll")]static extern bool ReleaseCapture();
  [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam);
  protected void BuildDialogChrome(){
   var bar=new Panel{Dock=DockStyle.Top,Height=36,BackColor=Theme.Bg};
   var title=new Label{Text=Text,Dock=DockStyle.Fill,Padding=new Padding(18,0,0,0),TextAlign=ContentAlignment.MiddleLeft,ForeColor=Theme.Text,Font=Theme.F(9,true)};
   var close=new CaptionButton(true){Dock=DockStyle.Right,Width=40,AccessibleName=L.T("Закрыть")};
   close.Click+=(_,__)=>Close();bar.Controls.Add(title);bar.Controls.Add(close);Controls.Add(bar);bar.BringToFront();
   void Drag(object s,MouseEventArgs e){if(e.Button!=MouseButtons.Left)return;ReleaseCapture();SendMessage(Handle,0x00A1,(IntPtr)2,IntPtr.Zero);}
   title.MouseDown+=Drag;bar.MouseDown+=Drag;
  }
  internal void FitToWorkArea(){
   var work=Screen.FromControl(this).WorkingArea;if(Height>work.Height)Height=work.Height;if(Width>work.Width)Width=work.Width;
   float k=DeviceDpi/96f;int margin=(int)Math.Round(18*k),gap=(int)Math.Round(12*k);
   var buttons=Controls.OfType<PillButton>().OrderBy(b=>b.Left).ToArray();
   if(buttons.Length==2){int width=Math.Max(40,(ClientSize.Width-2*margin-gap)/2);buttons[0].Left=margin;buttons[0].Width=width;buttons[1].Left=margin+width+gap;buttons[1].Width=width;}
   Location=new Point(Math.Clamp(Left,work.Left,work.Right-Width),Math.Clamp(Top,work.Top,work.Bottom-Height));
  }
  protected override void OnShown(EventArgs e){base.OnShown(e);FitToWorkArea();}
  protected override bool ProcessCmdKey(ref Message msg,Keys key){if(key==Keys.Escape){Close();return true;}return base.ProcessCmdKey(ref msg,key);}
 }
}
