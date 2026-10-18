using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Poc.Core;
using Poc.Functions;
using Xunit;

namespace Poc.Tests;

public sealed class WorkflowAuthorizationTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string BackendAppId = "22222222-2222-2222-2222-222222222222";
    private const string Audience = "api://workflow-api";
    private const string RequiredRole = "Workflow.Submit";
    private const string RequestId = "req-authz-001";

    [Fact]
    public void MissingAuthMode_DefaultsToPoCFunctionKeyAndAllowsOwnerlessCalls()
    {
        var options = WorkflowAuthenticationOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var request = CreateRequest("{}");

        Assert.Equal("PocFunctionKey", options.Mode);
        Assert.Null(new PocFunctionKeyCallerResolver().Resolve(request));
    }

    [Theory]
    [InlineData("WORKFLOW_AUTH_TENANT_ID")]
    [InlineData("WORKFLOW_AUTH_AUDIENCE")]
    [InlineData("WORKFLOW_AUTH_ALLOWED_BACKEND_APP_IDS")]
    [InlineData("WORKFLOW_AUTH_REQUIRED_APP_ROLE")]
    public void EntraEasyAuth_RejectsMissingSettings(string missingSetting)
    {
        var values = ValidSettings();
        values.Remove(missingSetting);

        Assert.Throws<InvalidOperationException>(() => WorkflowAuthenticationOptions.FromConfiguration(Configuration(values)));
    }

    [Theory]
    [InlineData("WEBSITE_AUTH_ENABLED")]
    [InlineData("WEBSITE_SITE_NAME")]
    [InlineData("WEBSITE_INSTANCE_ID")]
    public void EntraEasyAuth_RequiresPlatformEnvironmentMarkers(string missingMarker)
    {
        var values = ValidSettings();
        values.Remove(missingMarker);

        Assert.Throws<InvalidOperationException>(() => WorkflowAuthenticationOptions.FromConfiguration(Configuration(values)));
    }

    [Fact]
    public void EntraEasyAuth_RejectsPlatformAuthDisabled()
    {
        var values = ValidSettings();
        values["WEBSITE_AUTH_ENABLED"] = "false";

        Assert.Throws<InvalidOperationException>(() => WorkflowAuthenticationOptions.FromConfiguration(Configuration(values)));
    }

    [Fact]
    public void EntraEasyAuth_ResolverRejectsPoCOptions()
    {
        var options = WorkflowAuthenticationOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Throws<InvalidOperationException>(() => new EntraEasyAuthCallerResolver(options));
    }

    [Fact]
    public void EntraEasyAuth_RejectsUnknownMode()
    {
        var values = ValidSettings();
        values["WORKFLOW_AUTH_MODE"] = "EasyAuthMaybe";

        Assert.Throws<InvalidOperationException>(() => WorkflowAuthenticationOptions.FromConfiguration(Configuration(values)));
    }

    [Fact]
    public void EntraEasyAuth_ValidApplicationPrincipalReturnsOnlyForwardedEndUserOwner()
    {
        var request = CreateRequest("{}", AuthorizedHeaders("alice", "finance"));

        var owner = Resolver().Resolve(request);

        Assert.Equal(new WorkflowOwner("alice", "finance"), owner);
    }

    [Fact]
    public void EntraEasyAuth_AcceptsV2AuthorizedPartyClaimForApplicationId()
    {
        var claims = AuthorizedClaims();
        claims.RemoveAll(c => c.Typ == "appid");
        claims.Add(new("azp", BackendAppId));

        var owner = Resolver().Resolve(CreateRequest("{}", HeadersWithPrincipal(claims, "alice", "finance")));

        Assert.Equal(new WorkflowOwner("alice", "finance"), owner);
    }

    [Fact]
    public void EntraEasyAuth_AcceptsMappedTenantAndRoleClaims()
    {
        var claims = AuthorizedClaims();
        claims.RemoveAll(c => c.Typ == "tid");
        claims.Add(new("http://schemas.microsoft.com/identity/claims/tenantid", TenantId));
        claims.RemoveAll(c => c.Typ == "roles");
        claims.Add(new("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", RequiredRole));

        var owner = Resolver().Resolve(CreateRequest("{}", HeadersWithPrincipal(claims, "alice", "finance")));

        Assert.Equal(new WorkflowOwner("alice", "finance"), owner);
    }

    [Theory]
    [InlineData("unknown-app")]
    [InlineData("missing-role")]
    [InlineData("delegated-user")]
    [InlineData("scope-present")]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-audience")]
    [InlineData("wrong-auth-type")]
    [InlineData("duplicate-appid")]
    [InlineData("both-appid-aliases")]
    [InlineData("both-role-aliases")]
    public void EntraEasyAuth_RejectsUntrustedOrAmbiguousApplicationClaims(string variation)
    {
        var claims = AuthorizedClaims();
        switch (variation)
        {
            case "unknown-app": SetClaim(claims, "appid", "33333333-3333-3333-3333-333333333333"); break;
            case "missing-role": claims.RemoveAll(c => c.Typ == "roles"); break;
            case "delegated-user": SetClaim(claims, "idtyp", "user"); break;
            case "scope-present": claims.Add(new("scp", "Workflow.Submit")); break;
            case "wrong-tenant": SetClaim(claims, "tid", "44444444-4444-4444-4444-444444444444"); break;
            case "wrong-issuer": SetClaim(claims, "iss", "https://untrusted.invalid/"); break;
            case "wrong-audience": SetClaim(claims, "aud", "api://other-api"); break;
            case "wrong-auth-type": break;
            case "duplicate-appid": claims.Add(new("appid", BackendAppId)); break;
            case "both-appid-aliases": claims.Add(new("azp", BackendAppId)); break;
            case "both-role-aliases": claims.Add(new("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", RequiredRole)); break;
        }

        var headers = HeadersWithPrincipal(claims, "alice", "finance");
        if (variation == "wrong-auth-type")
        {
            headers.RemoveAll(h => h.Key == "X-MS-CLIENT-PRINCIPAL");
            headers.Add(new("X-MS-CLIENT-PRINCIPAL", PrincipalHeader(claims, "facebook")));
        }
        var request = CreateRequest("{}", headers);

        var exception = Assert.Throws<WorkflowCallerAuthenticationException>(() => Resolver().Resolve(request));
        Assert.Equal(variation == "wrong-auth-type" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Fact]
    public void EntraEasyAuth_RejectsDuplicatePrincipalHeaderAsUnauthenticated()
    {
        var headers = AuthorizedHeaders("alice", "finance");
        headers.Add(new("X-MS-CLIENT-PRINCIPAL", PrincipalHeader(AuthorizedClaims())));
        var request = CreateRequest("{}", headers);

        var exception = Assert.Throws<WorkflowCallerAuthenticationException>(() => Resolver().Resolve(request));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public void EntraEasyAuth_RejectsMalformedPrincipalAsUnauthenticated()
    {
        var headers = AuthorizedHeaders("alice", "finance");
        headers[0] = new("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(Encoding.UTF8.GetBytes("not-json")));

        var exception = Assert.Throws<WorkflowCallerAuthenticationException>(() =>
            Resolver().Resolve(CreateRequest("{}", headers)));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public void EntraEasyAuth_RejectsDuplicateJsonPropertiesAsUnauthenticated()
    {
        var headers = AuthorizedHeaders("alice", "finance");
        var principalWithDuplicateProperty = "{\"auth_typ\":\"aad\",\"auth_typ\":\"aad\",\"claims\":[]}";
        headers[0] = new("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(Encoding.UTF8.GetBytes(principalWithDuplicateProperty)));

        var exception = Assert.Throws<WorkflowCallerAuthenticationException>(() =>
            Resolver().Resolve(CreateRequest("{}", headers)));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public void EntraEasyAuth_RejectsOversizedBase64PrincipalWithValidAuthorizationClaims()
    {
        var headers = AuthorizedHeaders("alice", "finance");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            auth_typ = "aad",
            claims = AuthorizedClaims().Select(c => new { typ = c.Typ, val = c.Val }).ToArray(),
            padding = new string('A', 30000)
        });
        var principal = Convert.ToBase64String(payload);
        Assert.True(principal.Length > 32768);
        using (var parsed = JsonDocument.Parse(Convert.FromBase64String(principal)))
            Assert.Equal("aad", parsed.RootElement.GetProperty("auth_typ").GetString());
        headers[0] = new("X-MS-CLIENT-PRINCIPAL", principal);

        var exception = Assert.Throws<WorkflowCallerAuthenticationException>(() =>
            Resolver().Resolve(CreateRequest("{}", headers)));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Theory]
    [InlineData("owner-missing")]
    [InlineData("org-missing")]
    [InlineData("owner-duplicate")]
    [InlineData("org-duplicate")]
    public void EntraEasyAuth_RequiresUnambiguousOwnerAndOrganization(string variation)
    {
        var headers = AuthorizedHeaders("alice", "finance");
        switch (variation)
        {
            case "owner-missing": headers.RemoveAll(h => h.Key == "X-Poc-Owner-Id"); break;
            case "org-missing": headers.RemoveAll(h => h.Key == "X-Poc-Org-Id"); break;
            case "owner-duplicate": headers.Add(new("X-Poc-Owner-Id", "mallory")); break;
            case "org-duplicate": headers.Add(new("X-Poc-Org-Id", "engineering")); break;
        }
        var request = CreateRequest("{}", headers);

        var exception = Assert.Throws<WorkflowCallerAuthenticationException>(() => Resolver().Resolve(request));
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Theory]
    [InlineData("Start")]
    [InlineData("Status")]
    [InlineData("Result")]
    public async Task EntraEasyAuth_UnauthenticatedRequestIsRejectedBeforeAdmissionOrDurable(string api)
    {
        var repository = new Mock<IResultRepository>(MockBehavior.Strict);
        var client = new Mock<DurableTaskClient>("test");
        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repository.Object), Resolver());
        var response = await InvokeAsync(functions, api, CreateRequest("{ invalid"), client.Object);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Caller is not authorized to access this workflow.", (await ReadBodyAsync(response)).GetProperty("error").GetString());
        repository.VerifyNoOtherCalls();
        client.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Start")]
    [InlineData("Status")]
    [InlineData("Result")]
    public async Task EntraEasyAuth_ForbiddenCallerIsRejectedBeforeAdmissionOrDurable(string api)
    {
        var repository = new Mock<IResultRepository>(MockBehavior.Strict);
        var client = new Mock<DurableTaskClient>("test");
        var headers = AuthorizedHeaders("alice", "finance");
        headers.RemoveAll(h => h.Key == "X-MS-CLIENT-PRINCIPAL");
        var claims = AuthorizedClaims();
        SetClaim(claims, "roles", "Workflow.ReadOnly");
        headers.Add(new("X-MS-CLIENT-PRINCIPAL", PrincipalHeader(claims)));
        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repository.Object), Resolver());

        var response = await InvokeAsync(functions, api, CreateRequest("{}", headers), client.Object);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        repository.VerifyNoOtherCalls();
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EntraEasyAuth_AllowedBackendFlowsForwardedOwnerIntoExistingOwnerAuthorization()
    {
        var repository = new InMemoryResultRepository();
        var instanceId = WorkflowIdentity.InstanceId(RequestId);
        await repository.PutIfAbsentAsync(WorkflowIdentity.AdmissionPath(RequestId),
            new WorkflowAdmissionRecord(2, instanceId, new WorkflowRequest(RequestId), new WorkflowOwner("alice", "finance")),
            TestContext.Current.CancellationToken);
        var client = new Mock<DurableTaskClient>("test");
        var functions = new WorkflowFunctions(null!, new WorkflowAdmission(repository), Resolver());
        var response = await functions.GetWorkflowStatus(CreateRequest("", AuthorizedHeaders("mallory", "finance"),
            $"https://localhost/api/workflows/{RequestId}", "GET"), RequestId, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(repository.Documents.ContainsKey(WorkflowIdentity.AdmissionPath(RequestId)));
        client.VerifyNoOtherCalls();
    }

    private static async Task<HttpResponseData> InvokeAsync(WorkflowFunctions functions, string api,
        FakeAuthorizationHttpRequestData request, DurableTaskClient client) => api switch
    {
        "Start" => await functions.StartWorkflow(request, client, TestContext.Current.CancellationToken),
        "Status" => await functions.GetWorkflowStatus(request, RequestId, client, TestContext.Current.CancellationToken),
        "Result" => await functions.GetWorkflowResult(request, RequestId, client, TestContext.Current.CancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(api))
    };

    private static EntraEasyAuthCallerResolver Resolver() => new(WorkflowAuthenticationOptions.FromConfiguration(Configuration(ValidSettings())));

    private static Dictionary<string, string?> ValidSettings() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["WORKFLOW_AUTH_MODE"] = "EntraEasyAuth",
        ["WORKFLOW_AUTH_TENANT_ID"] = TenantId,
        ["WORKFLOW_AUTH_AUDIENCE"] = Audience,
        ["WORKFLOW_AUTH_ALLOWED_BACKEND_APP_IDS"] = BackendAppId,
        ["WORKFLOW_AUTH_REQUIRED_APP_ROLE"] = RequiredRole,
        ["WEBSITE_AUTH_ENABLED"] = "true",
        ["WEBSITE_SITE_NAME"] = "poc-function-app",
        ["WEBSITE_INSTANCE_ID"] = "fake-platform-instance"
    };

    private static IConfiguration Configuration(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static List<ClaimEntry> AuthorizedClaims() =>
    [
        new("tid", TenantId),
        new("iss", $"https://login.microsoftonline.com/{TenantId}/v2.0"),
        new("aud", Audience),
        new("appid", BackendAppId),
        new("idtyp", "app"),
        new("roles", RequiredRole)
    ];

    private static void SetClaim(List<ClaimEntry> claims, string typ, string value)
    {
        var matching = claims.Where(c => c.Typ == typ).ToArray();
        foreach (var claim in matching) claims.Remove(claim);
        claims.Add(new(typ, value));
    }

    private static List<KeyValuePair<string, string>> AuthorizedHeaders(string owner, string org) =>
        HeadersWithPrincipal(AuthorizedClaims(), owner, org);

    private static List<KeyValuePair<string, string>> HeadersWithPrincipal(List<ClaimEntry> claims, string owner, string org) =>
    [
        new("X-MS-CLIENT-PRINCIPAL", PrincipalHeader(claims)),
        new("X-Poc-Owner-Id", owner),
        new("X-Poc-Org-Id", org)
    ];

    // This is parser test data only; it does not demonstrate EasyAuth or Entra token validation.
    private static string PrincipalHeader(List<ClaimEntry> claims, string authType = "aad") => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
    {
        auth_typ = authType,
        claims = claims.Select(c => new { typ = c.Typ, val = c.Val }).ToArray()
    }));

    private static FakeAuthorizationHttpRequestData CreateRequest(string body,
        IEnumerable<KeyValuePair<string, string>>? headers = null,
        string url = "https://localhost/api/workflows", string method = "POST") =>
        new(body, new Uri(url), method, headers);

    private static async Task<JsonElement> ReadBodyAsync(HttpResponseData response)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, Encoding.UTF8);
        using var document = JsonDocument.Parse(await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }

    private sealed record ClaimEntry(string Typ, string Val);
}

