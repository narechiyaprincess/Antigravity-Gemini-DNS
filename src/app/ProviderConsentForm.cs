using System;
using System.Drawing;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 internal sealed class ProviderConsentForm:CompactDialog {
  internal readonly TextBox Details=new();
  internal readonly PillButton Allow=new(),CancelChoice=new();
  public ProviderConsentForm(string title,string detail) {
   SuspendLayout();AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
   Text=title;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;MaximizeBox=false;MinimizeBox=false;
   StartPosition=FormStartPosition.CenterParent;ClientSize=new Size(420,480);BackColor=Theme.Bg;ForeColor=Theme.Text;Font=Theme.F(10);
   var content=new Panel{Dock=DockStyle.Fill,Padding=new Padding(18,10,18,0),BackColor=Theme.Bg};
   Details.Multiline=true;Details.ReadOnly=true;Details.WordWrap=true;Details.ScrollBars=ScrollBars.Vertical;Details.BorderStyle=BorderStyle.None;
   Details.BackColor=Theme.Card;Details.ForeColor=Theme.Text;Details.Font=Theme.F(9.5f);Details.Dock=DockStyle.Fill;
   Details.Text=(detail??"").Replace("\r\n","\n").Replace("\n","\r\n");Details.AccessibleName=L.En?"Providers and service scope":"Провайдеры и выбранные сервисы";Details.TabIndex=2;
   var card=new Panel{Dock=DockStyle.Fill,Padding=new Padding(12),BackColor=Theme.Card};card.Controls.Add(Details);content.Controls.Add(card);Controls.Add(content);
   var headline=new Label{Text=L.En?"Choose who you trust":"Выберите, кому доверяете",Dock=DockStyle.Top,Height=42,Padding=new Padding(18,8,18,0),Font=Theme.F(12,true),ForeColor=Theme.Text,AutoEllipsis=true};Controls.Add(headline);
   var actions=new TableLayoutPanel{Dock=DockStyle.Bottom,Height=76,Padding=new Padding(12,14,12,14),ColumnCount=2,RowCount=1,BackColor=Theme.Bg};
   actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));actions.RowStyles.Add(new RowStyle(SizeType.Percent,100));
   CancelChoice.Text=L.En?"Cancel":"Отмена";CancelChoice.SetFill(Theme.Soft);CancelChoice.Dock=DockStyle.Fill;CancelChoice.Margin=new Padding(6,0,6,0);CancelChoice.TabIndex=0;CancelChoice.AccessibleRole=AccessibleRole.PushButton;
   Allow.Text=L.En?"Allow":"Разрешить";Allow.SetFill(Theme.Accent);Allow.Dock=DockStyle.Fill;Allow.Margin=new Padding(6,0,6,0);Allow.TabIndex=1;Allow.AccessibleRole=AccessibleRole.PushButton;
   CancelChoice.Click+=(_,__)=>Finish(false);Allow.Click+=(_,__)=>Finish(true);
   actions.Controls.Add(CancelChoice,0,0);actions.Controls.Add(Allow,1,0);Controls.Add(actions);
   BuildDialogChrome();ResumeLayout(true);Details.SelectionStart=0;Details.SelectionLength=0;
  }
  void Finish(bool accepted){DialogResult=accepted?DialogResult.Yes:DialogResult.Cancel;Close();}
  protected override void OnShown(EventArgs e){base.OnShown(e);CancelChoice.Focus();}
  protected override bool ProcessCmdKey(ref Message msg,Keys keyData) {
   if(keyData==Keys.Escape){Finish(false);return true;}
   if(keyData==Keys.Enter&&ActiveControl!=Allow){Finish(false);return true;}
   return base.ProcessCmdKey(ref msg,keyData);
  }
  internal void CancelForTest()=>Finish(false);
  internal static bool Confirm(IWin32Window owner,string title,string detail) {
   using var dialog=new ProviderConsentForm(title,detail);return dialog.ShowDialog(owner)==DialogResult.Yes;
  }
 }
}
