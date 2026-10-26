# t15_v111.ps1 - Tier 15: v1.1.1 restore path, offline only.
# Invoke-Restore flushes the Windows resolver cache once after the owned policy is removed; a failed flush is reported, never an error.
# The engine is dot-sourced with -LibraryOnly and its journal, NRPT, DoH and cache cmdlets are replaced in this process only, so nothing touches the host.

param([string]$ScratchPath)

$ErrorActionPreference = 'Stop'
$global:CurrentTier = "Tier 15: v1.1.1 restore flush"
$script:CurrentTier = $global:CurrentTier

$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\app')).Path

. (Join-Path $sourceRoot 'Engine.ps1') -LibraryOnly
. (Join-Path $PSScriptRoot 'helpers\TestHarness.ps1')

Write-Host "--- Executing Tier 15: v1.1.1 restore flush ---" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $ScratchPath | Out-Null

# Mocks: defined after the dot-source, so they replace the engine functions for this process only.
$script:FlushCalls = 0
$script:FlushResult = $true
$script:FakeState = $null
$script:FlushPhases = @(); $script:Events = @()
function Clear-DnsResolverCache { $script:FlushCalls++; $script:FlushPhases += $script:FakeState.Phase; $script:Events += 'Flush'; return $script:FlushResult }
function Read-State { return $script:FakeState }
function Save-State($State) { $script:Events += ('Save:' + $State.Phase) }
function Get-Rules { return @() }
function Get-Doh { return @() }
function Get-Effective { return @() }
function Get-DnsClientServerAddress { return @() }
function Get-Status { $script:Events += 'Status'; return [pscustomobject]@{ State = 'Not configured' } }
function New-FakeActiveState { [pscustomobject]@{ Schema = 1; LeaseVersion = 1; LeaseManaged = $false; Leases = @(); Owner = 'fixture'; Phase = 'Active'; ProfileName = 'fixture'; Template = ''; Protocol = 'DoH'; Targets = @(); Servers = @(); Rules = @(); Doh = @(); UpdatedUtc = '' } }

# T15_R01: a successful restore flushes the resolver cache exactly once and reports it
$script:FakeState = New-FakeActiveState
$script:FlushCalls = 0; $script:FlushResult = $true
$r1 = Invoke-Restore
Assert-Condition ($script:FlushCalls -eq 1 -and $r1.Code -eq 'ROLLED_BACK' -and $r1.DnsCacheFlushed -eq $true) 'T15_R01' 'Invoke-Restore flushes the resolver cache once after the owned policy is removed and reports DnsCacheFlushed' 'R11-FLUSH' -FailureMessage ("flush calls=$($script:FlushCalls) code=$($r1.Code) flushed=$($r1.DnsCacheFlushed)")

# T15_R02: a failed flush is reported as false and does not turn the restore into an error
$script:FakeState = New-FakeActiveState
$script:FlushCalls = 0; $script:FlushResult = $false
$r2 = $null; $threw = $false
try { $r2 = Invoke-Restore } catch { $threw = $true }
Assert-Condition ((-not $threw) -and $script:FlushCalls -eq 1 -and $r2.Code -eq 'ROLLED_BACK' -and $r2.DnsCacheFlushed -eq $false) 'T15_R02' 'A failed cache flush during restore is reported as DnsCacheFlushed=false and the restore still succeeds' 'R11-FLUSH' -FailureMessage ("threw=$threw flush calls=$($script:FlushCalls)")

# T15_R03: observe real manual restore ordering, independent of source spelling.
$script:FakeState = New-FakeActiveState; $script:FlushResult = $true; $script:Events = @()
$r3 = Invoke-Restore
$manualEvents = $script:Events -join ','
Assert-Condition ($manualEvents -eq 'Save:Restoring,Save:Restored,Flush,Status') 'T15_R03' 'Manual restore commits owned removal, flushes, then reads status' 'R11-FLUSH' -FailureMessage $manualEvents

# T15_R04: the Apply function itself still has one explicit cache invalidation.
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $sourceRoot 'Engine.ps1'), [ref]$tokens, [ref]$parseErrors)
$apply = $ast.Find({param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Apply-State'}, $true)
$applyFlushes = @($apply.Body.FindAll({param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Clear-DnsResolverCache'}, $true))
Assert-Condition ($parseErrors.Count -eq 0 -and $applyFlushes.Count -eq 1) 'T15_R04' 'Apply-State owns one cache invalidation independently of managed restore invalidation' 'R11-FLUSH'

# Managed completion must invalidate while Restoring, before durable completion; no duplicate Invoke-Restore flush.
$script:FakeState = New-FakeActiveState; $script:FakeState.LeaseManaged = $true
$script:FlushCalls = 0; $script:Events = @(); $script:FlushResult = $true
$rm = Invoke-Restore
Assert-Condition ($script:FlushCalls -eq 1 -and $rm.DnsCacheFlushed -and ($script:Events -join ',') -eq 'Save:Restoring,Flush,Save:Restored,Status' -and $script:FlushPhases[-1] -eq 'Restoring') 'T15_R05' 'Managed restore invalidates cache before completion and never flushes twice' 'R11-FLUSH'
$script:FakeState = New-FakeActiveState; $script:FakeState.LeaseManaged = $true
$script:FlushCalls = 0; $script:Events = @(); $script:FlushResult = $false
$failure = ''; try { Invoke-Restore | Out-Null } catch { $failure = $_.Exception.Message }
Assert-Condition ($failure -like '*DNS_CACHE_FLUSH_UNCONFIRMED*' -and $script:FakeState.Phase -eq 'Restoring' -and ($script:Events -join ',') -eq 'Save:Restoring,Flush') 'T15_R06' 'Failed managed invalidation retains Restoring journal and refuses durable completion' 'R11-FLUSH'
$script:FlushResult = $true; $script:Events = @(); $retry = Invoke-Restore
Assert-Condition ($script:FakeState.Phase -eq 'Restored' -and $retry.DnsCacheFlushed -and ($script:Events -join ',') -eq 'Save:Restoring,Flush,Save:Restored,Status') 'T15_R07' 'Retry invalidates cache before proving managed completion' 'R11-FLUSH'

Write-Host "--- Tier 15 Completed ---" -ForegroundColor Cyan
