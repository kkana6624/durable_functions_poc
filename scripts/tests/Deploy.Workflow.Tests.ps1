$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw "ASSERT: $Message" } }
function Assert-Fails([scriptblock]$Action,[string]$Expected) {
    try { & $Action | Out-Null } catch { Assert-True ($_.Exception.Message -match $Expected) "Expected $Expected"; return }
    throw "ASSERT: expected $Expected"
}
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('poc-workflow-tests-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path (Join-Path $temporary 'scripts') -Force
$oldTrace=$env:POC_FAKE_TRACE
$oldDefault=$env:POC_FAKE_DEFAULT
$oldRequest=$env:POC_FAKE_REQUESTID
$oldBackend=$env:POC_FAKE_BACKEND
$oldPath=$env:Path
try {
    foreach ($file in @('deploy.ps1','Deployment.Common.ps1','Test-Deployment.ps1','Validation.Assertions.ps1','Invoke-PrivateVmOperation.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../$file") -Destination (Join-Path $temporary "scripts/$file")
    }
    @'
# Test-only transport injection into the isolated copy; production files are unchanged.
function Invoke-ValidationHttp {
    param([string]$Uri,[string]$Method='Get',[hashtable]$Headers=@{},[string]$Body)
    [IO.File]::AppendAllText($env:POC_FAKE_TRACE,"http $Method`n")
    if ($Method -eq 'Post') {
        $env:POC_FAKE_REQUESTID=($Body | ConvertFrom-Json).requestId
        return @{ StatusCode=202; Data=[pscustomobject]@{ instanceId='instance'; statusQueryGetUri="https://poc.example/api/workflows/$env:POC_FAKE_REQUESTID" } }
    }
    return @{ StatusCode=200; Data=[pscustomobject]@{ runtimeStatus='Completed'; output=[pscustomobject]@{ state='Succeeded' } } }
}
'@ | Add-Content -LiteralPath (Join-Path $temporary 'scripts/Test-Deployment.ps1') -Encoding UTF8
    @'
# The isolated workflow tests exercise orchestration; transport is tested separately.
function Invoke-PrivateVmOperation {
    param([hashtable]$Outputs,[string]$Operation,[string]$PackagePath,[string]$EvidenceDirectory,[int]$TimeoutSeconds,[switch]$Smoke)
    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw 'Missing Function package' }
    [IO.File]::AppendAllText($env:POC_FAKE_TRACE,"vm-operation $Operation package smoke=$([bool]$Smoke) timeout=$TimeoutSeconds`n")
    return [pscustomobject]@{ validation=@{Ready=$true;Smoke='Pass'} }
}
'@ | Add-Content -LiteralPath (Join-Path $temporary 'scripts/Invoke-PrivateVmOperation.ps1') -Encoding UTF8
    $env:POC_FAKE_TRACE=Join-Path $temporary 'trace.txt'
    $fakeDotnet=Join-Path $temporary 'dotnet.ps1'
    @'
[IO.File]::AppendAllText($env:POC_FAKE_TRACE,"dotnet $($args -join ' ')`n")
if ($args[0] -eq 'publish') {
    $publish=$args[[Array]::IndexOf($args,'-o')+1]
    $null=New-Item -ItemType Directory -Path (Join-Path $publish '.azurefunctions') -Force
    foreach ($file in @('host.json','worker.config.json','Poc.Functions.dll','.azurefunctions/worker.dll')) {
        [IO.File]::WriteAllText((Join-Path $publish $file),'fixture')
    }
}
'fake dotnet success'
exit 0
'@ | Set-Content -LiteralPath $fakeDotnet -Encoding UTF8
    $fakeTerraform=Join-Path $temporary 'terraform.ps1'
    @'
[IO.File]::AppendAllText($env:POC_FAKE_TRACE,"terraform $($args -join ' ')`n")
if (($env:Path -split [regex]::Escape([string][IO.Path]::PathSeparator))[0] -ne $PSScriptRoot) { throw 'Terraform must inherit the configured Azure CLI directory in PATH.' }
if ('show' -in $args) { '{"resource_changes":[]}' }
elseif ('output' -in $args) {
    '{"subscription_id":"target-subscription","resource_group_name":"poc-rg","function_app_name":"poc-app","function_app_url":"https://poc.example","storage_account_name":"pocstorage","blob_container_name":"poc-results","blob_endpoint":"https://pocstorage.blob.core.windows.net/","batch_endpoint":"https://batch.example","batch_pool_id":"pool","batch_account_name":"account","batch_resource_group_name":"batch-rg","batch_node_count":null,"validation_vm_name":"poc-vm","vm_control_container_name":"vm-control","function_private_ip":"10.20.2.4"}'
}
else { 'fake terraform success' }
exit 0
'@ | Set-Content -LiteralPath $fakeTerraform -Encoding UTF8
    $fakeAz=Join-Path $temporary 'az.ps1'
    @'
$command=$args -join ' '
[IO.File]::AppendAllText($env:POC_FAKE_TRACE,"az $command`n")
if ($command -match '^version') { '{"azure-cli":"2.80.0"}' }
elseif ($command -match '^account show') { '{"id":"target-subscription","state":"Enabled","tenantId":"target-tenant"}' }
elseif ($command -match '^provider show') { '{"registrationState":"Registered"}' }
elseif ($command -match '^group exists') {
    if ($env:POC_FAKE_BACKEND -eq 'forbidden') { 'AuthorizationFailed'; exit 1 }
    if ($env:POC_FAKE_BACKEND -eq 'present') { 'true' } else { 'false' }
}
elseif ($command -match '^storage account list') { '[{"name":"pocstate123"}]' }
elseif ($command -match '^storage container exists') { '{"exists":true}' }
elseif ($command -match '^rest .*?/nodes\?') { '{"value":[{"state":"idle","schedulingState":"enabled"}]}' }
elseif ($command -match '^rest ') { '{"allocationState":"steady"}' }
elseif ($command -match '^functionapp function list') {
    @('StartWorkflow','GetWorkflowStatus','GetWorkflowResult','CancelWorkflow','MainOrchestrator','MonitorBatchTaskOrchestrator','ClassifyActivity','EnsureBatchJobActivity','EnsureBatchTaskActivity','GetBatchTaskStatusActivity','FetchAndPersistActivity','AggregateActivity','PublishManifestActivity','WorkflowAdmissionEntity','TerminateBatchJobActivity') | ForEach-Object { @{name="poc-app/$_"} } | ConvertTo-Json
}
elseif ($command -match '^functionapp function keys list') { '{"default":"PRIVATE-FUNCTION-KEY"}' }
elseif ($command -match '^functionapp keys list') { '{"systemKeys":{"durabletask_extension":"PRIVATE-SYSTEM-KEY"}}' }
elseif ($command -match '^storage blob download') {
    $file=$args[[Array]::IndexOf($args,'--file')+1]
    $root="https://pocstorage.blob.core.windows.net/poc-results/runs/poc-$env:POC_FAKE_REQUESTID"
    $document=@{ requestId=$env:POC_FAKE_REQUESTID; sum=3; taskCount=3; jobId='poc-job'; fingerprint='hash' }
    if ([IO.Path]::GetFileName($file) -eq 'manifest.json') {
        $document.status='Succeeded'; $document.aggregateUri="$root/final/aggregate.json"
        $document.taskUris=@('task-00','task-01','task-02' | ForEach-Object { "$root/tasks/$_/result.json" })
    } else { $document.taskIds=@('task-00','task-01','task-02') }
    $document | ConvertTo-Json | Set-Content -LiteralPath $file -Encoding UTF8
    '{}'
}
else { '{}' }
exit 0
'@ | Set-Content -LiteralPath $fakeAz -Encoding UTF8
    $configPath=Join-Path $temporary 'config.psd1'
    $config=@"
@{
 Terraform=@{ subscription_id='target-subscription'; resource_group_name='poc-rg'; name_suffix='test01'; batch_mode='existing' }
 Backend=@{ resource_group_name='state-rg'; storage_account_name='pocstate123'; container_name='tfstate'; Create=`$false }
 Tools=@{ DotNet='$($fakeDotnet.Replace("'","''"))'; Terraform='$($fakeTerraform.Replace("'","''"))'; AzureCli='$($fakeAz.Replace("'","''"))' }
 TimeoutSeconds=60
}
"@
    Set-Content -LiteralPath $configPath -Value $config -Encoding UTF8
    & (Join-Path $temporary 'scripts/deploy.ps1') -Environment 'offline' -ConfigFile $configPath -PlanOnly
    Assert-True ($env:Path -eq $oldPath) 'Deployment must restore the caller PATH after PlanOnly'
    $trace=Get-Content -LiteralPath $env:POC_FAKE_TRACE
    Assert-True (@($trace | Where-Object { $_ -match 'terraform .* apply |config-zip|vm-operation' }).Count -eq 0) 'PlanOnly must not apply or deploy code'
    Clear-Content -LiteralPath $env:POC_FAKE_TRACE
    & (Join-Path $temporary 'scripts/deploy.ps1') -Environment 'offline' -ConfigFile $configPath
    $trace=@(Get-Content -LiteralPath $env:POC_FAKE_TRACE)
    $joined=$trace -join "`n"
    $apply=[Array]::FindIndex($trace,[Predicate[string]]{ param($line) $line -match 'terraform .* apply ' })
    $pool=[Array]::FindIndex($trace,[Predicate[string]]{ param($line) $line -match '^az rest ' })
    $vm=[Array]::FindIndex($trace,[Predicate[string]]{ param($line) $line -match '^vm-operation Deploy ' })
    Assert-True ($apply -ge 0 -and $pool -gt $apply -and $vm -gt $pool) 'Expected apply -> pool readiness -> deployment on private VM'
    Assert-True ($trace[$vm] -match 'smoke=False timeout=60') 'Default deployment must pass timeout and omit VM smoke'
    Assert-True (@($trace | Where-Object { $_ -match 'config-zip|^http |dotnet run --project tools/BatchSmoke' }).Count -eq 0) 'Deployment must not call the private endpoints or Batch smoke locally'
    $resultFile=Get-ChildItem -LiteralPath (Join-Path $temporary '.deploy/offline') -Filter deployment-result.json -Recurse | Select-Object -First 1
    $result=Get-Content -LiteralPath $resultFile.FullName -Raw | ConvertFrom-Json
    Assert-True ($result.validation.Ready -eq $true -and $result.validation.Smoke -eq 'Pass' -and $result.smoke_requested -eq $false) 'Deployment summary must retain VM validation results and requested smoke mode'
    Assert-True ($joined -match 'dotnet .*test --project' -and $joined -match 'dotnet publish') 'Deployment should test and publish the Functions'
    Assert-True (@($trace | Where-Object { $_ -match '^az ' -and $_ -notmatch '^az version' -and $_ -notmatch '--subscription target-subscription' }).Count -eq 0) 'Every Azure resource call must target configured subscription'
    Clear-Content -LiteralPath $env:POC_FAKE_TRACE
    & (Join-Path $temporary 'scripts/deploy.ps1') -Environment 'offline' -ConfigFile $configPath -Smoke
    $trace=@(Get-Content -LiteralPath $env:POC_FAKE_TRACE)
    $vm=[Array]::FindIndex($trace,[Predicate[string]]{ param($line) $line -match '^vm-operation Deploy ' })
    Assert-True ($vm -ge 0 -and $trace[$vm] -match 'smoke=True timeout=60') 'Smoke switch and configured timeout must survive dot-sourcing and reach the VM'
    Assert-True (@($trace | Where-Object { $_ -match '^az rest .*?/nodes\?' }).Count -eq 0) 'Existing pool smoke must submit tasks without a node-allocation precondition'
    Assert-True (($trace -join "`n") -notmatch 'PRIVATE-FUNCTION-KEY|code=PRIVATE') 'Trace must not contain Function or Durable status keys'
    $bootstrapConfigPath=Join-Path $temporary 'bootstrap-config.psd1'
    $bootstrapConfig=$config.Replace('Create=$false',"Create=`$true; location='japaneast'; deployer_principal_id='deployer-object-id'")
    Set-Content -LiteralPath $bootstrapConfigPath -Value $bootstrapConfig -Encoding UTF8
    $env:POC_FAKE_BACKEND='missing'
    Clear-Content -LiteralPath $env:POC_FAKE_TRACE
    & (Join-Path $temporary 'scripts/deploy.ps1') -Environment 'offline-bootstrap' -ConfigFile $bootstrapConfigPath -PlanOnly
    $trace=@(Get-Content -LiteralPath $env:POC_FAKE_TRACE)
    $bootstrapInit=@($trace | Where-Object { $_ -match '^terraform -chdir=infra/bootstrap init ' })
    Assert-True ($bootstrapInit.Count -eq 1) 'Backend.Create=true PlanOnly must initialize bootstrap'
    Assert-True (@($trace | Where-Object { $_ -match '^terraform -chdir=infra/bootstrap plan ' }).Count -eq 1) 'Backend PlanOnly must create bootstrap plan'
    Assert-True (@($trace | Where-Object { $_ -match '^terraform -chdir=infra init |terraform .* apply |config-zip|vm-operation' }).Count -eq 0) 'Bootstrap PlanOnly must not initialize main backend, apply any plan, or deploy code'
    $expectedState=Join-Path $temporary '.deploy/offline-bootstrap/bootstrap/terraform.tfstate'
    Assert-True ([IO.Path]::IsPathRooted($expectedState)) 'Expected bootstrap state path must be absolute'
    Assert-True ($bootstrapInit[0] -match [regex]::Escape("-backend-config=path=$expectedState")) 'Bootstrap init must explicitly pin absolute local state path'
    Clear-Content -LiteralPath $env:POC_FAKE_TRACE
    $env:POC_FAKE_BACKEND='present'
    & (Join-Path $temporary 'scripts/deploy.ps1') -Environment 'offline-bootstrap' -ConfigFile $bootstrapConfigPath -PlanOnly
    $trace=@(Get-Content -LiteralPath $env:POC_FAKE_TRACE)
    Assert-True (@($trace | Where-Object { $_ -match '^terraform -chdir=infra plan ' }).Count -eq 1) 'Existing backend must allow main plan with Create=true'
    Assert-True (@($trace | Where-Object { $_ -match 'terraform .* apply |config-zip|vm-operation' }).Count -eq 0) 'Existing backend PlanOnly must not apply or deploy'
    Clear-Content -LiteralPath $env:POC_FAKE_TRACE
    $env:POC_FAKE_BACKEND='forbidden'
    $failed=$false
    try { & (Join-Path $temporary 'scripts/deploy.ps1') -Environment 'offline-bootstrap' -ConfigFile $bootstrapConfigPath -PlanOnly }
    catch { $failed=$_.Exception.Message -match 'AuthorizationFailed' }
    Assert-True $failed 'Backend permission errors must fail instead of reporting absence'
    $trace=@(Get-Content -LiteralPath $env:POC_FAKE_TRACE)
    Assert-True (@($trace | Where-Object { $_ -match '^terraform -chdir=infra init |terraform .* apply |config-zip|vm-operation' }).Count -eq 0) 'Failed backend probe must stop deployment'
    $env:POC_FAKE_BACKEND='present'
    Clear-Content -LiteralPath $env:POC_FAKE_TRACE
    & (Join-Path $temporary 'scripts/deploy.ps1') -Environment 'offline-bootstrap' -ConfigFile $bootstrapConfigPath
    $trace=@(Get-Content -LiteralPath $env:POC_FAKE_TRACE)
    $bootstrapApply=[Array]::FindIndex($trace,[Predicate[string]]{ param($line) $line -match '^terraform -chdir=infra/bootstrap apply ' })
    $mainInit=[Array]::FindIndex($trace,[Predicate[string]]{ param($line) $line -match '^terraform -chdir=infra init ' })
    $mainApply=[Array]::FindIndex($trace,[Predicate[string]]{ param($line) $line -match '^terraform -chdir=infra apply ' })
    Assert-True ($bootstrapApply -ge 0 -and $mainInit -gt $bootstrapApply -and $mainApply -gt $mainInit) 'New backend must bootstrap apply before main backend init and main apply'
    $bootstrapInit=@($trace | Where-Object { $_ -match '^terraform -chdir=infra/bootstrap init ' })
    Assert-True ($bootstrapInit.Count -eq 1 -and $bootstrapInit[0] -match [regex]::Escape("-backend-config=path=$expectedState")) 'Backend state path must remain stable across PlanOnly and apply'
    $identityPath=Join-Path $temporary '.deploy/offline/environment.json'
    $legacy=Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
    $legacy.PSObject.Properties.Remove('durable_backend'); $legacy.PSObject.Properties.Remove('durable_task_hub_name')
    $legacy | ConvertTo-Json | Set-Content -LiteralPath $identityPath -Encoding UTF8
    & (Join-Path $temporary 'scripts/deploy.ps1') -Environment 'offline' -ConfigFile $configPath -PlanOnly
    $identity=Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
    Assert-True ($identity.durable_backend -ceq 'azureStorage' -and $identity.durable_task_hub_name -ceq 'PocBatchHub') 'Successful legacy invocation must persist the original durable identity'
    $schedulerConfig=$config.Replace("batch_mode='existing'","batch_mode='existing'; durable_backend='durableTaskScheduler'; durable_task_hub_name='SchedulerHub'")
    Set-Content -LiteralPath $configPath -Value $schedulerConfig -Encoding UTF8
    foreach ($name in @('offline','other-environment')) {
        Clear-Content -LiteralPath $env:POC_FAKE_TRACE
        $failed=$false
        try { & (Join-Path $temporary 'scripts/deploy.ps1') -Environment $name -ConfigFile $configPath -PlanOnly }
        catch { $failed=$_.Exception.Message -match 'identity changed' }
        Assert-True $failed 'Changing durable identity of an existing Function must fail even through a new local Environment'
        Assert-True ((Get-Content -LiteralPath $env:POC_FAKE_TRACE -Raw) -notmatch 'terraform .* (init|apply)|dotnet restore') 'Backend switch must fail before build or state access'
    }
    $schedulerConfig=$schedulerConfig.Replace("name_suffix='test01'","name_suffix='scheduler'")
    Set-Content -LiteralPath $configPath -Value $schedulerConfig -Encoding UTF8
    Clear-Content -LiteralPath $env:POC_FAKE_TRACE
    & (Join-Path $temporary 'scripts/deploy.ps1') -Environment 'scheduler' -ConfigFile $configPath -PlanOnly
    Assert-True ((Get-Content -LiteralPath $env:POC_FAKE_TRACE -Raw) -match 'provider show --namespace Microsoft.DurableTask') 'Scheduler deployment must check its resource provider registration'
    $strictConfig=$config.Replace("batch_mode='existing'", "batch_mode='existing'; workflow_auth_mode='EntraEasyAuth'; workflow_auth_tenant_id='11111111-1111-1111-1111-111111111111'; workflow_auth_api_client_id='22222222-2222-2222-2222-222222222222'; workflow_auth_audience='api://workflow'; workflow_auth_required_app_role='Workflow.Invoke'; workflow_auth_allowed_backend_app_ids=@('33333333-3333-3333-3333-333333333333')")
    Set-Content -LiteralPath $configPath -Value $strictConfig -Encoding UTF8
    Clear-Content -LiteralPath $env:POC_FAKE_TRACE
    Assert-Fails { & (Join-Path $temporary 'scripts/deploy.ps1') -Environment 'strict' -ConfigFile $configPath -Smoke } 'does not support deploy -Smoke'
    Assert-True ([string]::IsNullOrWhiteSpace((Get-Content -LiteralPath $env:POC_FAKE_TRACE -Raw))) 'Strict legacy smoke must fail before tools, Azure or mutation'
    & (Join-Path $temporary 'scripts/deploy.ps1') -Environment 'strict' -ConfigFile $configPath -PlanOnly
    $strictVars=Get-ChildItem -LiteralPath (Join-Path $temporary '.deploy/strict') -Filter deployment.tfvars.json -Recurse | Select-Object -First 1 | Get-Content -Raw | ConvertFrom-Json
    Assert-True ($strictVars.workflow_auth_mode -ceq 'EntraEasyAuth' -and $strictVars.workflow_auth_audience -ceq 'api://workflow' -and $strictVars.workflow_auth_allowed_backend_app_ids.Count -eq 1) 'Strict PSD1 settings must reach generated Terraform variables without changing the audience'
    $changed=$config.Replace("name_suffix='test01'","name_suffix='changed'")
    Set-Content -LiteralPath $configPath -Value $changed -Encoding UTF8
    $failed=$false
    try { & (Join-Path $temporary 'scripts/deploy.ps1') -Environment 'offline' -ConfigFile $configPath }
    catch { $failed=$_.Exception.Message -match 'identity changed' }
    Assert-True $failed 'Deployment must reject changing persisted environment identity'
    Write-Host 'Deploy workflow offline tests: PASS (PlanOnly, bootstrap PlanOnly/apply/state path, private VM sequence, subscription, VM Smoke switch, environment identity; no Azure calls).'
} finally {
    $env:POC_FAKE_TRACE=$oldTrace
    $env:POC_FAKE_DEFAULT=$oldDefault
    $env:POC_FAKE_REQUESTID=$oldRequest
    $env:POC_FAKE_BACKEND=$oldBackend
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
