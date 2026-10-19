#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9-]{0,30}$')][string] $Environment,
    [Parameter(Mandatory)][string] $ConfigFile,
    [switch] $DestroyBackend,
    [switch] $Force
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/Deployment.Common.ps1"

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$config = Import-PowerShellDataFile -LiteralPath (Resolve-Path -LiteralPath $ConfigFile).Path
if (-not $config.ContainsKey('Terraform') -or -not $config.ContainsKey('Backend')) { throw 'Config must contain Terraform and Backend tables.' }
$variables = $config.Terraform
$backend = $config.Backend

$durableSettings = Get-DeploymentDurableSettings -Variables $variables
foreach ($key in $durableSettings.Keys) { $variables[$key] = $durableSettings[$key] }
$script:DeploymentSubscriptionId = $variables.subscription_id
$tools = if ($config.ContainsKey('Tools')) { $config.Tools } else { @{} }
$terraform = Resolve-DeploymentTool -Name terraform -ConfiguredPath $tools['Terraform'] -RepositoryRoot $repositoryRoot
$script:AzPath = Resolve-DeploymentTool -Name az -ConfiguredPath $tools['AzureCli'] -RepositoryRoot $repositoryRoot

$work = Join-Path $repositoryRoot ".deploy/$Environment"
if (-not (Test-Path -LiteralPath $work)) {
    throw "Deployment directory $work does not exist. Cannot locate terraform state."
}

$previousData = $env:TF_DATA_DIR
$previousSubscription = $env:ARM_SUBSCRIPTION_ID
$previousAad = $env:ARM_USE_AZUREAD
$previousPath = $env:Path
$previousRoot = Get-Location

try {
    Set-Location $repositoryRoot
    $env:Path = (Split-Path $script:AzPath -Parent) + [IO.Path]::PathSeparator + $env:Path
    $env:ARM_SUBSCRIPTION_ID = $variables.subscription_id
    $env:ARM_USE_AZUREAD = 'true'

    Write-Host "Preparing to destroy environment: $Environment (Resource Group: $($variables.resource_group_name))..."

    # Find the latest deployment run directory to obtain backend.hcl and tfvars
    $latestRun = Get-ChildItem -LiteralPath $work -Directory | Where-Object { $_.Name -match '^\d{8}-\d{6}-\d{4}$' } | Sort-Object Name -Descending | Select-Object -First 1
    if ($null -eq $latestRun) {
        throw "Could not find any deployment runs in $work."
    }

    $backendFile = Join-Path $latestRun.FullName 'backend.hcl'
    $variablesPath = Join-Path $latestRun.FullName 'deployment.tfvars.json'

    if (-not (Test-Path -LiteralPath $backendFile) -or -not (Test-Path -LiteralPath $variablesPath)) {
        throw "backend.hcl or deployment.tfvars.json not found in $($latestRun.FullName)."
    }

    $env:TF_DATA_DIR = Join-Path $work 'terraform-data'

    Write-Host "Initializing Terraform backend from $($backendFile)..."
    $null = Invoke-Tool $terraform @('-chdir=infra', 'init', '-input=false', "-backend-config=$backendFile", '-reconfigure')

    Write-Host "Executing terraform destroy for $Environment..."
    $destroyArgs = @('-chdir=infra', 'destroy', '-input=false', "-var-file=$variablesPath")
    if ($Force) {
        $destroyArgs += '-auto-approve'
    }

    & $terraform $destroyArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Terraform destroy failed with exit code $LASTEXITCODE."
    }

    Write-Host "Terraform destroy completed successfully for $Environment."

    if ($DestroyBackend -and $backend.ContainsKey('Create') -and $backend.Create) {
        $bootstrapWork = Join-Path $work 'bootstrap'
        $bootstrapState = Join-Path $bootstrapWork 'terraform.tfstate'
        $bootstrapVars = Join-Path $bootstrapWork 'bootstrap.tfvars.json'

        if ((Test-Path -LiteralPath $bootstrapState) -and (Test-Path -LiteralPath $bootstrapVars)) {
            Write-Host "Destroying dedicated bootstrap backend state ($($backend.resource_group_name))..."
            $env:TF_DATA_DIR = Join-Path $bootstrapWork 'data'
            $bootstrapArgs = @('-chdir=infra/bootstrap', 'destroy', '-input=false', "-var-file=$bootstrapVars")
            if ($Force) {
                $bootstrapArgs += '-auto-approve'
            }
            & $terraform $bootstrapArgs
            if ($LASTEXITCODE -ne 0) {
                throw "Terraform destroy for bootstrap failed with exit code $LASTEXITCODE."
            }
            Write-Host "Bootstrap backend destroyed successfully."
        }
    }
}
finally {
    $env:TF_DATA_DIR = $previousData
    $env:ARM_SUBSCRIPTION_ID = $previousSubscription
    $env:ARM_USE_AZUREAD = $previousAad
    $env:Path = $previousPath
    Set-Location $previousRoot
}
