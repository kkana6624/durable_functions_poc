<#
.SYNOPSIS
    Performs end-to-end integration and security validation for the Azure App Service upstream API.

.DESCRIPTION
    Validates S2S authentication, caller identity resolving, upload initiation/finalization,
    workflow admission and relative polling URL proxying, Durable secret/webhook non-leakage,
    cancellation forwarding, and anti-impersonation spoofing rejection (403 Forbidden).
    Strictly complies with AGENTS.md: secrets, Function keys, SAS tokens, and Durable
    management URLs are never logged or returned in outputs.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$AppServiceUrl,

    [Parameter(Mandatory = $false)]
    [string]$BearerToken,

    [Parameter(Mandatory = $false)]
    [string]$OwnerId = 'app-owner-001',

    [Parameter(Mandatory = $false)]
    [string]$OrgId = 'app-org-001',

    [Parameter(Mandatory = $false)]
    [ValidateRange(0, 60)]
    [int]$PollIntervalSeconds = 2,

    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 3600)]
    [int]$TimeoutSeconds = 120,

    [Parameter(Mandatory = $false)]
    [switch]$AsJson,

    [Parameter(Mandatory = $false)]
    [scriptblock]$HttpHandler
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SafeProperty {
    param($Object, [string]$PropertyName, $Default = $null)
    if ($null -eq $Object) { return $Default }
    if ($Object -is [hashtable]) {
        if ($Object.ContainsKey($PropertyName)) { return $Object[$PropertyName] }
        return $Default
    }
    if ($Object.PSObject.Properties[$PropertyName]) {
        return $Object.$PropertyName
    }
    return $Default
}

function Protect-AppServiceSecret {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $Text }
    $clean = $Text
    if (-not [string]::IsNullOrWhiteSpace($BearerToken)) {
        $clean = $clean.Replace($BearerToken, '[REDACTED_BEARER_TOKEN]')
    }
    $clean = [regex]::Replace($clean, 'Bearer\s+[A-Za-z0-9\-._~+/]+=*', 'Bearer [REDACTED_TOKEN]')
    $clean = [regex]::Replace($clean, 'sig=[^&"''\s]+', 'sig=[REDACTED_SIG]')
    $clean = [regex]::Replace($clean, 'code=[^&"''\s]+', 'code=[REDACTED_KEY]')
    $clean = [regex]::Replace($clean, 'x-functions-key:\s*[^\r\n]+', 'x-functions-key: [REDACTED_KEY]')
    return $clean
}

