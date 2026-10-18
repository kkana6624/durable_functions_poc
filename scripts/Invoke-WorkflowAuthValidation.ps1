[CmdletBinding()]
param([string]$DeploymentResultPath,[string]$Resource,[string]$ManagedIdentityClientId,
    [string]$OwnerId,[string]$OrgId,[string]$EvidenceDirectory,
    [ValidateRange(1,3600)][int]$TimeoutSeconds=900,[ValidateRange(1,60)][int]$PollSeconds=5)

. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')

function Stop-WorkflowAuthValidation([string]$Code) {
    $errorObject=[InvalidOperationException]::new($Code)
    $errorObject.Data['WorkflowAuthCode']=$Code
    throw $errorObject
}

function Get-WorkflowAuthToken([string]$Resource,[string]$ClientId) {
    # VM managed identity only. No user CLI token, raw token argument, or disk cache.
    $uri='http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource='+[Uri]::EscapeDataString($Resource)
    if ($ClientId) { $uri+='&client_id='+[Uri]::EscapeDataString($ClientId) }
    Add-Type -AssemblyName System.Net.Http
    $handler=[Net.Http.HttpClientHandler]::new(); $handler.UseProxy=$false; $handler.AllowAutoRedirect=$false
    $client=[Net.Http.HttpClient]::new($handler); $client.Timeout=[TimeSpan]::FromSeconds(20)
    $null=$client.DefaultRequestHeaders.TryAddWithoutValidation('Metadata','true')
    $httpResponse=$null
    try {
        $httpResponse=$client.GetAsync($uri).GetAwaiter().GetResult()
        if (-not $httpResponse.IsSuccessStatusCode) { throw 'Token request rejected' }
        $response=$httpResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()|ConvertFrom-Json
        if (-not $response.access_token -or $response.token_type -cne 'Bearer') { throw 'Invalid token response' }
        return [string]$response.access_token
    } catch { Stop-WorkflowAuthValidation 'ManagedIdentityTokenUnavailable' }
    finally { if ($httpResponse) {$httpResponse.Dispose()}; $client.Dispose(); $handler.Dispose() }
}

function Get-WorkflowAuthKeys([hashtable]$Outputs) {
    $result=@{}
    foreach ($name in @('StartWorkflow','GetWorkflowStatus','GetWorkflowResult')) {
        try {
            $keys=Invoke-AzJson -Sensitive -Arguments @('functionapp','function','keys','list','--name',$Outputs.function_app_name,
                '--resource-group',$Outputs.resource_group_name,'--function-name',$name)
            if (-not $keys.default) { throw 'Missing key' }
            $result[$name]=[string]$keys.default
        } catch { Stop-WorkflowAuthValidation 'FunctionKeyUnavailable' }
    }
    return $result
}

function Invoke-WorkflowAuthHttp([string]$Uri,[string]$Method,[hashtable]$Headers,[string]$Body) {
    # HttpClient avoids PowerShell HTTP error records containing URLs/headers/bodies.
    Add-Type -AssemblyName System.Net.Http
    $handler=[Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect=$false
    $client=[Net.Http.HttpClient]::new($handler); $client.Timeout=[TimeSpan]::FromSeconds(60)
    $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method),$Uri)
    $response=$null
    try {
        foreach ($name in $Headers.Keys) { $null=$request.Headers.TryAddWithoutValidation($name,[string]$Headers[$name]) }
        if ($Body) { $request.Content=[Net.Http.StringContent]::new($Body,[Text.Encoding]::UTF8,'application/json') }
        $response=$client.SendAsync($request).GetAwaiter().GetResult()
        $code=[int]$response.StatusCode
        $data=$null; $location=$null
        if ($code -in @(200,202)) {
            $content=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ($content.Length -gt 65536) { Stop-WorkflowAuthValidation 'OversizedResponse' }
            $data=ConvertTo-DeploymentHashtable ($content | ConvertFrom-Json)
            if ($response.Headers.Location) { $location=$response.Headers.Location.ToString() }
        }
        return @{Code=$code;Data=$data;Location=$location}
    } catch { Stop-WorkflowAuthValidation 'HttpRequestFailed' }
    finally { if ($response) {$response.Dispose()}; $request.Dispose(); $client.Dispose(); $handler.Dispose() }
}

