param([string]$EvidenceDirectory=(Join-Path $PSScriptRoot '..\.work\tooling-fixtures'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\tools\BoundedProcess.ps1')
$EvidenceDirectory=[IO.Path]::GetFullPath($EvidenceDirectory)
[IO.Directory]::CreateDirectory($EvidenceDirectory)|Out-Null
$scratch=Join-Path $EvidenceDirectory ('synthetic tree '+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch)|Out-Null
$count=0
function Check([bool]$value,[string]$name){if(-not $value){throw ('FIXTURE_FAIL '+$name)};$script:count++;Write-Output ('PASS '+$name)}
$shell=Get-WindowsPowerShellPath
function Run-Code([string]$code,[int]$deadline=5000,[string]$cancel=''){
 $enc=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
 Invoke-BoundedProcess -FilePath $shell -Arguments ('-NoProfile -EncodedCommand '+$enc) -TimeoutMilliseconds $deadline -CancellationFile $cancel -WindowsPowerShell
}
$r=Run-Code '[Console]::WriteLine("success");[Console]::Error.WriteLine("diagnostic");exit 0'
Check ($r.ExitCode -eq 0 -and $r.Stdout.Contains('success') -and $r.Stderr.Contains('diagnostic')) 'success and both pipes'
$r=Run-Code 'exit 7';Check ($r.ExitCode -eq 7) 'nonzero exit'
$r=Run-Code '$x="x"*65536;1..24|%{[Console]::WriteLine($x);[Console]::Error.WriteLine($x)}'
Check ($r.ExitCode -eq 0 -and $r.Stdout.Length -gt 1MB -and $r.Stderr.Length -gt 1MB) 'large stdout and stderr drain concurrently'
$r=Run-Code 'Start-Sleep -Seconds 60' 300
Check ($r.ExitCode -eq 124 -and $r.ElapsedMilliseconds -lt 3000 -and -not (Get-Process -Id $r.Pid -ErrorAction SilentlyContinue)) 'deadline terminates owned root'
# Parent exits while descendant holds the inherited output pipe. Job lifetime must cover it.
$pidfile=Join-Path $scratch 'descendant.pid'
$child='[IO.File]::WriteAllText("'+$pidfile+'",[string]$PID);Start-Sleep 60'
$enc=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($child))
$r=Run-Code ('& cmd.exe /c "start /b powershell.exe -NoProfile -EncodedCommand '+$enc+'";exit 0') 1500
Check ($r.TimedOut -and $r.ExitCode -eq 124 -and $r.ElapsedMilliseconds -lt 4500) 'inherited pipe deadline is finite'
if(Test-Path -LiteralPath $pidfile){$descendant=[int](Get-Content -LiteralPath $pidfile);Check (-not (Get-Process -Id $descendant -ErrorAction SilentlyContinue)) 'no orphan descendant'}else{throw 'Descendant fixture did not start'}
$cancel=Join-Path $scratch 'cancel';[IO.File]::WriteAllText($cancel,'cancel')
$r=Run-Code 'Start-Sleep 60' 5000 $cancel
Check ($r.ExitCode -eq 125 -and $r.Cancelled -and $r.ElapsedMilliseconds -lt 3000) 'cancellation is bounded'
# Compile a portable fixture EXE with independent known version output.
$fixture=Join-Path $scratch 'version fixture.exe'
Add-Type -TypeDefinition 'using System; class Fixture { static void Main(){Console.WriteLine("1.1.0");} }' -OutputAssembly $fixture -OutputType ConsoleApplication
$project=Join-Path $scratch 'project root';[IO.Directory]::CreateDirectory((Join-Path $project 'release'))|Out-Null
$old=Join-Path $project 'release\AntigravityGeminiDns.exe';[IO.File]::WriteAllText($old,'old candidate')
$lnk=Join-Path $project 'Открыть Antigravity-Gemini DNS.lnk';[IO.File]::WriteAllText($lnk,'old shortcut')
$unknown=Join-Path $project 'release\unknown DNS backup.txt';[IO.File]::WriteAllText($unknown,'protected')
$similar=Join-Path $project 'Открыть чужой DNS.lnk';[IO.File]::WriteAllText($similar,'protected shortcut')
$historical=Join-Path $project '.package-backups\historical\history.txt';[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($historical))|Out-Null;[IO.File]::WriteAllText($historical,'protected history')
$tool=(Resolve-Path (Join-Path $PSScriptRoot '..\tools\package-release.ps1')).Path
$args='-NoProfile -ExecutionPolicy Bypass -File "'+$tool+'" -ExePath "'+$fixture+'" -ProjectRoot "'+$project+'"'
$dry=Join-Path $scratch 'dry.json';$r=Invoke-BoundedProcess -FilePath $shell -Arguments ($args+' -WhatIf -ReceiptPath "'+$dry+'"') -WindowsPowerShell
$d=Get-Content -Raw -LiteralPath $dry|ConvertFrom-Json
Check ($r.ExitCode -eq 0 -and $d.Status -eq 'DRY_RUN' -and (Get-Content -Raw $old) -eq 'old candidate' -and @($d.Actions).Count -eq 0) 'dry run only writes receipt'
# Read-only exact shortcut causes a failure after EXE replacement; rollback must restore both preimages.
[IO.File]::SetAttributes($lnk,[IO.FileAttributes]::ReadOnly)
$failed=Join-Path $scratch 'failed.json';$r=Invoke-BoundedProcess -FilePath $shell -Arguments ($args+' -ReceiptPath "'+$failed+'"') -WindowsPowerShell
$f=Get-Content -Raw -LiteralPath $failed|ConvertFrom-Json
Check ($r.ExitCode -ne 0 -and $f.Status -eq 'FAIL' -and (Get-Content -Raw $old) -eq 'old candidate' -and (Get-Content -Raw $lnk) -eq 'old shortcut') 'partial failure preserves and rolls back previous candidate'
[IO.File]::SetAttributes($lnk,[IO.FileAttributes]::Normal)
$actual=Join-Path $scratch 'actual.json';$r=Invoke-BoundedProcess -FilePath $shell -Arguments ($args+' -ReceiptPath "'+$actual+'"') -WindowsPowerShell
$a=Get-Content -Raw -LiteralPath $actual|ConvertFrom-Json
Check ($r.ExitCode -eq 0 -and $a.Status -eq 'PASS' -and (Get-FileHash $old).Hash -eq (Get-FileHash $fixture).Hash) 'exact candidate replacement'
Check ((@($d.Plan.Path)-join '|') -eq (@($a.Actions.Path)-join '|')) 'dry run and actual actions agree'
Check ((Get-Content -Raw $unknown) -eq 'protected' -and (Get-Content -Raw $similar) -eq 'protected shortcut' -and (Get-Content -Raw $historical) -eq 'protected history') 'unknown files similar shortcuts and historical backups preserved'
Check (@($a.Plan|Where-Object{Test-Path -LiteralPath $_.Backup}).Count -eq 2) 'both original exact files retained as preimages'
# Portable archive succeeds, verifies its embedded EXE, and refuses existing artifacts.
$packTool=(Resolve-Path (Join-Path $PSScriptRoot '..\app\Package.ps1')).Path
$packOut=Join-Path $scratch 'portable package'
$packArgs='-NoProfile -File "'+$packTool+'" -ExePath "'+$fixture+'" -OutputDirectory "'+$packOut+'"'
$r=Invoke-BoundedProcess -FilePath $shell -Arguments $packArgs -WindowsPowerShell
$zipPath=Join-Path $packOut 'AntigravityGeminiDns-1.1.0-win-x64.zip'
Check ($r.ExitCode -eq 0 -and (Test-Path -LiteralPath $zipPath)) 'portable archive success and embedded identity'
$sumLines=@(Get-Content -LiteralPath (Join-Path $packOut 'SHA256SUMS.txt'))
Check ($sumLines.Count -eq 1 -and $sumLines[0] -eq ((Get-FileHash -LiteralPath $zipPath).Hash+'  '+[IO.Path]::GetFileName($zipPath))) 'portable checksum covers the delivered archive without absent files'
$zipHash=(Get-FileHash -LiteralPath $zipPath).Hash
$r=Invoke-BoundedProcess -FilePath $shell -Arguments $packArgs -WindowsPowerShell
Check ($r.ExitCode -ne 0 -and (Get-FileHash -LiteralPath $zipPath).Hash -eq $zipHash) 'portable package preserves previous archive'
$partial=Join-Path $scratch 'partial package'
$r=Invoke-BoundedProcess -FilePath $shell -Arguments ('-NoProfile -File "'+$packTool+'" -ExePath "'+$fixture+'" -OutputDirectory "'+$partial+'" -ReadmePath "'+(Join-Path $scratch 'absent readme.txt')+'"') -WindowsPowerShell
$pr=Get-ChildItem -LiteralPath $partial -Filter 'package-receipt-*.json'|ForEach-Object{Get-Content -Raw -LiteralPath $_.FullName|ConvertFrom-Json}
Check ($r.ExitCode -ne 0 -and $pr.Status -eq 'FAIL' -and @(Get-ChildItem -LiteralPath $partial -Filter '*.zip').Count -eq 0) 'partial portable package fails with receipt and no archive'
$hang=Join-Path $scratch 'hanging version.exe'
Add-Type -TypeDefinition 'using System; using System.Threading; class HangFixture { static void Main(){Console.WriteLine("start");Thread.Sleep(60000);} }' -OutputAssembly $hang -OutputType ConsoleApplication
$hangOut=Join-Path $scratch 'timeout package'
$r=Invoke-BoundedProcess -FilePath $shell -Arguments ('-NoProfile -File "'+$packTool+'" -ExePath "'+$hang+'" -OutputDirectory "'+$hangOut+'" -TimeoutSeconds 1') -TimeoutMilliseconds 15000 -WindowsPowerShell
$pr=Get-ChildItem -LiteralPath $hangOut -Filter 'package-receipt-*.json'|ForEach-Object{Get-Content -Raw -LiteralPath $_.FullName|ConvertFrom-Json}
Check ($r.ExitCode -ne 0 -and $pr.Status -eq 'FAIL' -and $pr.Process.TimedOut -and -not (Get-Process -Id $pr.Process.Pid -ErrorAction SilentlyContinue)) 'package version stdout cannot bypass timeout and leaves no orphan'
# Unsupported Feature must fail during initialization AND create the ordinary receipt.
$e2e=(Resolve-Path (Join-Path $PSScriptRoot 'run_offline_e2e_tests.ps1')).Path
$filterReceipt=Join-Path $scratch 'filter-receipt.json'
$r=Invoke-BoundedProcess -FilePath $shell -Arguments ('-NoProfile -File "'+$e2e+'" -Feature F01 -ReceiptPath "'+$filterReceipt+'"') -WindowsPowerShell
$e=Get-Content -LiteralPath $filterReceipt -Raw|ConvertFrom-Json
Check ($r.ExitCode -ne 0 -and -not $e.Completed -and $e.TotalTests -eq 0 -and $e.FatalError -match 'UNSUPPORTED_FEATURE_FILTER' -and $null -eq $e.HostUnchanged) 'unsupported filter gives FAIL initialization receipt and no invented host PASS'
# Missing required EXE really executes the tier-1 assertion and fails.
$missingReceipt=Join-Path $scratch 'missing-exe.json';$savedExe=$env:AGDNS_EXE
try{
 $env:AGDNS_EXE=Join-Path $scratch 'does not exist.exe'
 $r=Invoke-BoundedProcess -FilePath $shell -Arguments ('-NoProfile -File "'+$e2e+'" -Tier 1 -ReceiptPath "'+$missingReceipt+'"') -TimeoutMilliseconds 120000 -WindowsPowerShell
}finally{$env:AGDNS_EXE=$savedExe}
$e=Get-Content -LiteralPath $missingReceipt -Raw|ConvertFrom-Json
Check ($r.ExitCode -ne 0 -and @($e.Results|Where-Object{$_.TestId -eq 'T1_S6_05' -and -not $_.Passed}).Count -eq 1 -and $e.HostUnchanged) 'missing required EXE cannot pass tier 1'
# The quick developer loop must propagate the self-test exit, not the successful Select-Object pipeline.
$devRoot=Join-Path $scratch 'dev fixture';$devTools=Join-Path $devRoot 'tools';$devApp=Join-Path $devRoot 'app';$devOut=Join-Path $devRoot '.work\build-dev'
foreach($folder in @($devTools,$devApp,$devOut)){[IO.Directory]::CreateDirectory($folder)|Out-Null}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\tools\dev.ps1') -Destination $devTools
[IO.File]::WriteAllText((Join-Path $devApp 'Build.ps1'),'exit 0')
$failSelf=Join-Path $devOut 'AntigravityGeminiDns.exe'
Add-Type -TypeDefinition 'using System; class FailedSelfTestFixture { static int Main(){Console.WriteLine("SELFTEST FAILED");return 7;} }' -OutputAssembly $failSelf -OutputType ConsoleApplication
$r=Invoke-BoundedProcess -FilePath $shell -Arguments ('-NoProfile -File "'+(Join-Path $devTools 'dev.ps1')+'"') -WindowsPowerShell
Check ($r.ExitCode -eq 7 -and $r.Stdout.Contains('SELFTEST FAILED')) 'developer loop preserves nonzero self-test exit'
Write-Output ('TOOLING_FIXTURES_PASS '+$count+' scratch='+$scratch)
