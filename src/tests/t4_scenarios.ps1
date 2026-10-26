# t4_scenarios.ps1 - Tier 4: Real-World Application Scenarios Test Suite
# Realistic end-to-end multi-step application workflows (8 scenarios)
# Strictly offline: zero mutations to host network or system state.

param(
    [string]$ScratchPath,
    [switch]$EscalateKnownDefects
)

$ErrorActionPreference = 'Stop'
$global:CurrentTier = "Tier 4: Real-World Application Scenarios"
$script:CurrentTier = $global:CurrentTier

$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\app')).Path
$portableRoot = (Resolve-Path (Join-Path $PSScriptRoot 'fixtures\baseline-1.1.0')).Path
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

. (Join-Path $sourceRoot 'Engine.ps1') -LibraryOnly
$script:RunPath='HKCU:\Software\AgDnsTest\Run'  # isolated: tests never touch the user's real Run key
. (Join-Path $PSScriptRoot 'helpers\TestHarness.ps1')
. (Join-Path $PSScriptRoot 'helpers\SyntheticWire.ps1')
. (Join-Path $PSScriptRoot 'helpers\MockCmdlets.ps1')

Write-Host "--- Executing Tier 4: Real-World Application Scenarios Tests ---" -ForegroundColor Cyan

# ==============================================================================
# Scenario S01: Full Cold Start to Active Bridge (P1: dns.malw.link)
# ==============================================================================
Reset-MockEnvironment (Join-Path $ScratchPath 't4_s01')
# Step 1: Confirm unconfigured initial state
$initStatus = Get-Status
$s01Step1 = ($initStatus.State -eq 'Not configured' -and $script:MockRules.Count -eq 0)

# Step 2: Trigger activation with P1 profile
$script:Servers = @('179.254.166.171', '164.5.224.81')
$script:Template = 'https://dns.malw.link/dns-query'
$script:ProfileName = 'dns.malw.link'
$actResult = Enable-State $true $false

# Step 3: Verify state and rule invariants
$s01State = Read-State
$s01Step2 = ($s01State.Phase -eq 'Active' -and 
             $s01State.Rules.Count -eq 2 -and 
             $script:MockRules.Count -eq 2 -and 
             $script:MockDoh.Count -eq 2 -and
             $actResult.State -eq 'DNS active')
Assert-Condition ($s01Step1 -and $s01Step2) 'T4_S01' 'Scenario 1: Cold start activation transitions atomically to Active' 'P1+Lifecycle'

# ==============================================================================
# Scenario S02: Hot Provider Migration (P1 to P2)
# ==============================================================================
Reset-MockEnvironment (Join-Path $ScratchPath 't4_s02')
# 1. Start with P1 Active
$script:Servers = @('179.254.166.171', '164.5.224.81')
$script:Template = 'https://dns.malw.link/dns-query'
$script:ProfileName = 'dns.malw.link'
Enable-State $true $false | Out-Null
$p1Owner = (Read-State).Owner

# 2. Trigger migration to P2 (xbox-dns.ru)
$script:Servers = @('185.185.185.185', '185.185.185.186')
$script:Template = 'https://xbox-dns.ru/dns-query'
$script:ProfileName = 'xbox-dns.ru'
$p2Result = Enable-State $true $false

# 3. Verify clean handover: old P1 rules gone, new P2 rules active
$s02State = Read-State
$p2Owner = $s02State.Owner
$s02Clean = ($s02State.Phase -eq 'Active' -and 
             $p1Owner -ne $p2Owner -and 
             $s02State.Template -eq 'https://xbox-dns.ru/dns-query' -and 
             $script:MockRules.Count -eq 2 -and
             $script:MockDoh.Count -eq 2)
Assert-Condition $s02Clean 'T4_S02' 'Scenario 2: Hot provider migration cleanly replaces rules and journal' 'P1+P2+Lifecycle'

# ==============================================================================
# Scenario S03: Partial Outage Resilience (1 of 2 Servers Responding)
# ==============================================================================
Reset-MockEnvironment (Join-Path $ScratchPath 't4_s03')
# 1. Initial activation with 1 of 2 responsive servers
$script:Servers = @('179.254.166.171', '164.5.224.81')
$script:Template = 'https://dns.malw.link/dns-query'
$script:MockPartialNetworkOnlyOneServer = $true
Enable-State $true $false | Out-Null
$s03State1 = Read-State
$s03SingleActive = ($s03State1.Phase -eq 'Active' -and $s03State1.Servers.Count -eq 1)