function Assert-WorkflowAuthUri([string]$Value,[string]$Origin,[string]$Path) {
    $parsed=$null
    if (-not [Uri]::TryCreate($Value,[UriKind]::Absolute,[ref]$parsed) -or $parsed.Scheme -cne 'https' -or
        $parsed.Authority -cne ([Uri]$Origin).Authority -or $parsed.UserInfo -or $parsed.Query -or $parsed.Fragment -or
        $parsed.AbsolutePath -cne $Path) { Stop-WorkflowAuthValidation 'InvalidPublicUri' }
}

function Invoke-WorkflowAuthValidation {
    [CmdletBinding()]
    param([string]$DeploymentResultPath,[string]$Resource,[string]$ManagedIdentityClientId,[string]$OwnerId,[string]$OrgId,
        [string]$EvidenceDirectory,[int]$TimeoutSeconds=900,[int]$PollSeconds=5)
    $ErrorActionPreference='Stop'
    $summary=[ordered]@{schemaVersion=1;validationKind='AzureWorkflowAuth';status='Fail';reason='SetupFailed';checks=@();
        configuredDurableBackend=$null;configuredTaskHub=$null;
        provenance=@{source='deploymentDescriptor';livePackageVerified=$false;gitRevision=$null;gitDirty=$null;sourceSha256=$null;packageSha256=$null};
        completedUtc=$null;exitCode=1}
    $token=$null; $keys=$null; $headers=$null
    try {
        if ($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 3600 -or $PollSeconds -lt 1 -or $PollSeconds -gt 60) { Stop-WorkflowAuthValidation 'InvalidTiming' }
        if ([string]::IsNullOrWhiteSpace($Resource) -or $Resource -match '[\r\n?#]' -or
            $OwnerId -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._@-]{0,127}$' -or $OrgId -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._@-]{0,127}$' -or
            ($ManagedIdentityClientId -and $ManagedIdentityClientId -notmatch '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$')) { Stop-WorkflowAuthValidation 'InvalidAuthConfiguration' }
        $deployment=ConvertTo-DeploymentHashtable (Get-Content -LiteralPath $DeploymentResultPath -Raw | ConvertFrom-Json)
        # Descriptor metadata is claimed provenance, not a live deployed-code measurement.
        if ($deployment.ContainsKey('source_provenance') -and $deployment.source_provenance -is [hashtable]) {
            $provenance=$deployment.source_provenance
            if ($provenance.ContainsKey('gitRevision') -and $provenance.gitRevision -is [string] -and $provenance.gitRevision -cmatch '\A[0-9a-f]{40}\z') { $summary.provenance.gitRevision=$provenance.gitRevision }
            if ($provenance.ContainsKey('gitDirty') -and $provenance.gitDirty -is [bool]) { $summary.provenance.gitDirty=$provenance.gitDirty }
            if ($provenance.ContainsKey('sourceSha256') -and $provenance.sourceSha256 -is [string] -and $provenance.sourceSha256 -cmatch '\A[0-9a-f]{64}\z') { $summary.provenance.sourceSha256=$provenance.sourceSha256 }
        }
        if ($deployment.ContainsKey('package_sha256') -and $deployment.package_sha256 -is [string] -and $deployment.package_sha256 -cmatch '\A[0-9a-f]{64}\z') { $summary.provenance.packageSha256=$deployment.package_sha256 }
        $outputs=$deployment.outputs
        foreach ($field in @('subscription_id','resource_group_name','function_app_name','function_app_url')) {
            if (-not $outputs.ContainsKey($field) -or [string]::IsNullOrWhiteSpace([string]$outputs[$field])) { Stop-WorkflowAuthValidation 'MissingDeploymentOutput' }
        }
        $origin=([string]$outputs.function_app_url).TrimEnd('/')
        Assert-WorkflowAuthUri $origin $origin '/'
        $settings=Get-DeploymentDurableSettings -Variables $outputs -FromOutputs
        $summary.configuredDurableBackend=$settings.durable_backend
        $summary.configuredTaskHub=$settings.durable_task_hub_name
        $script:DeploymentSubscriptionId=$outputs.subscription_id
        if (-not (Get-Variable AzPath -Scope Script -ErrorAction SilentlyContinue)) { $script:AzPath=Resolve-DeploymentTool -Name az -RepositoryRoot (Split-Path $PSScriptRoot -Parent) }
        $keys=Get-WorkflowAuthKeys $outputs
        $token=Get-WorkflowAuthToken $Resource $ManagedIdentityClientId
        # Obtaining an MI token is not proof of API app-role/idtyp authorization.
        $id='auth-'+[Guid]::NewGuid().ToString('N')
        $sha=[Security.Cryptography.SHA256]::Create()
        try { $jobId='poc-'+([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($id))).Replace('-','').ToLowerInvariant()).Substring(0,40) }
        finally { $sha.Dispose() }
        $canonical=[ordered]@{Version=1;Request=[ordered]@{RequestId=$id;Classification='light';TaskCount=1;FailTaskIndex=$null;LightDelaySeconds=1;HeavyDelaySeconds=1};Classification='light'}|ConvertTo-Json -Depth 4 -Compress
        $sha=[Security.Cryptography.SHA256]::Create()
        try { $fingerprint=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).Replace('-','').ToLowerInvariant() }
        finally { $sha.Dispose() }
        $path='/api/workflows/'+$id; $start=$origin+'/api/workflows'
        $body=@{requestId=$id;classification='light';taskCount=1;lightDelaySeconds=1;heavyDelaySeconds=1}|ConvertTo-Json -Compress
        $headers=@{Authorization='Bearer '+$token;'X-Poc-Owner-Id'=$OwnerId;'X-Poc-Org-Id'=$OrgId;'x-functions-key'=$keys.StartWorkflow}
        # No-token probes: the forged header must never establish authentication.
        # EasyAuth may strip/replace it; this probe tests bypass resistance, not raw-header delivery.
        foreach ($probe in @('anonymous','keyOnly','forgedPrincipal','bearerWithoutKey')) {
          foreach ($api in @('StartWorkflow','GetWorkflowStatus','GetWorkflowResult')) {
            $probeHeaders=@{}
            if ($probe -in @('keyOnly','forgedPrincipal')) { $probeHeaders['x-functions-key']=$keys[$api] }
            if ($probe -eq 'bearerWithoutKey') { $probeHeaders=$headers.Clone();$probeHeaders.Remove('x-functions-key') }
            if ($probe -eq 'forgedPrincipal') { $probeHeaders['X-MS-CLIENT-PRINCIPAL']='eyJjbGFpbXMiOltdfQ==' }
            $uri=$origin+$path; $method='Get'; $requestBody=$null
            if ($api -eq 'StartWorkflow') { $uri=$start;$method='Post';$requestBody=$body }
            if ($api -eq 'GetWorkflowResult') { $uri+='/result' }
            $r=Invoke-WorkflowAuthHttp $uri $method $probeHeaders $requestBody
            if ($r.Code -notin @(401,403)) { Stop-WorkflowAuthValidation 'UnauthenticatedRequestAccepted' }
            $summary.checks+=@{name=$probe+$api;code=$r.Code}
          }
        }
        foreach ($missing in @('X-Poc-Owner-Id','X-Poc-Org-Id')) {
          foreach ($api in @('StartWorkflow','GetWorkflowStatus','GetWorkflowResult')) {
            $probeHeaders=$headers.Clone(); $probeHeaders.Remove($missing)
            $probeHeaders['x-functions-key']=$keys[$api]
            $uri=$origin+$path; $method='Get'; $requestBody=$null
            if ($api -eq 'StartWorkflow') { $uri=$start;$method='Post';$requestBody=$body }
            if ($api -eq 'GetWorkflowResult') { $uri+='/result' }
            $r=Invoke-WorkflowAuthHttp $uri $method $probeHeaders $requestBody
            if ($r.Code -ne 403) { Stop-WorkflowAuthValidation 'MissingOwnerNotRejected' }
          }
        }
        $r=Invoke-WorkflowAuthHttp $start Post $headers $body
        if ($r.Code -ne 202 -or $r.Data.instanceId -cne $jobId) { Stop-WorkflowAuthValidation 'StartRejected' }
        Assert-WorkflowAuthUri $r.Data.statusQueryGetUri $origin $path
        Assert-WorkflowAuthUri $r.Location $origin $path
        $r=Invoke-WorkflowAuthHttp $start Post $headers $body
        if ($r.Code -ne 202) { Stop-WorkflowAuthValidation 'ReplayRejected' }
        $different=@{requestId=$id;classification='light';taskCount=2;lightDelaySeconds=1;heavyDelaySeconds=1}|ConvertTo-Json -Compress
        $r=Invoke-WorkflowAuthHttp $start Post $headers $different
        if ($r.Code -ne 409) { Stop-WorkflowAuthValidation 'InputConflictNotRejected' }
        foreach ($change in @('X-Poc-Owner-Id','X-Poc-Org-Id')) {
            foreach ($api in @('StartWorkflow','GetWorkflowStatus','GetWorkflowResult')) {
                $probeHeaders=$headers.Clone(); $probeHeaders[$change]='other-'+[Guid]::NewGuid().ToString('N'); $probeHeaders['x-functions-key']=$keys[$api]
                $uri=$origin+$path; $method='Get'; $requestBody=$null
                if ($api -eq 'StartWorkflow') { $uri=$start;$method='Post';$requestBody=$body }
                if ($api -eq 'GetWorkflowResult') { $uri+='/result' }
                $r=Invoke-WorkflowAuthHttp $uri $method $probeHeaders $requestBody
                if ($r.Code -ne 403) { Stop-WorkflowAuthValidation 'OtherOwnerNotRejected' }
            }
        }
        $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        $headers['x-functions-key']=$keys.GetWorkflowStatus
        do {
            $r=Invoke-WorkflowAuthHttp ($origin+$path) Get $headers $null
            if ($r.Code -eq 200) {
                if ($r.Data.requestId -cne $id -or $r.Data.instanceId -cne $jobId -or $r.Data.jobId -cne $jobId) { Stop-WorkflowAuthValidation 'StatusIdentityMismatch' }
                if ($r.Data.status -in @('Failed','TimedOut','Cancelled')) { Stop-WorkflowAuthValidation 'WorkflowFailed' }
                if ($r.Data.status -ceq 'Succeeded' -and $r.Data.resultAvailable -eq $true) { break }
                if ($r.Data.status -cnotin @('Accepted','Preparing','Running','Publishing','Succeeded')) { Stop-WorkflowAuthValidation 'InvalidStatus' }
            } elseif ($r.Code -notin @(404,503)) { Stop-WorkflowAuthValidation 'StatusRejected' }
            if ([DateTime]::UtcNow -ge $deadline) { Stop-WorkflowAuthValidation 'WorkflowTimeout' }
            Start-Sleep -Seconds $PollSeconds
        } while ($true)
        $headers['x-functions-key']=$keys.GetWorkflowResult
        $r=Invoke-WorkflowAuthHttp ($origin+$path+'/result') Get $headers $null
        if ($r.Code -ne 200) { Stop-WorkflowAuthValidation ("ResultStatus$($r.Code)") }
        if ($r.Data.requestId -cne $id) { Stop-WorkflowAuthValidation 'ResultRequestIdMismatch' }
        if ($r.Data.status -cne 'Succeeded') { Stop-WorkflowAuthValidation 'ResultStateMismatch' }
        if ($r.Data.taskCount -ne 1 -or $r.Data.sum -ne 1) { Stop-WorkflowAuthValidation 'ResultSumMismatch' }
        if ((@($r.Data.taskIds) -join ',') -cne 'task-00') { Stop-WorkflowAuthValidation 'ResultTaskIdsMismatch' }
        if ($r.Data.jobId -cne $jobId) { Stop-WorkflowAuthValidation 'ResultJobIdMismatch' }
        if ($r.Data.fingerprint -cne $fingerprint) { Stop-WorkflowAuthValidation 'ResultFingerprintMismatch' }
        $summary.checks+=@{name='ownerReplayConflictStatusResult';code=200}
        $summary.status='Pass'; $summary.reason='CustomApiChecksPassed'; $summary.exitCode=0
    } catch { if ($_.Exception.Data['WorkflowAuthCode']) { $summary.reason=[string]$_.Exception.Data['WorkflowAuthCode'] } }
    finally {
        $token=$null; $keys=$null; $headers=$null
        $summary.completedUtc=[DateTime]::UtcNow.ToString('o')
        $null=New-Item -ItemType Directory -Path $EvidenceDirectory -Force
        Write-DeploymentJson -Path (Join-Path $EvidenceDirectory 'summary.json') -Value $summary
    }
    return [pscustomobject]$summary
}

if ($MyInvocation.InvocationName -ne '.') {
    $result=Invoke-WorkflowAuthValidation @PSBoundParameters
    Write-Host ('Workflow auth validation: '+$result.status+' ('+$result.reason+')')
    exit $result.exitCode
}
