param([Parameter(Mandatory=$true)][string]$ScratchPath)
$ErrorActionPreference='Stop'
# Reuse only the existing native test harness declarations, before its first test.
# Every Windows mutation cmdlet is mocked; Engine.ps1 itself and real journal file IO run unchanged.
$harness=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Engine.Tests.ps1') -Raw -Encoding UTF8
$first=[regex]::Match($harness,'(?m)^Reset-Mocks\r?$')
if(!$first.Success){throw 'Native fixture declaration boundary missing'}
$script:AssistedHarnessRoot=$PSScriptRoot
. ([scriptblock]::Create($harness.Substring(0,$first.Index).Replace('$PSScriptRoot','$script:AssistedHarnessRoot'))) -ScratchPath $ScratchPath
$script:ExpectedRevision='';$script:ExpectedUntilMs=0
function Test-Admin {return $true}
function Arm-Preview {
 $script:ExpectedRevision=(Get-AssistedContext).Revision
 $script:ExpectedUntilMs=[DateTimeOffset]::UtcNow.AddMinutes(5).ToUnixTimeMilliseconds()
}
function Disarm-Preview {$script:ExpectedRevision='';$script:ExpectedUntilMs=0}
Reset-Mocks
$one=Get-AssistedContext;$two=Get-AssistedContext
Check ($one.Revision -ceq $two.Revision -and $one.Baseline -ceq $two.Baseline -and $script:Writes -eq 0) 'assisted read-only context is stable and performs no Windows writes'
Arm-Preview;Enable-State $true $false | Out-Null
Check ((Read-State).Phase -eq 'Active' -and $script:FakeRules.Count -eq 2) 'assisted current prepared context applies through original engine'
Arm-Preview;Invoke-Restore | Out-Null
Check ((Read-State).Phase -eq 'Restored' -and $script:FakeRules.Count -eq 0 -and $script:FakeDoh.Count -eq 0) 'assisted current prepared restore removes only owned resources'
Disarm-Preview
Reset-Mocks;Arm-Preview;$script:ExpectedUntilMs=[DateTimeOffset]::UtcNow.AddMilliseconds(-1).ToUnixTimeMilliseconds()
Reject {Enable-State $true $false} 'ASSISTED_PREVIEW_EXPIRED' 'assisted expired elevation proof refused before native writes'
Check ($script:Writes -eq 0) 'assisted expiry preserves baseline'
Disarm-Preview
Reset-Mocks;Arm-Preview;$script:FakeDoh=@([pscustomobject]@{ServerAddress='1.1.1.1';DohTemplate='https://foreign.example/dns-query';AutoUpgrade=$true;AllowFallbackToUdp=$false})
Reject {Enable-State $true $false} 'ASSISTED_PREVIEW_STALE' 'assisted foreign DoH change invalidates elevated preimage'
Check ($script:Writes -eq 0 -and $script:FakeDoh.Count -eq 1) 'assisted stale proof retains foreign DoH'
Disarm-Preview
Reset-Mocks;Arm-Preview;$script:FakeRules=@([pscustomobject]@{Name='{00000000-0000-0000-0000-000000000001}';Namespace=@('foreign.example');NameServers='1.1.1.1';DisplayName='Foreign';Comment='Foreign'})
Reject {Enable-State $true $false} 'ASSISTED_PREVIEW_STALE' 'assisted foreign policy change invalidates preimage even outside selected scope'
Check ($script:Writes -eq 0 -and $script:FakeRules.Count -eq 1) 'assisted foreign rule retained before stale operation'
Disarm-Preview
Reset-Mocks;$script:FakeRules=@([pscustomobject]@{Name='{00000000-0000-0000-0000-000000000002}';Namespace=@('unrelated.example');NameServers='1.1.1.1';DisplayName='Foreign';Comment='Foreign'})
$baseline=Get-AssistedContext;Arm-Preview;Enable-State $true $false | Out-Null
$active=Get-AssistedContext
Check ($active.Rules.Count -eq 3 -and @($active.Rules | Where-Object {$_.Owned}).Count -eq 2 -and !$active.ForeignConflict) 'assisted owned projection is bounded and foreign policy remains distinct'
Arm-Preview;Invoke-Restore | Out-Null
$after=Get-AssistedContext
Check ($after.Baseline -ceq $baseline.Baseline -and $script:FakeRules.Count -eq 1 -and $script:FakeRules[0].Comment -eq 'Foreign') 'assisted semantic preimage verifies original foreign rule after rollback'
Disarm-Preview
# First native mutation must see the on-disk Applying journal. Instrument the existing fake write boundary.
Reset-Mocks
$script:OriginalAddDoh=${function:Add-DnsClientDohServerAddress};$script:JournalBeforeWrite=0
function Add-DnsClientDohServerAddress {
 param($ServerAddress,$DohTemplate,$AutoUpgrade,$AllowFallbackToUdp,$ErrorAction)
 if((Read-State).Phase -ne 'Applying'){throw 'JOURNAL_NOT_PERSISTED_BEFORE_WRITE'}
 $script:JournalBeforeWrite++;& $script:OriginalAddDoh @PSBoundParameters
}
Arm-Preview;Enable-State $true $false | Out-Null
Check ($script:JournalBeforeWrite -eq 2) 'assisted real protected journal IO precedes every mocked DoH mutation'
Disarm-Preview
# Actual partial-apply/compensation implementation, with a foreign sentinel kept in place.
Reset-Mocks;$script:FailDohAt=2
$script:FakeRules=@([pscustomobject]@{Name='{00000000-0000-0000-0000-000000000003}';Namespace=@('unrelated.example');NameServers='1.1.1.1';DisplayName='Foreign';Comment='Foreign'})
$baseline=Get-AssistedContext;Arm-Preview
Reject {Enable-State $true $false} 'APPLY_FAILED_ROLLED_BACK' 'assisted actual native partial apply is compensated'
Check ((Get-AssistedContext).Baseline -ceq $baseline.Baseline -and (Read-State).Phase -eq 'Restored') 'assisted native compensation restores semantic baseline and foreign rule'
Disarm-Preview
# The production Invoke-Engine entry retains its real on-disk FileShare.None operation lock.
Reset-Mocks;Initialize-StateDirectory;Arm-Preview;$script:Action='enable';$script:TrustProxy=$true
$held=Open-OperationLock
try{Reject {Invoke-Engine} 'OPERATION_IN_PROGRESS' 'assisted native mutation lock prevents a second child'}finally{$held.Dispose()}
Check ($script:Writes -eq 0) 'assisted lock contention performs no Windows writes'
Invoke-Engine | Out-Null
Check ((Read-State).Phase -eq 'Active') 'assisted guarded production entry applies under original operation lock'
Disarm-Preview;$script:Action='status';$script:TrustProxy=$false
# A route whose validated pin subset changed after preview is not silently substituted.
Reset-Mocks;Arm-Preview
$script:OriginalNetwork=${function:Get-Network}
function Get-Network {$n=& $script:OriginalNetwork;$n.Candidates=@($n.Candidates | Select-Object -First 1);return $n}
Reject {Enable-State $true $false} 'ASSISTED_PROVIDER_CHANGED' 'assisted changed validated address set requires a new preview'
Check ($script:Writes -eq 0) 'assisted provider revision refusal before mutation'
Set-Item Function:Get-Network -Value $script:OriginalNetwork
Disarm-Preview
# A failure during rollback must keep a Failed journal and explicit owned recovery work.
Reset-Mocks;$script:FailAfterRule=$true
function Remove-DnsClientNrptRule {param($Name,[switch]$Force,$ErrorAction);throw 'SIMULATED_RESTORE_REFUSAL'}
Arm-Preview
Reject {Enable-State $true $false} 'FAILED_RESTORABLE' 'assisted failed native compensation is never reported restored'
Check ((Read-State).Phase -eq 'Failed' -and $script:FakeRules.Count -gt 0) 'assisted failed rollback preserves native recovery journal and rules'
Disarm-Preview
# Restore the fixture removal function after the intentional refusal above.
function Remove-DnsClientNrptRule {param($Name,[switch]$Force,$ErrorAction);$script:FakeRules=@($script:FakeRules | Where-Object {$_.Name -ne $Name});$script:Writes++}
foreach($phase in 'Applying','Restoring','Failed'){
 Reset-Mocks;Arm-Preview;Enable-State $true $false | Out-Null;Disarm-Preview
 $state=Read-State;$state.Phase=$phase;Save-State $state
 $partial=Get-AssistedContext
 Check (!$partial.ForeignConflict -and @($partial.Rules | Where-Object {$_.Owned}).Count -eq 2) ('interrupted '+$phase+' exact owned rules retain recovery authority')
 $savedComment=$script:FakeRules[0].Comment;$script:FakeRules[0].Comment='Foreign edit'
 Check ((Get-AssistedContext).ForeignConflict) ('interrupted '+$phase+' edited owner remains foreign conflict')
 $script:FakeRules[0].Comment=$savedComment
 Arm-Preview;Invoke-Restore | Out-Null;Disarm-Preview
 Check ((Read-State).Phase -eq 'Restored' -and $script:FakeRules.Count -eq 0) ('interrupted '+$phase+' guarded native Restore completes')
}
Write-Output ('ASSISTED_NATIVE_FIXTURE_PASS '+$script:Tests)
