# t6_release_1_4.ps1 - Tier 6: Release 1.4 work items, offline only.
# Nothing here contacts a provider or touches NRPT/DoH/adapters/state.json; engine tests use mock cmdlets and scratch directories.

param(
    [string]$ScratchPath,
    [switch]$EscalateKnownDefects
)

$ErrorActionPreference = 'Stop'
$global:CurrentTier = "Tier 6: Release 1.4 work items"
$script:CurrentTier = $global:CurrentTier

$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\app')).Path
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

. (Join-Path $sourceRoot 'Engine.ps1') -LibraryOnly
$script:RunPath = 'HKCU:\Software\AgDnsTest\Run'
. (Join-Path $PSScriptRoot 'helpers\TestHarness.ps1')
. (Join-Path $PSScriptRoot 'helpers\SyntheticWire.ps1')
. (Join-Path $PSScriptRoot 'helpers\MockCmdlets.ps1')

Write-Host "--- Executing Tier 6: Release 1.4 Tests ---" -ForegroundColor Cyan

$sourceExe = if ($env:AGDNS_EXE) { $env:AGDNS_EXE } else { Join-Path $projectRoot '..\release\AntigravityGeminiDns.exe' }
$haveExe = Test-Path -LiteralPath $sourceExe
$clean = Join-Path $ScratchPath 'clean_exe'
[IO.Directory]::CreateDirectory($clean) | Out-Null
if ($haveExe) { Copy-Item -LiteralPath $sourceExe -Destination $clean }
$exe = Join-Path $clean (Split-Path -Leaf $sourceExe)

