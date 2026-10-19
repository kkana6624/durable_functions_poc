$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../Invoke-PocAutoscaleValidation.ps1')

function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw "ASSERT: $Message" } }
function Assert-Fails([scriptblock]$Action,[string]$Expected) {
    try { & $Action | Out-Null } catch {
        Assert-True ($_.Exception.Message -match $Expected) "Expected $Expected, got $($_.Exception.Message)"
        return
    }
    throw "ASSERT: expected $Expected"
}

$script:secret='DO-NOT-LOG-AUTOSCALE-SECRET'
$script:poolState=@{
    allocationState='steady'
    enableAutoScale=$false
    currentDedicatedNodes=0
    currentLowPriorityNodes=1
    targetDedicatedNodes=0
    targetLowPriorityNodes=1
}
$script:nodeCount=1
$script:restCalls=[Collections.Generic.List[string]]::new()
$script:failMode=$null

function Resolve-DeploymentTool { param($Name,$RepositoryRoot) return 'fake-az' }
function Invoke-DeploymentValidation {
    param($Outputs,$Phase,$TimeoutSeconds,$PollSeconds)
    return [pscustomobject]@{ Ready=($script:poolState.enableAutoScale -eq $false -and $script:poolState.targetLowPriorityNodes -eq 1) }
}
function Invoke-PrivateVmOperation {
    param($Outputs,$Operation,$Scenarios,$EvidenceDirectory,$TimeoutSeconds,$ExpectedFunctionAppName,$ExpectedPoolId,$AllowPoolAutoscaleTransition,$DedicatedEnvironment)
    if ($Operation -eq 'AutoscaleValidation') {
        $null=New-Item -ItemType Directory -Path $EvidenceDirectory -Force
        Write-DeploymentJson (Join-Path $EvidenceDirectory 'autoscale-result.json') @{ Status='Pass'; ExitCode=0; Reason='DelegatedAutoscalePassed' }
        Write-DeploymentJson (Join-Path $EvidenceDirectory 'summary.json') @{ Status='Pass'; ExitCode=0 }
        return [pscustomobject]@{ exit_code=0; operation='AutoscaleValidation' }
    }
    Assert-True ($script:poolState.enableAutoScale -eq $true -and $script:poolState.currentLowPriorityNodes -eq 0 -and $script:nodeCount -eq 0) 'Workflow must be submitted while Pool is in 0-node autoscale state'
    $null=New-Item -ItemType Directory -Path $EvidenceDirectory -Force
    if ($script:failMode -eq 'workflowFail') {
        return [pscustomobject]@{ exit_code=1 }
    }
    # Simulate Batch autoscale evaluating $PendingTasks > 0 and allocating 1 Spot node.
    $script:poolState.targetLowPriorityNodes=1
    $script:poolState.currentLowPriorityNodes=1
    $script:nodeCount=1
    Write-DeploymentJson (Join-Path $EvidenceDirectory 'summary.json') @{ Status='Pass'; ExitCode=0 }
    return [pscustomobject]@{ exit_code=0 }
}
function Invoke-PocValidation {
    param($DeploymentResultPath,$EvidenceDirectory,$TimeoutSeconds,$Scenarios,$DedicatedEnvironment)
    Assert-True ($script:poolState.enableAutoScale -eq $true -and $script:poolState.currentLowPriorityNodes -eq 0 -and $script:nodeCount -eq 0) 'Guest workflow must be submitted while Pool is in 0-node autoscale state'
    $null=New-Item -ItemType Directory -Path $EvidenceDirectory -Force
    if ($script:failMode -eq 'guestWorkflowFail') {
        return [pscustomobject]@{ ExitCode=1; Status='Fail' }
    }
    $script:poolState.targetLowPriorityNodes=1
    $script:poolState.currentLowPriorityNodes=1
    $script:nodeCount=1
    Write-DeploymentJson (Join-Path $EvidenceDirectory 'summary.json') @{ Status='Pass'; ExitCode=0 }
    return [pscustomobject]@{ ExitCode=0; Status='Pass' }
}
function Invoke-AzJson {
    param([switch]$Sensitive,[string[]]$Arguments)
    Assert-True ([bool]$Sensitive) 'All autoscale Azure calls must suppress raw output'
    $uri=$Arguments[[Array]::IndexOf($Arguments,'--url')+1]
    $method=$Arguments[[Array]::IndexOf($Arguments,'--method')+1]
    $script:restCalls.Add("$method $uri")
    if ($uri -like 'https://management.azure.com/*') {
        return [pscustomobject]@{
            id='/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/sites/app'
            properties=[pscustomobject]@{ state='Running'; defaultHostName='app.azurewebsites.net'; publicNetworkAccess='Disabled' }
        }
    }
    if ($uri -like '*/nodes?api-version=*') {
        $nodes=if ($script:nodeCount -gt 0) { @([pscustomobject]@{ id='node-1'; state='idle'; isDedicated=$false }) } else { @() }
        return [pscustomobject]@{ value=$nodes }
    }
    if ($uri -like '*/resize?api-version=*') {
        $bodyArg=$Arguments[[Array]::IndexOf($Arguments,'--body')+1]
        $rawBody=if ($bodyArg.StartsWith('@')) { Get-Content -LiteralPath $bodyArg.Substring(1) -Raw } else { $bodyArg }
        $body=$rawBody | ConvertFrom-Json
        $script:poolState.targetDedicatedNodes=[int]$body.targetDedicatedNodes
        $script:poolState.targetLowPriorityNodes=[int]$body.targetLowPriorityNodes
        $script:poolState.currentDedicatedNodes=[int]$body.targetDedicatedNodes
        $script:poolState.currentLowPriorityNodes=[int]$body.targetLowPriorityNodes
        $script:nodeCount=$script:poolState.currentDedicatedNodes+$script:poolState.currentLowPriorityNodes
        return $null
    }
    if ($uri -like '*/evaluateautoscale?api-version=*') {
        Assert-True ($script:poolState.enableAutoScale -eq $true) 'evaluateautoscale requires autoscaling to be enabled on the pool'
        return [pscustomobject]@{ results='$TargetDedicatedNodes=0;$TargetLowPriorityNodes=0;$NodeDeallocationOption=taskcompletion;'; error=$null }
    }
    if ($uri -like '*/enableautoscale?api-version=*') {
        if ($script:failMode -eq 'enableFail') { throw "Autoscale enable error $script:secret" }
        $script:poolState.enableAutoScale=$true
        $script:poolState.targetDedicatedNodes=0
        $script:poolState.targetLowPriorityNodes=0
        $script:poolState.currentDedicatedNodes=0
        $script:poolState.currentLowPriorityNodes=0
        $script:nodeCount=0
        return $null
    }
    if ($uri -like '*/disableautoscale?api-version=*') {
        if ($script:failMode -eq 'restoreFail') { throw $script:secret }
        $script:poolState.enableAutoScale=$false
        return $null
    }
    if ($uri -like '*/pools/pool?api-version=*') {
        return [pscustomobject]$script:poolState
    }
    throw "Unexpected REST URI: $uri"
}

