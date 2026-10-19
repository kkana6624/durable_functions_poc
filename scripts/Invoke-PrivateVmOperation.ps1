# Shared transport for private Function deployment and validation. No keys or SAS are transferred.
function Get-PrivateVmRunCommandStdout {
    param($Response)
    $stdout=@()
    if (-not $Response -or -not $Response.PSObject.Properties['value']) { return '' }
    foreach ($entry in @($Response.value)) {
        if ($entry.code -like '*/StdOut/succeeded') { $stdout += [string]$entry.message }
        elseif ($entry.code -eq 'ProvisioningState/succeeded') {
            # Linux Action Run Command can combine the streams into one status
            # message. Only its stdout section may contain the completion marker.
            $match=[regex]::Match([string]$entry.message,'(?ms)^\[stdout\]\r?\n(?<stdout>.*?)^\[stderr\](?:\r?\n|$)')
            if ($match.Success) { $stdout += $match.Groups['stdout'].Value }
        }
    }
    return $stdout -join "`n"
}

function Invoke-PrivateVmOperation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$Outputs,
        [Parameter(Mandatory)][ValidateSet('Deploy','Validate','RecoveryStart','RecoveryComplete','WorkflowAuth','FunctionValidation','PoolValidation','AutoscaleValidation')][string]$Operation,
        [Parameter(Mandatory)][string]$EvidenceDirectory,
        [string]$PackagePath,
        [ValidatePattern('^$|^[0-9a-fA-F]{64}$')][string]$PackageSha256='',
        [ValidateRange(60,5400)][int]$TimeoutSeconds=900,
        [ValidateSet('single','three','classification','failure','admission20','conflict','timeout','cancellation')][string[]]$Scenarios=@('single','three','classification','failure','admission20','conflict'),
        [switch]$Smoke,
        [switch]$DedicatedEnvironment,
        [hashtable]$RecoveryContext=@{},
        [string]$AuthResource='',
        [string]$AuthOwnerId='',
        [string]$AuthOrgId='',
        [string]$AuthUnauthorizedClientId='',
        [string]$AuthManagedIdentityClientId='',
        [string]$AssemblySha256='',
        [string]$PackageContentSha256='',
        [string]$ExpectedFunctionAppName='',
        [string]$ExpectedPoolId='',
        [switch]$AllowPoolAutoscaleTransition
    )
    foreach ($key in @('subscription_id','resource_group_name','validation_vm_name','vm_control_container_name','storage_account_name','function_app_name','function_app_url','function_private_ip')) {
        if (-not $Outputs.ContainsKey($key) -or [string]::IsNullOrWhiteSpace([string]$Outputs[$key])) { throw "Private VM output '$key' is required; apply the private network infrastructure first." }
    }
    if ($Operation -eq 'Deploy' -and (-not $PackagePath -or -not (Test-Path -LiteralPath $PackagePath -PathType Leaf))) { throw 'Deploy requires a Function ZIP package.' }
    if ($Operation -eq 'Validate' -and $Scenarios.Count -eq 0) { throw 'Select at least one scenario.' }
    if ($Operation -in @('RecoveryStart','RecoveryComplete')) {
        . (Join-Path $PSScriptRoot 'Recovery.Context.ps1')
        $phase=if ($Operation -eq 'RecoveryStart') { 'Start' } else { 'Complete' }
        $RecoveryContext=Assert-PocRecoveryContext -Phase $phase -Context $RecoveryContext
    }
    if ($Operation -eq 'WorkflowAuth') {
        if ([string]::IsNullOrWhiteSpace($AuthResource) -or [string]::IsNullOrWhiteSpace($AuthOwnerId) -or [string]::IsNullOrWhiteSpace($AuthOrgId)) {
            throw 'WorkflowAuth requires AuthResource, AuthOwnerId, and AuthOrgId.'
        }
    }
    $script:DeploymentSubscriptionId=$Outputs.subscription_id
    $id=[Guid]::NewGuid().ToString('N')
    $work=Join-Path ([IO.Path]::GetTempPath()) "poc-vm-$id"
    $bundle=Join-Path $work 'bundle'
    $null=New-Item -ItemType Directory -Path $bundle -Force
    $null=New-Item -ItemType Directory -Path $EvidenceDirectory -Force
    try {
        foreach ($file in @('Deployment.Common.ps1','Validation.Assertions.ps1','Test-Deployment.ps1','Invoke-PocValidation.ps1','PrivateVm.Worker.ps1')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $bundle
        }
        if ($Operation -in @('RecoveryStart','RecoveryComplete')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Invoke-PocRecoveryValidation.ps1') -Destination $bundle
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Recovery.Context.ps1') -Destination $bundle
        }
        if ($Operation -eq 'WorkflowAuth') {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Invoke-WorkflowAuthValidation.ps1') -Destination $bundle
        }
        if ($Operation -eq 'AutoscaleValidation') {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Invoke-PocAutoscaleValidation.ps1') -Destination $bundle
        }
        $request=@{ operation=$Operation; outputs=$Outputs; timeout_seconds=$TimeoutSeconds; scenarios=@($Scenarios); smoke=[bool]$Smoke; dedicated_environment=[bool]$DedicatedEnvironment; operation_id=$id; package_sha256=$PackageSha256; assembly_sha256=$AssemblySha256; package_content_sha256=$PackageContentSha256 }
        if ($Operation -in @('RecoveryStart','RecoveryComplete')) { $request.recovery_context=$RecoveryContext }
        if ($Operation -eq 'WorkflowAuth') {
            $request.auth_resource=$AuthResource
            $request.auth_owner_id=$AuthOwnerId
            $request.auth_org_id=$AuthOrgId
            if ($AuthUnauthorizedClientId) { $request.auth_unauthorized_client_id=$AuthUnauthorizedClientId }
            if ($AuthManagedIdentityClientId) { $request.auth_managed_identity_client_id=$AuthManagedIdentityClientId }
        }
        if ($Operation -eq 'AutoscaleValidation') {
            $request.expected_function_app_name=$ExpectedFunctionAppName
            $request.expected_pool_id=$ExpectedPoolId
            $request.allow_pool_autoscale_transition=[bool]$AllowPoolAutoscaleTransition
        }
        # Preserve the source identity on the initiating host. The guest executes a
        # bundle without .git and must not mislabel that bundle as a Git checkout.
        $repository=Split-Path $PSScriptRoot -Parent
        $revision=@(& git -C $repository rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -ne 0 -or $revision.Count -ne 1 -or $revision[0] -notmatch '^[0-9a-f]{40}$') { throw 'A Git checkout is required to record validation source provenance.' }
        $paths=@(& git -C $repository -c core.quotepath=false ls-files --cached --others --exclude-standard 2>$null | Sort-Object -Unique)
        if ($LASTEXITCODE -ne 0) { throw 'Unable to fingerprint source files.' }
        $entries=@($paths | ForEach-Object {
            $file=Join-Path $repository $_
            if (Test-Path -LiteralPath $file -PathType Leaf) { $_+':'+(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() } else { $_+':missing' }
        })
        $hash=[Security.Cryptography.SHA256]::Create()
        try { $sourceHash=([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes(($entries -join "`n"))))).Replace('-','').ToLowerInvariant() } finally { $hash.Dispose() }
        $request.source_provenance=@{ gitRevision=$revision[0]; gitDirty=[bool]@(& git -C $repository status --porcelain 2>$null).Count; sourceSha256=$sourceHash }
        if ($Operation -eq 'Deploy') {
            Copy-Item -LiteralPath $PackagePath -Destination (Join-Path $bundle 'functions.zip')
            $request.package_sha256=(Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
        }
        Write-DeploymentJson (Join-Path $bundle 'request.json') $request
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive=Join-Path $work 'bundle.zip'
        [IO.Compression.ZipFile]::CreateFromDirectory($bundle,$archive)
        $blob="operations/$id/bundle.zip"
        $null=Invoke-AzJson -Sensitive -Arguments @('storage','blob','upload','--account-name',$Outputs.storage_account_name,'--container-name',$Outputs.vm_control_container_name,'--name',$blob,'--file',$archive,'--auth-mode','login','--overwrite','false','--no-progress')
        # Only non-secret names and a hash enter Run Command (which is a privileged management operation).
        $transport=@{ subscription=$Outputs.subscription_id; account=$Outputs.storage_account_name; container=$Outputs.vm_control_container_name; blob=$blob; sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash; operation_id=$id }
        $encoded=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($transport | ConvertTo-Json -Compress)))
        $guestTimeoutSec = if ($Operation -eq 'AutoscaleValidation') { [Math]::Min(5300, $TimeoutSeconds + 1320) } else { [Math]::Max(900, [Math]::Min(5300, $TimeoutSeconds + 600)) }
        $shell=@'
