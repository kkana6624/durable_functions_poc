using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.Extensions.DependencyInjection;
using Grpc.Core;

if (args is ["--help"])
{
    Console.WriteLine("DtsProbe --endpoint https://<scheduler>.durabletask.io --task-hub <hub> --instance-id <id>");
    Console.WriteLine("Read-only metadata probe using DefaultAzure authentication. Exit: 0 found, 2 not found, 1 error.");
    return 0;
}

try
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length || args[i] is not ("--endpoint" or "--task-hub" or "--instance-id") ||
            !options.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException();
    }
    if (options.Count != 3 || !Uri.TryCreate(options["--endpoint"], UriKind.Absolute, out var endpoint) ||
        endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Port != 443 || endpoint.UserInfo.Length != 0 ||
        endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/" ||
        !endpoint.Host.EndsWith(".durabletask.io", StringComparison.OrdinalIgnoreCase) ||
        !Regex.IsMatch(options["--task-hub"], "^[A-Za-z][A-Za-z0-9]{2,44}$") ||
        !Regex.IsMatch(options["--instance-id"], "^[\\x20-\\x7E]{1,100}$"))
        throw new ArgumentException();

    // No console logging is registered: SDK exceptions can contain service details.
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDurableTaskClient(builder => builder.UseDurableTaskScheduler(
        $"Endpoint={endpoint.GetLeftPart(UriPartial.Authority)};TaskHub={options["--task-hub"]};Authentication=DefaultAzure"));
    await using var provider = services.BuildServiceProvider();
    var client = provider.GetRequiredService<DurableTaskClient>();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var instance = await client.GetInstanceAsync(options["--instance-id"], getInputsAndOutputs: false, timeout.Token);
    if (instance is null)
    {
        Console.WriteLine("{\"error\":\"InstanceNotFound\"}");
        return 2;
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        instanceId = instance.InstanceId,
        name = instance.Name,
        runtimeStatus = instance.RuntimeStatus.ToString(),
        createdAt = instance.CreatedAt,
        lastUpdatedAt = instance.LastUpdatedAt,
    }));
    return 0;
}
catch (ArgumentException)
{
    Console.Error.WriteLine("{\"error\":\"InvalidArguments\"}");
    return 1;
}
catch (Exception exception)
{
    // Fixed categories and type names only; no exception message, headers or trailers.
    var rpc = exception as RpcException ?? exception.InnerException as RpcException;
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        error = "MetadataQueryFailed",
        category = rpc is not null ? "Grpc" : exception is InvalidOperationException ? "ClientConfiguration" : "Client",
        exceptionType = exception.GetType().Name,
        grpcStatusCode = rpc?.StatusCode.ToString(),
    }));
    return 1;
}
