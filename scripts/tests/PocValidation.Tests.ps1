$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../Invoke-PocValidation.ps1')
$realConcurrentAdmission = ${function:Invoke-PocConcurrentAdmission}
$realGetPocBlobDocument = ${function:Get-PocBlobDocument}

$knownRequest=@{ requestId='known'; classification='auto'; taskCount=3; failTaskIndex=$null; lightDelaySeconds=1; heavyDelaySeconds=3 }
if ((Get-PocFingerprint -Request $knownRequest -Classification light) -ne '9f1c210ac5e0ca49867405274e67c50520202799d4a2ebed5e64db20e9518e08') { throw 'Canonical fingerprint differs from actual Poc.Core fixture (auto).' }
$knownRequest.classification='heavy'; $knownRequest.taskCount=1; $knownRequest.failTaskIndex=0
if ((Get-PocFingerprint -Request $knownRequest -Classification heavy) -ne 'a8a6d707ed0e9a618f08cf466dc14d1b55116fe39d30b1854e4adc30558f2f10') { throw 'Canonical fingerprint differs from actual Poc.Core fixture (heavy/failure).' }

function Assert-True([bool]$Value, [string]$Message) { if (-not $Value) { throw "ASSERT: $Message" } }
function Assert-Fails([scriptblock]$Action,[string]$Expected) {
    try { & $Action | Out-Null } catch {
        Assert-True ($_.Exception.Message -match $Expected) "Expected failure $Expected; got $($_.Exception.Message)"
        return
    }
    throw "ASSERT: expected failure $Expected"
}

# Published contracts are checked independently of provider and runner fakes.
foreach ($count in @(1,3)) {
    $ids=@(0..($count-1) | ForEach-Object { 'task-{0:00}' -f $_ })
    $aggregate=[pscustomobject]@{ requestId='known'; jobId='job'; fingerprint='fingerprint'; sum=$count; taskCount=$count; taskIds=$ids }
    $manifest=[pscustomobject]@{ requestId='known'; jobId='job'; fingerprint='fingerprint'; sum=$count; taskCount=$count; status='Succeeded'
        aggregateUri='https://blob.example/results/runs/poc-known/final/aggregate.json'
        taskUris=@($ids | ForEach-Object { "https://blob.example/results/runs/poc-known/tasks/$_/result.json" }) }
    $arguments=@{ RequestId='known'; TaskCount=$count; Aggregate=$aggregate; Manifest=$manifest; BlobRoot='https://blob.example/results'; JobId='job'; Fingerprint='fingerprint' }
    Assert-PocPublishedDocuments @arguments
    $manifest.fingerprint='tampered'
    Assert-Fails { Assert-PocPublishedDocuments @arguments } 'inconsistent'
    $manifest.fingerprint='fingerprint'; $manifest.taskUris[0]+='?sig=secret'
    Assert-Fails { Assert-PocPublishedDocuments @arguments } 'inconsistent'
    $manifest.taskUris[0]='https://blob.example/results/runs/poc-known/tasks/task-00/result.json'
    $aggregate.sum++
    Assert-Fails { Assert-PocPublishedDocuments @arguments } 'inconsistent'
}

# These fakes deliberately carry secret markers in untrusted extra fields. Evidence
# must select known fields, rather than serializing provider responses wholesale.
$script:secret = 'DO-NOT-LOG-VALIDATION-SECRET'
$script:mode = 'success'
$script:requests = @{}
$script:statusReads = 0
$script:posts = 0
$script:parallelCount = 0
$script:batchReads = 0
$script:historyReads = 0
$script:current = $null
$script:AzPath='unused-fake-az'
$script:outputs = @{
    subscription_id='subscription'; resource_group_name='poc-rg'; function_app_name='poc-app'
    function_app_url='https://poc.example'; storage_account_name='pocstorage'; blob_container_name='poc-results'
    blob_endpoint='https://pocstorage.blob.core.windows.net/'; batch_endpoint='https://batch.example'
    batch_pool_id='pool'; batch_account_name='account'; batch_resource_group_name='batch-rg'
}

