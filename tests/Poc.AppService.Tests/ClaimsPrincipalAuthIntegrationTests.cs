using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Poc.AppService.Clients;
using Poc.AppService.Models;
using Poc.AppService.Services;
using Poc.Core;
using Xunit;

namespace Poc.AppService.Tests;

public class ClaimsPrincipalAuthIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;
    private readonly InMemoryBlobStorageRepository inMemoryStorage = new();
    private readonly Mock<IWorkflowClient> workflowClientMock = new();

    public ClaimsPrincipalAuthIntegrationTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("APP_SERVICE_AUTH_MODE", "ClaimsPrincipal");
            builder.UseSetting("APP_SERVICE_AUTH_BYPASS_SIGNATURE_VALIDATION", "true");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IBlobStorageRepository>(inMemoryStorage);
                services.AddSingleton(inMemoryStorage);
                services.AddSingleton(workflowClientMock.Object);
            });
        });
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string CreateTestJwt(string ownerId, string? orgId = null)
    {
        var headerJson = JsonSerializer.Serialize(new { alg = "none", typ = "JWT" });
        var payloadDict = new Dictionary<string, object>
        {
            ["oid"] = ownerId,
            ["sub"] = ownerId
        };
        if (!string.IsNullOrWhiteSpace(orgId))
        {
            payloadDict["tid"] = orgId;
        }
        var payloadJson = JsonSerializer.Serialize(payloadDict);

        var headerBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        return $"{headerBase64}.{payloadBase64}.";
    }

    [Fact]
    public async Task WithoutBearerToken_Returns401Unauthorized()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/uploads/initiate", new InitiateUploadRequest("data.csv", 100));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WithValidBearerToken_AuthenticatesAndResolvesOwner()
    {
        var client = factory.CreateClient();
        var token = CreateTestJwt("alice", "tenant-alpha");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/uploads/initiate", new InitiateUploadRequest("data.csv", 100));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<InitiateUploadResponse>();
        Assert.NotNull(result);
        Assert.StartsWith("owners/alice/inputs/", result.BlobKey);
    }

    [Fact]
    public async Task WithBearerToken_AndSpoofedOwnerHeaderMismatch_Returns403Forbidden()
    {
        var client = factory.CreateClient();
        var token = CreateTestJwt("alice", "tenant-alpha");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Poc-Owner-Id", "bob"); // Spoofing bob

        var response = await client.PostAsJsonAsync("/api/uploads/initiate", new InitiateUploadRequest("data.csv", 100));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
