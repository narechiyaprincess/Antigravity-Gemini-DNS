using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AntigravityGeminiDns {
 public static partial class UiSurfaceCheck {
  [DllImport("gdi32.dll")] static extern IntPtr CreatePen(int style,int width,uint colour);
  [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
  readonly record struct ResourceSample(long Private,int Handles,int Gdi,int User);
  static ResourceSample SampleResources(){
   Pump(40);GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Pump(40);
   using(var p=Process.GetCurrentProcess()){p.Refresh();return new ResourceSample(p.PrivateMemorySize64,p.HandleCount,GetGuiResources(p.Handle,0),GetGuiResources(p.Handle,1));}
  }
  static void ResourceCycle(){
   // Same work in each cycle, isolated defaults; no Doctor network probes or settings writes.
   for(int repeat=0;repeat<3;repeat++)using(var window=new MainForm(true)){
    window.Show();Pump(30);
    foreach(int tab in new[]{0,1,2,3,4}){window.SelectTab(tab);Pump(10);}
    window.ShowCustomPanelForTest();Pump(10);window.KeyForTest(Keys.Escape);
    window.ShowBrowserPanelForTest(false);Pump(10);window.BrowserCancelForTest();
    using(var preview=new ReportPreviewForm("Resource lifecycle fixture\r\n"+new string('x',2000))){preview.Show(window);Pump(20);preview.Close();}
    window.Close();
   }
  }
  static bool ResourceSoak(){
   var injected=new List<IntPtr>();
   try{
    ResourceCycle();ResourceCycle(); // Warm native menus/fonts/theme and managed caches before measuring.
    var samples=new List<ResourceSample>{SampleResources()};
    bool fixture=Environment.GetEnvironmentVariable("AGDNS_RESOURCE_FIXTURE_FAIL")=="1";
    for(int cycle=1;cycle<=5;cycle++){
     ResourceCycle();
     if(fixture)for(int n=0;n<24;n++){var pen=CreatePen(0,1,0);if(pen==IntPtr.Zero)throw new InvalidOperationException("GDI fixture allocation failed");injected.Add(pen);}
     var sample=SampleResources();samples.Add(sample);
     Console.WriteLine($"UI_RESOURCE cycle={cycle} private={sample.Private} handles={sample.Handles} gdi={sample.Gdi} user={sample.User}");
    }
    double Slope(Func<ResourceSample,long> value){double mean=samples.Select(s=>(double)value(s)).Average();return samples.Select((s,i)=>(i-2.5)*(value(s)-mean)).Sum()/17.5;}
    double memory=Slope(s=>s.Private),handles=Slope(s=>s.Handles),gdi=Slope(s=>s.Gdi),user=Slope(s=>s.User);
    // Trend after warm-up, not a claim of no leaks. These catch sustained object/handle growth
    // over 15 repeated form/dialog lifetimes while tolerating bounded framework cache growth.
    bool ok=memory<=4*1024*1024&&handles<=12&&gdi<=8&&user<=8;
    Console.WriteLine($"UI_RESOURCE slopes_per_cycle private={memory:F0} handles={handles:F2} gdi={gdi:F2} user={user:F2}; limits=4194304,12,8,8; warmup=6_forms measured=15_forms");
    Console.WriteLine(ok?"UI_RESOURCE_SOAK_PASS":"UI_RESOURCE_SOAK_FAIL");return ok;
   }catch(Exception e){Console.WriteLine("UI_RESOURCE_SOAK_FAIL "+e.GetType().Name+": "+e.Message);return false;}
   finally{foreach(var pen in injected)DeleteObject(pen);Console.WriteLine("UI_RESOURCE_TEARDOWN injected_gdi_released="+injected.Count);}
  }
 }
}
