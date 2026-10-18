[CmdletBinding()]
param(
    [string]$DeploymentResultPath,
    [ValidateSet('single','three','classification','failure','admission20','conflict','timeout')][string[]]$Scenarios = @('single','three','classification','failure','admission20','conflict'),
    [ValidateRange(1,86400)][int]$TimeoutSeconds = 900,
    [ValidateRange(1,60)][int]$PollSeconds = 10,
    [string]$EvidenceDirectory,
    [switch]$DedicatedEnvironment
)

. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
. (Join-Path $PSScriptRoot 'Validation.Assertions.ps1')

function Stop-PocValidation([string]$Code) {
    $exception = [InvalidOperationException]::new($Code)
    $exception.Data['PocValidationCode'] = $Code
    throw $exception
}

function Invoke-PocValidationHttp {
    param([string]$Uri, [string]$Method='Get', [hashtable]$Headers=@{}, [string]$Body)
    try {
        $parameters = @{ Uri=$Uri; Method=$Method; Headers=$Headers; UseBasicParsing=$true; TimeoutSec=60; ErrorAction='Stop'; MaximumRedirection=0 }
        if ($Body) { $parameters.Body=$Body; $parameters.ContentType='application/json' }
        $response = Invoke-WebRequest @parameters
        return @{ StatusCode=[int]$response.StatusCode; Data=($response.Content | ConvertFrom-Json) }
    }
    catch {
        $property = $_.Exception.PSObject.Properties['Response']
        if ($property -and $property.Value) {
            $code = [int]$property.Value.StatusCode
            if ($code -in @(401,403)) { Stop-PocValidation "HttpAuthorization$code" }
            if ($code -in @(404,409)) { return @{ StatusCode=$code; Data=$null } }
        }
        Stop-PocValidation 'HttpRequestFailed'
    }
}

function Invoke-PocConcurrentAdmission {
    param([string]$Uri, [hashtable]$Headers, [string]$Body, [int]$Count=20)
    Add-Type -AssemblyName System.Net.Http
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.MaxConnectionsPerServer = $Count
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    $client.DefaultRequestHeaders.ExpectContinue = $false
    $requests = @(); $pending = @(); $responses = @()
    try {
        # Start every send before awaiting any response. No PowerShell jobs serialize the requests.
        for ($i=0; $i -lt $Count; $i++) {
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,$Uri)
            foreach ($name in $Headers.Keys) { $null = $request.Headers.TryAddWithoutValidation($name,[string]$Headers[$name]) }
            $request.Content = [Net.Http.StringContent]::new($Body,[Text.Encoding]::UTF8,'application/json')
            $requests += $request
            $pending += $client.SendAsync($request)
        }
        foreach ($task in $pending) {
            $response = $task.GetAwaiter().GetResult(); $responses += $response
            $code = [int]$response.StatusCode
            if ($code -in @(401,403)) { Stop-PocValidation "HttpAuthorization$code" }
            if ($code -ne 202) { Stop-PocValidation 'ConcurrentAdmissionRejected' }
            $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            @{ StatusCode=$code; Data=($content | ConvertFrom-Json) }
        }
    }
    catch {
        if ($_.Exception.Data['PocValidationCode']) { throw }
        Stop-PocValidation 'ConcurrentHttpFailed'
    }
    finally {
        # HttpClient disposal cancels unfinished requests; none of their errors or bodies are logged.
        $client.Dispose()
        foreach ($item in $responses) { $item.Dispose() }
        foreach ($item in $requests) { $item.Dispose() }
        $handler.Dispose()
    }
}

function Get-PocValidationKey([hashtable]$Outputs) {
    $keys = Invoke-AzJson -Sensitive -Arguments @('functionapp','function','keys','list','--name',$Outputs.function_app_name,
        '--resource-group',$Outputs.resource_group_name,'--function-name','StartWorkflow')
    if (-not $keys -or -not $keys.PSObject.Properties['default'] -or -not $keys.default) { Stop-PocValidation 'FunctionKeyUnavailable' }
    return [string]$keys.default
}

