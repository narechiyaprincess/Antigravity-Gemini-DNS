param([string]$ScratchPath)
$ErrorActionPreference='Stop'
# Reuse the established mock boundary; all native cmdlets remain in-memory throughout.
. (Join-Path $PSScriptRoot 'Engine.Tests.ps1') -ScratchPath $ScratchPath
$script:FaultPhase='';$script:FaultHit=$false
function Invoke-EngineCheckpoint([string]$Phase){if($script:FaultPhase -and $Phase -eq $script:FaultPhase){$script:FaultPhase='';$script:FaultHit=$true;throw ('INJECTED_'+$Phase)}}
function Start-Lease {
 $script:LeaseId=[guid]::NewGuid().ToString('N');$script:LeaseOwner='S-1-5-21-123:session1';$script:LeaseUntilMs=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()+3600000
 $script:TrustProxy=$true;$script:Plain=$false;$script:ExpectedRevision='';$script:Action='lease-acquire'
 Invoke-Engine
}
Reset-Mocks
Start-Lease | Out-Null
$s=Read-State
Check ($s.Schema -eq 2 -and $s.LeaseVersion -eq 1 -and $s.LeaseManaged -and @($s.Leases).Count -eq 1) 'versioned initial lease committed with native authority'
$bytes=[IO.File]::ReadAllText($script:StatePath);$writes=$script:Writes
Invoke-Engine | Out-Null
Check ($script:Writes -eq $writes -and [IO.File]::ReadAllText($script:StatePath) -ceq $bytes) 'duplicate lease acquire is exact no-op'
Reject {Enable-State $true $false} 'LEASE_CONFIGURATION_CONFLICT' 'legacy no-change enable cannot bypass durable leases'
$firstId=$script:LeaseId;$firstOwner=$script:LeaseOwner
$script:LeaseOwner='other:session2'
Reject {Invoke-Engine} 'LEASE_ID_REUSED' 'lease ID cannot be adopted by another owner'
$script:LeaseId=[guid]::NewGuid().ToString('N')
Invoke-Engine | Out-Null
Check (@((Read-State).Leases).Count -eq 2 -and $script:Writes -eq $writes) 'two owner references share one existing native bundle'
$script:LeaseId=$firstId;$script:Action='lease-release'
Reject {Invoke-Engine} 'LEASE_OWNER_MISMATCH' 'release validates exact requester identity'
$script:LeaseOwner=$firstOwner
Invoke-Engine | Out-Null
Check (@((Read-State).Leases).Count -eq 1 -and $script:FakeRules.Count -eq 2) 'release retains resources while another owner references them'
$releaseBytes=[IO.File]::ReadAllText($script:StatePath)
Invoke-Engine | Out-Null
Check ([IO.File]::ReadAllText($script:StatePath) -ceq $releaseBytes -and @((Read-State).Leases).Count -eq 1 -and $script:FakeRules.Count -eq 2) 'duplicate release never consumes another reference'
$s=Read-State;$s.Leases[0].ExpiresUtcMs=[long]1;Save-State $s;$script:Action='lease-recover'
Invoke-Engine | Out-Null
Check ((Read-State).Phase -eq 'Restored' -and $script:FakeRules.Count -eq 0 -and $script:FakeDoh.Count -eq 0) 'last expired lease restores exact owned bundle without GUI'
$writes=$script:Writes;Invoke-Engine | Out-Null
Check ($script:Writes -eq $writes) 'repeated expiry recovery is no-op'
Reset-Mocks;Start-Lease | Out-Null
$s=Read-State;$s.UpdatedUtc=[DateTime]::UtcNow.AddHours(1).ToString('o');[IO.File]::WriteAllText($script:StatePath,($s | ConvertTo-Json -Depth 10))
$script:Action='lease-recover';Invoke-Engine | Out-Null
Check ((Read-State).Phase -eq 'Restored' -and $script:FakeRules.Count -eq 0) 'backward clock cannot prolong durable native lease'
Reset-Mocks;Enable-State $true $false | Out-Null
$bytes=[IO.File]::ReadAllText($script:StatePath);$writes=$script:Writes
Reject {Start-Lease} 'LEASE_CONFIGURATION_CONFLICT' 'broker lease cannot adopt manual Assisted bridge'
$script:Action='lease-recover';Invoke-Engine | Out-Null
Check ($script:Writes -eq $writes -and [IO.File]::ReadAllText($script:StatePath) -ceq $bytes) 'automatic lease recovery leaves manual bridge untouched'
$s=Read-State;$s.Schema=1;$s.PSObject.Properties.Remove('LeaseVersion');$s.PSObject.Properties.Remove('LeaseManaged');$s.PSObject.Properties.Remove('Leases')
[IO.File]::WriteAllText($script:StatePath,($s | ConvertTo-Json -Depth 10))
$bytes=[IO.File]::ReadAllText($script:StatePath);$s=Read-State
Check ($s.Schema -eq 1 -and !$s.LeaseManaged -and @($s.Leases).Count -eq 0 -and [IO.File]::ReadAllText($script:StatePath) -ceq $bytes) 'schema1 read normalization performs no disk migration'
Restore-State $s | Out-Null
Check ((Read-State).Schema -eq 2 -and (Read-State).Phase -eq 'Restored') 'schema1 migrates durably on next authorized mutation'
$s=Read-State;$s.Schema=99;[IO.File]::WriteAllText($script:StatePath,($s | ConvertTo-Json -Depth 10))
Reject {Read-State} 'STATE_VERSION_UNSUPPORTED' 'unsupported journal version fails closed'
Reset-Mocks;Start-Lease | Out-Null
$s=Read-State;$before=[IO.File]::ReadAllText($script:StatePath)
$writer=${function:Write-JournalBytes}
function Write-JournalBytes([string]$Path,[byte[]]$Bytes){[IO.File]::WriteAllBytes($Path,[byte[]]@(123));throw 'SIMULATED_DISK_FULL'}
Reject {Save-State $s} 'SIMULATED_DISK_FULL' 'disk full during temporary journal write surfaces failure'
Check ([IO.File]::ReadAllText($script:StatePath) -ceq $before -and (Read-State).Phase -eq 'Active') 'disk full preserves last complete recovery authority byte-for-byte'
Set-Item function:Write-JournalBytes $writer
$script:FaultPhase='JournalDurableBeforeReplace';$script:FaultHit=$false
Reject {Save-State $s} 'INJECTED_' 'failure after flush before replacement surfaces failure'
Check ([IO.File]::ReadAllText($script:StatePath) -ceq $before) 'flushed temporary candidate cannot destroy previous authority'
Reset-Mocks
foreach($phase in @('ApplyingJournal','PendingDohJournal','AfterAddDoh','OwnedDohJournal','BeforeAddRule','AfterAddRule','OwnedRuleJournal','BeforeEffectiveVerification','ActiveJournal')){
 Reset-Mocks;$script:FaultPhase=$phase;$script:FaultHit=$false
 Reject {Start-Lease} $(if($phase -eq 'AfterAddDoh'){'FAILED_RESTORABLE'}else{'APPLY_FAILED_ROLLED_BACK'}) ('apply fault '+$phase)
 Check $script:FaultHit ('checkpoint reached '+$phase)
 $script:Action='lease-recover'
 if($phase -eq 'AfterAddDoh'){
  Reject {Invoke-Engine} 'DOH_OWNERSHIP_UNCERTAIN' 'post-add interrupted registration retained as uncertain'
  Check ($script:FakeDoh.Count -eq 1 -and (Read-State).Phase -eq 'Restoring') 'ambiguous native mutation keeps journal and resource'
  Reject {Invoke-Engine} 'DOH_OWNERSHIP_UNCERTAIN' 'repeated uncertain recovery remains safe refusal'
 }else{
  Invoke-Engine | Out-Null;Invoke-Engine | Out-Null
  Check ($script:FakeRules.Count -eq 0 -and $script:FakeDoh.Count -eq 0 -and (Read-State).Phase -eq 'Restored') ('owned-only repeated recovery '+$phase)
 }
}
foreach($phase in @('RestoringJournal','BeforeRemoveRule','AfterRemoveRule','BeforeRemoveDoh','AfterRemoveDoh','RestoredJournal','LeaseReleasedJournal')){
 Reset-Mocks;Start-Lease | Out-Null;$script:Action='lease-release';$script:FaultPhase=$phase;$script:FaultHit=$false
 Reject {Invoke-Engine} 'INJECTED_' ('restore fault '+$phase)
 Check ($script:FaultHit -and (Test-Path -LiteralPath $script:StatePath)) ('recovery authority retained '+$phase)
 $script:Action='lease-recover';Invoke-Engine | Out-Null;Invoke-Engine | Out-Null
 Check ($script:FakeRules.Count -eq 0 -and $script:FakeDoh.Count -eq 0 -and (Read-State).Phase -eq 'Restored') ('resume restore '+$phase)
}
Reset-Mocks;Start-Lease | Out-Null
$script:LeaseId=[guid]::NewGuid().ToString('N');$script:LeaseOwner='another:session2';$script:FaultPhase='LeaseAcquiredJournal';$script:FaultHit=$false
Reject {Invoke-Engine} 'INJECTED_' 'post-acquire journal failure retains reference for retry'
$writes=$script:Writes;Invoke-Engine | Out-Null
Check (@((Read-State).Leases).Count -eq 2 -and $script:Writes -eq $writes) 'acquire retry after lost response cannot duplicate native bundle or reference'
$s=Read-State;foreach($l in $s.Leases){$l.ExpiresUtcMs=[long]1};Save-State $s;$script:Action='lease-recover';$script:FaultPhase='LeaseExpiredJournal';$script:FaultHit=$false
Reject {Invoke-Engine} 'INJECTED_' 'expiry journal crash retains zero-ref recovery authority'
Invoke-Engine | Out-Null
Check ((Read-State).Phase -eq 'Restored' -and $script:FakeRules.Count -eq 0 -and $script:FakeDoh.Count -eq 0) 'zero-ref active bundle resumes recovery after expiry crash'
Reset-Mocks;Start-Lease | Out-Null
$script:FakeRules[0].NameServers='8.8.8.8';$s=Read-State;$s.Leases[0].ExpiresUtcMs=[long]1;Save-State $s;$script:Action='lease-recover'
Reject {Invoke-Engine} 'OWNED_RULE_CHANGED' 'expired lease cannot delete externally edited policy'
Check ($script:FakeRules.Count -eq 2 -and (Read-State).Phase -eq 'Restoring') 'external policy edit retains resource and recovery journal'
Reject {Invoke-Engine} 'OWNED_RULE_CHANGED' 'repeated edited-policy recovery refuses consistently'
Reset-Mocks;$held=Open-OperationLock
try{$script:Action='lease-recover';Reject {Invoke-Engine} 'OPERATION_IN_PROGRESS' 'broker recovery shares exact native Assisted mutation lock'}finally{$held.Dispose()}
Invoke-Engine | Out-Null
Check (!(Test-Path -LiteralPath $script:StatePath)) 'released lock permits no-op independent recovery without creating journal'
# Count the actual restore integration boundary; cache flushing remains mocked on the host.
$script:CacheFlushes=0;$script:CacheFlushPhases=@();$script:CacheFlushResult=$true
function Clear-DnsResolverCache {$script:CacheFlushes++;$script:CacheFlushPhases+= (Read-State).Phase;return $script:CacheFlushResult}
Reset-Mocks;Start-Lease | Out-Null
$flushBefore=$script:CacheFlushes;$script:Action='lease-recover';Invoke-Engine | Out-Null
Check ($script:CacheFlushes -eq $flushBefore) 'live lease recovery never flushes resolver cache'
$script:Action='lease-acquire';$script:LeaseId=[guid]::NewGuid().ToString('N');Invoke-Engine | Out-Null
$script:Action='lease-release';Invoke-Engine | Out-Null
Check ($script:CacheFlushes -eq $flushBefore) 'shared reference release never flushes working resolver cache'
$s=Read-State;$script:LeaseId=$s.Leases[0].Id;$script:LeaseOwner=$s.Leases[0].Owner
$r=Invoke-Engine
Check ($script:CacheFlushes -eq ($flushBefore+1) -and $script:CacheFlushPhases[-1] -eq 'Restoring' -and $r.DnsCacheFlushed) 'final release flushes stale proxy answers after proven native restore'
Invoke-Engine | Out-Null
Check ($script:CacheFlushes -eq ($flushBefore+1)) 'duplicate final release cannot flush twice'
Reset-Mocks;Start-Lease | Out-Null
$flushBefore=$script:CacheFlushes;$s=Read-State;$s.Leases[0].ExpiresUtcMs=[long]1;Save-State $s;$script:Action='lease-recover'
$r=Invoke-Engine
Check ($script:CacheFlushes -eq ($flushBefore+1) -and $script:CacheFlushPhases[-1] -eq 'Restoring' -and $r.DnsCacheFlushed) 'expiry recovery flushes stale proxy answers after proven native restore'
Reset-Mocks;Start-Lease | Out-Null
$flushBefore=$script:CacheFlushes;$script:FakeRules[0].NameServers='8.8.8.8';$script:Action='lease-release'
Reject {Invoke-Engine} 'OWNED_RULE_CHANGED' 'final release refuses external policy edit before cache flush'
Check ($script:CacheFlushes -eq $flushBefore) 'safe refusal never reports or attempts successful restore cache flush'
Reset-Mocks;Start-Lease | Out-Null
$script:CacheFlushResult=$false;$script:Action='lease-release'
Reject {Invoke-Engine} 'DNS_CACHE_FLUSH_UNCONFIRMED' 'managed cache flush failure refuses durable completion'
Check ((Read-State).Phase -eq 'Restoring' -and $script:FakeRules.Count -eq 0 -and $script:FakeDoh.Count -eq 0) 'failed managed cache flush retains journal after owned native removal'
$script:CacheFlushResult=$true;$script:Action='lease-recover';$r=Invoke-Engine
Check ((Read-State).Phase -eq 'Restored' -and $r.DnsCacheFlushed -and $script:CacheFlushPhases[-1] -eq 'Restoring') 'recovery retries cache flush before durable completion'
Reset-Mocks;Start-Lease | Out-Null
$flushBefore=$script:CacheFlushes;$script:Action='lease-release';$script:FaultPhase='RestoredJournal';$script:FaultHit=$false
Reject {Invoke-Engine} 'INJECTED_RestoredJournal' 'completion checkpoint crash occurs after managed cache flush'
Check ((Read-State).Phase -eq 'Restored' -and $script:CacheFlushes -eq ($flushBefore+1) -and $script:CacheFlushPhases[-1] -eq 'Restoring') 'Restored journal cannot survive with unflushed stale cache'
$script:Action='lease-recover';Invoke-Engine|Out-Null
Check ($script:CacheFlushes -eq ($flushBefore+1)) 'recovery after committed completion needs no additional cache flush'
Write-Output ('ENGINE_S05_PASS '+$script:Tests)
