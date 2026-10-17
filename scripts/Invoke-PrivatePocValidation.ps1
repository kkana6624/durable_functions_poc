[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DeploymentResultPath,
    [ValidateSet('single','three','classification','failure','admission20','conflict','timeout')][string[]]$Scenarios=@('single','three','classification','failure','admission20','conflict'),
    [ValidateRange(60,3600)][int]$TimeoutSeconds=900,
    [string]$EvidenceDirectory,
    [switch]$DedicatedEnvironment
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
. (Join-Path $PSScriptRoot 'Invoke-PrivateVmOperation.ps1')
try {
    $deployment=Get-Content -LiteralPath $DeploymentResultPath -Raw | ConvertFrom-Json
    $outputs=ConvertTo-DeploymentHashtable $deployment.outputs
    $script:AzPath=Resolve-DeploymentTool -Name az -RepositoryRoot (Split-Path $PSScriptRoot -Parent)
    if (-not $EvidenceDirectory) { $EvidenceDirectory=Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/private-validation/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-ffff')) }
    $packageHash=if ($deployment.PSObject.Properties['package_sha256']) { [string]$deployment.package_sha256 } else { '' }
    $result=Invoke-PrivateVmOperation -Outputs $outputs -Operation Validate -EvidenceDirectory $EvidenceDirectory -Scenarios $Scenarios -TimeoutSeconds $TimeoutSeconds -PackageSha256 $packageHash -DedicatedEnvironment:$DedicatedEnvironment
    Write-Host "Private VM validation completed. Sanitized evidence: $EvidenceDirectory"
    exit ([int]$result.exit_code)
} catch {
    Write-Error 'Private VM validation failed. Check the sanitized operation result, VM provisioning and identity permissions. Raw command output suppressed.'
    exit 1
}
