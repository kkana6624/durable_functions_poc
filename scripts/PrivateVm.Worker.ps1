# Guest-only entry point, delivered in a hash-checked bundle by Invoke-PrivateVmOperation.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
. (Join-Path $PSScriptRoot 'Test-Deployment.ps1')

function Get-PrivateScmHost([hashtable]$Outputs) {
    # Read the actual SCM name instead of guessing its position in a regional or
    # generated default hostname. This uses the existing sites/read permission.
    # CLI functionapp show enriches its response with configuration/publishing
    # reads. A direct ARM GET requires only the scoped sites/read permission.
    $cloud=Invoke-AzJson -Sensitive -Arguments @('cloud','show')
    $arm=[string]$cloud.endpoints.resourceManager
    if (-not $arm) { throw 'Resource Manager endpoint is unavailable.' }
    $subscription=[Uri]::EscapeDataString([string]$Outputs.subscription_id)
    $resourceGroup=[Uri]::EscapeDataString([string]$Outputs.resource_group_name)
    $site=[Uri]::EscapeDataString([string]$Outputs.function_app_name)
    $uri=$arm.TrimEnd('/')+"/subscriptions/$subscription/resourceGroups/$resourceGroup/providers/Microsoft.Web/sites/$site`?api-version=2024-11-01"
    $response=Invoke-AzJson -Sensitive -Arguments @('rest','--method','get','--url',$uri)
    $app=$response.properties
    if ($app.defaultHostName -ne ([Uri]$Outputs.function_app_url).DnsSafeHost) { throw 'Function hostname does not match deployment metadata.' }
    $hosts=@($app.enabledHostNames | Where-Object { $_ -match '^[a-zA-Z0-9.-]+\.scm\.[a-zA-Z0-9.-]*azurewebsites\.net$' })
    if ($hosts.Count -ne 1) { throw 'Expected exactly one SCM hostname.' }
    return [string]$hosts[0]
}

function Assert-PrivateFunctionDns([hashtable]$Outputs) {
    $hostName=([Uri]$Outputs.function_app_url).DnsSafeHost
    $scmHost=Get-PrivateScmHost $Outputs
    foreach ($name in @($hostName,$scmHost)) {
        $addresses=@([Net.Dns]::GetHostAddresses($name) | ForEach-Object { $_.IPAddressToString })
        if ($addresses.Count -eq 0 -or @($addresses | Where-Object { $_ -ne $Outputs.function_private_ip }).Count -gt 0) { throw 'Function/SCM DNS did not resolve exclusively to the expected Private Endpoint IP.' }
    }
}

function Assert-PrivateFunctionConnectivity([hashtable]$Outputs, [int]$TimeoutSeconds) {
    $workflowUri = [string]$Outputs.function_app_url.TrimEnd('/') + '/api/workflows'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Add-Type -AssemblyName System.Net.Http
    $handler = [Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler); $client.Timeout = [TimeSpan]::FromSeconds(10)
    try {
        do {
            try {
                $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $workflowUri)
                try {
                    $response = $client.SendAsync($request).GetAwaiter().GetResult()
                    $code = [int]$response.StatusCode
                    if ($code -in @(200, 401, 403, 404, 405)) { return }
                } finally { $request.Dispose() }
            } catch { }
            $remaining = $TimeoutSeconds - $watch.Elapsed.TotalSeconds
            if ($remaining -le 0) { break }
            Start-Sleep -Seconds ([Math]::Min(5, [Math]::Max(1, [Math]::Ceiling($remaining))))
        } while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds)
        throw "Function private HTTP endpoint did not respond successfully within $TimeoutSeconds seconds."
    } finally { $client.Dispose(); $handler.Dispose() }
}

