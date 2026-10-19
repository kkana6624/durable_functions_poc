using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Poc.AppService.Authentication;
using Poc.AppService.Clients;
using Poc.AppService.Endpoints;
using Poc.AppService.Services;
using Poc.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// 1. Authentication Configuration
builder.Services.Configure<AppServiceAuthenticationOptions>(options =>
{
    builder.Configuration.GetSection(AppServiceAuthenticationOptions.SectionName).Bind(options);
    if (!string.IsNullOrWhiteSpace(builder.Configuration["APP_SERVICE_AUTH_MODE"]))
        options.Mode = builder.Configuration["APP_SERVICE_AUTH_MODE"]!;
    if (!string.IsNullOrWhiteSpace(builder.Configuration["APP_SERVICE_DEFAULT_OWNER_ID"]))
        options.DefaultOwnerId = builder.Configuration["APP_SERVICE_DEFAULT_OWNER_ID"];
    if (!string.IsNullOrWhiteSpace(builder.Configuration["APP_SERVICE_DEFAULT_ORG_ID"]))
        options.DefaultOrgId = builder.Configuration["APP_SERVICE_DEFAULT_ORG_ID"];
    if (!string.IsNullOrWhiteSpace(builder.Configuration["APP_SERVICE_AUTH_AUTHORITY"]))
        options.Authority = builder.Configuration["APP_SERVICE_AUTH_AUTHORITY"];
    if (!string.IsNullOrWhiteSpace(builder.Configuration["APP_SERVICE_AUTH_AUDIENCE"]))
        options.Audience = builder.Configuration["APP_SERVICE_AUTH_AUDIENCE"];
    if (bool.TryParse(builder.Configuration["APP_SERVICE_AUTH_REQUIRE_HTTPS_METADATA"], out var requireHttps))
        options.RequireHttpsMetadata = requireHttps;
});

var authMode = builder.Configuration["APP_SERVICE_AUTH_MODE"]
    ?? builder.Configuration.GetSection(AppServiceAuthenticationOptions.SectionName)["Mode"]
    ?? "Stub";

// Reject legacy test-only settings instead of allowing them to weaken deployed authentication.
if (builder.Configuration.GetValue<bool>("APP_SERVICE_AUTH_BYPASS_SIGNATURE_VALIDATION")
    || builder.Configuration.GetSection(AppServiceAuthenticationOptions.SectionName).GetValue<bool>("BypassSignatureValidation"))
{
    throw new InvalidOperationException("JWT signature validation bypass is not supported. Remove the bypass setting; tests must supply their own signing key through dependency injection.");
}

builder.Services.AddAuthorization();

