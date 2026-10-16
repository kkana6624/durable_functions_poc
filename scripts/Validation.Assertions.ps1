# Shared by deployment smoke and the independent validation runner.
function Assert-PocPublishedDocuments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RequestId,
        [Parameter(Mandatory)][ValidateRange(1,3)][int]$TaskCount,
        [Parameter(Mandatory)]$Aggregate,
        [Parameter(Mandatory)]$Manifest,
        [Parameter(Mandatory)][string]$BlobRoot,
        [string]$JobId,
        [string]$Fingerprint
    )
    $expectedIds = @(0..($TaskCount-1) | ForEach-Object { 'task-{0:00}' -f $_ })
    if ($Aggregate.requestId -ne $RequestId -or $Manifest.requestId -ne $RequestId -or
        $Aggregate.sum -ne $TaskCount -or $Manifest.sum -ne $TaskCount -or
        $Aggregate.taskCount -ne $TaskCount -or $Manifest.taskCount -ne $TaskCount -or
        $Manifest.status -ne 'Succeeded' -or [string]::IsNullOrWhiteSpace($Aggregate.jobId) -or
        $Aggregate.jobId -ne $Manifest.jobId -or [string]::IsNullOrWhiteSpace($Aggregate.fingerprint) -or
        $Aggregate.fingerprint -ne $Manifest.fingerprint -or
        @($Aggregate.taskIds).Count -ne $TaskCount -or @($Manifest.taskUris).Count -ne $TaskCount -or
        ($JobId -and $Aggregate.jobId -ne $JobId) -or ($Fingerprint -and $Aggregate.fingerprint -ne $Fingerprint)) {
        throw 'Aggregate/manifest identity, count, sum, fingerprint, or success status is inconsistent.'
    }
    if (($Aggregate.taskIds -join ',') -cne ($expectedIds -join ',')) { throw 'Aggregate task IDs are inconsistent.' }
    $root = $BlobRoot.TrimEnd('/') + "/runs/poc-$RequestId"
    $aggregateUri = [Uri]$Manifest.aggregateUri
    if ($aggregateUri.Scheme -ne 'https' -or $aggregateUri.AbsoluteUri -cne "$root/final/aggregate.json") {
        throw 'Manifest aggregate URI is inconsistent.'
    }
    for ($index=0; $index -lt $TaskCount; $index++) {
        $taskUri = [Uri]$Manifest.taskUris[$index]
        if ($taskUri.Scheme -ne 'https' -or $taskUri.AbsoluteUri -cne "$root/tasks/$($expectedIds[$index])/result.json") {
            throw 'Manifest task URI is inconsistent.'
        }
    }
}
