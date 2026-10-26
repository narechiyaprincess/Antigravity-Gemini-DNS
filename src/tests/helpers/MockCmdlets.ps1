# MockCmdlets.ps1 - Pure offline mock cmdlet wrappers and simulation environment
# Guarantees ZERO mutations to Windows host network adapters, live NRPT rules, or DoH settings.

$ErrorActionPreference = 'Stop'

# Mock State storage
$script:MockRules = [System.Collections.Generic.List[PSObject]]::new()
$script:MockDoh = [System.Collections.Generic.List[PSObject]]::new()
$script:MockWriteCount = 0
$script:MockDohAddCount = 0
$script:MockRuleAddCount = 0

# Fault Injection Controls
$script:MockFailDohAt = 0
$script:MockFailRemoveRuleAt = 0   # fail the Nth Remove-DnsClientNrptRule call (interrupted restore)
$script:MockRuleRemoveCount = 0
$script:MockFailDohFrom = 0   # fail every DoH registration from the Nth one onwards (compensation tests)
$script:MockFailAfterRule = $false
$script:MockNetworkFailure = $false
$script:MockPartialNetworkOnlyOneServer = $false
$script:MockNetworkOfflineTimeout = $false
$script:MockIsAdmin = $true

function Reset-MockEnvironment {
    param([string]$ScratchPath)
    
    $script:MockRules.Clear()
    $script:MockDoh.Clear()
    $script:MockWriteCount = 0
    $script:MockDohAddCount = 0
    $script:MockRuleAddCount = 0
    $script:MockFailDohAt = 0
    $script:MockFailDohFrom = 0
    $script:MockFailRemoveRuleAt = 0
    $script:MockRuleRemoveCount = 0
    $script:MockFailAfterRule = $false
    $script:MockNetworkFailure = $false
    $script:MockPartialNetworkOnlyOneServer = $false
    $script:MockNetworkOfflineTimeout = $false
    $script:MockIsAdmin = $true
    
    if ($ScratchPath) {
        $script:Root = $ScratchPath
        $script:StatePath = Join-Path $ScratchPath 'state.json'
        if (-not (Test-Path -LiteralPath $ScratchPath)) {
            [System.IO.Directory]::CreateDirectory($ScratchPath) | Out-Null
        }
    }
}

# Mocked Cmdlet Implementations (Interceptors)
# Engine cache invalidation must never reach the real ipconfig.exe in offline tests.
$script:MockCacheFlushCount = 0
function Clear-DnsResolverCache { $script:MockCacheFlushCount++; return $true }
# Legacy Set-Autostart queries schtasks.exe even with an isolated RunPath.
# Intercept it too: no live task can be discovered or deleted by this fixture.
function Invoke-ScheduledTaskTool([string[]]$Arguments) {
    if (@($Arguments | Where-Object { $_ -eq '/Query' }).Count -ne 1) { throw 'OFFLINE_SCHEDULED_TASK_MUTATION_BLOCKED' }
    return 1
}
function Get-LegacyAutostartTask { return $null }
function Register-ScheduledTask { throw 'OFFLINE_SCHEDULED_TASK_MUTATION_BLOCKED' }
function Unregister-ScheduledTask { throw 'OFFLINE_SCHEDULED_TASK_MUTATION_BLOCKED' }

function Get-Rules {
    return @($script:MockRules)
}

function Get-Effective {
    return @($script:MockRules | Select-Object Namespace, NameServers)
}

function Get-Doh {
    return @($script:MockDoh | Select-Object ServerAddress, DohTemplate, AutoUpgrade, AllowFallbackToUdp)
}

# Restore-State asks for adapter DNS servers to detect shared DoH registrations; the real cmdlet is a CIM call and must never run (or hang) in offline tests.
function Get-DnsClientServerAddress {
    return @()
}

function Test-Admin {
    return [bool]$script:MockIsAdmin
}

function Test-HostsConflict {
    return $false
}

function Assert-Capabilities($Encrypted) {
    # No-op in mock environment
}

function Initialize-StateDirectory {
    if (-not (Test-Path -LiteralPath $script:Root)) {
        [System.IO.Directory]::CreateDirectory($script:Root) | Out-Null
    }
}

function Add-DnsClientDohServerAddress {
    param($ServerAddress, $DohTemplate, $AllowFallbackToUdp, $AutoUpgrade, $ErrorAction)
    $script:MockWriteCount++
    $script:MockDohAddCount++
    
    if ($script:MockFailDohAt -gt 0 -and $script:MockDohAddCount -eq $script:MockFailDohAt) {
        throw "SIMULATED_DOH_FAILURE_INJECTION"
    }
    if ($script:MockFailDohFrom -gt 0 -and $script:MockDohAddCount -ge $script:MockFailDohFrom) {
        throw "SIMULATED_DOH_FAILURE_FROM_INJECTION"
    }
    
    $entry = [PSCustomObject]@{
        ServerAddress      = $ServerAddress
        DohTemplate        = $DohTemplate
        AllowFallbackToUdp = $AllowFallbackToUdp
        AutoUpgrade        = $AutoUpgrade
    }
    $script:MockDoh.Add($entry)
    return $entry
}

