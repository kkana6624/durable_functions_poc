$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module Pester -ErrorAction SilentlyContinue

$scriptPath = Join-Path $PSScriptRoot '../Invoke-AppServiceValidation.ps1'

function New-DefaultFakeHttpHandler {
    param(
        [hashtable]$CustomOverrides = @{}
    )

    $handler = {
        param($Uri, $Method, $Headers, $Body)

        # Check if caller specified a custom override for this call
        if ($CustomOverrides.ContainsKey('Handler')) {
            return & $CustomOverrides['Handler'] $Uri $Method $Headers $Body
        }

        # Check for spoofing probe in Scenario 6
        if ($Headers -and $Headers.ContainsKey('X-Poc-Owner-Id') -and $Headers['X-Poc-Owner-Id'] -eq 'attacker-impersonator-999') {
            if ($CustomOverrides.ContainsKey('AllowSpoofing') -and $CustomOverrides['AllowSpoofing'] -eq $true) {
                return @{
                    StatusCode = 200
                    Headers    = @{ 'Content-Type' = 'application/json' }
                    Body       = '{"inputId":"spoofed-id","blobKey":"owners/attacker-impersonator-999/inputs/spoofed-id/file.bin","container":"workflow-input"}'
                }
            }
            return @{
                StatusCode = 403
                Headers    = @{ 'Content-Type' = 'application/json' }
                Body       = '{"error":"X-Poc-Owner-Id does not match authenticated identity."}'
            }
        }

        # Scenario 1: POST /api/uploads/initiate
        if ($Method -eq 'POST' -and $Uri -match '/api/uploads/initiate$') {
            if ($CustomOverrides.ContainsKey('InitiateStatus')) {
                return @{
                    StatusCode = [int]$CustomOverrides['InitiateStatus']
                    Headers    = @{ 'Content-Type' = 'application/json' }
                    Body       = '{"error":"Initiate failed"}'
                }
            }
            $owner = if ($Headers -and $Headers.ContainsKey('X-Poc-Owner-Id')) { $Headers['X-Poc-Owner-Id'] } else { 'app-owner-001' }
            return @{
                StatusCode = 200
                Headers    = @{ 'Content-Type' = 'application/json' }
                Body       = (@{
                    inputId   = "in-fake-001"
                    blobKey   = "owners/$owner/inputs/in-fake-001/validation-test.bin"
                    container = "workflow-input"
                } | ConvertTo-Json -Compress)
            }
        }

        # Scenario 2: POST /api/uploads/finalize
        if ($Method -eq 'POST' -and $Uri -match '/api/uploads/finalize$') {
            if ($CustomOverrides.ContainsKey('FinalizeStatus')) {
                return @{
                    StatusCode = [int]$CustomOverrides['FinalizeStatus']
                    Headers    = @{ 'Content-Type' = 'application/json' }
                    Body       = '{"error":"Finalize failed"}'
                }
            }
            $json = $Body | ConvertFrom-Json
            return @{
                StatusCode = 200
                Headers    = @{ 'Content-Type' = 'application/json' }
                Body       = (@{
                    readyManifestId = "ready-fake-001"
                    blobKey         = $json.blobKey
                    sizeBytes       = [long]$json.expectedSizeBytes
                    sha256          = [string]$json.expectedSha256
                } | ConvertTo-Json -Compress)
            }
        }

        # Scenario 3: POST /api/workflows
        if ($Method -eq 'POST' -and $Uri -match '/api/workflows$') {
            if ($CustomOverrides.ContainsKey('StartStatus')) {
                return @{
                    StatusCode = [int]$CustomOverrides['StartStatus']
                    Headers    = @{ 'Content-Type' = 'application/json' }
                    Body       = '{"error":"Start workflow conflict"}'
                }
            }
            $json = $Body | ConvertFrom-Json
            $reqId = $json.requestId
            $pollUrl = "/api/workflows/$reqId"
            return @{
                StatusCode = 202
                Headers    = @{
                    'Location'     = $pollUrl
                    'Retry-After'  = '10'
                    'Content-Type' = 'application/json'
                }
                Body       = (@{
                    requestId         = $reqId
                    instanceId        = "inst-$reqId"
                    statusQueryGetUri = $pollUrl
                    status            = "Accepted"
                } | ConvertTo-Json -Compress)
            }
        }

        # Scenario 4: GET /api/workflows/{requestId}
        if ($Method -eq 'GET' -and $Uri -match '/api/workflows/([^/?#]+)$') {
            $reqId = $Matches[1]
            if ($CustomOverrides.ContainsKey('StatusLeakSecret') -and $CustomOverrides['StatusLeakSecret'] -eq $true) {
                return @{
                    StatusCode = 200
                    Headers    = @{ 'Content-Type' = 'application/json' }
                    Body       = (@{
                        requestId            = $reqId
                        status               = "Running"
                        resultAvailable      = $false
                        sendEventPostUri     = "https://app.azurewebsites.net/runtime/webhooks/durabletask/instances/123?code=secretkey123"
                        purgeHistoryDeleteUri= "https://app.azurewebsites.net/runtime/webhooks/durabletask/instances/123"
                    } | ConvertTo-Json -Compress)
                }
            }
            if ($CustomOverrides.ContainsKey('StatusNeverCompletes') -and $CustomOverrides['StatusNeverCompletes'] -eq $true) {
                return @{
                    StatusCode = 200
                    Headers    = @{ 'Content-Type' = 'application/json' }
                    Body       = (@{
                        requestId       = $reqId
                        status          = "Running"
                        resultAvailable = $false
                        pollUrl         = "/api/workflows/$reqId"
                    } | ConvertTo-Json -Compress)
                }
            }
            if ($CustomOverrides.ContainsKey('PollSequence')) {
                $script:pollIndex++
                $seq = $CustomOverrides['PollSequence']
                $curr = if ($script:pollIndex -le $seq.Count) { $seq[$script:pollIndex - 1] } else { $seq[-1] }
                return @{
                    StatusCode = 200
                    Headers    = @{ 'Content-Type' = 'application/json' }
                    Body       = (@{
                        requestId       = $reqId
                        status          = $curr.status
                        resultAvailable = [bool]$curr.resultAvailable
                        pollUrl         = "/api/workflows/$reqId"
                    } | ConvertTo-Json -Compress)
                }
            }
            return @{
                StatusCode = 200
                Headers    = @{ 'Content-Type' = 'application/json' }
                Body       = (@{
                    requestId       = $reqId
                    status          = "Succeeded"
                    resultAvailable = $true
                    pollUrl         = "/api/workflows/$reqId"
                } | ConvertTo-Json -Compress)
            }
        }

        # Scenario 5: POST /api/workflows/{requestId}/cancel
        if ($Method -eq 'POST' -and $Uri -match '/api/workflows/([^/?#]+)/cancel$') {
            $reqId = $Matches[1]
            if ($CustomOverrides.ContainsKey('CancelStatus')) {
                return @{
                    StatusCode = [int]$CustomOverrides['CancelStatus']
                    Headers    = @{ 'Content-Type' = 'application/json' }
                    Body       = '{"error":"Cannot cancel"}'
                }
            }
            return @{
                StatusCode = 200
                Headers    = @{ 'Content-Type' = 'application/json' }
                Body       = (@{
                    requestId            = $reqId
                    status               = "Cancelled"
                    terminationConfirmed = $true
                } | ConvertTo-Json -Compress)
            }
        }

        return @{
            StatusCode = 404
            Headers    = @{}
            Body       = '{"error":"Not Found"}'
        }
    }.GetNewClosure()

    return $handler
}

