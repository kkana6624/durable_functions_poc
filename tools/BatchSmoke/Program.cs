using Azure.Compute.Batch;
using Azure.Identity;
using Poc.Core;

// This tool intentionally creates a PoC job/task in an existing pool; never creates or modifies pools.
static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new ArgumentException($"Set {name}.");
var endpoint = Required("BATCH_ENDPOINT");
var pool = Required("BATCH_POOL_ID");
var requestId = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? $"smoke-{DateTime.UtcNow:yyyyMMddHHmmss}";
var fail = args.Contains("--fail");
var batch = new AzureBatchGateway(new BatchClient(new Uri(endpoint), new DefaultAzureCredential()), pool);
var request = new WorkflowRequest(requestId, "light", 1, fail ? 0 : null, 2, 5);
var operations = new WorkflowOperations(batch, null!, new StubWorkloadClassifier(), new WorkflowSettings());
var plan = await operations.ClassifyAsync(request);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(plan.TimeoutMinutes));
await batch.EnsureJobAsync(plan, timeout.Token);
var spec = new TaskSpec(plan, 0);
await batch.EnsureTaskAsync(spec, timeout.Token);
Console.WriteLine($"jobId={plan.JobId}; taskId={spec.TaskId}; pool={pool}");
while (true)
{
    var status = await batch.GetStatusAsync(spec, timeout.Token);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(status));
    if (status.Completed)
    {
        if (!status.Succeeded) throw new InvalidOperationException(status.Failure);
        var result = await batch.ReadResultAsync(spec, timeout.Token);
        if (result.Trim() != "1") throw new InvalidDataException("Expected integer 1.");
        Console.WriteLine($"PASS: Batch File API returned result.txt={result.Trim()}");
        break;
    }
    // A standalone smoke test may use Delay; Durable orchestrators must use Durable Timer.
    await Task.Delay(TimeSpan.FromSeconds(plan.PollSeconds), timeout.Token);
}
