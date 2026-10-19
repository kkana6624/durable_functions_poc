[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DeploymentResultPath,
    [Parameter(Mandatory)][string]$Resource,
    [Parameter(Mandatory)][string]$OwnerId,
    [Parameter(Mandatory)][string]$OrgId,
    [string]$ManagedIdentityClientId,
    [string]$UnauthorizedManagedIdentityClientId,
    [ValidateRange(60,3600)][int]$TimeoutSeconds=900,
    [string]$EvidenceDirectory
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
. (Join-Path $PSScriptRoot 'Invoke-PrivateVmOperation.ps1')
try {
    $deployment=Get-Content -LiteralPath $DeploymentResultPath -Raw | ConvertFrom-Json
    $outputs=ConvertTo-DeploymentHashtable $deployment.outputs
    $script:AzPath=Resolve-DeploymentTool -Name az -RepositoryRoot (Split-Path $PSScriptRoot -Parent)
    $env:Path = (Split-Path $script:AzPath -Parent) + [IO.Path]::PathSeparator + $env:Path
    if (-not $EvidenceDirectory) { $EvidenceDirectory=Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/private-auth-validation/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-ffff')) }
    $packageHash=if ($deployment.PSObject.Properties['package_sha256']) { [string]$deployment.package_sha256 } else { '' }
    $assemblyHash=if ($deployment.PSObject.Properties['assembly_sha256']) { [string]$deployment.assembly_sha256 } else { '' }
    $opParams = @{
        Outputs = $outputs
        Operation = 'WorkflowAuth'
        EvidenceDirectory = $EvidenceDirectory
        TimeoutSeconds = $TimeoutSeconds
        PackageSha256 = $packageHash
        AssemblySha256 = $assemblyHash
        AuthResource = $Resource
        AuthOwnerId = $OwnerId
        AuthOrgId = $OrgId
    }
    if ($UnauthorizedManagedIdentityClientId) { $opParams['AuthUnauthorizedClientId'] = $UnauthorizedManagedIdentityClientId }
    if ($ManagedIdentityClientId) { $opParams['AuthManagedIdentityClientId'] = $ManagedIdentityClientId }
    $result=Invoke-PrivateVmOperation @opParams
    Write-Host "Private VM workflow auth validation completed. Sanitized evidence: $EvidenceDirectory"
    exit ([int]$result.exit_code)
} catch {
    Write-Error 'Private VM workflow auth validation failed. Check the sanitized operation result, VM provisioning and identity permissions. Raw command output suppressed.'
    exit 1
}
