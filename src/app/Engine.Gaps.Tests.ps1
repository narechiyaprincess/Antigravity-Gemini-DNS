param([string]$ScratchPath)
# Targeted mock tests for the Engine gaps that the existing suites did not cover (ENG-GAP-01..05).
# Same harness as Engine.Tests.ps1: every system cmdlet is replaced by a function below; nothing here reaches NRPT, DoH or adapters.
# Engine.ps1 is NOT changed by this file. A failed expectation is a finding, not a reason to edit the engine.
$ErrorActionPreference='Stop'
if(!$ScratchPath){throw 'Provide -ScratchPath under a disposable work directory'}
. (Join-Path $PSScriptRoot 'Engine.ps1') -LibraryOnly
$cat=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'profiles.json') -Raw -Encoding UTF8 | ConvertFrom-Json
# the two protected target names come from the catalog at run time (no copies in this file)
$script:HostA=$cat.targets[0];$script:HostB=$cat.targets[1]
$script:Tests=0;$script:Gaps=@();$script:FakeRules=@();$script:FakeDoh=@();$script:Writes=0;$script:FailDohAt=0;$script:DohAdds=0;$script:FailAfterRule=$false;$script:BadNetwork=$false;$script:StaleEffective=$null
$script:DefaultTargets=@($script:Targets)
function Check($Value,$Label){if(!$Value){throw ('FAIL '+$Label)};$script:Tests++;Write-Output ('PASS '+$Label)}
# A reported gap: the behaviour is real, the expectation was not met by the current engine. It is listed, never hidden and never counted as PASS.
function Gap($Label,$Detail){$script:Gaps+= $Label;Write-Output ('GAP '+$Label+' :: '+$Detail)}
function Reject($Call,$Expected,$Label){$caught=$false;try{& $Call | Out-Null}catch{if($_.Exception.Message -notlike ('*'+$Expected+'*')){throw};$caught=$true};Check $caught $Label}
function Reset-Mocks {
 $script:Root=Join-Path $ScratchPath ([guid]::NewGuid().ToString('N'));$script:StatePath=Join-Path $script:Root 'state.json'
 $script:FakeRules=@();$script:FakeDoh=@();$script:Writes=0;$script:FailDohAt=0;$script:DohAdds=0;$script:FailAfterRule=$false;$script:BadNetwork=$false;$script:StaleEffective=$null
 $script:Targets=@($script:DefaultTargets)
}
# mocks (the same surface as Engine.Tests.ps1)
function Clear-DnsResolverCache {return $true}
function Get-Rules {return $script:FakeRules}
function Get-Effective {if($null -ne $script:StaleEffective){return @($script:StaleEffective)};return @($script:FakeRules | Select-Object Namespace,NameServers)}
function Get-Doh {return $script:FakeDoh}
function Test-HostsConflict {return $false}
function Assert-Capabilities($Encrypted){}
function Initialize-StateDirectory {[IO.Directory]::CreateDirectory($script:Root) | Out-Null}
function Get-Network {return [pscustomobject]@{Candidates=@(foreach($ip in $script:Servers){[pscustomobject]@{Server=$ip;DohValidated=(!$script:BadNetwork);PlainValidated=(!$script:BadNetwork);TargetA=@('179.254.166.174');TargetAnswers=[pscustomobject]@{$script:HostA=@('179.254.166.174');$script:HostB=@('179.254.166.175')}}})}}
function Get-SystemAnswers($Domain) {if($Domain -eq $script:HostB){return @('179.254.166.175')};return @('179.254.166.174')}
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

