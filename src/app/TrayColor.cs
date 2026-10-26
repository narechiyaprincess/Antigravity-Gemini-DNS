using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AntigravityGeminiDns {
 // v1.4 tray colour: one dot on the existing icon says the state at a glance. The text of the tooltip stays the source of detail; the colour is never the only signal.
 public enum TrayState { Gray, Green, Yellow, Red }

 public static class TrayColor {
  // Order matters: a recovery need or a repair offer is red even while the bridge is up, a check in progress is yellow, then green or grey.
  public static TrayState From(bool isActive,bool checking,bool busy,bool recoveryNeeded,bool showFix) {
   if(recoveryNeeded||showFix)return TrayState.Red;
   if(busy||checking)return TrayState.Yellow;
   return isActive?TrayState.Green:TrayState.Gray;
  }

  public static Color DotOf(TrayState s)=>s==TrayState.Green?Color.FromArgb(82,224,138):s==TrayState.Yellow?Color.FromArgb(240,190,70):s==TrayState.Red?Color.FromArgb(240,98,92):Color.FromArgb(138,141,152);

  // The brand's micro A stays legible at tray size; its status dot has a dark rim for either taskbar theme.
  public static Icon Compose(Icon baseIcon,TrayState state,int size) {
   using(var bmp=new Bitmap(size,size,System.Drawing.Imaging.PixelFormat.Format32bppArgb))
   using(var g=Graphics.FromImage(bmp)) {
    g.Clear(Color.Transparent);
    g.SmoothingMode=SmoothingMode.AntiAlias;
    var mark=g.Save();g.ScaleTransform(size/224f,size/224f);g.TranslateTransform(0,12);
    using(var ink=new SolidBrush(Color.White)){
     g.FillPolygon(ink,new[]{new PointF(92,22),new PointF(116,22),new PointF(56,178),new PointF(34,178)});
     g.FillPolygon(ink,new[]{new PointF(104,22),new PointF(132,22),new PointF(194,178),new PointF(150,178)});
     g.FillRectangle(ink,66,110,86,13);
    }
    g.Restore(mark);
    float d=size*0.42f,x=size-d-0.5f,y=size-d-0.5f;
    using(var rim=new SolidBrush(Color.FromArgb(230,16,16,20)))g.FillEllipse(rim,x-1,y-1,d+2,d+2);
    using(var dot=new SolidBrush(DotOf(state)))g.FillEllipse(dot,x,y,d,d);
    IntPtr h=bmp.GetHicon();
    try{var tmp=Icon.FromHandle(h);return (Icon)tmp.Clone();}
    finally{DestroyIcon(h);}
   }
  }

  [DllImport("user32.dll")]static extern bool DestroyIcon(IntPtr handle);
 }
}
