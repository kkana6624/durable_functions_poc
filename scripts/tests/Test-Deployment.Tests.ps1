$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../Test-Deployment.ps1')

$script:scenario='success'
$script:poolReads=0
$script:httpReads=0
$script:requestId=''
$script:calls=[Collections.Generic.List[string]]::new()
function Assert-True([bool]$Value, [string]$Message) { if (-not $Value) { throw "ASSERT: $Message" } }
function Assert-Fails([scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null } catch {
        Assert-True ($_.Exception.Message -match $Expected) "Expected error matching $Expected; got $($_.Exception.Message)"
        return
    }
    throw "ASSERT: expected failure $Expected"
}
function Invoke-AzJson {
    param([string[]]$Arguments, [switch]$Sensitive)
    $command=$Arguments -join ' '
    $script:calls.Add($command)
    if ($command -match '^rest ' -and $command -notmatch '/nodes\?') {
        $script:poolReads++
        if ($script:scenario -eq 'permission') { throw 'AuthorizationPermissionMismatch: Batch data-plane read permission required.' }
        if ($script:scenario -eq 'rbacretry' -and $script:poolReads -lt 2) { throw 'AuthorizationPermissionMismatch: transient RBAC propagation' }
        if ($script:scenario -eq 'resize') { return [pscustomobject]@{ resizeErrors=@([pscustomobject]@{ code='AllocationFailed' }) } }
        return [pscustomobject]@{ allocationState='steady' }
    }
    if ($command -match '^rest .*?/nodes\?') {
        if ($script:scenario -in @('spotrecovery','spotonlydedicated','dedicatedonlyspot','missingcapacity')) {
            $nodes=@([pscustomobject]@{ state='idle'; schedulingState='enabled'; isDedicated=$true })
            if ($script:scenario -eq 'spotrecovery') {
                $nodes+= [pscustomobject]@{ state='preempted'; schedulingState='enabled'; isDedicated=$false }
                $nodes+= [pscustomobject]@{ state='unusable'; schedulingState='enabled'; isDedicated=$false }
                $nodes+= [pscustomobject]@{ state='idle'; schedulingState='disabled'; isDedicated=$false }
                if ($script:poolReads -ge 2) { $nodes+= [pscustomobject]@{ state='running'; schedulingState='enabled'; isDedicated=$false } }
            }
            if ($script:scenario -eq 'dedicatedonlyspot') { $nodes[0].isDedicated=$false }
            if ($script:scenario -eq 'missingcapacity') { $nodes=@([pscustomobject]@{ state='idle'; schedulingState='enabled' }) }
            return [pscustomobject]@{ value=$nodes }
        }
        if ($script:scenario -eq 'paging') {
            $page=[pscustomobject]@{ value=@([pscustomobject]@{ state='idle'; schedulingState='enabled' }) }
            if ($command -notmatch 'skiptoken') { $page | Add-Member -NotePropertyName 'odata.nextLink' -NotePropertyValue 'https://batch.example/pools/pool/nodes?api-version=2025-06-01&skiptoken=next' }
            return $page
        }
        $state=if ($script:scenario -eq 'neverready' -or ($script:scenario -eq 'retry' -and $script:poolReads -lt 2)) { 'creating' } else { 'idle' }
        $nodes=@([pscustomobject]@{ state=$state; schedulingState='enabled' })
        if ($script:scenario -eq 'allnodes' -and $script:poolReads -ge 2) { $nodes+= [pscustomobject]@{ state='idle'; schedulingState='enabled' } }
        return [pscustomobject]@{ value=$nodes }
    }
    if ($command -match '^functionapp function list ') {
        $names=@('StartWorkflow','MainOrchestrator','MonitorBatchTaskOrchestrator','ClassifyActivity','EnsureBatchJobActivity',
            'EnsureBatchTaskActivity','GetBatchTaskStatusActivity','FetchAndPersistActivity','AggregateActivity','PublishManifestActivity',
            'WorkflowAdmissionEntity','TerminateBatchJobActivity')
        if ($script:scenario -eq 'missingfunction') { $names=$names[0..8] }
        return @($names | ForEach-Object { [pscustomobject]@{ name="poc-app/$_" } })
    }
    if ($command -match '^functionapp function keys list ') {
        Assert-True $Sensitive 'Function key retrieval must be sensitive'
        return [pscustomobject]@{ default='DO-NOT-LOG-FUNCTION-KEY' }
    }
    if ($command -match '^storage blob download ') {
        Assert-True ('--no-progress' -in $Arguments) 'Blob transfer progress must not contaminate Azure CLI JSON'
        Assert-True ($command -match '--auth-mode login') 'Blob read must use Entra authentication'
        $file=$Arguments[[Array]::IndexOf($Arguments,'--file')+1]
        $sum=if ($script:scenario -eq 'corrupt') { 99 } else { 3 }
        $document=@{ requestId=$script:requestId; sum=$sum; taskCount=3; jobId='poc-job'; fingerprint='fingerprint' }
        if ([IO.Path]::GetFileName($file) -eq 'manifest.json') {
            $document.status='Succeeded'
            $document.aggregateUri="https://pocstorage.blob.core.windows.net/poc-results/runs/poc-$script:requestId/final/aggregate.json"
            $document.taskUris=@('task-00','task-01','task-02' | ForEach-Object { "https://pocstorage.blob.core.windows.net/poc-results/runs/poc-$script:requestId/tasks/$_/result.json" })
        } else { $document.taskIds=@('task-00','task-01','task-02') }
        $document | ConvertTo-Json | Set-Content -LiteralPath $file -Encoding UTF8
        return [pscustomobject]@{}
    }
    throw "Unexpected fake CLI command: $command"
}
function Invoke-ValidationHttp {
    param([string]$Uri,[string]$Method='Get',[hashtable]$Headers=@{},[string]$Body)
    if ($Method -eq 'Post') {
        $script:requestId=($Body | ConvertFrom-Json).requestId
        Assert-True ($Headers['x-functions-key'] -eq 'DO-NOT-LOG-FUNCTION-KEY') 'Key should use header'
        return @{ StatusCode=202; Data=[pscustomobject]@{ instanceId='poc-instance'; statusQueryGetUri='https://poc.example/status?code=DO-NOT-LOG-STATUS-KEY' } }
    }
    $script:httpReads++
    if ($script:scenario -eq 'workflowpending' -and $script:httpReads -eq 1) { return @{ StatusCode=404; Data=$null } }
    $state=if ($script:scenario -eq 'workflowfailed') { 'Failed' } else { 'Completed' }
    return @{ StatusCode=200; Data=[pscustomobject]@{ runtimeStatus=$state; output=[pscustomobject]@{ state='Succeeded' } } }
}
$outputs=@{ subscription_id='subscription'; resource_group_name='poc-rg'; function_app_name='poc-app'; function_app_url='https://poc.example';
    storage_account_name='pocstorage'; blob_container_name='poc-results'; blob_endpoint='https://pocstorage.blob.core.windows.net/'; batch_endpoint='https://batch.example';
    batch_pool_id='pool'; batch_account_name='account'; batch_resource_group_name='batch-rg' }
