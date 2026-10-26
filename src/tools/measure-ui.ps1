param([Parameter(Mandatory=$true)][string]$Exe,[Parameter(Mandatory=$true)][string]$OutputDirectory,[int]$IdleProcessId=0)
$ErrorActionPreference='Stop'
$Exe=[IO.Path]::GetFullPath($Exe)
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class UiMeasureNative {
 [DllImport("user32.dll")] public static extern int GetGuiResources(IntPtr process, int flag);
}
'@
$runs=@(foreach($round in 1..3){
 $png=Join-Path $OutputDirectory "startup-$round.png"
 $start=New-Object Diagnostics.ProcessStartInfo
 $start.FileName=$Exe;$start.Arguments='--ui-smoke "'+$png+'"'
 $start.UseShellExecute=$false;$start.CreateNoWindow=$true
 $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 $clock=[Diagnostics.Stopwatch]::StartNew()
 $process=[Diagnostics.Process]::Start($start)
 $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
 $first=$null;$peakGdi=0
 while(-not $process.HasExited){
  $process.Refresh()
  if($null -eq $first -and $process.MainWindowHandle -ne [IntPtr]::Zero){$first=$clock.ElapsedMilliseconds}
  $peakGdi=[Math]::Max($peakGdi,[UiMeasureNative]::GetGuiResources($process.Handle,0))
  if($clock.Elapsed.TotalSeconds -gt 60){throw 'Safe smoke measurement did not finish within 60 seconds'}
  Start-Sleep -Milliseconds 10
 }
 $process.WaitForExit()
 [pscustomobject]@{Round=$round;FirstWindowMs=$first;TotalSmokeMs=$clock.ElapsedMilliseconds;PeakGdi=$peakGdi;ExitCode=$process.ExitCode;Output=$stdout.Result;Error=$stderr.Result}
 $process.Dispose()
})
$idle=$null
if($IdleProcessId){
 $process=Get-Process -Id $IdleProcessId
 if(-not [string]::Equals($process.Path,$Exe,[StringComparison]::OrdinalIgnoreCase)){throw 'Idle sample process does not match exact EXE'}
 $cpu=$process.TotalProcessorTime.TotalMilliseconds
 $clock=[Diagnostics.Stopwatch]::StartNew();Start-Sleep -Seconds 3;$process.Refresh()
 $idle=[pscustomobject]@{ProcessId=$IdleProcessId;WorkingSetBytes=$process.WorkingSet64;PrivateBytes=$process.PrivateMemorySize64;GdiHandles=[UiMeasureNative]::GetGuiResources($process.Handle,0);CpuMsPerSecond=($process.TotalProcessorTime.TotalMilliseconds-$cpu)/$clock.Elapsed.TotalSeconds;Responsive=$process.Responding}
}
[pscustomobject]@{Exe=$Exe;SHA256=(Get-FileHash -LiteralPath $Exe).Hash;Bytes=(Get-Item -LiteralPath $Exe).Length;Method='Three safe synthetic UI smoke processes, first nonzero window handle, 10ms polling. Warm-cache uncontrolled runs; not cold boot or application readiness. Idle samples observe existing exact-path window without interaction.';Runs=$runs;Idle=$idle} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'measurements.json') -Encoding UTF8
'UI_MEASURE_COMPLETE'
