using System;
using System.Drawing;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 // Preview of the support report: the user sees the text that will be saved (read-only, already redacted). OK = "save", Cancel = close without saving.
 internal sealed class ReportPreviewForm:CompactDialog {
  internal readonly TextBox Box=new TextBox();
  readonly PillButton save=new PillButton(),close=new PillButton();

  public ReportPreviewForm(string text) {
   SuspendLayout();
   AutoScaleDimensions=new SizeF(96f,96f);AutoScaleMode=AutoScaleMode.Dpi;
   Text=L.T("Предпросмотр отчёта");FormBorderStyle=FormBorderStyle.None;MaximizeBox=false;MinimizeBox=false;ShowInTaskbar=false;
   ClientSize=new Size(520,560);BackColor=Theme.Bg;ForeColor=Theme.Text;Font=Theme.F(10);StartPosition=FormStartPosition.CenterParent;
   var hint=new Label{Text=L.T("Файл будет сохранён в таком виде. Имя пользователя, имя компьютера, личные папки и имена сетей вырезаны. Отчёт никуда не отправляется."),ForeColor=Theme.Muted,Font=Theme.F(8.5f),BackColor=Color.Transparent,AutoSize=false,Bounds=new Rectangle(18,48,484,52)};
   Controls.Add(hint);
   var card=new RoundPanel{Bounds=new Rectangle(14,106,492,384),Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right,Padding=new Padding(12)};Controls.Add(card);
   Box.Multiline=true;Box.ReadOnly=true;Box.ScrollBars=ScrollBars.Vertical;Box.WordWrap=true;Box.BorderStyle=BorderStyle.None;Box.BackColor=Theme.Card;Box.ForeColor=Theme.Text;Box.Font=new Font("Consolas",8.5f);
   Box.Dock=DockStyle.Fill;Box.Bounds=new Rectangle(12,12,468,360);Box.Text=(text??"").Replace("\r\n","\n").Replace("\n","\r\n");Box.TabStop=true;Box.AccessibleName=L.T("Отчёт для поддержки");card.Controls.Add(Box);
   save.Anchor=close.Anchor=AnchorStyles.Bottom|AnchorStyles.Left;
   save.Text=L.T("Сохранить…");save.Bounds=new Rectangle(18,502,236,44);save.SetFill(Theme.Accent);save.Click+=(_,__)=>{DialogResult=DialogResult.OK;Close();};Controls.Add(save);
   close.Text=L.T("Закрыть");close.Bounds=new Rectangle(266,502,236,44);close.SetFill(Theme.Soft);close.Click+=(_,__)=>{DialogResult=DialogResult.Cancel;Close();};Controls.Add(close);
   BuildDialogChrome();ResumeLayout(false);
   Box.SelectionStart=0;Box.SelectionLength=0;
  }
  protected override void OnHandleCreated(EventArgs e) {
   base.OnHandleCreated(e);
   try{int one=1;DwmSetWindowAttribute(Handle,20,ref one,4);}catch{}
  }
  [System.Runtime.InteropServices.DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int value,int size);
 }
}
