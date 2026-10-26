# t12_ui_v3.ps1 - Tier 12: bridge card, scope selector, browser dialog and icons. Offline only.
# Source contracts and the documents; the behaviour itself is in the exe's self-test (OfflineTests.UiV3.cs) and in --ui-smoke --states.
# This file is saved with a BOM: Windows PowerShell 5.1 mangles non-ASCII text in a script without one.

param([string]$ScratchPath,[switch]$EscalateKnownDefects)

$ErrorActionPreference = 'Stop'
$global:CurrentTier = "Tier 12: interface pass v3"
$script:CurrentTier = $global:CurrentTier
$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\app')).Path
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'helpers\TestHarness.ps1')

Write-Host "--- Executing Tier 12: interface pass v3 Tests ---" -ForegroundColor Cyan

function Get-Src([string]$Name) { Get-Content -LiteralPath (Join-Path $sourceRoot $Name) -Raw -Encoding UTF8 }

# N01: the program is called "Antigravity-Gemini DNS" in every string it shows; registry keys, mutexes and data folders keep the old identifiers (AppIdentity.StorageName, see docs/COMPATIBILITY-IDENTIFIERS.md)
$offenders = @()
foreach ($f in Get-ChildItem -LiteralPath $sourceRoot -Filter *.cs) {
    if ($f.Name -like 'OfflineTests*') { continue }
    foreach ($m in [regex]::Matches((Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8), '"[^"\r\n]*(Antigravity (DNS )?Unlocker|DNS Unlocker)[^"\r\n]*"')) { $offenders += "$($f.Name) :: $($m.Value)" }
}
Assert-Condition ($offenders.Count -eq 0) 'T12_N01' 'No string of the program says "Antigravity Unlocker" or "DNS Unlocker" (window, tray, notifications, dialogs, info, report, help)' 'R-UIV3' -FailureMessage (($offenders | Select-Object -First 5) -join ' | ')
$csproj = Get-Src 'AntigravityGeminiDns.csproj'
$mf = Get-Src 'MainForm.cs'
$programIdentity = Get-Src 'Program.cs'
Assert-Condition ($csproj -match '<AssemblyName>AntigravityGeminiDns</AssemblyName>' -and $csproj -match '<Version>1\.1\.0</Version>' -and $programIdentity -match 'StorageName="AntigravityDnsUnlocker"' -and $programIdentity -match 'ValueName=AppIdentity\.StorageName' -and $programIdentity -match '@"Local\\"\+AppIdentity\.StorageName\+"UI-"' -and $programIdentity -match '@"Local\\"\+AppIdentity\.StorageName\+"Show-"' -and $programIdentity -match 'Path.GetFullPath\(Environment.ProcessPath' -and $programIdentity -match 'SHA256.HashData') 'T12_N02' 'The assembly name, version 1.1.0 and the compatibility registry/mutex identity are preserved' 'R-UIV3'

# N03: "DNS is not proxied": no text says so; the one place that names a proxy says what it is (the provider's intermediary server)
$lang = Get-Src 'Lang.cs'
$proxyLines = @($lang -split "`n" | Where-Object { $_ -match 'прокси|[Pp]roxy' })
$bad = @($proxyLines | Where-Object { $_ -notmatch 'сервер-посредник \(SNI-прокси\)|intermediary server \(SNI proxy\)' })
Assert-Condition ($lang -notmatch 'проксир' -and $bad.Count -eq 0) 'T12_N03' 'The dictionary never says "проксировать"/"прокси DNS"; "SNI-прокси" appears only as the explanation of the provider''s intermediary server in the trust question' 'R-UIV3' -FailureMessage (($bad | Select-Object -First 3) -join ' | ')

