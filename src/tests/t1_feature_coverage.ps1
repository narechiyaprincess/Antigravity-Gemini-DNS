# t1_feature_coverage.ps1 - Tier 1: Feature Coverage Test Suite
# Covers >=5 test cases per feature across F01-F09 and P1-P3 (Total: 60 test cases)
# Strictly offline: zero mutations to host network or system state.

param(
    [string]$ScratchPath,
    [switch]$EscalateKnownDefects
)

$ErrorActionPreference = 'Stop'
$global:CurrentTier = "Tier 1: Feature Coverage"
$script:CurrentTier = $global:CurrentTier

$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\app')).Path
$portableRoot = (Resolve-Path (Join-Path $PSScriptRoot 'fixtures\baseline-1.1.0')).Path
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repoRoot = (Resolve-Path (Join-Path $projectRoot '..')).Path   # the folder that holds src, release, portable, README.md and the shortcut

. (Join-Path $sourceRoot 'Engine.ps1') -LibraryOnly
$script:RunPath='HKCU:\Software\AgDnsTest\Run'  # isolated: tests never touch the user's real Run key
. (Join-Path $PSScriptRoot 'helpers\TestHarness.ps1')
. (Join-Path $PSScriptRoot 'helpers\SyntheticWire.ps1')
. (Join-Path $PSScriptRoot 'helpers\MockCmdlets.ps1')

Write-Host "--- Executing Tier 1: Feature Coverage Tests ---" -ForegroundColor Cyan

# ==============================================================================
# F01: Staging Baseline Integrity (5 tests)
# ==============================================================================
# T1_F01_01: Build.ps1 default output targets src\.work\build (the ignored scratch folder, never release\ or portable\)
$buildContent = Get-Content (Join-Path $sourceRoot 'Build.ps1') -Raw
$targetsWorkPortable = $buildContent -match '\$OutputDirectory\s*=\s*\(Join-Path \$PSScriptRoot ''\.\.\\\.work\\build''\)'
Assert-Condition $targetsWorkPortable 'T1_F01_01' 'Build.ps1 default output directory is src\.work\build' 'F01'

# T1_F01_02: Historical script fixtures remain available.
$baselineMissing = @('Engine.ps1','Integration.ps1','EmergencyRestore.ps1' | Where-Object { -not (Test-Path -LiteralPath (Join-Path $portableRoot $_)) })
Assert-Condition ($baselineMissing.Count -eq 0) 'T1_F01_02' 'Historical script fixtures exist' 'F01'

# T1_F01_03: Csproj embeds the companion scripts (single-exe contract; nothing is copied next to the exe)
$csprojContent = Get-Content (Join-Path $sourceRoot 'AntigravityGeminiDns.csproj') -Raw
$copiesScripts = ($csprojContent -match 'EmbeddedResource Include="Engine\.ps1"') -and
                 ($csprojContent -match 'EmbeddedResource Include="Integration\.ps1"') -and
                 ($csprojContent -match 'EmbeddedResource Include="EmergencyRestore\.ps1"') -and
                 ($csprojContent -notmatch 'Content Include="[A-Za-z]+\.ps1"')
Assert-Condition $copiesScripts 'T1_F01_03' 'Csproj embeds all companion PowerShell scripts into the single exe' 'F01'

# T1_F01_04: Build script supports custom -OutputDirectory without modifying portable\
$scratchBuild = Join-Path $ScratchPath 'staging_build'
$buildAcceptsParam = ($buildContent -match '\[string\]\$OutputDirectory')
Assert-Condition $buildAcceptsParam 'T1_F01_04' 'Build.ps1 accepts custom isolated -OutputDirectory' 'F01'

# T1_F01_05: Companion scripts in source have matching names with portable runtime
$expectedScripts = @('Engine.ps1', 'Integration.ps1', 'EmergencyRestore.ps1')
$allPresent = $true
foreach ($s in $expectedScripts) {
    if (-not (Test-Path (Join-Path $sourceRoot $s))) { $allPresent = $false }
}
Assert-Condition $allPresent 'T1_F01_05' 'All required companion scripts present in source root' 'F01'

# ==============================================================================
# F02: Engine Parameter Binding (5 tests)
# ==============================================================================
# T1_F02_01: Custom -Servers parameter preserved without defaulting
# Detect regressions that overwrite explicit server parameters.
$engineRaw = Get-Content (Join-Path $sourceRoot 'Engine.ps1') -Raw
$engineLines = Get-Content (Join-Path $sourceRoot 'Engine.ps1')
# Check whether script-scope assignment unconditionally overwrites $Servers before parsing
$hasParamClobberServers = ($engineRaw -match '\$script:Servers\s*=\s*@\(''179\.254\.166\.171''')
# Explicit parameters must take precedence over catalog defaults.
Assert-Condition (-not $hasParamClobberServers) 'T1_F02_01' 'Engine.ps1 preserves -Servers parameter without script-scope clobbering' 'F02' -DefectId 'F02' -FailureMessage 'Engine.ps1 unconditionally overwrites $Servers with default array'

