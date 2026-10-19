using Azure.Identity;
using Azure.Storage.Blobs;
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
});

var authMode = builder.Configuration["APP_SERVICE_AUTH_MODE"]
    ?? builder.Configuration.GetSection(AppServiceAuthenticationOptions.SectionName)["Mode"]
    ?? "Stub";

if (string.Equals(authMode, "ClaimsPrincipal", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IAppServiceCallerResolver, ClaimsPrincipalAppServiceCallerResolver>();
}
else if (string.Equals(authMode, "EasyAuth", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IAppServiceCallerResolver, EasyAuthAppServiceCallerResolver>();
}
else
{
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

app.MapGet("/", () => Results.Ok(new { service = "Poc.AppService", status = "Healthy" }));
app.MapUploadEndpoints();
app.MapWorkflowEndpoints();

app.Run();

public partial class Program { }