# B01: the browser check only reads: no registry write, no file write, no other program started; the only start is the button's
$br = Get-Src 'Browsers.cs'
$bw = Get-Src 'MainForm.Browser.cs'
Assert-Condition ($br -notmatch 'SetValue|CreateSubKey|DeleteValue|DeleteSubKey|WriteAllText|WriteAllBytes|File\.Delete|File\.Move|File\.Copy|FileMode\.(Create|Append|OpenOrCreate|Truncate)|FileAccess\.(Write|ReadWrite)|Process\.Start|ProcessStartInfo|OpenSubKey\([^)]*,\s*true' -and $br -match 'FileAccess\.Read') 'T12_B01' 'Browsers.cs reads the registry and the browsers'' files and writes nothing' 'R-UIV3'
$starts = [regex]::Matches($bw, 'Process\.Start\(').Count
$openBlock = [regex]::Match($bw, 'void OpenBrowserSettings\(\)\{.*?\n  \}', 'Singleline').Value
Assert-Condition ($starts -eq 1 -and $openBlock -match 'Process\.Start\(' -and $openBlock -match 'info\.Exe' -and $openBlock -match 'SettingsUrl' -and $bw -notmatch 'Registry\.') 'T12_B02' 'The browser panel starts exactly one program, only in the handler of its button: the browser''s own exe with its settings address' 'R-UIV3'
# the change of scope waits for the answer; a refusal puts the selector back
$osel = [regex]::Match($mf, 'async void OnScopeSelected\(\)\{.*?\n  \}', 'Singleline').Value
Assert-Condition ($osel -match 'AskBrowserAsync\(\)' -and $osel -match 'if\(!await AskBrowserAsync\(\)\)\{SetScope\(ScopeNow\);return;\}' -and $osel.IndexOf('AskBrowserAsync') -lt $osel.IndexOf('Mutate("enable")') -and $osel.IndexOf('AskBrowserAsync') -lt $osel.IndexOf('SetScope(want)')) 'T12_B03' 'Choosing «Весь Google» asks for the browser before anything is applied; backing out restores the selector' 'R-UIV3'
Assert-Condition ($bw -match 'settings\.Browser=item\.Id') 'T12_B04' 'The chosen browser is remembered in the settings (AppSettings.Browser), not in the system' 'R-UIV3'
$ui = Get-Src 'UiKit.cs'
Assert-Condition ($ui -match 'public string Browser=""') 'T12_B05' 'AppSettings has the Browser field' 'R-UIV3'


# I01: info page text
Assert-Condition ($mf -match 'Российским аккаунтам для стабильной работы нужен ещё Antigravity Patcher\.' -and $mf -match 'tgRow\.Subtitle=L\.T\("Автор Antigravity-Gemini DNS"\)' -and $mf -match 'MakeLabel\("Antigravity-Gemini DNS",' -and $mf -match 'MakeLabel\("by "\+Credit,') 'T12_I01' 'Info: the patcher sentence, the contact caption «Автор Antigravity-Gemini DNS» and the card title «Antigravity DNS / by …»' 'R-UIV3'

# S01: the live-window stress (per-monitor DPI changes, animation, real mouse messages, tips, the browser question, repeated repaints) paints without a single failure
$sourceExe = if ($env:AGDNS_EXE) { $env:AGDNS_EXE } else { Join-Path $projectRoot '..\release\AntigravityGeminiDns.exe' }
if (Test-Path -LiteralPath $sourceExe) {
    $psi = New-Object Diagnostics.ProcessStartInfo $sourceExe
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.Arguments = '--ui-stress'
    $p = [Diagnostics.Process]::Start($psi); $so = $p.StandardOutput.ReadToEndAsync(); $se = $p.StandardError.ReadToEndAsync()
    $done = $p.WaitForExit(240000)
    if (-not $done) { try { $p.Kill() } catch {} }
    $out = if ($done) { $so.Result } else { 'TIMEOUT' }
    $err = if ($done) { $se.Result } else { 'TIMEOUT: stderr collection incomplete' }
    [void][IO.Directory]::CreateDirectory($ScratchPath)
    [IO.File]::WriteAllText((Join-Path $ScratchPath 'ui-stress.stdout.txt'), $out)
    [IO.File]::WriteAllText((Join-Path $ScratchPath 'ui-stress.stderr.txt'), $err)
    $exit = if ($done) { $p.ExitCode } else { $null }
    [pscustomobject]@{Completed=$done;ExitCode=$exit;CandidateSha256=(Get-FileHash -LiteralPath $sourceExe).Hash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ScratchPath 'ui-stress-process.json')
    Assert-Condition ($done -and $p.ExitCode -eq 0 -and $out -match 'UI_STRESS_PASS') 'T12_S01' 'The window survives the live-conditions stress (--ui-stress): DPI changes, animation, mouse, tips, the browser question, repaints' 'R-UIV3' -FailureMessage ('exit=' + $exit + '; ' + (($out -split "`n" | Select-Object -Last 6) -join ' | ') + '; stderr=' + $err)
}

