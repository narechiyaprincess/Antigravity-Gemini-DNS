param([string]$ScratchPath)
$ErrorActionPreference='Stop'
if(!$ScratchPath){throw 'Provide -ScratchPath under a disposable work directory'}
. (Join-Path $PSScriptRoot 'Engine.ps1') -LibraryOnly
$script:Tests=0;$script:AllRoots=@();$script:FakeRules=@();$script:FakeDoh=@();$script:Writes=0;$script:FailAfterRule=$false;$script:FailDohAt=0;$script:DohAdds=0
function Check($Value,$Label){if(!$Value){throw ('FAIL '+$Label)};$script:Tests++;Write-Output ('PASS '+$Label)}
function Reject($Call,$Expected,$Label){$caught=$false;try{& $Call | Out-Null}catch{if($_.Exception.Message -notlike ('*'+$Expected+'*')){throw};$caught=$true};Check $caught $Label}
function Reset-Mocks {
 $script:Root=Join-Path $ScratchPath ([guid]::NewGuid().ToString('N'));$script:AllRoots+= $script:Root
 $script:StatePath=Join-Path $script:Root 'state.json';$script:FakeRules=@();$script:FakeDoh=@();$script:Writes=0;$script:FailAfterRule=$false;$script:FailDohAt=0;$script:DohAdds=0;$script:BadNetwork=$false;$script:NetworkCalls=0
}
# Never flush the real host resolver cache from an offline fixture.
function Clear-DnsResolverCache {return $true}
function Get-Rules {return $script:FakeRules}
function Get-Effective {return @($script:FakeRules | Select-Object Namespace,NameServers)}
function Get-Doh {return $script:FakeDoh}
function Test-HostsConflict {return $false}
function Assert-Capabilities($Encrypted){}
function Initialize-StateDirectory {[IO.Directory]::CreateDirectory($script:Root) | Out-Null}
function Get-Network {$script:NetworkCalls++;return [pscustomobject]@{Candidates=@(foreach($ip in $script:Servers){[pscustomobject]@{Server=$ip;DohValidated=(!$script:BadNetwork);PlainValidated=(!$script:BadNetwork);TargetA=@('179.254.166.174');TargetAnswers=[pscustomobject]@{'daily-cloudcode-pa.googleapis.com'=@('179.254.166.174');'generativelanguage.googleapis.com'=@('179.254.166.175')}}})}}
function Get-SystemAnswers($Domain) {if($Domain -eq 'generativelanguage.googleapis.com'){return @('179.254.166.175')};return @('179.254.166.174')}
function Get-DnsClientServerAddress {return @()}
function Add-DnsClientDohServerAddress {
 param($ServerAddress,$DohTemplate,$AllowFallbackToUdp,$AutoUpgrade,$ErrorAction)
 $script:Writes++;$script:DohAdds++
 if($script:FailDohAt -eq $script:DohAdds){throw 'SIMULATED_DOH_FAILURE'}
 $script:FakeDoh+= [pscustomobject]@{ServerAddress=$ServerAddress;DohTemplate=$DohTemplate;AllowFallbackToUdp=$AllowFallbackToUdp;AutoUpgrade=$AutoUpgrade}
 return $script:FakeDoh[-1]
}
function Remove-DnsClientDohServerAddress {param($ServerAddress,$ErrorAction);$script:Writes++;$script:FakeDoh=@($script:FakeDoh | Where-Object {$_.ServerAddress -ne $ServerAddress})}
function Add-DnsClientNrptRule {
 param($Namespace,$NameServers,$DisplayName,$Comment,$NameEncoding,$DAProxyType,$DAIPsecEncryptionType,$DnsSecIPsecEncryptionType,$DAEnable,$DAIPsecRequired,$DnsSecEnable,$DnsSecValidationRequired,$DnsSecIPsecRequired,[switch]$PassThru,$ErrorAction)
 $script:Writes++;$r=[pscustomobject]@{Name=('{'+[guid]::NewGuid().ToString()+'}');Namespace=@($Namespace);NameServers=$NameServers;DisplayName=$DisplayName;Comment=$Comment;NameEncoding=$NameEncoding;DnsSecValidationRequired=$false};$script:FakeRules+= $r
 if($script:FailAfterRule){throw 'SIMULATED_CRASH_AFTER_RULE'}
 return $r
}
function Remove-DnsClientNrptRule {param($Name,[switch]$Force,$ErrorAction);$script:Writes++;$script:FakeRules=@($script:FakeRules | Where-Object {$_.Name -ne $Name})}

