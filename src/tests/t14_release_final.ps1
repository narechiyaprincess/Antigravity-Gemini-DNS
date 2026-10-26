# t14_release_final.ps1 - Tier 14: final 1.0.0 review (2026-10-15), offline only.
# The elevated PowerShell side resolves modules only from the system folder, and the portable package holds nothing but the exe and README.txt.
# Nothing here elevates or touches the live system: the engine runs unelevated with -LibraryOnly (no action), EmergencyRestore.ps1 is only read.

param([string]$ScratchPath)

$ErrorActionPreference = 'Stop'
$global:CurrentTier = "Tier 14: Release 1.0.0 final review"
$script:CurrentTier = $global:CurrentTier

$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\app')).Path
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repoRoot = (Resolve-Path (Join-Path $projectRoot '..')).Path

. (Join-Path $PSScriptRoot 'helpers\TestHarness.ps1')

Write-Host "--- Executing Tier 14: Release 1.0.0 final review ---" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $ScratchPath | Out-Null

# T14_M01: a module in a folder the user can write (first on the inherited PSModulePath) that exports Join-Path is never loaded by the engine.
# Before the fix the engine called Join-Path to build its restricted module path, so PowerShell autoloaded the user's module first.
$mods = Join-Path $ScratchPath 'usermods'
$marker = Join-Path $ScratchPath 'hijacked.txt'
New-Item -ItemType Directory -Force -Path (Join-Path $mods 'ZzAgDnsProbe') | Out-Null
$probe = "function Join-Path { [IO.File]::WriteAllText('" + $marker.Replace("'", "''") + "', 'hijacked'); throw 'hijacked' }`r`nfunction Add-Type { [IO.File]::WriteAllText('" + $marker.Replace("'", "''") + "', 'hijacked'); throw 'hijacked' }`r`nExport-ModuleMember -Function Join-Path, Add-Type`r`n"
[IO.File]::WriteAllText((Join-Path $mods 'ZzAgDnsProbe\ZzAgDnsProbe.psm1'), $probe)
$psi = New-Object Diagnostics.ProcessStartInfo (Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe')
$psi.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + (Join-Path $sourceRoot 'Engine.ps1') + '" -LibraryOnly'
$psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
$psi.EnvironmentVariables['PSModulePath'] = $mods + ';' + [Environment]::GetEnvironmentVariable('PSModulePath')
$psi.EnvironmentVariables.Remove('AGDNS_TEST_PSMODULEPATH')
$p = [Diagnostics.Process]::Start($psi)
$out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync()
$done = $p.WaitForExit(60000)
if (-not $done) { try { $p.Kill() } catch {} }
Assert-Condition ($done -and $p.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $marker)) 'T14_M01' 'Engine.ps1 restricts PSModulePath before its first cmdlet: a user module exporting Join-Path is never loaded' 'P2-MODPATH' -FailureMessage ("exit=" + $(if ($done) { $p.ExitCode } else { 'TIMEOUT' }) + " marker=" + (Test-Path -LiteralPath $marker) + " " + $err.Result)

# T14_M02: the scripts that start without the engine's protection restrict the path before their first cmdlet as well
$er = Get-Content -LiteralPath (Join-Path $sourceRoot 'EmergencyRestore.ps1') -Raw -Encoding UTF8
$erGuard = $er.IndexOf('$env:PSModulePath=[IO.Path]::Combine([Environment]::SystemDirectory')
$erFirstCmdlet = $er.IndexOf('Add-Type -AssemblyName')
$it = Get-Content -LiteralPath (Join-Path $sourceRoot 'Integration.ps1') -Raw -Encoding UTF8
$itBeforeEngine = $it.Substring(0, [Math]::Max(0, $it.IndexOf("'Engine.ps1'")))
Assert-Condition ($erGuard -ge 0 -and $erFirstCmdlet -gt $erGuard -and $er -notmatch 'Join-Path \$env:WINDIR' -and $itBeforeEngine -notmatch 'Join-Path') 'T14_M02' 'EmergencyRestore.ps1 sets the system module path before Add-Type; Integration.ps1 names no cmdlet before it loads the engine' 'P2-MODPATH'

# T14_M03: every PowerShell the exe starts gets the system module path (Program.SystemModulesOnly), none is started without it
$prog = Get-Content -LiteralPath (Join-Path $sourceRoot 'Program.cs') -Raw -Encoding UTF8
$starts = ([regex]::Matches($prog, 'new ProcessStartInfo\(PowerShell\)')).Count
$guarded = ([regex]::Matches($prog, 'SystemModulesOnly\(new ProcessStartInfo\(PowerShell\)')).Count
$allCs = (Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' | Where-Object { $_.Name -notlike 'OfflineTests*' } | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }) -join "`n"
$elsewhere = ([regex]::Matches($allCs, 'new ProcessStartInfo\(PowerShell\)')).Count
Assert-Condition ($starts -ge 2 -and $guarded -eq $starts -and $elsewhere -eq $starts -and $prog -match 'info\.Environment\["PSModulePath"\]=SystemModulePath') 'T14_M03' 'Every PowerShell started by the exe runs with PSModulePath = the system module folder only' 'P2-MODPATH' -FailureMessage ("starts=$starts guarded=$guarded all=$elsewhere")

# T14_P01: the local release\ folder (not in the repository) holds only this product's exe, under the current or the legacy published name; there is no portable\ folder
$release = Join-Path $repoRoot 'release'
$rf = @(if (Test-Path -LiteralPath $release) { Get-ChildItem -LiteralPath $release -Force | ForEach-Object { $_.Name } })
Assert-Condition ((($rf.Count -eq 0) -or (@($rf | Where-Object { $_ -notin 'AntigravityGeminiDns.exe','AntigravityDNS.exe' }).Count -eq 0)) -and -not (Test-Path -LiteralPath (Join-Path $repoRoot 'portable'))) 'T14_P01' 'release\ holds only this product''s exe and there is no portable\ folder' 'R10-PKG' -FailureMessage ('found: ' + ($rf -join ', '))

# T14_P02: package-release.ps1 checks the published exe against the build and its version, points the shortcut at it and never closes a process
$pk = Get-Content -LiteralPath (Join-Path $projectRoot 'tools\package-release.ps1') -Raw -Encoding UTF8
Assert-Condition ($pk.Contains('Invoke-BoundedProcess') -and $pk.Contains('$probe.Stdout.Trim() -ne $csv') -and $pk.Contains('release\AntigravityGeminiDns.exe') -and $pk.Contains('Get-FileHash -LiteralPath $dst') -and $pk.Contains('Copied candidate hash mismatch') -and $pk.Contains('Destination changed after planning') -and ($pk -match 'exit 1') -and ($pk -notmatch 'Stop-Process|taskkill')) 'T14_P02' 'package-release verifies version, exact destination/hash and unchanged plan without closing user processes' 'R10-PKG'

Assert-HostSafetyBaselineUnchanged "Tier 14 Completion"
Write-Host "--- Tier 14 Completed ---" -ForegroundColor Cyan
