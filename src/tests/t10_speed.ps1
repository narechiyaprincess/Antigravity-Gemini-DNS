# t10_speed.ps1 - Tier 10: speed of the connect / disconnect / diagnose paths, offline only.
# One exe start answers every Windows-resolver question of a check (batch), a restore brings its own status, and the window's waits overlap.
# The engine runs on mock cmdlets; the "exe" is a stub script, so no DNS query leaves the machine and nothing touches NRPT/DoH/adapters/state.json.
# This file is saved with a BOM: Windows PowerShell 5.1 mangles non-ASCII text in a script without one.

param(
    [string]$ScratchPath,
    [switch]$EscalateKnownDefects
)

$ErrorActionPreference = 'Stop'
$global:CurrentTier = "Tier 10: speed paths"
$script:CurrentTier = $global:CurrentTier

$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\app')).Path
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

. (Join-Path $sourceRoot 'Engine.ps1') -LibraryOnly
$script:RealGetSystemAnswers = ${function:Get-SystemAnswers}
$script:RunPath = 'HKCU:\Software\AgDnsTest\Run'
. (Join-Path $PSScriptRoot 'helpers\TestHarness.ps1')
. (Join-Path $PSScriptRoot 'helpers\SyntheticWire.ps1')
. (Join-Path $PSScriptRoot 'helpers\MockCmdlets.ps1')

Write-Host "--- Executing Tier 10: speed path Tests ---" -ForegroundColor Cyan

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
    $psi.Arguments = (@($Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' ')
    $p = [Diagnostics.Process]::Start($psi)
    $so = $p.StandardOutput.ReadToEndAsync(); $se = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { try { $p.Kill() } catch {}; return [pscustomobject]@{ Exit = -1; Out = 'TIMEOUT'; Err = '' } }
    $p.WaitForExit()
    [pscustomobject]@{ Exit = $p.ExitCode; Out = $so.Result; Err = $se.Result }
}
function Get-Src([string]$Name) { Get-Content -LiteralPath (Join-Path $sourceRoot $Name) -Raw -Encoding UTF8 }

# A stub "exe": logs every call and answers --system-dns-batch / --system-dns with host-specific addresses. No network.
[IO.Directory]::CreateDirectory($ScratchPath) | Out-Null
$stub = Join-Path $ScratchPath 'stub_exe.ps1'
$stubLog = Join-Path $ScratchPath 'stub_calls.log'
Set-Content -LiteralPath $stub -Encoding UTF8 -Value @'
Add-Content -LiteralPath $env:AGDNS_STUB_LOG -Value ($args -join ' ')
if ($args[0] -eq '--system-dns-batch') {
    if ($env:AGDNS_STUB_MODE -eq 'nobatch') { exit 1 }
    $hosts = @($args[1] -split ','); $types = @($args[2] -split ',')
    $out = @(foreach ($h in $hosts) { foreach ($t in $types) { [pscustomobject]@{ Host = $h; Type = $(if ($t -eq 'AAAA') { 28 } else { 1 }); Code = 'DNS_OK'; Addresses = @('10.0.' + ([array]::IndexOf($hosts, $h) + 1) + '.' + $(if ($t -eq 'AAAA') { 6 } else { 4 })) } } })
    ConvertTo-Json -InputObject $out -Depth 4
    exit 0
}
if ($args[0] -eq '--system-dns') { [pscustomobject]@{ Host = $args[1]; Type = 1; Code = 'DNS_OK'; Addresses = @('10.9.9.9') } | ConvertTo-Json; exit 0 }
exit 2
'@
$env:AGDNS_STUB_LOG = $stubLog
function Get-StubCalls { if (Test-Path -LiteralPath $stubLog) { @(Get-Content -LiteralPath $stubLog) } else { @() } }
function Clear-StubCalls { if (Test-Path -LiteralPath $stubLog) { Remove-Item -LiteralPath $stubLog -Force } }

$core = @($script:CoreHosts)
$realExe = $script:Exe
$script:Exe = $stub
$script:Targets = $core

# ==============================================================================
# S01-S04: one exe start answers the whole check; entries are used once; old exe: single queries
# ==============================================================================
$env:AGDNS_STUB_MODE = ''
Clear-StubCalls; Reset-SystemAnswers
$a1 = @(& $script:RealGetSystemAnswers $core[0]); $a2 = @(& $script:RealGetSystemAnswers $core[1])
$calls = @(Get-StubCalls)
Assert-Condition ($calls.Count -eq 1 -and $calls[0] -like '--system-dns-batch *' -and $calls[0] -match [regex]::Escape($core[0]) -and $calls[0] -match [regex]::Escape($core[1]) -and $a1.Count -eq 1 -and $a1[0] -eq '10.0.1.4' -and $a2.Count -eq 1 -and $a2[0] -eq '10.0.2.4') 'T10_S01' 'The answers of the Windows resolver for every host of a check come from one exe start, each to its own host' 'R-SPEED' -FailureMessage ($calls -join ' || ')

$a3 = @(& $script:RealGetSystemAnswers $core[0])
$calls = @(Get-StubCalls)
Assert-Condition ($calls.Count -eq 2 -and $a3[0] -eq '10.0.1.4') 'T10_S02' 'A cached answer is used once: asking again asks Windows again (a later re-check never sees an old answer)' 'R-SPEED'