#!/bin/sh
set -eu
umask 077
if ! timeout 900 cloud-init status --wait >/dev/null 2>&1; then
  echo 'Private VM initialization failed or timed out.' >&2
  exit 1
fi
work=$(mktemp -d /var/tmp/poc-vm-XXXXXXXX)
export AZURE_CONFIG_DIR="$work/azure"
export POC_VM_TRANSPORT='__TRANSPORT__'
# Worker output is deliberately suppressed; only allowlisted JSON artifacts are returned via Blob.
if ! timeout __GUEST_TIMEOUT__ pwsh -NoLogo -NoProfile -NonInteractive -Command '
  $ErrorActionPreference="Stop"
  try {
    $t=$env:POC_VM_TRANSPORT | ForEach-Object { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($_)) } | ConvertFrom-Json
    az login --identity --output none 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Login" }
    az account set --subscription $t.subscription 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Subscription" }
    $archive=Join-Path $PWD "bundle.zip"
    $downloaded=$false
    for ($attempt=0; $attempt -lt 30; $attempt++) {
      az storage blob download --account-name $t.account --container-name $t.container --name $t.blob --file $archive --overwrite true --auth-mode login --no-progress --only-show-errors --output none 2>$null
      if ($LASTEXITCODE -eq 0) { $downloaded=$true; break }
      Start-Sleep -Seconds 10
    }
    if (-not $downloaded -or (Get-FileHash $archive -Algorithm SHA256).Hash -ne $t.sha256) { throw "BundleIntegrity" }
    Expand-Archive -LiteralPath $archive -DestinationPath "bundle"
    & ./bundle/PrivateVm.Worker.ps1
    if ($LASTEXITCODE -ne 0) { throw "Worker" }
  } catch { [Console]::Error.WriteLine("Private VM operation failed; inspect the sanitized result if available."); exit 1 }
