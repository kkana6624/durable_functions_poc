# DTS metadata probe

Read-only status lookup against an Azure Durable Task Scheduler task hub. It never
schedules, terminates or purges an instance. Authenticate with `az login` and grant
the caller `Durable Task Data Reader` on the target task hub.

```powershell
$env:AZURE_TOKEN_CREDENTIALS = 'AzureCliCredential'
dotnet run --project tools/DtsProbe -- --endpoint https://YOUR-SCHEDULER.REGION.durabletask.io --task-hub DtsPocHub --instance-id YOUR-INSTANCE-ID
```

The connection uses `Authentication=DefaultAzure` and a 60-second request timeout.
The environment variable above limits authentication to the current Azure CLI login.
Only instance ID, name, runtime status and timestamps are printed. Inputs, outputs,
failure details, tokens, connection strings and raw exceptions are omitted.
Lookup failures expose only a fixed diagnostic category, exception type name and
optional gRPC status code; logging services are registered without console providers.
Exit code 0 means metadata was found; callers must check `runtimeStatus` separately.
Exit 2 means no instance was found, and exit 1 means invalid arguments or lookup failure.
Only HTTPS endpoints under `.durabletask.io` are accepted; the tool is for Azure
public cloud and does not connect to the emulator.

Client setup follows the [official .NET sample](https://github.com/Azure-Samples/Durable-Task-Scheduler/blob/main/samples/durable-task-sdks/dotnet/FunctionChaining/Client/Program.cs).
The pinned [Microsoft client package](https://www.nuget.org/packages/Microsoft.DurableTask.Client.AzureManaged/1.26.0)
is independent of the Function App's backend package version.
