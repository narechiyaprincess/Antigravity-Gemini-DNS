using System;
using System.Linq;

namespace AntigravityGeminiDns {
 public static partial class OfflineTests {
  static void RunVisualBrandTests(){
   using(var table=new BenchTable()){
    var missing=new BenchRow{Id="missing",Name="Missing",Result=new ProfileResult{Final=true,MedianMs=-1}};
    var slow=new BenchRow{Id="slow",Name="Beta",Result=new ProfileResult{Final=true,MedianMs=100,Availability=.5}};
    var fast=new BenchRow{Id="fast",Name="Alpha",Result=new ProfileResult{Final=true,MedianMs=20,Availability=1}};
    table.Rows.AddRange(new[]{missing,slow,fast});
    table.SortColumn(3);
    Check(table.DisplayRows.Select(r=>r.Id).SequenceEqual(new[]{"fast","slow","missing"}),"brand benchmark: latency sorts ascending, unavailable samples last");
    table.SortColumn(3);
    Check(table.DisplayRows.Select(r=>r.Id).SequenceEqual(new[]{"slow","fast","missing"}),"brand benchmark: repeated header reverses values, unavailable samples remain last");
    table.SortColumn(2);
    Check(table.DisplayRows.First()==fast,"brand benchmark: availability starts with highest success rate");
    table.SortColumn(1);
    Check(table.DisplayRows.First()==fast&&table.Rows[0]==missing,"brand benchmark: name sorting preserves the engine result order");
   }
  }
 }
}
