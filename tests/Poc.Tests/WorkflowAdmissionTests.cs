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
        var stored = Assert.Single(store.Documents);
        Assert.Equal(WorkflowIdentity.AdmissionPath(request.RequestId), stored.Key);
        var record = JsonSerializer.Deserialize<WorkflowAdmissionRecord>(stored.Value)!;
        Assert.Equal(2, record.SchemaVersion);
        Assert.Equal(request, record.Request);
        Assert.Null(record.Owner);
        entities.Verify(e => e.SignalEntityAsync(
            new EntityInstanceId(nameof(WorkflowAdmissionEntity), ids[0]), WorkflowAdmissionEntity.StartOperation,
            request, null, It.IsAny<CancellationToken>()), Times.Exactly(20));
        client.Verify(c => c.ScheduleNewOrchestrationInstanceAsync(It.IsAny<TaskName>(), It.IsAny<object>(),
            It.IsAny<StartOrchestrationOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConcurrentSameInputAndOwnerCreatesOneImmutableAdmission()
    {
        var request = new WorkflowRequest("owned-concurrent-001");
        var owner = new WorkflowOwner("alice", "finance");
        var store = new BarrierRepository(2, WorkflowIdentity.AdmissionPath(request.RequestId));
        var (client, entities) = Client();
        var admission = new WorkflowAdmission(store);

        var results = await RunRace(2, () => admission.AdmitAsync(request, client.Object, owner,
            TestContext.Current.CancellationToken));

        Assert.All(results, result => Assert.Equal(WorkflowIdentity.InstanceId(request.RequestId), result));
        var stored = Assert.Single(store.Documents);
        Assert.Equal(WorkflowIdentity.AdmissionPath(request.RequestId), stored.Key);
        var record = JsonSerializer.Deserialize<WorkflowAdmissionRecord>(stored.Value)!;
        Assert.Equal(2, record.SchemaVersion);
        Assert.Equal(request, record.Request);
        Assert.Equal(owner, record.Owner);
        entities.Verify(e => e.SignalEntityAsync(new EntityInstanceId(nameof(WorkflowAdmissionEntity), record.InstanceId),
            WorkflowAdmissionEntity.StartOperation, request, null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData("bob", "finance")]
    [InlineData("alice", "engineering")]
    public async Task ConcurrentDifferentOwnersOrOrganizationsForSameInputHaveOneWinnerAndForbidOther(string rivalOwnerId, string rivalOrgId)
    {
        var request = new WorkflowRequest("owner-race-001");
        var alice = new WorkflowOwner("alice", "finance");
        var rival = new WorkflowOwner(rivalOwnerId, rivalOrgId);
        var store = new BarrierRepository(2, WorkflowIdentity.AdmissionPath(request.RequestId));
        var (client, entities) = Client();
        var admission = new WorkflowAdmission(store);

        var attempts = await RunRace(2, async index =>
        {
            try
            {
                await admission.AdmitAsync(request, client.Object, index == 0 ? alice : rival,
                    TestContext.Current.CancellationToken);
                return (Error: (string?)null, Owner: index == 0 ? alice : rival);
            }
            catch (WorkflowForbiddenException) { return (Error: "Forbidden", Owner: index == 0 ? alice : rival); }
        });

        Assert.Single(attempts, result => result.Error is null);
        Assert.Single(attempts, result => result.Error == "Forbidden");
        var record = JsonSerializer.Deserialize<WorkflowAdmissionRecord>(
            Assert.Single(store.Documents).Value)!;
        Assert.Equal(attempts.Single(result => result.Error is null).Owner, record.Owner);
        Assert.Equal(request, record.Request);
        entities.Verify(e => e.SignalEntityAsync(It.IsAny<EntityInstanceId>(), It.IsAny<string>(),
            It.IsAny<object>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConcurrentOwnerlessAndOwnedFirstAdmissionsCannotClaimEachOther()
    {
        var request = new WorkflowRequest("ownerless-race-001");
        var owner = new WorkflowOwner("alice");
        var store = new BarrierRepository(2, WorkflowIdentity.AdmissionPath(request.RequestId));
        var (client, _) = Client();
        var admission = new WorkflowAdmission(store);

        var attempts = await RunRace(2, async index =>
        {
            try
            {
                await admission.AdmitAsync(request, client.Object, index == 0 ? null : owner,
                    TestContext.Current.CancellationToken);
                return (Error: (string?)null, Owner: index == 0 ? null : owner);
            }
            catch (WorkflowForbiddenException) { return (Error: "Forbidden", Owner: index == 0 ? null : owner); }
        });

        Assert.Single(attempts, result => result.Error is null);
        Assert.Single(attempts, result => result.Error == "Forbidden");
        var winner = JsonSerializer.Deserialize<WorkflowAdmissionRecord>(Assert.Single(store.Documents).Value)!;
        Assert.Equal(attempts.Single(result => result.Error is null).Owner, winner.Owner);
        Assert.Equal(request, winner.Request);
    }

    [Fact]
    public async Task ConcurrentSameOwnerDifferentInputsHaveOneWinnerAndConflictForOther()
    {
        var first = new WorkflowRequest("owned-input-race-001", TaskCount: 2);
        var second = first with { TaskCount = 3 };
        var owner = new WorkflowOwner("alice", "finance");
        var store = new BarrierRepository(2, WorkflowIdentity.AdmissionPath(first.RequestId));
        var (client, entities) = Client();
        var admission = new WorkflowAdmission(store);

        var attempts = await RunRace(2, async index =>
        {
            var input = index == 0 ? first : second;
            try
            {
                await admission.AdmitAsync(input, client.Object, owner, TestContext.Current.CancellationToken);
                return (Error: (string?)null, Input: input);
            }
            catch (WorkflowConflictException) { return (Error: "Conflict", Input: input); }
        });

        Assert.Single(attempts, result => result.Error is null);
        Assert.Single(attempts, result => result.Error == "Conflict");
        var winner = JsonSerializer.Deserialize<WorkflowAdmissionRecord>(Assert.Single(store.Documents).Value)!;
        Assert.Equal(attempts.Single(result => result.Error is null).Input, winner.Request);
        Assert.Equal(owner, winner.Owner);
        entities.Verify(e => e.SignalEntityAsync(It.IsAny<EntityInstanceId>(), It.IsAny<string>(),
            It.IsAny<object>(), null, It.IsAny<CancellationToken>()), Times.Once);
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
        var owner = new WorkflowOwner("alice", "finance");
        var admission = new WorkflowAdmission(store);
        await Assert.ThrowsAsync<HttpRequestException>(() => admission.AdmitAsync(request, client.Object, owner, TestContext.Current.CancellationToken));
        Assert.Single(store.Documents);
        var persisted = JsonSerializer.Deserialize<WorkflowAdmissionRecord>(store.Documents[WorkflowIdentity.AdmissionPath(request.RequestId)])!;
        Assert.Equal(owner, persisted.Owner);
        Assert.Equal(WorkflowIdentity.InstanceId(request.RequestId), await admission.AdmitAsync(request, client.Object, owner, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => admission.AdmitAsync(request with { TaskCount = 1 }, client.Object, owner, TestContext.Current.CancellationToken));
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
        Assert.True(store.Documents.ContainsKey(WorkflowIdentity.AdmissionPath(request.RequestId)));
        entities.Verify(e => e.SignalEntityAsync(It.IsAny<EntityInstanceId>(), It.IsAny<string>(),
            It.IsAny<object>(), null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConsistentLegacyRequestAndOwnerNormalizeOnlyForSameOwnerAndInput()
    {
        var store = new AtomicRepository();
        var (client, _) = Client();
        var request = new WorkflowRequest("legacy-pair-001");
        var owner = new WorkflowOwner("alice", "finance");
        await SeedLegacy(store, request, owner);
        var admission = new WorkflowAdmission(store);

        await Assert.ThrowsAsync<WorkflowForbiddenException>(() => admission.AdmitAsync(request, client.Object,
            new WorkflowOwner("bob", "finance"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => admission.AdmitAsync(request with { TaskCount = 1 },
            client.Object, owner, TestContext.Current.CancellationToken));
        Assert.False(store.Documents.ContainsKey(WorkflowIdentity.AdmissionPath(request.RequestId)));

        await admission.AdmitAsync(request, client.Object, owner, TestContext.Current.CancellationToken);
        var normalized = JsonSerializer.Deserialize<WorkflowAdmissionRecord>(
            store.Documents[WorkflowIdentity.AdmissionPath(request.RequestId)])!;
        Assert.Equal(request, normalized.Request);
        Assert.Equal(owner, normalized.Owner);
    }

    [Fact]
    public async Task LegacyRequestOnlyAllowsOwnerlessRetryButCannotBeUpgraded()
    {
        var store = new AtomicRepository();
        var (client, _) = Client();
        var request = new WorkflowRequest("legacy-ownerless-001");
        await SeedLegacy(store, request, owner: null);
        var admission = new WorkflowAdmission(store);

        await Assert.ThrowsAsync<WorkflowForbiddenException>(() => admission.AdmitAsync(request, client.Object,
            new WorkflowOwner("alice"), TestContext.Current.CancellationToken));
        Assert.False(store.Documents.ContainsKey(WorkflowIdentity.AdmissionPath(request.RequestId)));

        await admission.AdmitAsync(request, client.Object, owner: null, TestContext.Current.CancellationToken);
        var normalized = JsonSerializer.Deserialize<WorkflowAdmissionRecord>(
            store.Documents[WorkflowIdentity.AdmissionPath(request.RequestId)])!;
        Assert.Null(normalized.Owner);
        Assert.Equal(request, normalized.Request);
    }

    [Fact]
    public async Task OrphanLegacyOwnerAndContradictoryNewAndLegacyRecordsFailClosed()
    {
        var (client, _) = Client();
        var orphanStore = new AtomicRepository();
        var orphanRequest = new WorkflowRequest("legacy-orphan-owner-001");
        await orphanStore.PutIfAbsentAsync(WorkflowIdentity.OwnerPath(orphanRequest.RequestId),
            new WorkflowOwnerRecord(1, orphanRequest.RequestId, WorkflowIdentity.InstanceId(orphanRequest.RequestId), "alice"),
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => new WorkflowAdmission(orphanStore).AdmitAsync(
            orphanRequest, client.Object, new WorkflowOwner("alice"), TestContext.Current.CancellationToken));
        Assert.False(orphanStore.Documents.ContainsKey(WorkflowIdentity.AdmissionPath(orphanRequest.RequestId)));

        var contradictoryStore = new AtomicRepository();
        var request = new WorkflowRequest("legacy-disagreement-001");
        await contradictoryStore.PutIfAbsentAsync(WorkflowIdentity.AdmissionPath(request.RequestId),
            new WorkflowAdmissionRecord(2, WorkflowIdentity.InstanceId(request.RequestId), request, null),
            TestContext.Current.CancellationToken);
        await SeedLegacy(contradictoryStore, request with { TaskCount = 1 }, owner: null);
        await Assert.ThrowsAsync<InvalidDataException>(() => new WorkflowAdmission(contradictoryStore).AdmitAsync(
            request, client.Object, owner: null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstanceOnlyLegacyAdmissionAllowsOwnerlessRetryButNotOwnerUpgrade()
    {
        var store = new AtomicRepository();
        var (client, _) = Client();
        var request = new WorkflowRequest("legacy-instance-only-001");
        client.Setup(c => c.GetInstanceAsync(WorkflowIdentity.InstanceId(request.RequestId), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrchestrationMetadata(nameof(WorkflowFunctions.MainOrchestrator), WorkflowIdentity.InstanceId(request.RequestId))
            {
                DataConverter = new JsonDataConverter(),
                SerializedInput = JsonSerializer.Serialize(request),
                RuntimeStatus = OrchestrationRuntimeStatus.Running
            });
        var admission = new WorkflowAdmission(store);

        await Assert.ThrowsAsync<WorkflowForbiddenException>(() => admission.AdmitAsync(request, client.Object,
            new WorkflowOwner("alice"), TestContext.Current.CancellationToken));
        await admission.AdmitAsync(request, client.Object, owner: null, TestContext.Current.CancellationToken);
        var normalized = JsonSerializer.Deserialize<WorkflowAdmissionRecord>(
            store.Documents[WorkflowIdentity.AdmissionPath(request.RequestId)])!;
        Assert.Null(normalized.Owner);
        Assert.Equal(request, normalized.Request);
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
            Documents.TryGetValue(path, out var json)
                ? Task.FromResult(JsonSerializer.Deserialize<T>(json)!)
                : Task.FromException<T>(new FileNotFoundException(path));
        public string GetUri(string path) => "https://test.invalid/" + path;
    }

    private sealed class BarrierRepository(int participants, string barrierPath) : IResultRepository
    {
        private readonly AtomicRepository _inner = new();
        private readonly TaskCompletionSource _allInitialReads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _initialReads;
        public ConcurrentDictionary<string, string> Documents => _inner.Documents;

        public Task<string> PutIfAbsentAsync<T>(string path, T value, CancellationToken cancellationToken = default) =>
            _inner.PutIfAbsentAsync(path, value, cancellationToken);

        public async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
        {
            var missingAtRead = !Documents.TryGetValue(path, out var json);
            if (path == barrierPath && missingAtRead && Interlocked.Increment(ref _initialReads) <= participants)
            {
                if (Volatile.Read(ref _initialReads) == participants) _allInitialReads.TrySetResult();
                await _allInitialReads.Task.WaitAsync(cancellationToken);
            }
            if (missingAtRead) throw new FileNotFoundException(path);
            return JsonSerializer.Deserialize<T>(json!)!;
        }

        public string GetUri(string path) => _inner.GetUri(path);
    }

    private static async Task<TResult[]> RunRace<TResult>(int participants, Func<int, Task<TResult>> operation)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, participants).Select(index => Task.Run(async () =>
        {
            await start.Task;
            return await operation(index);
        })).ToArray();
        start.TrySetResult();
        return await Task.WhenAll(attempts);
    }

    private static Task<TResult[]> RunRace<TResult>(int participants, Func<Task<TResult>> operation) =>
        RunRace(participants, _ => operation());

    private static async Task SeedLegacy(AtomicRepository store, WorkflowRequest request, WorkflowOwner? owner)
    {
        var instanceId = WorkflowIdentity.InstanceId(request.RequestId);
        await store.PutIfAbsentAsync(WorkflowIdentity.RequestPath(request.RequestId),
            new WorkflowAdmission.AdmissionRecord(1, instanceId, request), TestContext.Current.CancellationToken);
        if (owner is not null)
        {
            await store.PutIfAbsentAsync(WorkflowIdentity.OwnerPath(request.RequestId),
                new WorkflowOwnerRecord(1, request.RequestId, instanceId, owner.OwnerId, owner.OrgId),
                TestContext.Current.CancellationToken);
        }
    }
}
