Set-StrictMode -Version Latest

function ConvertTo-DeploymentHashtable {
    param([AllowNull()] $Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Collections.IDictionary]) {
        $result = @{}
        foreach ($key in $Value.Keys) { $result[$key] = ConvertTo-DeploymentHashtable $Value[$key] }
        return $result
    }
    if ($Value -is [string] -or $Value -is [System.ValueType]) { return $Value }
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
    if ($Name -eq 'az') {
        foreach ($candidate in @('C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd', 'C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\wbin\az.cmd')) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
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
    if ($subscription -and $subscription.Value -and $Arguments[0] -notin @('version','cloud') -and '--subscription' -notin $Arguments) {
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

function Assert-DeploymentSecretReferences {
    param([Parameter(Mandatory)][hashtable]$Variables)
    # Reject before creating .deploy files or invoking tools; sensitive=true only masks display.
    if ($Variables.ContainsKey('app_service_workflow_function_key') -and $null -ne $Variables.app_service_workflow_function_key) {
        throw 'Raw Function keys are not supported. Remove app_service_workflow_function_key and use app_service_workflow_function_key_secret_uri.'
    }
    if ($Variables.ContainsKey('app_service_workflow_function_key_secret_uri') -and $null -ne $Variables.app_service_workflow_function_key_secret_uri) {
        $secretUri = $Variables.app_service_workflow_function_key_secret_uri
        if ($secretUri -isnot [string] -or $secretUri -cnotmatch '^https://[A-Za-z0-9-]+\.vault\.azure\.net/secrets/[A-Za-z0-9-]+(/[A-Fa-f0-9]{32})?/?$') {
            throw 'app_service_workflow_function_key_secret_uri must be a Key Vault secret URI without credentials, query or fragment.'
        }
    } elseif ($Variables.ContainsKey('enable_app_service') -and $Variables.enable_app_service) {
        throw 'An enabled App Service requires app_service_workflow_function_key_secret_uri.'
    }
}

function Get-DeploymentWorkflowAuthSettings {
    param([Parameter(Mandatory)][hashtable]$Variables, [switch]$Smoke)
    $mode = if ($Variables.ContainsKey('workflow_auth_mode')) { [string]$Variables.workflow_auth_mode } else { 'PocFunctionKey' }
    if ($mode -ceq 'PocFunctionKey') { return @{ workflow_auth_mode=$mode } }
    if ($mode -cne 'EntraEasyAuth') { throw 'Invalid workflow_auth_mode.' }
    $result = @{ workflow_auth_mode=$mode }
    foreach ($key in @('workflow_auth_tenant_id','workflow_auth_api_client_id','workflow_auth_audience','workflow_auth_required_app_role')) {
        if (-not $Variables.ContainsKey($key) -or $Variables[$key] -isnot [string] -or [string]::IsNullOrWhiteSpace($Variables[$key])) { throw "EntraEasyAuth requires $key." }
        $result[$key] = $Variables[$key].Trim()
    }
    foreach ($key in @('workflow_auth_tenant_id','workflow_auth_api_client_id')) {
        $id = [Guid]::Empty
        if ($result[$key] -notmatch '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$' -or -not [Guid]::TryParse($result[$key], [ref]$id) -or $id -eq [Guid]::Empty) { throw "EntraEasyAuth requires a nonempty GUID for $key." }
        $result[$key] = $id.ToString()
    }
    $key = 'workflow_auth_allowed_backend_app_ids'
    if (-not $Variables.ContainsKey($key) -or $Variables[$key] -isnot [array] -or $Variables[$key].Count -eq 0) { throw "EntraEasyAuth requires a nonempty GUID array for $key." }
    $ids = @()
    foreach ($value in $Variables[$key]) {
        $id = [Guid]::Empty
        if ($value -isnot [string] -or $value -notmatch '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$' -or -not [Guid]::TryParse($value, [ref]$id) -or $id -eq [Guid]::Empty) { throw "EntraEasyAuth requires nonempty backend GUID identifiers." }
        if ($ids -contains $id.ToString()) { throw 'EntraEasyAuth backend GUID identifiers must be unique.' }
        $ids += $id.ToString()
    }
    $result[$key] = $ids
    if ($Smoke) { throw 'EntraEasyAuth does not support deploy -Smoke. Use Invoke-WorkflowAuthValidation.ps1 with the separately authorized validation VM Managed Identity.' }
    return $result
}

function Get-DeploymentDurableSettings {
    param([Parameter(Mandatory)][hashtable]$Variables, [switch]$FromOutputs)
    $backend = if ($Variables.ContainsKey('durable_backend')) { [string]$Variables.durable_backend } else { 'azureStorage' }
    $hubKey = if ($FromOutputs) { 'task_hub_name' } else { 'durable_task_hub_name' }
    $hub = if ($Variables.ContainsKey($hubKey)) { [string]$Variables[$hubKey] } else { 'PocBatchHub' }
    if ($backend -cnotin @('azureStorage','durableTaskScheduler')) { throw 'Invalid durable backend.' }
    if ($hub -cnotmatch '^[A-Za-z][A-Za-z0-9]{2,44}$') { throw 'Invalid durable task hub name.' }
    return @{ durable_backend=$backend; durable_task_hub_name=$hub }
}

function Assert-DeploymentEnvironmentIdentity {
    param([Parameter(Mandatory)][hashtable]$Previous, [Parameter(Mandatory)][hashtable]$Current, [switch]$DurableOnly)
    # Legacy identity files describe the original Azure Storage / PocBatchHub deployment.
    $legacy = @{ durable_backend='azureStorage'; durable_task_hub_name='PocBatchHub' }
    $keys = if ($DurableOnly) { @('durable_backend','durable_task_hub_name') } else { @($Current.Keys) }
    foreach ($key in $keys) {
        $old = if ($Previous.ContainsKey($key)) { $Previous[$key] } elseif ($legacy.ContainsKey($key)) { $legacy[$key] } else { $null }
        if ($old -cne $Current[$key]) { throw "Environment identity changed ($key). Use a separate Function App and state for a new durable backend or task hub; history and Entity state are not migrated." }
    }
}

function Assert-DeploymentPlan {
    param([Parameter(Mandatory)] $Plan, [Parameter(Mandatory)][string] $BatchMode, [switch]$AllowBatchPoolRecreation)
    # Existing resources are reference-only in both modes; replacement/deletion needs a separate migration.
    if (-not $Plan.PSObject.Properties['resource_changes']) { return }
    foreach ($change in @($Plan.resource_changes)) {
        if ($null -eq $change) { continue }
        if ($change.mode -eq 'data') { continue }
        $actions = @($change.change.actions)
        if ($BatchMode -eq 'existing' -and $change.type -in @('azurerm_batch_account', 'azurerm_batch_pool')) {
            if (@($actions | Where-Object { $_ -ne 'no-op' -and $_ -ne 'read' }).Count -gt 0) {
                throw "Existing Batch resources must not change: $($change.address)"
            }
        }
        if ('delete' -in $actions) {
            if ($AllowBatchPoolRecreation -and $change.type -eq 'azurerm_batch_pool' -and $BatchMode -eq 'new') {
                Write-Warning "Allowing replacement of Batch pool $($change.address) under -AllowBatchPoolRecreation."
            } else {
                throw "Deployment would delete or replace $($change.address). Use a separate reviewed migration."
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
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # .NET Framework's CreateFromDirectory writes Windows separators into ZIP
    # entries. Linux treats those as literal filename characters, hiding the
    # .azurefunctions payload from the host. Build portable entry names explicitly.
    $root=[IO.Path]::GetFullPath($PublishDirectory).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    $stream=[IO.File]::Open([IO.Path]::GetFullPath($Destination),[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        $archive=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
        try {
            foreach ($file in [IO.Directory]::EnumerateFiles($root,'*',[IO.SearchOption]::AllDirectories)) {
                $name=$file.Substring($root.Length).Replace('\','/')
                $entry=$archive.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal)
                $source=[IO.File]::OpenRead($file)
                try {
                    $target=$entry.Open()
                    try { $source.CopyTo($target) } finally { $target.Dispose() }
                } finally { $source.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
}
