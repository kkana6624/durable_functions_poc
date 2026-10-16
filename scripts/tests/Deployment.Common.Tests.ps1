$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../Deployment.Common.ps1')
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw "ASSERT: $Message" } }
function Assert-Fails([scriptblock]$Action,[string]$Expected) {
    try { & $Action | Out-Null } catch {
        Assert-True ($_.Exception.Message -match $Expected) "Expected $Expected, received $($_.Exception.Message)"
        return
    }
    throw "ASSERT: expected $Expected"
}
function Change([string]$Type,[string[]]$Actions) {
    return [pscustomobject]@{ mode='managed'; type=$Type; address="$Type.example"; change=[pscustomobject]@{ actions=$Actions } }
}
Assert-DeploymentPlan -Plan ([pscustomobject]@{ resource_changes=@((Change 'azurerm_batch_pool' @('no-op'))) }) -BatchMode existing
Assert-DeploymentPlan -Plan ([pscustomobject]@{}) -BatchMode existing
Assert-DeploymentPlan -Plan ([pscustomobject]@{ resource_changes=@((Change 'azurerm_batch_pool' @('create'))) }) -BatchMode new
Assert-Fails { Assert-DeploymentPlan -Plan ([pscustomobject]@{ resource_changes=@((Change 'azurerm_batch_pool' @('update'))) }) -BatchMode existing } 'must not change'
Assert-Fails { Assert-DeploymentPlan -Plan ([pscustomobject]@{ resource_changes=@((Change 'azurerm_linux_function_app' @('delete','create'))) }) -BatchMode new } 'delete or replace'
Assert-Fails { Assert-DeploymentPlan -Plan ([pscustomobject]@{ resource_changes=@((Change 'azurerm_shared_image' @('create'))) }) -BatchMode new } 'reference-only'
$converted=ConvertTo-DeploymentHashtable ([pscustomobject]@{ outer=[pscustomobject]@{ values=@('a','b') } })
Assert-True ($converted -is [hashtable] -and $converted.outer -is [hashtable] -and $converted.outer.values.Count -eq 2) 'JSON conversion should preserve nested arrays'

$temporary=Join-Path ([IO.Path]::GetTempPath()) ('poc-common-tests-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $temporary
try {
    $publish=Join-Path $temporary 'publish'
    $null=New-Item -ItemType Directory -Path (Join-Path $publish '.azurefunctions') -Force
    foreach ($file in @('host.json','worker.config.json','Poc.Functions.dll','.azurefunctions/worker.dll')) {
        [IO.File]::WriteAllText((Join-Path $publish $file),'fixture')
    }
    $zip=Join-Path $temporary 'functions.zip'
    New-FunctionZip -PublishDirectory $publish -Destination $zip
    $archive=[IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entries=@($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
        Assert-True ('host.json' -in $entries -and '.azurefunctions/worker.dll' -in $entries) 'ZIP must preserve root host.json and hidden extension directory'
    } finally { $archive.Dispose() }
    $native=Join-Path $temporary 'failure.cmd'
    [IO.File]::WriteAllText($native,"@echo off`r`necho PRIVATE-KEY-MARKER`r`nexit /b 7`r`n")
    Assert-Fails { Invoke-Tool -FilePath $native } 'exit 7'
    $log=Join-Path $temporary 'sensitive.log'
    try { Invoke-Tool -FilePath $native -Sensitive -LogPath $log | Out-Null; throw 'Expected command failure' }
    catch { Assert-True ($_.Exception.Message -match 'suppressed' -and $_.Exception.Message -notmatch 'PRIVATE-KEY-MARKER') 'Sensitive failures must redact output' }
    Assert-True (-not (Test-Path -LiteralPath $log)) 'Sensitive command output must not be written to log'
    [IO.File]::WriteAllText($native,"@echo off`r`necho PRIVATE-KEY-MARKER-invalid-json`r`nexit /b 0`r`n")
    $script:AzPath=$native
    try { Invoke-AzJson -Arguments @('fake') -Sensitive | Out-Null; throw 'Expected JSON failure' }
    catch { Assert-True ($_.Exception.Message -match 'suppressed' -and $_.Exception.Message -notmatch 'PRIVATE-KEY-MARKER') 'Sensitive JSON parse failures must redact output' }
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
Write-Host 'Deployment Common offline tests: PASS (plan guards, no-op, ZIP, native exit, secret redaction, JSON).'
