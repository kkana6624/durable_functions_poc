namespace Poc.AppService.Clients;

public sealed class WorkflowClientOptions
{
    public const string SectionName = "WorkflowClient";

    /// <summary>
    /// Base URL of the target Azure Functions instance (e.g. "https://func-poc.azurewebsites.net" or "http://localhost:7071").
    /// </summary>
    public string FunctionAppUrl { get; set; } = "http://localhost:7071";

    /// <summary>
    /// Authentication mode for Function App S2S calls: "PocFunctionKey" or "EntraBearer".
    /// </summary>
    public string AuthMode { get; set; } = "PocFunctionKey";

    /// <summary>
    /// Function key (x-functions-key) used when AuthMode is "PocFunctionKey".
    /// Kept out of logs, repository files, and client-facing responses.
    /// </summary>
    public string? FunctionKey { get; set; }

    /// <summary>
    /// Target Entra ID resource/audience URI when AuthMode is "EntraBearer".
    /// </summary>
    public string? EntraAudience { get; set; }

    /// <summary>
    /// HTTP client timeout in seconds (default 30 seconds).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}
