function Stop-PocRecoveryContext([string]$Code) {
    $exception=[InvalidOperationException]::new($Code)
    $exception.Data['PocValidationCode']=$Code
    throw $exception
}
function Get-PocRecoveryIdentity([string]$RequestId) {
    $hash=[Security.Cryptography.SHA256]::Create()
    try { return 'poc-'+([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($RequestId))).Replace('-','').ToLowerInvariant()).Substring(0,40) }
    finally { $hash.Dispose() }
}
function Get-PocRecoveryTimestamp($Value) {
    if ($Value -is [DateTime]) { return $Value.ToUniversalTime().ToString('o') }
    if ($Value -is [DateTimeOffset]) { return $Value.UtcDateTime.ToString('o') }
    $timestamp=[DateTimeOffset]::MinValue
    if ($Value -isnot [string] -or $Value.Length -gt 40 -or -not [DateTimeOffset]::TryParse($Value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal,[ref]$timestamp)) { Stop-PocRecoveryContext 'InvalidRecoveryTimestamp' }
    return $timestamp.UtcDateTime.ToString('o')
}
function Assert-PocRecoveryContext {
    param([ValidateSet('Start','Complete')][string]$Phase,[hashtable]$Context)
    if ($Context -and $Context.ContainsKey('mode') -and $Context['mode'] -ceq 'HostStoppedBatchWallClock') {
        $fields=@(if ($Phase -eq 'Start') { 'mode' } else { 'mode','request','instanceId','createdTime','jobCreationTime','batchMaxWallClockSeconds','taskCreationTime','runningObservedUtc','taskStartedUtc' })
        if ($Context.Count -ne $fields.Count -or @($Context.Keys|Where-Object { $_ -cnotin $fields }).Count) { Stop-PocRecoveryContext 'InvalidRecoveryContext' }
        if ($Phase -eq 'Start') { return @{ mode='HostStoppedBatchWallClock' } }
        $wallClock=$Context.batchMaxWallClockSeconds
        if (($wallClock -isnot [int] -and $wallClock -isnot [long]) -or $wallClock -lt 60 -or $wallClock -gt 3600) { Stop-PocRecoveryContext 'InvalidRecoveryContext' }
        $request=$Context.request
        $requestFields=@('requestId','classification','taskCount','failTaskIndex','lightDelaySeconds','heavyDelaySeconds')
        if ($request -isnot [hashtable] -or $request.Count -ne 6 -or @($request.Keys|Where-Object { $_ -cnotin $requestFields }).Count -or
            $request.requestId -cnotmatch '^recovery-[0-9a-f]{32}$' -or $request.classification -cne 'light' -or $request.taskCount -ne 1 -or $null -ne $request.failTaskIndex -or $request.heavyDelaySeconds -ne 3 -or
            ($request.lightDelaySeconds -isnot [int] -and $request.lightDelaySeconds -isnot [long]) -or $request.lightDelaySeconds -le $wallClock -or $request.lightDelaySeconds -gt 3600 -or $Context.instanceId -cne (Get-PocRecoveryIdentity $request.requestId)) { Stop-PocRecoveryContext 'InvalidRecoveryContext' }
        $clean=@{ mode='HostStoppedBatchWallClock'; request=@{requestId=[string]$request.requestId; classification='light'; taskCount=1; failTaskIndex=$null; lightDelaySeconds=[int]$request.lightDelaySeconds; heavyDelaySeconds=3}; instanceId=[string]$Context.instanceId; batchMaxWallClockSeconds=[int]$wallClock }
        foreach ($field in @('createdTime','jobCreationTime','taskCreationTime','runningObservedUtc','taskStartedUtc')) {
            $clean[$field]=Get-PocRecoveryTimestamp $Context[$field]
        }
        return $clean
    }
    $fields=@(if ($Phase -eq 'Start') { 'delaySeconds' } else { 'request','instanceId','createdTime','taskCreationTime','runningObservedUtc','taskStartedUtc' })
    if (-not $Context -or $Context.Count -ne $fields.Count -or @($Context.Keys|Where-Object { $_ -cnotin $fields }).Count) { Stop-PocRecoveryContext 'InvalidRecoveryContext' }
    if ($Phase -eq 'Start') {
        if (($Context.delaySeconds -isnot [int] -and $Context.delaySeconds -isnot [long]) -or $Context.delaySeconds -lt 120 -or $Context.delaySeconds -gt 600) { Stop-PocRecoveryContext 'RecoveryDelayOutOfRange' }
        return @{ delaySeconds=[int]$Context.delaySeconds }
    }
    $request=$Context.request
    $requestFields=@('requestId','classification','taskCount','failTaskIndex','lightDelaySeconds','heavyDelaySeconds')
    if ($request -isnot [hashtable] -or $request.Count -ne 6 -or @($request.Keys|Where-Object { $_ -cnotin $requestFields }).Count -or
        $request.requestId -cnotmatch '^recovery-[0-9a-f]{32}$' -or $request.classification -cne 'light' -or $request.taskCount -ne 1 -or $null -ne $request.failTaskIndex -or $request.heavyDelaySeconds -ne 3 -or $request.lightDelaySeconds -lt 120 -or $request.lightDelaySeconds -gt 600 -or $Context.instanceId -cne (Get-PocRecoveryIdentity $request.requestId)) { Stop-PocRecoveryContext 'InvalidRecoveryContext' }
    $clean=@{ request=@{requestId=[string]$request.requestId; classification='light'; taskCount=1; failTaskIndex=$null; lightDelaySeconds=[int]$request.lightDelaySeconds; heavyDelaySeconds=3}; instanceId=[string]$Context.instanceId }
    foreach ($field in @('createdTime','taskCreationTime','runningObservedUtc','taskStartedUtc')) {
        $clean[$field]=Get-PocRecoveryTimestamp $Context[$field]
    }
    return $clean
}