# 2. Re-enable with same profile under continuing partial outage
# Desired behavior: bridge MUST NOT be torn down or restored prematurely
$s03Preserved = $false
try {
    Enable-State $true $false | Out-Null
    $s03State2 = Read-State
    $s03Preserved = ($s03State2.Phase -eq 'Active' -and $s03State2.Servers.Count -eq 1 -and $script:MockRules.Count -eq 2)
} catch {}
Assert-Condition $s03Preserved 'T4_S03' 'Scenario 3: Active bridge with 1 healthy server survives re-enable without teardown' 'F04+P1'

# ==============================================================================
# Scenario S04: Total Outage Shield (Active Bridge Retained)
# ==============================================================================
Reset-MockEnvironment (Join-Path $ScratchPath 't4_s04')
# 1. Establish active bridge
$script:Servers = @('179.254.166.171', '164.5.224.81')
$script:Template = 'https://dns.malw.link/dns-query'
Enable-State $true $false | Out-Null
$rulesBeforeOutage = $script:MockRules.Count

# 2. External total outage occurs (all candidate probes fail)
$script:MockNetworkFailure = $true
try {
    # Attempting to re-enable during outage
    Enable-State $true $false | Out-Null
} catch {}

# Active rules MUST be preserved
$rulesAfterOutage = $script:MockRules.Count
Assert-EqualValues $rulesAfterOutage $rulesBeforeOutage 'T4_S04' 'Scenario 4: Total external outage shields active bridge from removal' 'F04'

# ==============================================================================
# Scenario S05: Aborted Provider Migration with Rollback (P1 to broken P3)
# ==============================================================================
Reset-MockEnvironment (Join-Path $ScratchPath 't4_s05')
# 1. Start with active P1
$script:Servers = @('179.254.166.171', '164.5.224.81')
$script:Template = 'https://dns.malw.link/dns-query'
$script:ProfileName = 'dns.malw.link'
Enable-State $true $false | Out-Null
$p1RulesCount = $script:MockRules.Count

# 2. Attempt migration to P3 with non-functional network
$script:Servers = @('92.223.109.31', '92.38.150.150')
$script:Template = 'https://dns.comss.one/dns-query'
$script:ProfileName = 'dns.comss.one'
$script:MockNetworkFailure = $true
try {
    Enable-State $true $false | Out-Null
} catch {}

# Desired invariant: P1 rules MUST be retained when P3 migration preflight fails
# Failed preflight must preserve the active profile.
$p1Intact = ($script:MockRules.Count -eq $p1RulesCount -and (Read-State).Phase -eq 'Active')
Assert-Condition $p1Intact 'T4_S05' 'Scenario 5: Failed migration to P3 preserves active P1 bridge' 'F04+P1+P3' -DefectId 'F04' -FailureMessage 'Engine.ps1 deletes active P1 rules before validating P3 candidate'

# ==============================================================================
# Scenario S06: Disaster Recovery from Crashed Mutation
# ==============================================================================
Reset-MockEnvironment (Join-Path $ScratchPath 't4_s06')
$script:Servers = @('179.254.166.171', '164.5.224.81')
$script:Template = 'https://dns.malw.link/dns-query'
# 1. Simulate uncommitted state from interrupted mutation
Initialize-StateDirectory
$crashState = [PSCustomObject]@{
    Schema = 1
    Owner = [Guid]::NewGuid().ToString('N')
    Phase = 'Applying'
    ProfileName = 'crash-test'
    Template = $script:Template
    Protocol = 'DoH'
    Targets = $script:Targets
    Servers = $script:Servers
    Rules = @(
        [PSCustomObject]@{ Namespace = $script:Targets[0]; Name = $null; Fingerprint = $null },
        [PSCustomObject]@{ Namespace = $script:Targets[1]; Name = $null; Fingerprint = $null }
    )
    Doh = @(
        [PSCustomObject]@{ Server = $script:Servers[0]; Template = $script:Template; Created = $true; Ownership = 'Planned' },
        [PSCustomObject]@{ Server = $script:Servers[1]; Template = $script:Template; Created = $true; Ownership = 'Planned' }
    )
    UpdatedUtc = ''
}
Save-State $crashState

