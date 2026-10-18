using System.Globalization;

namespace Poc.Core;

public sealed class WorkflowOperations(IBatchGateway batch, IResultRepository results, IWorkloadClassifier classifier, WorkflowSettings settings)
{
    public async Task<WorkflowPlan> ClassifyAsync(WorkflowRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        if (settings.PollSeconds is < 1 or > 300 || settings.TimeoutMinutes is < 1 or > 1440)
            throw new ArgumentException("Workflow polling/timeout settings are invalid.");
        var classification = await classifier.ClassifyAsync(request, cancellationToken);
        if (classification.Classification is not ("light" or "heavy")) throw new InvalidOperationException("Classifier must return light or heavy.");
        return new(request, classification.Classification, classification.Reason, WorkflowIdentity.JobId(request.RequestId),
            WorkflowIdentity.Fingerprint(request, classification.Classification), settings.PollSeconds, settings.TimeoutMinutes);
    }
    public Task<BatchJobTerminationStatus> TerminateJobAsync(WorkflowPlan plan, CancellationToken cancellationToken = default) => batch.TerminateJobAsync(plan, cancellationToken);
    public Task EnsureJobAsync(WorkflowPlan plan, CancellationToken cancellationToken = default) => batch.EnsureJobAsync(plan, cancellationToken);
    public Task EnsureTaskAsync(TaskSpec spec, CancellationToken cancellationToken = default) => batch.EnsureTaskAsync(spec, cancellationToken);
    public Task<BatchTaskStatus> GetStatusAsync(TaskSpec spec, CancellationToken cancellationToken = default) => batch.GetStatusAsync(spec, cancellationToken);
    public async Task<ResultReference[]> FetchAndPersistAsync(WorkflowPlan plan, CancellationToken cancellationToken = default)
    {
        var references = new List<ResultReference>();
        for (int i = 0; i < plan.Request.TaskCount; i++)
        {
            var spec = new TaskSpec(plan, i);
            var status = await batch.GetStatusAsync(spec, cancellationToken);
            if (!status.Completed || !status.Succeeded) throw new InvalidOperationException($"Task {spec.TaskId} is not successful: {status.Failure}");
            var text = await batch.ReadResultAsync(spec, cancellationToken);
            if (text.Length > 4096 || !int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value != 1)
                throw new InvalidDataException($"Task {spec.TaskId}: expected a small result containing integer 1.");
            var uri = await results.PutIfAbsentAsync(WorkflowIdentity.TaskPath(spec), new TaskResult(plan.Request.RequestId,
                plan.JobId, spec.TaskId, plan.Classification, plan.Reason, plan.Fingerprint, value), cancellationToken);
            references.Add(new(plan.Request.RequestId, spec.TaskId, uri));
        }
        return references.ToArray();
    }
    private async Task<TaskResult[]> ReadTasksAsync(WorkflowPlan plan, CancellationToken cancellationToken)
    {
        var tasks = new List<TaskResult>();
        for (int i = 0; i < plan.Request.TaskCount; i++)
        {
            var spec = new TaskSpec(plan, i);
            var task = await results.ReadAsync<TaskResult>(WorkflowIdentity.TaskPath(spec), cancellationToken);
            if (task.RequestId != plan.Request.RequestId || task.JobId != plan.JobId || task.TaskId != spec.TaskId ||
                task.Fingerprint != plan.Fingerprint || task.Classification != plan.Classification || task.Value != 1)
                throw new InvalidDataException($"Persisted result identity/value mismatch: {spec.TaskId}.");
            tasks.Add(task);
        }
        return tasks.ToArray();
    }
    public async Task<AggregateReference> AggregateAsync(WorkflowPlan plan, CancellationToken cancellationToken = default)
    {
        var tasks = await ReadTasksAsync(plan, cancellationToken);
        var aggregate = new AggregateResult(plan.Request.RequestId, plan.JobId, plan.Fingerprint, tasks.Length, tasks.Sum(t => t.Value), tasks.Select(t => t.TaskId).ToArray());
        var uri = await results.PutIfAbsentAsync(WorkflowIdentity.AggregatePath(plan), aggregate, cancellationToken);
        return new(plan.Request.RequestId, uri, aggregate.Sum, aggregate.TaskCount);
    }
    public async Task<ManifestReference> PublishManifestAsync(WorkflowPlan plan, CancellationToken cancellationToken = default)
    {
        var tasks = await ReadTasksAsync(plan, cancellationToken);
        var aggregate = await results.ReadAsync<AggregateResult>(WorkflowIdentity.AggregatePath(plan), cancellationToken);
        if (aggregate.TaskIds is null) throw new InvalidDataException("Aggregate is missing task IDs.");
        if (aggregate.RequestId != plan.Request.RequestId || aggregate.JobId != plan.JobId || aggregate.Fingerprint != plan.Fingerprint ||
            aggregate.TaskCount != tasks.Length || aggregate.Sum != tasks.Sum(t => t.Value) || !aggregate.TaskIds.SequenceEqual(tasks.Select(t => t.TaskId)))
            throw new InvalidDataException("Aggregate does not match the complete set of task results.");
        var aggregateUri = results.GetUri(WorkflowIdentity.AggregatePath(plan));
        var taskUris = Enumerable.Range(0, tasks.Length).Select(i => results.GetUri(WorkflowIdentity.TaskPath(new(plan, i)))).ToArray();
        var uri = await results.PutIfAbsentAsync(WorkflowIdentity.ManifestPath(plan), new ManifestResult(plan.Request.RequestId, plan.JobId,
            plan.Fingerprint, "Succeeded", tasks.Length, aggregate.Sum, aggregateUri, taskUris), cancellationToken);
        return new(plan.Request.RequestId, uri, aggregateUri, tasks.Length, aggregate.Sum);
    }
    public async Task<WorkflowResultResponse?> TryGetPublishedResultAsync(string requestId, CancellationToken cancellationToken = default)
    {
        ManifestResult manifest;
        AggregateResult aggregate;
        try
        {
            manifest = await results.ReadAsync<ManifestResult>(WorkflowIdentity.ManifestPath(requestId), cancellationToken);
            aggregate = await results.ReadAsync<AggregateResult>(WorkflowIdentity.AggregatePath(requestId), cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        if (manifest.Status != "Succeeded" ||
            manifest.RequestId != requestId ||
            aggregate.RequestId != requestId ||
            manifest.JobId != aggregate.JobId ||
            manifest.Fingerprint != aggregate.Fingerprint ||
            manifest.Sum != aggregate.Sum ||
            manifest.TaskCount != aggregate.TaskCount ||
            aggregate.TaskIds is null)
        {
            throw new InvalidDataException("Published manifest and aggregate do not match or are invalid.");
        }

        return new WorkflowResultResponse(requestId, manifest.JobId, manifest.Fingerprint, "Succeeded", aggregate.TaskCount, aggregate.Sum, aggregate.TaskIds);
    }
}
