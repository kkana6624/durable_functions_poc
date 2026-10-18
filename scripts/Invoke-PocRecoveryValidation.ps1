[CmdletBinding()]
param(
    [string]$DeploymentResultPath,
    [string]$ExpectedFunctionAppName,
    [ValidateSet('Restart','HostStoppedBatchWallClock')][string]$ValidationMode='Restart',
    [switch]$AllowFunctionRestart,
    [switch]$AllowFunctionStop,
    [switch]$DedicatedEnvironment,
    [ValidateRange(120,600)][int]$DelaySeconds=300,
    [ValidateRange(300,3600)][int]$TimeoutSeconds=900,
    [string]$EvidenceDirectory
)
$recoveryEntryArguments=@{}
# Dependency scripts have param blocks; preserve this entry point's arguments.
foreach ($recoveryArgumentName in @('DeploymentResultPath','ExpectedFunctionAppName','ValidationMode','AllowFunctionRestart','AllowFunctionStop','DedicatedEnvironment','DelaySeconds','TimeoutSeconds','EvidenceDirectory')) { $recoveryEntryArguments[$recoveryArgumentName]=Get-Variable -Name $recoveryArgumentName -ValueOnly }
if (-not (Get-Command Invoke-PocValidation -ErrorAction SilentlyContinue)) { . (Join-Path $PSScriptRoot 'Invoke-PocValidation.ps1') }
if (-not (Get-Command Invoke-PrivateVmOperation -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Invoke-PrivateVmOperation.ps1') -PathType Leaf)) { . (Join-Path $PSScriptRoot 'Invoke-PrivateVmOperation.ps1') }
. (Join-Path $PSScriptRoot 'Recovery.Context.ps1')
foreach ($recoveryArgumentName in $recoveryEntryArguments.Keys) { Set-Variable -Name $recoveryArgumentName -Value $recoveryEntryArguments[$recoveryArgumentName] }

function Get-PocBatchWallClockConstraintSeconds {
    param($Job)
    $constraintSeconds=$null
    try {
        if ($Job -and $Job.PSObject.Properties['constraints'] -and $Job.constraints -and $Job.constraints.PSObject.Properties['maxWallClockTime']) {
            $constraintSeconds=[int]([Xml.XmlConvert]::ToTimeSpan([string]$Job.constraints.maxWallClockTime).TotalSeconds)
        }
    } catch { Stop-PocValidation 'InvalidBatchWallClockConstraint' }
    if ($null -eq $constraintSeconds -or $constraintSeconds -lt 60 -or $constraintSeconds -gt 3600) { Stop-PocValidation 'BatchWallClockConstraintMismatch' }
    return $constraintSeconds
}

function Invoke-PocRecoveryGuest {
    param([hashtable]$Outputs,[ValidateSet('Start','Complete')][string]$Phase,
        [hashtable]$Context,[string]$EvidenceDirectory,[int]$TimeoutSeconds=900)
    $ErrorActionPreference='Stop'
    $null=New-Item -ItemType Directory -Path $EvidenceDirectory -Force
    $summary=@{ schemaVersion=1; validationKind='Azure'; phase=$Phase; Status='Fail'; Reason='RecoveryValidationFailed'; ExitCode=1;
        recoveryContext=$null; completedUtc=$null; entityCheckpointInterruptionObserved=$false }
    try {
        $Context=Assert-PocRecoveryContext -Phase $Phase -Context $Context
        $mode=if ($Context.ContainsKey('mode')) { [string]$Context.mode } else { 'Restart' }
        if ($Phase -eq 'Start') {
            $settings=Get-PocWorkflowSettings $Outputs
            $wallClockSeconds=$null
            if ($mode -ceq 'HostStoppedBatchWallClock') {
                if ($settings.TimeoutMinutes -lt 1 -or $settings.TimeoutMinutes -ge 60) { Stop-PocValidation 'RecoveryWorkflowTimeoutTooShort' }
                $wallClockSeconds=[int]($settings.TimeoutMinutes*60)
                $delay=[Math]::Min(3600,$wallClockSeconds+180)
            } else {
                $delay=[int]$Context.delaySeconds
                if ($delay -lt 120 -or $delay -gt 600) { Stop-PocValidation 'RecoveryDelayOutOfRange' }
                if ($settings.TimeoutMinutes*60 -le $delay+120) { Stop-PocValidation 'RecoveryWorkflowTimeoutTooShort' }
            }
            $request=@{ requestId='recovery-'+[Guid]::NewGuid().ToString('N'); classification='light'; taskCount=1; failTaskIndex=$null; lightDelaySeconds=$delay; heavyDelaySeconds=3 }
            $instance=Get-PocIdentity $request.requestId
            # Fail before submitting work when the host-key permission is absent.
            $statusUri=Get-PocRecoveryStatusUri -Outputs $Outputs -InstanceId $instance
            $key=Get-PocValidationKey $Outputs
            try { $accepted=Invoke-PocValidationHttp -Uri ($Outputs.function_app_url.TrimEnd('/')+'/api/workflows') -Method Post -Headers @{'x-functions-key'=$key} -Body ($request|ConvertTo-Json -Compress) }
            finally { $key=$null }
            $statusUri=Assert-PocAccepted -Response $accepted -Outputs $Outputs -InstanceId $instance -RequestId $request.requestId
            $watch=[Diagnostics.Stopwatch]::StartNew()
            do {
                $batch=$null
                # The Job may not exist yet. Raw CLI errors remain suppressed.
                try { $batch=Get-PocBatchObservation -Outputs $Outputs -JobId $instance } catch { $batch=$null }
                if ($batch -and $batch.Job.id -eq $instance -and $batch.Job.poolInfo.poolId -eq $Outputs.batch_pool_id -and @($batch.Tasks).Count -eq 1 -and $batch.Tasks[0].id -eq 'task-00' -and $batch.Tasks[0].state -eq 'running') { break }
                Start-Sleep -Seconds 5
            } while ($watch.Elapsed.TotalSeconds -lt [Math]::Min($TimeoutSeconds,600))
            if (-not $batch -or $batch.Job.id -ne $instance -or $batch.Job.poolInfo.poolId -ne $Outputs.batch_pool_id -or @($batch.Tasks).Count -ne 1 -or $batch.Tasks[0].id -cne 'task-00' -or $batch.Tasks[0].state -ne 'running') { Stop-PocValidation 'RecoveryRunningTaskNotObserved' }
            $status=Invoke-PocValidationHttp -Uri $statusUri
            if ($status.StatusCode -notin @(200,202) -or $status.Data.runtimeStatus -ne 'Running' -or -not $status.Data.createdTime -or -not $batch.Tasks[0].creationTime -or -not $batch.Tasks[0].executionInfo.startTime) { Stop-PocValidation 'RecoveryRunningInstanceNotObserved' }
            if ($mode -ceq 'HostStoppedBatchWallClock') {
                $constraintSeconds=Get-PocBatchWallClockConstraintSeconds $batch.Job
                if ($constraintSeconds -ne $wallClockSeconds -or -not $batch.Job.PSObject.Properties['creationTime'] -or -not $batch.Job.creationTime) { Stop-PocValidation 'BatchWallClockConstraintMismatch' }
                $summary.recoveryContext=Assert-PocRecoveryContext -Phase Complete -Context @{ mode='HostStoppedBatchWallClock'; request=$request; instanceId=$instance; createdTime=(Get-PocRecoveryTimestamp $status.Data.createdTime); jobCreationTime=(Get-PocRecoveryTimestamp $batch.Job.creationTime); batchMaxWallClockSeconds=$constraintSeconds; taskCreationTime=(Get-PocRecoveryTimestamp $batch.Tasks[0].creationTime); runningObservedUtc=[DateTime]::UtcNow.ToString('o'); taskStartedUtc=(Get-PocRecoveryTimestamp $batch.Tasks[0].executionInfo.startTime) }
                $summary.Reason='BatchTaskRunningBeforeHostStop'
            } else {
                $summary.recoveryContext=Assert-PocRecoveryContext -Phase Complete -Context @{ request=$request; instanceId=$instance; createdTime=(Get-PocRecoveryTimestamp $status.Data.createdTime); taskCreationTime=(Get-PocRecoveryTimestamp $batch.Tasks[0].creationTime); runningObservedUtc=[DateTime]::UtcNow.ToString('o'); taskStartedUtc=(Get-PocRecoveryTimestamp $batch.Tasks[0].executionInfo.startTime) }
                $summary.Reason='BatchTaskRunningBeforeRestart'
            }
        } elseif ($mode -ceq 'HostStoppedBatchWallClock') {
            $request=$Context.request
            $instance=[string]$Context.instanceId
            $siteId='/subscriptions/'+$Outputs.subscription_id+'/resourceGroups/'+$Outputs.resource_group_name+'/providers/Microsoft.Web/sites/'+$Outputs.function_app_name
            $appBefore=Invoke-AzJson -Sensitive -Arguments @('rest','--method','get','--url',('https://management.azure.com'+$siteId+'?api-version=2024-11-01'))
            if ($appBefore.id -ine $siteId -or $appBefore.properties.state -ne 'Stopped') { Stop-PocValidation 'FunctionHostNotStoppedDuringSafetyNetCheck' }
            $watch=[Diagnostics.Stopwatch]::StartNew()
            $batch=$null
            do {
                $batch=Get-PocBatchObservation -Outputs $Outputs -JobId $instance
                if ($batch -and $batch.Job -and $batch.Job.state -eq 'completed') { break }
                $remaining=$TimeoutSeconds-$watch.Elapsed.TotalSeconds
                if ($remaining -le 0) { break }
                Start-Sleep -Seconds ([Math]::Min(10,[Math]::Ceiling($remaining)))
            } while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds)
            if (-not $batch -or -not $batch.Job -or $batch.Job.state -ne 'completed') { Stop-PocValidation 'BatchWallClockTerminationTimedOut' }
            $appAfter=Invoke-AzJson -Sensitive -Arguments @('rest','--method','get','--url',('https://management.azure.com'+$siteId+'?api-version=2024-11-01'))
            if ($appAfter.id -ine $siteId -or $appAfter.properties.state -ne 'Stopped') { Stop-PocValidation 'FunctionHostNotStoppedAtExpiryObservation' }
            if ($batch.Job.id -ne $instance -or $batch.Job.poolInfo.poolId -ne $Outputs.batch_pool_id -or -not $batch.Job.PSObject.Properties['creationTime'] -or (Get-PocRecoveryTimestamp $batch.Job.creationTime) -ne $Context.jobCreationTime) { Stop-PocValidation 'RecoveryJobIdentityChanged' }
            $constraintSeconds=Get-PocBatchWallClockConstraintSeconds $batch.Job
            if ($constraintSeconds -ne $Context.batchMaxWallClockSeconds) { Stop-PocValidation 'BatchWallClockConstraintMismatch' }
            if (-not $batch.Job.PSObject.Properties['executionInfo'] -or -not $batch.Job.executionInfo -or -not $batch.Job.executionInfo.PSObject.Properties['terminateReason'] -or [string]$batch.Job.executionInfo.terminateReason -cne 'MaxWallClockTimeExpiry') { Stop-PocValidation 'BatchWallClockExpiryReasonMissing' }
            if (@($batch.Tasks).Count -ne 1 -or $batch.Tasks[0].id -cne 'task-00' -or (Get-PocRecoveryTimestamp $batch.Tasks[0].creationTime) -ne $Context.taskCreationTime -or $batch.Tasks[0].state -ne 'completed') { Stop-PocValidation 'RecoveryTaskNotTerminatedByWallClock' }
            $fingerprint=Get-PocFingerprint -Request $request -Classification light
            $metadata=@($batch.Job.metadata | Where-Object { $_.name -ceq 'poc-fingerprint' })
            if ($metadata.Count -ne 1 -or $metadata[0].value -cne $fingerprint) { Stop-PocValidation 'RecoveryBatchFingerprintMismatch' }
            $root='runs/poc-'+$request.requestId
            if (Test-PocBlobExists -Outputs $Outputs -Path "$root/final/manifest.json") { Stop-PocValidation 'UnexpectedSuccessManifest' }
            $summary.recoveryContext=@{
                mode='HostStoppedBatchWallClock'
                instanceId=$instance
                createdTime=$Context.createdTime
                jobCreationTime=(Get-PocRecoveryTimestamp $batch.Job.creationTime)
                batchMaxWallClockSeconds=$constraintSeconds
                batchJobState='completed'
                batchTerminationReason='MaxWallClockTimeExpiry'
                taskCreationTime=(Get-PocRecoveryTimestamp $batch.Tasks[0].creationTime)
                taskState='completed'
                functionStateWhileObserved='Stopped'
                manifestPublished=$false
            }
            $summary.Reason='BatchMaxWallClockTerminatedWhileFunctionStopped'
        } else {
            $request=$Context.request
            if ($request.requestId -notmatch '^recovery-[0-9a-f]{32}$' -or $Context.instanceId -cne (Get-PocIdentity $request.requestId) -or $request.classification -cne 'light' -or $request.taskCount -ne 1 -or -not $Context.createdTime -or -not $Context.taskCreationTime) { Stop-PocValidation 'InvalidRecoveryContext' }
            $instance=[string]$Context.instanceId
            $statusUri=Get-PocRecoveryStatusUri -Outputs $Outputs -InstanceId $instance
            $status=Wait-PocWorkflow -StatusUri $statusUri -TimeoutSeconds $TimeoutSeconds -PollSeconds 5
            if ($status.runtimeStatus -ne 'Completed' -or (Get-PocRecoveryTimestamp $status.createdTime) -ne $Context.createdTime -or $status.output.state -ne 'Succeeded' -or $status.output.requestId -ne $request.requestId -or $status.output.jobId -ne $instance) { Stop-PocValidation 'RecoveryInstanceChangedOrFailed' }
            $batch=Get-PocBatchObservation -Outputs $Outputs -JobId $instance
            if ($batch.Job.id -ne $instance -or $batch.Job.poolInfo.poolId -ne $Outputs.batch_pool_id -or @($batch.Tasks).Count -ne 1 -or $batch.Tasks[0].id -cne 'task-00' -or (Get-PocRecoveryTimestamp $batch.Tasks[0].creationTime) -ne $Context.taskCreationTime) { Stop-PocValidation 'RecoveryTaskIdentityChanged' }
            $task=$batch.Tasks[0]
            if ($task.state -ne 'completed' -or $task.executionInfo.exitCode -ne 0 -or $task.executionInfo.result -ne 'success') { Stop-PocValidation 'RecoveryTaskNotSuccessful' }
            $root='runs/poc-'+$request.requestId
            $aggregate=Get-PocBlobDocument -Outputs $Outputs -Path "$root/final/aggregate.json"
            $manifest=Get-PocBlobDocument -Outputs $Outputs -Path "$root/final/manifest.json"
            $fingerprint=Get-PocFingerprint -Request $request -Classification light
            $metadata=@($batch.Job.metadata | Where-Object { $_.name -ceq 'poc-fingerprint' })
            if ($metadata.Count -ne 1 -or $metadata[0].value -cne $fingerprint) { Stop-PocValidation 'RecoveryBatchFingerprintMismatch' }
            Assert-PocPublishedDocuments -RequestId $request.requestId -TaskCount 1 -Aggregate $aggregate -Manifest $manifest -BlobRoot ($Outputs.blob_endpoint.TrimEnd('/')+'/'+$Outputs.blob_container_name) -JobId $instance -Fingerprint $fingerprint
            $result=Get-PocBlobDocument -Outputs $Outputs -Path "$root/tasks/task-00/result.json"
            if ($result.requestId -ne $request.requestId -or $result.jobId -ne $instance -or $result.taskId -ne 'task-00' -or $result.fingerprint -ne $fingerprint -or $result.value -ne 1) { Stop-PocValidation 'RecoveryTaskResultMismatch' }
            # Rebuild only allowlisted context, never serialize the supplied object.
            $summary.recoveryContext=@{ instanceId=$instance; createdTime=(Get-PocRecoveryTimestamp $status.createdTime); taskCreationTime=(Get-PocRecoveryTimestamp $task.creationTime) }
            $summary.Reason='SameInstanceAndTaskCompletedAfterRestart'
        }
        $summary.Status='Pass'; $summary.ExitCode=0
    } catch {
        if ($_.Exception.Data['PocValidationCode']) { $summary.Reason=[string]$_.Exception.Data['PocValidationCode'] }
    } finally {
        $statusUri=$null; $accepted=$null; $key=$null
        $summary.completedUtc=[DateTime]::UtcNow.ToString('o')
        Write-DeploymentJson (Join-Path $EvidenceDirectory 'summary.json') $summary
        [IO.File]::WriteAllLines((Join-Path $EvidenceDirectory 'summary.txt'),[string[]]@("Recovery $Phase`: $($summary.Status) ($($summary.Reason))",'Entity checkpoint boundary interruption: not observed.'),[Text.UTF8Encoding]::new($false))
    }
    return [pscustomobject]$summary
}

function Invoke-PocRecoveryValidation {
    param([string]$DeploymentResultPath,[string]$ExpectedFunctionAppName,[ValidateSet('Restart','HostStoppedBatchWallClock')][string]$ValidationMode='Restart',
        [switch]$AllowFunctionRestart,[switch]$AllowFunctionStop,[switch]$DedicatedEnvironment,
        [int]$DelaySeconds=300,[int]$TimeoutSeconds=900,[string]$EvidenceDirectory)
    $ErrorActionPreference='Stop'
    if ($ValidationMode -ceq 'HostStoppedBatchWallClock') {
        if (-not $AllowFunctionStop -or -not $DedicatedEnvironment -or -not $ExpectedFunctionAppName) { throw 'Explicit AllowFunctionStop, DedicatedEnvironment and ExpectedFunctionAppName are required for HostStoppedBatchWallClock mode.' }
    } else {
        if (-not $AllowFunctionRestart -or -not $DedicatedEnvironment -or -not $ExpectedFunctionAppName) { throw 'Explicit AllowFunctionRestart, DedicatedEnvironment and ExpectedFunctionAppName are required.' }
    }
    $deployment=Get-Content -LiteralPath $DeploymentResultPath -Raw | ConvertFrom-Json
    $outputs=ConvertTo-DeploymentHashtable $deployment.outputs
    if ($outputs.function_app_name -cne $ExpectedFunctionAppName -or $outputs.network_mode -cne 'private-function-vm') { throw 'Recovery target does not match the explicitly selected private Function.' }
    $script:AzPath=Resolve-DeploymentTool -Name az -RepositoryRoot (Split-Path $PSScriptRoot -Parent)
    $script:DeploymentSubscriptionId=$outputs.subscription_id
    $siteId='/subscriptions/'+$outputs.subscription_id+'/resourceGroups/'+$outputs.resource_group_name+'/providers/Microsoft.Web/sites/'+$outputs.function_app_name
    $app=Invoke-AzJson -Sensitive -Arguments @('rest','--method','get','--url',('https://management.azure.com'+$siteId+'?api-version=2024-11-01'))
    if ($app.id -ine $siteId -or $app.properties.state -ne 'Running' -or $app.properties.defaultHostName -ine ([Uri]$outputs.function_app_url).DnsSafeHost -or $app.properties.publicNetworkAccess -ne 'Disabled') { throw 'Recovery requires a running, identity-matched private Function.' }
    $startContext=if ($ValidationMode -ceq 'HostStoppedBatchWallClock') { @{mode='HostStoppedBatchWallClock'} } else { @{delaySeconds=$DelaySeconds} }
    $start=Invoke-PrivateVmOperation -Outputs $outputs -Operation RecoveryStart -RecoveryContext $startContext -EvidenceDirectory (Join-Path $EvidenceDirectory 'before') -TimeoutSeconds $TimeoutSeconds
    if ($start.exit_code -ne 0) { throw 'Recovery start did not observe a running Task; no Function host interruption was attempted.' }
    $before=Get-Content -LiteralPath (Join-Path $EvidenceDirectory 'before/summary.json') -Raw | ConvertFrom-Json
    $context=Assert-PocRecoveryContext -Phase Complete -Context (ConvertTo-DeploymentHashtable $before.recoveryContext)
    $elapsed=([DateTimeOffset]::UtcNow-[DateTimeOffset]::Parse($context.taskStartedUtc)).TotalSeconds
    if ($ValidationMode -ceq 'HostStoppedBatchWallClock') {
        if ($elapsed -lt 0 -or $elapsed -ge ($context.batchMaxWallClockSeconds-120)) { throw 'Running Task observation is too old to stop Function safely before MaxWallClockTime.' }
        if ($TimeoutSeconds -lt ($context.batchMaxWallClockSeconds+120)) { throw 'TimeoutSeconds is too short to observe Batch MaxWallClockTime expiry while stopped.' }
        $stopAttempted=$false
        $operation=@{ schemaVersion=1; mode='HostStoppedBatchWallClock'; functionAppName=$outputs.function_app_name; instanceId=$context.instanceId; batchMaxWallClockSeconds=$context.batchMaxWallClockSeconds; stopStartedUtc=$null; stopCompletedUtc=$null; stopSucceeded=$false; stoppedStateVerified=$false; stoppedVerifiedAfterExpiry=$false; restoredStartCommandSucceeded=$false; restoredRunningStateVerified=$false; validationExitCode=$null; completedUtc=$null; entityCheckpointInterruptionObserved=$false; limitation='Function App stopped via ARM while Batch Task running; Batch MaxWallClockTime terminates Job independently before Function is started in finally.' }
        try {
            $stopAttempted=$true
            $operation.stopStartedUtc=[DateTime]::UtcNow.ToString('o')
            $null=Invoke-AzJson -Sensitive -Arguments @('functionapp','stop','--name',$outputs.function_app_name,'--resource-group',$outputs.resource_group_name)
            $operation.stopSucceeded=$true
            $stoppedApp=Invoke-AzJson -Sensitive -Arguments @('rest','--method','get','--url',('https://management.azure.com'+$siteId+'?api-version=2024-11-01'))
            if ($stoppedApp.id -ine $siteId -or $stoppedApp.properties.state -ne 'Stopped') { throw 'Function App did not transition to Stopped state.' }
            $operation.stoppedStateVerified=$true
            $operation.stopCompletedUtc=[DateTime]::UtcNow.ToString('o')
            $result=Invoke-PrivateVmOperation -Outputs $outputs -Operation RecoveryComplete -RecoveryContext $context -EvidenceDirectory (Join-Path $EvidenceDirectory 'after') -TimeoutSeconds $TimeoutSeconds
            $afterStoppedApp=Invoke-AzJson -Sensitive -Arguments @('rest','--method','get','--url',('https://management.azure.com'+$siteId+'?api-version=2024-11-01'))
            $operation.stoppedVerifiedAfterExpiry=($afterStoppedApp.id -ieq $siteId -and $afterStoppedApp.properties.state -eq 'Stopped')
            if (-not $operation.stoppedVerifiedAfterExpiry) { throw 'Function App was not in Stopped state when Batch MaxWallClockTime completion was observed.' }
            $operation.validationExitCode=[int]$result.exit_code
            return $result
        } finally {
            try {
                if ($stopAttempted) {
                    $null=Invoke-AzJson -Sensitive -Arguments @('functionapp','start','--name',$outputs.function_app_name,'--resource-group',$outputs.resource_group_name)
                    $operation.restoredStartCommandSucceeded=$true
                    $restoredApp=Invoke-AzJson -Sensitive -Arguments @('rest','--method','get','--url',('https://management.azure.com'+$siteId+'?api-version=2024-11-01'))
                    $operation.restoredRunningStateVerified=($restoredApp.id -ieq $siteId -and $restoredApp.properties.state -eq 'Running')
                }
            } finally {
                $operation.completedUtc=[DateTime]::UtcNow.ToString('o')
                Write-DeploymentJson (Join-Path $EvidenceDirectory 'recovery-result.json') $operation
            }
        }
    }
    if ($elapsed -lt 0 -or $elapsed -ge ($context.request.lightDelaySeconds-60)) { throw 'Running Task observation is too old to restart safely within its delay window.' }
    $restartAttempted=$false
    $operation=@{ schemaVersion=1; functionAppName=$outputs.function_app_name; instanceId=$context.instanceId; restartStartedUtc=$null; restartCompletedUtc=$null; restartSucceeded=$false; restoredStartCommandSucceeded=$false; validationExitCode=$null; completedUtc=$null; entityCheckpointInterruptionObserved=$false; limitation='ARM restart while Batch Task observed running; no crash or Entity checkpoint boundary guarantee.' }
    try {
        $restartAttempted=$true
        $operation.restartStartedUtc=[DateTime]::UtcNow.ToString('o')
        $null=Invoke-AzJson -Sensitive -Arguments @('functionapp','restart','--name',$outputs.function_app_name,'--resource-group',$outputs.resource_group_name)
        $operation.restartSucceeded=$true
        $operation.restartCompletedUtc=[DateTime]::UtcNow.ToString('o')
        $result=Invoke-PrivateVmOperation -Outputs $outputs -Operation RecoveryComplete -RecoveryContext $context -EvidenceDirectory (Join-Path $EvidenceDirectory 'after') -TimeoutSeconds $TimeoutSeconds
        $operation.validationExitCode=[int]$result.exit_code
        return $result
    } finally {
        # A failed management request can have reached the service. Restore the
        # explicitly selected app to running even if restart/validation failed.
        try {
            if ($restartAttempted) { $null=Invoke-AzJson -Sensitive -Arguments @('functionapp','start','--name',$outputs.function_app_name,'--resource-group',$outputs.resource_group_name); $operation.restoredStartCommandSucceeded=$true }
        } finally {
            $operation.completedUtc=[DateTime]::UtcNow.ToString('o')
            Write-DeploymentJson (Join-Path $EvidenceDirectory 'recovery-result.json') $operation
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        if (-not $EvidenceDirectory) { $EvidenceDirectory=Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/recovery/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-ffff')) }
        $result=Invoke-PocRecoveryValidation -DeploymentResultPath $DeploymentResultPath -ExpectedFunctionAppName $ExpectedFunctionAppName -ValidationMode $ValidationMode -AllowFunctionRestart:$AllowFunctionRestart -AllowFunctionStop:$AllowFunctionStop -DedicatedEnvironment:$DedicatedEnvironment -DelaySeconds $DelaySeconds -TimeoutSeconds $TimeoutSeconds -EvidenceDirectory $EvidenceDirectory
        Write-Host "Private Function recovery validation completed. Sanitized evidence: $EvidenceDirectory"
        exit ([int]$result.exit_code)
    } catch { Write-Error 'Recovery validation failed; raw command and HTTP output suppressed. Inspect sanitized evidence and verify the target Function is running.'; exit 1 }
}
