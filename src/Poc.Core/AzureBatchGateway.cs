using Azure;
using Azure.Compute.Batch;

namespace Poc.Core;

public sealed class AzureBatchGateway(BatchClient client, string poolId, BatchStorageOptions? storageOptions = null) : IBatchGateway
{
    private const string FingerprintName = "poc-fingerprint";
    private const string FingerprintEnvironment = "POC_FINGERPRINT";
    private static void ValidatePlan(WorkflowPlan plan)
    {
        plan.Request.Validate();
        if (plan.JobId != WorkflowIdentity.JobId(plan.Request.RequestId))
            throw new ArgumentException("Invalid workflow identity.");
        if (string.IsNullOrWhiteSpace(plan.Fingerprint) || plan.Fingerprint.Length != 64 || !plan.Fingerprint.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException("Invalid workflow identity.");
        if (string.IsNullOrEmpty(plan.Request.ReadyManifestId) && plan.Fingerprint != WorkflowIdentity.Fingerprint(plan.Request, plan.Classification))
            throw new ArgumentException("Invalid workflow identity.");
    }
    public async Task EnsureJobAsync(WorkflowPlan plan, CancellationToken cancellationToken = default)
    {
        ValidatePlan(plan);
        if (string.IsNullOrWhiteSpace(poolId)) throw new ArgumentException("Existing pool ID is required.");
        var create = new BatchJobCreateOptions(plan.JobId, new BatchPoolInfo { PoolId = poolId })
        {
            // Server-side safety net also applies while the Functions host is unavailable.
            Constraints = new BatchJobConstraints { MaxWallClockTime = TimeSpan.FromMinutes(plan.TimeoutMinutes) }
        };
        create.Metadata.Add(new BatchMetadataItem(FingerprintName, plan.Fingerprint));
        try { await client.CreateJobAsync(create, cancellationToken: cancellationToken); }
        catch (RequestFailedException ex) when (ex.Status == 409 && ex.ErrorCode == "JobExists") { }
        var job = (await client.GetJobAsync(plan.JobId, cancellationToken: cancellationToken)).Value;
        if (job.PoolInfo.PoolId != poolId || job.Metadata.SingleOrDefault(m => m.Name == FingerprintName)?.Value != plan.Fingerprint)
            throw new WorkflowConflictException($"Batch job {plan.JobId} belongs to different input or pool.");
        if (job.State != BatchJobState.Active) throw new WorkflowConflictException($"Batch job {plan.JobId} is not active ({job.State}).");
    }
    private static void ValidateSpec(TaskSpec spec)
    {
        ValidatePlan(spec.Plan);
        if (spec.Index < 0 || spec.Index >= spec.Plan.Request.TaskCount) throw new ArgumentOutOfRangeException(nameof(spec));
    }
    public async Task EnsureTaskAsync(TaskSpec spec, CancellationToken cancellationToken = default)
    {
        ValidateSpec(spec);
        if (spec.DeadlineUtc is { } deadline && DateTime.UtcNow >= deadline)
            throw new TimeoutException("Task submission deadline exceeded.");
        // A terminated job cannot accept new tasks. This read detects conflicts before submission;
        // Batch enforces the final state check atomically if termination races CreateTask.
        var job = (await client.GetJobAsync(spec.Plan.JobId, cancellationToken: cancellationToken)).Value;
        ValidateJob(spec.Plan, job);
        if (job.State != BatchJobState.Active) throw new WorkflowConflictException("Batch job is no longer active.");
        if (spec.DeadlineUtc is { } submissionDeadline && DateTime.UtcNow >= submissionDeadline)
            throw new TimeoutException("Task submission deadline exceeded while checking the job.");

        ResourceFile? resourceFile = null;
        OutputFile? outputFile = null;
        string? inputRelativePath = null;
        string? outputFileName = null;

        var manifest = spec.Plan.Manifest;
        if (manifest is not null && storageOptions?.BlobEndpoint is not null)
        {
            inputRelativePath = "inputs/input.dat";
            outputFileName = "output.json";

            var endpointStr = storageOptions.BlobEndpoint.AbsoluteUri.TrimEnd('/');
            var inputUriBuilder = new UriBuilder($"{endpointStr}/{manifest.Blob.Container}/{manifest.Blob.BlobKey}");
            if (!string.IsNullOrEmpty(manifest.Blob.VersionId))
            {
                inputUriBuilder.Query = $"versionId={Uri.EscapeDataString(manifest.Blob.VersionId)}";
            }

            var identityRef = !string.IsNullOrWhiteSpace(storageOptions.PoolUamiResourceId)
                ? new BatchNodeIdentityReference { ResourceId = new Azure.Core.ResourceIdentifier(storageOptions.PoolUamiResourceId) }
                : null;

            resourceFile = new ResourceFile
            {
                HttpUri = inputUriBuilder.Uri,
                FilePath = inputRelativePath,
                IdentityReference = identityRef
            };

            var outputContainer = storageOptions.OutputContainer ?? manifest.Blob.Container;
            var outputContainerUri = new Uri($"{endpointStr}/{outputContainer}");
            outputFile = new OutputFile(
                filePattern: outputFileName,
                destination: new OutputFileDestination
                {
                    Container = new OutputFileBlobContainerDestination(outputContainerUri)
                    {
                        IdentityReference = identityRef,
                        Path = spec.OutputBlobPath
                    }
                },
                uploadOptions: new OutputFileUploadConfig(OutputFileUploadCondition.TaskSuccess)
            );
        }

        var command = WindowsTaskCommand.Build(spec.DelaySeconds, spec.InjectFailure, inputRelativePath, outputFileName);
        var create = new BatchTaskCreateOptions(spec.TaskId, command) { Constraints = new BatchTaskConstraints { MaxTaskRetryCount = 0 } };
        create.EnvironmentSettings.Add(new EnvironmentSetting(FingerprintEnvironment) { Value = spec.Plan.Fingerprint });

        if (resourceFile is not null)
        {
            create.ResourceFiles.Add(resourceFile);
        }
        if (outputFile is not null)
        {
            create.OutputFiles.Add(outputFile);
        }

        try { await client.CreateTaskAsync(spec.Plan.JobId, create, cancellationToken: cancellationToken); }
        catch (RequestFailedException ex) when (ex.Status == 409 && ex.ErrorCode == "TaskExists") { }
        var task = (await client.GetTaskAsync(spec.Plan.JobId, spec.TaskId, cancellationToken: cancellationToken)).Value;
        if (task.CommandLine != command || task.Constraints?.MaxTaskRetryCount != 0 ||
            task.EnvironmentSettings.SingleOrDefault(e => e.Name == FingerprintEnvironment)?.Value != spec.Plan.Fingerprint)
            throw new WorkflowConflictException($"Batch task {spec.TaskId} belongs to different input.");
    }
    private void ValidateJob(WorkflowPlan plan, BatchJob job)
    {
        if (job.PoolInfo?.PoolId != poolId || job.Metadata.SingleOrDefault(m => m.Name == FingerprintName)?.Value != plan.Fingerprint)
            throw new WorkflowConflictException($"Batch job {plan.JobId} belongs to different input or pool.");
    }
    public async Task<BatchJobTerminationStatus> TerminateJobAsync(WorkflowPlan plan, CancellationToken cancellationToken = default)
    {
        ValidatePlan(plan);
        try
        {
            var job = (await client.GetJobAsync(plan.JobId, cancellationToken: cancellationToken)).Value;
            ValidateJob(plan, job);
            if (job.State == BatchJobState.Completed) return new(job.State.ToString(), true);
            if (job.State != BatchJobState.Terminating)
            {
                try
                {
                    await client.TerminateJobAsync(WaitUntil.Started, plan.JobId,
                        new BatchJobTerminateOptions { TerminationReason = "PoC workflow monitoring deadline exceeded." },
                        requestConditions: new RequestConditions { IfMatch = job.ETag },
                        cancellationToken: cancellationToken);
                }
                catch (RequestFailedException ex) when (ex.Status == 409 && ex.ErrorCode is "JobCompleted" or "JobBeingTerminated") { }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    // A natural state transition also changes the ETag. Never bypass the condition for a
                    // changed active job; report "not completed" so the orchestrator polling loop retries.
                    var changed = (await client.GetJobAsync(plan.JobId, cancellationToken: cancellationToken)).Value;
                    ValidateJob(plan, changed);
                    if (changed.State != BatchJobState.Completed && changed.State != BatchJobState.Terminating)
                        return new(changed.State.ToString(), false);
                    return new(changed.State.ToString(), changed.State == BatchJobState.Completed);
                }
            }
            job = (await client.GetJobAsync(plan.JobId, cancellationToken: cancellationToken)).Value;
            ValidateJob(plan, job);
            return new(job.State.ToString(), job.State == BatchJobState.Completed);
        }
        // The job no longer exists, so there is nothing left to stop.
        catch (RequestFailedException ex) when (ex.Status == 404) { return new("NotFound", true); }
    }
    public async Task<BatchTaskStatus> GetStatusAsync(TaskSpec spec, CancellationToken cancellationToken = default)
    {
        ValidateSpec(spec);
        var task = (await client.GetTaskAsync(spec.Plan.JobId, spec.TaskId, cancellationToken: cancellationToken)).Value;
        var completed = task.State == BatchTaskState.Completed;
        var execution = task.ExecutionInfo;
        var success = completed && execution?.Result == BatchTaskExecutionResult.Success && execution.ExitCode == 0 && execution.FailureInfo is null;
        var failure = execution?.FailureInfo;
        return new(spec.TaskId, task.State.ToString(), completed, success, execution?.ExitCode,
            success || !completed ? null : failure is null ? $"Result={execution?.Result}; ExitCode={execution?.ExitCode}" : $"{failure.Code}: {failure.Message}");
    }
    public async Task<string> ReadResultAsync(TaskSpec spec, CancellationToken cancellationToken = default)
    {
        ValidateSpec(spec);
        // Discover the file under the task root; no hard-coded assumption about slash direction.
        var matches = new List<string>();
        await foreach (var file in client.GetTaskFilesAsync(spec.Plan.JobId, spec.TaskId, recursive: true, cancellationToken: cancellationToken))
        {
            var normalized = file.Name.Replace('\\', '/');
            if (file.IsDirectory == false && normalized == "wd/result.txt") matches.Add(file.Name);
        }
        if (matches.Count != 1) throw new FileNotFoundException($"Expected wd/result.txt for {spec.TaskId}, found {matches.Count}.");
        Response<BinaryData> response;
        try { response = await client.GetTaskFileAsync(spec.Plan.JobId, spec.TaskId, matches[0], ocpRange: "bytes=0-4096", cancellationToken: cancellationToken); }
        catch (RequestFailedException ex) when (ex.Status == 416) { throw new InvalidDataException("Batch task output was empty.", ex); }
        var text = response.Value.ToString();
        if (response.Value.ToMemory().Length > 4096) throw new InvalidDataException("Batch result exceeds PoC size limit.");
        return text;
    }
}
