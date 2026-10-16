using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.Entities;
using Microsoft.DurableTask.Converters;
using Microsoft.DurableTask.Entities;
using Moq;
using Poc.Core;
using Poc.Functions;
using Xunit;

namespace Poc.Tests;

public class WorkflowAdmissionTests
{
    [Fact]
    public async Task ConcurrentIdenticalRequestsClaimOneInputAndSignalSameEntity()
    {
        var store = new AtomicRepository();
        var (client, entities) = Client();
        var request = new WorkflowRequest("concurrent-001");
        var admission = new WorkflowAdmission(store);
        var ids = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => admission.AdmitAsync(request, client.Object, TestContext.Current.CancellationToken))));
        Assert.All(ids, id => Assert.Equal(WorkflowIdentity.InstanceId(request.RequestId), id));
        Assert.Single(store.Documents);
        entities.Verify(e => e.SignalEntityAsync(
            new EntityInstanceId(nameof(WorkflowAdmissionEntity), ids[0]), WorkflowAdmissionEntity.StartOperation,
            request, null, It.IsAny<CancellationToken>()), Times.Exactly(20));
        client.Verify(c => c.ScheduleNewOrchestrationInstanceAsync(It.IsAny<TaskName>(), It.IsAny<object>(),
            It.IsAny<StartOrchestrationOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConcurrentDifferentInputsHaveOneWinnerAndConflictForOtherInput()
    {
        var store = new AtomicRepository();
        var (client, entities) = Client();
        var first = new WorkflowRequest("conflicting-001");
        var second = first with { TaskCount = 1 };
        var admission = new WorkflowAdmission(store);
        var outcomes = await Task.WhenAll(new[] { first, second }.Select(input => Task.Run(async () =>
        {
            try { await admission.AdmitAsync(input, client.Object, TestContext.Current.CancellationToken); return "Accepted"; }
            catch (WorkflowConflictException) { return "Conflict"; }
        })));
        Assert.Single(outcomes, value => value == "Accepted");
        Assert.Single(outcomes, value => value == "Conflict");
        entities.Verify(e => e.SignalEntityAsync(It.IsAny<EntityInstanceId>(), It.IsAny<string>(),
            It.IsAny<object>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LostSignalResponseRetainsClaimAndAllowsSameInputRetry()
    {
        var store = new AtomicRepository();
        var (client, entities) = Client();
        entities.SetupSequence(e => e.SignalEntityAsync(It.IsAny<EntityInstanceId>(), It.IsAny<string>(),
            It.IsAny<object>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Response lost after enqueue."))
            .Returns(Task.CompletedTask);
        var request = new WorkflowRequest("lost-response-001");
        var admission = new WorkflowAdmission(store);
        await Assert.ThrowsAsync<HttpRequestException>(() => admission.AdmitAsync(request, client.Object, TestContext.Current.CancellationToken));
        Assert.Single(store.Documents);
        Assert.Equal(WorkflowIdentity.InstanceId(request.RequestId), await admission.AdmitAsync(request, client.Object, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => admission.AdmitAsync(request with { TaskCount = 1 }, client.Object, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RepeatedEntitySignalsIncludingAfterStateReloadScheduleOnlyOnce()
    {
        var request = new WorkflowRequest("entity-001");
        var context = new Mock<TaskEntityContext>();
        context.SetupGet(c => c.Id).Returns(new EntityInstanceId(nameof(WorkflowAdmissionEntity), WorkflowIdentity.InstanceId(request.RequestId)));
        object? persisted = null;
        for (var i = 0; i < 5; i++)
        {
            var state = new Mock<TaskEntityState>();
            state.Setup(s => s.GetState<WorkflowAdmission.AdmissionRecord>(null!)).Returns(() => (WorkflowAdmission.AdmissionRecord)persisted!);
            state.Setup(s => s.SetState(It.IsAny<object>())).Callback<object>(value =>
                persisted = JsonSerializer.Deserialize<WorkflowAdmission.AdmissionRecord>(JsonSerializer.Serialize(value)));
            var operation = new Mock<TaskEntityOperation>();
            operation.SetupGet(o => o.Name).Returns(WorkflowAdmissionEntity.StartOperation);
            operation.SetupGet(o => o.State).Returns(state.Object);
            operation.SetupGet(o => o.Context).Returns(context.Object);
            operation.Setup(o => o.GetInput<WorkflowRequest>()).Returns(request);
            WorkflowAdmissionEntity.Handle(operation.Object);
        }
        context.Verify(c => c.ScheduleNewOrchestration(nameof(WorkflowFunctions.MainOrchestrator), request,
            It.Is<StartOrchestrationOptions>(o => o.InstanceId == WorkflowIdentity.InstanceId(request.RequestId))), Times.Once);
    }

    [Fact]
    public async Task ExistingLegacyInstanceIsReusedAndDifferentInputDoesNotPoisonClaim()
    {
        var store = new AtomicRepository();
        var (client, entities) = Client();
        var request = new WorkflowRequest("legacy-001");
        client.Setup(c => c.GetInstanceAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrchestrationMetadata(nameof(WorkflowFunctions.MainOrchestrator), WorkflowIdentity.InstanceId(request.RequestId))
            {
                DataConverter = new JsonDataConverter(),
                SerializedInput = JsonSerializer.Serialize(request),
                RuntimeStatus = OrchestrationRuntimeStatus.Completed
            });
        var admission = new WorkflowAdmission(store);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => admission.AdmitAsync(request with { TaskCount = 1 }, client.Object, TestContext.Current.CancellationToken));
        Assert.Empty(store.Documents);
        Assert.Equal(WorkflowIdentity.InstanceId(request.RequestId), await admission.AdmitAsync(request, client.Object, TestContext.Current.CancellationToken));
        entities.Verify(e => e.SignalEntityAsync(It.IsAny<EntityInstanceId>(), It.IsAny<string>(),
            It.IsAny<object>(), null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void EntityRejectsForeignIdentityWithoutScheduling()
    {
        var context = new Mock<TaskEntityContext>();
        context.SetupGet(c => c.Id).Returns(new EntityInstanceId(nameof(WorkflowAdmissionEntity), "foreign"));
        var operation = new Mock<TaskEntityOperation>();
        operation.SetupGet(o => o.Name).Returns(WorkflowAdmissionEntity.StartOperation);
        operation.SetupGet(o => o.Context).Returns(context.Object);
        operation.Setup(o => o.GetInput<WorkflowRequest>()).Returns(new WorkflowRequest("entity-002"));
        Assert.Throws<ArgumentException>(() => WorkflowAdmissionEntity.Handle(operation.Object));
        context.Verify(c => c.ScheduleNewOrchestration(It.IsAny<TaskName>(), It.IsAny<object>(),
            It.IsAny<StartOrchestrationOptions>()), Times.Never);
    }

    private static (Mock<DurableTaskClient>, Mock<DurableEntityClient>) Client()
    {
        var client = new Mock<DurableTaskClient>("test");
        var entities = new Mock<DurableEntityClient>("test");
        client.SetupGet(c => c.Entities).Returns(entities.Object);
        client.Setup(c => c.GetInstanceAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>())).ReturnsAsync((OrchestrationMetadata?)null);
        entities.Setup(e => e.SignalEntityAsync(It.IsAny<EntityInstanceId>(), It.IsAny<string>(),
            It.IsAny<object>(), null, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return (client, entities);
    }

    private sealed class AtomicRepository : IResultRepository
    {
        public ConcurrentDictionary<string, string> Documents { get; } = new();
        public Task<string> PutIfAbsentAsync<T>(string path, T value, CancellationToken cancellationToken = default)
        {
            var content = JsonSerializer.Serialize(value);
            if (Documents.GetOrAdd(path, content) != content) throw new WorkflowConflictException("Existing claim differs.");
            return Task.FromResult(GetUri(path));
        }
        public Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.Deserialize<T>(Documents[path])!);
        public string GetUri(string path) => "https://test.invalid/" + path;
    }
}
