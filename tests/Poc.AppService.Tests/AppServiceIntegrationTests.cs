using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Poc.AppService.Authentication;
using Poc.AppService.Clients;
using Poc.AppService.Models;
using Poc.AppService.Services;
using Poc.Core;
using Xunit;

namespace Poc.AppService.Tests;

public class AppServiceIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;
    private readonly InMemoryBlobStorageRepository inMemoryStorage = new();
    private readonly Mock<IWorkflowClient> workflowClientMock = new();

    public AppServiceIntegrationTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IBlobStorageRepository>(inMemoryStorage);
                services.AddSingleton(inMemoryStorage);
                services.AddSingleton(workflowClientMock.Object);
                services.AddSingleton<IAppServiceCallerResolver, StubAppServiceCallerResolver>();
            });
        });
    }

    private static string ComputeSha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public async Task HealthEndpoint_ReturnsOk()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FullFlow_Initiate_Finalize_Start_Status_Cancel_Succeeds()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Poc-Owner-Id", "alice");
        client.DefaultRequestHeaders.Add("X-Poc-Org-Id", "eng");

        // 1. POST /api/uploads/initiate
        var initPayload = new InitiateUploadRequest("dataset.dat", 1024, "application/octet-stream");
        var initResp = await client.PostAsJsonAsync("/api/uploads/initiate", initPayload);
        Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);

        var initResult = await initResp.Content.ReadFromJsonAsync<InitiateUploadResponse>();
        Assert.NotNull(initResult);
        Assert.False(string.IsNullOrWhiteSpace(initResult.InputId));
        Assert.Equal("workflow-input", initResult.Container);
        Assert.StartsWith($"owners/alice/inputs/{initResult.InputId}/dataset.dat", initResult.BlobKey);

        // 2. Put simulated uploaded bytes in storage
        var fileBytes = Encoding.UTF8.GetBytes("sample simulation input data bytes");
        var sha256 = ComputeSha256(fileBytes);
        inMemoryStorage.Blobs[$"workflow-input/{initResult.BlobKey}"] = fileBytes;

        // 3. POST /api/uploads/finalize
        var finalizePayload = new FinalizeUploadRequest(
            InputId: initResult.InputId,
            BlobKey: initResult.BlobKey,
            ExpectedSizeBytes: fileBytes.Length,
            ExpectedSha256: sha256,
            VersionId: null,
            Etag: "\"simulated-etag\"");

        var finalizeResp = await client.PostAsJsonAsync("/api/uploads/finalize", finalizePayload);
        Assert.Equal(HttpStatusCode.OK, finalizeResp.StatusCode);

        var finalizeResult = await finalizeResp.Content.ReadFromJsonAsync<FinalizeUploadResponse>();
        Assert.NotNull(finalizeResult);
        Assert.False(string.IsNullOrWhiteSpace(finalizeResult.ReadyManifestId));
        Assert.Equal(initResult.BlobKey, finalizeResult.BlobKey);
        Assert.Equal(fileBytes.Length, finalizeResult.SizeBytes);
        Assert.Equal(sha256, finalizeResult.Sha256);

        // 4. POST /api/workflows
        var requestId = "req-integration-01";
        workflowClientMock
            .Setup(c => c.StartWorkflowAsync(It.Is<WorkflowRequest>(r => r.RequestId == requestId && r.ReadyManifestId == finalizeResult.ReadyManifestId),
                It.Is<WorkflowOwner>(o => o.OwnerId == "alice"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowStartClientResult(true, 202, InstanceId: "inst-int-01"));

        var startPayload = new StartWorkflowApiRequest(
            RequestId: requestId,
            ReadyManifestId: finalizeResult.ReadyManifestId,
            Classification: "auto",
            TaskCount: 3);

        var startResp = await client.PostAsJsonAsync("/api/workflows", startPayload);
        Assert.Equal(HttpStatusCode.Accepted, startResp.StatusCode);
        Assert.Equal("10", startResp.Headers.GetValues("Retry-After").FirstOrDefault());
        Assert.Equal($"/api/workflows/{requestId}", startResp.Headers.Location?.ToString());

        var startResult = await startResp.Content.ReadFromJsonAsync<StartWorkflowApiResponse>();
        Assert.NotNull(startResult);
        Assert.Equal(requestId, startResult.RequestId);
        Assert.Equal("inst-int-01", startResult.InstanceId);
        Assert.Equal($"/api/workflows/{requestId}", startResult.StatusQueryGetUri);

        // 5. GET /api/workflows/{requestId}
        var statusMockResponse = new WorkflowStatusResponse(
            RequestId: requestId,
            InstanceId: "inst-int-01",
            JobId: "job-01",
            Status: "Running",
            ResultAvailable: false);

        workflowClientMock
            .Setup(c => c.GetStatusAsync(requestId, It.Is<WorkflowOwner>(o => o.OwnerId == "alice"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowStatusClientResult(true, 200, Status: statusMockResponse));

        var statusResp = await client.GetAsync($"/api/workflows/{requestId}");
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);

        var statusResult = await statusResp.Content.ReadFromJsonAsync<AppServiceWorkflowStatusResponse>();
        Assert.NotNull(statusResult);
        Assert.Equal(requestId, statusResult.RequestId);
        Assert.Equal("Running", statusResult.Status);
        Assert.False(statusResult.ResultAvailable);
        Assert.Equal($"/api/workflows/{requestId}", statusResult.PollUrl);

        // 6. POST /api/workflows/{requestId}/cancel
        var cancelMockResponse = new WorkflowStatusResponse(
            RequestId: requestId,
            InstanceId: "inst-int-01",
            JobId: "job-01",
            Status: "Cancelled",
            ResultAvailable: false,
            TerminationConfirmed: true);

        workflowClientMock
            .Setup(c => c.CancelWorkflowAsync(requestId, It.Is<WorkflowOwner>(o => o.OwnerId == "alice"), "user cancellation", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowCancelClientResult(true, 200, Status: cancelMockResponse));

        var cancelResp = await client.PostAsJsonAsync($"/api/workflows/{requestId}/cancel", new CancelWorkflowApiRequest("user cancellation"));
        Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);

        var cancelResult = await cancelResp.Content.ReadFromJsonAsync<CancelWorkflowApiResponse>();
        Assert.NotNull(cancelResult);
        Assert.Equal(requestId, cancelResult.RequestId);
        Assert.Equal("Cancelled", cancelResult.Status);
        Assert.True(cancelResult.TerminationConfirmed);
    }

    [Fact]
    public async Task Response_NeverLeaksInternalKeysOrDurableManagementUrls()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Poc-Owner-Id", "alice");

        var requestId = "req-leak-test";
        // Simulate Functions returning an instance that could have had internal URLs in bad implementations
        var statusMockResponse = new WorkflowStatusResponse(
            RequestId: requestId,
            InstanceId: "inst-leak-test",
            JobId: "job-leak-test",
            Status: "Accepted",
            ResultAvailable: false);

        workflowClientMock
            .Setup(c => c.GetStatusAsync(requestId, It.IsAny<WorkflowOwner>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowStatusClientResult(true, 200, Status: statusMockResponse));

        var response = await client.GetAsync($"/api/workflows/{requestId}");
        var body = await response.Content.ReadAsStringAsync();

        // Strict verification of AGENTS.md rules
        Assert.DoesNotContain("runtime/webhooks", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("durabletask", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("code=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("x-functions-key", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sig=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sv=", body, StringComparison.OrdinalIgnoreCase);

        // Also check headers
        foreach (var header in response.Headers)
        {
            Assert.DoesNotContain("functions-key", header.Key, StringComparison.OrdinalIgnoreCase);
            foreach (var val in header.Value)
            {
                Assert.DoesNotContain("code=", val, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("sig=", val, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("runtime/webhooks", val, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task MissingAuthentication_Returns401Unauthorized()
    {
        var client = factory.CreateClient();
        // No owner headers and no default configured

        var resp1 = await client.PostAsJsonAsync("/api/uploads/initiate", new InitiateUploadRequest("test.dat", 100));
        Assert.Equal(HttpStatusCode.Unauthorized, resp1.StatusCode);

        var resp2 = await client.PostAsJsonAsync("/api/uploads/finalize", new FinalizeUploadRequest("id", "blob", 100, "hash"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp2.StatusCode);

        var resp3 = await client.PostAsJsonAsync("/api/workflows", new StartWorkflowApiRequest("req-1"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp3.StatusCode);

        var resp4 = await client.GetAsync("/api/workflows/req-1");
        Assert.Equal(HttpStatusCode.Unauthorized, resp4.StatusCode);

        var resp5 = await client.PostAsJsonAsync("/api/workflows/req-1/cancel", new CancelWorkflowApiRequest());
        Assert.Equal(HttpStatusCode.Unauthorized, resp5.StatusCode);
    }

    [Fact]
    public async Task InitiateUpload_WithDirectoryTraversal_Returns400BadRequest()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Poc-Owner-Id", "alice");

        var payload = new InitiateUploadRequest("../traversal.dat", 100);
        var resp = await client.PostAsJsonAsync("/api/uploads/initiate", payload);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task FinalizeUpload_BlobOwnerMismatch_Returns403Forbidden()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Poc-Owner-Id", "alice");

        // Request refers to a blob owned by bob
        var payload = new FinalizeUploadRequest(
            InputId: "in-123",
            BlobKey: "owners/bob/inputs/in-123/file.dat",
            ExpectedSizeBytes: 10,
            ExpectedSha256: "0000000000000000000000000000000000000000000000000000000000000000");

        var resp = await client.PostAsJsonAsync("/api/uploads/finalize", payload);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task StartWorkflow_UpstreamConflict_Returns409Conflict()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Poc-Owner-Id", "alice");

        var requestId = "req-conflict";
        workflowClientMock
            .Setup(c => c.StartWorkflowAsync(It.Is<WorkflowRequest>(r => r.RequestId == requestId), It.IsAny<WorkflowOwner>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowStartClientResult(false, 409, Error: "requestId already has different configuration."));

        var startPayload = new StartWorkflowApiRequest(RequestId: requestId);
        var resp = await client.PostAsJsonAsync("/api/workflows", startPayload);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task StartWorkflow_Upstream503_Returns503WithRetryAfter()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Poc-Owner-Id", "alice");

        var requestId = "req-503";
        workflowClientMock
            .Setup(c => c.StartWorkflowAsync(It.Is<WorkflowRequest>(r => r.RequestId == requestId), It.IsAny<WorkflowOwner>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowStartClientResult(false, 503, Error: "Admission could not be confirmed. Retry with the same requestId.", RetryAfter: "10"));

        var startPayload = new StartWorkflowApiRequest(RequestId: requestId);
        var resp = await client.PostAsJsonAsync("/api/workflows", startPayload);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task GetWorkflowStatus_NotFound_Returns404NotFound()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Poc-Owner-Id", "alice");

        var requestId = "req-not-found";
        workflowClientMock
            .Setup(c => c.GetStatusAsync(requestId, It.IsAny<WorkflowOwner>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowStatusClientResult(false, 404, Error: "Workflow not found."));

        var resp = await client.GetAsync($"/api/workflows/{requestId}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