Reset-Mocks
Reject {Enable-State $false $false} 'TRUST_DECISION_REQUIRED' 'trust gate before writes'
Check ($script:Writes -eq 0) 'trust refusal preserves system'
$firstResult=Enable-State $true $false
Check (@($firstResult).Count -eq 1 -and $firstResult.State -eq 'DNS active') 'CIM mutation output cannot leak into engine result'
Check ($script:FakeRules.Count -eq 2 -and $script:FakeDoh.Count -eq 2) 'Apply exact scope + two DoH registrations'
$writes=$script:Writes;Enable-State $true $false | Out-Null
Check ($script:Writes -eq $writes) 'Enable twice idempotent'
Check ($script:NetworkCalls -eq 1) 'identical active profile uses system readback without repeating provider preflight'
Check ((Read-State).Phase -eq 'Active') 'journal commit Active'
Restore-State (Read-State) | Out-Null
Check ($script:FakeRules.Count -eq 0 -and $script:FakeDoh.Count -eq 0 -and (Read-State).Phase -eq 'Restored') 'Restore removes owned changes'
$writes=$script:Writes;Restore-State (Read-State) | Out-Null
Check ($script:Writes -eq $writes) 'Restore twice no-op'

Reset-Mocks;$script:FakeRules=@([pscustomobject]@{Name='{00000000-0000-0000-0000-000000000001}';Namespace=@('.googleapis.com');NameServers='1.1.1.1';DisplayName='Foreign';Comment='Foreign'})
Reject {Enable-State $true $false} 'NRPT_CONFLICT' 'foreign parent NRPT fails closed'
Check ($script:Writes -eq 0 -and $script:FakeRules.Count -eq 1) 'foreign policy preserved'
Reset-Mocks;$script:FakeRules=@([pscustomobject]@{Name='{00000000-0000-0000-0000-000000000002}';Namespace=@('unrelated.example');NameServers='1.1.1.1';DisplayName='Foreign';Comment='Foreign'})
Enable-State $true $false | Out-Null;Restore-State (Read-State) | Out-Null
Check ($script:FakeRules.Count -eq 1 -and $script:FakeRules[0].Comment -eq 'Foreign') 'unrelated NRPT survives apply/restore'

Reset-Mocks;$script:FailDohAt=2
Reject {Enable-State $true $false} 'APPLY_FAILED_ROLLED_BACK' 'failure between DoH registrations rolled back'
Check ($script:FakeDoh.Count -eq 0 -and $script:FakeRules.Count -eq 0 -and (Read-State).Phase -eq 'Restored') 'partial DoH transaction recovered'
Reset-Mocks;$script:FailAfterRule=$true
Reject {Enable-State $true $false} 'APPLY_FAILED_ROLLED_BACK' 'crash after policy creation before GUID journal'
Check ($script:FakeRules.Count -eq 0 -and $script:FakeDoh.Count -eq 0) 'pending owner marker recovers policy'

