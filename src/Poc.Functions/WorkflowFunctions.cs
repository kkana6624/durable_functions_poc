using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using Poc.Core;

namespace Poc.Functions;

public sealed class WorkflowFunctions(WorkflowOperations operations)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function(nameof(StartWorkflow))]
    public async Task<HttpResponseData> StartWorkflow(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "workflows")] HttpRequestData request,
        [DurableClient] DurableTaskClient client)
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
        var instanceId = WorkflowIdentity.InstanceId(input.RequestId);
        // P0 observation only: this check does not serialize concurrent starters.
        var existing = await client.GetInstanceAsync(instanceId, getInputsAndOutputs: true);
        if (existing is not null)
        {
            if (existing.ReadInputAs<WorkflowRequest>() != input)
            {
                var conflict = request.CreateResponse(HttpStatusCode.Conflict);
                await conflict.WriteAsJsonAsync(new { error = "requestId already has different configuration.", instanceId });
                return conflict;
            }
            return await AcceptedAsync(request, client, instanceId);
        }
        await client.ScheduleNewOrchestrationInstanceAsync(nameof(MainOrchestrator), input,
            new StartOrchestrationOptions { InstanceId = instanceId });
        return await AcceptedAsync(request, client, instanceId);
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
        try
        {
            var plan = await context.CallActivityAsync<WorkflowPlan>(nameof(ClassifyActivity), input);
            logger.LogInformation("Request {RequestId}, job {JobId}, classification {Classification}: {Reason}",
                input.RequestId, plan.JobId, plan.Classification, plan.Reason);
            context.SetCustomStatus(new { state = "Submitting", plan.JobId, plan.Classification });
            await context.CallActivityAsync(nameof(EnsureBatchJobActivity), plan);
            var monitors = Enumerable.Range(0, input.TaskCount).Select(index =>
                context.CallSubOrchestratorAsync<TaskMonitorResult>(nameof(MonitorBatchTaskOrchestrator), new TaskSpec(plan, index))).ToArray();
            // Terminal outcomes allow all siblings to finish before propagating a task failure.
            var results = await Task.WhenAll(monitors);
            var failures = results.Where(result => result.State != "Succeeded").ToArray();
            if (failures.Length > 0)
            {
                var state = failures.Any(result => result.State == "TimedOut") ? "TimedOut" : "Failed";
                terminalState = state;
                context.SetCustomStatus(new { state, plan.JobId, tasks = results });
                throw new InvalidOperationException($"{state}: {string.Join("; ", failures.Select(result => $"{result.TaskId}: {result.Error}"))}");
            }
            context.SetCustomStatus(new { state = "Persisting", plan.JobId });
            await context.CallActivityAsync<ResultReference[]>(nameof(FetchAndPersistActivity), plan);
            await context.CallActivityAsync<AggregateReference>(nameof(AggregateActivity), plan);
            var manifest = await context.CallActivityAsync<ManifestReference>(nameof(PublishManifestActivity), plan);
            context.SetCustomStatus(new { state = "Succeeded", plan.JobId, manifest.Uri });
            return new WorkflowOutput("Succeeded", input.RequestId, plan.JobId, manifest.Uri);
        }
        catch (Exception ex)
        {
            context.SetCustomStatus(new { state = terminalState, jobId = WorkflowIdentity.JobId(input.RequestId), error = ex.Message });
            logger.LogError("Request {RequestId} failed: {Error}", input.RequestId, ex.Message);
            throw;
        }
    }

    [Function(nameof(MonitorBatchTaskOrchestrator))]
    public static async Task<TaskMonitorResult> MonitorBatchTaskOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var spec = context.GetInput<TaskSpec>() ?? throw new ArgumentException("Task input missing.");
        var deadline = context.CurrentUtcDateTime.AddMinutes(spec.Plan.TimeoutMinutes);
        var logger = context.CreateReplaySafeLogger(nameof(MonitorBatchTaskOrchestrator));
        try
        {
            await context.CallActivityAsync(nameof(EnsureBatchTaskActivity), spec);
            while (context.CurrentUtcDateTime < deadline)
            {
                var status = await context.CallActivityAsync<BatchTaskStatus>(nameof(GetBatchTaskStatusActivity), spec);
                logger.LogInformation("Job {JobId}, task {TaskId}: {State}, exit {ExitCode}",
                    spec.Plan.JobId, spec.TaskId, status.State, status.ExitCode);
                if (status.Completed)
                    return new TaskMonitorResult(spec.TaskId, status.Succeeded ? "Succeeded" : "Failed", status.Failure ?? (status.Succeeded ? null : $"Exit code {status.ExitCode}"));
                var next = context.CurrentUtcDateTime.AddSeconds(spec.Plan.PollSeconds);
                await context.CreateTimer(next < deadline ? next : deadline, CancellationToken.None);
            }
            return new TaskMonitorResult(spec.TaskId, "TimedOut", "Batch task monitoring deadline exceeded; Batch task may still be running.");
        }
        catch (TaskFailedException ex)
        {
            return new TaskMonitorResult(spec.TaskId, "Failed", ex.Message);
        }
    }

    [Function(nameof(ClassifyActivity))]
    public Task<WorkflowPlan> ClassifyActivity([ActivityTrigger] WorkflowRequest input) => operations.ClassifyAsync(input);
    [Function(nameof(EnsureBatchJobActivity))]
    public Task EnsureBatchJobActivity([ActivityTrigger] WorkflowPlan plan) => operations.EnsureJobAsync(plan);
    [Function(nameof(EnsureBatchTaskActivity))]
    public Task EnsureBatchTaskActivity([ActivityTrigger] TaskSpec spec) => operations.EnsureTaskAsync(spec);
    [Function(nameof(GetBatchTaskStatusActivity))]
    public Task<BatchTaskStatus> GetBatchTaskStatusActivity([ActivityTrigger] TaskSpec spec) => operations.GetStatusAsync(spec);
    [Function(nameof(FetchAndPersistActivity))]
    public Task<ResultReference[]> FetchAndPersistActivity([ActivityTrigger] WorkflowPlan plan) => operations.FetchAndPersistAsync(plan);
    [Function(nameof(AggregateActivity))]
    public Task<AggregateReference> AggregateActivity([ActivityTrigger] WorkflowPlan plan) => operations.AggregateAsync(plan);
    [Function(nameof(PublishManifestActivity))]
    public Task<ManifestReference> PublishManifestActivity([ActivityTrigger] WorkflowPlan plan) => operations.PublishManifestAsync(plan);
}

public sealed record TaskMonitorResult(string TaskId, string State, string? Error);
public sealed record WorkflowOutput(string State, string RequestId, string JobId, string ManifestUri);
