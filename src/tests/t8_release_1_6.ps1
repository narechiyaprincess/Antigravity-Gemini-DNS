# t8_release_1_6.ps1 - Tier 8: Release 1.6 "address and provider resilience", offline only.
# The exe is driven through its synthetic hooks (--connect-synthetic, --bootstrap-synthetic, --alternative-synthetic, --health-synthetic): a scenario file says what the
# "network" answers and the real production logic runs on it. No provider is contacted; nothing touches NRPT/DoH/adapters/state.json.
# This file is saved with a BOM: Windows PowerShell 5.1 mangles Cyrillic in a script without one.

param(
    [string]$ScratchPath,
    [switch]$EscalateKnownDefects
)

$ErrorActionPreference = 'Stop'
$global:CurrentTier = "Tier 8: Release 1.6 address and provider resilience"
$script:CurrentTier = $global:CurrentTier

$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\app')).Path
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

. (Join-Path $PSScriptRoot 'helpers\TestHarness.ps1')
. (Join-Path $PSScriptRoot 'helpers\SyntheticWire.ps1')

Write-Host "--- Executing Tier 8: Release 1.6 Tests ---" -ForegroundColor Cyan

$sourceExe = if ($env:AGDNS_EXE) { $env:AGDNS_EXE } else { Join-Path $projectRoot '..\release\AntigravityGeminiDns.exe' }
$haveExe = Test-Path -LiteralPath $sourceExe
$clean = Join-Path $ScratchPath 'clean_exe'
[IO.Directory]::CreateDirectory($clean) | Out-Null
if ($haveExe) { Copy-Item -LiteralPath $sourceExe -Destination $clean }
$exe = Join-Path $clean (Split-Path -Leaf $sourceExe)

