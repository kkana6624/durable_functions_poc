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
    $null=New-Item -ItemType Directory -Path (Join-Path $publish '.azurefunctions/nested') -Force
    $null=New-Item -ItemType Directory -Path (Join-Path $publish 'runtimes/linux-x64/native') -Force
    foreach ($file in @('host.json','worker.config.json','Poc.Functions.dll','.azurefunctions/worker.dll','.azurefunctions/nested/extension.dll','runtimes/linux-x64/native/runtime.so')) {
        [IO.File]::WriteAllText((Join-Path $publish $file),'fixture')
    }
    $zip=Join-Path $temporary 'functions.zip'
    New-FunctionZip -PublishDirectory $publish -Destination $zip
    $archive=[IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entries=@($archive.Entries | ForEach-Object { $_.FullName })
        Assert-True ('host.json' -in $entries -and '.azurefunctions/worker.dll' -in $entries) 'ZIP must preserve root host.json and hidden extension directory'
        Assert-True ('.azurefunctions/nested/extension.dll' -in $entries -and 'runtimes/linux-x64/native/runtime.so' -in $entries) 'Nested extension and runtime entries must use portable forward slashes'
        Assert-True (@($entries | Where-Object { $_.Contains('\') }).Count -eq 0) 'Raw ZIP entry names must never contain backslashes'
        foreach ($entry in $archive.Entries) {
            $reader=[IO.StreamReader]::new($entry.Open())
            try { Assert-True ($reader.ReadToEnd() -eq 'fixture') 'Portable ZIP entries must preserve file contents' }
            finally { $reader.Dispose() }
        }
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
    $script:DeploymentSubscriptionId='target-subscription'
    $script:capturedArguments=@()
    function Invoke-Tool { param([string]$FilePath,[string[]]$Arguments,[switch]$Sensitive); $script:capturedArguments=$Arguments; return '{}' }
    $null=Invoke-AzJson -Arguments @('cloud','show')
    Assert-True ('--subscription' -notin $script:capturedArguments) 'Local cloud metadata command must not receive an unsupported subscription argument'
    $null=Invoke-AzJson -Arguments @('account','get-access-token','--resource','https://appservice.azure.com') -Sensitive
    Assert-True ('--subscription' -in $script:capturedArguments -and 'target-subscription' -in $script:capturedArguments) 'Token acquisition must stay pinned to the target subscription'
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
Write-Host 'Deployment Common offline tests: PASS (plan guards, no-op, ZIP, native exit, secret redaction, JSON).'
