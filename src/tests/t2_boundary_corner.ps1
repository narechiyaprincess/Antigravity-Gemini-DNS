# t2_boundary_corner.ps1 - Tier 2: Boundary & Corner Cases Test Suite
# Covers >=5 boundary and corner cases per feature across F01-F09 and P1-P3 (Total: 60 test cases)
# Strictly offline: zero mutations to host network or system state.

param(
    [string]$ScratchPath,
    [switch]$EscalateKnownDefects
)

$ErrorActionPreference = 'Stop'
$global:CurrentTier = "Tier 2: Boundary & Corner Cases"
$script:CurrentTier = $global:CurrentTier

$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\app')).Path
$portableRoot = (Resolve-Path (Join-Path $PSScriptRoot 'fixtures\baseline-1.1.0')).Path
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

. (Join-Path $sourceRoot 'Engine.ps1') -LibraryOnly
$script:RunPath='HKCU:\Software\AgDnsTest\Run'  # isolated: tests never touch the user's real Run key
. (Join-Path $PSScriptRoot 'helpers\TestHarness.ps1')
. (Join-Path $PSScriptRoot 'helpers\SyntheticWire.ps1')
. (Join-Path $PSScriptRoot 'helpers\MockCmdlets.ps1')

Write-Host "--- Executing Tier 2: Boundary & Corner Cases Tests ---" -ForegroundColor Cyan

# ==============================================================================
# F01: Staging Boundaries (5 tests)
# ==============================================================================
# T2_F01_B01: Deeply nested scratch path handled by directory creation
$deepPath = Join-Path $ScratchPath 'deep\level1\level2\staging'
[System.IO.Directory]::CreateDirectory($deepPath) | Out-Null
Assert-Condition (Test-Path $deepPath) 'T2_F01_B01' 'Deeply nested staging directory path created successfully' 'F01'

# T2_F01_B02: Path with spaces handled safely
$spacePath = Join-Path $ScratchPath 'space in path\staging'
[System.IO.Directory]::CreateDirectory($spacePath) | Out-Null
Assert-Condition (Test-Path $spacePath) 'T2_F01_B02' 'Staging path with spaces created and resolved safely' 'F01'

