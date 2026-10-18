[CmdletBinding()]
param(
    [string]$DeploymentResultPath,
    [string]$ExpectedFunctionAppName,
    [string]$ExpectedPoolId,
    [switch]$AllowPoolAutoscaleTransition,
    [switch]$DedicatedEnvironment,
    [ValidateRange(300,3600)][int]$TimeoutSeconds=1800,
    [string]$EvidenceDirectory
)
$autoscaleEntryArguments=@{}
foreach ($autoscaleArgumentName in @('DeploymentResultPath','ExpectedFunctionAppName','ExpectedPoolId','AllowPoolAutoscaleTransition','DedicatedEnvironment','TimeoutSeconds','EvidenceDirectory')) {
    $autoscaleEntryArguments[$autoscaleArgumentName]=Get-Variable -Name $autoscaleArgumentName -ValueOnly
}
if (-not (Get-Command Write-DeploymentJson -ErrorAction SilentlyContinue)) { . (Join-Path $PSScriptRoot 'Deployment.Common.ps1') }
if (-not (Get-Command Invoke-DeploymentValidation -ErrorAction SilentlyContinue)) { . (Join-Path $PSScriptRoot 'Test-Deployment.ps1') }
if (-not (Get-Command Invoke-PrivateVmOperation -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Invoke-PrivateVmOperation.ps1') -PathType Leaf)) { . (Join-Path $PSScriptRoot 'Invoke-PrivateVmOperation.ps1') }
foreach ($autoscaleArgumentName in $autoscaleEntryArguments.Keys) { Set-Variable -Name $autoscaleArgumentName -Value $autoscaleEntryArguments[$autoscaleArgumentName] }

function Get-PocAutoscaleFormula {
    param([ValidateSet('dedicated','spot')][string]$NodeType='spot',[ValidateRange(1,3)][int]$MaxNodes=1)
    $dedicated=if ($NodeType -ceq 'dedicated') { "`$tasks > 0 ? $MaxNodes : 0" } else { '0' }
    $spot=if ($NodeType -ceq 'spot') { "`$tasks > 0 ? $MaxNodes : 0" } else { '0' }
    return @(
        '$sampleSeconds = 180 * TimeInterval_Second;',
        '$samples = $PendingTasks.GetSamplePercent($sampleSeconds);',
        '$tasks = $samples < 70 ? 0 : max(0, max($PendingTasks.GetSample($sampleSeconds)));',
        "`$TargetDedicatedNodes = $dedicated;",
        "`$TargetLowPriorityNodes = $spot;",
        '$NodeDeallocationOption = taskcompletion;'
    ) -join "`n"
}

function Invoke-PocBatchPoolRest {
    param([ValidateSet('get','post')][string]$Method,[string]$Uri,[hashtable]$Body)
    $bodyFile=$null
    try {
        $arguments=@('rest','--method',$Method,'--url',$Uri,'--resource','https://batch.core.windows.net/')
        if ($Body) {
            $bodyFile=Join-Path ([IO.Path]::GetTempPath()) ('poc-batch-rest-'+[Guid]::NewGuid().ToString('N')+'.json')
            [IO.File]::WriteAllText($bodyFile,($Body|ConvertTo-Json -Compress -Depth 8),[Text.UTF8Encoding]::new($false))
            $arguments+=@('--headers','Content-Type=application/json; odata=minimalmetadata','--body',"@$bodyFile")
        }
        return Invoke-AzJson -Sensitive -Arguments $arguments
    } finally {
        if ($bodyFile) { Remove-Item -LiteralPath $bodyFile -Force -ErrorAction SilentlyContinue }
    }
}

function Wait-PocPoolSteady {
    param([string]$Endpoint,[string]$PoolId,[int]$TimeoutSeconds=600,[int]$PollSeconds=10)
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do {
        $pool=Invoke-PocBatchPoolRest -Method get -Uri "$Endpoint/pools/$PoolId`?api-version=2025-06-01"
        if ($pool.allocationState -eq 'steady') {
            return $pool
        }
        $remaining=$TimeoutSeconds-$watch.Elapsed.TotalSeconds
        if ($remaining -le 0) { break }
        Start-Sleep -Seconds ([Math]::Min($PollSeconds,[Math]::Ceiling($remaining)))
    } while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw 'Batch pool did not reach steady allocationState within timeout.'
}

function Wait-PocPoolZeroNodes {
    param([string]$Endpoint,[string]$PoolId,[int]$TimeoutSeconds=600,[int]$PollSeconds=10)
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do {
        $pool=Invoke-PocBatchPoolRest -Method get -Uri "$Endpoint/pools/$PoolId`?api-version=2025-06-01"
        $nodes=Invoke-PocBatchPoolRest -Method get -Uri "$Endpoint/pools/$PoolId/nodes?api-version=2025-06-01"
        $nodeList=@(if ($nodes -and $nodes.PSObject.Properties['value']) { $nodes.value } else { @() })
        if ($pool.allocationState -eq 'steady' -and [int]$pool.currentDedicatedNodes -eq 0 -and [int]$pool.currentLowPriorityNodes -eq 0 -and $nodeList.Count -eq 0) {
            return $pool
        }
        $remaining=$TimeoutSeconds-$watch.Elapsed.TotalSeconds
        if ($remaining -le 0) { break }
        Start-Sleep -Seconds ([Math]::Min($PollSeconds,[Math]::Ceiling($remaining)))
    } while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw 'Batch pool did not reach steady 0-node state within timeout.'
}

function Wait-PocAutoscaleIdleEvaluation {
    param([string]$Endpoint,[string]$PoolId,[string]$Formula,[int]$TimeoutSeconds=300,[int]$PollSeconds=10)
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do {
        $eval=Invoke-PocBatchPoolRest -Method post -Uri "$Endpoint/pools/$PoolId/evaluateautoscale?api-version=2025-06-01" -Body @{ autoScaleFormula=$Formula }
        $hasError=($eval.PSObject.Properties['error'] -and $null -ne $eval.error)
        $results=if ($eval.PSObject.Properties['results']) { [string]$eval.results } else { '' }
        if (-not $hasError -and $results -match '\$TargetDedicatedNodes=0\b' -and $results -match '\$TargetLowPriorityNodes=0\b') {
            return $eval
        }
        $remaining=$TimeoutSeconds-$watch.Elapsed.TotalSeconds
        if ($remaining -le 0) { break }
        Start-Sleep -Seconds ([Math]::Min($PollSeconds,[Math]::Ceiling($remaining)))
    } while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw 'Batch autoscale formula evaluation did not converge to 0 target nodes before enabling autoscale.'
}

function Invoke-PocAutoscaleValidation {
    param([string]$DeploymentResultPath,[string]$ExpectedFunctionAppName,[string]$ExpectedPoolId,
        [switch]$AllowPoolAutoscaleTransition,[switch]$DedicatedEnvironment,[int]$TimeoutSeconds=1800,[string]$EvidenceDirectory)
    $ErrorActionPreference='Stop'
    if (-not $AllowPoolAutoscaleTransition -or -not $DedicatedEnvironment -or -not $ExpectedFunctionAppName -or -not $ExpectedPoolId) {
        throw 'Explicit AllowPoolAutoscaleTransition, DedicatedEnvironment, ExpectedFunctionAppName and ExpectedPoolId are required.'
    }
    $deployment=Get-Content -LiteralPath $DeploymentResultPath -Raw | ConvertFrom-Json
    $outputs=ConvertTo-DeploymentHashtable $deployment.outputs
    if ($outputs.function_app_name -cne $ExpectedFunctionAppName -or $outputs.batch_pool_id -cne $ExpectedPoolId -or $outputs.network_mode -cne 'private-function-vm') {
        throw 'Autoscale validation target does not match the explicitly selected private Function and Batch Pool.'
    }
    if ($outputs.ContainsKey('batch_mode') -and $outputs.batch_mode -ceq 'existing') {
        throw 'Shared existing Batch pools must never be modified by the autoscale transition runner.'
    }
    $null=New-Item -ItemType Directory -Path $EvidenceDirectory -Force
    $script:AzPath=Resolve-DeploymentTool -Name az -RepositoryRoot (Split-Path $PSScriptRoot -Parent)
    $script:DeploymentSubscriptionId=$outputs.subscription_id
    $siteId='/subscriptions/'+$outputs.subscription_id+'/resourceGroups/'+$outputs.resource_group_name+'/providers/Microsoft.Web/sites/'+$outputs.function_app_name
    $app=Invoke-AzJson -Sensitive -Arguments @('rest','--method','get','--url',('https://management.azure.com'+$siteId+'?api-version=2024-11-01'))
    if ($app.id -ine $siteId -or $app.properties.state -ne 'Running' -or $app.properties.defaultHostName -ine ([Uri]$outputs.function_app_url).DnsSafeHost -or $app.properties.publicNetworkAccess -ne 'Disabled') {
        throw 'Autoscale validation requires a running, identity-matched private Function.'
    }
    $endpoint=$outputs.batch_endpoint.TrimEnd('/')
    $poolId=[Uri]::EscapeDataString($outputs.batch_pool_id)
    $nodeType=if ($outputs.ContainsKey('batch_node_type') -and $outputs.batch_node_type) { [string]$outputs.batch_node_type } else { 'spot' }
    $maxNodes=if ($outputs.ContainsKey('batch_node_count') -and $null -ne $outputs.batch_node_count -and [int]$outputs.batch_node_count -ge 1) { [int]$outputs.batch_node_count } else { 1 }
    $initialPool=Invoke-PocBatchPoolRest -Method get -Uri "$endpoint/pools/$poolId`?api-version=2025-06-01"
    $restoreDedicated=if ($nodeType -ceq 'dedicated') { $maxNodes } else { 0 }
    $restoreLowPriority=if ($nodeType -ceq 'spot') { $maxNodes } else { 0 }
    $zeroFormula='$TargetDedicatedNodes = 0; $TargetLowPriorityNodes = 0; $NodeDeallocationOption = taskcompletion;'
    $formula=Get-PocAutoscaleFormula -NodeType $nodeType -MaxNodes $maxNodes
    $poolMutated=$false
    $summary=@{
        schemaVersion=1
        validationKind='Azure'
        scenario='autoscaleZeroToOne'
        Status='Fail'
        Reason='AutoscaleValidationFailed'
        stage='Initial'
        ExitCode=1
        functionAppName=$outputs.function_app_name
        poolId=$outputs.batch_pool_id
        nodeType=$nodeType
        evaluationInterval='PT5M'
        initialEnableAutoScale=[bool]$initialPool.enableAutoScale
        zeroNodeSteadyObservedUtc=$null
        autoscaleEnabledObservedUtc=$null
        zeroNodesVerifiedBeforeSubmission=$false
        workflowExitCode=$null
        scaledUpObserved=$false
        postRunCurrentDedicatedNodes=$null
        postRunCurrentLowPriorityNodes=$null
        postRunTargetDedicatedNodes=$null
        postRunTargetLowPriorityNodes=$null
        restoredFixedScaleSucceeded=$false
        restoredPoolReadyVerified=$false
        completedUtc=$null
    }
    try {
        $poolMutated=$true
        $summary.stage='PrepareZeroNodes'
        if ($initialPool.enableAutoScale) {
            $null=Invoke-PocBatchPoolRest -Method post -Uri "$endpoint/pools/$poolId/disableautoscale?api-version=2025-06-01"
        }
        $steadyInitial=Wait-PocPoolSteady -Endpoint $endpoint -PoolId $poolId -TimeoutSeconds 300
        if ([int]$steadyInitial.targetDedicatedNodes -ne 0 -or [int]$steadyInitial.targetLowPriorityNodes -ne 0 -or [int]$steadyInitial.currentDedicatedNodes -ne 0 -or [int]$steadyInitial.currentLowPriorityNodes -ne 0) {
            $null=Invoke-PocBatchPoolRest -Method post -Uri "$endpoint/pools/$poolId/resize?api-version=2025-06-01" -Body @{
                targetDedicatedNodes=0
                targetLowPriorityNodes=0
                resizeTimeout='PT15M'
                nodeDeallocationOption='taskcompletion'
            }
        }
        $null=Wait-PocPoolZeroNodes -Endpoint $endpoint -PoolId $poolId -TimeoutSeconds ([Math]::Min(600,$TimeoutSeconds))
        $summary.zeroNodeSteadyObservedUtc=[DateTime]::UtcNow.ToString('o')
        $summary.stage='EnablePendingTasksAutoscale'
        $null=Invoke-PocBatchPoolRest -Method post -Uri "$endpoint/pools/$poolId/enableautoscale?api-version=2025-06-01" -Body @{
            autoScaleFormula=$formula
            autoScaleEvaluationInterval='PT5M'
        }
        $zeroAutoscalePool=Wait-PocPoolZeroNodes -Endpoint $endpoint -PoolId $poolId -TimeoutSeconds 180
        $summary.stage='EvaluatePendingTasksAutoscale'
        $null=Wait-PocAutoscaleIdleEvaluation -Endpoint $endpoint -PoolId $poolId -Formula $formula -TimeoutSeconds 300
        if (-not $zeroAutoscalePool.enableAutoScale -or [int]$zeroAutoscalePool.targetDedicatedNodes -ne 0 -or [int]$zeroAutoscalePool.targetLowPriorityNodes -ne 0) {
            throw 'Pool did not remain at 0 target nodes after enabling autoscale.'
        }
        $summary.autoscaleEnabledObservedUtc=[DateTime]::UtcNow.ToString('o')
        $summary.zeroNodesVerifiedBeforeSubmission=$true
        $summary.stage='SubmitSingleWorkflowOnZeroNodePool'
        $vmResult=Invoke-PrivateVmOperation -Outputs $outputs -Operation Validate -Scenarios @('single') -EvidenceDirectory (Join-Path $EvidenceDirectory 'workflow') -TimeoutSeconds $TimeoutSeconds
        $summary.workflowExitCode=[int]$vmResult.exit_code
        if ($vmResult.exit_code -ne 0) { throw 'Single workflow validation on 0-node autoscale pool did not pass.' }
        $summary.stage='VerifyAutoscaleExpansion'
        $postPool=Invoke-PocBatchPoolRest -Method get -Uri "$endpoint/pools/$poolId`?api-version=2025-06-01"
        $summary.postRunCurrentDedicatedNodes=[int]$postPool.currentDedicatedNodes
        $summary.postRunCurrentLowPriorityNodes=[int]$postPool.currentLowPriorityNodes
        $summary.postRunTargetDedicatedNodes=[int]$postPool.targetDedicatedNodes
        $summary.postRunTargetLowPriorityNodes=[int]$postPool.targetLowPriorityNodes
        $expectedScaled=$(if ($nodeType -ceq 'spot') { [int]$postPool.targetLowPriorityNodes -ge 1 -or [int]$postPool.currentLowPriorityNodes -ge 1 } else { [int]$postPool.targetDedicatedNodes -ge 1 -or [int]$postPool.currentDedicatedNodes -ge 1 })
        if (-not $postPool.enableAutoScale -or -not $expectedScaled) {
            throw 'Pool did not observe autoscale expansion after zero-node submission.'
        }
        $summary.scaledUpObserved=$true
        $summary.stage='Completed'
        $summary.Status='Pass'
        $summary.Reason='ZeroNodeAutoscaleAllocatedAndCompletedTask'
        $summary.ExitCode=0
    } finally {
        try {
            if ($poolMutated) {
                $currentPool=Invoke-PocBatchPoolRest -Method get -Uri "$endpoint/pools/$poolId`?api-version=2025-06-01"
                if ($currentPool.enableAutoScale) {
                    $null=Invoke-PocBatchPoolRest -Method post -Uri "$endpoint/pools/$poolId/disableautoscale?api-version=2025-06-01"
                }
                $afterDisable=Wait-PocPoolSteady -Endpoint $endpoint -PoolId $poolId -TimeoutSeconds 600
                if ([int]$afterDisable.targetDedicatedNodes -ne $restoreDedicated -or [int]$afterDisable.targetLowPriorityNodes -ne $restoreLowPriority -or [int]$afterDisable.currentDedicatedNodes -ne $restoreDedicated -or [int]$afterDisable.currentLowPriorityNodes -ne $restoreLowPriority) {
                    $null=Invoke-PocBatchPoolRest -Method post -Uri "$endpoint/pools/$poolId/resize?api-version=2025-06-01" -Body @{
                        targetDedicatedNodes=$restoreDedicated
                        targetLowPriorityNodes=$restoreLowPriority
                        resizeTimeout='PT15M'
                        nodeDeallocationOption='taskcompletion'
                    }
                }
                $summary.restoredFixedScaleSucceeded=$true
                $readiness=Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds 600 -PollSeconds 10
                $summary.restoredPoolReadyVerified=[bool]$readiness.Ready
                if (-not $summary.restoredPoolReadyVerified) {
                    throw 'Restored Batch pool failed readiness verification.'
                }
            }
        } catch {
            $summary.Status='Fail'
            $summary.Reason='PoolRestorationFailed'
            $summary.ExitCode=1
            throw
        } finally {
            $summary.completedUtc=[DateTime]::UtcNow.ToString('o')
            Write-DeploymentJson (Join-Path $EvidenceDirectory 'autoscale-result.json') $summary
            [IO.File]::WriteAllLines((Join-Path $EvidenceDirectory 'summary.txt'),[string[]]@("Autoscale V09: $($summary.Status) ($($summary.Reason))","Zero nodes verified before submission: $($summary.zeroNodesVerifiedBeforeSubmission)","Scaled up observed: $($summary.scaledUpObserved)","Restored fixed scale: $($summary.restoredFixedScaleSucceeded) (ready=$($summary.restoredPoolReadyVerified))"),[Text.UTF8Encoding]::new($false))
        }
    }
    return [pscustomobject]$summary
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        if (-not $EvidenceDirectory) { $EvidenceDirectory=Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/autoscale/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-ffff')) }
        $result=Invoke-PocAutoscaleValidation -DeploymentResultPath $DeploymentResultPath -ExpectedFunctionAppName $ExpectedFunctionAppName -ExpectedPoolId $ExpectedPoolId -AllowPoolAutoscaleTransition:$AllowPoolAutoscaleTransition -DedicatedEnvironment:$DedicatedEnvironment -TimeoutSeconds $TimeoutSeconds -EvidenceDirectory $EvidenceDirectory
        Write-Host "Zero-node autoscale validation completed. Sanitized evidence: $EvidenceDirectory"
        exit ([int]$result.ExitCode)
    } catch { Write-Error 'Autoscale validation failed; raw output suppressed. Inspect sanitized evidence and verify Batch pool state.'; exit 1 }
}