$work=Join-Path ([IO.Path]::GetTempPath()) ('poc-autoscale-tests-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $work -Force
try {
    $outputs=@{
        subscription_id='sub'; resource_group_name='rg'; function_app_name='app'; function_app_url='https://app.azurewebsites.net';
        network_mode='private-function-vm'; batch_mode='new'; batch_endpoint='https://batch.example'; batch_pool_id='pool';
        batch_node_type='spot'; batch_node_count=1
    }
    $deployment=Join-Path $work 'deployment.json'
    Write-DeploymentJson $deployment @{ outputs=$outputs }

    Assert-Fails { Invoke-PocAutoscaleValidation -DeploymentResultPath $deployment -ExpectedFunctionAppName app -ExpectedPoolId pool } 'Explicit'
    $existingOutputs=$outputs.Clone(); $existingOutputs.batch_mode='existing'
    $existingDeployment=Join-Path $work 'existing.json'
    Write-DeploymentJson $existingDeployment @{ outputs=$existingOutputs }
    Assert-Fails { Invoke-PocAutoscaleValidation -DeploymentResultPath $existingDeployment -ExpectedFunctionAppName app -ExpectedPoolId pool -AllowPoolAutoscaleTransition -DedicatedEnvironment -EvidenceDirectory (Join-Path $work 'existing-ev') } 'Shared existing Batch pools'

    $evidence=Join-Path $work 'pass'
    $result=Invoke-PocAutoscaleValidation -DeploymentResultPath $deployment -ExpectedFunctionAppName app -ExpectedPoolId pool -AllowPoolAutoscaleTransition -DedicatedEnvironment -EvidenceDirectory $evidence
    Assert-True ($result.ExitCode -eq 0 -and $result.Status -eq 'Pass' -and $result.zeroNodesVerifiedBeforeSubmission -and $result.scaledUpObserved -and $result.restoredFixedScaleSucceeded -and $result.restoredPoolReadyVerified) 'Autoscale validation should pass and restore fixed scale'
    Assert-True ($script:poolState.enableAutoScale -eq $false -and $script:poolState.targetLowPriorityNodes -eq 1) 'Pool must return to fixed 1-node scale after test'

    $script:failMode='workflowFail'
    $failEvidence=Join-Path $work 'fail'
    Assert-Fails { Invoke-PocAutoscaleValidation -DeploymentResultPath $deployment -ExpectedFunctionAppName app -ExpectedPoolId pool -AllowPoolAutoscaleTransition -DedicatedEnvironment -EvidenceDirectory $failEvidence } 'did not pass'
    $saved=Get-Content -LiteralPath (Join-Path $failEvidence 'autoscale-result.json') -Raw | ConvertFrom-Json
    Assert-True ($saved.restoredFixedScaleSucceeded -and $saved.restoredPoolReadyVerified -and $script:poolState.enableAutoScale -eq $false -and $script:poolState.targetLowPriorityNodes -eq 1) 'Failed workflow must still restore Pool to fixed 1-node scale in finally'

    $script:failMode='enableFail'
    $enableFailEvidence=Join-Path $work 'enable-fail'
    Assert-Fails { Invoke-PocAutoscaleValidation -DeploymentResultPath $deployment -ExpectedFunctionAppName app -ExpectedPoolId pool -AllowPoolAutoscaleTransition -DedicatedEnvironment -EvidenceDirectory $enableFailEvidence } $script:secret
    $savedEnable=Get-Content -LiteralPath (Join-Path $enableFailEvidence 'autoscale-result.json') -Raw | ConvertFrom-Json
    Assert-True ($savedEnable.restoredFixedScaleSucceeded -and $script:poolState.targetLowPriorityNodes -eq 1) 'Failed enableautoscale after 0-node resize must still restore fixed 1-node scale in finally'

    $script:failMode='restoreFail'
    $restoreFailEvidence=Join-Path $work 'restore-fail'
    Assert-Fails { Invoke-PocAutoscaleValidation -DeploymentResultPath $deployment -ExpectedFunctionAppName app -ExpectedPoolId pool -AllowPoolAutoscaleTransition -DedicatedEnvironment -EvidenceDirectory $restoreFailEvidence } $script:secret
    $savedRestore=Get-Content -LiteralPath (Join-Path $restoreFailEvidence 'autoscale-result.json') -Raw | ConvertFrom-Json
    Assert-True ($savedRestore.Status -eq 'Fail' -and $savedRestore.Reason -eq 'PoolRestorationFailed' -and $savedRestore.ExitCode -eq 1) 'Restoration failure must record Status=Fail, Reason=PoolRestorationFailed, ExitCode=1'
    $script:failMode=$null
    # Private Batch delegation test
    $privateOutputs=$outputs.Clone(); $privateOutputs.batch_private_ip='10.80.2.5'
    $privateDeployment=Join-Path $work 'private-deployment.json'
    Write-DeploymentJson $privateDeployment @{ outputs=$privateOutputs }
    $pvtEvidence=Join-Path $work 'pvt-pass'
    $pvtResult=Invoke-PocAutoscaleValidation -DeploymentResultPath $privateDeployment -ExpectedFunctionAppName app -ExpectedPoolId pool -AllowPoolAutoscaleTransition -DedicatedEnvironment -EvidenceDirectory $pvtEvidence
    Assert-True ($pvtResult.ExitCode -eq 0 -and $pvtResult.Status -eq 'Pass' -and $pvtResult.Reason -eq 'DelegatedAutoscalePassed') 'Private Batch autoscale must delegate to Invoke-PrivateVmOperation'

    # Guest-mode execution test (runs pool transition directly and calls Invoke-PocValidation)
    $guestEvidence=Join-Path $work 'guest-pass'
    $guestResult=Invoke-PocAutoscaleValidation -DeploymentResultPath $privateDeployment -ExpectedFunctionAppName app -ExpectedPoolId pool -AllowPoolAutoscaleTransition -DedicatedEnvironment -EvidenceDirectory $guestEvidence -AsGuest
    Assert-True ($guestResult.ExitCode -eq 0 -and $guestResult.Status -eq 'Pass' -and $guestResult.zeroNodesVerifiedBeforeSubmission -and $guestResult.scaledUpObserved) 'Guest mode autoscale validation should execute directly without host delegation'
    Assert-True (Test-Path -LiteralPath (Join-Path $guestEvidence 'autoscale-result.json')) 'Guest mode must write autoscale-result.json'
    Assert-True (Test-Path -LiteralPath (Join-Path $guestEvidence 'summary.json')) 'Guest mode must write summary.json'
    Assert-True (Test-Path -LiteralPath (Join-Path $guestEvidence 'summary.txt')) 'Guest mode must write summary.txt'

    # POC_VM_DEADLINE_UTC environment variable adoption and enforcement tests
    [Environment]::SetEnvironmentVariable('POC_VM_DEADLINE_UTC', [DateTime]::UtcNow.AddSeconds(-10).ToString('o'))
    $expiredEvidence = Join-Path $work 'expired-deadline'
    Assert-Fails { Invoke-PocAutoscaleValidation -DeploymentResultPath $privateDeployment -ExpectedFunctionAppName app -ExpectedPoolId pool -AllowPoolAutoscaleTransition -DedicatedEnvironment -EvidenceDirectory $expiredEvidence -AsGuest } 'Insufficient time remaining before pool preparation'

    [Environment]::SetEnvironmentVariable('POC_VM_DEADLINE_UTC', [DateTime]::UtcNow.AddSeconds(630).ToString('o'))
    $tightEvidence = Join-Path $work 'tight-deadline'
    Assert-Fails { Invoke-PocAutoscaleValidation -DeploymentResultPath $privateDeployment -ExpectedFunctionAppName app -ExpectedPoolId pool -AllowPoolAutoscaleTransition -DedicatedEnvironment -EvidenceDirectory $tightEvidence -AsGuest } 'Insufficient time remaining before pool preparation'

    [Environment]::SetEnvironmentVariable('POC_VM_DEADLINE_UTC', [DateTime]::UtcNow.AddSeconds(3600).ToString('o'))
    $validDeadlineEvidence = Join-Path $work 'valid-deadline'
    $validResult = Invoke-PocAutoscaleValidation -DeploymentResultPath $privateDeployment -ExpectedFunctionAppName app -ExpectedPoolId pool -AllowPoolAutoscaleTransition -DedicatedEnvironment -EvidenceDirectory $validDeadlineEvidence -AsGuest
    Assert-True ($validResult.ExitCode -eq 0 -and $validResult.Status -eq 'Pass') 'Valid POC_VM_DEADLINE_UTC should be adopted and pass'
    [Environment]::SetEnvironmentVariable('POC_VM_DEADLINE_UTC', $null)

    foreach ($file in (Get-ChildItem -LiteralPath $work -Recurse -File)) {
        Assert-True (-not (Get-Content -LiteralPath $file.FullName -Raw).Contains($script:secret)) 'Secret leaked to autoscale evidence'
    }
    Write-Host 'Autoscale validation Fake tests passed (not Azure execution).'
} finally {
    [Environment]::SetEnvironmentVariable('POC_VM_DEADLINE_UTC', $null)
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
