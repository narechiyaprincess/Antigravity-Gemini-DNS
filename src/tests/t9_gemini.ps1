# t9_gemini.ps1 - Tier 9: Gemini scope, offline only.
# The second host set of the catalog (geminiTargets) is a scope of the bridge: off = the old behaviour, on = rules for every host, a change of scope on a
# working bridge is a compensated profile switch (the old bridge stays if the new one cannot be applied), and nothing but the catalog can name a host.
# Engine tests run on mock cmdlets and a scratch directory; nothing contacts a provider or touches NRPT/DoH/adapters/state.json.
# This file is saved with a BOM: Windows PowerShell 5.1 mangles non-ASCII text in a script without one.

param(
    [string]$ScratchPath,
    [switch]$EscalateKnownDefects
)

$ErrorActionPreference = 'Stop'
$global:CurrentTier = "Tier 9: Gemini host set"
$script:CurrentTier = $global:CurrentTier

$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\app')).Path
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

. (Join-Path $sourceRoot 'Engine.ps1') -LibraryOnly
# the engine's own network helpers, before the shared mocks replace them (G04d runs them against a stub exe)
$script:RealGetNetwork = ${function:Get-Network}
$script:RealGetSystemAnswers = ${function:Get-SystemAnswers}
$script:RunPath = 'HKCU:\Software\AgDnsTest\Run'
. (Join-Path $PSScriptRoot 'helpers\TestHarness.ps1')
. (Join-Path $PSScriptRoot 'helpers\SyntheticWire.ps1')
. (Join-Path $PSScriptRoot 'helpers\MockCmdlets.ps1')

Write-Host "--- Executing Tier 9: Gemini host set Tests ---" -ForegroundColor Cyan

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

# Scope-aware replacements of the shared mocks: an answer per target in the current scope, and a provider that can be made to fail only for the Gemini hosts.
$script:GeminiDown = $false
function Get-Network {
    if ($script:MockNetworkOfflineTimeout) { throw "NETWORK_PROBE_FAILED: timeout or network unreachable" }
    $serversToTest = if ($script:Servers -and $script:Servers.Count -gt 0) { $script:Servers } else { @('179.254.166.171', '164.5.224.81') }
    $hasGemini = @(@($script:Targets) | Where-Object { $_ -in @($script:GeminiHosts) }).Count -gt 0
    $answers = [ordered]@{}; $i = 0
    foreach ($t in @($script:Targets)) { $answers[$t] = @('179.254.166.' + (174 + $i)); $i++ }
    $candidates = @(foreach ($srv in $serversToTest) {
        [pscustomobject]@{ Server = $srv; DohValidated = !($hasGemini -and $script:GeminiDown); PlainValidated = !($hasGemini -and $script:GeminiDown); TargetA = @('179.254.166.174'); TargetAnswers = [pscustomobject]$answers }
    })
    return [pscustomobject]@{ TimestampUtc = [DateTime]::UtcNow.ToString('o'); Resolver = $script:Template; Target = @($script:Targets)[0]; Protocol = 'RFC8484 HTTP/2 with default TLS validation'; Candidates = $candidates }
}
function Get-SystemAnswers([string]$Domain, [string]$Type = 'A') {
    $idx = [array]::IndexOf(@($script:Targets), $Domain); if ($idx -lt 0) { $idx = 0 }
    return @('179.254.166.' + (174 + $idx))
}
$script:HostsConflictHosts = @()
function Test-HostsConflict($Scope = $null) {
    $h = @(if ($Scope) { $Scope } else { $script:Targets })
    return (@($h | Where-Object { $_ -in @($script:HostsConflictHosts) }).Count -gt 0)
}
$script:FailNamespace = ''
$script:RealAddRule = ${function:Add-DnsClientNrptRule}
function Add-DnsClientNrptRule {
    param($Namespace, $NameServers, $DisplayName, $Comment, $NameEncoding, $DAProxyType, $DAIPsecEncryptionType, $DnsSecIPsecEncryptionType,
        $DAEnable, $DAIPsecRequired, $DnsSecEnable, $DnsSecValidationRequired, $DnsSecIPsecRequired, [switch]$PassThru, $ErrorAction)
    if ($script:FailNamespace -and $Namespace -eq $script:FailNamespace) { $script:FailNamespace = ''; throw 'SIMULATED_NAMESPACE_FAILURE' }   # fails once, so the compensation can re-apply
    & $script:RealAddRule @PSBoundParameters
}

