[CmdletBinding()]
param(
    [hashtable]$Outputs,
    [ValidateRange(1,86400)][int]$TimeoutSeconds = 900,
    [ValidateRange(1,60)][int]$PollSeconds = 10,
    [ValidateSet('Pool','Functions','All')][string]$Phase = 'All',
    [switch]$Smoke
)

. (Join-Path $PSScriptRoot 'Validation.Assertions.ps1')

function Invoke-ValidationHttp {
    param([string]$Uri, [string]$Method = 'Get', [hashtable]$Headers = @{}, [string]$Body)
    try {
        $parameters = @{ Uri=$Uri; Method=$Method; Headers=$Headers; UseBasicParsing=$true; TimeoutSec=60; ErrorAction='Stop' }
        if ($Body) { $parameters.Body=$Body; $parameters.ContentType='application/json' }
        $response = Invoke-WebRequest @parameters
        return @{ StatusCode=[int]$response.StatusCode; Data=($response.Content | ConvertFrom-Json) }
    }
    catch {
        # URLs contain Durable system keys; neither request URLs nor exception text may be logged.
        $responseProperty = $_.Exception.PSObject.Properties['Response']
        if ($responseProperty -and $responseProperty.Value) {
            $status = [int]$responseProperty.Value.StatusCode
            if ($status -eq 404 -and $Method -eq 'Get') {
                # Entity admission is asynchronous; instance creation can follow the HTTP 202.
                return @{ StatusCode=404; Data=$null }
            }
            if ($status -in @(401,403)) { Stop-Validation "Workflow HTTP authorization failed (HTTP $status); secret-bearing URLs were suppressed." }
        }
        throw 'Workflow HTTP request failed. Check network access, Function authentication, and Application Insights; secret-bearing URLs were suppressed.'
    }
}

function Wait-ValidationCondition {
    param([scriptblock]$Check, [string]$Description, [int]$TimeoutSeconds, [int]$PollSeconds, [switch]$RetryErrors)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $lastError = $null
    do {
        try { $result = & $Check }
        catch {
            if (-not $RetryErrors -or $_.Exception.Data['PermanentValidation']) { throw }
            $lastError = $_.Exception.Message
            $result = $null
        }
        if ($result) { return $result }
        $remaining = $TimeoutSeconds - $watch.Elapsed.TotalSeconds
        if ($remaining -le 0) { break }
        Start-Sleep -Seconds ([Math]::Min($PollSeconds, [Math]::Ceiling($remaining)))
    } while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw "$Description timed out after $TimeoutSeconds seconds. Last error: $lastError"
}

function Stop-Validation([string]$Message) {
    $error = [InvalidOperationException]::new($Message)
    $error.Data['PermanentValidation']=$true
    throw $error
}