function Publish-PrivateFunctionPackage([hashtable]$Outputs,[string]$Path,[int]$TimeoutSeconds) {
    $script:PrivateVmScmDiagnostics=@{ stage='scm_host'; http_status=$null }
    $hostName=Get-PrivateScmHost $Outputs
    $script:PrivateVmScmDiagnostics.stage='cloud_metadata'
    $cloud=Invoke-AzJson -Sensitive -Arguments @('cloud','show')
    $resource=[string]$cloud.endpoints.appServiceResourceId
    if (-not $resource) { throw 'App Service token audience is unavailable.' }
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $uri="https://$hostName/api/zipdeploy?isAsync=true"
    $next=$null
    do {
        # Refresh through CLI's token cache; never emit a token or credential-bearing error.
        $script:PrivateVmScmDiagnostics.stage='token'
        $credential=Invoke-AzJson -Sensitive -Arguments @('account','get-access-token','--resource',$resource)
        $headers=@{ Authorization="Bearer $($credential.accessToken)" }
        try {
            $remaining=[Math]::Max(1,[Math]::Ceiling($TimeoutSeconds-$watch.Elapsed.TotalSeconds))
            $parameters=@{ Uri=$(if ($next) { $next } else { $uri }); Headers=$headers; TimeoutSec=[Math]::Min(120,$remaining); MaximumRedirection=0; UseBasicParsing=$true; ErrorAction='Stop' }
            if (-not $next) {
                $script:PrivateVmScmDiagnostics.stage='scm_upload'
                $response=Invoke-WebRequest @parameters -Method Post -ContentType 'application/zip' -InFile $Path
                if ([int]$response.StatusCode -ne 202 -or -not $response.Headers.Location) { throw 'Missing asynchronous deployment response.' }
                $location=[string](@($response.Headers.Location)[0])
                $candidate=[Uri]::new([Uri]$uri,$location)
                if ($candidate.Scheme -ne 'https' -or $candidate.DnsSafeHost -ne $hostName -or $candidate.Port -ne 443 -or $candidate.UserInfo -or -not $candidate.AbsolutePath.StartsWith('/api/deployments/')) { throw 'Invalid deployment polling location.' }
                $next=$candidate.AbsoluteUri
            } else {
                $script:PrivateVmScmDiagnostics.stage='scm_poll'
                $response=Invoke-WebRequest @parameters -Method Get
                if ([int]$response.StatusCode -notin @(200,202)) { throw 'Unexpected deployment response.' }
                if ($response.Content) {
                    $state=$response.Content | ConvertFrom-Json
                    if ([int]$state.status -eq 3) { throw 'ZIP deployment failed.' }
                    if ([int]$state.status -eq 4) { return }
                } elseif ([int]$response.StatusCode -eq 200) { throw 'Missing deployment status.' }
            }
        } catch {
            $responseProperty=$_.Exception.PSObject.Properties['Response']
            if ($responseProperty -and $responseProperty.Value) {
                $statusProperty=$responseProperty.Value.PSObject.Properties['StatusCode']
                if ($statusProperty -and $null -ne $statusProperty.Value) {
                    $status=[int]$statusProperty.Value
                    if ($status -ge 100 -and $status -le 599) { $script:PrivateVmScmDiagnostics.http_status=$status }
                }
            }
            throw 'Private SCM ZIP deployment failed; HTTP details suppressed.'
        }
        finally { $credential=$null; $headers=$null }
        if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw 'Private SCM ZIP deployment timed out.' }
        Start-Sleep -Seconds ([Math]::Min(5,[Math]::Max(1,[Math]::Ceiling($TimeoutSeconds-$watch.Elapsed.TotalSeconds))))
    } while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw 'Private SCM ZIP deployment timed out.'
}

if ($MyInvocation.InvocationName -eq '.') { return }