function Invoke-Exe([string[]]$Arguments, [int]$TimeoutSec = 120) {
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
# Strips // comments so that a contract about code is not satisfied (or broken) by a comment.
function Get-Code([string]$Name) { ((Get-Src $Name) -split "`n" | ForEach-Object { $_ -replace '^\s*//.*$', '' -replace '\s//\s.*$', '' }) -join "`n" }
$script:scn = 0
function Invoke-Synthetic([string]$Flag, $Scenario, [int]$TimeoutSec = 120) {
    $script:scn++
    $file = Join-Path $ScratchPath ("scenario_{0}.json" -f $script:scn)
    [IO.File]::WriteAllText($file, ($Scenario | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    $r = Invoke-Exe @($Flag, $file) $TimeoutSec
    $obj = $null; try { $obj = $r.Out | ConvertFrom-Json } catch {}
    [pscustomobject]@{ Exit = $r.Exit; Json = $obj; Raw = $r.Out; Err = $r.Err }
}
function Join-Set($a) { (@($a) | Sort-Object) -join ',' }

# ==============================================================================
# A: the last working addresses (pins -> saved -> bootstrap)
# ==============================================================================
$tmpl = 'https://dns.example.test/dns-query'
$pins = @('193.233.112.67', '193.233.112.68')
$savedIps = @('83.220.169.155', '195.133.25.16', '212.109.195.93')
$now = [DateTime]::SpecifyKind([datetime]::ParseExact('2026-10-12 12:30:00', 'yyyy-MM-dd HH:mm:ss', $null), 'Utc')
function Stamp([double]$daysAgo) { $now.AddDays(-$daysAgo).ToString('o') }
function New-Connect([double]$AgeDays, $Alive, [string]$Bootstrap = 'fail', $Saved = $savedIps, $Boot = $null) {
    $o = [ordered]@{ template = $tmpl; nowUtc = $now.ToString('o'); pins = $pins; alive = @($Alive); bootstrap = $Bootstrap }
    if ($Boot) { $o.bootstrap = @($Boot) }
    if ($null -ne $Saved) { $o.saved = [ordered]@{ servers = @($Saved); utc = (Stamp $AgeDays) } }
    $o
}

if ($haveExe) {
    # A01: every pin is dead, the bootstrap fails right now, the saved addresses are alive -> the connection goes ahead on them
    $r = Invoke-Synthetic '--connect-synthetic' (New-Connect 3 $savedIps)
    $j = $r.Json
    Assert-Condition ($r.Exit -eq 0 -and $j -and $j.Source -eq 'saved' -and (Join-Set $j.Servers) -eq (Join-Set $savedIps) -and -not $j.Error -and $j.BootstrapCalls -eq 0) 'T8_A01' 'Dead pins and a failed bootstrap, live saved addresses -> the plan uses the saved addresses (the network is not even bootstrapped)' 'R16-1' -FailureMessage $r.Raw

    # A02: the saved addresses are dead too -> the provider name is bootstrapped, and the saved ones were tested first
    $r = Invoke-Synthetic '--connect-synthetic' (New-Connect 3 @() -Boot @('212.109.195.91'))
    $j = $r.Json
    Assert-Condition ($j -and $j.Source -eq 'found' -and (Join-Set $j.Servers) -eq '212.109.195.91' -and $j.BootstrapCalls -eq 1 -and @($j.PreflightAsked).Count -eq 2) 'T8_A02' 'Dead pins and dead saved addresses -> the provider name is bootstrapped' 'R16-1' -FailureMessage $r.Raw

    # A03: an entry older than 30 days is ignored (not even tested); the bootstrap decides, so a failing bootstrap fails the connection
    $r = Invoke-Synthetic '--connect-synthetic' (New-Connect 31 $savedIps)
    $j = $r.Json
    Assert-Condition ($j -and $j.Error -eq 'BOOTSTRAP_FAILED' -and @($j.Servers).Count -eq 0 -and @($j.PreflightAsked).Count -eq 1 -and $j.BootstrapCalls -eq 1) 'T8_A03' 'A saved entry older than 30 days is ignored: only the pins are tested, the failed bootstrap ends the attempt' 'R16-1' -FailureMessage $r.Raw
    $r30 = Invoke-Synthetic '--connect-synthetic' (New-Connect 30 $savedIps)
    $r29 = Invoke-Synthetic '--connect-synthetic' (New-Connect 29 $savedIps)
    Assert-Condition ($r30.Json.Source -eq 'saved' -and $r29.Json.Source -eq 'saved') 'T8_A03b' 'An entry of 29 days and of exactly 30 days is still used' 'R16-1' -FailureMessage ($r30.Raw + $r29.Raw)
    $rf = Invoke-Synthetic '--connect-synthetic' (New-Connect -2 $savedIps)
    Assert-Condition ($rf.Json.Error -eq 'BOOTSTRAP_FAILED') 'T8_A03c' 'An entry dated in the future is ignored' 'R16-1' -FailureMessage $rf.Raw

    # A04: a saved address that does not pass preflight is never used
    $r = Invoke-Synthetic '--connect-synthetic' (New-Connect 3 @())
    Assert-Condition ($r.Json.Error -eq 'BOOTSTRAP_FAILED' -and @($r.Json.Servers).Count -eq 0 -and @($r.Json.PreflightAsked).Count -eq 2) 'T8_A04' 'Saved addresses that fail preflight are not used (nothing is handed to the engine)' 'R16-1' -FailureMessage $r.Raw
    $r = Invoke-Synthetic '--connect-synthetic' (New-Connect 3 @($savedIps[1]))
    Assert-Condition ($r.Json.Source -eq 'saved' -and (Join-Set $r.Json.Servers) -eq $savedIps[1]) 'T8_A04b' 'Of the saved addresses only those that pass preflight are used' 'R16-1' -FailureMessage $r.Raw

    # A05: working pins win; the saved addresses are not consulted
    $r = Invoke-Synthetic '--connect-synthetic' (New-Connect 3 $pins)
    Assert-Condition ($r.Json.Source -eq 'pinned' -and @($r.Json.PreflightAsked).Count -eq 1 -and $r.Json.BootstrapCalls -eq 0) 'T8_A05' 'A working pin is used first; the saved addresses and the bootstrap are not touched' 'R16-1' -FailureMessage $r.Raw
}

# A06: wiring in the window. The saved set of the chosen profile reaches the resolver; it is stored only after a successful connect and only for found addresses.
$main = Get-Code 'MainForm.cs'
$assistedMain = Get-Content -LiteralPath (Join-Path $sourceRoot "MainForm.Assisted.cs") -Raw -Encoding UTF8
$assistedNative = Get-Content -LiteralPath (Join-Path $sourceRoot "AssistedNative.cs") -Raw -Encoding UTF8
Assert-Condition ($assistedMain.Contains("SavedAddressSource=template=>SavedAddresses.Find(settings.SavedAddrs,template)") -and $assistedNative.Contains("SavedFor(request.Template)") -and $assistedNative.Contains("AddressResolver.ResolveAsync(request.Template,request.Servers.ToArray()") -and $assistedNative.Contains("saved:saved")) 'T8_A06' 'The actual coordinator passes exact-template saved addresses to the original resolver' 'R16-1'
Assert-Condition ($assistedMain -match 'if\(outcome.Outcome==AssistedOutcome.NativeVerified&&assistedBackend.AddressPlan!=null\)' -and $assistedMain -match 'if\(plan.Source=="found"\)SavedAddresses.Put\(settings.SavedAddrs,p.Template,plan.Servers,plan.FoundUtc\)') 'T8_A07' 'Found addresses are saved only after current native verification' 'R16-1'
$ui = Get-Src 'UiKit.cs'
Assert-Condition ($ui -match 'List<SavedAddr> SavedAddrs' -and $main -match 'SourceText\(settings\.LastAddrSource,when\)') 'T8_A08' 'The saved set lives in the user settings (not in the engine journal) and its source is shown in the window (since 2026-10-13 in «Подробнее»)' 'R16-1'
Assert-Condition ((Get-Src 'Engine.ps1') -notmatch 'SavedAddr' -and (Get-Src 'Recovery.cs') -notmatch 'SavedAddr') 'T8_A09' 'The engine journal and the recovery code know nothing about the saved addresses (schema 1 is unchanged)' 'R16-1'

# ==============================================================================
# B: resilient bootstrap (more resolvers, parallel, one 20 s budget)
# ==============================================================================
$bt = 'https://dns.example.test/dns-query'
function Silent($via, $server) { [ordered]@{ via = $via; server = $server; code = 'DNS_TIMEOUT_OR_UNREACHABLE'; addresses = @() } }
if ($haveExe) {
    # B01: the system resolver and the three foreign resolvers are unreachable; only another provider of the catalog answered
    $r = Invoke-Synthetic '--bootstrap-synthetic' ([ordered]@{ template = $bt; system = 'fail'
        cross = @((Silent 'https://cloudflare-dns.com/dns-query' '1.1.1.1'), (Silent 'https://dns.google/dns-query' '8.8.8.8'), (Silent 'https://dns.quad9.net/dns-query' '9.9.9.9'),
                  [ordered]@{ via = 'https://dns.comss.one/dns-query'; server = '83.220.169.155'; code = 'DNS_OK'; addresses = @('212.109.195.91') })
        tlsOk = @('212.109.195.91'); dohOk = @('212.109.195.91') })
    Assert-Condition ($r.Json.Source -eq 'found' -and (Join-Set $r.Json.Servers) -eq '212.109.195.91' -and -not $r.Json.Error) 'T8_B01' 'System resolver and the three foreign resolvers unreachable, only another catalog provider answered -> the address is found' 'R16-2' -FailureMessage $r.Raw

    # B02: an address a resolver claims, without a valid certificate for the provider name, is not taken (and DoH is not even asked about it)
    $r = Invoke-Synthetic '--bootstrap-synthetic' ([ordered]@{ template = $bt; system = 'fail'
        cross = @([ordered]@{ via = 'https://dns.comss.one/dns-query'; server = '83.220.169.155'; code = 'DNS_OK'; addresses = @('212.109.195.77') })
        tlsOk = @(); dohOk = @('212.109.195.77') })
    Assert-Condition ($r.Json.Error -eq 'BOOTSTRAP_NO_ADDRESS' -and @($r.Json.Servers).Count -eq 0 -and @($r.Json.DohAsked).Count -eq 0) 'T8_B02' 'A resolver answer without valid TLS for the provider name is not taken, whoever supplied it' 'R16-2' -FailureMessage $r.Raw

    # B03: a step that never answers: the search ends at the budget with BOOTSTRAP_TIMEOUT (not a hang, not "no address passed")
    $r = Invoke-Synthetic '--bootstrap-synthetic' ([ordered]@{ template = $bt; system = @('212.109.195.91'); cross = @(); tlsOk = @('212.109.195.91'); dohOk = @('212.109.195.91'); dohHang = $true; budgetMs = 800; discoveryMs = 300 }) 40
    Assert-Condition ($r.Exit -eq 0 -and $r.Json.Error -eq 'BOOTSTRAP_TIMEOUT' -and @($r.Json.Servers).Count -eq 0 -and $r.Json.ElapsedMs -lt 6000) 'T8_B03' 'A check that never completes ends the search at the budget with BOOTSTRAP_TIMEOUT and nothing is chosen' 'R16-2' -FailureMessage $r.Raw
    $r = Invoke-Synthetic '--bootstrap-synthetic' ([ordered]@{ template = $bt; system = 'hang'; cross = 'hang'; budgetMs = 800; discoveryMs = 300 }) 40
    Assert-Condition ($r.Exit -eq 0 -and $r.Json.Error -eq 'BOOTSTRAP_TIMEOUT' -and $r.Json.ElapsedMs -lt 6000) 'T8_B03b' 'No resolver answers at all: also an honest timeout' 'R16-2' -FailureMessage $r.Raw

    # B04: a silent cross-check does not hold up what the system resolver found
    $r = Invoke-Synthetic '--bootstrap-synthetic' ([ordered]@{ template = $bt; system = @('212.109.195.91'); cross = 'hang'; tlsOk = @('212.109.195.91'); dohOk = @('212.109.195.91'); budgetMs = 1500; discoveryMs = 300 }) 40
    Assert-Condition ($r.Json.Source -eq 'found' -and (Join-Set $r.Json.Servers) -eq '212.109.195.91') 'T8_B04' 'A hanging cross-check is dropped after the discovery time; the address found meanwhile is still validated and used' 'R16-2' -FailureMessage $r.Raw

    # B04b: the resolvers are asked in parallel: 1 s + 1 s of silence costs about 1 s, not 2 s
    $r = Invoke-Synthetic '--bootstrap-synthetic' ([ordered]@{ template = $bt; system = @(); systemDelayMs = 1000; crossDelayMs = 1000
        cross = @([ordered]@{ via = 'https://dns.comss.one/dns-query'; server = '83.220.169.155'; code = 'DNS_OK'; addresses = @('212.109.195.91') })
        tlsOk = @('212.109.195.91'); dohOk = @('212.109.195.91'); budgetMs = 8000; discoveryMs = 4000 }) 40
    Assert-Condition ($r.Json.Source -eq 'found' -and $r.Json.ElapsedMs -ge 900 -and $r.Json.ElapsedMs -lt 1800) 'T8_B04b' 'The system resolver and the other resolvers are asked in parallel (two 1-second answers take about one second)' 'R16-2' -FailureMessage $r.Raw

    # B05: the resolver list: documented public DoH + other catalog providers by their pins; never the provider itself; https and public IPs only
    $rr = Invoke-Exe @('--bootstrap-resolvers', 'dns.comss.one'); $list = $null; try { $list = $rr.Out | ConvertFrom-Json } catch {}
    $cat = Get-Content -LiteralPath (Join-Path $sourceRoot 'profiles.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $comss = $cat.profiles | Where-Object { $_.id -eq 'dns.comss.one' }; $malw = $cat.profiles | Where-Object { $_.id -eq 'dns.malw.link' }
    $templates = @($list | ForEach-Object { $_.Template }); $servers = @($list | ForEach-Object { $_.Server })
    Assert-Condition ($list -and $templates -contains 'https://dns.adguard-dns.com/dns-query' -and $templates -contains 'https://unfiltered.adguard-dns.com/dns-query' -and $templates -contains 'https://dns.sb/dns-query' -and $servers -contains '1.1.1.1' -and $servers -contains '8.8.8.8' -and $servers -contains '9.9.9.9') 'T8_B05' 'The resolver list holds the earlier three and the AdGuard DNS and DNS.SB entries from their official pages' 'R16-2' -FailureMessage $rr.Out
    $badSrv = @($servers | Where-Object { $_ -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $_ -match '^(10|127)\.' -or $_ -match '^192\.168\.' })
    $selfAsk = @(@($comss.servers) | Where-Object { $servers -contains $_ })
    Assert-Condition (($servers -contains $malw.servers[0]) -and $selfAsk.Count -eq 0 -and $badSrv.Count -eq 0 -and (@($templates | Where-Object { $_ -notmatch '^https://' }).Count -eq 0) -and (@($list | ForEach-Object { $_.Server + '|' + $_.Template } | Select-Object -Unique).Count -eq @($list).Count)) 'T8_B06' 'Other catalog providers ask by their pins; the provider being searched does not ask itself; https templates, public addresses, no duplicates' 'R16-2' -FailureMessage ($servers -join ',')

    # B07: the new message is in the table, in Russian, with an action
    $cl = Invoke-Exe @('--codes-list'); $tbl = $null; try { $tbl = $cl.Out | ConvertFrom-Json } catch {}
    $bto = $tbl | Where-Object { $_.Code -eq 'BOOTSTRAP_TIMEOUT' }
    Assert-Condition ($bto -and $bto.Text -match '[Ѐ-ӿ]' -and $bto.Action -match '[Ѐ-ӿ]' -and $bto.Title) 'T8_B07' 'BOOTSTRAP_TIMEOUT has a Russian text and an action in the message table' 'R16-2'
}

# B08: contracts of the source: sources of the resolver addresses are named, Yandex is documented as not usable, the budget is 20 s and bounds every step
$probing = Get-Src 'Probing.cs'; $probCode = Get-Code 'Probing.cs'
Assert-Condition ($probing -match 'adguard-dns\.io/kb/general/dns-providers' -and $probing -match 'dns\.sb/doh' -and $probing -match 'yandex\.ru/support/dns/ru/ya-dns-modes' -and $probing -match 'no host name or URL template') 'T8_B08' 'Every added resolver names its official documentation page in a comment; the reason Yandex DNS is not used is recorded' 'R16-2'
Assert-Condition ($probCode -match 'Budget=TimeSpan\.FromSeconds\(20\)' -and $probCode -match 'overall\.CancelAfter\(options\.Budget\)' -and ([regex]::Matches($probCode, 'Within\(t=>')).Count -ge 4 -and $probCode -match 'Task\.WhenAll\(systemTask,crossTask\)') 'T8_B09' 'The bootstrap budget is 20 s, applies to the whole search, and the resolvers and the checks run in parallel through the bounded helper' 'R16-2'
Assert-Condition ($probCode -notmatch 'SystemQuery|UdpClient|ProtocolType\.Udp') 'T8_B10' 'The bootstrap adds no plain-DNS path (the resolvers are asked only over DoH)' 'R16-2'

# ==============================================================================
# C: when the current provider has no usable address, another working profile is OFFERED (never applied by itself)
# ==============================================================================
function New-A([string]$name, [string]$ip) { [Convert]::ToBase64String((New-SyntheticDnsResponse -QuestionDomain $name -QuestionType 1 -Id 1234 -AnswerRecords @((New-ARecord $name $ip)))) }
function New-AMulti([string]$name, [string[]]$ips) { [Convert]::ToBase64String((New-SyntheticDnsResponse -QuestionDomain $name -QuestionType 1 -Id 1234 -AnswerRecords @($ips | ForEach-Object { New-ARecord $name $_ }))) }
$D1 = 'daily-cloudcode-pa.googleapis.com'; $D2 = 'generativelanguage.googleapis.com'
$sv2 = @('193.233.112.67', '193.233.112.68')
$timeoutStep = @{ timeout = $true }
function Step([int]$ms, [string]$packet) { @{ ms = $ms; packet = $packet } }
function New-Prof([string]$id, [int]$ms, [bool]$exp = $false, $answers1 = $null, $answerTls = @{}) {
    $pk1 = if ($answers1) { New-AMulti $D1 $answers1 } else { New-A $D1 '193.233.112.67' }
    $q1 = @(Step $ms $pk1)
    @{ id = $id; template = "https://$id.test/dns-query"; servers = $sv2; tls = @(@{ ok = $true; ms = 40 }); queries = @{ $D1 = $q1; $D2 = @((Step $ms (New-A $D2 '193.233.112.88'))) }; answerTls = $answerTls; experimental = $exp }
}
function New-DeadProf([string]$id) { @{ id = $id; template = "https://$id.test/dns-query"; servers = $sv2; tls = @(@{ ok = $true; ms = 40 }); queries = @{ $D1 = @($timeoutStep); $D2 = @($timeoutStep) }; answerTls = @{}; experimental = $false } }
function Invoke-Alt([string]$Current, $Profiles) { Invoke-Synthetic '--alternative-synthetic' ([ordered]@{ attempts = 6; current = $Current; profiles = @($Profiles) }) }

if ($haveExe) {
    # C01: the current provider is dead, another one is alive -> the offer names the right profile
    $r = Invoke-Alt 'current' @((New-DeadProf 'current'), (New-Prof 'alive' 80))
    Assert-Condition ($r.Exit -eq 0 -and $r.Json.Kind -eq 'Offer' -and $r.Json.ProfileId -eq 'alive' -and $r.Json.From -eq 'current') 'T8_C01' 'Current provider dead, another alive -> the offer is that profile (and it comes from the profile that failed)' 'R16-3' -FailureMessage $r.Raw
    # C02: the best of several by the benchmark ranking, not the first one
    $r = Invoke-Alt 'current' @((New-DeadProf 'current'), (New-Prof 'slow' 650), (New-Prof 'fast' 30), (New-Prof 'mid' 250))
    Assert-Condition ($r.Json.Kind -eq 'Offer' -and $r.Json.ProfileId -eq 'fast') 'T8_C02' 'Of several working profiles the best one by the benchmark ranking is offered' 'R16-3' -FailureMessage $r.Raw
    # C03: an experimental profile is not offered, even the only one that works and however fast
    $r = Invoke-Alt 'current' @((New-DeadProf 'current'), (New-Prof 'expfast' 10 $true))
    Assert-Condition ($r.Json.Kind -eq 'NoWorking' -and -not $r.Json.ProfileId) 'T8_C03' 'An experimental profile is never offered' 'R16-3' -FailureMessage $r.Raw
    $r = Invoke-Alt 'current' @((New-DeadProf 'current'), (New-Prof 'expfast' 10 $true), (New-Prof 'plain' 500))
    Assert-Condition ($r.Json.Kind -eq 'Offer' -and $r.Json.ProfileId -eq 'plain') 'T8_C03b' 'A non-experimental profile wins against a faster experimental one' 'R16-3' -FailureMessage $r.Raw
    # C04: everything else is dead -> "no working provider" (the caller removes nothing)
    $r = Invoke-Alt 'current' @((New-DeadProf 'current'), (New-DeadProf 'd2'), (New-DeadProf 'd3'))
    Assert-Condition ($r.Json.Kind -eq 'NoWorking' -and $r.Json.Checked -eq 2) 'T8_C04' 'Every other profile is dead -> NoWorking' 'R16-3' -FailureMessage $r.Raw
    # C05: the 75 % rule of the benchmark: a pool whose answer addresses are mostly dead is not offered
    $pool = @('193.233.112.71', '193.233.112.72', '193.233.112.73', '193.233.112.74')
    $r = Invoke-Alt 'current' @((New-DeadProf 'current'), (New-Prof 'halfdead' 20 $false $pool @{ '193.233.112.72' = $false; '193.233.112.73' = $false; '193.233.112.74' = $false }))
    Assert-Condition ($r.Json.Kind -eq 'NoWorking') 'T8_C05' 'A profile with fewer than 75 % reachable answer addresses is not offered' 'R16-3' -FailureMessage $r.Raw
    # C06: the profile that just failed is not offered back
    $r = Invoke-Alt 'alive' @((New-Prof 'alive' 20), (New-Prof 'other' 300))
    Assert-Condition ($r.Json.Kind -eq 'Offer' -and $r.Json.ProfileId -eq 'other' -and $r.Json.Checked -eq 1) 'T8_C06' 'The profile that failed is never offered back' 'R16-3' -FailureMessage $r.Raw

    # C06b: the user's answer to the trust dialog: declined -> nothing is enabled; accepted -> trust is asked first, then the profile is enabled
    $tr = @((New-DeadProf 'current'), (New-Prof 'alive' 80))
    $r = Invoke-Synthetic '--alternative-synthetic' ([ordered]@{ attempts = 6; current = 'current'; profiles = $tr; trust = 'decline' })
    Assert-Condition ($r.Json.Kind -eq 'Offer' -and $r.Json.Applied -eq $false -and (@($r.Json.Calls) -join ',') -eq 'trust:alive') 'T8_C06b' 'Trust declined: the offer is not carried out, enable is never called' 'R16-3' -FailureMessage $r.Raw
    $r = Invoke-Synthetic '--alternative-synthetic' ([ordered]@{ attempts = 6; current = 'current'; profiles = $tr; trust = 'accept' })
    Assert-Condition ($r.Json.Applied -eq $true -and (@($r.Json.Calls) -join ',') -eq 'trust:alive,enable:alive') 'T8_C06c' 'Trust accepted: the trust decision comes first, then the profile is enabled once' 'R16-3' -FailureMessage $r.Raw

    # C07: both new messages exist, in Russian, with an action; "no working provider" says the bridge is untouched
    $cl = Invoke-Exe @('--codes-list'); $tbl = $null; try { $tbl = $cl.Out | ConvertFrom-Json } catch {}
    $off = $tbl | Where-Object { $_.Code -eq 'PROVIDER_SWITCH_OFFERED' }; $none = $tbl | Where-Object { $_.Code -eq 'NO_WORKING_PROVIDER' }
    Assert-Condition ($off -and $none -and $off.Kind -eq 'Switch' -and $off.Text -match '[Ѐ-ӿ]' -and $off.Action -match '[Ѐ-ӿ]' -and $none.Text -match '[Ѐ-ӿ]' -and $none.Action -match '[Ѐ-ӿ]' -and $none.Text -match 'Мост не менялся') 'T8_C07' 'PROVIDER_SWITCH_OFFERED and NO_WORKING_PROVIDER are in the message table (Russian, with an action; the second says the bridge is untouched)' 'R16-3'

    # C08: the self-test carries the switch checks, and the new window states render
    $st = Invoke-Exe @('--self-test') 300
    $swLines = @($st.Out -split "`n" | Where-Object { $_ -match '^PASS switch' }).Count
    Assert-Condition ($st.Exit -eq 0 -and $swLines -ge 20) 'T8_C08' "The exe's self-test carries the switch checks (count=$swLines)" 'R16-3' -FailureMessage ($st.Out.Substring([Math]::Max(0, $st.Out.Length - 300)))
    $shotDir = Join-Path $ScratchPath 'shots'; [IO.Directory]::CreateDirectory($shotDir) | Out-Null
    $ui = Invoke-Exe @('--ui-smoke', (Join-Path $shotDir 'smoke.png'), '--states') 300
    $needed = @('smoke_state_switch.png', 'smoke_state_noprovider.png')
    $absent = @($needed | Where-Object { -not (Test-Path (Join-Path $shotDir $_)) -or (Get-Item (Join-Path $shotDir $_)).Length -lt 2000 })
    Assert-Condition ($ui.Exit -eq 0 -and $absent.Count -eq 0) 'T8_C09' '--ui-smoke --states renders the "offer" and "no working provider" states' 'R16-3' -FailureMessage ($absent -join ', ')
}

# C10: contracts of the window code
$mainRaw = Get-Src 'MainForm.cs'
$ri = $mainRaw.IndexOf('// ---- another provider when the current one has none'); $rj = $mainRaw.IndexOf('// Smoke only: renders the preview dialog')
$region = if ($ri -ge 0 -and $rj -gt $ri) { $mainRaw.Substring($ri, $rj - $ri) } else { '' }
$oi = $region.IndexOf('async Task OfferAlternativeAsync'); $si = $region.IndexOf('async Task SwitchToOfferedAsync')
$offerBlock = if ($oi -ge 0 -and $si -gt $oi) { $region.Substring($oi, $si - $oi) } else { '' }
$switchBlock = if ($si -ge 0) { $region.Substring($si) } else { '' }
Assert-Condition ($offerBlock.Length -gt 400 -and $offerBlock -notmatch 'Elevate\(' -and $offerBlock -notmatch 'Mutate\(' -and $offerBlock -notmatch '"restore"|"enable"' -and $offerBlock -notmatch 'SetSelectedSilently|settings\.Profile' -and $offerBlock -notmatch 'Engine\(') 'T8_C10' 'The search for another provider only reads (no elevation, no engine call, no restore) and does not change the selected profile' 'R16-3' -FailureMessage ("len " + $offerBlock.Length)
$ap = $switchBlock.IndexOf('ProviderSwitch.ApplyAsync'); $sel = $switchBlock.IndexOf('SetSelectedSilently'); $mut = $switchBlock.IndexOf('Mutate("enable")')
$trustCode = Get-Code 'ConnectionTrust.cs'
$coordinatorCode = Get-Code 'AssistedCoordinator.cs'
Assert-Condition ($ap -ge 0 -and $sel -gt $ap -and $mut -gt $sel -and $switchBlock -notmatch 'EnsureTrust|Elevate\(|"restore"' -and $assistedMain -match 'assisted\.RunCandidates\(requests' -and $assistedMain -match 'Permit\(preview\)&&AssistedTrust\(preview\)' -and $assistedMain -match 'ProviderConsentForm\.Confirm' -and $trustCode -match 'ProviderIdentity\(provider\)' -and $trustCode -match 'Pack=PolicyJson\.Write\(p\.Compile\.Pack\)' -and $trustCode -match 'Scopes=Sorted\(b\.AuthorizedScopes\)') 'T8_C11' 'Offered switches enter ordinary enable once; production coordinator owns consent tied to provider, pack and scope without a duplicate name-only trust prompt' 'R16-3' -FailureMessage "ap=$ap sel=$sel mut=$mut"
$mainCode = Get-Code 'MainForm.cs'
Assert-Condition ($assistedMain -notmatch 'OfferAlternativeAsync\(p,' -and $assistedMain -match 'ConnectionCandidates\(request\)' -and $assistedMain -match '!x\.Custom&&!x\.Experimental' -and $assistedMain -notmatch 'ProviderPool\.Usable' -and $coordinatorCode -match 'Take\(4\)' -and $coordinatorCode -match 'CancelAfter\(deadline\?\?TimeSpan\.FromMinutes\(4\)\)' -and $coordinatorCode -match 'CancelAfter\(candidateTimeout\?\?TimeSpan\.FromSeconds\(70\)\)' -and $coordinatorCode.Contains('i>0&&(authorized==null||!authorized(request))') -and $assistedMain.Contains('candidate=>Permit()&&ConnectionTrust.Covers(candidate)') -and $trustCode.Contains('Covers(AssistedRequest r)=>Has(RequestKey(r))||IsCatalogProvider(r)&&CatalogConsent') -and $coordinatorCode.Contains('last.Outcome!=AssistedOutcome.Refused||last.Changed||!ProviderSwitch.IsTrigger(FailureCode(last))') -and $coordinatorCode.Contains('Superseded(generation)||ManualOff') -and $coordinatorCode.Contains('token=mutationToken??token;token.ThrowIfCancellationRequested();') -and $mainCode.Contains('assisted.ManualOff||assisted.RecoveryPending')) 'T8_C12' 'Production connect automatically tries at most four authorized catalog candidates, ignores unscoped history, stops on cancellation or any unsafe result, and preserves native mutation deadlines' 'R16-3'
$mh = $mainRaw.IndexOf('// ---- 1.5-1: health check in the tray'); $mh2 = $mainRaw.IndexOf('// ---- 1.5-2: support report')
$healthRegion = if ($mh -ge 0 -and $mh2 -gt $mh) { $mainRaw.Substring($mh, $mh2 - $mh) } else { '' }
# v1.3 C3: the one exception is the opt-in failover. It is gated by FailoverGuard.Triggered(settings.AutoFailover, ...) and only offers: it never applies or enables anything.
$optIn = [regex]::Match($healthRegion, 'async Task FailoverOfferAsync\(\)\{.*?\n  \}', [System.Text.RegularExpressions.RegexOptions]::Singleline).Value
$healthOutside = $healthRegion.Replace($optIn, '')
Assert-Condition ($healthRegion.Length -gt 500 -and $healthOutside -notmatch 'OfferAlternative|ProviderSwitch|SwitchToOffered' -and $healthRegion -match 'FailoverGuard\.Triggered\(settings\.AutoFailover' -and $optIn.Length -gt 200 -and $optIn -notmatch 'ApplyAsync|Mutate\("enable"\)|SwitchToOffered|Elevate\(') 'T8_C13' 'The background health check starts a search for another provider only through the opt-in failover (off by default), and that path only offers; nothing is applied or enabled' 'R16-3'
$sw = Get-Code 'Switch.cs'
$trig = [regex]::Match($sw, 'Triggers=\{([^}]*)\}').Groups[1].Value
Assert-Condition (@([regex]::Matches($trig, '"([A-Z_]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object) -join ',' -eq 'BOOTSTRAP_NO_ADDRESS,BOOTSTRAP_TIMEOUT,DOH_NO_VALIDATED_ENDPOINT' -and $sw -match 'r\.Selectable&&!r\.Experimental' -and $sw -match '!p\.Experimental' -and $sw -notmatch 'Elevate|Engine\(|Registry|Process') 'T8_C14' 'Switch.cs: exactly the three "no usable address" codes trigger it, it uses the benchmark Selectable rule without experimental profiles, and it has no elevation or system access' 'R16-3'
$sc = Get-Src 'Switch.cs'
$scan = @(); foreach ($f in 'Switch.cs', 'Connect.cs') { foreach ($mm in [regex]::Matches((Get-Src $f), '(?:Code|Error)="([A-Z][A-Z0-9_]+)"|"(BOOTSTRAP_[A-Z_]+|NO_WORKING_PROVIDER|PROVIDER_SWITCH_OFFERED|DOH_NO_VALIDATED_ENDPOINT)"')) { $scan += $(if ($mm.Groups[1].Success) { $mm.Groups[1].Value } else { $mm.Groups[2].Value }) } }
foreach ($mm in [regex]::Matches((Get-Src 'Probing.cs'), '"(BOOTSTRAP_[A-Z_]+)"')) { $scan += $mm.Groups[1].Value }
$knownCodes = @(); if ($haveExe -and $tbl) { $knownCodes = @($tbl | ForEach-Object { $_.Code }) }
$unknown = @($scan | Sort-Object -Unique | Where-Object { $knownCodes -notcontains $_ })
Assert-Condition ($haveExe -and $unknown.Count -eq 0 -and $scan.Count -ge 5) 'T8_C15' 'Every message code literal in Switch.cs, Connect.cs and Probing.cs has a message table entry' 'R16-3' -FailureMessage ($unknown -join ', ')

# ==============================================================================
# D: quick provider check every 10 minutes (no engine, no CIM); Suspect and Drift unchanged; the full check stays at 45 minutes
# ==============================================================================
$activeJournal = [ordered]@{ Schema = 1; Phase = 'Active'; Servers = @('212.109.195.91', '83.220.169.155', '10.0.0.7'); Template = 'https://x.example.test/dns-query' }
function Invoke-Health($Journal, [string]$Provider, $Steps) { Invoke-Synthetic '--health-synthetic' ([ordered]@{ journal = $Journal; provider = $Provider; steps = @($Steps) }) }
if ($haveExe) {
    # D01: a working provider: Healthy, and the engine (CIM status) and the Windows resolver were never asked
    $r = Invoke-Health $activeJournal 'ok' @('light')
    $rep = @($r.Json.Reports)[0]
    Assert-Condition ($r.Exit -eq 0 -and $rep.Kind -eq 'Healthy' -and $rep.Light -eq $true -and $r.Json.StatusCalls -eq 0 -and $r.Json.SystemCalls -eq 0 -and $r.Json.ProviderCalls -eq 1) 'T8_D01' 'The light check of a working provider is Healthy and never calls the engine status (no CIM) or the Windows resolver' 'R16-4' -FailureMessage $r.Raw
    Assert-Condition ((Join-Set $r.Json.ServersSeen) -eq (Join-Set @('212.109.195.91', '83.220.169.155')) -and $r.Json.TemplateSeen -eq 'https://x.example.test/dns-query') 'T8_D01b' 'The addresses and the template come from the journal; a private address in the journal is never contacted' 'R16-4' -FailureMessage $r.Raw

    # D02: Suspect -> Drift: one failure is not reported; the repeat is; a success in between starts over
    $r = Invoke-Health $activeJournal 'down' @('light', 'light')
    $a = @($r.Json.Reports)
    Assert-Condition ($a[0].Kind -eq 'Suspect' -and $a[0].Code -eq 'HEALTH_PROVIDER_DOWN' -and $a[0].CanFix -eq $false -and $a[1].Kind -eq 'Drift' -and $a[1].CanFix -eq $true -and $r.Json.StatusCalls -eq 0) 'T8_D02' 'A provider that does not answer: Suspect on the first light check, Drift (with the repair) only on the repeat; still no engine call' 'R16-4' -FailureMessage $r.Raw
    $r = Invoke-Health $activeJournal 'down' @('light', [ordered]@{ kind = 'light'; provider = 'ok' }, [ordered]@{ kind = 'light'; provider = 'down' })
    $a = @($r.Json.Reports)
    Assert-Condition ($a[0].Kind -eq 'Suspect' -and $a[1].Kind -eq 'Healthy' -and $a[2].Kind -eq 'Suspect') 'T8_D02b' 'A healthy answer between two failures starts the confirmation over (never an instant drift)' 'R16-4' -FailureMessage $r.Raw
    $r = Invoke-Health $activeJournal 'throws' @('light')
    Assert-Condition (@($r.Json.Reports)[0].Kind -eq 'Suspect') 'T8_D02c' 'A probe that fails with an exception is a soft failure, not a crash' 'R16-4' -FailureMessage $r.Raw
    # the full check keeps the same rule
    $r = Invoke-Health $activeJournal 'down' @('full', 'full')
    $a = @($r.Json.Reports)
    Assert-Condition ($a[0].Kind -eq 'Suspect' -and $a[1].Kind -eq 'Drift' -and $a[1].Light -eq $false -and $r.Json.StatusCalls -eq 2) 'T8_D03' 'The full check still goes Suspect -> Drift, and it does use the engine status (twice here)' 'R16-4' -FailureMessage $r.Raw

    # D04: no bridge / switched off / unfinished operation: decided from the journal alone, no network, no engine
    foreach ($case in @(@('none', 'Off'), @('restored', 'Off'), @('restoring', 'Drift'))) {
        $j = switch ($case[0]) { 'none' { $null } 'restored' { [ordered]@{ Phase = 'Restored' } } 'restoring' { [ordered]@{ Schema = 1; Phase = 'Restoring'; Servers = @('212.109.195.91'); Template = 'https://x.example.test/dns-query' } } }
        $r = Invoke-Health $j 'ok' @('light')
        Assert-Condition (@($r.Json.Reports)[0].Kind -eq $case[1] -and $r.Json.StatusCalls -eq 0 -and $r.Json.ProviderCalls -eq 0) ('T8_D04_' + $case[0]) ("Journal '" + $case[0] + "': " + $case[1] + " without a network probe or an engine call") 'R16-4' -FailureMessage $r.Raw
    }

    # D05: the cadence: a light check every 10 minutes, the full check at 45 and 90 minutes, each full check restarts the light cadence
    $r = Invoke-Synthetic '--health-synthetic' ([ordered]@{ schedule = [ordered]@{ minutes = 90; stepSeconds = 30 } })
    Assert-Condition ((@($r.Json.Sequence) -join ',') -eq '10:Light,20:Light,30:Light,40:Light,45:Periodic,55:Light,65:Light,75:Light,85:Light,90:Periodic') 'T8_D05' 'Over 90 minutes: light checks every 10 minutes, full checks at minute 45 and 90' 'R16-4' -FailureMessage $r.Raw

    $r = Invoke-Synthetic '--health-synthetic' ([ordered]@{ schedule = [ordered]@{ minutes = 240; stepSeconds = 7200 } })
    Assert-Condition ((@($r.Json.Sequence) -join ',') -eq '120:Periodic,240:Periodic') 'T8_D05b' 'When a light and a full check are both due (after a long sleep) the full check runs, and it also counts as the light one' 'R16-4' -FailureMessage $r.Raw
    $r = Invoke-Synthetic '--health-synthetic' ([ordered]@{ schedule = [ordered]@{ minutes = 15; stepSeconds = 30; suspectFirst = $true } })
    Assert-Condition ((@($r.Json.Sequence) -join ',') -eq '10:Light,11:Light') 'T8_D05c' 'A suspicion raised by a light check is confirmed by a light check a minute later' 'R16-4' -FailureMessage $r.Raw
    $r = Invoke-Synthetic '--health-synthetic' ([ordered]@{ journal = $activeJournal; provider = 'ok'; resolver = 'mismatch'; steps = @('full', 'light', 'full') })
    $a = @($r.Json.Reports)
    Assert-Condition ($a[0].Kind -eq 'Suspect' -and $a[0].Code -eq 'HEALTH_RESOLVER_MISMATCH' -and $a[1].Kind -eq 'Healthy' -and $a[1].Light -eq $true -and $a[2].Kind -eq 'Drift' -and $a[2].Code -eq 'HEALTH_RESOLVER_MISMATCH') 'T8_D05d' 'A healthy light check does not erase the full check''s pending resolver suspicion (the second full check still confirms it)' 'R16-4' -FailureMessage $r.Raw

    # D06: the self-test carries the light-check and scheduler checks
    $st = Invoke-Exe @('--self-test') 300
    $lightLines = @($st.Out -split "`n" | Where-Object { $_ -match '^PASS light health' }).Count
    $schedLines = @($st.Out -split "`n" | Where-Object { $_ -match '^PASS health scheduler' }).Count
    Assert-Condition ($st.Exit -eq 0 -and $lightLines -ge 15 -and $schedLines -ge 18) 'T8_D06' "The exe's self-test carries the light-check and scheduler checks (light=$lightLines scheduler=$schedLines)" 'R16-4' -FailureMessage ($st.Out.Substring([Math]::Max(0, $st.Out.Length - 300)))
}

# D07: contracts of the source
$healthCode = Get-Code 'Health.cs'
$li = $healthCode.IndexOf('async Task<HealthReport> LightAsync'); $ci = $healthCode.IndexOf('public async Task<HealthReport> CheckAsync')
$lightBlock = if ($li -ge 0 -and $ci -gt $li) { $healthCode.Substring($li, $ci - $li) } else { '' }
Assert-Condition ($lightBlock.Length -gt 800 -and $lightBlock -notmatch 'probe\.Status\(' -and $lightBlock -notmatch 'Program\.Engine' -and $lightBlock -notmatch 'SystemAnswers' -and $lightBlock -notmatch 'Get-Cim|Get-Wmi|Add-Dns|Remove-Dns|Elevate' -and $lightBlock -match 'probe\.Provider\(') 'T8_D07' 'The light check reaches Windows only through the provider probe: no engine status, no CIM, no resolver comparison, no write' 'R16-4' -FailureMessage ("len " + $lightBlock.Length)
Assert-Condition ($healthCode -match 'Period=TimeSpan\.FromMinutes\(45\)' -and $healthCode -match 'LightPeriod=TimeSpan\.FromMinutes\(10\)' -and $healthCode -match 'if\(!wasLight\)nextPeriodic=n\+o\.Period' -and $healthCode -match 'if\(trigger==HealthTrigger\.Light\)return await LightAsync') 'T8_D08' 'Full period 45 minutes and light period 10 minutes in the options; a light check does not postpone the full one; the monitor dispatches the light trigger to the light check' 'R16-4'
$mainRaw2 = Get-Src 'MainForm.cs'
$ai = $mainRaw2.IndexOf('internal void ApplyHealth'); $aj = $mainRaw2.IndexOf('internal HealthReport CurrentHealth')
$applyBlock = if ($ai -ge 0 -and $aj -gt $ai) { $mainRaw2.Substring($ai, $aj - $ai) } else { '' }
Assert-Condition ($applyBlock -match 'r\.Light&&r\.Kind==HealthKind\.Healthy' -and $applyBlock -match 'health\.Code==HealthMonitor\.ProviderDown' -and $applyBlock -match 'r\.Light&&r\.Kind==HealthKind\.Drift&&health!=null&&health\.Code!=r\.Code') 'T8_D09' 'The window treats a light report as provider-only: a healthy one clears only a provider-down drift, a failing one never replaces a different drift' 'R16-4'
$tickIdx = $mainRaw2.IndexOf('async Task HealthTick'); $tickEnd = $mainRaw2.IndexOf('internal void ApplyHealth')
$tickText = if ($tickIdx -ge 0 -and $tickEnd -gt $tickIdx) { $mainRaw2.Substring($tickIdx, $tickEnd - $tickIdx) } else { '' }
Assert-Condition ($tickText -match 'TryStart' -and $tickText -match 'busy' -and $tickText -match 'benchRunning' -and $tickText -notmatch 'Mutate\(|Elevate\(') 'T8_D10' 'The light check runs through the same guarded tick as the full one (not during an operation or the benchmark, never a repair by itself)' 'R16-4'

# ==============================================================================
# E: No unattended repair; the recovery broker remains optional and explicitly installed.
# ==============================================================================
$srcAll = ''
foreach ($f in Get-ChildItem -LiteralPath $sourceRoot -File | Where-Object { $_.Extension -in '.cs', '.ps1' }) { $srcAll += (Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8) }
$brokerSource = Get-Content (Join-Path $sourceRoot 'Broker.cs') -Raw -Encoding UTF8
$programSource = Get-Content (Join-Path $sourceRoot 'Program.cs') -Raw -Encoding UTF8
$assistedSource = Get-Content (Join-Path $sourceRoot 'AssistedNative.cs') -Raw -Encoding UTF8
$installCalls = @([regex]::Matches($brokerSource, '(?<![A-Za-z0-9_])Install\(\);'))
$explicitInstall = $brokerSource -match 'if\(args.Length==1&&args\[0\]=="--broker-install"\)\{Install\(\);' -and $installCalls.Count -eq 1
$brokerOptIn = $brokerSource -match 'if\(!args.Any\(a=>a.StartsWith\("--broker-",StringComparison.Ordinal\)\)\)return false;'
$dispatch = $programSource.IndexOf('Broker.TryRun(args,out int brokerExit)')
$gui = $programSource.IndexOf('Application.Run(form)')
$assistedIndependent = $assistedSource -match 'Program\.Engine\(' -and $assistedSource -notmatch '\bBroker\.'
Assert-Condition ($srcAll -notmatch '--auto-repair|ITaskService|sc\.exe\s+create' -and $explicitInstall -and $brokerOptIn -and $dispatch -ge 0 -and $gui -gt $dispatch -and $assistedIndependent) 'T8_E02' 'No unattended repair; broker installation requires its explicit CLI operation before GUI dispatch, while Native Assisted uses Engine independently' 'R16-5'
