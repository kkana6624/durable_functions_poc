using System.Net;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.Entities;
using Microsoft.DurableTask.Converters;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Poc.Core;
using Poc.Functions;
using Xunit;

namespace Poc.Tests;

public class WorkflowHttpTests
{
    [Theory]
    [InlineData("not a json payload")]
    [InlineData("{ invalid json ")]
    public async Task StartWorkflow_InvalidJsonSyntax_ReturnsBadRequest(string invalidJson)
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var request = CreateRequest(invalidJson);

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.Equal("Workflow request is invalid.", errorProp.GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("""{"classification": "auto"}""")]
    [InlineData("""{"requestId": ""}""")]
    [InlineData("""{"requestId": "   "}""")]
    [InlineData("""{"requestId": "-invalid-start-hyphen"}""")]
    [InlineData("""{"requestId": "valid-id", "classification": "unknown"}""")]
    [InlineData("""{"requestId": "valid-id", "taskCount": 0}""")]
    [InlineData("""{"requestId": "valid-id", "taskCount": 4}""")]
    public async Task StartWorkflow_MissingOrInvalidFields_ReturnsBadRequest(string invalidBody)
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var request = CreateRequest(invalidBody);

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.Equal("Workflow request is invalid.", errorProp.GetString());
    }

    [Fact]
    public async Task StartWorkflow_AdmissionConflict_ReturnsConflictWithInstanceId()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var workflowReq = new WorkflowRequest("req-conflict-123");
        await repo.PutIfAbsentAsync(WorkflowIdentity.AdmissionPath(workflowReq.RequestId),
            new WorkflowAdmissionRecord(2, WorkflowIdentity.InstanceId(workflowReq.RequestId), workflowReq with { TaskCount = 1 }, null),
            TestContext.Current.CancellationToken);
        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var request = CreateRequest(JsonSerializer.Serialize(workflowReq));

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.Equal("requestId already has different configuration.", errorProp.GetString());
        Assert.True(body.TryGetProperty("instanceId", out var instanceIdProp));
        Assert.Equal(WorkflowIdentity.InstanceId("req-conflict-123"), instanceIdProp.GetString());
    }

    [Fact]
    public async Task StartWorkflow_TransientRpcUnavailable_ReturnsServiceUnavailableWithRetryAfterHeader()
    {
        var (client, _) = CreateDurableClient();
        var repoMock = new Mock<IResultRepository>();
        repoMock.Setup(r => r.PutIfAbsentAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RpcException(new Status(StatusCode.Unavailable, "Durable backend temporarily unreachable.")));

        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repoMock.Object), new PocFunctionKeyCallerResolver());
        var workflowReq = new WorkflowRequest("req-transient-503");
        var request = CreateRequest(JsonSerializer.Serialize(workflowReq));

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var retryAfterValues));
        Assert.Equal("10", Assert.Single(retryAfterValues));

        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.Equal("Admission could not be confirmed. Retry with the same requestId and input.", errorProp.GetString());
    }

    [Fact]
    public async Task StartWorkflow_PermanentRpcFailure_ReturnsFixedServerError()
    {
        var (client, _) = CreateDurableClient();
        var repo = new Mock<IResultRepository>();
        repo.Setup(r => r.PutIfAbsentAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RpcException(new Status(StatusCode.PermissionDenied, "SECRET_SENTINEL")));
        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repo.Object), new PocFunctionKeyCallerResolver());
        var request = CreateRequest(JsonSerializer.Serialize(new WorkflowRequest("req-permanent-rpc")));

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.Equal("Workflow admission could not be confirmed.", body.GetProperty("error").GetString());
        Assert.DoesNotContain("SECRET_SENTINEL", body.GetRawText());
    }

    [Theory]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.ResourceExhausted)]
    public async Task StartWorkflow_TransientRpcOtherCodes_ReturnsServiceUnavailableWithRetryAfterHeader(StatusCode code)
    {
        var (client, _) = CreateDurableClient();
        var repoMock = new Mock<IResultRepository>();
        repoMock.Setup(r => r.PutIfAbsentAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RpcException(new Status(code, "gRPC transient issue.")));

        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repoMock.Object), new PocFunctionKeyCallerResolver());
        var workflowReq = new WorkflowRequest($"req-transient-{code}");
        var request = CreateRequest(JsonSerializer.Serialize(workflowReq));

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var retryAfterValues));
        Assert.Equal("10", Assert.Single(retryAfterValues));

        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.Equal("Admission could not be confirmed. Retry with the same requestId and input.", errorProp.GetString());
    }

    [Fact]
    public async Task StartWorkflow_SuccessfulAdmission_ReturnsAcceptedWithLocationAndStatusPayload()
    {
        var (client, entities) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var workflowReq = new WorkflowRequest("req-success-202");
        var request = CreateRequest(JsonSerializer.Serialize(workflowReq),
            "https://myfunctions.azurewebsites.net/tenant/api/workflows?code=secret-function-key");

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Location header verification
        Assert.True(response.Headers.TryGetValues("Location", out var locationValues));
        var location = Assert.Single(locationValues);
        Assert.Equal("https://myfunctions.azurewebsites.net/tenant/api/workflows/req-success-202", location);
        Assert.DoesNotContain("secret-function-key", location);
        Assert.DoesNotContain("runtime/webhooks", location, StringComparison.OrdinalIgnoreCase);

        // Retry-After header verification
        Assert.True(response.Headers.TryGetValues("Retry-After", out var retryAfterValues));
        Assert.Equal("10", Assert.Single(retryAfterValues));

        // Body verification
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        var expectedInstanceId = WorkflowIdentity.InstanceId("req-success-202");

        Assert.True(body.TryGetProperty("id", out var idProp));
        Assert.Equal(expectedInstanceId, idProp.GetString());

        Assert.True(body.TryGetProperty("instanceId", out var instanceIdProp));
        Assert.Equal(expectedInstanceId, instanceIdProp.GetString());

        Assert.True(body.TryGetProperty("statusQueryGetUri", out var statusUriProp));
        Assert.Equal(location, statusUriProp.GetString());
        Assert.DoesNotContain("secret-function-key", body.GetRawText());

        var statusUri = new Uri(statusUriProp.GetString()!);
        Assert.Equal("myfunctions.azurewebsites.net", statusUri.Host);
        Assert.Equal("/tenant/api/workflows/req-success-202", statusUri.AbsolutePath);
        Assert.Empty(statusUri.Query);

        // Verify entity was signaled for orchestration start
        entities.Verify(e => e.SignalEntityAsync(
            new EntityInstanceId(nameof(WorkflowAdmissionEntity), expectedInstanceId),
            WorkflowAdmissionEntity.StartOperation,
            workflowReq,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("alice", null)]
    [InlineData("alice", "org-1")]
    [InlineData("user@domain.com", "org.sub_domain-2")]
    [InlineData("a", null)]
    public void WorkflowOwner_ValidInputs_PassValidation(string ownerId, string? orgId)
    {
        var owner = new WorkflowOwner(ownerId, orgId);
        owner.Validate();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("-starts-hyphen", null)]
    [InlineData("_starts-underscore", null)]
    [InlineData("@starts-at", null)]
    [InlineData(".starts-dot", null)]
    [InlineData("invalid spaces", null)]
    [InlineData("invalid$char", null)]
    public void WorkflowOwner_InvalidOwnerId_ThrowsArgumentException(string? ownerId, string? orgId)
    {
        var owner = new WorkflowOwner(ownerId!, orgId);
        Assert.Throws<ArgumentException>(() => owner.Validate());
    }

    [Theory]
    [InlineData("valid-owner", "")]
    [InlineData("valid-owner", "   ")]
    [InlineData("valid-owner", "-starts-hyphen")]
    [InlineData("valid-owner", "invalid spaces")]
    [InlineData("valid-owner", "invalid#char")]
    public void WorkflowOwner_InvalidOrgId_ThrowsArgumentException(string ownerId, string? orgId)
    {
        var owner = new WorkflowOwner(ownerId, orgId);
        Assert.Throws<ArgumentException>(() => owner.Validate());
    }

    [Fact]
    public async Task StartWorkflow_WithOwnerHeaders_BindsOwnershipAndReturnsAccepted()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var req = new WorkflowRequest("req-owner-001");
        var headers = new Dictionary<string, string>
        {
            ["X-Poc-Owner-Id"] = "alice",
            ["X-Poc-Org-Id"] = "finance-dept"
        };
        var request = CreateRequest(JsonSerializer.Serialize(req), headers: headers);

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var admissionRecord = await repo.ReadAsync<WorkflowAdmissionRecord>(WorkflowIdentity.AdmissionPath("req-owner-001"), TestContext.Current.CancellationToken);
        Assert.Equal(new WorkflowOwner("alice", "finance-dept"), admissionRecord.Owner);
        Assert.Equal(req, admissionRecord.Request);
        Assert.False(repo.Documents.ContainsKey(WorkflowIdentity.OwnerPath("req-owner-001")));
    }

    [Fact]
    public async Task StartWorkflow_ReplayWithSameOwner_SucceedsWith202()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var req = new WorkflowRequest("req-owner-replay-001");
        var headers = new Dictionary<string, string>
        {
            ["X-Poc-Owner-Id"] = "alice",
            ["X-Poc-Org-Id"] = "finance-dept"
        };

        var response1 = await functions.StartWorkflow(CreateRequest(JsonSerializer.Serialize(req), headers: headers), client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response1.StatusCode);

        var response2 = await functions.StartWorkflow(CreateRequest(JsonSerializer.Serialize(req), headers: headers), client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response2.StatusCode);
    }

    [Fact]
    public async Task StartWorkflow_ReplayWithDifferentOwner_Returns403ForbiddenWithoutSecrets()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var req = new WorkflowRequest("req-owner-forbidden-001");

        var response1 = await functions.StartWorkflow(CreateRequest(JsonSerializer.Serialize(req), headers: new Dictionary<string, string>
        {
            ["X-Poc-Owner-Id"] = "alice",
            ["X-Poc-Org-Id"] = "finance-dept"
        }), client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response1.StatusCode);

        var response2 = await functions.StartWorkflow(CreateRequest(JsonSerializer.Serialize(req), headers: new Dictionary<string, string>
        {
            ["X-Poc-Owner-Id"] = "bob",
            ["X-Poc-Org-Id"] = "finance-dept"
        }), client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response2.StatusCode);
        var body = await ReadJsonBodyAsync(response2, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.Equal("Caller is not authorized to access this workflow.", errorProp.GetString());
        Assert.False(body.TryGetProperty("instanceId", out _));
        Assert.False(body.TryGetProperty("statusQueryGetUri", out _));
        Assert.False(response2.Headers.Contains("Location"));
    }

    [Fact]
    public async Task StartWorkflow_ReplayWithoutOwnerOnOwnedWorkflow_Returns403Forbidden()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var req = new WorkflowRequest("req-owner-anon-forbidden");

        await functions.StartWorkflow(CreateRequest(JsonSerializer.Serialize(req), headers: new Dictionary<string, string>
        {
            ["X-Poc-Owner-Id"] = "alice"
        }), client.Object, TestContext.Current.CancellationToken);

        var response = await functions.StartWorkflow(CreateRequest(JsonSerializer.Serialize(req)), client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.False(body.TryGetProperty("instanceId", out _));
        Assert.False(body.TryGetProperty("statusQueryGetUri", out _));
    }

    [Fact]
    public async Task StartWorkflow_OrgIdWithoutOwnerId_Returns400BadRequest()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var req = new WorkflowRequest("req-org-without-owner");
        var headers = new Dictionary<string, string> { ["X-Poc-Org-Id"] = "org-only" };

        var response = await functions.StartWorkflow(CreateRequest(JsonSerializer.Serialize(req), headers: headers), client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.Equal("Workflow request is invalid.", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task StartWorkflow_InvalidOwnerIdHeader_Returns400BadRequest()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var req = new WorkflowRequest("req-bad-owner-header");
        var headers = new Dictionary<string, string> { ["X-Poc-Owner-Id"] = "-invalid-leading-hyphen" };

        var response = await functions.StartWorkflow(CreateRequest(JsonSerializer.Serialize(req), headers: headers), client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StartWorkflow_ReplayWithOwnerOnUnownedWorkflow_Returns403Forbidden()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var req = new WorkflowRequest("req-unowned-first");

        // 1. Initial unowned admission
        var resp1 = await functions.StartWorkflow(CreateRequest(JsonSerializer.Serialize(req)), client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, resp1.StatusCode);

        // 2. Attacker / caller tries to retroactively claim it with an owner header
        var resp2 = await functions.StartWorkflow(CreateRequest(JsonSerializer.Serialize(req), headers: new Dictionary<string, string>
        {
            ["X-Poc-Owner-Id"] = "eve"
        }), client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, resp2.StatusCode);
        var body = await ReadJsonBodyAsync(resp2, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.Equal("Caller is not authorized to access this workflow.", errorProp.GetString());
    }

    [Fact]
    public async Task StartWorkflow_OrgIdMismatchWhenCreatedWithoutOrg_Returns403Forbidden()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var req = new WorkflowRequest("req-no-org-first");

        // 1. Initial admission with owner only (no org)
        var resp1 = await functions.StartWorkflow(CreateRequest(JsonSerializer.Serialize(req), headers: new Dictionary<string, string>
        {
            ["X-Poc-Owner-Id"] = "alice"
        }), client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, resp1.StatusCode);

        // 2. Replay passing an extra OrgId
        var resp2 = await functions.StartWorkflow(CreateRequest(JsonSerializer.Serialize(req), headers: new Dictionary<string, string>
        {
            ["X-Poc-Owner-Id"] = "alice",
            ["X-Poc-Org-Id"] = "extra-org"
        }), client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, resp2.StatusCode);
    }

    [Fact]
    public async Task GetWorkflowStatus_OrgIdMismatchWhenCreatedWithoutOrg_Returns403Forbidden()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var requestId = "req-status-symmetric-org";
        var instanceId = WorkflowIdentity.InstanceId(requestId);

        await repo.PutIfAbsentAsync(WorkflowIdentity.RequestPath(requestId),
            new WorkflowAdmission.AdmissionRecord(1, instanceId, new WorkflowRequest(requestId)), TestContext.Current.CancellationToken);
        // Created without OrgId
        await repo.PutIfAbsentAsync(WorkflowIdentity.OwnerPath(requestId),
            new WorkflowOwnerRecord(1, requestId, instanceId, "alice", null), TestContext.Current.CancellationToken);

        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());

        // Caller specifies extra OrgId
        var request = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}", method: "GET",
            headers: new Dictionary<string, string> { ["X-Poc-Owner-Id"] = "alice", ["X-Poc-Org-Id"] = "extra-org" });

        var response = await functions.GetWorkflowStatus(request, requestId, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StartWorkflow_OwnerlessHistoricalInstanceCannotBeClaimedByOwner()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var requestId = "req-conflict-no-poison";
        var instanceId = WorkflowIdentity.InstanceId(requestId);

        var originalRequest = new WorkflowRequest(requestId, Classification: "auto", TaskCount: 2);
        client.Setup(c => c.GetInstanceAsync(instanceId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrchestrationMetadata(nameof(WorkflowFunctions.MainOrchestrator), instanceId)
            {
                DataConverter = new JsonDataConverter(),
                SerializedInput = JsonSerializer.Serialize(originalRequest),
                RuntimeStatus = OrchestrationRuntimeStatus.Running
            });

        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());

        // Submit different input with owner header
        var conflictingRequest = new WorkflowRequest(requestId, Classification: "auto", TaskCount: 3);
        var response = await functions.StartWorkflow(CreateRequest(JsonSerializer.Serialize(conflictingRequest), headers: new Dictionary<string, string>
        {
            ["X-Poc-Owner-Id"] = "mallory"
        }), client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The historical instance cannot acquire a new owner or a new admission claim.
        Assert.False(repo.Documents.ContainsKey(WorkflowIdentity.OwnerPath(requestId)));
        Assert.False(repo.Documents.ContainsKey(WorkflowIdentity.AdmissionPath(requestId)));
    }

    [Theory]
    [InlineData(null, null, "Accepted", false, null, null)]
    [InlineData(OrchestrationRuntimeStatus.Pending, null, "Accepted", false, null, null)]
    [InlineData(OrchestrationRuntimeStatus.Running, """{"state": "Submitting"}""", "Preparing", false, null, null)]
    [InlineData(OrchestrationRuntimeStatus.Running, """{"state": "Persisting"}""", "Publishing", false, null, null)]
    [InlineData(OrchestrationRuntimeStatus.Running, """{"state": "Stopping", "termination": {"completed": false}}""", "Stopping", false, false, null)]
    [InlineData(OrchestrationRuntimeStatus.Running, """{"state": "Stopping", "termination": {"completed": true}}""", "Stopping", false, true, null)]
    [InlineData(OrchestrationRuntimeStatus.Running, """{"state": "Cancelling"}""", "Cancelling", false, null, null)]
    [InlineData(OrchestrationRuntimeStatus.Running, null, "Running", false, null, null)]
    [InlineData(OrchestrationRuntimeStatus.Running, """{"state": "SomethingElse"}""", "Running", false, null, null)]
    [InlineData(OrchestrationRuntimeStatus.Completed, null, "Succeeded", false, null, null)]
    [InlineData(OrchestrationRuntimeStatus.Failed, """{"state": "TimedOut", "termination": {"completed": true}}""", "TimedOut", false, true, "Workflow exceeded its monitoring deadline.")]
    [InlineData(OrchestrationRuntimeStatus.Failed, """{"state": "TimedOut", "termination": {"completed": false}}""", "TimedOut", false, false, "Workflow exceeded its monitoring deadline.")]
    [InlineData(OrchestrationRuntimeStatus.Failed, """{"state": "TimedOut", "termination": {"completed": true}, "terminationError": "SECRET_SENTINEL"}""", "TimedOut", false, false, "Batch job termination could not be confirmed.")]
    [InlineData(OrchestrationRuntimeStatus.Failed, """{"state": "Failed", "error": "SECRET_SENTINEL"}""", "Failed", false, null, "Workflow execution failed.")]
    [InlineData(OrchestrationRuntimeStatus.Terminated, null, "Cancelled", false, null, null)]
    public void NormalizeStatus_MapsCorrectly(
        OrchestrationRuntimeStatus? status,
        string? customStatus,
        string expectedStatus,
        bool expectedResultAvailable,
        bool? expectedTerminationConfirmed,
        string? expectedError)
    {
        var requestId = "req-norm-test";
        OrchestrationMetadata? instance = status.HasValue
            ? new OrchestrationMetadata(nameof(WorkflowFunctions.MainOrchestrator), WorkflowIdentity.InstanceId(requestId))
            {
                RuntimeStatus = status.Value,
                SerializedCustomStatus = customStatus
            }
            : null;

        var normalized = WorkflowFunctions.NormalizeStatus(requestId, instance);

        Assert.Equal(requestId, normalized.RequestId);
        Assert.Equal(WorkflowIdentity.InstanceId(requestId), normalized.InstanceId);
        Assert.Equal(WorkflowIdentity.JobId(requestId), normalized.JobId);
        Assert.Equal(expectedStatus, normalized.Status);
        Assert.Equal(expectedResultAvailable, normalized.ResultAvailable);
        Assert.Equal(expectedTerminationConfirmed, normalized.TerminationConfirmed);
        Assert.Equal(expectedError, normalized.Error);
        Assert.DoesNotContain("SECRET_SENTINEL", JsonSerializer.Serialize(normalized));
    }

    [Fact]
    public void NormalizeStatus_CanceledStatus_MapsToCancelled()
    {
        var requestId = "req-canceled-status";
#pragma warning disable CS0618
        var instance = new OrchestrationMetadata(nameof(WorkflowFunctions.MainOrchestrator), WorkflowIdentity.InstanceId(requestId))
        {
            RuntimeStatus = OrchestrationRuntimeStatus.Canceled
        };
#pragma warning restore CS0618

        var normalized = WorkflowFunctions.NormalizeStatus(requestId, instance);
        Assert.Equal("Cancelled", normalized.Status);
        Assert.False(normalized.ResultAvailable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-invalid-id")]
    [InlineData("invalid spaces")]
    public async Task GetWorkflowStatus_InvalidRequestId_ReturnsBadRequest(string invalidRequestId)
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var request = CreateRequest("", url: $"https://localhost/api/workflows/{invalidRequestId}", method: "GET");

        var response = await functions.GetWorkflowStatus(request, invalidRequestId, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.Equal("Workflow request is invalid.", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetWorkflowStatus_InvalidOwnerHeader_ReturnsBadRequest()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var headers = new Dictionary<string, string> { ["X-Poc-Owner-Id"] = "-invalid-owner" };
        var request = CreateRequest("", url: "https://localhost/api/workflows/valid-req", method: "GET", headers: headers);

        var response = await functions.GetWorkflowStatus(request, "valid-req", client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.Equal("Workflow request is invalid.", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetWorkflowStatus_NotFound_WhenWorkflowDoesNotExist()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var request = CreateRequest("", url: "https://localhost/api/workflows/non-existent-req", method: "GET");

        var response = await functions.GetWorkflowStatus(request, "non-existent-req", client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.Equal("Workflow not found.", errorProp.GetString());
        Assert.True(body.TryGetProperty("requestId", out var reqProp));
        Assert.Equal("non-existent-req", reqProp.GetString());
    }

    [Fact]
    public async Task GetWorkflowStatus_PreRegistrationWindow_Returns200Accepted()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var requestId = "req-pre-reg-001";
        var instanceId = WorkflowIdentity.InstanceId(requestId);
        await repo.PutIfAbsentAsync(WorkflowIdentity.RequestPath(requestId),
            new WorkflowAdmission.AdmissionRecord(1, instanceId, new WorkflowRequest(requestId)), TestContext.Current.CancellationToken);

        client.Setup(c => c.GetInstanceAsync(instanceId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrchestrationMetadata?)null);

        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var request = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}", method: "GET");

        var response = await functions.GetWorkflowStatus(request, requestId, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.Equal(requestId, body.GetProperty("requestId").GetString());
        Assert.Equal(instanceId, body.GetProperty("instanceId").GetString());
        Assert.Equal("Accepted", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("resultAvailable").GetBoolean());
        // Verify secrets are hidden
        Assert.False(body.TryGetProperty("statusQueryGetUri", out _));
    }

    [Fact]
    public async Task GetWorkflowStatus_OldCustomStatusError_IsNotReflected()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var requestId = "req-status-secret-mask";
        var instanceId = WorkflowIdentity.InstanceId(requestId);
        await repo.PutIfAbsentAsync(WorkflowIdentity.RequestPath(requestId),
            new WorkflowAdmission.AdmissionRecord(1, instanceId, new WorkflowRequest(requestId)), TestContext.Current.CancellationToken);
        client.Setup(c => c.GetInstanceAsync(instanceId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrchestrationMetadata(nameof(WorkflowFunctions.MainOrchestrator), instanceId)
            {
                RuntimeStatus = OrchestrationRuntimeStatus.Failed,
                SerializedCustomStatus = """{"state":"Failed","error":"SECRET_SENTINEL","terminationError":"SECRET_SENTINEL"}"""
            });

        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var request = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}", method: "GET");
        var response = await functions.GetWorkflowStatus(request, requestId, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.Equal("Workflow execution failed.", body.GetProperty("error").GetString());
        Assert.DoesNotContain("SECRET_SENTINEL", body.GetRawText());
    }

    [Fact]
    public async Task GetWorkflowStatus_CompletedWithoutVerifiedResult_ReportsResultUnavailable()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var requestId = "req-status-no-result";
        var instanceId = WorkflowIdentity.InstanceId(requestId);
        await repo.PutIfAbsentAsync(WorkflowIdentity.RequestPath(requestId),
            new WorkflowAdmission.AdmissionRecord(1, instanceId, new WorkflowRequest(requestId)), TestContext.Current.CancellationToken);
        client.Setup(c => c.GetInstanceAsync(instanceId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrchestrationMetadata(nameof(WorkflowFunctions.MainOrchestrator), instanceId)
            {
                RuntimeStatus = OrchestrationRuntimeStatus.Completed
            });

        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var statusRequest = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}", method: "GET");
        var statusResponse = await functions.GetWorkflowStatus(statusRequest, requestId, client.Object, TestContext.Current.CancellationToken);
        var statusBody = await ReadJsonBodyAsync(statusResponse, TestContext.Current.CancellationToken);
        Assert.Equal("Succeeded", statusBody.GetProperty("status").GetString());
        Assert.False(statusBody.GetProperty("resultAvailable").GetBoolean());

        var resultRequest = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}/result", method: "GET");
        var resultResponse = await functions.GetWorkflowResult(resultRequest, requestId, client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, resultResponse.StatusCode);
    }

    [Theory]
    [InlineData(StatusCode.Unavailable, HttpStatusCode.ServiceUnavailable, "Workflow status could not be verified. Retry later.")]
    [InlineData(StatusCode.PermissionDenied, HttpStatusCode.InternalServerError, "Workflow status could not be verified.")]
    public async Task GetWorkflowStatus_RepositoryFailure_ReturnsFixedResponse(StatusCode grpcStatus, HttpStatusCode expectedStatus, string expectedError)
    {
        var (client, _) = CreateDurableClient();
        var requestId = "req-status-repository-error";
        var instanceId = WorkflowIdentity.InstanceId(requestId);
        var repo = new Mock<IResultRepository>();
        repo.Setup(r => r.ReadAsync<WorkflowAdmissionRecord>(WorkflowIdentity.AdmissionPath(requestId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowAdmissionRecord(2, instanceId, new WorkflowRequest(requestId), null));
        repo.Setup(r => r.ReadAsync<WorkflowAdmission.AdmissionRecord>(WorkflowIdentity.RequestPath(requestId), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException());
        repo.Setup(r => r.ReadAsync<WorkflowOwnerRecord>(WorkflowIdentity.OwnerPath(requestId), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException());
        repo.Setup(r => r.ReadAsync<ManifestResult>(WorkflowIdentity.ManifestPath(requestId), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RpcException(new Status(grpcStatus, "SECRET_SENTINEL")));
        client.Setup(c => c.GetInstanceAsync(instanceId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrchestrationMetadata?)null);

        var functions = new WorkflowFunctions(CreateOperations(repo.Object), new WorkflowAdmission(repo.Object), new PocFunctionKeyCallerResolver());
        var request = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}", method: "GET");
        var response = await functions.GetWorkflowStatus(request, requestId, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, response.StatusCode);
        if (expectedStatus == HttpStatusCode.ServiceUnavailable)
        {
            Assert.True(response.Headers.TryGetValues("Retry-After", out var retryAfter));
            Assert.Equal("10", Assert.Single(retryAfter));
        }
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.Equal(expectedError, body.GetProperty("error").GetString());
        Assert.DoesNotContain("SECRET_SENTINEL", body.GetRawText());

        var resultRequest = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}/result", method: "GET");
        var resultResponse = await functions.GetWorkflowResult(resultRequest, requestId, client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(expectedStatus, resultResponse.StatusCode);
        if (expectedStatus == HttpStatusCode.ServiceUnavailable)
        {
            Assert.True(resultResponse.Headers.TryGetValues("Retry-After", out var retryAfter));
            Assert.Equal("10", Assert.Single(retryAfter));
        }
        var resultBody = await ReadJsonBodyAsync(resultResponse, TestContext.Current.CancellationToken);
        var expectedResultError = expectedStatus == HttpStatusCode.ServiceUnavailable
            ? "Workflow result could not be verified. Retry later."
            : "Workflow result could not be verified.";
        Assert.Equal(expectedResultError, resultBody.GetProperty("error").GetString());
        Assert.DoesNotContain("SECRET_SENTINEL", resultBody.GetRawText());
    }

    [Fact]
    public async Task StartWorkflow_MissingOwnerFieldInAdmissionRecordFailsClosed()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var request = new WorkflowRequest("req-missing-owner-field");
        repo.Documents[WorkflowIdentity.AdmissionPath(request.RequestId)] = JsonSerializer.Serialize(new
        {
            SchemaVersion = 2,
            InstanceId = WorkflowIdentity.InstanceId(request.RequestId),
            Request = request
        });
        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());

        var response = await functions.StartWorkflow(CreateRequest(JsonSerializer.Serialize(request)), client.Object,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.Equal("Workflow admission could not be confirmed.", body.GetProperty("error").GetString());
        Assert.DoesNotContain("InstanceId", body.GetRawText());
        Assert.DoesNotContain("req-missing-owner-field", body.GetRawText());
    }

    [Fact]
    public async Task GetWorkflowStatusAndResult_InvalidPublishedArtifacts_ReturnFixed500WithoutDetails()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var requestId = "req-invalid-published";
        var instanceId = WorkflowIdentity.InstanceId(requestId);
        await repo.PutIfAbsentAsync(WorkflowIdentity.RequestPath(requestId),
            new WorkflowAdmission.AdmissionRecord(1, instanceId, new WorkflowRequest(requestId)), TestContext.Current.CancellationToken);
        repo.Documents[WorkflowIdentity.ManifestPath(requestId)] = "not-json-SECRET_SENTINEL";
        client.Setup(c => c.GetInstanceAsync(instanceId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrchestrationMetadata?)null);

        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var statusRequest = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}", method: "GET");
        var statusResponse = await functions.GetWorkflowStatus(statusRequest, requestId, client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, statusResponse.StatusCode);
        var statusBody = await ReadJsonBodyAsync(statusResponse, TestContext.Current.CancellationToken);
        Assert.Equal("Published workflow results are inconsistent.", statusBody.GetProperty("error").GetString());
        Assert.DoesNotContain("SECRET_SENTINEL", statusBody.GetRawText());

        var resultRequest = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}/result", method: "GET");
        var resultResponse = await functions.GetWorkflowResult(resultRequest, requestId, client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, resultResponse.StatusCode);
        var resultBody = await ReadJsonBodyAsync(resultResponse, TestContext.Current.CancellationToken);
        Assert.Equal("Published workflow results are inconsistent.", resultBody.GetProperty("error").GetString());
        Assert.DoesNotContain("SECRET_SENTINEL", resultBody.GetRawText());
    }

    [Fact]
    public async Task GetWorkflowStatus_OwnerAuthorization_EnforcedAndNoSecretsLeaked()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var requestId = "req-owned-status-001";
        var instanceId = WorkflowIdentity.InstanceId(requestId);

        await repo.PutIfAbsentAsync(WorkflowIdentity.RequestPath(requestId),
            new WorkflowAdmission.AdmissionRecord(1, instanceId, new WorkflowRequest(requestId)), TestContext.Current.CancellationToken);
        await repo.PutIfAbsentAsync(WorkflowIdentity.OwnerPath(requestId),
            new WorkflowOwnerRecord(1, requestId, instanceId, "alice", "org-finance"), TestContext.Current.CancellationToken);

        var instance = new OrchestrationMetadata(nameof(WorkflowFunctions.MainOrchestrator), instanceId)
        {
            RuntimeStatus = OrchestrationRuntimeStatus.Running,
            SerializedCustomStatus = JsonSerializer.Serialize(new { state = "Submitting" })
        };
        client.Setup(c => c.GetInstanceAsync(instanceId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);

        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());

        // Missing headers -> 403 Forbidden
        var noHeaderReq = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}", method: "GET");
        var respNoHeader = await functions.GetWorkflowStatus(noHeaderReq, requestId, client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, respNoHeader.StatusCode);
        var bodyForbidden = await ReadJsonBodyAsync(respNoHeader, TestContext.Current.CancellationToken);
        Assert.False(bodyForbidden.TryGetProperty("statusQueryGetUri", out _));
        Assert.False(bodyForbidden.TryGetProperty("instanceId", out _));

        // Wrong owner -> 403 Forbidden
        var wrongOwnerReq = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}", method: "GET",
            headers: new Dictionary<string, string> { ["X-Poc-Owner-Id"] = "bob", ["X-Poc-Org-Id"] = "org-finance" });
        var respWrongOwner = await functions.GetWorkflowStatus(wrongOwnerReq, requestId, client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, respWrongOwner.StatusCode);

        // Wrong org -> 403 Forbidden
        var wrongOrgReq = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}", method: "GET",
            headers: new Dictionary<string, string> { ["X-Poc-Owner-Id"] = "alice", ["X-Poc-Org-Id"] = "org-engineering" });
        var respWrongOrg = await functions.GetWorkflowStatus(wrongOrgReq, requestId, client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, respWrongOrg.StatusCode);

        // Correct owner -> 200 OK with normalized status
        var authorizedReq = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}", method: "GET",
            headers: new Dictionary<string, string> { ["X-Poc-Owner-Id"] = "alice", ["X-Poc-Org-Id"] = "org-finance" });
        var respOk = await functions.GetWorkflowStatus(authorizedReq, requestId, client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, respOk.StatusCode);
        var bodyOk = await ReadJsonBodyAsync(respOk, TestContext.Current.CancellationToken);
        Assert.Equal("Preparing", bodyOk.GetProperty("status").GetString());
        Assert.False(bodyOk.TryGetProperty("statusQueryGetUri", out _));
    }

    [Fact]
    public async Task GetWorkflowResult_NotFound_WhenWorkflowDoesNotExist()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var request = CreateRequest("", url: "https://localhost/api/workflows/non-existent/result", method: "GET");

        var response = await functions.GetWorkflowResult(request, "non-existent", client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetWorkflowResult_OwnerMismatch_Returns403Forbidden()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var requestId = "req-result-owner-check";
        var instanceId = WorkflowIdentity.InstanceId(requestId);

        await repo.PutIfAbsentAsync(WorkflowIdentity.RequestPath(requestId),
            new WorkflowAdmission.AdmissionRecord(1, instanceId, new WorkflowRequest(requestId)), TestContext.Current.CancellationToken);
        await repo.PutIfAbsentAsync(WorkflowIdentity.OwnerPath(requestId),
            new WorkflowOwnerRecord(1, requestId, instanceId, "alice"), TestContext.Current.CancellationToken);

        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var request = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}/result", method: "GET",
            headers: new Dictionary<string, string> { ["X-Poc-Owner-Id"] = "bob" });

        var response = await functions.GetWorkflowResult(request, requestId, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.Equal("Caller is not authorized to access this workflow.", errorProp.GetString());
        Assert.False(body.TryGetProperty("instanceId", out _));
    }

    [Fact]
    public async Task GetWorkflowResult_WhenWorkflowStillRunning_Returns409ConflictWithNormalizedStatus()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var requestId = "req-result-running";
        var instanceId = WorkflowIdentity.InstanceId(requestId);

        await repo.PutIfAbsentAsync(WorkflowIdentity.RequestPath(requestId),
            new WorkflowAdmission.AdmissionRecord(1, instanceId, new WorkflowRequest(requestId)), TestContext.Current.CancellationToken);

        var instance = new OrchestrationMetadata(nameof(WorkflowFunctions.MainOrchestrator), instanceId)
        {
            RuntimeStatus = OrchestrationRuntimeStatus.Running,
            SerializedCustomStatus = JsonSerializer.Serialize(new { state = "Submitting" })
        };
        client.Setup(c => c.GetInstanceAsync(instanceId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);

        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var request = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}/result", method: "GET");

        var response = await functions.GetWorkflowResult(request, requestId, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.Equal("Workflow result is not available.", errorProp.GetString());
        Assert.Equal(requestId, body.GetProperty("requestId").GetString());
        Assert.Equal("Preparing", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetWorkflowResult_WhenWorkflowFailed_Returns409ConflictWithNormalizedStatus()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var requestId = "req-result-failed";
        var instanceId = WorkflowIdentity.InstanceId(requestId);

        await repo.PutIfAbsentAsync(WorkflowIdentity.RequestPath(requestId),
            new WorkflowAdmission.AdmissionRecord(1, instanceId, new WorkflowRequest(requestId)), TestContext.Current.CancellationToken);

        var instance = new OrchestrationMetadata(nameof(WorkflowFunctions.MainOrchestrator), instanceId)
        {
            RuntimeStatus = OrchestrationRuntimeStatus.Failed,
            SerializedCustomStatus = JsonSerializer.Serialize(new { state = "TimedOut", termination = new { completed = true } })
        };
        client.Setup(c => c.GetInstanceAsync(instanceId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);

        var functions = new WorkflowFunctions(CreateOperations(repo), new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var request = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}/result", method: "GET");

        var response = await functions.GetWorkflowResult(request, requestId, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.Equal("TimedOut", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetWorkflowResult_WhenPublished_Returns200WithResultResponseWithoutSecrets()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var requestId = "req-result-published-001";
        var workflowRequest = new WorkflowRequest(requestId, "light", 3);
        var operations = CreateOperations(repo);
        var plan = await operations.ClassifyAsync(workflowRequest, TestContext.Current.CancellationToken);
        await operations.FetchAndPersistAsync(plan, TestContext.Current.CancellationToken);
        await operations.AggregateAsync(plan, TestContext.Current.CancellationToken);
        await operations.PublishManifestAsync(plan, TestContext.Current.CancellationToken);
        var jobId = plan.JobId;
        var fingerprint = plan.Fingerprint;

        await repo.PutIfAbsentAsync(WorkflowIdentity.RequestPath(requestId),
            new WorkflowAdmission.AdmissionRecord(1, WorkflowIdentity.InstanceId(requestId), new WorkflowRequest(requestId)), TestContext.Current.CancellationToken);

        var instanceId = WorkflowIdentity.InstanceId(requestId);
        client.Setup(c => c.GetInstanceAsync(instanceId, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrchestrationMetadata(nameof(WorkflowFunctions.MainOrchestrator), instanceId)
            {
                RuntimeStatus = OrchestrationRuntimeStatus.Completed
            });

        var functions = new WorkflowFunctions(operations, new WorkflowAdmission(repo), new PocFunctionKeyCallerResolver());
        var statusRequest = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}", method: "GET");
        var statusResponse = await functions.GetWorkflowStatus(statusRequest, requestId, client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var statusBody = await ReadJsonBodyAsync(statusResponse, TestContext.Current.CancellationToken);
        Assert.True(statusBody.GetProperty("resultAvailable").GetBoolean());

        var request = CreateRequest("", url: $"https://localhost/api/workflows/{requestId}/result", method: "GET");

        var response = await functions.GetWorkflowResult(request, requestId, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);

        Assert.Equal(requestId, body.GetProperty("requestId").GetString());
        Assert.Equal(jobId, body.GetProperty("jobId").GetString());
        Assert.Equal(fingerprint, body.GetProperty("fingerprint").GetString());
        Assert.Equal("Succeeded", body.GetProperty("status").GetString());
        Assert.Equal(3, body.GetProperty("taskCount").GetInt32());
        Assert.Equal(3, body.GetProperty("sum").GetInt32());

        var taskIds = body.GetProperty("taskIds").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(["task-00", "task-01", "task-02"], taskIds);

        // Assert secret hiding: No URI / SAS / function key in response
        Assert.False(body.TryGetProperty("aggregateUri", out _));
        Assert.False(body.TryGetProperty("taskUris", out _));
        Assert.False(body.TryGetProperty("statusQueryGetUri", out _));
    }

    private static WorkflowOperations CreateOperations(IResultRepository repo) =>
        new(new FakeBatchGateway(), repo, new StubWorkloadClassifier(), new WorkflowSettings());

    private static (Mock<DurableTaskClient> Client, Mock<DurableEntityClient> Entities) CreateDurableClient()
    {
        var client = new Mock<DurableTaskClient>("test");
        var entities = new Mock<DurableEntityClient>("test");
        client.SetupGet(c => c.Entities).Returns(entities.Object);
        client.Setup(c => c.GetInstanceAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrchestrationMetadata?)null);
        client.Setup(c => c.GetInstanceAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrchestrationMetadata?)null);
        entities.Setup(e => e.SignalEntityAsync(It.IsAny<EntityInstanceId>(), It.IsAny<string>(),
            It.IsAny<object>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return (client, entities);
    }

    private static FakeHttpRequestData CreateRequest(string body, string url = "https://localhost:7071/api/workflows", string method = "POST", IDictionary<string, string>? headers = null)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<WorkerOptions>(options =>
        {
            options.Serializer = new Azure.Core.Serialization.JsonObjectSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });
        var sp = services.BuildServiceProvider();

        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.InstanceServices).Returns(sp);

        var request = new FakeHttpRequestData(context.Object, body, new Uri(url), method);
        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                request.Headers.Add(k, v);
            }
        }
        return request;
    }

    private static async Task<JsonElement> ReadJsonBodyAsync(HttpResponseData response, CancellationToken cancellationToken)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, Encoding.UTF8);
        var content = await reader.ReadToEndAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.Clone();
    }
}

