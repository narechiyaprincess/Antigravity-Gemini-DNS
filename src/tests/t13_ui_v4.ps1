# t13_ui_v4.ps1 - Tier 13: autostart, immediate scope changes, profile latency,
# the browser screen, the language switched without a restart, the tab bar highlight. Offline only.
# Source contracts; the behaviour itself is in the exe's self-test (OfflineTests.UiV4.cs) and in --ui-smoke --dpi N.
# This file is saved with a BOM: Windows PowerShell 5.1 mangles non-ASCII text in a script without one.

param([string]$ScratchPath,[switch]$EscalateKnownDefects)

$ErrorActionPreference = 'Stop'
$global:CurrentTier = "Tier 13: interface pass v4"
$script:CurrentTier = $global:CurrentTier
$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\app')).Path
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'helpers\TestHarness.ps1')

Write-Host "--- Executing Tier 13: interface pass v4 Tests ---" -ForegroundColor Cyan

function Get-Src([string]$Name) { Get-Content -LiteralPath (Join-Path $sourceRoot $Name) -Raw -Encoding UTF8 }
function Get-Block([string]$Text, [string]$Pattern) { [regex]::Match($Text, $Pattern, 'Singleline').Value }

$mf = Get-Src 'MainForm.cs'
$prog = Get-Src 'Program.cs'
$ui = Get-Src 'UiKit.cs'
$bw = Get-Src 'MainForm.Browser.cs'

# A: autostart
$allSrc = (Get-ChildItem -LiteralPath $sourceRoot -Filter *.cs | Where-Object { $_.Name -notlike 'OfflineTests*' } | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }) -join "`n"
Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'Install.cs')) -and $allSrc -notmatch 'StableCopy' -and $allSrc -notmatch 'LocalApplicationData\)\s*,\s*"Programs"') 'T13_A01' 'There is no stable copy any more: no StableCopy class, no %LocalAppData%\Programs' 'R-UIV4'
Assert-Condition ($mf -notmatch 'Автозапуск указывает на другой файл' -and $mf -notmatch 'CheckAutostartTarget' -and $mf -notmatch 'MessageBoxButtons\.YesNoCancel') 'T13_A02' 'The question «Автозапуск указывает на другой файл» and its code are gone; nothing at start asks anything' 'R-UIV4'
$tog = Get-Block $mf 'void OnAutostartToggled\(\)\{.*?\n  \}'
Assert-Condition ($tog -match 'EnableForCurrent\(exe' -and $tog -notmatch 'MessageBox\.Show\(this,L\.T\("(Программа|Автозапуск Windows)') 'T13_A03' 'Turning autostart on names the running exe and asks nothing (the only message box is the failure of the registry write)' 'R-UIV4'
$setTrue = [regex]::Matches($allSrc, 'SetEnabled\(true').Count
$repointCalls = [regex]::Matches($allSrc, '(?m)^[^\r\n]*\bRepointIfMissing\s*\(') | Where-Object { $_.Value -notmatch 'public static bool RepointIfMissing\s*\(' -and $_.Value -notmatch '^\s*//' }
Assert-Condition ($setTrue -eq 4 -and @($repointCalls).Count -eq 1 -and $mf -match 'if\(!smoke\)AutostartHelper.RepointIfMissing' -and $mf -match 'AutostartHelper.RebindOwnCopy\(Environment.ProcessPath\)' -and $prog -match 'File.Exists\(target\)' -and $prog -match '!Directory.Exists\(root\)' -and $tog -match 'EnableForCurrent\(exe' -and $tog -match 'if\(autostartUpdating\)return;') 'T13_A04' 'Normal launch repairs a missing registered EXE on an available drive and silently rebinds an entry that names another copy of this same program (foreign exes and absent registrations remain untouched); explicit Settings toggle registers current EXE' 'R-UIV4'
$rm = Get-Block $prog 'public static CopyRemoval RemovePreviousCopy.*?\n  \}'
Assert-Condition ($rm -match 'ProductOf' -and $rm -match 'IsRunning' -and $rm -match 'ThisCopy' -and $rm -match 'File\.Delete\(path\)' -and ([regex]::Matches($allSrc,'File\.Delete\(').Count -le 10 -and $allSrc -match 'File\.Delete\(InstalledExe\)')) 'T13_A05' 'A previous copy is deleted only after the ProductName, not-running and not-this-exe checks (the other File.Delete calls belong to the protected administrator-startup copy and its temp files)' 'R-UIV4'
Assert-Condition ($mf -match 'DeleteConfirm!=null\?DeleteConfirm\(path\):MessageBox\.Show' -and $mf -match 'prevDelete') 'T13_A06' 'Deleting a previous copy is behind a confirmation' 'R-UIV4'