' >"$work/worker.log" 2>&1; then
  echo 'Private VM operation failed; no raw worker output is returned.' >&2
  exit 1
fi
echo 'Private VM operation completed; sanitized results are in the control container.'
'@
        # Execute from the operation directory so concurrent runs do not share files.
        $shell=$shell.Replace('export AZURE_CONFIG_DIR=', 'cd "$work"' + "`n" + 'export AZURE_CONFIG_DIR=').Replace('__TRANSPORT__',$encoded).Replace('__GUEST_TIMEOUT__',[string]$guestTimeoutSec)
        $shellPath=Join-Path $work 'run.sh'
        [IO.File]::WriteAllText($shellPath,$shell.Replace("`r`n","`n"),[Text.UTF8Encoding]::new($false))
        $commandFailed=$false
        try {
            $command=Invoke-AzJson -Sensitive -Arguments @('vm','run-command','invoke','--resource-group',$Outputs.resource_group_name,'--name',$Outputs.validation_vm_name,'--command-id','RunShellScript','--scripts',"@$shellPath")
            $stdout=Get-PrivateVmRunCommandStdout $command
            if ($stdout -notmatch '(?m)^Private VM operation completed; sanitized results are in the control container\.\r?$') { $commandFailed=$true }
        } catch { $commandFailed=$true }
        # Action Run Command can succeed at ARM level even when the guest script fails.
        $resultPath=Join-Path $EvidenceDirectory 'vm-operation.json'
        try {
            $null=Invoke-AzJson -Sensitive -Arguments @('storage','blob','download','--account-name',$Outputs.storage_account_name,'--container-name',$Outputs.vm_control_container_name,'--name',"operations/$id/result.json",'--file',$resultPath,'--overwrite','true','--auth-mode','login','--no-progress')
        } catch {
            Write-DeploymentJson $resultPath @{ operation_id=$id; operation=$Operation; exit_code=1; error_code='PrivateVmResultUnavailable'; summary_available=$false; completed_utc=[DateTime]::UtcNow.ToString('o') }
            throw "Private VM operation $id did not return a result. Check VM provisioning, Run Command status, identity permissions and outbound connectivity; raw output suppressed."
        }
        $result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        if ($result.operation_id -ne $id -or $result.operation -ne $Operation -or [int]$result.exit_code -notin @(0,1,2)) { throw 'Private VM returned an invalid operation result.' }
        $failureStage=$null
        if ($result.PSObject.Properties['failure_stage'] -and $null -ne $result.failure_stage) {
            if ($result.failure_stage -notin @('dns','package_integrity','scm_publish','function_connectivity','function_validation','pool_validation','autoscale_validation','validation_runner','validation_summary','summary_upload','operation')) { throw 'Private VM returned an invalid failure stage.' }
            $failureStage=[string]$result.failure_stage
        }
        if ($Operation -in @('Validate','RecoveryStart','RecoveryComplete','WorkflowAuth','AutoscaleValidation') -and $result.summary_available) {
            $summaryNames = if ($result.PSObject.Properties['summary_artifacts'] -and $result.summary_artifacts.Count -gt 0) {
                @($result.summary_artifacts)
            } elseif ($Operation -eq 'WorkflowAuth') {
                @('summary.json')
            } elseif ($Operation -eq 'AutoscaleValidation') {
                @('autoscale-result.json','summary.json','summary.txt')
            } else {
                @('summary.json','summary.txt')
            }
            $retrievalErrors = @()
            foreach ($name in $summaryNames) {
                try {
                    $null=Invoke-AzJson -Sensitive -Arguments @('storage','blob','download','--account-name',$Outputs.storage_account_name,'--container-name',$Outputs.vm_control_container_name,'--name',"operations/$id/$name",'--file',(Join-Path $EvidenceDirectory $name),'--overwrite','true','--auth-mode','login','--no-progress')
                } catch {
                    $retrievalErrors += $name
                }
            }
            if ($retrievalErrors.Count -gt 0) {
                throw "Private VM operation $id completed (exit_code=$($result.exit_code)), but failed to retrieve evidence summary file(s) ($($retrievalErrors -join ', ')) from the control container."
            }
        }
        if ($commandFailed -or ($Operation -in @('Deploy','FunctionValidation','PoolValidation','AutoscaleValidation') -and [int]$result.exit_code -ne 0)) { throw "Private VM operation $id failed ($($result.error_code)); stage=$failureStage; raw output suppressed." }
        return $result
    }
    finally {
        # Verified task-owned temp path only. Remote control artifacts are retained for audit/explicit cleanup.
        $resolved=[IO.Path]::GetFullPath($work)
        $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
        if ($resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -eq "poc-vm-$id") { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
