# Shared Windows PowerShell 5.1 / PowerShell 7 process runner. No persistent environment changes.
if (-not ('AntigravityGeminiDns.ToolProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'BoundedProcess.cs') }
function Invoke-BoundedProcess {
 param([string]$FilePath,[string]$Arguments='',[string]$WorkingDirectory=$PWD.Path,[int]$TimeoutMilliseconds=30000,[string]$CancellationFile='',[switch]$WindowsPowerShell)
 $modulePath=if($WindowsPowerShell){Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\Modules'}else{$null}
 [AntigravityGeminiDns.ToolProcess]::Run($FilePath,$Arguments,$WorkingDirectory,$TimeoutMilliseconds,$CancellationFile,$modulePath)
}
function Get-WindowsPowerShellPath { Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe' }
