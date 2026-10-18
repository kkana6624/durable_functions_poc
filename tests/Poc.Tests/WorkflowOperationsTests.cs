using System.Text.Json;
using Poc.Core;
using Xunit;

namespace Poc.Tests;

public class WorkflowOperationsTests
{
    private readonly FakeBatchGateway batch = new();
    private readonly InMemoryResultRepository repository = new();
    private WorkflowOperations Operations => new(batch, repository, new StubWorkloadClassifier(), new WorkflowSettings());

    [Theory]
    [InlineData("auto", "light", 5)]
    [InlineData("light", "light", 5)]
    [InlineData("heavy", "heavy", 15)]
    public async Task SuccessfulThreeTasksPersistClassificationAndSum(string input, string expected, int delay)
    {
        var operations = Operations;
        var plan = await operations.ClassifyAsync(new WorkflowRequest("feasibility-001", input), TestContext.Current.CancellationToken);
        Assert.Equal(expected, plan.Classification);
        Assert.NotEmpty(plan.Reason);
        Assert.Equal(delay, new TaskSpec(plan, 0).DelaySeconds);
        await EnsureTasksAsync(operations, plan);
        var references = await operations.FetchAndPersistAsync(plan, TestContext.Current.CancellationToken);
        var aggregate = await operations.AggregateAsync(plan, TestContext.Current.CancellationToken);
        var manifest = await operations.PublishManifestAsync(plan, TestContext.Current.CancellationToken);
        Assert.Equal(3, references.Length);
        Assert.Equal(3, aggregate.Sum);
        Assert.Equal(3, aggregate.TaskCount);
        Assert.Equal(3, manifest.Sum);
        Assert.Equal(6, repository.Documents.Count);
        foreach (var index in Enumerable.Range(0, 3))
        {
            var result = await repository.ReadAsync<TaskResult>(WorkflowIdentity.TaskPath(new TaskSpec(plan, index)), TestContext.Current.CancellationToken);
            Assert.Equal(expected, result.Classification);
            Assert.Equal(1, result.Value);
        }
    }