function Get-PocBlobDocument {
    param([hashtable]$Outputs,[string]$Path)
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ('poc-validation-' + [Guid]::NewGuid().ToString('N') + '.json')
    try {
        $null = Invoke-AzJson -Sensitive -Arguments @('storage','blob','download','--account-name',$Outputs.storage_account_name,
            '--container-name',$Outputs.blob_container_name,'--name',$Path,'--file',$temporary,'--overwrite','true','--auth-mode','login','--no-progress')
        return Get-Content -LiteralPath $temporary -Raw | ConvertFrom-Json
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

function Test-PocBlobExists {
    param([hashtable]$Outputs,[string]$Path)
    $result = Invoke-AzJson -Sensitive -Arguments @('storage','blob','exists','--account-name',$Outputs.storage_account_name,
        '--container-name',$Outputs.blob_container_name,'--name',$Path,'--auth-mode','login')
    return [bool]$result.exists
}

function Get-PocBatchObservation {
    param([hashtable]$Outputs,[string]$JobId)
    $endpoint = $Outputs.batch_endpoint.TrimEnd('/')
    $root = "$endpoint/jobs/$([Uri]::EscapeDataString($JobId))"
    $job = Invoke-AzJson -Sensitive -Arguments @('rest','--method','get','--url',"$root`?api-version=2025-06-01",'--resource','https://batch.core.windows.net/')
    $tasks = @(); $visited = @{}; $next = "$root/tasks?api-version=2025-06-01"
    while ($next) {
        $uri = [Uri]$next
        if ($uri.Scheme -ne 'https' -or $uri.Authority -ne ([Uri]$endpoint).Authority -or $visited.ContainsKey($next)) { Stop-PocValidation 'InvalidBatchContinuation' }
        $visited[$next] = $true
        $page = Invoke-AzJson -Sensitive -Arguments @('rest','--method','get','--url',$next,'--resource','https://batch.core.windows.net/')
        $tasks += @($page.value)
        $next = if ($page.PSObject.Properties['odata.nextLink']) { [string]$page.'odata.nextLink' } else { $null }
    }
    return @{ Job=$job; Tasks=$tasks }
}

function Get-PocHistoryObservation([string]$StatusUri) {
    # Management history only covers the current generation. It cannot prove the absence
    # of a replaced generation, so never upgrade exactly-once evidence based on that API.
    return @{ Available=$false; ExecutionStarts=$null }
}

function Get-PocWorkflowSettings([hashtable]$Outputs) {
    # CLI config appsettings list also queries slotConfigNames (sites/config/read).
    # Direct ARM POST requires only the scoped sites/config/list/action permission.
    $subscription = [Uri]::EscapeDataString([string]$Outputs.subscription_id)
    $resourceGroup = [Uri]::EscapeDataString([string]$Outputs.resource_group_name)
    $site = [Uri]::EscapeDataString([string]$Outputs.function_app_name)
    $uri = "https://management.azure.com/subscriptions/$subscription/resourceGroups/$resourceGroup/providers/Microsoft.Web/sites/$site/config/appsettings/list?api-version=2024-11-01"
    $response = Invoke-AzJson -Sensitive -Arguments @('rest','--method','post','--url',$uri)
    $props = if ($response -and $response.PSObject.Properties['properties']) { $response.properties } else { $null }
    $timeout = if ($props -and $props.PSObject.Properties['WORKFLOW_TIMEOUT_MIN'] -and $props.WORKFLOW_TIMEOUT_MIN) { [int]$props.WORKFLOW_TIMEOUT_MIN } else { 15 }
    $poll = if ($props -and $props.PSObject.Properties['BATCH_POLL_SECONDS'] -and $props.BATCH_POLL_SECONDS) { [int]$props.BATCH_POLL_SECONDS } else { 10 }
    return @{ TimeoutMinutes=$timeout; PollSeconds=$poll }
}

function Wait-PocWorkflow {
    param([string]$StatusUri,[int]$TimeoutSeconds,[int]$PollSeconds,[hashtable]$Observation=@{})
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $response = Invoke-PocValidationHttp -Uri $StatusUri
        if ($response.StatusCode -in @(200,202,404)) {
            $code = [string]$response.StatusCode
            if (-not $Observation.ContainsKey($code)) { $Observation[$code]=0 }
            $Observation[$code]++
        }
        if ($response.StatusCode -ne 404) {
            if ($response.StatusCode -notin @(200,202) -or -not $response.Data -or -not $response.Data.PSObject.Properties['runtimeStatus'] -or $response.Data.runtimeStatus -notin @('Pending','Running','ContinuedAsNew','Completed','Failed','Terminated','Canceled','Suspended')) { Stop-PocValidation 'InvalidStatusResponse' }
            if ($response.Data.runtimeStatus -in @('Completed','Failed','Terminated','Canceled','Suspended')) { return $response.Data }
        }
        $remaining = $TimeoutSeconds - $watch.Elapsed.TotalSeconds
        if ($remaining -le 0) { break }
        Start-Sleep -Seconds ([Math]::Min($PollSeconds,[Math]::Ceiling($remaining)))
    } while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    Stop-PocValidation 'StatusWaitTimedOut'
}

function Assert-PocAccepted {
    param($Response,[hashtable]$Outputs,[string]$InstanceId,[string]$RequestId)
    if ($Response.StatusCode -ne 202 -or -not $Response.Data.instanceId -or -not $Response.Data.statusQueryGetUri) { Stop-PocValidation 'InvalidAdmissionResponse' }
    if ($Response.Data.instanceId -ne $InstanceId) { Stop-PocValidation 'InstanceIdentityMismatch' }
    Assert-PocPublicStatusUri -StatusUri $Response.Data.statusQueryGetUri -Outputs $Outputs -RequestId $RequestId
    # Detailed outcome/history checks are privileged runner diagnostics. Never
    # obtain their system-key URL from the public admission response.
    return Get-PocRecoveryStatusUri -Outputs $Outputs -InstanceId $InstanceId
}

function Get-PocIdentity([string]$RequestId) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return 'poc-' + ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($RequestId))).Replace('-','').ToLowerInvariant()).Substring(0,40) }
    finally { $hash.Dispose() }
}

