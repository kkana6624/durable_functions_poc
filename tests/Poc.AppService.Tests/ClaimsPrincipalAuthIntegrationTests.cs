using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Poc.AppService.Clients;
using Poc.AppService.Models;
using Poc.AppService.Services;
using Poc.Core;
using Xunit;

namespace Poc.AppService.Tests;

public class ClaimsPrincipalAuthIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Issuer = "https://test-issuer.example";
    private const string Audience = "test-app-service";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("app-service-tests-only-signing-key-32-bytes"));
    private readonly WebApplicationFactory<Program> factory;
    private readonly InMemoryBlobStorageRepository inMemoryStorage = new();
    private readonly Mock<IWorkflowClient> workflowClientMock = new();

    public ClaimsPrincipalAuthIntegrationTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("APP_SERVICE_AUTH_MODE", "ClaimsPrincipal");
            builder.UseSetting("APP_SERVICE_AUTH_AUTHORITY", Issuer);
            builder.UseSetting("APP_SERVICE_AUTH_AUDIENCE", Audience);
            builder.ConfigureTestServices(services =>
            {
                // Only metadata/key discovery is local; the real JWT validator still verifies every token.
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    options.TokenValidationParameters.IssuerSigningKey = SigningKey;
                });
                services.AddSingleton<IBlobStorageRepository>(inMemoryStorage);
                services.AddSingleton(inMemoryStorage);
                services.AddSingleton(workflowClientMock.Object);
            });
        });
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string CreateUnsignedJwt(string ownerId)
    {
        var headerJson = JsonSerializer.Serialize(new { alg = "none", typ = "JWT" });
        var payloadJson = JsonSerializer.Serialize(new { oid = ownerId, sub = ownerId, iss = Issuer, aud = Audience });
        return $"{Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson))}.{Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson))}.";
    }

    private static string CreateTestJwt(string ownerId, string? orgId = null, string issuer = Issuer,
        string audience = Audience, DateTime? expires = null, SymmetricSecurityKey? signingKey = null)
    {
        var payloadDict = new Dictionary<string, object>
        {
            ["oid"] = ownerId,
            ["sub"] = ownerId
        };
        if (!string.IsNullOrWhiteSpace(orgId))
        {
            payloadDict["tid"] = orgId;
        }
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = payloadDict,
            IssuedAt = DateTime.UtcNow.AddHours(-2),
            NotBefore = DateTime.UtcNow.AddHours(-2),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(signingKey ?? SigningKey, SecurityAlgorithms.HmacSha256)
        });
    }

    [Theory]
    [InlineData("APP_SERVICE_AUTH_BYPASS_SIGNATURE_VALIDATION")]
    [InlineData("AppServiceAuth:BypassSignatureValidation")]
    public void Production_WithLegacyBypassEnabled_RefusesStartup(string setting)
    {
        using var unsafeFactory = factory.WithWebHostBuilder(builder => builder.UseSetting(setting, "true"));
        var exception = Assert.Throws<InvalidOperationException>(() => unsafeFactory.CreateClient());
        Assert.Contains("JWT signature validation bypass is not supported", exception.ToString());
    }

    [Theory]
    [InlineData("unsigned")]
    [InlineData("wrong-signature")]
    [InlineData("expired")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-audience")]
    public async Task Production_WithInvalidBearerToken_Returns401Unauthorized(string invalidity)
    {
        var token = invalidity switch
        {
            "unsigned" => CreateUnsignedJwt("alice"),
            "wrong-signature" => CreateTestJwt("alice", signingKey: new SymmetricSecurityKey(Encoding.UTF8.GetBytes("different-tests-only-signing-key-32-bytes"))),
            "expired" => CreateTestJwt("alice", expires: DateTime.UtcNow.AddHours(-1)),
            "wrong-issuer" => CreateTestJwt("alice", issuer: "https://untrusted.example"),
            "wrong-audience" => CreateTestJwt("alice", audience: "different-app"),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidity))
        };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/uploads/initiate", new InitiateUploadRequest("data.csv", 100));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
