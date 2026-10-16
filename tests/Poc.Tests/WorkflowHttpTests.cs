using System.Net;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.Entities;
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
        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repo));
        var request = CreateRequest(invalidJson);

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.False(string.IsNullOrWhiteSpace(errorProp.GetString()));
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
        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repo));
        var request = CreateRequest(invalidBody);

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.False(string.IsNullOrWhiteSpace(errorProp.GetString()));
    }

    [Fact]
    public async Task StartWorkflow_AdmissionConflict_ReturnsConflictWithInstanceId()
    {
        var (client, _) = CreateDurableClient();
        var repoMock = new Mock<IResultRepository>();
        repoMock.Setup(r => r.PutIfAbsentAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WorkflowConflictException("Existing document differs."));

        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repoMock.Object));
        var workflowReq = new WorkflowRequest("req-conflict-123");
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

        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repoMock.Object));
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

    [Theory]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.ResourceExhausted)]
    public async Task StartWorkflow_TransientRpcOtherCodes_ReturnsServiceUnavailableWithRetryAfterHeader(StatusCode code)
    {
        var (client, _) = CreateDurableClient();
        var repoMock = new Mock<IResultRepository>();
        repoMock.Setup(r => r.PutIfAbsentAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RpcException(new Status(code, "gRPC transient issue.")));

        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repoMock.Object));
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
        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repo));
        var workflowReq = new WorkflowRequest("req-success-202");
        var request = CreateRequest(JsonSerializer.Serialize(workflowReq), "https://myfunctions.azurewebsites.net/api/workflows");

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Location header verification
        Assert.True(response.Headers.TryGetValues("Location", out var locationValues));
        var location = Assert.Single(locationValues);
        Assert.NotEmpty(location);

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

        // Verify entity was signaled for orchestration start
        entities.Verify(e => e.SignalEntityAsync(
            new EntityInstanceId(nameof(WorkflowAdmissionEntity), expectedInstanceId),
            WorkflowAdmissionEntity.StartOperation,
            workflowReq,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (Mock<DurableTaskClient> Client, Mock<DurableEntityClient> Entities) CreateDurableClient()
    {
        var client = new Mock<DurableTaskClient>("test");
        var entities = new Mock<DurableEntityClient>("test");
        client.SetupGet(c => c.Entities).Returns(entities.Object);
        client.Setup(c => c.GetInstanceAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrchestrationMetadata?)null);
        entities.Setup(e => e.SignalEntityAsync(It.IsAny<EntityInstanceId>(), It.IsAny<string>(),
            It.IsAny<object>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return (client, entities);
    }

    private static FakeHttpRequestData CreateRequest(string body, string url = "https://localhost:7071/api/workflows")
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<WorkerOptions>(options =>
        {
            options.Serializer = new Azure.Core.Serialization.JsonObjectSerializer();
        });
        var sp = services.BuildServiceProvider();

        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.InstanceServices).Returns(sp);

        return new FakeHttpRequestData(context.Object, body, new Uri(url));
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

    public FakeHttpRequestData(FunctionContext functionContext, string body = "", Uri? url = null)
        : base(functionContext)
    {
        _bodyStream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        Url = url ?? new Uri("https://localhost/api/workflows");
    }

    public override Stream Body => _bodyStream;
    public override HttpHeadersCollection Headers => _headers;
    public override IReadOnlyCollection<IHttpCookie> Cookies => Array.Empty<IHttpCookie>();
    public override Uri Url { get; }
    public override IEnumerable<System.Security.Claims.ClaimsIdentity> Identities => Array.Empty<System.Security.Claims.ClaimsIdentity>();
    public override string Method => "POST";

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