Reset-Mocks
$script:FakeDoh=@([pscustomobject]@{ServerAddress=$script:Servers[0];DohTemplate=$script:Template;AutoUpgrade=$true;AllowFallbackToUdp=$false})
Enable-State $true $false | Out-Null;Restore-State (Read-State) | Out-Null
Check ($script:FakeDoh.Count -eq 1 -and $script:FakeDoh[0].ServerAddress -eq $script:Servers[0]) 'preexisting compatible DoH retained'
Reset-Mocks;Enable-State $true $false | Out-Null
$script:FakeRules[0].NameServers='8.8.8.8'
Reject {Restore-State (Read-State)} 'OWNED_RULE_CHANGED' 'independently modified NRPT retained'
Check ($script:FakeRules.Count -eq 2) 'modified NRPT not deleted'
Reset-Mocks;Enable-State $true $false | Out-Null
$script:FakeRules[-1].NameServers='8.8.8.8';$writes=$script:Writes
Reject {Restore-State (Read-State)} 'OWNED_RULE_CHANGED' 'direct restore preflights the last rule before removing the first'
Check ($script:Writes -eq $writes -and $script:FakeRules.Count -eq 2 -and (Read-State).Phase -eq 'Restoring') 'late foreign edit leaves the whole configuration intact with recovery intent'
Reset-Mocks;Enable-State $true $false | Out-Null
$script:FakeDoh[0].AllowFallbackToUdp=$true
Reject {Restore-State (Read-State)} 'DOH_CHANGED_INDEPENDENTLY' 'independently changed DoH retained'
Check ($script:FakeDoh.Count -eq 2) 'modified DoH not deleted'
Check ($script:FakeRules.Count -eq 2 -and (Read-State).Phase -eq 'Restoring') 'DoH edit preflight preserves owned rules and recovery journal'
Reset-Mocks;Enable-State $true $false | Out-Null
$script:FakeDoh[-1].AllowFallbackToUdp=$true;$writes=$script:Writes
Reject {Restore-State (Read-State)} 'DOH_CHANGED_INDEPENDENTLY' 'direct restore preflights the last DoH registration'
Check ($script:Writes -eq $writes -and $script:FakeRules.Count -eq 2 -and $script:FakeDoh.Count -eq 2) 'late DoH edit leaves all policies and registrations intact'
Reset-Mocks;Enable-State $true $false | Out-Null
$script:FakeRules+= [pscustomobject]@{Name='{00000000-0000-0000-0000-000000000003}';Namespace=@('shared.example');NameServers=$script:Servers[0];DisplayName='Foreign';Comment='Foreign'}
$sharedResult=Restore-State (Read-State)
Check ($sharedResult.Code -eq 'ROLLED_BACK_SHARED_DOH_RETAINED' -and (Read-State).Phase -eq 'Restored') 'shared DoH retained without blocking lifecycle'
Check ($script:FakeRules.Count -eq 1 -and $script:FakeDoh.Count -eq 1) 'shared policy and registration preserved'

