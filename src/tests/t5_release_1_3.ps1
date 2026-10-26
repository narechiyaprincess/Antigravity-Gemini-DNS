# t5_release_1_3.ps1 - Tier 5: Release 1.3.0 (single exe, provider catalog, benchmark, compact UI)
# Offline: no provider is contacted, nothing is applied to NRPT/DoH/adapters/state.json.
# Uses a lone copy of the built exe in an otherwise empty folder (the single-exe contract), mock cmdlets for the engine,
# and tests\helpers\SyntheticWire.ps1 to build the DNS wire packets that feed the benchmark.

param(
    [string]$ScratchPath,
    [switch]$EscalateKnownDefects
)

$ErrorActionPreference = 'Stop'
$global:CurrentTier = "Tier 5: Release 1.3.0"
$script:CurrentTier = $global:CurrentTier

$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\app')).Path
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

. (Join-Path $sourceRoot 'Engine.ps1') -LibraryOnly
$script:RunPath = 'HKCU:\Software\AgDnsTest\Run'  # isolated: tests never touch the user's real Run key
. (Join-Path $PSScriptRoot 'helpers\TestHarness.ps1')
. (Join-Path $PSScriptRoot 'helpers\SyntheticWire.ps1')
. (Join-Path $PSScriptRoot 'helpers\MockCmdlets.ps1')

Write-Host "--- Executing Tier 5: Release 1.3.0 Tests ---" -ForegroundColor Cyan

$sourceExe = if ($env:AGDNS_EXE) { $env:AGDNS_EXE } else { Join-Path $projectRoot '..\release\AntigravityGeminiDns.exe' }
$haveExe = Test-Path -LiteralPath $sourceExe
Assert-Condition $haveExe 'T5_X00' "Built exe is available for the single-exe tests ($sourceExe)" 'R13-EXE' -FailureMessage 'Run src\app\Build.ps1 first (or set AGDNS_EXE)'

# A lone copy in a fresh folder: nothing else may be needed next to it.
$clean = Join-Path $ScratchPath 'clean_exe'
[IO.Directory]::CreateDirectory($clean) | Out-Null
if ($haveExe) { Copy-Item -LiteralPath $sourceExe -Destination $clean }
$exe = Join-Path $clean (Split-Path -Leaf $sourceExe)

