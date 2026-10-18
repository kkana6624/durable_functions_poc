using System.Text.Json;
using Azure;
using Poc.Core;
using Xunit;

namespace Poc.Tests;

public sealed class WorkflowPublishedResultTests
{
    private const string RequestId = "published-contract";

    [Theory]
    [InlineData(1, "light")]
    [InlineData(3, "heavy")]
    public async Task PublishedArtifactsFromNormalActivitiesCanBeRead(int count, string classification)
    {
        var (operations, _, plan) = await CreatePublishedAsync(count, classification);
        var result = await operations.TryGetPublishedResultAsync(RequestId, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(plan.JobId, result.JobId);
        Assert.Equal(plan.Fingerprint, result.Fingerprint);
        Assert.Equal(count, result.TaskCount);
        Assert.Equal(count, result.Sum);
        Assert.Equal(Enumerable.Range(0, count).Select(index => $"task-{index:00}"), result.TaskIds);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("aggregate")]
    [InlineData("task")]
    public async Task MissingPublishedArtifactIsUnavailable(string artifact)
    {
        var (operations, repository, plan) = await CreatePublishedAsync();
        repository.Documents.Remove(PathFor(artifact, plan));
        Assert.Null(await operations.TryGetPublishedResultAsync(RequestId, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("manifest-status")]
    [InlineData("request-id")]
    [InlineData("job-id")]
    [InlineData("fingerprint-empty")]
    [InlineData("fingerprint-format")]
    [InlineData("fingerprint-mismatch")]
    [InlineData("count-zero")]
    [InlineData("count-four")]
    [InlineData("count-mismatch")]
    [InlineData("sum")]
    [InlineData("task-ids")]
    [InlineData("task-ids-null")]
    [InlineData("aggregate-uri")]
    [InlineData("task-uri")]
    [InlineData("task-uri-count")]
    [InlineData("task-uri-null")]
    [InlineData("task-request-id")]
    [InlineData("task-job-id")]
    [InlineData("task-fingerprint")]
    [InlineData("task-id")]
    [InlineData("task-value")]
    [InlineData("task-classification")]
    [InlineData("task-classification-mismatch")]
    public async Task CorruptedArtifactsAreRejected(string corruption)
    {
        var (operations, repository, plan) = await CreatePublishedAsync();
        var manifestPath = WorkflowIdentity.ManifestPath(plan);
        var aggregatePath = WorkflowIdentity.AggregatePath(plan);
        var taskPath = WorkflowIdentity.TaskPath(new TaskSpec(plan, 0));
        var manifest = await repository.ReadAsync<ManifestResult>(manifestPath, TestContext.Current.CancellationToken);
        var aggregate = await repository.ReadAsync<AggregateResult>(aggregatePath, TestContext.Current.CancellationToken);
        var task = await repository.ReadAsync<TaskResult>(taskPath, TestContext.Current.CancellationToken);
        switch (corruption)
        {
            case "manifest-status": manifest = manifest with { Status = "Failed" }; break;
            case "request-id": manifest = manifest with { RequestId = "other" }; aggregate = aggregate with { RequestId = "other" }; break;
            case "job-id": manifest = manifest with { JobId = "other" }; aggregate = aggregate with { JobId = "other" }; break;
            case "fingerprint-empty": manifest = manifest with { Fingerprint = "" }; aggregate = aggregate with { Fingerprint = "" }; break;
            case "fingerprint-format": manifest = manifest with { Fingerprint = new string('z', 64) }; aggregate = aggregate with { Fingerprint = manifest.Fingerprint }; break;
            case "fingerprint-mismatch": manifest = manifest with { Fingerprint = WorkflowIdentity.Hash("other") }; break;
            case "count-zero": manifest = manifest with { TaskCount = 0 }; aggregate = aggregate with { TaskCount = 0 }; break;
            case "count-four": manifest = manifest with { TaskCount = 4 }; aggregate = aggregate with { TaskCount = 4 }; break;
            case "count-mismatch": manifest = manifest with { TaskCount = 1 }; break;
            case "sum": manifest = manifest with { Sum = 99 }; aggregate = aggregate with { Sum = 99 }; break;
            case "task-ids": aggregate = aggregate with { TaskIds = ["task-00", "task-00", "task-02"] }; break;
            case "task-ids-null": aggregate = aggregate with { TaskIds = null! }; break;
            case "aggregate-uri": manifest = manifest with { AggregateUri = manifest.AggregateUri + "?sig=DO-NOT-EXPOSE" }; break;
            case "task-uri": manifest = manifest with { TaskUris = ["https://other.invalid/DO-NOT-EXPOSE", .. manifest.TaskUris.Skip(1)] }; break;
            case "task-uri-count": manifest = manifest with { TaskUris = manifest.TaskUris[..1] }; break;
            case "task-uri-null": manifest = manifest with { TaskUris = null! }; break;
            case "task-request-id": task = task with { RequestId = "other" }; break;
            case "task-job-id": task = task with { JobId = "other" }; break;
            case "task-fingerprint": task = task with { Fingerprint = WorkflowIdentity.Hash("other") }; break;
            case "task-id": task = task with { TaskId = "task-99" }; break;
            case "task-value": task = task with { Value = 2 }; break;
            case "task-classification": task = task with { Classification = "other" }; break;
            case "task-classification-mismatch": task = task with { Classification = "heavy" }; break;
        }
        repository.Documents[manifestPath] = JsonSerializer.Serialize(manifest);
        repository.Documents[aggregatePath] = JsonSerializer.Serialize(aggregate);
        repository.Documents[taskPath] = JsonSerializer.Serialize(task);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            operations.TryGetPublishedResultAsync(RequestId, TestContext.Current.CancellationToken));
        Assert.Equal("Published workflow results are invalid.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("manifest", "{\"DO-NOT-EXPOSE\":")]
    [InlineData("aggregate", "{\"DO-NOT-EXPOSE\":")]
    [InlineData("task", "{\"DO-NOT-EXPOSE\":")]
    [InlineData("manifest", "null")]
    [InlineData("aggregate", "null")]
    [InlineData("task", "null")]
    public async Task MalformedOrNullDocumentsProduceOnlySafeError(string artifact, string content)
    {
        var (operations, repository, plan) = await CreatePublishedAsync();
        repository.Documents[PathFor(artifact, plan)] = content;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            operations.TryGetPublishedResultAsync(RequestId, TestContext.Current.CancellationToken));
        Assert.Equal("Published workflow results are invalid.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(503)]
    public async Task Storage404MeansUnavailableAndOtherFailuresPropagate(int status)
    {
        var failure = new RequestFailedException(status, "provider detail");
        var operations = new WorkflowOperations(new FakeBatchGateway(), new FailingRepository(failure),
            new StubWorkloadClassifier(), new WorkflowSettings());
        if (status == 404)
            Assert.Null(await operations.TryGetPublishedResultAsync(RequestId, TestContext.Current.CancellationToken));
        else
            Assert.Same(failure, await Assert.ThrowsAsync<RequestFailedException>(() =>
                operations.TryGetPublishedResultAsync(RequestId, TestContext.Current.CancellationToken)));
    }

    private static string PathFor(string artifact, WorkflowPlan plan) => artifact switch
    {
        "manifest" => WorkflowIdentity.ManifestPath(plan),
        "aggregate" => WorkflowIdentity.AggregatePath(plan),
        _ => WorkflowIdentity.TaskPath(new TaskSpec(plan, 0))
    };

    private static async Task<(WorkflowOperations Operations, InMemoryResultRepository Repository, WorkflowPlan Plan)> CreatePublishedAsync(
        int count = 3, string classification = "light")
    {
        var repository = new InMemoryResultRepository();
        var operations = new WorkflowOperations(new FakeBatchGateway(), repository, new StubWorkloadClassifier(), new WorkflowSettings());
        var plan = await operations.ClassifyAsync(new WorkflowRequest(RequestId, classification, count), TestContext.Current.CancellationToken);
        await operations.FetchAndPersistAsync(plan, TestContext.Current.CancellationToken);
        await operations.AggregateAsync(plan, TestContext.Current.CancellationToken);
        await operations.PublishManifestAsync(plan, TestContext.Current.CancellationToken);
        return (operations, repository, plan);
    }

    private sealed class FailingRepository(Exception failure) : IResultRepository
    {
        public Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default) => Task.FromException<T>(failure);
        public string GetUri(string path) => throw new NotSupportedException();
        public Task<string> PutIfAbsentAsync<T>(string path, T value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
