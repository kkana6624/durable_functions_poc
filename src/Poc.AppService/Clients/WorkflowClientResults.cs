using Poc.Core;

namespace Poc.AppService.Clients;

public sealed record WorkflowStartClientResult(
    bool Success,
    int StatusCode,
    string? InstanceId = null,
    string? Error = null,
    string? RetryAfter = null);

public sealed record WorkflowStatusClientResult(
    bool Success,
    int StatusCode,
    WorkflowStatusResponse? Status = null,
    string? Error = null,
    string? RetryAfter = null);

public sealed record WorkflowCancelClientResult(
    bool Success,
    int StatusCode,
    WorkflowStatusResponse? Status = null,
    string? Error = null);