function Invoke-DeploymentValidation {
    [CmdletBinding()]
    param([Parameter(Mandatory)][hashtable]$Outputs,
        [ValidateRange(1,86400)][int]$TimeoutSeconds=900,
        [ValidateRange(1,60)][int]$PollSeconds=10, [switch]$Smoke,
        [ValidateSet('Pool','Functions','All')][string]$Phase='All')

    $required = @('subscription_id','resource_group_name','function_app_name','function_app_url','storage_account_name',
        'blob_container_name','blob_endpoint','batch_endpoint','batch_pool_id','batch_account_name','batch_resource_group_name')
    foreach ($name in $required) {
        if (-not $Outputs.ContainsKey($name) -or [string]::IsNullOrWhiteSpace([string]$Outputs[$name])) {
            throw "Deployment output '$name' is missing."
        }
    }
    $script:DeploymentSubscriptionId = $Outputs.subscription_id
    # Explicit audience avoids changing the user's Batch CLI login configuration.
    # https://learn.microsoft.com/rest/api/batchservice/nodes/list-nodes
    $endpoint = $Outputs.batch_endpoint.TrimEnd('/')
    $poolId = [Uri]::EscapeDataString($Outputs.batch_pool_id)
    if ($Phase -in @('Pool','All')) {
    # A referenced or autoscale pool may intentionally have zero nodes until a task triggers autoscale.
    # New fixed-scale pools expose their configured count; all of those nodes must be usable.
    $scaleMode = if ($Outputs.ContainsKey('batch_mode') -and $Outputs.batch_mode -eq 'new' -and $Outputs.ContainsKey('batch_scale_mode') -and $null -ne $Outputs.batch_scale_mode) { [string]$Outputs.batch_scale_mode } else { $null }
    if ($scaleMode -and $scaleMode -cnotin @('fixed','autoscale')) { throw 'batch_scale_mode must be fixed or autoscale.' }
    $minimumNodes = if ($Outputs.ContainsKey('batch_node_count') -and $null -ne $Outputs.batch_node_count) { [int]$Outputs.batch_node_count } else { 0 }
    if ($minimumNodes -lt 0) { throw 'batch_node_count cannot be negative.' }
    if ($Outputs.ContainsKey('batch_mode') -and $Outputs.batch_mode -eq 'new' -and $scaleMode -ne 'autoscale' -and $minimumNodes -lt 1) { throw 'New pools require a positive batch_node_count.' }
    # New metadata identifies the requested capacity type. Older deployment
    # results and reference-only pools retain the previous readiness behavior.
    $nodeType=$null
    if ($Outputs.ContainsKey('batch_mode') -and $Outputs.batch_mode -eq 'new' -and $Outputs.ContainsKey('batch_node_type') -and $null -ne $Outputs.batch_node_type) {
        $nodeType=[string]$Outputs.batch_node_type
        if ($nodeType -cnotin @('dedicated','spot')) { throw 'batch_node_type must be dedicated or spot.' }
    }
    $readinessDescription=if ($nodeType -eq 'spot') { 'Spot Batch node readiness (capacity is not guaranteed)' } else { 'Batch node readiness' }
    $null = Wait-ValidationCondition -RetryErrors -Description $readinessDescription -TimeoutSeconds $TimeoutSeconds -PollSeconds $PollSeconds -Check {
        $pool = Invoke-AzJson -Arguments @('rest','--method','get','--url',"$endpoint/pools/$poolId`?api-version=2025-06-01",'--resource','https://batch.core.windows.net/')
        if ($scaleMode -eq 'autoscale') {
            if (-not $pool.PSObject.Properties['enableAutoScale'] -or $pool.enableAutoScale -ne $true) { Stop-Validation 'Batch pool autoscale is not enabled.' }
            if ($pool.PSObject.Properties['autoScaleRun'] -and $pool.autoScaleRun -and $pool.autoScaleRun.PSObject.Properties['error'] -and $pool.autoScaleRun.error) {
                Stop-Validation "Batch pool autoscale evaluation failed ($($pool.autoScaleRun.error.code))."
            }
        }
        if ($minimumNodes -eq 0) { return $true }
        if ($pool.PSObject.Properties['resizeErrors'] -and @($pool.resizeErrors).Count -gt 0) {
            $codes = @($pool.resizeErrors | ForEach-Object { $_.code }) -join ', '
            Stop-Validation "Batch pool resize failed ($codes). Check quota, image access, networking, and Batch permissions."
        }
        $nodes = @()
        $next = "$endpoint/pools/$poolId/nodes?api-version=2025-06-01"
        $visited = @{}
        while ($next) {
            $nextUri = [Uri]$next
            if ($nextUri.Scheme -ne 'https' -or $nextUri.Authority -ne ([Uri]$endpoint).Authority -or $visited.ContainsKey($next)) {
                Stop-Validation 'Batch node pagination returned an invalid or repeated continuation URL.'
            }
            $visited[$next] = $true
            $page = Invoke-AzJson -Arguments @('rest','--method','get','--url',$next,'--resource','https://batch.core.windows.net/')
            $nodes += @($page.value)
            $next = if ($page.PSObject.Properties['odata.nextLink']) { [string]$page.'odata.nextLink' } else { $null }
        }
        # Preempted/unusable nodes remain in the API list, but cannot run tasks.
        $readyNodes=@($nodes | Where-Object { $_.state -in @('idle','running') -and $_.schedulingState -ne 'disabled' })
        if ($nodeType) {
            foreach ($node in $readyNodes) {
                if (-not $node.PSObject.Properties['isDedicated'] -or $node.isDedicated -isnot [bool]) { Stop-Validation 'Batch node capacity type is missing; readiness cannot be verified.' }
            }
            $readyNodes=@($readyNodes | Where-Object { $_.isDedicated -eq ($nodeType -eq 'dedicated') })
        }
        return $readyNodes.Count -ge $minimumNodes
    }
    if ($scaleMode -eq 'autoscale' -and $minimumNodes -eq 0) { Write-Host 'Autoscale Batch pool is enabled and accessible; tasks will trigger node allocation from zero.' }
    elseif ($minimumNodes -eq 0) { Write-Host 'Existing Batch pool is accessible; tasks may trigger node allocation.' }
    elseif ($nodeType) { Write-Host "All configured $nodeType Batch nodes are ready." }
    else { Write-Host 'All configured Batch nodes are ready.' }
    if ($Phase -eq 'Pool') { return [pscustomobject]@{ Ready=$true; Phase='Pool'; Smoke='Not tested'; BatchNodeType=$nodeType; BatchScaleMode=$scaleMode } }
    }
    $expected = @('StartWorkflow','GetWorkflowStatus','GetWorkflowResult','MainOrchestrator','MonitorBatchTaskOrchestrator','ClassifyActivity',
        'EnsureBatchJobActivity','EnsureBatchTaskActivity','GetBatchTaskStatusActivity','FetchAndPersistActivity','AggregateActivity','PublishManifestActivity',
        'WorkflowAdmissionEntity','TerminateBatchJobActivity')
    $null = Wait-ValidationCondition -RetryErrors -Description 'Function indexing' -TimeoutSeconds $TimeoutSeconds -PollSeconds $PollSeconds -Check {
        $functions = @(Invoke-AzJson -Arguments @('functionapp','function','list','--name',$Outputs.function_app_name,
            '--resource-group',$Outputs.resource_group_name))
        $names = @($functions | ForEach-Object { ($_.name -split '/')[-1] })
        return @($expected | Where-Object { $_ -notin $names }).Count -eq 0
    }
    Write-Host "All $($expected.Count) Functions are indexed."
    if (-not $Smoke) { return [pscustomobject]@{ Ready=$true; Smoke='Not tested' } }

    $keys = Wait-ValidationCondition -RetryErrors -Description 'Function key availability (requires listKeys permission)' -TimeoutSeconds $TimeoutSeconds -PollSeconds $PollSeconds -Check {
        $available = Invoke-AzJson -Sensitive -Arguments @('functionapp','function','keys','list','--name',$Outputs.function_app_name,
            '--resource-group',$Outputs.resource_group_name,'--function-name','StartWorkflow')
        if ($available -and $available.PSObject.Properties['default'] -and $available.default) { return $available }
        return $null
    }
    $key = $keys.default
    if (-not $key) { throw 'StartWorkflow function key is unavailable; check listKeys permission and Function indexing.' }
    $requestId = 'deploy-' + [Guid]::NewGuid().ToString('N')
    $body = @{ requestId=$requestId; classification='light'; taskCount=3; lightDelaySeconds=1; heavyDelaySeconds=3 } | ConvertTo-Json -Compress
    $uri = $Outputs.function_app_url.TrimEnd('/') + '/api/workflows'
    $started = Wait-ValidationCondition -RetryErrors -Description 'Workflow HTTP starter' -TimeoutSeconds $TimeoutSeconds -PollSeconds $PollSeconds -Check {
        return Invoke-ValidationHttp -Uri $uri -Method Post -Headers @{ 'x-functions-key'=$key } -Body $body
    }
    $key = $null; $keys = $null
    if ($started.StatusCode -ne 202 -or -not $started.Data.instanceId -or -not $started.Data.statusQueryGetUri) {
        throw 'Workflow starter did not return HTTP 202 with instanceId and statusQueryGetUri.'
    }
    Assert-PocPublicStatusUri -StatusUri $started.Data.statusQueryGetUri -Outputs $Outputs -RequestId $requestId
    # Smoke inspects Durable output internally; system credentials never come
    # from the caller-facing response and are never included in evidence.
    $statusUri = [Uri](Get-PocRecoveryStatusUri -Outputs $Outputs -InstanceId $started.Data.instanceId)
    $status = Wait-ValidationCondition -RetryErrors -Description 'Three-task workflow' -TimeoutSeconds $TimeoutSeconds -PollSeconds $PollSeconds -Check {
        $response = Invoke-ValidationHttp -Uri $statusUri.AbsoluteUri
        if ($response.StatusCode -eq 404) { return $null }
        $data = $response.Data
        if ($data.runtimeStatus -in @('Failed','Terminated','Canceled','Suspended')) {
            Stop-Validation "Smoke workflow ended with runtimeStatus $($data.runtimeStatus). Check Application Insights using the request ID."
        }
        if ($data.runtimeStatus -eq 'Completed') { return $data }
        return $null
    }
    if ($status.output.state -ne 'Succeeded') { throw 'Completed workflow output is not Succeeded.' }
    $tempDirectory = Join-Path ([IO.Path]::GetTempPath()) ('poc-verify-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $tempDirectory
    try {
        $documents = @{}
        foreach ($name in @('aggregate','manifest')) {
            $file = Join-Path $tempDirectory "$name.json"
            $null = Wait-ValidationCondition -RetryErrors -Description "Blob $name availability (requires Blob data read permission)" -TimeoutSeconds $TimeoutSeconds -PollSeconds $PollSeconds -Check {
                $null = Invoke-AzJson -Arguments @('storage','blob','download','--account-name',$Outputs.storage_account_name,
                    '--container-name',$Outputs.blob_container_name,'--name',"runs/poc-$requestId/final/$name.json",'--file',$file,'--overwrite','true','--auth-mode','login','--no-progress')
                return $true
            }
            $documents[$name] = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        }
        $blobRoot = $Outputs.blob_endpoint.TrimEnd('/') + '/' + $Outputs.blob_container_name
        Assert-PocPublishedDocuments -RequestId $requestId -TaskCount 3 -Aggregate $documents.aggregate -Manifest $documents.manifest -BlobRoot $blobRoot
    }
    finally {
        # The directory is an absolute, freshly generated task-only temporary directory.
        if (Test-Path -LiteralPath $tempDirectory) { Remove-Item -LiteralPath $tempDirectory -Recurse -Force }
    }
    Write-Host "Smoke passed: requestId=$requestId, taskCount=3, sum=3."
    return [pscustomobject]@{ Ready=$true; Smoke='Pass'; RequestId=$requestId; InstanceId=$started.Data.instanceId; Sum=3; TaskCount=3 }
}

if ($MyInvocation.InvocationName -ne '.') {
    . (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
    if (-not $Outputs) { throw 'Supply the unified deployment Outputs hashtable.' }
    $script:AzPath = Resolve-DeploymentTool -Name az -RepositoryRoot (Split-Path $PSScriptRoot -Parent)
    Invoke-DeploymentValidation -Outputs $Outputs -TimeoutSeconds $TimeoutSeconds -PollSeconds $PollSeconds -Smoke:$Smoke -Phase $Phase
}
