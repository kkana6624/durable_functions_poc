using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using Poc.Core;

namespace Poc.Functions;

// Both owned and ownerless admissions compete for the same immutable Blob.
// Retain the claim after an uncertain signal so an identical retry can recover.
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
        var record = await ReadRecordAsync(input.RequestId, cancellationToken);
        if (record is not null) VerifyCallerAndInput(record, owner, input);

        var existing = await client.GetInstanceAsync(instanceId, getInputsAndOutputs: true, cancellationToken);
        if (existing is not null)
        {
            // Historical ownerless executions cannot acquire an owner through replay.
            if (record is null && owner is not null) ThrowForbidden();
            var previousInput = existing.ReadInputAs<WorkflowRequest>();
            if (record is not null && previousInput != record.Request)
                throw new InvalidDataException("Admission and orchestration input disagree.");
            if (previousInput != input)
                throw new WorkflowConflictException("requestId already has different configuration.");
        }

        var proposed = record ?? new WorkflowAdmissionRecord(2, instanceId, input, owner);
        try
        {
            await results.PutIfAbsentAsync(WorkflowIdentity.AdmissionPath(input.RequestId), proposed, cancellationToken);
        }
        catch (WorkflowConflictException)
        {
            // Classify against the atomic winner, including the null-owner boundary.
            var winner = await ReadRecordAsync(input.RequestId, cancellationToken)
                ?? throw new InvalidDataException("Admission winner is missing.");
            VerifyCallerAndInput(winner, owner, input);
            // Equivalent typed values with different serialized bytes are not a valid replay.
            throw new InvalidDataException("Admission content is not canonical.");
        }
        if (existing is not null) return instanceId;

        await client.Entities.SignalEntityAsync(new EntityInstanceId(nameof(WorkflowAdmissionEntity), instanceId),
            WorkflowAdmissionEntity.StartOperation, input, cancellation: cancellationToken);
        return instanceId;
    }

    public async Task AuthorizeAccessAsync(string requestId, WorkflowOwner? caller, CancellationToken cancellationToken = default)
    {
        caller?.Validate();
        var record = await ReadRecordAsync(requestId, cancellationToken);
        // Ownerless historical PoC reads remain available; they do not assign ownership.
        if (record?.Owner is not null && record.Owner != caller) ThrowForbidden();
    }

    public async Task<bool> ExistsAsync(string requestId, DurableTaskClient client, CancellationToken cancellationToken = default)
    {
        if (await ReadRecordAsync(requestId, cancellationToken) is not null) return true;
        var instance = await client.GetInstanceAsync(WorkflowIdentity.InstanceId(requestId), getInputsAndOutputs: false, cancellationToken);
        return instance is not null;
    }

    public async Task<WorkflowCancelRecord> CancelAsync(string requestId, WorkflowOwner? caller, string? reason = null, CancellationToken cancellationToken = default)
    {
        caller?.Validate();
        await AuthorizeAccessAsync(requestId, caller, cancellationToken);
        var record = new WorkflowCancelRecord(1, requestId, reason, DateTimeOffset.UtcNow, caller);
        try
        {
            await results.PutIfAbsentAsync(WorkflowIdentity.CancelPath(requestId), record, cancellationToken);
        }
        catch (WorkflowConflictException)
        {
            // Already recorded, idempotent
        }
        return record;
    }

    public async Task<WorkflowCancelRecord?> TryGetCancelRecordAsync(string requestId, CancellationToken cancellationToken = default)
    {
        return await TryReadAsync<WorkflowCancelRecord>(WorkflowIdentity.CancelPath(requestId), cancellationToken);
    }


    private async Task<WorkflowAdmissionRecord?> ReadRecordAsync(string requestId, CancellationToken cancellationToken)
    {
        var current = await TryReadAsync<WorkflowAdmissionRecord>(WorkflowIdentity.AdmissionPath(requestId), cancellationToken);
        var legacy = await TryReadAsync<AdmissionRecord>(WorkflowIdentity.RequestPath(requestId), cancellationToken);
        var legacyOwner = await TryReadAsync<WorkflowOwnerRecord>(WorkflowIdentity.OwnerPath(requestId), cancellationToken);
        var instanceId = WorkflowIdentity.InstanceId(requestId);
        if (current is not null) ValidateRecord(current, requestId);
        if (legacy is not null)
        {
            if (legacy.Version != 1 || legacy.InstanceId != instanceId || legacy.Request is null || legacy.Request.RequestId != requestId)
                throw new InvalidDataException("Invalid legacy admission identity or version.");
            ValidateStoredRequest(legacy.Request);
        }
        if (legacyOwner is not null)
        {
            if (legacy is null || legacyOwner.Version != 1 || legacyOwner.RequestId != requestId || legacyOwner.InstanceId != instanceId)
                throw new InvalidDataException("Invalid or orphaned legacy owner record.");
            ValidateStoredOwner(new WorkflowOwner(legacyOwner.OwnerId, legacyOwner.OrgId));
        }
        if (legacy is null) return current;
        var normalized = new WorkflowAdmissionRecord(2, instanceId, legacy.Request,
            legacyOwner is null ? null : new WorkflowOwner(legacyOwner.OwnerId, legacyOwner.OrgId));
        if (current is not null && current != normalized)
            throw new InvalidDataException("Current and legacy admission records disagree.");
        return current ?? normalized;
    }

    private static void ValidateRecord(WorkflowAdmissionRecord record, string requestId)
    {
        if (record.SchemaVersion != 2 || record.InstanceId != WorkflowIdentity.InstanceId(requestId) ||
            record.Request is null || record.Request.RequestId != requestId)
            throw new InvalidDataException("Invalid admission identity or schema version.");
        ValidateStoredRequest(record.Request);
        if (record.Owner is not null) ValidateStoredOwner(record.Owner);
    }

    private static void ValidateStoredRequest(WorkflowRequest request)
    {
        try { request.Validate(); }
        catch (ArgumentException) { throw new InvalidDataException("Invalid stored admission request."); }
    }

    private static void ValidateStoredOwner(WorkflowOwner owner)
    {
        try { owner.Validate(); }
        catch (ArgumentException) { throw new InvalidDataException("Invalid stored admission owner."); }
    }

    private static void VerifyCallerAndInput(WorkflowAdmissionRecord record, WorkflowOwner? caller, WorkflowRequest input)
    {
        if (record.Owner != caller) ThrowForbidden();
        if (record.Request != input)
            throw new WorkflowConflictException("requestId already has different configuration.");
    }

    private static void ThrowForbidden() =>
        throw new WorkflowForbiddenException("Caller is not authorized to access this workflow.");

    private async Task<T?> TryReadAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        try { return await results.ReadAsync<T>(path, cancellationToken); }
        catch (FileNotFoundException) { return null; }
        catch (KeyNotFoundException) { return null; }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    // Kept unchanged for persisted Entity state and legacy request.json compatibility.
    public sealed record AdmissionRecord(int Version, string InstanceId, WorkflowRequest Request);
}
