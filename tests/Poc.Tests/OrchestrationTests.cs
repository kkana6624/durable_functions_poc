using Microsoft.DurableTask;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Poc.Core;
using Poc.Functions;
using Xunit;

// These calls configure mocks; the actual Functions live in the referenced project.
#pragma warning disable DURABLE2003, DURABLE2004

namespace Poc.Tests;

public class OrchestrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentWaitsForEveryChildAndPublishesOnlyAllSuccess(bool failure)
    {
        var request = new WorkflowRequest("orchestrator-001");
        var plan = Plan(request);
        var context = Context();
        context.Setup(c => c.GetInput<WorkflowRequest>()).Returns(request);
        context.Setup(c => c.CallActivityAsync<WorkflowPlan>(nameof(WorkflowFunctions.ClassifyActivity), request, It.IsAny<TaskOptions>()))
            .ReturnsAsync(plan);
        context.Setup(c => c.CallActivityAsync<object>(nameof(WorkflowFunctions.EnsureBatchJobActivity), plan, It.IsAny<TaskOptions>()))
            .ReturnsAsync((object)null!);
        var children = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource<TaskMonitorResult>()).ToArray();
        context.Setup(c => c.CallSubOrchestratorAsync<TaskMonitorResult>(nameof(WorkflowFunctions.MonitorBatchTaskOrchestrator), It.IsAny<object>(), null))
            .Returns((TaskName _, object input, TaskOptions? _) => children[((TaskSpec)input).Index].Task);
        context.Setup(c => c.CallActivityAsync<ResultReference[]>(nameof(WorkflowFunctions.FetchAndPersistActivity), plan, It.IsAny<TaskOptions>())).ReturnsAsync([]);
        context.Setup(c => c.CallActivityAsync<AggregateReference>(nameof(WorkflowFunctions.AggregateActivity), plan, It.IsAny<TaskOptions>()))
            .ReturnsAsync(new AggregateReference(request.RequestId, "aggregate", 3, 3));
        context.Setup(c => c.CallActivityAsync<ManifestReference>(nameof(WorkflowFunctions.PublishManifestActivity), plan, It.IsAny<TaskOptions>()))
            .ReturnsAsync(new ManifestReference(request.RequestId, "manifest", "aggregate", 3, 3));

        var run = WorkflowFunctions.MainOrchestrator(context.Object);
        context.Verify(c => c.CallSubOrchestratorAsync<TaskMonitorResult>(nameof(WorkflowFunctions.MonitorBatchTaskOrchestrator), It.IsAny<object>(), null), Times.Exactly(3));
        children[0].SetResult(new TaskMonitorResult("task-00", failure ? "Failed" : "Succeeded", failure ? "exit 42" : null));
        children[1].SetResult(new TaskMonitorResult("task-01", "Succeeded", null));
        Assert.False(run.IsCompleted);
        context.Verify(c => c.CallActivityAsync<ResultReference[]>(nameof(WorkflowFunctions.FetchAndPersistActivity), plan, It.IsAny<TaskOptions>()), Times.Never);
        children[2].SetResult(new TaskMonitorResult("task-02", "Succeeded", null));
        if (failure) await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        else Assert.Equal("Succeeded", (await run).State);
        var expected = failure ? Times.Never() : Times.Once();
        context.Verify(c => c.CallActivityAsync<ResultReference[]>(nameof(WorkflowFunctions.FetchAndPersistActivity), plan, It.IsAny<TaskOptions>()), expected);
        context.Verify(c => c.CallActivityAsync<ManifestReference>(nameof(WorkflowFunctions.PublishManifestActivity), plan, It.IsAny<TaskOptions>()), expected);
    }

    [Fact]
    public async Task MainOrchestrator_WhenCancelRequestedBeforeStart_TerminatesWithoutBatchJob()
    {
        var request = new WorkflowRequest("orchestrator-cancel-pre");
        var plan = Plan(request) with { CancelRequested = true };
        var context = Context();
        context.Setup(c => c.GetInput<WorkflowRequest>()).Returns(request);
        context.Setup(c => c.CallActivityAsync<WorkflowPlan>(nameof(WorkflowFunctions.ClassifyActivity), request, It.IsAny<TaskOptions>()))
            .ReturnsAsync(plan);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowFunctions.MainOrchestrator(context.Object));
        Assert.Equal("Workflow was cancelled by user.", ex.Message);

        context.Verify(c => c.CallActivityAsync<object>(nameof(WorkflowFunctions.EnsureBatchJobActivity), It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
        context.Verify(c => c.CallSubOrchestratorAsync<TaskMonitorResult>(nameof(WorkflowFunctions.MonitorBatchTaskOrchestrator), It.IsAny<object>(), null), Times.Never);
    }

    [Fact]
    public async Task MainOrchestrator_WhenCancelRequestedMidExecution_TerminatesBatchJobAndCancels()
    {
        var request = new WorkflowRequest("orchestrator-cancel-mid");
        var plan = Plan(request);
        var context = Context();
        var cancelTcs = new TaskCompletionSource<WorkflowCancelRecord>();
        context.Setup(c => c.WaitForExternalEvent<WorkflowCancelRecord>(WorkflowIdentity.CancelEventName, It.IsAny<CancellationToken>()))
            .Returns(cancelTcs.Task);
        context.Setup(c => c.GetInput<WorkflowRequest>()).Returns(request);
        context.Setup(c => c.CallActivityAsync<WorkflowPlan>(nameof(WorkflowFunctions.ClassifyActivity), request, It.IsAny<TaskOptions>()))
            .ReturnsAsync(plan);
        context.Setup(c => c.CallActivityAsync<object>(nameof(WorkflowFunctions.EnsureBatchJobActivity), plan, It.IsAny<TaskOptions>()))
            .ReturnsAsync((object)null!);
        var children = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource<TaskMonitorResult>()).ToArray();
        context.Setup(c => c.CallSubOrchestratorAsync<TaskMonitorResult>(nameof(WorkflowFunctions.MonitorBatchTaskOrchestrator), It.IsAny<object>(), null))
            .Returns((TaskName _, object input, TaskOptions? _) => children[((TaskSpec)input).Index].Task);
        context.Setup(c => c.CallActivityAsync<BatchJobTerminationStatus>(nameof(WorkflowFunctions.TerminateBatchJobActivity), plan, It.IsAny<TaskOptions>()))
            .ReturnsAsync(new BatchJobTerminationStatus("Completed", true));

        var run = WorkflowFunctions.MainOrchestrator(context.Object);
        Assert.False(run.IsCompleted);

        cancelTcs.SetResult(new WorkflowCancelRecord(1, request.RequestId, "user cancelled", DateTimeOffset.UtcNow, null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal("Workflow was cancelled by user.", ex.Message);

        context.Verify(c => c.CallActivityAsync<BatchJobTerminationStatus>(nameof(WorkflowFunctions.TerminateBatchJobActivity), plan, It.IsAny<TaskOptions>()), Times.Once);
        context.Verify(c => c.CallActivityAsync<ManifestReference>(nameof(WorkflowFunctions.PublishManifestActivity), plan, It.IsAny<TaskOptions>()), Times.Never);
    }

    [Fact]
    public async Task MainOrchestrator_WhenPublishManifestActivityThrowsWorkflowCancelled_SetsStateCancelled()
    {
        var request = new WorkflowRequest("orchestrator-manifest-cancelled");
        var plan = Plan(request);
        var context = Context();
        context.Setup(c => c.GetInput<WorkflowRequest>()).Returns(request);
        context.Setup(c => c.CallActivityAsync<WorkflowPlan>(nameof(WorkflowFunctions.ClassifyActivity), request, It.IsAny<TaskOptions>()))
            .ReturnsAsync(plan);
        context.Setup(c => c.CallActivityAsync<object>(nameof(WorkflowFunctions.EnsureBatchJobActivity), plan, It.IsAny<TaskOptions>()))
            .ReturnsAsync((object)null!);
        var children = Enumerable.Range(0, 3).Select(i => Task.FromResult(new TaskMonitorResult($"task-{i:00}", "Succeeded", null))).ToArray();
        context.Setup(c => c.CallSubOrchestratorAsync<TaskMonitorResult>(nameof(WorkflowFunctions.MonitorBatchTaskOrchestrator), It.IsAny<object>(), null))
            .Returns((TaskName _, object input, TaskOptions? _) => children[((TaskSpec)input).Index]);
        context.Setup(c => c.CallActivityAsync<ResultReference[]>(nameof(WorkflowFunctions.FetchAndPersistActivity), plan, It.IsAny<TaskOptions>())).ReturnsAsync([]);
        context.Setup(c => c.CallActivityAsync<AggregateReference>(nameof(WorkflowFunctions.AggregateActivity), plan, It.IsAny<TaskOptions>()))
            .ReturnsAsync(new AggregateReference(request.RequestId, "aggregate", 3, 3));

        var details = new TaskFailureDetails(typeof(WorkflowCancelledException).FullName!, "Workflow was cancelled; manifest will not be published.", null, null, null);
        context.Setup(c => c.CallActivityAsync<ManifestReference>(nameof(WorkflowFunctions.PublishManifestActivity), plan, It.IsAny<TaskOptions>()))
            .ThrowsAsync(new TaskFailedException(nameof(WorkflowFunctions.PublishManifestActivity), 1, details));

        object? lastCustomStatus = null;
        context.Setup(c => c.SetCustomStatus(It.IsAny<object>()))
            .Callback<object>(s => lastCustomStatus = s);

        await Assert.ThrowsAsync<TaskFailedException>(() => WorkflowFunctions.MainOrchestrator(context.Object));

        Assert.NotNull(lastCustomStatus);
        var statusProp = lastCustomStatus.GetType().GetProperty("state");
        Assert.NotNull(statusProp);
        Assert.Equal("Cancelled", statusProp.GetValue(lastCustomStatus));
    }

    [Fact]
    public async Task PublishManifestActivity_PreservesWorkflowCancelledException_AcrossBoundary()
    {
        var repo = new InMemoryResultRepository();
        var operations = new WorkflowOperations(new FakeBatchGateway(), repo, new StubWorkloadClassifier(), new WorkflowSettings());
        var functions = new WorkflowFunctions(operations, new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());

        var request = new WorkflowRequest("activity-cancel-preserve");
        var plan = Plan(request);

        await repo.PutIfAbsentAsync(WorkflowIdentity.ResolutionPath(request.RequestId),
            new WorkflowResolutionRecord(1, request.RequestId, "Cancelled"), TestContext.Current.CancellationToken);

        for (int i = 0; i < 3; i++)
        {
            var spec = new TaskSpec(plan, i);
            await repo.PutIfAbsentAsync(WorkflowIdentity.TaskPath(spec),
                new TaskResult(request.RequestId, plan.JobId, spec.TaskId, "light", "Test", plan.Fingerprint, 1),
                TestContext.Current.CancellationToken);
        }
        await repo.PutIfAbsentAsync(WorkflowIdentity.AggregatePath(plan),
            new AggregateResult(request.RequestId, plan.JobId, plan.Fingerprint, 3, 3, ["task-00", "task-01", "task-02"]),
            TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<WorkflowCancelledException>(() =>
            functions.PublishManifestActivity(plan, TestContext.Current.CancellationToken));
        Assert.Contains("cancelled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MonitorUsesDurableTimerAndStopsAtDeadline()

    {
        var plan = Plan(new WorkflowRequest("timeout-001")) with { PollSeconds = 40, TimeoutMinutes = 1 };
        var spec = new TaskSpec(plan, 0);
        var context = Context();
        context.Setup(c => c.GetInput<TaskSpec>()).Returns(spec);
        var initial = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);
        var current = initial;
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(() => current);
        context.Setup(c => c.CallActivityAsync<object>(nameof(WorkflowFunctions.EnsureBatchTaskActivity), spec, It.IsAny<TaskOptions>())).ReturnsAsync((object)null!);
        context.Setup(c => c.CallActivityAsync<BatchTaskStatus>(nameof(WorkflowFunctions.GetBatchTaskStatusActivity), spec, It.IsAny<TaskOptions>()))
            .ReturnsAsync(new BatchTaskStatus(spec.TaskId, "running", false, false, null, null));
        var timerDates = new List<DateTime>();
        context.Setup(c => c.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns((DateTime date, CancellationToken _) => { if (_.CanBeCanceled) return Task.Delay(Timeout.Infinite, _); timerDates.Add(date); current = date; return Task.CompletedTask; });

        var result = await WorkflowFunctions.MonitorBatchTaskOrchestrator(context.Object);
        Assert.Equal("TimedOut", result.State);
        Assert.Equal(new[] { initial.AddSeconds(40), initial.AddMinutes(1) }, timerDates);
    }

    private static Mock<TaskOrchestrationContext> Context()
    {
        var context = new Mock<TaskOrchestrationContext>();
        context.Setup(c => c.CreateReplaySafeLogger(It.IsAny<string>())).Returns(NullLogger.Instance);
        context.Setup(c => c.WaitForExternalEvent<WorkflowCancelRecord>(WorkflowIdentity.CancelEventName, It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<WorkflowCancelRecord>().Task);
        return context;
    }


    private static WorkflowPlan Plan(WorkflowRequest request) => new(request, "light", "Test", WorkflowIdentity.JobId(request.RequestId),
        WorkflowIdentity.Fingerprint(request, "light"), 10, 15);
}
