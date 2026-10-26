param([string]$Tag='run',[switch]$NoBuild,[switch]$Publish,[switch]$NoPublish,[string]$OutDir='.work\build',[string]$ExeOverride='',[string]$Dotnet='')
# Build, offline tests, UI checks and standalone EXE verification.
# Logs: src\.work\logs\<Tag>. release\ and the shortcut are updated ONLY with -Publish, after all checks pass.
# -NoPublish is accepted as a no-op for older commands; it cannot be combined with -Publish.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'BoundedProcess.ps1')
if($Publish -and $NoPublish){throw 'CONFLICT: -Publish and -NoPublish are mutually exclusive; nothing was built, tested or published'}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
$work=Join-Path $root '.work'
# SDK selection: -Dotnet, AGDNS_DOTNET, then PATH.
$dn=if($Dotnet){$Dotnet}elseif($env:AGDNS_DOTNET){$env:AGDNS_DOTNET}else{'dotnet'}
$log=Join-Path $work "logs\$Tag"
if(Test-Path -LiteralPath $log){throw 'LOG_TAG_ALREADY_EXISTS: choose a fresh -Tag to preserve evidence'}
New-Item -ItemType Directory $log|Out-Null
if(Test-Path -LiteralPath 'HKCU:\Software\AgDnsTest'){throw 'PREEXISTING_TEST_REGISTRY: preserve HKCU\Software\AgDnsTest before verification'}
$ui=Join-Path $log 'ui'
New-Item -ItemType Directory -Force (Join-Path $ui 'states'),(Join-Path $ui 'states-dpi')|Out-Null
$outFull=if([IO.Path]::IsPathRooted($OutDir)){$OutDir}else{Join-Path $root $OutDir}
$exeP=if($ExeOverride){$ExeOverride}else{Join-Path $outFull 'AntigravityGeminiDns.exe'}
$env:AGDNS_EXE=$exeP
function RunP($file,$argl,$out,$to){
 $isPS=([IO.Path]::GetFileName($file) -ieq 'powershell.exe')
 if($isPS){$file=Get-WindowsPowerShellPath}
 $r=Invoke-BoundedProcess -FilePath $file -Arguments $argl -WorkingDirectory $root -TimeoutMilliseconds ($to*1000) -WindowsPowerShell:$isPS
 Set-Content -Encoding UTF8 -LiteralPath $out ($r.Stdout+$r.Stderr+$r.Error)
 $r | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 -LiteralPath ($out+'.process.json')
 return $r.ExitCode
}
if(-not $NoBuild){
 $b=RunP 'powershell.exe' "-NoProfile -File `"$root\app\Build.ps1`" -Dotnet `"$dn`" -OutputDirectory `"$outFull`"" (Join-Path $log 'build.txt') 600
 "BUILD exit=$b"
 if($b -ne 0){exit 1}
}
$s=RunP $exeP '--self-test' (Join-Path $log 'selftest.txt') 300
"SELFTEST exit=$s"
$e=RunP 'powershell.exe' "-NoProfile -ExecutionPolicy Bypass -File `"$root\tests\run_offline_e2e_tests.ps1`" -ReceiptPath `"$log\offline_e2e_receipt.json`"" (Join-Path $log 'e2e.txt') 1500
"E2E exit=$e"
$u=RunP $exeP "--ui-smoke `"$ui\smoke.png`"" (Join-Path $log 'ui.txt') 300
"UISMOKE exit=$u"
$u2=RunP $exeP "--ui-smoke `"$ui\states\smoke.png`" --states" (Join-Path $log 'ui_states.txt') 300
"UISMOKE_STATES exit=$u2"
$u3=RunP $exeP '--ui-stress' (Join-Path $log 'ui_stress.txt') 240
"UISTRESS exit=$u3"
$u4=RunP $exeP '--ui-surfaces' (Join-Path $log 'ui_surfaces.txt') 120
"UISURFACES exit=$u4"
# The main page at 100 / 125 / 150 / 200 % in both languages, each in a fresh process, isolated under this tag.
$scaleBad=0
foreach($d in 96,120,144,192){foreach($en in '','--en'){
 $r=RunP $exeP "--ui-smoke `"$ui\states-dpi\s$d$en.png`" --states --dpi $d $en" (Join-Path $log "ui_scale_$d$en.txt") 300
 if($r -ne 0){$scaleBad++}
}}
"UISCALE failures=$scaleBad"
# the exe alone in an empty folder: nothing next to it is needed
$empty=Join-Path ([IO.Path]::GetTempPath()) ('agdns-empty-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $empty|Out-Null
$emptyBad=0
try{
 # under the name it is published with (release\AntigravityGeminiDns.exe): nothing in the program depends on its file name
 $lone=Join-Path $empty 'AntigravityGeminiDns.exe'
 Copy-Item -LiteralPath $exeP -Destination $lone
 $csv=[regex]::Match((Get-Content -LiteralPath (Join-Path $root 'app\AntigravityGeminiDns.csproj') -Raw),'<Version>([^<]+)</Version>').Groups[1].Value
 $r1=RunP $lone '--version' (Join-Path $log 'empty_version.txt') 60
 $ver=((Get-Content -LiteralPath (Join-Path $log 'empty_version.txt') -Raw) -replace '\s','')
 if($r1 -ne 0 -or $ver -ne $csv){$emptyBad++}
 $r2=RunP $lone '--self-test' (Join-Path $log 'empty_selftest.txt') 300
 if($r2 -ne 0){$emptyBad++}
 $r3=RunP $lone '--verify-scripts' (Join-Path $log 'empty_verify_scripts.txt') 120
 if($r3 -ne 0 -or -not ((Get-Content -LiteralPath (Join-Path $log 'empty_verify_scripts.txt') -Raw) -match '"Verified": true')){$emptyBad++}
 "EMPTYDIR version=$ver (csproj $csv) failures=$emptyBad"
}finally{
 $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 $resolvedEmpty=[IO.Path]::GetFullPath($empty)
 if(-not $resolvedEmpty.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $resolvedEmpty) -notmatch '^agdns-empty-[a-f0-9]{32}$'){throw 'Unsafe standalone cleanup path'}
 if((Get-Item -LiteralPath $resolvedEmpty).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Standalone cleanup refuses reparse points'}
 Remove-Item -LiteralPath $resolvedEmpty -Recurse -Force -ErrorAction SilentlyContinue
}

# Update the release and shortcut only after all checks pass.
$allOk=($s -eq 0 -and $e -eq 0 -and $u -eq 0 -and $u2 -eq 0 -and $u3 -eq 0 -and $u4 -eq 0 -and $scaleBad -eq 0 -and $emptyBad -eq 0)
$pub=0
if($allOk -and $Publish){
 & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\package-release.ps1') -ExePath $exeP *>&1 | Tee-Object -FilePath (Join-Path $log 'publish.txt')
 $pub=$LASTEXITCODE
 "PUBLISH exit=$pub"
}else{"PUBLISH skipped ("+$(if(-not $Publish){'no -Publish: release\ and shortcut untouched'}else{'a check was red'})+")"}
# The E2E runner owns its one generated scratch path and attempts its own cleanup.
# Protected leftovers stay available for diagnosis; this wrapper never changes ACLs or removes other runs.
if(-not $allOk -or $pub -ne 0){exit 1}