function Get-PocFingerprint {
    param([hashtable]$Request,[string]$Classification)
    # Match WorkflowIdentity's default System.Text.Json PascalCase record order.
    # Every string here is generated locally from the ASCII request contract.
    $canonical = [ordered]@{ Version=1; Request=[ordered]@{ RequestId=$Request.requestId; Classification=$Request.classification;
        TaskCount=$Request.taskCount; FailTaskIndex=$Request.failTaskIndex; LightDelaySeconds=$Request.lightDelaySeconds;
        HeavyDelaySeconds=$Request.heavyDelaySeconds }; Classification=$Classification } | ConvertTo-Json -Depth 4 -Compress
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)))).Replace('-','').ToLowerInvariant() }
    finally { $hash.Dispose() }
}

function Get-PocSourceHash([string]$Repository) {
    # Include tracked and non-ignored untracked files, including local edits. Store
    # only a digest, never diff contents or file contents in the evidence.
    $paths = @(& git -C $Repository -c core.quotepath=false ls-files --cached --others --exclude-standard 2>$null | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0) { return $null }
    $entries = @($paths | ForEach-Object {
        $file = Join-Path $Repository $_
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            $_ + ':' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        } else { $_ + ':missing' }
    })
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes(($entries -join "`n"))))).Replace('-','').ToLowerInvariant() }
    finally { $hash.Dispose() }
}

