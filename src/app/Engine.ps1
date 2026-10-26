[CmdletBinding()]
param(
 [ValidateSet('status','diagnose','prepare','enable','restore','lease-acquire','lease-release','lease-recover','lease-status')][string]$Action='status',
 [ValidateSet('status','enable','disable')][string]$Autostart,
 [ValidateSet('Registry','Task')][string]$AutostartMethod='Registry',
 [switch]$TrustProxy,[switch]$Plain,[switch]$LibraryOnly,[switch]$Repair,
 [string[]]$Servers,
 [string]$Template,
 [string]$ProfileName,
 [string]$ExePath,
 [string[]]$Targets,
 [string]$ExpectedRevision,
 [long]$ExpectedUntilMs,
 [string]$LeaseId,
 [string]$LeaseOwner,
 [long]$LeaseUntilMs
)
Set-StrictMode -Version 2
$ErrorActionPreference='Stop'
# The engine runs elevated. PowerShell loads DnsClient, NetAdapter and the security cmdlets by name from every folder of PSModulePath, and the first folders there are the
# user's own (Documents\WindowsPowerShell\Modules): a module a user-level program put there would run with administrator rights. Only the system's folder is searched.
# Set before the first cmdlet and without one (Join-Path itself would be autoloaded from the inherited path), from the system directory, not from an environment variable.
$env:PSModulePath=[IO.Path]::Combine([Environment]::SystemDirectory,'WindowsPowerShell\v1.0\Modules')
# test seam: a folder of fake modules, honoured only by a run WITHOUT administrator rights (such a run gains nothing from it; an elevated one never reads it)
if($env:AGDNS_TEST_PSMODULEPATH -and !([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){$env:PSModulePath=$env:AGDNS_TEST_PSMODULEPATH+';'+$env:PSModulePath}
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
# The protected domains and the default profile are defined only in profiles.json. The exe passes them as parameters;
# without parameters (library/test use, EmergencyRestore.ps1) the copy staged or exported next to this script is read.
$script:Catalog=$null
$catalogFile=Join-Path $PSScriptRoot 'profiles.json'
if(Test-Path -LiteralPath $catalogFile){try{$script:Catalog=Get-Content -LiteralPath $catalogFile -Raw -Encoding UTF8 | ConvertFrom-Json}catch{$script:Catalog=$null}}
$defaultProfile=$null
if($script:Catalog){$defaultProfile=@($script:Catalog.profiles | Where-Object {$_.id -eq $script:Catalog.default})[0]}
$defaultServers=@(if($defaultProfile){$defaultProfile.servers})
$defaultTemplate=$(if($defaultProfile){[string]$defaultProfile.template}else{''})
$parsedTargets=@(if($PSBoundParameters.ContainsKey('Targets') -and $Targets){@((@($Targets) -join ',') -split '[,;\s]+' | Where-Object {$_})})
$script:Targets=@(if($parsedTargets.Count -gt 0){$parsedTargets}elseif($script:Catalog){$script:Catalog.targets})
# The catalog's two host sets: the core domains and the optional Gemini set. A journal written for either of them is a recognised scope (see Test-KnownScope).
$script:CoreHosts=@(if($script:Catalog){$script:Catalog.targets})
$script:GeminiHosts=@(if($script:Catalog -and $script:Catalog.PSObject.Properties['geminiTargets']){$script:Catalog.geminiTargets})
# "+ Браузер": suffix namespaces (".google.com" = every name under google.com). They become NRPT rules; every check (DoH, TLS, Windows answers) runs on the exact hosts.
$script:GoogleNamespaces=@(if($script:Catalog -and $script:Catalog.PSObject.Properties['googleTargets']){$script:Catalog.googleTargets})
function Get-Hosts($List) {
 $hosts=@(@($List) | Where-Object {$_ -and !([string]$_).StartsWith('.')})
 # a scope made of suffixes only (just the browser) has no exact name: it is checked on the catalog's own hosts, which all lie under those suffixes
 if($hosts.Count -eq 0 -and @($List).Count -gt 0){return @(@($script:CoreHosts)+@($script:GeminiHosts) | Where-Object {$_})}
 return $hosts
}
$parsedServers=@(if($PSBoundParameters.ContainsKey('Servers') -and $Servers){
 @((@($Servers) -join ',') -split '[,;\s]+' | Where-Object {$_})
})
# A profile that names a template but no IPs (custom, name-only) must not borrow the default profile's servers.
$script:Servers=if(@($parsedServers).Length -gt 0){$parsedServers}elseif($PSBoundParameters.ContainsKey('Template') -and $Template){@()}else{$defaultServers}
$script:Template=if($PSBoundParameters.ContainsKey('Template') -and $Template){$Template}else{$defaultTemplate}
$script:DefaultResolver=$(if($defaultProfile){[string]$defaultProfile.id}else{''})
$script:ProfileName=if($PSBoundParameters.ContainsKey('ProfileName') -and $ProfileName){$ProfileName}else{''}
# -Repair (the tray repair button): an Active journal whose bridge no longer verifies is taken through the same refusals and the same compensated switch as a profile change.
$script:Repair=[bool]$Repair
$script:Root=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AntigravityDnsUnlocker'   # compatibility name, shared with older builds (docs/COMPATIBILITY-IDENTIFIERS.md); the known folder, as the exe does (an environment variable can be set by the user)
$script:StatePath=Join-Path $script:Root 'state.json'
# The exe passes its own (or, when elevated, its hash-verified protected copy's) path; the sibling lookup remains for library/test use.
$script:Exe=if($PSBoundParameters.ContainsKey('ExePath') -and $ExePath){$ExePath}else{Join-Path $PSScriptRoot 'AntigravityGeminiDns.exe'}
$script:RunPath='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Test-Admin {
 return ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Get-Rules { return @(Get-DnsClientNrptRule -ErrorAction Stop | Select-Object Name,Namespace,NameServers,Comment,DisplayName,DirectAccessDnsServers,DirectAccessEnabled,DirectAccessProxyName,DirectAccessProxyType,DirectAccessQueryIPsecEncryption,DirectAccessQueryIPsecRequired,DnsSecEnabled,DnsSecQueryIPsecEncryption,DnsSecQueryIPsecRequired,DnsSecValidationRequired,IPsecCARestriction,NameEncoding) }
function Get-Effective { return @(Get-DnsClientNrptPolicy -Effective -ErrorAction Stop | Select-Object Namespace,NameServers) }
function Get-Doh { return @(Get-DnsClientDohServerAddress -ErrorAction Stop | Select-Object ServerAddress,DohTemplate,AutoUpgrade,AllowFallbackToUdp) }
function Split-Servers($Value) { return @((@($Value) -join ',') -split '[,;\s]+' | Where-Object {$_}) }
function Same-Set($Left,$Right) { return ((@($Left | Sort-Object -Unique) -join ',') -eq (@($Right | Sort-Object -Unique) -join ',')) }
function Test-KnownScope($Targets) {
 # The host sets of one bridge this engine recognises: a non-empty union of whole catalog groups (core domains, Gemini set, Google suffixes). A part of a group (an old single-domain journal) or a foreign name is an old or foreign scope.
 if(@($script:CoreHosts).Count -lt 1){return $false}
 $list=@($Targets)
 if($list.Count -eq 0 -or @($list | Select-Object -Unique).Count -ne $list.Count){return $false}
 $all=@()
 foreach($group in @(@($script:CoreHosts),@($script:GeminiHosts),@($script:GoogleNamespaces))){
  if(@($group).Count -eq 0){continue}
  $present=@($group | Where-Object {$_ -in $list}).Count
  if($present -ne 0 -and $present -ne @($group).Count){return $false}
  $all+=@($group)
 }
 return (@($list | Where-Object {$_ -notin $all}).Count -eq 0)
}
function Get-GeminiArgs([string[]]$Hosts) {
 # The flags that make the exe's own target set contain these hosts: --gemini for the Gemini set, --browser for the Google suffixes, --no-core when no core host is present.
 $a=@()
 if(@($script:GeminiHosts).Count -gt 0 -and @($Hosts | Where-Object {$_ -in @($script:GeminiHosts)}).Count -gt 0){$a+='--gemini'}
 if(@($script:GoogleNamespaces).Count -gt 0 -and @($Hosts | Where-Object {$_ -in @($script:GoogleNamespaces)}).Count -gt 0){$a+='--browser'}
 if(@($Hosts | Where-Object {$_ -in @($script:CoreHosts)}).Count -eq 0 -and @($a).Count -gt 0){$a+='--no-core'}
 return $a
}
function Test-Overlap([string]$Namespace,[string]$Domain) {
 $ns=$Namespace.ToLowerInvariant();$domain=$Domain.ToLowerInvariant()
 # a suffix of ours (".google.com") overlaps every foreign rule for a name under it, and the bare name itself
 if($domain.StartsWith('.') -and ($ns.TrimStart('.') -eq $domain.Substring(1) -or ('.'+$ns.TrimStart('.')).EndsWith($domain))){return $true}
 return ($ns -eq '.' -or $ns -eq $domain -or ($ns.StartsWith('.') -and ($domain -eq $ns.Substring(1) -or $domain.EndsWith($ns))) -or $ns.StartsWith('*') -or $ns.Contains('/'))
}
function Get-Conflicts($Rules,$Owner,$Scope=$null) {
 $hosts=@(if($Scope){$Scope}else{$script:Targets})
 return @($Rules | Where-Object {
  $rule=$_; if($null -eq $rule){return $false}; $owned=($Owner -and $rule.PSObject.Properties['Comment'] -and $rule.Comment -eq ('AgDns:'+ $Owner) -and $rule.DisplayName -eq ('AgDns '+$Owner))
  !$owned -and @($hosts | Where-Object { $t=$_; @($rule.Namespace | Where-Object {Test-Overlap $_ $t}).Count -gt 0 }).Count -gt 0
 })
}
function Read-State {
 if(!(Test-Path -LiteralPath $script:StatePath)){return $null}
 try {
  $s=$null;for($try=0;$try -lt 3 -and !$s;$try++){try{$s=Get-Content -LiteralPath $script:StatePath -Raw -Encoding UTF8 | ConvertFrom-Json}catch{if($try -ge 2){throw};Start-Sleep -Milliseconds 150}}
  if($s.Schema -notin @(1,2)){throw 'STATE_VERSION_UNSUPPORTED'}
  if($s.Schema -eq 1){$s | Add-Member -NotePropertyName LeaseVersion -NotePropertyValue 1 -Force;$s | Add-Member -NotePropertyName LeaseManaged -NotePropertyValue $false -Force;$s | Add-Member -NotePropertyName Leases -NotePropertyValue @() -Force}
  if($s.LeaseVersion -ne 1 -or $s.LeaseManaged -isnot [bool] -or !$s.PSObject.Properties['Leases'] -or @($s.Leases).Count -gt 128){throw 'lease schema'}
  $ids=@();foreach($lease in @($s.Leases)){
   if($lease.Id -notmatch '^[a-f0-9]{32}$' -or $lease.Owner -notmatch '^[A-Za-z0-9:._-]{1,192}$' -or $lease.ExpiresUtcMs -isnot [long] -and $lease.ExpiresUtcMs -isnot [int] -or $lease.ExpiresUtcMs -le 0 -or $lease.Policy -ne 'RestoreWhenUnreferenced' -or $lease.Id -in $ids){throw 'lease'}
   $ids+= $lease.Id
  }
  if(!$s.LeaseManaged -and @($s.Leases).Count -gt 0){throw 'unmanaged leases'}
  if($s.Schema -notin @(1,2) -or $s.Owner -notmatch '^[a-f0-9]{32}$' -or $s.Phase -notin @('Applying','Active','Restoring','Restored','Failed') -or $s.Protocol -notin @('DoH','Plain DNS')){throw 'schema'}
  # the scope of the journal (which hosts) is judged by the callers that need it (RESTORE_BEFORE_SCOPE_UPGRADE): a restore must work for any scope the journal itself describes consistently
  if(@($s.Servers).Count -lt 1 -or @($s.Targets).Count -lt 1){throw 'scope'}
  foreach($srv in @($s.Servers)){
   $ipTest=$null;if(![System.Net.IPAddress]::TryParse([string]$srv,[ref]$ipTest)){throw 'scope'}
  }
  if(@($s.Rules).Count -ne @($s.Targets).Count -or !(Same-Set @($s.Rules | ForEach-Object {$_.Namespace}) $s.Targets)){throw 'rules scope'}
  if(($s.Protocol -eq 'DoH' -and !(Same-Set @($s.Doh | ForEach-Object {$_.Server}) $s.Servers)) -or ($s.Protocol -eq 'Plain DNS' -and @($s.Doh).Count -ne 0)){throw 'doh scope'}
  foreach($r in @($s.Rules)) {
   if($r.Namespace -notin @($s.Targets) -or ($r.Name -and $r.Name -notmatch '^\{?[a-fA-F0-9-]{36}\}?$') -or ($r.Fingerprint -and ($r.Fingerprint -isnot [string] -or $r.Fingerprint.Length -gt 8192))){throw 'rules'}
  }
  $expectedTmpl = if($s.PSObject.Properties['Template'] -and $s.Template){$s.Template}else{$script:Template}
  foreach($d in @($s.Doh)) {
   if($d.Server -notin $s.Servers -or $d.Template -ne $expectedTmpl -or $d.Created -isnot [bool] -or $d.Ownership -notin @('Planned','Pending','Owned','Shared','Relinquished')){throw 'doh'}
  }
  return $s
 }catch {throw ('STATE_INVALID: journal unreadable ('+$_.Exception.Message+'); no system settings changed. Preserve state.json for recovery.')}
}
# SYSTEM and Administrators: full control; Users: read and execute; nothing inherited from the parent.
function New-ProtectedDirectoryAcl {
 $acl=New-Object Security.AccessControl.DirectorySecurity
 $acl.SetAccessRuleProtection($true,$false)
 foreach($sid in @('S-1-5-18','S-1-5-32-544')) {
  $rule=New-Object Security.AccessControl.FileSystemAccessRule ([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'); $acl.AddAccessRule($rule)
 }
 $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule ([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))
 return $acl
}
# Created with those rules in the same call: the directory never exists with the parent's (user-writable) inherited rights.
function New-ProtectedDirectory([string]$Path) { [IO.Directory]::CreateDirectory($Path,(New-ProtectedDirectoryAcl)) | Out-Null }
function Initialize-StateDirectory {
 if(!(Test-Path -LiteralPath $script:Root)){New-ProtectedDirectory $script:Root}
 $item=Get-Item -LiteralPath $script:Root
 if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'STATE_PATH_REPARSE_POINT'}
 $ownerSid=(Get-Acl -LiteralPath $script:Root).GetOwner([Security.Principal.SecurityIdentifier]).Value
 if($ownerSid -notin @('S-1-5-18','S-1-5-32-544')){throw 'STATE_DIR_UNTRUSTED: directory owner is not SYSTEM/Administrators; remove it manually'}
 $acl=New-ProtectedDirectoryAcl
 $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));Set-Acl -LiteralPath $script:Root -AclObject $acl
}
# No external input enables fault injection. Tests redefine this function inside their own library process.
function Invoke-EngineCheckpoint([string]$Phase) {}
function Write-JournalBytes([string]$Path,[byte[]]$Bytes) {
 $stream=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
 try{$stream.Write($Bytes,0,$Bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
}
function Save-State($State) {
 $State.UpdatedUtc=[DateTime]::UtcNow.ToString('o')
 $temp=Join-Path $script:Root ('state-'+[guid]::NewGuid().ToString('N')+'.tmp')
 # Only complete, flushed bytes can replace the last recovery authority. Any write/flush failure leaves state.json intact.
 if($State.Schema -eq 1){
  $State | Add-Member -NotePropertyName LeaseVersion -NotePropertyValue 1 -Force
  $State | Add-Member -NotePropertyName LeaseManaged -NotePropertyValue $false -Force
  $State | Add-Member -NotePropertyName Leases -NotePropertyValue @() -Force
  $State.Schema=2
 }
 $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($State | ConvertTo-Json -Depth 10))
 Write-JournalBytes $temp $bytes
 Invoke-EngineCheckpoint 'JournalDurableBeforeReplace'
 if(Test-Path -LiteralPath $script:StatePath){[IO.File]::Replace($temp,$script:StatePath,(Join-Path $script:Root 'state.previous.json'))}else{[IO.File]::Move($temp,$script:StatePath)}
}
function Get-Network {
 if(@($script:Servers).Count -lt 1){throw 'NO_ADDRESSES_FOR_PROFILE: no server IPs are known for this profile; the exe finds them by bootstrap before connecting'}
 $args=@('--network')+@(Get-GeminiArgs $script:Targets)
 if($script:Servers -and $script:Servers.Count -gt 0){
  $args+= '--servers'
  $args+= ($script:Servers -join ',')
 }
 if($script:Template){
  $args+= '--template'
  $args+= $script:Template
 }
 $raw=& $script:Exe @args
 if($LASTEXITCODE -ne 0){throw 'NETWORK_PROBE_FAILED'}
 return ($raw | ConvertFrom-Json)
}
# Answers of the Windows resolver, fetched for every host of the current check in one exe start (each start costs ~0.2 s, each query ~0.3 s; the exe runs them in parallel).
# The cache lives for one check only: callers reset it before a loop, and every entry is used once, so a later re-check always asks Windows again.
$script:SystemAnswerCache=@{};$script:SystemAnswerTypes=@('A');$script:SystemAnswerExtra=@()
function Reset-SystemAnswers([string[]]$Types=@('A'),[string[]]$Extra=@()) { $script:SystemAnswerCache=@{};$script:SystemAnswerTypes=@($Types);$script:SystemAnswerExtra=@($Extra) }
function Get-SystemAnswers([string]$Domain,[string]$Type='A') {
 $key=$Domain+'|'+$Type
 if(!$script:SystemAnswerCache.ContainsKey($key)){
  try {
   $hosts=@(@(Get-Hosts $script:Targets)+@($script:SystemAnswerExtra)+@($Domain) | Where-Object {$_} | Select-Object -Unique)
   $types=@(@($script:SystemAnswerTypes)+@($Type) | Select-Object -Unique)
   $ga=@(Get-GeminiArgs $hosts)
   $raw=& $script:Exe --system-dns-batch ($hosts -join ',') ($types -join ',') @ga
   if($LASTEXITCODE -eq 0){foreach($r in ($raw | ConvertFrom-Json)){$script:SystemAnswerCache[([string]$r.Host+'|'+$(if([int]$r.Type -eq 28){'AAAA'}else{'A'}))]=@($r.Addresses)}}
  }catch{}
 }
 if($script:SystemAnswerCache.ContainsKey($key)){$v=@($script:SystemAnswerCache[$key]);[void]$script:SystemAnswerCache.Remove($key);return $v}
 try { $ga=@(Get-GeminiArgs @($Domain));$raw=& $script:Exe --system-dns $Domain $Type @ga;if($LASTEXITCODE -ne 0){return @()};$reply=$raw | ConvertFrom-Json;return @($reply.Addresses) }catch{return @()}
}
function Test-HostsConflict($Scope=$null,[string]$HostsFile=(Join-Path ([Environment]::SystemDirectory) 'drivers\etc\hosts')) {
 if(!(Test-Path -LiteralPath $hostsFile)){return $false}   # some cleaners delete it: nothing to conflict with
 $lines=Get-Content -LiteralPath $hostsFile -ErrorAction Stop
 foreach($line in $lines) { $names=@(($line -split '#')[0].Trim() -split '\s+' | Select-Object -Skip 1);foreach($t in @(if($Scope){$Scope}else{$script:Targets})){if($t -in $names -or ($t.StartsWith('.') -and @($names | Where-Object {$_ -and ($_.ToLowerInvariant() -eq $t.Substring(1) -or $_.ToLowerInvariant().EndsWith($t))}).Count -gt 0)){return $true}} }
 return $false
}
function Get-Status {
 $s=Read-State; $rules=@(Get-Rules); $effective=@(Get-Effective)
 # Conflicts are looked for over the hosts of the bridge that exists, not of the scope this run asks for (a pending change of scope is checked by Enable-State).
 $scopeHosts=@(if($s -and $s.Phase -ne 'Restored'){$s.Targets}else{$script:Targets})
 $conflicts=@(Get-Conflicts (@($rules)+@($effective)) $(if($s){$s.Owner}else{''}) $scopeHosts)
 # Effective policies have no ownership field; remove only policies matching our journal.
 if($s){$conflicts=@($conflicts | Where-Object { $policy=$_; @($s.Targets | Where-Object {Same-Set $policy.Namespace @($_)}).Count -ne 1 -or !(Same-Set (Split-Servers $policy.NameServers) $s.Servers) -or $policy.PSObject.Properties['Name'] })}
 $active=$false;$state='Not configured';$code='NOT_CONFIGURED'
 if($s -and $s.Phase -ne 'Restored'){
  $owned=@($rules | Where-Object {$_.Comment -eq ('AgDns:'+$s.Owner)})
  $active=($s.Phase -eq 'Active' -and @($s.Targets | Where-Object {$t=$_;@($owned | Where-Object {Same-Set $_.Namespace @($t)}).Count -eq 1}).Count -eq $s.Targets.Count)
  $state='Failed (restorable)';$code='RECOVERY_REQUIRED'
  if($active -and $s.Phase -eq 'Active'){
   try {
    if(!(Same-Set $s.Targets $script:Targets) -and !(Test-KnownScope $s.Targets)){throw 'RESTORE_BEFORE_SCOPE_UPGRADE'}
    foreach($intent in $s.Rules){$r=@($owned | Where-Object {$_.Name -eq $intent.Name});if($r.Count -ne 1){throw 'RECOVERY_REQUIRED'};Assert-OwnedRule $r[0] $s $intent.Namespace}
    $dohTmpl = if($s.PSObject.Properties['Template'] -and $s.Template){$s.Template}else{$script:Template}
    if($s.Protocol -eq 'DoH'){$dohNow=@(Get-Doh);foreach($d in $s.Doh){$registration=@($dohNow | Where-Object {$_.ServerAddress -eq $d.Server});if($registration.Count -ne 1 -or $registration[0].DohTemplate -ne $dohTmpl -or !$registration[0].AutoUpgrade -or $registration[0].AllowFallbackToUdp){throw 'DOH_REGISTRATION_CONFLICT'}}}
    foreach($t in $s.Targets){$policy=@($effective | Where-Object {Same-Set $_.Namespace @($t)} | Where-Object {Same-Set (Split-Servers $_.NameServers) $s.Servers});if($policy.Count -ne 1){throw 'NRPT_NOT_EFFECTIVE'}}
    $state='DNS active';$code='DNS_CONFIGURED_APPLICATION_NOT_TESTED'
   }catch{$active=$false;$code=$_.Exception.Message}
  }
 }
 if($conflicts.Count -gt 0 -or (Test-HostsConflict $scopeHosts)){$active=$false;$state='Conflict';$code='NRPT_OR_HOSTS_CONFLICT'}
 $resolver=$script:DefaultResolver
 if($s -and $s.PSObject.Properties['ProfileName'] -and $s.ProfileName){$resolver=$s.ProfileName}
 elseif($s -and $s.PSObject.Properties['Template'] -and $s.Template){$resolver=$s.Template}
 [pscustomobject]@{State=$state;Code=$code;Resolver=$resolver;ProfileName=$(if($s -and $s.PSObject.Properties['ProfileName']){$s.ProfileName}else{''});Template=$(if($s -and $s.PSObject.Properties['Template']){$s.Template}else{$script:Template});Protocol=$(if($s -and $s.Phase -ne 'Restored'){$s.Protocol}else{'Not configured'});Targets=@(if($s -and $s.Phase -ne 'Restored'){$s.Targets}else{$script:Targets});Servers=@(if($s -and $s.Phase -ne 'Restored'){$s.Servers});Rules=@(if($s){$s.Rules});JournalPhase=$(if($s){$s.Phase}else{'Absent'});Conflicts=@($conflicts | Select-Object Namespace,NameServers);TransportProof='Not verified on wire';Application='AUTH_REQUIRED_FOR_E2E_TEST';Active=$active}
}
function Diagnose {
 $status=Get-Status
 if($status.Servers -and $status.Servers.Count -gt 0){$script:Servers=@($status.Servers)}
 if($status.Template){$script:Template=$status.Template}
 if(@($status.Targets).Count -gt 0){$script:Targets=@($status.Targets)}
 $network=Get-Network;$effective=@(Get-Effective);$s=Read-State
 Reset-SystemAnswers @('A','AAAA') @('www.microsoft.com')
 $answers=@(foreach($t in (Get-Hosts $script:Targets)){[pscustomobject]@{Host=$t;A=@(Get-SystemAnswers $t 'A');AAAA=@(Get-SystemAnswers $t 'AAAA')}})
 $nrpt='Not configured'
 if($status.Active){
  $policyOk=(@($script:Targets | Where-Object {$t=$_;@($effective | Where-Object {Same-Set $_.Namespace @($t)} | Where-Object {Same-Set (Split-Servers $_.NameServers) $s.Servers}).Count -eq 1}).Count -eq $script:Targets.Count)
  $answerOk=@($answers | Where-Object {$entry=$_;$t=$entry.Host;$expected=@($network.Candidates | Where-Object {$_.Server -in $s.Servers} | ForEach-Object {$_.TargetAnswers.PSObject.Properties[$t].Value});$entry.A.Count -lt 1 -or @($entry.A | Where-Object {$_ -notin $expected}).Count -gt 0}).Count -eq 0
  $nrpt=if($policyOk -and $answerOk){'Policy + Windows resolver answers match (wire destination unverified)'}else{'NRPT_NOT_EFFECTIVE'}
  if(!$policyOk -or !$answerOk){$status.Active=$false;$status.State='Failed (restorable)';$status.Code='NRPT_NOT_EFFECTIVE'}
 }
 [pscustomobject]@{State=$status.State;Code=$status.Code;Status=$status;Network=$network;SystemResolver=$answers;NrptEffective=$nrpt;DoHEncrypted='Direct probe: see Network; Windows resolver wire encryption: NOT VERIFIED';ControlAnswers=@(Get-SystemAnswers 'www.microsoft.com');VpnPresent=@(Get-NetAdapter | Where-Object {$_.Status -eq 'Up' -and $_.InterfaceDescription -match 'VPN|Tunnel|tun'} | Select-Object -ExpandProperty InterfaceDescription);Application='AUTH_REQUIRED_FOR_E2E_TEST';Note='Provider uses DNS + third-party SNI proxy. TLS verification stays enabled. DNS answers do not prove account eligibility or model response.'}
}
function Assert-Capabilities([bool]$Encrypted) {
 foreach($cmd in @('Get-DnsClientNrptRule','Get-DnsClientNrptPolicy','Add-DnsClientNrptRule','Remove-DnsClientNrptRule')){if(!(Get-Command $cmd -ErrorAction SilentlyContinue)){throw 'NRPT_UNSUPPORTED_BY_OS'}}
 if($Encrypted){foreach($cmd in @('Get-DnsClientDohServerAddress','Add-DnsClientDohServerAddress','Remove-DnsClientDohServerAddress')){if(!(Get-Command $cmd -ErrorAction SilentlyContinue)){throw 'DOH_UNSUPPORTED_BY_OS'}}}
}
function Assert-OwnedRule($Rule,$State,$Namespace) {
 if($Rule.Comment -ne ('AgDns:'+$State.Owner) -or $Rule.DisplayName -ne ('AgDns '+$State.Owner) -or !(Same-Set $Rule.Namespace @($Namespace)) -or !(Same-Set (Split-Servers $Rule.NameServers) $State.Servers)){throw 'OWNED_RULE_CHANGED: retained, no automatic overwrite'}
 $intent=@($State.Rules | Where-Object {$_.Namespace -eq $Namespace})[0]
 $expected=$intent.Fingerprint
 if(!$expected){$expected=Rule-Fingerprint ([pscustomobject]@{Comment=('AgDns:'+$State.Owner);DisplayName=('AgDns '+$State.Owner);Namespace=@($Namespace);NameServers=$State.Servers;NameEncoding='Disable'})}
 if((Rule-Fingerprint $Rule) -ne $expected){throw 'OWNED_RULE_CHANGED: security/encoding/auxiliary policy edited; retained'}
}
function Rule-Fingerprint($Rule) {
 $data=[ordered]@{}
 foreach($name in @('Comment','DisplayName','Namespace','NameServers','DirectAccessDnsServers','DirectAccessEnabled','DirectAccessProxyName','DirectAccessProxyType','DirectAccessQueryIPsecEncryption','DirectAccessQueryIPsecRequired','DnsSecEnabled','DnsSecQueryIPsecEncryption','DnsSecQueryIPsecRequired','DnsSecValidationRequired','IPsecCARestriction','NameEncoding')){
  $value=$null;if($Rule.PSObject.Properties[$name]){$value=$Rule.$name}
  if($name -in @('Namespace','NameServers','DirectAccessDnsServers')){$data[$name]=@((Split-Servers $value) | ForEach-Object {$_.ToLowerInvariant()} | Sort-Object -Unique)}
  elseif($name -in @('DirectAccessEnabled','DirectAccessQueryIPsecRequired','DnsSecEnabled','DnsSecQueryIPsecRequired','DnsSecValidationRequired')){$data[$name]=[bool]$value}
  elseif($name -in @('Comment','DisplayName')){$data[$name]=[string]$value}
  else{$data[$name]=([string]$value).ToLowerInvariant()}
 }
 return ($data | ConvertTo-Json -Depth 4 -Compress)
}
function Assisted-Hash([string]$Text) {
 $hash=[Security.Cryptography.SHA256]::Create()
 try{return ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-','')}finally{$hash.Dispose()}
}
# Read-only finite preimage. ExpectedRevision is checked under the existing mutation lock,
# again after provider preflight and immediately before the first native journal/write phase.
function Get-AssistedContext {
 $state=Read-State;$rules=@(Get-Rules);$effective=@(Get-Effective);$doh=@(Get-Doh)
 if($rules.Count -gt 256 -or $effective.Count -gt 256 -or $doh.Count -gt 256){throw 'ASSISTED_SNAPSHOT_LIMIT'}
 $rows=@(foreach($r in $rules){
  $owned=$false;$expected=''
  if($state -and $state.Phase -ne 'Restored'){
   $intent=@($state.Rules | Where-Object {$_.Name -eq $r.Name})
   if($intent.Count -eq 1){try{Assert-OwnedRule $r $state $intent[0].Namespace;$owned=$true;$expected=[string]$intent[0].Fingerprint;if(!$expected){$expected=Rule-Fingerprint $r}}catch{}}
  }
  [pscustomobject]@{Name=[string]$r.Name;Namespaces=@($r.Namespace | Sort-Object);Servers=@((Split-Servers $r.NameServers) | Sort-Object);Owned=$owned;Fingerprint=(Rule-Fingerprint $r);ExpectedFingerprint=$expected}
 }) | Sort-Object Name
 $rows=@($rows)
 $eff=@($effective | ForEach-Object {[pscustomobject]@{Namespaces=@($_.Namespace | Sort-Object);Servers=@((Split-Servers $_.NameServers) | Sort-Object)}} | Sort-Object {($_.Namespaces -join ',')+':'+($_.Servers -join ',')})
 $registrations=@($doh | ForEach-Object {[pscustomobject]@{Server=[string]$_.ServerAddress;Template=[string]$_.DohTemplate;Auto=[bool]$_.AutoUpgrade;Fallback=[bool]$_.AllowFallbackToUdp}} | Sort-Object Server)
 $network=@([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Sort-Object Id | ForEach-Object {
  $p=$_.GetIPProperties();$_.Id+':'+$_.OperationalStatus+':'+(@($p.UnicastAddresses | ForEach-Object {$_.Address.ToString()} | Sort-Object) -join ',')+':'+(@($p.DnsAddresses | ForEach-Object {$_.ToString()} | Sort-Object) -join ',')+':'+(@($p.GatewayAddresses | ForEach-Object {$_.Address.ToString()} | Sort-Object) -join ',')
 })
 $journal=if(Test-Path -LiteralPath $script:StatePath){Get-Content -LiteralPath $script:StatePath -Raw -Encoding UTF8}else{'absent'}
 $conflict=@($rows | Where-Object {!$_.Owned -and @($_.Namespaces | Where-Object {$n=$_;@($script:Targets | Where-Object {Test-Overlap $n $_}).Count -gt 0}).Count -gt 0}).Count -gt 0
 foreach($e in $eff){foreach($n in $e.Namespaces){
  $matches=@($script:Targets | Where-Object {Test-Overlap $n $_}).Count -gt 0
  # Interrupted phases can retain exact owned policy. Only rows already checked by
  # Assert-OwnedRule above can explain effective policy; the journal phase alone cannot.
  $ours=$state -and ($n -in $state.Targets) -and (Same-Set $e.Servers $state.Servers) -and @($rows | Where-Object {$_.Owned -and (Same-Set @($n) $_.Namespaces) -and (Same-Set $e.Servers $_.Servers)}).Count -eq 1
  if($matches -and !$ours){$conflict=$true}
 }}
 if(Test-HostsConflict){$conflict=$true}
 $data=[ordered]@{Journal=(Assisted-Hash $journal);Rules=$rows;Effective=$eff;Doh=$registrations;Network=$network;ForeignConflict=$conflict}
 $text=$data | ConvertTo-Json -Depth 12 -Compress;if($text.Length -gt 262144){throw 'ASSISTED_SNAPSHOT_LIMIT'}
 $semantic=@(foreach($r in $rows){$fp=$r.Fingerprint | ConvertFrom-Json;if($r.Owned){$fp.Comment='';$fp.DisplayName=''};[pscustomobject]@{Name=$(if($r.Owned){''}else{$r.Name});Policy=$fp}}) | Sort-Object {($_.Policy.Namespace -join ',')+':'+$_.Name}
 $semantic=@($semantic)
 $phase=if($state){$state.Phase}else{'Absent'}
 $configuration=if($state -and $state.Phase -ne 'Restored'){[ordered]@{Targets=@($state.Targets | Sort-Object);Servers=@($state.Servers | Sort-Object);Template=[string]$state.Template;Protocol=[string]$state.Protocol}}else{'off'}
 $baseline=Assisted-Hash (([ordered]@{Configuration=$configuration;Rules=$semantic;Effective=$eff;Doh=$registrations} | ConvertTo-Json -Depth 12 -Compress))
 [pscustomobject]@{Revision=(Assisted-Hash $text);Baseline=$baseline;Phase=$phase;ForeignConflict=$conflict;Status=(Get-Status);Journal=$state;Rules=$rows;Network=(Assisted-Hash ($network -join '|'))}
}
function Assert-AssistedContext {
 if(!$script:ExpectedRevision){return}
 if($script:ExpectedRevision -notmatch '^[A-F0-9]{64}$'){throw 'ASSISTED_PREVIEW_INVALID'}
 $now=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
 if($script:ExpectedUntilMs -le $now -or $script:ExpectedUntilMs -gt ($now+300000)){throw 'ASSISTED_PREVIEW_EXPIRED'}
 if((Get-AssistedContext).Revision -cne $script:ExpectedRevision){throw 'ASSISTED_PREVIEW_STALE: system context changed; no native operation started'}
}
function Restore-State($s) {
 if(!$s -or $s.Phase -eq 'Restored'){return [pscustomobject]@{State='Not configured';Code='ROLLED_BACK';Result='No-op'}}
 # Refuse independent edits before removing any other owned policy. This also applies to direct recovery, not just profile switching.
 $s.Phase='Restoring';Save-State $s;Invoke-EngineCheckpoint 'RestoringJournal'
 Assert-RestorableJournal $s
 foreach($intent in @($s.Rules)) {
  $rules=@(Get-Rules)
  $candidates=@($rules | Where-Object {($intent.Name -and $_.Name -eq $intent.Name) -or (!$intent.Name -and $_.Comment -eq ('AgDns:'+$s.Owner) -and (Same-Set $_.Namespace @($intent.Namespace)))})
  foreach($r in $candidates){Assert-OwnedRule $r $s $intent.Namespace; Invoke-EngineCheckpoint 'BeforeRemoveRule';Remove-DnsClientNrptRule -Name $r.Name -Force -ErrorAction Stop | Out-Null;Invoke-EngineCheckpoint 'AfterRemoveRule'}
 }
 if(@(Get-Rules | Where-Object {$_.Comment -eq ('AgDns:'+$s.Owner)}).Count -gt 0){throw 'OWNED_RULES_REMAIN'}
 $retained=@();$ambiguous=@()
 foreach($d in @($s.Doh | Where-Object {$_.Created})) {
  $current=@(Get-Doh | Where-Object {$_.ServerAddress -eq $d.Server})
  if($current.Count -eq 0){continue}
  if($d.Ownership -in @('Planned','Shared','Relinquished')){continue}
  if($d.Ownership -eq 'Pending'){$ambiguous+= $d.Server;continue}
  if($current.Count -ne 1 -or $current[0].DohTemplate -ne $d.Template -or !$current[0].AutoUpgrade -or $current[0].AllowFallbackToUdp){throw 'DOH_CHANGED_INDEPENDENTLY: registration retained'}
  $refs=@(Get-Rules | Where-Object {$d.Server -in (Split-Servers $_.NameServers)})+@(Get-Effective | Where-Object {$d.Server -in (Split-Servers $_.NameServers)})+@(Get-DnsClientServerAddress | Where-Object {$d.Server -in $_.ServerAddresses})
  if($refs.Count -gt 0){$retained+= $d.Server;$d.Ownership='Relinquished';Save-State $s;continue}
  Invoke-EngineCheckpoint 'BeforeRemoveDoh'
  Remove-DnsClientDohServerAddress -ServerAddress $d.Server -ErrorAction Stop | Out-Null
  Invoke-EngineCheckpoint 'AfterRemoveDoh'
  if(@(Get-Doh | Where-Object {$_.ServerAddress -eq $d.Server}).Count -gt 0){throw 'DOH_RESTORE_FAILED'}
 }
 if($ambiguous.Count -gt 0){throw ('DOH_OWNERSHIP_UNCERTAIN: pending registration retained for administrator review: '+($ambiguous -join ','))}
 # Managed completion includes cache invalidation before the durable Restored commit.
 if($s.LeaseManaged -and !(Clear-DnsResolverCache)){throw 'DNS_CACHE_FLUSH_UNCONFIRMED: native policy removed; journal retained for recovery retry'}
 $s.Phase='Restored';$s.Leases=@();Save-State $s;Invoke-EngineCheckpoint 'RestoredJournal'
 $result=[pscustomobject]@{State='Not configured';Code=$(if($retained.Count -gt 0){'ROLLED_BACK_SHARED_DOH_RETAINED'}else{'ROLLED_BACK'});Result='Owned policies removed; adapters/hosts/proxy unchanged';RetainedSharedDoh=$retained}
 if($s.LeaseManaged){$result | Add-Member -NotePropertyName DnsCacheFlushed -NotePropertyValue $true}
 return $result
}
function Assert-DohTemplate([string]$Value) {
 $u=$null;$ip=$null
 if(![uri]::TryCreate($Value,[UriKind]::Absolute,[ref]$u) -or $u.Scheme -ne 'https' -or $u.Port -lt 1 -or $u.Port -gt 65535 -or [Net.IPAddress]::TryParse($u.IdnHost,[ref]$ip)){throw 'DOH_TEMPLATE_INVALID: absolute https:// URL with a host name required (explicit port such as :8443 is passed through unchanged)'}
}
function Test-ForeignConflict($State) {
 # Foreign NRPT rules / effective policies / hosts entries that overlap the protected domains. Our own journaled rules are not conflicts.
 $owner=if($State){$State.Owner}else{''}
 $scope=@($script:Targets)+@(if($State){$State.Targets})   # the new scope and the scope of the bridge that is about to be replaced
 $rules=@(Get-Conflicts @(Get-Rules) $owner $scope)
 $policies=@(Get-Conflicts @(Get-Effective) $owner $scope)
 if($State){$policies=@($policies | Where-Object { $policy=$_; @($State.Targets | Where-Object {Same-Set $policy.Namespace @($_)}).Count -ne 1 -or !(Same-Set (Split-Servers $policy.NameServers) $State.Servers) -or $policy.PSObject.Properties['Name'] })}
 return ($rules.Count -gt 0 -or $policies.Count -gt 0 -or (Test-HostsConflict $scope))
}
function Test-DohRegistrationConflict($Servers,$IgnoreServers) {
 # A registration that exists for a selected IP with another template / fallback settings cannot be adopted. Registrations the old journal owns will be removed first.
 $doh=@(Get-Doh)
 foreach($ip in $Servers){
  if($ip -in @($IgnoreServers)){continue}
  $old=@($doh | Where-Object {$_.ServerAddress -eq $ip})
  if($old.Count -gt 0 -and ($old.Count -ne 1 -or $old[0].DohTemplate -ne $script:Template -or !$old[0].AutoUpgrade -or $old[0].AllowFallbackToUdp)){return $true}
 }
 return $false
}
function Assert-RestorableJournal($State) {
 # Dry run of the refusals Restore-State can raise (an owned rule or DoH registration edited by someone else), so a profile switch learns about them
 # while the working bridge is still whole. Without it a refusal on the second rule came after the first one was already removed.
 foreach($intent in @($State.Rules)) {
  $candidates=@(Get-Rules | Where-Object {($intent.Name -and $_.Name -eq $intent.Name) -or (!$intent.Name -and $_.Comment -eq ('AgDns:'+$State.Owner) -and (Same-Set $_.Namespace @($intent.Namespace)))})
  foreach($r in $candidates){Assert-OwnedRule $r $State $intent.Namespace}
 }
 foreach($d in @($State.Doh | Where-Object {$_.Created -and $_.Ownership -eq 'Owned'})) {
  $current=@(Get-Doh | Where-Object {$_.ServerAddress -eq $d.Server})
  if($current.Count -gt 0 -and ($current.Count -ne 1 -or $current[0].DohTemplate -ne $d.Template -or !$current[0].AutoUpgrade -or $current[0].AllowFallbackToUdp)){throw 'DOH_CHANGED_INDEPENDENTLY: registration retained'}
 }
}
# ipconfig.exe, not the DnsClient cmdlets: a plain exe is bounded and does not depend on the CIM/WMI layer. A failed flush is reported, never fatal.
function Clear-DnsResolverCache {
 try { & (Join-Path ([Environment]::SystemDirectory) 'ipconfig.exe') /flushdns | Out-Null; return ($LASTEXITCODE -eq 0) } catch { return $false }
}
function Apply-State($selected,$net,[bool]$Unencrypted,$InitialLease=$null) {
 if(Test-ForeignConflict $null){throw 'NRPT_CONFLICT'}
 $doh=@(if(!$Unencrypted){Get-Doh})
 if(!$Unencrypted -and (Test-DohRegistrationConflict $selected @())){throw 'DOH_REGISTRATION_CONFLICT'}
 Initialize-StateDirectory
 $owner=[guid]::NewGuid().ToString('N')
 $s=[pscustomobject]@{Schema=2;LeaseVersion=1;LeaseManaged=($null -ne $InitialLease);Leases=@(if($InitialLease){$InitialLease});Owner=$owner;Phase='Applying';ProfileName=$script:ProfileName;Template=$script:Template;Protocol=$(if($Unencrypted){'Plain DNS'}else{'DoH'});Targets=$script:Targets;Servers=$selected;Rules=@(foreach($t in $script:Targets){[pscustomobject]@{Namespace=$t;Name=$null;Fingerprint=$null}});Doh=@(if(!$Unencrypted){foreach($ip in $selected){$new=(@($doh | Where-Object {$_.ServerAddress -eq $ip}).Count -eq 0);[pscustomobject]@{Server=$ip;Template=$script:Template;Created=$new;Ownership=$(if($new){'Planned'}else{'Shared'})}}});UpdatedUtc=''}
 Save-State $s
 try {
  Invoke-EngineCheckpoint 'ApplyingJournal'
  foreach($d in @($s.Doh | Where-Object {$_.Created})) {
   $d.Ownership='Pending';Save-State $s;Invoke-EngineCheckpoint 'PendingDohJournal'
   Add-DnsClientDohServerAddress -ServerAddress $d.Server -DohTemplate $d.Template -AllowFallbackToUdp $false -AutoUpgrade $true -ErrorAction Stop | Out-Null
   Invoke-EngineCheckpoint 'AfterAddDoh'
   $check=@(Get-Doh | Where-Object {$_.ServerAddress -eq $d.Server});if($check.Count -ne 1 -or $check[0].DohTemplate -ne $d.Template -or !$check[0].AutoUpgrade -or $check[0].AllowFallbackToUdp){throw 'DOH_ADD_VERIFICATION_FAILED'}
   $d.Ownership='Owned';Save-State $s;Invoke-EngineCheckpoint 'OwnedDohJournal'
  }
  foreach($intent in $s.Rules) {
   Invoke-EngineCheckpoint 'BeforeAddRule'
   $r=Add-DnsClientNrptRule -Namespace $intent.Namespace -NameServers $s.Servers -NameEncoding Disable -DAProxyType '' -DAIPsecEncryptionType '' -DnsSecIPsecEncryptionType '' -DAEnable:$false -DAIPsecRequired:$false -DnsSecEnable:$false -DnsSecValidationRequired:$false -DnsSecIPsecRequired:$false -DisplayName ('AgDns '+$owner) -Comment ('AgDns:'+$owner) -PassThru -ErrorAction Stop
   Invoke-EngineCheckpoint 'AfterAddRule'
   $intent.Name=[string]$r.Name;$intent.Fingerprint=Rule-Fingerprint $r;Save-State $s;Invoke-EngineCheckpoint 'OwnedRuleJournal'
   Assert-OwnedRule $r $s $intent.Namespace
  }
  Invoke-EngineCheckpoint 'BeforeEffectiveVerification'
  $effective=@(Get-Effective)
  foreach($t in $s.Targets){$match=@($effective | Where-Object {Same-Set $_.Namespace @($t)} | Where-Object {Same-Set (Split-Servers $_.NameServers) $s.Servers});if($match.Count -ne 1){throw 'NRPT_NOT_EFFECTIVE'}}
  # Answers cached before the new policy would make the check below test the old resolver path: flush once, right after the policy is in place.
  $cacheFlushed=Clear-DnsResolverCache
  # Resolve through Windows APIs, never an explicit -Server query as NRPT proof.
  Reset-SystemAnswers
  foreach($t in (Get-Hosts $s.Targets)){$answer=@(Get-SystemAnswers $t);$expected=@($net.Candidates | Where-Object {$_.Server -in $s.Servers} | ForEach-Object {$_.TargetAnswers.PSObject.Properties[$t].Value});if($answer.Count -lt 1 -or @($answer | Where-Object {$_ -notin $expected}).Count -gt 0){throw 'NRPT_NOT_EFFECTIVE: Windows resolver differs; cache/TUN/app resolver may interfere'}}
  $s.Phase='Active';Save-State $s;Invoke-EngineCheckpoint 'ActiveJournal'
  $status=Get-Status
  if(!$status.Active -or $status.State -ne 'DNS active'){throw 'APPLY_READBACK_FAILED: effective configuration changed before completion'}
  $status | Add-Member -NotePropertyName DnsCacheFlushed -NotePropertyValue $cacheFlushed -Force
  return $status
 }catch {
  $failure=$_.Exception.Message
  try {$rollback=Restore-State $s;throw ('APPLY_FAILED_ROLLED_BACK: '+$failure)}catch{if($_.Exception.Message -like 'APPLY_FAILED_ROLLED_BACK:*'){throw};$s.Phase='Failed';Save-State $s;throw ('FAILED_RESTORABLE: '+$failure+'; restore: '+$_.Exception.Message)}
 }
}
function Restore-PreviousProfile($Prev,[string]$NewFailure) {
 # Compensation, not an atomic transaction: the old bridge was removed and the new one did not come up, so put the old profile back from the journal copy.
 $prevName=$(if($Prev.ProfileName){$Prev.ProfileName}else{$Prev.Template})
 $left=Read-State
 if($left -and $left.Phase -notin @('Restored')){throw ('RECOVERY_REQUIRED: switching to the new profile failed ('+$NewFailure+') and its cleanup is incomplete, so the previous profile '+$prevName+' was not re-applied. Run Restore.')}
 $saved=[pscustomobject]@{Servers=$script:Servers;Template=$script:Template;ProfileName=$script:ProfileName;Targets=@($script:Targets)}
 if($Prev.PSObject.Properties['Targets'] -and @($Prev.Targets).Count -gt 0){$script:Targets=@($Prev.Targets)}
 $script:Servers=@($Prev.Servers);$script:Template=$Prev.Template;$script:ProfileName=$Prev.ProfileName
 $plain=($Prev.Protocol -eq 'Plain DNS')
 try {
  $prevNet=Get-Network
  $prevSelected=@($prevNet.Candidates | Where-Object {if($plain){$_.PlainValidated}else{$_.DohValidated}} | Select-Object -ExpandProperty Server)
  if($prevSelected.Count -lt 1){throw 'DOH_NO_VALIDATED_ENDPOINT: the previous provider no longer passes preflight'}
  Apply-State $prevSelected $prevNet $plain | Out-Null
 }catch{
  $script:Servers=$saved.Servers;$script:Template=$saved.Template;$script:ProfileName=$saved.ProfileName;$script:Targets=$saved.Targets
  throw ('RECOVERY_REQUIRED: the new profile was not applied ('+$NewFailure+') and the previous profile '+$prevName+' could not be restored ('+$_.Exception.Message+'). The bridge is not active.')
 }
 $script:Servers=$saved.Servers;$script:Template=$saved.Template;$script:ProfileName=$saved.ProfileName;$script:Targets=$saved.Targets
 throw ('PROFILE_SWITCH_FAILED_PREVIOUS_RESTORED: the new profile was not applied ('+$NewFailure+'); the previous profile '+$prevName+' is active again.')
}
function Enable-State([bool]$Trust,[bool]$Unencrypted) {
 if(!$Trust){throw 'TRUST_DECISION_REQUIRED: the selected DNS provider can send HTTPS through a third-party SNI proxy'}
 if(!$Unencrypted){Assert-DohTemplate $script:Template}
 Assert-Capabilities (!$Unencrypted)
 $s=Read-State
 if($s -and $s.LeaseManaged -and $s.Phase -ne 'Restored'){throw 'LEASE_CONFIGURATION_CONFLICT: release or recover durable leases before Native Assisted mutation'}
 if($s -and $s.Phase -notin @('Active','Restored')){throw 'RECOVERY_REQUIRED: Restore unfinished journal first'}
 if($s -and $s.Phase -eq 'Active' -and !(Same-Set $s.Targets $script:Targets) -and !(Test-KnownScope $s.Targets)){throw 'RESTORE_BEFORE_SCOPE_UPGRADE'}
 # An identical request is a configuration read-back, not another network measurement. Explicit Repair still revalidates the provider.
 if($s -and $s.Phase -eq 'Active' -and !$script:Repair -and (Same-Set $s.Servers $script:Servers) -and (Same-Set $s.Targets $script:Targets) -and $s.Template -eq $script:Template -and $s.Protocol -eq $(if($Unencrypted){'Plain DNS'}else{'DoH'})){
  Assert-AssistedContext
  $status=Get-Status
  if(!$status.Active -or $status.State -ne 'DNS active'){throw $status.Code}
  return $status
 }
 $net=Get-Network
 $selected=@($net.Candidates | Where-Object {if($Unencrypted){$_.PlainValidated}else{$_.DohValidated}} | Select-Object -ExpandProperty Server)
 if($selected.Count -lt 1){throw $(if($Unencrypted){'DNS_TIMEOUT_OR_UNREACHABLE_OR_TLS_FAILURE'}else{'DOH_NO_VALIDATED_ENDPOINT: plaintext fallback disabled'})}
 if($script:ExpectedRevision -and !(Same-Set $selected $script:Servers)){throw 'ASSISTED_PROVIDER_CHANGED: reprepare the exact validated address set'}
 Assert-AssistedContext
 $prev=$null
 if($s -and $s.Phase -eq 'Active'){
  $protoReq = if($Unencrypted){'Plain DNS'}else{'DoH'}
  $stateTmpl = if($s.PSObject.Properties['Template']){$s.Template}else{''}
  $sameServers = Same-Set $s.Servers $selected
  $sameTmpl = (!$stateTmpl -or $stateTmpl -eq $script:Template)
  $sameProto = ($s.Protocol -eq $protoReq)
  $sameScope = (Same-Set $s.Targets $script:Targets)
  if($sameServers -and $sameTmpl -and $sameProto -and $sameScope){
   try {
    foreach($intent in $s.Rules){$r=@(Get-Rules | Where-Object {$_.Name -eq $intent.Name});if($r.Count -ne 1){throw 'RECOVERY_REQUIRED'};Assert-OwnedRule $r[0] $s $intent.Namespace}
    if($s.Protocol -eq 'DoH'){foreach($d in $s.Doh){$current=@(Get-Doh | Where-Object {$_.ServerAddress -eq $d.Server});if($current.Count -ne 1 -or $current[0].DohTemplate -ne $d.Template -or !$current[0].AutoUpgrade -or $current[0].AllowFallbackToUdp){throw 'DOH_REGISTRATION_CONFLICT'}}}
    foreach($t in $s.Targets){$p=@(Get-Effective | Where-Object {Same-Set $_.Namespace @($t)} | Where-Object {Same-Set (Split-Servers $_.NameServers) $s.Servers});if($p.Count -ne 1){throw 'NRPT_NOT_EFFECTIVE'}}
    $status=Get-Status;if($status.State -ne 'DNS active'){throw 'NRPT_CONFLICT'};return $status
   }catch{
    # Without -Repair a bridge that no longer verifies is reported as it is. With it, fall through to the compensated switch below (same refusals, same fallback to the previous profile).
    if(!$script:Repair){throw}
   }
  }
  # Switching profiles: everything that can be refused is checked while the working bridge is still in place.
  if(Test-ForeignConflict $s){throw 'NRPT_CONFLICT'}
  Assert-RestorableJournal $s
  if(!$Unencrypted -and (Test-DohRegistrationConflict $selected @($s.Doh | Where-Object {$_.Created -and $_.Ownership -eq 'Owned'} | ForEach-Object {$_.Server}))){throw 'DOH_REGISTRATION_CONFLICT'}
  $prev=[pscustomobject]@{Targets=@($s.Targets);Servers=@($s.Servers);Template=$stateTmpl;ProfileName=$(if($s.PSObject.Properties['ProfileName']){$s.ProfileName}else{''});Protocol=$s.Protocol}
  Restore-State $s | Out-Null
  $s=Read-State
 }
 if($s -and $s.Phase -ne 'Restored'){throw 'RECOVERY_REQUIRED: Restore unfinished journal first'}
 if(!$prev){return (Apply-State $selected $net $Unencrypted)}
 try{return (Apply-State $selected $net $Unencrypted)}catch{Restore-PreviousProfile $prev $_.Exception.Message}
}
# A restore answers with the status after it (Status field), so the window needs no second engine start. A status that cannot be read is simply left out.
function Invoke-Restore {
 Assert-AssistedContext
 $r=Restore-State (Read-State)
 # The bridge answers are still in the Windows cache: flush once the policy is gone. A failed flush is reported in DnsCacheFlushed, never an error.
 if(!$r.PSObject.Properties['DnsCacheFlushed']){$r | Add-Member -NotePropertyName DnsCacheFlushed -NotePropertyValue (Clear-DnsResolverCache) -Force}
 $after=$null;try{$after=Get-Status}catch{}
 if($after){$r | Add-Member -NotePropertyName Status -NotePropertyValue $after -Force}
 return $r
}
function Get-ActiveProfileName {
 try{$st=Read-State;if($st -and $st.Phase -eq 'Active'){$n=[string]$st.ProfileName;if(!$n){$n=[string]$st.Template};return $n}}catch{}
 return ''
}
function Invoke-ScheduledTaskTool([string[]]$Arguments) {
 & (Join-Path ([Environment]::SystemDirectory) 'schtasks.exe') @Arguments 2>&1 | Out-Null
 return $LASTEXITCODE
}
function Get-LegacyAutostartTask {
 # COM distinguishes a missing task from access/query failures without depending on CIM/WMI.
 $service=New-Object -ComObject 'Schedule.Service';$service.Connect();$folder=$service.GetFolder('\')
 try{return [string]$folder.GetTask('AntigravityDnsUnlockerLogon').Xml}catch{
  $errorCode=$_.Exception.HResult;if($_.Exception.InnerException){$errorCode=$_.Exception.InnerException.HResult}
  if($errorCode -eq -2147024894){return $null} # HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)
  throw 'AUTOSTART_TASK_QUERY_FAILED: existing task ownership is unknown'
 }
}
function Test-LegacyAutostartTask([string]$Xml) {
 try {
  $document=New-Object Xml.XmlDocument;$document.XmlResolver=$null
  $settings=New-Object Xml.XmlReaderSettings;$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null
  $reader=[Xml.XmlReader]::Create([IO.StringReader]::new($Xml),$settings)
  try{$document.Load($reader)}finally{$reader.Dispose()}
  $principals=@($document.SelectNodes('/*[local-name()="Task"]/*[local-name()="Principals"]/*'))
  $actions=@($document.SelectNodes('/*[local-name()="Task"]/*[local-name()="Actions"]/*'))
  if($principals.Count -ne 1 -or $actions.Count -ne 1 -or $actions[0].LocalName -ne 'Exec'){return $false}
  $principal=$principals[0];$action=$actions[0]
  if([string]$principal.RunLevel -ne 'LeastPrivilege' -or [string]$principal.LogonType -ne 'InteractiveToken'){return $false}
  $user=[string]$principal.UserId;$current=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
  if($user -notmatch '^S-1-'){$user=([Security.Principal.NTAccount]::new($user)).Translate([Security.Principal.SecurityIdentifier]).Value}
  $arguments=$action.SelectSingleNode('*[local-name()="Arguments"]');$argumentText=if($arguments){$arguments.InnerText}else{''}
  if($user -ne $current -or $argumentText -notin @('','--minimized')){return $false}
  return [IO.Path]::GetFullPath(([string]$action.Command).Trim('"')) -ieq [IO.Path]::GetFullPath($script:Exe)
 }catch{return $false}
}
function Get-AutostartStatus {
 $runPath=$script:RunPath
 $entryName='AntigravityDnsUnlocker'
 $regVal=$null;$enabled=$false;$method='None'
 if(Test-Path -LiteralPath $runPath){
  $props=Get-ItemProperty -LiteralPath $runPath -ErrorAction SilentlyContinue
  if($props -and $props.PSObject.Properties[$entryName]){
   $regVal=[string]$props.$entryName
   $target=$script:Exe
   $regPath=$regVal.Trim();if($regPath.StartsWith('"')){$regPath=($regPath -split '"')[1]}else{$regPath=($regPath -split '\s+--')[0]}
   if($regVal -and $regPath -ieq $target){
    $enabled=$true;$method='Registry'
   }
  }
 }
 $taskName='AntigravityDnsUnlockerLogon'
 $taskExists=$false;$taskOwned=$false;$taskQueryFailed=$false
 try {
  $taskXml=Get-LegacyAutostartTask
  if($null -ne $taskXml){
   $taskExists=$true;$taskOwned=Test-LegacyAutostartTask $taskXml
   if($taskOwned -and !$enabled){$enabled=$true;$method='Task'}
  }
 }catch{$taskQueryFailed=$true}
 return [pscustomobject]@{Enabled=$enabled;Method=$method;NonElevated=$true;EntryName=$entryName;Target=$script:Exe;RegistryValue=$regVal;TaskExists=$taskExists;TaskOwned=$taskOwned;TaskQueryFailed=$taskQueryFailed}
}
function Set-Autostart([bool]$Enable,[string]$Method='Registry') {
 $runPath=$script:RunPath
 $entryName='AntigravityDnsUnlocker'
 $taskName='AntigravityDnsUnlockerLogon'
 if($Enable){
  if($Method -eq 'Registry'){
   if(!(Test-Path -LiteralPath $runPath)){New-Item -Path $runPath -Force | Out-Null}
   $quotedExe='"' + $script:Exe + '" --minimized'
   Set-ItemProperty -LiteralPath $runPath -Name $entryName -Value $quotedExe -Type String -ErrorAction Stop
  }elseif($Method -eq 'Task'){
   $taskXml=Get-LegacyAutostartTask
   if($null -ne $taskXml){if(!(Test-LegacyAutostartTask $taskXml)){throw 'AUTOSTART_TASK_NOT_OWNED: existing task retained'};return Get-AutostartStatus}
   $act=New-ScheduledTaskAction -Execute $script:Exe
   $taskUser=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
   $trig=New-ScheduledTaskTrigger -AtLogOn -User $taskUser
   $princ=New-ScheduledTaskPrincipal -UserId $taskUser -LogonType Interactive -RunLevel LeastPrivilege
   $sett=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 0)
   Register-ScheduledTask -TaskName $taskName -Action $act -Trigger $trig -Principal $princ -Settings $sett -ErrorAction Stop | Out-Null
  }
 }else{
  $taskXml=Get-LegacyAutostartTask
  if($null -ne $taskXml -and !(Test-LegacyAutostartTask $taskXml)){throw 'AUTOSTART_TASK_NOT_OWNED: existing task retained'}
  if(Test-Path -LiteralPath $runPath){
   Remove-ItemProperty -LiteralPath $runPath -Name $entryName -ErrorAction SilentlyContinue
  }
  if($null -ne $taskXml -and (Invoke-ScheduledTaskTool @('/Delete','/TN',$taskName,'/F')) -ne 0){throw 'AUTOSTART_TASK_DELETE_FAILED'}
 }
 return Get-AutostartStatus
}
# A lease references the one finite native resource bundle. Incompatible bundles cannot share it.
# Expiry is processed only by explicit recovery/startup; it never authorizes autonomous route selection.
function Assert-LeaseIdentity {
 if($LeaseId -notmatch '^[a-f0-9]{32}$' -or $LeaseOwner -notmatch '^[A-Za-z0-9:._-]{1,192}$'){throw 'LEASE_IDENTITY_INVALID'}
}
function Get-NativeLeaseStatus {
 $s=Read-State
 [pscustomobject]@{Code='LEASE_STATUS';LeaseVersion=1;Managed=([bool]($s -and $s.LeaseManaged));Phase=$(if($s){$s.Phase}else{'Absent'});Leases=@(if($s){$s.Leases})}
}
function Acquire-NativeLease {
 Assert-LeaseIdentity
 if(!$TrustProxy){throw 'TRUST_DECISION_REQUIRED'}
 $now=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
 if($LeaseUntilMs -le $now -or $LeaseUntilMs -gt ($now+86400000)){throw 'LEASE_EXPIRY_INVALID'}
 if(!$Plain){Assert-DohTemplate $script:Template}
 Assert-Capabilities (!$Plain)
 $s=Read-State
 $lease=[pscustomobject]@{Id=$LeaseId;Owner=$LeaseOwner;ExpiresUtcMs=$LeaseUntilMs;Policy='RestoreWhenUnreferenced'}
 if($s -and $s.Phase -ne 'Restored'){
  if($s.Phase -ne 'Active'){throw 'RECOVERY_REQUIRED'}
  $protocol=if($Plain){'Plain DNS'}else{'DoH'}
  if(!$s.LeaseManaged -or !(Same-Set $s.Targets $script:Targets) -or !(Same-Set $s.Servers $script:Servers) -or $s.Template -ne $script:Template -or $s.Protocol -ne $protocol){throw 'LEASE_CONFIGURATION_CONFLICT'}
  $old=@($s.Leases | Where-Object {$_.Id -eq $LeaseId})
  if($old.Count -gt 0){
   if($old[0].Owner -cne $LeaseOwner -or $old[0].ExpiresUtcMs -ne $LeaseUntilMs){throw 'LEASE_ID_REUSED'}
   return Get-NativeLeaseStatus
  }
  if(@($s.Leases).Count -ge 128){throw 'LEASE_LIMIT'}
  # A new reference cannot legitimize a changed or broken existing native bundle.
  Assert-RestorableJournal $s
  $status=Get-Status;if(!$status.Active){throw 'RECOVERY_REQUIRED'}
  $s.Leases=@($s.Leases)+@($lease);Save-State $s;Invoke-EngineCheckpoint 'LeaseAcquiredJournal'
 }else{
  $net=Get-Network
  $selected=@($net.Candidates | Where-Object {if($Plain){$_.PlainValidated}else{$_.DohValidated}} | Select-Object -ExpandProperty Server)
  if($selected.Count -lt 1){throw 'DOH_NO_VALIDATED_ENDPOINT'}
  if(!(Same-Set $selected $script:Servers)){throw 'LEASE_PROVIDER_CHANGED'}
  Assert-AssistedContext
  Apply-State $selected $net ([bool]$Plain) $lease | Out-Null
 }
 Get-NativeLeaseStatus
}
function Release-NativeLease {
 Assert-LeaseIdentity
 $s=Read-State
 if(!$s -or !$s.LeaseManaged -or $s.Phase -eq 'Restored'){return Get-NativeLeaseStatus}
 $old=@($s.Leases | Where-Object {$_.Id -eq $LeaseId})
 if($old.Count -gt 0 -and $old[0].Owner -cne $LeaseOwner){throw 'LEASE_OWNER_MISMATCH'}
 if($old.Count -eq 0 -and @($s.Leases).Count -gt 0){return Get-NativeLeaseStatus}
 $s.Leases=@($s.Leases | Where-Object {$_.Id -ne $LeaseId});Save-State $s;Invoke-EngineCheckpoint 'LeaseReleasedJournal'
 if(@($s.Leases).Count -eq 0){
  $restored=Restore-State $s
  $flushed=$restored.DnsCacheFlushed
  $result=Get-NativeLeaseStatus;$result | Add-Member -NotePropertyName DnsCacheFlushed -NotePropertyValue $flushed
  return $result
 }
 Get-NativeLeaseStatus
}
function Recover-NativeLeases {
 $s=Read-State
 if(!$s -or !$s.LeaseManaged -or $s.Phase -eq 'Restored'){return Get-NativeLeaseStatus}
 $now=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
 $remaining=@($s.Leases | Where-Object {$_.ExpiresUtcMs -gt $now})
 # A backward clock cannot prolong a finite lease: conservatively restore the owned bundle.
 $lastWrite=[DateTimeOffset]::MinValue
 if(![DateTimeOffset]::TryParse([string]$s.UpdatedUtc,[ref]$lastWrite) -or $now -lt $lastWrite.ToUnixTimeMilliseconds()){$remaining=@()}
 # An interrupted native apply/restore must be recovered even when its requester lease has time remaining.
 if($s.Phase -ne 'Active'){$remaining=@()}
 if($remaining.Count -ne @($s.Leases).Count){$s.Leases=$remaining;Save-State $s;Invoke-EngineCheckpoint 'LeaseExpiredJournal'}
 if($remaining.Count -eq 0){
  $restored=Restore-State $s
  $flushed=$restored.DnsCacheFlushed
  $result=Get-NativeLeaseStatus;$result | Add-Member -NotePropertyName DnsCacheFlushed -NotePropertyValue $flushed
  return $result
 }
 Get-NativeLeaseStatus
}
# One mutation at a time. The lock is a file in the protected state directory held open with no sharing: only SYSTEM and Administrators can create it, so a user-level
# program cannot occupy it in advance (a named global mutex can be created by anybody, and then no connect or restore could ever start). It is released when the process ends.
function Open-OperationLock {
 [IO.Directory]::CreateDirectory($script:Root) | Out-Null   # exists and is protected already; only a test scratch directory is created here
 $path=Join-Path $script:Root 'operation.lock'
 try{return [IO.File]::Open($path,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
 catch [IO.DirectoryNotFoundException]{throw}
 catch [IO.IOException]{throw 'OPERATION_IN_PROGRESS'}
}
function Invoke-Engine {
 if($Autostart){
  switch($Autostart){
   'status'{return (Get-AutostartStatus)}
   'enable'{return (Set-Autostart -Enable $true -Method $AutostartMethod)}
   'disable'{return (Set-Autostart -Enable $false)}
  }
 }
 if(@($script:Targets).Count -lt 1){throw 'ENGINE_TARGETS_MISSING: profiles.json not found next to Engine.ps1 and no -Targets given'}
 if($Action -in @('enable','restore','lease-acquire','lease-release','lease-recover') -and !(Test-Admin)){throw 'ADMIN_REQUIRED: approve Windows UAC for this operation'}
 $lock=$null
 try {
  if($Action -in @('enable','restore','lease-acquire','lease-release','lease-recover')){Initialize-StateDirectory;$lock=Open-OperationLock}
  switch($Action){'status'{Get-Status};'diagnose'{Diagnose};'prepare'{Get-AssistedContext};'restore'{Invoke-Restore};'enable'{Enable-State ([bool]$TrustProxy) ([bool]$Plain)};'lease-acquire'{Acquire-NativeLease};'lease-release'{Release-NativeLease};'lease-recover'{Recover-NativeLeases};'lease-status'{Get-NativeLeaseStatus}}
 }finally{if($lock){$lock.Dispose()}}
}
if(!$LibraryOnly){
 try {Invoke-Engine | ConvertTo-Json -Depth 12;exit 0}catch{[pscustomobject]@{State='Failed (restorable)';Code=$_.Exception.Message;ActiveProfile=(Get-ActiveProfileName);SystemChanges='Use --restore if an ownership journal is present'} | ConvertTo-Json -Depth 3;exit 1}
}
