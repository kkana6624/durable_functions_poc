using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Poc.Core;

public sealed record WorkflowRequest(string RequestId, string Classification = "auto", int TaskCount = 3,
    int? FailTaskIndex = null, int LightDelaySeconds = 5, int HeavyDelaySeconds = 15)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RequestId) || !Regex.IsMatch(RequestId, @"\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,79}\z"))
            throw new ArgumentException("requestId must contain 1–80 ASCII letters, digits, hyphens or underscores and start with a letter or digit.");
        if (Classification is not ("auto" or "light" or "heavy")) throw new ArgumentException("classification must be auto, light or heavy.");
        if (TaskCount is < 1 or > 3) throw new ArgumentException("taskCount must be 1–3.");
        if (FailTaskIndex.HasValue && (FailTaskIndex < 0 || FailTaskIndex >= TaskCount)) throw new ArgumentException("failTaskIndex is outside the task range.");
        if (LightDelaySeconds is < 1 or > 3600 || HeavyDelaySeconds is < 1 or > 3600) throw new ArgumentException("Delays must be 1–3600 seconds.");
    }
}
public sealed record WorkflowSettings(int PollSeconds = 10, int TimeoutMinutes = 15);
public sealed record WorkloadClassification(string Classification, string Reason);
public sealed record WorkflowPlan(WorkflowRequest Request, string Classification, string Reason, string JobId,
    string Fingerprint, int PollSeconds, int TimeoutMinutes, bool CancelRequested = false);
public sealed record TaskSpec(WorkflowPlan Plan, int Index, DateTime? DeadlineUtc = null)
{
    public string TaskId => $"task-{Index:00}";
    public int DelaySeconds => Plan.Classification == "heavy" ? Plan.Request.HeavyDelaySeconds : Plan.Request.LightDelaySeconds;
    public bool InjectFailure => Plan.Request.FailTaskIndex == Index;
}
public sealed record BatchJobTerminationStatus(string State, bool Completed);
public sealed class TransientWorkflowException : Exception
{
    public TransientWorkflowException(string message) : base(message) { }
    public TransientWorkflowException(string message, Exception innerException) : base(message, innerException) { }
}
public sealed class PermanentWorkflowException() : Exception("Workflow activity failed.");
public sealed class WorkflowCancelledException(string message = "Workflow was cancelled by user.") : Exception(message);
public sealed record BatchTaskStatus(string TaskId, string State, bool Completed, bool Succeeded, int? ExitCode, string? Failure);
public sealed record ResultReference(string RequestId, string TaskId, string Uri);
public sealed record AggregateReference(string RequestId, string Uri, int Sum, int TaskCount);
public sealed record ManifestReference(string RequestId, string Uri, string AggregateUri, int TaskCount, int Sum);
public sealed record TaskResult(string RequestId, string JobId, string TaskId, string Classification, string Reason, string Fingerprint, int Value);
public sealed record AggregateResult(string RequestId, string JobId, string Fingerprint, int TaskCount, int Sum, string[] TaskIds);
public sealed record ManifestResult(string RequestId, string JobId, string Fingerprint, string Status, int TaskCount, int Sum, string AggregateUri, string[] TaskUris);
public sealed class WorkflowConflictException(string message) : Exception(message);
public sealed class WorkflowForbiddenException(string message) : Exception(message);


public sealed record WorkflowOwner(string OwnerId, string? OrgId = null)
{
    private static readonly Regex Pattern = new(@"\A[a-zA-Z0-9][a-zA-Z0-9_.@-]{0,127}\z", RegexOptions.Compiled);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OwnerId) || !Pattern.IsMatch(OwnerId))
            throw new ArgumentException("ownerId must contain 1–128 ASCII letters, digits, underscores, dots, at-signs or hyphens and start with a letter or digit.");
        if (OrgId is not null && (string.IsNullOrWhiteSpace(OrgId) || !Pattern.IsMatch(OrgId)))
            throw new ArgumentException("orgId must contain 1–128 ASCII letters, digits, underscores, dots, at-signs or hyphens and start with a letter or digit.");
    }
}

public sealed record WorkflowOwnerRecord(int Version, string RequestId, string InstanceId, string OwnerId, string? OrgId = null);
public sealed record WorkflowAdmissionRecord(int SchemaVersion, string InstanceId, WorkflowRequest Request,
    [property: System.Text.Json.Serialization.JsonRequired] WorkflowOwner? Owner);