function Test-AppServiceSecretLeak {
    param(
        [string]$Content,
        [hashtable]$Headers = $null
    )
    $leakPatterns = @(
        'runtime/webhooks',
        'code=',
        'x-functions-key',
        'sig=',
        'purgeHistoryDeleteUri',
        'sendEventPostUri',
        'terminatePostUri',
        'rewindPostUri'
    )
    foreach ($pat in $leakPatterns) {
        if ($Content -and $Content.IndexOf($pat, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }
    if ($Headers) {
        foreach ($k in $Headers.Keys) {
            $v = [string]$Headers[$k]
            foreach ($pat in $leakPatterns) {
                if ($k.IndexOf($pat, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                    $v.IndexOf($pat, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    return $true
                }
            }
        }
    }
    return $false
}

function Invoke-AppServiceHttpCall {
    param(
        [string]$Uri,
        [string]$Method,
        [hashtable]$Headers,
        [string]$Body = $null,
        [scriptblock]$Handler = $null
    )

    if ($Handler) {
        $raw = & $Handler $Uri $Method $Headers $Body

        $statusCodeRaw = Get-SafeProperty $raw 'StatusCode'
        if ($null -eq $statusCodeRaw) { $statusCodeRaw = Get-SafeProperty $raw 'Code' }
        $statusCode = if ($null -ne $statusCodeRaw) { [int]$statusCodeRaw } else { 200 }

        $respHeadersRaw = Get-SafeProperty $raw 'Headers'
        $respHeaders = if ($respHeadersRaw) { $respHeadersRaw } else { @{} }

        $bodyRaw = Get-SafeProperty $raw 'Body'
        if ($null -eq $bodyRaw) { $bodyRaw = Get-SafeProperty $raw 'Content' }
        $dataRaw = Get-SafeProperty $raw 'Data'

        $bodyStr = ''
        if ($null -ne $bodyRaw) {
            $bodyStr = [string]$bodyRaw
        } elseif ($null -ne $dataRaw) {
            if ($dataRaw -is [string]) { $bodyStr = $dataRaw }
            else { $bodyStr = $dataRaw | ConvertTo-Json -Depth 6 -Compress }
        }

        $data = $null
        if ($null -ne $dataRaw -and $dataRaw -isnot [string]) {
            $data = $dataRaw
        } elseif (-not [string]::IsNullOrWhiteSpace($bodyStr)) {
            try { $data = $bodyStr | ConvertFrom-Json } catch { $data = $null }
        }

        return @{
            StatusCode = $statusCode
            Headers    = $respHeaders
            Body       = $bodyStr
            Data       = $data
        }
    }

    # Real HTTP execution using HttpClient
    Add-Type -AssemblyName System.Net.Http
    $clientHandler = [System.Net.Http.HttpClientHandler]::new()
    $clientHandler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($clientHandler)
    $client.Timeout = [TimeSpan]::FromSeconds(30)
    $httpRequest = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Uri)
    $httpResponse = $null
    try {
        if ($Headers) {
            foreach ($k in $Headers.Keys) {
                $null = $httpRequest.Headers.TryAddWithoutValidation($k, [string]$Headers[$k])
            }
        }
        if ($Body) {
            $httpRequest.Content = [System.Net.Http.StringContent]::new($Body, [System.Text.Encoding]::UTF8, 'application/json')
        }
        $httpResponse = $client.SendAsync($httpRequest).GetAwaiter().GetResult()
        $statusCode = [int]$httpResponse.StatusCode
        $respHeaders = @{}
        foreach ($h in $httpResponse.Headers) {
            $respHeaders[$h.Key] = ($h.Value -join ', ')
        }
        if ($httpResponse.Content -and $httpResponse.Content.Headers) {
            foreach ($h in $httpResponse.Content.Headers) {
                $respHeaders[$h.Key] = ($h.Value -join ', ')
            }
        }
        $bodyStr = $httpResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $data = $null
        if (-not [string]::IsNullOrWhiteSpace($bodyStr)) {
            try { $data = $bodyStr | ConvertFrom-Json } catch { $data = $null }
        }
        return @{
            StatusCode = $statusCode
            Headers    = $respHeaders
            Body       = $bodyStr
            Data       = $data
        }
    }
    finally {
        if ($httpResponse) { $httpResponse.Dispose() }
        $httpRequest.Dispose()
        $client.Dispose()
        $clientHandler.Dispose()
    }
}

function Invoke-AppServiceValidationMain {
    [CmdletBinding()]
    param(
        [string]$AppServiceUrl,
        [string]$BearerToken,
        [string]$OwnerId,
        [string]$OrgId,
        [int]$PollIntervalSeconds,
        [int]$TimeoutSeconds,
        [switch]$AsJson,
        [scriptblock]$HttpHandler
    )

    # 1. URL & Argument Validation
    $appUri = $null
    if (-not [Uri]::TryCreate($AppServiceUrl, [UriKind]::Absolute, [ref]$appUri) -or ($appUri.Scheme -cne 'http' -and $appUri.Scheme -cne 'https')) {
        throw "Invalid AppServiceUrl '$AppServiceUrl'. Must be a valid HTTP or HTTPS absolute URL."
    }
    if ($appUri.Query -or $appUri.Fragment) {
        throw "AppServiceUrl must not contain query parameters or fragments."
    }
    $baseUrl = $AppServiceUrl.Trim().TrimEnd('/')

    if ([string]::IsNullOrWhiteSpace($OwnerId) -or $OwnerId -match '[\r\n\t]') {
        throw "Invalid OwnerId '$OwnerId'."
    }
    if ($OrgId -and $OrgId -match '[\r\n\t]') {
        throw "Invalid OrgId '$OrgId'."
    }

    $secretLeakDetected = $false

    # Helper for building standard headers
    $buildHeaders = {
        param([string]$CustomOwner = $null, [string]$CustomOrg = $null)
        $h = @{}
        $useOwner = if ($null -ne $CustomOwner) { $CustomOwner } else { $OwnerId }
        $useOrg   = if ($null -ne $CustomOrg) { $CustomOrg } else { $OrgId }

        if (-not [string]::IsNullOrWhiteSpace($useOwner)) { $h['X-Poc-Owner-Id'] = $useOwner }
        if (-not [string]::IsNullOrWhiteSpace($useOrg)) { $h['X-Poc-Org-Id'] = $useOrg }
        if (-not [string]::IsNullOrWhiteSpace($BearerToken)) { $h['Authorization'] = "Bearer $BearerToken" }
        return $h
    }

    $defaultHeaders = & $buildHeaders

    # --- Scenario 1: POST /api/uploads/initiate ---
    $initiatePayload = @{
        filename          = "validation-test.bin"
        expectedSizeBytes = 1048576
        contentType       = "application/octet-stream"
    } | ConvertTo-Json -Compress

    $scen1Res = Invoke-AppServiceHttpCall -Uri "$baseUrl/api/uploads/initiate" -Method 'POST' -Headers $defaultHeaders -Body $initiatePayload -Handler $HttpHandler

    $scen1Passed = $false
    $scen1Error = $null

    $scen1StatusCode = [int]$scen1Res['StatusCode']
    $scen1Body = [string]$scen1Res['Body']
    $scen1Headers = $scen1Res['Headers']
    $scen1Data = $scen1Res['Data']

    $scen1InputId = Get-SafeProperty $scen1Data 'inputId'
    $scen1BlobKey = Get-SafeProperty $scen1Data 'blobKey'
    $scen1Container = Get-SafeProperty $scen1Data 'container'

    if (Test-AppServiceSecretLeak -Content $scen1Body -Headers $scen1Headers) {
        $secretLeakDetected = $true
        $scen1Error = "Secret or internal URL leaked in InitiateUpload response."
    } elseif ($scen1StatusCode -ne 200) {
        $scen1Error = "InitiateUpload returned HTTP $scen1StatusCode instead of 200: $scen1Body"
    } elseif (-not $scen1InputId -or -not $scen1BlobKey -or -not $scen1Container) {
        $scen1Error = "InitiateUpload response missing required fields (inputId, blobKey, container)."
    } elseif (-not ($scen1BlobKey -like "owners/$OwnerId/inputs/*" -and $scen1BlobKey.EndsWith("validation-test.bin"))) {
        $scen1Error = "InitiateUpload blobKey '$scen1BlobKey' does not match expected prefix 'owners/$OwnerId/inputs/' or filename."
    } else {
        $scen1Passed = $true
    }

    $scenario1Result = [pscustomobject]@{
        Scenario   = 'InitiateUpload'
        Passed     = $scen1Passed
        StatusCode = $scen1StatusCode
        InputId    = $scen1InputId
        BlobKey    = $scen1BlobKey
        Container  = $scen1Container
        Error      = Protect-AppServiceSecret $scen1Error
    }

    # --- Scenario 2: POST /api/uploads/finalize ---
    $scen2Passed = $false
    $scen2Error = $null
    $scenario2Result = $null

    if (-not $scen1Passed) {
        $scen2Error = "Skipped because Scenario 1 (InitiateUpload) failed."
        $scenario2Result = [pscustomobject]@{
            Scenario        = 'FinalizeUpload'
            Passed          = $false
            StatusCode      = $null
            ReadyManifestId = $null
            SizeBytes       = $null
            Sha256          = $null
            Error           = $scen2Error
        }
    } else {
        $expectedSha256 = 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'
        $finalizePayload = @{
            inputId           = $scen1InputId
            blobKey           = $scen1BlobKey
            expectedSizeBytes = 1048576
            expectedSha256    = $expectedSha256
            container         = $scen1Container
        } | ConvertTo-Json -Compress

        $scen2Res = Invoke-AppServiceHttpCall -Uri "$baseUrl/api/uploads/finalize" -Method 'POST' -Headers $defaultHeaders -Body $finalizePayload -Handler $HttpHandler

        $scen2StatusCode = [int]$scen2Res['StatusCode']
        $scen2Body = [string]$scen2Res['Body']
        $scen2Headers = $scen2Res['Headers']
        $scen2Data = $scen2Res['Data']

        $scen2ReadyManifestId = Get-SafeProperty $scen2Data 'readyManifestId'
        $scen2SizeBytes = Get-SafeProperty $scen2Data 'sizeBytes'
        $scen2Sha256 = Get-SafeProperty $scen2Data 'sha256'

        if (Test-AppServiceSecretLeak -Content $scen2Body -Headers $scen2Headers) {
            $secretLeakDetected = $true
            $scen2Error = "Secret or internal URL leaked in FinalizeUpload response."
        } elseif ($scen2StatusCode -ne 200) {
            $scen2Error = "FinalizeUpload returned HTTP $scen2StatusCode instead of 200: $scen2Body"
        } elseif (-not $scen2ReadyManifestId) {
            $scen2Error = "FinalizeUpload response missing readyManifestId."
        } elseif ($scen2SizeBytes -ne 1048576) {
            $scen2Error = "FinalizeUpload sizeBytes ($scen2SizeBytes) does not match expected (1048576)."
        } elseif (-not [string]::Equals([string]$scen2Sha256, $expectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
            $scen2Error = "FinalizeUpload sha256 ($scen2Sha256) does not match expected ($expectedSha256)."
        } else {
            $scen2Passed = $true
        }

        $scenario2Result = [pscustomobject]@{
            Scenario        = 'FinalizeUpload'
            Passed          = $scen2Passed
            StatusCode      = $scen2StatusCode
            ReadyManifestId = $scen2ReadyManifestId
            SizeBytes       = $scen2SizeBytes
            Sha256          = $scen2Sha256
            Error           = Protect-AppServiceSecret $scen2Error
        }
    }

    # --- Scenario 3: POST /api/workflows ---
    $workflowRequestId = "appval-" + [Guid]::NewGuid().ToString("N")
    $scen2Manifest = if ($scenario2Result) { Get-SafeProperty $scenario2Result 'ReadyManifestId' } else { $null }
    $readyManifestId = if ($scen2Manifest) { $scen2Manifest } else { "ready-dummy-001" }

    $startPayload = @{
        requestId       = $workflowRequestId
        readyManifestId = $readyManifestId
        classification  = "light"
        taskCount       = 1
    } | ConvertTo-Json -Compress

    $scen3Res = Invoke-AppServiceHttpCall -Uri "$baseUrl/api/workflows" -Method 'POST' -Headers $defaultHeaders -Body $startPayload -Handler $HttpHandler

    $scen3StatusCode = [int]$scen3Res['StatusCode']
    $scen3Body = [string]$scen3Res['Body']
    $scen3Headers = $scen3Res['Headers']
    $scen3Data = $scen3Res['Data']

    $scen3Passed = $false
    $scen3Error = $null
    $pollUrl = $null

    if (Test-AppServiceSecretLeak -Content $scen3Body -Headers $scen3Headers) {
        $secretLeakDetected = $true
        $scen3Error = "CRITICAL: Secret, key, SAS, or Durable webhook URL leaked in StartWorkflow response."
    } elseif ($scen3StatusCode -ne 202) {
        $scen3Error = "StartWorkflow returned HTTP $scen3StatusCode instead of 202 Accepted: $scen3Body"
    } else {
        $locHeader = Get-SafeProperty $scen3Headers 'Location'
        if (-not $locHeader) { $locHeader = Get-SafeProperty $scen3Headers 'location' }

        $bodyPollUrl = Get-SafeProperty $scen3Data 'statusQueryGetUri'
        $pollUrl = if ($bodyPollUrl) { $bodyPollUrl } else { $locHeader }

        if (-not $pollUrl) {
            $scen3Error = "StartWorkflow response missing statusQueryGetUri or Location header."
        } elseif (Test-AppServiceSecretLeak -Content $pollUrl) {
            $secretLeakDetected = $true
            $scen3Error = "CRITICAL: Poll URL leaked internal keys, SAS, or Durable management webhook."
        } elseif ($pollUrl -notmatch "(/api/workflows/|$workflowRequestId)") {
            $scen3Error = "StartWorkflow pollUrl '$pollUrl' does not point to /api/workflows/$workflowRequestId."
        } else {
            $scen3Passed = $true
        }
    }

    $scenario3Result = [pscustomobject]@{
        Scenario   = 'StartWorkflow'
        Passed     = $scen3Passed
        StatusCode = $scen3StatusCode
        RequestId  = $workflowRequestId
        PollUrl    = $pollUrl
        Error      = Protect-AppServiceSecret $scen3Error
    }

    # --- Scenario 4: GET /api/workflows/{requestId} (Polling & Secret Redaction) ---
    $scen4Passed = $false
    $scen4Error = $null
    $finalStatus = $null
    $resultAvailable = $false
    $pollElapsed = 0
    $scen4StatusCode = $null

    $pollTarget = if ($pollUrl -and ($pollUrl.StartsWith('http://', [StringComparison]::OrdinalIgnoreCase) -or $pollUrl.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase))) {
        $pollUrl
    } elseif ($pollUrl) {
        "$baseUrl/$($pollUrl.TrimStart('/'))"
    } else {
        "$baseUrl/api/workflows/$workflowRequestId"
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $timeoutMs = $TimeoutSeconds * 1000
    $terminalStatuses = @('Succeeded', 'Failed', 'Terminated', 'Cancelled')

    while ($true) {
        $scen4Res = Invoke-AppServiceHttpCall -Uri $pollTarget -Method 'GET' -Headers $defaultHeaders -Handler $HttpHandler

        $scen4StatusCode = [int]$scen4Res['StatusCode']
        $scen4Body = [string]$scen4Res['Body']
        $scen4Headers = $scen4Res['Headers']
        $scen4Data = $scen4Res['Data']

        if (Test-AppServiceSecretLeak -Content $scen4Body -Headers $scen4Headers) {
            $secretLeakDetected = $true
            $scen4Error = "CRITICAL SECURITY LEAK: Durable management URL, Function Key, or SAS token found in status response!"
            break
        }

        if ($scen4StatusCode -ne 200) {
            $scen4Error = "GetWorkflowStatus returned HTTP ${scen4StatusCode}: $scen4Body"
            break
        }

        if (-not $scen4Data) {
            $scen4Error = "GetWorkflowStatus returned empty or invalid JSON."
            break
        }

        $currentStatus = [string](Get-SafeProperty $scen4Data 'status')
        $finalStatus = $currentStatus
        $resAvailRaw = Get-SafeProperty $scen4Data 'resultAvailable'
        $resultAvailable = if ($null -ne $resAvailRaw) { [bool]$resAvailRaw } else { $false }

        if ($currentStatus -in $terminalStatuses -or $resultAvailable) {
            $scen4Passed = $true
            break
        }

        if ($stopwatch.ElapsedMilliseconds -ge $timeoutMs) {
            $scen4Error = "Timeout after $($TimeoutSeconds)s polling workflow status (last status: '$currentStatus')."
            break
        }

        if ($PollIntervalSeconds -gt 0) {
            Start-Sleep -Seconds $PollIntervalSeconds
        }
    }
    $stopwatch.Stop()
    $pollElapsed = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 2)

    $scenario4Result = [pscustomobject]@{
        Scenario           = 'GetWorkflowStatus'
        Passed             = $scen4Passed
        StatusCode         = $scen4StatusCode
        RequestId          = $workflowRequestId
        FinalStatus        = $finalStatus
        ResultAvailable    = $resultAvailable
        ElapsedSeconds     = $pollElapsed
        SecretLeakDetected = [bool]$secretLeakDetected
        Error              = Protect-AppServiceSecret $scen4Error
    }

    # --- Scenario 5: POST /api/workflows/{requestId}/cancel ---
    $cancelRequestId = "appval-cnl-" + [Guid]::NewGuid().ToString("N")
    $cancelStartPayload = @{
        requestId       = $cancelRequestId
        readyManifestId = $readyManifestId
        classification  = "light"
        taskCount       = 1
    } | ConvertTo-Json -Compress

    $null = Invoke-AppServiceHttpCall -Uri "$baseUrl/api/workflows" -Method 'POST' -Headers $defaultHeaders -Body $cancelStartPayload -Handler $HttpHandler

    $cancelPayload = @{
        reason = "Integration validation cancellation test"
    } | ConvertTo-Json -Compress

    $scen5Res = Invoke-AppServiceHttpCall -Uri "$baseUrl/api/workflows/$cancelRequestId/cancel" -Method 'POST' -Headers $defaultHeaders -Body $cancelPayload -Handler $HttpHandler

    $scen5StatusCode = [int]$scen5Res['StatusCode']
    $scen5Body = [string]$scen5Res['Body']
    $scen5Headers = $scen5Res['Headers']
    $scen5Data = $scen5Res['Data']

    $scen5Passed = $false
    $scen5Error = $null

    $scen5Status = Get-SafeProperty $scen5Data 'status'
    $scen5Term = Get-SafeProperty $scen5Data 'terminationConfirmed'

    if (Test-AppServiceSecretLeak -Content $scen5Body -Headers $scen5Headers) {
        $secretLeakDetected = $true
        $scen5Error = "Secret or internal URL leaked in CancelWorkflow response."
    } elseif ($scen5StatusCode -ne 200 -and $scen5StatusCode -ne 202) {
        $scen5Error = "CancelWorkflow returned HTTP $scen5StatusCode instead of 200/202: $scen5Body"
    } elseif (-not $scen5Status) {
        $scen5Error = "CancelWorkflow response missing status field."
    } elseif ($scen5Status -cnotmatch '^(Cancelled|Cancelling)$') {
        $scen5Error = "CancelWorkflow status was '$scen5Status', expected Cancelled or Cancelling."
    } else {
        $scen5Passed = $true
    }

    $scenario5Result = [pscustomobject]@{
        Scenario             = 'CancelWorkflow'
        Passed               = $scen5Passed
        StatusCode           = $scen5StatusCode
        RequestId            = $cancelRequestId
        Status               = $scen5Status
        TerminationConfirmed = if ($null -ne $scen5Term) { [bool]$scen5Term } else { $null }
        Error                = Protect-AppServiceSecret $scen5Error
    }

    # --- Scenario 6 (Security): Owner Impersonation Spoofing ---
    $spoofedOwnerId = "attacker-impersonator-999"
    $spoofHeaders = & $buildHeaders -CustomOwner $spoofedOwnerId
    $spoofPayload = @{
        filename          = "spoof-probe.bin"
        expectedSizeBytes = 100
        contentType       = "application/octet-stream"
    } | ConvertTo-Json -Compress

    $scen6Res = Invoke-AppServiceHttpCall -Uri "$baseUrl/api/uploads/initiate" -Method 'POST' -Headers $spoofHeaders -Body $spoofPayload -Handler $HttpHandler

    $scen6StatusCode = [int]$scen6Res['StatusCode']
    $scen6Passed = $false
    $scen6Error = $null

    if ($scen6StatusCode -eq 403) {
        $scen6Passed = $true
    } else {
        $scen6Error = "Expected 403 Forbidden when spoofing owner '$spoofedOwnerId', but received HTTP $scen6StatusCode."
    }

    $scenario6Result = [pscustomobject]@{
        Scenario             = 'SecurityOwnerSpoofing'
        Passed               = $scen6Passed
        StatusCode           = $scen6StatusCode
        SpoofedOwnerRejected = ($scen6StatusCode -eq 403)
        Error                = Protect-AppServiceSecret $scen6Error
    }

    # --- Overall Result Compilation ---
    $allPassed = $scen1Passed -and $scen2Passed -and $scen3Passed -and $scen4Passed -and $scen5Passed -and $scen6Passed -and (-not $secretLeakDetected)

    $summary = [pscustomobject]@{
        ValidationKind     = 'AppServiceValidation'
        Status             = if ($allPassed) { 'Pass' } else { 'Fail' }
        AppServiceUrl      = $baseUrl
        OwnerId            = $OwnerId
        OrgId              = $OrgId
        CompletedUtc       = [DateTime]::UtcNow.ToString('o')
        AllScenariosPassed = [bool]$allPassed
        SecretLeakDetected = [bool]$secretLeakDetected
        Scenarios          = [ordered]@{
            InitiateUpload        = $scenario1Result
            FinalizeUpload        = $scenario2Result
            StartWorkflow         = $scenario3Result
            GetWorkflowStatus     = $scenario4Result
            CancelWorkflow        = $scenario5Result
            SecurityOwnerSpoofing = $scenario6Result
        }
        ExitCode           = if ($allPassed) { 0 } else { 1 }
    }

    if ($AsJson) {
        return ($summary | ConvertTo-Json -Depth 6)
    }

    return $summary
}

# Invoke when executed as script
Invoke-AppServiceValidationMain `
    -AppServiceUrl $AppServiceUrl `
    -BearerToken $BearerToken `
    -OwnerId $OwnerId `
    -OrgId $OrgId `
    -PollIntervalSeconds $PollIntervalSeconds `
    -TimeoutSeconds $TimeoutSeconds `
    -AsJson:$AsJson `
    -HttpHandler $HttpHandler
