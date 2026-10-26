param([string]$Out='.work\build-dev',[string]$Src='app')
# Quick loop: build into src\.work\build-dev and run the self-test (paths relative to src\).
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dn=if($env:AGDNS_DOTNET){$env:AGDNS_DOTNET}else{'dotnet'}
& powershell.exe -NoProfile -File (Join-Path $root "$Src\Build.ps1") -Dotnet $dn -OutputDirectory (Join-Path $root $Out)
if($LASTEXITCODE -ne 0){exit 1}
& (Join-Path $root "$Out\AntigravityGeminiDns.exe") --self-test | Select-Object -Last 3
exit $LASTEXITCODE
