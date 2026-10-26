# t7_release_1_5.ps1 - Tier 7: review of the 1.4 code and Release 1.5 work items, offline only.
# Nothing here contacts a provider or touches NRPT/DoH/adapters/state.json; engine tests use mock cmdlets and scratch directories.
# This file is saved with a BOM: Windows PowerShell 5.1 mangles Cyrillic in a script without one.

param(
    [string]$ScratchPath,
    [switch]$EscalateKnownDefects
)

$ErrorActionPreference = 'Stop'
$global:CurrentTier = "Tier 7: 1.4 review and Release 1.5 work items"
$script:CurrentTier = $global:CurrentTier

$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\app')).Path
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

. (Join-Path $sourceRoot 'Engine.ps1') -LibraryOnly
$script:RunPath = 'HKCU:\Software\AgDnsTest\Run'
. (Join-Path $PSScriptRoot 'helpers\TestHarness.ps1')
. (Join-Path $PSScriptRoot 'helpers\SyntheticWire.ps1')
. (Join-Path $PSScriptRoot 'helpers\MockCmdlets.ps1')

Write-Host "--- Executing Tier 7: 1.4 review and Release 1.5 Tests ---" -ForegroundColor Cyan

$sourceExe = if ($env:AGDNS_EXE) { $env:AGDNS_EXE } else { Join-Path $projectRoot '..\release\AntigravityGeminiDns.exe' }
$haveExe = Test-Path -LiteralPath $sourceExe
$clean = Join-Path $ScratchPath 'clean_exe'
[IO.Directory]::CreateDirectory($clean) | Out-Null
if ($haveExe) { Copy-Item -LiteralPath $sourceExe -Destination $clean }
$exe = Join-Path $clean (Split-Path -Leaf $sourceExe)

