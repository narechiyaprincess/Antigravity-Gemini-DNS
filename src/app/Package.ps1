param(
 [string]$ExePath=(Join-Path $PSScriptRoot '..\.work\build\AntigravityGeminiDns.exe'),
 [string]$OutputDirectory=(Join-Path $PSScriptRoot '..\.work\dist'),
 [string]$ReadmePath='', [int]$TimeoutSeconds=30, [string]$CancellationFile=''
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\tools\BoundedProcess.ps1')
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
$receiptPath=Join-Path $OutputDirectory ('package-receipt-'+[guid]::NewGuid().ToString('N')+'.json')
$receipt=[ordered]@{Status='FAIL';TimestampUtc=[DateTime]::UtcNow.ToString('o');ExePath=$ExePath;Process=$null;ZipPath=$null;ExeSHA256=$null;ZipSHA256=$null;Error=$null}
$temp=$null
try{
 $version=[regex]::Match((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AntigravityGeminiDns.csproj') -Raw),'<Version>([^<]+)</Version>').Groups[1].Value
 if(-not $version){throw 'Missing project version'}
 $ExePath=(Resolve-Path -LiteralPath $ExePath).Path
 $r=Invoke-BoundedProcess -FilePath $ExePath -Arguments '--version' -WorkingDirectory ([IO.Path]::GetDirectoryName($ExePath)) -TimeoutMilliseconds ($TimeoutSeconds*1000) -CancellationFile $CancellationFile
 $receipt.Process=$r
 if($r.ExitCode -ne 0){throw ('Version probe failed: exit '+$r.ExitCode+' '+$r.Error)}
 if($r.Stdout.Trim() -ne $version){throw 'Version mismatch; rebuild the candidate'}
 $zipPath=Join-Path $OutputDirectory "AntigravityGeminiDns-$version-win-x64.zip"
 $sumsPath=Join-Path $OutputDirectory 'SHA256SUMS.txt'
 if((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $sumsPath)){throw 'OUTPUT_ALREADY_EXISTS: choose a new output directory; previous packages are preserved'}
 Add-Type -AssemblyName System.IO.Compression
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $temp=Join-Path $OutputDirectory ([guid]::NewGuid().ToString('N')+'.partial.zip')
 $zip=[IO.Compression.ZipFile]::Open($temp,[IO.Compression.ZipArchiveMode]::Create)
 try{
  [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$ExePath,'AntigravityGeminiDns.exe',[IO.Compression.CompressionLevel]::Optimal)
  if($ReadmePath){
   if((Split-Path -Leaf $ReadmePath) -ieq 'AntigravityGeminiDns.exe'){throw 'Readme entry collides with the executable'}
   [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Resolve-Path -LiteralPath $ReadmePath).Path,(Split-Path -Leaf $ReadmePath),[IO.Compression.CompressionLevel]::Optimal)
  }
 }finally{$zip.Dispose()}
 # Verify bytes inside the archive, independently of the source hash.
 $receipt.ExeSHA256=(Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
 $check=[IO.Compression.ZipFile]::OpenRead($temp)
 try{$stream=$check.GetEntry('AntigravityGeminiDns.exe').Open();try{$sha=[Security.Cryptography.SHA256]::Create();try{$inside=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$sha.Dispose()}}finally{$stream.Dispose()}}finally{$check.Dispose()}
 if($inside -ne $receipt.ExeSHA256){throw 'Archive executable hash mismatch'}
 [IO.File]::Move($temp,$zipPath);$temp=$null
 $receipt.ZipPath=$zipPath;$receipt.ZipSHA256=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
 [IO.File]::WriteAllText($sumsPath,($receipt.ZipSHA256+'  '+[IO.Path]::GetFileName($zipPath)+"`r`n"),[Text.UTF8Encoding]::new($false))
 $receipt.Status='PASS';Write-Output ('PACKAGE_PASS '+$zipPath)
}catch{$receipt.Error=$_.Exception.Message;Write-Error -ErrorAction Continue $receipt.Error}
finally{
 if($temp -and [IO.File]::Exists($temp)){[IO.File]::Delete($temp)}
 $receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $receiptPath -Encoding UTF8
 Write-Output ('Receipt: '+$receiptPath)
}
if($receipt.Status -ne 'PASS'){exit 1}
