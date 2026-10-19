$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../Invoke-WorkflowAuthValidation.ps1')
$realGetKeys=${function:Get-WorkflowAuthKeys}
$realInvokeHttp=${function:Invoke-WorkflowAuthHttp}
function Assert-True([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
$directory=Join-Path ([IO.Path]::GetTempPath()) ('workflow-auth-tests-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $directory
$script:AzPath='fake'
$script:secret='NEVER-SERIALIZE-BEARER-KEY-PRINCIPAL'
$script:mode='pass'; $script:calls=@(); $script:accepted=$false
$deployment=Join-Path $directory 'deployment.json'
Write-DeploymentJson $deployment @{source_provenance=@{gitRevision=('A'*40);gitDirty=$false;sourceSha256=('B'*64);untrusted='NEVER-SERIALIZE-BEARER-KEY-PRINCIPAL'};package_sha256=('C'*64);assembly_sha256=('D'*64);package_content_sha256=('C'*64);outputs=@{subscription_id='sub';resource_group_name='rg';function_app_name='app';function_app_url='https://app.example';durable_backend='azureStorage';task_hub_name='PocBatchHub';workflow_auth_allowed_backend_app_ids=@('11111111-2222-3333-4444-555555555555')}}
function Get-WorkflowAuthToken {
    param($Resource,$ClientId)
    if ($Resource -eq 'https://management.azure.com/') { return 'unauthorized-audience-token' }
    if ($ClientId -eq '11111111-2222-3333-4444-555555555555') { return 'missing-role-token' }
    return $script:secret
}
function Get-WorkflowAuthKeys { param($Outputs) return @{StartWorkflow='start-key';GetWorkflowStatus='status-key';GetWorkflowResult='result-key'} }
function Invoke-WorkflowAuthHttp {
    param($Uri,$Method,$Headers,$Body)
    $script:calls+=@{uri=$Uri;method=$Method;key=$Headers['x-functions-key']}
    if (-not $Headers.ContainsKey('x-functions-key')) { return @{Code=401;Data=$null;Location=$null} }
    if (-not $Headers.ContainsKey('Authorization')) {
        if ($script:mode -eq 'bypass') { return @{Code=202;Data=$null;Location=$null} }
        return @{Code=401;Data=$null;Location=$null}
    }
    if ($Headers['Authorization'] -eq 'Bearer unauthorized-audience-token') {
        return @{Code=401;Data=$null;Location=$null}
    }
    if ($Headers['Authorization'] -eq 'Bearer missing-role-token') {
        return @{Code=403;Data=$null;Location=$null}
    }
    if (-not $Headers.ContainsKey('X-Poc-Owner-Id') -or -not $Headers.ContainsKey('X-Poc-Org-Id') -or $Headers['X-Poc-Owner-Id'] -ne 'owner' -or $Headers['X-Poc-Org-Id'] -ne 'org') {
        return @{Code=403;Data=$null;Location=$null}
    }
    $script:httpCallIndex++
    $pkgHeader = if ($script:mode -eq 'verifiedPackage') { ('c'*64) }
                 elseif ($script:mode -eq 'inconsistentPackage') {
                     if ($script:httpCallIndex -eq 1) { ('c'*64) }
                     elseif ($script:httpCallIndex -eq 2) { ('d'*64) }
                     else { $null }
                 } else { $null }
    $asmHeader = if ($script:mode -in @('verifiedPackage','inconsistentPackage')) { ('d'*64) } else { $null }
    if ($Method -eq 'Post') {
        $request=$Body|ConvertFrom-Json
        if ($request.taskCount -eq 2) { return @{Code=409;Data=$null;Location=$null} }
        $script:requestId=$request.requestId
        $sha=[Security.Cryptography.SHA256]::Create()
        try { $script:jobId='poc-'+([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($script:requestId))).Replace('-','').ToLowerInvariant()).Substring(0,40) } finally {$sha.Dispose()}
        $canonical=[ordered]@{Version=1;Request=[ordered]@{RequestId=$script:requestId;Classification='light';TaskCount=1;FailTaskIndex=$null;LightDelaySeconds=1;HeavyDelaySeconds=1};Classification='light'}|ConvertTo-Json -Depth 4 -Compress
        $sha=[Security.Cryptography.SHA256]::Create()
        try {$script:fingerprint=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()}
        $location='https://app.example/api/workflows/'+$script:requestId
        if ($script:mode -eq 'unsafeUrl') {$location+='?sig='+$script:secret}
        return @{Code=202;Data=@{instanceId=$script:jobId;statusQueryGetUri=$location;untrusted=$script:secret};Location=$location;PackageSha256=$pkgHeader;AssemblySha256=$asmHeader}
    }
    if ($Uri.EndsWith('/result')) {
        Assert-True ($Headers['x-functions-key'] -eq 'result-key') 'Result must use its own Function key'
        $sum=1; if ($script:mode -eq 'wrongResult') {$sum=2}
        return @{Code=200;Data=@{requestId=$script:requestId;jobId=$script:jobId;fingerprint=$script:fingerprint;status='Succeeded';taskCount=1;sum=$sum;taskIds=@('task-00');untrusted=$script:secret};Location=$null;PackageSha256=$pkgHeader;AssemblySha256=$asmHeader}
    }
    Assert-True ($Headers['x-functions-key'] -eq 'status-key') 'Status must use its own Function key'
    return @{Code=200;Data=@{requestId=$script:requestId;instanceId=$script:jobId;jobId=$script:jobId;status='Succeeded';resultAvailable=$true;untrusted=$script:secret};Location=$null;PackageSha256=$pkgHeader;AssemblySha256=$asmHeader}
}
try {
    $script:keyNames=@()
    function Invoke-AzJson {
        param([string[]]$Arguments,[switch]$Sensitive)
        Assert-True ([bool]$Sensitive) 'Key retrieval must suppress CLI output'
        Assert-True (($Arguments[0..3] -join ' ') -eq 'functionapp function keys list') 'Use individual Function keys only'
        $index=[Array]::IndexOf($Arguments,'--function-name')
        Assert-True ($index -ge 0) 'Missing Function key scope'
        $script:keyNames+=$Arguments[$index+1]
        return [pscustomobject]@{default='secret-key'}
    }
    $retrieved=& $realGetKeys @{function_app_name='app';resource_group_name='rg'}
    Assert-True (($script:keyNames -join ',') -eq 'StartWorkflow,GetWorkflowStatus,GetWorkflowResult' -and $retrieved.Count -eq 3) 'Retrieve three individually scoped keys'
    foreach ($mode in @('pass','bypass','unsafeUrl','wrongResult','verifiedPackage','inconsistentPackage')) {
        $script:mode=$mode; $script:calls=@(); $script:httpCallIndex=0
        $evidence=Join-Path $directory $mode
        $result=Invoke-WorkflowAuthValidation -DeploymentResultPath $deployment -Resource 'api://api-app' -OwnerId owner -OrgId org -EvidenceDirectory $evidence -PollSeconds 1 -TimeoutSeconds 1
        Assert-True (($result.exitCode -eq 0) -eq ($mode -in @('pass','verifiedPackage','inconsistentPackage'))) ('Unexpected result: '+$mode+' '+$result.reason)
        if ($mode -eq 'verifiedPackage') {
            Assert-True ($result.provenance.livePackageVerified -eq $true -and $result.provenance.livePackageSha256 -ceq ('c'*64) -and $result.provenance.liveAssemblySha256 -ceq ('d'*64)) 'Live package must be verified'
        } elseif ($mode -eq 'inconsistentPackage') {
            Assert-True ($result.provenance.livePackageVerified -eq $false) 'Inconsistent package sequence must remain unverified (livePackageVerified=false)'
        } else {
            Assert-True ($result.provenance.gitRevision -ceq ('a'*40) -and $result.provenance.sourceSha256 -ceq ('b'*64) -and $result.provenance.packageSha256 -ceq ('c'*64) -and $result.provenance.livePackageVerified -eq $false) 'Descriptor provenance must be selected, normalized to lowercase, and remain unverified'
        }
        $text=Get-Content (Join-Path $evidence 'summary.json') -Raw
        Assert-True ($text -notmatch $script:secret -and $text -notmatch 'app.example|start-key|status-key|result-key|sig=') 'Evidence leaked untrusted data'
        Assert-True (@($script:calls|Where-Object {$_.uri -match '/runtime/'}).Count -eq 0) 'Runner must use only custom APIs'
    }
    $script:calls=@(); $script:httpCallIndex=0
    $script:mode='pass'
    $unauthResult=Invoke-WorkflowAuthValidation -DeploymentResultPath $deployment -Resource 'api://api-app' -UnauthorizedManagedIdentityClientId '11111111-2222-3333-4444-555555555555' -OwnerId owner -OrgId org -EvidenceDirectory (Join-Path $directory 'unauth-probe') -PollSeconds 1 -TimeoutSeconds 1
    Assert-True ($unauthResult.exitCode -eq 0 -and @($unauthResult.checks | Where-Object { $_.name -like 'missingAppRole*' }).Count -eq 3) 'MissingAppRole probes must be executed and recorded'
    Assert-True ($unauthResult.provenance.unauthorizedClientConfirmedInAllowedList -eq $true) 'Allowed list premise must be confirmed'

    # Verify that an unauthorized client not in allowed_backend_app_ids fails with UnauthorizedClientNotInAllowedBackendList
    $script:calls=@(); $script:httpCallIndex=0
    $unlistedResult=Invoke-WorkflowAuthValidation -DeploymentResultPath $deployment -Resource 'api://api-app' -UnauthorizedManagedIdentityClientId '99999999-9999-9999-9999-999999999999' -OwnerId owner -OrgId org -EvidenceDirectory (Join-Path $directory 'unlisted-client') -PollSeconds 1 -TimeoutSeconds 1
    Assert-True ($unlistedResult.exitCode -eq 1 -and $unlistedResult.reason -eq 'UnauthorizedClientNotInAllowedBackendList') 'Unlisted client ID must be rejected by premise assertion'
    $script:calls=@()
    $invalidDescriptor=ConvertTo-DeploymentHashtable (Get-Content $deployment -Raw|ConvertFrom-Json)
    $invalidDescriptor.source_provenance.gitRevision=@(('a'*40),$script:secret)
    $invalidDescriptor.source_provenance.sourceSha256=@(('b'*64),$script:secret)
    $invalidDescriptor.package_sha256=@(('c'*64),$script:secret)
    $invalidPath=Join-Path $directory 'invalid-provenance.json'
    Write-DeploymentJson $invalidPath $invalidDescriptor
    $script:mode='pass'
    $result=Invoke-WorkflowAuthValidation -DeploymentResultPath $invalidPath -Resource 'api://api-app' -OwnerId owner -OrgId org -EvidenceDirectory (Join-Path $directory 'invalid-provenance') -TimeoutSeconds 1 -PollSeconds 1
    Assert-True ($result.exitCode -eq 0 -and $null -eq $result.provenance.gitRevision -and $null -eq $result.provenance.sourceSha256 -and $null -eq $result.provenance.packageSha256) 'Array provenance must become unknown'
    Assert-True ((Get-Content (Join-Path $directory 'invalid-provenance/summary.json') -Raw) -notmatch $script:secret) 'Array provenance leaked secret'
    $script:calls=@()
    $result=Invoke-WorkflowAuthValidation -DeploymentResultPath $deployment -Resource 'api://api-app' -OwnerId 'invalid:value' -OrgId org -EvidenceDirectory (Join-Path $directory 'invalid')
    Assert-True ($result.reason -eq 'InvalidAuthConfiguration' -and $script:calls.Count -eq 0) 'Invalid config must fail before HTTP'
    foreach ($url in @('http://app.example/api/workflows/x','https://evil.example/api/workflows/x','https://app.example/api/workflows/x?q=secret','https://app.example/api/workflows/x#secret','https://user@app.example/api/workflows/x','https://app.example/runtime/webhooks/durabletask/instances/x')) {
        $failed=$false
        try {Assert-WorkflowAuthUri $url 'https://app.example' '/api/workflows/x'} catch {$failed=$true}
        Assert-True $failed 'Unsafe public URL accepted'
    }

    # Verify real Invoke-WorkflowAuthHttp behavior on malformed JSON containing sensitive content
    Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
public sealed class MalformedJsonLoopbackServer : IDisposable {
    private readonly TcpListener listener;
    public readonly int Port;
    public MalformedJsonLoopbackServer(string payload) {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task.Factory.StartNew(() => {
            try {
                using (var client = listener.AcceptTcpClient())
                using (var stream = client.GetStream()) {
                    var reader = new System.IO.StreamReader(stream, Encoding.UTF8, false, 1024, true);
                    string line;
                    while (!String.IsNullOrEmpty(line = reader.ReadLine())) {}
                    var bytes = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(payload) + "\r\nConnection: close\r\n\r\n" + payload);
                    stream.Write(bytes, 0, bytes.Length);
                }
            } catch {}
        }, TaskCreationOptions.LongRunning);
    }
    public void Dispose() { try { listener.Stop(); } catch {} }
}
'@
    $server = [MalformedJsonLoopbackServer]::new("{ malformed json body: " + $script:secret + " }")
    try {
        $caught = $null
        try {
            & $realInvokeHttp -Uri "http://127.0.0.1:$($server.Port)/api" -Method 'Get' -Headers @{} -Body $null
        } catch {
            $caught = $_.Exception
        }
        Assert-True ($caught -ne $null -and $caught.Data['WorkflowAuthCode'] -eq 'InvalidJsonResponse') 'Malformed JSON must fail immediately with InvalidJsonResponse'
        Assert-True ($null -eq $caught.Data['WorkflowAuthMessage'] -or $caught.Data['WorkflowAuthMessage'] -notmatch $script:secret) 'Malformed JSON must not leak response content in exception message'
    } finally {
        $server.Dispose()
    }

    Write-Host 'WorkflowAuthValidation offline tests passed (Fake HTTP/token/keys; no Azure calls).'
} finally { Remove-Item -LiteralPath $directory -Recurse -Force }
