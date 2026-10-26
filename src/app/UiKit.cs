using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 public sealed class AppSettings {
  public bool CloseToTray=true;
  public bool StartHidden=true;
  public bool Notify=true;
  public bool Core=true;   // Antigravity: the catalog's core hosts; on by default
  public bool Gemini;   // Gemini: the catalog's extra Gemini hosts; off by default
  public bool Google;   // Browser: the catalog's Google suffixes; off by default, independent of the others
  public string Language="ru";   // "ru" or "en"; switching restarts the program
  [System.Text.Json.Serialization.JsonIgnore]public bool NoSave;   // a render run or the self-test works on defaults and never writes the owner's file
  public string Browser="";   // the browser used for Google (BrowserCatalog id, "other" or empty); only read from the system, never changed there
  public bool TrayHintShown;
  public string AutostartPath="";              // the exe the user chose for autostart («Файл автозапуска»); empty = this running copy
  public string PreviousCopy="";               // the exe the autostart entry named before it was moved here; offered for deletion, never deleted on its own
  public string Profile=ProfileCatalog.BuiltIn.Default;
  public string CustomName="";
  public string CustomServers="";
  public string CustomTemplate="";
  public int WinX=int.MinValue,WinY=int.MinValue;   // last window position (screen pixels); restored only if it is still on a screen
  // Where the addresses of the last successful connect came from: "pinned" or "found" (bootstrap) at LastAddrTime (UTC, round-trip format).
  public string LastAddrSource="",LastAddrTime="",LastAddrServers="",LastAddrTemplate="";
  // up to four addresses per profile that an earlier bootstrap found, with the date; tried after the pins and before a new bootstrap.
  public List<SavedAddr> SavedAddrs=new List<SavedAddr>();
  // v1.3: the Top-3 working profiles of the last measurement (checked first on a switch) and the opt-in offer of a working provider after a confirmed failure
  public List<PoolEntry> Pool=new List<PoolEntry>();
  public int SettingsVersion=SettingsRecovery.CurrentVersion;   // a file from a newer program is not read (see SettingsRecovery)
  public bool AutoFailover;   // off by default: a confirmed provider failure is offered, never switched by itself
  static string FilePath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),AppIdentity.StorageName,"settings.json");
  // a file that cannot be used (broken, wrong types, a newer version) gives the defaults; the file is kept as a dated copy and the reason is noted in LoadNote
  public static string LoadNote;
  public static AppSettings Load(){
   try{
    if(File.Exists(FilePath)){
     var parsed=SettingsRecovery.Parse(File.ReadAllText(FilePath));
     if(parsed.Settings!=null)return parsed.Settings;
     SettingsRecovery.Quarantine(FilePath,parsed.Reason,DateTime.UtcNow);
     LoadNote=parsed.Reason;
    }
   }catch{LoadNote="READ_FAILED";}
   return new AppSettings();
  }
  public const string CustomProfileId="__custom__";
  public int ProfileIdentityVersion;
  [System.Text.Json.Serialization.JsonIgnore]public string LastSaveError {get;private set;}
  // Old files used the display name as identity, including names colliding with built-ins.
  // Preserve that user's endpoint on migration; subsequent selections use an explicit ID.
  // A built-in that left the catalog (retired providers such as xbox-dns.ru) or any unknown name selects the default; the own profile's fields stay as they are.
  public void NormalizeProfileIdentity(){
   if(ProfileIdentityVersion<1){if(!string.IsNullOrWhiteSpace(CustomTemplate)&&string.Equals((Profile??"").Trim(),(CustomName??"").Trim(),StringComparison.OrdinalIgnoreCase))Profile=CustomProfileId;ProfileIdentityVersion=1;}
   if(Profile!=CustomProfileId&&ProfileCatalog.BuiltIn.Find(Profile??"")==null)Profile=ProfileCatalog.BuiltIn.Default;
  }
  public bool Save()=>TrySaveTo(FilePath);
  internal bool TrySaveTo(string path){
   try{SaveTo(path);LastSaveError=null;return true;}
   catch(Exception e) when(e is IOException||e is UnauthorizedAccessException||e is System.Security.SecurityException){LastSaveError=e.GetType().Name;return false;}
  }
  internal void SaveChecked()=>SaveTo(FilePath);
  internal void SaveTo(string path){
   if(NoSave)return;
   Directory.CreateDirectory(Path.GetDirectoryName(path));
   string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{File.WriteAllText(tmp,JsonSerializer.Serialize(this,Program.JsonOptions));
    if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);
   }finally{try{if(File.Exists(tmp))File.Delete(tmp);}catch{}}
  }
 }


 // A paint that fails does not stop the program: the control stays as it was and the failure is written down (a short file next to the settings), so the cause can be read later.
 internal static class UiDiag {
  public static bool Enabled;   // on in the real window only; the self-test and the render run write nothing
  static int written;
  static string FilePath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),AppIdentity.StorageName,"ui-errors.log");
  public static void Thread(Exception e){Write("window",e);}
  public static void Paint(string who,Exception e)=>Write("paint of "+who,e);
  static void Write(string who,Exception e){
   if(!Enabled||written>=20)return;
   try{
    written++;Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
    var fi=new FileInfo(FilePath);if(fi.Exists&&fi.Length>64*1024)return;
    File.AppendAllText(FilePath,DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+"  "+who+" failed: "+e.ToString()+Environment.NewLine+Environment.NewLine);
   }catch{}
  }
 }

 internal static class Theme {
  public const int ContentInset=20,SectionGap=16,FieldHeight=42,SurfaceRadius=16;
  public const int HoverMs=140,PressMs=90,ColorMs=220,StatusMs=260;
  public static readonly Color Bg=Color.FromArgb(9,9,10),Card=Color.FromArgb(24,24,27),Field=Color.FromArgb(13,13,15),Line=Color.FromArgb(47,47,53),
   Text=Color.FromArgb(240,240,244),Muted=Color.FromArgb(155,155,164),Accent=Color.FromArgb(67,115,242),AccentHover=Color.FromArgb(133,169,255),
   Orange=Color.FromArgb(255,138,61),Green=Color.FromArgb(82,224,138),Danger=Color.FromArgb(176,62,58),Soft=Color.FromArgb(37,37,42),NavSel=Color.FromArgb(38,56,88),
   Red=Color.FromArgb(240,98,92),Amber=Color.FromArgb(240,190,70);
  // GreenSolid carries white bold text (contrast 5:1); Green stays the bright status colour.
  public static readonly Color GreenSolid=Color.FromArgb(24,128,68),GreenPressed=Color.FromArgb(18,104,55);
  // AmberSolid: the emergency «Починить»; it carries dark text (contrast 8:1).
  public static readonly Color AmberSolid=Color.FromArgb(214,160,40),AmberPressed=Color.FromArgb(184,136,30);
  public static readonly Color AccentPressed=Color.FromArgb(47,95,214),Divider=Color.FromArgb(26,26,29),
   BorderStrong=Color.FromArgb(68,68,76),Focus=Color.FromArgb(133,169,255),Disabled=Color.FromArgb(29,29,33);
  public static void FocusRing(Graphics g,Control control,float radius=10){
   if(!control.Focused)return;
   float k=control.DeviceDpi/96f;
   using(var p=Round(new RectangleF(2*k,2*k,control.Width-5*k,control.Height-5*k),radius*k))
   using(var pen=new Pen(Focus,2*k))g.DrawPath(pen,p);
  }
  public static GraphicsPath Round(RectangleF r,float rad){
   var p=new GraphicsPath();float d=Math.Min(rad*2,Math.Min(r.Width,r.Height));
   if(d<=0){p.AddRectangle(r);return p;}
   p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
  }
  // A rectangle rounded only on the chosen sides: the pieces of one continuous band (a run of segments) share square inner edges.
  public static GraphicsPath RoundSides(RectangleF r,float rad,bool left,bool right){
   var p=new GraphicsPath();float d=Math.Min(rad*2,Math.Min(r.Width,r.Height));
   if(d<=0||!left&&!right){p.AddRectangle(r);return p;}
   if(left){p.AddArc(r.X,r.Y,d,d,180,90);}else p.AddLine(r.X,r.Y,r.X,r.Y);
   if(right){p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);}else{p.AddLine(r.Right,r.Y,r.Right,r.Bottom);}
   if(left){p.AddArc(r.X,r.Bottom-d,d,d,90,90);}else p.AddLine(r.X,r.Bottom,r.X,r.Bottom);
   p.CloseFigure();return p;
  }
  public static Color Mix(Color a,Color b,float t)=>Color.FromArgb((int)(a.R+(b.R-a.R)*t),(int)(a.G+(b.G-a.G)*t),(int)(a.B+(b.B-a.B)*t));
  static readonly HashSet<string> installed=LoadFamilies();
  static HashSet<string> LoadFamilies(){try{using(var c=new System.Drawing.Text.InstalledFontCollection())return new HashSet<string>(c.Families.Select(f=>f.Name),StringComparer.OrdinalIgnoreCase);}catch{return new HashSet<string>();}}
  public static bool Has(string family)=>installed.Contains(family);
  // Windows 11 ships Segoe UI Variable: its Display cut for large type, Text for the rest. Windows 10 falls back to Segoe UI.
  public static string Family(float pt,bool bold)=>Has("Segoe UI Variable Text")&&Has("Segoe UI Variable Display Semib")
   ?(pt>=14?(bold?"Segoe UI Variable Display Semib":"Segoe UI Variable Display"):(bold?"Segoe UI Variable Text Semibold":"Segoe UI Variable Text"))
   :(bold?"Segoe UI Semibold":"Segoe UI");
  public static Font F(float pt,bool bold=false)=>new Font(Family(pt,bold),pt);
  // The largest size (down to minPt) at which the text fits the width; the caller disposes the font. A text that cannot fit even then keeps the smallest size and is ellipsised by its painter.
  public static Font Fit(Graphics g,string text,float pt,float minPt,int width,bool bold=false){
   for(float size=pt;size>minPt;size-=.25f){var f=F(size,bold);if(string.IsNullOrEmpty(text)||TextRenderer.MeasureText(g,text,f,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width<=width)return f;f.Dispose();}
   return F(minPt,bold);
  }
  public static readonly string IconFont=Has("Segoe Fluent Icons")?"Segoe Fluent Icons":"Segoe MDL2 Assets";
  public static readonly string MonoFont=Has("Cascadia Mono")?"Cascadia Mono":"Consolas";
 }

 // One scale for good / so-so / bad for every measured figure: the benchmark table and the profile card colour their numbers by these limits.
 internal static class Latency {
  public const long GoodBelowMs=300,SlowFromMs=800;   // DoH median, peak and spread: green below 300 ms, amber up to 800 ms, red from there (a median above 700 ms is also the «Медленно» verdict)
  public const double AvailabilityGood=0.9,AvailabilityOk=0.6;
  public static Color Of(long ms)=>ms<0?Theme.Muted:ms<GoodBelowMs?Theme.Green:ms<SlowFromMs?Theme.Amber:Theme.Red;
  public static Color OfTls(long ms)=>ms<0?Theme.Muted:Of(ms/2);   // a TLS handshake takes about two round trips: its limits are twice as wide
  public static Color OfAvailability(double a)=>a>=AvailabilityGood?Theme.Green:a>=AvailabilityOk?Theme.Amber:Theme.Red;
 }

 // One UI-thread clock for every small transition (hover, toggle, navigation, logo). It runs only while something moves.
 // Off in smoke/self-test renders (instant states) and when Windows "Show animations" is off.
 internal static class Motion {
  public static bool Enabled=true;
  static System.Windows.Forms.Timer timer;
  static readonly List<Func<bool>> steps=new List<Func<bool>>();
  public static void Run(Func<bool> step){
   if(timer==null){timer=new System.Windows.Forms.Timer{Interval=15};timer.Tick+=(_,__)=>{for(int i=steps.Count-1;i>=0;i--){bool more;try{more=steps[i]();}catch{more=false;}if(!more)steps.RemoveAt(i);}if(steps.Count==0)timer.Stop();};}
   if(!steps.Contains(step))steps.Add(step);
   if(!timer.Enabled)timer.Start();
  }
  public static float Ease(float t)=>1f-(1f-t)*(1f-t)*(1f-t);   // ease-out cubic
 }
 internal sealed class Anim {
  readonly Control owner;readonly int ms;float from,to,val;long start;bool running;
  public Anim(Control owner,float initial,int ms){this.owner=owner;val=from=to=initial;this.ms=ms;}
  public float Value=>val;
  public float Target=>to;
  public void To(float target,bool instant=false){
   if(instant||!Motion.Enabled||!owner.IsHandleCreated||!owner.Visible){running=false;val=from=to=target;owner.Invalidate();return;}
   if(target==to)return;
   from=val;to=target;start=Stopwatch.GetTimestamp();
   if(!running){running=true;Motion.Run(Step);}
  }
  bool Step(){
   if(owner.IsDisposed){running=false;return false;}
   float t=(float)((Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency/ms);
   if(t>=1f){val=to;running=false;owner.Invalidate();return false;}
   val=from+(to-from)*Motion.Ease(t);owner.Invalidate();return true;
  }
 }

 public static class Branding {
  [DllImport("user32.dll")]static extern bool DestroyIcon(IntPtr h);
  public static void DrawLogo(Graphics g,float x,float y,float s,bool active)=>DrawLogo(g,x,y,s,active?1f:0f,-1f);
  // act: 0 = off (grey) .. 1 = on (colour). orbit >= 0: angle of the satellite that runs along the orbit while an operation is in progress.
  // px: the size the mark is finally shown at (a supersampled render draws at s = px * factor); stroke weights follow px so small icons stay legible.
  public static void DrawLogo(Graphics g,float x,float y,float s,float act,float orbit,float px=0,bool tileVisible=true){
   if(s<=0)return;if(px<=0)px=s;
   var saved=g.Save();g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
   act=Math.Clamp(act,0f,1f);
   var tile=new RectangleF(x+s*.03f,y+s*.03f,s*.94f,s*.94f);
   if(tileVisible)using(var path=Theme.Round(tile,s*.206f))
   using(var fill=new LinearGradientBrush(tile,Color.FromArgb(36,41,66),Color.FromArgb(8,9,15),90f)){
    g.FillPath(fill,path);using(var pen=new Pen(Color.FromArgb(55,68,79),Math.Max(.6f,s/256)))g.DrawPath(pen,path);
   }
   // Exact polygon geometry from the visual-brand SVG, in its 224-unit construction space.
   g.TranslateTransform(x+s/2,y+s/2);g.ScaleTransform(s*.8f/256,s*.8f/256);g.TranslateTransform(-112,-100);
   bool micro=px<=20;
   using(var mark=new GraphicsPath()){
    mark.AddPolygon(micro?new[]{new PointF(92,22),new PointF(116,22),new PointF(56,178),new PointF(34,178)}:new[]{new PointF(96,22),new PointF(108,22),new PointF(52,178),new PointF(38,178)});
    mark.AddPolygon(micro?new[]{new PointF(104,22),new PointF(132,22),new PointF(194,178),new PointF(150,178)}:new[]{new PointF(102,22),new PointF(122,22),new PointF(184,178),new PointF(158,178)});
    using(var ring=new GraphicsPath()){
     ring.AddEllipse(-92,-34,184,68);
     using(var matrix=new Matrix()){matrix.Rotate(16);matrix.Translate(112,112,MatrixOrder.Append);ring.Transform(matrix);}
     using(var gradient=new LinearGradientBrush(new PointF(23.56f,86.64f),new PointF(200.44f,137.36f),Theme.Mix(Color.FromArgb(112,120,140),Color.FromArgb(84,152,255),act),Theme.Mix(Color.FromArgb(144,149,160),Theme.Orange,act)))
     using(var pen=new Pen(gradient,13)){
      if(!micro){
       var clip=g.Save();using(var exclusion=(GraphicsPath)mark.Clone())using(var gap=new Pen(Color.Black,8)){exclusion.Widen(gap);g.SetClip(exclusion,CombineMode.Exclude);g.DrawPath(pen,ring);}g.Restore(clip);
      }
      using(var ink=new SolidBrush(Theme.Mix(Color.FromArgb(168,174,188),Color.White,act)))g.FillPath(ink,mark);
      if(micro){using(var ink=new SolidBrush(Theme.Mix(Color.FromArgb(168,174,188),Color.White,act)))g.FillRectangle(ink,66,110,86,13);}
      else{
       // Front half, including the dark separation around the interwoven orbit.
       var front=g.Save();g.TranslateTransform(112,112);g.RotateTransform(16);
       using(var gap=new Pen(Color.FromArgb(12,14,22),21))g.DrawArc(gap,-92,-34,184,68,0,180);
       g.Restore(front);
       using(var arc=new GraphicsPath()){
        arc.AddArc(-92,-34,184,68,0,180);using(var matrix=new Matrix()){matrix.Rotate(16);matrix.Translate(112,112,MatrixOrder.Append);arc.Transform(matrix);}g.DrawPath(pen,arc);
       }
       if(orbit>=0){double a=orbit;float ox=(float)(92*Math.Cos(a)),oy=(float)(34*Math.Sin(a));var moving=g.Save();g.TranslateTransform(112,112);g.RotateTransform(16);using(var dot=new SolidBrush(Theme.Orange))g.FillEllipse(dot,ox-7,oy-7,14,14);g.Restore(moving);}
      }
     }
    }
   }
   g.Restore(saved);
  }

  // The Telegram mark for the contact rows: a blue disc with the white paper plane.
  public static void DrawTelegram(Graphics g,RectangleF r){
   var old=g.SmoothingMode;g.SmoothingMode=SmoothingMode.AntiAlias;
   using(var br=new LinearGradientBrush(r,Color.FromArgb(55,174,226),Color.FromArgb(30,150,200),LinearGradientMode.Vertical))g.FillEllipse(br,r);
   PointF P(float x,float y)=>new PointF(r.X+r.Width*x,r.Y+r.Height*y);
   using(var plane=new GraphicsPath()){
    plane.AddPolygon(new[]{P(.225f,.49f),P(.735f,.285f),P(.655f,.715f),P(.505f,.6f),P(.425f,.675f),P(.415f,.55f),P(.66f,.355f),P(.37f,.525f)});
    using(var w=new SolidBrush(Color.White))g.FillPath(w,plane);
   }
   using(var fold=new SolidBrush(Color.FromArgb(200,222,240)))g.FillPolygon(fold,new[]{P(.415f,.55f),P(.425f,.675f),P(.505f,.6f)});
   g.SmoothingMode=old;
  }
  // Small sizes are drawn four times larger and reduced with a bicubic filter: thin strokes and the round ends come out clean instead of stair-stepped.
  public static Bitmap Render(int size,bool active)=>Render(size,active?1f:0f,-1f);
  public static Bitmap Render(int size,float act,float orbit){
   int f=size<=64?4:size<=160?2:1;
   var b=new Bitmap(size,size,PixelFormat.Format32bppArgb);
   using(var big=new Bitmap(size*f,size*f,PixelFormat.Format32bppPArgb)){
    using(var g=Graphics.FromImage(big)){g.Clear(Color.Transparent);DrawLogo(g,0,0,size*f,act,orbit,size);}
    using(var g=Graphics.FromImage(b)){g.Clear(Color.Transparent);g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.CompositingQuality=CompositingQuality.HighQuality;
     using(var ia=new ImageAttributes()){ia.SetWrapMode(WrapMode.TileFlipXY);g.DrawImage(big,new Rectangle(0,0,size,size),0,0,big.Width,big.Height,GraphicsUnit.Pixel,ia);}}
   }
   return b;
  }
  public static Icon ToIcon(int size,bool active){
   using(var b=Render(size,active)){IntPtr h=b.GetHicon();try{return (Icon)Icon.FromHandle(h).Clone();}finally{DestroyIcon(h);}}
  }
  // Every size Windows asks for at 100-200 % scaling (title bar, taskbar, Alt+Tab, Explorer), so it never has to stretch a neighbour.
  public static readonly int[] IconSizes={16,20,24,32,40,48,64,256};
  // The window icon: one Icon with all sizes, Windows picks the right one for each place.
  public static Icon IconSet(bool active){using(var ms=new MemoryStream(IcoBytes(active)))return new Icon(ms);}
  // ICO file: 32-bit DIB frames up to 64 px (read by every Windows API), PNG for 256 px.
  public static byte[] IcoBytes(bool active=true){
   var frames=new List<byte[]>();
   foreach(int sz in IconSizes)using(var b=Render(sz,active)){
    if(sz>=256){using(var ms=new MemoryStream()){b.Save(ms,ImageFormat.Png);frames.Add(ms.ToArray());}continue;}
    frames.Add(Dib(b));
   }
   using(var o=new MemoryStream())using(var w=new BinaryWriter(o)){
    w.Write((short)0);w.Write((short)1);w.Write((short)IconSizes.Length);int offset=6+16*IconSizes.Length;
    for(int i=0;i<IconSizes.Length;i++){int sz=IconSizes[i];w.Write((byte)(sz>=256?0:sz));w.Write((byte)(sz>=256?0:sz));w.Write((byte)0);w.Write((byte)0);w.Write((short)1);w.Write((short)32);w.Write(frames[i].Length);w.Write(offset);offset+=frames[i].Length;}
    foreach(var p in frames)w.Write(p);
    w.Flush();return o.ToArray();
   }
  }
  static byte[] Dib(Bitmap b){
   int n=b.Width,maskStride=(n+31)/32*4;
   using(var o=new MemoryStream())using(var w=new BinaryWriter(o)){
    w.Write(40);w.Write(n);w.Write(n*2);w.Write((short)1);w.Write((short)32);w.Write(0);w.Write(n*n*4+maskStride*n);w.Write(0);w.Write(0);w.Write(0);w.Write(0);
    var data=b.LockBits(new Rectangle(0,0,n,n),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
    try{var row=new byte[n*4];for(int yy=n-1;yy>=0;yy--){Marshal.Copy(IntPtr.Add(data.Scan0,yy*data.Stride),row,0,row.Length);w.Write(row);}}
    finally{b.UnlockBits(data);}
    w.Write(new byte[maskStride*n]);   // AND mask unused: the alpha channel carries the shape
    w.Flush();return o.ToArray();
   }
  }
 }

 internal sealed class DarkColors:ProfessionalColorTable{
  public override Color ToolStripDropDownBackground=>Theme.Card;
  public override Color ImageMarginGradientBegin=>Theme.Card;
  public override Color ImageMarginGradientMiddle=>Theme.Card;
  public override Color ImageMarginGradientEnd=>Theme.Card;
  public override Color MenuBorder=>Theme.Line;
  public override Color MenuItemBorder=>Theme.NavSel;
  public override Color MenuItemSelected=>Theme.Soft;
  public override Color MenuItemSelectedGradientBegin=>Theme.Soft;
  public override Color MenuItemSelectedGradientEnd=>Theme.Soft;
  public override Color SeparatorDark=>Theme.Line;
  public override Color SeparatorLight=>Theme.Line;
 }

 internal class Surface:Control{
  protected Surface(){SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.UserPaint|ControlStyles.SupportsTransparentBackColor|ControlStyles.Selectable,true);BackColor=Color.Transparent;}
  protected override void OnGotFocus(EventArgs e){Invalidate();base.OnGotFocus(e);}
  protected override void OnLostFocus(EventArgs e){Invalidate();base.OnLostFocus(e);}
  protected override void OnMouseDown(MouseEventArgs e){if(CanSelect)Focus();base.OnMouseDown(e);}
  protected float K=>(FindForm()?.DeviceDpi??DeviceDpi)/96f;
 }

 internal sealed class RoundPanel:Panel{
  public Color Fill=Theme.Card;public float Radius=Theme.SurfaceRadius;public Color Border=Theme.Line;
  public RoundPanel(){SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.UserPaint|ControlStyles.SupportsTransparentBackColor,true);BackColor=Color.Transparent;}
  protected override void OnPaint(PaintEventArgs e){
   var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
   using(var p=Theme.Round(new RectangleF(0,0,Width-1,Height-1),Radius*DeviceDpi/96f))using(var b=new SolidBrush(Fill)){g.FillPath(b,p);if(Border.A>0)using(var pen=new Pen(Border))g.DrawPath(pen,p);}
  }
 }

 internal sealed class LogoBox:Surface{
  readonly Anim act;bool spinning;float angle;long last;
  Timer ambientTimer;bool ambient;long pulseStart;float pulse=-1;
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public bool Ambient{get=>ambient;set{ambient=value;RefreshMotion();}}
  internal bool AmbientRunning=>ambientTimer?.Enabled==true;
  internal void RefreshMotion(){
   if(!ambient||!Motion.Enabled||!SystemInformation.UIEffectsEnabled||!IsHandleCreated||!Visible||spinning||FindForm()?.WindowState==FormWindowState.Minimized){ambientTimer?.Stop();pulse=-1;return;}
   if(ambientTimer==null){ambientTimer=new Timer();ambientTimer.Tick+=(_,__)=>AmbientTick();}
   if(!ambientTimer.Enabled){pulse=-1;ambientTimer.Interval=6500;ambientTimer.Start();}
  }
  void AmbientTick(){
   if(IsDisposed||!Visible||!Motion.Enabled||!SystemInformation.UIEffectsEnabled||spinning||FindForm()?.WindowState==FormWindowState.Minimized){ambientTimer.Stop();pulse=-1;return;}
   if(pulse<0){pulseStart=Stopwatch.GetTimestamp();pulse=0;ambientTimer.Interval=33;}
   else{pulse=(float)((Stopwatch.GetTimestamp()-pulseStart)*1000d/Stopwatch.Frequency/1400);if(pulse>=1){pulse=-1;ambientTimer.Interval=6500;}}
   Invalidate();
  }
  protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);RefreshMotion();if(ambientTimer?.Enabled==true){pulse=0;pulseStart=Stopwatch.GetTimestamp();ambientTimer.Interval=33;Invalidate();}}
  protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);RefreshMotion();}
  protected override void Dispose(bool disposing){if(disposing)ambientTimer?.Dispose();base.Dispose(disposing);}
  public LogoBox(){act=new Anim(this,0f,320);}
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public bool Active{get=>act.Target>0.5f;set=>act.To(value?1f:0f);}
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public bool Spinning{get=>spinning;set{if(spinning==value)return;spinning=value;if(value&&Motion.Enabled){last=Stopwatch.GetTimestamp();Motion.Run(Spin);}RefreshMotion();Invalidate();}}
  bool Spin(){
   if(IsDisposed||!spinning||!Visible||FindForm()?.WindowState==FormWindowState.Minimized){Invalidate();return false;}
   long now=Stopwatch.GetTimestamp();angle+=(float)((now-last)*Math.PI*2/1.5/Stopwatch.Frequency);last=now;
   if(angle>Math.PI*2)angle-=(float)(Math.PI*2);
   Invalidate();return true;
  }
  protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);RefreshMotion();if(Visible&&spinning&&Motion.Enabled){last=Stopwatch.GetTimestamp();Motion.Run(Spin);}}
  protected override void OnPaint(PaintEventArgs e){
   int sz=Math.Min(Width,Height);float o=spinning?(Motion.Enabled?angle:2.3f):pulse>=0?pulse*(float)Math.PI*2:-1f;
   if(sz>=80){float lift=pulse<0?0:-(float)Math.Sin(pulse*Math.PI)*1.2f*K;Branding.DrawLogo(e.Graphics,0,lift,sz,act.Value,o);return;}
   // small marks (header, info card) go through the supersampled render, drawn 1:1
   using(var b=Branding.Render(sz,act.Value,o)){var g=e.Graphics;g.InterpolationMode=InterpolationMode.NearestNeighbor;g.PixelOffsetMode=PixelOffsetMode.Half;g.DrawImage(b,new Rectangle(0,0,sz,sz));}
  }
 }

 internal sealed class PillButton:Surface{
  public bool OrbitToggle;public bool OrbitBusy;public Color OrbitInk=Theme.Muted;
  Color orbitFrom=Theme.Muted;Anim orbitFade;
  float orbitAngle;long orbitTick;bool orbitRunning;
  public void SetOrbit(bool waiting,Color ink){
   if(orbitFade==null)orbitFade=new Anim(this,1f,Theme.StatusMs);
   if(OrbitInk!=ink){orbitFrom=Theme.Mix(orbitFrom,OrbitInk,orbitFade.Value);OrbitInk=ink;orbitFade.To(0,true);orbitFade.To(1);}
   OrbitBusy=waiting;
   if(waiting&&!orbitRunning&&Motion.Enabled){orbitRunning=true;orbitTick=Stopwatch.GetTimestamp();Motion.Run(OrbitStep);}
   Invalidate();
  }
  bool OrbitStep(){
   if(IsDisposed||!OrbitBusy||!Visible||FindForm()?.WindowState==FormWindowState.Minimized){orbitRunning=false;return false;}
   long now=Stopwatch.GetTimestamp();orbitAngle+=(float)((now-orbitTick)*240d/Stopwatch.Frequency);orbitTick=now;Invalidate();return true;
  }
  protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);if(Visible&&OrbitBusy)SetOrbit(true,OrbitInk);}
  protected override void OnSizeChanged(EventArgs e){
   base.OnSizeChanged(e);
   if(!OrbitToggle||Width<=0||Height<=0)return;
   using(var path=new GraphicsPath()){
    float diameter=Math.Min(Width,Height)-1;
    path.AddEllipse((Width-diameter)/2,(Height-diameter)/2,diameter,diameter);
    var previous=Region;Region=new Region(path);previous?.Dispose();
   }
  }
  public Color Fill=Theme.Accent;Color fromFill=Theme.Accent;readonly Anim hoverA,downA,fillA;
  public PillButton(){Cursor=Cursors.Hand;Font=Theme.F(10,true);ForeColor=Color.White;hoverA=new Anim(this,0f,Theme.HoverMs);downA=new Anim(this,0f,Theme.PressMs);fillA=new Anim(this,1f,Theme.ColorMs);}
  // The label is kept here, not in the native window text: a button is only painted, and the native text of a window created late came back empty.
  string label="";
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public override string Text{get=>label;set{value=value??"";if(label==value)return;label=value;AccessibleName=value;OnTextChanged(EventArgs.Empty);}}
  Color CurrentFill=>Theme.Mix(fromFill,Fill,fillA.Value);
  public void SetFill(Color c){if(c==Fill)return;fromFill=CurrentFill;Fill=c;fillA.To(0f,true);fillA.To(1f);}
  protected override void OnMouseEnter(EventArgs e){hoverA.To(1f);base.OnMouseEnter(e);}
  protected override void OnMouseLeave(EventArgs e){hoverA.To(0f);downA.To(0f);base.OnMouseLeave(e);}
  protected override void OnMouseDown(MouseEventArgs e){downA.To(1f,!OrbitToggle);base.OnMouseDown(e);}
  protected override void OnMouseUp(MouseEventArgs e){downA.To(0f);base.OnMouseUp(e);}
  protected override void OnEnabledChanged(EventArgs e){Invalidate();base.OnEnabledChanged(e);}
  protected override void OnTextChanged(EventArgs e){AccessibleName=Text;Invalidate();base.OnTextChanged(e);}
  protected override bool IsInputKey(Keys k)=>k==Keys.Space||k==Keys.Enter||base.IsInputKey(k);
  protected override void OnKeyDown(KeyEventArgs e){if(OrbitToggle&&(e.KeyCode==Keys.Space||e.KeyCode==Keys.Enter))downA.To(1f);base.OnKeyDown(e);}
  protected override void OnKeyUp(KeyEventArgs e){if(e.KeyCode==Keys.Space||e.KeyCode==Keys.Enter){downA.To(0f);if(Enabled)OnClick(EventArgs.Empty);}base.OnKeyUp(e);}
  // Glyph: an icon-font character drawn before the text. Subtle: a quiet tinted button (dark fill, hairline border, coloured text) for an action
  // that is available but should not shout, e.g. the red "Отключить мост" while everything works.
  string glyph;bool subtle;
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public string Glyph{get=>glyph;set{if(glyph!=value){glyph=value;Invalidate();}}}
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public bool Subtle{get=>subtle;set{if(subtle!=value){subtle=value;Invalidate();}}}
  const TextFormatFlags One=TextFormatFlags.SingleLine|TextFormatFlags.NoPadding;
  Font GlyphFont()=>new Font(Theme.IconFont,Font.SizeInPoints*.98f);
  // Width the content needs in device pixels (text, glyph, side padding); used to size the centred main button.
  public int PreferredWidth(){
   int w=TextRenderer.MeasureText(Text??"",Font,Size.Empty,One).Width;
   if(!string.IsNullOrEmpty(Glyph))using(var gf=GlyphFont())w+=TextRenderer.MeasureText(Glyph,gf,Size.Empty,One).Width+(int)(9*K);
   return w+(int)(52*K);
  }
  internal void PaintForSnapshot(Graphics g)=>OnPaint(new PaintEventArgs(g,new Rectangle(0,0,Width,Height)));
  internal void PerformClickForTest(){if(Enabled&&Visible)OnClick(EventArgs.Empty);}
  protected override void OnPaint(PaintEventArgs e){
   var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
   if(OrbitToggle){
    float size=Math.Min(Width,Height)-10*K,press=3*K*downA.Value;
    var circle=new RectangleF((Width-size)/2+press,(Height-size)/2+press,size-2*press,size-2*press);
    using(var fill=new SolidBrush(Theme.Mix(Theme.Card,Theme.Soft,hoverA.Value)))g.FillEllipse(fill,circle);
    var circleInk=Theme.Mix(Theme.Mix(orbitFrom,OrbitInk,orbitFade?.Value??1f),Theme.Text,.22f*hoverA.Value);
    using(var pen=new Pen(Theme.Mix(Theme.Line,circleInk,.5f),2*K))g.DrawEllipse(pen,circle);
    using(var pen=new Pen(circleInk,3*K){StartCap=LineCap.Round,EndCap=LineCap.Round}){
     if(OrbitBusy)g.DrawArc(pen,circle,orbitAngle-90,95);else g.DrawEllipse(pen,circle);
    }
    Branding.DrawLogo(g,circle.X+size*.18f,circle.Y+size*.18f,circle.Width*.64f,OrbitInk==Theme.Green?1f:0f,-1,0,false);
    if(Focused&&ShowFocusCues)using(var pen=new Pen(Theme.Focus,2*K)){circle.Inflate(4*K,4*K);g.DrawEllipse(pen,circle);}
    return;
   }
   Color b=CurrentFill,ink=ForeColor,edge=Color.Empty;
   Color c;
   if(subtle){c=Theme.Mix(Theme.Mix(b,Theme.Bg,.8f),b,.16f*hoverA.Value);edge=Theme.Mix(b,Theme.Bg,.42f-.18f*hoverA.Value);ink=Theme.Mix(b,Color.White,.52f);c=Theme.Mix(c,Color.Black,.15f*downA.Value);}
   else c=Theme.Mix(Theme.Mix(b,b==Theme.Accent?Theme.AccentPressed:b==Theme.GreenSolid?Theme.GreenPressed:b==Theme.AmberSolid?Theme.AmberPressed:Theme.BorderStrong,.6f*hoverA.Value),Color.Black,.15f*downA.Value);
   if(!Enabled){c=Theme.Disabled;ink=Theme.Muted;if(subtle)edge=Theme.Mix(b,Theme.Bg,.5f);}
   using(var p=Theme.Round(new RectangleF(.5f,.5f,Width-2,Height-2),Math.Min(Height/2f,24*K))){
    if(subtle&&Enabled&&Height>2){   // a soft glow of the button's own colour: a gentle vertical gradient and a faint inner halo along the edge (no outside effect, nothing heavy)
     using(var br=new LinearGradientBrush(new RectangleF(0,0,Width,Height),Theme.Mix(c,b,.30f+.12f*hoverA.Value),Theme.Mix(c,Theme.Bg,.10f),90f))g.FillPath(br,p);
    }else using(var br=new SolidBrush(c))g.FillPath(br,p);
    if(edge!=Color.Empty)using(var pen=new Pen(edge,1f))g.DrawPath(pen,p);
    if(subtle&&Enabled){
     var saved=g.Save();g.SetClip(p,CombineMode.Intersect);
     using(var halo=Theme.Round(new RectangleF(.5f,.5f,Width-2,Height-2),Math.Min(Height/2f,24*K)))
     using(var pen=new Pen(Color.FromArgb((int)(34+26*hoverA.Value),b),5*K))g.DrawPath(pen,halo);
     g.Restore(saved);
    }
   }
   if(ShowFocusCues)Theme.FocusRing(g,this,12);
   var flags=TextFormatFlags.VerticalCenter|One;
   if(string.IsNullOrEmpty(Glyph)){TextRenderer.DrawText(g,Text,Font,new Rectangle(0,0,Width,Height),ink,flags|TextFormatFlags.HorizontalCenter|TextFormatFlags.EndEllipsis);return;}
   // glyph and text are centred together as one group
   using(var gf=GlyphFont()){
    int tw=TextRenderer.MeasureText(g,Text??"",Font,Size.Empty,One).Width,gw=TextRenderer.MeasureText(g,Glyph,gf,Size.Empty,One).Width,gap=(int)(9*K);
    int total=gw+gap+tw,x0=Math.Max((int)(12*K),(Width-total)/2);
    TextRenderer.DrawText(g,Glyph,gf,new Rectangle(x0,0,gw,Height),ink,flags);
    TextRenderer.DrawText(g,Text,Font,new Rectangle(x0+gw+gap,0,Math.Max(0,Width-(x0+gw+gap)-(int)(12*K)),Height),ink,flags|TextFormatFlags.EndEllipsis);
   }
  }
 }

 internal sealed class ToggleRow:Surface{
  bool on;readonly Anim onA;string sub;IconKind icon;bool divider;Color subColor=Theme.Muted;
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public bool Checked{get=>on;set{if(on!=value){on=value;onA.To(on?1f:0f);AccessibilityNotifyClients(AccessibleEvents.StateChange,-1);CheckedChanged?.Invoke(this,EventArgs.Empty);}}}
  // Optional second line (what the switch does), an icon on the left and a hairline under the row (rows inside one card).
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public string Subtitle{get=>sub;set{if(sub!=value){sub=value;Invalidate();}}}
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public Color SubtitleColor{get=>subColor;set{if(subColor!=value){subColor=value;Invalidate();}}}
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public IconKind Icon{get=>icon;set{icon=value;Invalidate();}}
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public bool Divider{get=>divider;set{divider=value;Invalidate();}}
  public event EventHandler CheckedChanged;
  protected override AccessibleObject CreateAccessibilityInstance()=>new ToggleAccessible(this);
  sealed class ToggleAccessible:ControlAccessibleObject{
   readonly ToggleRow owner;public ToggleAccessible(ToggleRow c):base(c){owner=c;}
   public override AccessibleStates State=>base.State|(owner.Checked?AccessibleStates.Checked:AccessibleStates.None);
   public override string Value {get=>owner.Checked?"On":"Off";set{}}
   public override string DefaultAction=>L.En?"Toggle":"Переключить";
   public override void DoDefaultAction(){if(owner.Enabled)owner.Checked=!owner.Checked;}
  }
  public ToggleRow(){AccessibleRole=AccessibleRole.CheckButton;Cursor=Cursors.Hand;Font=Theme.F(9);Height=40;onA=new Anim(this,0f,160);}
  protected override void OnClick(EventArgs e){if(Enabled)Checked=!Checked;base.OnClick(e);}
  protected override void OnEnabledChanged(EventArgs e){Invalidate();base.OnEnabledChanged(e);}
  protected override void OnTextChanged(EventArgs e){AccessibleName=Text;Invalidate();base.OnTextChanged(e);}
  protected override bool IsInputKey(Keys k)=>k==Keys.Space||base.IsInputKey(k);
  protected override void OnKeyUp(KeyEventArgs e){if(e.KeyCode==Keys.Space&&Enabled)Checked=!Checked;base.OnKeyUp(e);}
  protected override void OnPaint(PaintEventArgs e){
   var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;float k=K,v=onA.Value;
   int left=RowKit.Icon(g,icon,k,Height,Enabled);
   RowKit.Texts(g,Text,sub,Font,new Rectangle(left,0,(int)(Width-left-50*k),Height),Enabled?Theme.Text:Theme.Muted,subColor,k);
   float w=40*k,h=22*k,x=Width-w-2*k,y=(Height-h)/2;
   Color onColor=Enabled?Theme.Accent:Theme.Mix(Theme.Accent,Theme.Bg,.5f);
   using(var p=Theme.Round(new RectangleF(x,y,w,h),h/2))using(var b=new SolidBrush(Theme.Mix(Color.FromArgb(58,58,68),onColor,v)))g.FillPath(b,p);
   float d=h-6*k,kx=x+3*k+(w-d-6*k)*v;using(var b=new SolidBrush(Enabled?Color.White:Color.FromArgb(170,170,176)))g.FillEllipse(b,kx,y+3*k,d,d);
   if(divider)RowKit.Divider(g,left,Width,Height,k);if(ShowFocusCues)Theme.FocusRing(g,this,8);
  }
 }

 internal enum IconKind{None,Power,Tray,Minimize,Bell,Globe,Server,Compass,Download,Shield}

 // Line icons of one family: a 16-unit grid, 1.5 stroke, round caps and joins, about 12 units of ink. Each is drawn symmetrically about the middle of
 // the grid; Nudge only corrects the few shapes that carry more ink on one side (the bell, the power sign), measured by the self-test on the real pixels.
 internal static class Icons{
  public static PointF Nudge(IconKind k){
   switch(k){
    case IconKind.Bell:return new PointF(0f,-.35f);
    case IconKind.Download:return new PointF(0f,-1f);
    default:return PointF.Empty;
   }
  }
  public static void Draw(Graphics g,IconKind kind,RectangleF box,Color ink){
   var saved=g.Save();var sm=g.SmoothingMode;var po=g.PixelOffsetMode;g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.Half;   // Half: coordinates are pixel edges, so a shape centred on the tile is centred on its pixels
   float s=box.Width/16f;var nd=Nudge(kind);
   g.TranslateTransform(box.X,box.Y);g.ScaleTransform(s,s);g.TranslateTransform(nd.X,nd.Y);
   using(var pen=new Pen(ink,1.5f){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round})using(var fill=new SolidBrush(ink)) {
    switch(kind) {
     case IconKind.Power:
      g.DrawArc(pen,new RectangleF(3f,3.4f,10f,10f),-55f,290f);
      g.DrawLine(pen,8f,1.9f,8f,7.4f);
      break;
     case IconKind.Tray:   // a window over its taskbar, with one small icon in the tray corner
      using(var p=Theme.Round(new RectangleF(1.5f,2.5f,13f,11f),2.2f))g.DrawPath(pen,p);
      g.DrawLine(pen,1.5f,10f,14.5f,10f);
      g.FillEllipse(fill,10.6f,11.05f,1.9f,1.9f);
      break;
     case IconKind.Minimize:   // a window with a chevron that folds it away
      using(var p=Theme.Round(new RectangleF(1.5f,2.5f,13f,11f),2.2f))g.DrawPath(pen,p);
      g.DrawLine(pen,1.5f,5.9f,14.5f,5.9f);
      g.DrawLines(pen,new[]{new PointF(5.4f,8.4f),new PointF(8f,11f),new PointF(10.6f,8.4f)});
      break;
     case IconKind.Bell:
      using(var bell=new GraphicsPath()){
       bell.AddBezier(3.2f,11.2f,4.4f,10.2f,4.6f,8.8f,4.6f,6.6f);
       bell.AddBezier(4.6f,6.6f,4.6f,4.4f,6.1f,3.3f,8f,3.3f);
       bell.AddBezier(8f,3.3f,9.9f,3.3f,11.4f,4.4f,11.4f,6.6f);
       bell.AddBezier(11.4f,6.6f,11.4f,8.8f,11.6f,10.2f,12.8f,11.2f);
       bell.CloseFigure();g.DrawPath(pen,bell);
      }
      g.DrawLine(pen,8f,1.7f,8f,3.3f);
      g.DrawArc(pen,new RectangleF(6.2f,10.6f,3.6f,3.2f),0f,180f);
      break;
     case IconKind.Globe:
      g.DrawEllipse(pen,1.8f,1.8f,12.4f,12.4f);
      g.DrawEllipse(pen,5.1f,1.8f,5.8f,12.4f);
      g.DrawLine(pen,1.8f,8f,14.2f,8f);
      break;
     case IconKind.Server:
      using(var a=Theme.Round(new RectangleF(1.7f,2.2f,12.6f,4.6f),1.7f))g.DrawPath(pen,a);
      using(var b=Theme.Round(new RectangleF(1.7f,9.2f,12.6f,4.6f),1.7f))g.DrawPath(pen,b);
      g.FillEllipse(fill,3.7f,3.6f,1.8f,1.8f);g.FillEllipse(fill,3.7f,10.6f,1.8f,1.8f);
      break;
     case IconKind.Compass:
      g.DrawEllipse(pen,1.7f,1.7f,12.6f,12.6f);
      g.FillPolygon(fill,new[]{new PointF(10.9f,5.1f),new PointF(8.9f,9f),new PointF(5.1f,10.9f),new PointF(7.1f,7f)});
      break;
     case IconKind.Shield:   // a shield with a tick: the program runs with administrator rights
      using(var sh=new GraphicsPath()){
       sh.AddBezier(8f,1.8f,10.2f,3.4f,12f,3.6f,13.4f,3.7f);
       sh.AddBezier(13.4f,3.7f,13.4f,9.6f,11.6f,12.4f,8f,14.2f);
       sh.AddBezier(8f,14.2f,4.4f,12.4f,2.6f,9.6f,2.6f,3.7f);
       sh.AddBezier(2.6f,3.7f,4f,3.6f,5.8f,3.4f,8f,1.8f);
       sh.CloseFigure();g.DrawPath(pen,sh);
      }
      g.DrawLines(pen,new[]{new PointF(5.6f,8f),new PointF(7.3f,9.7f),new PointF(10.6f,6.1f)});
      break;
     case IconKind.Download:   // an arrow into a line
      g.DrawLine(pen,8f,2.5f,8f,10.4f);
      g.DrawLines(pen,new[]{new PointF(4.5f,7f),new PointF(8f,10.5f),new PointF(11.5f,7f)});
      g.DrawLine(pen,4.5f,13.5f,11.5f,13.5f);
      break;
    }
   }
   g.SmoothingMode=sm;g.PixelOffsetMode=po;g.Restore(saved);
  }
 }

 // Shared drawing of list rows (settings, links): a small rounded icon tile, a title with an optional muted second line, a hairline divider.
 internal static class RowKit{
  // The tile and the line icon inside it. The icon is drawn by code on a 16-unit grid and centred on the tile's middle, so every tile looks the same
  // at every scale (no font metrics, no dependence on which icon font Windows has).
  public static readonly Color TileFill=Theme.Field;
  public static int Icon(Graphics g,IconKind icon,float k,int h,bool enabled){
   if(icon==IconKind.None)return 0;
   float s=28*k,y=(h-s)/2,box=16*k;
   using(var p=Theme.Round(new RectangleF(0,y,s,s),8*k))using(var b=new SolidBrush(TileFill))g.FillPath(b,p);
   Icons.Draw(g,icon,new RectangleF((s-box)/2,y+(s-box)/2,box,box),enabled?Color.FromArgb(170,190,235):Theme.Muted);
   return (int)(s+8*k);
  }
  public static void Texts(Graphics g,string title,string sub,Font font,Rectangle r,Color ink,Color subInk,float k){
   const TextFormatFlags f=TextFormatFlags.SingleLine|TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis;
   if(string.IsNullOrEmpty(sub)){TextRenderer.DrawText(g,title,font,r,ink,f|TextFormatFlags.VerticalCenter);return;}
   using(var sf=Theme.F(7.5f)){
    int th=TextRenderer.MeasureText(g,"Ag",font,Size.Empty,f).Height,sh=TextRenderer.MeasureText(g,"Ag",sf,Size.Empty,f).Height,gap=(int)(1*k);
    int top=r.Y+(r.Height-th-sh-gap)/2;
    TextRenderer.DrawText(g,title,font,new Rectangle(r.X,top,r.Width,th),ink,f);
    TextRenderer.DrawText(g,sub,sf,new Rectangle(r.X,top+th+gap,r.Width,sh),subInk,f);
   }
  }
  public static void Divider(Graphics g,int left,int width,int height,float k){using(var pen=new Pen(Theme.Divider))g.DrawLine(pen,left,height-1,width,height-1);}
 }

 // One muted line of a settings card that holds a file path: the middle of the path gives way when it is too long, the file name stays.
 internal sealed class PathLabel:Surface{
  public bool Divider;
  public PathLabel(){Font=Theme.F(8f);}
  protected override void OnTextChanged(EventArgs e){AccessibleName=Text;Invalidate();base.OnTextChanged(e);}
  protected override void OnPaint(PaintEventArgs e){
   var g=e.Graphics;float k=K;int left=(int)(40*k);
   TextRenderer.DrawText(g,Text,Font,new Rectangle(left,0,Math.Max(0,Width-left-(int)(86*k)),Height),Theme.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding|TextFormatFlags.PathEllipsis);
   if(Divider)RowKit.Divider(g,left,Width,Height,k);if(ShowFocusCues)Theme.FocusRing(g,this,8);
  }
 }

 // A clickable list row: icon (a glyph, the Telegram mark or a download arrow), title, muted second line, chevron. Used for links and for opening an editor.
 internal sealed class LinkRow:Surface{
  public enum Mark{Glyph,Telegram}
  public Mark Kind=Mark.Glyph;public IconKind Icon;public bool Divider,ShowChevron=true;string sub;readonly Anim hoverA;
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public string Subtitle{get=>sub;set{if(sub!=value){sub=value;Invalidate();}}}
  public LinkRow(){AccessibleRole=AccessibleRole.Link;Cursor=Cursors.Hand;Font=Theme.F(9.5f,true);Height=48;hoverA=new Anim(this,0f,140);}
  protected override void OnMouseEnter(EventArgs e){hoverA.To(1f);base.OnMouseEnter(e);}
  protected override void OnMouseLeave(EventArgs e){hoverA.To(0f);base.OnMouseLeave(e);}
  protected override void OnTextChanged(EventArgs e){AccessibleName=Text;Invalidate();base.OnTextChanged(e);}
  protected override bool IsInputKey(Keys k)=>k==Keys.Space||k==Keys.Enter||base.IsInputKey(k);
  protected override void OnKeyUp(KeyEventArgs e){if(e.KeyCode==Keys.Space||e.KeyCode==Keys.Enter)OnClick(EventArgs.Empty);base.OnKeyUp(e);}
  internal void ClickForTest()=>OnClick(EventArgs.Empty);
  protected override void OnPaint(PaintEventArgs e){
   var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;float k=K;
   if(hoverA.Value>0&&ShowChevron)using(var p=Theme.Round(new RectangleF(-6*k,2*k,Width+12*k-1,Height-4*k),10*k))using(var b=new SolidBrush(Color.FromArgb((int)(18*hoverA.Value),255,255,255)))g.FillPath(b,p);
   int left;
   if(Kind==Mark.Telegram){float s=28*k;Branding.DrawTelegram(g,new RectangleF(0,(Height-s)/2,s,s));left=(int)(s+12*k);}
   else left=RowKit.Icon(g,Icon,k,Height,true);
   RowKit.Texts(g,Text,sub,Font,new Rectangle(left,0,(int)(Width-left-(ShowChevron?26:4)*k),Height),Theme.Text,Theme.Muted,k);
   if(ShowChevron)using(var f=new Font(Theme.IconFont,8f))TextRenderer.DrawText(g,"\uE76C",f,new Rectangle((int)(Width-20*k),0,(int)(20*k),Height),Theme.Mix(Theme.Muted,Theme.Text,hoverA.Value),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
   if(Divider)RowKit.Divider(g,left,Width,Height,k);if(ShowFocusCues)Theme.FocusRing(g,this,8);
  }
 }

 // Two or three mutually exclusive choices in one inset track (the language). The highlight slides to the chosen segment.
 internal static class ServiceArtwork {
  internal static readonly Bitmap Antigravity=Load("Antigravity"),Gemini=Load("Gemini");
  static Bitmap Load(string name){using(var stream=typeof(ServiceArtwork).Assembly.GetManifestResourceStream("Service."+name+".png"))using(var image=Image.FromStream(stream))return new Bitmap(image);}
 }

 internal sealed class Segmented:Surface{
  string[] items=new string[0],tips=new string[0];int sel;readonly Anim pos;int hot=-1;
  readonly ToolTip tip=new ToolTip{InitialDelay=350,ReshowDelay=100,AutoPopDelay=10000};
  // Busy: a choice is being applied. The highlight breathes, the pointer shows work, clicks are ignored; the control still looks alive (not greyed out).
  bool busy;
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public bool Busy{get=>busy;set{if(busy==value)return;busy=value;Cursor=value?Cursors.AppStarting:Cursors.Hand;if(value&&Motion.Enabled)Motion.Run(BusyStep);Invalidate();}}
  bool BusyStep(){if(IsDisposed||!busy||!Visible||FindForm()?.WindowState==FormWindowState.Minimized){Invalidate();return false;}Invalidate();return true;}
  protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);if(Visible&&busy&&Motion.Enabled)Motion.Run(BusyStep);}
  internal float BusyPhase=>!busy?0f:!Motion.Enabled?1f:.5f+.5f*(float)Math.Sin(Stopwatch.GetTimestamp()*Math.PI*2/Stopwatch.Frequency/1.2);   // 0..1; a still frame shows the strongest tone
  protected override void Dispose(bool disposing){if(disposing)tip.Dispose();base.Dispose(disposing);}
  public event EventHandler SelectedIndexChanged;   // raised by the user's click only
  protected override AccessibleObject CreateAccessibilityInstance()=>new SelectionAccessible(this);
  sealed class SelectionAccessible:ControlAccessibleObject{
   readonly Segmented owner;public SelectionAccessible(Segmented c):base(c){owner=c;}
   public override string Value {get=>owner.sel>=0&&owner.sel<owner.items.Length?owner.items[owner.sel]:"";set{}}
   public override int GetChildCount()=>owner.items.Length;
   public override AccessibleObject GetChild(int index)=>index>=0&&index<GetChildCount()?new ItemAccessible(owner,index):null;
   public override AccessibleObject GetSelected()=>GetChild(owner.sel);
  }
  sealed class ItemAccessible:AccessibleObject{
   readonly Segmented owner;readonly int index;public ItemAccessible(Segmented c,int i){owner=c;index=i;}
   public override string Name {get=>index<owner.items.Length?owner.items[index]:"";set{}}
   public override AccessibleRole Role=>AccessibleRole.PageTab;
   public override AccessibleObject Parent=>owner.AccessibilityObject;
   public override AccessibleStates State=>AccessibleStates.Selectable|(owner.Enabled&&!owner.Busy?AccessibleStates.None:AccessibleStates.Unavailable)|(owner.sel==index?AccessibleStates.Selected:AccessibleStates.None)|(owner.Focused&&owner.sel==index?AccessibleStates.Focused:AccessibleStates.None);
   public override Rectangle Bounds=>owner.RectangleToScreen(owner.ClientRectangle);
   public override void Select(AccessibleSelection flags){if(owner.Enabled&&!owner.Busy){owner.SelectedIndex=index;owner.SelectedIndexChanged?.Invoke(owner,EventArgs.Empty);}}
   public override void DoDefaultAction()=>Select(AccessibleSelection.TakeSelection);
  }
  public Segmented(){AccessibleRole=AccessibleRole.PageTabList;Cursor=Cursors.Hand;Font=Theme.F(8.5f,true);pos=new Anim(this,0f,200);}
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public string[] Items{get=>items;set{items=value??new string[0];AccessibilityNotifyClients(AccessibleEvents.NameChange,-1);AccessibilityNotifyClients(AccessibleEvents.ValueChange,-1);Invalidate();}}
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public string[] Tips{get=>tips;set{tips=value??new string[0];}}
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public int SelectedIndex{get=>sel;set{if(value<0||value>=items.Length)return;sel=value;pos.To(value);AccessibilityNotifyClients(AccessibleEvents.Selection,-1);AccessibilityNotifyClients(AccessibleEvents.ValueChange,-1);}}
  int At(int x)=>items.Length==0?-1:Math.Max(0,Math.Min(items.Length-1,x*items.Length/Math.Max(1,Width)));
  protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);int i=At(e.X);if(i!=hot){hot=i;Invalidate();}}
  protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hot=-1;Invalidate();}
  protected override void OnEnabledChanged(EventArgs e){Invalidate();base.OnEnabledChanged(e);}
  protected override void OnMouseClick(MouseEventArgs e){Focus();base.OnMouseClick(e);if(!Enabled||busy)return;int i=At(e.X);if(i>=0&&i!=sel){SelectedIndex=i;SelectedIndexChanged?.Invoke(this,EventArgs.Empty);}}
  protected override bool IsInputKey(Keys key)=>key==Keys.Left||key==Keys.Right||base.IsInputKey(key);
  protected override void OnKeyDown(KeyEventArgs e){if(Enabled&&!busy&&items.Length>0&&(e.KeyCode==Keys.Left||e.KeyCode==Keys.Right)){int next=Math.Clamp(sel+(e.KeyCode==Keys.Right?1:-1),0,items.Length-1);if(next!=sel){SelectedIndex=next;SelectedIndexChanged?.Invoke(this,EventArgs.Empty);}e.Handled=true;}base.OnKeyDown(e);}
  internal void ClickForTest(int i){if(busy)return;if(i!=sel&&i>=0&&i<items.Length){SelectedIndex=i;SelectedIndexChanged?.Invoke(this,EventArgs.Empty);}}
  // The rectangle of the highlight in client pixels (the settled one when the animation is over).
  internal RectangleF PillRect(float at){float k=K,w=(Width-6*k)/Math.Max(1,items.Length);return new RectangleF(3*k+at*w,3*k,w,Height-6*k-1);}
  internal int PaintFailures;   // a paint that failed is counted (the self-test requires 0) instead of turning the control into the framework's red cross
  protected override void OnPaint(PaintEventArgs e){try{PaintCore(e.Graphics);if(ShowFocusCues)Theme.FocusRing(e.Graphics,this,16);}catch(Exception ex){PaintFailures++;UiDiag.Paint("Segmented",ex);}}
  void PaintCore(Graphics g){
   g.SmoothingMode=SmoothingMode.AntiAlias;float k=K;if(items.Length==0)return;
   using(var p=Theme.Round(new RectangleF(0,0,Width-1,Height-1),10*k))using(var b=new SolidBrush(Theme.Field))using(var pen=new Pen(Theme.Line)){g.FillPath(b,p);g.DrawPath(pen,p);}
   float w=(Width-6*k)/items.Length;
   var pill=PillRect(pos.Value);
   float bp=BusyPhase;
   using(var p=Theme.Round(pill,7*k))using(var b=new SolidBrush(Enabled?Theme.Mix(Theme.NavSel,Theme.Accent,.30f*bp):Theme.Soft))using(var pen=new Pen(Enabled?Theme.Mix(Color.FromArgb(60,90,160),Theme.AccentHover,.8f*bp):Theme.Line)){g.FillPath(b,p);g.DrawPath(pen,p);}
   for(int i=0;i<items.Length;i++){
    float near=Math.Max(0f,1f-Math.Abs(i-pos.Value));
    Color c=!Enabled?Theme.Muted:Theme.Mix(i==hot?Theme.Text:Theme.Muted,Color.White,near);
    var itemRect=new Rectangle((int)(3*k+i*w),0,(int)w,Height);
    DrawItem(g,items[i],itemRect,c,k);
   }
  }
  void DrawItem(Graphics g,string text,Rectangle r,Color c,float k){
   TextRenderer.DrawText(g,text,Font,r,c,TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis|TextFormatFlags.HorizontalCenter);
  }

 }

 // The services behind the bridge: independent switches in one inset track (any of the three, in any combination; at least one stays on).
 // A cell that is on is filled and outlined; an off cell shows only its icon and name. The browser's own icon and name come from the settings.
 internal sealed class ServicePicker:Surface{
  public const int Count=3;
  string[] items=new string[Count],tips=new string[Count];readonly bool[] on=new bool[Count];readonly Anim[] fill=new Anim[Count];readonly Anim hoverAll;int hot=-1,focus;
  readonly ToolTip tip=new ToolTip{InitialDelay=350,ReshowDelay=100,AutoPopDelay=10000};
  public Image BrowserIcon;
  bool busy;
  public event EventHandler SelectionChanged;   // raised by the user's click only
  public ServicePicker(){AccessibleRole=AccessibleRole.Grouping;Cursor=Cursors.Hand;Font=Theme.F(8.5f,true);for(int i=0;i<Count;i++)fill[i]=new Anim(this,0f,180);hoverAll=new Anim(this,0f,Theme.HoverMs);}
  protected override void Dispose(bool disposing){if(disposing)tip.Dispose();base.Dispose(disposing);}
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public string[] Items{get=>items;set{items=value??new string[Count];AccessibilityNotifyClients(AccessibleEvents.NameChange,-1);Invalidate();}}
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public string[] Tips{get=>tips;set{tips=value??new string[Count];}}
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public bool Busy{get=>busy;set{if(busy==value)return;busy=value;Cursor=value?Cursors.AppStarting:Cursors.Hand;if(value&&Motion.Enabled)Motion.Run(BusyStep);Invalidate();}}
  bool BusyStep(){if(IsDisposed||!busy||!Visible||FindForm()?.WindowState==FormWindowState.Minimized){Invalidate();return false;}Invalidate();return true;}
  protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);if(Visible&&busy&&Motion.Enabled)Motion.Run(BusyStep);}
  internal float BusyPhase=>!busy?0f:!Motion.Enabled?1f:.5f+.5f*(float)Math.Sin(Stopwatch.GetTimestamp()*Math.PI*2/Stopwatch.Frequency/1.2);   // 0..1; a still frame shows the strongest tone
  public bool IsOn(int i)=>i>=0&&i<Count&&on[i];
  public int OnCount=>on.Count(x=>x);
  // The state the program decided on (not a click): no event, the highlight settles at once.
  public void SetOn(bool core,bool gemini,bool google){on[0]=core;on[1]=gemini;on[2]=google;for(int i=0;i<Count;i++)fill[i].To(on[i]?1f:0f,true);AccessibilityNotifyClients(AccessibleEvents.Selection,-1);AccessibilityNotifyClients(AccessibleEvents.ValueChange,-1);Invalidate();}
  int At(int x)=>Math.Max(0,Math.Min(Count-1,(int)((x-3*K)*Count/Math.Max(1f,Width-6*K))));
  internal RectangleF CellRect(int i){float k=K,w=(Width-6*k)/Count;return new RectangleF(3*k+i*w,3*k,w,Height-6*k-1);}
  // A toggle from the user. The last service that is on cannot be switched off.
  bool Toggle(int i){
   if(!Enabled||busy||i<0||i>=Count)return false;
   if(on[i]&&OnCount==1){if(IsHandleCreated)tip.Show(L.En?"At least one service stays selected":"Должен остаться хотя бы один сервис",this,(int)CellRect(i).Left,Height+(int)(4*K),1800);return false;}
   on[i]=!on[i];fill[i].To(on[i]?1f:0f);
   AccessibilityNotifyClients(AccessibleEvents.Selection,-1);AccessibilityNotifyClients(AccessibleEvents.ValueChange,-1);
   SelectionChanged?.Invoke(this,EventArgs.Empty);return true;
  }
  internal bool ClickForTest(int i)=>Toggle(i);
  // The whole selection at once, as one change (tests; a user changes one switch per click).
  internal bool SelectForTest(bool core,bool gemini,bool google){
   if(busy||!(core||gemini||google))return false;
   if(on[0]==core&&on[1]==gemini&&on[2]==google)return false;
   on[0]=core;on[1]=gemini;on[2]=google;for(int i=0;i<Count;i++)fill[i].To(on[i]?1f:0f,true);SelectionChanged?.Invoke(this,EventArgs.Empty);return true;
  }
  protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);int i=At(e.X);if(i!=hot){hot=i;Invalidate();}}
  protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hoverAll.To(1f);}
  protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hot=-1;hoverAll.To(0f);Invalidate();}
  protected override void OnEnabledChanged(EventArgs e){Invalidate();base.OnEnabledChanged(e);}
  protected override void OnMouseClick(MouseEventArgs e){Focus();base.OnMouseClick(e);if(e.Button!=MouseButtons.Left)return;focus=At(e.X);Toggle(focus);}
  protected override bool IsInputKey(Keys key)=>key==Keys.Left||key==Keys.Right||key==Keys.Space||key==Keys.Enter||base.IsInputKey(key);
  protected override void OnKeyDown(KeyEventArgs e){
   if(e.KeyCode==Keys.Left||e.KeyCode==Keys.Right){focus=Math.Clamp(focus+(e.KeyCode==Keys.Right?1:-1),0,Count-1);Invalidate();e.Handled=true;}
   else if(e.KeyCode==Keys.Space||e.KeyCode==Keys.Enter){Toggle(focus);e.Handled=true;}
   base.OnKeyDown(e);
  }
  protected override AccessibleObject CreateAccessibilityInstance()=>new PickerAccessible(this);
  sealed class PickerAccessible:ControlAccessibleObject{
   readonly ServicePicker owner;public PickerAccessible(ServicePicker c):base(c){owner=c;}
   public override string Value{get=>string.Join(", ",Enumerable.Range(0,Count).Where(owner.IsOn).Select(i=>owner.items[i]));set{}}
   public override int GetChildCount()=>Count;
   public override AccessibleObject GetChild(int index)=>index>=0&&index<Count?new CellAccessible(owner,index):null;
  }
  sealed class CellAccessible:AccessibleObject{
   readonly ServicePicker owner;readonly int index;public CellAccessible(ServicePicker c,int i){owner=c;index=i;}
   public override string Name{get=>owner.items[index];set{}}
   public override AccessibleRole Role=>AccessibleRole.CheckButton;
   public override AccessibleObject Parent=>owner.AccessibilityObject;
   public override AccessibleStates State=>AccessibleStates.Focusable|(owner.Enabled&&!owner.busy?AccessibleStates.None:AccessibleStates.Unavailable)|(owner.on[index]?AccessibleStates.Checked:AccessibleStates.None)|(owner.Focused&&owner.focus==index?AccessibleStates.Focused:AccessibleStates.None);
   public override Rectangle Bounds=>owner.RectangleToScreen(Rectangle.Round(owner.CellRect(index)));
   public override string DefaultAction=>L.En?"Toggle":"Переключить";
   public override void DoDefaultAction(){owner.Toggle(index);}
  }
  internal int PaintFailures;   // a paint that failed is counted (the self-test requires 0) instead of turning the control into the framework's red cross
  protected override void OnPaint(PaintEventArgs e){try{PaintCore(e.Graphics);if(ShowFocusCues)Theme.FocusRing(e.Graphics,this,16);}catch(Exception ex){PaintFailures++;UiDiag.Paint("ServicePicker",ex);}}
  // One piece of hardware, not three buttons: the services that are on form one continuous lit band (neighbours that are both on share a square edge,
  // only the ends of a run are rounded). The seams between the cells appear only while the pointer is over the control.
  void PaintCore(Graphics g){
   g.SmoothingMode=SmoothingMode.AntiAlias;float k=K,bp=BusyPhase,hv=Enabled?hoverAll.Value:0f;
   using(var p=Theme.Round(new RectangleF(0,0,Width-1,Height-1),12*k))using(var b=new SolidBrush(Theme.Field))using(var pen=new Pen(Theme.Mix(Theme.Field,Theme.Line,.75f+.25f*hv))){g.FillPath(b,p);g.DrawPath(pen,p);}
   Color lit=Enabled?Theme.Mix(Theme.Mix(Theme.Field,Theme.Accent,.24f),Theme.Accent,.32f*bp):Theme.Soft;
   Color rim=Enabled?Theme.Mix(Theme.Mix(Theme.Field,Theme.AccentHover,.34f),Theme.AccentHover,.6f*bp):Theme.Line;
   bool On(int i)=>i>=0&&i<Count&&fill[i].Value>.5f;
   float inner=9*k;
   // hover of a cell that is off: a quiet well, drawn under the band
   if(hot>=0&&Enabled&&!busy&&!On(hot)){using(var p=Theme.Round(CellRect(hot),inner))using(var b=new SolidBrush(Theme.Mix(Theme.Field,Theme.Soft,.85f)))g.FillPath(b,p);}
   for(int i=0;i<Count;i++){
    float a=fill[i].Value;if(a<=0.01f)continue;
    var cell=CellRect(i);bool joinL=On(i)&&On(i-1),joinR=On(i)&&On(i+1);
    if(joinL){cell.X-=1;cell.Width+=1;}if(joinR)cell.Width+=1;   // overlap the neighbour by a pixel: no anti-aliased seam inside a run
    Color c=Theme.Mix(Theme.Field,i==hot&&!busy?Theme.Mix(lit,Theme.AccentHover,.10f):lit,a);
    using(var p=Theme.RoundSides(cell,inner,!joinL,!joinR))using(var b=new LinearGradientBrush(RectangleF.Inflate(cell,0,1),Theme.Mix(c,Color.White,.07f*a),c,90f))g.FillPath(b,p);   // the lit band has a soft lift at its top
   }
   // one outline around each run
   for(int i=0;i<Count;){
    if(!On(i)){i++;continue;}
    int j=i;while(On(j+1))j++;
    var a=CellRect(i);var z=CellRect(j);float alpha=Enumerable.Range(i,j-i+1).Min(x=>fill[x].Value);
    using(var p=Theme.Round(RectangleF.FromLTRB(a.Left,a.Top,z.Right,z.Bottom),inner))using(var pen=new Pen(Theme.Mix(Theme.Field,rim,alpha),k))g.DrawPath(pen,p);
    i=j+1;
   }
   if(hv>0.01f)for(int i=1;i<Count;i++){
    // the seams: a groove inside a lit run, a hairline between quiet cells
    float x=CellRect(i).Left;Color seam=On(i-1)&&On(i)?Theme.Mix(lit,Theme.Field,.7f):Theme.Line;
    using(var pen=new Pen(Color.FromArgb((int)(255*hv),seam),k))g.DrawLine(pen,x,9*k,x,Height-9*k);
   }
   for(int i=0;i<Count;i++){
    var cell=CellRect(i);float a=fill[i].Value;
    Color c=!Enabled?Theme.Muted:Theme.Mix(i==hot?Theme.Mix(Theme.Muted,Theme.Text,.7f):Theme.Muted,Color.White,a);
    int textWidth=Math.Min((int)(cell.Width-34*k),TextRenderer.MeasureText(g,items[i]??"",Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width);
    float s=18*k,gap=7*k,group=s+gap+textWidth,x=cell.Left+(cell.Width-group)/2,cy=Height/2f;
    Image artwork=i==0?ServiceArtwork.Antigravity:i==1?ServiceArtwork.Gemini:BrowserIcon;
    if(artwork!=null)DrawArtwork(g,artwork,new RectangleF(x,cy-s/2,s,s),Enabled?.42f+.58f*Math.Max(a,i==hot?.35f:0f):.35f);else Icons.Draw(g,IconKind.Compass,new RectangleF(x,cy-s/2,s,s),c);
    TextRenderer.DrawText(g,items[i]??"",Font,new Rectangle((int)Math.Round(x+s+gap),0,textWidth,Height),c,TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis|TextFormatFlags.Left);
   }
   if(Focused&&ShowFocusCues){var f=CellRect(focus);f.Inflate(-2*k,-2*k);using(var p=Theme.Round(f,8*k))using(var pen=new Pen(Theme.Focus,1.5f*k))g.DrawPath(pen,p);}
  }
  // A service's mark at full colour when it is on; desaturated and dimmed when it is off, so the lit band carries the choice.
  static void DrawArtwork(Graphics g,Image image,RectangleF r,float strength){
   if(strength>=.999f){g.DrawImage(image,r);return;}
   float s=Math.Clamp(strength,0f,1f),gray=1f-s,lr=.299f*gray,lg=.587f*gray,lb=.114f*gray;
   var m=new ColorMatrix(new[]{new[]{s+lr,lr,lr,0f,0f},new[]{lg,s+lg,lg,0f,0f},new[]{lb,lb,s+lb,0f,0f},new[]{0f,0f,0f,.45f+.55f*s,0f},new[]{0f,0f,0f,0f,1f}});
   using(var ia=new ImageAttributes()){ia.SetColorMatrix(m);g.DrawImage(image,Rectangle.Round(r),0,0,image.Width,image.Height,GraphicsUnit.Pixel,ia);}
  }
 }

 // The drop-down of the profile: the chosen name, and at its right a short detail with a coloured dot (the ping); every row of the list carries its own detail
 // («—» while it is not known). The detail comes from Detail(i) and can change while the list is open (RefreshDetails).
 // The line under the title. With a DotSource it shows a small status dot before a single line of text (the pair centred as one group); otherwise it is a plain label.
 internal sealed class ContextLabel:Label {
  public Func<Color> DotSource;
  const TextFormatFlags Flags=TextFormatFlags.Left|TextFormatFlags.Top|TextFormatFlags.NoPadding|TextFormatFlags.SingleLine;
  protected override void OnPaint(PaintEventArgs e){
   Color dot=DotSource?.Invoke()??Color.Empty;var g=e.Graphics;
   if(dot==Color.Empty||string.IsNullOrEmpty(Text)||Text.Contains((char)10)){base.OnPaint(e);return;}
   float k=DeviceDpi/96f,dd=6*k,gap=7*k;
   int tw=TextRenderer.MeasureText(g,Text,Font,Size.Empty,Flags).Width,lh=TextRenderer.MeasureText(g,"Ag",Font,Size.Empty,Flags).Height;
   if(tw+dd+gap>Width){base.OnPaint(e);return;}
   float left=(Width-(dd+gap+tw))/2f;
   var old=g.SmoothingMode;g.SmoothingMode=SmoothingMode.AntiAlias;
   using(var b=new SolidBrush(dot))g.FillEllipse(b,left,(lh-dd)/2f+.5f*k,dd,dd);
   g.SmoothingMode=old;
   TextRenderer.DrawText(g,Text,Font,new Rectangle((int)Math.Round(left+dd+gap),0,tw+2,lh),ForeColor,Flags);
  }
 }

 internal sealed class MetricLabel:Label {
  public MetricLabel(){SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer,true);AccessibleRole=AccessibleRole.StaticText;TabStop=false;}
  protected override void OnTextChanged(EventArgs e){AccessibleName=Text;base.OnTextChanged(e);}
  // A caption always shows all of its words: the type gives way (down to 6.5 pt) before an ellipsis.
  protected override void OnPaint(PaintEventArgs e){
   using(var fit=Theme.Fit(e.Graphics,Text,Font.SizeInPoints,6.5f,ClientSize.Width))
    TextRenderer.DrawText(e.Graphics,Text,fit,ClientRectangle,ForeColor,(TextAlign==ContentAlignment.MiddleCenter?TextFormatFlags.HorizontalCenter:TextFormatFlags.Left)|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis);
  }
 }

 // What the selected profile's card shows: a row of small measured values (verdict, median, peak, TLS, responses, when), the median latency of the profile
 // over the last 30 minutes, and the spread of the answer times of its last measurement (min / average / max). Only real benchmark results reach it;
 // with none it says so. A chart without data says that it has none.
 internal sealed class TelemetryData {
  public string Verdict="",Hint="";public Color Dot=Color.Empty;public bool Stale;
  public bool HasMetrics;   // false: only the verdict chip and a hint line (no measurement yet, or nothing answered)
  public string Median="—",Peak="—",Tls="—",ResponsesValue="",ResponsesLabel="",AgeLabel="",AgeValue="",Age="";
  public long MedianMs=-1,PeakMs=-1,TlsMs=-1;public double Availability=-1;   // the same figures as numbers: they pick the tint of each value
  public long[] Samples=new long[0];   // answer times of the last measurement, ms
  public (DateTime Utc,long Ms)[] Points=new (DateTime,long)[0];   // medians of this profile in the last 30 minutes
  public DateTime NowUtc=DateTime.UtcNow;
  public bool HasTrend;public int TrendPct;public long PrevMedianMs=-1;   // the median against the previous real measurement of this profile (real runs only); HasTrend=false: there is none to compare with
  public string Share="";   // successes / attempts as a percentage ("100 %"); empty when nothing was asked
 }

 internal sealed class ProfileTelemetry:Surface{
  public const int WindowMinutes=30;
  TelemetryData data=new TelemetryData();string summary="";long[] trend=new long[0];
  public ProfileTelemetry(){SetStyle(ControlStyles.Selectable,false);TabStop=false;Cursor=Cursors.Default;AccessibleRole=AccessibleRole.StaticText;}
  internal string Summary=>summary;
  internal long[] Trend=>trend;
  internal TelemetryData Data=>data;
  internal void ClickForTest()=>OnClick(EventArgs.Empty);
  public void Show(TelemetryData d,string summary,long[] trend){
   data=d??new TelemetryData();this.summary=summary??"";this.trend=trend??new long[0];
   AccessibleName=this.summary;Text=this.summary;Invalidate();
  }
  protected override void OnClick(EventArgs e){base.OnClick(e);}

  // the scale: the smallest round number above the largest value
  internal static long NiceMax(long value){
   long[] steps={20,50,100,150,200,300,400,500,750,1000,1500,2000,3000,5000,10000};
   foreach(long step in steps)if(value*1.05<=step)return step;
   return (value/1000+1)*1000;
  }

  // The board: one surface of the card, three bands without boxes of their own, the spare height shared as air between them.
  //   status: the title with how old the measurement is at the right (and the half-hour trend between them, when there are three or more points);
  //   readout: four equal columns divided by hairlines: median, peak, TLS, responses; a value is tinted by the same limits as the benchmark table;
  //   spread: the answer times of the last measurement on a ruler from zero whose zones (good / so-so / slow) use those limits, the median marked on it.
  const TextFormatFlags One=TextFormatFlags.SingleLine|TextFormatFlags.NoPadding,Mid=TextFormatFlags.VerticalCenter|One|TextFormatFlags.EndEllipsis;
  const int TitleH=13,GridH=32,SpreadH=28;   // layout units: the least each band needs
  internal const int MinBoardUnits=TitleH+GridH+SpreadH+2*2;   // the least height (units) that holds the three bands with their smallest gaps
  static string Cap(string s)=>string.IsNullOrEmpty(s)?s:char.ToUpperInvariant(s[0])+s.Substring(1);
  internal static string LatencyLabel=>L.En?"Latency":"Задержка";
  static int TextW(Graphics g,string s,Font f)=>TextRenderer.MeasureText(g,s??"",f,Size.Empty,One).Width;
  static readonly Color Hair=Theme.Mix(Theme.Field,Theme.Line,.85f);
  // a figure keeps most of the text colour and takes a part of its zone colour: readable first, meaningful second
  static Color Tinted(Color tone,bool stale)=>stale?Theme.Mix(Theme.Muted,tone,.4f):Theme.Mix(Theme.Text,tone,.55f);   // an earlier run keeps a quieter tint: it still says good or bad, but not "now"
  protected override void WndProc(ref Message m){if(m.Msg==0x84){m.Result=(IntPtr)(-1);return;}base.WndProc(ref m);}   // WM_NCHITTEST -> HTTRANSPARENT: the card under the board takes the mouse (hover, click, cursor)
  protected override void OnPaint(PaintEventArgs e){
   var g=e.Graphics;float k=K;g.SmoothingMode=SmoothingMode.AntiAlias;
   var d=data;
   if(!d.HasMetrics){PaintEmpty(g,k);return;}
   float title=TitleH*k,grid=GridH*k,spread=SpreadH*k;
   float gap=Math.Min(9*k,Math.Max(2*k,(Height-title-grid-spread)/3f));
   float top=Math.Max(0,(Height-(title+grid+spread+2*gap))/2f);
   PaintStatus(g,new RectangleF(0,top,Width,title),k);
   PaintReadout(g,new RectangleF(0,top+title+gap,Width,grid),k);
   PaintSpread(g,new RectangleF(0,top+title+gap+grid+gap,Width,spread),k);
  }
  // No numbers to show: a stopwatch tile, the verdict and what to do, and a chevron to the benchmark (the whole board is the link).
  void PaintEmpty(Graphics g,float k){
   var d=data;float tile=30*k,ty=Math.Max(0,(Math.Min(Height,58*k)-tile)/2f);
   using(var p=Theme.Round(new RectangleF(0,ty,tile,tile),9*k))using(var b=new SolidBrush(Theme.Soft))g.FillPath(b,p);
   using(var gf=new Font(Theme.IconFont,11f))TextRenderer.DrawText(g,"",gf,Rectangle.Round(new RectangleF(0,ty,tile,tile)),d.Dot!=Color.Empty?d.Dot:Theme.AccentHover,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|One);
   int left=(int)(tile+12*k),right=(int)(Width-22*k);
   using(var vf=Theme.F(9f,true))using(var hf=Theme.F(8f)){
    int vh=TextRenderer.MeasureText(g,"Ag",vf,Size.Empty,One).Height;
    var hint=new Rectangle(left,(int)(ty+tile/2f),Math.Max(1,right-left),(int)Math.Max(12*k,Height-ty-tile/2f));
    TextRenderer.DrawText(g,d.Verdict,vf,new Rectangle(left,(int)(ty+tile/2f-vh-1*k),Math.Max(1,right-left),vh),Theme.Text,One|TextFormatFlags.EndEllipsis);
    TextRenderer.DrawText(g,d.Hint,hf,hint,Theme.Muted,TextFormatFlags.Left|TextFormatFlags.Top|TextFormatFlags.WordBreak|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding);
   }
  }
  void PaintStatus(Graphics g,RectangleF r,float k){
   var d=data;float x=r.X;
   if(d.Dot!=Color.Empty){float dd=7*k;using(var b=new SolidBrush(d.Stale?Theme.Mix(d.Dot,Theme.Field,.45f):d.Dot))g.FillEllipse(b,x,r.Y+(r.Height-dd)/2f,dd,dd);x+=dd+7*k;}
   using(var vf=Theme.F(8.5f,true))using(var af=Theme.F(7.5f)){
    string age=d.Age??"";   // a measurement of an earlier run is told by its muted figures; the age says how old it is
    // the age at the right edge with a small refresh mark after it (a picture only: nothing to press, nothing is requested)
    float ic=age.Length>0?10*k:0,icGap=age.Length>0?5*k:0,edge=r.Right-ic-icGap;
    if(ic>0)PaintRefreshMark(g,new RectangleF(r.Right-ic,r.Y+(r.Height-ic)/2f,ic,ic),d.Stale?Theme.Mix(Theme.Muted,Theme.Field,.4f):Theme.Muted,k);
    int aw=Math.Min((int)(r.Width*.55f),TextW(g,age,af));
    TextRenderer.DrawText(g,age,af,new Rectangle((int)(edge-aw),(int)r.Y,aw,(int)r.Height),Theme.Muted,Mid|TextFormatFlags.Right);
    int vw=TextW(g,d.Verdict,vf);
    TextRenderer.DrawText(g,d.Verdict,vf,new Rectangle((int)x,(int)r.Y,(int)Math.Max(1,Math.Min(vw+2,edge-aw-x-8*k)),(int)r.Height),d.Stale?Theme.Muted:Theme.Text,Mid);
    // the trend of the last half hour, when there is one to draw, between the verdict and the age
    float sw=46*k,sx=edge-aw-10*k-sw;
    if(sx>x+vw+12*k)PaintSparkline(g,new RectangleF(sx,r.Y+3*k,sw,r.Height-6*k),k);
   }
  }
  // a circular arrow: an open ring with a small head at its end
  static void PaintRefreshMark(Graphics g,RectangleF r,Color ink,float k){
   var c=RectangleF.Inflate(r,-2.2f*k,-2.2f*k);
   using(var pen=new Pen(ink,1.35f*k){StartCap=LineCap.Round,EndCap=LineCap.Round})g.DrawArc(pen,c,-30,285);
   double a=(-30+285)*Math.PI/180;float rad=c.Width/2f,px=c.X+rad+rad*(float)Math.Cos(a),py=c.Y+rad+rad*(float)Math.Sin(a);
   float tx=-(float)Math.Sin(a),ty=(float)Math.Cos(a),nx=(float)Math.Cos(a),ny=(float)Math.Sin(a);   // direction of travel, outward normal
   using(var b=new SolidBrush(ink))g.FillPolygon(b,new[]{new PointF(px+tx*3.2f*k,py+ty*3.2f*k),new PointF(px+nx*2.6f*k,py+ny*2.6f*k),new PointF(px-nx*2.6f*k,py-ny*2.6f*k)});
  }
  void PaintSparkline(Graphics g,RectangleF r,float k){
   var pts=data.Points.Where(p=>(data.NowUtc-p.Utc).TotalMinutes<=WindowMinutes&&(data.NowUtc-p.Utc).TotalMinutes>=-1).OrderBy(p=>p.Utc).ToArray();
   if(pts.Length<3)return;   // two points are a stroke, not a trend
   long lo=pts.Min(p=>p.Ms),hi=Math.Max(lo+1,pts.Max(p=>p.Ms));
   var line=pts.Select(p=>{float t=(float)Math.Max(0,Math.Min(1,1-(data.NowUtc-p.Utc).TotalMinutes/WindowMinutes));return new PointF(r.X+t*r.Width,r.Bottom-(p.Ms-lo)*r.Height/(float)(hi-lo));}).ToArray();
   Color ink=data.Stale?Theme.Muted:Theme.AccentHover;
   using(var pen=new Pen(Color.FromArgb(200,ink),1.4f*k){LineJoin=LineJoin.Round,StartCap=LineCap.Round,EndCap=LineCap.Round})g.DrawLines(pen,line);
   float dot=4*k;var last=line[line.Length-1];using(var b=new SolidBrush(ink))g.FillEllipse(b,last.X-dot/2,last.Y-dot/2,dot,dot);
  }
  readonly struct Cell{public readonly string Label,Value,Extra;public readonly Color Tone,ExtraTone;public Cell(string label,string value,Color tone,string extra="",Color extraTone=default){Label=label;Value=value;Tone=tone;Extra=extra??"";ExtraTone=extraTone;}}
  internal readonly struct Seg{public readonly string Text;public readonly Font Font;public readonly Color Ink;public Seg(string text,Font font,Color ink){Text=text;Font=font;Ink=ink;}}
  readonly struct Zone{public readonly long From,To;public readonly Color Tone;public Zone(long from,long to,Color tone){From=from;To=to;Tone=tone;}}
  // «↓ 12 %» (faster than the previous measurement: green), «↑ 8 %» (slower: amber, red once the median itself is in the red zone), «→ 0 %» (the same)
  internal static string TrendText(int pct)=>pct<0?"↓ "+(-pct)+" %":pct>0?"↑ "+pct+" %":"→ 0 %";
  internal static Color TrendTone(int pct,long medianMs)=>pct<0?Theme.Green:pct>0?(Latency.Of(medianMs)==Theme.Red?Theme.Red:Theme.Amber):Theme.Muted;
  void PaintReadout(Graphics g,RectangleF r,float k){
   var d=data;
   var cells=new[]{
    new Cell(LatencyLabel,d.Median,d.MedianMs>=0?Latency.Of(d.MedianMs):Theme.Muted,d.HasTrend&&d.MedianMs>=0?TrendText(d.TrendPct):"",TrendTone(d.TrendPct,d.MedianMs)),
    new Cell("P95",d.Peak,d.PeakMs>=0?Latency.Of(d.PeakMs):Theme.Muted),
    new Cell("TLS",d.Tls,d.TlsMs>=0?Latency.OfTls(d.TlsMs):Theme.Muted),
    new Cell(Cap(string.IsNullOrEmpty(d.ResponsesLabel)?(L.En?"responses":"ответов"):d.ResponsesLabel),d.ResponsesValue,d.Availability>=0?Latency.OfAvailability(d.Availability):Theme.Muted,d.Share,d.Availability>=0?Latency.OfAvailability(d.Availability):Theme.Muted)};
   float cw=r.Width/cells.Length,pad=12*k,labelH=10*k;
   using(var lf=Theme.F(7f))using(var ef=Theme.F(7.5f))
   for(int i=0;i<cells.Length;i++){
    float cx=r.X+i*cw,x=cx+(i==0?0:pad),w=cw-(i==0?0:pad)-3*k;
    if(i>0)using(var pen=new Pen(Hair,k))g.DrawLine(pen,cx,r.Y+1*k,cx,r.Bottom-1*k);
    TextRenderer.DrawText(g,cells[i].Label,lf,Rectangle.Round(new RectangleF(x,r.Y,w,labelH)),Theme.Muted,Mid);
    var (n,un)=Split(cells[i].Value);string full=n+(un.Length>0?" "+un:"");
    // the small figure beside the value (trend, share) is dropped, never squeezed in over the value, when the column is too narrow for both
    float ew=cells[i].Extra.Length>0?TextW(g,cells[i].Extra,ef)+6*k:0;
    Font fit=Theme.Fit(g,full,11.5f,8f,(int)Math.Max(1,w-ew),true);
    if(ew>0&&fit.SizeInPoints<10f){fit.Dispose();ew=0;fit=Theme.Fit(g,full,11.5f,8f,(int)w,true);}
    using(fit){
     int fh=TextRenderer.MeasureText(g,"0",fit,Size.Empty,One).Height,w2=TextW(g,n,fit);
     float vy=r.Bottom-fh,end=x+w2+2*k;
     TextRenderer.DrawText(g,n,fit,new Rectangle((int)x,(int)vy,w2+2,fh),Tinted(cells[i].Tone,d.Stale),One);
     if(un.Length>0)using(var uf=Theme.F(Math.Max(6.5f,fit.SizeInPoints*.72f))){int uh=TextRenderer.MeasureText(g,"0",uf,Size.Empty,One).Height,uw=TextW(g,un,uf);TextRenderer.DrawText(g,un,uf,new Rectangle((int)end,(int)(vy+fh-uh-fh*.1f),(int)Math.Max(1,w-w2-2*k),uh),Theme.Muted,One);end+=uw;}
     if(ew>0){int eh=TextRenderer.MeasureText(g,"0",ef,Size.Empty,One).Height;Color tone=cells[i].ExtraTone;TextRenderer.DrawText(g,cells[i].Extra,ef,new Rectangle((int)(end+6*k),(int)(vy+fh-eh-fh*.1f),(int)Math.Max(1,x+w-end-6*k),eh),d.Stale?Theme.Mix(Theme.Muted,tone,.4f):Theme.Mix(Theme.Muted,tone,.85f),One);}
    }
   }
  }
  // "213 мс" gives ("213","мс"); a value without a unit stays whole
  static (string,string) Split(string value){value=value??"";int sp=value.IndexOf(' ');return sp>0&&char.IsDigit(value[0])?(value.Substring(0,sp),value.Substring(sp+1)):(value,"");}
  // The spread on a ruler from zero: its zones are the limits of Latency (green, amber, red); the part up to the median is bright, the rest quiet, each answer a tick,
  // the median a white mark. Under the ruler three numbers stand on three fixed anchors (left edge, middle of the card, right edge): they cannot meet.
  static void FillZones(Graphics g,RectangleF track,long max,long from,long to,float amount){
   var zones=new[]{new Zone(0,Latency.GoodBelowMs,Theme.Green),new Zone(Latency.GoodBelowMs,Latency.SlowFromMs,Theme.Amber),new Zone(Latency.SlowFromMs,long.MaxValue,Theme.Red)};
   foreach(var z in zones){
    long a=Math.Max(from,z.From),b=Math.Min(to,z.To);if(b<=a)continue;
    float x0=track.X+track.Width*Math.Min(1f,a/(float)max),x1=track.X+track.Width*Math.Min(1f,b/(float)max);
    using(var br=new SolidBrush(Theme.Mix(Theme.Field,z.Tone,amount)))g.FillRectangle(br,x0,track.Y,Math.Max(0,x1-x0),track.Height);
   }
  }
  // "min 38 мс" · "медиана 57 мс" · "max 296 мс" as pieces; variant 0 spells it out, 1 shortens the middle word, 2 leaves the units out, 3 keeps only figures (never needed in practice)
  static Seg[][] SpreadLabels(Font sf,Font nf,long lo,long med,long hi,bool stale,int variant){
   string ms=L.En?" ms":" мс",mid=variant>=1?(L.En?"med ":"мед. "):(L.En?"median ":"медиана ");
   Seg[] Label(string word,long v){var list=new List<Seg>();if(word.Length>0)list.Add(new Seg(word,sf,Theme.Muted));list.Add(new Seg(v.ToString(),nf,Tinted(Latency.Of(v),stale)));if(variant<2)list.Add(new Seg(ms,sf,Theme.Muted));return list.ToArray();}
   return new[]{Label("min ",lo),Label(variant>=3?"":mid,med),Label("max ",hi)};
  }
  static int LabelW(Graphics g,Seg[] parts){int sum=0;foreach(var p in parts)sum+=TextW(g,p.Text,p.Font);return sum;}
  // The three labels' rectangles (x relative to the band, width in px) inside a band of this width: the left one at the left edge, the middle one centred on the band,
  // the right one at the right edge. The first variant in which neighbours keep a gap is used; the painter uses the same code.
  internal static RectangleF[] SpreadLabelRects(Graphics g,float width,float k,long lo,long med,long hi,out int variant)=>SpreadLabelRects(g,width,k,lo,med,hi,false,out variant);
  static RectangleF[] SpreadLabelRects(Graphics g,float width,float k,long lo,long med,long hi,bool stale,out int variant){
   using(var sf=Theme.F(7f))using(var nf=Theme.F(7.5f,true)){
    float gap=8*k;variant=3;RectangleF[] rects=null;
    for(int v=0;v<=3;v++){
     var p=SpreadLabels(sf,nf,lo,med,hi,stale,v);float w1=LabelW(g,p[0]),w2=LabelW(g,p[1]),w3=LabelW(g,p[2]);
     rects=new[]{new RectangleF(0,0,w1,1),new RectangleF(width/2f-w2/2f,0,w2,1),new RectangleF(width-w3,0,w3,1)};
     variant=v;
     if(rects[0].Right+gap<=rects[1].Left&&rects[1].Right+gap<=rects[2].Left)break;
    }
    return rects;
   }
  }
  void PaintSpread(Graphics g,RectangleF r,float k){
   var s=data.Samples;bool stale=data.Stale;
   using(var sf=Theme.F(7f))using(var nf=Theme.F(7.5f,true)){
    float rowH=11*k;
    if(s.Length<2){   // nothing to draw a ruler of: the caption and what is known
     string caption=L.En?"spread":"разброс";int capW=TextW(g,caption,sf);float ry=r.Y+(r.Height-rowH)/2f;
     TextRenderer.DrawText(g,caption,sf,Rectangle.Round(new RectangleF(r.X,ry,capW+2,rowH)),Theme.Muted,Mid);
     var legend=new Rectangle((int)(r.X+capW+12*k),(int)ry,(int)Math.Max(1,r.Width-capW-12*k),(int)rowH);
     TextRenderer.DrawText(g,s.Length==0?(L.En?"no answer times":"нет времён ответа"):(L.En?"one sample":"один ответ"),sf,legend,Theme.Muted,Mid|TextFormatFlags.Right);
     return;
    }
    var ordered=s.OrderBy(v=>v).ToArray();long lo=ordered[0],hi=ordered[ordered.Length-1],med=ordered[(ordered.Length-1)/2],max=NiceMax(hi);
    // the numbers under the ruler
    float labY=r.Bottom-rowH;
    var rects=SpreadLabelRects(g,r.Width,k,lo,med,hi,stale,out int variant);
    var parts=SpreadLabels(sf,nf,lo,med,hi,stale,variant);
    for(int i=0;i<3;i++){float px=r.X+rects[i].X;foreach(var p in parts[i]){int pw=TextW(g,p.Text,p.Font);TextRenderer.DrawText(g,p.Text,p.Font,new Rectangle((int)Math.Round(px),(int)labY,pw+2,(int)rowH),p.Ink,One|TextFormatFlags.VerticalCenter);px+=pw;}}
    // the ruler
    float cy=r.Y+(r.Height-rowH)/2f;
    var track=new RectangleF(r.X,cy-2.5f*k,r.Width,5*k);
    float X(long v)=>track.X+track.Width*Math.Min(1f,v/(float)max);
    var save=g.Save();
    using(var clip=Theme.Round(track,track.Height/2f)){
     g.SetClip(clip,CombineMode.Intersect);
     FillZones(g,track,max,0,max,stale?.10f:.20f);
     var bright=RectangleF.FromLTRB(track.X,track.Y,Math.Max(X(med),track.X+track.Height),track.Bottom);
     using(var rp=Theme.Round(bright,track.Height/2f)){g.SetClip(rp,CombineMode.Intersect);FillZones(g,track,max,0,max,stale?.38f:.85f);}
    }
    g.Restore(save);
    foreach(long v in s){float x=X(v);using(var pen=new Pen(Color.FromArgb(stale?120:200,stale?Theme.Muted:Theme.Mix(Latency.Of(v),Color.White,.35f)),1.2f*k){StartCap=LineCap.Round,EndCap=LineCap.Round})g.DrawLine(pen,x,cy-4f*k,x,cy+4f*k);}
    float ad=9*k,ax=X(med);
    using(var b=new SolidBrush(Theme.Field))g.FillEllipse(b,ax-ad/2-1.5f*k,cy-ad/2-1.5f*k,ad+3*k,ad+3*k);
    using(var b=new SolidBrush(stale?Theme.Mix(Theme.Muted,Color.White,.65f):Color.White))g.FillEllipse(b,ax-ad/2,cy-ad/2,ad,ad);
   }
  }
 }

 internal sealed class PillSelect:Surface{
  public int HeaderHeight;
  public bool AccentBorder;   // the card of the chosen profile: its outline is the accent colour, not the neutral line
  public readonly List<string> Items=new List<string>();int sel;ContextMenuStrip menu;readonly Anim hoverA;
  public event EventHandler SelectedIndexChanged;
  public event EventHandler DropDownOpening;   // the list is about to open (the owner may start measuring)
  public Func<int,(string text,Color dot)> Detail;
  public Func<(string text,Color dot)> HeaderStatus;
  readonly ToolTip tip=new ToolTip{InitialDelay=400,ReshowDelay=100,AutoPopDelay=10000};string tipText;
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public string Tip{get=>tipText;set=>tipText=value;}   // kept for accessibility/tests; never shown as a hover popup
  protected override void Dispose(bool disposing){if(disposing)tip.Dispose();base.Dispose(disposing);}
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public int SelectedIndex{get=>sel;set{if(value<0||value>=Items.Count||value==sel)return;sel=value;Invalidate();AccessibilityNotifyClients(AccessibleEvents.Selection,-1);AccessibilityNotifyClients(AccessibleEvents.ValueChange,-1);SelectedIndexChanged?.Invoke(this,EventArgs.Empty);}}
  public void SetSelectedSilently(int i){if(i>=0&&i<Items.Count){sel=i;Invalidate();AccessibilityNotifyClients(AccessibleEvents.Selection,-1);AccessibilityNotifyClients(AccessibleEvents.ValueChange,-1);}}
  internal void OpenForTest()=>OnClick(EventArgs.Empty);
  internal ContextMenuStrip OpenMenu=>menu;
  public void RefreshDetails(){Invalidate();if(menu!=null&&!menu.IsDisposed)menu.Invalidate();}
  protected override AccessibleObject CreateAccessibilityInstance()=>new SelectionAccessible(this);
  sealed class SelectionAccessible:ControlAccessibleObject{
   readonly PillSelect owner;public SelectionAccessible(PillSelect c):base(c){owner=c;}
   public override string DefaultAction=>L.En?"Open":"Открыть";
   public override void DoDefaultAction(){if(owner.Enabled)owner.OnClick(EventArgs.Empty);}
   public override string Value {get=>owner.sel>=0&&owner.sel<owner.Items.Count?owner.Items[owner.sel]:"";set{}}
   public override int GetChildCount()=>owner.Items.Count;
   public override AccessibleObject GetChild(int index)=>index>=0&&index<GetChildCount()?new ItemAccessible(owner,index):null;
   public override AccessibleObject GetSelected()=>GetChild(owner.sel);
  }
  sealed class ItemAccessible:AccessibleObject{
   readonly PillSelect owner;readonly int index;public ItemAccessible(PillSelect c,int i){owner=c;index=i;}
   public override string Name {get=>index<owner.Items.Count?owner.Items[index]:"";set{}}
   public override AccessibleRole Role=>AccessibleRole.ListItem;
   public override AccessibleObject Parent=>owner.AccessibilityObject;
   public override AccessibleStates State=>AccessibleStates.Selectable|(owner.Enabled?AccessibleStates.None:AccessibleStates.Unavailable)|(owner.sel==index?AccessibleStates.Selected:AccessibleStates.None)|(owner.Focused&&owner.sel==index?AccessibleStates.Focused:AccessibleStates.None);
   public override Rectangle Bounds=>owner.RectangleToScreen(owner.ClientRectangle);
   public override void Select(AccessibleSelection flags){if(owner.Enabled){owner.SelectedIndex=index;}}
   public override void DoDefaultAction()=>Select(AccessibleSelection.TakeSelection);
  }
  public PillSelect(){Cursor=Cursors.Hand;Font=Theme.F(10);AccessibleRole=AccessibleRole.ComboBox;hoverA=new Anim(this,0f,Theme.HoverMs);}
  protected override bool IsInputKey(Keys key)=>key==Keys.Space||key==Keys.Enter||key==Keys.Down||key==Keys.Up||base.IsInputKey(key);
  protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space||e.KeyCode==Keys.Enter||e.KeyCode==Keys.Down){OnClick(EventArgs.Empty);e.Handled=true;}base.OnKeyDown(e);}
  // Windows reports a leave when the pointer passes onto a child window (the board of the card), although it is still on the card: such a leave is not one.
  [DllImport("user32.dll")]static extern IntPtr WindowFromPoint(Point point);
  [DllImport("user32.dll")]static extern bool IsChild(IntPtr parent,IntPtr window);
  bool PointerOnCard(){if(!IsHandleCreated||!Visible)return false;var at=Cursor.Position;if(!RectangleToScreen(ClientRectangle).Contains(at))return false;var w=WindowFromPoint(at);return w==Handle||IsChild(Handle,w);}
  protected override void OnMouseEnter(EventArgs e){hoverA.To(1f);base.OnMouseEnter(e);}
  protected override void OnMouseMove(MouseEventArgs e){if(Enabled)hoverA.To(1f);base.OnMouseMove(e);}
  protected override void OnMouseLeave(EventArgs e){if(!PointerOnCard())hoverA.To(0f);base.OnMouseLeave(e);}
  protected override void OnEnabledChanged(EventArgs e){if(!Enabled)hoverA.To(0f,true);Invalidate();base.OnEnabledChanged(e);}
  internal float HoverForTest=>hoverA.Value;
  internal void HoverForTestSet(bool on){if(on)OnMouseEnter(EventArgs.Empty);else OnMouseLeave(EventArgs.Empty);}
  internal const TextFormatFlags Flags=TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding;
  protected override void OnClick(EventArgs e){
   base.OnClick(e);if(!Enabled||Items.Count==0)return;if(menu!=null&&!menu.IsDisposed){menu.Close();return;}
   DropDownOpening?.Invoke(this,EventArgs.Empty);
   float k=K;
   var m=menu=new ContextMenuStrip{Renderer=new DetailRenderer(new DarkColors(),DetailOf,()=>sel){RoundedEdges=false},ShowImageMargin=false,ShowCheckMargin=false,AutoSize=false,Width=Width,BackColor=Theme.Card,ForeColor=Theme.Text,Font=Theme.F(9.5f),Padding=new Padding(0,(int)Math.Round(4*k),0,(int)Math.Round(4*k))};
   int rowH=(int)Math.Round(34*k);
   for(int i=0;i<Items.Count;i++){int idx=i;var it=new ToolStripMenuItem(Items[i]){ForeColor=Theme.Text,AutoSize=false,Size=new Size(Width-2,rowH),Padding=Padding.Empty,Margin=Padding.Empty,Tag=idx};it.Click+=(s,a)=>SelectedIndex=idx;m.Items.Add(it);}
   var viewport=Parent?.RectangleToScreen(Parent.ClientRectangle)??Screen.FromControl(this).WorkingArea;
   var own=RectangleToScreen(ClientRectangle);int gap=(int)Math.Round(4*k);
   var popup=PopupBounds(viewport,own,Width,m.Items.Count*rowH+(int)Math.Round(8*k)+4,gap);
   m.MaximumSize=popup.Size;m.Height=popup.Height;
   // ToolStripDropDown uses Windows' menu reveal/dismiss animation and reduced-motion preference.
   m.Opened+=(s,a)=>{
    // Native menu creation may rescale its frame/items on a different-DPI monitor.
    // Clamp the final outer window as well as the pre-creation layout rectangle.
    var view=Parent?.RectangleToScreen(Parent.ClientRectangle)??Screen.FromControl(this).WorkingArea;
    if(m.Width>view.Width||m.Height>view.Height)m.Size=new Size(Math.Min(m.Width,view.Width),Math.Min(m.Height,view.Height));
    m.Location=new Point(Math.Clamp(m.Left,view.Left,Math.Max(view.Left,view.Right-m.Width)),Math.Clamp(m.Top,view.Top,Math.Max(view.Top,view.Bottom-m.Height)));
    RoundPopup(m.Handle);
   };
   m.Closed+=(s,a)=>{if(menu==m)menu=null;try{if(IsHandleCreated&&!IsDisposed)BeginInvoke(new Action(m.Dispose));else m.Dispose();}catch{}};   // one menu per opening: released once it has closed and its click is handled
   m.Show(popup.Location);
  }
  internal static Rectangle PopupBounds(Rectangle viewport,Rectangle anchor,int width,int desired,int gap){
   int top=Math.Clamp(anchor.Top,viewport.Top,viewport.Bottom),bottom=Math.Clamp(anchor.Bottom,viewport.Top,viewport.Bottom);
   int below=Math.Max(0,viewport.Bottom-bottom-gap),above=Math.Max(0,top-viewport.Top-gap);bool up=desired>below&&above>below;
   int height=Math.Min(desired,Math.Max(1,up?above:below));width=Math.Min(width,viewport.Width);
   return new Rectangle(Math.Clamp(anchor.Left,viewport.Left,viewport.Right-width),up?top-gap-height:bottom+gap,width,height);
  }
  // the profile card's mark: a small tile with a server (two stacked units)
  static void PaintServerTile(Graphics g,RectangleF r,float k){
   using(var p=Theme.Round(r,8*k))using(var b=new SolidBrush(Theme.Soft))g.FillPath(b,p);
   float w=r.Width*.5f,h=r.Height*.2f,x=r.X+(r.Width-w)/2f;
   for(int i=0;i<2;i++){
    var unit=new RectangleF(x,r.Y+r.Height*(i==0?.26f:.54f),w,h);
    using(var p=Theme.Round(unit,2.5f*k))using(var pen=new Pen(Theme.AccentHover,1.4f*k))g.DrawPath(pen,p);
    using(var b=new SolidBrush(Theme.AccentHover))g.FillEllipse(b,unit.Right-h*.95f,unit.Y+h*.3f,h*.4f,h*.4f);
   }
  }
  (string text,Color dot) DetailOf(int i)=>Detail!=null?Detail(i):("",Color.Empty);
  internal const float DetailW=100;   // the column of the list that holds a detail (units)
  static string Shown(string text)=>string.IsNullOrEmpty(text)?"—":text;
  internal static float SignalW(float k)=>16*k;
  // The ping as a signal meter: three rising bars, lit by how quick the answer was (the colour says good or slow); a profile that did not answer lights none.
  internal static void PaintSignal(Graphics g,RectangleF r,string text,Color ink,float k){
   int ms=-1;if(!string.IsNullOrEmpty(text)&&char.IsDigit(text[0])){int n=0;while(n<text.Length&&char.IsDigit(text[n]))n++;int.TryParse(text.Substring(0,n),out ms);}
   int lit=ms<0?0:ms<=80?3:ms<=200?2:1;
   float bw=4*k,gap=2*k,h=r.Height;
   var old=g.SmoothingMode;g.SmoothingMode=SmoothingMode.AntiAlias;
   for(int i=0;i<3;i++){
    float bh=h*(.5f+.25f*i);var bar=new RectangleF(r.X+i*(bw+gap),r.Bottom-bh,bw,bh);
    using(var p=Theme.Round(bar,1*k))using(var b=new SolidBrush(i<lit?ink:Theme.Mix(Theme.Card,Theme.Line,.9f)))g.FillPath(b,p);
   }
   g.SmoothingMode=old;
  }
  // A detail at the right of a row: the words right-aligned to the edge, the signal meter just before them.
  internal static void PaintDetail(Graphics g,Font font,(string text,Color dot) d,Rectangle col,float k,bool alwaysShow){
   if(string.IsNullOrEmpty(d.text)&&!alwaysShow)return;
   string text=Shown(d.text);if(text.Length>0&&char.IsDigit(text[0]))text="TCP "+text;Color ink=string.IsNullOrEmpty(d.text)||d.dot==Theme.Muted?Theme.Muted:Theme.Text;
   float sw=SignalW(k),sh=12*k;int room=Math.Max(0,col.Width-(int)(sw+5*k));
   int tw=Math.Min(room,TextRenderer.MeasureText(g,text,font,Size.Empty,Flags).Width);
   // the meter stands right before the words, both right-aligned to the edge
   if(d.dot!=Color.Empty&&!string.IsNullOrEmpty(d.text))PaintSignal(g,new RectangleF(col.Right-tw-5*k-sw,col.Y+(col.Height-sh)/2f-.5f*k,sw,sh),d.text,d.dot,k);
   TextRenderer.DrawText(g,text,font,new Rectangle(col.X+(int)(sw+5*k),col.Y,room,col.Height),ink,Flags|TextFormatFlags.Right|TextFormatFlags.EndEllipsis);
  }
  // A label of the list "name  ·  tag" (the catalog marks experimental and own profiles that way): the name, and the tag as a small badge.
  internal static (string name,string tag) SplitLabel(string label){label=label??"";int at=label.LastIndexOf("  ·  ",StringComparison.Ordinal);return at>0?(label.Substring(0,at),label.Substring(at+5).Trim()):(label,"");}
  internal static void PaintTag(Graphics g,string tag,RectangleF at,Color tint,float k){
   using(var f=Theme.F(7f,true)){
    int w=TextRenderer.MeasureText(g,tag,f,Size.Empty,Flags).Width+(int)(12*k),h=(int)(16*k);
    var r=new RectangleF(at.X,at.Y+(at.Height-h)/2f,w,h);
    var old=g.SmoothingMode;g.SmoothingMode=SmoothingMode.AntiAlias;
    using(var p=Theme.Round(r,h/2f))using(var b=new SolidBrush(Theme.Mix(Theme.Card,tint,.16f)))g.FillPath(b,p);
    g.SmoothingMode=old;
    TextRenderer.DrawText(g,tag,f,Rectangle.Round(r),Theme.Mix(tint,Color.White,.15f),Flags|TextFormatFlags.HorizontalCenter);
   }
  }
  internal static Color TagTint(string tag)=>tag.StartsWith("эксп",StringComparison.Ordinal)||tag.StartsWith("exp",StringComparison.OrdinalIgnoreCase)?Theme.Amber:Theme.AccentHover;
  sealed class DetailRenderer:ToolStripProfessionalRenderer{
   readonly Func<int,(string text,Color dot)> detail;readonly Func<int> selected;
   public DetailRenderer(ProfessionalColorTable t,Func<int,(string text,Color dot)> detail,Func<int> selected):base(t){this.detail=detail;this.selected=selected;}
   // the window draws its own rounded border (Windows 11); the list paints no square frame of its own
   protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e){}
   protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e){using(var b=new SolidBrush(Theme.Card))e.Graphics.FillRectangle(b,e.AffectedBounds);}
   protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e){
    var g=e.Graphics;float k=e.Item.Owner.DeviceDpi/96f;bool current=e.Item.Tag is int idx&&idx==selected();
    using(var b=new SolidBrush(Theme.Card))g.FillRectangle(b,new Rectangle(Point.Empty,e.Item.Size));
    if(!e.Item.Selected&&!current)return;
    var old=g.SmoothingMode;g.SmoothingMode=SmoothingMode.AntiAlias;
    var r=new RectangleF(4*k,1.5f*k,e.Item.Width-8*k,e.Item.Height-3*k);
    using(var p=Theme.Round(r,8*k))using(var b=new SolidBrush(e.Item.Selected?Theme.Soft:Theme.Mix(Theme.Card,Theme.Accent,.10f)))g.FillPath(b,p);
    if(current)using(var p=Theme.Round(new RectangleF(4*k,r.Y+r.Height/2f-8*k,3*k,16*k),1.5f*k))using(var b=new SolidBrush(Theme.AccentHover))g.FillPath(b,p);
    g.SmoothingMode=old;
   }
   protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e){
    if(!(e.Item is ToolStripMenuItem mi)||!(mi.Tag is int idx)){base.OnRenderItemText(e);return;}
    float k=e.Item.Owner.DeviceDpi/96f;int colW=(int)Math.Round(DetailW*k);   // the item's own width: its padding on the right is the room kept for this column
    int pad=(int)Math.Round(16*k);bool current=idx==selected();
    var (name,tag)=SplitLabel(e.Text);
    using(var nf=Theme.F(e.TextFont.SizeInPoints,current)){
     int room=Math.Max(1,e.Item.Width-colW-3*pad),nw=Math.Min(room,TextRenderer.MeasureText(e.Graphics,name,nf,Size.Empty,Flags).Width);
     if(tag.Length>0)using(var tf=Theme.F(7f,true))nw=Math.Min(nw,room-TextRenderer.MeasureText(e.Graphics,tag,tf,Size.Empty,Flags).Width-(int)(20*k));
     TextRenderer.DrawText(e.Graphics,name,nf,new Rectangle(pad,0,Math.Max(1,nw),e.Item.Height),current?Color.White:Theme.Text,Flags|TextFormatFlags.EndEllipsis);
     if(tag.Length>0)PaintTag(e.Graphics,tag,new RectangleF(pad+nw+8*k,0,0,e.Item.Height),TagTint(tag),k);
    }
    PaintDetail(e.Graphics,e.TextFont,detail(idx),new Rectangle(e.Item.Width-colW-(int)(12*k),0,colW,e.Item.Height),k,true);
   }
  }
  [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int value,int size);
  static void RoundPopup(IntPtr handle){try{int round=2;DwmSetWindowAttribute(handle,33,ref round,4);int border=ColorTranslator.ToWin32(Theme.Line);DwmSetWindowAttribute(handle,34,ref border,4);}catch{}}
  protected override void OnPaint(PaintEventArgs e){
   var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;float k=K;
   int titleHeight=HeaderHeight>0?Math.Min(Height,(int)Math.Round(HeaderHeight*k)):Height;
   float lift=Enabled?hoverA.Value:0f;   // one eased value drives fill and outline together: no stepped or layered fade
   using(var p=Theme.Round(new RectangleF(0,0,Width-1,Height-1),12*k))using(var b=new SolidBrush(Theme.Mix(Theme.Field,Theme.Soft,lift)))using(var pen=new Pen(Focused?Theme.Focus:AccentBorder?Theme.Mix(Theme.Line,Theme.Accent,.8f+.2f*lift):Theme.Mix(Theme.Line,Theme.BorderStrong,lift),AccentBorder?1.3f*k:1f)){g.FillPath(b,p);g.DrawPath(pen,p);}
   string t=sel<Items.Count?Items[sel]:"";
   (string text,Color dot) d=HeaderStatus!=null?HeaderStatus():Detail!=null&&sel<Items.Count?Detail(sel):("",Color.Empty);
   int chevron=(int)(36*k),pad=(int)(14*k);int detailW=0;
   if(HeaderHeight>0){float tile=30*k;PaintServerTile(g,new RectangleF(12*k,(titleHeight-tile)/2f,tile,tile),k);pad=(int)(12*k+tile+10*k);}
   if(!string.IsNullOrEmpty(d.text)){
    using(var df=Theme.F(HeaderStatus!=null?7.5f:9f)){
     int tw=TextRenderer.MeasureText(g,d.text,df,Size.Empty,Flags).Width;detailW=(int)(SignalW(k)+5*k)+tw;
     if(HeaderStatus==null)PaintDetail(g,df,d,new Rectangle(Width-chevron-detailW,0,detailW,titleHeight),k,false);
     else{
      int nameRoom=Math.Max(1,Width-pad-chevron-detailW-(int)(8*k));int nameWidth=Math.Min(nameRoom,TextRenderer.MeasureText(g,t,Font,Size.Empty,Flags).Width);int sx=pad+nameWidth+(int)(8*k);
      using(var dotBrush=new SolidBrush(d.dot))g.FillEllipse(dotBrush,sx,(titleHeight-6*k)/2,6*k,6*k);
      TextRenderer.DrawText(g,d.text,df,new Rectangle(sx+(int)(10*k),0,Math.Max(1,Width-chevron-sx-(int)(10*k)),titleHeight),d.dot,Flags|TextFormatFlags.EndEllipsis);
     }
    }
   }
   TextRenderer.DrawText(g,t,Font,new Rectangle(pad,0,Math.Max(0,Width-pad-chevron-(detailW>0?detailW+(int)(10*k):0)),titleHeight),Enabled?Theme.Text:Theme.Muted,Flags|TextFormatFlags.EndEllipsis);
   if(HeaderHeight>0&&titleHeight<Height)using(var pen=new Pen(Theme.Mix(Theme.Field,Theme.Line,.85f),k))g.DrawLine(pen,12*k,titleHeight,Width-12*k,titleHeight);   // the header and the board below are one card, divided by a hairline
   float cx=Width-20*k,cy=titleHeight/2f;using(var pen=new Pen(Theme.Muted,1.8f*k){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round})g.DrawLines(pen,new[]{new PointF(cx-4*k,cy-2*k),new PointF(cx,cy+2*k),new PointF(cx+4*k,cy-2*k)});
  }
 }

 internal sealed class BenchRow {
  public string Id,Name,Slot;public ProfileResult Result;public bool Running,Stale,Selected;public string Note,CatalogNote;
 }

 // Benchmark results: rank, profile + verdict, availability, median/p95 DoH latency, TLS time. Colour-coded; the best row is outlined.
 internal sealed class BenchTable:Surface{
  public readonly List<BenchRow> Rows=new List<BenchRow>();
  public float Progress=-1;
  int tipRow=-2;   // the row under the cursor (highlight only: the details live in the log and the diagnostics, not in a hover popup)
  int sortColumn=-1;bool descending;int focusedRow;
  public BenchTable(){Font=Theme.F(8.5f);AccessibleRole=AccessibleRole.Table;AccessibleName=L.T("Замер");}
  internal BenchRow[] DisplayRows{
   get{
    if(sortColumn<0)return Rows.ToArray();
    if(sortColumn==1)return (descending?Rows.OrderByDescending(r=>r.Name,StringComparer.OrdinalIgnoreCase):Rows.OrderBy(r=>r.Name,StringComparer.OrdinalIgnoreCase)).ToArray();
    double Value(BenchRow row){var r=row.Result;if(r==null||!r.Final)return double.NaN;return sortColumn==0?(r.Rank>0?r.Rank:double.NaN):sortColumn==2?r.Availability:sortColumn==3?r.MedianMs:r.TlsMs;}
    bool Missing(BenchRow row){double n=Value(row);return double.IsNaN(n)||n<0;}
    return Rows.OrderBy(r=>Missing(r)?1:0).ThenBy(r=>Missing(r)?0:Value(r)*(descending?-1:1)).ToArray();
   }
  }
  internal void SortColumn(int column){if(column<0||column>=ColX.Length)return;descending=sortColumn==column?!descending:column==2;sortColumn=column;tipRow=-2;Invalidate();}
  protected override bool IsInputKey(Keys key)=>key==Keys.Up||key==Keys.Down||key==Keys.Enter||base.IsInputKey(key);
  protected override void OnKeyDown(KeyEventArgs e){
   if(e.KeyCode==Keys.Up||e.KeyCode==Keys.Down){focusedRow=Math.Clamp(focusedRow+(e.KeyCode==Keys.Down?1:-1),0,Math.Max(0,Rows.Count-1));Invalidate();e.Handled=true;}
   if(e.KeyCode==Keys.Enter&&Rows.Count>0){RowClicked?.Invoke(DisplayRows[Math.Min(focusedRow,Rows.Count-1)]);e.Handled=true;}
   base.OnKeyDown(e);
  }
  static readonly float[] ColX={8,30,182,234,290};   // rank, profile, availability, median, TLS (the peak lives in the details)
  float HeaderH=>26*K;
  float RowH=>Rows.Count==0?36*K:Math.Min(60*K,(Height-HeaderH-6*K)/Rows.Count);   // the table fills its page; rows grow up to 60 dp
  public static string VerdictText(string v)=>L.T(v=="Good"?"Работает":v=="Slow"?"Медленно":v=="Flaky"?"Нестабильно":v=="Partial"?"Частично":v=="Dead"?"Недоступен":v=="NotMeasured"?"Не измерен":"Измеряю…");
  public static Color VerdictColor(string v)=>v=="Good"?Theme.Green:v=="Slow"||v=="Flaky"||v=="Partial"?Theme.Amber:v=="Dead"?Theme.Red:v=="NotMeasured"?Theme.Muted:Theme.Accent;
  static Color AvailColor(double a)=>Latency.OfAvailability(a);
  static Color LatColor(long ms)=>Latency.Of(ms);
  // The row states, all told apart: hover (a faint lift), chosen (the fill of the active tab of the bar below), chosen under the cursor (that fill, lifted), measuring (a blue breath).
  // Keyboard focus is the outline drawn over any of them.
  internal static Color RowTint(bool selected,bool hover,bool running)=>selected?(hover?Theme.Mix(Theme.NavSel,Color.White,.07f):Theme.NavSel):running?Theme.Mix(Theme.Card,Theme.Accent,.06f):hover?Theme.Mix(Theme.Card,Color.White,.035f):Theme.Card;
  static string Ms(long ms)=>ms<0?"—":ms.ToString();
  public string Describe(BenchRow r) {
   var x=r.Result;if(x==null)return r.Name+(r.Note==null?"":"\n"+r.Note);
   var s=new System.Text.StringBuilder();
   s.Append(r.Name+"  ·  "+VerdictText(x.Verdict));
   s.Append("\n"+L.T("Доступность: ")+(int)Math.Round(x.Availability*100)+"% ("+x.Successes+L.T(" из ")+x.Attempts+L.T(" раундов)"));
   s.Append("\n"+L.T("Задержка DoH: медиана ")+Ms(x.MedianMs)+L.T(" мс")+", "+(L.En?"peak ":"пик ")+Ms(x.P95Ms)+L.T(" мс")+"  ·  TLS "+Ms(x.TlsMs)+L.T(" мс"));
   s.Append("\n"+L.T("Стабильность ответов: ")+(int)Math.Round(x.Stability*100)+"%  ·  "+L.T("достижимость адресов: ")+(int)Math.Round(x.Reach*100)+"%");
   foreach(var d in x.Domains)s.Append("\n"+d.Host.Split('.')[0]+": "+(d.Resolved&&d.TlsOk?L.T("резолв + TLS ок (")+d.Reachable+"/"+d.Answers+")":d.Resolved?L.T("резолв ок, TLS не прошёл"):L.T("нет ответа")));
   if(!string.IsNullOrEmpty(x.Detail))s.Append("\n"+x.Detail);
   if(r.Note!=null)s.Append("\n"+r.Note);
   return s.ToString();   // long provider notes live in the details panel (click a row), not in this tooltip

  }
  public event Action<BenchRow> RowClicked;
  internal void ClickRowForTest(BenchRow row)=>RowClicked?.Invoke(row);
  internal RectangleF RowRectForTest(int i){float k=K,rh=RowH;return new RectangleF(5*k,HeaderH+i*rh+2*k,Width-10*k,rh-4*k);}
  protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);if(tipRow!=-2){tipRow=-2;Invalidate();}}
  protected override void OnMouseClick(MouseEventArgs e){
   base.OnMouseClick(e);
   if(e.Y<HeaderH){int column=0;while(column<ColX.Length-1&&e.X>=ColX[column+1]*K)column++;SortColumn(column);return;}
   int i=e.Y<HeaderH?-1:(int)((e.Y-HeaderH)/RowH);
   if(i>=0&&i<Rows.Count){focusedRow=i;RowClicked?.Invoke(DisplayRows[i]);}
  }
  protected override void OnMouseMove(MouseEventArgs e){
   base.OnMouseMove(e);
   int i=e.Y<HeaderH?-1:(int)((e.Y-HeaderH)/RowH);if(i>=Rows.Count)i=-1;
   if(i!=tipRow){tipRow=i;Invalidate();}Cursor=e.Y<HeaderH||i>=0?Cursors.Hand:Cursors.Default;
  }
  // The row's second line must always be readable: the font gives way (down to 6.5 pt) before an ellipsis is accepted.
  static Font FitFont(Graphics g,string text,float pt,int width){
   for(float size=pt;size>=6.5f;size-=.25f){var f=Theme.F(size);if(TextRenderer.MeasureText(g,text,f,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width<=width)return f;f.Dispose();}
   return Theme.F(6.5f);
  }
  // A measured value as plain type: the figure in its colour, the unit small and muted after it, right-aligned to the column's edge.
  // A measured figure sits on a soft chip of its own colour (green good, amber so-so, red bad), so the table reads at a glance; «—» stays bare.
  static void Value(Graphics g,string text,Font font,Font unitFont,RectangleF cell,Color ink,float k,float chip=0f){
   const TextFormatFlags f=TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding;
   int sp=text.IndexOf(' ');string num=sp>0?text.Substring(0,sp):text,unit=sp>0?text.Substring(sp+1):"";
   int right=(int)(cell.Right-8*k),uw=unit.Length>0?TextRenderer.MeasureText(g,unit,unitFont,Size.Empty,f).Width:0,nw=TextRenderer.MeasureText(g,num,font,Size.Empty,f).Width;
   int nx=right-uw-(unit.Length>0?(int)(2*k):0)-nw;
   if(chip>0){
    float ch=Math.Min(cell.Height-6*k,22*k),cx=Math.Max(cell.X+1*k,nx-6*k),cw=right+6*k-cx;
    var r=new RectangleF(cx,cell.Y+(cell.Height-ch)/2f,cw,ch);
    var old=g.SmoothingMode;g.SmoothingMode=SmoothingMode.AntiAlias;
    using(var p=Theme.Round(r,ch/2f))using(var b=new SolidBrush(Theme.Mix(Theme.Card,ink,chip)))g.FillPath(b,p);
    g.SmoothingMode=old;
   }
   TextRenderer.DrawText(g,num,font,new Rectangle(nx,(int)cell.Y,nw+1,(int)cell.Height),ink,f);
   if(unit.Length>0){
    // the unit stands on the figure's baseline
    int fh=TextRenderer.MeasureText(g,num,font,Size.Empty,f).Height,uh=TextRenderer.MeasureText(g,unit,unitFont,Size.Empty,f).Height;
    float bottom=cell.Y+(cell.Height+fh)/2f-(fh-uh)*.22f;   // the descent is about a fifth of a line: the two lines end on one baseline
    TextRenderer.DrawText(g,unit,unitFont,new Rectangle(right-uw,(int)cell.Y,uw+2,(int)Math.Round(bottom-cell.Y)),Theme.Muted,TextFormatFlags.Bottom|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding|TextFormatFlags.NoClipping);
   }
  }
  // One table on one card: rows divided by hairlines (no box per row, no box per value). The profile in use carries the accent bar and a tint,
  // the winner of a fresh measurement a green rank and the «лучший» badge. A measurement from an earlier run keeps its verdicts and muted figures.
  protected override void OnPaint(PaintEventArgs e){
   var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;float k=K;
   using(var p=Theme.Round(new RectangleF(0,0,Width-1,Height-1),16*k))using(var b=new SolidBrush(Theme.Card))g.FillPath(b,p);
   var flags=TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis;
   float tableRight=Width/k-8;   // the last column ends here (layout units)
   float ColEnd(int c)=>c<ColX.Length-1?ColX[c+1]:tableRight;
   using(var hf=Theme.F(7f,true)) {
    string[] heads={"#",L.T("ПРОФИЛЬ"),L.T("ДОСТ."),(L.En?"MED.":"МЕД."),"TLS"};
    for(int c=0;c<heads.Length;c++){
     float x=ColX[c]*k,w=(ColEnd(c)-ColX[c])*k;
     var cell=new Rectangle((int)x,0,(int)(c>=2?w-8*k:w),(int)HeaderH);
     Color ink=c==sortColumn?Theme.Text:Theme.Muted;
     TextRenderer.DrawText(g,heads[c]+(c==sortColumn?(descending?" ↓":" ↑"):""),hf,cell,ink,flags|(c==0?TextFormatFlags.HorizontalCenter:c>=2?TextFormatFlags.Right:TextFormatFlags.Left));
    }
   }
   using(var pen=new Pen(Theme.Mix(Theme.Card,Theme.Line,.7f),k))g.DrawLine(pen,12*k,HeaderH-1,Width-12*k,HeaderH-1);
   float rh=RowH;
   var displayed=DisplayRows;
   using(var nf=Theme.F(9f,true))using(var vf=Theme.F(9.5f,true))using(var uf=Theme.F(7f))using(var rf=Theme.F(8.5f,true))
   for(int i=0;i<displayed.Length;i++) {
    var row=displayed[i];var r=row.Result;bool best=r!=null&&r.Best&&r.Final&&!row.Stale;
    var rect=new RectangleF(5*k,HeaderH+i*rh+2*k,Width-10*k,rh-4*k);
    if(row.Selected||i==tipRow||row.Running){
     using(var path=Theme.Round(rect,10*k))using(var b=new SolidBrush(RowTint(row.Selected,i==tipRow,row.Running)))g.FillPath(b,path);
    }
    if(Focused&&ShowFocusCues&&i==focusedRow)using(var path=Theme.Round(rect,10*k))using(var pen=new Pen(Theme.Focus,1.5f*k))g.DrawPath(pen,path);
    if(i<displayed.Length-1&&!row.Selected&&!displayed[i+1].Selected)using(var pen=new Pen(Theme.Mix(Theme.Card,Theme.Line,.55f),k))g.DrawLine(pen,ColX[1]*k,HeaderH+(i+1)*rh,Width-12*k,HeaderH+(i+1)*rh);
    int ry=(int)rect.Y,rhh=(int)rect.Height;
    // rank: a plain figure, green for the winner of a fresh measurement
    string rank=r!=null&&r.Final&&r.Rank>0?r.Rank.ToString():"";
    if(rank.Length>0)TextRenderer.DrawText(g,rank,rf,new Rectangle((int)(ColX[0]*k),ry,(int)((ColX[1]-ColX[0])*k),rhh),best?Theme.Green:Theme.Mix(Theme.Muted,Theme.Card,.25f),flags|TextFormatFlags.HorizontalCenter);
    // name (+ the badge of the winner) and the verdict under it
    int nameX=(int)(ColX[1]*k),nameW=(int)((ColX[2]-ColX[1]-6)*k);
    int lineH=(int)(rhh*.42f),top=ry+(int)(rhh*.08f);
    int nw=Math.Min(nameW,TextRenderer.MeasureText(g,row.Name,nf,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width);
    if(best)using(var tf=Theme.F(7f,true))nw=Math.Min(nw,nameW-TextRenderer.MeasureText(g,L.T("лучший"),tf,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width-(int)(20*k));
    TextRenderer.DrawText(g,row.Name,nf,new Rectangle(nameX,top,Math.Max(1,nw),lineH),Theme.Text,flags);
    if(best)PillSelect.PaintTag(g,L.T("лучший"),new RectangleF(nameX+nw+6*k,top,0,lineH),Theme.Green,k);
    string v=r==null?(row.Running?"Pending":"None"):r.Verdict;
    string status=r==null?(row.Running?L.T("Измеряю…"):row.Stale?(L.En?"Earlier run":"Прошлый замер"):(row.Note??"—")):(v=="Pending"?L.T("Измеряю…")+" "+r.Attempts+"/…":VerdictText(v)+(!row.Stale&&r.Final&&r.Domains.Length>0?"  ·  "+L.T("домены")+" "+r.Domains.Count(d=>d.Resolved&&d.TlsOk)+"/"+r.Domains.Length:""));
    Color statusInk=r==null?(row.Running?Theme.AccentHover:Theme.Muted):v=="Pending"?Theme.AccentHover:row.Stale?Theme.Mix(VerdictColor(v),Theme.Muted,.45f):VerdictColor(v);
    using(var sf=FitFont(g,status,8f,nameW)){
     float dot=6*k;bool showDot=r!=null&&r.Final;
     int sy=ry+(int)(rhh*.52f),sh=(int)(rhh*.38f);
     if(showDot)using(var b=new SolidBrush(statusInk))g.FillEllipse(b,nameX,sy+(sh-dot)/2f,dot,dot);
     int off=showDot?(int)(dot+5*k):0;
     TextRenderer.DrawText(g,status,sf,new Rectangle(nameX+off,sy,nameW-off,sh),showDot?Theme.Mix(statusInk,Theme.Muted,.35f):statusInk,flags);
    }
    // the figures
    for(int c=2;c<ColX.Length;c++){
     var cell=new RectangleF(ColX[c]*k,ry,(ColEnd(c)-ColX[c])*k,rhh);
     string text="—";Color ink=Theme.Mix(Theme.Muted,Theme.Card,.3f);float chip=0f;
     if(r!=null&&r.Attempts>0){
      text=c==2?(int)Math.Round(r.Availability*100)+"%":c==3?(r.MedianMs<0?"—":r.MedianMs+(L.En?" ms":" мс")):(r.TlsMs<0?"—":r.TlsMs+(L.En?" ms":" мс"));
      if(text!="—"){ink=c==2?AvailColor(r.Availability):c==3?LatColor(r.MedianMs):Latency.OfTls(r.TlsMs);chip=.16f;}
      if(row.Stale&&text!="—"){ink=Theme.Mix(ink,Theme.Muted,.35f);chip=.10f;}
     }
     Value(g,text,vf,uf,cell,ink,k,chip);
    }
   }
   if(Progress>=0){
    var track=new RectangleF(12*k,Height-6*k,Width-24*k,2.5f*k);
    using(var b=new SolidBrush(Theme.Mix(Theme.Card,Theme.Line,.6f)))g.FillRectangle(b,track);
    using(var b=new SolidBrush(Theme.Accent))g.FillRectangle(b,track.X,track.Y,track.Width*Math.Min(1f,Progress),track.Height);
   }
  }
 }

 internal sealed class NavBar:Surface{
  // Segoe MDL2 Assets: power, stopwatch, settings, command prompt, info.
  public static readonly string[] Glyphs={"\uE7E8","\uE916","\uE713","\uE756","\uE946"};
  public static readonly string[] Labels={"Мост","Замер","Настройки","Логи","Инфо"};
  public static int Count=>Labels.Length;
  int sel;public event EventHandler SelectedChanged;
  [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]public int Selected{get=>sel;set{if(value>=0&&value<Count&&sel!=value){sel=value;selA.To(value);AccessibleName=L.T(Labels[value]);SelectedChanged?.Invoke(this,EventArgs.Empty);}}}
  int hot=-1;
  readonly Anim selA;public NavBar(){Cursor=Cursors.Hand;AccessibleRole=AccessibleRole.PageTabList;AccessibleName=L.T(Labels[0]);selA=new Anim(this,0f,200);}
  protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);int next=Math.Clamp(e.X*Count/Math.Max(1,Width),0,Count-1);if(hot!=next){hot=next;Invalidate();}}
  protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hot=-1;Invalidate();}
  protected override bool IsInputKey(Keys key)=>key==Keys.Left||key==Keys.Right||key==Keys.Home||key==Keys.End||base.IsInputKey(key);
  protected override void OnKeyDown(KeyEventArgs e){int next=e.KeyCode==Keys.Home?0:e.KeyCode==Keys.End?Count-1:e.KeyCode==Keys.Right?(sel+1)%Count:e.KeyCode==Keys.Left?(sel+Count-1)%Count:sel;if(next!=sel){Selected=next;e.Handled=true;}base.OnKeyDown(e);}
  protected override void OnMouseClick(MouseEventArgs e){if(e.Button!=MouseButtons.Left)return;Focus();int i=Math.Max(0,Math.Min(Count-1,e.X*Count/Math.Max(1,Width)));Selected=i;base.OnMouseClick(e);}
  protected override void OnPaint(PaintEventArgs e){
   Paint(e.Graphics,Width,Height,K,selA.Value);
   if(hot>=0&&hot!=sel)using(var b=new SolidBrush(Color.FromArgb(12,255,255,255)))e.Graphics.FillRectangle(b,Pill(hot,Width,Height,K,Count));
   if(Focused&&ShowFocusCues){var r=Pill(sel,Width,Height,K,Count);r.Inflate(-(int)(4*K),-(int)(5*K));using(var pen=new Pen(Theme.Focus,K))e.Graphics.DrawRectangle(pen,r);}
  }

  // Shared sector boundaries for painting and hit testing; rounding adjacent edges
  // together prevents seams at fractional DPI. The selection slides as one piece.
  internal static Rectangle Pill(float pos,int width,int height,float k,int count){
   float cell=width/(float)count;int x=(int)Math.Round(pos*cell),right=(int)Math.Round((pos+1)*cell);
   return new Rectangle(x,0,right-x,height);
  }
  internal new static void Paint(Graphics g,int width,int height,float k,float pos){
   g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.Half;
   using(var b=new SolidBrush(Theme.Card))g.FillRectangle(b,0,0,width,height);
   int count=Count;
   var pill=Pill(pos,width,height,k,count);
   // A full-height cast segment: no floating pill or inset around the selection.
   using(var b=new SolidBrush(Theme.NavSel))g.FillRectangle(b,pill);
   using(var pen=new Pen(Theme.Line,k))g.DrawLine(pen,0,k/2,width,k/2);
   using(var gf=new Font(Theme.IconFont,13f))using(var lf=Theme.F(7.5f,true)){
    const TextFormatFlags f=TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding|TextFormatFlags.SingleLine|TextFormatFlags.NoClipping;
    int gh=(int)Math.Ceiling(gf.GetHeight(g)),lh=(int)Math.Ceiling(lf.GetHeight(g)),gap=(int)Math.Round(3*k);
    int top=pill.Y+(pill.Height-(gh+gap+lh))/2+(int)Math.Round(k);
    for(int i=0;i<count;i++){
     var cellPill=Pill(i,width,height,k,count);   // this tab's own highlight rectangle: the icon and the word are centred on it
     Color c=Theme.Mix(Theme.Muted,Theme.Text,Math.Max(0f,1f-Math.Abs(i-pos)));
     TextRenderer.DrawText(g,Glyphs[i],gf,new Rectangle(cellPill.X,top,cellPill.Width,gh),c,f);
     TextRenderer.DrawText(g,L.T(Labels[i]),lf,new Rectangle(cellPill.X-(int)(4*k),top+gh+gap,cellPill.Width+(int)(8*k),lh),c,f);
    }
   }
  }
 }
}