internal sealed class FakeHttpRequestData : HttpRequestData
{
    private readonly MemoryStream _bodyStream;
    private readonly HttpHeadersCollection _headers = new();

    public FakeHttpRequestData(FunctionContext functionContext, string body = "", Uri? url = null, string method = "POST")
        : base(functionContext)
    {
        _bodyStream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        Url = url ?? new Uri("https://localhost/api/workflows");
        Method = method;
    }

    public override Stream Body => _bodyStream;
    public override HttpHeadersCollection Headers => _headers;
    public override IReadOnlyCollection<IHttpCookie> Cookies => Array.Empty<IHttpCookie>();
    public override Uri Url { get; }
    public override IEnumerable<System.Security.Claims.ClaimsIdentity> Identities => Array.Empty<System.Security.Claims.ClaimsIdentity>();
    public override string Method { get; }

    public override HttpResponseData CreateResponse()
    {
        return new FakeHttpResponseData(FunctionContext);
    }
}

internal sealed class FakeHttpResponseData : HttpResponseData
{
    public FakeHttpResponseData(FunctionContext functionContext) : base(functionContext)
    {
    }

    public override HttpStatusCode StatusCode { get; set; }
    public override HttpHeadersCollection Headers { get; set; } = new();
    public override Stream Body { get; set; } = new MemoryStream();
    public override HttpCookies Cookies => throw new NotImplementedException();
}
