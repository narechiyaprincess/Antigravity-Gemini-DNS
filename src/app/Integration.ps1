$ErrorActionPreference='Stop'
# no cmdlet before the engine has restricted the module path (see Engine.ps1)
. ([IO.Path]::Combine($PSScriptRoot,'Engine.ps1')) -LibraryOnly
$steps=@();$opLock=$null;$before=$null;$after=$null;$enabled=$false;$failure=$null;$ownedRules=@()
function Snapshot {
 [ordered]@{
  Nrpt=@(Get-Rules | Sort-Object Name)
  Doh=@(Get-DnsClientDohServerAddress | Select-Object ServerAddress,DohTemplate,AutoUpgrade,AllowFallbackToUdp | Sort-Object ServerAddress)
  Adapters=@(Get-DnsClientServerAddress | Select-Object InterfaceIndex,AddressFamily,ServerAddresses | Sort-Object InterfaceIndex,AddressFamily)
  Dhcp=@(Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True' | Select-Object Index,DHCPEnabled,DNSServerSearchOrder,DNSDomainSuffixSearchOrder | Sort-Object Index)
  Suffix=(Get-DnsClientGlobalSetting | Select-Object SuffixSearchList,UseDevolution,DevolutionLevel)
  HostsSha256=(Get-FileHash -LiteralPath (Join-Path ([Environment]::SystemDirectory) 'drivers\etc\hosts')).Hash
  WinHttpProxy=((& (Join-Path ([Environment]::SystemDirectory) 'netsh.exe') winhttp show proxy) -join "`n")
 } | ConvertTo-Json -Depth 7 -Compress
}
try {
 if(!(Test-Admin)){throw 'ADMIN_REQUIRED'}
 Initialize-StateDirectory
 $opLock=Open-OperationLock
 $old=Read-State;if($old -and $old.Phase -ne 'Restored'){throw 'EXISTING_CONFIGURATION: restore first; live test will not disrupt it'}
 $before=Snapshot
 $steps+= [pscustomobject]@{Check='Baseline';Result='Captured adapters/DHCP/NRPT/DoH/suffix/hosts/proxy before writes'}
 $result=Enable-State $true $false;$enabled=$true;$ownedRules=(Read-State).Rules
 if($result.State -ne 'DNS active'){throw 'ENABLE_NOT_ACTIVE'}
 $steps+= [pscustomobject]@{Check='Enable';Result='PASS';Rules=$ownedRules;Servers=(Read-State).Servers}
 $current=Read-State;$first=$current | ConvertTo-Json -Depth 10 -Compress
 Enable-State $true $false | Out-Null
 if((Read-State | ConvertTo-Json -Depth 10 -Compress) -ne $first){throw 'ENABLE_NOT_IDEMPOTENT'}
 $steps+= [pscustomobject]@{Check='Enable twice';Result='PASS'}
 $diagnosis=Diagnose
 if($diagnosis.Status.State -ne 'DNS active' -or $diagnosis.NrptEffective -like 'NRPT_NOT_EFFECTIVE*'){throw 'NRPT_NOT_EFFECTIVE'}
 $steps+= [pscustomobject]@{Check='Windows resolver bypass cache';Result=$diagnosis.NrptEffective;Answers=$diagnosis.SystemResolver;DoH=$diagnosis.DoHEncrypted}
 Restore-State (Read-State) | Out-Null;$enabled=$false
 Restore-State (Read-State) | Out-Null
 $after=Snapshot
 if($before -ne $after){throw 'RESTORE_BASELINE_MISMATCH'}
 $steps+= [pscustomobject]@{Check='Restore twice + exact semantic baseline comparison';Result='PASS'}
 # Fault injection AFTER actual NRPT creation, exercising pending-GUID recovery.
 function Add-DnsClientNrptRule {
  param($Namespace,$NameServers,$DisplayName,$Comment,$NameEncoding,$DAProxyType,$DAIPsecEncryptionType,$DnsSecIPsecEncryptionType,$DAEnable,$DAIPsecRequired,$DnsSecEnable,$DnsSecValidationRequired,$DnsSecIPsecRequired,[switch]$PassThru,$ErrorAction)
  DnsClient\Add-DnsClientNrptRule -Namespace $Namespace -NameServers $NameServers -NameEncoding $NameEncoding -DAProxyType $DAProxyType -DAIPsecEncryptionType $DAIPsecEncryptionType -DnsSecIPsecEncryptionType $DnsSecIPsecEncryptionType -DAEnable:$false -DAIPsecRequired:$false -DnsSecEnable:$false -DnsSecValidationRequired:$false -DnsSecIPsecRequired:$false -DisplayName $DisplayName -Comment $Comment -PassThru -ErrorAction Stop | Out-Null
  throw 'LIVE_SIMULATED_FAILURE_AFTER_NRPT_CREATE'
 }
 $rolled=$false
 try {Enable-State $true $false | Out-Null}catch{if($_.Exception.Message -like '*APPLY_FAILED_ROLLED_BACK*'){$rolled=$true}else{throw}}
 if(!$rolled){throw 'LIVE_FAULT_DID_NOT_ROLL_BACK'}
 $after=Snapshot;if($before -ne $after){throw 'FAULT_RESTORE_BASELINE_MISMATCH'}
 $steps+= [pscustomobject]@{Check='Injected failure after real NRPT create + rollback';Result='PASS'}
}catch {$failure=$_.Exception.Message}
finally {
 if($before){
  try {$s=Read-State;if($s -and $s.Phase -ne 'Restored'){Restore-State $s | Out-Null};$after=Snapshot;if($before -ne $after){$failure=($failure+'; FINAL_BASELINE_MISMATCH')}}catch{$failure=($failure+'; FINAL_RESTORE_FAILED: '+$_.Exception.Message)}
 }
 if($opLock){$opLock.Dispose()}
}
[pscustomobject]@{State=$(if($failure){'Failed (restorable)'}else{'Not configured'});Code=$(if($failure){'LIVE_INTEGRATION_FAILED: '+$failure}else{'LIVE_INTEGRATION_PASS'});Steps=$steps;TestedRules=$ownedRules;BaselineRestored=($before -and $after -and $before -eq $after);Application='AUTH_REQUIRED_FOR_E2E_TEST';Transport='Direct RFC8484 DoH verified; Windows resolver on-wire encryption not independently captured'} | ConvertTo-Json -Depth 12
if($failure){exit 1}else{exit 0}
