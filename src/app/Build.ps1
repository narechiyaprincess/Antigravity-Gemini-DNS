param([string]$Dotnet='dotnet',[string]$OutputDirectory=(Join-Path $PSScriptRoot '..\.work\build'),[string]$BuildRoot=(Join-Path ([IO.Path]::GetTempPath()) ('AntigravityGeminiDnsBuild-'+[guid]::NewGuid().ToString('N'))),[string]$RestoreSource='')
$ErrorActionPreference='Stop'
# Safety: a heavy build must not start on a nearly full disk (override: $env:AGDNS_MIN_FREE_GB).
$minFreeGb=10;if($env:AGDNS_MIN_FREE_GB -match '^\d+$'){$minFreeGb=[int]$env:AGDNS_MIN_FREE_GB}
foreach($p in @($BuildRoot,(Join-Path $PSScriptRoot '..'))){
 $drv=[IO.Path]::GetPathRoot([IO.Path]::GetFullPath($p));$di=New-Object IO.DriveInfo($drv)
 if($di.AvailableFreeSpace -lt ($minFreeGb*1GB)){throw ('Build aborted: only {0:N1} GB free on {1} (minimum {2} GB)' -f ($di.AvailableFreeSpace/1GB),$drv,$minFreeGb)}
}
# Only a build root created by this script (default under %TEMP%) is removed afterwards; env changes are process-local and restored.
$ownRoot=-not $PSBoundParameters.ContainsKey('BuildRoot')
$saved=@{};foreach($n in 'DOTNET_CLI_HOME','DOTNET_GENERATE_ASPNET_CERTIFICATE','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_ADD_GLOBAL_TOOLS_TO_PATH','DOTNET_SKIP_FIRST_TIME_EXPERIENCE','DOTNET_NOLOGO'){$saved[$n]=[Environment]::GetEnvironmentVariable($n,'Process')}
try{
 $env:DOTNET_CLI_HOME=Join-Path $BuildRoot 'cli-home'
 $env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
 $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
 # Root cause of PATH bloat: with a fresh DOTNET_CLI_HOME every build is a "first run", and the dotnet CLI then
 # persists <DOTNET_CLI_HOME>\.dotnet\tools into the USER PATH in the registry (one dead entry per build).
 $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
 $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
 $env:DOTNET_NOLOGO='1'
 New-Item -ItemType Directory -Force -Path $BuildRoot | Out-Null
 $bin=Join-Path $BuildRoot 'bin';$obj=Join-Path $BuildRoot 'obj'
 $restoreArgs=@();if($RestoreSource){$restoreArgs+=('-p:RestoreSources='+[IO.Path]::GetFullPath($RestoreSource))}
 & $Dotnet publish (Join-Path $PSScriptRoot 'AntigravityGeminiDns.csproj') -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:Deterministic=true "-p:PathMap=$PSScriptRoot=/_/src/app" -o $OutputDirectory "-p:BaseOutputPath=$bin/" "-p:BaseIntermediateOutputPath=$obj/" @restoreArgs --nologo
 if($LASTEXITCODE -ne 0){throw 'Publish failed'}
 Write-Output ('Portable folder: '+[IO.Path]::GetFullPath($OutputDirectory))
}finally{
 foreach($n in $saved.Keys){[Environment]::SetEnvironmentVariable($n,$saved[$n],'Process')}
 if($ownRoot -and (Split-Path -Leaf $BuildRoot) -like 'AntigravityGeminiDnsBuild-*' -and (Test-Path -LiteralPath $BuildRoot)){
  try{[IO.Directory]::Delete($BuildRoot,$true)}catch{Write-Warning ('Build temp not removed: '+$BuildRoot)}
 }
}
