using System.ClientModel.Primitives;
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
        var failure = await Assert.ThrowsAsync<TransientWorkflowException>(() => WorkflowFunctions.RetryableActivity<int>(
            () => Task.FromException<int>(new RequestFailedException(status, "SECRET_SENTINEL")), TestContext.Current.CancellationToken));

        Assert.Equal("Workflow activity temporarily failed.", failure.Message);
        Assert.Null(failure.InnerException);
        var details = TaskFailureDetails.FromException(failure);
        Assert.True(details.IsCausedBy<TransientWorkflowException>());
        Assert.Equal(failure.Message, details.ErrorMessage);
        Assert.Null(details.InnerFailure);
        Assert.DoesNotContain("SECRET_SENTINEL", details.ErrorMessage);
    }

    [Fact]
    public async Task PermanentActivityFailuresUseSafeFailureDetailsAndAreNeverMarkedTransient()
    {
        await AssertSafePermanentFailureAsync(new RequestFailedException(409, "SECRET_SENTINEL"));
        await AssertSafePermanentFailureAsync(new WorkflowConflictException("SECRET_SENTINEL"));
        await AssertSafePermanentFailureAsync(new InvalidDataException("SECRET_SENTINEL"));
    }

    [Fact]
    public async Task ExhaustedSdkTransportRetriesAreMarkedTransientOnlyWhenEveryFailureIsTransient()
    {
        var temporary = new AggregateException(new HttpRequestException("SECRET_SENTINEL"), new AggregateException(new RequestFailedException(503, "SECRET_SENTINEL")));
        var transient = await Assert.ThrowsAsync<TransientWorkflowException>(() => WorkflowFunctions.RetryableActivity<int>(() => Task.FromException<int>(temporary), TestContext.Current.CancellationToken));
        Assert.Equal("Workflow activity temporarily failed.", transient.Message);
        Assert.Null(transient.InnerException);
        var transientDetails = TaskFailureDetails.FromException(transient);
        Assert.True(transientDetails.IsCausedBy<TransientWorkflowException>());
        Assert.Null(transientDetails.InnerFailure);
        Assert.DoesNotContain("SECRET_SENTINEL", transientDetails.ErrorMessage);

        var mixed = new AggregateException(new HttpRequestException("SECRET_SENTINEL"), new InvalidDataException("SECRET_SENTINEL"));
        await AssertSafePermanentFailureAsync(mixed);
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
        var canceled = await Assert.ThrowsAsync<OperationCanceledException>(() => WorkflowFunctions.RetryableActivity<int>(
            () => Task.FromException<int>(new OperationCanceledException("SECRET_SENTINEL", caller.Token)), caller.Token));
        Assert.Equal("Workflow activity was canceled.", canceled.Message);
        Assert.Equal(caller.Token, canceled.CancellationToken);
        Assert.Null(canceled.InnerException);
        var cancellationDetails = TaskFailureDetails.FromException(canceled);
        Assert.Null(cancellationDetails.InnerFailure);
        Assert.DoesNotContain("SECRET_SENTINEL", cancellationDetails.ErrorMessage);
    }

    [Fact]
    public async Task ActivityTimeoutKeepsTimeoutTypeWithoutPersistingRawDetails()
    {
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => WorkflowFunctions.RetryableActivity<int>(
            () => Task.FromException<int>(new TimeoutException("SECRET_SENTINEL")), TestContext.Current.CancellationToken));

        Assert.Equal("Workflow activity timed out.", timeout.Message);
        Assert.Null(timeout.InnerException);
        var details = TaskFailureDetails.FromException(timeout);
        Assert.True(details.IsCausedBy<TimeoutException>());
        Assert.Null(details.InnerFailure);
        Assert.DoesNotContain("SECRET_SENTINEL", details.ErrorMessage);
    }

    [Fact]
    public async Task RetryUsesDurableBackoffAndStopsAtAttemptLimitOrDeadline()
    {
        var context = Context();
        var now = DateTime.UtcNow;
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(() => now);
        context.Setup(c => c.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns((DateTime date, CancellationToken _) => { now = date; return Task.CompletedTask; });
        var safeTransient = new TransientWorkflowException("Workflow activity temporarily failed.");
        var failure = TaskFailureDetails.FromException(safeTransient);
        Assert.Null(failure.InnerFailure);
        var handler = WorkflowFunctions.RetryOptions(now.AddMinutes(1)).Retry!.Handler!;
        Assert.True(await handler(new RetryContext(context.Object, 1, failure, TimeSpan.Zero, CancellationToken.None)));
        context.Verify(c => c.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(await handler(new RetryContext(context.Object, 3, failure, TimeSpan.Zero, CancellationToken.None)));
        var expires = WorkflowFunctions.RetryOptions(now.AddSeconds(4)).Retry!.Handler!;
        Assert.False(await expires(new RetryContext(context.Object, 1, failure, TimeSpan.Zero, CancellationToken.None)));
        var permanent = TaskFailureDetails.FromException(new PermanentWorkflowException());
        Assert.False(await handler(new RetryContext(context.Object, 1, permanent, TimeSpan.Zero, CancellationToken.None)));
    }

    private static async Task AssertSafePermanentFailureAsync(Exception original)
    {
        var failure = await Assert.ThrowsAsync<PermanentWorkflowException>(() => WorkflowFunctions.RetryableActivity<int>(
            () => Task.FromException<int>(original), TestContext.Current.CancellationToken));

        Assert.Equal("Workflow activity failed.", failure.Message);
        Assert.Null(failure.InnerException);
        var details = TaskFailureDetails.FromException(failure);
        Assert.True(details.IsCausedBy<PermanentWorkflowException>());
        Assert.False(details.IsCausedBy<TransientWorkflowException>());
        Assert.Equal(failure.Message, details.ErrorMessage);
        Assert.Null(details.InnerFailure);
        Assert.DoesNotContain("SECRET_SENTINEL", details.ErrorMessage);
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
        if (stopFails) stopping.ThrowsAsync(new WorkflowConflictException("SECRET_SENTINEL"));
        else stopping.ReturnsAsync(new BatchJobTerminationStatus("Completed", true));
        var statuses = new List<string>();
        context.Setup(c => c.SetCustomStatus(It.IsAny<object>())).Callback<object>(s => statuses.Add(System.Text.Json.JsonSerializer.Serialize(s)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowFunctions.MainOrchestrator(context.Object));
        context.Verify(c => c.CallActivityAsync<ManifestReference>(nameof(WorkflowFunctions.PublishManifestActivity), It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
        Assert.Contains("TimedOut", statuses.Last());
        Assert.Contains(stopFails ? "batch_termination_failed" : "Completed", statuses.Last());
        Assert.DoesNotContain("SECRET_SENTINEL", statuses.Last());
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

    [Fact]
    public async Task PermanentEnsureJobFailureAtDeadlineIsNotSwallowedAndNoTerminationIsRequested()
    {
        var request = new WorkflowRequest("conflict-at-deadline");
        var plan = Plan(request);
        var context = Context();
        var start = DateTime.UtcNow;
        var reads = 0;
        // First read computes the deadline; later reads are past it.
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(() => reads++ == 0 ? start : start.AddHours(1));
        context.Setup(c => c.GetInput<WorkflowRequest>()).Returns(request);
        context.Setup(c => c.CallActivityAsync<WorkflowPlan>(nameof(WorkflowFunctions.ClassifyActivity), request, It.IsAny<TaskOptions>())).ReturnsAsync(plan);
        var details = new TaskFailureDetails(typeof(WorkflowConflictException).FullName!, "foreign job", null, null, null);
        context.Setup(c => c.CallActivityAsync(nameof(WorkflowFunctions.EnsureBatchJobActivity), plan, It.IsAny<TaskOptions>()))
            .ThrowsAsync(new TaskFailedException(nameof(WorkflowFunctions.EnsureBatchJobActivity), 1, details));
        context.Setup(c => c.SetCustomStatus(It.IsAny<object>()));

        await Assert.ThrowsAsync<TaskFailedException>(() => WorkflowFunctions.MainOrchestrator(context.Object));
        context.Verify(c => c.CallActivityAsync<BatchJobTerminationStatus>(nameof(WorkflowFunctions.TerminateBatchJobActivity), It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
        context.Verify(c => c.CallSubOrchestratorAsync<TaskMonitorResult>(nameof(WorkflowFunctions.MonitorBatchTaskOrchestrator), It.IsAny<object>(), null), Times.Never);
    }

    [Theory]
    [InlineData(Grpc.Core.StatusCode.Unavailable, true)]
    [InlineData(Grpc.Core.StatusCode.DeadlineExceeded, true)]
    [InlineData(Grpc.Core.StatusCode.ResourceExhausted, true)]
    [InlineData(Grpc.Core.StatusCode.Cancelled, false)]
    [InlineData(Grpc.Core.StatusCode.Unknown, false)]
    [InlineData(Grpc.Core.StatusCode.InvalidArgument, false)]
    [InlineData(Grpc.Core.StatusCode.NotFound, false)]
    [InlineData(Grpc.Core.StatusCode.AlreadyExists, false)]
    [InlineData(Grpc.Core.StatusCode.PermissionDenied, false)]
    [InlineData(Grpc.Core.StatusCode.Unauthenticated, false)]
    [InlineData(Grpc.Core.StatusCode.FailedPrecondition, false)]
    [InlineData(Grpc.Core.StatusCode.Aborted, false)]
    [InlineData(Grpc.Core.StatusCode.OutOfRange, false)]
    [InlineData(Grpc.Core.StatusCode.Unimplemented, false)]
    [InlineData(Grpc.Core.StatusCode.Internal, false)]
    [InlineData(Grpc.Core.StatusCode.DataLoss, false)]
    public async Task GrpcActivityRetryUsesOnlyExplicitlyAllowedStatusCodes(Grpc.Core.StatusCode code, bool expectedTransient)
    {
        var rpc = new Grpc.Core.RpcException(new Grpc.Core.Status(code, "SECRET_SENTINEL"));
        await AssertGrpcRetryBoundaryAsync(rpc, expectedTransient);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        Assert.False(WorkflowFunctions.IsTransientFailure(rpc, caller.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NestedGrpcAggregatesRetryOnlyWhenEveryFailureIsAllowed(bool allTransient)
    {
        var failures = new AggregateException(
            new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "SECRET_SENTINEL")),
            new AggregateException(
                new RequestFailedException(503, "SECRET_SENTINEL"),
                new Grpc.Core.RpcException(new Grpc.Core.Status(allTransient
                    ? Grpc.Core.StatusCode.ResourceExhausted : Grpc.Core.StatusCode.PermissionDenied, "SECRET_SENTINEL"))));
        await AssertGrpcRetryBoundaryAsync(failures, allTransient);
    }

    private static async Task AssertGrpcRetryBoundaryAsync(Exception original, bool expectedTransient)
    {
        Assert.Equal(expectedTransient, WorkflowFunctions.IsTransientFailure(original, TestContext.Current.CancellationToken));
        var failure = await Record.ExceptionAsync(() => WorkflowFunctions.RetryableActivity<int>(
            () => Task.FromException<int>(original), TestContext.Current.CancellationToken));
        Assert.NotNull(failure);
        if (expectedTransient)
        {
            Assert.IsType<TransientWorkflowException>(failure);
            Assert.Equal("Workflow activity temporarily failed.", failure.Message);
        }
        else
        {
            Assert.IsType<PermanentWorkflowException>(failure);
            Assert.Equal("Workflow activity failed.", failure.Message);
        }
        Assert.Null(failure.InnerException);
        Assert.Empty(failure.Data);
        var details = TaskFailureDetails.FromException(failure);
        Assert.Equal(expectedTransient, details.IsCausedBy<TransientWorkflowException>());
        Assert.Null(details.InnerFailure);
        Assert.DoesNotContain("SECRET_SENTINEL", details.ErrorMessage);

        var context = Context();
        var now = DateTime.UtcNow;
        context.SetupGet(c => c.CurrentUtcDateTime).Returns(now);
        context.Setup(c => c.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var handler = WorkflowFunctions.RetryOptions(now.AddMinutes(1)).Retry!.Handler!;
        Assert.Equal(expectedTransient, await handler(new RetryContext(context.Object, 1, details, TimeSpan.Zero, CancellationToken.None)));
        context.Verify(c => c.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            expectedTransient ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task TerminateTreatsMissingJobAsCompleted()
    {
        var plan = Plan(new WorkflowRequest("stop-404"));
        var client = new Mock<BatchClient>();
        client.Setup(c => c.GetJobAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "gone", "JobNotFound", null));
        var status = await new AzureBatchGateway(client.Object, "pool").TerminateJobAsync(plan, TestContext.Current.CancellationToken);
        Assert.True(status.Completed);
    }

    [Fact]
    public async Task PreconditionFailureOnStillActiveJobReportsNotCompletedSoPollingRetries()
    {
        var plan = Plan(new WorkflowRequest("stop-412-active"));
        var client = new Mock<BatchClient>();
        client.Setup(c => c.GetJobAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(Job(plan, BatchJobState.Active, "pool", plan.Fingerprint), Mock.Of<Response>()));
        client.Setup(c => c.TerminateJobAsync(WaitUntil.Started, plan.JobId, It.IsAny<BatchJobTerminateOptions>(), null, null, null, It.IsAny<RequestConditions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(412, "etag changed"));
        Assert.False((await new AzureBatchGateway(client.Object, "pool").TerminateJobAsync(plan, TestContext.Current.CancellationToken)).Completed);
    }

    [Fact]
    public async Task BlobPutIfAbsentCreatesAndIsIdempotentButRejectsDifferentContent()
    {
        var value = new { name = "a", value = 1 };
        var same = BinaryData.FromObjectAsJson(value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)).ToString();
        const string path = "runs/poc-test/result.json";

        var created = await Repository(new FakeBlobHandler(201, null)).PutIfAbsentAsync(path, value, TestContext.Current.CancellationToken);
        Assert.EndsWith(path, created);
        await Repository(new FakeBlobHandler(409, same)).PutIfAbsentAsync(path, value, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkflowConflictException>(() =>
            Repository(new FakeBlobHandler(409, "{\"name\":\"other\"}")).PutIfAbsentAsync(path, value, TestContext.Current.CancellationToken));
    }

    private static BlobResultRepository Repository(HttpMessageHandler handler) => new(new Azure.Storage.Blobs.BlobContainerClient(
        new Uri("https://test.invalid/poc-results"),
        new Azure.Storage.Blobs.BlobClientOptions
        {
            Transport = new Azure.Core.Pipeline.HttpClientTransport(new HttpClient(handler)),
            Retry = { MaxRetries = 0 }
        }));

    // Test-only fake Blob endpoint: PUT answers with the configured status, GET returns existing content.
    private sealed class FakeBlobHandler(int putStatus, string? existing) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            if (request.Method == HttpMethod.Put)
            {
                response = new HttpResponseMessage((System.Net.HttpStatusCode)putStatus) { Content = new ByteArrayContent([]) };
                if (putStatus == 409) response.Headers.Add("x-ms-error-code", "BlobAlreadyExists");
            }
            else
            {
                response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(existing ?? "") };
                response.Headers.Add("x-ms-blob-type", "BlockBlob");
            }
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"0x1\"");
            response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
            return Task.FromResult(response);
        }
    }

    private static Mock<BatchClient> BatchReturning(BatchJob job)
    {
        var client = new Mock<BatchClient>();
        client.Setup(c => c.GetJobAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(job, Mock.Of<Response>()));
        return client;
    }
    [Fact]
    public async Task EnsureTaskHappyPathCreatesAndValidatesTask()
    {
        var plan = Plan(new WorkflowRequest("ensure-task-happy"));
        var spec = new TaskSpec(plan, 0);
        var client = BatchReturning(Job(plan, BatchJobState.Active, "pool", plan.Fingerprint));
        var command = WindowsTaskCommand.Build(spec.DelaySeconds, spec.InjectFailure);
        var task = TaskModel(spec.TaskId, BatchTaskState.Active, command, plan.Fingerprint);

        client.Setup(c => c.CreateTaskAsync(spec.Plan.JobId, It.IsAny<BatchTaskCreateOptions>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Response)null!);
        client.Setup(c => c.GetTaskAsync(spec.Plan.JobId, spec.TaskId, It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(task, Mock.Of<Response>()));

        var gateway = new AzureBatchGateway(client.Object, "pool");
        await gateway.EnsureTaskAsync(spec, TestContext.Current.CancellationToken);

        client.Verify(c => c.CreateTaskAsync(spec.Plan.JobId, It.Is<BatchTaskCreateOptions>(o => o.Id == spec.TaskId && o.CommandLine == command), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetTaskAsync(spec.Plan.JobId, spec.TaskId, It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureTaskIdempotentConflictWithMatchingFingerprintSucceeds()
    {
        var plan = Plan(new WorkflowRequest("ensure-task-idempotent"));
        var spec = new TaskSpec(plan, 0);
        var client = BatchReturning(Job(plan, BatchJobState.Active, "pool", plan.Fingerprint));
        var command = WindowsTaskCommand.Build(spec.DelaySeconds, spec.InjectFailure);
        var task = TaskModel(spec.TaskId, BatchTaskState.Active, command, plan.Fingerprint);

        client.Setup(c => c.CreateTaskAsync(spec.Plan.JobId, It.IsAny<BatchTaskCreateOptions>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(409, "Task exists", "TaskExists", null));
        client.Setup(c => c.GetTaskAsync(spec.Plan.JobId, spec.TaskId, It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(task, Mock.Of<Response>()));

        var gateway = new AzureBatchGateway(client.Object, "pool");
        await gateway.EnsureTaskAsync(spec, TestContext.Current.CancellationToken);

        client.Verify(c => c.CreateTaskAsync(spec.Plan.JobId, It.IsAny<BatchTaskCreateOptions>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetTaskAsync(spec.Plan.JobId, spec.TaskId, It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true, "foreign-fingerprint", null)]
    [InlineData(false, "foreign-fingerprint", null)]
    [InlineData(true, "match", "cmd /c exit 99")]
    public async Task EnsureTaskDifferentInputConflictThrowsWorkflowConflictException(bool simulate409Conflict, string fingerprintMode, string? overrideCommand)
    {
        var plan = Plan(new WorkflowRequest("ensure-task-conflict"));
        var spec = new TaskSpec(plan, 0);
        var client = BatchReturning(Job(plan, BatchJobState.Active, "pool", plan.Fingerprint));
        var command = overrideCommand ?? WindowsTaskCommand.Build(spec.DelaySeconds, spec.InjectFailure);
        var taskFingerprint = fingerprintMode == "match" ? plan.Fingerprint : "foreign-fingerprint";
        var task = TaskModel(spec.TaskId, BatchTaskState.Active, command, taskFingerprint);

        var createSetup = client.Setup(c => c.CreateTaskAsync(spec.Plan.JobId, It.IsAny<BatchTaskCreateOptions>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()));
        if (simulate409Conflict)
            createSetup.ThrowsAsync(new RequestFailedException(409, "Task exists", "TaskExists", null));
        else
            createSetup.ReturnsAsync((Response)null!);

        client.Setup(c => c.GetTaskAsync(spec.Plan.JobId, spec.TaskId, It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(task, Mock.Of<Response>()));

        var gateway = new AzureBatchGateway(client.Object, "pool");
        await Assert.ThrowsAsync<WorkflowConflictException>(() => gateway.EnsureTaskAsync(spec, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsureTaskDeadlineExceededBeforeJobCheckThrowsTimeoutException()
    {
        var plan = Plan(new WorkflowRequest("deadline-before-check"));
        var spec = new TaskSpec(plan, 0, DateTime.UtcNow.AddSeconds(-1));
        var client = new Mock<BatchClient>();
        var gateway = new AzureBatchGateway(client.Object, "pool");

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => gateway.EnsureTaskAsync(spec, TestContext.Current.CancellationToken));
        Assert.Equal("Task submission deadline exceeded.", ex.Message);
        client.Verify(c => c.GetJobAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureTaskDeadlineExceededDuringJobCheckThrowsTimeoutException()
    {
        var plan = Plan(new WorkflowRequest("deadline-during-check"));
        var spec = new TaskSpec(plan, 0, DateTime.UtcNow.AddMilliseconds(50));
        var job = Job(plan, BatchJobState.Active, "pool", plan.Fingerprint);
        var client = new Mock<BatchClient>();
        client.Setup(c => c.GetJobAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(100);
                return Response.FromValue(job, Mock.Of<Response>());
            });

        var gateway = new AzureBatchGateway(client.Object, "pool");
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => gateway.EnsureTaskAsync(spec, TestContext.Current.CancellationToken));
        Assert.Equal("Task submission deadline exceeded while checking the job.", ex.Message);
        client.Verify(c => c.CreateTaskAsync(It.IsAny<string>(), It.IsAny<BatchTaskCreateOptions>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetStatusAsyncCompletedSuccessReturnsSuccessStatus()
    {
        var plan = Plan(new WorkflowRequest("status-success"));
        var spec = new TaskSpec(plan, 0);
        var command = WindowsTaskCommand.Build(spec.DelaySeconds, spec.InjectFailure);
        var execution = ExecutionInfo(exitCode: 0, result: BatchTaskExecutionResult.Success);
        var task = TaskModel(spec.TaskId, BatchTaskState.Completed, command, plan.Fingerprint, execution: execution);
        var client = new Mock<BatchClient>();
        client.Setup(c => c.GetTaskAsync(spec.Plan.JobId, spec.TaskId, It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(task, Mock.Of<Response>()));

        var gateway = new AzureBatchGateway(client.Object, "pool");
        var status = await gateway.GetStatusAsync(spec, TestContext.Current.CancellationToken);

        Assert.Equal(spec.TaskId, status.TaskId);
        Assert.Equal(BatchTaskState.Completed.ToString(), status.State);
        Assert.True(status.Completed);
        Assert.True(status.Succeeded);
        Assert.Equal(0, status.ExitCode);
        Assert.Null(status.Failure);
    }

    [Fact]
    public async Task GetStatusAsyncCompletedFailureReturnsFailureStatus()
    {
        var plan = Plan(new WorkflowRequest("status-fail", FailTaskIndex: 0));
        var spec = new TaskSpec(plan, 0);
        var command = WindowsTaskCommand.Build(spec.DelaySeconds, spec.InjectFailure);
        var execution = ExecutionInfo(exitCode: 42, result: BatchTaskExecutionResult.Failure);
        var task = TaskModel(spec.TaskId, BatchTaskState.Completed, command, plan.Fingerprint, execution: execution);
        var client = new Mock<BatchClient>();
        client.Setup(c => c.GetTaskAsync(spec.Plan.JobId, spec.TaskId, It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(task, Mock.Of<Response>()));

        var gateway = new AzureBatchGateway(client.Object, "pool");
        var status = await gateway.GetStatusAsync(spec, TestContext.Current.CancellationToken);

        Assert.Equal(spec.TaskId, status.TaskId);
        Assert.Equal(BatchTaskState.Completed.ToString(), status.State);
        Assert.True(status.Completed);
        Assert.False(status.Succeeded);
        Assert.Equal(42, status.ExitCode);
        Assert.Equal($"Result={BatchTaskExecutionResult.Failure}; ExitCode=42", status.Failure);
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Active")]
    [InlineData("Preparing")]
    public async Task GetStatusAsyncStillRunningReturnsIncompleteStatus(string stateName)
    {
        var plan = Plan(new WorkflowRequest("status-running"));
        var spec = new TaskSpec(plan, 0);
        var command = WindowsTaskCommand.Build(spec.DelaySeconds, spec.InjectFailure);
        var runningState = new BatchTaskState(stateName);
        var task = TaskModel(spec.TaskId, runningState, command, plan.Fingerprint);
        var client = new Mock<BatchClient>();
        client.Setup(c => c.GetTaskAsync(spec.Plan.JobId, spec.TaskId, It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(task, Mock.Of<Response>()));

        var gateway = new AzureBatchGateway(client.Object, "pool");
        var status = await gateway.GetStatusAsync(spec, TestContext.Current.CancellationToken);

        Assert.Equal(spec.TaskId, status.TaskId);
        Assert.Equal(stateName, status.State);
        Assert.False(status.Completed);
        Assert.False(status.Succeeded);
        Assert.Null(status.ExitCode);
        Assert.Null(status.Failure);
    }

    [Fact]
    public async Task ReadResultAsyncDiscoversBackslashPathAndReadsFile()
    {
        var plan = Plan(new WorkflowRequest("read-backslash"));
        var spec = new TaskSpec(plan, 0);
        var client = new Mock<BatchClient>();

        var file = ModelReaderWriter.Read<BatchNodeFile>(BinaryData.FromString("""
        {
          "name": "wd\\result.txt",
          "isDirectory": false
        }
        """))!;
        var page = Azure.Page<BatchNodeFile>.FromValues([file], null, Mock.Of<Response>());
        client.Setup(c => c.GetTaskFilesAsync(spec.Plan.JobId, spec.TaskId, null, null, null, null, true, It.IsAny<CancellationToken>()))
            .Returns(Azure.AsyncPageable<BatchNodeFile>.FromPages([page]));

        var response = Response.FromValue(BinaryData.FromString("42\r\n"), Mock.Of<Response>());
        client.Setup(c => c.GetTaskFileAsync(spec.Plan.JobId, spec.TaskId, "wd\\result.txt", It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), "bytes=0-4096", It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var gateway = new AzureBatchGateway(client.Object, "pool");
        var result = await gateway.ReadResultAsync(spec, TestContext.Current.CancellationToken);
        Assert.Equal("42\r\n", result);
    }

    [Fact]
    public async Task ReadResultAsyncConverts416ToInvalidDataException()
    {
        var plan = Plan(new WorkflowRequest("read-416"));
        var spec = new TaskSpec(plan, 0);
        var client = new Mock<BatchClient>();

        var file = ModelReaderWriter.Read<BatchNodeFile>(BinaryData.FromString("""
        {
          "name": "wd/result.txt",
          "isDirectory": false
        }
        """))!;
        var page = Azure.Page<BatchNodeFile>.FromValues([file], null, Mock.Of<Response>());
        client.Setup(c => c.GetTaskFilesAsync(spec.Plan.JobId, spec.TaskId, null, null, null, null, true, It.IsAny<CancellationToken>()))
            .Returns(Azure.AsyncPageable<BatchNodeFile>.FromPages([page]));

        client.Setup(c => c.GetTaskFileAsync(spec.Plan.JobId, spec.TaskId, "wd/result.txt", It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), "bytes=0-4096", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(416, "Requested Range Not Satisfiable"));

        var gateway = new AzureBatchGateway(client.Object, "pool");
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => gateway.ReadResultAsync(spec, TestContext.Current.CancellationToken));
        Assert.Equal("Batch task output was empty.", ex.Message);
        Assert.IsType<RequestFailedException>(ex.InnerException);
        Assert.Equal(416, ((RequestFailedException)ex.InnerException).Status);
    }

    [Fact]
    public async Task ReadResultAsyncThrowsFileNotFoundExceptionWhenResultTxtMissing()
    {
        var plan = Plan(new WorkflowRequest("read-missing"));
        var spec = new TaskSpec(plan, 0);
        var client = new Mock<BatchClient>();

        var file = ModelReaderWriter.Read<BatchNodeFile>(BinaryData.FromString("""
        {
          "name": "stderr.txt",
          "isDirectory": false
        }
        """))!;
        var page = Azure.Page<BatchNodeFile>.FromValues([file], null, Mock.Of<Response>());
        client.Setup(c => c.GetTaskFilesAsync(spec.Plan.JobId, spec.TaskId, null, null, null, null, true, It.IsAny<CancellationToken>()))
            .Returns(Azure.AsyncPageable<BatchNodeFile>.FromPages([page]));

        var gateway = new AzureBatchGateway(client.Object, "pool");
        await Assert.ThrowsAsync<FileNotFoundException>(() => gateway.ReadResultAsync(spec, TestContext.Current.CancellationToken));
    }

    private static BatchJob Job(WorkflowPlan plan, BatchJobState state, string pool, string fingerprint) =>
        ComputeBatchModelFactory.BatchJob(id: plan.JobId, state: state, poolInfo: new BatchPoolInfo { PoolId = pool }, metadata: [new BatchMetadataItem("poc-fingerprint", fingerprint)]);
    private static BatchTaskExecutionInfo ExecutionInfo(int? exitCode, BatchTaskExecutionResult? result, BatchTaskFailureInfo? failureInfo = null) =>
        ComputeBatchModelFactory.BatchTaskExecutionInfo(exitCode: exitCode, result: result, failureInfo: failureInfo);
    private static BatchTask TaskModel(string id, BatchTaskState state, string commandLine, string fingerprint, int maxRetryCount = 0, BatchTaskExecutionInfo? execution = null) =>
        ComputeBatchModelFactory.BatchTask(
            id: id,
            state: state,
            commandLine: commandLine,
            constraints: new BatchTaskConstraints { MaxTaskRetryCount = maxRetryCount },
            environmentSettings: [new EnvironmentSetting("POC_FINGERPRINT") { Value = fingerprint }],
            executionInfo: execution);
    private static WorkflowPlan Plan(WorkflowRequest request) => new(request, "light", "Test", WorkflowIdentity.JobId(request.RequestId), WorkflowIdentity.Fingerprint(request, "light"), 10, 15);
    private static Mock<TaskOrchestrationContext> Context()
    {
        var context = new Mock<TaskOrchestrationContext>();
        context.Setup(c => c.CreateReplaySafeLogger(It.IsAny<string>())).Returns(NullLogger.Instance);
        return context;
    }
}
