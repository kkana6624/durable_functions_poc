<#
.SYNOPSIS
    Tests private connectivity to Azure Storage endpoints (Blob, Queue, Table).

.DESCRIPTION
    Validates DNS resolution to private IP addresses (RFC 1918) and TCP port 443
    reachability for Azure Storage endpoints. Detects public IP leakage and
    connection failures, returning structured diagnostic results.
    Does not log or expose any SAS tokens, secrets, or keys.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$StorageAccountName,

    [Parameter(Mandatory = $false)]
    [string]$BlobEndpoint,

    [Parameter(Mandatory = $false)]
    [string]$QueueEndpoint,

    [Parameter(Mandatory = $false)]
    [string]$TableEndpoint,

    [Parameter(Mandatory = $false)]
    [string]$ExpectedPrivateSubnetPrefix,

    [Parameter(Mandatory = $false)]
    [switch]$SkipAuthorizationCheck,

    [Parameter(Mandatory = $false)]
    [int]$TimeoutMs = 5000,

    [Parameter(Mandatory = $false)]
    [switch]$AsJson,

    [Parameter(Mandatory = $false)]
    [scriptblock]$DnsResolver,

    [Parameter(Mandatory = $false)]
    [scriptblock]$TcpTester
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Redaction & URL Helpers ---

function Protect-EndpointUrl {
    param([string]$Url)
    if ([string]::IsNullOrWhiteSpace($Url)) { return $Url }
    # Strip query parameters and fragment to prevent SAS token or key leakage
    if ($Url -match '^([^?#]+)') {
        return $Matches[1]
    }
    return $Url
}

function Get-EndpointFqdn {
    param([string]$EndpointUri)
    if ([string]::IsNullOrWhiteSpace($EndpointUri)) { return $null }
    $clean = Protect-EndpointUrl $EndpointUri.Trim()
    if ($clean -match '^[a-zA-Z][a-zA-Z0-9+.-]*://([^/:]+)') {
        return $Matches[1]
    }
    if ($clean -match '^([^/:]+)') {
        return $Matches[1]
    }
    return $clean
}

# --- Network & IP Validation Helpers ---

function Test-IsRfc1918PrivateIp {
    param([System.Net.IPAddress]$IpAddress)
    if (-not $IpAddress) { return $false }
    if ($IpAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        return $false
    }
    $bytes = $IpAddress.GetAddressBytes()
    # 10.0.0.0/8
    if ($bytes[0] -eq 10) { return $true }
    # 172.16.0.0/12 (172.16.0.0 - 172.31.255.255)
    if ($bytes[0] -eq 172 -and ($bytes[1] -ge 16 -and $bytes[1] -le 31)) { return $true }
    # 192.168.0.0/16
    if ($bytes[0] -eq 192 -and $bytes[1] -eq 168) { return $true }
    return $false
}

function Test-MatchesSubnetPrefix {
    param(
        [System.Net.IPAddress]$IpAddress,
        [string]$PrefixOrCidr
    )
    if ([string]::IsNullOrWhiteSpace($PrefixOrCidr)) { return $true }
    $target = $PrefixOrCidr.Trim()

    # IPv4 CIDR matching (e.g. 10.80.2.0/24)
    if ($target -match '^(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})/(\d{1,2})$') {
        $baseIp = $null
        if (-not [System.Net.IPAddress]::TryParse($Matches[1], [ref]$baseIp)) {
            return $false
        }
        $prefixLen = [int]$Matches[2]
        if ($prefixLen -lt 0 -or $prefixLen -gt 32) { return $false }

        $ipBytes = $IpAddress.GetAddressBytes()
        $baseBytes = $baseIp.GetAddressBytes()
        $maskBytes = [byte[]]::new(4)
        for ($i = 0; $i -lt 4; $i++) {
            $bits = [Math]::Max(0, [Math]::Min(8, $prefixLen - ($i * 8)))
            if ($bits -eq 8) {
                $maskBytes[$i] = [byte]255
            } elseif ($bits -eq 0) {
                $maskBytes[$i] = [byte]0
            } else {
                $maskBytes[$i] = [byte](256 - [Math]::Pow(2, 8 - $bits))
            }
        }

        for ($i = 0; $i -lt 4; $i++) {
            if (($ipBytes[$i] -band $maskBytes[$i]) -ne ($baseBytes[$i] -band $maskBytes[$i])) {
                return $false
            }
        }
        return $true
    }

    # String prefix matching (e.g. '10.80.2.' or '10.0.')
    return $IpAddress.ToString().StartsWith($target)
}

