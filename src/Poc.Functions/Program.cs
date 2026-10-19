using Azure.Compute.Batch;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Poc.Core;
using Poc.Functions;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

var authentication = WorkflowAuthenticationOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(authentication);
builder.Services.AddSingleton<IWorkflowCallerResolver>(authentication.Mode == "EntraEasyAuth"
    ? new EntraEasyAuthCallerResolver(authentication)
    : new PocFunctionKeyCallerResolver());

string Required(string name) => builder.Configuration[name] is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Required setting {name} is missing.");
int Positive(string name, int fallback) => builder.Configuration[name] is not { Length: > 0 } text
    ? fallback : int.TryParse(text, out var value) && value > 0
        ? value : throw new InvalidOperationException($"{name} must be a positive integer.");

var credential = new DefaultAzureCredential();
var batchOptions = new BatchClientOptions();
batchOptions.Retry.MaxRetries = 1;
batchOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
var blobOptions = new BlobClientOptions();
blobOptions.Retry.MaxRetries = 1;
blobOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
builder.Services.AddSingleton<IBatchGateway>(new AzureBatchGateway(
    new BatchClient(new Uri(Required("BATCH_ENDPOINT")), credential, batchOptions), Required("BATCH_POOL_ID")));
var blobServiceClient = new BlobServiceClient(new Uri(Required("BLOB_ENDPOINT")), credential, blobOptions);
var containerClient = blobServiceClient.GetBlobContainerClient(Required("BLOB_CONTAINER"));
var resultRepo = new BlobResultRepository(containerClient);
var storageRepo = new BlobStorageRepository(resultRepo, blobServiceClient, containerClient);
builder.Services.AddSingleton<IResultRepository>(resultRepo);
builder.Services.AddSingleton(storageRepo);
builder.Services.AddSingleton<IWorkloadClassifier, StubWorkloadClassifier>();
builder.Services.AddSingleton(new WorkflowSettings(Positive("BATCH_POLL_SECONDS", 10), Positive("WORKFLOW_TIMEOUT_MIN", 15)));
builder.Services.AddSingleton<WorkflowOperations>();
builder.Services.AddSingleton<WorkflowAdmission>();
builder.Build().Run();
