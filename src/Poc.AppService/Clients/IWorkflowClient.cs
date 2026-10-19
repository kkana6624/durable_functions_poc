using Poc.Core;

namespace Poc.AppService.Clients;

/// <summary>
/// Client interface for invoking upstream Azure Functions workflow endpoints.
/// Supports both Function Key and Entra ID Bearer token authentication.
/// </summary>
public interface IWorkflowClient
{
    Task<WorkflowStartClientResult> StartWorkflowAsync(WorkflowRequest request, WorkflowOwner? owner, CancellationToken cancellationToken = default);
    Task<WorkflowStatusClientResult> GetStatusAsync(string requestId, WorkflowOwner? owner, CancellationToken cancellationToken = default);
    Task<WorkflowCancelClientResult> CancelWorkflowAsync(string requestId, WorkflowOwner? owner, string? reason = null, CancellationToken cancellationToken = default);
}
