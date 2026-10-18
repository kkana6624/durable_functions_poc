using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using Poc.Core;

namespace Poc.Functions;

// The immutable Blob claim arbitrates input conflicts; the entity serializes start commands.
// A claim is deliberately retained after an uncertain signal, so a retry can safely recover.
public sealed class WorkflowAdmission(IResultRepository results)
{
    public Task<string> AdmitAsync(WorkflowRequest input, DurableTaskClient client,
        CancellationToken cancellationToken = default) =>
        AdmitAsync(input, client, owner: null, cancellationToken);

    public async Task<string> AdmitAsync(WorkflowRequest input, DurableTaskClient client,
        WorkflowOwner? owner, CancellationToken cancellationToken = default)
    {
        input.Validate();
        owner?.Validate();
        var instanceId = WorkflowIdentity.InstanceId(input.RequestId);

        var existing = await client.GetInstanceAsync(instanceId, getInputsAndOutputs: true, cancellationToken);
        // Also protect instances created before the immutable admission record was introduced.
        if (existing is not null && existing.ReadInputAs<WorkflowRequest>() != input)
            throw new WorkflowConflictException("requestId already has different configuration.");

        if (owner is not null)
        {
            await VerifyOrClaimOwnerAsync(input.RequestId, instanceId, owner, existing is not null, cancellationToken);
        }
        else
        {
            var existingOwner = await TryReadAsync<WorkflowOwnerRecord>(WorkflowIdentity.OwnerPath(input.RequestId), cancellationToken);
            if (existingOwner is not null)
                throw new WorkflowForbiddenException("Caller is not authorized to access this workflow.");
        }

        await results.PutIfAbsentAsync(WorkflowIdentity.RequestPath(input.RequestId),
            new AdmissionRecord(1, instanceId, input), cancellationToken);
        if (existing is not null) return instanceId;

        await client.Entities.SignalEntityAsync(new EntityInstanceId(nameof(WorkflowAdmissionEntity), instanceId),
            WorkflowAdmissionEntity.StartOperation, input, cancellation: cancellationToken);
        return instanceId;
    }

    private async Task VerifyOrClaimOwnerAsync(string requestId, string instanceId, WorkflowOwner owner, bool instanceExists, CancellationToken cancellationToken)
    {
        var existing = await TryReadAsync<WorkflowOwnerRecord>(WorkflowIdentity.OwnerPath(requestId), cancellationToken);
        if (existing is not null)
        {
            if (existing.OwnerId != owner.OwnerId || existing.OrgId != owner.OrgId)
                throw new WorkflowForbiddenException("Caller is not authorized to access this workflow.");
            return;
        }

        if (instanceExists || await TryReadAsync<AdmissionRecord>(WorkflowIdentity.RequestPath(requestId), cancellationToken) is not null)
        {
            throw new WorkflowForbiddenException("Caller is not authorized to access this workflow.");
        }

        try
        {
            await results.PutIfAbsentAsync(WorkflowIdentity.OwnerPath(requestId),
                new WorkflowOwnerRecord(1, requestId, instanceId, owner.OwnerId, owner.OrgId), cancellationToken);
        }
        catch (WorkflowConflictException)
        {
            throw new WorkflowForbiddenException("Caller is not authorized to access this workflow.");
        }
    }

    public async Task AuthorizeAccessAsync(string requestId, WorkflowOwner? caller, CancellationToken cancellationToken = default)
    {
        var ownerRecord = await TryReadAsync<WorkflowOwnerRecord>(WorkflowIdentity.OwnerPath(requestId), cancellationToken);
        if (ownerRecord is not null)
        {
            if (caller is null ||
                caller.OwnerId != ownerRecord.OwnerId ||
                caller.OrgId != ownerRecord.OrgId)
            {
                throw new WorkflowForbiddenException("Caller is not authorized to access this workflow.");
            }
        }
    }

    public async Task<bool> ExistsAsync(string requestId, DurableTaskClient client, CancellationToken cancellationToken = default)
    {
        var requestRecord = await TryReadAsync<AdmissionRecord>(WorkflowIdentity.RequestPath(requestId), cancellationToken);
        if (requestRecord is not null) return true;
        var instance = await client.GetInstanceAsync(WorkflowIdentity.InstanceId(requestId), getInputsAndOutputs: false, cancellationToken);
        return instance is not null;
    }

    private async Task<T?> TryReadAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await results.ReadAsync<T>(path, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public sealed record AdmissionRecord(int Version, string InstanceId, WorkflowRequest Request);
}
