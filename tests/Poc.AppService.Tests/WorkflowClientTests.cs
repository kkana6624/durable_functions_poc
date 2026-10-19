using System.Net;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Options;
using Moq;
using Poc.AppService.Clients;
using Poc.Core;
using Xunit;

namespace Poc.AppService.Tests;

public class WorkflowClientTests
{
    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }

    [Fact]
    public async Task StartWorkflowAsync_WithFunctionKey_SendsXFunctionsKeyAndOwnerHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new DelegatingHandlerStub(req =>
        {
            capturedRequest = req;
            var response = new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { instanceId = "instance-1" }))
            };
            return Task.FromResult(response);
        });

        var httpClient = new HttpClient(handler);
        var options = Options.Create(new WorkflowClientOptions
        {
            FunctionAppUrl = "https://poc-functions.azurewebsites.net",
            AuthMode = "PocFunctionKey",
            FunctionKey = "secret-function-key-1234"
        });

        var client = new WorkflowClient(httpClient, options);
        var owner = new WorkflowOwner("alice", "eng");
        var request = new WorkflowRequest("req-1");

        var result = await client.StartWorkflowAsync(request, owner);

        Assert.True(result.Success);
        Assert.Equal(202, result.StatusCode);
        Assert.Equal("instance-1", result.InstanceId);
        Assert.NotNull(capturedRequest);

        Assert.True(capturedRequest.Headers.TryGetValues("x-functions-key", out var keyValues));
        Assert.Equal("secret-function-key-1234", keyValues.First());

        Assert.True(capturedRequest.Headers.TryGetValues("X-Poc-Owner-Id", out var ownerValues));
        Assert.Equal("alice", ownerValues.First());

        Assert.True(capturedRequest.Headers.TryGetValues("X-Poc-Org-Id", out var orgValues));
        Assert.Equal("eng", orgValues.First());
    }

    [Fact]
    public async Task StartWorkflowAsync_WithEntraBearer_InjectsBearerToken()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new DelegatingHandlerStub(req =>
        {
            capturedRequest = req;
            var response = new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { instanceId = "instance-2" }))
            };
            return Task.FromResult(response);
        });

        var httpClient = new HttpClient(handler);
        var options = Options.Create(new WorkflowClientOptions
        {
            FunctionAppUrl = "https://poc-functions.azurewebsites.net",
            AuthMode = "EntraBearer",
            EntraAudience = "api://workflow-backend"
        });

        var tokenCredentialMock = new Mock<TokenCredential>();
        tokenCredentialMock
            .Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken("test-bearer-token", DateTimeOffset.UtcNow.AddHours(1)));

        var client = new WorkflowClient(httpClient, options, tokenCredentialMock.Object);
        var owner = new WorkflowOwner("bob");
        var request = new WorkflowRequest("req-2");

        var result = await client.StartWorkflowAsync(request, owner);

        Assert.True(result.Success);
        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.Headers.Authorization);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization.Scheme);
        Assert.Equal("test-bearer-token", capturedRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task GetStatusAsync_UpstreamReturnsStatus_DeserializesCorrectly()
    {
        var statusResponse = new WorkflowStatusResponse("req-1", "inst-1", "job-1", "Running", ResultAvailable: false);
        var handler = new DelegatingHandlerStub(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(statusResponse, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            };
            return Task.FromResult(response);
        });

        var httpClient = new HttpClient(handler);
        var options = Options.Create(new WorkflowClientOptions { FunctionAppUrl = "https://test" });
        var client = new WorkflowClient(httpClient, options);

        var result = await client.GetStatusAsync("req-1", new WorkflowOwner("alice"));

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(result.Status);
        Assert.Equal("Running", result.Status.Status);
        Assert.Equal("req-1", result.Status.RequestId);
    }

    [Fact]
    public async Task CancelWorkflowAsync_UpstreamReturnsCancelled_DeserializesCorrectly()
    {
        var statusResponse = new WorkflowStatusResponse("req-1", "inst-1", "job-1", "Cancelled", ResultAvailable: false, TerminationConfirmed: true);
        var handler = new DelegatingHandlerStub(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(statusResponse, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            };
            return Task.FromResult(response);
        });

        var httpClient = new HttpClient(handler);
        var options = Options.Create(new WorkflowClientOptions { FunctionAppUrl = "https://test" });
        var client = new WorkflowClient(httpClient, options);

        var result = await client.CancelWorkflowAsync("req-1", new WorkflowOwner("alice"), "user requested");

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(result.Status);
        Assert.Equal("Cancelled", result.Status.Status);
        Assert.True(result.Status.TerminationConfirmed);
    }
}