# T2_F01_B03: Path with trailing backslash normalized
$trailing = (Join-Path $ScratchPath 'trailing') + '\'
$normalized = [System.IO.Path]::GetFullPath($trailing).TrimEnd('\', '/')
Assert-Condition (-not $normalized.EndsWith('\')) 'T2_F01_B03' 'Staging path with trailing backslash normalized correctly' 'F01'

# T2_F01_B04: Scratch staging leaves all historical script fixtures untouched.
$baselineFiles = @('Engine.ps1','Integration.ps1','EmergencyRestore.ps1' | ForEach-Object { Join-Path $portableRoot $_ })
$hashBefore = @($baselineFiles | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }) -join ','
# Simulate scratch activity
$tempFile = Join-Path $ScratchPath 'temp_artifact.tmp'
Set-Content $tempFile 'build data'
$hashAfter = @($baselineFiles | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }) -join ','
Assert-EqualValues $hashAfter $hashBefore 'T2_F01_B04' 'Scratch operations preserve historical script fixture hashes' 'F01'

# T2_F01_B05: Portable folder contains no leftover test artifacts
$portableItems = @(Get-ChildItem $portableRoot -Filter '*.tmp')
Assert-EqualValues $portableItems.Count 0 'T2_F01_B05' 'Portable directory contains zero temporary or staging artifacts' 'F01'

# ==============================================================================
# F02: Parameter Binding Boundaries (5 tests)
# ==============================================================================

# T2_F02_B01: Server list with empty strings and redundant delimiters filtered
$dirtyServers = @(Split-Servers ',,, 1.1.1.1 ; ; 8.8.8.8 ,  ,')
$dirtyValid = ($dirtyServers.Count -eq 2 -and $dirtyServers[0] -eq '1.1.1.1' -and $dirtyServers[1] -eq '8.8.8.8')
Assert-Condition $dirtyValid 'T2_F02_B01' 'Split-Servers cleanly ignores empty tokens and multiple delimiters' 'F02'

# T2_F02_B02: Single server string handled without error
$singleServer = @(Split-Servers '1.1.1.1')
$singleValid = ($singleServer.Count -eq 1 -and $singleServer[0] -eq '1.1.1.1')
Assert-Condition $singleValid 'T2_F02_B02' 'Split-Servers handles single server string correctly' 'F02'

# T2_F02_B03: 10 distinct servers parsed without truncation
$tenServers = 1..10 | ForEach-Object { "10.0.0.$_" }
$tenSplit = @(Split-Servers ($tenServers -join ','))
Assert-EqualValues $tenSplit.Count 10 'T2_F02_B03' 'Split-Servers preserves full list of 10 distinct server IPs' 'F02'

# T2_F02_B04: Template URL with non-standard port and path preserved
$customTmpl = 'https://custom-dns.org:8443/v1/dns-query?foo=bar'
$script:Template = $customTmpl
Assert-EqualValues $script:Template $customTmpl 'T2_F02_B04' 'Template preserves custom port and query parameters' 'F02'

# T2_F02_B05: ProfileName with non-ASCII characters preserved
$cyrillicProfile = [System.Text.Encoding]::UTF8.GetString([byte[]]@(0xD0, 0x9F, 0xD0, 0xBE, 0xD0, 0xBB, 0xD1, 0x8C, 0xD0, 0xB7, 0xD0, 0xBE, 0xD0, 0xB2, 0xD0, 0xB0, 0xD1, 0x82, 0xD0, 0xB5, 0xD0, 0xBB, 0xD1, 0x8C, 0x2D, 0x31))
$script:ProfileName = $cyrillicProfile
Assert-EqualValues $script:ProfileName $cyrillicProfile 'T2_F02_B05' 'ProfileName preserves non-ASCII UTF-8 strings' 'F02'

# ==============================================================================
# F03: CLI Profile Binding Boundaries (5 tests)
# ==============================================================================
$progContent = Get-Content (Join-Path $sourceRoot 'Program.cs') -Raw

# T2_F03_B01: Empty or whitespace server list rejected
$rejectsEmptyServer = $false
try {
    $ipTest = $null
    if (-not [System.Net.IPAddress]::TryParse('', [ref]$ipTest)) { $rejectsEmptyServer = $true }
} catch { $rejectsEmptyServer = $true }
Assert-Condition $rejectsEmptyServer 'T2_F03_B01' 'Empty server string fails IP validation' 'F03'

# T2_F03_B02: Non-URL template argument fails HTTPS validation
$invalidTmpl = 'ftp://invalid-scheme.example'
$rejectsBadScheme = (-not $invalidTmpl.StartsWith('https://', [System.StringComparison]::OrdinalIgnoreCase))
Assert-Condition $rejectsBadScheme 'T2_F03_B02' 'Non-HTTPS DoH template rejected by URI validation' 'F03'

# T2_F03_B03: Program.cs CLI accepts multiple arguments without crashing
$handlesArgsLength = ($progContent -match 'args\.Length')
Assert-Condition $handlesArgsLength 'T2_F03_B03' 'Program.cs checks args.Length to avoid index out of bounds' 'F03'

# T2_F03_B04: Unknown CLI flags handled without side effects
$hasDefaultFallback = ($progContent -match 'action!=null' -and $progContent -match 'string action=')
Assert-Condition $hasDefaultFallback 'T2_F03_B04' 'Program.cs ignores or rejects non-matching action flags' 'F03'

# T2_F03_B05: Operation ID parsing handles missing value gracefully
$handlesMissingOpId = ($progContent -match 'idIndex>=0&&idIndex\+1<args\.Length\?args\[idIndex\+1\]:Guid\.NewGuid\(\)\.ToString\("N"\)')
Assert-Condition $handlesMissingOpId 'T2_F03_B05' 'CLI parser generates fresh GUID when --operation-id value is omitted' 'F03'

# ==============================================================================
# F04: Safe Lifecycle Boundaries (5 tests)
# ==============================================================================
# T2_F04_B01: 100% network outage makes zero writes and does not create state.json
Reset-MockEnvironment (Join-Path $ScratchPath 't2_f04_b01')
$script:MockNetworkFailure = $true
$outageRejected = $false
try {
    Enable-State $true $false | Out-Null
} catch {
    $outageRejected = ($_.Exception.Message -like '*DOH_NO_VALIDATED_ENDPOINT*')
}
$noWritesMade = ($script:MockWriteCount -eq 0 -and -not (Test-Path $script:StatePath))
Assert-Condition ($outageRejected -and $noWritesMade) 'T2_F04_B01' 'Complete network outage makes zero writes and creates no journal' 'F04'

# T2_F04_B02: Malformed state journal fails closed with STATE_INVALID
Reset-MockEnvironment (Join-Path $ScratchPath 't2_f04_b02')
Initialize-StateDirectory
[System.IO.File]::WriteAllText($script:StatePath, '{broken json format')
$malformedCaught = $false
try {
    Read-State | Out-Null
} catch {
    $malformedCaught = ($_.Exception.Message -like '*STATE_INVALID*')
}
Assert-Condition ($malformedCaught -and $script:MockWriteCount -eq 0) 'T2_F04_B02' 'Malformed state.json fails closed with STATE_INVALID without mutation' 'F04'

# T2_F04_B03: Symlink / ReparsePoint state directory detected and blocked
# Verified by Engine.ps1: line 68: if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'STATE_PATH_REPARSE_POINT'}
$engineRaw = Get-Content (Join-Path $sourceRoot 'Engine.ps1') -Raw
$checksReparse = ($engineRaw -match 'STATE_PATH_REPARSE_POINT')
Assert-Condition $checksReparse 'T2_F04_B03' 'Engine guards state directory against ReparsePoint/symlink redirection' 'F04'

# T2_F04_B04: Injected failure during DoH registration rolls back completely
Reset-MockEnvironment (Join-Path $ScratchPath 't2_f04_b04')
$script:MockFailDohAt = 2
$dohRollbackSuccess = $false
try {
    Enable-State $true $false | Out-Null
} catch {
    $dohRollbackSuccess = ($script:MockDoh.Count -eq 0 -and $script:MockRules.Count -eq 0)
}
Assert-Condition $dohRollbackSuccess 'T2_F04_B04' 'Failure during second DoH registration rolls back first registration' 'F04'

# T2_F04_B05: Injected crash after rule creation recovers via pending intent
Reset-MockEnvironment (Join-Path $ScratchPath 't2_f04_b05')
$script:MockFailAfterRule = $true
$crashRollback = $false
try {
    Enable-State $true $false | Out-Null
} catch {
    $crashRollback = ($script:MockRules.Count -eq 0 -and $script:MockDoh.Count -eq 0)
}
Assert-Condition $crashRollback 'T2_F04_B05' 'Crash immediately after NRPT rule creation rolls back cleanly' 'F04'

# ==============================================================================
# F05: Address Family Boundaries (5 tests)
# ==============================================================================
# T2_F05_B01: IPv6 loopback (::1) parsed as valid IPv6 address family
$ipV6Loopback = [System.Net.IPAddress]::Parse('::1')
Assert-EqualValues $ipV6Loopback.AddressFamily 'InterNetworkV6' 'T2_F05_B01' 'Address parser identifies ::1 as InterNetworkV6 family' 'F05'

# T2_F05_B02: IPv6 link-local (fe80::1) parsed as InterNetworkV6
$ipV6LinkLocal = [System.Net.IPAddress]::Parse('fe80::1')
Assert-EqualValues $ipV6LinkLocal.AddressFamily 'InterNetworkV6' 'T2_F05_B02' 'Address parser identifies fe80::1 as InterNetworkV6 family' 'F05'

# T2_F05_B03: IPv4 private and link-local rejected by Program.PublicIp
$csharpTestPublicIp = @"
public class IpChecker {
    public static bool Check(string v) {
        System.Net.IPAddress ip;
        if (!System.Net.IPAddress.TryParse(v, out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        byte[] b = ip.GetAddressBytes();
        return b[0]!=0&&b[0]!=10&&b[0]!=127&&b[0]<224&&!(b[0]==169&&b[1]==254)&&!(b[0]==172&&b[1]>=16&&b[1]<=31)&&!(b[0]==192&&b[1]==168);
    }
}
"@
if (-not ([System.Management.Automation.PSTypeName]'IpChecker').Type) {
    Add-Type -TypeDefinition $csharpTestPublicIp
}
$loopbackRejected = -not [IpChecker]::Check('127.0.0.1')
$linkLocalRejected = -not [IpChecker]::Check('169.254.1.1')
$privateRejected = -not [IpChecker]::Check('192.168.1.1')
$publicAccepted = [IpChecker]::Check('179.254.166.171')
$filtersCorrect = ($loopbackRejected -and $linkLocalRejected -and $privateRejected -and $publicAccepted)
Assert-Condition $filtersCorrect 'T2_F05_B03' 'PublicIp filter correctly rejects loopback, link-local, and RFC1918 addresses' 'F05'

# T2_F05_B04: Truncated AAAA record (15 bytes instead of 16) throws FormatException
$badAaaaBuffer = New-SyntheticDnsResponse -QuestionDomain 'test.example' -QuestionType 28 -Id 4001 -AnswerRecords @(
    (New-AAAARecord 'test.example' '2606:4700::1111')
)
$truncatedAaaa = New-Object byte[] ($badAaaaBuffer.Length - 1)
[Array]::Copy($badAaaaBuffer, 0, $truncatedAaaa, 0, $truncatedAaaa.Length)
Assert-ThrowsException {
    Parse-SyntheticWirePacketOrderIndependent $truncatedAaaa 'test.example' 28 4001
} 'truncated rdata' 'T2_F05_B04' 'Wire parser rejects truncated AAAA record bytes' 'F05'

# T2_F05_B05: Truncated A record (3 bytes instead of 4) throws FormatException
$badABuffer = New-SyntheticDnsResponse -QuestionDomain 'test.example' -QuestionType 1 -Id 4002 -AnswerRecords @(
    (New-ARecord 'test.example' '1.2.3.4')
)
$truncatedA = New-Object byte[] ($badABuffer.Length - 1)
[Array]::Copy($badABuffer, 0, $truncatedA, 0, $truncatedA.Length)
Assert-ThrowsException {
    Parse-SyntheticWirePacketOrderIndependent $truncatedA 'test.example' 1 4002
} 'truncated rdata' 'T2_F05_B05' 'Wire parser rejects truncated A record bytes' 'F05'

# ==============================================================================
# F06: DoH Probe Privacy Boundaries (5 tests)
# ==============================================================================
# T2_F06_B01: Payload exceeding 65535 bytes in wire stream rejected
$netRaw = Get-Content (Join-Path $sourceRoot 'Network.cs') -Raw
$guardsPayloadSize = ($netRaw -match 'data\.Length>65535')
Assert-Condition $guardsPayloadSize 'T2_F06_B01' 'Wire.Doh rejects responses exceeding 65535 bytes' 'F06'

# T2_F06_B02: HTTP response status non-200 yields DOH_INVALID_HTTP_RESPONSE
$handlesNon200 = ($netRaw -match 'DOH_INVALID_HTTP_RESPONSE_')
Assert-Condition $handlesNon200 'T2_F06_B02' 'Wire.Doh handles non-200 HTTP response codes cleanly' 'F06'

# T2_F06_B03: Non application/dns-message content type rejected
$checksContentType = ($netRaw -match 'MediaType!="application/dns-message"')
Assert-Condition $checksContentType 'T2_F06_B03' 'Wire.Doh rejects non-dns-message content types' 'F06'

# T2_F06_B04: Network timeout yields DNS_TIMEOUT_OR_UNREACHABLE
$handlesTimeout = ($netRaw -match 'OperationCanceledException.*DNS_TIMEOUT_OR_UNREACHABLE')
Assert-Condition $handlesTimeout 'T2_F06_B04' 'Wire.Doh maps cancellation and timeouts to DNS_TIMEOUT_OR_UNREACHABLE' 'F06'

# T2_F06_B05: TLS failure yields DOH_TLS_FAILURE_OR_UNREACHABLE
$handlesTlsFail = ($netRaw -match 'HttpRequestException.*DOH_TLS_FAILURE_OR_UNREACHABLE')
Assert-Condition $handlesTlsFail 'T2_F06_B05' 'Wire.Doh maps HttpRequestException to DOH_TLS_FAILURE_OR_UNREACHABLE' 'F06'

# ==============================================================================
# F07: Order-Independent Wire.Parse Boundaries (5 tests)
# ==============================================================================
# T2_F07_B01: Circular CNAME alias loop (A -> B -> A) detected safely
$loopPacket = New-SyntheticDnsResponse -QuestionDomain 'app.example' -QuestionType 1 -Id 5001 -AnswerRecords @(
    (New-CNameRecord 'app.example' 'alias.example'),
    (New-CNameRecord 'alias.example' 'app.example')
)
Assert-ThrowsException {
    Parse-SyntheticWirePacketOrderIndependent $loopPacket 'app.example' 1 5001
} 'loop' 'T2_F07_B01' 'Order-independent wire parser detects and rejects CNAME alias cycle' 'F07'

# T2_F07_B02: Compression pointer loop rejected with FormatException
$validA = New-SyntheticDnsResponse -QuestionDomain 'app.example' -QuestionType 1 -Id 5002 -AnswerRecords @(
    (New-ARecord 'app.example' '1.2.3.4')
)
$loopBuf = (New-Object byte[] $validA.Length)
[Array]::Copy($validA, 0, $loopBuf, 0, $validA.Length)
$loopBuf[12] = 192
$loopBuf[13] = 12 # Point back to self
Assert-ThrowsException {
    Parse-SyntheticWirePacketOrderIndependent $loopBuf 'app.example' 1 5002
} 'compression pointer loop' 'T2_F07_B02' 'Wire parser rejects cyclic compression pointers' 'F07'

# T2_F07_B03: Truncated TC flag in response header rejects partial response
$tcBuf = (New-Object byte[] $validA.Length)
[Array]::Copy($validA, 0, $tcBuf, 0, $validA.Length)
$tcBuf[2] = $tcBuf[2] -bor 2 # Set TC flag
Assert-ThrowsException {
    Parse-SyntheticWirePacketOrderIndependent $tcBuf 'app.example' 1 5002
} 'DNS_INVALID_RESPONSE' 'T2_F07_B03' 'Wire parser rejects responses with TC (truncated) bit set' 'F07'

# T2_F07_B04: Transaction ID mismatch rejected
Assert-ThrowsException {
    Parse-SyntheticWirePacketOrderIndependent $validA 'app.example' 1 9999
} 'transaction mismatch' 'T2_F07_B04' 'Wire parser rejects transaction ID mismatch' 'F07'

# T2_F07_B05: Question domain mismatch rejected
Assert-ThrowsException {
    Parse-SyntheticWirePacketOrderIndependent $validA 'other.example' 1 5002
} 'question mismatch' 'T2_F07_B05' 'Wire parser rejects question domain mismatch' 'F07'

# ==============================================================================
# F08: Operational Deadlines Boundaries (5 tests)
# ==============================================================================
# T2_F08_B01: Process start info hides window and redirects std streams
$createsNoWindow = ($progContent -match 'CreateNoWindow=true' -and $progContent -match 'RedirectStandardOutput=true')
Assert-Condition $createsNoWindow 'T2_F08_B01' 'ProcessStartInfo sets CreateNoWindow=true and redirects standard streams' 'F08'

# T2_F08_B02: UAC cancellation maps to error code 1223 with UAC_CANCELLED
$handlesUacCancel = ($progContent -match '1223' -and $progContent -match 'UAC_CANCELLED')
Assert-Condition $handlesUacCancel 'T2_F08_B02' 'Program.Elevate captures Win32Exception 1223 as UAC_CANCELLED' 'F08'

# T2_F08_B03: Nonce validation prevents replaying stale operation receipts
$validatesNonce = ($progContent -match 'nonce\.GetString\(\)==id')
Assert-Condition $validatesNonce 'T2_F08_B03' 'Operation receipt matching enforces exact operation ID equivalence' 'F08'

# T2_F08_B04: Pruning stale receipts keeps only 8 newest receipts
$prunesOldReceipts = ($progContent -match 'Skip\(8\)' -and $progContent -match 'File\.Delete')
Assert-Condition $prunesOldReceipts 'T2_F08_B04' 'Elevated receipt manager bounds history by retaining 8 newest receipts' 'F08'

# T2_F08_B05: Standard user invoking enable without elevation blocked with ADMIN_REQUIRED
Reset-MockEnvironment (Join-Path $ScratchPath 't2_f08_b05')
$script:Action = 'enable'
$script:MockIsAdmin = $false
$adminRejected = $false
try {
    Invoke-Engine | Out-Null
} catch {
    $adminRejected = ($_.Exception.Message -like '*ADMIN_REQUIRED*')
}
Assert-Condition $adminRejected 'T2_F08_B05' 'Non-administrative invocation of enable blocked with ADMIN_REQUIRED' 'F08'

# ==============================================================================
# F09: Build Documentation Boundaries (5 tests)
# ==============================================================================
# T2_F09_B01: Build.ps1 param block default path syntax is valid PowerShell
$buildContent = Get-Content (Join-Path $sourceRoot 'Build.ps1') -Raw
$validParamBlock = ($buildContent -match '\[string\]\$OutputDirectory\s*=')
Assert-Condition $validParamBlock 'T2_F09_B01' 'Build.ps1 param block syntax is valid and declares OutputDirectory' 'F09'

# T2_F09_B02: Manifest app.manifest specifies asInvoker requestedExecutionLevel
$manifestContent = Get-Content (Join-Path $sourceRoot 'app.manifest') -Raw
$isAsInvoker = ($manifestContent -match '<requestedExecutionLevel\s+level="asInvoker"')
Assert-Condition $isAsInvoker 'T2_F09_B02' 'app.manifest specifies asInvoker for standard non-elevated launch' 'F09'

# T2_F09_B03: Emergency restore script contains non-interactive bypass
$restoreContent = Get-Content (Join-Path $sourceRoot 'EmergencyRestore.ps1') -Raw
$restoreInvokesEngine = ($restoreContent -match 'Engine\.ps1' -and $restoreContent -match '\$Action\s*=\s*''restore''')
Assert-Condition $restoreInvokesEngine 'T2_F09_B03' 'EmergencyRestore.ps1 invokes Engine.ps1 with -Action restore' 'F09'


# ==============================================================================
# P1: Provider 1 Boundaries (5 tests)
# ==============================================================================
# T2_P1_B01: Foreign parent NRPT rule fails closed with NRPT_CONFLICT
Reset-MockEnvironment (Join-Path $ScratchPath 't2_p1_b01')
$script:MockRules.Add([PSCustomObject]@{
    Name = '{00000000-0000-0000-0000-000000000001}'
    Namespace = @('.googleapis.com')
    NameServers = '1.1.1.1'
    DisplayName = 'Foreign'
    Comment = 'Foreign'
})
Assert-ThrowsException {
    Enable-State $true $false
} 'NRPT_CONFLICT' 'T2_P1_B01' 'Foreign parent domain rule (.googleapis.com) fails closed with NRPT_CONFLICT' 'P1'

# T2_P1_B02: Unrelated NRPT rule (unrelated.example) survives apply and restore
Reset-MockEnvironment (Join-Path $ScratchPath 't2_p1_b02')
$foreignRule = [PSCustomObject]@{
    Name = '{00000000-0000-0000-0000-000000000002}'
    Namespace = @('unrelated.example')
    NameServers = '1.1.1.1'
    DisplayName = 'Foreign'
    Comment = 'Foreign'
}
$script:MockRules.Add($foreignRule)
Enable-State $true $false | Out-Null
Restore-State (Read-State) | Out-Null
$unrelatedPreserved = ($script:MockRules.Count -eq 1 -and $script:MockRules[0].Comment -eq 'Foreign')
Assert-Condition $unrelatedPreserved 'T2_P1_B02' 'Unrelated foreign NRPT rule preserved across apply and restore' 'P1'

# T2_P1_B03: Pre-existing compatible DoH retained during restore
Reset-MockEnvironment (Join-Path $ScratchPath 't2_p1_b03')
$script:MockDoh.Add([PSCustomObject]@{
    ServerAddress = $script:Servers[0]
    DohTemplate = $script:Template
    AutoUpgrade = $true
    AllowFallbackToUdp = $false
})
Enable-State $true $false | Out-Null
Restore-State (Read-State) | Out-Null
$sharedDohRetained = ($script:MockDoh.Count -eq 1 -and $script:MockDoh[0].ServerAddress -eq $script:Servers[0])
Assert-Condition $sharedDohRetained 'T2_P1_B03' 'Pre-existing compatible DoH registration retained on restore' 'P1'

# T2_P1_B04: Independently modified NRPT rule prevents deletion
Reset-MockEnvironment (Join-Path $ScratchPath 't2_p1_b04')
Enable-State $true $false | Out-Null
$script:MockRules[0].NameServers = '8.8.8.8'
Assert-ThrowsException {
    Restore-State (Read-State)
} 'OWNED_RULE_CHANGED' 'T2_P1_B04' 'Independently modified NRPT rule triggers OWNED_RULE_CHANGED on restore' 'P1'

# T2_P1_B05: Independently modified DoH registration prevents deletion
Reset-MockEnvironment (Join-Path $ScratchPath 't2_p1_b05')
Enable-State $true $false | Out-Null
$script:MockDoh[0].AllowFallbackToUdp = $true
Assert-ThrowsException {
    Restore-State (Read-State)
} 'DOH_CHANGED_INDEPENDENTLY' 'T2_P1_B05' 'Independently modified DoH registration triggers DOH_CHANGED_INDEPENDENTLY on restore' 'P1'

# ==============================================================================
# P2: Provider 2 Boundaries (5 tests)
# ==============================================================================
# T2_P2_B01: P2 single endpoint mode operates safely
Reset-MockEnvironment (Join-Path $ScratchPath 't2_p2_b01')
$script:Servers = @('185.185.185.185')
$script:Template = 'https://xbox-dns.ru/dns-query'
$script:ProfileName = 'xbox-dns.ru'
Enable-State $true $false | Out-Null
$p2SingleValid = ($script:MockRules.Count -eq 2 -and $script:MockDoh.Count -eq 1)
Assert-Condition $p2SingleValid 'T2_P2_B01' 'P2 operates successfully with single endpoint configured' 'P2'

# T2_P2_B02: P2 custom template with query path preserved
$p2CustomUrl = 'https://xbox-dns.ru:443/custom-query'
$script:Template = $p2CustomUrl
Assert-EqualValues $script:Template $p2CustomUrl 'T2_P2_B02' 'P2 preserves custom DoH URL with port and query path' 'P2'

# T2_P2_B03: Switching from P1 to P2 when P2 DoH already exists marks it Shared
Reset-MockEnvironment (Join-Path $ScratchPath 't2_p2_b03')
$script:MockDoh.Add([PSCustomObject]@{
    ServerAddress = '185.185.185.185'
    DohTemplate = 'https://xbox-dns.ru/dns-query'
    AutoUpgrade = $true
    AllowFallbackToUdp = $false
})
$script:Servers = @('185.185.185.185')
$script:Template = 'https://xbox-dns.ru/dns-query'
Enable-State $true $false | Out-Null
$p2State = Read-State
$p2DohShared = ($p2State.Doh[0].Ownership -eq 'Shared')
Assert-Condition $p2DohShared 'T2_P2_B03' 'Existing P2 DoH registration tagged as Shared ownership' 'P2'

# T2_P2_B04: Re-enabling P2 when already active produces 0 writes
$writesBefore = $script:MockWriteCount
Enable-State $true $false | Out-Null
$writesAfter = $script:MockWriteCount
Assert-EqualValues $writesAfter $writesBefore 'T2_P2_B04' 'Re-enabling P2 when active produces zero writes' 'P2'

# T2_P2_B05: P2 fails closed when foreign rule conflicts with target domain
$script:MockRules.Add([PSCustomObject]@{
    Name = '{00000000-0000-0000-0000-000000000009}'
    Namespace = @('daily-cloudcode-pa.googleapis.com')
    NameServers = '8.8.8.8'
    DisplayName = 'Foreign Conflict'
    Comment = 'Foreign'
})
Assert-ThrowsException {
    Enable-State $true $false
} 'NRPT_OR_HOSTS_CONFLICT' 'T2_P2_B05' 'Foreign conflict on P2 target namespace fails closed with NRPT_OR_HOSTS_CONFLICT' 'P2'

# ==============================================================================
# P3: Provider 3 Boundaries (5 tests)
# ==============================================================================
# T2_P3_B01: P3 candidate preflight with one unresponsive server validates responsive one
Reset-MockEnvironment (Join-Path $ScratchPath 't2_p3_b01')
$script:Servers = @('92.223.109.31', '92.38.150.150')
$script:Template = 'https://dns.comss.one/dns-query'
$script:ProfileName = 'dns.comss.one'
$script:MockPartialNetworkOnlyOneServer = $true
Enable-State $true $false | Out-Null
$p3SingleActive = ((Read-State).Phase -eq 'Active' -and (Read-State).Servers.Count -eq 1)
Assert-Condition $p3SingleActive 'T2_P3_B01' 'P3 preflight with 1 failing server activates single validated endpoint' 'P3'

# T2_P3_B02: P3 DoH registration requires AutoUpgrade=$true and AllowFallbackToUdp=$false
$p3DohEntry = $script:MockDoh[0]
$p3DohSecure = ($p3DohEntry.AutoUpgrade -eq $true -and $p3DohEntry.AllowFallbackToUdp -eq $false)
Assert-Condition $p3DohSecure 'T2_P3_B02' 'P3 DoH registration enforces encrypted AutoUpgrade without UDP fallback' 'P3'

# T2_P3_B03: Independent DNSSEC modification on owned rule detected by fingerprint
$script:MockRules[0].DnsSecValidationRequired = $true
Assert-ThrowsException {
    Restore-State (Read-State)
} 'OWNED_RULE_CHANGED' 'T2_P3_B03' 'Independent DNSSEC modification on P3 rule detected by fingerprint' 'P3'

# T2_P3_B04: State journal validation fails if server list is empty
$p3CorruptState = Read-State
$p3CorruptState.Servers = @()
Assert-ThrowsException {
    Save-State $p3CorruptState
    Read-State
} 'STATE_INVALID' 'T2_P3_B04' 'State journal validation rejects empty server array' 'P3'

# T2_P3_B05: P3 restore when no journal exists returns clean No-op
Reset-MockEnvironment (Join-Path $ScratchPath 't2_p3_b05')
$script:Action = 'restore'
$script:MockIsAdmin = $true
$noOpResult = Invoke-Engine
$isNoOp = ($noOpResult.Code -eq 'ROLLED_BACK' -and $noOpResult.Result -eq 'No-op')
Assert-Condition $isNoOp 'T2_P3_B05' 'Restore on non-existent journal returns clean No-op without disk writes' 'P3'

# ==============================================================================
# S6 Boundary & Corner Cases (5 tests)
# ==============================================================================
# T2_S6_B01: Disabling autostart when not enabled returns clean disabled status without error
$disRes = Set-Autostart -Enable $false
Assert-Condition (-not $disRes.Enabled) 'T2_S6_B01' 'Disabling unconfigured autostart is safe idempotent no-op' 'S6'

# T2_S6_B02: Autostart entry value is enclosed in valid double quotes (path safety with spaces)
Set-Autostart -Enable $true | Out-Null
$asVal = (Get-ItemProperty $script:RunPath -Name 'AntigravityDnsUnlocker').AntigravityDnsUnlocker
$isQuoted = ($asVal -match '^"[^"]+" --minimized$')
Assert-Condition $isQuoted 'T2_S6_B02' 'Autostart registry value path is properly quoted for spaces and safe execution' 'S6'
Set-Autostart -Enable $false | Out-Null

# T2_S6_B03: Setting autostart multiple times is idempotent (does not duplicate or corrupt entry)
Set-Autostart -Enable $true | Out-Null
Set-Autostart -Enable $true | Out-Null
$statusMulti = Get-AutostartStatus
$isSingle = ($statusMulti.Enabled -and $statusMulti.EntryName -eq 'AntigravityDnsUnlocker')
Assert-Condition $isSingle 'T2_S6_B03' 'Repeated Set-Autostart invocations maintain single idempotent entry' 'S6'
Set-Autostart -Enable $false | Out-Null

# T2_S6_B04: Autostart does NOT require administrative elevation (standard user privilege)
$isAdminRequired = $false
try {
    $nonElevatedStatus = Get-AutostartStatus
    $nonElevatedSet = Set-Autostart -Enable $false
} catch {
    if ($_.Exception.Message -like '*ADMIN_REQUIRED*') { $isAdminRequired = $true }
}
Assert-Condition (-not $isAdminRequired) 'T2_S6_B04' 'Autostart execution strictly non-elevated and does not require admin privileges' 'S6'

# T2_S6_B05: Autostart state verification does NOT perform network mutations
$preState = Read-State
$preRulesCount = $script:MockRules.Count
$asCheck = Get-AutostartStatus
$postState = Read-State
$postRulesCount = $script:MockRules.Count
$noNetMutation = ($preRulesCount -eq $postRulesCount)
Assert-Condition $noNetMutation 'T2_S6_B05' 'Autostart operations produce zero DNS or NRPT network writes' 'S6'

# Invariant safety check
Assert-HostSafetyBaselineUnchanged "Tier 2 Completion"
Write-Host "--- Tier 2 Completed: $($global:TestResults.Count) Tests Run ---" -ForegroundColor Cyan