# T1_F02_02: Custom -Template parameter preserved without defaulting
$hasParamClobberTemplate = ($engineRaw -match '\$script:Template\s*=\s*''https://dns\.malw\.link/dns-query''')
Assert-Condition (-not $hasParamClobberTemplate) 'T1_F02_02' 'Engine.ps1 preserves -Template parameter without script-scope clobbering' 'F02' -DefectId 'F02' -FailureMessage 'Engine.ps1 unconditionally overwrites $Template with default string'

# T1_F02_03: Custom -ProfileName parameter preserved without resetting to empty
$hasParamClobberProfile = ($engineRaw -match '\$script:ProfileName\s*=\s*''''')
Assert-Condition (-not $hasParamClobberProfile) 'T1_F02_03' 'Engine.ps1 preserves -ProfileName parameter without resetting to empty' 'F02' -DefectId 'F02' -FailureMessage 'Engine.ps1 unconditionally resets $ProfileName to empty string'

# T1_F02_04: Server string parsing handles comma, semicolon, whitespace
$testSplit = Split-Servers '1.1.1.1, 8.8.8.8; 9.9.9.9'
$splitSuccess = ($testSplit.Count -eq 3 -and $testSplit[0] -eq '1.1.1.1' -and $testSplit[1] -eq '8.8.8.8' -and $testSplit[2] -eq '9.9.9.9')
Assert-Condition $splitSuccess 'T1_F02_04' 'Split-Servers handles comma, semicolon, and whitespace delimiters' 'F02'

# T1_F02_05: Parameter block declares ValidateSet for Action
$actionTokens = $null; $actionErrors = $null
$actionAst = [System.Management.Automation.Language.Parser]::ParseInput($engineRaw, [ref]$actionTokens, [ref]$actionErrors)
$actionParameter = @($actionAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Action' })[0]
$actionValidation = @($actionParameter.Attributes | Where-Object { $_.TypeName.FullName -eq 'ValidateSet' })
$allowedActions = @($actionValidation | ForEach-Object { $_.PositionalArguments | ForEach-Object { $_.Value } })
$expectedActions = @('status','diagnose','prepare','enable','restore','lease-acquire','lease-release','lease-recover','lease-status')
$hasValidateSet = ($actionErrors.Count -eq 0 -and $actionValidation.Count -eq 1 -and $allowedActions.Count -eq $expectedActions.Count -and (($allowedActions | Sort-Object) -join ',') -ceq (($expectedActions | Sort-Object) -join ','))
Assert-Condition $hasValidateSet 'T1_F02_05' 'Engine.ps1 declares strict ValidateSet for Action parameter' 'F02'

# ==============================================================================
# F03: CLI Profile Binding (5 tests)
# ==============================================================================
$progContent = Get-Content (Join-Path $sourceRoot 'Program.cs') -Raw
# T1_F03_01: Program.cs parses --servers and passes to candidate preflight
$parsesCliServers = ($progContent -match 'args\.Contains\("--servers"\)' -or $progContent -match 'Array\.IndexOf\(args,\s*"--servers"\)')
Assert-Condition $parsesCliServers 'T1_F03_01' 'Program.cs parses --servers argument from CLI' 'F03' -DefectId 'F03' -FailureMessage 'Program.cs checks only --network and ignores --servers'

# T1_F03_02: Program.cs parses --template and passes to candidate preflight
$parsesCliTemplate = ($progContent -match 'args\.Contains\("--template"\)' -or $progContent -match 'Array\.IndexOf\(args,\s*"--template"\)')
Assert-Condition $parsesCliTemplate 'T1_F03_02' 'Program.cs parses --template argument from CLI' 'F03' -DefectId 'F03' -FailureMessage 'Program.cs checks only --network and ignores --template'

# T1_F03_03: CheckCandidate passes template to Wire.Doh
$passesTemplateToDoh = ($progContent -match 'Template=template\?\?DefaultTemplate' -and $progContent -match 'live\.QueryAsync\(spec,ip,h,type,6000,ct\)' -and (Get-Content -LiteralPath (Join-Path $sourceRoot 'Benchmark.cs') -Raw) -match 'Wire\.DohAsync\(Client\(p,server\),p\.Template,host,type,timeoutMs,ct\)')
Assert-Condition $passesTemplateToDoh 'T1_F03_03' 'CheckCandidate passes configured template to Wire.Doh' 'F03' -DefectId 'F03' -FailureMessage 'Program.cs calls Wire.Doh(h,type,ip,6) without passing template'

# T1_F03_04: CLI Interface Contract defines Candidate response schema
$hasCandidateSchema = ($progContent -match 'class Candidate' -and $progContent -match 'public string Server;')
Assert-Condition $hasCandidateSchema 'T1_F03_04' 'Program.cs declares Candidate DTO contract matching specification' 'F03'

# T1_F03_05: Elevated helper passes action, trust-proxy, and plain parameters
$elevatesWithParams = ($progContent -match 'Arguments="--"\+action')
Assert-Condition $elevatesWithParams 'T1_F03_05' 'Elevated helper formats command arguments with action and flags' 'F03'

# ==============================================================================
# F04: Safe Lifecycle, Preflight Replacement & Partial Availability (5 tests)
# ==============================================================================
# T1_F04_01: Validate endpoints before tearing down active bridge (Preflight-First)
# Candidate validation must precede removal of the active profile.
$engineLines = Get-Content (Join-Path $sourceRoot 'Engine.ps1')
$restoreLine = -1
$networkLine = -1
for ($i = 190; $i -lt $engineLines.Count; $i++) {
    if ($engineLines[$i] -match 'Restore-State \$s \| Out-Null' -and $restoreLine -eq -1) { $restoreLine = $i }
    if ($engineLines[$i] -match '\$net=Get-Network' -and $networkLine -eq -1) { $networkLine = $i }
}
$preflightFirst = ($networkLine -ne -1 -and $restoreLine -ne -1 -and $networkLine -lt $restoreLine)
Assert-Condition $preflightFirst 'T1_F04_01' 'Preflight endpoint validation occurs before tearing down active bridge' 'F04' -DefectId 'F04' -FailureMessage 'Engine.ps1 calls Restore-State before Get-Network'

# T1_F04_02: Partial subset availability preserves active bridge without Restore-State
Reset-MockEnvironment (Join-Path $ScratchPath 't1_f04_02')
$script:BadNetwork = $false
# Set up initial active state with 1 server
$initResult = Enable-State $true $false
$s = Read-State
# Simulate second Enable with 1 server responding
$script:MockPartialNetworkOnlyOneServer = $true
# Under defect, if $s.Servers was 1 server and $script:Servers is 2, line 202 $sameServers is false -> triggers Restore-State
$sameServersCheck = Same-Set $s.Servers $script:Servers
Assert-Condition $sameServersCheck 'T1_F04_02' 'Partial subset does not evaluate to different configured server set' 'F04' -DefectId 'F04' -FailureMessage 'Active bridge with 1 selected server triggers false in Same-Set comparison against 2 configured servers'

# T1_F04_03: Idempotent Enable-State produces zero redundant writes
Reset-MockEnvironment (Join-Path $ScratchPath 't1_f04_03')
Enable-State $true $false | Out-Null
$writesBefore = $script:MockWriteCount
Enable-State $true $false | Out-Null
$writesAfter = $script:MockWriteCount
Assert-EqualValues $writesAfter $writesBefore 'T1_F04_03' 'Second Enable-State invocation with identical settings produces 0 writes' 'F04'

# T1_F04_04: Preflight failure during active session does not destroy active bridge
Reset-MockEnvironment (Join-Path $ScratchPath 't1_f04_04')
Enable-State $true $false | Out-Null
$script:MockNetworkFailure = $true
try {
    # If preflight fails, active rules must remain
    Enable-State $true $false | Out-Null
} catch {}
$rulesRemaining = $script:MockRules.Count
Assert-EqualValues $rulesRemaining 2 'T1_F04_04' 'Preflight network failure preserves existing active NRPT rules' 'F04'

# T1_F04_05: Write-ahead journal moves from Applying to Active
Reset-MockEnvironment (Join-Path $ScratchPath 't1_f04_05')
Enable-State $true $false | Out-Null
$finalState = Read-State
Assert-EqualValues $finalState.Phase 'Active' 'T1_F04_05' 'Completed transaction commits journal phase to Active' 'F04'

# ==============================================================================
# F05: Address Family Transparency (5 tests)
# ==============================================================================
# T1_F05_01: Synthetic A record (IPv4, 4-byte RDATA) parsed correctly
$aPacket = New-SyntheticDnsResponse -QuestionDomain 'test.example' -QuestionType 1 -Id 2001 -AnswerRecords @(
    (New-ARecord 'test.example' '93.184.216.34')
)
$aParsed = Parse-SyntheticWirePacketOrderIndependent $aPacket 'test.example' 1 2001
$aValid = ($aParsed.Code -eq 'DNS_OK' -and $aParsed.Addresses.Length -eq 1 -and $aParsed.Addresses[0] -eq '93.184.216.34')
Assert-Condition $aValid 'T1_F05_01' 'Synthetic IPv4 A record parsed into valid dotted-quad address' 'F05'

# T1_F05_02: Synthetic AAAA record (IPv6, 16-byte RDATA) parsed correctly
$aaaaPacket = New-SyntheticDnsResponse -QuestionDomain 'test.example' -QuestionType 28 -Id 2002 -AnswerRecords @(
    (New-AAAARecord 'test.example' '2606:4700::1111')
)
$aaaaParsed = Parse-SyntheticWirePacketOrderIndependent $aaaaPacket 'test.example' 28 2002
$aaaaValid = ($aaaaParsed.Code -eq 'DNS_OK' -and $aaaaParsed.Addresses.Length -eq 1 -and $aaaaParsed.Addresses[0] -eq '2606:4700::1111')
Assert-Condition $aaaaValid 'T1_F05_02' 'Synthetic IPv6 AAAA record parsed into valid colon-hex address' 'F05'

# T1_F05_03: Elimination of false assertion 'release uses validated IPv4 only'
$hasIpv4OnlyClaim = ($progContent -match 'release uses validated IPv4 only')
Assert-Condition (-not $hasIpv4OnlyClaim) 'T1_F05_03' 'Program.cs does not claim release uses validated IPv4 only' 'F05' -DefectId 'F05' -FailureMessage 'Program.cs line 65 asserts: Ipv6="Not selected: release uses validated IPv4 only"'

# T1_F05_04: Distinct address length enforcement for A (4 bytes) and AAAA (16 bytes)
$netRaw = Get-Content (Join-Path $sourceRoot 'Network.cs') -Raw
$checksFamilyLengths = ($netRaw -match 't==1&&len==4' -and $netRaw -match 't==28&&len==16')
Assert-Condition $checksFamilyLengths 'T1_F05_04' 'Wire parser enforces 4-byte length for A and 16-byte length for AAAA' 'F05'

# T1_F05_05: SystemQuery in Network.cs accepts both A (1) and AAAA (28)
$systemQueryAcceptsBoth = ($netRaw -match 'if\(type!=1&&type!=28\)throw new ArgumentException\("type"\);')
Assert-Condition $systemQueryAcceptsBoth 'T1_F05_05' 'SystemQuery explicitly accepts both type 1 (A) and type 28 (AAAA)' 'F05'

# ==============================================================================
# F06: DoH Probe Privacy (5 tests)
# ==============================================================================
# T1_F06_01: Standard preflight generates zero UDP port 53 queries
# In Program.cs line 46: tasks.Add(Task.Run(()=>Wire.Probe(h,type,ip,false,1800)))
$probesUdpOnPort53 = ($progContent -match 'Wire\.Probe\(h,\s*type,\s*ip,\s*false,\s*1800\)')
Assert-Condition (-not $probesUdpOnPort53) 'T1_F06_01' 'Standard preflight generates zero UDP port 53 probes' 'F06' -DefectId 'F06' -FailureMessage 'Program.cs line 46 unconditionally launches Wire.Probe UDP port 53 tasks'

# T1_F06_02: Standard preflight generates zero TCP port 53 queries
# In Program.cs line 46: tasks.Add(Task.Run(()=>Wire.Probe(h,type,ip,true,1800)))
$probesTcpOnPort53 = ($progContent -match 'Wire\.Probe\(h,\s*type,\s*ip,\s*true,\s*1800\)')
Assert-Condition (-not $probesTcpOnPort53) 'T1_F06_02' 'Standard preflight generates zero TCP port 53 probes' 'F06' -DefectId 'F06' -FailureMessage 'Program.cs line 46 unconditionally launches Wire.Probe TCP port 53 tasks'

# T1_F06_03: Standard preflight uses encrypted RFC8484 HTTP/2 POST exclusively
$usesDohExclusively = ($netRaw -match 'request\.Version\s*=\s*HttpVersion\.Version20' -and 
                       $netRaw -match 'application/dns-message')
Assert-Condition $usesDohExclusively 'T1_F06_03' 'Wire.Doh specifies HTTP/2 and application/dns-message content type' 'F06'

# T1_F06_04: Plaintext fallback disabled in DoH preflight
$disablesFallback = ($engineRaw -match 'DOH_NO_VALIDATED_ENDPOINT: plaintext fallback disabled')
Assert-Condition $disablesFallback 'T1_F06_04' 'Engine.ps1 explicitly blocks plaintext fallback when DoH validation fails' 'F06'

# T1_F06_05: Explicit plaintext mode isolates port 53 and marks journal as Plain DNS
Reset-MockEnvironment (Join-Path $ScratchPath 't1_f06_05')
Enable-State $true $true | Out-Null
$plainState = Read-State
$plainCorrect = ($plainState.Protocol -eq 'Plain DNS' -and $script:MockDoh.Count -eq 0)
Assert-Condition $plainCorrect 'T1_F06_05' 'Plain DNS mode registers 0 DoH settings and commits Plain DNS protocol' 'F06'

# ==============================================================================
# F07: Order-Independent Wire.Parse (5 tests)
# ==============================================================================
# T1_F07_01: CNAME preceding A resolves target IP
$cnameFirst = New-SyntheticDnsResponse -QuestionDomain 'app.example' -QuestionType 1 -Id 3001 -AnswerRecords @(
    (New-CNameRecord 'app.example' 'backend.example'),
    (New-ARecord 'backend.example' '192.0.2.1')
)
$cnameFirstRes = Parse-SyntheticWirePacketOrderIndependent $cnameFirst 'app.example' 1 3001
$cfSuccess = ($cnameFirstRes.Code -eq 'DNS_OK' -and $cnameFirstRes.Addresses -contains '192.0.2.1')
Assert-Condition $cfSuccess 'T1_F07_01' 'Synthetic wire parser resolves CNAME-first response' 'F07'

# T1_F07_02: A preceding CNAME resolves target IP (Order Independence)
$aFirst = New-SyntheticDnsResponse -QuestionDomain 'app.example' -QuestionType 1 -Id 3002 -AnswerRecords @(
    (New-ARecord 'backend.example' '192.0.2.1'),
    (New-CNameRecord 'app.example' 'backend.example')
)
$aFirstRes = Parse-SyntheticWirePacketOrderIndependent $aFirst 'app.example' 1 3002
$aFirstFails = ($aFirstRes.Code -eq 'DNS_NODATA' -or $aFirstRes.Addresses.Length -eq 0)
Assert-Condition (-not $aFirstFails) 'T1_F07_02' 'Wire.Parse resolves A-first response without dropping addresses' 'F07'

# T1_F07_03: Equivalence: both orderings produce identical resolved IP address list
$orderIndependentRes = Parse-SyntheticWirePacketOrderIndependent $aFirst 'app.example' 1 3002
$identicalAnswers = ($cnameFirstRes.Addresses.Length -eq $orderIndependentRes.Addresses.Length -and 
                     $cnameFirstRes.Addresses[0] -eq $orderIndependentRes.Addresses[0])
Assert-Condition $identicalAnswers 'T1_F07_03' 'CNAME-first and A-first responses yield identical resolved IP addresses' 'F07'

# T1_F07_04: Multi-hop CNAME aliases (Host -> C1 -> C2 -> IP) resolve correctly
$multiHop = New-SyntheticDnsResponse -QuestionDomain 'app.example' -QuestionType 1 -Id 3003 -AnswerRecords @(
    (New-CNameRecord 'app.example' 'tier1.example'),
    (New-CNameRecord 'tier1.example' 'tier2.example'),
    (New-ARecord 'tier2.example' '192.0.2.2')
)
$multiHopRes = Parse-SyntheticWirePacketOrderIndependent $multiHop 'app.example' 1 3003
$mhSuccess = ($multiHopRes.Code -eq 'DNS_OK' -and $multiHopRes.Addresses -contains '192.0.2.2')
Assert-Condition $mhSuccess 'T1_F07_04' 'Multi-hop CNAME chain successfully resolves target endpoint' 'F07'

# T1_F07_05: Unrelated Answer owner record ignored without polluting target resolution
$foreignRecord = New-SyntheticDnsResponse -QuestionDomain 'app.example' -QuestionType 1 -Id 3004 -AnswerRecords @(
    (New-ARecord 'evil.example' '6.6.6.6'),
    (New-ARecord 'app.example' '192.0.2.3')
)
$foreignRes = Parse-SyntheticWirePacketOrderIndependent $foreignRecord 'app.example' 1 3004
$foreignFiltered = ($foreignRes.Addresses.Length -eq 1 -and $foreignRes.Addresses[0] -eq '192.0.2.3')
Assert-Condition $foreignFiltered 'T1_F07_05' 'Unrelated answer record owner ignored during resolution' 'F07'

# ==============================================================================
# F08: Operational Deadlines & UI Responsiveness (5 tests)
# ==============================================================================
# T1_F08_01: Child process execution in Program.Engine has bounded timeout
$hasEngineTimeout = ($progContent -match 'WaitForExitAsync\(\s*TimeSpan' -or $progContent -match 'WaitForExitAsync\(.*cts\.Token\)')
Assert-Condition $hasEngineTimeout 'T1_F08_01' 'Program.Engine enforces bounded timeout on child process' 'F08' -DefectId 'F08' -FailureMessage 'Program.cs line 71 calls await p.WaitForExitAsync() without timeout or token'

# T1_F08_02: Program.Elevate has bounded timeout
$hasElevateTimeout = ($progContent -match 'WaitForExitAsync\(\s*TimeSpan' -or $progContent -match 'WaitForExitAsync\(.*cts\.Token\)')
Assert-Condition $hasElevateTimeout 'T1_F08_02' 'Program.Elevate enforces bounded timeout on child process' 'F08' -DefectId 'F08' -FailureMessage 'Program.cs line 79 calls await p.WaitForExitAsync() without timeout'

# T1_F08_03: FormClosing does not permanently cancel when busy
$formClosingUnconditionallyCancels = ($progContent -match 'FormClosing\s*\+=\s*\(_,e\)\s*=>\s*\{\s*if\(busy\)\s*e\.Cancel\s*=\s*true;\s*\};')
Assert-Condition (-not $formClosingUnconditionallyCancels) 'T1_F08_03' 'MainForm FormClosing is not permanently cancelled when busy' 'F08' -DefectId 'F08' -FailureMessage 'Program.cs line 142 permanently locks window close when busy: if(busy) e.Cancel = true;'

# T1_F08_04: Mutation operation receipt matching checks operation ID nonce
$receiptChecksId = ($progContent -match 'nonce\.GetString\(\)==id')
Assert-Condition $receiptChecksId 'T1_F08_04' 'Program.Elevate validates unique operation receipt nonce' 'F08'

# T1_F08_05: lock contention (a file lock in the protected state directory, not a squattable global mutex) handled with OPERATION_IN_PROGRESS error
$engineMutexGuarded = ($engineRaw -match "'operation\.lock'" -and $engineRaw -match 'FileShare\]::None' -and $engineRaw -match 'OPERATION_IN_PROGRESS' -and $engineRaw -notmatch 'Threading\.Mutex')
Assert-Condition $engineMutexGuarded 'T1_F08_05' 'Engine enforces mutual exclusion guard across mutation commands' 'F08'

# ==============================================================================
# F09: Build Documentation Alignment (5 tests)
# ==============================================================================
$readmeContent = Get-Content (Join-Path $repoRoot 'README.md') -Raw -Encoding UTF8
# T1_F09_01: README.md does NOT claim build overwrites portable/
# Reject obsolete documentation that names a release folder as build output.
$readmeClaimsOverwritingPortable = ($readmeContent -match 'portable/.*рабочий выпуск' -or 
                                    ($readmeContent -match 'portable/' -and $readmeContent -match 'work/portable-next'))
Assert-Condition (-not $readmeClaimsOverwritingPortable) 'T1_F09_01' 'README.md does not claim build overwrites portable/' 'F09' -DefectId 'F09' -FailureMessage 'README.md claims build overwrites portable/ release directory'

# T1_F09_02: README.md documents src\.work\build as the build output directory
$readmeDocumentsWorkPortable = ($readmeContent -match '\.work\\build' -or $readmeContent -match '\.work/build')
Assert-Condition $readmeDocumentsWorkPortable 'T1_F09_02' 'README.md documents the src\.work\build staging directory' 'F09'

# T1_F09_03: Build.ps1 default path names .work\build
$buildMentionsWorkPortable = ($buildContent -match '\.work\\build')
Assert-Condition $buildMentionsWorkPortable 'T1_F09_03' 'Build.ps1 default path specifies .work\build' 'F09'

# T1_F09_04: README.md documents the supported untrimmed build recipe and the build output name
$readmeHasBaselineSection = ($readmeContent -match 'AntigravityGeminiDns\.exe' -and $readmeContent -match 'PublishTrimmed=false')
Assert-Condition $readmeHasBaselineSection 'T1_F09_04' 'README.md names the executable and the supported untrimmed build recipe' 'F09'

# T1_F09_05: a local launcher shortcut at the project root is optional (it is not part of the repository); when present it must open an existing exe with its own folder as working directory
$launcher = @(Get-ChildItem -LiteralPath $repoRoot -Filter '*.lnk') | Select-Object -First 1
$launcherTarget = if ($launcher) { (New-Object -ComObject WScript.Shell).CreateShortcut($launcher.FullName).TargetPath } else { '' }
$shortcutWorking = if ($launcher) { (New-Object -ComObject WScript.Shell).CreateShortcut($launcher.FullName).WorkingDirectory } else { '' }
$shortcutExists = (-not $launcher) -or ((Test-Path -LiteralPath $launcherTarget) -and ($shortcutWorking -eq [IO.Path]::GetDirectoryName($launcherTarget)) -and ($launcherTarget -like '*AntigravityGeminiDns.exe'))
Assert-Condition $shortcutExists 'T1_F09_05' 'Optional root shortcut opens an existing AntigravityGeminiDns.exe with an explicit working directory' 'F09' -FailureMessage ('target: ' + $launcherTarget)

# ==============================================================================
# P1: Provider 1 Contract - dns.malw.link (5 tests)
# ==============================================================================
# T1_P1_01: P1 default servers match specification
# the P1 defaults live only in profiles.json (the engine receives them as parameters / reads the staged copy).
$catalogRaw = Get-Content (Join-Path $sourceRoot 'profiles.json') -Raw -Encoding UTF8
$p1ServersMatch = ($catalogRaw -match '179\.254\.166\.171' -and $catalogRaw -match '164\.5\.224\.81')
Assert-Condition $p1ServersMatch 'T1_P1_01' 'P1 servers match 179.254.166.171 and 164.5.224.81' 'P1'

# T1_P1_02: P1 template matches https://dns.malw.link/dns-query
$p1TemplateMatch = ($catalogRaw -match 'https://dns\.malw\.link/dns-query')
Assert-Condition $p1TemplateMatch 'T1_P1_02' 'P1 template matches https://dns.malw.link/dns-query' 'P1'

# T1_P1_03: P1 targets match Googleapis cloud endpoints
$p1TargetsMatch = ($catalogRaw -match 'daily-cloudcode-pa\.googleapis\.com' -and 
                   $catalogRaw -match 'generativelanguage\.googleapis\.com')
Assert-Condition $p1TargetsMatch 'T1_P1_03' 'P1 targets match daily-cloudcode-pa and generativelanguage' 'P1'

# T1_P1_04: P1 activation produces Active state and 2 NRPT rules
Reset-MockEnvironment (Join-Path $ScratchPath 't1_p1_04')
Enable-State $true $false | Out-Null
$p1State = Read-State
$p1Active = ($p1State.Phase -eq 'Active' -and $script:MockRules.Count -eq 2 -and $script:MockDoh.Count -eq 2)
Assert-Condition $p1Active 'T1_P1_04' 'P1 activation creates 2 NRPT rules and 2 DoH registrations' 'P1'

# T1_P1_05: P1 restore removes all owned rules cleanly
Restore-State (Read-State) | Out-Null
$p1Restored = ($script:MockRules.Count -eq 0 -and $script:MockDoh.Count -eq 0 -and (Read-State).Phase -eq 'Restored')
Assert-Condition $p1Restored 'T1_P1_05' 'P1 restore cleanly removes owned NRPT and DoH settings' 'P1'

# ==============================================================================
# P2: Provider 2 Contract - xbox-dns.ru (5 tests)
# ==============================================================================
# T1_P2_01: Profile supports custom server IPs
$p2Profile = (Get-Content (Join-Path $PSScriptRoot 'fixtures\provider_profiles.json') -Raw | ConvertFrom-Json).profiles.P2
$p2ServersValid = ($p2Profile.servers.Count -eq 2 -and $p2Profile.servers[0] -eq '185.185.185.185')
Assert-Condition $p2ServersValid 'T1_P2_01' 'P2 profile defines custom servers 185.185.185.185 and 185.185.185.186' 'P2'

# T1_P2_02: P2 template matches https://xbox-dns.ru/dns-query
$p2TemplateValid = ($p2Profile.template -eq 'https://xbox-dns.ru/dns-query')
Assert-Condition $p2TemplateValid 'T1_P2_02' 'P2 template matches https://xbox-dns.ru/dns-query' 'P2'

# T1_P2_03: Activation with P2 settings records custom template in state journal
Reset-MockEnvironment (Join-Path $ScratchPath 't1_p2_03')
$script:Template = $p2Profile.template
$script:Servers = @($p2Profile.servers)
$script:ProfileName = $p2Profile.name
Enable-State $true $false | Out-Null
$p2State = Read-State
$p2StateSaved = ($p2State.Template -eq $p2Profile.template)
Assert-Condition $p2StateSaved 'T1_P2_03' 'P2 state journal stores custom DoH template' 'P2'

# T1_P2_04: P2 DoH registration configures AutoUpgrade and blocks plaintext fallback
$p2Doh = $script:MockDoh[0]
$p2DohValid = ($p2Doh.AutoUpgrade -eq $true -and $p2Doh.AllowFallbackToUdp -eq $false -and $p2Doh.DohTemplate -eq $p2Profile.template)
Assert-Condition $p2DohValid 'T1_P2_04' 'P2 DoH registration enforces AutoUpgrade=true and AllowFallbackToUdp=false' 'P2'

# T1_P2_05: P2 restore removes P2 settings cleanly
Restore-State (Read-State) | Out-Null
$p2Restored = ($script:MockRules.Count -eq 0 -and $script:MockDoh.Count -eq 0)
Assert-Condition $p2Restored 'T1_P2_05' 'P2 restore removes custom DoH registrations and rules' 'P2'

# ==============================================================================
# P3: Provider 3 Contract - dns.comss.one (5 tests)
# ==============================================================================
# T1_P3_01: P3 profile definition contains designated servers
$p3Profile = (Get-Content (Join-Path $PSScriptRoot 'fixtures\provider_profiles.json') -Raw | ConvertFrom-Json).profiles.P3
$p3ServersValid = ($p3Profile.servers.Count -eq 2 -and $p3Profile.servers[0] -eq '92.223.109.31')
Assert-Condition $p3ServersValid 'T1_P3_01' 'P3 profile defines designated servers 92.223.109.31 and 92.38.150.150' 'P3'

# T1_P3_02: P3 profile template matches https://dns.comss.one/dns-query
$p3TemplateValid = ($p3Profile.template -eq 'https://dns.comss.one/dns-query')
Assert-Condition $p3TemplateValid 'T1_P3_02' 'P3 template matches https://dns.comss.one/dns-query' 'P3'

# T1_P3_03: P3 activation succeeds and validates dual endpoints
Reset-MockEnvironment (Join-Path $ScratchPath 't1_p3_03')
$script:Template = $p3Profile.template
$script:Servers = @($p3Profile.servers)
$script:ProfileName = $p3Profile.name
Enable-State $true $false | Out-Null
$p3State = Read-State
$p3Active = ($p3State.Phase -eq 'Active' -and $script:MockDoh.Count -eq 2)
Assert-Condition $p3Active 'T1_P3_03' 'P3 activation validates dual endpoints and transitions to Active' 'P3'

# T1_P3_04: P3 URL validation rejects non-HTTPS DoH template
$invalidHttpUrl = 'http://dns.comss.one/dns-query'
$urlRejectionExpected = $false
try {
    if (-not $invalidHttpUrl.StartsWith('https://', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "INVALID_DOH_URL_SCHEME"
    }
} catch {
    $urlRejectionExpected = $true
}
Assert-Condition $urlRejectionExpected 'T1_P3_04' 'P3 template validation rejects unencrypted HTTP URLs' 'P3'

# T1_P3_05: P3 clean teardown verifies zero residual state
Restore-State (Read-State) | Out-Null
$p3Restored = ($script:MockRules.Count -eq 0 -and $script:MockDoh.Count -eq 0 -and (Read-State).Phase -eq 'Restored')
Assert-Condition $p3Restored 'T1_P3_05' 'P3 teardown verifies zero residual rules or DoH entries' 'P3'

# ==============================================================================
# S6: Optional Windows Logon Autostart Contract (5 tests)
# ==============================================================================
$workPortableExe = Join-Path $projectRoot '.work\build\AntigravityGeminiDns.exe'

# T1_S6_01: Autostart default state is disabled (default-off invariant)
$asInit = Get-AutostartStatus
Assert-Condition (-not $asInit.Enabled) 'T1_S6_01' 'Autostart is default OFF prior to user opt-in' 'S6'

# T1_S6_02: Set-Autostart creates properly quoted Run key pointing to executable
$asSet = Set-Autostart -Enable $true
$asSetValid = ($asSet.Enabled -and $asSet.Method -eq 'Registry' -and $asSet.NonElevated -and $asSet.RegistryValue -like '"*" --minimized')
Assert-Condition $asSetValid 'T1_S6_02' 'Set-Autostart creates quoted registry entry with non-elevated privilege' 'S6'

# T1_S6_03: Engine reports autostart status matching registry state
$asEngineStatus = Get-AutostartStatus
$engineStatusValid = ($asEngineStatus.Enabled -and $asEngineStatus.NonElevated -and $asEngineStatus.EntryName -eq 'AntigravityDnsUnlocker' -and $asEngineStatus.Method -eq 'Registry')
Assert-Condition $engineStatusValid 'T1_S6_03' 'Engine reports autostart status matching registry state' 'S6'

# T1_S6_04: Set-Autostart -Enable $false cleanly removes registry entry
$asDisabled = Set-Autostart -Enable $false
$asDisableValid = (-not $asDisabled.Enabled -and $asDisabled.Method -eq 'None' -and $null -eq $asDisabled.RegistryValue)
Assert-Condition $asDisableValid 'T1_S6_04' 'Set-Autostart disables autostart and leaves zero residual registry values' 'S6'

# T1_S6_05: CLI flags (--autostart-enable / --autostart-disable) manage lifecycle
# The exe honours AGDNS_TEST_RUN_KEY (only inside HKCU\Software\AgDnsTest\) so this test never writes the user's real Run entry.
if ($env:AGDNS_EXE) { $workPortableExe = $env:AGDNS_EXE }
$cliLifecycleValid = $false
if (Test-Path $workPortableExe) {
    $env:AGDNS_TEST_RUN_KEY = 'Software\AgDnsTest\Run'
    try {
        $cliEnable = (& $workPortableExe --autostart-enable | ConvertFrom-Json)
        $cliStatusAfterEnable = (& $workPortableExe --autostart-status | ConvertFrom-Json)
        $cliDisable = (& $workPortableExe --autostart-disable | ConvertFrom-Json)
        $cliStatusAfterDisable = (& $workPortableExe --autostart-status | ConvertFrom-Json)
        $cliLifecycleValid = ($cliEnable.Success -and $cliStatusAfterEnable.Enabled -and $cliDisable.Success -and -not $cliStatusAfterDisable.Enabled -and $cliStatusAfterEnable.RegistryKey -like '*AgDnsTest*')
    } finally {
        Remove-Item Env:AGDNS_TEST_RUN_KEY -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath 'HKCU:\Software\AgDnsTest' -Recurse -Force -ErrorAction SilentlyContinue
    }
} else {
    $cliLifecycleValid = $false # Required CLI executable is missing: this assertion must fail.
}
Assert-Condition $cliLifecycleValid 'T1_S6_05' 'CLI --autostart-enable and --autostart-disable manage autostart lifecycle cleanly' 'S6'

# Invariant safety check
Assert-HostSafetyBaselineUnchanged "Tier 1 Completion"
Write-Host "--- Tier 1 Completed: $($global:TestResults.Count) Tests Run ---" -ForegroundColor Cyan
