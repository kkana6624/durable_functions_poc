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
        context.Setup(c => c.CallActivityAsync<WorkflowPlan>(nameof(WorkflowFunctions.ClassifyActivity), request, null))
            .ReturnsAsync(plan);
        context.Setup(c => c.CallActivityAsync<object>(nameof(WorkflowFunctions.EnsureBatchJobActivity), plan, null))
            .ReturnsAsync((object)null!);
        var children = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource<TaskMonitorResult>()).ToArray();
        context.Setup(c => c.CallSubOrchestratorAsync<TaskMonitorResult>(nameof(WorkflowFunctions.MonitorBatchTaskOrchestrator), It.IsAny<object>(), null))
            .Returns((TaskName _, object input, TaskOptions? _) => children[((TaskSpec)input).Index].Task);
        context.Setup(c => c.CallActivityAsync<ResultReference[]>(nameof(WorkflowFunctions.FetchAndPersistActivity), plan, null)).ReturnsAsync([]);
        context.Setup(c => c.CallActivityAsync<AggregateReference>(nameof(WorkflowFunctions.AggregateActivity), plan, null))
            .ReturnsAsync(new AggregateReference(request.RequestId, "aggregate", 3, 3));
        context.Setup(c => c.CallActivityAsync<ManifestReference>(nameof(WorkflowFunctions.PublishManifestActivity), plan, null))
            .ReturnsAsync(new ManifestReference(request.RequestId, "manifest", "aggregate", 3, 3));

        var run = WorkflowFunctions.MainOrchestrator(context.Object);
        context.Verify(c => c.CallSubOrchestratorAsync<TaskMonitorResult>(nameof(WorkflowFunctions.MonitorBatchTaskOrchestrator), It.IsAny<object>(), null), Times.Exactly(3));
        children[0].SetResult(new TaskMonitorResult("task-00", failure ? "Failed" : "Succeeded", failure ? "exit 42" : null));
        children[1].SetResult(new TaskMonitorResult("task-01", "Succeeded", null));
        Assert.False(run.IsCompleted);
        context.Verify(c => c.CallActivityAsync<ResultReference[]>(nameof(WorkflowFunctions.FetchAndPersistActivity), plan, null), Times.Never);
        children[2].SetResult(new TaskMonitorResult("task-02", "Succeeded", null));
        if (failure) await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        else Assert.Equal("Succeeded", (await run).State);
        var expected = failure ? Times.Never() : Times.Once();
        context.Verify(c => c.CallActivityAsync<ResultReference[]>(nameof(WorkflowFunctions.FetchAndPersistActivity), plan, null), expected);
        context.Verify(c => c.CallActivityAsync<ManifestReference>(nameof(WorkflowFunctions.PublishManifestActivity), plan, null), expected);
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
        context.Setup(c => c.CallActivityAsync<object>(nameof(WorkflowFunctions.EnsureBatchTaskActivity), spec, null)).ReturnsAsync((object)null!);
        context.Setup(c => c.CallActivityAsync<BatchTaskStatus>(nameof(WorkflowFunctions.GetBatchTaskStatusActivity), spec, null))
            .ReturnsAsync(new BatchTaskStatus(spec.TaskId, "running", false, false, null, null));
        var timerDates = new List<DateTime>();
        context.Setup(c => c.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns((DateTime date, CancellationToken _) => { timerDates.Add(date); current = date; return Task.CompletedTask; });

        var result = await WorkflowFunctions.MonitorBatchTaskOrchestrator(context.Object);
        Assert.Equal("TimedOut", result.State);
        Assert.Equal(new[] { initial.AddSeconds(40), initial.AddMinutes(1) }, timerDates);
    }

    private static Mock<TaskOrchestrationContext> Context()
    {
        var context = new Mock<TaskOrchestrationContext>();
        context.Setup(c => c.CreateReplaySafeLogger(It.IsAny<string>())).Returns(NullLogger.Instance);
        return context;
    }

    private static WorkflowPlan Plan(WorkflowRequest request) => new(request, "light", "Test", WorkflowIdentity.JobId(request.RequestId),
        WorkflowIdentity.Fingerprint(request, "light"), 10, 15);
}
