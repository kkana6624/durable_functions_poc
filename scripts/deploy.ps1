#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9-]{0,30}$')][string] $Environment,
    [Parameter(Mandatory)][string] $ConfigFile,
    [switch] $PlanOnly,
    [switch] $Smoke
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/Deployment.Common.ps1"
. "$PSScriptRoot/Test-Deployment.ps1" -Smoke:$Smoke
. "$PSScriptRoot/Invoke-PrivateVmOperation.ps1"

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$config = Import-PowerShellDataFile -LiteralPath (Resolve-Path -LiteralPath $ConfigFile).Path
if (-not $config.ContainsKey('Terraform') -or -not $config.ContainsKey('Backend')) { throw 'Config must contain Terraform and Backend tables.' }
$variables = $config.Terraform
$workflowAuth = Get-DeploymentWorkflowAuthSettings -Variables $variables -Smoke:$Smoke
foreach ($key in $workflowAuth.Keys) { $variables[$key] = $workflowAuth[$key] }
foreach ($key in @('subscription_id', 'resource_group_name', 'name_suffix')) {
    if (-not $variables.ContainsKey($key) -or [string]::IsNullOrWhiteSpace($variables[$key])) { throw "Terraform.$key is required." }
}
if (-not $variables.ContainsKey('batch_mode')) { $variables.batch_mode = 'existing' }
$durableSettings = Get-DeploymentDurableSettings -Variables $variables
foreach ($key in $durableSettings.Keys) { $variables[$key] = $durableSettings[$key] }
$script:DeploymentSubscriptionId = $variables.subscription_id
$tools = if ($config.ContainsKey('Tools')) { $config.Tools } else { @{} }
$dotnet = Resolve-DeploymentTool -Name dotnet -ConfiguredPath $tools['DotNet'] -RepositoryRoot $repositoryRoot
$terraform = Resolve-DeploymentTool -Name terraform -ConfiguredPath $tools['Terraform'] -RepositoryRoot $repositoryRoot
$script:AzPath = Resolve-DeploymentTool -Name az -ConfiguredPath $tools['AzureCli'] -RepositoryRoot $repositoryRoot
$timeout = if ($config.ContainsKey('TimeoutSeconds')) { [int]$config.TimeoutSeconds } else { 1800 }
if ($timeout -lt 60 -or $timeout -gt 3600) { throw 'TimeoutSeconds must be between 60 and 3600 for private VM operations.' }
$backend = $config.Backend
foreach ($key in @('resource_group_name', 'storage_account_name', 'container_name')) {
    if (-not $backend.ContainsKey($key) -or -not $backend[$key]) { throw "Backend.$key is required." }
}
if ($backend.storage_account_name -notmatch '^poc[a-z0-9]{3,21}$') { throw 'Use a dedicated backend storage name starting with poc (6-24 lowercase alphanumeric characters).' }