# B: levels
$osel = Get-Block $mf 'async void OnScopeSelected\(\)\{.*?\n  \}'
Assert-Condition ($osel.Length -gt 200 -and $osel -notmatch 'MessageBox' -and $osel -notmatch 'ScopeConfirm' -and $osel -match 'scopeApplying=true' -and $osel -match 'finally\{scopeApplying=false;\}' -and $osel -match 'SetScope\(prev\)') 'T13_B01' 'Choosing a level asks no question, shows busy while it is applied, and puts the selector back on failure' 'R-UIV4'
Assert-Condition ($ui -match 'public bool Busy' -and $ui -match 'if\(!Enabled\|\|busy\)return;') 'T13_B02' 'The levels control has a busy state that ignores clicks' 'R-UIV4'

# C: card and ping
Assert-Condition ($ui -notmatch 'class BridgeRows' -and $mf -notmatch 'BridgeRows' -and $mf -notmatch 'summary\.Set' -and $mf -notmatch 'L\.T\("Пинг"\),') 'T13_C01' 'The main page has no card with «Пинг» and «DNS» rows' 'R-UIV4'
Assert-Condition ($mf -match 'profileSelect\.Detail=PingDetail' -and $mf -match 'MeasureProfilesAsync' -and $mf -match 'SemaphoreSlim\(3\)' -and $ui -match 'DropDownOpening') 'T13_C02' 'The ping sits in the profile field and in every row of the list; the list is measured in the background, three at a time' 'R-UIV4'
$mp = Get-Block $mf 'async Task MeasureProfilesAsync\(bool force\)\{.*?\n  \}'
Assert-Condition ($mp -notmatch 'Elevate|Engine\(|Mutate|SetValue|Registry' -and $mp -match 'BridgeSpeed\.MeasureAsync') 'T13_C03' 'Measuring the list is read-only: DoH queries and TLS handshakes, no engine, no UAC, no registry' 'R-UIV4'

# D: browser screen
Assert-Condition ($bw -notmatch 'class VerdictBox' -and $bw -match 'class VerdictLine' -and $bw -match 'browserBack' -and $mf -match 'keyData==Keys\.Escape&&browserPanel!=null&&browserPanel\.Visible') 'T13_D01' 'The browser screen has a one-line verdict instead of a card, a «Назад» button, and Esc closes it' 'R-UIV4'

# E: language without a restart
Assert-Condition ($prog -notmatch '--after' -and $mf -notmatch '--after' -and $mf -notmatch 'Process\.Start\(new ProcessStartInfo\(Environment\.ProcessPath\)' -and $mf -match 'internal void ApplyLanguage\(bool en\)') 'T13_E01' 'The language switch rewrites the window; the restart path (--after) is gone' 'R-UIV4'
$lang = Get-Src 'Lang.cs'
Assert-Condition ($lang -match 'public static string Swap\(string text,bool toEnglish\)' -and $lang -match 'SharedEnglish') 'T13_E02' 'The dictionary can be swapped both ways and a self-test keeps it reversible' 'R-UIV4'

# F: tab bar
$nav = Get-Block $ui 'internal sealed class NavBar:Surface\{.*'
Assert-Condition ($nav -match 'internal static Rectangle Pill\(' -and $nav -match 'internal (?:new )?static void Paint\(' -and $nav -notmatch 'w-12\*k' -and $nav -match 'NoClipping') 'T13_F01' 'The tab bar highlight comes from one function: connected full-height sectors with a shared paint path' 'R-UIV4'

Assert-HostSafetyBaselineUnchanged "Tier 13 Completion"
Write-Host "--- Tier 13 Completed: $($global:TestResults.Count) Tests Run ---" -ForegroundColor Cyan