function Remove-DnsClientDohServerAddress {
    param($ServerAddress, $ErrorAction)
    $script:MockWriteCount++
    for ($i = $script:MockDoh.Count - 1; $i -ge 0; $i--) {
        if ($script:MockDoh[$i].ServerAddress -eq $ServerAddress) {
            $script:MockDoh.RemoveAt($i)
        }
    }
}

function Add-DnsClientNrptRule {
    param(
        $Namespace, $NameServers, $DisplayName, $Comment, $NameEncoding,
        $DAProxyType, $DAIPsecEncryptionType, $DnsSecIPsecEncryptionType,
        $DAEnable, $DAIPsecRequired, $DnsSecEnable, $DnsSecValidationRequired,
        $DnsSecIPsecRequired, [switch]$PassThru, $ErrorAction
    )
    $script:MockWriteCount++
    $script:MockRuleAddCount++
    
    $ruleId = "{" + [Guid]::NewGuid().ToString() + "}"
    $rule = [PSCustomObject]@{
        Name                      = $ruleId
        Namespace                 = @($Namespace)
        NameServers               = $NameServers
        DisplayName               = $DisplayName
        Comment                   = $Comment
        NameEncoding              = $NameEncoding
        DnsSecValidationRequired  = $false
        DirectAccessDnsServers    = @()
        DirectAccessEnabled       = $false
        DirectAccessProxyName     = ""
        DirectAccessProxyType     = ""
        DirectAccessQueryIPsecEncryption = ""
        DirectAccessQueryIPsecRequired   = $false
        DnsSecEnabled             = $false
        DnsSecQueryIPsecEncryption= ""
        DnsSecQueryIPsecRequired  = $false
        IPsecCARestriction        = ""
    }
    $script:MockRules.Add($rule)
    
    if ($script:MockFailAfterRule) {
        throw "SIMULATED_CRASH_AFTER_RULE_INJECTION"
    }
    return $rule
}

function Remove-DnsClientNrptRule {
    param($Name, [switch]$Force, $ErrorAction)
    $script:MockRuleRemoveCount++
    if ($script:MockFailRemoveRuleAt -gt 0 -and $script:MockRuleRemoveCount -eq $script:MockFailRemoveRuleAt) { throw "SIMULATED_REMOVE_FAILURE_INJECTION" }
    $script:MockWriteCount++
    for ($i = $script:MockRules.Count - 1; $i -ge 0; $i--) {
        if ($script:MockRules[$i].Name -eq $Name) {
            $script:MockRules.RemoveAt($i)
        }
    }
}

function Get-Network {
    if ($script:MockNetworkOfflineTimeout) {
        throw "NETWORK_PROBE_FAILED: timeout or network unreachable"
    }
    
    $serversToTest = if ($script:Servers -and $script:Servers.Count -gt 0) { $script:Servers } else { @('179.254.166.171','164.5.224.81') }
    $candidates = [System.Collections.Generic.List[PSObject]]::new()
    
    $idx = 0
    foreach ($srv in $serversToTest) {
        $idx++
        $isValid = $true
        if ($script:MockNetworkFailure) {
            $isValid = $false
        } elseif ($script:MockPartialNetworkOnlyOneServer -and $idx -gt 1) {
            $isValid = $false
        }
        
        $answers = [PSCustomObject]@{
            'daily-cloudcode-pa.googleapis.com' = @('179.254.166.174')
            'generativelanguage.googleapis.com' = @('179.254.166.175')
        }
        
        $c = [PSCustomObject]@{
            Server         = $srv
            DohValidated   = $isValid
            PlainValidated = $isValid
            TargetA        = @('179.254.166.174')
            TargetAnswers  = $answers
        }
        $candidates.Add($c)
    }
    
    return [PSCustomObject]@{
        TimestampUtc = [DateTime]::UtcNow.ToString('o')
        Resolver     = $(if ($script:Template) { $script:Template } else { 'dns.malw.link' })
        Target       = 'daily-cloudcode-pa.googleapis.com'
        Protocol     = 'RFC8484 HTTP/2 with default TLS validation'
        Candidates   = $candidates.ToArray()
    }
}

function Get-SystemAnswers([string]$Domain, [string]$Type='A') {
    if ($Domain -eq 'generativelanguage.googleapis.com') {
        return @('179.254.166.175')
    }
    return @('179.254.166.174')
}
