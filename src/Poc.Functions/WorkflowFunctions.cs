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

public sealed class WorkflowFunctions(WorkflowOperations operations, WorkflowAdmission admission, IWorkflowCallerResolver callerResolver)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string InvalidRequestMessage = "Workflow request is invalid.";
    private const string WorkflowFailedMessage = "Workflow execution failed.";
    private const string WorkflowTimedOutMessage = "Workflow exceeded its monitoring deadline.";
    private const string WorkflowCancelledMessage = "Workflow was cancelled by user.";
    private const string TerminationUnconfirmedMessage = "Batch job termination could not be confirmed.";
    private const string IntegrityFailureMessage = "Published workflow results are inconsistent.";
    private const string StatusUnavailableMessage = "Workflow status could not be verified. Retry later.";
    private const string ResultUnavailableMessage = "Workflow result could not be verified. Retry later.";

    public const string PackageSha256Header = "X-Poc-Package-Sha256";
    public const string AssemblySha256Header = "X-Poc-Assembly-Sha256";
    private static readonly string PackageSha256 = ComputePackageSha256();
    private static readonly string AssemblySha256 = ComputeAssemblySha256();

    private static string ComputeAssemblySha256()
    {
        try
        {
            var basePath = AppContext.BaseDirectory;
            var path = Path.Combine(basePath, "assembly-sha256.txt");
            if (File.Exists(path))
            {
                var content = File.ReadAllText(path).Trim();
                if (content.Length == 64) return content.ToLowerInvariant();
            }
            var dllPath = typeof(WorkflowFunctions).Assembly.Location;
            if (!string.IsNullOrEmpty(dllPath) && File.Exists(dllPath))
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var stream = File.OpenRead(dllPath);
                return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            }
        }
        catch { }
        return "unknown";
    }

    private static string ComputePackageSha256()
    {
        try
        {
            var basePath = AppContext.BaseDirectory;
            var path = Path.Combine(basePath, "package-sha256.txt");
            if (File.Exists(path))
            {
                var content = File.ReadAllText(path).Trim();
                if (content.Length == 64) return content.ToLowerInvariant();
            }
            return ComputeAssemblySha256();
        }
        catch { }
        return "unknown";
    }

    private static HttpResponseData CreateWorkflowResponse(HttpRequestData request, HttpStatusCode statusCode)
    {
        var response = request.CreateResponse(statusCode);
        response.Headers.Add(PackageSha256Header, PackageSha256);
        response.Headers.Add(AssemblySha256Header, AssemblySha256);
        return response;
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
            owner = callerResolver.Resolve(request);
            input = await JsonSerializer.DeserializeAsync<WorkflowRequest>(request.Body, JsonOptions)
                ?? throw new ArgumentException("JSON request body is required.");
            input.Validate();
        }
        catch (WorkflowCallerAuthenticationException ex)
        {
            return await CreateErrorResponseAsync(request, ex.StatusCode, "Caller is not authorized to access this workflow.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            var badRequest = CreateWorkflowResponse(request, HttpStatusCode.BadRequest);
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
            var forbidden = CreateWorkflowResponse(request, HttpStatusCode.Forbidden);
            await forbidden.WriteAsJsonAsync(new { error = "Caller is not authorized to access this workflow." });
            return forbidden;
        }
        catch (WorkflowConflictException)
        {
            var conflict = CreateWorkflowResponse(request, HttpStatusCode.Conflict);
            await conflict.WriteAsJsonAsync(new { error = "requestId already has different configuration.", instanceId = WorkflowIdentity.InstanceId(input.RequestId) });
            return conflict;
        }
        catch (Exception ex) when (IsTransientRequestFailure(ex, cancellationToken))
        {
            var unavailable = CreateWorkflowResponse(request, HttpStatusCode.ServiceUnavailable);
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
            if (customState == "Cancelled")
            {
                bool unconfirmed = terminationFailed || terminationCompleted == false;
                var status = unconfirmed ? "Cancelling" : "Cancelled";
                bool? confirmed = unconfirmed ? false : (terminationCompleted == true ? true : null);
                return new WorkflowStatusResponse(requestId, instanceId, jobId, status, ResultAvailable: false,
                    TerminationConfirmed: confirmed, Error: unconfirmed ? TerminationUnconfirmedMessage : WorkflowCancelledMessage);
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
            caller = callerResolver.Resolve(request);
        }
        catch (WorkflowCallerAuthenticationException ex)
        {
            return await CreateErrorResponseAsync(request, ex.StatusCode, "Caller is not authorized to access this workflow.");
        }
        catch (ArgumentException)
        {
            var badRequest = CreateWorkflowResponse(request, HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = InvalidRequestMessage });
            return badRequest;
        }

        try
        {
            if (!await admission.ExistsAsync(requestId, client, cancellationToken))
            {
                var notFound = CreateWorkflowResponse(request, HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = "Workflow not found.", requestId });
                return notFound;
            }

            await admission.AuthorizeAccessAsync(requestId, caller, cancellationToken);
            var instance = await client.GetInstanceAsync(WorkflowIdentity.InstanceId(requestId), getInputsAndOutputs: true, cancellationToken);
            var normalized = NormalizeStatus(requestId, instance);
            var publishedResult = await operations.TryGetPublishedResultAsync(requestId, cancellationToken);
            normalized = normalized with { ResultAvailable = publishedResult is not null };
            if (normalized.Status is "Accepted" or "Preparing" or "Running")
            {
                if (await admission.IsCancellationRequestedAsync(requestId, cancellationToken))
                {
                    normalized = normalized with { Status = "Cancelling" };
                }
            }
            if (normalized.Status == "Cancelling" || (normalized.Status == "Cancelled" && normalized.TerminationConfirmed != true))
            {
                var term = await admission.ReadTerminationAsync(requestId, cancellationToken);
                if (term?.Completed == true)
                {
                    normalized = normalized with
                    {
                        Status = "Cancelled",
                        TerminationConfirmed = true,
                        Error = WorkflowCancelledMessage
                    };
                }
            }

            var response = CreateWorkflowResponse(request, HttpStatusCode.OK);
            await response.WriteAsJsonAsync(normalized);
            return response;
        }

        catch (WorkflowForbiddenException)
        {
            var forbidden = CreateWorkflowResponse(request, HttpStatusCode.Forbidden);
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
            caller = callerResolver.Resolve(request);
        }
        catch (WorkflowCallerAuthenticationException ex)
        {
            return await CreateErrorResponseAsync(request, ex.StatusCode, "Caller is not authorized to access this workflow.");
        }
        catch (ArgumentException)
        {
            var badRequest = CreateWorkflowResponse(request, HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = InvalidRequestMessage });
            return badRequest;
        }

        try
        {
            if (!await admission.ExistsAsync(requestId, client, cancellationToken))
            {
                var notFound = CreateWorkflowResponse(request, HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = "Workflow not found.", requestId });
                return notFound;
            }

            await admission.AuthorizeAccessAsync(requestId, caller, cancellationToken);
            var result = await operations.TryGetPublishedResultAsync(requestId, cancellationToken);
            if (result is null)
            {
                var instance = await client.GetInstanceAsync(WorkflowIdentity.InstanceId(requestId), getInputsAndOutputs: true, cancellationToken);
                var normalized = NormalizeStatus(requestId, instance);
                if (normalized.Status == "Cancelling" || (normalized.Status == "Cancelled" && normalized.TerminationConfirmed != true))
                {
                    var term = await admission.ReadTerminationAsync(requestId, cancellationToken);
                    if (term?.Completed == true)
                    {
                        normalized = normalized with
                        {
                            Status = "Cancelled",
                            TerminationConfirmed = true,
                            Error = WorkflowCancelledMessage
                        };
                    }
                }
                var conflict = CreateWorkflowResponse(request, HttpStatusCode.Conflict);
                await conflict.WriteAsJsonAsync(new { error = "Workflow result is not available.", requestId, status = normalized.Status });
                return conflict;
            }

            var response = CreateWorkflowResponse(request, HttpStatusCode.OK);
            await response.WriteAsJsonAsync(result);
            return response;
        }
        catch (WorkflowForbiddenException)
        {
            var forbidden = CreateWorkflowResponse(request, HttpStatusCode.Forbidden);
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
        var response = CreateWorkflowResponse(request, HttpStatusCode.ServiceUnavailable);
        response.Headers.Add("Retry-After", "10");
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }

    private static async Task<HttpResponseData> CreateErrorResponseAsync(HttpRequestData request, HttpStatusCode statusCode, string message)
    {
        var response = CreateWorkflowResponse(request, statusCode);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }

    private static string BuildStatusUrl(Uri requestUrl, string requestId)
    {
        var path = requestUrl.AbsolutePath;
        var workflowsIdx = path.IndexOf("/workflows", StringComparison.OrdinalIgnoreCase);
        var basePath = workflowsIdx >= 0 ? path[..workflowsIdx] : string.Empty;
        return new UriBuilder(requestUrl)
        {
            Path = $"{basePath}/workflows/{Uri.EscapeDataString(requestId)}",
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri.AbsoluteUri;
    }

    private static async Task<HttpResponseData> AcceptedAsync(HttpRequestData request, string instanceId, string requestId)
    {
        var statusUrl = BuildStatusUrl(request.Url, requestId);
        var response = CreateWorkflowResponse(request, HttpStatusCode.Accepted);
        response.Headers.Add("Location", statusUrl);
        response.Headers.Add("Retry-After", "10");
        await response.WriteAsJsonAsync(new { id = instanceId, instanceId, statusQueryGetUri = statusUrl });
        return response;
    }

    [Function(nameof(CancelWorkflow))]
    public async Task<HttpResponseData> CancelWorkflow(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "workflows/{requestId}/cancel")] HttpRequestData request,
        string requestId,
        [DurableClient] DurableTaskClient client,
        CancellationToken cancellationToken)
    {
        WorkflowOwner? caller;
        try
        {
            new WorkflowRequest(requestId).Validate();
            caller = callerResolver.Resolve(request);
        }
        catch (WorkflowCallerAuthenticationException ex)
        {
            return await CreateErrorResponseAsync(request, ex.StatusCode, "Caller is not authorized to access this workflow.");
        }
        catch (ArgumentException)
        {
            var badRequest = CreateWorkflowResponse(request, HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = InvalidRequestMessage });
            return badRequest;
        }

        try
        {
            if (!await admission.ExistsAsync(requestId, client, cancellationToken))
            {
                var notFound = CreateWorkflowResponse(request, HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = "Workflow not found.", requestId });
                return notFound;
            }

            await admission.AuthorizeCancellationAsync(requestId, caller, cancellationToken);

            var publishedResult = await operations.TryGetPublishedResultAsync(requestId, cancellationToken);
            if (publishedResult is not null)
            {
                var conflict = CreateWorkflowResponse(request, HttpStatusCode.Conflict);
                await conflict.WriteAsJsonAsync(new { error = "Workflow is already completed and cannot be cancelled.", requestId, status = "Succeeded" });
                return conflict;
            }

            var instanceId = WorkflowIdentity.InstanceId(requestId);
            var instance = await client.GetInstanceAsync(instanceId, getInputsAndOutputs: true, cancellationToken);
            if (instance is not null)
            {
                var normalized = NormalizeStatus(requestId, instance);
                if (normalized.Status == "Cancelling" || (normalized.Status == "Cancelled" && normalized.TerminationConfirmed != true))
                {
                    var term = await admission.ReadTerminationAsync(requestId, cancellationToken);
                    if (term?.Completed == true)
                    {
                        normalized = normalized with
                        {
                            Status = "Cancelled",
                            TerminationConfirmed = true,
                            Error = WorkflowCancelledMessage
                        };
                    }
                }
                if (normalized.Status == "Succeeded")
                {
                    var conflict = CreateWorkflowResponse(request, HttpStatusCode.Conflict);
                    await conflict.WriteAsJsonAsync(new { error = "Workflow is already completed and cannot be cancelled.", requestId, status = "Succeeded" });
                    return conflict;
                }
                if (normalized.Status is "Failed" or "TimedOut")
                {
                    var conflict = CreateWorkflowResponse(request, HttpStatusCode.Conflict);
                    await conflict.WriteAsJsonAsync(new { error = $"Workflow is already {normalized.Status.ToLowerInvariant()} and cannot be cancelled.", requestId, status = normalized.Status });
                    return conflict;
                }
                if (normalized.Status == "Cancelled")
                {
                    var ok = CreateWorkflowResponse(request, HttpStatusCode.OK);
                    await ok.WriteAsJsonAsync(normalized);
                    return ok;
                }
            }

            string? reason = null;
            try
            {
                if (!request.Body.CanSeek || request.Body.Length > 0)
                {
                    using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("reason", out var reasonProp) &&
                        reasonProp.ValueKind == JsonValueKind.String)
                    {
                        reason = reasonProp.GetString();
                    }
                }
            }
            catch (JsonException) { }

            var cancelRecord = await admission.CancelAsync(requestId, caller, reason, cancellationToken);

            if (instance is not null && instance.RuntimeStatus == OrchestrationRuntimeStatus.Failed)
            {
                var record = await admission.GetRecordAsync(requestId, cancellationToken);
                if (record?.Request is not null)
                {
                    var plan = await operations.ClassifyAsync(record.Request, cancellationToken);
                    var term = await operations.TerminateJobAsync(plan, cancellationToken);
                    if (term.Completed)
                    {
                        await admission.RecordTerminationAsync(requestId, term, cancellationToken);
                        var confirmedResponse = new WorkflowStatusResponse(
                            requestId, instanceId, plan.JobId, "Cancelled", ResultAvailable: false,
                            TerminationConfirmed: true, Error: WorkflowCancelledMessage);
                        var ok = CreateWorkflowResponse(request, HttpStatusCode.OK);
                        await ok.WriteAsJsonAsync(confirmedResponse);
                        return ok;
                    }
                }
            }
            else
            {
                await client.RaiseEventAsync(instanceId, WorkflowIdentity.CancelEventName, cancelRecord, cancellation: cancellationToken);
            }

            var statusUrl = BuildStatusUrl(request.Url, requestId);
            var response = CreateWorkflowResponse(request, HttpStatusCode.Accepted);
            response.Headers.Add("Location", statusUrl);
            response.Headers.Add("Retry-After", "10");
            await response.WriteAsJsonAsync(new
            {
                id = instanceId,
                instanceId,
                requestId,
                status = "Cancelling",
                statusQueryGetUri = statusUrl
            });
            return response;
        }
        catch (WorkflowConflictException ex)
        {
            var conflict = CreateWorkflowResponse(request, HttpStatusCode.Conflict);
            await conflict.WriteAsJsonAsync(new { error = ex.Message, requestId, status = "Succeeded" });
            return conflict;
        }
        catch (WorkflowForbiddenException)
        {
            var forbidden = CreateWorkflowResponse(request, HttpStatusCode.Forbidden);
            await forbidden.WriteAsJsonAsync(new { error = "Caller is not authorized to access this workflow." });
            return forbidden;
        }
        catch (InvalidDataException)
        {
            return await CreateErrorResponseAsync(request, HttpStatusCode.InternalServerError, IntegrityFailureMessage);
        }
        catch (Exception ex) when (IsTransientRequestFailure(ex, cancellationToken))
        {
            return await CreateUnavailableResponseAsync(request, "Workflow cancellation could not be confirmed. Retry later.");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await CreateErrorResponseAsync(request, HttpStatusCode.InternalServerError, "Workflow cancellation could not be confirmed.");
        }
    }

    [Function(nameof(MainOrchestrator))]
    public static async Task<WorkflowOutput> MainOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<WorkflowRequest>() ?? throw new ArgumentException("Workflow input missing.");
        var logger = context.CreateReplaySafeLogger(nameof(MainOrchestrator));
        var terminalState = "Failed";
        BatchJobTerminationStatus? termination = null;
        string? terminationErrorCode = null;
        var cancelEventTask = context.WaitForExternalEvent<WorkflowCancelRecord>(WorkflowIdentity.CancelEventName);
        try
        {
            var plan = await context.CallActivityAsync<WorkflowPlan>(nameof(ClassifyActivity), input, RetryOptions());
            var deadline = context.CurrentUtcDateTime.AddMinutes(plan.TimeoutMinutes);
            logger.LogInformation("Request {RequestId}, job {JobId}, classification {Classification}: {Reason}",
                input.RequestId, plan.JobId, plan.Classification, plan.Reason);

            if (plan.CancelRequested || (cancelEventTask.IsCompleted && cancelEventTask.Result is not null))
            {
                terminalState = "Cancelled";
                context.SetCustomStatus(new { state = "Cancelled", plan.JobId, errorCode = "workflow_cancelled" });
                throw new InvalidOperationException(WorkflowCancelledMessage);
            }

            context.SetCustomStatus(new { state = "Submitting", plan.JobId, plan.Classification });
            // Do not abandon job creation: it could create a new job after cleanup saw no job.
            try { await context.CallActivityAsync(nameof(EnsureBatchJobActivity), plan, RetryOptions(deadline)); }
            catch (TaskFailedException ex) when (context.CurrentUtcDateTime >= deadline && ex.FailureDetails.IsCausedBy<TransientWorkflowException>())
            {
                // An ambiguous network failure may have created the job. Expired children trigger
                // identity-checked cleanup rather than submitting work after the deadline.
            }

            if (cancelEventTask.IsCompleted && cancelEventTask.Result is not null)
            {
                terminalState = "Cancelled";
                (termination, terminationErrorCode) = await PerformBatchTerminationAsync(context, plan, "Cancelling");
                context.SetCustomStatus(new { state = "Cancelled", plan.JobId, errorCode = "workflow_cancelled", terminationErrorCode, termination });
                throw new InvalidOperationException(WorkflowCancelledMessage);
            }

            var monitors = Enumerable.Range(0, input.TaskCount).Select(index =>
                context.CallSubOrchestratorAsync<TaskMonitorResult>(nameof(MonitorBatchTaskOrchestrator), new TaskSpec(plan, index, deadline))).ToArray();
            var allMonitors = Task.WhenAll(monitors);
            var completedTask = await Task.WhenAny(allMonitors, cancelEventTask);

            if (completedTask == cancelEventTask && cancelEventTask.Result is not null)
            {
                terminalState = "Cancelled";
                (termination, terminationErrorCode) = await PerformBatchTerminationAsync(context, plan, "Cancelling");
                context.SetCustomStatus(new { state = "Cancelled", plan.JobId, errorCode = "workflow_cancelled", terminationErrorCode, termination });
                throw new InvalidOperationException(WorkflowCancelledMessage);
            }

            // Terminal outcomes allow all siblings to finish before propagating a task failure.
            var results = await allMonitors;
            var failures = results.Where(result => result.State != "Succeeded").ToArray();
            if (failures.Length > 0)
            {
                var state = failures.Any(result => result.State == "TimedOut") ? "TimedOut" : "Failed";
                terminalState = state;
                if (state == "TimedOut")
                {
                    (termination, terminationErrorCode) = await PerformBatchTerminationAsync(context, plan, "Stopping");
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

            if (cancelEventTask.IsCompleted && cancelEventTask.Result is not null)
            {
                terminalState = "Cancelled";
                (termination, terminationErrorCode) = await PerformBatchTerminationAsync(context, plan, "Cancelling");
                context.SetCustomStatus(new { state = "Cancelled", plan.JobId, errorCode = "workflow_cancelled", terminationErrorCode, termination });
                throw new InvalidOperationException(WorkflowCancelledMessage);
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
            if (ex is TaskFailedException tfe && tfe.FailureDetails?.IsCausedBy<WorkflowCancelledException>() == true)
            {
                terminalState = "Cancelled";
            }
            var errorCode = terminalState switch
            {
                "TimedOut" => "workflow_timed_out",
                "Cancelled" => "workflow_cancelled",
                _ => "workflow_failed"
            };
            context.SetCustomStatus(new { state = terminalState, jobId = WorkflowIdentity.JobId(input.RequestId), errorCode, termination, terminationErrorCode });
            logger.LogError("Request {RequestId} ended in state {State} with {ErrorCode}", input.RequestId, terminalState, errorCode);
            throw;
        }
    }

    private static async Task<(BatchJobTerminationStatus? termination, string? terminationErrorCode)> PerformBatchTerminationAsync(
        TaskOrchestrationContext context, WorkflowPlan plan, string state = "Stopping")
    {
        var stopDeadline = context.CurrentUtcDateTime.AddMinutes(2);
        BatchJobTerminationStatus? termination = null;
        string? terminationErrorCode = null;
        try
        {
            do
            {
                termination = await BeforeDeadline(context,
                    context.CallActivityAsync<BatchJobTerminationStatus>(nameof(TerminateBatchJobActivity), plan, RetryOptions(stopDeadline)), stopDeadline);
                context.SetCustomStatus(new { state, plan.JobId, termination });
                if (termination.Completed) break;
                var next = context.CurrentUtcDateTime.AddSeconds(5);
                await context.CreateTimer(next < stopDeadline ? next : stopDeadline, CancellationToken.None);
            } while (context.CurrentUtcDateTime < stopDeadline);
            if (termination?.Completed != true) terminationErrorCode = "batch_termination_unconfirmed";
        }
        catch (Exception)
        {
            terminationErrorCode = "batch_termination_failed";
        }
        return (termination, terminationErrorCode);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Retain host/caller cancellation semantics without retaining SDK
            // messages, inner failures, or a linked timeout token.
            throw new OperationCanceledException("Workflow activity was canceled.", cancellationToken);
        }
        catch (Exception ex) when (IsTransientFailure(ex, cancellationToken))
        {
            // Classify before discarding the source exception. Durable serializes
            // exception messages and inner failures across the Activity boundary.
            throw new TransientWorkflowException("Workflow activity temporarily failed.");
        }
        catch (TimeoutException)
        {
            // MonitorBatchTaskOrchestrator recognizes this top-level type.
            throw new TimeoutException("Workflow activity timed out.");
        }
        catch (WorkflowCancelledException ex)
        {
            throw new WorkflowCancelledException(ex.Message);
        }
        catch (Exception)
        {
            throw new PermanentWorkflowException();
        }
    }
    public static bool IsTransientFailure(Exception error, CancellationToken cancellationToken = default) => !cancellationToken.IsCancellationRequested && error switch
    {
        AggregateException aggregate => aggregate.Flatten().InnerExceptions is { Count: > 0 } errors && errors.All(inner => IsTransientFailure(inner, cancellationToken)),
        // Azure.Core uses a linked timeout token. The caller's token distinguishes host cancellation.
        OperationCanceledException => true,
        RequestFailedException { Status: 0 or 408 or 429 or 500 or 502 or 503 or 504 } => true,
        Grpc.Core.RpcException { StatusCode: Grpc.Core.StatusCode.Unavailable or Grpc.Core.StatusCode.DeadlineExceeded or Grpc.Core.StatusCode.ResourceExhausted } => true,
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
    public Task<BatchJobTerminationStatus> TerminateBatchJobActivity([ActivityTrigger] WorkflowPlan plan, CancellationToken cancellationToken) =>
        RetryableActivity(async () =>
        {
            var status = await operations.TerminateJobAsync(plan, cancellationToken);
            if (status.Completed)
            {
                await admission.RecordTerminationAsync(plan.Request.RequestId, status, cancellationToken);
            }
            return status;
        }, cancellationToken);
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