internal sealed class FakeAuthorizationHttpRequestData : HttpRequestData
{
    private readonly MemoryStream body;
    private readonly HttpHeadersCollection headers = new();
    private readonly FunctionContext context;

    public FakeAuthorizationHttpRequestData(string content, Uri url, string method,
        IEnumerable<KeyValuePair<string, string>>? values) : base(CreateContext())
    {
        context = FunctionContext;
        body = new MemoryStream(Encoding.UTF8.GetBytes(content));
        Url = url;
        Method = method;
        if (values is not null)
            foreach (var (name, value) in values) headers.Add(name, value);
    }

    public override Stream Body => body;
    public override HttpHeadersCollection Headers => headers;
    public override IReadOnlyCollection<IHttpCookie> Cookies => Array.Empty<IHttpCookie>();
    public override Uri Url { get; }
    public override IEnumerable<ClaimsIdentity> Identities => Array.Empty<ClaimsIdentity>();
    public override string Method { get; }
    public override HttpResponseData CreateResponse() => new FakeHttpResponseData(context);

    private static FunctionContext CreateContext()
    {
        var context = new Mock<FunctionContext>();
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<WorkerOptions>(options =>
        {
            options.Serializer = new Azure.Core.Serialization.JsonObjectSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });
        context.SetupGet(c => c.InstanceServices).Returns(services.BuildServiceProvider());
        return context.Object;
    }
}
