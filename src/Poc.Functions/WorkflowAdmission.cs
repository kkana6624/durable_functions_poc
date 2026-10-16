using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using Poc.Core;

namespace Poc.Functions;

// The immutable Blob claim arbitrates input conflicts; the entity serializes start commands.
// A claim is deliberately retained after an uncertain signal, so a retry can safely recover.
public sealed class WorkflowAdmission(IResultRepository results)
{
    public async Task<string> AdmitAsync(WorkflowRequest input, DurableTaskClient client,
        CancellationToken cancellationToken = default)
    {
        input.Validate();
        var instanceId = WorkflowIdentity.InstanceId(input.RequestId);
        var existing = await client.GetInstanceAsync(instanceId, getInputsAndOutputs: true, cancellationToken);
        // Also protect instances created before the immutable admission record was introduced.
        if (existing is not null && existing.ReadInputAs<WorkflowRequest>() != input)
            throw new WorkflowConflictException("requestId already has different configuration.");

        await results.PutIfAbsentAsync($"runs/poc-{input.RequestId}/request.json",
            new AdmissionRecord(1, instanceId, input), cancellationToken);
        if (existing is not null) return instanceId;

        await client.Entities.SignalEntityAsync(new EntityInstanceId(nameof(WorkflowAdmissionEntity), instanceId),
            WorkflowAdmissionEntity.StartOperation, input, cancellation: cancellationToken);
        return instanceId;
    }

    public sealed record AdmissionRecord(int Version, string InstanceId, WorkflowRequest Request);
}