Clear-StubCalls; Reset-SystemAnswers @('A', 'AAAA') @('www.microsoft.com')
$d1 = @(& $script:RealGetSystemAnswers $core[0] 'A'); $d2 = @(& $script:RealGetSystemAnswers $core[0] 'AAAA'); $d3 = @(& $script:RealGetSystemAnswers $core[1] 'AAAA'); $d4 = @(& $script:RealGetSystemAnswers 'www.microsoft.com' 'A')
$calls = @(Get-StubCalls)
Assert-Condition ($calls.Count -eq 1 -and $calls[0] -match 'www\.microsoft\.com' -and $calls[0] -match 'A,AAAA' -and $d2[0] -eq '10.0.1.6' -and $d3[0] -eq '10.0.2.6' -and $d4[0] -eq '10.0.3.4' -and $d1[0] -eq '10.0.1.4') 'T10_S03' 'Diagnose: A and AAAA for every host plus the control host in a single exe start' 'R-SPEED' -FailureMessage ($calls -join ' || ')

$env:AGDNS_STUB_MODE = 'nobatch'
Clear-StubCalls; Reset-SystemAnswers
$f1 = @(& $script:RealGetSystemAnswers $core[0])
$calls = @(Get-StubCalls)
Assert-Condition ($calls.Count -eq 2 -and $calls[0] -like '--system-dns-batch *' -and $calls[1] -like '--system-dns *' -and $f1[0] -eq '10.9.9.9') 'T10_S04' 'An exe that cannot answer the batch still gets the single query (nothing is lost, only speed)' 'R-SPEED'
$env:AGDNS_STUB_MODE = ''
$script:Exe = $realExe

# the engine resets the cache right before each check
$eng = Get-Src 'Engine.ps1'
Assert-Condition ($eng -match 'Reset-SystemAnswers\s*\n\s*foreach\(\$t in (\(Get-Hosts )?\$s\.Targets\)?\)\{\$answer=@\(Get-SystemAnswers' -and $eng -match "Reset-SystemAnswers @\('A','AAAA'\) @\('www\.microsoft\.com'\)") 'T10_S05' 'Apply-State resets the answer cache right before its resolver check; Diagnose asks for A, AAAA and the control host in one go' 'R-SPEED'

# ==============================================================================
# S06: a restore brings its own status
# ==============================================================================
$P1s = @('179.254.166.171', '164.5.224.81'); $P1t = 'https://dns.malw.link/dns-query'
Reset-MockEnvironment (Join-Path $ScratchPath 't10_s06')
$script:Repair = $false; $script:Servers = $P1s; $script:Template = $P1t; $script:ProfileName = 'dns.malw.link'; $script:Targets = $core
$null = Enable-State $true $false
$o = Invoke-Restore
$hasStatus = [bool]$o.PSObject.Properties['Status']
Assert-Condition ($o.Code -eq 'ROLLED_BACK' -and $hasStatus -and $o.Status.State -eq 'Not configured' -and $o.Status.JournalPhase -eq 'Restored' -and $script:MockRules.Count -eq 0) 'T10_S06' 'A restore answers with the status after it (Status: not configured, journal Restored), so the window needs no second engine start' 'R-SPEED'
$engSwitch = $eng -match "'restore'\{Invoke-Restore\}"
Assert-Condition $engSwitch 'T10_S06b' 'The engine action restore goes through Invoke-Restore' 'R-SPEED'

# ==============================================================================
# S07: the batch keeps the host limits of --system-dns (no DNS query is made for a refused call)
# ==============================================================================
if ($haveExe) {
    $b1 = Invoke-Exe @('--system-dns-batch', 'evil.example', 'A')
    $b2 = Invoke-Exe @('--system-dns-batch', $core[0], 'MX')
    $b3 = Invoke-Exe @('--system-dns-batch', $core[0])
    Assert-Condition ($b1.Exit -ne 0 -and $b2.Exit -ne 0 -and $b3.Exit -ne 0) 'T10_S07' 'The batch refuses a host outside the bridge set, an unknown record type and a missing argument (exit != 0)' 'R-SPEED'
}

# ==============================================================================
# S08: native preparation is revision-bound; the UI renders only current verified snapshots.
# ==============================================================================
$assistedMain = Get-Src 'MainForm.Assisted.cs'
$assistedNative = Get-Src 'AssistedNative.cs'
$coordinator = Get-Src 'AssistedCoordinator.cs'
$captureMatch=[regex]::Match($coordinator,'var before=(?:recoveryAdmission\?\?)?await backend\.Capture\(')
$iCapture=if($captureMatch.Success){$captureMatch.Index}else{-1}
$iPrepare=$coordinator.IndexOf('prepared=await backend.Prepare(')
$iTrust=$coordinator.IndexOf('!await trust(prepared,token)')
$iReady=$coordinator.IndexOf('var ready=await backend.Capture(')
$iApply=$coordinator.IndexOf('started=true;var applied=await backend.Apply(')
Assert-Condition ($iCapture -ge 0 -and $iPrepare -gt $iCapture -and $iTrust -gt $iPrepare -and $iReady -gt $iTrust -and $iApply -gt $iReady -and $assistedNative.Contains('AddressResolver.ResolveAsync(') -and $assistedNative.Contains('await task.WaitAsync(budget.Token)')) 'T10_S08a' 'Connect binds preparation and explicit trust to a fresh native revision before UAC; address resolution is bounded' 'R-SPEED'
Assert-Condition ($assistedMain.Contains('bool verified=outcome.Outcome==AssistedOutcome.NativeVerified||outcome.Outcome==AssistedOutcome.DisabledVerified;') -and $assistedMain.Contains('if(verified&&assistedBackend.LastSnapshot!=null)') -and $assistedMain.Contains('JsonDocument.Parse(assistedBackend.LastSnapshot.Output)') -and $assistedNative.Contains('LastSnapshot=')) 'T10_S08b' 'The window renders the current verified native snapshot after successful operation' 'R-SPEED'
