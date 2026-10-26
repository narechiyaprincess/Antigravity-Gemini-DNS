[CmdletBinding()]
param([string]$ExePath='', [switch]$WhatIf, [string]$ProjectRoot='', [string]$ReceiptPath='')
# Local replacement only: exact EXE + exact shortcut. Historical/unknown files and backups are never swept.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'BoundedProcess.ps1')
$src=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repo=if($ProjectRoot){(Resolve-Path -LiteralPath $ProjectRoot).Path}else{(Resolve-Path (Join-Path $src '..')).Path}
if(-not $ExePath){$ExePath=Join-Path $src '.work\build\AntigravityGeminiDns.exe'}
$runId=[guid]::NewGuid().ToString('N')
if(-not $ReceiptPath){$ReceiptPath=Join-Path $repo ('.package-receipts\'+$runId+'.json')}
$ReceiptPath=[IO.Path]::GetFullPath($ReceiptPath)
if(Test-Path -LiteralPath $ReceiptPath){throw 'Receipt already exists; preserve prior evidence'}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($ReceiptPath))|Out-Null
$dst=Join-Path $repo 'release\AntigravityGeminiDns.exe'
$lnk=Join-Path $repo 'Открыть Antigravity-Gemini DNS.lnk'
$backup=Join-Path $repo ('.package-backups\'+$runId)
$receipt=[ordered]@{Status='FAIL';DryRun=[bool]$WhatIf;TimestampUtc=[DateTime]::UtcNow.ToString('o');Source=$ExePath;SourceSHA256=$null;Plan=@();Actions=@();Rollback=@();Error=$null}
$mutated=@();$preimages=@{}
function Assert-Scope([string]$path){
 $full=[IO.Path]::GetFullPath($path)
 if(-not $full.StartsWith($repo.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw ('OUT_OF_SCOPE: '+$full)}
 $cursor=$full
 while($cursor -and $cursor.Length -ge $repo.Length){
  if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('REPARSE_POINT: '+$cursor)}
  $cursor=[IO.Path]::GetDirectoryName($cursor)
 }
 return $full
}
try{
 $exe=(Resolve-Path -LiteralPath $ExePath).Path
 if($exe -ieq $dst){throw 'Source must differ from replacement destination'}
 $csv=[regex]::Match((Get-Content -LiteralPath (Join-Path $src 'app\AntigravityGeminiDns.csproj') -Raw),'<Version>([^<]+)</Version>').Groups[1].Value
 $probe=Invoke-BoundedProcess -FilePath $exe -Arguments '--version' -TimeoutMilliseconds 30000
 if($probe.ExitCode -ne 0 -or $probe.Stdout.Trim() -ne $csv){throw 'Candidate version probe failed'}
 $hash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash;$receipt.SourceSHA256=$hash
 foreach($path in @($dst,$lnk)){
  [void](Assert-Scope $path)
  $prior=if(Test-Path -LiteralPath $path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}else{$null}
  $receipt.Plan+=@{Action='ReplaceExactFile';Path=$path;PreimageSHA256=$prior;Backup=(Join-Path $backup ([IO.Path]::GetFileName($path)))}
 }
 $receipt.Status='PLANNED'
 $receipt|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $ReceiptPath -Encoding UTF8
 if($WhatIf){$receipt.Status='DRY_RUN';Write-Output 'PACKAGE_DRY_RUN: only receipt written; destinations and backups untouched'}else{
  [void](Assert-Scope $backup);[IO.Directory]::CreateDirectory($backup)|Out-Null
  # Capture all preimages before the first destination mutation.
  foreach($entry in $receipt.Plan){
   [void](Assert-Scope $entry.Path)
   $current=if(Test-Path -LiteralPath $entry.Path){(Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash}else{$null}
   if($current -ne $entry.PreimageSHA256){throw 'Destination changed after planning'}
   if($current){[IO.File]::Copy($entry.Path,$entry.Backup,$false);$preimages[$entry.Path]=$entry.Backup}
  }
  [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dst))|Out-Null
  [void](Assert-Scope $dst);$mutated+=,$dst
  [IO.File]::Copy($exe,$dst,$true);$receipt.Actions+=@{Action='ReplaceExactFile';Path=$dst}
  if((Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash -ne $hash){throw 'Copied candidate hash mismatch'}
  $ws=New-Object -ComObject WScript.Shell
  $tempLink=Join-Path $backup 'candidate-shortcut.lnk'
  $shortcut=$ws.CreateShortcut($tempLink);$shortcut.TargetPath=$dst;$shortcut.WorkingDirectory=[IO.Path]::GetDirectoryName($dst);$shortcut.IconLocation=$dst+',0';$shortcut.Description='Antigravity-Gemini DNS';$shortcut.Save()
  [void](Assert-Scope $lnk);$mutated+=,$lnk
  [IO.File]::Copy($tempLink,$lnk,$true);$receipt.Actions+=@{Action='ReplaceExactFile';Path=$lnk}
  if($ws.CreateShortcut($lnk).TargetPath -ine $dst){throw 'Shortcut target mismatch'}
  $receipt.Status='PASS';Write-Output ('PACKAGE_PASS '+$dst+' SHA256 '+$hash)
 }
}catch{
 $receipt.Status='FAIL'
 $receipt.Error=$_.Exception.Message
 foreach($path in $mutated){
  try{
   [void](Assert-Scope $path)
   if($preimages.ContainsKey($path)){[IO.File]::Copy($preimages[$path],$path,$true);$receipt.Rollback+=@{Path=$path;Status='RESTORED'}}
   elseif([IO.File]::Exists($path)){[IO.File]::Delete($path);$receipt.Rollback+=@{Path=$path;Status='REMOVED_OWN_NEW_FILE'}}
  }catch{$receipt.Rollback+=@{Path=$path;Status='FAILED';Error=$_.Exception.Message}}
 }
 Write-Error -ErrorAction Continue $receipt.Error
}finally{
 $receipt|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $ReceiptPath -Encoding UTF8
 Write-Output ('Receipt: '+$ReceiptPath)
}
if($receipt.Status -eq 'FAIL'){exit 1}
