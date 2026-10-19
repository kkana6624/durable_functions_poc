$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module Pester -ErrorAction SilentlyContinue

$scriptPath = Join-Path $PSScriptRoot '../Test-PrivateStorageConnectivity.ps1'

Describe "Test-PrivateStorageConnectivity" {
    Context "正常系 (All endpoints resolve to private IP and TCP succeeds)" {
        It "Passes when all endpoints resolve to RFC 1918 private IPs and TCP port 443 succeeds" {
            $mockDns = {
                param($HostName)
                switch -Wildcard ($HostName) {
                    '*.blob.*'  { return @([System.Net.IPAddress]::Parse('10.80.2.10')) }
                    '*.queue.*' { return @([System.Net.IPAddress]::Parse('10.80.2.11')) }
                    '*.table.*' { return @([System.Net.IPAddress]::Parse('10.80.2.12')) }
                    default     { return @([System.Net.IPAddress]::Parse('10.80.2.10')) }
                }
            }
            $mockTcp = {
                param($HostName, $Port, $Timeout)
                return [pscustomobject]@{ Success = $true; ErrorMessage = $null }
            }

            $result = & $scriptPath -StorageAccountName 'pocstorage' `
                -ExpectedPrivateSubnetPrefix '10.80.2.0/24' `
                -DnsResolver $mockDns `
                -TcpTester $mockTcp

            $result.AllPassed | Should Be $true
            $result.StorageAccountName | Should Be 'pocstorage'
            $result.Endpoints.Count | Should Be 3

            $blob = $result.Endpoints | Where-Object { $_.Service -eq 'blob' }
            $blob.Status | Should Be 'Pass'
            $blob.IsPrivateIp | Should Be $true
            $blob.TcpSuccess | Should Be $true
            $blob.SubnetMatch | Should Be $true
            $blob.ResolvedIps[0] | Should Be '10.80.2.10'

            $queue = $result.Endpoints | Where-Object { $_.Service -eq 'queue' }
            $queue.Status | Should Be 'Pass'
            $queue.IsPrivateIp | Should Be $true
            $queue.TcpSuccess | Should Be $true
            $queue.SubnetMatch | Should Be $true

            $table = $result.Endpoints | Where-Object { $_.Service -eq 'table' }
            $table.Status | Should Be 'Pass'
            $table.IsPrivateIp | Should Be $true
            $table.TcpSuccess | Should Be $true
            $table.SubnetMatch | Should Be $true
        }

        It "Passes with string prefix matching like '10.80.'" {
            $mockDns = {
                param($HostName)
                return @([System.Net.IPAddress]::Parse('10.80.2.10'))
            }
            $mockTcp = {
                param($HostName, $Port, $Timeout)
                return [pscustomobject]@{ Success = $true; ErrorMessage = $null }
            }

            $result = & $scriptPath -StorageAccountName 'pocstorage' `
                -ExpectedPrivateSubnetPrefix '10.80.' `
                -DnsResolver $mockDns `
                -TcpTester $mockTcp

            $result.AllPassed | Should Be $true
            $blob = $result.Endpoints | Where-Object { $_.Service -eq 'blob' }
            $blob.SubnetMatch | Should Be $true
        }

        It "Passes when ExpectedPrivateSubnetPrefix is omitted" {
            $mockDns = {
                param($HostName)
                return @([System.Net.IPAddress]::Parse('10.80.2.10'))
            }
            $mockTcp = {
                param($HostName, $Port, $Timeout)
                return [pscustomobject]@{ Success = $true; ErrorMessage = $null }
            }

            $result = & $scriptPath -StorageAccountName 'pocstorage' `
                -DnsResolver $mockDns `
                -TcpTester $mockTcp

            $result.AllPassed | Should Be $true
            $blob = $result.Endpoints | Where-Object { $_.Service -eq 'blob' }
            $blob.SubnetMatch | Should Be $null
        }

        It "Supports SkipAuthorizationCheck switch" {
            $mockDns = {
                param($HostName)
                return @([System.Net.IPAddress]::Parse('10.80.2.10'))
            }
            $mockTcp = {
                param($HostName, $Port, $Timeout)
                return [pscustomobject]@{ Success = $true; ErrorMessage = $null }
            }

            $result = & $scriptPath -StorageAccountName 'pocstorage' `
                -SkipAuthorizationCheck `
                -DnsResolver $mockDns `
                -TcpTester $mockTcp

            $result.AllPassed | Should Be $true
            $result.SkipAuthorizationCheck | Should Be $true
        }
    }

    Context "異常系 1: パブリック IP に解決された場合 (Public IP leakage)" {
        It "Fails and flags public IP when an endpoint resolves to a non-private IP" {
            $mockDns = {
                param($HostName)
                if ($HostName -match 'blob') {
                    return @([System.Net.IPAddress]::Parse('20.150.80.1'))
                }
                return @([System.Net.IPAddress]::Parse('10.80.2.11'))
            }
            $mockTcp = {
                param($HostName, $Port, $Timeout)
                return [pscustomobject]@{ Success = $true; ErrorMessage = $null }
            }

            $result = & $scriptPath -StorageAccountName 'pocstorage' `
                -DnsResolver $mockDns `
                -TcpTester $mockTcp

            $result.AllPassed | Should Be $false

            $blob = $result.Endpoints | Where-Object { $_.Service -eq 'blob' }
            $blob.Status | Should Be 'Failed'
            $blob.IsPrivateIp | Should Be $false
            $blob.ErrorMessage | Should Match 'public IP'
            $blob.TcpSuccess | Should Be $false # TCP handshake must not be attempted if public IP

            # Queue and Table should still pass
            $queue = $result.Endpoints | Where-Object { $_.Service -eq 'queue' }
            $queue.Status | Should Be 'Pass'
            $queue.IsPrivateIp | Should Be $true

            $table = $result.Endpoints | Where-Object { $_.Service -eq 'table' }
            $table.Status | Should Be 'Pass'
            $table.IsPrivateIp | Should Be $true
        }
    }

    Context "異常系 2: DNS 解決失敗 (DNS resolution error)" {
        It "Fails when DNS resolution throws an exception" {
            $mockDns = {
                param($HostName)
                throw [System.Net.Sockets.SocketException]::new(11001) # Host not found
            }
            $mockTcp = {
                param($HostName, $Port, $Timeout)
                return [pscustomobject]@{ Success = $true; ErrorMessage = $null }
            }

            $result = & $scriptPath -StorageAccountName 'pocstorage' `
                -DnsResolver $mockDns `
                -TcpTester $mockTcp

            $result.AllPassed | Should Be $false
            foreach ($ep in $result.Endpoints) {
                $ep.Status | Should Be 'Failed'
                $ep.IsPrivateIp | Should Be $false
                $ep.TcpSuccess | Should Be $false
                $ep.ErrorMessage | Should Match 'DNS resolution failed'
            }
        }

        It "Fails when DNS resolution returns empty list" {
            $mockDns = {
                param($HostName)
                return @()
            }
            $mockTcp = {
                param($HostName, $Port, $Timeout)
                return [pscustomobject]@{ Success = $true; ErrorMessage = $null }
            }

            $result = & $scriptPath -StorageAccountName 'pocstorage' `
                -DnsResolver $mockDns `
                -TcpTester $mockTcp

            $result.AllPassed | Should Be $false
            $blob = $result.Endpoints | Where-Object { $_.Service -eq 'blob' }
            $blob.Status | Should Be 'Failed'
            $blob.ErrorMessage | Should Match 'returned no IP addresses'
        }
    }

    Context "異常系 3: TCP 疎通失敗 (TCP port 443 handshake failure)" {
        It "Fails when TCP port 443 connection fails or times out" {
            $mockDns = {
                param($HostName)
                return @([System.Net.IPAddress]::Parse('10.80.2.10'))
            }
            $mockTcp = {
                param($HostName, $Port, $Timeout)
                return [pscustomobject]@{ Success = $false; ErrorMessage = "Connection refused by destination host" }
            }

            $result = & $scriptPath -StorageAccountName 'pocstorage' `
                -DnsResolver $mockDns `
                -TcpTester $mockTcp

            $result.AllPassed | Should Be $false
            $blob = $result.Endpoints | Where-Object { $_.Service -eq 'blob' }
            $blob.Status | Should Be 'Failed'
            $blob.IsPrivateIp | Should Be $true
            $blob.TcpSuccess | Should Be $false
            $blob.ErrorMessage | Should Match 'TCP port 443 connection failed'
        }
    }

    Context "異常系 4: ExpectedPrivateSubnetPrefix 不一致 (Subnet prefix mismatch)" {
        It "Fails when private IP does not match the expected subnet prefix" {
            # 172.16.5.10 is RFC 1918 private, but does not match 10.80.2.0/24
            $mockDns = {
                param($HostName)
                if ($HostName -match 'blob') {
                    return @([System.Net.IPAddress]::Parse('172.16.5.10'))
                }
                return @([System.Net.IPAddress]::Parse('10.80.2.11'))
            }
            $mockTcp = {
                param($HostName, $Port, $Timeout)
                return [pscustomobject]@{ Success = $true; ErrorMessage = $null }
            }

            $result = & $scriptPath -StorageAccountName 'pocstorage' `
                -ExpectedPrivateSubnetPrefix '10.80.2.0/24' `
                -DnsResolver $mockDns `
                -TcpTester $mockTcp

            $result.AllPassed | Should Be $false
            $blob = $result.Endpoints | Where-Object { $_.Service -eq 'blob' }
            $blob.Status | Should Be 'Failed'
            $blob.IsPrivateIp | Should Be $true
            $blob.SubnetMatch | Should Be $false
            $blob.ErrorMessage | Should Match 'does not match expected subnet prefix'
            $blob.TcpSuccess | Should Be $false # TCP should be skipped on subnet mismatch
        }
    }

    Context "セキュリティ・サニタイズ (Secret redaction)" {
        It "Redacts SAS tokens and query parameters from endpoint outputs" {
            $mockDns = {
                param($HostName)
                return @([System.Net.IPAddress]::Parse('10.80.2.10'))
            }
            $mockTcp = {
                param($HostName, $Port, $Timeout)
                return [pscustomobject]@{ Success = $true; ErrorMessage = $null }
            }

            $leakyBlobUri = "https://pocstorage.blob.core.windows.net/?sv=2021-06-08&ss=b&srt=sco&sp=rwdlac&sig=SUPER_SECRET_TOKEN"
            $result = & $scriptPath -StorageAccountName 'pocstorage' `
                -BlobEndpoint $leakyBlobUri `
                -DnsResolver $mockDns `
                -TcpTester $mockTcp

            $blob = $result.Endpoints | Where-Object { $_.Service -eq 'blob' }
            $blob.Endpoint | Should Not Match 'SUPER_SECRET_TOKEN'
            $blob.Endpoint | Should Not Match 'sig='
            $blob.Endpoint | Should Be 'https://pocstorage.blob.core.windows.net/'
            $blob.FQDN | Should Be 'pocstorage.blob.core.windows.net'
        }
    }

    Context "JSON 出力対応 (-AsJson)" {
        It "Outputs valid JSON matching the structured object when -AsJson is specified" {
            $mockDns = {
                param($HostName)
                return @([System.Net.IPAddress]::Parse('10.80.2.10'))
            }
            $mockTcp = {
                param($HostName, $Port, $Timeout)
                return [pscustomobject]@{ Success = $true; ErrorMessage = $null }
            }

            $jsonString = & $scriptPath -StorageAccountName 'pocstorage' `
                -AsJson `
                -DnsResolver $mockDns `
                -TcpTester $mockTcp

            $jsonString | Should BeOfType [string]

            $parsed = $jsonString | ConvertFrom-Json
            $parsed.AllPassed | Should Be $true
            $parsed.StorageAccountName | Should Be 'pocstorage'
            $parsed.Endpoints.Count | Should Be 3
        }
    }
}
