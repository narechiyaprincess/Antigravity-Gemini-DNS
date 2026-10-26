param([string]$OutputPath='')
$ErrorActionPreference='Stop'
$src=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repo=(Resolve-Path (Join-Path $src '..')).Path
if(-not $OutputPath){$OutputPath=Join-Path $repo 'source code.zip'}
$OutputPath=[IO.Path]::GetFullPath($OutputPath)

$rootNames=@('README.md','CHANGELOG.md','LICENSE','NOTICE','SECURITY.md','CONTRIBUTING.md','global.json','.gitignore','.gitattributes','NuGet.Config','nuget.config','Directory.Build.props','Directory.Build.targets','Directory.Packages.props')
$files=@(Get-ChildItem -LiteralPath $repo -File -Force | Where-Object { $_.Name -in $rootNames })
$files+=Get-ChildItem -LiteralPath $src -File | Where-Object {$_.Extension -in '.md','.json'}
# Sources include their actual resources, documentation and attribution. Never include
# build outputs, local state, signing keys or historical executables.
foreach($folder in 'app','tests','tools'){
 $files+=Get-ChildItem -LiteralPath (Join-Path $src $folder) -Recurse -File -Force | Where-Object{
  $_.FullName -notmatch '[\\/](bin|obj|\.work|\.vs|__pycache__)[\\/]' -and $_.Extension -notin '.pyc','.pdb','.log'
 }
}
$files+=Get-ChildItem -LiteralPath (Join-Path $repo 'docs') -Recurse -File -Force | Where-Object {$_.FullName -notmatch '[\\/](history|\.work|bin|obj)[\\/]'}
if(Test-Path -LiteralPath (Join-Path $repo '.github')){$files+=Get-ChildItem -LiteralPath (Join-Path $repo '.github') -Recurse -File -Force}
$files=@($files | Sort-Object FullName -Unique)
if(@($files | Where-Object {$_.Extension -in '.pfx','.p12','.key','.pem','.exe','.dll','.zip' -or $_.Name -like '.env*'}).Count){throw 'Unexpected binary or secret-bearing file in source selection; inspect before packaging'}
if(@($files | Where-Object{$_.FullName -eq $OutputPath}).Count){throw 'Output path overlaps a source file'}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$parent=[IO.Path]::GetDirectoryName($OutputPath)
[IO.Directory]::CreateDirectory($parent)|Out-Null
$temp=Join-Path $parent ([IO.Path]::GetRandomFileName()+'.zip')
try{
 $zip=[IO.Compression.ZipFile]::Open($temp,[IO.Compression.ZipArchiveMode]::Create)
 try{
  foreach($file in $files){
   $entry=$file.FullName.Substring($repo.Length+1).Replace('\','/')
   [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$entry,[IO.Compression.CompressionLevel]::Optimal)
  }
 }finally{$zip.Dispose()}
 $check=[IO.Compression.ZipFile]::OpenRead($temp)
 try{if($check.Entries.Count -ne $files.Count){throw 'Archive entry count mismatch'}}finally{$check.Dispose()}
 [IO.File]::Copy($temp,$OutputPath,$true)
}finally{if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
'SOURCE_PACKAGE_PASS files='+$files.Count
'SHA256 '+(Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
$OutputPath