function Invoke-PocScenario {
    param([string]$Name,[hashtable]$Outputs,[string]$Key,[int]$TimeoutSeconds,[int]$PollSeconds,[switch]$DedicatedEnvironment,[hashtable]$Evidence)
    if ($Name -eq 'timeout' -and -not $DedicatedEnvironment) { return @{ Status='NotRun'; Reason='DedicatedEnvironmentRequired' } }
    $classes = if ($Name -eq 'classification') { @('light','heavy','auto') } else { @('light') }
    $runs = @(); $Evidence.runs = $runs
    foreach ($classification in $classes) {
        $requestId = 'validate-' + [Guid]::NewGuid().ToString('N')
        $jobId = Get-PocIdentity $requestId
        $count = if ($Name -in @('three','admission20')) { 3 } else { 1 }
        $request = @{ requestId=$requestId; classification=$classification; taskCount=$count; failTaskIndex=$null; lightDelaySeconds=1; heavyDelaySeconds=3 }
        if ($Name -eq 'failure') { $request.failTaskIndex=0 }
        if ($Name -eq 'timeout') {
            $settings = Get-PocWorkflowSettings $Outputs
            $Evidence.settings = $settings
            if ($settings.TimeoutMinutes -lt 1 -or $settings.TimeoutMinutes -ge 60 -or $settings.PollSeconds -lt 1 -or $settings.PollSeconds -gt 300) { return @{ Status='NotRun'; Reason='TimeoutSettingsUnsuitable' } }
            if ($TimeoutSeconds -lt ($settings.TimeoutMinutes*60 + 180)) { return @{ Status='NotRun'; Reason='RunnerWaitTooShortForTimeoutAndTermination' } }
            $request.lightDelaySeconds = [Math]::Min(3600,($settings.TimeoutMinutes*60+180))
        }
        $run = @{ requestId=$requestId; instanceId=$jobId; jobId=$jobId; taskIds=@(); requestedClassification=$classification }
        $runs += $run; $Evidence.runs = $runs
        $body = $request | ConvertTo-Json -Compress
        $uri = $Outputs.function_app_url.TrimEnd('/') + '/api/workflows'
        $headers = @{ 'x-functions-key'=$Key }
        $responses = if ($Name -eq 'admission20') { @(Invoke-PocConcurrentAdmission -Uri $uri -Headers $headers -Body $body -Count 20) }
            else { @(Invoke-PocValidationHttp -Uri $uri -Method Post -Headers $headers -Body $body) }
        if ($Name -eq 'admission20' -and $responses.Count -ne 20) { Stop-PocValidation 'ConcurrentResponseCountMismatch' }
        $statusUri = $null
        foreach ($response in $responses) { $statusUri = Assert-PocAccepted -Response $response -Outputs $Outputs -InstanceId $jobId -RequestId $requestId }
        $run.admissionHttpStatuses = @($responses | ForEach-Object { 202 })
        if ($Name -eq 'conflict') {
            $different = $request.Clone(); $different.heavyDelaySeconds=4
            $conflict = Invoke-PocValidationHttp -Uri $uri -Method Post -Headers $headers -Body ($different | ConvertTo-Json -Compress)
            if ($conflict.StatusCode -ne 409) { Stop-PocValidation 'ExpectedConflict409' }
            $run.conflictHttpStatus=409
        }
        $run.statusHttpCounts=@{}
        $status = Wait-PocWorkflow -StatusUri $statusUri -TimeoutSeconds $TimeoutSeconds -PollSeconds $PollSeconds -Observation $run.statusHttpCounts
        $run.runtimeStatus = if ($status.runtimeStatus -in @('Completed','Failed','Terminated','Canceled','Suspended')) { [string]$status.runtimeStatus } else { 'Unknown' }
        $batch = Get-PocBatchObservation -Outputs $Outputs -JobId $jobId
        if ($batch.Job.id -ne $jobId -or $batch.Job.poolInfo.poolId -ne $Outputs.batch_pool_id) { Stop-PocValidation 'BatchJobIdentityMismatch' }
        if ($Name -eq 'timeout') {
            # Observe the deployed Batch safety net rather than inferring it from
            # the workflow configuration. Its clock starts at Job creation.
            $constraintSeconds=$null
            try {
                if ($batch.Job.PSObject.Properties['constraints'] -and $batch.Job.constraints -and
                    $batch.Job.constraints.PSObject.Properties['maxWallClockTime']) {
                    $constraintSeconds=[Xml.XmlConvert]::ToTimeSpan([string]$batch.Job.constraints.maxWallClockTime).TotalSeconds
                }
            } catch { Stop-PocValidation 'InvalidBatchWallClockConstraint' }
            if ($null -eq $constraintSeconds -or $constraintSeconds -ne ($settings.TimeoutMinutes*60)) { Stop-PocValidation 'BatchWallClockConstraintMismatch' }
            $run.batchMaxWallClockSeconds=[int]$constraintSeconds
            $run.batchTerminationReason='Unobserved'
            if ($batch.Job.PSObject.Properties['executionInfo'] -and $batch.Job.executionInfo -and
                $batch.Job.executionInfo.PSObject.Properties['terminateReason']) {
                # Never copy the arbitrary service string into evidence.
                $reason=[string]$batch.Job.executionInfo.terminateReason
                $run.batchTerminationReason=if ($reason -ceq 'MaxWallClockTimeExpiry') { 'MaxWallClockTimeExpiry' } else { 'Other' }
            }
        }
        $selected = if ($classification -eq 'auto') { 'light' } else { $classification }
        $expectedFingerprint = Get-PocFingerprint -Request $request -Classification $selected
        $fingerprints = @($batch.Job.metadata | Where-Object { $_.name -eq 'poc-fingerprint' })
        if ($fingerprints.Count -ne 1 -or $fingerprints[0].value -ne $expectedFingerprint) { Stop-PocValidation 'BatchFingerprintMismatch' }
        $run.fingerprint=$expectedFingerprint; $run.classification=$selected
        $expectedIds = @(0..($count-1) | ForEach-Object { 'task-{0:00}' -f $_ })
        $observedIds = @($batch.Tasks | ForEach-Object { [string]$_.id } | Sort-Object)
        if (@($observedIds | Where-Object { $_ -notin $expectedIds }).Count -or ($Name -ne 'timeout' -and ($observedIds -join ',') -ne ($expectedIds -join ','))) { Stop-PocValidation 'BatchTaskIdentityMismatch' }
        $run.taskIds=$observedIds
        $run.batchJobState = if ($batch.Job.state -in @('active','disabling','disabled','enabling','terminating','completed','deleting')) { [string]$batch.Job.state } else { 'Unknown' }
        $run.tasks = @($batch.Tasks | ForEach-Object {
            $exit=$null
            if ($_.PSObject.Properties['executionInfo'] -and $_.executionInfo -and $_.executionInfo.PSObject.Properties['exitCode'] -and $null -ne $_.executionInfo.exitCode) { $exit=[int]$_.executionInfo.exitCode }
            @{ taskId=[string]$_.id; state=$(if ($_.state -in @('active','preparing','running','completed')) { [string]$_.state } else { 'Unknown' }); exitCode=$exit }
        })
        foreach ($task in $batch.Tasks) {
            $environment = @($task.environmentSettings | Where-Object { $_.name -eq 'POC_FINGERPRINT' })
            if ($environment.Count -ne 1 -or $environment[0].value -ne $expectedFingerprint) { Stop-PocValidation 'TaskFingerprintMismatch' }
        }
        $storedRequest = Get-PocBlobDocument -Outputs $Outputs -Path "runs/poc-$requestId/request.json"
        if ($storedRequest.version -ne 1 -or $storedRequest.instanceId -ne $jobId) { Stop-PocValidation 'InputIdentityMismatch' }
        foreach ($field in $request.Keys) { if ($storedRequest.request.$field -ne $request[$field]) { Stop-PocValidation 'InputContentMismatch' } }
        $root = "runs/poc-$requestId"
        if ($Name -in @('failure','timeout')) {
            $expectedState = if ($Name -eq 'timeout') { 'TimedOut' } else { 'Failed' }
            if ($status.runtimeStatus -ne 'Failed' -or $status.customStatus.state -ne $expectedState) { Stop-PocValidation 'ExpectedBusinessFailureMissing' }
            $run.businessState=$expectedState
            if (Test-PocBlobExists -Outputs $Outputs -Path "$root/final/manifest.json") { Stop-PocValidation 'UnexpectedSuccessManifest' }
            if ($Name -eq 'failure') {
                if ($batch.Tasks.Count -ne 1 -or $batch.Tasks[0].state -ne 'completed' -or $batch.Tasks[0].executionInfo.exitCode -ne 42) { Stop-PocValidation 'ExpectedExit42Missing' }
                $run.exitCodes=@(42)
            }
            elseif ($batch.Job.state -ne 'completed' -or -not $status.customStatus.termination.completed -or $status.customStatus.terminationError) { Stop-PocValidation 'JobTerminationUnconfirmed' }
            continue
        }
        if ($status.runtimeStatus -ne 'Completed' -or $status.output.state -ne 'Succeeded' -or $status.output.requestId -ne $requestId -or $status.output.jobId -ne $jobId) { Stop-PocValidation 'UnexpectedWorkflowOutcome' }
        $aggregate = Get-PocBlobDocument -Outputs $Outputs -Path "$root/final/aggregate.json"
        $manifest = Get-PocBlobDocument -Outputs $Outputs -Path "$root/final/manifest.json"
        $blobRoot = $Outputs.blob_endpoint.TrimEnd('/') + '/' + $Outputs.blob_container_name
        Assert-PocPublishedDocuments -RequestId $requestId -TaskCount $count -Aggregate $aggregate -Manifest $manifest -BlobRoot $blobRoot -JobId $jobId -Fingerprint $expectedFingerprint
        foreach ($task in $batch.Tasks) {
            if ($task.state -ne 'completed' -or $task.executionInfo.exitCode -ne 0 -or $task.executionInfo.result -ne 'success' -or
                ($task.executionInfo.PSObject.Properties['failureInfo'] -and $task.executionInfo.failureInfo)) { Stop-PocValidation 'BatchTaskNotSuccessful' }
            $result = Get-PocBlobDocument -Outputs $Outputs -Path "$root/tasks/$($task.id)/result.json"
            if ($result.requestId -ne $requestId -or $result.jobId -ne $jobId -or $result.taskId -ne $task.id -or $result.classification -ne $selected -or $result.fingerprint -ne $manifest.fingerprint -or $result.value -ne 1) { Stop-PocValidation 'TaskResultMismatch' }
        }
        $run.sum=$manifest.sum; $run.taskCount=$count; $run.classification=$selected; $run.fingerprint=[string]$manifest.fingerprint
        if ($Name -eq 'admission20') {
            $resend = Invoke-PocValidationHttp -Uri $uri -Method Post -Headers $headers -Body $body
            $resendUri = Assert-PocAccepted -Response $resend -Outputs $Outputs -InstanceId $jobId -RequestId $requestId
            $after = Wait-PocWorkflow -StatusUri $resendUri -TimeoutSeconds $TimeoutSeconds -PollSeconds $PollSeconds
            if ($after.runtimeStatus -ne 'Completed' -or $after.createdTime -ne $status.createdTime) { Stop-PocValidation 'CompletedResendChangedExecution' }
            $afterBatch = Get-PocBatchObservation -Outputs $Outputs -JobId $jobId
            if ((@($afterBatch.Tasks | ForEach-Object { $_.id } | Sort-Object) -join ',') -ne ($expectedIds -join ',')) { Stop-PocValidation 'CompletedResendChangedTasks' }
            $history = Get-PocHistoryObservation -StatusUri $statusUri
            $run.historyObservable=[bool]$history.Available
            if (-not $history.Available) { return @{ Status='NotRun'; Reason='AdmissionObservedButExecutionHistoryUnavailable' } }
            if ($history.ExecutionStarts -ne 1) { Stop-PocValidation 'DuplicateMainExecutionObserved' }
        }
    }
    return @{ Status='Pass'; Reason='ExpectedOutcomeObserved' }
}