$core = @($script:CoreHosts)
$gem = @($script:GeminiHosts)
$all4 = @($core) + @($gem)
$P1s = @('179.254.166.171', '164.5.224.81'); $P1t = 'https://dns.malw.link/dns-query'

function New-Bridge([string]$name, [string[]]$Scope) {
    Reset-MockEnvironment (Join-Path $ScratchPath $name)
    $script:Repair = $false; $script:GeminiDown = $false; $script:FailNamespace = ''
    $script:Servers = $P1s; $script:Template = $P1t; $script:ProfileName = 'dns.malw.link'
    $script:Targets = @($Scope)
    return (Enable-State $true $false)
}
function Invoke-Enable([string[]]$Scope) {
    $script:Targets = @($Scope)
    try { Enable-State $true $false | Out-Null; return 'NO_ERROR' } catch { return $_.Exception.Message }
}
function Get-RuleNamespaces { return @($script:MockRules | ForEach-Object { @($_.Namespace)[0] } | Sort-Object) }

# ==============================================================================
# G01-G03: the catalog and the two scopes
# ==============================================================================
$catalog = Get-Content -LiteralPath (Join-Path $sourceRoot 'profiles.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$gHosts = @($catalog.geminiTargets)
Assert-Condition ($gHosts.Count -eq 2 -and @($gHosts | Where-Object { $_ -match '\*' -or $_ -notmatch '^[a-z0-9.-]+\.[a-z]{2,}$' -or $_ -in @($catalog.targets) }).Count -eq 0 -and (Same-Set $gHosts $gem) -and (Same-Set $core @($catalog.targets))) 'T9_G01' 'The catalog lists exactly two Gemini hosts (no wildcard, none of the core hosts) and the engine reads the same sets' 'R-GEM'

$b = New-Bridge 't9_g02' $core
$st = Get-Status
$journal = Read-State
Assert-Condition ($b.State -eq 'DNS active' -and $script:MockRuleAddCount -eq 2 -and (Same-Set (Get-RuleNamespaces) $core) -and (Same-Set @($journal.Targets) $core) -and (Same-Set @($st.Targets) $core)) 'T9_G02' 'Gemini off: the bridge is exactly the old one (two rules for the core hosts, journal and status name only them)' 'R-GEM'

$b = New-Bridge 't9_g03' $all4
$journal = Read-State
$ruleOk = @($all4 | Where-Object { $t = $_; @($script:MockRules | Where-Object { (@($_.Namespace)[0] -eq $t) -and (Same-Set (Split-Servers $_.NameServers) $P1s) -and $_.Comment -eq ('AgDns:' + $journal.Owner) }).Count -eq 1 }).Count -eq 4
Assert-Condition ($b.State -eq 'DNS active' -and $script:MockRules.Count -eq 4 -and $ruleOk -and (Same-Set @($journal.Targets) $all4) -and @($journal.Rules).Count -eq 4 -and $script:MockDoh.Count -eq 2) 'T9_G03' 'Gemini on: one rule per host (all four) with the provider servers, one journal, DoH for the two servers' 'R-GEM'

# ==============================================================================
# G04: nothing but the catalog can name a host
# ==============================================================================
$prog = Get-Src 'Program.cs'
Assert-Condition ($prog -notmatch '"--targets"' -and $prog -notmatch '"--domains"' -and $prog -match 'TargetsOf\(Core,Gemini,Google\)' -and $prog -match '\(Gemini\?" --gemini":""\)') 'T9_G04a' 'Program.cs has no flag that takes hosts; the hosts come from TargetsFor(Gemini) and the elevated copy gets only the --gemini flag' 'R-GEM'
if ($haveExe) {
    $r1 = Invoke-Exe @('--enable', '--targets', 'evil.example')
    $r2 = Invoke-Exe @('--network', '--gemini', '--targets', 'evil.example')
    $r3 = Invoke-Exe @('--status', '--gemini-targets', 'evil.example')
    $h = Invoke-Exe @('--help')
    Assert-Condition ($r1.Exit -ne 0 -and ($r1.Err + $r1.Out) -match 'Unknown argument' -and $r2.Exit -ne 0 -and ($r2.Err + $r2.Out) -match 'Unknown argument|Unrecognized' -and $r3.Exit -ne 0 -and ($r3.Err + $r3.Out) -match 'Unknown argument' -and $h.Exit -eq 0 -and $h.Out -match '--gemini') 'T9_G04b' 'A host list on the command line is refused (exit != 0) in every form; --help documents --gemini' 'R-GEM'
}
# the engine passes --gemini on to the exe only for a scope that contains Gemini hosts
$script:Targets = $core; $off = @(Get-GeminiArgs $script:Targets)
$script:Targets = $all4; $on = @(Get-GeminiArgs $script:Targets)
Assert-Condition ($off.Count -eq 0 -and $on.Count -eq 1 -and $on[0] -eq '--gemini') 'T9_G04c' 'The engine hands --gemini to the exe (network probe, system resolver) only when the scope holds Gemini hosts' 'R-GEM'

# ==============================================================================
# G05: a provider without the Gemini hosts: the working Antigravity bridge is not touched
# ==============================================================================
$null = New-Bridge 't9_g05' $core
$before = $script:MockWriteCount; $ownerBefore = (Read-State).Owner
$script:GeminiDown = $true
$res = Invoke-Enable $all4
$j5 = $null; try { $j5 = Read-State } catch { $j5 = $null }
Assert-Condition ($res -like 'DOH_NO_VALIDATED_ENDPOINT*' -and $script:MockWriteCount -eq $before -and $j5 -and $j5.Phase -eq 'Active' -and $j5.Owner -eq $ownerBefore -and (Same-Set @($j5.Targets) $core) -and $script:MockRules.Count -eq 2) 'T9_G05a' 'A provider that fails the Gemini hosts: connecting with Gemini on is refused before any change; the Antigravity bridge stays exactly as it was' 'R-GEM'
$script:GeminiDown = $false; $script:Targets = $core
$st5 = Get-Status
Assert-Condition ($st5.State -eq 'DNS active' -and (Invoke-Enable $core) -eq 'NO_ERROR' -and $script:MockWriteCount -eq $before) 'T9_G05b' 'And the bridge without Gemini keeps working (status active, re-connecting is a no-op)' 'R-GEM'

# ==============================================================================
# G06: 2 -> 4 and back on a working bridge, without losing it
# ==============================================================================
$null = New-Bridge 't9_g06' $core
$ownerA = (Read-State).Owner
$res = Invoke-Enable $all4
$j6 = Read-State
$stUp = Get-Status
Assert-Condition ($res -eq 'NO_ERROR' -and $j6.Phase -eq 'Active' -and $j6.Owner -ne $ownerA -and (Same-Set @($j6.Targets) $all4) -and $script:MockRules.Count -eq 4 -and (Same-Set (Get-RuleNamespaces) $all4) -and $script:MockDoh.Count -eq 2 -and $stUp.State -eq 'DNS active' -and (Same-Set @($stUp.Targets) $all4)) 'T9_G06a' '2 -> 4 hosts on a working bridge: the bridge is replaced in one operation (four rules, no leftovers, status active for four hosts)' 'R-GEM'
$res = Invoke-Enable $core
$j6b = Read-State
$stDown = Get-Status
Assert-Condition ($res -eq 'NO_ERROR' -and $j6b.Phase -eq 'Active' -and (Same-Set @($j6b.Targets) $core) -and $script:MockRules.Count -eq 2 -and (Same-Set (Get-RuleNamespaces) $core) -and $script:MockDoh.Count -eq 2 -and $stDown.State -eq 'DNS active') 'T9_G06b' '4 -> 2 hosts on a working bridge: back to the two core rules, nothing of the Gemini hosts left behind' 'R-GEM'
# the status reads the bridge that exists, not the scope this run asks for
$null = New-Bridge 't9_g06c' $core
$script:Targets = $all4
$stAsk = Get-Status
Assert-Condition ($stAsk.State -eq 'DNS active' -and $stAsk.Code -eq 'DNS_CONFIGURED_APPLICATION_NOT_TESTED' -and (Same-Set @($stAsk.Targets) $core)) 'T9_G06c' 'A status asked with the Gemini scope while the bridge has the core scope reports the bridge (active, its own two hosts), not a scope error' 'R-GEM'

# ==============================================================================
# G07: the change cannot be applied: the previous scope comes back
# ==============================================================================
$null = New-Bridge 't9_g07' $core
$script:FailNamespace = $gem[1]
$res = Invoke-Enable $all4
$j7 = Read-State
Assert-Condition ($res -like 'PROFILE_SWITCH_FAILED_PREVIOUS_RESTORED*' -and $j7.Phase -eq 'Active' -and (Same-Set @($j7.Targets) $core) -and $script:MockRules.Count -eq 2 -and (Same-Set (Get-RuleNamespaces) $core) -and $script:MockDoh.Count -eq 2 -and (Get-Status).State -eq 'DNS active') 'T9_G07a' '2 -> 4 fails on the last Gemini rule: the compensated switch puts the old two-host bridge back (active, two rules, no partial Gemini rule)' 'R-GEM'
$script:FailNamespace = ''
$null = New-Bridge 't9_g07b' $all4
$script:FailNamespace = $core[1]
$res = Invoke-Enable $core
$j7b = Read-State
Assert-Condition ($res -like 'PROFILE_SWITCH_FAILED_PREVIOUS_RESTORED*' -and $j7b.Phase -eq 'Active' -and (Same-Set @($j7b.Targets) $all4) -and $script:MockRules.Count -eq 4) 'T9_G07b' '4 -> 2 fails: the four-host bridge comes back as it was' 'R-GEM'
$script:FailNamespace = ''

# ==============================================================================
# G08-G09: journals of the other scope and of an old format
# ==============================================================================
$null = New-Bridge 't9_g08' $core
$script:Targets = $all4
$readOk = $true; try { $j8 = Read-State } catch { $readOk = $false }
$statusOk = $true; try { $s8 = Get-Status } catch { $statusOk = $false }
$restoreOk = $true; try { Restore-State (Read-State) | Out-Null } catch { $restoreOk = $false }
Assert-Condition ($readOk -and $statusOk -and $s8.State -eq 'DNS active' -and $restoreOk -and (Read-State).Phase -eq 'Restored' -and $script:MockRules.Count -eq 0 -and $script:MockDoh.Count -eq 0) 'T9_G08a' 'A journal written for the core hosts (every earlier release) is read, reported and restored while the Gemini scope is selected' 'R-GEM'
$null = New-Bridge 't9_g08b' $all4
$script:Targets = $core
$readOk = $true; try { $j8 = Read-State } catch { $readOk = $false }
$restoreOk = $true; try { Restore-State (Read-State) | Out-Null } catch { $restoreOk = $false }
Assert-Condition ($readOk -and $restoreOk -and (Read-State).Phase -eq 'Restored' -and $script:MockRules.Count -eq 0 -and $script:MockDoh.Count -eq 0) 'T9_G08b' 'A journal written with the Gemini hosts is read and restored while the core scope is selected' 'R-GEM'

# a journal in the 1.3.0 form (no new fields) keeps working
$null = New-Bridge 't9_g08c' $core
$raw = Get-Content -LiteralPath $script:StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
$old = [pscustomobject]@{ Schema = 1; Owner = $raw.Owner; Phase = $raw.Phase; Protocol = $raw.Protocol; Targets = $raw.Targets; Servers = $raw.Servers; Rules = $raw.Rules; Doh = $raw.Doh; UpdatedUtc = $raw.UpdatedUtc }
[IO.File]::WriteAllText($script:StatePath, ($old | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
$script:Targets = $all4
$ok130 = $true; try { $s130 = Get-Status; Restore-State (Read-State) | Out-Null } catch { $ok130 = $false }
Assert-Condition ($ok130 -and $s130.State -eq 'DNS active' -and (Read-State).Phase -eq 'Restored' -and $script:MockRules.Count -eq 0) 'T9_G08c' 'A journal without Template/ProfileName (1.3.0 form) is still read and restored under the Gemini scope' 'R-GEM'

# a scope that is neither of the two catalog scopes stays invalid; the old single-domain journal keeps its own refusal
$null = New-Bridge 't9_g09' $core
$raw = Get-Content -LiteralPath $script:StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
$foreign = @($core) + @('evil.example')
$raw.Targets = $foreign
$raw.Rules = @(@($raw.Rules) + [pscustomobject]@{ Namespace = 'evil.example'; Name = $null; Fingerprint = $null })
[IO.File]::WriteAllText($script:StatePath, ($raw | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
$script:Targets = $core
$msg = ''; $readOk = $null; try { $readOk = Read-State } catch { $msg = $_.Exception.Message }
$stF = Get-Status
$msgE = ''; try { Enable-State $true $false | Out-Null } catch { $msgE = $_.Exception.Message }
Assert-Condition ($msg -eq '' -and $readOk -and $stF.State -ne 'DNS active' -and $stF.Code -in @('RECOVERY_REQUIRED','RESTORE_BEFORE_SCOPE_UPGRADE') -and $msgE -eq 'RESTORE_BEFORE_SCOPE_UPGRADE') 'T9_G09a' 'A consistent journal of a scope this catalog does not know (a host it no longer lists) is still readable: it is not called an active bridge (restorable), connecting is refused (restore first), and nothing says STATE_INVALID' 'R-GEM' -FailureMessage ("read='$msg' state=$($stF.State)/$($stF.Code) enable='$msgE'")
$null = Restore-State (Read-State)
Assert-Condition ((Read-State).Phase -eq 'Restored' -and $script:MockRules.Count -eq 0 -and $script:MockDoh.Count -eq 0) 'T9_G09c' 'That journal can be restored: only the program''s own entries are removed and the journal ends Restored (a catalog change must never strand a bridge)' 'R-GEM'
# AUDIT: the state directory is created with its protected rules in one call (checked on a scratch directory with the same helper the engine uses)
$pd = Join-Path $ScratchPath 'protected_dir_probe'
try {
    New-ProtectedDirectory $pd
    $pAcl = Get-Acl -LiteralPath $pd
    $pRules = @($pAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    Assert-Condition ($pAcl.AreAccessRulesProtected -and $pRules.Count -eq 3 -and @($pRules | Where-Object { $_.IsInherited }).Count -eq 0 -and (($pRules | ForEach-Object { $_.IdentityReference.Value } | Sort-Object) -join ',') -eq 'S-1-5-18,S-1-5-32-544,S-1-5-32-545') 'T9_P01' 'The engine creates its state directory with exactly the protected rules (SYSTEM, Administrators, Users read) and nothing inherited' 'R-AUDIT'
} finally {
    try { $cur = [Security.Principal.WindowsIdentity]::GetCurrent().User; $a2 = Get-Acl -LiteralPath $pd; $a2.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($cur, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))); Set-Acl -LiteralPath $pd -AclObject $a2; Remove-Item -LiteralPath $pd -Recurse -Force } catch {}
}

# AUDIT: one mutation at a time is a file lock in the (protected) state directory: held by another run -> OPERATION_IN_PROGRESS; free -> the operation goes on; released afterwards
$null = New-Bridge 't9_lock' $core
$lockFs = [IO.File]::Open((Join-Path $script:Root 'operation.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$script:Action = 'restore'; $busyMsg = ''
try { try { Invoke-Engine | Out-Null } catch { $busyMsg = $_.Exception.Message } } finally { $lockFs.Dispose() }
$freeMsg = ''; try { Invoke-Engine | Out-Null } catch { $freeMsg = $_.Exception.Message }
$againFs = $null; try { $againFs = [IO.File]::Open((Join-Path $script:Root 'operation.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) } catch {} finally { if ($againFs) { $againFs.Dispose() } }
Assert-Condition ($busyMsg -like 'OPERATION_IN_PROGRESS*' -and $freeMsg -eq '' -and $againFs) 'T9_L01' 'A second mutation while another holds the lock is refused with OPERATION_IN_PROGRESS; with the lock free it runs, and it lets go of the lock afterwards' 'R-AUDIT' -FailureMessage ("busy='$busyMsg' free='$freeMsg' released=$([bool]$againFs)")
$script:Action = 'status'

$null = New-Bridge 't9_g09b' @($core[0])
$script:Targets = $core
$st9 = Get-Status
$msg2 = ''; try { Enable-State $true $false | Out-Null } catch { $msg2 = $_.Exception.Message }
Assert-Condition ($st9.Code -eq 'RESTORE_BEFORE_SCOPE_UPGRADE' -and $msg2 -eq 'RESTORE_BEFORE_SCOPE_UPGRADE') 'T9_G09b' 'The old single-domain journal keeps its refusal (RESTORE_BEFORE_SCOPE_UPGRADE): restore first, then connect' 'R-GEM'

# ==============================================================================
# G04d: the exe is asked about the same scope as the engine works on
# ==============================================================================
$stub = Join-Path $ScratchPath 'stub_exe.cmd'
[IO.Directory]::CreateDirectory($ScratchPath) | Out-Null
Set-Content -LiteralPath $stub -Encoding ASCII -Value @('@echo off', 'echo {"Candidates":[],"Addresses":["%*"],"Args":"%*"}')
$realExe = $script:Exe
$script:Exe = $stub; $script:Servers = $P1s; $script:Template = $P1t
$script:Targets = $core; $n0 = & $script:RealGetNetwork; $a0 = @(& $script:RealGetSystemAnswers $gem[0])
$script:Targets = $all4; $n1 = & $script:RealGetNetwork; $a1 = @(& $script:RealGetSystemAnswers $core[0])
$a2 = @(& $script:RealGetSystemAnswers $gem[0])
$script:Exe = $realExe
Assert-Condition ($n0.Args -match '--network' -and $n0.Args -notmatch '--gemini' -and $n1.Args -match '--network' -and $n1.Args -match '--gemini' -and $a0[0] -match '--gemini' -and $a1[0] -notmatch '--gemini' -and $a2[0] -match '--gemini') 'T9_G04d' 'Get-Network passes --gemini to the exe only for a scope with Gemini hosts; the system-resolver query passes it only for a Gemini host' 'R-GEM'

# ==============================================================================
# G11: conflicts are looked for over the hosts of the bridge that exists
# ==============================================================================
$null = New-Bridge 't9_g11' $core
$script:HostsConflictHosts = @($gem[0])
$script:Targets = $all4
$st11 = Get-Status
Assert-Condition ($st11.State -eq 'DNS active' -and (Same-Set @($st11.Targets) $core)) 'T9_G11a' 'A hosts entry for a Gemini host does not turn a working core bridge into a conflict just because the Gemini scope is selected' 'R-GEM'
$before = $script:MockWriteCount
$res = Invoke-Enable $all4
$j11 = $null; try { $j11 = Read-State } catch { $j11 = $null }
Assert-Condition ($res -like 'NRPT_CONFLICT*' -and $script:MockWriteCount -eq $before -and $j11 -and $j11.Phase -eq 'Active' -and (Same-Set @($j11.Targets) $core)) 'T9_G11b' 'The same entry refuses the change to four hosts before anything is touched (the working bridge stays)' 'R-GEM'
$script:HostsConflictHosts = @()
$null = New-Bridge 't9_g11c' $all4
$script:HostsConflictHosts = @($gem[0])
$script:Targets = $core
$st11c = Get-Status
$script:HostsConflictHosts = @()
Assert-Condition ($st11c.State -eq 'Conflict' -and $st11c.Code -eq 'NRPT_OR_HOSTS_CONFLICT') 'T9_G11c' 'A hosts entry for a Gemini host is still found when a four-host bridge is asked about with the core scope' 'R-GEM'

$null = New-Bridge 't9_g11d' $all4
$script:HostsConflictHosts = @($gem[0])
$before = $script:MockWriteCount
$res = Invoke-Enable $core
$script:HostsConflictHosts = @()
$j11d = $null; try { $j11d = Read-State } catch { $j11d = $null }
Assert-Condition ($res -like 'NRPT_CONFLICT*' -and $script:MockWriteCount -eq $before -and $j11d -and $j11d.Phase -eq 'Active' -and (Same-Set @($j11d.Targets) $all4)) 'T9_G11d' 'A conflict on a host of the bridge that is about to be replaced also refuses the change before anything is touched' 'R-GEM'

# ==============================================================================
# G12: window contract (source): after a reconnect the toggle follows the journal; a failure is worded for the scope that was asked for
# ==============================================================================
$mf = Get-Src 'MainForm.cs'
$assistedMain = Get-Content -LiteralPath (Join-Path $sourceRoot "MainForm.Assisted.cs") -Raw -Encoding UTF8
Assert-Condition ($mf -match 'await Mutate\("enable"\);\}\s*finally\{scopeApplying=false;\}\s*if\(!SyncGeminiFromJournal\(\)' -and $assistedMain.Contains('var p=Current;bool geminiAtStart=Program.Gemini;') -and $assistedMain.Contains('ShowOperationFailure(unconfirmed,geminiAtStart)') -and $assistedMain.Contains('ShowFailure(failure,null,geminiAtStart)') -and $mf -match 'if\(busy\|\|benchRunning\)\{SetScope\(ScopeNow\);return;\}') 'T9_G12' 'MainForm: the scope selector (since 2026-10-13; was the Gemini toggle) is refused during a measurement, follows the journal after a refused reconnect, and failures are worded for the scope that was asked for' 'R-GEM'

# ==============================================================================
# G10: the exe's --gemini switch really widens the host set (and only it)
# ==============================================================================
if ($haveExe) {
    $pl0 = Invoke-Exe @('--profiles-list'); $pl1 = Invoke-Exe @('--gemini', '--profiles-list'); $pl2 = Invoke-Exe @('--profiles-list', '--GEMINI')
    $j0 = $null; $j1 = $null; $j2 = $null
    try { $j0 = $pl0.Out | ConvertFrom-Json; $j1 = $pl1.Out | ConvertFrom-Json; $j2 = $pl2.Out | ConvertFrom-Json } catch {}
    Assert-Condition ($pl0.Exit -eq 0 -and $pl1.Exit -eq 0 -and $pl2.Exit -eq 0 -and $j0 -and $j1 -and $j2 -and (Same-Set @($j0.Targets) $core) -and (Same-Set @($j1.Targets) $all4) -and (Same-Set @($j2.Targets) $all4) -and (Same-Set @($j0.GeminiTargets) $gem)) 'T9_G10' 'The exe: without --gemini the host set is the core one, with --gemini (any letter case) it is core plus the catalog Gemini hosts' 'R-GEM'
}
