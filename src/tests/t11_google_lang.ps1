# t11_google_lang.ps1 - Tier 11: "Весь Google" scope and the English interface, offline only.
# The catalog's Google suffixes (".google.com" ...) are NRPT namespaces; every check (DoH, TLS, Windows answers) still runs on the exact hosts.
# Engine tests run on mock cmdlets and a scratch directory; nothing contacts a provider or touches NRPT/DoH/adapters/state.json.
# This file is saved with a BOM: Windows PowerShell 5.1 mangles non-ASCII text in a script without one.

param([string]$ScratchPath,[switch]$EscalateKnownDefects)

$ErrorActionPreference = 'Stop'
$global:CurrentTier = "Tier 11: all of Google and English"
$script:CurrentTier = $global:CurrentTier
$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\app')).Path
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

. (Join-Path $sourceRoot 'Engine.ps1') -LibraryOnly
$script:RealHostsConflict = ${function:Test-HostsConflict}   # the shared mocks replace it; the suffix matching is tested on the real one
$script:RunPath = 'HKCU:\Software\AgDnsTest\Run'
. (Join-Path $PSScriptRoot 'helpers\TestHarness.ps1')
. (Join-Path $PSScriptRoot 'helpers\SyntheticWire.ps1')
. (Join-Path $PSScriptRoot 'helpers\MockCmdlets.ps1')

Write-Host "--- Executing Tier 11: all of Google and English Tests ---" -ForegroundColor Cyan

