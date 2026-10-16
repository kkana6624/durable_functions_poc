[CmdletBinding()]
param(
    [hashtable]$Outputs,
    [ValidateRange(1,86400)][int]$TimeoutSeconds = 900,
    [ValidateRange(1,60)][int]$PollSeconds = 10,
    [ValidateSet('Pool','Functions','All')][string]$Phase = 'All',
    [switch]$Smoke
)

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
    # A referenced pool may intentionally have zero nodes until a task triggers autoscale.
    # New pools expose their configured count; all of those nodes must be usable.
    $minimumNodes = if ($Outputs.ContainsKey('batch_node_count') -and $null -ne $Outputs.batch_node_count) { [int]$Outputs.batch_node_count } else { 0 }
    if ($minimumNodes -lt 0) { throw 'batch_node_count cannot be negative.' }
    if ($Outputs.ContainsKey('batch_mode') -and $Outputs.batch_mode -eq 'new' -and $minimumNodes -lt 1) { throw 'New pools require a positive batch_node_count.' }
    $null = Wait-ValidationCondition -RetryErrors -Description 'Batch node readiness' -TimeoutSeconds $TimeoutSeconds -PollSeconds $PollSeconds -Check {
        $pool = Invoke-AzJson -Arguments @('rest','--method','get','--url',"$endpoint/pools/$poolId`?api-version=2025-06-01",'--resource','https://batch.core.windows.net/')
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
        return @($nodes | Where-Object { $_.state -in @('idle','running') -and $_.schedulingState -ne 'disabled' }).Count -ge $minimumNodes
    }
    if ($minimumNodes -eq 0) { Write-Host 'Existing Batch pool is accessible; tasks may trigger node allocation.' }
    else { Write-Host 'All configured Batch nodes are ready.' }
    if ($Phase -eq 'Pool') { return [pscustomobject]@{ Ready=$true; Phase='Pool'; Smoke='Not tested' } }
    }
    $expected = @('StartWorkflow','MainOrchestrator','MonitorBatchTaskOrchestrator','ClassifyActivity',
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
    $statusUri = [Uri]$started.Data.statusQueryGetUri
    if ($statusUri.Scheme -ne 'https' -or $statusUri.Host -ne ([Uri]$Outputs.function_app_url).Host) {
        throw 'Workflow status URL must use HTTPS on this Function App host.'
    }
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
                    '--container-name',$Outputs.blob_container_name,'--name',"runs/poc-$requestId/final/$name.json",'--file',$file,'--overwrite','true','--auth-mode','login')
                return $true
            }
            $documents[$name] = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        }
        $aggregate=$documents.aggregate; $manifest=$documents.manifest
        if ($aggregate.requestId -ne $requestId -or $manifest.requestId -ne $requestId -or
            $aggregate.sum -ne 3 -or $manifest.sum -ne 3 -or $aggregate.taskCount -ne 3 -or $manifest.taskCount -ne 3 -or
            $manifest.status -ne 'Succeeded' -or $aggregate.jobId -ne $manifest.jobId -or
            $aggregate.fingerprint -ne $manifest.fingerprint -or @($aggregate.taskIds).Count -ne 3 -or @($manifest.taskUris).Count -ne 3) {
            throw 'Aggregate/manifest identity, count, sum, or success status is inconsistent.'
        }
        $expectedIds = @('task-00','task-01','task-02')
        if (($aggregate.taskIds -join ',') -ne ($expectedIds -join ',')) { throw 'Aggregate task IDs are inconsistent.' }
        $aggregateUri = [Uri]$manifest.aggregateUri
        $blobRoot = $Outputs.blob_endpoint.TrimEnd('/') + '/' + $Outputs.blob_container_name
        if ($aggregateUri.Scheme -ne 'https' -or $aggregateUri.AbsoluteUri -ne "$blobRoot/runs/poc-$requestId/final/aggregate.json") {
            throw 'Manifest aggregate URI is inconsistent.'
        }
        for ($index=0; $index -lt 3; $index++) {
            $taskUri = [Uri]$manifest.taskUris[$index]
            if ($taskUri.Scheme -ne 'https' -or
                $taskUri.AbsoluteUri -ne "$blobRoot/runs/poc-$requestId/tasks/$($expectedIds[$index])/result.json") {
                throw 'Manifest task URI is inconsistent.'
            }
        }
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