$work = Join-Path $repositoryRoot ".deploy/$Environment"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$run = Join-Path $work (Get-Date -Format 'yyyyMMdd-HHmmss-ffff')
New-Item -ItemType Directory -Path $run | Out-Null
$previousData = $env:TF_DATA_DIR
$previousSubscription = $env:ARM_SUBSCRIPTION_ID
$previousAad = $env:ARM_USE_AZUREAD
$previousPath = $env:Path
$previousRoot = Get-Location
try {
    Set-Location $repositoryRoot
    # AzureRM launches az by name even when Config.Tools.AzureCli is explicit.
    # This also supports CLI installs made after this shell was started.
    $env:Path = (Split-Path $script:AzPath -Parent) + [IO.Path]::PathSeparator + $env:Path
    $env:ARM_SUBSCRIPTION_ID = $variables.subscription_id
    $env:ARM_USE_AZUREAD = 'true'
    Write-Host 'Checking tools, Azure login and resource providers...'
    $null = Invoke-Tool $dotnet @('--version')
    $null = Invoke-Tool $terraform @('version')
    $cliVersion = Invoke-AzJson @('version')
    if ([version]$cliVersion.'azure-cli' -lt [version]'2.48.1') { throw 'Azure CLI 2.48.1 or later is required for Entra ZIP deployment with Basic authentication disabled.' }
    $account = Invoke-AzJson @('account', 'show', '--subscription', $variables.subscription_id)
    if ($account.id -ne $variables.subscription_id -or $account.state -ne 'Enabled') { throw 'The target subscription is unavailable.' }
    $providers = @('Microsoft.Web', 'Microsoft.Storage', 'Microsoft.Insights', 'Microsoft.OperationalInsights', 'Microsoft.Batch', 'Microsoft.Compute', 'Microsoft.Network')
    if ($variables.durable_backend -ceq 'durableTaskScheduler') { $providers += 'Microsoft.DurableTask' }
    foreach ($provider in $providers) {
        $registration = Invoke-AzJson @('provider', 'show', '--namespace', $provider, '--subscription', $variables.subscription_id)
        if ($registration.registrationState -ne 'Registered') { throw "$provider is not registered. Register it in the target subscription before deployment." }
    }
    # Pin the environment to its original state backend and subscription. Never silently switch state.
    $identity = @{ subscription_id = $variables.subscription_id; backend_rg = $backend.resource_group_name; backend_storage = $backend.storage_account_name; backend_container = $backend.container_name; state_key = "$Environment.tfstate"; batch_mode = $variables.batch_mode; resource_group_name = $variables.resource_group_name; name_suffix = $variables.name_suffix }
    foreach ($key in $durableSettings.Keys) { $identity[$key] = $durableSettings[$key] }
    $identityPath = Join-Path $work 'environment.json'
    if (Test-Path -LiteralPath $identityPath) {
        $previousIdentity = ConvertTo-DeploymentHashtable (Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json)
        Assert-DeploymentEnvironmentIdentity -Previous $previousIdentity -Current $identity
    }
    # A different local Environment must not silently reconfigure the same Function App.
    foreach ($knownEnvironment in @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot '.deploy') -Directory)) {
        $knownIdentityPath = Join-Path $knownEnvironment.FullName 'environment.json'
        if ($knownIdentityPath -eq $identityPath -or -not (Test-Path -LiteralPath $knownIdentityPath -PathType Leaf)) { continue }
        $other = ConvertTo-DeploymentHashtable (Get-Content -LiteralPath $knownIdentityPath -Raw | ConvertFrom-Json)
        if ($other.subscription_id -eq $identity.subscription_id -and $other.resource_group_name -eq $identity.resource_group_name -and $other.name_suffix -eq $identity.name_suffix) {
            Assert-DeploymentEnvironmentIdentity -Previous $other -Current $identity -DurableOnly
        }
    }
    Write-DeploymentJson $identityPath $identity

    Write-Host 'Building and testing Function App...'
    $null = Invoke-Tool $dotnet @('restore', 'src/Poc.Functions/Poc.Functions.csproj') -LogPath (Join-Path $run 'restore-function.log')
    $null = Invoke-Tool $dotnet @('restore', 'DurableBatchPoc.slnx') -LogPath (Join-Path $run 'restore-solution.log')
    $null = Invoke-Tool $dotnet @('build', 'DurableBatchPoc.slnx', '--no-restore') -LogPath (Join-Path $run 'build.log')
    $null = Invoke-Tool $dotnet @('test', '--project', 'tests/Poc.Tests/Poc.Tests.csproj', '--no-restore') -LogPath (Join-Path $run 'test.log')
    $publish = Join-Path $run 'publish'
    $null = Invoke-Tool $dotnet @('publish', 'src/Poc.Functions/Poc.Functions.csproj', '-c', 'Release', '--no-restore', '-o', $publish) -LogPath (Join-Path $run 'publish.log')
    $assemblyDll = Join-Path $publish 'Poc.Functions.dll'
    $assemblySha = (Get-FileHash -LiteralPath $assemblyDll -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $publish 'assembly-sha256.txt'), $assemblySha, [Text.UTF8Encoding]::new($false))
    $publishFiles = @(Get-ChildItem -LiteralPath $publish -File -Recurse | Where-Object { $_.Name -notin @('package-sha256.txt','assembly-sha256.txt','package-manifest.txt') } | Sort-Object FullName)
    $manifestLines = @($publishFiles | ForEach-Object {
        $rel = $_.FullName.Substring($publish.Length).TrimStart('\','/').Replace('\','/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $rel + ':' + $hash
    })
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $packageContentSha = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($manifestLines -join [char]10)))).Replace('-','').ToLowerInvariant()
    } finally { $sha.Dispose() }
    [IO.File]::WriteAllText((Join-Path $publish 'package-sha256.txt'), $packageContentSha, [Text.UTF8Encoding]::new($false))
    $zip = Join-Path $run 'functions.zip'
    New-FunctionZip $publish $zip
    $packageSha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($backend.ContainsKey('Create') -and $backend.Create) {
        $bootstrapWork = Join-Path $work 'bootstrap'
        New-Item -ItemType Directory -Force -Path $bootstrapWork | Out-Null
        $bootstrap = @{ subscription_id = $variables.subscription_id; resource_group_name = $backend.resource_group_name; storage_account_name = $backend.storage_account_name; container_name = $backend.container_name }
        foreach ($key in @('location', 'deployer_principal_id')) { if (-not $backend.ContainsKey($key) -or -not $backend[$key]) { throw "Backend.$key is required when Create=true." }; $bootstrap[$key] = $backend[$key] }
        Write-DeploymentJson (Join-Path $bootstrapWork 'bootstrap.tfvars.json') $bootstrap
        $env:TF_DATA_DIR = Join-Path $bootstrapWork 'data'
        $null = Invoke-Tool $terraform @('-chdir=infra/bootstrap', 'init', '-input=false', "-backend-config=path=$(Join-Path $bootstrapWork 'terraform.tfstate')") -LogPath (Join-Path $run 'bootstrap-init.log')
        $bootstrapPlan = Join-Path $run 'bootstrap.tfplan'
        $null = Invoke-Tool $terraform @('-chdir=infra/bootstrap', 'plan', '-input=false', "-var-file=$(Join-Path $bootstrapWork 'bootstrap.tfvars.json')", "-out=$bootstrapPlan") -LogPath (Join-Path $run 'bootstrap-plan.log')
        $bootstrapJson = Invoke-Tool $terraform @('-chdir=infra/bootstrap', 'show', '-json', $bootstrapPlan) | ConvertFrom-Json
        Assert-DeploymentPlan -Plan $bootstrapJson -BatchMode existing
        if ($PlanOnly) {
            # Successful existence queries distinguish absence from RBAC/network failures.
            # Do not catch errors here: an inaccessible backend is not a missing backend.
            $groupExists = Invoke-AzJson @('group', 'exists', '--name', $backend.resource_group_name)
            $backendExists = $false
            if ($groupExists -eq $true) {
                $accounts = @(Invoke-AzJson @('storage', 'account', 'list', '--resource-group', $backend.resource_group_name))
                if (@($accounts | Where-Object { $_.name -eq $backend.storage_account_name }).Count -gt 0) {
                    $container = Invoke-AzJson @('storage', 'container', 'exists', '--account-name', $backend.storage_account_name,
                        '--name', $backend.container_name, '--auth-mode', 'login')
                    $backendExists = $container.exists -eq $true
                }
            }
            if (-not $backendExists) {
                Write-Host "Bootstrap plan saved to $bootstrapPlan. Backend does not exist; main plan requires bootstrap apply. No apply or code deployment performed."
                return
            }
            Write-Host 'Backend exists; continuing to the main infrastructure plan without applying bootstrap.'
        }
        else {
            Write-Host 'Creating or updating the dedicated Terraform state backend...'
            $null = Invoke-Tool $terraform @('-chdir=infra/bootstrap', 'apply', '-input=false', $bootstrapPlan) -LogPath (Join-Path $run 'bootstrap-apply.log')
        }
    }
    $backendConfig = @{ subscription_id = $variables.subscription_id; resource_group_name = $backend.resource_group_name; storage_account_name = $backend.storage_account_name; container_name = $backend.container_name; key = "$Environment.tfstate"; use_azuread_auth = $true }
    $backendFile = Join-Path $run 'backend.hcl'
    $backendLines = foreach ($key in $backendConfig.Keys) {
        $value = $backendConfig[$key]
        if ($value -is [bool]) { "$key = $($value.ToString().ToLowerInvariant())" }
        else { "$key = $($value | ConvertTo-Json -Compress)" }
    }
    [IO.File]::WriteAllLines($backendFile, [string[]]$backendLines, [Text.UTF8Encoding]::new($false))
    $variablesPath = Join-Path $run 'deployment.tfvars.json'
    Write-DeploymentJson $variablesPath $variables
    $env:TF_DATA_DIR = Join-Path $work 'terraform-data'
    # Init can initially fail while the new backend role assignment propagates.
    $deadline = (Get-Date).AddSeconds($timeout)
    while ($true) {
        try { $null = Invoke-Tool $terraform @('-chdir=infra', 'init', '-input=false', "-backend-config=$backendFile") -LogPath (Join-Path $run 'init.log'); break }
        catch {
            $newBackend = -not $PlanOnly -and $backend.ContainsKey('Create') -and $backend.Create
            $permissionPropagation = $_.Exception.Message -match '(?i)403|AuthorizationPermissionMismatch|AuthorizationFailure|Forbidden|not authorized|permission'
            if (-not $newBackend -or -not $permissionPropagation -or (Get-Date) -ge $deadline) { throw }
            Start-Sleep -Seconds 10
        }
    }
    $null = Invoke-Tool $terraform @('-chdir=infra', 'validate', '-no-color') -LogPath (Join-Path $run 'validate.log')
    $plan = Join-Path $run 'deployment.tfplan'
    $null = Invoke-Tool $terraform @('-chdir=infra', 'plan', '-input=false', "-var-file=$variablesPath", "-out=$plan") -LogPath (Join-Path $run 'plan.log')
    $planJson = Invoke-Tool $terraform @('-chdir=infra', 'show', '-json', $plan) | ConvertFrom-Json
    Assert-DeploymentPlan -Plan $planJson -BatchMode $variables.batch_mode
    if ($PlanOnly) { Write-Host "Plan saved to $plan. No apply or code deployment performed."; return }
    Write-Host 'Applying infrastructure...'
    $null = Invoke-Tool $terraform @('-chdir=infra', 'apply', '-input=false', $plan) -LogPath (Join-Path $run 'apply.log')
    $outputs = ConvertTo-DeploymentHashtable (Invoke-Tool $terraform @('-chdir=infra', 'output', '-json', 'deployment') | ConvertFrom-Json)
    $null = Invoke-DeploymentValidation -Outputs $outputs -Phase Pool -TimeoutSeconds $timeout

    Write-Host 'Deploying the private Function package from the validation VM...'
    $null = Invoke-PrivateVmOperation -Outputs $outputs -Operation Deploy -PackagePath $zip -PackageSha256 $packageSha -AssemblySha256 $assemblySha -PackageContentSha256 $packageContentSha -EvidenceDirectory (Join-Path $run 'private-vm-deploy') -TimeoutSeconds $timeout
    Write-Host "Restarting Function App to ensure newly published assembly is loaded..."
    $null = Invoke-AzJson @('functionapp', 'restart', '--name', $outputs.function_app_name, '--resource-group', $outputs.resource_group_name)
    Start-Sleep -Seconds 10
    Write-Host 'Verifying Function registration and connectivity after restart from the validation VM...'
    $postRestartResult = Invoke-PrivateVmOperation -Outputs $outputs -Operation FunctionValidation -PackageSha256 $packageSha -AssemblySha256 $assemblySha -PackageContentSha256 $packageContentSha -EvidenceDirectory (Join-Path $run 'private-vm-validation') -TimeoutSeconds $timeout -Smoke:$Smoke
    $validation = $postRestartResult.validation
    $summary = @{ environment = $Environment; completed_utc = [DateTime]::UtcNow.ToString('o'); package_sha256 = $packageSha; assembly_sha256 = $assemblySha; package_content_sha256 = $packageContentSha; outputs = $outputs; validation = $validation; smoke_requested = [bool]$Smoke }
    Write-DeploymentJson (Join-Path $run 'deployment-result.json') $summary
    Write-Host "Deployment completed: $($outputs.function_app_name). Logs: $run"
}
finally {
    $env:TF_DATA_DIR = $previousData
    $env:ARM_SUBSCRIPTION_ID = $previousSubscription
    $env:ARM_USE_AZUREAD = $previousAad
    $env:Path = $previousPath
    Set-Location $previousRoot
}