$sourceExe = if ($env:AGDNS_EXE) { $env:AGDNS_EXE } else { Join-Path $projectRoot '..\release\AntigravityGeminiDns.exe' }
$haveExe = Test-Path -LiteralPath $sourceExe
$clean = Join-Path $ScratchPath 'clean_exe'
[IO.Directory]::CreateDirectory($clean) | Out-Null
if ($haveExe) { Copy-Item -LiteralPath $sourceExe -Destination $clean }
$exe = Join-Path $clean (Split-Path -Leaf $sourceExe)
function Invoke-Exe([string[]]$Arguments, [int]$TimeoutSec = 60) {
    $psi = New-Object Diagnostics.ProcessStartInfo $exe
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8; $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    $psi.Arguments = (@($Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' ')
    $p = [Diagnostics.Process]::Start($psi)
    $so = $p.StandardOutput.ReadToEndAsync(); $se = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { try { $p.Kill() } catch {}; return [pscustomobject]@{ Exit = -1; Out = 'TIMEOUT'; Err = '' } }
    $p.WaitForExit()
    [pscustomobject]@{ Exit = $p.ExitCode; Out = $so.Result; Err = $se.Result }
}
function Get-Src([string]$Name) { Get-Content -LiteralPath (Join-Path $sourceRoot $Name) -Raw -Encoding UTF8 }

# Network mock: one answer per name the exe would check. A suffix never reaches it (the exe only checks hosts); if one does, the probe fails.
$script:AskedHosts = New-Object System.Collections.Generic.List[string]
function Get-Network {
    $serversToTest = if ($script:Servers -and $script:Servers.Count -gt 0) { $script:Servers } else { @('179.254.166.171', '164.5.224.81') }
    $answers = [ordered]@{}; $i = 0
    foreach ($t in @(Get-Hosts $script:Targets)) { $answers[$t] = @('179.254.166.' + (174 + $i)); $i++ }
    $candidates = @(foreach ($srv in $serversToTest) { [pscustomobject]@{ Server = $srv; DohValidated = $true; PlainValidated = $true; TargetA = @('179.254.166.174'); TargetAnswers = [pscustomobject]$answers } })
    return [pscustomobject]@{ TimestampUtc = [DateTime]::UtcNow.ToString('o'); Resolver = $script:Template; Target = @($script:Targets)[0]; Protocol = 'RFC8484 HTTP/2 with default TLS validation'; Candidates = $candidates }
}
function Get-SystemAnswers([string]$Domain, [string]$Type = 'A') {
    $script:AskedHosts.Add($Domain)
    $idx = [array]::IndexOf(@(Get-Hosts $script:Targets), $Domain); if ($idx -lt 0) { return @() }
    return @('179.254.166.' + (174 + $idx))
}
# Pass a scratch hosts file explicitly; production resolves its default from SystemDirectory.
$script:FakeWin = Join-Path $ScratchPath 'win'
[IO.Directory]::CreateDirectory((Join-Path $script:FakeWin 'System32\drivers\etc')) | Out-Null
function Set-HostsLines([string[]]$Lines) { [IO.File]::WriteAllLines((Join-Path $script:FakeWin 'System32\drivers\etc\hosts'), [string[]]@($Lines)) }
Set-HostsLines @('127.0.0.1 localhost')
function Test-HostsConflict($Scope = $null) { & $script:RealHostsConflict $Scope (Join-Path $script:FakeWin 'System32\drivers\etc\hosts') }
# AUDIT: no hosts file means no hosts conflict (some "cleaners" delete the file); the status must not crash on it
Remove-Item -LiteralPath (Join-Path $script:FakeWin 'System32\drivers\etc\hosts') -Force -ErrorAction SilentlyContinue
Assert-Condition ((Test-HostsConflict) -eq $false) 'T11_H01' 'A missing hosts file is no conflict (it used to end the status and the connect with a raw "Cannot find path")' 'R-AUDIT'

$core = @($script:CoreHosts); $gem = @($script:GeminiHosts); $goo = @($script:GoogleNamespaces)
$all = @($core) + @($gem) + @($goo); $four = @($core) + @($gem)
$P1s = @('179.254.166.171', '164.5.224.81'); $P1t = 'https://dns.malw.link/dns-query'
function New-Bridge([string]$name, [string[]]$Scope) {
    Reset-MockEnvironment (Join-Path $ScratchPath $name)
    $script:Repair = $false; $script:AskedHosts.Clear(); Set-HostsLines @('127.0.0.1 localhost')
    $script:Servers = $P1s; $script:Template = $P1t; $script:ProfileName = 'dns.malw.link'
    $script:Targets = @($Scope)
    return (Enable-State $true $false)
}
function Invoke-Enable([string[]]$Scope) { $script:Targets = @($Scope); try { Enable-State $true $false | Out-Null; return 'NO_ERROR' } catch { return $_.Exception.Message } }
function Read-StateSafe { try { return Read-State } catch { return [pscustomobject]@{ Phase = 'UNREADABLE'; Targets = @() } } }
function Get-StatusSafe { try { return Get-Status } catch { return [pscustomobject]@{ State = 'UNREADABLE' } } }
function Get-RuleNamespaces { return @($script:MockRules | ForEach-Object { @($_.Namespace)[0] } | Sort-Object) }

# ==============================================================================
# G01-G03: catalog, a bridge for all of Google, checks only on hosts
# ==============================================================================
$catalog = Get-Content -LiteralPath (Join-Path $sourceRoot 'profiles.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$gt = @($catalog.googleTargets)
Assert-Condition ($gt.Count -ge 4 -and @($gt | Where-Object { -not $_.StartsWith('.') -or $_ -match '\*' }).Count -eq 0 -and '.google.com' -in $gt -and '.googleapis.com' -in $gt) 'T11_G01' 'The catalog lists Google suffixes (leading dot, no wildcard), .google.com and .googleapis.com among them' 'R-GOOGLE'

$b = New-Bridge 'g02' $all
$journal = Read-State; $st = Get-Status
Assert-Condition ($b.State -eq 'DNS active' -and $script:MockRules.Count -eq $all.Count -and (Same-Set (Get-RuleNamespaces) $all) -and (Same-Set @($journal.Targets) $all) -and $st.State -eq 'DNS active' -and $script:MockDoh.Count -eq 2) 'T11_G02' "All of Google: one rule per host and per suffix ($($all.Count)), one journal, DoH for the two servers, status active" 'R-GOOGLE'
$askedSuffix = @($script:AskedHosts | Where-Object { $_.StartsWith('.') })
Assert-Condition ($askedSuffix.Count -eq 0 -and (Same-Set @($script:AskedHosts | Select-Object -Unique) $four)) 'T11_G03' 'The Windows resolver is asked about the exact hosts only, never about a suffix' 'R-GOOGLE' -FailureMessage (($script:AskedHosts | Select-Object -Unique) -join ',')

# ==============================================================================
# G04-G05: changing scope on a working bridge, older journals
# ==============================================================================
$res = Invoke-Enable $core; $j = Read-StateSafe
Assert-Condition ($res -eq 'NO_ERROR' -and $j.Phase -eq 'Active' -and (Same-Set @($j.Targets) $core) -and $script:MockRules.Count -eq 2 -and (Get-StatusSafe).State -eq 'DNS active') 'T11_G04a' 'All of Google -> Antigravity on a working bridge: back to the two core rules' 'R-GOOGLE'
$res = Invoke-Enable $all; $j = Read-StateSafe
Assert-Condition ($res -eq 'NO_ERROR' -and (Same-Set @($j.Targets) $all) -and $script:MockRules.Count -eq $all.Count) 'T11_G04b' 'Antigravity -> all of Google on a working bridge: every suffix rule is added' 'R-GOOGLE'
$null = New-Bridge 'g05' $core
$script:Targets = $all
$readOk = $true; try { $null = Read-State } catch { $readOk = $false }
$restoreOk = $true; try { $null = Restore-State (Read-State) } catch { $restoreOk = $false }
Assert-Condition ($readOk -and $restoreOk -and (Read-StateSafe).Phase -eq 'Restored' -and $script:MockRules.Count -eq 0) 'T11_G05' 'A core journal (every earlier release) is read and restored while all of Google is selected' 'R-GOOGLE'

# ==============================================================================
# G06-G07: foreign rules and hosts entries under a Google suffix are conflicts (before anything is touched)
# ==============================================================================
$null = New-Bridge 'g06' $core
$script:MockRules.Add([pscustomobject]@{ Name = '{11111111-2222-3333-4444-555555555555}'; Namespace = @('notebooklm.google.com'); NameServers = @('9.9.9.9'); Comment = 'foreign'; DisplayName = 'foreign' })
$before = $script:MockWriteCount
$res = Invoke-Enable $all
Assert-Condition ($res -like 'NRPT_CONFLICT*' -and $script:MockWriteCount -eq $before -and (Same-Set @((Read-StateSafe).Targets) $core)) 'T11_G06a' 'A foreign rule for a name under .google.com refuses the change to all of Google; the working bridge stays' 'R-GOOGLE' -FailureMessage $res
$script:Targets = $core
Assert-Condition ((Get-StatusSafe).State -eq 'DNS active') 'T11_G06b' 'The same foreign rule does not disturb the narrower Antigravity bridge' 'R-GOOGLE'
$null = New-Bridge 'g07' $core
Set-HostsLines @('127.0.0.1 localhost', '142.250.0.1 www.google.com')
$res = Invoke-Enable $all
Assert-Condition ($res -like 'NRPT_CONFLICT*' -and (Same-Set @((Read-StateSafe).Targets) $core)) 'T11_G07' 'A hosts entry for a name under a Google suffix is a conflict for all of Google' 'R-GOOGLE' -FailureMessage $res

# ==============================================================================
# G08: the exe knows the scope; the elevated copy gets only the --gemini / --browser / --no-core flags
# ==============================================================================
$prog = Get-Src 'Program.cs'
Assert-Condition ($prog -match '\(Google\?" --browser":""\)' -and $prog -match 'TargetsOf\(Core,Gemini,Google\)' -and $prog -notmatch '"--targets"') 'T11_G08a' 'Program.cs: all of Google comes from the catalog (TargetsForGoogle) and the elevated copy gets only --google' 'R-GOOGLE'
if ($haveExe) {
    $pl = Invoke-Exe @('--profiles-list', '--google'); $j0 = $null; try { $j0 = $pl.Out | ConvertFrom-Json } catch {}
    Assert-Condition ($pl.Exit -eq 0 -and $j0 -and (Same-Set @($j0.Targets) $all) -and (Same-Set @($j0.GoogleTargets) $goo)) 'T11_G08b' 'The exe with --google lists the hosts and the suffixes as its scope' 'R-GOOGLE' -FailureMessage $pl.Out
}

# ==============================================================================
# L01: every text the window asks to translate has an English entry
# ==============================================================================
$lang = Get-Src 'Lang.cs'
$missing = @()
foreach ($f in 'MainForm.cs', 'MainForm.Browser.cs', 'Browsers.cs', 'UiKit.cs', 'Speed.cs', 'ReportPreviewForm.cs', 'Connect.cs', 'Messages.cs') {
    $src = Get-Src $f
    foreach ($m in [regex]::Matches($src, 'L\.[TF]\("((?:[^"\\\r\n]|\\.)*)"')) { $k = $m.Groups[1].Value; if (-not $lang.Contains('{"' + $k + '",')) { $missing += "$f :: $k" } }
}
Assert-Condition ($missing.Count -eq 0) 'T11_L01' 'Every L.T/L.F text of the window has an English entry in Lang.cs' 'R-LANG' -FailureMessage (($missing | Select-Object -First 8) -join ' | ')
if ($haveExe) {
    $sm = Invoke-Exe @('--ui-smoke', (Join-Path $ScratchPath 'en.png'), '--en') 120
    Assert-Condition ($sm.Exit -eq 0 -and $sm.Out -match 'UI_SMOKE_PASS') 'T11_L02' 'The window renders in English (--ui-smoke --en)' 'R-LANG' -FailureMessage $sm.Out
}
