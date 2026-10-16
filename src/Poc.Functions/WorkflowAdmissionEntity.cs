using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Entities;
using Poc.Core;

namespace Poc.Functions;

public static class WorkflowAdmissionEntity
{
    public const string StartOperation = "Start";

    [Function(nameof(WorkflowAdmissionEntity))]
    public static Task Run([EntityTrigger] TaskEntityDispatcher dispatcher) =>
        dispatcher.DispatchAsync(operation => ValueTask.FromResult(Handle(operation)));

    // No external I/O: state and the outgoing start message belong to the entity checkpoint.
    public static object? Handle(TaskEntityOperation operation)
    {
        if (operation.Name != StartOperation) throw new ArgumentException("Unsupported admission operation.");
        var input = operation.GetInput<WorkflowRequest>() ?? throw new ArgumentException("Workflow input missing.");
        input.Validate();
        var instanceId = WorkflowIdentity.InstanceId(input.RequestId);
        if (operation.Context.Id.Key != instanceId) throw new ArgumentException("Admission entity identity mismatch.");
        var existing = operation.State.GetState<WorkflowAdmission.AdmissionRecord>();
        if (existing is not null)
        {
            if (existing != new WorkflowAdmission.AdmissionRecord(1, instanceId, input))
                throw new WorkflowConflictException("Admission entity already has different input.");
            return null;
        }

        var options = new StartOrchestrationOptions { InstanceId = instanceId };
        operation.Context.ScheduleNewOrchestration(nameof(WorkflowFunctions.MainOrchestrator), input, options);
        operation.State.SetState(new WorkflowAdmission.AdmissionRecord(1, instanceId, input));
        return null;
    }
}