if (string.Equals(authMode, "ClaimsPrincipal", StringComparison.OrdinalIgnoreCase))
{
    var authSection = builder.Configuration.GetSection(AppServiceAuthenticationOptions.SectionName);
    var authority = builder.Configuration["APP_SERVICE_AUTH_AUTHORITY"] ?? authSection["Authority"];
    var audience = builder.Configuration["APP_SERVICE_AUTH_AUDIENCE"] ?? authSection["Audience"];
    var requireHttpsMetadata = builder.Configuration.GetValue<bool?>("APP_SERVICE_AUTH_REQUIRE_HTTPS_METADATA")
        ?? authSection.GetValue<bool?>("RequireHttpsMetadata")
        ?? true;

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            if (!string.IsNullOrWhiteSpace(authority))
            {
                options.Authority = authority;
            }
            if (!string.IsNullOrWhiteSpace(audience))
            {
                options.Audience = audience;
            }
            options.RequireHttpsMetadata = requireHttpsMetadata;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                ValidateIssuer = !string.IsNullOrWhiteSpace(authority),
                ValidIssuer = authority,
                ValidateAudience = !string.IsNullOrWhiteSpace(audience),
                ValidAudience = audience,
                ValidateLifetime = true
            };
        });

    builder.Services.AddSingleton<IAppServiceCallerResolver, ClaimsPrincipalAppServiceCallerResolver>();
}
else if (string.Equals(authMode, "EasyAuth", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddAuthentication();
    builder.Services.AddSingleton<IAppServiceCallerResolver, EasyAuthAppServiceCallerResolver>();
}
else
{
    builder.Services.AddAuthentication();
    builder.Services.AddSingleton<IAppServiceCallerResolver, StubAppServiceCallerResolver>();
}

// 2. WorkflowClient Configuration
builder.Services.Configure<WorkflowClientOptions>(options =>
{
    builder.Configuration.GetSection(WorkflowClientOptions.SectionName).Bind(options);
    if (!string.IsNullOrWhiteSpace(builder.Configuration["FUNCTION_APP_URL"]))
        options.FunctionAppUrl = builder.Configuration["FUNCTION_APP_URL"]!;
    if (!string.IsNullOrWhiteSpace(builder.Configuration["WORKFLOW_FUNCTION_KEY"]))
        options.FunctionKey = builder.Configuration["WORKFLOW_FUNCTION_KEY"];
    if (!string.IsNullOrWhiteSpace(builder.Configuration["WORKFLOW_AUTH_MODE"]))
        options.AuthMode = builder.Configuration["WORKFLOW_AUTH_MODE"]!;
    if (!string.IsNullOrWhiteSpace(builder.Configuration["WORKFLOW_AUTH_AUDIENCE"]))
        options.EntraAudience = builder.Configuration["WORKFLOW_AUTH_AUDIENCE"];
});
builder.Services.AddOptions<WorkflowClientOptions>()
    .Validate(options => options.FunctionKey is null
        || !options.FunctionKey.TrimStart().StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase),
        "The workflow Function key reference has not been resolved. Check the App Service Key Vault reference configuration.")
    .ValidateOnStart();

var uamiClientId = builder.Configuration["APP_SERVICE_UAMI_CLIENT_ID"];
if (!string.IsNullOrWhiteSpace(uamiClientId))
{
    builder.Services.AddSingleton<Azure.Core.TokenCredential>(new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ManagedIdentityClientId = uamiClientId
    }));
}
else
{
    builder.Services.AddSingleton<Azure.Core.TokenCredential>(_ => new DefaultAzureCredential());
}

builder.Services.AddHttpClient<IWorkflowClient, WorkflowClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorkflowClientOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
});

// 3. Storage Configuration
builder.Services.Configure<UploadServiceOptions>(options =>
{
    builder.Configuration.GetSection(UploadServiceOptions.SectionName).Bind(options);
    if (!string.IsNullOrWhiteSpace(builder.Configuration["WORKFLOW_INPUT_CONTAINER"]))
        options.DefaultContainer = builder.Configuration["WORKFLOW_INPUT_CONTAINER"]!;
});

var blobEndpoint = builder.Configuration["BLOB_SERVICE_ENDPOINT"]
    ?? builder.Configuration["BlobStorage:BlobServiceEndpoint"];

if (!string.IsNullOrWhiteSpace(blobEndpoint))
{
    builder.Services.AddSingleton(sp =>
    {
        var cred = sp.GetRequiredService<Azure.Core.TokenCredential>();
        return new BlobServiceClient(new Uri(blobEndpoint), cred);
    });
    builder.Services.AddSingleton<IBlobStorageRepository>(sp =>
    {
        var blobClient = sp.GetRequiredService<BlobServiceClient>();
        var containerName = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<UploadServiceOptions>>().Value.DefaultContainer;
        var containerClient = blobClient.GetBlobContainerClient(containerName);
        return new BlobStorageRepository(blobClient, containerClient);
    });
}
else
{
    var inMemory = new InMemoryBlobStorageRepository();
    builder.Services.AddSingleton<IBlobStorageRepository>(inMemory);
    builder.Services.AddSingleton(inMemory);
}

// 4. Upload Service
builder.Services.AddSingleton<IUploadService, UploadService>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { service = "Poc.AppService", status = "Healthy" }));
app.MapUploadEndpoints();
app.MapWorkflowEndpoints();

app.Run();

public partial class Program { }