# --- Pluggable / Mockable Operations ---

function Invoke-ResolveStorageDns {
    param([string]$HostName, [scriptblock]$CustomResolver)
    if ($CustomResolver) {
        return (& $CustomResolver $HostName)
    }
    if (Get-Command 'Resolve-StorageHostDns' -ErrorAction SilentlyContinue) {
        return (Resolve-StorageHostDns -HostName $HostName)
    }
    return ([System.Net.Dns]::GetHostAddresses($HostName))
}

function Invoke-TestStorageTcp {
    param(
        [string]$HostName,
        [int]$Port = 443,
        [int]$TimeoutMilliseconds = 5000,
        [scriptblock]$CustomTcpTester
    )
    if ($CustomTcpTester) {
        return (& $CustomTcpTester $HostName $Port $TimeoutMilliseconds)
    }
    if (Get-Command 'Test-StorageTcpHandshake' -ErrorAction SilentlyContinue) {
        return (Test-StorageTcpHandshake -HostName $HostName -Port $Port -TimeoutMilliseconds $TimeoutMilliseconds)
    }

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $asyncResult = $client.BeginConnect($HostName, $Port, $null, $null)
        $waitHandle = $asyncResult.AsyncWaitHandle
        if (-not $waitHandle.WaitOne($TimeoutMilliseconds, $false)) {
            $client.Close()
            return [pscustomobject]@{
                Success      = $false
                ErrorMessage = "TCP connection to $HostName`:$Port timed out after $TimeoutMilliseconds ms"
            }
        }
        $client.EndConnect($asyncResult)
        return [pscustomobject]@{
            Success      = $true
            ErrorMessage = $null
        }
    }
    catch {
        return [pscustomobject]@{
            Success      = $false
            ErrorMessage = $_.Exception.Message
        }
    }
    finally {
        $client.Dispose()
    }
}

# --- Main Verification Logic ---

