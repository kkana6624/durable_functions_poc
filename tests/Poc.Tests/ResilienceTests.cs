using Azure;
using Azure.Compute.Batch;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Poc.Core;
using Poc.Functions;
using Xunit;

#pragma warning disable DURABLE2003, DURABLE2004
namespace Poc.Tests;

public class ResilienceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task TransientAzureFailureIsMarkedForDurableRetry(int status)
    {
        await Assert.ThrowsAsync<TransientWorkflowException>(() => WorkflowFunctions.RetryableActivity<int>(
            () => Task.FromException<int>(new RequestFailedException(status, "temporary")), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConflictAndCorruptDataAreNeverMarkedTransient()
    {
        await Assert.ThrowsAsync<RequestFailedException>(() => WorkflowFunctions.RetryableActivity<int>(
            () => Task.FromException<int>(new RequestFailedException(409, "conflict")), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => WorkflowFunctions.RetryableActivity<int>(
            () => Task.FromException<int>(new WorkflowConflictException("identity")), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkflowFunctions.RetryableActivity<int>(
            () => Task.FromException<int>(new InvalidDataException("corrupt")), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExhaustedSdkTransportRetriesAreMarkedTransientOnlyWhenEveryFailureIsTransient()
    {
        var temporary = new AggregateException(new HttpRequestException("network one"), new AggregateException(new RequestFailedException(503, "network two")));
        await Assert.ThrowsAsync<TransientWorkflowException>(() => WorkflowFunctions.RetryableActivity<int>(() => Task.FromException<int>(temporary), TestContext.Current.CancellationToken));
        var mixed = new AggregateException(new HttpRequestException("network"), new InvalidDataException("corrupt"));
        await Assert.ThrowsAsync<AggregateException>(() => WorkflowFunctions.RetryableActivity<int>(() => Task.FromException<int>(mixed), TestContext.Current.CancellationToken));
        Assert.True(WorkflowFunctions.IsTransientFailure(new OperationCanceledException(), TestContext.Current.CancellationToken));
        Assert.False(WorkflowFunctions.IsTransientFailure(new HttpRequestException("bad input", null, System.Net.HttpStatusCode.BadRequest), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NetworkTimeoutIsRetriedButCallerCancellationIsPreserved()
    {
        await Assert.ThrowsAsync<TransientWorkflowException>(() => WorkflowFunctions.RetryableActivity<int>(
            () => Task.FromException<int>(new OperationCanceledException("SDK network timeout")), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TransientWorkflowException>(() => WorkflowFunctions.RetryableActivity<int>(
            () => Task.FromException<int>(new AggregateException(new OperationCanceledException("SDK timeout"), new RequestFailedException(503, "temporary"))), TestContext.Current.CancellationToken));
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        Assert.False(WorkflowFunctions.IsTransientFailure(new OperationCanceledException(), caller.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => WorkflowFunctions.RetryableActivity<int>(
            () => Task.FromException<int>(new OperationCanceledException(caller.Token)), caller.Token));
    }

    [Fact]
    public async Task RetryUsesDurableBackoffAndStopsAtAttemptLimitOrDeadline()
    {
        var context = Context();
        var now = DateTime.UtcNow;
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(() => now);
        context.Setup(c => c.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns((DateTime date, CancellationToken _) => { now = date; return Task.CompletedTask; });
        var failure = new TaskFailureDetails(typeof(TransientWorkflowException).FullName!, "temporary", null, null, null);
        var handler = WorkflowFunctions.RetryOptions(now.AddMinutes(1)).Retry!.Handler!;
        Assert.True(await handler(new RetryContext(context.Object, 1, failure, TimeSpan.Zero, CancellationToken.None)));
        context.Verify(c => c.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(await handler(new RetryContext(context.Object, 3, failure, TimeSpan.Zero, CancellationToken.None)));
        var expires = WorkflowFunctions.RetryOptions(now.AddSeconds(4)).Retry!.Handler!;
        Assert.False(await expires(new RetryContext(context.Object, 1, failure, TimeSpan.Zero, CancellationToken.None)));
        var permanent = new TaskFailureDetails(typeof(InvalidDataException).FullName!, "corrupt", null, null, null);
        Assert.False(await handler(new RetryContext(context.Object, 1, permanent, TimeSpan.Zero, CancellationToken.None)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentTimeoutConfirmsStopOrRecordsFailureAndNeverPublishes(bool stopFails)
    {
        var request = new WorkflowRequest("timeout-parent");
        var plan = Plan(request);
        var context = Context();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(DateTime.UtcNow);
        context.Setup(c => c.GetInput<WorkflowRequest>()).Returns(request);
        context.Setup(c => c.CallActivityAsync<WorkflowPlan>(nameof(WorkflowFunctions.ClassifyActivity), request, It.IsAny<TaskOptions>())).ReturnsAsync(plan);
        context.Setup(c => c.CallActivityAsync<object>(nameof(WorkflowFunctions.EnsureBatchJobActivity), plan, It.IsAny<TaskOptions>())).ReturnsAsync((object)null!);
        context.Setup(c => c.CallSubOrchestratorAsync<TaskMonitorResult>(nameof(WorkflowFunctions.MonitorBatchTaskOrchestrator), It.IsAny<object>(), null))
            .ReturnsAsync(new TaskMonitorResult("task-00", "TimedOut", "deadline"));
        context.Setup(c => c.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns((DateTime _, CancellationToken token) => Task.Delay(Timeout.Infinite, token));
        var stopping = context.Setup(c => c.CallActivityAsync<BatchJobTerminationStatus>(nameof(WorkflowFunctions.TerminateBatchJobActivity), plan, It.IsAny<TaskOptions>()));
        if (stopFails) stopping.ThrowsAsync(new WorkflowConflictException("foreign job"));
        else stopping.ReturnsAsync(new BatchJobTerminationStatus("Completed", true));
        var statuses = new List<string>();
        context.Setup(c => c.SetCustomStatus(It.IsAny<object>())).Callback<object>(s => statuses.Add(System.Text.Json.JsonSerializer.Serialize(s)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowFunctions.MainOrchestrator(context.Object));
        context.Verify(c => c.CallActivityAsync<ManifestReference>(nameof(WorkflowFunctions.PublishManifestActivity), It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
        Assert.Contains("TimedOut", statuses.Last());
        Assert.Contains(stopFails ? "foreign job" : "Completed", statuses.Last());
    }

    [Fact]
    public async Task HangingSubmissionActivityTimesOutWithoutWaitingForIt()
    {
        var initial = DateTime.UtcNow;
        var spec = new TaskSpec(Plan(new WorkflowRequest("hung-submit")), 0, initial.AddMinutes(1));
        var context = Context();
        var now = initial;
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(() => now);
        context.Setup(c => c.GetInput<TaskSpec>()).Returns(spec);
        var pending = new TaskCompletionSource<object>();
        context.Setup(c => c.CallActivityAsync<object>(nameof(WorkflowFunctions.EnsureBatchTaskActivity), spec, It.IsAny<TaskOptions>())).Returns(pending.Task);
        context.Setup(c => c.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns((DateTime date, CancellationToken _) => { now = date; return Task.CompletedTask; });
        var result = await WorkflowFunctions.MonitorBatchTaskOrchestrator(context.Object);
        Assert.Equal("TimedOut", result.State);
        Assert.False(pending.Task.IsCompleted);
        context.Verify(c => c.CallActivityAsync<BatchTaskStatus>(nameof(WorkflowFunctions.GetBatchTaskStatusActivity), It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    [Fact]
    public async Task ExpiredChildDoesNotSubmit()
    {
        var spec = new TaskSpec(Plan(new WorkflowRequest("expired-submit")), 0, DateTime.UtcNow.AddMinutes(-1));
        var context = Context();
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(DateTime.UtcNow);
        context.Setup(c => c.GetInput<TaskSpec>()).Returns(spec);
        Assert.Equal("TimedOut", (await WorkflowFunctions.MonitorBatchTaskOrchestrator(context.Object)).State);
        context.Verify(c => c.CallActivityAsync<object>(nameof(WorkflowFunctions.EnsureBatchTaskActivity), It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    [Theory]
    [InlineData("other-pool", "same")]
    [InlineData("pool", "different")]
    public async Task TerminationRefusesForeignPoolOrFingerprint(string pool, string fingerprint)
    {
        var plan = Plan(new WorkflowRequest("safe-stop"));
        var client = BatchReturning(Job(plan, BatchJobState.Active, pool, fingerprint == "same" ? plan.Fingerprint : "foreign"));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => new AzureBatchGateway(client.Object, "pool").TerminateJobAsync(plan, TestContext.Current.CancellationToken));
        client.Verify(c => c.TerminateJobAsync(It.IsAny<WaitUntil>(), It.IsAny<string>(), It.IsAny<BatchJobTerminateOptions>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<bool?>(), It.IsAny<RequestConditions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AlreadyCompletedJobDoesNotNeedAnotherStopRequest()
    {
        var plan = Plan(new WorkflowRequest("stopped"));
        var client = BatchReturning(Job(plan, BatchJobState.Completed, "pool", plan.Fingerprint));
        Assert.True((await new AzureBatchGateway(client.Object, "pool").TerminateJobAsync(plan, TestContext.Current.CancellationToken)).Completed);
        client.Verify(c => c.TerminateJobAsync(It.IsAny<WaitUntil>(), It.IsAny<string>(), It.IsAny<BatchJobTerminateOptions>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<bool?>(), It.IsAny<RequestConditions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StopRequestWaitsForConfirmedJobCompletionAcrossActivityReplays()
    {
        var plan = Plan(new WorkflowRequest("stop-replay"));
        var client = new Mock<BatchClient>();
        var active = Job(plan, BatchJobState.Active, "pool", plan.Fingerprint);
        var stopping = Job(plan, BatchJobState.Terminating, "pool", plan.Fingerprint);
        var complete = Job(plan, BatchJobState.Completed, "pool", plan.Fingerprint);
        client.SetupSequence(c => c.GetJobAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(active, Mock.Of<Response>()))
            .ReturnsAsync(Response.FromValue(stopping, Mock.Of<Response>()))
            .ReturnsAsync(Response.FromValue(stopping, Mock.Of<Response>()))
            .ReturnsAsync(Response.FromValue(complete, Mock.Of<Response>()));
        client.Setup(c => c.TerminateJobAsync(WaitUntil.Started, plan.JobId, It.IsAny<BatchJobTerminateOptions>(), null, null, null, It.IsAny<RequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TerminateJobOperation)null!);
        var gateway = new AzureBatchGateway(client.Object, "pool");
        Assert.False((await gateway.TerminateJobAsync(plan, TestContext.Current.CancellationToken)).Completed);
        Assert.True((await gateway.TerminateJobAsync(plan, TestContext.Current.CancellationToken)).Completed);
        client.Verify(c => c.TerminateJobAsync(WaitUntil.Started, plan.JobId, It.IsAny<BatchJobTerminateOptions>(), null, null, null, It.IsAny<RequestConditions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConcurrentNaturalCompletionAfterIdentityCheckIsSafe()
    {
        var plan = Plan(new WorkflowRequest("concurrent-stop"));
        var client = new Mock<BatchClient>();
        client.SetupSequence(c => c.GetJobAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(Job(plan, BatchJobState.Active, "pool", plan.Fingerprint), Mock.Of<Response>()))
            .ReturnsAsync(Response.FromValue(Job(plan, BatchJobState.Completed, "pool", plan.Fingerprint), Mock.Of<Response>()));
        client.Setup(c => c.TerminateJobAsync(WaitUntil.Started, plan.JobId, It.IsAny<BatchJobTerminateOptions>(), null, null, null, It.IsAny<RequestConditions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(412, "state changed"));
        Assert.True((await new AzureBatchGateway(client.Object, "pool").TerminateJobAsync(plan, TestContext.Current.CancellationToken)).Completed);
    }

    [Fact]
    public async Task JobCreationIncludesServerEnforcedTimeoutForHostOutages()
    {
        var plan = Plan(new WorkflowRequest("server-timeout"));
        var client = BatchReturning(Job(plan, BatchJobState.Active, "pool", plan.Fingerprint));
        BatchJobCreateOptions? creation = null;
        client.Setup(c => c.CreateJobAsync(It.IsAny<BatchJobCreateOptions>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Callback<BatchJobCreateOptions, TimeSpan?, DateTimeOffset?, CancellationToken>((options, _, _, _) => creation = options)
            .ThrowsAsync(new RequestFailedException(409, "already created", "JobExists", null));
        await new AzureBatchGateway(client.Object, "pool").EnsureJobAsync(plan, TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromMinutes(plan.TimeoutMinutes), creation!.Constraints.MaxWallClockTime);
    }

    private static Mock<BatchClient> BatchReturning(BatchJob job)
    {
        var client = new Mock<BatchClient>();
        client.Setup(c => c.GetJobAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(job, Mock.Of<Response>()));
        return client;
    }
    private static BatchJob Job(WorkflowPlan plan, BatchJobState state, string pool, string fingerprint) =>
        ComputeBatchModelFactory.BatchJob(id: plan.JobId, state: state, poolInfo: new BatchPoolInfo { PoolId = pool }, metadata: [new BatchMetadataItem("poc-fingerprint", fingerprint)]);
    private static WorkflowPlan Plan(WorkflowRequest request) => new(request, "light", "Test", WorkflowIdentity.JobId(request.RequestId), WorkflowIdentity.Fingerprint(request, "light"), 10, 15);
    private static Mock<TaskOrchestrationContext> Context()
    {
        var context = new Mock<TaskOrchestrationContext>();
        context.Setup(c => c.CreateReplaySafeLogger(It.IsAny<string>())).Returns(NullLogger.Instance);
        return context;
    }
}
