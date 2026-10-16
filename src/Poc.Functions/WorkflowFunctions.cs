using System.Net;
using System.Text.Json;
using Azure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using Poc.Core;

namespace Poc.Functions;

public sealed class WorkflowFunctions(WorkflowOperations operations, WorkflowAdmission admission)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function(nameof(StartWorkflow))]
    public async Task<HttpResponseData> StartWorkflow(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "workflows")] HttpRequestData request,
        [DurableClient] DurableTaskClient client, CancellationToken cancellationToken)
    {
        WorkflowRequest input;
        try
        {
            input = await JsonSerializer.DeserializeAsync<WorkflowRequest>(request.Body, JsonOptions)
                ?? throw new ArgumentException("JSON request body is required.");
            input.Validate();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            var badRequest = request.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = ex.Message });
            return badRequest;
        }
        try
        {
            var instanceId = await admission.AdmitAsync(input, client, cancellationToken);
            return await AcceptedAsync(request, client, instanceId);
        }
        catch (WorkflowConflictException)
        {
            var conflict = request.CreateResponse(HttpStatusCode.Conflict);
            await conflict.WriteAsJsonAsync(new { error = "requestId already has different configuration.", instanceId = WorkflowIdentity.InstanceId(input.RequestId) });
            return conflict;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && (IsTransientFailure(ex, cancellationToken) || ex is
            Grpc.Core.RpcException { StatusCode: Grpc.Core.StatusCode.Unavailable or Grpc.Core.StatusCode.DeadlineExceeded or Grpc.Core.StatusCode.ResourceExhausted }))
        {
            var unavailable = request.CreateResponse(HttpStatusCode.ServiceUnavailable);
            unavailable.Headers.Add("Retry-After", "10");
            await unavailable.WriteAsJsonAsync(new { error = "Admission could not be confirmed. Retry with the same requestId and input." });
            return unavailable;
        }
    }

    private static async Task<HttpResponseData> AcceptedAsync(HttpRequestData request, DurableTaskClient client, string instanceId)
    {
        var management = client.CreateHttpManagementPayload(instanceId, request);
        var response = request.CreateResponse(HttpStatusCode.Accepted);
        response.Headers.Add("Location", management.StatusQueryGetUri);
        response.Headers.Add("Retry-After", "10");
        await response.WriteAsJsonAsync(new { id = instanceId, instanceId, statusQueryGetUri = management.StatusQueryGetUri });
        return response;
    }

    [Function(nameof(MainOrchestrator))]
    public static async Task<WorkflowOutput> MainOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<WorkflowRequest>() ?? throw new ArgumentException("Workflow input missing.");
        var logger = context.CreateReplaySafeLogger(nameof(MainOrchestrator));
        var terminalState = "Failed";
        BatchJobTerminationStatus? termination = null;
        string? terminationError = null;
        try
        {
            var plan = await context.CallActivityAsync<WorkflowPlan>(nameof(ClassifyActivity), input, RetryOptions());
            var deadline = context.CurrentUtcDateTime.AddMinutes(plan.TimeoutMinutes);
            logger.LogInformation("Request {RequestId}, job {JobId}, classification {Classification}: {Reason}",
                input.RequestId, plan.JobId, plan.Classification, plan.Reason);
            context.SetCustomStatus(new { state = "Submitting", plan.JobId, plan.Classification });
            // Do not abandon job creation: it could create a new job after cleanup saw no job.
            try { await context.CallActivityAsync(nameof(EnsureBatchJobActivity), plan, RetryOptions(deadline)); }
            catch (TaskFailedException ex) when (context.CurrentUtcDateTime >= deadline && ex.FailureDetails.IsCausedBy<TransientWorkflowException>())
            {
                // An ambiguous network failure may have created the job. Expired children trigger
                // identity-checked cleanup rather than submitting work after the deadline.
            }
            var monitors = Enumerable.Range(0, input.TaskCount).Select(index =>
                context.CallSubOrchestratorAsync<TaskMonitorResult>(nameof(MonitorBatchTaskOrchestrator), new TaskSpec(plan, index, deadline))).ToArray();
            // Terminal outcomes allow all siblings to finish before propagating a task failure.
            var results = await Task.WhenAll(monitors);
            var failures = results.Where(result => result.State != "Succeeded").ToArray();
            if (failures.Length > 0)
            {
                var state = failures.Any(result => result.State == "TimedOut") ? "TimedOut" : "Failed";
                terminalState = state;
                if (state == "TimedOut")
                {
                    var stopDeadline = context.CurrentUtcDateTime.AddMinutes(2);
                    try
                    {
                        do
                        {
                            termination = await BeforeDeadline(context,
                                context.CallActivityAsync<BatchJobTerminationStatus>(nameof(TerminateBatchJobActivity), plan, RetryOptions(stopDeadline)), stopDeadline);
                            context.SetCustomStatus(new { state = "Stopping", plan.JobId, termination });
                            if (termination.Completed) break;
                            var next = context.CurrentUtcDateTime.AddSeconds(5);
                            await context.CreateTimer(next < stopDeadline ? next : stopDeadline, CancellationToken.None);
                        } while (context.CurrentUtcDateTime < stopDeadline);
                        if (termination?.Completed != true) terminationError = "Batch job termination confirmation deadline exceeded.";
                    }
                    catch (Exception stopException) { terminationError = stopException.Message; }
                }
                context.SetCustomStatus(new { state, plan.JobId, tasks = results });
                throw new InvalidOperationException($"{state}: {string.Join("; ", failures.Select(result => $"{result.TaskId}: {result.Error}"))}");
            }
            context.SetCustomStatus(new { state = "Persisting", plan.JobId });
            await context.CallActivityAsync<ResultReference[]>(nameof(FetchAndPersistActivity), plan, RetryOptions());
            await context.CallActivityAsync<AggregateReference>(nameof(AggregateActivity), plan, RetryOptions());
            var manifest = await context.CallActivityAsync<ManifestReference>(nameof(PublishManifestActivity), plan, RetryOptions());
            context.SetCustomStatus(new { state = "Succeeded", plan.JobId, manifest.Uri });
            return new WorkflowOutput("Succeeded", input.RequestId, plan.JobId, manifest.Uri);
        }
        catch (Exception ex)
        {
            context.SetCustomStatus(new { state = terminalState, jobId = WorkflowIdentity.JobId(input.RequestId), error = ex.Message, termination, terminationError });
            logger.LogError("Request {RequestId} failed: {Error}", input.RequestId, ex.Message);
            throw;
        }
    }

    [Function(nameof(MonitorBatchTaskOrchestrator))]
    public static async Task<TaskMonitorResult> MonitorBatchTaskOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var spec = context.GetInput<TaskSpec>() ?? throw new ArgumentException("Task input missing.");
        var deadline = spec.DeadlineUtc ?? context.CurrentUtcDateTime.AddMinutes(spec.Plan.TimeoutMinutes);
        var logger = context.CreateReplaySafeLogger(nameof(MonitorBatchTaskOrchestrator));
        try
        {
            if (context.CurrentUtcDateTime >= deadline) return new(spec.TaskId, "TimedOut", "Shared monitoring deadline exceeded before submission.");
            await BeforeDeadline(context, context.CallActivityAsync(nameof(EnsureBatchTaskActivity), spec, RetryOptions(deadline)), deadline);
            while (context.CurrentUtcDateTime < deadline)
            {
                var status = await BeforeDeadline(context, context.CallActivityAsync<BatchTaskStatus>(nameof(GetBatchTaskStatusActivity), spec, RetryOptions(deadline)), deadline);
                logger.LogInformation("Job {JobId}, task {TaskId}: {State}, exit {ExitCode}",
                    spec.Plan.JobId, spec.TaskId, status.State, status.ExitCode);
                if (status.Completed)
                    return new TaskMonitorResult(spec.TaskId, status.Succeeded ? "Succeeded" : "Failed", status.Failure ?? (status.Succeeded ? null : $"Exit code {status.ExitCode}"));
                var next = context.CurrentUtcDateTime.AddSeconds(spec.Plan.PollSeconds);
                await context.CreateTimer(next < deadline ? next : deadline, CancellationToken.None);
            }
            return new TaskMonitorResult(spec.TaskId, "TimedOut", "Shared monitoring deadline exceeded; parent will terminate the Batch job.");
        }
        catch (TimeoutException ex) { return new(spec.TaskId, "TimedOut", ex.Message); }
        catch (TaskFailedException ex)
        {
            return new TaskMonitorResult(spec.TaskId, context.CurrentUtcDateTime >= deadline || ex.FailureDetails.IsCausedBy<TimeoutException>() ? "TimedOut" : "Failed", ex.Message);
        }
    }

    public static TaskOptions RetryOptions(DateTime? deadline = null) => TaskOptions.FromRetryHandler(async retry =>
    {
        if (retry.LastAttemptNumber >= 3 || !retry.LastFailure.IsCausedBy<TransientWorkflowException>()) return false;
        var next = retry.OrchestrationContext.CurrentUtcDateTime.AddSeconds(5 * retry.LastAttemptNumber);
        if (deadline is { } limit && next >= limit) return false;
        await retry.OrchestrationContext.CreateTimer(next, retry.CancellationToken);
        return deadline is null || retry.OrchestrationContext.CurrentUtcDateTime < deadline;
    });

    private static Task BeforeDeadline(TaskOrchestrationContext context, Task activity, DateTime deadline) =>
        BeforeDeadline(context, CompleteActivity(activity), deadline);

    private static async Task<bool> CompleteActivity(Task activity) { await activity; return true; }

    private static async Task<T> BeforeDeadline<T>(TaskOrchestrationContext context, Task<T> activity, DateTime deadline)
    {
        using var cancellation = new CancellationTokenSource();
        var timer = context.CreateTimer(deadline, cancellation.Token);
        var completed = await Task.WhenAny(activity, timer);
        if (completed == timer) throw new TimeoutException("Shared monitoring deadline exceeded during an Activity.");
        cancellation.Cancel();
        // A failed Activity must not be masked as a timeout: permanent failures keep their cause.
        if (completed == activity && activity.IsFaulted) return await activity;
        if (context.CurrentUtcDateTime >= deadline) throw new TimeoutException("Shared monitoring deadline exceeded during an Activity.");
        return await activity;
    }

    public static async Task<T> RetryableActivity<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        try { return await operation(); }
        catch (Exception ex) when (IsTransientFailure(ex, cancellationToken))
        { throw new TransientWorkflowException(ex.Message, ex); }
    }
    public static bool IsTransientFailure(Exception error, CancellationToken cancellationToken = default) => !cancellationToken.IsCancellationRequested && error switch
    {
        AggregateException aggregate => aggregate.Flatten().InnerExceptions is { Count: > 0 } errors && errors.All(inner => IsTransientFailure(inner, cancellationToken)),
        // Azure.Core uses a linked timeout token. The caller's token distinguishes host cancellation.
        OperationCanceledException => true,
        RequestFailedException { Status: 0 or 408 or 429 or 500 or 502 or 503 or 504 } => true,
        Grpc.Core.RpcException => true,
        HttpRequestException { StatusCode: null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout } => true,
        _ => false
    };
    private static async Task RetryableActivity(Func<Task> operation, CancellationToken cancellationToken) =>
        await RetryableActivity(async () => { await operation(); return true; }, cancellationToken);

    [Function(nameof(ClassifyActivity))]
    public Task<WorkflowPlan> ClassifyActivity([ActivityTrigger] WorkflowRequest input, CancellationToken cancellationToken) => RetryableActivity(() => operations.ClassifyAsync(input, cancellationToken), cancellationToken);
    [Function(nameof(EnsureBatchJobActivity))]
    public Task EnsureBatchJobActivity([ActivityTrigger] WorkflowPlan plan, CancellationToken cancellationToken) => RetryableActivity(() => operations.EnsureJobAsync(plan, cancellationToken), cancellationToken);
    [Function(nameof(TerminateBatchJobActivity))]
    public Task<BatchJobTerminationStatus> TerminateBatchJobActivity([ActivityTrigger] WorkflowPlan plan, CancellationToken cancellationToken) => RetryableActivity(() => operations.TerminateJobAsync(plan, cancellationToken), cancellationToken);
    [Function(nameof(EnsureBatchTaskActivity))]
    public Task EnsureBatchTaskActivity([ActivityTrigger] TaskSpec spec, CancellationToken cancellationToken) => RetryableActivity(() => operations.EnsureTaskAsync(spec, cancellationToken), cancellationToken);
    [Function(nameof(GetBatchTaskStatusActivity))]
    public Task<BatchTaskStatus> GetBatchTaskStatusActivity([ActivityTrigger] TaskSpec spec, CancellationToken cancellationToken) => RetryableActivity(() => operations.GetStatusAsync(spec, cancellationToken), cancellationToken);
    [Function(nameof(FetchAndPersistActivity))]
    public Task<ResultReference[]> FetchAndPersistActivity([ActivityTrigger] WorkflowPlan plan, CancellationToken cancellationToken) => RetryableActivity(() => operations.FetchAndPersistAsync(plan, cancellationToken), cancellationToken);
    [Function(nameof(AggregateActivity))]
    public Task<AggregateReference> AggregateActivity([ActivityTrigger] WorkflowPlan plan, CancellationToken cancellationToken) => RetryableActivity(() => operations.AggregateAsync(plan, cancellationToken), cancellationToken);
    [Function(nameof(PublishManifestActivity))]
    public Task<ManifestReference> PublishManifestActivity([ActivityTrigger] WorkflowPlan plan, CancellationToken cancellationToken) => RetryableActivity(() => operations.PublishManifestAsync(plan, cancellationToken), cancellationToken);
}

public sealed record TaskMonitorResult(string TaskId, string State, string? Error);
public sealed record WorkflowOutput(string State, string RequestId, string JobId, string ManifestUri);
