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
        $state=if ($script:scenario -eq 'neverready' -or ($script:scenario -eq 'retry' -and $script:poolReads -lt 2)) { 'creating' } else { 'idle' }
        $nodes=@([pscustomobject]@{ state=$state; schedulingState='enabled' })
        if ($script:scenario -eq 'allnodes' -and $script:poolReads -ge 2) { $nodes+= [pscustomobject]@{ state='idle'; schedulingState='enabled' } }
        return [pscustomobject]@{ value=$nodes }
    }
    if ($command -match '^functionapp function list ') {
        $names=@('StartWorkflow','MainOrchestrator','MonitorBatchTaskOrchestrator','ClassifyActivity','EnsureBatchJobActivity',
            'EnsureBatchTaskActivity','GetBatchTaskStatusActivity','FetchAndPersistActivity','AggregateActivity','PublishManifestActivity')
        if ($script:scenario -eq 'missingfunction') { $names=$names[0..8] }
        return @($names | ForEach-Object { [pscustomobject]@{ name="poc-app/$_" } })
    }
    if ($command -match '^functionapp function keys list ') {
        Assert-True $Sensitive 'Function key retrieval must be sensitive'
        return [pscustomobject]@{ default='DO-NOT-LOG-FUNCTION-KEY' }
    }
    if ($command -match '^storage blob download ') {
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
    $state=if ($script:scenario -eq 'workflowfailed') { 'Failed' } else { 'Completed' }
    return @{ StatusCode=200; Data=[pscustomobject]@{ runtimeStatus=$state; output=[pscustomobject]@{ state='Succeeded' } } }
}
$outputs=@{ subscription_id='subscription'; resource_group_name='poc-rg'; function_app_name='poc-app'; function_app_url='https://poc.example';
    storage_account_name='pocstorage'; blob_container_name='poc-results'; blob_endpoint='https://pocstorage.blob.core.windows.net/'; batch_endpoint='https://batch.example';
    batch_pool_id='pool'; batch_account_name='account'; batch_resource_group_name='batch-rg' }

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
$outputs.Remove('batch_node_count')
$script:scenario='resize'
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 1 -PollSeconds 1 } 'AllocationFailed'
$script:scenario='permission'
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 1 -PollSeconds 1 } 'permission'
$script:scenario='neverready'
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 1 -PollSeconds 1 } 'timed out'
$script:scenario='missingfunction'
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Functions -TimeoutSeconds 1 -PollSeconds 1 } 'indexing timed out'
$script:scenario='workflowfailed'
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Functions -Smoke -TimeoutSeconds 1 -PollSeconds 1 } 'runtimeStatus Failed'
$script:scenario='corrupt'
Assert-Fails { Invoke-DeploymentValidation -Outputs $outputs -Phase Functions -Smoke -TimeoutSeconds 1 -PollSeconds 1 } 'inconsistent'

# Check the real HTTP wrapper's redaction independently of the HTTP fake used above.
. (Join-Path $PSScriptRoot '../Test-Deployment.ps1')
function Invoke-WebRequest { throw 'Failed URL https://poc.example/status?code=DO-NOT-LOG-STATUS-KEY' }
try { Invoke-ValidationHttp -Uri 'https://poc.example/status?code=DO-NOT-LOG-STATUS-KEY'; throw 'Expected HTTP failure' }
catch {
    Assert-True ($_.Exception.Message -match 'suppressed' -and $_.Exception.Message -notmatch 'DO-NOT-LOG') 'HTTP error must suppress secrets'
}
Write-Host 'Deployment validation offline tests: PASS (11 scenarios; no Azure calls).'