function Invoke-Exe([string[]]$Arguments, [int]$TimeoutSec = 90, [hashtable]$Env = @{}) {
    $psi = New-Object Diagnostics.ProcessStartInfo $exe
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8; $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($k in $Env.Keys) { $psi.EnvironmentVariables[$k] = [string]$Env[$k] }
    $psi.Arguments = (@($Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' ')
    $p = [Diagnostics.Process]::Start($psi)
    $so = $p.StandardOutput.ReadToEndAsync(); $se = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { try { $p.Kill() } catch {}; return [pscustomobject]@{ Exit = -1; Out = 'TIMEOUT'; Err = '' } }
    $p.WaitForExit()
    [pscustomobject]@{ Exit = $p.ExitCode; Out = $so.Result; Err = $se.Result }
}
function Invoke-Ps([string]$Command, [int]$TimeoutSec = 60, [string]$FileArgs = '', [hashtable]$Env = @{}) {
    $psi = New-Object Diagnostics.ProcessStartInfo (Join-Path $PSHOME 'powershell.exe')
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    foreach ($k in $Env.Keys) { $psi.EnvironmentVariables[$k] = [string]$Env[$k] }
    # -FileArgs: run a script file the way the exe does (-File), where $Command is the script path.
    $psi.Arguments = $(if ($FileArgs -ne '') { '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $Command + '" ' + $FileArgs } else { '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + ($Command -replace '"', '\"') + '"' })
    $p = [Diagnostics.Process]::Start($psi)
    $so = $p.StandardOutput.ReadToEndAsync(); $se = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { try { $p.Kill() } catch {}; return [pscustomobject]@{ Exit = -1; Out = 'TIMEOUT'; Err = '' } }
    $p.WaitForExit()
    [pscustomobject]@{ Exit = $p.ExitCode; Out = $so.Result; Err = $se.Result }
}

# ==============================================================================
# R14-6: one source for the target domains and the default profile
# ==============================================================================
$catalogFile = Join-Path $sourceRoot 'profiles.json'
$cat = Get-Content -LiteralPath $catalogFile -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-Condition (@($cat.targets).Count -eq 2 -and $cat.default -and (@($cat.profiles | Where-Object { $_.id -eq $cat.default }).Count -eq 1)) 'T6_D01' 'profiles.json defines the protected domains and the default profile' 'R14-6'

$exempt = @('OfflineTests.cs', 'Engine.Tests.ps1')
$dupes = @()
$defaultProfile = $cat.profiles | Where-Object { $_.id -eq $cat.default }
$needles = @($cat.targets) + @($defaultProfile.servers) + @($defaultProfile.template, $defaultProfile.id)
foreach ($f in Get-ChildItem -LiteralPath $sourceRoot -File | Where-Object { $_.Extension -in '.cs', '.ps1', '.csproj' -and $_.Name -notin $exempt -and $_.Name -notlike 'OfflineTests*.cs' }) {
    $text = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8
    foreach ($n in $needles) { if ($text.Contains([string]$n)) { $dupes += "$($f.Name): $n" } }
}
Assert-Condition ($dupes.Count -eq 0) 'T6_D02' 'No source file outside profiles.json and the self-tests repeats the default domains, IPs, template or profile id' 'R14-6' -FailureMessage ($dupes -join '; ')

$libArgs = ". '$sourceRoot\Engine.ps1' -LibraryOnly -Targets 'a.example.test,b.example.test' -Servers '1.2.3.4' -Template 'https://x.example.test/dns-query'; (@(`$script:Targets) -join '|') + ' ' + (@(`$script:Servers) -join '|') + ' ' + `$script:Template"
$libRun = Invoke-Ps $libArgs
Assert-Condition (($libRun.Out).Trim() -eq 'a.example.test|b.example.test 1.2.3.4 https://x.example.test/dns-query') 'T6_D03' 'Engine.ps1 takes -Targets/-Servers/-Template from its parameters, not from built-in copies' 'R14-6' -FailureMessage ($libRun.Out + $libRun.Err)

$libDefault = Invoke-Ps ". '$sourceRoot\Engine.ps1' -LibraryOnly; (@(`$script:Targets) -join '|') + ' ' + (@(`$script:Servers) -join '|') + ' ' + `$script:Template"
$expectDefault = (@($cat.targets) -join '|') + ' ' + (@($defaultProfile.servers) -join '|') + ' ' + $defaultProfile.template
Assert-Condition (($libDefault.Out).Trim() -eq $expectDefault) 'T6_D04' 'Without parameters the engine reads its defaults from the catalog file next to it' 'R14-6' -FailureMessage ($libDefault.Out + $libDefault.Err)

$noCat = Join-Path $ScratchPath 'engine_without_catalog'
[IO.Directory]::CreateDirectory($noCat) | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceRoot 'Engine.ps1') -Destination $noCat
$missing = Invoke-Ps "$noCat\Engine.ps1" 60 '-Action status'
Assert-Condition ($missing.Exit -eq 1 -and $missing.Out -match 'ENGINE_TARGETS_MISSING') 'T6_D05' 'An engine with neither -Targets nor a catalog file refuses to run (no hidden fallback)' 'R14-6' -FailureMessage ($missing.Out + $missing.Err)

if ($haveExe) {
    $ver = Invoke-Exe @('--version')
    $csprojVersion = ([regex]::Match((Get-Content -LiteralPath (Join-Path $sourceRoot 'AntigravityGeminiDns.csproj') -Raw), '<Version>([^<]+)</Version>')).Groups[1].Value
    Assert-Condition ($ver.Exit -eq 0 -and $ver.Out.Trim() -eq $csprojVersion -and $csprojVersion) 'T6_D06' "--version equals <Version> in the .csproj ($csprojVersion)" 'R14-6'

    $exportDir = Join-Path $ScratchPath 'exported14'
    $export = Invoke-Exe @('--export-scripts', $exportDir)
    $sums = Get-Content -LiteralPath (Join-Path $exportDir 'SHA256SUMS.txt') -ErrorAction SilentlyContinue
    $catHash = (Get-FileHash -LiteralPath $catalogFile -Algorithm SHA256).Hash
    Assert-Condition ($export.Exit -eq 0 -and (Test-Path (Join-Path $exportDir 'profiles.json')) -and ($sums -match $catHash)) 'T6_D07' '--export-scripts also exports profiles.json (hash-listed) so EmergencyRestore.ps1 needs no baked-in domains' 'R14-6'

    $exported = Invoke-Ps ". '$exportDir\Engine.ps1' -LibraryOnly; (@(`$script:Targets) -join '|')"
    Assert-Condition (($exported.Out).Trim() -eq (@($cat.targets) -join '|')) 'T6_D08' 'The exported Engine.ps1 finds the domains in the exported catalog' 'R14-6' -FailureMessage ($exported.Out + $exported.Err)

    $vs = Invoke-Exe @('--verify-scripts'); $vj = $null; try { $vj = $vs.Out | ConvertFrom-Json } catch {}
    Assert-Condition ($vj -and $vj.Verified -and $vj.Hashes.'profiles.json' -eq $catHash -and (Test-Path (Join-Path $vj.Directory 'profiles.json'))) 'T6_D09' 'The staged script folder carries a hash-verified profiles.json identical to source\profiles.json' 'R14-6'
}

# ==============================================================================
# R14-4: choice of the best profile (reach >= 75 %, experimental flag), through --benchmark-synthetic
# ==============================================================================
$D1 = 'daily-cloudcode-pa.googleapis.com'; $D2 = 'generativelanguage.googleapis.com'
function New-A([string]$name, [string[]]$ips) { [Convert]::ToBase64String((New-SyntheticDnsResponse -QuestionDomain $name -QuestionType 1 -Id 1234 -AnswerRecords @($ips | ForEach-Object { New-ARecord $name $_ }))) }
function New-SynProfile([string]$id, $q1, $q2, $answerTls = @{}, [bool]$experimental = $false) {
    @{ id = $id; template = "https://$id.test/dns-query"; servers = @('193.233.112.67', '193.233.112.68'); experimental = $experimental
       tls = @(@{ ok = $true; ms = 40 }); queries = @{ $D1 = @($q1); $D2 = @($q2) }; answerTls = $answerTls }
}
$pool = @('193.233.112.71', '193.233.112.72', '193.233.112.73', '193.233.112.74')
$one = { param($host_, $ms, $ip) @(@{ ms = $ms; packet = (New-A $host_ @($ip)) }) }
$healthyP = New-SynProfile 'healthy' (& $one $D1 80 '193.233.112.67') (& $one $D2 80 '193.233.112.88')
$halfdeadP = New-SynProfile 'halfdead' @(@{ ms = 20; packet = (New-A $D1 $pool) }) (& $one $D2 20 '193.233.112.88') @{ $pool[1] = $false; $pool[2] = $false; $pool[3] = $false }
$expSlowP = New-SynProfile 'expslow' (& $one $D1 900 '193.233.112.67') (& $one $D2 900 '193.233.112.88') @{} $true
$expGoodP = New-SynProfile 'expgood' (& $one $D1 30 '193.233.112.67') (& $one $D2 30 '193.233.112.88') @{} $true

function Invoke-Scenario([string]$name, $profiles) {
    $file = Join-Path $ScratchPath ("scenario_$name.json")
    (@{ attempts = 6; profiles = $profiles } | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $file -Encoding UTF8
    $out = Invoke-Exe @('--benchmark-synthetic', $file)
    try { return ($out.Out | ConvertFrom-Json) } catch { return $null }
}
if ($haveExe) {
    $runA = Invoke-Scenario 'A' @($halfdeadP, $healthyP, $expSlowP)
    $PA = @{}; if ($runA) { foreach ($res in $runA.Results) { $PA[$res.Id] = $res } }
    Assert-Condition ($runA -and $PA['halfdead'].MedianMs -lt $PA['healthy'].MedianMs -and $PA['halfdead'].DomainsOk -and $PA['halfdead'].Reach -lt 0.75) 'T6_B01' 'Partly dead pool: better latency than the healthy profile, answer-address reach below 75%' 'R14-4'
    Assert-Condition ($runA -and -not $PA['halfdead'].Selectable -and $PA['halfdead'].Verdict -eq 'Flaky') 'T6_B02' 'Partly dead pool is not selectable' 'R14-4'
    Assert-Condition ($runA -and $runA.Winner -eq 'healthy' -and $PA['halfdead'].Rank -gt $PA['healthy'].Rank) 'T6_B03' 'The partly dead pool with good latency ranks below the healthy profile; the healthy one is the winner' 'R14-4'
    Assert-Condition ($runA -and $PA['expslow'].Experimental -and $PA['expslow'].Selectable -and -not $PA['expslow'].Best -and $PA['expslow'].Rank -lt $PA['halfdead'].Rank) 'T6_B04' 'An experimental profile without the Good verdict is never the winner' 'R14-4'
    $runB = Invoke-Scenario 'B' @($halfdeadP, $expSlowP)
    Assert-Condition ($runB -and -not $runB.Winner -and @($runB.Results | Where-Object { $_.Best }).Count -eq 0) 'T6_B05' 'With only weak or experimental-not-Good profiles there is no recommendation' 'R14-4'
    $runC = Invoke-Scenario 'C' @($healthyP, $expGoodP)
    Assert-Condition ($runC -and $runC.Winner -eq 'healthy' -and @($runC.Results | Where-Object { $_.Id -eq 'expgood' -and $_.Recommendable }).Count -eq 1) 'T6_B06' 'An experimental profile that earns the Good verdict is recommendable but does not displace a Good confirmed one' 'R14-4'
    $pl = Invoke-Exe @('--profiles-list'); $plj = $null; try { $plj = $pl.Out | ConvertFrom-Json } catch {}
    $expSlots = @($plj.Profiles | Where-Object { $_.Experimental } | ForEach-Object { $_.Slot } | Sort-Object)
    Assert-Condition (($expSlots -join ',') -eq 'P4,P7,P8') 'T6_B07' 'The catalog flags exactly P4, P7 and P8 as experimental' 'R14-4'
}

# ==============================================================================
# R14-3: switching profiles never costs a working bridge (mock cmdlets, no live changes)
# ==============================================================================
$P1s = @('179.254.166.171', '164.5.224.81'); $P1t = 'https://dns.malw.link/dns-query'
$P2s = @('185.185.185.185', '185.185.185.186'); $P2t = 'https://xbox-dns.ru/dns-query'
function New-ActiveBridge([string]$name) {
    Reset-MockEnvironment (Join-Path $ScratchPath $name)
    $script:Servers = $P1s; $script:Template = $P1t; $script:ProfileName = 'dns.malw.link'
    return (Enable-State $true $false)
}
function Switch-ToP2 {
    $script:Servers = $P2s; $script:Template = $P2t; $script:ProfileName = 'xbox-dns.ru'
    try { Enable-State $true $false | Out-Null; return 'NO_ERROR' } catch { return $_.Exception.Message }
}
function Test-P1Intact {
    $st = Read-State
    return ($st.Phase -eq 'Active' -and $st.ProfileName -eq 'dns.malw.link' -and (Same-Set $st.Servers $P1s) -and $script:MockRules.Count -eq 2 -and
        @($script:MockRules | Where-Object { Same-Set (Split-Servers $_.NameServers) $P1s }).Count -eq 2 -and
        $script:MockDoh.Count -eq 2 -and @($script:MockDoh | Where-Object { $_.ServerAddress -in $P1s -and $_.DohTemplate -eq $P1t }).Count -eq 2 -and
        (Get-Status).State -eq 'DNS active')
}

# (a) a conflict is found while the old bridge is still in place: nothing is removed
New-ActiveBridge 't6_sw_a' | Out-Null
$script:MockRules.Add([pscustomobject]@{ Name = '{00000000-0000-0000-0000-0000000000f1}'; Namespace = @('.googleapis.com'); NameServers = '1.1.1.1'; DisplayName = 'Foreign'; Comment = 'Foreign' })
$writesBefore = $script:MockWriteCount
$msgA = Switch-ToP2
$st = Read-State
Assert-Condition ($msgA -like '*NRPT_CONFLICT*' -and $script:MockWriteCount -eq $writesBefore -and $st.Phase -eq 'Active' -and $st.ProfileName -eq 'dns.malw.link' -and $script:MockRules.Count -eq 3 -and $script:MockDoh.Count -eq 2 -and (Same-Set $script:MockDoh.ServerAddress $P1s)) 'T6_S01' 'Switch: a foreign NRPT conflict is detected before anything is removed (no writes, journal still Active on the old profile)' 'R14-3' -FailureMessage $msgA

New-ActiveBridge 't6_sw_a2' | Out-Null
$script:MockDoh.Add([pscustomobject]@{ ServerAddress = $P2s[0]; DohTemplate = 'https://someone-else.example/dns-query'; AutoUpgrade = $true; AllowFallbackToUdp = $false })
$writesBefore = $script:MockWriteCount
$msgA2 = Switch-ToP2
Assert-Condition ($msgA2 -like '*DOH_REGISTRATION_CONFLICT*' -and $script:MockWriteCount -eq $writesBefore -and $script:MockRules.Count -eq 2 -and (Read-State).Phase -eq 'Active' -and @($script:MockDoh | Where-Object { $_.ServerAddress -in $P1s }).Count -eq 2) 'T6_S02' 'Switch: a clashing DoH registration for the new IP is detected before anything is removed' 'R14-3' -FailureMessage $msgA2

New-ActiveBridge 't6_sw_a3' | Out-Null
$script:MockNetworkFailure = $true
$writesBefore = $script:MockWriteCount
$msgA3 = Switch-ToP2
$script:MockNetworkFailure = $false
Assert-Condition ($msgA3 -like '*DOH_NO_VALIDATED_ENDPOINT*' -and $script:MockWriteCount -eq $writesBefore -and (Test-P1Intact)) 'T6_S03' 'Switch: a new profile that fails preflight leaves the working bridge untouched' 'R14-3'

# (b) applying the new profile fails after the old one was removed: the previous profile is active again and verified
New-ActiveBridge 't6_sw_b' | Out-Null
$script:MockFailDohAt = $script:MockDohAddCount + 1
$msgB = Switch-ToP2
Assert-Condition ($msgB -like 'PROFILE_SWITCH_FAILED_PREVIOUS_RESTORED*' -and $msgB -match 'dns\.malw\.link' -and (Test-P1Intact)) 'T6_S04' 'Switch: the new profile failed after the old one was removed -> the previous profile is applied again, verified and named in the result' 'R14-3' -FailureMessage $msgB
Assert-Condition (@($script:MockRules | Where-Object { $_.NameServers -match '185\.185' }).Count -eq 0 -and @($script:MockDoh | Where-Object { $_.ServerAddress -in $P2s }).Count -eq 0) 'T6_S05' 'Switch: nothing of the failed new profile is left behind' 'R14-3'

# (c) neither the new nor the previous profile can be applied: explicit RECOVERY_REQUIRED, no false success
New-ActiveBridge 't6_sw_c' | Out-Null
$script:MockFailDohFrom = $script:MockDohAddCount + 1
$msgC = Switch-ToP2
$stC = Read-State
Assert-Condition ($msgC -like 'RECOVERY_REQUIRED*' -and $msgC -match 'dns\.malw\.link' -and $msgC -match 'could not be restored' -and $stC.Phase -ne 'Active' -and $script:MockRules.Count -eq 0 -and $script:MockDoh.Count -eq 0) 'T6_S06' 'Switch: new and previous profile both fail -> RECOVERY_REQUIRED naming both failures, the journal is not Active, no leftovers' 'R14-3' -FailureMessage $msgC
$script:MockFailDohFrom = 0

# a successful switch still works and leaves the new profile active
New-ActiveBridge 't6_sw_ok' | Out-Null
$msgOk = Switch-ToP2
$stOk = Read-State
Assert-Condition ($msgOk -eq 'NO_ERROR' -and $stOk.Phase -eq 'Active' -and $stOk.ProfileName -eq 'xbox-dns.ru' -and (Same-Set $stOk.Servers $P2s) -and $script:MockRules.Count -eq 2 -and $script:MockDoh.Count -eq 2) 'T6_S07' 'Switch: a healthy switch replaces the profile and the journal names the new one' 'R14-3' -FailureMessage $msgOk

$engineText = Get-Content -LiteralPath (Join-Path $sourceRoot 'Engine.ps1') -Raw
$ei = $engineText.IndexOf('function Enable-State'); $enableBody = $engineText.Substring($ei, $engineText.IndexOf('function Get-ActiveProfileName') - $ei)
$posNet = $enableBody.IndexOf('$net=Get-Network'); $posConf = $enableBody.IndexOf('Test-ForeignConflict $s'); $posDoh = $enableBody.IndexOf('Test-DohRegistrationConflict $selected'); $posRestore = $enableBody.IndexOf('Restore-State $s | Out-Null')
Assert-Condition ($posNet -gt 0 -and $posNet -lt $posConf -and $posConf -lt $posRestore -and $posDoh -lt $posRestore) 'T6_S08' 'Enable-State order: preflight, conflict check and DoH check all come before Restore-State' 'R14-3'

# ==============================================================================
# R14-2: catalog 1.4 and the engine side of connect-time addresses
# ==============================================================================
$byIdCat = @{}; foreach ($p in $cat.profiles) { $byIdCat[$p.id] = $p }
$p3 = $byIdCat['dns.comss.one']
Assert-Condition ((@($p3.servers) -join ',') -eq '195.133.25.16,212.109.195.93,94.250.253.55,185.146.157.78' -and $p3.bootstrap.date -eq '2026-10-26' -and (@($p3.servers | Where-Object { $_ -notin @($p3.bootstrap.pinned) }).Count -eq 0) -and $p3.bootstrap.tls) 'T6_A01' 'P3 pins are the four addresses that passed bootstrap on 2026-10-26, with provenance recorded' 'R14-2'
Assert-Condition (($cat.pending | Where-Object { $_.id -eq 'dns.astracat.ru' }) -and -not $byIdCat.ContainsKey('dns.astracat.ru')) 'T6_A02' 'Astracat stays pending (not shipped as a pin)' 'R14-2'
$expIds = @($cat.profiles | Where-Object { $_.PSObject.Properties['experimental'] -and $_.experimental } | ForEach-Object { $_.id } | Sort-Object)
Assert-Condition (($expIds -join ',') -eq 'dns.zapret.moe,eu.geohide.ru,geohide.ru') 'T6_A03' 'The degraded geohide.ru and the new zapret/GeoHide EU profiles are experimental; no other profile is' 'R14-2'

$nameOnly = Invoke-Ps ". '$sourceRoot\Engine.ps1' -LibraryOnly -Template 'https://custom.example.test/dns-query'; 'servers=' + @(`$script:Servers).Count; try { Get-Network } catch { `$_.Exception.Message }"
Assert-Condition ($nameOnly.Out -match 'servers=0' -and $nameOnly.Out -match 'NO_ADDRESSES_FOR_PROFILE') 'T6_A04' 'A name-only profile does not borrow the default profile''s IPs; the engine refuses to probe without addresses' 'R14-2' -FailureMessage ($nameOnly.Out + $nameOnly.Err)

# The elevated engine repeats preflight and takes only validated addresses from the list it is given (it does not trust the UI list).
Reset-MockEnvironment (Join-Path $ScratchPath 't6_a05')
$script:Servers = @('83.220.169.155', '195.133.25.16', '212.109.195.93', '185.185.185.185'); $script:Template = 'https://dns.comss.one/dns-query'; $script:ProfileName = 'dns.comss.one'
$script:MockPartialNetworkOnlyOneServer = $true
Enable-State $true $false | Out-Null
$stA = Read-State
Assert-Condition ($stA.Phase -eq 'Active' -and @($stA.Servers).Count -eq 1 -and $stA.Servers[0] -eq '83.220.169.155' -and $script:MockDoh.Count -eq 1) 'T6_A05' 'The engine keeps only the addresses that pass its own preflight, whatever list the UI handed over' 'R14-2'
Restore-State (Read-State) | Out-Null
$script:MockPartialNetworkOnlyOneServer = $false
Reset-MockEnvironment (Join-Path $ScratchPath 't6_a06')
$script:Servers = @('83.220.169.155'); $script:Template = 'https://dns.comss.one/dns-query'
$script:MockNetworkFailure = $true
$rej = ''; try { Enable-State $true $false | Out-Null } catch { $rej = $_.Exception.Message }
Assert-Condition ($rej -like '*DOH_NO_VALIDATED_ENDPOINT*' -and $script:MockWriteCount -eq 0 -and -not (Test-Path (Join-Path $ScratchPath 't6_a06\state.json'))) 'T6_A06' 'Addresses that fail the engine''s preflight (e.g. a spoofed resolver answer) are refused before any write' 'R14-2'
$script:MockNetworkFailure = $false

# ==============================================================================
# R14-1: CIM hang gate, restore from Restoring, RESTORE_UNCONFIRMED instruction
# ==============================================================================
# Restore is always allowed from Restoring, and an interrupted restore can be repeated.
New-ActiveBridge 't6_rs_a' | Out-Null
$script:MockFailRemoveRuleAt = 1
$firstRestore = ''; try { Restore-State (Read-State) | Out-Null; $firstRestore = 'NO_ERROR' } catch { $firstRestore = $_.Exception.Message }
$mid = Read-State
Assert-Condition ($firstRestore -like '*SIMULATED_REMOVE_FAILURE*' -and $mid.Phase -eq 'Restoring' -and $script:MockRules.Count -eq 2) 'T6_R01' 'An interrupted restore leaves the journal in Restoring with the rules still present' 'R14-1' -FailureMessage $firstRestore
$script:MockFailRemoveRuleAt = 0
$enableWhileRestoring = ''; try { $script:Servers = $P1s; $script:Template = $P1t; Enable-State $true $false | Out-Null; $enableWhileRestoring = 'NO_ERROR' } catch { $enableWhileRestoring = $_.Exception.Message }
Assert-Condition ($enableWhileRestoring -like 'RECOVERY_REQUIRED*') 'T6_R02' 'A new connect is refused while the journal is in Restoring (restore first)' 'R14-1' -FailureMessage $enableWhileRestoring
Restore-State (Read-State) | Out-Null
Assert-Condition ((Read-State).Phase -eq 'Restored' -and $script:MockRules.Count -eq 0 -and $script:MockDoh.Count -eq 0) 'T6_R03' 'Restore from the Restoring phase is always allowed and completes' 'R14-1'
$mid2 = Restore-State (Read-State)
Assert-Condition ($mid2.Result -eq 'No-op') 'T6_R04' 'A repeated restore on a restored journal is a harmless no-op' 'R14-1'

# A journal written by 1.3.0 (same schema 1, no new fields) is read and restored by this engine.
Reset-MockEnvironment (Join-Path $ScratchPath 't6_rs_130')
$script:Servers = $P1s; $script:Template = $P1t; $script:ProfileName = 'dns.malw.link'
$ownerId = 'b' * 32
$ruleObjs = @(); $ruleJson = @()
foreach ($t in $script:Targets) {
    $r = Add-DnsClientNrptRule -Namespace $t -NameServers $P1s -NameEncoding Disable -DisplayName ("AgDns $ownerId") -Comment ("AgDns:$ownerId") -PassThru
    $ruleJson += ('{"Namespace":"' + $t + '","Name":"' + $r.Name + '","Fingerprint":' + ((Rule-Fingerprint $r) | ConvertTo-Json -Compress) + '}')
}
foreach ($ip in $P1s) { Add-DnsClientDohServerAddress -ServerAddress $ip -DohTemplate $P1t -AllowFallbackToUdp $false -AutoUpgrade $true | Out-Null }
$j130 = '{"Schema":1,"Owner":"' + $ownerId + '","Phase":"Active","ProfileName":"dns.malw.link","Template":"' + $P1t + '","Protocol":"DoH","Targets":["' + ($script:Targets -join '","') + '"],"Servers":["' + ($P1s -join '","') + '"],"Rules":[' + ($ruleJson -join ',') + '],"Doh":[' + (($P1s | ForEach-Object { '{"Server":"' + $_ + '","Template":"' + $P1t + '","Created":true,"Ownership":"Owned"}' }) -join ',') + '],"UpdatedUtc":"2026-10-11T19:00:00.0000000Z"}'
Initialize-StateDirectory
[IO.File]::WriteAllText($script:StatePath, $j130, [Text.UTF8Encoding]::new($false))
$read130 = Read-State
Assert-Condition ($read130.Phase -eq 'Active' -and (Get-Status).State -eq 'DNS active') 'T6_R05' 'A 1.3.0-style journal is read and the bridge it describes is recognised as active' 'R14-6'
Restore-State (Read-State) | Out-Null
Assert-Condition ((Read-State).Phase -eq 'Restored' -and $script:MockRules.Count -eq 0 -and $script:MockDoh.Count -eq 0) 'T6_R06' 'A 1.3.0-style journal is restored by this engine' 'R14-6'

# CIM hang: the real engine run by the real exe resolves Get-DnsClient* to the fake module (AGDNS_TEST_PSMODULEPATH, honoured by a non-elevated run only), which sleeps for minutes.
if ($haveExe) {
    $fakeDir = Join-Path $PSScriptRoot 'helpers\FakeCim'
    $fakeLog = Join-Path $ScratchPath 'fake_cim_mutations.log'
    $envHang = @{ AGDNS_TEST_PSMODULEPATH = $fakeDir; AGDNS_FAKE_CIM = 'hang'; AGDNS_FAKE_CIM_LOG = $fakeLog }
    $swGuard = [Diagnostics.Stopwatch]::StartNew()
    $hung = Invoke-Exe @('--preflight') 90 $envHang
    $swGuard.Stop()
    $hj = $null; try { $hj = $hung.Out | ConvertFrom-Json } catch {}
    Assert-Condition ($hung.Exit -eq 1 -and $hj -and $hj.Allowed -eq $false -and $hj.Code -eq 'WMI_NOT_RESPONDING') 'T6_R07' 'Hanging CIM: the pre-check reports WMI_NOT_RESPONDING (mutation does not start)' 'R14-1' -FailureMessage ($hung.Out + $hung.Err)
    Assert-Condition ($swGuard.Elapsed.TotalSeconds -ge 18 -and $swGuard.Elapsed.TotalSeconds -lt 40) 'T6_R08' ("The pre-check gives up after two attempts of ten seconds (took $([math]::Round($swGuard.Elapsed.TotalSeconds,1)) s)") 'R14-1'
    Assert-Condition (-not (Test-Path -LiteralPath $fakeLog)) 'T6_R09' 'No mutating cmdlet was called while CIM hung (the fake mutation log is empty)' 'R14-1'

    $envOk = @{ AGDNS_TEST_PSMODULEPATH = $fakeDir; AGDNS_FAKE_CIM = 'ok'; AGDNS_FAKE_CIM_LOG = $fakeLog }
    $alive = Invoke-Exe @('--preflight') 60 $envOk
    $aj = $null; try { $aj = $alive.Out | ConvertFrom-Json } catch {}
    Assert-Condition ($alive.Exit -eq 0 -and $aj -and $aj.Allowed -eq $true -and -not (Test-Path -LiteralPath $fakeLog)) 'T6_R10' 'Responsive CIM: the pre-check passes (the same stub, not hanging)' 'R14-1' -FailureMessage ($alive.Out + $alive.Err)

    # Resolve the cmdlets without invoking CIM: host WMI availability is independent of module-search isolation.
    # The negative control proves that the inherited path really selects the planted module without the engine's guard.
    $envHijack = @{ PSModulePath = ($fakeDir + ';' + $env:PSModulePath); AGDNS_FAKE_CIM = 'hang'; AGDNS_FAKE_CIM_LOG = $fakeLog }
    $resolve = '(Get-Command Get-DnsClientNrptRule -ErrorAction Stop).Module.Path'
    $control = Invoke-Ps -Command $resolve -Env $envHijack
    $fakeModule = Join-Path $fakeDir 'DnsClient\DnsClient.psm1'
    Assert-Condition ($control.Exit -eq 0 -and $control.Out.Trim() -ieq $fakeModule) 'T6_M00' 'Negative control: an inherited user module path selects the planted DnsClient module' 'R-AUDIT' -FailureMessage ($control.Out + $control.Err)
    $importEngine = ". '" + (Join-Path $sourceRoot 'Engine.ps1').Replace("'", "''") + "' -LibraryOnly; "
    $hij = Invoke-Ps -Command ($importEngine + $resolve) -Env $envHijack
    $systemModule = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\Modules\DnsClient\DnsClient.psm1'
    $resolvedModule = $hij.Out.Trim()
    $systemDnsRoot = Split-Path -Parent $systemModule
    Assert-Condition ($hij.Exit -eq 0 -and $resolvedModule.StartsWith($systemDnsRoot+'\',[StringComparison]::OrdinalIgnoreCase) -and -not (Test-Path -LiteralPath $fakeLog)) 'T6_M01' 'Engine resolves DnsClient only from the system module directory, independently of WMI health' 'R-AUDIT' -FailureMessage ($hij.Out + $hij.Err)

    # RESTORE_UNCONFIRMED instruction from a journal file (read-only CLI, no registry access)
    $jf = Join-Path $ScratchPath 'journal_restoring.json'
    $jr = $j130.Replace('"Phase":"Active"', '"Phase":"Restoring"')
    [IO.File]::WriteAllText($jf, $jr, [Text.UTF8Encoding]::new($false))
    $ins = Invoke-Exe @('--recovery-instructions', $jf)
    $ruleName = ($jr | ConvertFrom-Json).Rules[0].Name.Trim('{', '}')
    Assert-Condition ($ins.Exit -eq 0 -and $ins.Out.Contains('DnsPolicyConfig\{' + $ruleName.ToLowerInvariant() + '}') -and $ins.Out.Contains('DohWellKnownServers\' + $P1s[0]) -and $ins.Out.Contains("AgDns:$ownerId")) 'T6_R11' '--recovery-instructions prints the exact NRPT/DoH keys and the owner marker from the journal' 'R14-1' -FailureMessage $ins.Out
    $none = Invoke-Exe @('--recovery-instructions', (Join-Path $ScratchPath 'does_not_exist.json'))
    Assert-Condition ($none.Exit -eq 1 -and $none.Out.Trim() -eq 'NOTHING_TO_RECOVER') 'T6_R12' '--recovery-instructions reports NOTHING_TO_RECOVER for a missing journal' 'R14-1'
}
$prodText = (Get-Content -LiteralPath (Join-Path $sourceRoot 'Recovery.cs') -Raw)
Assert-Condition ($prodText -notmatch 'Registry\.|OpenSubKey|DeleteSubKey|SetValue|reg\.exe') 'T6_R13' 'The registry fallback is not wired into production code (Recovery.cs only generates text)' 'R14-1'

# ==============================================================================
# R14-5: every code the engine or the exe can return has a plain Russian text and one action
# ==============================================================================
$codeRx = "'([A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+)(?=[:' ])"
$found = @{}
foreach ($f in 'Engine.ps1', 'EmergencyRestore.ps1') {
    $text = Get-Content -LiteralPath (Join-Path $sourceRoot $f) -Raw -Encoding UTF8
    foreach ($m in [regex]::Matches($text, $codeRx)) { $found[$m.Groups[1].Value] = $f }
}
$integration = Get-Content -LiteralPath (Join-Path $sourceRoot 'Integration.ps1') -Raw -Encoding UTF8
foreach ($m in [regex]::Matches($integration, "'(LIVE_INTEGRATION_(?:PASS|FAILED))")) { $found[$m.Groups[1].Value] = 'Integration.ps1' }
foreach ($f in 'Program.cs', 'Recovery.cs', 'Connect.cs') {
    $text = Get-Content -LiteralPath (Join-Path $sourceRoot $f) -Raw -Encoding UTF8
    foreach ($m in [regex]::Matches($text, '(?:Code|Error)="([A-Z][A-Z0-9_]+)"')) { $found[$m.Groups[1].Value] = $f }
    foreach ($m in [regex]::Matches($text, 'const string \w+="([A-Z][A-Z0-9_]+)"')) { $found[$m.Groups[1].Value] = $f }
}
$ignore = @('AUTH_REQUIRED_FOR_E2E_TEST')
$wanted = @($found.Keys | Where-Object { $_ -notin $ignore } | Sort-Object)
Assert-Condition ($wanted.Count -ge 40) 'T6_E01' "The source scan found the engine/exe codes ($($wanted.Count) distinct)" 'R14-5' -FailureMessage ($wanted -join ',')

if ($haveExe) {
    $cl = Invoke-Exe @('--codes-list'); $table = $null; try { $table = $cl.Out | ConvertFrom-Json } catch {}
    $have = @{}; if ($table) { foreach ($row in $table) { $have[$row.Code] = $row } }
    $missing = @($wanted | Where-Object { -not $have.ContainsKey($_) })
    Assert-Condition ($table -and $missing.Count -eq 0) 'T6_E02' 'Every code found in Engine.ps1 / Integration.ps1 / Program.cs / Recovery.cs / Connect.cs has an entry in the message table' 'R14-5' -FailureMessage ('missing: ' + ($missing -join ', '))
    $bad = @($table | Where-Object { -not $_.Text -or -not $_.Action -or -not $_.Title -or $_.Text -notmatch '[\u0400-\u04FF]' -or $_.Action -notmatch '[\u0400-\u04FF]' })
    Assert-Condition ($table -and $bad.Count -eq 0) 'T6_E03' 'Each table entry has a title, a Russian text and exactly one Russian action sentence' 'R14-5' -FailureMessage (($bad | ForEach-Object { $_.Code }) -join ', ')
    $restoreKinds = @($table | Where-Object { $_.Kind -eq 'Restore' })
    Assert-Condition ($restoreKinds.Count -ge 4 -and @($restoreKinds | Where-Object { $_.Action -notmatch 'Восстановить' }).Count -eq 0 -and $have.ContainsKey('RESTORE_UNCONFIRMED') -and $have.ContainsKey('WMI_NOT_RESPONDING')) 'T6_E04' 'Codes that need a restore point at the one "Восстановить" action; WMI_NOT_RESPONDING and RESTORE_UNCONFIRMED exist' 'R14-5'
}

# ==============================================================================
# R14-6, packaging: archive name carries the .csproj version, which the exe also reports
# ==============================================================================
if ($haveExe) {
    $distDir = Join-Path $ScratchPath 'dist'
    $pk = & (Join-Path $PSHOME 'powershell.exe') -NoProfile -ExecutionPolicy Bypass -File (Join-Path $sourceRoot 'Package.ps1') -ExePath $sourceExe -OutputDirectory $distDir 2>&1 | Out-String
    $expectedZip = Join-Path $distDir "AntigravityGeminiDns-$csprojVersion-win-x64.zip"
    Assert-Condition ((Test-Path -LiteralPath $expectedZip) -and (Test-Path -LiteralPath (Join-Path $distDir 'SHA256SUMS.txt'))) 'T6_P01' "Package.ps1 produces AntigravityGeminiDns-$csprojVersion-win-x64.zip and SHA256SUMS.txt" 'R14-6' -FailureMessage $pk
    if (Test-Path -LiteralPath $expectedZip) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zf = [IO.Compression.ZipFile]::OpenRead($expectedZip)
        try { $entries = @($zf.Entries | ForEach-Object { $_.FullName }) } finally { $zf.Dispose() }
        $sumsText = Get-Content -LiteralPath (Join-Path $distDir 'SHA256SUMS.txt') -Raw
        $zipHash2 = (Get-FileHash -LiteralPath $expectedZip -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert-Condition (($entries -join ',') -eq 'AntigravityGeminiDns.exe' -and $sumsText.ToLowerInvariant().Contains($zipHash2 + '  ' + (Split-Path $expectedZip -Leaf).ToLowerInvariant())) 'T6_P02' 'The archive holds only the exe and SHA256SUMS.txt lists the independently computed archive hash (hex case-insensitive)' 'R14-6'
    }
}

# CLI help lists the 1.4 read-only flags
if ($haveExe) {
    $help = Invoke-Exe @('--help')
    $helpOk = $help.Exit -eq 0
    foreach ($flag in '--version', '--preflight', '--recovery-instructions', '--codes-list', '--states') { if ($help.Out -notmatch [regex]::Escape($flag)) { $helpOk = $false } }
    Assert-Condition $helpOk 'T6_H01' '--help documents --version, --preflight, --recovery-instructions, --codes-list and --ui-smoke --states' 'R14-6' -FailureMessage $help.Out
}