$outputs.batch_node_count=1

$result=Invoke-DeploymentValidation -Outputs $outputs -Phase All -Smoke -TimeoutSeconds 2 -PollSeconds 1
Assert-True ($result.Smoke -eq 'Pass' -and $result.Sum -eq 3) 'Full smoke should pass'
Assert-True (@($script:calls | Where-Object { $_ -match '--resource https://batch.core.windows.net/' }).Count -eq 2) 'Batch reads must use correct audience'
Assert-True (@($script:calls | Where-Object { $_ -match '(delete|resize|reboot|account login)' }).Count -eq 0) 'Validation must not mutate pool or local Batch login'
$script:scenario='retry'; $script:poolReads=0
$result=Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 3 -PollSeconds 1
Assert-True ($script:poolReads -eq 2 -and $result.Phase -eq 'Pool') 'Readiness should retry transient allocation'
$script:scenario='rbacretry'; $script:poolReads=0
$result=Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 3 -PollSeconds 1
Assert-True ($script:poolReads -eq 2 -and $result.Ready) 'Readiness should retry transient RBAC propagation'
$script:scenario='allnodes'; $script:poolReads=0
$outputs.batch_node_count=2
$result=Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 3 -PollSeconds 1
Assert-True ($script:poolReads -eq 2 -and $result.Ready) 'New Pool should wait for every configured node'
$script:scenario='paging'; $script:poolReads=0
$result=Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 2 -PollSeconds 1
Assert-True ($result.Ready -and $script:poolReads -eq 1) 'Readiness must count nodes across continuation pages'
$outputs.batch_node_count=1
$script:scenario='resize'
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 1 -PollSeconds 1 } 'AllocationFailed'
$script:scenario='permission'
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 1 -PollSeconds 1 } 'permission'
$script:scenario='neverready'
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 1 -PollSeconds 1 } 'timed out'
$outputs.batch_mode='new'; $outputs.batch_node_type='spot'
$script:scenario='spotrecovery'; $script:poolReads=0
$result=Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 3 -PollSeconds 1
Assert-True ($script:poolReads -eq 2 -and $result.Ready -and $result.BatchNodeType -eq 'spot') 'Spot readiness must ignore dedicated/preempted/unusable/disabled nodes until a runnable Spot node appears'
$script:scenario='spotonlydedicated'
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 1 -PollSeconds 1 } 'Spot Batch node readiness.*capacity is not guaranteed.*timed out'
$outputs.batch_node_type='dedicated'
$result=Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 1 -PollSeconds 1
Assert-True ($result.Ready -and $result.BatchNodeType -eq 'dedicated') 'Typed dedicated metadata must accept runnable dedicated nodes'
$outputs.batch_node_type='dedicated'; $script:scenario='dedicatedonlyspot'
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 1 -PollSeconds 1 } 'timed out'
$outputs.batch_node_type='spot'; $script:scenario='missingcapacity'; $script:poolReads=0
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 1 -PollSeconds 1 } 'capacity type is missing'
Assert-True ($script:poolReads -eq 1) 'Missing capacity metadata must fail immediately instead of counting an unknown node'
$outputs.batch_node_type='invalid'; $script:calls.Clear()
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 1 -PollSeconds 1 } 'batch_node_type'
Assert-True ($script:calls.Count -eq 0) 'Invalid capacity metadata must fail before Azure calls'
$outputs.Remove('batch_node_type'); $outputs.Remove('batch_mode')
$outputs.Remove('batch_node_count')
$script:calls.Clear()
$result=Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 1 -PollSeconds 1
Assert-True ($result.Ready -and @($script:calls | Where-Object { $_ -match '/nodes\?' }).Count -eq 0) 'Existing zero-node pool must pass accessibility without waiting for allocation'
$outputs.batch_mode='existing'; $outputs.batch_node_count=$null; $outputs.batch_node_type=$null
$outputs.batch_dedicated_node_count=$null; $outputs.batch_spot_node_count=$null
$script:calls.Clear()
$result=Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 1 -PollSeconds 1
Assert-True ($result.Ready -and $null -eq $result.BatchNodeType -and @($script:calls | Where-Object { $_ -match '/nodes\?' }).Count -eq 0) 'New reference-only null metadata must preserve existing zero-node pool access checks'
$script:scenario='permission'
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 1 -PollSeconds 1 } 'permission'
$script:scenario='missingfunction'
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Functions -TimeoutSeconds 1 -PollSeconds 1 } 'indexing timed out'
$script:scenario='workflowfailed'
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Functions -Smoke -TimeoutSeconds 1 -PollSeconds 1 } 'runtimeStatus Failed'
$script:scenario='workflowpending'; $script:httpReads=0
$result=Invoke-DeploymentValidation -Outputs $outputs -Phase Functions -Smoke -TimeoutSeconds 3 -PollSeconds 1
Assert-True ($result.Smoke -eq 'Pass' -and $script:httpReads -eq 2) 'Status polling must wait through instance creation HTTP 404 after entity admission'
$script:scenario='corrupt'
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Functions -Smoke -TimeoutSeconds 1 -PollSeconds 1 } 'inconsistent'

