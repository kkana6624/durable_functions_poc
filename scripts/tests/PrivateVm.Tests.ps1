$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../Deployment.Common.ps1')
. (Join-Path $PSScriptRoot '../Invoke-PrivateVmOperation.ps1')
. (Join-Path $PSScriptRoot '../PrivateVm.Worker.ps1')
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw "ASSERT: $Message" } }
function Assert-Fails([scriptblock]$Action,[string]$Expected) {
    try { & $Action | Out-Null } catch {
        Assert-True ($_.Exception.Message -match $Expected) "Expected $Expected, received $($_.Exception.Message)"
        return
    }
    throw "ASSERT: expected $Expected"
}
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('poc-transport-tests-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $temporary
$script:transportExit=0
$script:transportGuestFails=$false
$script:transportAggregate=$false
$script:transportStderrMarker=$false
$script:transportInvalidId=$false
$script:transportFailureStage=$null
$script:transportDownloads=@()
$script:transportRequest=$null
$script:transportBundleHash=''
$script:transportPackageHash=''
$script:transportCalls=@()
$script:transportSummaryDownloadFails=$false
$script:transportSummaryArtifacts=$null
$script:transportLastShell=''
function Invoke-AzJson {
    param([string[]]$Arguments,[switch]$Sensitive)
    Assert-True ([bool]$Sensitive) 'All transport Azure calls must suppress raw output'
    $script:transportCalls+=,($Arguments -join ' ')
    if ($Arguments[0] -eq 'storage' -and $Arguments[2] -in @('upload','download')) {
        Assert-True ('--no-progress' -in $Arguments) 'Blob transfer progress must be suppressed to preserve machine-readable JSON'
    }
    Assert-True ($Arguments[0] -ne 'functionapp') 'Hostname discovery must not invoke enriched functionapp show or publishing-profile reads'
    if ($Arguments[0] -eq 'rest') {
        Assert-True ($Arguments[1] -eq '--method' -and $Arguments[2] -eq 'get' -and $Arguments[3] -eq '--url' -and $Arguments[4] -eq 'https://management.azure.com/subscriptions/subscription/resourceGroups/rg/providers/Microsoft.Web/sites/function?api-version=2024-11-01') 'Hostname discovery must issue only the sites/read ARM GET'
        return [pscustomobject]@{ properties=[pscustomobject]@{ defaultHostName='function.azurewebsites.net'; enabledHostNames=@('function.azurewebsites.net','function.scm.azurewebsites.net') } }
    }
    if ($Arguments[0] -eq 'cloud') { return [pscustomobject]@{ endpoints=[pscustomobject]@{appServiceResourceId='https://appservice.azure.com';resourceManager='https://management.azure.com/'} } }
    if ($Arguments[0] -eq 'account') { return [pscustomobject]@{accessToken='PRIVATE-SCM-TOKEN'} }
    function Argument([string]$Name) { return $Arguments[[Array]::IndexOf($Arguments,$Name)+1] }
    if ($Arguments[0] -eq 'storage' -and $Arguments[2] -eq 'upload') {
        Assert-True ((Argument '--auth-mode') -eq 'login') 'Transfer must use Entra identity instead of SAS'
        $file=Argument '--file'
        $script:transportBundleHash=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        $archive=[IO.Compression.ZipFile]::OpenRead($file)
        try {
            $entry=$archive.GetEntry('request.json')
            $reader=[IO.StreamReader]::new($entry.Open())
            try { $json=$reader.ReadToEnd() } finally { $reader.Dispose() }
            Assert-True ($json -notmatch '(?i)PRIVATE-FUNCTION-KEY|[?&](sig|code)=') 'Uploaded request must contain no keys or signed URLs'
            $script:transportRequest=$json | ConvertFrom-Json
            if ($script:transportRequest.operation -eq 'Deploy') {
                $entry=$archive.GetEntry('functions.zip')
                $stream=$entry.Open()
                $sha=[Security.Cryptography.SHA256]::Create()
                try { $script:transportPackageHash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','') }
                finally { $stream.Dispose(); $sha.Dispose() }
                Assert-True ($script:transportPackageHash -eq $script:transportRequest.package_sha256) 'Transferred package must match the recorded SHA-256'
            }
        } finally { $archive.Dispose() }
        return [pscustomobject]@{}
    }
    if ($Arguments[0] -eq 'vm') {
        $shell=Get-Content -LiteralPath ((Argument '--scripts').Substring(1)) -Raw
        Assert-True ($shell -match "export POC_VM_TRANSPORT='([A-Za-z0-9+/=]+)'") 'Run Command must carry serialized transport metadata'
        $transport=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1])) | ConvertFrom-Json
        Assert-True ($transport.sha256 -eq $script:transportBundleHash) 'Guest must receive the uploaded bundle hash'
        Assert-True ($transport.operation_id -eq $script:transportRequest.operation_id) 'Guest operation identity must match bundle'
        Assert-True ($transport.budget_seconds -gt 0) 'Guest transport must include budget_seconds'
        Assert-True ($shell -notmatch 'PRIVATE-FUNCTION-KEY|[?&](sig|code)=') 'Run Command must contain no secrets'
        Assert-True ($shell -match 'az storage blob download [^\r\n]*--no-progress') 'Guest bundle download must suppress progress'
        $message=if ($script:transportGuestFails) { 'guest script failed' } else { 'Private VM operation completed; sanitized results are in the control container.' }
        $script:transportLastShell = $shell
        if ($script:transportAggregate) {
            $stderr=if ($script:transportStderrMarker) { 'Private VM operation completed; sanitized results are in the control container.' } else { '' }
            return [pscustomobject]@{ value=@([pscustomobject]@{ code='ProvisioningState/succeeded';message="Enable succeeded:`n[stdout]`n$message`n[stderr]`n$stderr`n" }) }
        }
        return [pscustomobject]@{ value=@([pscustomobject]@{ code='ComponentStatus/StdOut/succeeded';message=$message }) }
    }
    if ($Arguments[0] -eq 'storage' -and $Arguments[2] -eq 'download') {
        $file=Argument '--file'
        $name=[IO.Path]::GetFileName($file)
        $script:transportDownloads+=$name
        if ($name -eq 'vm-operation.json') {
            $id=if ($script:transportInvalidId) { 'wrong-id' } else { $script:transportRequest.operation_id }
            $res=@{ operation_id=$id;operation=$script:transportRequest.operation;exit_code=$script:transportExit;error_code='FixtureFailure';failure_stage=$script:transportFailureStage;summary_available=($script:transportRequest.operation -in @('Validate','RecoveryStart','RecoveryComplete','WorkflowAuth','AutoscaleValidation'));validation=@{Ready=$true} }
            if ($null -ne $script:transportSummaryArtifacts) {
                $res['summary_artifacts'] = $script:transportSummaryArtifacts
            }
            $res | ConvertTo-Json | Set-Content -LiteralPath $file -Encoding UTF8
        } elseif ($name -eq 'summary.json') {
            if ($script:transportSummaryDownloadFails) { throw 'BlobNotFound' }
            '{"status":"NotRun"}' | Set-Content -LiteralPath $file -Encoding UTF8
        } else { 'NotRun fixture' | Set-Content -LiteralPath $file -Encoding UTF8 }
        return [pscustomobject]@{}
    }
    throw 'Unexpected Azure operation'
}
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # Execute the guest's actual -Command payload against a child script that
    # returns NotRun. Without explicit exit, -Command collapses exit 2 to exit 1.
    $workerSource=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../PrivateVm.Worker.ps1') -Raw
    $runnerMatch=[regex]::Match($workerSource,'(?s)\$runner=@''\r?\n(?<command>.*?)\r?\n''@')
    Assert-True $runnerMatch.Success 'Guest validation command must be available for the exit-code regression'
    Assert-True ($workerSource -notmatch '(?i)\$phase\s*=') 'PrivateVm.Worker.ps1 must not assign $phase in the scope where Test-Deployment.ps1 defines [ValidateSet] $Phase'
    $childDirectory=Join-Path $temporary 'exit-code-child'
    $null=New-Item -ItemType Directory -Path $childDirectory
    [IO.File]::WriteAllText((Join-Path $childDirectory 'request.json'),'{"timeout_seconds":60,"scenarios":["admission20"],"dedicated_environment":false}')
    [IO.File]::WriteAllText((Join-Path $childDirectory 'Invoke-PocValidation.ps1'),'param($DeploymentResultPath,$EvidenceDirectory,$TimeoutSeconds,$Scenarios,[switch]$DedicatedEnvironment); exit 2')
    $childShell=if ($PSVersionTable.PSEdition -eq 'Desktop') { Join-Path $PSHOME 'powershell.exe' } else { Join-Path $PSHOME 'pwsh' }
    Push-Location $childDirectory
    try {
        $null=& $childShell -NoLogo -NoProfile -NonInteractive -Command $runnerMatch.Groups['command'].Value 2>&1
        Assert-True ($LASTEXITCODE -eq 2) 'Actual guest command must propagate NotRun exit 2 across the process boundary'
    } finally { Pop-Location }
    $outputs=@{subscription_id='subscription';resource_group_name='rg';validation_vm_name='vm';vm_control_container_name='vm-control';storage_account_name='storage';function_app_name='function';function_app_url='https://function.azurewebsites.net';function_private_ip='10.20.2.4'}
    $package=Join-Path $temporary 'functions.zip'
    [IO.File]::WriteAllText($package,'fixture package bytes')
    $evidence=Join-Path $temporary 'evidence'
    $result=Invoke-PrivateVmOperation -Outputs $outputs -Operation Deploy -PackagePath $package -EvidenceDirectory $evidence -Smoke -TimeoutSeconds 60
    Assert-True ($result.exit_code -eq 0 -and $script:transportRequest.smoke -and $script:transportRequest.timeout_seconds -eq 60) 'Deploy should transfer smoke request and timeout'
    Assert-True ($script:transportPackageHash -eq (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash) 'Archive must preserve package contents'
    $script:transportAggregate=$true
    $result=Invoke-PrivateVmOperation -Outputs $outputs -Operation Deploy -PackagePath $package -EvidenceDirectory $evidence
    Assert-True ($result.exit_code -eq 0) 'Aggregate Run Command stdout must preserve a successful guest result'
    $script:transportGuestFails=$true
    $script:transportStderrMarker=$true
    Assert-Fails { Invoke-PrivateVmOperation -Outputs $outputs -Operation Deploy -PackagePath $package -EvidenceDirectory $evidence } 'failed .*raw output suppressed'
    $script:transportStderrMarker=$false
    $script:transportAggregate=$false
    $script:transportGuestFails=$true
    Assert-Fails { Invoke-PrivateVmOperation -Outputs $outputs -Operation Deploy -PackagePath $package -EvidenceDirectory $evidence } 'failed .*raw output suppressed'
    $script:transportGuestFails=$false
    $script:transportExit=1
    $script:transportFailureStage='scm_publish'
    Assert-Fails { Invoke-PrivateVmOperation -Outputs $outputs -Operation Deploy -PackagePath $package -EvidenceDirectory $evidence } 'stage=scm_publish; raw output suppressed'
    $script:transportFailureStage='https://secret.example/?code=secret'
    Assert-Fails { Invoke-PrivateVmOperation -Outputs $outputs -Operation Deploy -PackagePath $package -EvidenceDirectory $evidence } '^Private VM returned an invalid failure stage\.$'
    $script:transportFailureStage=$null
    Assert-Fails { Invoke-PrivateVmOperation -Outputs $outputs -Operation Deploy -PackagePath $package -EvidenceDirectory $evidence } 'failed .*raw output suppressed'
    $script:transportExit=0
    $result=Invoke-PrivateVmOperation -Outputs $outputs -Operation FunctionValidation -EvidenceDirectory $evidence -Smoke -TimeoutSeconds 60
    Assert-True ($result.exit_code -eq 0 -and $script:transportRequest.smoke -and $result.validation.Ready) 'FunctionValidation should transfer smoke and return validation'
    $script:transportExit=1
    $script:transportFailureStage='function_connectivity'
    Assert-Fails { Invoke-PrivateVmOperation -Outputs $outputs -Operation FunctionValidation -EvidenceDirectory $evidence } 'stage=function_connectivity; raw output suppressed'
    $script:transportFailureStage='function_validation'
    Assert-Fails { Invoke-PrivateVmOperation -Outputs $outputs -Operation FunctionValidation -EvidenceDirectory $evidence } 'stage=function_validation; raw output suppressed'
    $script:transportFailureStage=$null
    $script:transportExit=0
    $result=Invoke-PrivateVmOperation -Outputs $outputs -Operation PoolValidation -EvidenceDirectory $evidence -TimeoutSeconds 60
    Assert-True ($result.exit_code -eq 0 -and $script:transportRequest.operation -eq 'PoolValidation') 'PoolValidation should execute and return success'
    $script:transportExit=1
    $script:transportFailureStage='pool_validation'
    Assert-Fails { Invoke-PrivateVmOperation -Outputs $outputs -Operation PoolValidation -EvidenceDirectory $evidence } 'stage=pool_validation; raw output suppressed'
    $script:transportFailureStage=$null
    $script:transportExit=0
    $script:transportDownloads=@()
    $result=Invoke-PrivateVmOperation -Outputs $outputs -Operation AutoscaleValidation -EvidenceDirectory $evidence -ExpectedFunctionAppName 'app' -ExpectedPoolId 'pool' -AllowPoolAutoscaleTransition -DedicatedEnvironment
    Assert-True ($result.exit_code -eq 0 -and $script:transportRequest.operation -eq 'AutoscaleValidation' -and $script:transportRequest.expected_function_app_name -eq 'app' -and $script:transportRequest.expected_pool_id -eq 'pool' -and $script:transportRequest.allow_pool_autoscale_transition) 'AutoscaleValidation should transfer parameters and execute successfully'
    Assert-True ('autoscale-result.json' -in $script:transportDownloads -and 'summary.json' -in $script:transportDownloads -and 'summary.txt' -in $script:transportDownloads) 'AutoscaleValidation must retrieve autoscale-result and summaries'
    Assert-True ($script:transportLastShell -match 'timeout 2400 pwsh') 'AutoscaleValidation default timeout must compute guest timeout to reserve restoration time'
    Assert-True ($script:transportLastShell -match '\$env:POC_VM_DEADLINE_UTC\s*=\s*\$guestStartUtc\.AddSeconds\(\[int\]\$t\.budget_seconds\)\.ToString\("o"\)') 'Guest must initialize POC_VM_DEADLINE_UTC from transport budget_seconds'
    $result=Invoke-PrivateVmOperation -Outputs $outputs -Operation AutoscaleValidation -EvidenceDirectory $evidence -ExpectedFunctionAppName 'app' -ExpectedPoolId 'pool' -AllowPoolAutoscaleTransition -DedicatedEnvironment -TimeoutSeconds 3600
    Assert-True ($script:transportLastShell -match 'timeout 5100 pwsh') 'AutoscaleValidation with 3600s must compute guest timeout exceeding 4200 to reserve restoration time'
    $script:transportExit=1
    $script:transportFailureStage='autoscale_validation'
    Assert-Fails { Invoke-PrivateVmOperation -Outputs $outputs -Operation AutoscaleValidation -EvidenceDirectory $evidence } 'stage=autoscale_validation; raw output suppressed'
    $script:transportFailureStage=$null
    $script:transportExit=2
    $result=Invoke-PrivateVmOperation -Outputs $outputs -Operation Validate -Scenarios @('admission20') -EvidenceDirectory $evidence
    Assert-True ($result.exit_code -eq 2) 'Validate must preserve NotRun exit code 2'
    Assert-True ($script:transportRequest.scenarios.Count -eq 1 -and $script:transportRequest.scenarios[0] -eq 'admission20') 'Selected validation scenarios must reach the VM'
    Assert-True ('summary.json' -in $script:transportDownloads -and 'summary.txt' -in $script:transportDownloads) 'Validate must retrieve both sanitized summaries'
    $script:transportSummaryArtifacts = @('../../probe.txt')
    Assert-Fails { Invoke-PrivateVmOperation -Outputs $outputs -Operation Validate -Scenarios @('admission20') -EvidenceDirectory $evidence } 'untrusted or invalid summary artifact name'
    $script:transportSummaryArtifacts = @('unauthorized.json')
    Assert-Fails { Invoke-PrivateVmOperation -Outputs $outputs -Operation Validate -Scenarios @('admission20') -EvidenceDirectory $evidence } 'untrusted or invalid summary artifact name'
    $script:transportSummaryArtifacts = $null
    $script:transportSummaryDownloadFails = $true
    Assert-Fails { Invoke-PrivateVmOperation -Outputs $outputs -Operation Validate -Scenarios @('admission20') -EvidenceDirectory $evidence } 'failed to retrieve evidence summary file\(s\) \(summary\.json\)'
    $script:transportSummaryDownloadFails = $false
    $script:transportExit=0
    $script:transportDownloads=@()
    $result=Invoke-PrivateVmOperation -Outputs $outputs -Operation RecoveryStart -RecoveryContext @{delaySeconds=300} -EvidenceDirectory $evidence
    Assert-True ($result.exit_code -eq 0 -and $script:transportRequest.recovery_context.delaySeconds -eq 300) 'Recovery start must preserve the validated delay context'
    Assert-True ('summary.json' -in $script:transportDownloads -and 'summary.txt' -in $script:transportDownloads) 'Recovery must download sanitized phase summaries'
    $requestId='recovery-'+('a'*32)
    . (Join-Path $PSScriptRoot '../Recovery.Context.ps1')
    $context=@{request=@{requestId=$requestId; classification='light'; taskCount=1; failTaskIndex=$null; lightDelaySeconds=300; heavyDelaySeconds=3};instanceId=(Get-PocRecoveryIdentity $requestId);createdTime='2026-10-17T12:00:00Z';taskCreationTime='2026-10-17T12:00:01Z';runningObservedUtc='2026-10-17T12:00:03Z';taskStartedUtc='2026-10-17T12:00:02Z'}
    $result=Invoke-PrivateVmOperation -Outputs $outputs -Operation RecoveryComplete -RecoveryContext $context -EvidenceDirectory $evidence
    Assert-True ($result.exit_code -eq 0 -and $script:transportRequest.recovery_context.instanceId -eq $context.instanceId) 'Recovery completion must preserve safe request identity'
    $callCount=$script:transportCalls.Count
    $badContext=$context.Clone(); $badContext.statusUri='https://function.example/?code=PRIVATE-FUNCTION-KEY'
    Assert-Fails { Invoke-PrivateVmOperation -Outputs $outputs -Operation RecoveryComplete -RecoveryContext $badContext -EvidenceDirectory $evidence } 'InvalidRecoveryContext'
    $script:transportInvalidId=$false
    $script:transportDownloads=@()
    $result=Invoke-PrivateVmOperation -Outputs $outputs -Operation WorkflowAuth -AuthResource 'api://res' -AuthOwnerId 'owner1' -AuthOrgId 'org1' -AuthUnauthorizedClientId '11111111-2222-3333-4444-555555555555' -EvidenceDirectory $evidence
    Assert-True ($result.exit_code -eq 0 -and $script:transportRequest.auth_resource -eq 'api://res' -and $script:transportRequest.auth_owner_id -eq 'owner1' -and $script:transportRequest.auth_unauthorized_client_id -eq '11111111-2222-3333-4444-555555555555') 'WorkflowAuth must transfer resource and owner arguments'
    Assert-True ('summary.json' -in $script:transportDownloads) 'WorkflowAuth must download sanitized summary.json'
    Assert-Fails { Invoke-PrivateVmOperation -Outputs $outputs -Operation WorkflowAuth -EvidenceDirectory $evidence } 'WorkflowAuth requires AuthResource, AuthOwnerId, and AuthOrgId'
    $script:transportInvalidId=$true
    Assert-Fails { Invoke-PrivateVmOperation -Outputs $outputs -Operation Validate -EvidenceDirectory $evidence } 'invalid operation result'
    Assert-True (($script:transportCalls -join "`n") -notmatch 'PRIVATE-FUNCTION-KEY|--sas-token|[?&](sig|code)=') 'Transport commands must not carry secrets'
    function Start-Sleep { param([int]$Seconds) }
    $script:scmStates=@(0,4)
    $script:scmLocation='/api/deployments/fixture'
    $script:scmMethods=@()
    $script:scmHttpFailure=$false
    function Invoke-WebRequest {
        param([Uri]$Uri,[hashtable]$Headers,[int]$TimeoutSec,[int]$MaximumRedirection,[switch]$UseBasicParsing,[string]$ErrorAction,[string]$Method,[string]$ContentType,[string]$InFile)
        $script:scmMethods+=$Method
        Assert-True ($Uri.Scheme -eq 'https' -and $Uri.DnsSafeHost -eq 'function.scm.azurewebsites.net') 'SCM requests must stay on the expected HTTPS host'
        Assert-True ($MaximumRedirection -eq 0 -and $TimeoutSec -gt 0) 'SCM must suppress redirects and bound each HTTP request'
        Assert-True ($Headers.Authorization -eq 'Bearer PRIVATE-SCM-TOKEN') 'SCM must use the acquired Entra bearer token'
        if ($script:scmHttpFailure) {
            $httpError=[InvalidOperationException]::new('SECRET request details must not escape')
            $httpError | Add-Member -NotePropertyName Response -NotePropertyValue ([pscustomobject]@{StatusCode=403})
            throw $httpError
        }
        if ($Method -eq 'Post') {
            Assert-True ($InFile -eq $package -and $ContentType -eq 'application/zip') 'SCM must receive the Function ZIP'
            return [pscustomobject]@{StatusCode=202;Headers=@{Location=$script:scmLocation}}
        }
        $state=$script:scmStates[0]
        $script:scmStates=@($script:scmStates | Select-Object -Skip 1)
        return [pscustomobject]@{StatusCode=200;Content=(@{status=$state} | ConvertTo-Json)}
    }
    Publish-PrivateFunctionPackage $outputs $package 60
    Assert-True (($script:scmMethods -join ',') -eq 'Post,Get,Get') 'Asynchronous ZIP deployment must poll pending status until success'
    $script:scmStates=@(3)
    Assert-Fails { Publish-PrivateFunctionPackage $outputs $package 60 } '^Private SCM ZIP deployment failed; HTTP details suppressed\.$'
    Assert-True ($script:PrivateVmScmDiagnostics.stage -eq 'scm_poll' -and $null -eq $script:PrivateVmScmDiagnostics.http_status) 'SCM failure must preserve only the fixed polling stage'
    $script:scmHttpFailure=$true
    Assert-Fails { Publish-PrivateFunctionPackage $outputs $package 60 } '^Private SCM ZIP deployment failed; HTTP details suppressed\.$'
    Assert-True ($script:PrivateVmScmDiagnostics.stage -eq 'scm_upload' -and $script:PrivateVmScmDiagnostics.http_status -eq 403) 'SCM HTTP failure must preserve the fixed upload stage and numeric status only'
    $script:scmHttpFailure=$false
    $script:scmLocation='https://attacker.example/api/deployments/fixture'
    $script:scmMethods=@()
    Assert-Fails { Publish-PrivateFunctionPackage $outputs $package 60 } '^Private SCM ZIP deployment failed; HTTP details suppressed\.$'
    Assert-True (($script:scmMethods -join ',') -eq 'Post') 'An untrusted polling location must fail before a bearer token can be sent to it'
    Write-Host 'Private VM transport offline tests: PASS (bundle/package integrity, secret-free metadata, guest/deployment failure, invalid identity, NotRun and summaries, SCM async polling/failure/untrusted location; no Azure calls).'
} finally {
    $resolved=[IO.Path]::GetFullPath($temporary)
    $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if ($resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -like 'poc-transport-tests-*') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