# ENG-GAP-01: legacy journal (Schema 1, 9 targets, 2 owned DoH, no Lease* fields)
Reset-Mocks
$core=@($cat.targets);$gemini=@($cat.geminiTargets);$google=@($cat.googleTargets)
Check ($core.Count -eq 2 -and $gemini.Count -eq 2 -and $google.Count -eq 5) 'ENG-GAP-01 fixture: 9 targets = core 2 + Gemini 2 + Google suffixes 5 (from profiles.json)'
$script:Targets=@($core+$gemini+$google)
$owner='0123456789abcdef0123456789abcdef';$servers=@('192.0.2.10','192.0.2.11');$tmpl=$script:Template
$legacyRules=@($script:Targets | ForEach-Object {[pscustomobject]@{Namespace=$_;Name=('{'+[guid]::NewGuid().ToString()+'}');Fingerprint=$null}})
$script:FakeRules=@($legacyRules | ForEach-Object {[pscustomobject]@{Name=$_.Name;Namespace=@($_.Namespace);NameServers=$servers;DisplayName=('AgDns '+$owner);Comment=('AgDns:'+$owner);NameEncoding='Disable';DnsSecValidationRequired=$false}})
$script:FakeRules+= [pscustomobject]@{Name='{00000000-0000-0000-0000-0000000000f1}';Namespace=@('corp.example');NameServers='10.0.0.53';DisplayName='Corp';Comment='Corp';NameEncoding='Disable';DnsSecValidationRequired=$false}
$script:FakeDoh=@($servers | ForEach-Object {[pscustomobject]@{ServerAddress=$_;DohTemplate=$tmpl;AutoUpgrade=$true;AllowFallbackToUdp=$false}})
$script:FakeDoh+= [pscustomobject]@{ServerAddress='192.0.2.99';DohTemplate='https://foreign.example/dns-query';AutoUpgrade=$true;AllowFallbackToUdp=$false}
$legacy=[pscustomobject]@{Schema=1;Owner=$owner;Phase='Active';ProfileName='legacy';Template=$tmpl;Protocol='DoH';Targets=$script:Targets;Servers=$servers;Rules=$legacyRules;Doh=@($servers | ForEach-Object {[pscustomobject]@{Server=$_;Template=$tmpl;Created=$true;Ownership='Owned'}});UpdatedUtc=[DateTime]::UtcNow.ToString('o')}
Initialize-StateDirectory;[IO.File]::WriteAllText($script:StatePath,($legacy|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
$loaded=Read-State
Check ($loaded.Schema -eq 1 -and $loaded.Rules.Count -eq 9 -and $loaded.Doh.Count -eq 2) 'ENG-GAP-01 legacy Schema 1 journal is accepted by Read-State'
$writesBefore=$script:Writes
$res=Restore-State $loaded
$after=Read-State
Check ($after.Phase -eq 'Restored' -and $after.Schema -eq 2 -and @($after.Leases).Count -eq 0) 'ENG-GAP-01 restore ends Restored and the journal is written back as Schema 2'
Check ($res.Code -eq 'ROLLED_BACK' -and @($res.RetainedSharedDoh).Count -eq 0) 'ENG-GAP-01 restore of the legacy journal completes without retained DoH'
Check (@($script:FakeRules).Count -eq 1 -and $script:FakeRules[0].Comment -eq 'Corp') 'ENG-GAP-01 foreign NRPT rule survives; all 9 owned policies removed'
Check (@($script:FakeDoh).Count -eq 1 -and $script:FakeDoh[0].ServerAddress -eq '192.0.2.99') 'ENG-GAP-01 foreign DoH survives; the 2 owned registrations removed'
Check (($script:Writes-$writesBefore) -eq 11) 'ENG-GAP-01 exactly 11 mutations (9 owned rules + 2 owned DoH) and no additions'

# ENG-GAP-02: Get-Effective still reports the removed policy after its deletion
Reset-Mocks
Enable-State $true $false | Out-Null
$script:StaleEffective=@($script:FakeRules | Select-Object Namespace,NameServers)
$writesBefore=$script:Writes
$res=Restore-State (Read-State)
$j=Read-State
Check (@($script:FakeRules).Count -eq 0 -and ($script:Writes-$writesBefore) -eq 2) 'ENG-GAP-02 owned NRPT rules are removed while the effective view is stale'
Check ($res.Code -eq 'ROLLED_BACK_SHARED_DOH_RETAINED' -and @($res.RetainedSharedDoh).Count -eq 2) 'ENG-GAP-02 stale effective policy keeps both DoH registrations (conservative, no removal)'
Check (@($script:FakeDoh).Count -eq 2 -and @($j.Doh | Where-Object {$_.Ownership -eq 'Relinquished'}).Count -eq 2 -and $j.Phase -eq 'Restored') 'ENG-GAP-02 journal records Relinquished DoH and Restored; registrations are left as they are'
# Orphan check: once the view catches up, a later Restore is a no-op because the journal is already Restored.
$script:StaleEffective=$null
$again=Restore-State (Read-State)
# ENG-GAP-02-ORPHAN = KNOWN_LIMITATION (INTENDED_SAFE_RELINQUISH): Relinquished means ownership was given up on purpose, so a later Restore never removes the entry.
# Pinned here as a contract: nothing is removed, nothing is duplicated, and the next Enable adopts the entry as Shared (never owned, never removed by a later restore).
Check ($again.Result -eq 'No-op' -and @($script:FakeDoh).Count -eq 2) 'ENG-GAP-02-ORPHAN KNOWN_LIMITATION: later Restore is a No-op and leaves the Relinquished registrations (inert, no rule or adapter refers to them)'
$addsBefore=$script:DohAdds
Enable-State $true $false | Out-Null
$e=Read-State
Check ($script:DohAdds -eq $addsBefore -and @($script:FakeDoh).Count -eq 2 -and @($e.Doh | Where-Object {$_.Ownership -eq 'Shared' -and !$_.Created}).Count -eq 2) 'ENG-GAP-02-ORPHAN next Enable adopts the leftover registrations as Shared: no second Add, no duplicates'
Restore-State (Read-State) | Out-Null
Check ((Read-State).Phase -eq 'Restored' -and @($script:FakeDoh).Count -eq 2 -and @($script:FakeRules).Count -eq 0) 'ENG-GAP-02-ORPHAN Shared leftovers are never removed by a later Restore; owned rules are'

# ENG-GAP-03: ambiguous Pending DoH registration
Reset-Mocks
Enable-State $true $false | Out-Null
$s=Read-State
$ambiguousIp=$s.Doh[0].Server
$s.Phase='Applying';$s.Doh[0].Ownership='Pending';Save-State $s
Reject {Restore-State (Read-State)} 'DOH_OWNERSHIP_UNCERTAIN' 'ENG-GAP-03 Pending DoH after an interrupted apply is not claimed: DOH_OWNERSHIP_UNCERTAIN'
Check (@($script:FakeDoh | Where-Object {$_.ServerAddress -eq $ambiguousIp}).Count -eq 1) 'ENG-GAP-03 the ambiguous registration is not removed by the refused restore'
Reject {Restore-State (Read-State)} 'DOH_OWNERSHIP_UNCERTAIN' 'ENG-GAP-03 a second restore attempt refuses the same way'
Check (@($script:FakeDoh | Where-Object {$_.ServerAddress -eq $ambiguousIp}).Count -eq 1) 'ENG-GAP-03 still retained after the second attempt'
# Resolution is an explicit owner decision in the journal ("not ours"): the restore then completes and keeps that registration.
$s=Read-State;$s.Doh[0].Ownership='Shared';Save-State $s
$done=Restore-State (Read-State)
Check ((Read-State).Phase -eq 'Restored' -and @($script:FakeDoh | Where-Object {$_.ServerAddress -eq $ambiguousIp}).Count -eq 1) 'ENG-GAP-03 after the ambiguity is resolved the restore completes and keeps the foreign-or-unknown registration'

# ENG-GAP-04: NRPT overlap table against the real profile namespaces (pure functions, no system access)
Reset-Mocks
$t1=$core[0];$t2=$core[1]
$cases=@(
 @{Ns='.';Dom=$gemini[0];Want=$true;Why='root namespace overlaps every name'}
 @{Ns='.';Dom='.google';Want=$true;Why='root overlaps the TLD suffix target'}
 @{Ns='*.google.com';Dom=$gemini[0];Want=$true;Why='wildcard: conservative overlap'}
 @{Ns='*.google.com';Dom='.googleapis.com';Want=$true;Why='wildcard: conservative overlap (no exact mapping)'}
 @{Ns='.google.com';Dom=$gemini[0];Want=$true;Why='suffix covers the Gemini host'}
 @{Ns='.google.com';Dom=$gemini[1];Want=$true;Why='suffix covers the clients6 host'}
 @{Ns='.google.com';Dom='.google.com';Want=$true;Why='same suffix'}
 @{Ns='.google.com';Dom='.googleapis.com';Want=$false;Why='different suffix'}
 @{Ns='.google.com';Dom='.google';Want=$false;Why='.google.com does not cover the broader .google suffix'}
 @{Ns='GOOGLE.COM';Dom='.google.com';Want=$true;Why='case-insensitive; exact name overlaps the bare name of a suffix target'}
 @{Ns='GOOGLE.COM';Dom=$gemini[0];Want=$false;Why='an exact name does not cover a subdomain'}
 @{Ns='.google';Dom='.google';Want=$true;Why='identical suffix'}
 @{Ns='.google';Dom='.google.com';Want=$false;Why='.google does not cover names under google.com'}
 @{Ns='.google';Dom=$gemini[0];Want=$false;Why='.google does not cover gemini.google.com'}
 @{Ns='example.com';Dom=$gemini[0];Want=$false;Why='unrelated foreign name'}
 @{Ns='example.com';Dom='.google.com';Want=$false;Why='unrelated foreign name vs Google suffix'}
 @{Ns='.';Dom=$t1;Want=$true;Why='root vs a real core target'}
 @{Ns='example.com';Dom=$t2;Want=$false;Why='unrelated foreign name vs a real core target'}
)
$gapCount=0
foreach($c in $cases){
 $got=[bool](Test-Overlap $c.Ns $c.Dom)
 if($got -eq $c.Want){Check $true ('ENG-GAP-04 overlap '+$c.Ns+' vs '+$c.Dom+' = '+$c.Want+' ('+$c.Why+')')}
 else{$gapCount++;Gap ('ENG-GAP-04 '+$c.Ns+' vs '+$c.Dom) ('expected '+$c.Want+', engine returned '+$got+' ('+$c.Why+')')}
}
$foreignSuffix=@([pscustomobject]@{Name='{00000000-0000-0000-0000-0000000000f2}';Namespace=@('.google');NameServers='10.0.0.53';DisplayName='Corp';Comment='Corp'})
$googleNoTld=@($gemini+@($google | Where-Object {$_ -ne '.google'}))
Check (@(Get-Conflicts $foreignSuffix '' $googleNoTld).Count -eq 0) 'ENG-GAP-04 a foreign .google rule is not a conflict for the Google scope without the .google target'
Check (@(Get-Conflicts $foreignSuffix '' @($gemini+$google)).Count -eq 1) 'ENG-GAP-04 a foreign .google rule IS a conflict when .google is itself a target (identical namespace)'
$foreignRoot=@([pscustomobject]@{Name='{00000000-0000-0000-0000-0000000000f3}';Namespace=@('.');NameServers='10.0.0.53';DisplayName='Corp';Comment='Corp'})
Check (@(Get-Conflicts $foreignRoot '' @($gemini+$google)).Count -eq 1) 'ENG-GAP-04 a foreign root rule is a conflict for the Google scope'
$ownRoot=@([pscustomobject]@{Name='{00000000-0000-0000-0000-0000000000f4}';Namespace=@('.');NameServers='10.0.0.53';DisplayName='AgDns abc';Comment='AgDns:abc'})
Check (@(Get-Conflicts $ownRoot 'abc' @($gemini+$google)).Count -eq 0) 'ENG-GAP-04 our own rule for the same owner is never a conflict'

# ENG-GAP-05: foreign DoH registration on a selected IP with other settings
$variants=@(
 @{Name='another template';Tmpl='https://foreign.example/dns-query';Fallback=$false;Auto=$true},
 @{Name='same template, UDP fallback on';Tmpl=$tmpl;Fallback=$true;Auto=$true},
 @{Name='same template, AutoUpgrade off';Tmpl=$tmpl;Fallback=$false;Auto=$false}
)
foreach($v in $variants){
 Reset-Mocks
 $ip=$servers[0]
 $script:FakeDoh=@([pscustomobject]@{ServerAddress=$ip;DohTemplate=$v.Tmpl;AutoUpgrade=$v.Auto;AllowFallbackToUdp=$v.Fallback})
 $before=@($script:FakeDoh | ForEach-Object {$_.DohTemplate+'|'+$_.AutoUpgrade+'|'+$_.AllowFallbackToUdp}) -join ';'
 Reject {Enable-State $true $false} 'DOH_REGISTRATION_CONFLICT' ('ENG-GAP-05 foreign registration ('+$v.Name+') refuses the apply')
 $after=@($script:FakeDoh | ForEach-Object {$_.DohTemplate+'|'+$_.AutoUpgrade+'|'+$_.AllowFallbackToUdp}) -join ';'
 Check ($script:Writes -eq 0 -and $before -eq $after -and -not (Test-Path -LiteralPath $script:StatePath)) ('ENG-GAP-05 ('+$v.Name+') Writes=0, no journal, foreign registration unchanged')
}

Write-Output ('ENGINE_GAPS_PASS '+$script:Tests+' GAPS '+$script:Gaps.Count)