# Check the real HTTP wrapper's redaction independently of the HTTP fake used above.
. (Join-Path $PSScriptRoot '../Test-Deployment.ps1')
function Invoke-WebRequest { throw 'Failed URL https://poc.example/status?code=DO-NOT-LOG-STATUS-KEY' }
try { Invoke-ValidationHttp -Uri 'https://poc.example/status?code=DO-NOT-LOG-STATUS-KEY'; throw 'Expected HTTP failure' }
catch {
    Assert-True ($_.Exception.Message -match 'suppressed' -and $_.Exception.Message -notmatch 'DO-NOT-LOG') 'HTTP error must suppress secrets'
}
$script:httpStatus=404
function Invoke-WebRequest {
    $error=[InvalidOperationException]::new('Failed secret-bearing URL')
    $error | Add-Member -NotePropertyName Response -NotePropertyValue ([pscustomobject]@{ StatusCode=$script:httpStatus })
    throw $error
}
$pending=Invoke-ValidationHttp -Uri 'https://poc.example/status?code=DO-NOT-LOG'
Assert-True ($pending.StatusCode -eq 404 -and $null -eq $pending.Data) 'HTTP wrapper must expose pending instance without reading a missing JSON body'
foreach ($status in @(401,403)) {
    $script:httpStatus=$status
    try { Invoke-ValidationHttp -Uri 'https://poc.example/status?code=DO-NOT-LOG'; throw 'Expected authorization failure' }
    catch {
        Assert-True ($_.Exception.Data['PermanentValidation'] -and $_.Exception.Message -notmatch 'DO-NOT-LOG') 'HTTP authorization failures must stop retries and suppress credentials'
    }
}
Write-Host 'Deployment validation offline tests: PASS (node pagination, Spot/dedicated capacity types, preempted-node exclusion, existing zero-node pools, readiness, smoke, redaction; no Azure calls).'