# S02: a wrong operation id is refused before anything is started (a mutation must not run and then lose its receipt)
if (Test-Path -LiteralPath $sourceExe) {
    $psi2 = New-Object Diagnostics.ProcessStartInfo $sourceExe
    $psi2.UseShellExecute = $false; $psi2.CreateNoWindow = $true; $psi2.RedirectStandardOutput = $true; $psi2.RedirectStandardError = $true; $psi2.Arguments = '--status --operation-id not-a-guid'
    $p2 = [Diagnostics.Process]::Start($psi2); $so2 = $p2.StandardOutput.ReadToEndAsync(); $se2 = $p2.StandardError.ReadToEndAsync()
    $done2 = $p2.WaitForExit(60000); if (-not $done2) { try { $p2.Kill() } catch {} }
    Assert-Condition ($done2 -and $p2.ExitCode -eq 1 -and $se2.Result -match 'Invalid operation ID' -and [string]::IsNullOrWhiteSpace($so2.Result)) 'T12_S02' 'An invalid --operation-id is refused up front (exit 1, nothing run, nothing printed)' 'R-AUDIT' -FailureMessage ($se2.Result + $so2.Result)
}

# A01: the staging of the elevated copy (audit of the core, 2026-10-14): protected directories are born protected, and the exe is read once (hashed and copied from the same bytes)
$ss = Get-Src 'ScriptStore.cs'
$ens = [regex]::Match($ss, 'public static void EnsureProtectedDirectory\(string path\) \{.*?'+[char]10+'  \}', 'Singleline').Value
$prep = [regex]::Match($ss, 'public static Prepared Prepare\(.*?'+[char]10+'  \}', 'Singleline').Value
Assert-Condition ($ens -match 'CreateProtected\(path\)' -and $ens -notmatch 'di\.Create\(\)' -and $prep -match 'exeBytes' -and $prep -match 'CheckRunningImage\(exeBytes' -and $prep -notmatch 'File\.Copy\(' -and $prep -notmatch 'Sha256File\(exeSrc\)' -and $prep -match 'FileShare\.Read') 'T12_A01' 'The protected directory is created with its rules in one call and the elevated exe copy is hashed and written from one read of the file (no gap between hashing and copying)' 'R-AUDIT'

# A02: the engine's own state directory is created through the protected-creation helper (no plain CreateDirectory in Initialize-StateDirectory)
$engSrc = Get-Src 'Engine.ps1'
$isd = [regex]::Match($engSrc, 'function Initialize-StateDirectory \{.*?'+[char]10+'\}', 'Singleline').Value
Assert-Condition ($isd -match 'New-ProtectedDirectory \$script:Root' -and $isd -notmatch 'CreateDirectory\(\$script:Root\)' -and $isd -match 'New-ProtectedDirectoryAcl') 'T12_A02' 'Initialize-StateDirectory creates the state directory with its protected rules in one call and reuses the same rule set afterwards' 'R-AUDIT'

# R01: certificate revocation with soft failure for unreachable revocation servers: every place that validates a provider's or Google's certificate asks CertRevocation
$ok = $true; $missing = @()
foreach ($f in @('Program.cs', 'Network.cs', 'Benchmark.cs')) { if ((Get-Src $f) -notmatch 'RemoteCertificateValidationCallback=CertRevocation\.Callback') { $ok = $false; $missing += $f } }
if ((Get-Src 'Probing.cs') -notmatch 'new X509Certificate2\(rc\)' -or (Get-Src 'Probing.cs') -notmatch 'using\(copy\)return CertRevocation\.IsRevoked\(copy\)' -or (Get-Src 'Probing.cs') -notmatch 'WaitAsync\(cts.Token\)') { $ok = $false; $missing += 'Probing.cs' }
$cr = Get-Src 'CertRevocation.cs'
Assert-Condition ($ok -and $cr -match 'X509RevocationMode\.Online' -and $cr -match 'X509ChainStatusFlags\.Revoked' -and $cr -match 'Unknown' -and $cr -match 'FromHours\(6\)') 'T12_R01' 'Every TLS check of a provider or Google (HTTP probes, DoH, benchmark, TLS-on-name) refuses an explicitly revoked certificate; an unknown status is no refusal; one look per certificate per six hours' 'R-AUDIT' -FailureMessage ($missing -join ', ')
