Set-StrictMode -Version Latest

function ConvertTo-DeploymentHashtable {
    param([AllowNull()] $Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Collections.IDictionary]) {
        $result = @{}
        foreach ($key in $Value.Keys) { $result[$key] = ConvertTo-DeploymentHashtable $Value[$key] }
        return $result
    }
    if ($Value -is [pscustomobject]) {
        $result = @{}
        foreach ($property in $Value.PSObject.Properties) { $result[$property.Name] = ConvertTo-DeploymentHashtable $property.Value }
        return $result
    }
    if ($Value -is [array]) { return ,@($Value | ForEach-Object { ConvertTo-DeploymentHashtable $_ }) }
    return $Value
}

function Resolve-DeploymentTool {
    param([string] $Name, [string] $ConfiguredPath, [string] $RepositoryRoot)
    if ($ConfiguredPath) {
        $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ConfiguredPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Tool not found: $resolved" }
        return $resolved
    }
    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    $bundled = Join-Path $RepositoryRoot ".tools/$Name/$Name.exe"
    if (Test-Path -LiteralPath $bundled -PathType Leaf) { return $bundled }
    throw "$Name is required. Install it or provide its executable path in Config.Tools."
}

function Invoke-Tool {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $FilePath, [string[]] $Arguments = @(), [string] $LogPath, [switch] $Sensitive)
    $previousPreference = $ErrorActionPreference
    try {
        # Windows PowerShell represents native stderr as ErrorRecords even for successful tools.
        $ErrorActionPreference = 'Continue'
        $lines = @(& $FilePath @Arguments 2>&1 | ForEach-Object { $_.ToString() })
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    $output = $lines -join [Environment]::NewLine
    if ($LogPath -and -not $Sensitive) { [IO.File]::WriteAllText($LogPath, $output, [Text.UTF8Encoding]::new($false)) }
    if ($code -ne 0) {
        if ($Sensitive) { throw "Command failed (exit $code); output suppressed because it may contain credentials or management URLs." }
        throw "Command failed (exit $code): $FilePath`n$output"
    }
    return $output
}

function Invoke-AzJson {
    param([Parameter(Mandatory)][string[]] $Arguments, [switch] $Sensitive)
    $subscription = Get-Variable -Name DeploymentSubscriptionId -Scope Script -ErrorAction SilentlyContinue
    if ($subscription -and $subscription.Value -and $Arguments[0] -ne 'version' -and '--subscription' -notin $Arguments) {
        $Arguments += @('--subscription', [string]$subscription.Value)
    }
    $output = Invoke-Tool -FilePath $script:AzPath -Arguments ($Arguments + @('--only-show-errors', '--output', 'json')) -Sensitive:$Sensitive
    if ([string]::IsNullOrWhiteSpace($output)) { return $null }
    try { return ($output | ConvertFrom-Json) }
    catch { if ($Sensitive) { throw 'Azure CLI returned invalid JSON; credential-bearing output suppressed.' }; throw }
}

function Write-DeploymentJson {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)] $Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
}

function Assert-DeploymentPlan {
    param([Parameter(Mandatory)] $Plan, [Parameter(Mandatory)][string] $BatchMode)
    # Existing resources are reference-only in both modes; replacement/deletion needs a separate migration.
    if (-not $Plan.PSObject.Properties['resource_changes']) { return }
    foreach ($change in @($Plan.resource_changes)) {
        if ($null -eq $change) { continue }
        if ($change.mode -eq 'data') { continue }
        $actions = @($change.change.actions)
        if ('delete' -in $actions) { throw "Deployment would delete or replace $($change.address). Use a separate reviewed migration."
        }
        if ($BatchMode -eq 'existing' -and $change.type -in @('azurerm_batch_account', 'azurerm_batch_pool')) {
            if (@($actions | Where-Object { $_ -ne 'no-op' -and $_ -ne 'read' }).Count -gt 0) {
                throw "Existing Batch resources must not change: $($change.address)"
            }
        }
        if ($change.type -like 'azurerm_shared_image*') { throw "Gallery resources must remain reference-only in this deployment." }
    }
}

function New-FunctionZip {
    param([Parameter(Mandatory)][string] $PublishDirectory, [Parameter(Mandatory)][string] $Destination)
    foreach ($required in @('host.json', 'worker.config.json', 'Poc.Functions.dll', '.azurefunctions')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $required))) { throw "Publish output is incomplete: $required" }
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # ZipFile includes dot directories on all supported hosts; host.json stays at the archive root.
    [IO.Compression.ZipFile]::CreateFromDirectory($PublishDirectory, $Destination, [IO.Compression.CompressionLevel]::Optimal, $false)
}