Reset-Mocks;$script:BadNetwork=$true
Reject {Enable-State $true $false} 'DOH_NO_VALIDATED_ENDPOINT' 'unreachable network no fallback'
Check ($script:Writes -eq 0 -and !(Test-Path $script:StatePath)) 'offline failure makes no journal or system writes'
Reset-Mocks;Initialize-StateDirectory;[IO.File]::WriteAllText($script:StatePath,'{broken')
Reject {Read-State} 'STATE_INVALID' 'malformed state fails closed'
Check ($script:Writes -eq 0) 'malformed state never mutates'
Reset-Mocks;Enable-State $true $false | Out-Null
$s=Read-State;$s.Phase='Applying';$s.Rules[0].Name=$null;Save-State $s
Restore-State (Read-State) | Out-Null
Check ($script:FakeRules.Count -eq 0 -and $script:FakeDoh.Count -eq 0) 'reboot recovery from pending intent'
Reset-Mocks;Enable-State $true $true | Out-Null
Check ((Read-State).Protocol -eq 'Plain DNS' -and $script:FakeDoh.Count -eq 0) 'explicit plaintext never creates DoH settings'
Restore-State (Read-State) | Out-Null
Check ($script:FakeRules.Count -eq 0) 'plaintext restore exact ownership'
Reset-Mocks;Enable-State $true $false | Out-Null
$script:FakeDoh=@()
Check ((Get-Status).State -eq 'Failed (restorable)' -and !(Get-Status).Active) 'missing DoH cannot report DNS active'
Reset-Mocks;Enable-State $true $false | Out-Null
$script:FakeDoh[0].AllowFallbackToUdp=$true
Check ((Get-Status).Code -eq 'DOH_REGISTRATION_CONFLICT') 'UDP downgrade detected by status'
Reset-Mocks;Enable-State $true $false | Out-Null
$s=Read-State;$s.Phase='Applying';$s.Doh[0].Ownership='Pending';Save-State $s
Reject {Restore-State (Read-State)} 'DOH_OWNERSHIP_UNCERTAIN' 'ambiguous registration never claimed from intent'
Check ($script:FakeRules.Count -eq 0 -and $script:FakeDoh.Count -eq 1) 'ambiguous DoH retained; confirmed own settings removed'
Reset-Mocks;Enable-State $true $false | Out-Null
$s=Read-State;$s.Phase='Applying';$s.Doh[0].Ownership='Planned';Save-State $s
Restore-State (Read-State) | Out-Null
Check ($script:FakeDoh.Count -eq 1) 'unexecuted intent cannot delete later foreign registration'
Reset-Mocks;Enable-State $true $false | Out-Null
$script:FakeRules[0].DnsSecValidationRequired=$true
Reject {Restore-State (Read-State)} 'OWNED_RULE_CHANGED' 'independent DNSSEC policy edit retained'
Check ($script:FakeRules.Count -eq 2) 'complete policy fingerprint protects security changes'
Reset-Mocks
function Test-Admin {return $true}
$Action='restore';$noJournal=Invoke-Engine
Check ($noJournal.Code -eq 'ROLLED_BACK' -and $noJournal.Result -eq 'No-op' -and (Test-Path $script:Root)) 'first Restore initializes receipt directory without journal writes'
Reset-Mocks
$taskScope=$script:Targets;$script:Targets=@('daily-cloudcode-pa.googleapis.com')
Enable-State $true $false | Out-Null
$script:Targets=$taskScope
Check ((Get-Status).Code -eq 'RESTORE_BEFORE_SCOPE_UPGRADE' -and !(Get-Status).Active) 'legacy single-domain active journal cannot claim complete bridge'
Reject {Enable-State $true $false} 'RESTORE_BEFORE_SCOPE_UPGRADE' 'scope upgrade requires restoring owned legacy policy'
Restore-State (Read-State) | Out-Null
Check ($script:FakeRules.Count -eq 0 -and $script:FakeDoh.Count -eq 0) 'legacy journal ownership restores without migration writes'
Reset-Mocks
Enable-State $true $false | Out-Null
Check ((Diagnose).State -eq 'DNS active') 'distinct answers validated against their own API domain'
function Get-SystemAnswers($Domain) {return @('179.254.166.174')}
Check (!(Diagnose).Status.Active -and (Diagnose).Code -eq 'NRPT_NOT_EFFECTIVE') 'wrong second-domain answer clears active status'
function Get-SystemAnswers($Domain) {if($Domain -eq 'generativelanguage.googleapis.com'){return @('179.254.166.175')};return @('179.254.166.174')}
Reset-Mocks
Enable-State $true $false | Out-Null
foreach($phase in @('Applying','Restoring','Failed')){$s=Read-State;$s.Phase=$phase;Save-State $s;Check (!(Get-Status).Active -and (Get-Status).Code -eq 'RECOVERY_REQUIRED') ('uncommitted '+$phase+' never reports connected')}
Reset-Mocks
function Invoke-EngineCheckpoint([string]$Phase){if($Phase -eq 'ActiveJournal'){$script:FakeDoh=@()}}
Reject {Enable-State $true $false} 'APPLY_FAILED_ROLLED_BACK' 'final readback rejects DoH removed after effective verification'
Check ($script:FakeRules.Count -eq 0 -and (Read-State).Phase -eq 'Restored') 'final readback failure restores owned policies before reporting failure'
Reset-Mocks
function Invoke-EngineCheckpoint([string]$Phase){if($Phase -eq 'ActiveJournal'){$script:FakeDoh[-1].AllowFallbackToUdp=$true}}
Reject {Enable-State $true $false} 'FAILED_RESTORABLE' 'final readback rejects independently changed DoH without false apply success'
Check ((Read-State).Phase -eq 'Failed' -and $script:FakeRules.Count -eq 2 -and $script:FakeDoh[-1].AllowFallbackToUdp) 'partial restore error preserves foreign edit and durable recovery journal'
function Invoke-EngineCheckpoint([string]$Phase){}
# Boundary refusal, rollback retry, and late external adapter references.
Reset-Mocks
$Action='enable';function Test-Admin {return $false}
Reject {Invoke-Engine} 'ADMIN_REQUIRED' 'unavailable privilege boundary refuses before journal and native writes'
Check ($script:Writes -eq 0 -and !(Test-Path $script:StatePath)) 'elevation refusal leaves no recovery obligation'
function Test-Admin {return $true}
Reset-Mocks;Enable-State $true $false | Out-Null
$held=Open-OperationLock;$Action='restore';$writes=$script:Writes;$before=[IO.File]::ReadAllText($script:StatePath)
try {Reject {Invoke-Engine} 'OPERATION_IN_PROGRESS' 'concurrent restore respects the mutation lock'}finally{$held.Dispose()}
Check ($script:Writes -eq $writes -and [IO.File]::ReadAllText($script:StatePath) -ceq $before) 'lock refusal preserves complete active journal'
Reset-Mocks;Enable-State $true $false | Out-Null
$script:RemoveCalls=0;$script:FailRemove=$true
function Remove-DnsClientNrptRule {
 param($Name,[switch]$Force,$ErrorAction)
 $script:RemoveCalls++;if($script:FailRemove -and $script:RemoveCalls -eq 2){throw 'SIMULATED_REMOVE_FAILURE'}
 $script:Writes++;$script:FakeRules=@($script:FakeRules | Where-Object {$_.Name -ne $Name})
}
Reject {Restore-State (Read-State)} 'SIMULATED_REMOVE_FAILURE' 'partial rollback surfaces native removal failure'
Check ((Read-State).Phase -eq 'Restoring' -and $script:FakeRules.Count -eq 1 -and $script:FakeDoh.Count -eq 2) 'failed restore retains journal and needed registrations'
$script:FailRemove=$false;Restore-State (Read-State) | Out-Null
Check ((Read-State).Phase -eq 'Restored' -and $script:FakeRules.Count -eq 0 -and $script:FakeDoh.Count -eq 0) 'retry finishes partially completed restore without lost ownership'
Reset-Mocks;Enable-State $true $false | Out-Null
$script:AdapterReference=$script:Servers[0]
function Get-DnsClientServerAddress {return @([pscustomobject]@{InterfaceIndex=42;AddressFamily=2;ServerAddresses=@($script:AdapterReference)})}
Restore-State (Read-State) | Out-Null
Check ($script:FakeRules.Count -eq 0 -and $script:FakeDoh.Count -eq 1 -and $script:FakeDoh[0].ServerAddress -eq $script:AdapterReference) 'external adapter reference appearing after apply retains only referenced DoH'
function Get-DnsClientServerAddress {return @()}
Reset-Mocks
$savedServers=$script:Servers;$script:Servers=@('2001:db8::53','2001:db8::54')
try {
 Enable-State $true $false | Out-Null
 Check ((Get-Status).Active -and $script:FakeDoh.Count -eq 2) 'IPv6 bootstrap pins apply through same scoped ownership model'
 Restore-State (Read-State) | Out-Null
 Check ($script:FakeRules.Count -eq 0 -and $script:FakeDoh.Count -eq 0) 'IPv6 bootstrap registration restore removes only owned resources'
}finally{$script:Servers=$savedServers}
# Legacy task compatibility must never infer ownership from the task name alone.
# Task Scheduler/registry mutation is intercepted; no host task is queried or changed.
$script:RunPath='HKCU:\Software\AgDnsTaskOwnershipFixture-'+[guid]::NewGuid().ToString('N')
$script:TaskXml=$null;$script:TaskQueryFails=$false;$script:TaskRegistrations=0;$script:TaskDeletes=0
function Own-TaskXml {
 $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
 $path=[Security.SecurityElement]::Escape($script:Exe)
 return ('<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Principals><Principal id="Author"><UserId>'+$sid+'</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals><Actions Context="Author"><Exec><Command>'+$path+'</Command></Exec></Actions></Task>')
}
function Get-LegacyAutostartTask {if($script:TaskQueryFails){throw 'AUTOSTART_TASK_QUERY_FAILED'};return $script:TaskXml}
function Invoke-ScheduledTaskTool([string[]]$Arguments){if(($Arguments -join ' ') -ne '/Delete /TN AntigravityDnsUnlockerLogon /F'){throw 'UNEXPECTED_TASK_OPERATION'};$script:TaskDeletes++;$script:TaskXml=$null;return 0}
function New-ScheduledTaskAction {param($Execute);return [pscustomobject]@{Execute=$Execute}}
function New-ScheduledTaskTrigger {param([switch]$AtLogOn,$User);return [pscustomobject]@{User=$User}}
function New-ScheduledTaskPrincipal {param($UserId,$LogonType,$RunLevel);return [pscustomobject]@{UserId=$UserId}}
function New-ScheduledTaskSettingsSet {param([switch]$AllowStartIfOnBatteries,[switch]$DontStopIfGoingOnBatteries,$ExecutionTimeLimit);return [pscustomobject]@{Offline=$true}}
function Register-ScheduledTask {param($TaskName,$Action,$Trigger,$Principal,$Settings,[switch]$Force,$ErrorAction);if($Force){throw 'FORCE_TASK_REPLACEMENT_FORBIDDEN'};$script:TaskRegistrations++;$script:TaskXml=Own-TaskXml}
$own=Own-TaskXml
Check (Test-LegacyAutostartTask $own) 'legacy task exact executable/current SID/least privilege recognized'
foreach($foreign in @($own.Replace('<Command>','<Command>C:\foreign\'),$own.Replace('LeastPrivilege','HighestAvailable'),$own.Replace('<UserId>','<UserId>foreign-'),$own.Replace('</Command>','</Command><Arguments>--enable</Arguments>'),$own.Replace('</Exec>','</Exec><Exec><Command>foreign.exe</Command></Exec>'))){
 $script:TaskXml=$foreign
 Check (!(Get-AutostartStatus).Enabled) 'foreign same-name task is not reported as application autostart'
 Reject {Set-Autostart $true 'Task'} 'AUTOSTART_TASK_NOT_OWNED' 'foreign same-name task cannot be replaced'
 Reject {Set-Autostart $false} 'AUTOSTART_TASK_NOT_OWNED' 'foreign same-name task cannot be deleted'
}
Check ($script:TaskDeletes -eq 0 -and $script:TaskRegistrations -eq 0) 'foreign task refusals perform zero scheduler mutations'
$script:TaskQueryFails=$true
Reject {Set-Autostart $true 'Task'} 'AUTOSTART_TASK_QUERY_FAILED' 'unknown scheduler query cannot authorize task creation'
Reject {Set-Autostart $false} 'AUTOSTART_TASK_QUERY_FAILED' 'unknown scheduler query cannot authorize task deletion'
Check ((Get-AutostartStatus).TaskQueryFailed) 'status exposes unknown task ownership'
$script:TaskQueryFails=$false;$script:TaskXml=$own
Set-Autostart $true 'Task' | Out-Null
Check ($script:TaskRegistrations -eq 0 -and (Get-AutostartStatus).Enabled) 'owned existing task enable is idempotent without replacement'
Set-Autostart $false | Out-Null
Check ($script:TaskDeletes -eq 1 -and !(Get-AutostartStatus).Enabled) 'owned task disable deletes exactly the validated task'
Set-Autostart $true 'Task' | Out-Null
Check ($script:TaskRegistrations -eq 1 -and (Get-AutostartStatus).Enabled) 'absent task creation uses current identity without Force'
Write-Output ('ENGINE_OFFLINE_PASS '+$script:Tests)