public sealed record WorkflowCancelRecord(int SchemaVersion, string RequestId, string? Reason, DateTimeOffset RequestedAtUtc,
    [property: System.Text.Json.Serialization.JsonRequired] WorkflowOwner? Caller);
public sealed record WorkflowResolutionRecord(int Version, string RequestId, string Status);
public sealed record WorkflowStatusResponse(string RequestId, string InstanceId, string JobId, string Status, bool ResultAvailable, bool? TerminationConfirmed = null, string? Error = null);
public sealed record WorkflowResultResponse(string RequestId, string JobId, string Fingerprint, string Status, int TaskCount, int Sum, string[] TaskIds);

public interface IBatchGateway
{
    Task<BatchJobTerminationStatus> TerminateJobAsync(WorkflowPlan plan, CancellationToken cancellationToken = default);
    Task EnsureJobAsync(WorkflowPlan plan, CancellationToken cancellationToken = default);
    Task EnsureTaskAsync(TaskSpec spec, CancellationToken cancellationToken = default);
    Task<BatchTaskStatus> GetStatusAsync(TaskSpec spec, CancellationToken cancellationToken = default);
    Task<string> ReadResultAsync(TaskSpec spec, CancellationToken cancellationToken = default);
}
public interface IResultRepository
{
    Task<string> PutIfAbsentAsync<T>(string path, T value, CancellationToken cancellationToken = default);
    Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default);
    string GetUri(string path);
}
public interface IWorkloadClassifier
{
    Task<WorkloadClassification> ClassifyAsync(WorkflowRequest request, CancellationToken cancellationToken = default);
}
public sealed class StubWorkloadClassifier : IWorkloadClassifier
{
    public Task<WorkloadClassification> ClassifyAsync(WorkflowRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new WorkloadClassification(request.Classification == "auto" ? "light" : request.Classification,
            request.Classification == "auto" ? "P0 stub: auto always selects light." : "Explicit request classification."));
}
public static class WorkflowIdentity
{
    public const string CancelEventName = "CancelRequested";
    public const string WorkflowCancelledMessage = "Workflow was cancelled by user.";

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string JobId(string requestId) => "poc-" + Hash(requestId)[..40];
    public static string InstanceId(string requestId) => JobId(requestId);
    public static string Fingerprint(WorkflowRequest request, string classification) => Hash(JsonSerializer.Serialize(new { Version = 1, Request = request, Classification = classification }));
    public static string Root(WorkflowPlan plan) => $"runs/poc-{plan.Request.RequestId}";
    public static string TaskPath(TaskSpec spec) => $"{Root(spec.Plan)}/tasks/{spec.TaskId}/result.json";
    public static string RequestPath(string requestId) => $"runs/poc-{requestId}/request.json";
    public static string AdmissionPath(string requestId) => $"runs/poc-{requestId}/admission.json";
    public static string OwnerPath(string requestId) => $"runs/poc-{requestId}/owner.json";
    public static string CancelPath(string requestId) => $"runs/poc-{requestId}/cancel.json";
    public static string ResolutionPath(string requestId) => $"runs/poc-{requestId}/final/resolution.json";
    public static string AggregatePath(string requestId) => $"runs/poc-{requestId}/final/aggregate.json";
    public static string ManifestPath(string requestId) => $"runs/poc-{requestId}/final/manifest.json";
    public static string AggregatePath(WorkflowPlan plan) => AggregatePath(plan.Request.RequestId);
    public static string ManifestPath(WorkflowPlan plan) => ManifestPath(plan.Request.RequestId);
}

public static class WindowsTaskCommand
{
    public static string Build(int delaySeconds, bool fail)
    {
        if (delaySeconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(delaySeconds));
        // Only validated integers and constant text reach the shell. Explicit exit propagates through cmd.
        return $"cmd /d /c powershell.exe -NoLogo -NoProfile -NonInteractive -Command \"$ErrorActionPreference='Stop'; Start-Sleep -Seconds {delaySeconds}; " +
            (fail ? "exit 42\"" : "Set-Content -LiteralPath result.txt -Value 1 -Encoding ASCII; exit 0\"");
    }
}
