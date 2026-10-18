$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../Invoke-PocRecoveryValidation.ps1')
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw "ASSERT: $Message" } }
function Assert-Fails([scriptblock]$Action,[string]$Code) {
    try { & $Action|Out-Null } catch { if ($_.Exception.Message -like "*$Code*") { return }; throw }
    throw "Expected $Code"
}
$script:secret='DO-NOT-LOG-RECOVERY-SECRET'
$script:mode='success'; $script:posts=0; $script:commands=@()
$script:now=[DateTime]::UtcNow.ToString('o')
$script:outputs=@{subscription_id='subscription';resource_group_name='rg';function_app_name='app';function_app_url='https://app.azurewebsites.net';
    network_mode='private-function-vm';batch_pool_id='pool';blob_endpoint='https://store.blob.core.windows.net/';blob_container_name='results'}
function Get-PocValidationKey { param($Outputs) return $script:secret }
function Get-PocWorkflowSettings { param($Outputs) return @{TimeoutMinutes=15} }
function Invoke-PocValidationHttp {
    param($Uri,$Method,$Headers,$Body)
    if ($Method -eq 'Post') {
        $script:posts++; $script:request=ConvertTo-DeploymentHashtable ($Body|ConvertFrom-Json);$script:instance=Get-PocIdentity $script:request.requestId
        return @{StatusCode=202;Data=[pscustomobject]@{instanceId=$script:instance;statusQueryGetUri="https://app.azurewebsites.net/api/workflows/$($script:request.requestId)"}}
    }
    return @{StatusCode=202;Data=[pscustomobject]@{runtimeStatus='Running';createdTime=$script:now}}
}
function Get-PocBatchObservation {
    param($Outputs,$JobId)
    $creation=if ($script:mode -eq 'recreated') {'2000-01-01T00:00:00Z'}else{$script:now}
    $jobState=if ($script:completing -and $script:wallClockMode) {'completed'}else{'active'}
    $terminateReason=if ($script:mode -eq 'wrongTerminateReason') {'UserTerminate'}else{'MaxWallClockTimeExpiry'}
    return @{Job=[pscustomobject]@{id=$JobId;state=$jobState;creationTime=$creation;poolInfo=[pscustomobject]@{poolId='pool'};constraints=[pscustomobject]@{maxWallClockTime='PT15M'};executionInfo=[pscustomobject]@{terminateReason=$terminateReason};metadata=@([pscustomobject]@{name='poc-fingerprint';value=(Get-PocFingerprint $script:request light)})};Tasks=@([pscustomobject]@{id='task-00';creationTime=$creation;state=$(if($script:completing){'completed'}else{'running'});executionInfo=[pscustomobject]@{startTime=$script:now;exitCode=0;result='success'}})}
}
function Wait-PocWorkflow {
    param($StatusUri,$TimeoutSeconds,$PollSeconds)
    Assert-True ($StatusUri -like "*code=$script:secret") 'Status URL must use in-memory system key'
    return [pscustomobject]@{runtimeStatus='Completed';createdTime=$script:now;output=[pscustomobject]@{state='Succeeded';requestId=$script:request.requestId;jobId=$script:instance}}
}
$script:appState='Running'
function Test-PocBlobExists { param($Outputs,$Path) return ($script:mode -eq 'leakedManifest') }
function Invoke-AzJson {
    param([switch]$Sensitive,$Arguments)
    Assert-True ([bool]$Sensitive) 'Every provider command must suppress raw output'
    $script:commands+=($Arguments[0..1]-join ' ')
    if ($script:mode -eq 'keyDenied') { throw $script:secret }
    if ($Arguments[0] -eq 'rest') { return [pscustomobject]@{id='/subscriptions/subscription/resourceGroups/rg/providers/Microsoft.Web/sites/app';properties=[pscustomobject]@{state=$script:appState;defaultHostName='app.azurewebsites.net';publicNetworkAccess='Disabled'}} }
    if ($Arguments[0..1]-join ' ' -eq 'functionapp keys') { return [pscustomobject]@{systemKeys=[pscustomobject]@{durabletask_extension=$script:secret}} }
    if ($Arguments[0..1]-join ' ' -eq 'functionapp restart' -and $script:mode -eq 'restartFailed') { throw $script:secret }
    if ($Arguments[0..1]-join ' ' -eq 'functionapp stop') {
        if ($script:mode -eq 'stopFailed') { throw $script:secret }
        $script:appState='Stopped'
    }
    if ($Arguments[0..1]-join ' ' -eq 'functionapp start') { $script:appState='Running' }
    return $null
}
function Get-PocBlobDocument {
    param($Outputs,$Path)
    $fingerprint=Get-PocFingerprint $script:request light
    $base=@{requestId=$script:request.requestId;jobId=$script:instance;fingerprint=$fingerprint;sum=1;taskCount=1;taskIds=@('task-00')}
    if ($Path -like '*manifest.json') { $base.status='Succeeded';$base.aggregateUri="https://store.blob.core.windows.net/results/runs/poc-$($script:request.requestId)/final/aggregate.json";$base.taskUris=@("https://store.blob.core.windows.net/results/runs/poc-$($script:request.requestId)/tasks/task-00/result.json") }
    if ($Path -like '*tasks*') { $base.taskId='task-00';$base.value=1 }
    $base.untrusted=$script:secret
    return [pscustomobject]$base
}
$work=Join-Path (Split-Path $PSScriptRoot -Parent|Split-Path -Parent) ('artifacts/recovery-tests/'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $work -Force
$script:completing=$false;$script:wallClockMode=$false
$start=Invoke-PocRecoveryGuest -Outputs $script:outputs -Phase Start -Context @{delaySeconds=300} -EvidenceDirectory (Join-Path $work 'start')
Assert-True ($start.ExitCode -eq 0) "Start running observation failed: $($start.Reason)"
$context=$start.recoveryContext
Assert-Fails { Assert-PocRecoveryContext Start @{delaySeconds=300;url=$script:secret} } 'InvalidRecoveryContext'
$malicious=$context.Clone();$malicious.secret=$script:secret
Assert-Fails { Assert-PocRecoveryContext Complete $malicious } 'InvalidRecoveryContext'
$malicious=$context.Clone();$malicious.request=$context.request.Clone();$malicious.request.extra=$script:secret
Assert-Fails { Assert-PocRecoveryContext Complete $malicious } 'InvalidRecoveryContext'
$script:completing=$true
$complete=Invoke-PocRecoveryGuest -Outputs $script:outputs -Phase Complete -Context $context -EvidenceDirectory (Join-Path $work 'complete')
Assert-True ($complete.ExitCode -eq 0) "Completion failed: $($complete.Reason)"
Assert-True ($script:posts -eq 1) 'Complete must not POST another admission'
$script:now=[DateTime]::Parse($script:now).ToUniversalTime()
$datetimeResult=Invoke-PocRecoveryGuest -Outputs $script:outputs -Phase Complete -Context $context -EvidenceDirectory (Join-Path $work 'datetime')
Assert-True ($datetimeResult.ExitCode -eq 0) 'JSON DateTime objects must preserve precise instant equality'
$script:now=$script:now.ToString('o')
$script:mode='keyDenied';$postCount=$script:posts
$denied=Invoke-PocRecoveryGuest -Outputs $script:outputs -Phase Start -Context @{delaySeconds=300} -EvidenceDirectory (Join-Path $work 'denied')
Assert-True ($denied.ExitCode -eq 1 -and $script:posts -eq $postCount) 'Missing host-key permission must fail before Task submission'
$script:mode='recreated'
$changed=Invoke-PocRecoveryGuest -Outputs $script:outputs -Phase Complete -Context $context -EvidenceDirectory (Join-Path $work 'changed')
Assert-True ($changed.Reason -eq 'RecoveryTaskIdentityChanged' -and $changed.ExitCode -eq 1) 'Recreated Task must fail'
# HostStoppedBatchWallClock (V08) guest and host tests
$script:mode='success';$script:completing=$false;$script:wallClockMode=$true;$script:appState='Running'
$v08Start=Invoke-PocRecoveryGuest -Outputs $script:outputs -Phase Start -Context @{mode='HostStoppedBatchWallClock'} -EvidenceDirectory (Join-Path $work 'v08-start')
Assert-True ($v08Start.ExitCode -eq 0 -and $v08Start.Reason -eq 'BatchTaskRunningBeforeHostStop' -and $v08Start.recoveryContext.batchMaxWallClockSeconds -eq 900) "V08 Start failed: $($v08Start.Reason)"
$v08Context=$v08Start.recoveryContext
$script:completing=$true;$script:appState='Running'
$v08NotStopped=Invoke-PocRecoveryGuest -Outputs $script:outputs -Phase Complete -Context $v08Context -EvidenceDirectory (Join-Path $work 'v08-not-stopped')
Assert-True ($v08NotStopped.ExitCode -eq 1 -and $v08NotStopped.Reason -eq 'FunctionHostNotStoppedDuringSafetyNetCheck') 'V08 Complete must reject a running Function host'
$script:appState='Stopped'
$v08Complete=Invoke-PocRecoveryGuest -Outputs $script:outputs -Phase Complete -Context $v08Context -EvidenceDirectory (Join-Path $work 'v08-complete')
Assert-True ($v08Complete.ExitCode -eq 0 -and $v08Complete.Reason -eq 'BatchMaxWallClockTerminatedWhileFunctionStopped' -and $v08Complete.recoveryContext.batchTerminationReason -eq 'MaxWallClockTimeExpiry') "V08 Complete failed: $($v08Complete.Reason)"
$script:mode='wrongTerminateReason'
$v08WrongReason=Invoke-PocRecoveryGuest -Outputs $script:outputs -Phase Complete -Context $v08Context -EvidenceDirectory (Join-Path $work 'v08-wrong-reason')
Assert-True ($v08WrongReason.ExitCode -eq 1 -and $v08WrongReason.Reason -eq 'BatchWallClockExpiryReasonMissing') 'V08 Complete must require MaxWallClockTimeExpiry'
$script:mode='leakedManifest'
$v08Manifest=Invoke-PocRecoveryGuest -Outputs $script:outputs -Phase Complete -Context $v08Context -EvidenceDirectory (Join-Path $work 'v08-manifest')
Assert-True ($v08Manifest.ExitCode -eq 1 -and $v08Manifest.Reason -eq 'UnexpectedSuccessManifest') 'V08 Complete must reject published success manifest'
$script:mode='success';$script:appState='Running'
function Resolve-DeploymentTool { param($Name,$RepositoryRoot) return 'fake-az' }
function Invoke-PrivateVmOperation {
    param($Outputs,$Operation,$RecoveryContext,$EvidenceDirectory,$TimeoutSeconds)
    if ($Operation -eq 'RecoveryStart') {
        $null=New-Item -ItemType Directory -Path $EvidenceDirectory -Force
        Write-DeploymentJson (Join-Path $EvidenceDirectory 'summary.json') @{recoveryContext=$script:startContext}
    }
    return [pscustomobject]@{exit_code=0}
}
$script:startContext=$context
$deployment=Join-Path $work 'deployment.json';Write-DeploymentJson $deployment @{outputs=$script:outputs}
$invokeArgs=@{DeploymentResultPath=$deployment;ExpectedFunctionAppName='app';AllowFunctionRestart=$true;DedicatedEnvironment=$true;EvidenceDirectory=(Join-Path $work 'wrapper')}
Assert-Fails { Invoke-PocRecoveryValidation -DeploymentResultPath $deployment -ExpectedFunctionAppName app } 'Explicit'
$wrong=$invokeArgs.Clone();$wrong.ExpectedFunctionAppName='other'
Assert-Fails { Invoke-PocRecoveryValidation @wrong } 'target'
$script:mode='restartFailed';$script:commands=@()
Assert-Fails { Invoke-PocRecoveryValidation @invokeArgs } $script:secret
Assert-True ('functionapp start' -in $script:commands) 'Failed restart must still restore selected Function to running'
$script:mode='success';$script:commands=@()
$old=$context.Clone();$old.taskStartedUtc=[DateTime]::UtcNow.AddHours(-1).ToString('o');$script:startContext=$old
Assert-Fails { Invoke-PocRecoveryValidation @invokeArgs } 'too old'
Assert-True ('functionapp restart' -notin $script:commands) 'Stale running observation must prevent restart'
# V08 host wrapper tests
$script:startContext=$v08Context;$script:commands=@();$script:appState='Running'
$v08Args=@{DeploymentResultPath=$deployment;ExpectedFunctionAppName='app';ValidationMode='HostStoppedBatchWallClock';AllowFunctionStop=$true;DedicatedEnvironment=$true;TimeoutSeconds=1200;EvidenceDirectory=(Join-Path $work 'v08-wrapper')}
Assert-Fails { Invoke-PocRecoveryValidation -DeploymentResultPath $deployment -ExpectedFunctionAppName app -ValidationMode HostStoppedBatchWallClock -DedicatedEnvironment } 'AllowFunctionStop'
$v08Result=Invoke-PocRecoveryValidation @v08Args
$v08Op=Get-Content -LiteralPath (Join-Path $work 'v08-wrapper/recovery-result.json') -Raw | ConvertFrom-Json
Assert-True ($v08Result.exit_code -eq 0 -and $v08Op.stopSucceeded -and $v08Op.stoppedStateVerified -and $v08Op.stoppedVerifiedAfterExpiry -and $v08Op.restoredStartCommandSucceeded -and $v08Op.restoredRunningStateVerified -and $script:appState -eq 'Running') 'V08 wrapper must stop Function, verify Stopped before/after expiry, and restore Running in finally'
$script:mode='stopFailed';$script:commands=@();$script:appState='Running'
Assert-Fails { Invoke-PocRecoveryValidation @v08Args } $script:secret
Assert-True ('functionapp start' -in $script:commands -and $script:appState -eq 'Running') 'Failed stop must still issue functionapp start in finally'
$script:mode='success'
# A fresh child proves dependency param blocks preserve actual CLI arguments.
$child=Join-Path $work 'child';$null=New-Item -ItemType Directory -Path $child -Force
foreach ($name in @('Invoke-PocRecoveryValidation.ps1','Recovery.Context.ps1','Deployment.Common.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../$name") -Destination $child }
$fakeDependency=@'
param($DeploymentResultPath,$EvidenceDirectory,[int]$TimeoutSeconds=900,[switch]$DedicatedEnvironment)
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
function Invoke-PocValidation {}
function Resolve-DeploymentTool { param($Name,$RepositoryRoot) return 'fake-az' }
function Invoke-AzJson {
 param($Arguments,[switch]$Sensitive)
 if ($Arguments[0] -eq 'rest') { return [pscustomobject]@{id='/subscriptions/subscription/resourceGroups/rg/providers/Microsoft.Web/sites/app';properties=[pscustomobject]@{state='Running';defaultHostName='app.azurewebsites.net';publicNetworkAccess='Disabled'}} }
 return $null
}
'@
[IO.File]::WriteAllText((Join-Path $child 'Invoke-PocValidation.ps1'),$fakeDependency,[Text.UTF8Encoding]::new($false))
$fakeTransport=@'
function Invoke-PrivateVmOperation {
 param($Outputs,$Operation,$RecoveryContext,$EvidenceDirectory,$TimeoutSeconds)
 if ($TimeoutSeconds -ne 1234) { throw 'CLI timeout lost' }
 if ($Operation -eq 'RecoveryStart') {
  if ($RecoveryContext.delaySeconds -ne 450) { throw 'CLI delay lost' }
  $context=ConvertTo-DeploymentHashtable (Get-Content (Join-Path $PSScriptRoot 'context.json') -Raw|ConvertFrom-Json)
  $null=New-Item -ItemType Directory -Path $EvidenceDirectory -Force
  Write-DeploymentJson (Join-Path $EvidenceDirectory 'summary.json') @{recoveryContext=$context}
 }
 return [pscustomobject]@{exit_code=0}
}
'@
[IO.File]::WriteAllText((Join-Path $child 'Invoke-PrivateVmOperation.ps1'),$fakeTransport,[Text.UTF8Encoding]::new($false))
Write-DeploymentJson (Join-Path $child 'context.json') $context
$childEvidence=Join-Path $child 'explicit-evidence'
$childShell=if ($PSVersionTable.PSEdition -eq 'Core') { Join-Path $PSHOME 'pwsh' } else { Join-Path $PSHOME 'powershell.exe' }
$null=& $childShell -NoProfile -NonInteractive -File (Join-Path $child 'Invoke-PocRecoveryValidation.ps1') -DeploymentResultPath $deployment -EvidenceDirectory $childEvidence -ExpectedFunctionAppName app -AllowFunctionRestart -DedicatedEnvironment -DelaySeconds 450 -TimeoutSeconds 1234 2>&1
Assert-True ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath (Join-Path $childEvidence 'recovery-result.json'))) 'Fresh CLI invocation must preserve all explicit arguments'
Remove-Item -LiteralPath (Join-Path $child 'Invoke-PrivateVmOperation.ps1') -Force
$null=& $childShell -NoProfile -NonInteractive -Command ". '$($child.Replace("'","''"))/Invoke-PocRecoveryValidation.ps1'; if (-not (Get-Command Invoke-PocRecoveryGuest -ErrorAction SilentlyContinue)) { exit 1 }" 2>&1
Assert-True ($LASTEXITCODE -eq 0) 'Guest bundle without Invoke-PrivateVmOperation.ps1 must dot-source Invoke-PocRecoveryValidation.ps1 cleanly'
$files=@(Get-ChildItem -LiteralPath $work -Filter 'summary*' -Recurse -File)
foreach ($file in $files) { Assert-True (-not (Get-Content -LiteralPath $file.FullName -Raw).Contains($script:secret)) 'Secret leaked to evidence' }
Write-Host 'Recovery validation Fake tests passed (not Azure execution).'