function Invoke-PrivateStorageConnectivity {
    param(
        [string]$StorageAccountName,
        [string]$BlobEndpoint,
        [string]$QueueEndpoint,
        [string]$TableEndpoint,
        [string]$ExpectedPrivateSubnetPrefix,
        [bool]$SkipAuthorizationCheck = $false,
        [int]$TimeoutMs = 5000,
        [switch]$AsJson,
        [scriptblock]$DnsResolver,
        [scriptblock]$TcpTester
    )

    if ([string]::IsNullOrWhiteSpace($BlobEndpoint)) {
        $BlobEndpoint = "https://${StorageAccountName}.blob.core.windows.net"
    }
    if ([string]::IsNullOrWhiteSpace($QueueEndpoint)) {
        $QueueEndpoint = "https://${StorageAccountName}.queue.core.windows.net"
    }
    if ([string]::IsNullOrWhiteSpace($TableEndpoint)) {
        $TableEndpoint = "https://${StorageAccountName}.table.core.windows.net"
    }

    $services = @(
        @{ Name = 'blob';  RawEndpoint = $BlobEndpoint },
        @{ Name = 'queue'; RawEndpoint = $QueueEndpoint },
        @{ Name = 'table'; RawEndpoint = $TableEndpoint }
    )

    $endpointResults = @()

    foreach ($svc in $services) {
        $serviceName = $svc.Name
        $rawEndpoint = $svc.RawEndpoint
        $safeEndpoint = Protect-EndpointUrl $rawEndpoint
        $fqdn = Get-EndpointFqdn $rawEndpoint

        $resIps = @()
        $isPrivate = $false
        $subnetMatch = $null
        $tcpSuccess = $false
        $status = 'Failed'
        $errorMsg = $null

        if ([string]::IsNullOrWhiteSpace($fqdn)) {
            $endpointResults += [pscustomobject]@{
                Service      = $serviceName
                Endpoint     = $safeEndpoint
                FQDN         = $fqdn
                ResolvedIps  = @()
                IsPrivateIp  = $false
                SubnetMatch  = $null
                TcpSuccess   = $false
                Status       = 'Failed'
                ErrorMessage = "Invalid endpoint URI: unable to extract host name"
            }
            continue
        }

        # 1. DNS Resolution
        [System.Net.IPAddress[]]$resolvedAddresses = @()
        try {
            $rawDns = Invoke-ResolveStorageDns -HostName $fqdn -CustomResolver $DnsResolver
            if ($rawDns) {
                $resolvedAddresses = [System.Net.IPAddress[]]@($rawDns)
            }
        }
        catch {
            $errorMsg = "DNS resolution failed for '$fqdn': $($_.Exception.Message)"
        }

        if (@($resolvedAddresses).Count -eq 0 -and [string]::IsNullOrEmpty($errorMsg)) {
            $errorMsg = "DNS resolution returned no IP addresses for '$fqdn'"
        }

        if (@($resolvedAddresses).Count -gt 0) {
            $resIps = @($resolvedAddresses | ForEach-Object { $_.ToString() })

            # Check if all resolved addresses are RFC 1918 private IPs
            $nonPrivateIps = @($resolvedAddresses | Where-Object { -not (Test-IsRfc1918PrivateIp $_) })
            if ($nonPrivateIps.Count -gt 0) {
                $isPrivate = $false
                $errorMsg = "FQDN '$fqdn' resolved to public IP address ($($nonPrivateIps -join ', ')). Private Endpoint or Private DNS zone link is not active."
            }
            else {
                $isPrivate = $true
            }

            # Check ExpectedPrivateSubnetPrefix if specified
            if ($isPrivate -and -not [string]::IsNullOrWhiteSpace($ExpectedPrivateSubnetPrefix)) {
                $mismatchedIps = @($resolvedAddresses | Where-Object { -not (Test-MatchesSubnetPrefix $_ $ExpectedPrivateSubnetPrefix) })
                if ($mismatchedIps.Count -gt 0) {
                    $subnetMatch = $false
                    $errorMsg = "Resolved IP ($($resIps -join ', ')) does not match expected subnet prefix '$ExpectedPrivateSubnetPrefix'"
                }
                else {
                    $subnetMatch = $true
                }
            }
            elseif ($isPrivate) {
                $subnetMatch = $null
            }

            # 2. TCP Port 443 Handshake
            # Only attempt TCP handshake if DNS succeeded and resolved to private IP (and prefix matched if configured)
            if ($isPrivate -and ($null -eq $subnetMatch -or $subnetMatch -eq $true)) {
                $tcpResult = Invoke-TestStorageTcp -HostName $fqdn -Port 443 -TimeoutMilliseconds $TimeoutMs -CustomTcpTester $TcpTester
                if ($tcpResult.Success) {
                    $tcpSuccess = $true
                    $status = 'Pass'
                }
                else {
                    $tcpSuccess = $false
                    $errorMsg = "TCP port 443 connection failed: $($tcpResult.ErrorMessage)"
                }
            }
        }

        $endpointResults += [pscustomobject]@{
            Service      = $serviceName
            Endpoint     = $safeEndpoint
            FQDN         = $fqdn
            ResolvedIps  = $resIps
            IsPrivateIp  = $isPrivate
            SubnetMatch  = $subnetMatch
            TcpSuccess   = $tcpSuccess
            Status       = $status
            ErrorMessage = $errorMsg
        }
    }

    $allPassed = @($endpointResults | Where-Object { $_.Status -ne 'Pass' }).Count -eq 0

    $outputObject = [pscustomobject]@{
        StorageAccountName          = $StorageAccountName
        AllPassed                   = $allPassed
        ExpectedPrivateSubnetPrefix = $ExpectedPrivateSubnetPrefix
        SkipAuthorizationCheck      = [bool]$SkipAuthorizationCheck
        Endpoints                   = $endpointResults
    }

    if ($AsJson) {
        return ($outputObject | ConvertTo-Json -Depth 5)
    }

    return $outputObject
}

# Invoke if run directly as a script
Invoke-PrivateStorageConnectivity `
    -StorageAccountName $StorageAccountName `
    -BlobEndpoint $BlobEndpoint `
    -QueueEndpoint $QueueEndpoint `
    -TableEndpoint $TableEndpoint `
    -ExpectedPrivateSubnetPrefix $ExpectedPrivateSubnetPrefix `
    -SkipAuthorizationCheck:$SkipAuthorizationCheck `
    -TimeoutMs $TimeoutMs `
    -AsJson:$AsJson `
    -DnsResolver $DnsResolver `
    -TcpTester $TcpTester
