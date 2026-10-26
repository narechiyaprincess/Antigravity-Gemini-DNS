# Non-interactive technical gate shared by CI and manual verification. No live DNS operations.
param([string]$Dotnet='')
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$src=Join-Path $repo 'src'
$log=Join-Path $src '.work\logs\ci'
New-Item -ItemType Directory -Force -Path $log | Out-Null
$dn=if($Dotnet){$Dotnet}elseif($env:AGDNS_DOTNET){$env:AGDNS_DOTNET}else{'dotnet'}

Push-Location $repo
try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src 'app\Build.ps1') -Dotnet $dn 2>&1 | Tee-Object -FilePath (Join-Path $log 'build.txt')
    if($LASTEXITCODE -ne 0){throw 'Build failed'}
    $exe=(Resolve-Path (Join-Path $src '.work\build\AntigravityGeminiDns.exe')).Path
    $project=[xml](Get-Content -LiteralPath (Join-Path $src 'app\AntigravityGeminiDns.csproj') -Raw)
    $version=(& $exe --version | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $version -ne [string]$project.Project.PropertyGroup.Version){throw 'EXE/project version mismatch'}

    $selfTest=@(& $exe --self-test)
    $selfExit=$LASTEXITCODE
    $selfTest | Tee-Object -FilePath (Join-Path $log 'selftest.txt')
    if($selfExit -ne 0 -or ($selfTest -join "`n") -notmatch '(?m)^OFFLINE_WIRE_PASS \d+\s*$'){throw 'Self-test failed or success marker missing'}

    $scripts=@(& $exe --verify-scripts)
    $scriptsExit=$LASTEXITCODE
    $scripts | Tee-Object -FilePath (Join-Path $log 'verify-scripts.json')
    if($scriptsExit -ne 0 -or ($scripts -join "`n" | ConvertFrom-Json).Verified -ne $true){throw 'Embedded script verification failed'}

    $engineScratch=Join-Path $src ('.work\engine-ci-'+[guid]::NewGuid().ToString('N'))
    $engineTest=@(& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $src 'app\Engine.Tests.ps1') -ScratchPath $engineScratch)
    $engineExit=$LASTEXITCODE
    $engineTest | Tee-Object -FilePath (Join-Path $log 'engine.txt')
    if($engineExit -ne 0 -or ($engineTest -join "`n") -notmatch '(?m)^ENGINE_OFFLINE_PASS \d+\s*$'){throw 'Offline engine tests failed or success marker missing'}

    # Tier 14 checks module-path hardening and packaging without UAC or fake-CIM assumptions.
    # The All tier includes tier 6's non-elevated CIM seam; run the full wrapper locally instead.
    $savedExe=$env:AGDNS_EXE
    try {
        $env:AGDNS_EXE=$exe
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src 'tests\run_offline_e2e_tests.ps1') -Tier 14 2>&1 | Tee-Object -FilePath (Join-Path $log 'e2e.txt')
        if($LASTEXITCODE -ne 0){throw 'Offline tier 14 failed'}
        $receipt=Get-Content -LiteralPath (Join-Path $src '.work\offline_e2e_receipt.json') -Raw | ConvertFrom-Json
        if($receipt.Completed -ne $true -or $receipt.HostUnchanged -ne $true -or $receipt.TotalTests -le 0 -or $receipt.PassCount -ne $receipt.TotalTests -or $receipt.DefectCount -ne 0 -or $receipt.FailOtherCount -ne 0){throw 'Offline receipt did not pass'}
    } finally {
        $env:AGDNS_EXE=$savedExe
    }
    Write-Output ('CI_VERIFY_PASS version='+$version)
} finally {
    Pop-Location
}