$request=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'request.json') -Raw | ConvertFrom-Json
$outputs=ConvertTo-DeploymentHashtable $request.outputs
$script:DeploymentSubscriptionId=$outputs.subscription_id
$script:AzPath=Resolve-DeploymentTool -Name az -RepositoryRoot $PSScriptRoot
$evidence=Join-Path $PSScriptRoot 'evidence'
$null=New-Item -ItemType Directory -Path $evidence -Force
$result=@{ operation_id=[string]$request.operation_id; operation=[string]$request.operation; exit_code=1; error_code='PrivateVmOperationFailed'; failure_stage=$null; scm_failure_stage=$null; http_status=$null; summary_available=$false; completed_utc=$null }
$stage='dns'
try {
    Assert-PrivateFunctionDns $outputs
    if ($request.operation -eq 'Deploy') {
        $stage='package_integrity'
        $package=Join-Path $PSScriptRoot 'functions.zip'
        if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $request.package_sha256) { throw 'Package hash mismatch.' }
        $stage='scm_publish'
        Publish-PrivateFunctionPackage $outputs $package ([int]$request.timeout_seconds)
        $result.package_sha256=$request.package_sha256
        $result.exit_code=0
    } elseif ($request.operation -eq 'FunctionValidation') {
        $watch=[Diagnostics.Stopwatch]::StartNew()
        $remaining=[Math]::Max(1,[Math]::Floor([int]$request.timeout_seconds-$watch.Elapsed.TotalSeconds))
        $stage='function_connectivity'
        Assert-PrivateFunctionConnectivity $outputs ([Math]::Min(120, $remaining))
        $remaining=[Math]::Max(1,[Math]::Floor([int]$request.timeout_seconds-$watch.Elapsed.TotalSeconds))
        $stage='function_validation'
        $validation=Invoke-DeploymentValidation -Outputs $outputs -Phase Functions -TimeoutSeconds $remaining -Smoke:([bool]$request.smoke)
        $result.validation=$validation
        $result.exit_code=0
    } elseif ($request.operation -eq 'Validate') {
        $stage='validation_runner'
        $deployment=Join-Path $PSScriptRoot 'deployment-result.json'
        Write-DeploymentJson $deployment @{ outputs=$outputs; source_provenance=$request.source_provenance; package_sha256=$request.package_sha256 }
        # A child process contains the runner's exit statement. Its output is never forwarded.
        # PowerShell -File consumes only one array item on native invocation. Use a fixed
        # command that reads the already hash-verified JSON instead of interpolating values.
        $runner=@'
$r=Get-Content -LiteralPath ./request.json -Raw | ConvertFrom-Json
& ./Invoke-PocValidation.ps1 -DeploymentResultPath ./deployment-result.json -EvidenceDirectory ./evidence -TimeoutSeconds ([int]$r.timeout_seconds) -Scenarios ([string[]]$r.scenarios) -DedicatedEnvironment:([bool]$r.dedicated_environment)
exit $LASTEXITCODE
'@
        Push-Location $PSScriptRoot
        try {
            $null=& (Join-Path $PSHOME 'pwsh') -NoLogo -NoProfile -NonInteractive -Command $runner 2>&1
            $result.exit_code=[int]$LASTEXITCODE
        } finally { Pop-Location }
        if ($result.exit_code -notin @(0,1,2)) { $result.exit_code=1 }
        if ($result.exit_code -eq 1) { $result.failure_stage='validation_runner' }
        $stage='validation_summary'
        $result.summary_available=(Test-Path -LiteralPath (Join-Path $evidence 'summary.json')) -and (Test-Path -LiteralPath (Join-Path $evidence 'summary.txt'))
        if (-not $result.summary_available) { $result.exit_code=1; throw 'Missing sanitized validation summary.' }
        $stage='summary_upload'
        foreach ($file in @('summary.json','summary.txt')) {
            $null=Invoke-AzJson -Sensitive -Arguments @('storage','blob','upload','--account-name',$outputs.storage_account_name,'--container-name',$outputs.vm_control_container_name,'--name',"operations/$($request.operation_id)/$file",'--file',(Join-Path $evidence $file),'--auth-mode','login','--overwrite','false','--no-progress')
        }
    } elseif ($request.operation -in @('RecoveryStart','RecoveryComplete')) {
        $stage='validation_runner'
        . (Join-Path $PSScriptRoot 'Invoke-PocRecoveryValidation.ps1')
        $recoveryPhase=if ($request.operation -eq 'RecoveryStart') { 'Start' } else { 'Complete' }
        $summary=Invoke-PocRecoveryGuest -Outputs $outputs -Phase $recoveryPhase -Context (ConvertTo-DeploymentHashtable $request.recovery_context) -EvidenceDirectory $evidence -TimeoutSeconds ([int]$request.timeout_seconds)
        $result.exit_code=[int]$summary.ExitCode
        if ($result.exit_code -notin @(0,1,2)) { $result.exit_code=1 }
        if ($result.exit_code -eq 1) { $result.failure_stage='validation_runner' }
        $stage='validation_summary'
        $result.summary_available=(Test-Path -LiteralPath (Join-Path $evidence 'summary.json')) -and (Test-Path -LiteralPath (Join-Path $evidence 'summary.txt'))
        if (-not $result.summary_available) { throw 'Missing sanitized recovery summary.' }
        $stage='summary_upload'
        foreach ($file in @('summary.json','summary.txt')) {
            $null=Invoke-AzJson -Sensitive -Arguments @('storage','blob','upload','--account-name',$outputs.storage_account_name,'--container-name',$outputs.vm_control_container_name,'--name',"operations/$($request.operation_id)/$file",'--file',(Join-Path $evidence $file),'--auth-mode','login','--overwrite','false','--no-progress')
        }
    } elseif ($request.operation -eq 'WorkflowAuth') {
        $stage='validation_runner'
        $deployment=Join-Path $PSScriptRoot 'deployment-result.json'
        $deploymentObj=@{ outputs=$outputs; source_provenance=$request.source_provenance; package_sha256=$request.package_sha256 }
        if ($request.PSObject.Properties['assembly_sha256'] -and $request.assembly_sha256) { $deploymentObj.assembly_sha256 = $request.assembly_sha256 }
        if ($request.PSObject.Properties['package_content_sha256'] -and $request.package_content_sha256) { $deploymentObj.package_content_sha256 = $request.package_content_sha256 }
        Write-DeploymentJson $deployment $deploymentObj
        $runner=@'
$r=Get-Content -LiteralPath ./request.json -Raw | ConvertFrom-Json
$authParams = @{
    DeploymentResultPath = './deployment-result.json'
    Resource = [string]$r.auth_resource
    OwnerId = [string]$r.auth_owner_id
    OrgId = [string]$r.auth_org_id
    EvidenceDirectory = './evidence'
    TimeoutSeconds = [int]$r.timeout_seconds
}
if ($r.PSObject.Properties['auth_unauthorized_client_id'] -and $r.auth_unauthorized_client_id) {
    $authParams['UnauthorizedManagedIdentityClientId'] = [string]$r.auth_unauthorized_client_id
}
if ($r.PSObject.Properties['auth_managed_identity_client_id'] -and $r.auth_managed_identity_client_id) {
    $authParams['ManagedIdentityClientId'] = [string]$r.auth_managed_identity_client_id
}
& ./Invoke-WorkflowAuthValidation.ps1 @authParams
exit $LASTEXITCODE
'@
        Push-Location $PSScriptRoot
        try {
            $null=& (Join-Path $PSHOME 'pwsh') -NoLogo -NoProfile -NonInteractive -Command $runner 2>&1
            $result.exit_code=[int]$LASTEXITCODE
        } finally { Pop-Location }
        if ($result.exit_code -notin @(0,1,2)) { $result.exit_code=1 }
        if ($result.exit_code -eq 1) { $result.failure_stage='validation_runner' }
        $stage='validation_summary'
        $result.summary_available=(Test-Path -LiteralPath (Join-Path $evidence 'summary.json'))
        if (-not $result.summary_available) { throw 'Missing sanitized workflow auth summary.' }
        $stage='summary_upload'
        $null=Invoke-AzJson -Sensitive -Arguments @('storage','blob','upload','--account-name',$outputs.storage_account_name,'--container-name',$outputs.vm_control_container_name,'--name',"operations/$($request.operation_id)/summary.json",'--file',(Join-Path $evidence 'summary.json'),'--auth-mode','login','--overwrite','false','--no-progress')
    } else { $stage='operation'; throw 'Unknown private VM operation.' }
    $result.error_code=$(if ($result.exit_code -eq 1) { 'ValidationFailed' } else { $null })
} catch {
    # No exception messages: HTTP requests and deployment responses can carry secrets.
    $result.exit_code=1
    $result.error_code='PrivateVmOperationFailed'
    $result.failure_stage=$stage
    if ($stage -eq 'scm_publish' -and (Get-Variable -Name PrivateVmScmDiagnostics -Scope Script -ErrorAction SilentlyContinue)) {
        $result.scm_failure_stage=$script:PrivateVmScmDiagnostics.stage
        $result.http_status=$script:PrivateVmScmDiagnostics.http_status
    }
} finally {
    $result.completed_utc=[DateTime]::UtcNow.ToString('o')
    $resultPath=Join-Path $evidence 'result.json'
    Write-DeploymentJson $resultPath $result
    $null=Invoke-AzJson -Sensitive -Arguments @('storage','blob','upload','--account-name',$outputs.storage_account_name,'--container-name',$outputs.vm_control_container_name,'--name',"operations/$($request.operation_id)/result.json",'--file',$resultPath,'--auth-mode','login','--overwrite','false','--no-progress')
}
# Failure/NotRun are represented in the JSON. Transport completion is independent.
exit 0