function Invoke-Exe([string[]]$Arguments, [int]$TimeoutSec = 120) {
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

# ==============================================================================
# V: review of the 1.4 engine code (Enable-State / Apply-State / Restore-PreviousProfile)
# ==============================================================================
$P1s = @('179.254.166.171', '164.5.224.81'); $P1t = 'https://dns.malw.link/dns-query'
$P2s = @('185.185.185.185', '185.185.185.186'); $P2t = 'https://xbox-dns.ru/dns-query'
function New-ActiveBridge([string]$name) {
    Reset-MockEnvironment (Join-Path $ScratchPath $name)
    $script:Repair = $false
    $script:Servers = $P1s; $script:Template = $P1t; $script:ProfileName = 'dns.malw.link'
    return (Enable-State $true $false)
}
function Invoke-Enable([bool]$UseP2 = $false) {
    if ($UseP2) { $script:Servers = $P2s; $script:Template = $P2t; $script:ProfileName = 'xbox-dns.ru' }
    else { $script:Servers = $P1s; $script:Template = $P1t; $script:ProfileName = 'dns.malw.link' }
    try { Enable-State $true $false | Out-Null; return 'NO_ERROR' } catch { return $_.Exception.Message }
}
function Test-P1Active {
    $st = Read-State
    return ($st.Phase -eq 'Active' -and (Same-Set $st.Servers $P1s) -and $script:MockRules.Count -eq 2 -and $script:MockDoh.Count -eq 2 -and (Get-Status).State -eq 'DNS active')
}

# V01: an owned rule that was edited behind our back makes Restore-State refuse. The switch has to learn that BEFORE it removes anything
# (the refusal used to come on the second rule, after the first one was already gone).
New-ActiveBridge 't7_v01' | Out-Null
$script:MockRules[1].DnsSecEnabled = $true
$writes = $script:MockWriteCount
$msg = Invoke-Enable $true
Assert-Condition ($msg -like '*OWNED_RULE_CHANGED*' -and $script:MockWriteCount -eq $writes -and $script:MockRules.Count -eq 2 -and (Read-State).Phase -eq 'Active' -and $script:MockDoh.Count -eq 2) 'T7_V01' 'Switch: an owned rule edited by someone else is refused before anything is removed (both rules, both DoH records and the Active journal stay)' 'R15-V' -FailureMessage $msg

# V02: the same for a DoH registration whose template was changed independently
New-ActiveBridge 't7_v02' | Out-Null
$script:MockDoh[1].DohTemplate = 'https://someone-else.example/dns-query'
$writes = $script:MockWriteCount
$msg = Invoke-Enable $true
Assert-Condition ($msg -like '*DOH_CHANGED_INDEPENDENTLY*' -and $script:MockWriteCount -eq $writes -and $script:MockRules.Count -eq 2 -and (Read-State).Phase -eq 'Active') 'T7_V02' 'Switch: a DoH registration changed independently is refused before anything is removed' 'R15-V' -FailureMessage $msg

# V03: the pre-check does not break a healthy switch (and a plain re-enable of a healthy bridge stays a no-op)
New-ActiveBridge 't7_v03' | Out-Null
$writes = $script:MockWriteCount
$same = Invoke-Enable $false
Assert-Condition ($same -eq 'NO_ERROR' -and $script:MockWriteCount -eq $writes -and (Test-P1Active)) 'T7_V03' 'Re-enabling a healthy bridge changes nothing' 'R15-V'
$sw = Invoke-Enable $true
Assert-Condition ($sw -eq 'NO_ERROR' -and (Read-State).ProfileName -eq 'xbox-dns.ru' -and $script:MockRules.Count -eq 2) 'T7_V03b' 'A healthy profile switch still completes after the new pre-check' 'R15-V' -FailureMessage $sw

# V04: the elevated window waits longer than the engine deadline, and enable has room for two preflights (source contract)
$prog = Get-Src 'Program.cs'
$m = [regex]::Match($prog, 'Deadline\(string action\)=>action=="enable"\?TimeSpan\.FromSeconds\((\d+)\)')
Assert-Condition ($m.Success -and [int]$m.Groups[1].Value -ge 150 -and $prog -match 'ElevationWait\(action\)' -and $prog -match 'cts\.CancelAfter\(ElevationWait\(action\)\)') 'T7_V04' 'Enable deadline is at least 150 s and the elevation wait is longer than the engine deadline' 'R15-V'

# V05: the guard message names the button that exists in the recovery state
$rec = Get-Src 'Recovery.cs'
$btn = [regex]::Match($rec, '4\. [^"]*"').Value
Assert-Condition ($btn -match "«Восстановить»" -and $btn -notmatch "Отключить мост") 'T7_V05' 'The manual recovery text tells to press the recovery button, not "disconnect"' 'R15-V' -FailureMessage $btn

# ==============================================================================
# R: "Repair" (the tray button): an Active journal that no longer verifies goes through the compensated switch
# ==============================================================================
# R01: without -Repair the previous contract holds (a broken bridge is reported, not touched)
New-ActiveBridge 't7_r01' | Out-Null
$script:MockRules.RemoveAt(0)
$writes = $script:MockWriteCount
$msg = Invoke-Enable $false
Assert-Condition ($msg -like 'RECOVERY_REQUIRED*' -and $script:MockWriteCount -eq $writes) 'T7_R01' 'Without -Repair a bridge whose rule disappeared is reported as RECOVERY_REQUIRED and nothing is written' 'R15-1'

# R02: with -Repair the bridge is applied again and verified
New-ActiveBridge 't7_r02' | Out-Null
$script:MockRules.RemoveAt(0)
$script:Repair = $true
$msg = Invoke-Enable $false
$script:Repair = $false
Assert-Condition ($msg -eq 'NO_ERROR' -and (Test-P1Active)) 'T7_R02' 'Repair: a bridge that lost a rule is re-applied and verified (journal Active, 2 rules, 2 DoH, status active)' 'R15-1' -FailureMessage $msg

# R03: a vanished DoH registration
New-ActiveBridge 't7_r03' | Out-Null
$script:MockDoh.RemoveAt(0)
$script:Repair = $true
$msg = Invoke-Enable $false
$script:Repair = $false
Assert-Condition ($msg -eq 'NO_ERROR' -and (Test-P1Active)) 'T7_R03' 'Repair: a vanished DoH registration is re-created' 'R15-1' -FailureMessage $msg

# R04: repair refuses what the program does not own (an edited rule), before any write
New-ActiveBridge 't7_r04' | Out-Null
$script:MockRules[0].DnsSecEnabled = $true
$writes = $script:MockWriteCount
$script:Repair = $true
$msg = Invoke-Enable $false
$script:Repair = $false
Assert-Condition ($msg -like '*OWNED_RULE_CHANGED*' -and $script:MockWriteCount -eq $writes -and $script:MockRules.Count -eq 2 -and (Read-State).Phase -eq 'Active') 'T7_R04' 'Repair never overwrites an owned rule that someone edited (refused before any write)' 'R15-1' -FailureMessage $msg

# R05: repair of a healthy bridge is a no-op
New-ActiveBridge 't7_r05' | Out-Null
$writes = $script:MockWriteCount
$script:Repair = $true
$msg = Invoke-Enable $false
$script:Repair = $false
Assert-Condition ($msg -eq 'NO_ERROR' -and $script:MockWriteCount -eq $writes -and (Test-P1Active)) 'T7_R05' 'Repair of a healthy bridge changes nothing' 'R15-1' -FailureMessage $msg

# R06: a foreign conflicting rule appeared: repair refuses without touching anything
New-ActiveBridge 't7_r06' | Out-Null
$script:MockRules.Add([pscustomobject]@{ Name = '{00000000-0000-0000-0000-0000000000f7}'; Namespace = @('.googleapis.com'); NameServers = '1.1.1.1'; DisplayName = 'Foreign'; Comment = 'Foreign' })
$writes = $script:MockWriteCount
$script:Repair = $true
$msg = Invoke-Enable $false
$script:Repair = $false
Assert-Condition ($msg -like '*NRPT_CONFLICT*' -and $script:MockWriteCount -eq $writes -and (Read-State).Phase -eq 'Active') 'T7_R06' 'Repair refuses while a foreign rule claims the domains (nothing removed)' 'R15-1' -FailureMessage $msg

# R07: the repair cannot succeed (apply fails twice): RECOVERY_REQUIRED, no false success, no half-state
New-ActiveBridge 't7_r07' | Out-Null
$script:MockRules.RemoveAt(0)
$script:MockFailDohFrom = $script:MockDohAddCount + 1
$script:Repair = $true
$msg = Invoke-Enable $false
$script:Repair = $false; $script:MockFailDohFrom = 0
$stR = Read-State
Assert-Condition ($msg -like 'RECOVERY_REQUIRED*' -and $stR.Phase -ne 'Active' -and $script:MockRules.Count -eq 0 -and $script:MockDoh.Count -eq 0) 'T7_R07' 'Repair that cannot re-apply ends in RECOVERY_REQUIRED with a clean journal, not in a false success' 'R15-1' -FailureMessage $msg

# R08: a repair starts from the "Repair" click paths, and by itself only through the gated self-repair (2026-10-26: administrator rights, rate limit)
$main = Get-Src 'MainForm.cs'
$assistedSrc = Get-Src 'MainForm.Assisted.cs'
$selfRepair = [regex]::Match($assistedSrc, 'internal async Task<bool> AutoRepairAsync[\s\S]*?\n  \}').Value
Assert-Condition ($main -match 'Mutate\("enable",true\)' -and ([regex]::Matches($main, 'Mutate\("enable",true\)')).Count -eq 2 -and ([regex]::Matches($assistedSrc, 'Mutate\("enable",true\)')).Count -eq 1 -and $selfRepair.Contains('Mutate("enable",true)') -and $selfRepair.Contains('if(!CanRepairSilently()||autoRepairs.Count>=3||') -and $selfRepair.Contains('TimeSpan.FromMinutes(5)') -and $selfRepair.Contains('assisted.ManualOff||assisted.RecoveryPending') -and $main.Contains('internal Func<bool> CanRepairSilently=()=>Program.IsAdmin();') -eq $false -and $assistedSrc.Contains('internal Func<bool> CanRepairSilently=()=>Program.IsAdmin();')) 'T7_R08' 'Mutate("enable", repair) is called from the window button, the tray item and only the gated self-repair (administrator rights, 5 min / 3 per hour, never after a manual off)' 'R15-1'

# ==============================================================================
# H: health check (1.5-1): read-only, no UAC, no benchmark, no automatic repair
# ==============================================================================
$health = Get-Src 'Health.cs'
$forbidden = @('Benchmark', 'LiveTransport', 'Elevate(', 'Add-DnsClient', 'Remove-DnsClient', 'Set-DnsClient', 'Get-DnsClient', 'Get-Cim', 'Get-Wmi', 'Registry', 'ProcessStartInfo', 'Process.Start', '"enable"', '"restore"', 'Mutate(')
$healthCode = (($health -split "`n") | ForEach-Object { $_ -replace '//.*$', '' }) -join "`n"
$hits = @($forbidden | Where-Object { $healthCode.Contains($_) })
Assert-Condition ($hits.Count -eq 0) 'T7_H01' 'Health.cs has no benchmark, elevation, mutation, registry or direct CIM/process code' 'R15-1' -FailureMessage ($hits -join ', ')
Assert-Condition (([regex]::Matches($healthCode, 'Program\.Engine\(')).Count -eq 1 -and $healthCode -match 'Program\.Engine\("status"') 'T7_H02' 'The only way Health.cs reaches Windows is Program.Engine("status") (CIM through the engine, with the short deadline)' 'R15-1'

$mi = $main.IndexOf('// ---- 1.5-1: health check in the tray'); $mj = $main.IndexOf('// ---- 1.5-2: support report')
$healthBlock = if ($mi -ge 0 -and $mj -gt $mi) { $main.Substring($mi, $mj - $mi) } else { '' }
$ti = $healthBlock.IndexOf('async Task HealthTick'); $ai = $healthBlock.IndexOf('internal HealthReport CurrentHealth')
$tickBlock = if ($ti -ge 0 -and $ai -gt $ti) { $healthBlock.Substring($ti, $ai - $ti) } else { '' }
Assert-Condition ($healthBlock.Length -gt 500 -and $tickBlock.Length -gt 300 -and $tickBlock -notmatch 'Mutate\(' -and $tickBlock -notmatch 'Elevate\(' -and $tickBlock -notmatch 'Benchmark|ToggleBenchmark' -and $healthBlock -notmatch 'ToggleBenchmark|Benchmark\.' -and ([regex]::Matches($healthBlock, 'AutoRepairAsync\(')).Count -eq 1 -and $healthBlock.Contains('if(r.CanFix)_=AutoRepairAsync(r);')) 'T7_H03' 'The window code of the health check never starts an elevation or a benchmark; a confirmed fixable drift only hands over to the gated self-repair' 'R15-1' -FailureMessage ("len " + $healthBlock.Length + "/" + $tickBlock.Length)
Assert-Condition ($tickBlock -match 'busy' -and $tickBlock -match 'benchRunning' -and $tickBlock -match 'TryStart' -and $healthBlock -match 'NetworkAddressChanged' -and $healthBlock -match 'PowerModes\.Resume' -and $healthBlock -match 'HealthTrigger\.Startup') 'T7_H04' 'Triggers: startup, network change, wake-up and the periodic timer; nothing runs while an operation or the benchmark is in progress' 'R15-1'
Assert-Condition ($main -match 'if\(hasPhase&&!needsRestore\)health=null') 'T7_H05' 'A switched-off bridge clears any drift: the automation never argues with "Disconnect"' 'R15-1'
# a check that overlapped an operation is queued again, not dropped: the state is checked once the operation has settled
Assert-Condition ($tickBlock -match 'if\(busy\)\s*\{\s*healthSched\.Trigger\(HealthTrigger\.Manual\);\s*return;\s*\}' -and $tickBlock -notmatch 'IsDisposed\s*\|\|\s*busy') 'T7_H14' 'A check that overlapped an operation is queued again (not dropped), so the state is checked after the operation settles' 'R15-1'

$consts = @([regex]::Matches($health, 'const string[^;]*') | ForEach-Object { [regex]::Matches($_.Value, '"(HEALTH_[A-Z_]+)"') | ForEach-Object { $_.Groups[1].Value } } | Sort-Object -Unique)
Assert-Condition ($consts.Count -eq 6) 'T7_H06' 'Health.cs defines the six health codes' 'R15-1' -FailureMessage ($consts -join ',')

if ($haveExe) {
    $cl = Invoke-Exe @('--codes-list'); $table = $null; try { $table = $cl.Out | ConvertFrom-Json } catch {}
    $have = @{}; if ($table) { foreach ($row in $table) { $have[$row.Code] = $row } }
    $missing = @(($consts + @('REPORT_SAVED', 'REPORT_SAVE_FAILED')) | Where-Object { -not $have.ContainsKey($_) })
    Assert-Condition ($table -and $missing.Count -eq 0) 'T7_H07' 'Every health and report code has an entry in the message table' 'R15-1' -FailureMessage ($missing -join ', ')
    $ru = '[Ѐ-ӿ]'
    $bad = @($consts | Where-Object { $r = $have[$_]; -not $r -or $r.Text -notmatch $ru -or $r.Action -notmatch $ru -or -not $r.Title })
    $fix = @($consts | Where-Object { $have.ContainsKey($_) -and $have[$_].Kind -eq 'Fix' })
    $fixWrong = @($fix | Where-Object { $have[$_].Action -notmatch "Починить" })
    Assert-Condition ($bad.Count -eq 0 -and $fix.Count -eq 2 -and $fixWrong.Count -eq 0) 'T7_H08' 'Each health code has a Russian text and one action; exactly the two codes with a one-click repair (DRIFT_RULES, PROVIDER_DOWN) carry the Fix action naming the repair button; RESOLVER_MISMATCH points to Diagnostics' 'R15-1' -FailureMessage (($bad -join ',') + ' fix=' + $fix.Count)
    # the scan that T6_E02 does for the other sources, for the new files too
    $scanMissing = @()
    foreach ($f in 'Health.cs', 'SupportReport.cs', 'MainForm.cs') {
        $text = Get-Src $f
        foreach ($mm in [regex]::Matches($text, '(?:Code|Error)="([A-Z][A-Z0-9_]+)"|const string \w+="([A-Z][A-Z0-9_]+)"')) {
            $c = if ($mm.Groups[1].Success) { $mm.Groups[1].Value } else { $mm.Groups[2].Value }
            if (-not $have.ContainsKey($c)) { $scanMissing += "$f`:$c" }
        }
    }
    Assert-Condition ($scanMissing.Count -eq 0) 'T7_H09' 'Every code literal in Health.cs, SupportReport.cs and MainForm.cs has a table entry' 'R15-1' -FailureMessage ($scanMissing -join ', ')

    $help = Invoke-Exe @('--help')
    Assert-Condition ($help.Exit -eq 0 -and $help.Out -match '--repair') 'T7_H10' '--help documents --enable --repair' 'R15-1'

    # the self-tests that carry the scheduler/monitor/report checks are really in the exe (a full run is T5_E03; here only the labels)
    $st = Invoke-Exe @('--self-test') 300
    $healthLines = @($st.Out -split "`n" | Where-Object { $_ -match '^PASS health' }).Count
    $schedLines = @($st.Out -split "`n" | Where-Object { $_ -match '^PASS health scheduler' }).Count
    $reportLines = @($st.Out -split "`n" | Where-Object { $_ -match '^PASS report' }).Count
    Assert-Condition ($st.Exit -eq 0 -and $healthLines -ge 35 -and $schedLines -ge 10 -and $reportLines -ge 18) 'T7_H11' "The exe's self-test carries the health, scheduler and report checks (health=$healthLines of which scheduler=$schedLines, report=$reportLines)" 'R15-1' -FailureMessage ($st.Out.Substring([Math]::Max(0, $st.Out.Length - 400)))

    # states renders the new states; the five default shots are produced by the plain call and are not part of --states
    $shotDir = Join-Path $ScratchPath 'shots'; [IO.Directory]::CreateDirectory($shotDir) | Out-Null
    $ui = Invoke-Exe @('--ui-smoke', (Join-Path $shotDir 'smoke.png'), '--states') 300
    $needed = @('smoke_state_drift.png', 'smoke_report_preview.png', 'smoke_state_idle.png', 'smoke_details_recovery.png')
    $absent = @($needed | Where-Object { -not (Test-Path (Join-Path $shotDir $_)) -or (Get-Item (Join-Path $shotDir $_)).Length -lt 2000 })
    Assert-Condition ($ui.Exit -eq 0 -and $absent.Count -eq 0) 'T7_H12' '--ui-smoke --states renders the drift state and the report preview' 'R15-1' -FailureMessage ($absent -join ', ')
    $plainShots = Join-Path $ScratchPath 'plain_shots'; [IO.Directory]::CreateDirectory($plainShots) | Out-Null
    $ui2 = Invoke-Exe @('--ui-smoke', (Join-Path $plainShots 'smoke.png')) 300
    $extra = @(Get-ChildItem -LiteralPath $plainShots -File | Where-Object { $_.Name -match 'state|details|preview' })
    Assert-Condition ($ui2.Exit -eq 0 -and (Get-ChildItem -LiteralPath $plainShots -File).Count -eq 5 -and $extra.Count -eq 0) 'T7_H13' 'The plain --ui-smoke still writes exactly the five default shots (new states only under --states)' 'R15-1'
}

# ==============================================================================
# P: support report (1.5-2): local, redacted, previewed, never sent
# ==============================================================================
$rep = Get-Src 'SupportReport.cs'; $prev = Get-Src 'ReportPreviewForm.cs'
$net = @('HttpClient', 'WebClient', 'WebRequest', 'HttpWebRequest', 'Socket', 'SmtpClient', 'http://', 'https://', 'mailto:', 'Upload', 'Dns.')
$netHits = @($net | Where-Object { $rep.Contains($_) -or $prev.Contains($_) })
Assert-Condition ($netHits.Count -eq 0) 'T7_P01' 'The report code has no network or sending code (HTTP, sockets, mail, upload)' 'R15-2' -FailureMessage ($netHits -join ', ')
Assert-Condition ($rep -match 'netsh\.exe' -and ($rep -replace 'netsh\.exe', '') -notmatch 'ProcessStartInfo\(\s*[^)]*(cmd|powershell|curl|wget)') 'T7_P02' 'The only process the report starts is netsh (to read the Wi-Fi network name so it can be removed)' 'R15-2'
$ri = $main.IndexOf('// ---- 1.5-2: support report'); $report = if ($ri -ge 0) { $main.Substring($ri) } else { '' }
Assert-Condition ($report -match 'ReportPreviewForm' -and $report -match 'ShowDialog' -and $report -match 'DialogResult\.OK' -and $report.IndexOf('ShowReportPreview(text)') -lt $report.IndexOf('File.WriteAllText(sfd.FileName')) 'T7_P03' 'The report is shown in a preview first and written only after "Save" and a file name chosen by the person' 'R15-2'
Assert-Condition (([regex]::Matches($main, 'File\.WriteAllText\(sfd\.FileName,text')).Count -eq 1 -and $main -notmatch 'Process\.Start\([^)]*mailto') 'T7_P04' 'The saved file is exactly the previewed text, in one place' 'R15-2'

# Extended duplicate check (T6_D02 covers the default profile only): no IP or DoH URL of ANY catalog profile is repeated in the sources.
$catalogFile = Join-Path $sourceRoot 'profiles.json'
$cat = Get-Content -LiteralPath $catalogFile -Raw -Encoding UTF8 | ConvertFrom-Json
$needles = @(); foreach ($p in $cat.profiles) { $needles += @($p.servers); $needles += $p.template }
$exempt = @('OfflineTests.cs', 'Engine.Tests.ps1')
$dupes = @()
foreach ($f in Get-ChildItem -LiteralPath $sourceRoot -File | Where-Object { $_.Extension -in '.cs', '.ps1', '.csproj' -and $_.Name -notin $exempt }) {
    $text = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8
    foreach ($n in $needles) { if ($n -and $text.Contains([string]$n)) { $dupes += "$($f.Name): $n" } }
}
Assert-Condition ($dupes.Count -eq 0) 'T7_D01' 'No server IP or DoH URL of any catalog profile is repeated outside profiles.json and the self-tests' 'R15-V' -FailureMessage ($dupes -join '; ')

# ==============================================================================
# K: hard kill on a mutation boundary: unique NRPT owner markers recover; pending DoH cannot prove ownership
# ==============================================================================
function New-KilledApply([string]$name, [bool]$RuleAdded, [bool]$DohAdded) {
    Reset-MockEnvironment (Join-Path $ScratchPath $name)
    $script:Repair = $false
    $script:Servers = $P1s; $script:Template = $P1t; $script:ProfileName = 'dns.malw.link'
    Initialize-StateDirectory
    $owner = [guid]::NewGuid().ToString('N')
    $s = [pscustomobject]@{ Schema = 1; Owner = $owner; Phase = 'Applying'; ProfileName = $script:ProfileName; Template = $script:Template; Protocol = 'DoH'; Targets = $script:Targets; Servers = $P1s
        Rules = @(foreach ($t in $script:Targets) { [pscustomobject]@{ Namespace = $t; Name = $null; Fingerprint = $null } })
        Doh = @(foreach ($ip in $P1s) { [pscustomobject]@{ Server = $ip; Template = $script:Template; Created = $true; Ownership = 'Pending' } }); UpdatedUtc = '' }
    Save-State $s
    if ($DohAdded) { foreach ($ip in $P1s) { Add-DnsClientDohServerAddress -ServerAddress $ip -DohTemplate $script:Template -AllowFallbackToUdp $false -AutoUpgrade $true | Out-Null } }
    if ($RuleAdded) { foreach ($t in $script:Targets) { Add-DnsClientNrptRule -Namespace $t -NameServers $P1s -NameEncoding Disable -DAProxyType '' -DAIPsecEncryptionType '' -DnsSecIPsecEncryptionType '' -DAEnable:$false -DAIPsecRequired:$false -DnsSecEnable:$false -DnsSecValidationRequired:$false -DnsSecIPsecRequired:$false -DisplayName ('AgDns ' + $owner) -Comment ('AgDns:' + $owner) -PassThru | Out-Null } }
}
foreach ($case in @(@('t7_k1', $false, $true), @('t7_k2', $true, $true), @('t7_k3', $true, $false))) {
    New-KilledApply $case[0] $case[1] $case[2]
    $beforeRecovery = @($script:MockDoh | Sort-Object ServerAddress | Select-Object ServerAddress,DohTemplate,AutoUpgrade,AllowFallbackToUdp | ConvertTo-Json -Compress)
    $res = ''; try { Restore-State (Read-State) | Out-Null; $res = 'OK' } catch { $res = $_.Exception.Message }
    if ($case[2]) {
        $retained = @($script:MockDoh | Sort-Object ServerAddress | Select-Object ServerAddress,DohTemplate,AutoUpgrade,AllowFallbackToUdp | ConvertTo-Json -Compress)
        Assert-Condition ($res -like '*DOH_OWNERSHIP_UNCERTAIN*' -and (Read-State).Phase -eq 'Restoring' -and $script:MockRules.Count -eq 0 -and $script:MockDoh.Count -eq $P1s.Count -and ($beforeRecovery -join '') -ceq ($retained -join '') -and (Same-Set @($script:MockDoh | ForEach-Object {$_.ServerAddress}) $P1s)) ('T7_K' + $case[0].Substring(4)) 'Killed before DoH ownership commit: recovery removes marked NRPT but retains ambiguous DoH and recovery journal' 'R15-V' -FailureMessage $res
        $again = ''; try { Restore-State (Read-State) | Out-Null } catch { $again = $_.Exception.Message }
        $afterRetry = @($script:MockDoh | Sort-Object ServerAddress | Select-Object ServerAddress,DohTemplate,AutoUpgrade,AllowFallbackToUdp | ConvertTo-Json -Compress)
        Assert-Condition ($again -like '*DOH_OWNERSHIP_UNCERTAIN*' -and (Read-State).Phase -eq 'Restoring' -and ($retained -join '') -ceq ($afterRetry -join '')) ('T7_K' + $case[0].Substring(4) + '_Retry') 'Repeated interrupted recovery preserves ambiguous registrations byte-for-byte and refuses ownership' 'R15-V' -FailureMessage $again
    } else {
        Assert-Condition ($res -eq 'OK' -and (Read-State).Phase -eq 'Restored' -and $script:MockRules.Count -eq 0 -and $script:MockDoh.Count -eq 0) ('T7_K' + $case[0].Substring(4)) 'Killed after marked NRPT creation with no DoH mutation: recovery removes owned rules and closes journal' 'R15-V' -FailureMessage $res
    }
}