function Get-PocValidationKey { param($Outputs) return $script:secret }
function Get-PocWorkflowSettings { param($Outputs) return @{ TimeoutMinutes=1; PollSeconds=1 } }
function Get-FakeHash([string]$Value) {
    $hash=[Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-','').ToLowerInvariant() }
    finally { $hash.Dispose() }
}
function Get-PocJobId([string]$RequestId) { return 'poc-' + (Get-FakeHash $RequestId).Substring(0,40) }
function Get-FakeFingerprint($Request) {
    $classification=if ($Request.classification -eq 'auto') { 'light' } else { $Request.classification }
    $canonical=[ordered]@{ Version=1; Request=[ordered]@{ RequestId=$Request.requestId; Classification=$Request.classification; TaskCount=[int]$Request.taskCount
        FailTaskIndex=$Request.failTaskIndex; LightDelaySeconds=[int]$Request.lightDelaySeconds; HeavyDelaySeconds=[int]$Request.heavyDelaySeconds }; Classification=$classification }
    return Get-FakeHash ($canonical | ConvertTo-Json -Compress -Depth 6)
}
function New-FakeAdmission {
    param([string]$Body)
    $request = $Body | ConvertFrom-Json
    $script:posts++
    if ($script:requests.ContainsKey($request.requestId)) {
        $existing = $script:requests[$request.requestId]
        if ($existing.classification -ne $request.classification -or $existing.taskCount -ne $request.taskCount -or
            $existing.lightDelaySeconds -ne $request.lightDelaySeconds -or $existing.heavyDelaySeconds -ne $request.heavyDelaySeconds) {
            return @{ StatusCode=409; Data=[pscustomobject]@{ error=$script:secret } }
        }
    }
    else { $script:requests[$request.requestId] = $request }
    $script:current = $script:requests[$request.requestId]
    $instance = Get-PocJobId -RequestId $request.requestId
    return @{ StatusCode=202; Data=[pscustomobject]@{
        instanceId=$instance; statusQueryGetUri="https://poc.example/runtime/webhooks/durabletask/instances/$instance`?code=$script:secret"
        terminatePostUri="https://poc.example/terminate?code=$script:secret"; unexpected=$script:secret
    } }
}

function Invoke-PocValidationHttp {
    param([string]$Uri, [string]$Method='Get', [hashtable]$Headers=@{}, [string]$Body)
    if ($Method -eq 'Post') {
        Assert-True ($Headers['x-functions-key'] -eq $script:secret) 'Starter must receive its key through the header'
        if ($script:mode -eq 'starterError') { throw "Secret provider failure: $script:secret $Uri" }
        return New-FakeAdmission -Body $Body
    }
    $script:statusReads++
    if ($script:mode -eq 'pending' -and $script:statusReads -eq 1) { return @{ StatusCode=404; Data=$null } }
    if ($script:mode -eq 'acceptedRunning' -and $script:statusReads -eq 1) { return @{ StatusCode=202; Data=[pscustomobject]@{runtimeStatus='Running'} } }
    if ($script:mode -eq 'invalidAccepted') { return @{ StatusCode=202; Data=[pscustomobject]@{runtimeStatus='invalid'} } }
    if ($script:mode -eq 'missingAccepted') { return @{ StatusCode=202; Data=[pscustomobject]@{unexpected=$script:secret} } }
    if ($script:mode -eq 'neverStarted') { return @{ StatusCode=404; Data=$null } }
    if ($script:mode -eq 'running') { return @{ StatusCode=200; Data=[pscustomobject]@{ runtimeStatus='Running' } } }
    if ($script:mode -in @('401','403')) { return @{ StatusCode=[int]$script:mode; Data=[pscustomobject]@{ error=$script:secret } } }
    $request = $script:current
    $state = 'Succeeded'; $runtime='Completed'
    if ($null -ne $request.failTaskIndex -or $script:mode -eq 'unexpectedFailure') { $state='Failed'; $runtime='Failed' }
    if ($script:mode -in @('timeout','terminationFailed','timeoutNoConstraint','timeoutWrongConstraint','timeoutBadConstraint')) { $state='TimedOut'; $runtime='Failed' }
    return @{ StatusCode=200; Data=[pscustomobject]@{
        runtimeStatus=$runtime; createdTime='2026-10-17T00:00:00Z'; output=[pscustomobject]@{ state=$state; requestId=$request.requestId; jobId=(Get-PocJobId $request.requestId) }
        customStatus=[pscustomobject]@{ state=$state; jobId=(Get-PocJobId $request.requestId); error=$script:secret
            termination=[pscustomobject]@{ state='Completed'; completed=($script:mode -ne 'terminationFailed') }; terminationError=$(if ($script:mode -eq 'terminationFailed') { $script:secret } else { $null }) }
        input=$request; unexpected=$script:secret
    } }
}

function Invoke-PocConcurrentAdmission {
    param([string]$Uri,[hashtable]$Headers,[string]$Body,[int]$Count=20)
    $script:parallelCount=$Count
    $responses = @(1..$Count | ForEach-Object { New-FakeAdmission -Body $Body })
    if ($script:mode -eq 'splitAdmission') { $responses[-1].Data.instanceId='another-instance' }
    return $responses
}

function Get-PocHistoryObservation {
    param([string]$StatusUri)
    $script:historyReads++
    if ($script:mode -eq 'historyUnavailable') { return @{ Available=$false; ExecutionStarts=$null } }
    return @{ Available=$true; ExecutionStarts=$(if ($script:mode -eq 'duplicateHistory') { 2 } else { 1 }) }
}

function Test-PocBlobExists {
    param($Outputs,[string]$Path)
    return $script:mode -eq 'leakedManifest'
}

function Get-PocBlobDocument {
    param($Outputs,[string]$Path)
    $request=$script:current; $jobId=Get-PocJobId $request.requestId; $fingerprint=Get-FakeFingerprint $request
    $classification=if ($request.classification -eq 'auto') { 'light' } else { $request.classification }
    $root=$Outputs.blob_endpoint.TrimEnd('/')+'/'+$Outputs.blob_container_name+"/runs/poc-$($request.requestId)"
    $ids=@(0..($request.taskCount-1) | ForEach-Object { 'task-{0:00}' -f $_ })
    if ($Path -match '/request.json$') {
        return [pscustomobject]@{ version=1; instanceId=$jobId; request=$request; unexpected=$script:secret }
    }
    $document=[pscustomobject]@{ requestId=$request.requestId; jobId=$jobId; fingerprint=$fingerprint; unexpected=$script:secret }
    if ($Path -match '/tasks/(task-[0-9]+)/result.json$') {
        $document | Add-Member -NotePropertyMembers @{ taskId=$Matches[1]; classification=$classification; reason='Fixture classification'; value=1 }
        if ($script:mode -eq 'badTask') { $document.value=2 }
    }
    elseif ($Path -match '/aggregate.json$') {
        $document | Add-Member -NotePropertyMembers @{ taskCount=[int]$request.taskCount; sum=[int]$request.taskCount; taskIds=$ids }
        if ($script:mode -eq 'badAggregate') { $document.sum=99 }
    }
    elseif ($Path -match '/manifest.json$') {
        $document | Add-Member -NotePropertyMembers @{ taskCount=[int]$request.taskCount; sum=[int]$request.taskCount; status='Succeeded'
            aggregateUri="$root/final/aggregate.json"; taskUris=@($ids | ForEach-Object { "$root/tasks/$_/result.json" }) }
        if ($script:mode -eq 'badFingerprint') { $document.fingerprint='wrong' }
        if ($script:mode -eq 'badUri') { $document.aggregateUri+='?sig='+$script:secret }
    }
    else { throw "Unexpected fixture Blob path: $Path" }
    return $document
}

function Get-PocBatchObservation {
    param($Outputs,[string]$JobId)
    $script:batchReads++
    $request=$script:current; $fingerprint=Get-FakeFingerprint $request
    $job=[pscustomobject]@{ id=$JobId; state=$(if ($script:mode -like 'timeout*') { 'completed' } else { 'active' })
        poolInfo=[pscustomobject]@{ poolId=$Outputs.batch_pool_id }; metadata=@([pscustomobject]@{ name='poc-fingerprint'; value=$fingerprint })
        constraints=[pscustomobject]@{ maxWallClockTime='PT1M' }; unexpected=$script:secret }
    if ($script:mode -eq 'timeoutNoConstraint') { $job.constraints=$null }
    if ($script:mode -eq 'timeoutWrongConstraint') { $job.constraints.maxWallClockTime='PT2M' }
    if ($script:mode -eq 'timeoutBadConstraint') { $job.constraints.maxWallClockTime=$script:secret }
    $tasks=@(0..($request.taskCount-1) | ForEach-Object {
        $exit=if ($null -ne $request.failTaskIndex -and $_ -eq $request.failTaskIndex) { 42 } else { 0 }
        if ($script:mode -eq 'wrongExit' -and $_ -eq $request.failTaskIndex) { $exit=7 }
        [pscustomobject]@{ id=('task-{0:00}' -f $_); state='completed'; executionInfo=[pscustomobject]@{ exitCode=$exit; result=$(if ($exit -eq 0) { 'success' } else { 'failure' }); failureInfo=$null }
            environmentSettings=@([pscustomobject]@{ name='POC_FINGERPRINT'; value=$fingerprint }); unexpected=$script:secret }
    })
    if ($script:mode -eq 'extraTask') { $tasks+= $tasks[0] }
    return @{ Job=$job; Tasks=$tasks }
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('poc-validation-tests-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temporary
try {
    function Invoke-AzJson {
        param([string[]]$Arguments,[switch]$Sensitive)
        Assert-True ($Sensitive -and $Arguments[0] -eq 'storage' -and $Arguments[2] -eq 'download' -and '--no-progress' -in $Arguments) 'Runner Blob transfer must suppress progress and sensitive output'
        $file=$Arguments[[Array]::IndexOf($Arguments,'--file')+1]
        [IO.File]::WriteAllText($file,'{"transfer":"checked"}')
    }
    $transfer=& $realGetPocBlobDocument -Outputs $script:outputs -Path 'fixture.json'
    Assert-True ($transfer.transfer -eq 'checked') 'Real Blob document reader must parse the downloaded JSON'
    $deployment=Join-Path $temporary 'deployment-result.json'
    @{ outputs=$script:outputs; package_sha256=('a'*64); secret=$script:secret } | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath $deployment
    $script:caseNumber=0
    function Invoke-TestCase {
        param([string]$Mode,[string[]]$Scenarios,[int]$ExpectedCode,[string]$ExpectedStatus,[switch]$DedicatedEnvironment,[int]$Wait=2)
        $script:mode=$Mode; $script:requests=@{}; $script:statusReads=0; $script:posts=0
        $script:parallelCount=0; $script:batchReads=0; $script:historyReads=0; $script:current=$null; $script:caseNumber++
        $directory=Join-Path $temporary ([string]$script:caseNumber)
        $summary=Invoke-PocValidation -DeploymentResultPath $deployment -Scenarios $Scenarios -EvidenceDirectory $directory -TimeoutSeconds $Wait -PollSeconds 1 -DedicatedEnvironment:$DedicatedEnvironment
        Assert-True ($summary.ExitCode -eq $ExpectedCode) "$Mode expected exit $ExpectedCode; got $($summary.ExitCode): $($summary.Scenarios | ConvertTo-Json -Depth 10 -Compress)"
        Assert-True ($summary.Scenarios[0].Status -eq $ExpectedStatus) "$Mode expected $ExpectedStatus"
        $saved=Get-Content -LiteralPath (Join-Path $directory 'summary.json') -Raw
        $readable=Get-Content -LiteralPath (Join-Path $directory 'summary.txt') -Raw
        Assert-True ($saved -notmatch $script:secret -and $readable -notmatch $script:secret) "$Mode evidence must exclude all secret-bearing provider fields/errors"
        Assert-True ($saved -notmatch 'statusQueryGetUri|terminatePostUri|x-functions-key|https://poc.example/status') 'Management URLs and auth headers must never be evidence'
        $persisted=$saved | ConvertFrom-Json
        Assert-True ($persisted.ExitCode -eq $ExpectedCode -and $persisted.completedUtc) 'Summary must persist through success/failure/NotRun'
        return $summary
    }
    $result=Invoke-TestCase success @('single','three','classification','failure','admission20','conflict') 0 Pass
    Assert-True ($result.Scenarios.Count -eq 6 -and @($result.Scenarios | Where-Object Status -ne Pass).Count -eq 0) 'All expected outcomes should pass'
    Assert-True ($script:parallelCount -eq 20 -and $script:historyReads -eq 1) 'Concurrent scenario must send twenty admissions and inspect history'
    Assert-True ($result.Scenarios[2].Evidence.runs.Count -eq 3) 'Classification must cover explicit light, heavy and auto'
    Assert-True ($result.packageSha256 -eq ('a'*64) -and $result.gitRevision -match '^[a-f0-9]{40}$') 'Evidence must record deployment hash and Git revision'
    Assert-True ($result.sourceSha256 -match '^[a-f0-9]{64}$') 'Evidence must identify local source including uncommitted files'
    Assert-True ($null -eq $result.settings.batchNodeType) 'Legacy results must not claim an observed capacity type'
    Assert-True ($result.settings.durableBackend -ceq 'azureStorage' -and $result.settings.taskHubName -ceq 'PocBatchHub') 'Legacy results must retain configured original durable identity'
    $script:outputs.durable_backend='durableTaskScheduler'; $script:outputs.task_hub_name='SchedulerHub'
    @{ outputs=$script:outputs; package_sha256=('a'*64) } | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath $deployment
    $scheduler=Invoke-TestCase success @('single') 0 Pass
    Assert-True ($scheduler.settings.durableBackend -ceq 'durableTaskScheduler' -and $scheduler.settings.taskHubName -ceq 'SchedulerHub') 'Summary must retain configured Scheduler metadata without claiming provider validation'
    $script:outputs.durable_backend='invalid'
    @{ outputs=$script:outputs } | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath $deployment
    $script:posts=0
    $badDurable=Invoke-PocValidation -DeploymentResultPath $deployment -Scenarios @('single') -EvidenceDirectory (Join-Path $temporary 'invalid-durable')
    Assert-True ($badDurable.ExitCode -eq 1 -and $badDurable.Scenarios[0].Reason -eq 'InvalidDurableSettings' -and $script:posts -eq 0) 'Untrusted durable metadata must fail before work submission'
    $script:outputs.Remove('durable_backend'); $script:outputs.Remove('task_hub_name')
    @{ outputs=$script:outputs; package_sha256=('a'*64) } | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath $deployment
    $accepted=Invoke-TestCase acceptedRunning @('single') 0 Pass
    Assert-True ($script:statusReads -eq 2 -and $accepted.Scenarios[0].Evidence.runs[0].statusHttpCounts['202'] -eq 1 -and $accepted.Scenarios[0].Evidence.runs[0].statusHttpCounts['200'] -eq 1) 'HTTP 202 Running must be polled until HTTP 200 Completed, preserving both observations'
    foreach ($invalid in @('invalidAccepted','missingAccepted')) {
        $invalidStatus=Invoke-TestCase $invalid @('single') 1 Fail
        Assert-True ($invalidStatus.Scenarios[0].Reason -eq 'InvalidStatusResponse' -and $script:statusReads -eq 1) 'HTTP 202 with absent or unknown runtime status must fail immediately'
    }
    $script:outputs.batch_node_type='spot'
    @{ outputs=$script:outputs; package_sha256=('a'*64) } | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath $deployment
    $spot=Invoke-TestCase success @('single') 0 Pass
    Assert-True ($spot.settings.batchNodeType -eq 'spot' -and 'SpotEvictionRecovery' -in $spot.excluded) 'Evidence must record configured Spot capacity and leave eviction recovery unvalidated'
    $script:outputs.batch_node_type='invalid'
    @{ outputs=$script:outputs } | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath $deployment
    $script:posts=0
    $invalidType=Invoke-PocValidation -DeploymentResultPath $deployment -Scenarios @('single') -EvidenceDirectory (Join-Path $temporary 'invalid-capacity')
    Assert-True ($invalidType.ExitCode -eq 1 -and $invalidType.Scenarios[0].Reason -eq 'InvalidBatchNodeType' -and $script:posts -eq 0) 'Invalid capacity metadata must fail without task submission'
    $script:outputs.Remove('batch_node_type')
    # A VM bundle has no .git. It must preserve validated source identity from the
    # initiating checkout, without trying to inspect a nonexistent guest checkout.
    $provenance=@{ gitRevision=('b'*40); gitDirty=$true; sourceSha256=('c'*64) }
    @{ outputs=$script:outputs; package_sha256=('a'*64); source_provenance=$provenance } | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath $deployment
    function git { throw 'Guest bundle must not invoke Git.' }
    $realResolver=${function:Resolve-DeploymentTool}
    function Resolve-DeploymentTool { param([string]$Name,[string]$RepositoryRoot); if (-not $RepositoryRoot) { throw 'Missing tool resolution root.' }; return 'guest-fake-az' }
    Remove-Variable AzPath -Scope Script
    try {
        $remote=Invoke-TestCase success @('single') 0 Pass
        Assert-True ($remote.gitRevision -eq ('b'*40) -and $remote.gitDirty -and $remote.sourceSha256 -eq ('c'*64)) 'Guest must preserve the initiating source identity'
        $provenance.sourceSha256='malformed'
        @{ outputs=$script:outputs; source_provenance=$provenance } | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath $deployment
        $invalid=Invoke-PocValidation -DeploymentResultPath $deployment -Scenarios @('single') -EvidenceDirectory (Join-Path $temporary 'invalid-provenance')
        Assert-True ($invalid.ExitCode -eq 1 -and $invalid.Scenarios[0].Reason -eq 'InvalidSourceProvenance') 'Invalid provenance must fail before any workflow is submitted'
    } finally { Remove-Item Function:\git; Set-Item Function:\Resolve-DeploymentTool $realResolver; $script:AzPath='unused-fake-az' }
    @{ outputs=$script:outputs; package_sha256=('a'*64); secret=$script:secret } | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath $deployment
    $result=Invoke-TestCase pending @('single') 0 Pass -Wait 3
    Assert-True ($script:statusReads -eq 2) 'Transient initial 404 must be retried'
    Assert-True ($result.Scenarios[0].Evidence.runs[0].statusHttpCounts['404'] -eq 1 -and $result.Scenarios[0].Evidence.runs[0].statusHttpCounts['200'] -eq 1) 'Polling evidence must retain safe HTTP counts'
    foreach ($mode in @('neverStarted','running','unexpectedFailure','starterError','badTask','badAggregate','badFingerprint','badUri','extraTask')) {
        $null=Invoke-TestCase $mode @('single') 1 Fail -Wait 1
    }
    foreach ($mode in @('401','403')) {
        $null=Invoke-TestCase $mode @('single') 1 Fail
        Assert-True ($script:statusReads -eq 1) 'Authorization failures must not be polled repeatedly'
    }
    foreach ($mode in @('splitAdmission','duplicateHistory')) { $null=Invoke-TestCase $mode @('admission20') 1 Fail }
    $null=Invoke-TestCase historyUnavailable @('admission20') 2 NotRun
    Assert-True ($script:posts -eq 21 -and $script:batchReads -eq 2) 'Admission observation must include completed resend and Batch Task reinspection'
    foreach ($mode in @('wrongExit','leakedManifest')) { $null=Invoke-TestCase $mode @('failure') 1 Fail }
    $null=Invoke-TestCase timeout @('timeout') 2 NotRun
    Assert-True ($script:posts -eq 0) 'Timeout must not submit work without dedicated-environment acknowledgement'
    $null=Invoke-TestCase timeout @('timeout') 2 NotRun -DedicatedEnvironment
    Assert-True ($script:posts -eq 0) 'Timeout must not submit work when runner wait is too short for stop confirmation'
    $timeoutResult=Invoke-TestCase timeout @('timeout') 0 Pass -DedicatedEnvironment -Wait 240
    Assert-True ($timeoutResult.Scenarios[0].Evidence.runs[0].batchMaxWallClockSeconds -eq 60 -and $timeoutResult.Scenarios[0].Evidence.runs[0].batchTerminationReason -eq 'Unobserved') 'Timeout evidence must observe the Batch constraint without inventing a termination reason'
    foreach ($constraintMode in @('timeoutNoConstraint','timeoutWrongConstraint','timeoutBadConstraint')) {
        $constraintResult=Invoke-TestCase $constraintMode @('timeout') 1 Fail -DedicatedEnvironment -Wait 240
        Assert-True ($constraintResult.Scenarios[0].Reason -in @('BatchWallClockConstraintMismatch','InvalidBatchWallClockConstraint')) 'Missing, different or malformed Batch safety-net constraints must fail'
    }
    $null=Invoke-TestCase terminationFailed @('timeout') 1 Fail -DedicatedEnvironment -Wait 240
    $mixed=Invoke-TestCase unexpectedFailure @('single','timeout') 1 Fail
    Assert-True ($mixed.Scenarios.Count -eq 2 -and $mixed.Scenarios[1].Status -eq 'NotRun') 'Fail must outrank NotRun and later scenarios must still be recorded'

    # Setup failures still emit summary and mark selected scenarios as NotRun.
    $badDeployment=Join-Path $temporary 'invalid.json'
    [IO.File]::WriteAllText($badDeployment,'not-json '+$script:secret)
    $badDirectory=Join-Path $temporary 'setup-failure'
    $bad=Invoke-PocValidation -DeploymentResultPath $badDeployment -Scenarios @('single','three') -EvidenceDirectory $badDirectory
    $badEvidence=Get-Content -LiteralPath (Join-Path $badDirectory 'summary.json') -Raw
    Assert-True ($bad.ExitCode -eq 1 -and @($bad.Scenarios | Where-Object Status -eq NotRun).Count -eq 2 -and $badEvidence -notmatch $script:secret) 'Setup failure must save sanitized summary and unrun selections'
    $empty=Invoke-PocValidation -DeploymentResultPath $deployment -Scenarios @() -EvidenceDirectory (Join-Path $temporary 'empty-selection')
    Assert-True ($empty.ExitCode -eq 1 -and $empty.Scenarios[0].Reason -eq 'EmptyScenarioSelection') 'An empty suite must not report success'

    # Real HttpClient transport against a loopback listener. No response is released
    # until twenty connections arrive (or the bounded overlap window expires).
    # A sequential sender cannot satisfy this barrier.
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
public sealed class PocAdmissionOverlapServer : IDisposable {
    private readonly TcpListener listener;
    public readonly Task Worker;
    public readonly int Port;
    public int BeforeFirstResponse;
    public PocAdmissionOverlapServer() {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Worker = Task.Factory.StartNew(Run, TaskCreationOptions.LongRunning);
    }
    private void Respond(TcpClient client) {
        using (client) {
            client.ReceiveTimeout = 3000;
            var stream = client.GetStream();
            var reader = new System.IO.StreamReader(stream, Encoding.UTF8, false, 1024, true);
            int length = 0;
            string line;
            while (!String.IsNullOrEmpty(line = reader.ReadLine())) {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = Int32.Parse(line.Substring(15).Trim());
            }
            var body = new char[length];
            int read = 0;
            while (read < length) { int n = reader.Read(body, read, length-read); if (n == 0) break; read += n; }
            var payload = "{\"instanceId\":\"same\",\"statusQueryGetUri\":\"https://poc.example/status\"}";
            var bytes = Encoding.UTF8.GetBytes("HTTP/1.1 202 Accepted\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(payload) + "\r\nConnection: close\r\n\r\n" + payload);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
    private void Run() {
        var clients = new List<TcpClient>();
        var watch = Stopwatch.StartNew();
        while (clients.Count < 20 && watch.ElapsedMilliseconds < 4000) {
            if (listener.Pending()) clients.Add(listener.AcceptTcpClient());
            else System.Threading.Thread.Sleep(5);
        }
        BeforeFirstResponse = clients.Count;
        foreach (var client in clients) Respond(client);
        int total = clients.Count;
        while (total < 20 && watch.ElapsedMilliseconds < 10000) {
            if (listener.Pending()) { Respond(listener.AcceptTcpClient()); total++; }
            else System.Threading.Thread.Sleep(5);
        }
    }
    public void Dispose() { listener.Stop(); }
}
'@
    $server=[PocAdmissionOverlapServer]::new()
    try {
        $responses=@(& $realConcurrentAdmission -Uri "http://127.0.0.1:$($server.Port)/workflows" -Headers @{ 'x-functions-key'='loopback-only' } -Body '{}' -Count 20)
        $server.Worker.GetAwaiter().GetResult()
        Assert-True ($responses.Count -eq 20 -and $server.BeforeFirstResponse -eq 20) 'Real admission transport must start all twenty sends before awaiting a response'
    }
    finally { $server.Dispose() }

    # Exercise the real HTTP wrapper with exception objects shaped like Windows
    # PowerShell HTTP failures, without a network call or provider credentials.
    . (Join-Path $PSScriptRoot '../Invoke-PocValidation.ps1')
    $script:httpStatus=404
    function Invoke-WebRequest {
        $exception=[InvalidOperationException]::new('Secret URL '+$script:secret)
        $exception | Add-Member -NotePropertyName Response -NotePropertyValue ([pscustomobject]@{ StatusCode=$script:httpStatus })
        throw $exception
    }
    foreach ($code in @(404,409)) {
        $script:httpStatus=$code
        $response=Invoke-PocValidationHttp -Uri ('https://poc.example/status?code='+$script:secret)
        Assert-True ($response.StatusCode -eq $code -and $null -eq $response.Data) 'Expected HTTP statuses must survive wrapper classification without raw body'
    }
    foreach ($code in @(401,403)) {
        $script:httpStatus=$code
        Assert-Fails { Invoke-PocValidationHttp -Uri ('https://poc.example/status?code='+$script:secret) } "HttpAuthorization$code"
    }
    $script:httpStatus=500
    Assert-Fails { Invoke-PocValidationHttp -Uri ('https://poc.example/status?code='+$script:secret) } '^HttpRequestFailed$'
    Write-Host 'PoC validation offline tests: PASS (contracts, scenarios, corruption, bounded polling, evidence redaction, summaries, real loopback twenty-send overlap; no Azure calls).'
}
finally {
    # This is an absolute, freshly generated directory owned only by this test.
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