function Invoke-PocValidation {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$DeploymentResultPath,
        [ValidateSet('single','three','classification','failure','admission20','conflict','timeout')][string[]]$Scenarios=@('single','three','classification','failure','admission20','conflict'),
        [ValidateRange(1,86400)][int]$TimeoutSeconds=900,[ValidateRange(1,60)][int]$PollSeconds=10,
        [Parameter(Mandatory)][string]$EvidenceDirectory,[switch]$DedicatedEnvironment)
    $ErrorActionPreference='Stop'
    $directory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceDirectory)
    $null = New-Item -ItemType Directory -Path $directory -Force
    $summary = [ordered]@{ schemaVersion=1; startedUtc=[DateTime]::UtcNow.ToString('o'); completedUtc=$null; validationKind='Azure';
        gitRevision=$null; gitDirty=$null; sourceSha256=$null; packageSha256=$null; settings=@{ timeoutSeconds=$TimeoutSeconds; pollSeconds=$PollSeconds; dedicatedEnvironment=[bool]$DedicatedEnvironment; batchNodeType=$null; durableBackend=$null; taskHubName=$null };
        Scenarios=@(); excluded=@('HostRecovery','PermissionFaults','NetworkFaults','ZeroNodeAutoscale','RealCpp','SpotEvictionRecovery'); ExitCode=1 }
    $key=$null
    try {
        if (-not $Scenarios -or $Scenarios.Count -eq 0) { Stop-PocValidation 'EmptyScenarioSelection' }
        $repository = Split-Path $PSScriptRoot -Parent
        $deployment = ConvertTo-DeploymentHashtable (Get-Content -LiteralPath $DeploymentResultPath -Raw | ConvertFrom-Json)
        if ($deployment.ContainsKey('source_provenance')) {
            $provenance=$deployment.source_provenance
            if ($provenance.gitRevision -notmatch '^[0-9a-f]{40}$' -or $provenance.sourceSha256 -notmatch '^[0-9a-fA-F]{64}$' -or $provenance.gitDirty -isnot [bool]) { Stop-PocValidation 'InvalidSourceProvenance' }
            $summary.gitRevision=$provenance.gitRevision
            $summary.gitDirty=$provenance.gitDirty
            $summary.sourceSha256=$provenance.sourceSha256
        } else {
            $revision = @(& git -C $repository rev-parse HEAD 2>$null)
            if ($LASTEXITCODE -eq 0 -and $revision.Count -eq 1 -and $revision[0] -match '^[0-9a-f]{40}$') { $summary.gitRevision=$revision[0]; $summary.gitDirty=[bool]@(& git -C $repository status --porcelain 2>$null).Count }
            $summary.sourceSha256 = Get-PocSourceHash -Repository $repository
        }
        if (-not $deployment.ContainsKey('outputs')) { Stop-PocValidation 'DeploymentOutputsMissing' }
        $outputs = $deployment.outputs
        try { $durableSettings = Get-DeploymentDurableSettings -Variables $outputs -FromOutputs }
        catch { Stop-PocValidation 'InvalidDurableSettings' }
        # Configured backend identity; this is not evidence of provider-specific Azure execution.
        $summary.settings.durableBackend=$durableSettings.durable_backend
        $summary.settings.taskHubName=$durableSettings.durable_task_hub_name
        if ($outputs.ContainsKey('batch_node_type') -and $null -ne $outputs.batch_node_type) {
            if ($outputs.batch_node_type -cnotin @('dedicated','spot')) { Stop-PocValidation 'InvalidBatchNodeType' }
            $summary.settings.batchNodeType=$outputs.batch_node_type
        }
        foreach ($field in @('subscription_id','resource_group_name','function_app_name','function_app_url','storage_account_name','blob_container_name','blob_endpoint','batch_endpoint','batch_pool_id')) {
            if (-not $outputs.ContainsKey($field) -or [string]::IsNullOrWhiteSpace([string]$outputs[$field])) { Stop-PocValidation 'RequiredDeploymentOutputMissing' }
        }
        foreach ($field in @('function_app_url','blob_endpoint','batch_endpoint')) {
            $endpoint=[Uri]$outputs[$field]
            if (-not $endpoint.IsAbsoluteUri -or $endpoint.Scheme -ne 'https' -or $endpoint.Query -or $endpoint.UserInfo -or $endpoint.Fragment) { Stop-PocValidation 'InvalidDeploymentEndpoint' }
        }
        if ($deployment.ContainsKey('package_sha256') -and $deployment.package_sha256 -match '^[0-9a-fA-F]{64}$') { $summary.packageSha256=$deployment.package_sha256.ToLowerInvariant() }
        $script:DeploymentSubscriptionId = $outputs.subscription_id
        if (-not (Get-Variable AzPath -Scope Script -ErrorAction SilentlyContinue)) { $script:AzPath = Resolve-DeploymentTool -Name az -RepositoryRoot $repository }
        $key = Get-PocValidationKey $outputs
        foreach ($name in @($Scenarios | Select-Object -Unique)) {
            $scenario = [ordered]@{ Name=$name; Status='Fail'; Reason='ScenarioFailed'; startedUtc=[DateTime]::UtcNow.ToString('o'); completedUtc=$null; Evidence=@{} }
            $summary.Scenarios += $scenario
            try {
                $outcome = Invoke-PocScenario -Name $name -Outputs $outputs -Key $key -TimeoutSeconds $TimeoutSeconds -PollSeconds $PollSeconds -DedicatedEnvironment:$DedicatedEnvironment -Evidence $scenario.Evidence
                $scenario.Status=$outcome.Status; $scenario.Reason=$outcome.Reason
            }
            catch { if ($_.Exception.Data['PocValidationCode']) { $scenario.Reason=[string]$_.Exception.Data['PocValidationCode'] } }
            finally { $scenario.completedUtc=[DateTime]::UtcNow.ToString('o') }
        }
    }
    catch {
        $reason='SetupFailed'; if ($_.Exception.Data['PocValidationCode']) { $reason=[string]$_.Exception.Data['PocValidationCode'] }
        $summary.Scenarios += @{ Name='setup'; Status='Fail'; Reason=$reason; Evidence=@{}; startedUtc=$summary.startedUtc; completedUtc=[DateTime]::UtcNow.ToString('o') }
        foreach ($name in @($Scenarios | Select-Object -Unique)) {
            if ($name -notin @($summary.Scenarios | ForEach-Object { $_.Name })) { $summary.Scenarios += @{ Name=$name; Status='NotRun'; Reason='SetupFailed'; Evidence=@{}; startedUtc=$null; completedUtc=$null } }
        }
    }
    finally {
        $key=$null
        $summary.completedUtc=[DateTime]::UtcNow.ToString('o')
        $summary.ExitCode = if (@($summary.Scenarios | Where-Object { $_.Status -eq 'Fail' }).Count) { 1 } elseif (@($summary.Scenarios | Where-Object { $_.Status -eq 'NotRun' }).Count) { 2 } else { 0 }
        Write-DeploymentJson -Path (Join-Path $directory 'summary.json') -Value $summary
        $lines = @("PoC validation ($($summary.validationKind))", "Completed UTC: $($summary.completedUtc)", "Exit code: $($summary.ExitCode)")
        $lines += @($summary.Scenarios | ForEach-Object { '{0}: {1} ({2})' -f $_.Name,$_.Status,$_.Reason })
        $lines += 'Excluded: host recovery, permission/network fault injection, zero-node autoscale, real C++, Spot eviction recovery.'
        [IO.File]::WriteAllLines((Join-Path $directory 'summary.txt'),[string[]]$lines,[Text.UTF8Encoding]::new($false))
    }
    return [pscustomobject]$summary
}

if ($MyInvocation.InvocationName -ne '.') {
    if (-not $EvidenceDirectory) { $EvidenceDirectory=Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/validation/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)) }
    if (-not $DeploymentResultPath) { $DeploymentResultPath=Join-Path (Get-Location) 'deployment-result.json' }
    $result = Invoke-PocValidation -DeploymentResultPath $DeploymentResultPath -Scenarios $Scenarios -EvidenceDirectory $EvidenceDirectory -TimeoutSeconds $TimeoutSeconds -PollSeconds $PollSeconds -DedicatedEnvironment:$DedicatedEnvironment
    Write-Host "Validation completed. Exit code: $($result.ExitCode). Evidence: $EvidenceDirectory"
    exit $result.ExitCode
}