# 2. Invoke Restore to recover system
Restore-State (Read-State) | Out-Null
$recoveredState = Read-State
$s06Clean = ($recoveredState.Phase -eq 'Restored' -and $script:MockRules.Count -eq 0)
Assert-Condition $s06Clean 'T4_S06' 'Scenario 6: Disaster recovery cleans up Applying state to Restored' 'Lifecycle'

# ==============================================================================
# Scenario S07: Clean Staging Packaging Verification
# ==============================================================================
# Verify packaging prerequisites and staging separation
$buildScript = Join-Path $sourceRoot 'Build.ps1'
$buildRaw = Get-Content $buildScript -Raw
$hasWorkPortableDefault = ($buildRaw -match '\.work\\build')
$hasCsprojTarget = ($buildRaw -match 'AntigravityGeminiDns\.csproj')
$portableDirExists = Test-Path $portableRoot
$s07Valid = ($hasWorkPortableDefault -and $hasCsprojTarget -and $portableDirExists)
Assert-Condition $s07Valid 'T4_S07' 'Scenario 7: Packaging contracts isolate build output to src\.work\build' 'F01+Packaging'

# ==============================================================================
# Scenario S08: Emergency Clean Teardown Workflow
# ==============================================================================
Reset-MockEnvironment (Join-Path $ScratchPath 't4_s08')
# 1. Inject foreign rule
$foreignRuleId = '{00000000-0000-0000-0000-000000000088}'
$script:MockRules.Add([PSCustomObject]@{
    Name = $foreignRuleId
    Namespace = @('foreign.example')
    NameServers = '1.1.1.1'
    DisplayName = 'Foreign Rule'
    Comment = 'Foreign Rule'
})

# 2. Activate owned bridge
$script:Servers = @('179.254.166.171', '164.5.224.81')
$script:Template = 'https://dns.malw.link/dns-query'
Enable-State $true $false | Out-Null
$rulesDuringActive = $script:MockRules.Count # Should be 3 (1 foreign + 2 owned)

# 3. Perform Emergency Restore
$restoreResult = Restore-State (Read-State)

# 4. Verify owned rules removed, foreign rule preserved, journal Restored
$rulesAfterRestore = $script:MockRules.Count
$foreignSurvives = ($rulesAfterRestore -eq 1 -and $script:MockRules[0].Name -eq $foreignRuleId)
$journalRestored = ((Read-State).Phase -eq 'Restored')
$s08Success = ($rulesDuringActive -eq 3 -and $foreignSurvives -and $journalRestored)
Assert-Condition $s08Success 'T4_S08' 'Scenario 8: Complete emergency teardown removes owned rules and preserves foreign rules' 'Lifecycle+Safety'

# ==============================================================================
# Scenario S09: End-to-End Logon Autostart Lifecycle
# ==============================================================================
# 1. Enable autostart
Set-Autostart -Enable $true | Out-Null
$asActive = Get-AutostartStatus
# 2. Simulate logon: non-elevated status read
$logonStatus = Get-Status
# 3. Simulate user manual connect
$script:Servers = @('179.254.166.171', '164.5.224.81')
$script:Template = 'https://dns.malw.link/dns-query'
Enable-State $true $false | Out-Null
# 4. User manual disconnect
Restore-State (Read-State) | Out-Null
# 5. Disable autostart
Set-Autostart -Enable $false | Out-Null
$asClean = Get-AutostartStatus
$s09Pass = ($asActive.Enabled -and $logonStatus.State -eq 'Not configured' -and (Read-State).Phase -eq 'Restored' -and -not $asClean.Enabled)
Assert-Condition $s09Pass 'T4_S09' 'Scenario 9: Full autostart enable, logon read, manual lifecycle and cleanup pass' 'Autostart+Lifecycle'

# Invariant safety check
Assert-HostSafetyBaselineUnchanged "Tier 4 Completion"
Write-Host "--- Tier 4 Completed: $($global:TestResults.Count) Tests Run ---" -ForegroundColor Cyan