    [Fact]
    public async Task ReplayedActivitiesReuseResourcesAndManifest()
    {
        var operations = Operations;
        var plan = await operations.ClassifyAsync(new WorkflowRequest("replay-001"), TestContext.Current.CancellationToken);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await EnsureTasksAsync(operations, plan);
            await operations.FetchAndPersistAsync(plan, TestContext.Current.CancellationToken);
            await operations.AggregateAsync(plan, TestContext.Current.CancellationToken);
            await operations.PublishManifestAsync(plan, TestContext.Current.CancellationToken);
        }
        Assert.Single(batch.Jobs);
        Assert.Equal(3, batch.Tasks.Count);
        Assert.Equal(6, repository.Creates);
    }

    [Fact]
    public async Task ChangedInputWithSameRequestIdConflicts()
    {
        var operations = Operations;
        var first = await operations.ClassifyAsync(new WorkflowRequest("same-id"), TestContext.Current.CancellationToken);
        var changed = await operations.ClassifyAsync(new WorkflowRequest("same-id", FailTaskIndex: 1), TestContext.Current.CancellationToken);
        await operations.EnsureJobAsync(first, TestContext.Current.CancellationToken);
        await operations.EnsureTaskAsync(new TaskSpec(first, 0), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => operations.EnsureJobAsync(changed, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => operations.EnsureTaskAsync(new TaskSpec(changed, 0), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedTaskPreventsFinalArtifacts()
    {
        var operations = Operations;
        var plan = await operations.ClassifyAsync(new WorkflowRequest("failed-001", FailTaskIndex: 1), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.FetchAndPersistAsync(plan, TestContext.Current.CancellationToken));
        Assert.False(repository.Documents.ContainsKey(WorkflowIdentity.AggregatePath(plan)));
        Assert.False(repository.Documents.ContainsKey(WorkflowIdentity.ManifestPath(plan)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => operations.PublishManifestAsync(plan, TestContext.Current.CancellationToken));
        Assert.False(repository.Documents.ContainsKey(WorkflowIdentity.ManifestPath(plan)));
    }

    [Fact]
    public async Task MissingTaskFileIsAnErrorAndPreventsManifest()
    {
        batch.MissingFileIndex = 1;
        var operations = Operations;
        var plan = await operations.ClassifyAsync(new WorkflowRequest("missing-001"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<FileNotFoundException>(() => operations.FetchAndPersistAsync(plan, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FileNotFoundException>(() => operations.AggregateAsync(plan, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FileNotFoundException>(() => operations.PublishManifestAsync(plan, TestContext.Current.CancellationToken));
        Assert.False(repository.Documents.ContainsKey(WorkflowIdentity.ManifestPath(plan)));
    }

    [Fact]
    public async Task CorruptAggregateCannotBePublished()
    {
        var operations = Operations;
        var plan = await operations.ClassifyAsync(new WorkflowRequest("corrupt-001"), TestContext.Current.CancellationToken);
        await operations.FetchAndPersistAsync(plan, TestContext.Current.CancellationToken);
        await operations.AggregateAsync(plan, TestContext.Current.CancellationToken);
        var path = WorkflowIdentity.AggregatePath(plan);
        var aggregate = await repository.ReadAsync<AggregateResult>(path, TestContext.Current.CancellationToken);
        repository.Documents[path] = JsonSerializer.Serialize(aggregate with { Sum = 999 });
        await Assert.ThrowsAsync<InvalidDataException>(() => operations.PublishManifestAsync(plan, TestContext.Current.CancellationToken));
        Assert.False(repository.Documents.ContainsKey(WorkflowIdentity.ManifestPath(plan)));
    }

    [Fact]
    public async Task PublishManifest_ThrowsWorkflowCancelledException_WhenResolutionAlreadyCancelled()
    {
        var operations = Operations;
        var plan = await operations.ClassifyAsync(new WorkflowRequest("cancel-race-001"), TestContext.Current.CancellationToken);
        await EnsureTasksAsync(operations, plan);
        await operations.FetchAndPersistAsync(plan, TestContext.Current.CancellationToken);
        await operations.AggregateAsync(plan, TestContext.Current.CancellationToken);

        await repository.PutIfAbsentAsync(
            WorkflowIdentity.ResolutionPath(plan.Request.RequestId),
            new WorkflowResolutionRecord(1, plan.Request.RequestId, "Cancelled"),
            TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<WorkflowCancelledException>(() => operations.PublishManifestAsync(plan, TestContext.Current.CancellationToken));
        Assert.Contains("cancelled", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(repository.Documents.ContainsKey(WorkflowIdentity.ManifestPath(plan)));
    }

    [Fact]
    public async Task ClassifyAsync_RecognizesCancellation_WhenResolutionIsCancelledEvenIfCancelRecordMissing()
    {
        var operations = Operations;
        var requestId = "partial-cancel-write-001";
        // Simulate partial cancel write: resolution.json created with Cancelled, cancel.json failed/missing
        await repository.PutIfAbsentAsync(
            WorkflowIdentity.ResolutionPath(requestId),
            new WorkflowResolutionRecord(1, requestId, "Cancelled"),
            TestContext.Current.CancellationToken);

        var plan = await operations.ClassifyAsync(new WorkflowRequest(requestId), TestContext.Current.CancellationToken);
        Assert.True(plan.CancelRequested, "ClassifyAsync must recognize CancelRequested from resolution.json even if cancel.json is missing.");
    }

    [Theory]
    [InlineData(2, "corrupt-res-001", "Cancelled")] // Bad version
    [InlineData(1, "wrong-request-id", "Cancelled")] // Mismatched requestId
    [InlineData(1, "corrupt-res-001", "UnknownStatus")] // Invalid status
    public async Task PublishManifest_ThrowsInvalidDataException_WhenResolutionRecordIsCorrupt(int version, string recordRequestId, string status)
    {
        var operations = Operations;
        var plan = await operations.ClassifyAsync(new WorkflowRequest("corrupt-res-001"), TestContext.Current.CancellationToken);
        await EnsureTasksAsync(operations, plan);
        await operations.FetchAndPersistAsync(plan, TestContext.Current.CancellationToken);
        await operations.AggregateAsync(plan, TestContext.Current.CancellationToken);

        // Put corrupted resolution in repository
        var corruptResolution = new WorkflowResolutionRecord(version, recordRequestId, status);
        repository.Documents[WorkflowIdentity.ResolutionPath(plan.Request.RequestId)] = System.Text.Json.JsonSerializer.Serialize(corruptResolution);

        await Assert.ThrowsAsync<InvalidDataException>(() => operations.PublishManifestAsync(plan, TestContext.Current.CancellationToken));
    }

    private static async Task EnsureTasksAsync(WorkflowOperations operations, WorkflowPlan plan)
    {
        await operations.EnsureJobAsync(plan, TestContext.Current.CancellationToken);
        await Task.WhenAll(Enumerable.Range(0, plan.Request.TaskCount)
            .Select(index => operations.EnsureTaskAsync(new TaskSpec(plan, index), TestContext.Current.CancellationToken)));
    }
}