Describe "Invoke-AppServiceValidation" {

    Context "正常系 (All 6 scenarios pass with Fake HTTP handler)" {
        It "Executes all scenarios successfully and returns structured Pass object" {
            $handler = New-DefaultFakeHttpHandler
            $result = & $scriptPath -AppServiceUrl "https://app-poc-test.azurewebsites.net" `
                -BearerToken "fake-valid-jwt-token" `
                -OwnerId "app-owner-001" `
                -OrgId "app-org-001" `
                -PollIntervalSeconds 0 `
                -TimeoutSeconds 10 `
                -HttpHandler $handler

            $result.ValidationKind | Should Be 'AppServiceValidation'
            $result.Status | Should Be 'Pass'
            $result.ExitCode | Should Be 0
            $result.AllScenariosPassed | Should Be $true
            $result.SecretLeakDetected | Should Be $false

            # Verify Scenario 1: InitiateUpload
            $scen1 = $result.Scenarios.InitiateUpload
            $scen1.Passed | Should Be $true
            $scen1.StatusCode | Should Be 200
            $scen1.InputId | Should Be "in-fake-001"
            $scen1.BlobKey | Should Be "owners/app-owner-001/inputs/in-fake-001/validation-test.bin"
            $scen1.Container | Should Be "workflow-input"

            # Verify Scenario 1.5: UploadBlob
            $scenUpload = $result.Scenarios.UploadBlob
            $scenUpload.Passed | Should Be $true
            $scenUpload.Container | Should Be "workflow-input"
            $scenUpload.BlobKey | Should Be "owners/app-owner-001/inputs/in-fake-001/validation-test.bin"
            $scenUpload.SizeBytes | Should Be 1024
            $scenUpload.Sha256 | Should Be "785b0751fc2c53dc14a4ce3d800e69ef9ce1009eb327ccf458afe09c242c26c9"
            $scenUpload.UploadMethod | Should Be "Simulated"

            # Verify Scenario 2: FinalizeUpload
            $scen2 = $result.Scenarios.FinalizeUpload
            $scen2.Passed | Should Be $true
            $scen2.StatusCode | Should Be 200
            $scen2.ReadyManifestId | Should Be "ready-fake-001"
            $scen2.SizeBytes | Should Be 1024
            $scen2.Sha256 | Should Be "785b0751fc2c53dc14a4ce3d800e69ef9ce1009eb327ccf458afe09c242c26c9"

            # Verify Scenario 3: StartWorkflow
            $scen3 = $result.Scenarios.StartWorkflow
            $scen3.Passed | Should Be $true
            $scen3.StatusCode | Should Be 202
            $scen3.PollUrl | Should Match "^/api/workflows/appval-"

            # Verify Scenario 4: GetWorkflowStatus
            $scen4 = $result.Scenarios.GetWorkflowStatus
            $scen4.Passed | Should Be $true
            $scen4.StatusCode | Should Be 200
            $scen4.FinalStatus | Should Be "Succeeded"
            $scen4.ResultAvailable | Should Be $true
            $scen4.SecretLeakDetected | Should Be $false

            # Verify Scenario 5: CancelWorkflow
            $scen5 = $result.Scenarios.CancelWorkflow
            $scen5.Passed | Should Be $true
            $scen5.StatusCode | Should Be 200
            $scen5.Status | Should Be "Cancelled"
            $scen5.TerminationConfirmed | Should Be $true

            # Verify Scenario 6: SecurityOwnerSpoofing
            $scen6 = $result.Scenarios.SecurityOwnerSpoofing
            $scen6.Passed | Should Be $true
            $scen6.StatusCode | Should Be 403
            $scen6.SpoofedOwnerRejected | Should Be $true
        }

        It "Supports multi-step polling before reaching terminal state" {
            $script:pollIndex = 0
            $pollSeq = @(
                @{ status = "Accepted"; resultAvailable = $false },
                @{ status = "Running"; resultAvailable = $false },
                @{ status = "Succeeded"; resultAvailable = $true }
            )
            $handler = New-DefaultFakeHttpHandler -CustomOverrides @{ PollSequence = $pollSeq }

            $result = & $scriptPath -AppServiceUrl "http://localhost:5000" `
                -PollIntervalSeconds 0 `
                -TimeoutSeconds 10 `
                -HttpHandler $handler

            $result.AllScenariosPassed | Should Be $true
            $result.Scenarios.GetWorkflowStatus.Passed | Should Be $true
            $result.Scenarios.GetWorkflowStatus.FinalStatus | Should Be "Succeeded"
            $result.Scenarios.GetWorkflowStatus.ResultAvailable | Should Be $true
        }
    }

    Context "セキュリティ: 秘密情報・Durable Webhook URL 漏洩の検知" {
        It "Detects runtime/webhooks Durable management URL leakage in status query and fails validation" {
            $handler = New-DefaultFakeHttpHandler -CustomOverrides @{ StatusLeakSecret = $true }

            $result = & $scriptPath -AppServiceUrl "https://app-poc-test.azurewebsites.net" `
                -PollIntervalSeconds 0 `
                -TimeoutSeconds 5 `
                -HttpHandler $handler

            $result.AllScenariosPassed | Should Be $false
            $result.Status | Should Be 'Fail'
            $result.SecretLeakDetected | Should Be $true
            $result.Scenarios.GetWorkflowStatus.Passed | Should Be $false
            $result.Scenarios.GetWorkflowStatus.SecretLeakDetected | Should Be $true
            $result.Scenarios.GetWorkflowStatus.Error | Should Match "CRITICAL SECURITY LEAK"
        }

        It "Detects SAS token sig= parameter leakage in response" {
            $handler = {
                param($Uri, $Method, $Headers, $Body)
                if ($Uri -match '/api/workflows/([^/?#]+)$') {
                    return @{
                        StatusCode = 200
                        Headers    = @{ 'Content-Type' = 'application/json' }
                        Body       = '{"requestId":"req-1","status":"Running","resultAvailable":false,"blobUri":"https://storage.blob.core.windows.net/container/file?sig=supersecrettoken123"}'
                    }
                }
                $defaultHandler = New-DefaultFakeHttpHandler
                return & $defaultHandler $Uri $Method $Headers $Body
            }

            $result = & $scriptPath -AppServiceUrl "https://app-poc-test.azurewebsites.net" `
                -PollIntervalSeconds 0 `
                -TimeoutSeconds 5 `
                -HttpHandler $handler

            $result.SecretLeakDetected | Should Be $true
            $result.AllScenariosPassed | Should Be $false
        }

        It "Redacts BearerToken and keys from structured output and errors" {
            $secretToken = "HIGHLY-CONFIDENTIAL-BEARER-TOKEN-XYZ987"
            $handler = {
                param($Uri, $Method, $Headers, $Body)
                if ($Uri -match '/api/uploads/initiate') {
                    # Return error string echoing token or key
                    return @{
                        StatusCode = 500
                        Headers    = @{ 'Content-Type' = 'application/json' }
                        Body       = "Internal error with Bearer $secretToken"
                    }
                }
                $defaultHandler = New-DefaultFakeHttpHandler
                return & $defaultHandler $Uri $Method $Headers $Body
            }

            $result = & $scriptPath -AppServiceUrl "https://app-poc-test.azurewebsites.net" `
                -BearerToken $secretToken `
                -PollIntervalSeconds 0 `
                -TimeoutSeconds 5 `
                -HttpHandler $handler

            $jsonOutput = $result | ConvertTo-Json -Depth 6
            $jsonOutput | Should Not Match $secretToken
            $jsonOutput | Should Match "\[REDACTED"
        }
    }

    Context "セキュリティ: 所有者詐称 (X-Poc-Owner-Id spoofing) 検知" {
        It "Fails Scenario 6 when spoofed X-Poc-Owner-Id is not rejected with 403 Forbidden" {
            # Handler improperly accepts the spoofed owner
            $handler = New-DefaultFakeHttpHandler -CustomOverrides @{ AllowSpoofing = $true }

            $result = & $scriptPath -AppServiceUrl "https://app-poc-test.azurewebsites.net" `
                -PollIntervalSeconds 0 `
                -TimeoutSeconds 5 `
                -HttpHandler $handler

            $result.AllScenariosPassed | Should Be $false
            $result.Status | Should Be 'Fail'
            $result.Scenarios.SecurityOwnerSpoofing.Passed | Should Be $false
            $result.Scenarios.SecurityOwnerSpoofing.SpoofedOwnerRejected | Should Be $false
            $result.Scenarios.SecurityOwnerSpoofing.Error | Should Match "Expected 403 Forbidden"
        }
    }

    Context "異常系・タイムアウト・エラーハンドリング" {
        It "Fails with timeout error when workflow status polling never reaches terminal state" {
            $handler = New-DefaultFakeHttpHandler -CustomOverrides @{ StatusNeverCompletes = $true }

            $result = & $scriptPath -AppServiceUrl "https://app-poc-test.azurewebsites.net" `
                -PollIntervalSeconds 0 `
                -TimeoutSeconds 1 `
                -HttpHandler $handler

            $result.AllScenariosPassed | Should Be $false
            $result.Scenarios.GetWorkflowStatus.Passed | Should Be $false
            $result.Scenarios.GetWorkflowStatus.Error | Should Match "Timeout after 1s polling"
        }

        It "Fails and cascades skip when Scenario 1 InitiateUpload fails" {
            $handler = New-DefaultFakeHttpHandler -CustomOverrides @{ InitiateStatus = 500 }

            $result = & $scriptPath -AppServiceUrl "https://app-poc-test.azurewebsites.net" `
                -PollIntervalSeconds 0 `
                -TimeoutSeconds 5 `
                -HttpHandler $handler

            $result.AllScenariosPassed | Should Be $false
            $result.Scenarios.InitiateUpload.Passed | Should Be $false
            $result.Scenarios.InitiateUpload.StatusCode | Should Be 500
            $result.Scenarios.UploadBlob.Passed | Should Be $false
            $result.Scenarios.UploadBlob.Error | Should Match "Skipped because Scenario 1"
            $result.Scenarios.FinalizeUpload.Passed | Should Be $false
            $result.Scenarios.FinalizeUpload.Error | Should Match "Skipped because Scenario 1"
        }

        It "Fails when Scenario 3 StartWorkflow returns 409 Conflict" {
            $handler = New-DefaultFakeHttpHandler -CustomOverrides @{ StartStatus = 409 }

            $result = & $scriptPath -AppServiceUrl "https://app-poc-test.azurewebsites.net" `
                -PollIntervalSeconds 0 `
                -TimeoutSeconds 5 `
                -HttpHandler $handler

            $result.AllScenariosPassed | Should Be $false
            $result.Scenarios.StartWorkflow.Passed | Should Be $false
            $result.Scenarios.StartWorkflow.StatusCode | Should Be 409
        }

        It "Fails when Scenario 5 CancelWorkflow returns 500" {
            $handler = New-DefaultFakeHttpHandler -CustomOverrides @{ CancelStatus = 500 }

            $result = & $scriptPath -AppServiceUrl "https://app-poc-test.azurewebsites.net" `
                -PollIntervalSeconds 0 `
                -TimeoutSeconds 5 `
                -HttpHandler $handler

            $result.AllScenariosPassed | Should Be $false
            $result.Scenarios.CancelWorkflow.Passed | Should Be $false
            $result.Scenarios.CancelWorkflow.StatusCode | Should Be 500
        }
    }

    Context "入力パラメータ検証" {
        It "Throws exception for invalid AppServiceUrl" {
            { & $scriptPath -AppServiceUrl "invalid-not-url" } | Should Throw
            { & $scriptPath -AppServiceUrl "ftp://not-http.example.com" } | Should Throw
            { & $scriptPath -AppServiceUrl "https://example.com/api?query=1" } | Should Throw
        }

        It "Throws exception for invalid OwnerId" {
            { & $scriptPath -AppServiceUrl "https://example.com" -OwnerId "   " } | Should Throw
            { & $scriptPath -AppServiceUrl "https://example.com" -OwnerId "`n" } | Should Throw
        }
    }

    Context "Blob アップロードおよび Finalize 整合性検証 (Scenario 1.5 & Scenario 2)" {
        It "Uploads through the native CLI with a temporary payload path containing spaces" {
            $probeRoot = Join-Path ([IO.Path]::GetTempPath()) ('poc-upload-' + [Guid]::NewGuid().ToString('N'))
            $probeDir = Join-Path $probeRoot 'temporary files with spaces'
            $null = New-Item -ItemType Directory -Path $probeDir -Force
            $oldTemp = $env:TEMP
            $oldTmp = $env:TMP
            $oldPath = $env:Path
            try {
                $fakeAz = Join-Path $probeDir 'az.cmd'
                @'
@echo off
setlocal
:next
if "%~1"=="" exit /b 71
if "%~1"=="--file" goto payload
shift
goto next
:payload
shift
if not exist "%~1" exit /b 72
for %%F in ("%~1") do if not "%%~zF"=="1024" exit /b 73
if "%~2"=="--overwrite" exit /b 0
exit /b 74
'@ | Set-Content -LiteralPath $fakeAz -Encoding ASCII
                $env:TEMP = $probeDir
                $env:TMP = $probeDir
                $env:Path = $probeDir + [IO.Path]::PathSeparator + $oldPath
                $result = & $scriptPath -AppServiceUrl 'https://example.com' -StorageAccountName 'teststorage' -HttpHandler (New-DefaultFakeHttpHandler) -PollIntervalSeconds 0 -TimeoutSeconds 10
                $result.Scenarios.UploadBlob.UploadMethod | Should Be 'AzCli'
                $result.Scenarios.UploadBlob.Passed | Should Be $true
                $result.AllScenariosPassed | Should Be $true
                @(Get-ChildItem -LiteralPath $probeDir -Filter '*.tmp').Count | Should Be 0
            } finally {
                $env:TEMP = $oldTemp
                $env:TMP = $oldTmp
                $env:Path = $oldPath
                $resolved = [IO.Path]::GetFullPath($probeRoot)
                $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
                if ($resolved.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -like 'poc-upload-*') {
                    Remove-Item -LiteralPath $resolved -Recurse -Force
                }
            }
        }

        It "Invokes custom BlobUploader with correct parameters (Container, BlobKey, 1024-byte payload, accurate SHA-256)" {
            $recorded = @{}
            $uploader = {
                param($Container, $BlobKey, $Data, $SizeBytes, $Sha256)
                $recorded['Container'] = $Container
                $recorded['BlobKey']   = $BlobKey
                $recorded['DataLen']   = $Data.Length
                $recorded['SizeBytes'] = $SizeBytes
                $recorded['Sha256']    = $Sha256
                return @{ Passed = $true }
            }

            $handler = New-DefaultFakeHttpHandler
            $result = & $scriptPath -AppServiceUrl "https://app-poc-test.azurewebsites.net" `
                -PollIntervalSeconds 0 `
                -TimeoutSeconds 10 `
                -HttpHandler $handler `
                -BlobUploader $uploader

            $result.AllScenariosPassed | Should Be $true
            $recorded['Container'] | Should Be "workflow-input"
            $recorded['BlobKey'] | Should Be "owners/app-owner-001/inputs/in-fake-001/validation-test.bin"
            $recorded['DataLen'] | Should Be 1024
            $recorded['SizeBytes'] | Should Be 1024
            $recorded['Sha256'] | Should Be "785b0751fc2c53dc14a4ce3d800e69ef9ce1009eb327ccf458afe09c242c26c9"

            $result.Scenarios.UploadBlob.Passed | Should Be $true
            $result.Scenarios.UploadBlob.UploadMethod | Should Be "BlobUploader"
            $result.Scenarios.UploadBlob.SizeBytes | Should Be 1024
            $result.Scenarios.UploadBlob.Sha256 | Should Be "785b0751fc2c53dc14a4ce3d800e69ef9ce1009eb327ccf458afe09c242c26c9"
        }

        It "Ensures Finalize receives accurate expectedSizeBytes and expectedSha256 matching uploaded blob" {
            $finalizeReceived = @{}
            $handler = {
                param($Uri, $Method, $Headers, $Body)
                if ($Method -eq 'POST' -and $Uri -match '/api/uploads/finalize$') {
                    $json = $Body | ConvertFrom-Json
                    $finalizeReceived['expectedSizeBytes'] = $json.expectedSizeBytes
                    $finalizeReceived['expectedSha256']    = $json.expectedSha256
                    $finalizeReceived['blobKey']           = $json.blobKey
                    $finalizeReceived['container']         = $json.container
                }
                $defaultHandler = New-DefaultFakeHttpHandler
                return & $defaultHandler $Uri $Method $Headers $Body
            }

            $result = & $scriptPath -AppServiceUrl "https://app-poc-test.azurewebsites.net" `
                -PollIntervalSeconds 0 `
                -TimeoutSeconds 10 `
                -HttpHandler $handler

            $result.AllScenariosPassed | Should Be $true
            $finalizeReceived['expectedSizeBytes'] | Should Be 1024
            $finalizeReceived['expectedSha256'] | Should Be "785b0751fc2c53dc14a4ce3d800e69ef9ce1009eb327ccf458afe09c242c26c9"
            $finalizeReceived['container'] | Should Be "workflow-input"
            $finalizeReceived['blobKey'] | Should Be "owners/app-owner-001/inputs/in-fake-001/validation-test.bin"
        }

        It "Fails validation and skips FinalizeUpload when BlobUploader returns failure" {
            $uploader = {
                param($Container, $BlobKey, $Data, $SizeBytes, $Sha256)
                return @{ Passed = $false; Error = "Disk quota exceeded in blob storage" }
            }

            $handler = New-DefaultFakeHttpHandler
            $result = & $scriptPath -AppServiceUrl "https://app-poc-test.azurewebsites.net" `
                -PollIntervalSeconds 0 `
                -TimeoutSeconds 10 `
                -HttpHandler $handler `
                -BlobUploader $uploader

            $result.AllScenariosPassed | Should Be $false
            $result.Scenarios.UploadBlob.Passed | Should Be $false
            $result.Scenarios.UploadBlob.Error | Should Match "Disk quota exceeded"
            $result.Scenarios.FinalizeUpload.Passed | Should Be $false
            $result.Scenarios.FinalizeUpload.Error | Should Match "Skipped because UploadBlob failed"
        }

        It "Fails validation and skips FinalizeUpload when BlobUploader throws exception" {
            $uploader = {
                param($Container, $BlobKey, $Data, $SizeBytes, $Sha256)
                throw "Network socket reset during blob transfer"
            }

            $handler = New-DefaultFakeHttpHandler
            $result = & $scriptPath -AppServiceUrl "https://app-poc-test.azurewebsites.net" `
                -PollIntervalSeconds 0 `
                -TimeoutSeconds 10 `
                -HttpHandler $handler `
                -BlobUploader $uploader

            $result.AllScenariosPassed | Should Be $false
            $result.Scenarios.UploadBlob.Passed | Should Be $false
            $result.Scenarios.UploadBlob.Error | Should Match "BlobUploader threw exception"
            $result.Scenarios.FinalizeUpload.Passed | Should Be $false
            $result.Scenarios.FinalizeUpload.Error | Should Match "Skipped because UploadBlob failed"
        }
    }

    Context "出力形式 (-AsJson)" {
        It "Returns valid JSON string when -AsJson is specified" {
            $handler = New-DefaultFakeHttpHandler
            $json = & $scriptPath -AppServiceUrl "https://app-poc-test.azurewebsites.net" `
                -PollIntervalSeconds 0 `
                -TimeoutSeconds 10 `
                -AsJson `
                -HttpHandler $handler

            $json | Should BeOfType [string]
            $parsed = $json | ConvertFrom-Json
            $parsed.ValidationKind | Should Be 'AppServiceValidation'
            $parsed.Status | Should Be 'Pass'
            $parsed.AllScenariosPassed | Should Be $true
            $parsed.ExitCode | Should Be 0
            $parsed.Scenarios.InitiateUpload.Passed | Should Be $true
            $parsed.Scenarios.UploadBlob.Passed | Should Be $true
            $parsed.Scenarios.FinalizeUpload.Passed | Should Be $true
        }
    }
}
