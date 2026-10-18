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
    private const string InvalidRequestMessage = "Workflow request is invalid.";
    private const string WorkflowFailedMessage = "Workflow execution failed.";
    private const string WorkflowTimedOutMessage = "Workflow exceeded its monitoring deadline.";
    private const string TerminationUnconfirmedMessage = "Batch job termination could not be confirmed.";
    private const string IntegrityFailureMessage = "Published workflow results are inconsistent.";
    private const string StatusUnavailableMessage = "Workflow status could not be verified. Retry later.";
    private const string ResultUnavailableMessage = "Workflow result could not be verified. Retry later.";

    private static WorkflowOwner? ExtractOwner(HttpRequestData request)
    {
        string? ownerId = request.Headers.TryGetValues("X-Poc-Owner-Id", out var ownerVals) ? ownerVals.FirstOrDefault() : null;
        string? orgId = request.Headers.TryGetValues("X-Poc-Org-Id", out var orgVals) ? orgVals.FirstOrDefault() : null;

        if (ownerId is not null)
        {
            var owner = new WorkflowOwner(ownerId, orgId);
            owner.Validate();
            return owner;
        }
        if (orgId is not null)
        {
            throw new ArgumentException("X-Poc-Owner-Id header is required when X-Poc-Org-Id is specified.");
        }
        return null;
    }

    [Function(nameof(StartWorkflow))]
    public async Task<HttpResponseData> StartWorkflow(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "workflows")] HttpRequestData request,
        [DurableClient] DurableTaskClient client, CancellationToken cancellationToken)
    {
        WorkflowRequest input;
        WorkflowOwner? owner;
        try
        {
            owner = ExtractOwner(request);
            input = await JsonSerializer.DeserializeAsync<WorkflowRequest>(request.Body, JsonOptions)
                ?? throw new ArgumentException("JSON request body is required.");
            input.Validate();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            var badRequest = request.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = InvalidRequestMessage });
            return badRequest;
        }
        try
        {
            var instanceId = await admission.AdmitAsync(input, client, owner, cancellationToken);
            return await AcceptedAsync(request, instanceId, input.RequestId);
        }
        catch (WorkflowForbiddenException)
        {
            var forbidden = request.CreateResponse(HttpStatusCode.Forbidden);
            await forbidden.WriteAsJsonAsync(new { error = "Caller is not authorized to access this workflow." });
            return forbidden;
        }
        catch (WorkflowConflictException)
        {
            var conflict = request.CreateResponse(HttpStatusCode.Conflict);
            await conflict.WriteAsJsonAsync(new { error = "requestId already has different configuration.", instanceId = WorkflowIdentity.InstanceId(input.RequestId) });
            return conflict;
        }
        catch (Exception ex) when (IsTransientRequestFailure(ex, cancellationToken))
        {
            var unavailable = request.CreateResponse(HttpStatusCode.ServiceUnavailable);
            unavailable.Headers.Add("Retry-After", "10");
            await unavailable.WriteAsJsonAsync(new { error = "Admission could not be confirmed. Retry with the same requestId and input." });
            return unavailable;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await CreateErrorResponseAsync(request, HttpStatusCode.InternalServerError, "Workflow admission could not be confirmed.");
        }
    }

    public static WorkflowStatusResponse NormalizeStatus(string requestId, OrchestrationMetadata? instance)
    {
        var instanceId = WorkflowIdentity.InstanceId(requestId);
        var jobId = WorkflowIdentity.JobId(requestId);

        if (instance is null || instance.RuntimeStatus == OrchestrationRuntimeStatus.Pending)
        {
            return new WorkflowStatusResponse(requestId, instanceId, jobId, "Accepted", ResultAvailable: false);
        }

        string? customState = null;
        bool terminationFailed = false;
        bool? terminationCompleted = null;

        if (!string.IsNullOrWhiteSpace(instance.SerializedCustomStatus))
        {
            try
            {
                using var doc = JsonDocument.Parse(instance.SerializedCustomStatus);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("state", out var stateEl) || root.TryGetProperty("State", out stateEl))
                    {
                        if (stateEl.ValueKind == JsonValueKind.String) customState = stateEl.GetString();
                    }
                    if (root.TryGetProperty("terminationError", out var termErrEl) || root.TryGetProperty("TerminationError", out termErrEl))
                    {
                        terminationFailed = termErrEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(termErrEl.GetString());
                    }
                    if (root.TryGetProperty("terminationErrorCode", out var termErrCodeEl) || root.TryGetProperty("TerminationErrorCode", out termErrCodeEl))
                    {
                        terminationFailed |= termErrCodeEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(termErrCodeEl.GetString());
                    }
                    if (root.TryGetProperty("termination", out var termEl) || root.TryGetProperty("Termination", out termEl))
                    {
                        if (termEl.ValueKind == JsonValueKind.Object)
                        {
                            if (termEl.TryGetProperty("completed", out var compEl) || termEl.TryGetProperty("Completed", out compEl))
                            {
                                if (compEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                                    terminationCompleted = compEl.GetBoolean();
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        if (instance.RuntimeStatus == OrchestrationRuntimeStatus.Running)
        {
            var mappedStatus = customState switch
            {
                "Submitting" => "Preparing",
                "Persisting" => "Publishing",
                "Stopping" => "Stopping",
                "Cancelling" => "Cancelling",
                _ => "Running"
            };
            bool? terminationConfirmed = mappedStatus == "Stopping" ? (terminationCompleted ?? false) : null;
            return new WorkflowStatusResponse(requestId, instanceId, jobId, mappedStatus, ResultAvailable: false, TerminationConfirmed: terminationConfirmed);
        }

        if (instance.RuntimeStatus == OrchestrationRuntimeStatus.Completed)
        {
            return new WorkflowStatusResponse(requestId, instanceId, jobId, "Succeeded", ResultAvailable: false);
        }

        if (instance.RuntimeStatus == OrchestrationRuntimeStatus.Failed)
        {
            if (customState == "TimedOut")
            {
                bool confirmed = (terminationCompleted == true) && !terminationFailed;
                return new WorkflowStatusResponse(requestId, instanceId, jobId, "TimedOut", ResultAvailable: false,
                    TerminationConfirmed: confirmed, Error: terminationFailed ? TerminationUnconfirmedMessage : WorkflowTimedOutMessage);
            }
            return new WorkflowStatusResponse(requestId, instanceId, jobId, "Failed", ResultAvailable: false, Error: WorkflowFailedMessage);
        }

#pragma warning disable CS0618
        if (instance.RuntimeStatus is OrchestrationRuntimeStatus.Terminated or OrchestrationRuntimeStatus.Canceled)
#pragma warning restore CS0618
        {
            return new WorkflowStatusResponse(requestId, instanceId, jobId, "Cancelled", ResultAvailable: false);
        }

        return new WorkflowStatusResponse(requestId, instanceId, jobId, "Running", ResultAvailable: false);
    }

    [Function(nameof(GetWorkflowStatus))]
    public async Task<HttpResponseData> GetWorkflowStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "workflows/{requestId}")] HttpRequestData request,
        string requestId,
        [DurableClient] DurableTaskClient client,
        CancellationToken cancellationToken)
    {
        WorkflowOwner? caller;
        try
        {
            new WorkflowRequest(requestId).Validate();
            caller = ExtractOwner(request);
        }
        catch (ArgumentException)
        {
            var badRequest = request.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = InvalidRequestMessage });
            return badRequest;
        }

        try
        {
            if (!await admission.ExistsAsync(requestId, client, cancellationToken))
            {
                var notFound = request.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = "Workflow not found.", requestId });
                return notFound;
            }

            await admission.AuthorizeAccessAsync(requestId, caller, cancellationToken);
            var instance = await client.GetInstanceAsync(WorkflowIdentity.InstanceId(requestId), getInputsAndOutputs: true, cancellationToken);
            var normalized = NormalizeStatus(requestId, instance);
            var publishedResult = await operations.TryGetPublishedResultAsync(requestId, cancellationToken);
            normalized = normalized with { ResultAvailable = publishedResult is not null };

            var response = request.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(normalized);
            return response;
        }
        catch (WorkflowForbiddenException)
        {
            var forbidden = request.CreateResponse(HttpStatusCode.Forbidden);
            await forbidden.WriteAsJsonAsync(new { error = "Caller is not authorized to access this workflow." });
            return forbidden;
        }
        catch (InvalidDataException)
        {
            return await CreateErrorResponseAsync(request, HttpStatusCode.InternalServerError, IntegrityFailureMessage);
        }
        catch (Exception ex) when (IsTransientRequestFailure(ex, cancellationToken))
        {
            return await CreateUnavailableResponseAsync(request, StatusUnavailableMessage);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await CreateErrorResponseAsync(request, HttpStatusCode.InternalServerError, "Workflow status could not be verified.");
        }
    }

    [Function(nameof(GetWorkflowResult))]
    public async Task<HttpResponseData> GetWorkflowResult(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "workflows/{requestId}/result")] HttpRequestData request,
        string requestId,
        [DurableClient] DurableTaskClient client,
        CancellationToken cancellationToken)
    {
        WorkflowOwner? caller;
        try
        {
            new WorkflowRequest(requestId).Validate();
            caller = ExtractOwner(request);
        }
        catch (ArgumentException)
        {
            var badRequest = request.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = InvalidRequestMessage });
            return badRequest;
        }

        try
        {
            if (!await admission.ExistsAsync(requestId, client, cancellationToken))
            {
                var notFound = request.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = "Workflow not found.", requestId });
                return notFound;
            }

            await admission.AuthorizeAccessAsync(requestId, caller, cancellationToken);
            var result = await operations.TryGetPublishedResultAsync(requestId, cancellationToken);
            if (result is null)
            {
                var instance = await client.GetInstanceAsync(WorkflowIdentity.InstanceId(requestId), getInputsAndOutputs: true, cancellationToken);
                var normalized = NormalizeStatus(requestId, instance);
                var conflict = request.CreateResponse(HttpStatusCode.Conflict);
                await conflict.WriteAsJsonAsync(new { error = "Workflow result is not available.", requestId, status = normalized.Status });
                return conflict;
            }

            var response = request.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(result);
            return response;
        }
        catch (WorkflowForbiddenException)
        {
            var forbidden = request.CreateResponse(HttpStatusCode.Forbidden);
            await forbidden.WriteAsJsonAsync(new { error = "Caller is not authorized to access this workflow." });
            return forbidden;
        }
        catch (InvalidDataException)
        {
            return await CreateErrorResponseAsync(request, HttpStatusCode.InternalServerError, IntegrityFailureMessage);
        }
        catch (Exception ex) when (IsTransientRequestFailure(ex, cancellationToken))
        {
            return await CreateUnavailableResponseAsync(request, ResultUnavailableMessage);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await CreateErrorResponseAsync(request, HttpStatusCode.InternalServerError, "Workflow result could not be verified.");
        }
    }

    private static bool IsTransientRequestFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        if (exception is AggregateException aggregate)
        {
            var errors = aggregate.Flatten().InnerExceptions;
            return errors.Count > 0 && errors.All(inner => IsTransientRequestFailure(inner, cancellationToken));
        }
        if (exception is Grpc.Core.RpcException rpcException)
        {
            return rpcException.StatusCode is Grpc.Core.StatusCode.Unavailable or
                Grpc.Core.StatusCode.DeadlineExceeded or Grpc.Core.StatusCode.ResourceExhausted;
        }
        return IsTransientFailure(exception, cancellationToken);
    }

    private static async Task<HttpResponseData> CreateUnavailableResponseAsync(HttpRequestData request, string message)
    {
        var response = request.CreateResponse(HttpStatusCode.ServiceUnavailable);
        response.Headers.Add("Retry-After", "10");
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }

    private static async Task<HttpResponseData> CreateErrorResponseAsync(HttpRequestData request, HttpStatusCode statusCode, string message)
    {
        var response = request.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }

    private static async Task<HttpResponseData> AcceptedAsync(HttpRequestData request, string instanceId, string requestId)
    {
        var requestUrl = request.Url;
        var statusUrl = new UriBuilder(requestUrl)
        {
            Path = $"{requestUrl.AbsolutePath.TrimEnd('/')}/{Uri.EscapeDataString(requestId)}",
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri.AbsoluteUri;
        var response = request.CreateResponse(HttpStatusCode.Accepted);
        response.Headers.Add("Location", statusUrl);
        response.Headers.Add("Retry-After", "10");
        await response.WriteAsJsonAsync(new { id = instanceId, instanceId, statusQueryGetUri = statusUrl });
        return response;
    }

    [Function(nameof(MainOrchestrator))]
    public static async Task<WorkflowOutput> MainOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<WorkflowRequest>() ?? throw new ArgumentException("Workflow input missing.");
        var logger = context.CreateReplaySafeLogger(nameof(MainOrchestrator));
        var terminalState = "Failed";
        BatchJobTerminationStatus? termination = null;
        string? terminationErrorCode = null;
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
                        if (termination?.Completed != true) terminationErrorCode = "batch_termination_unconfirmed";
                    }
                    catch (Exception) { terminationErrorCode = "batch_termination_failed"; }
                }
                var failureCode = state == "TimedOut" ? "workflow_timed_out" : "workflow_failed";
                var taskSummary = results.Select(result => new
                {
                    result.TaskId,
                    result.State,
                    errorCode = result.State == "TimedOut" ? "task_timed_out" : result.State == "Failed" ? "task_failed" : null
                }).ToArray();
                context.SetCustomStatus(new { state, plan.JobId, errorCode = failureCode, terminationErrorCode, tasks = taskSummary });
                throw new InvalidOperationException(state == "TimedOut" ? WorkflowTimedOutMessage : WorkflowFailedMessage);
            }
            context.SetCustomStatus(new { state = "Persisting", plan.JobId });
            await context.CallActivityAsync<ResultReference[]>(nameof(FetchAndPersistActivity), plan, RetryOptions());
            await context.CallActivityAsync<AggregateReference>(nameof(AggregateActivity), plan, RetryOptions());
            var manifest = await context.CallActivityAsync<ManifestReference>(nameof(PublishManifestActivity), plan, RetryOptions());
            context.SetCustomStatus(new { state = "Succeeded", plan.JobId, manifest.Uri });
            return new WorkflowOutput("Succeeded", input.RequestId, plan.JobId, manifest.Uri);
        }
        catch (Exception)
        {
            var errorCode = terminalState == "TimedOut" ? "workflow_timed_out" : "workflow_failed";
            context.SetCustomStatus(new { state = terminalState, jobId = WorkflowIdentity.JobId(input.RequestId), errorCode, termination, terminationErrorCode });
            logger.LogError("Request {RequestId} failed with {ErrorCode}", input.RequestId, errorCode);
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
                    return new TaskMonitorResult(spec.TaskId, status.Succeeded ? "Succeeded" : "Failed", status.Succeeded ? null : "Batch task failed.");
                var next = context.CurrentUtcDateTime.AddSeconds(spec.Plan.PollSeconds);
                await context.CreateTimer(next < deadline ? next : deadline, CancellationToken.None);
            }
            return new TaskMonitorResult(spec.TaskId, "TimedOut", "Shared monitoring deadline exceeded; parent will terminate the Batch job.");
        }
        catch (TimeoutException) { return new(spec.TaskId, "TimedOut", "Workflow monitoring deadline exceeded."); }
        catch (TaskFailedException ex)
        {
            var timedOut = context.CurrentUtcDateTime >= deadline || ex.FailureDetails.IsCausedBy<TimeoutException>();
            return new TaskMonitorResult(spec.TaskId, timedOut ? "TimedOut" : "Failed",
                timedOut ? "Workflow monitoring deadline exceeded." : "Batch task monitoring failed.");
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