function Invoke-Exe([string[]]$Arguments, [int]$TimeoutSec = 90) {
    # System.Diagnostics.Process (not Start-Process): Windows PowerShell 5.1 loses ExitCode for Start-Process -PassThru.
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
# ==============================================================================
# R13-CAT: provider catalog is versioned data
# ==============================================================================
$catPath = Join-Path $sourceRoot 'profiles.json'
$cat = Get-Content -LiteralPath $catPath -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-Condition ($cat.schema -eq 1 -and $cat.version -match '^\d{4}\.\d{2}\.\d{2}\.\d+$' -and $cat.updated) 'T5_C01' 'profiles.json carries a schema, a dated version and an update stamp' 'R13-CAT'

$ids = @($cat.profiles | ForEach-Object { $_.id }); $slots = @($cat.profiles | ForEach-Object { $_.slot })
Assert-Condition (($ids | Select-Object -Unique).Count -eq $ids.Count -and ($slots | Select-Object -Unique).Count -eq $slots.Count -and $ids.Count -ge 6) 'T5_C02' 'Catalog has unique ids and P-slots and at least six profiles' 'R13-CAT'

function Test-PublicV4([string]$ip) {
    $a = $null
    if (-not [Net.IPAddress]::TryParse($ip, [ref]$a) -or $a.AddressFamily -ne 'InterNetwork') { return $false }
    $b = $a.GetAddressBytes()
    return -not ($b[0] -in 0, 10, 127 -or $b[0] -ge 224 -or ($b[0] -eq 169 -and $b[1] -eq 254) -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or ($b[0] -eq 192 -and $b[1] -eq 168) -or ($b[0] -eq 100 -and $b[1] -ge 64 -and $b[1] -le 127) -or ($b[0] -eq 192 -and $b[1] -eq 0 -and $b[2] -eq 2) -or ($b[0] -eq 198 -and $b[1] -in 18, 19, 51) -or ($b[0] -eq 203 -and $b[1] -eq 0 -and $b[2] -eq 113))
}
$allPublic = $true; $allHttps = $true
foreach ($p in $cat.profiles) {
    foreach ($s in $p.servers) { if (-not (Test-PublicV4 $s)) { $allPublic = $false } }
    $u = [uri]$p.template; $parsedIp = $null
    if ($u.Scheme -ne 'https' -or [Net.IPAddress]::TryParse($u.Host, [ref]$parsedIp)) { $allHttps = $false }
}
Assert-Condition ($allPublic -and $allHttps) 'T5_C03' 'Every pinned server is a public IPv4 and every template is an https URL with a host name' 'R13-CAT'

$byId = @{}; foreach ($p in $cat.profiles) { $byId[$p.id] = $p }
Assert-Condition ($byId['geohide.ru'].template -eq 'https://geohide.ru/dns-query' -and $byId['dns.zapret.moe'].template -eq 'https://dns.zapret.moe/dns-query' -and $byId['dns.bezmezhau.com'].template -eq 'https://dns.bezmezhau.com/dns-query' -and -not $byId.ContainsKey('dns.dns-ai.ru') -and -not $byId.ContainsKey('xbox-dns.ru') -and @($cat.retired | ForEach-Object { $_.id }).Count -eq 2) 'T5_C04' 'GeoHide, Zapret and BezMezhau use their published DoH templates; DNS-AI and Xbox DNS are retired' 'R13-CAT'

Assert-Condition (($cat.pending | Where-Object { $_.id -eq 'dns.astracat.ru' -and $_.template -eq 'https://dns.astracat.ru:8443/dns-query' -and $_.reason }) -and -not $byId.ContainsKey('dns.astracat.ru')) 'T5_C05' 'Astracat (:8443) is recorded as pending with a reason, not shipped as an unverified pin' 'R13-CAT'

$p123 = @{ 'dns.malw.link' = '179.254.166.171,164.5.224.81'; 'dns.comss.one' = '195.133.25.16,212.109.195.93,94.250.253.55,185.146.157.78' }  # P3 pins validated by bootstrap on 2026-10-26.
$pinsKept = $true; foreach ($k in $p123.Keys) { if (($byId[$k].servers -join ',') -ne $p123[$k]) { $pinsKept = $false } }
Assert-Condition $pinsKept 'T5_C06' 'P1 pins are unchanged from 1.2.0; P3 carries the addresses validated by bootstrap on 2026-10-26' 'R13-CAT'

$bootstrapped = @('geohide.ru', 'dns.bezmezhau.com', 'dns.comss.one', 'dns.zapret.moe', 'eu.geohide.ru') | ForEach-Object { $byId[$_] }
$provenance = $true
foreach ($p in $bootstrapped) { if (-not $p.bootstrap.date -or -not $p.bootstrap.crossCheck -or -not $p.bootstrap.tls -or (@($p.servers | Where-Object { $_ -notin @($p.bootstrap.pinned) }).Count -gt 0) -or (@($p.bootstrap.pinned | Where-Object { $_ -notin @($p.bootstrap.resolved) }).Count -gt 0)) { $provenance = $false } }
Assert-Condition $provenance 'T5_C07' 'Each bootstrapped pin is a resolved address with recorded date, cross-check and TLS evidence' 'R13-CAT'

Assert-Condition ($byId['dns.bezmezhau.com'].note -match 'DoT' -and $byId['dns.bezmezhau.com'].note -match 'DoH') 'T5_C08' 'BezMezhau DoT-vs-DoH finding is documented in the catalog' 'R13-CAT'

$progText = Get-Content -LiteralPath (Join-Path $sourceRoot 'Program.cs') -Raw
Assert-Condition ($progText -notmatch '\["P2"\]=\(new\[\]' -and $progText -match 'ProfileCatalog\.BuiltIn\.Profiles') 'T5_C09' 'Program.cs builds KnownProfiles from the catalog instead of hard-coded provider tables' 'R13-CAT'

Assert-Condition ($progText -match 'static async Task<string> FirstTls\(' -and $progText -match 'FirstTls\(h0\.Addresses,host,ct\)' -and $progText -match 'addresses\.Take\(4\)' -and $progText -notmatch 'Tls\(h0\.Addresses\[0\],host\)') 'T5_C10' 'Cancellable preflight TLS validation accepts any reachable answer address (up to four) instead of failing on one dead proxy address' 'R13-CAT'

# ==============================================================================
# R13-PORT: non-443 DoH templates (Astracat :8443) through the engine's registration path (mock cmdlets)
# ==============================================================================
Reset-MockEnvironment (Join-Path $ScratchPath 't5_port')
$script:Servers = @('135.106.217.200')
$script:Template = 'https://dns.astracat.ru:8443/dns-query'
$script:ProfileName = 'dns.astracat.ru'
$portActive = Enable-State $true $false
$portState = Read-State
$portOk = ($portState.Phase -eq 'Active' -and $portState.Template -eq 'https://dns.astracat.ru:8443/dns-query' -and $script:MockDoh.Count -eq 1 -and $script:MockDoh[0].DohTemplate -eq 'https://dns.astracat.ru:8443/dns-query' -and $script:MockDoh[0].ServerAddress -eq '135.106.217.200' -and $script:MockDoh[0].AutoUpgrade -eq $true -and $script:MockDoh[0].AllowFallbackToUdp -eq $false -and $portActive.State -eq 'DNS active')
Assert-Condition $portOk 'T5_P01' 'Add-DnsClientDohServerAddress receives the :8443 template byte-for-byte and the bridge activates' 'R13-PORT'
Restore-State (Read-State) | Out-Null
Assert-Condition ($script:MockDoh.Count -eq 0 -and $script:MockRules.Count -eq 0 -and (Read-State).Phase -eq 'Restored') 'T5_P02' 'Restore removes the :8443 registration and both rules' 'R13-PORT'

$badIndex = 0
foreach ($bad in @('http://dns.example/dns-query', 'ftp://dns.example/dns-query', 'https://203.0.113.5/dns-query', 'https://dns.example:70000/dns-query', 'not a url')) {
    $badIndex++
    Reset-MockEnvironment (Join-Path $ScratchPath 't5_port_bad')
    $script:Servers = @('135.106.217.200'); $script:Template = $bad; $script:ProfileName = 'x'
    $rejected = $false
    try { Enable-State $true $false | Out-Null } catch { $rejected = ($_.Exception.Message -like '*DOH_TEMPLATE_INVALID*') }
    Assert-Condition ($rejected -and $script:MockDoh.Count -eq 0 -and $script:MockRules.Count -eq 0) ("T5_P03_" + $badIndex) "Invalid DoH template '$bad' is refused before any write" 'R13-PORT'
}

# Engine accepts the exe path from the host process (embedded-script mode) and keeps the sibling lookup as the default.
$engineFile = Join-Path $sourceRoot 'Engine.ps1'
$withParam = & (Join-Path $PSHOME 'powershell.exe') -NoProfile -ExecutionPolicy Bypass -Command ". '$engineFile' -LibraryOnly -ExePath 'C:\Staged\bin\AntigravityGeminiDns.exe'; `$script:Exe"
$withoutParam = & (Join-Path $PSHOME 'powershell.exe') -NoProfile -ExecutionPolicy Bypass -Command ". '$engineFile' -LibraryOnly; `$script:Exe"
Assert-Condition ($withParam -eq 'C:\Staged\bin\AntigravityGeminiDns.exe' -and $withoutParam -eq (Join-Path $sourceRoot 'AntigravityGeminiDns.exe')) 'T5_P04' 'Engine.ps1 -ExePath selects the callback exe; library use still defaults to the sibling file' 'R13-PORT'

# ==============================================================================
# R13-EXE: one exe, nothing beside it
# ==============================================================================
Assert-Condition ($haveExe -and @(Get-ChildItem -LiteralPath $clean -Force).Count -eq 1) 'T5_E01' 'The test folder contains exactly one file: the exe' 'R13-EXE'
if ($haveExe) {
    $exeSize = (Get-Item -LiteralPath $exe).Length
    # The final release keeps the full WinForms runtime and compresses the single file without R2R.
    $projectXml=Get-Content -LiteralPath (Join-Path $sourceRoot 'AntigravityGeminiDns.csproj') -Raw
    Assert-Condition ($exeSize -gt 0 -and $exeSize -lt 80MB -and $projectXml -match '<PublishSingleFile>true</PublishSingleFile>' -and $projectXml -match '<PublishTrimmed>false</PublishTrimmed>' -and $projectXml -match '<PublishReadyToRun>false</PublishReadyToRun>' -and $projectXml -match '<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>') 'T5_E02' "Untrimmed compressed single-file configuration; size $([math]::Round($exeSize/1MB,1)) MiB below 80 MiB" 'R13-EXE'

    $selfTest = Invoke-Exe @('--self-test') 150
    $m = [regex]::Match([string]$selfTest.Out, 'OFFLINE_WIRE_PASS (\d+)')
    Assert-Condition ($selfTest.Exit -eq 0 -and $m.Success -and [int]$m.Groups[1].Value -ge 97 -and $selfTest.Out -cnotmatch '(?m)^FAIL') 'T5_E03' "Built-in self-test passes from the lone exe ($($m.Groups[1].Value) checks)" 'R13-EXE' -FailureMessage ((([string]$selfTest.Out) -split "`n" | Where-Object { $_ -cmatch '^FAIL' } | Select-Object -First 3) -join ' | ') + " exit=$($selfTest.Exit) tail: " + ((([string]$selfTest.Out) -split "`n" | Select-Object -Last 2) -join ' | ')

    $verify = Invoke-Exe @('--verify-scripts')
    $vj = $null; try { $vj = $verify.Out | ConvertFrom-Json } catch {}
    $srcHashes = @{}; foreach ($n in 'Engine.ps1', 'Integration.ps1', 'EmergencyRestore.ps1') { $srcHashes[$n] = (Get-FileHash -LiteralPath (Join-Path $sourceRoot $n) -Algorithm SHA256).Hash }
    $hashMatch = $vj -and $vj.Verified -and ($vj.Hashes.'Engine.ps1' -eq $srcHashes['Engine.ps1']) -and ($vj.Hashes.'Integration.ps1' -eq $srcHashes['Integration.ps1']) -and ($vj.Hashes.'EmergencyRestore.ps1' -eq $srcHashes['EmergencyRestore.ps1'])
    Assert-Condition ([bool]$hashMatch) 'T5_E04' 'Scripts embedded in the exe are byte-identical (SHA-256) to src\app\*.ps1' 'R13-EXE'

    $stage = if ($vj) { $vj.Directory } else { '' }
    $localBin = Join-Path $env:LOCALAPPDATA 'AntigravityDnsUnlocker\bin'
    Assert-Condition ($stage -and $stage.StartsWith($localBin, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path (Join-Path $stage 'Engine.ps1'))) 'T5_E05' 'Non-elevated runs stage the scripts under %LocalAppData%, not next to the exe' 'R13-EXE'

    # Tamper with the staged copy: the next run must detect and replace it before launching anything.
    Add-Content -LiteralPath (Join-Path $stage 'Engine.ps1') -Value '# tampered'
    $heal = Invoke-Exe @('--verify-scripts'); $hj = $null; try { $hj = $heal.Out | ConvertFrom-Json } catch {}
    Assert-Condition ($hj -and $hj.Verified -and (Get-FileHash -LiteralPath (Join-Path $stage 'Engine.ps1') -Algorithm SHA256).Hash -eq $srcHashes['Engine.ps1']) 'T5_E06' 'A tampered staged script is detected and replaced by the embedded one' 'R13-EXE'

    # Launch the staged Engine the way the exe does (File + ExePath), on an action that needs no WMI: -Autostart status is read-only.
    $stagedEngine = Join-Path $stage 'Engine.ps1'
    $asRaw = & (Join-Path $PSHOME 'powershell.exe') -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $stagedEngine -Autostart status -ExePath $exe
    $asj = $null; try { $asj = ($asRaw -join "`n") | ConvertFrom-Json } catch {}
    Assert-Condition ($asj -and $asj.Target -eq $exe -and $asj.NonElevated -eq $true -and $asj.EntryName -eq 'AntigravityDnsUnlocker') 'T5_E12' 'The staged Engine.ps1 runs under PowerShell with -ExePath and reports the lone exe as its callback target' 'R13-EXE'

    $exportDir = Join-Path $ScratchPath 'exported'
    $export = Invoke-Exe @('--export-scripts', $exportDir)
    $sums = Get-Content -LiteralPath (Join-Path $exportDir 'SHA256SUMS.txt') -ErrorAction SilentlyContinue
    Assert-Condition ($export.Exit -eq 0 -and ($sums -match $srcHashes['Engine.ps1']) -and (Test-Path (Join-Path $exportDir 'EmergencyRestore.ps1'))) 'T5_E07' '--export-scripts gives the emergency-restore script and a SHA256SUMS list from the lone exe' 'R13-EXE'

    $cfg = Invoke-Exe @('--profiles-list'); $cj = $null; try { $cj = $cfg.Out | ConvertFrom-Json } catch {}
    Assert-Condition ($cj -and $cj.Version -eq $cat.version -and @($cj.Profiles).Count -eq @($cat.profiles).Count) 'T5_E08' 'The exe serves the same catalog version and profile count as source\profiles.json' 'R13-EXE'

    Assert-Condition (@(Get-ChildItem -LiteralPath $clean -Force).Count -eq 1) 'T5_E09' 'After running everything the folder still holds only the exe (no files dropped beside it)' 'R13-EXE'

    # The Run-key override is honoured only inside HKCU\Software\AgDnsTest\ (read-only status query, nothing is written).
    $env:AGDNS_TEST_RUN_KEY = 'Software\Microsoft\Windows\CurrentVersion\Run'
    try { $rk = Invoke-Exe @('--autostart-status'); $rkj = $rk.Out | ConvertFrom-Json } finally { Remove-Item Env:AGDNS_TEST_RUN_KEY -ErrorAction SilentlyContinue }
    $env:AGDNS_TEST_RUN_KEY = 'Software\AgDnsTest\Run'
    try { $rk2 = Invoke-Exe @('--autostart-status'); $rkj2 = $rk2.Out | ConvertFrom-Json } finally { Remove-Item Env:AGDNS_TEST_RUN_KEY -ErrorAction SilentlyContinue }
    Assert-Condition ($rkj.RegistryKey -like '*CurrentVersion\Run' -and $rkj2.RegistryKey -like '*AgDnsTest*') 'T5_E10' 'AGDNS_TEST_RUN_KEY redirects only into HKCU\Software\AgDnsTest\ and is ignored elsewhere' 'R13-EXE'
}

$portableFiles = @(if ($haveExe) { Get-ChildItem -LiteralPath (Split-Path -Parent $sourceExe) -Force | ForEach-Object { $_.Name } })
$allowedPortableFiles = @((Split-Path -Leaf $sourceExe), 'BUILDINFO.txt', 'SHA256SUMS.txt', 'LICENSES.txt')
Assert-Condition (@($portableFiles | Where-Object { $_ -notin $allowedPortableFiles }).Count -eq 0) 'T5_E11' 'The selected directory holds only the candidate exe and optional release metadata (no runtime dependencies, scripts or pdb); other directories are preserved' 'R13-EXE' -FailureMessage ("found: " + ($portableFiles -join ', '))

# ==============================================================================
# R13-BENCH: benchmark on the synthetic wire (dead / slow / unstable / partial)
# ==============================================================================
function New-A([string]$name, [string]$ip) { [Convert]::ToBase64String((New-SyntheticDnsResponse -QuestionDomain $name -QuestionType 1 -Id 1234 -AnswerRecords @((New-ARecord $name $ip)))) }
function New-Nx([string]$name) { [Convert]::ToBase64String((New-SyntheticDnsResponse -QuestionDomain $name -QuestionType 1 -Id 1234 -RCode 3 -AnswerRecords @())) }
$D1 = 'daily-cloudcode-pa.googleapis.com'; $D2 = 'generativelanguage.googleapis.com'
$servers2 = @('193.233.112.67', '193.233.112.68')
function Step([int]$ms, [string]$packet) { @{ ms = $ms; packet = $packet } }
$timeout = @{ timeout = $true }
function New-Prof([string]$id, $servers, $tls, $q1, $q2, $answerTls = @{}) {
    @{ id = $id; template = "https://$id.test/dns-query"; servers = $servers; tls = $tls; queries = @{ $D1 = $q1; $D2 = $q2 }; answerTls = $answerTls }
}
$goodTls = @(@{ ok = $true; ms = 40 })
$scenario = @{
    attempts = 6
    profiles = @(
        (New-Prof 'good' $servers2 $goodTls @((Step 2000 (New-A $D1 '193.233.112.67')), (Step 2000 (New-A $D1 '193.233.112.67')), (Step 60 (New-A $D1 '193.233.112.67')), (Step 60 (New-A $D1 '193.233.112.67')), (Step 60 (New-A $D1 '193.233.112.67')), (Step 60 (New-A $D1 '193.233.112.67'))) @((Step 70 (New-A $D2 '193.233.112.88')))),
        (New-Prof 'slow' $servers2 @(@{ ok = $true; ms = 500 }) @((Step 900 (New-A $D1 '193.233.112.67'))) @((Step 1100 (New-A $D2 '193.233.112.88')))),
        (New-Prof 'flaky' $servers2 $goodTls @((Step 80 (New-A $D1 '193.233.112.67')), $timeout) @((Step 90 (New-A $D2 '193.233.112.88')), $timeout)),
        (New-Prof 'partial' $servers2 $goodTls @((Step 70 (New-A $D1 '193.233.112.67'))) @((Step 40 (New-Nx $D2)))),
        (New-Prof 'dead' @('193.233.112.69') @(@{ ok = $false }) @($timeout) @($timeout)),
        (New-Prof 'shifty' $servers2 $goodTls @((Step 70 (New-A $D1 '193.233.112.67')), (Step 70 (New-A $D1 '193.233.112.68'))) @((Step 70 (New-A $D2 '193.233.112.88')), (Step 70 (New-A $D2 '193.233.112.89')))),
        (New-Prof 'blockedtls' $servers2 $goodTls @((Step 70 (New-A $D1 '193.233.112.67'))) @((Step 70 (New-A $D2 '193.233.112.88'))) @{ $D2 = $false })
    )
}
$scenarioFile = Join-Path $ScratchPath 'bench_scenario.json'
($scenario | ConvertTo-Json -Depth 10) | Set-Content -LiteralPath $scenarioFile -Encoding UTF8

$historyFile = Join-Path $env:LOCALAPPDATA 'AntigravityDnsUnlocker\benchmark-history.json'
$histBefore = if (Test-Path $historyFile) { (Get-FileHash $historyFile -Algorithm SHA256).Hash } else { 'NONE' }
$bench = $null
if ($haveExe) {
    $runOut = Invoke-Exe @('--benchmark-synthetic', $scenarioFile)
    try { $bench = $runOut.Out | ConvertFrom-Json } catch {}
}
$histAfter = if (Test-Path $historyFile) { (Get-FileHash $historyFile -Algorithm SHA256).Hash } else { 'NONE' }
Assert-Condition ($null -ne $bench -and @($bench.Results).Count -eq 7 -and $bench.Synthetic) 'T5_B00' 'Synthetic benchmark runs through the exe CLI and returns 7 profile results' 'R13-BENCH'
$BP = @{}; if ($bench) { foreach ($res in $bench.Results) { $BP[$res.Id] = $res } }  # note: PowerShell variables are case-insensitive ($R would collide with $r)
function V([string]$id) { if ($BP.ContainsKey($id)) { $BP[$id].Verdict } else { 'MISSING' } }

Assert-Condition ((V 'good') -eq 'Good' -and $BP['good'].Availability -eq 1 -and $BP['good'].Stability -eq 1 -and $BP['good'].DomainsOk -and $BP['good'].Selectable) 'T5_B01' 'Healthy profile: Good, availability 100%, stability 100%, both Google domains verified' 'R13-BENCH'
Assert-Condition ($BP['good'].P95Ms -eq 70 -and $BP['good'].MedianMs -ge 60 -and $BP['good'].MedianMs -le 70 -and $BP['good'].TlsMs -eq 40) 'T5_B02' 'Median/p95 are computed from warm queries (the 2000 ms cold queries are excluded); TLS time reported' 'R13-BENCH'
Assert-Condition ((V 'slow') -eq 'Slow' -and $BP['slow'].Selectable -and $BP['slow'].MedianMs -ge 900) 'T5_B03' 'Slow profile: verdict Slow, still selectable' 'R13-BENCH'
Assert-Condition ((V 'flaky') -eq 'Flaky' -and $BP['flaky'].Alive -and -not $BP['flaky'].Selectable -and $BP['flaky'].Availability -lt 0.6) 'T5_B04' 'Unreliable profile: Flaky, alive, not selectable' 'R13-BENCH'
Assert-Condition ((V 'partial') -eq 'Partial' -and $BP['partial'].Alive -and -not $BP['partial'].DomainsOk -and -not $BP['partial'].Selectable) 'T5_B05' 'Profile failing one Google domain: Partial, not selectable' 'R13-BENCH'
Assert-Condition ((V 'dead') -eq 'Dead' -and -not $BP['dead'].Alive -and $BP['dead'].Score -eq 0 -and $BP['dead'].Attempts -eq 1) 'T5_B06' 'Dead profile: Dead, score 0, stopped after one round (no flooding of a dead endpoint)' 'R13-BENCH'
Assert-Condition ((V 'shifty') -eq 'Flaky' -and $BP['shifty'].Stability -le 0.5 -and $BP['shifty'].Availability -eq 1 -and -not $BP['shifty'].Selectable) 'T5_B07' 'Alternating answer sets: stability 50%, Flaky, not selectable' 'R13-BENCH'
Assert-Condition ((V 'blockedtls') -eq 'Partial' -and (@($BP['blockedtls'].Domains | Where-Object { $_.Host -eq $D2 })[0].TlsOk -eq $false)) 'T5_B08' 'Answer IP that fails TLS for a Google domain makes the profile Partial' 'R13-BENCH'
$ranks = @($bench.Results | Sort-Object Rank | ForEach-Object { $_.Id })
Assert-Condition ($bench.Winner -eq 'good' -and $BP['good'].Best -and @($bench.Results | Where-Object { $_.Best }).Count -eq 1 -and $ranks[0] -eq 'good' -and $ranks[-1] -eq 'dead') 'T5_B09' 'Ranking: exactly one highlighted best profile (good) first, dead last' 'R13-BENCH'
Assert-Condition ($histBefore -eq $histAfter) 'T5_B10' 'Synthetic runs never write the benchmark history file' 'R13-BENCH'

$tarpit = @{ attempts = 6; totalBudgetMs = 300; profiles = @(
        @{ id = 'tarpit'; template = 'https://tarpit.test/dns-query'; servers = $servers2; tls = @(@{ ok = $true; ms = 10; delay = 400 }); queries = @{ $D1 = @(@{ ms = 10; delay = 400; packet = (New-A $D1 '193.233.112.67') }); $D2 = @(@{ ms = 10; delay = 400; packet = (New-A $D2 '193.233.112.88') }) }; answerTls = @{} }
    ) }
$tarFile = Join-Path $ScratchPath 'bench_tarpit.json'
($tarpit | ConvertTo-Json -Depth 10) | Set-Content -LiteralPath $tarFile -Encoding UTF8
$sw = [Diagnostics.Stopwatch]::StartNew()
$limited = $null; if ($haveExe) { try { $limited = (Invoke-Exe @('--benchmark-synthetic', $tarFile)).Out | ConvertFrom-Json } catch {} }
$sw.Stop()
Assert-Condition ($limited -and $limited.Truncated -and -not $limited.Cancelled -and $sw.Elapsed.TotalSeconds -lt 20) 'T5_B11' 'Total time budget (300 ms) truncates a tarpit provider run instead of waiting for it' 'R13-BENCH'

# ==============================================================================
# R13-UI: compact layout, main page without card/title/top-right logo, benchmark tab, preserved features
# ==============================================================================
$mf = Get-Content -LiteralPath (Join-Path $sourceRoot 'MainForm.cs') -Raw -Encoding UTF8
$uk = Get-Content -LiteralPath (Join-Path $sourceRoot 'UiKit.cs') -Raw -Encoding UTF8
$serviceUi = Get-Content -LiteralPath (Join-Path $sourceRoot 'MainForm.Service.cs') -Raw -Encoding UTF8
$assistedUi = Get-Content -LiteralPath (Join-Path $sourceRoot 'MainForm.Assisted.cs') -Raw -Encoding UTF8
$connectionTrust = Get-Content -LiteralPath (Join-Path $sourceRoot 'ConnectionTrust.cs') -Raw -Encoding UTF8
$providerConsent = Get-Content -LiteralPath (Join-Path $sourceRoot 'ProviderConsentForm.cs') -Raw -Encoding UTF8
$mainBody = [regex]::Match($mf, 'void BuildMain\(Panel pg\)\{.*?\n  \}', 'Singleline').Value
Assert-Condition ($mainBody.Length -gt 200 -and $mainBody -notmatch 'Card\(' -and $mainBody -notmatch 'RoundPanel') 'T5_U01' 'The main page builds no card: logo, status and buttons sit directly on the page background' 'R13-UI'
$hh = [regex]::Match($mf, 'HeaderH=(\d+)')
Assert-Condition ($mf -notmatch 'title\.(Text|Visible|Bounds)' -and $mf -notmatch 'smallLogo' -and $mf -notmatch '"Запуск"' -and $hh.Success -and [int]$hh.Groups[1].Value -le 12) 'T5_U02' 'No page has a title or a small top-right logo (the form has neither; the pages start under a 12-unit margin at most)' 'R13-UI'
$wh = [regex]::Match($mf, 'const int W=(\d+),H=(\d+)')
# The Material compact canvas keeps the primary flow unscrolled through 150% DPI.
# Smaller work areas use one scrolling viewport; navigation stays pinned.
Assert-Condition ($wh.Success -and [int]$wh.Groups[1].Value -eq 380 -and [int]$wh.Groups[2].Value -eq 584 -and $serviceUi -match 'page.AutoScroll=true' -and $serviceUi -match 'rect.Bottom-nav.Height') 'T5_U03' "The 380x584 DIP canvas keeps a scrolling viewport and bottom navigation" 'R13-UI'
Assert-Condition ($serviceUi -match 'Readiness\.State==ReadinessState\.Ready' -and $serviceUi -match 'Completeness==EvidenceCompleteness\.Complete' -and $serviceUi -match 'Confirmed==serviceReport\.Readiness\.Required' -and $mf -match 'statusLabel\.ForeColor=ServiceReady\?Theme\.Green' -and $mf -match '"Мост подключён"' -and $mf -match 'bigLogo') 'T5_U04' 'Green requires current complete application readiness; connected policy text and central logo are preserved' 'R13-UI'
Assert-Condition ($mf -match 'NotifyIcon' -and $mf -match 'CloseToTray' -and $mf -match 'RegisterShowEvent' -and $progText -match 'StorageName="AntigravityDnsUnlocker"' -and $progText -match '@"Local\\"\+AppIdentity\.StorageName\+"UI-"' -and $assistedUi -match 'AuthorizeConnectionSet' -and $assistedUi -match 'ConnectionTrust.Has' -and $assistedUi -match 'if\(!ProviderConsentForm.Confirm[^\r\n]+return false;' -and $providerConsent -match 'ShowDialog\(owner\)==DialogResult.Yes' -and $connectionTrust -match 'CoveredPins' -and $connectionTrust -match 'EndpointIdentity' -and $mf -match 'TryParseCustom' -and $mf -match 'tg://resolve\?domain=' -and $mf -match 'https://t\.me/' -and $mf -match 'your_sister_loves_me') 'T5_U05' 'Tray, close-to-tray, single instance, trust consent, custom profile and Telegram (tg:// + t.me) are preserved' 'R13-UI'
Assert-Condition ($uk -match 'Labels=\{"Мост","Замер"' -and $uk -match 'class BenchTable' -and $mf -match 'Выбрать лучший' -and $mf -match 'Измерить' -and $mf -match 'benchCts\?\.Cancel') 'T5_U06' 'A "Замер" tab with a results table, Measure/Cancel and "Выбрать лучший" exists' 'R13-UI'
Assert-Condition ($mf -match 'Benchmark\.AppendHistory' -and $mf -match 'LoadBenchHistory' -and $mf -match 'Task\.Run\(\(\)=>Benchmark\.RunAsync' -and $mf -match 'benchCooldownUntil') 'T5_U07' 'Benchmark runs off the UI thread, persists history, and has a cooldown against repeated runs' 'R13-UI'

# Autostart is by consent only.
$asEnableCalls = [regex]::Matches($mf, 'SetEnabled\(true')
# since 2026-10-14 there is no question and no copy: only the switch turns it on and it names the running exe (tier 13 holds the details)
$asConsent = ($mf -match 'EnableForCurrent\(exe' -and $mf -notmatch 'StableCopy' -and $progText -notmatch 'AutostartHelper\.SetEnabled\(true\);\s*\r?\n\s*if\(!' )
Assert-Condition ($asEnableCalls.Count -le 3 -and $asConsent -and $mf -match 'if\(autostartUpdating\)return;') 'T5_U08' 'Autostart is only enabled from the user toggle (it names the running exe; no question, no copy)' 'R13-UI'

if ($haveExe) {
    $shots = Join-Path $ScratchPath 'ui'
    [IO.Directory]::CreateDirectory($shots) | Out-Null
    $smoke = Invoke-Exe @('--ui-smoke', (Join-Path $shots 'smoke.png')) 90
    $sm = [regex]::Match([string]$smoke.Out, 'tabs (\d+); client \{Width=(\d+), Height=(\d+)\}')
    $pngs = @(Get-ChildItem -LiteralPath $shots -Filter '*.png')
    Assert-Condition ($smoke.Exit -eq 0 -and $smoke.Out -match 'UI_SMOKE_PASS' -and $sm.Success -and [int]$sm.Groups[1].Value -eq 5 -and $pngs.Count -eq 5) 'T5_U09' 'ui-smoke from the lone exe renders all 5 tabs' 'R13-UI'

    Add-Type -AssemblyName System.Drawing
    $img = [Drawing.Bitmap]::FromFile((Join-Path $shots 'smoke.png'))
    try {
        # Sample outer body margins, away from the new left status mark and text.
        $px = $img.GetPixel([int]($img.Width * 0.025), [int]($img.Height * 0.25))
        $logoRowBg = $img.GetPixel([int]($img.Width * 0.975), [int]($img.Height * 0.25))
    } finally { $img.Dispose() }
    $themeSource = Get-Content -LiteralPath (Join-Path $sourceRoot 'UiKit.cs') -Raw -Encoding UTF8
    $bgMatch = [regex]::Match($themeSource, 'Bg=Color.FromArgb\((\d+),(\d+),(\d+)\)')
    $expectedBg = [Drawing.Color]::FromArgb([int]$bgMatch.Groups[1].Value,[int]$bgMatch.Groups[2].Value,[int]$bgMatch.Groups[3].Value)
    Assert-Condition ($bgMatch.Success -and $px.ToArgb() -eq $expectedBg.ToArgb() -and $logoRowBg.ToArgb() -eq $expectedBg.ToArgb()) 'T5_U10' "Main page pixels beside the content match the theme background, no raised card (sample $($px.R),$($px.G),$($px.B))" 'R13-UI'
    $renderDpi = [regex]::Match([string]$smoke.Out, '; dpi (\d+)')
    $renderScale = if ($renderDpi.Success) { [double]$renderDpi.Groups[1].Value / 96 } else { 0 }
    Assert-Condition ($sm.Success -and $renderScale -gt 0 -and [math]::Abs([double]$sm.Groups[2].Value - 380*$renderScale) -le 2 -and [math]::Abs([double]$sm.Groups[3].Value - 584*$renderScale) -le 2) 'T5_U11' 'Rendered client preserves the design dimensions at the actual Windows DPI' 'R13-UI'
}

Assert-HostSafetyBaselineUnchanged "Tier 5 Completion"
Write-Host "--- Tier 5 Completed: $($global:TestResults.Count) Tests Run ---" -ForegroundColor Cyan
