using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Poc.Core;

namespace Poc.AppService.Clients;

public sealed class WorkflowClient(
    HttpClient httpClient,
    IOptions<WorkflowClientOptions> options,
    TokenCredential? tokenCredential = null,
    ILogger<WorkflowClient>? logger = null) : IWorkflowClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WorkflowClientOptions clientOptions = options.Value;

    private async Task PrepareRequestAsync(HttpRequestMessage request, WorkflowOwner? owner, CancellationToken cancellationToken)
    {
        if (string.Equals(clientOptions.AuthMode, "EntraBearer", StringComparison.OrdinalIgnoreCase))
        {
            if (tokenCredential is not null)
            {
                var audience = clientOptions.EntraAudience ?? $"{clientOptions.FunctionAppUrl.TrimEnd('/')}/.default";
                var tokenResult = await tokenCredential.GetTokenAsync(new TokenRequestContext([audience]), cancellationToken);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.Token);
            }
        }
        else if (!string.IsNullOrWhiteSpace(clientOptions.FunctionKey))
        {
            request.Headers.Add("x-functions-key", clientOptions.FunctionKey);
        }

        if (owner is not null)
        {
            request.Headers.Add("X-Poc-Owner-Id", owner.OwnerId);
            if (!string.IsNullOrWhiteSpace(owner.OrgId))
            {
                request.Headers.Add("X-Poc-Org-Id", owner.OrgId);
            }
        }
    }

    private Uri BuildUri(string relativePath)
    {
        var baseUri = new Uri(clientOptions.FunctionAppUrl.TrimEnd('/') + "/");
        return new Uri(baseUri, relativePath.TrimStart('/'));
    }

    public async Task<WorkflowStartClientResult> StartWorkflowAsync(WorkflowRequest request, WorkflowOwner? owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestUri = BuildUri("api/workflows");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri);
        var json = JsonSerializer.Serialize(request, JsonOptions);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        await PrepareRequestAsync(httpRequest, owner, cancellationToken);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger?.LogError(ex, "Failed to send StartWorkflow request to Functions.");
            return new WorkflowStartClientResult(false, 503, Error: "Functions service unreachable.");
        }

        var statusCode = (int)response.StatusCode;
        var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString()
            ?? (response.Headers.TryGetValues("Retry-After", out var vals) && vals is not null ? vals.FirstOrDefault() : null);

        if (response.IsSuccessStatusCode)
        {
            string? instanceId = null;
            try
            {
                var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);
                if (doc.RootElement.TryGetProperty("instanceId", out var idProp) || doc.RootElement.TryGetProperty("id", out idProp))
                {
                    instanceId = idProp.GetString();
                }
            }
            catch (JsonException) { }

            return new WorkflowStartClientResult(true, statusCode, InstanceId: instanceId ?? WorkflowIdentity.InstanceId(request.RequestId));
        }

        string? errorMessage = null;
        try
        {
            var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);
            if (doc.RootElement.TryGetProperty("error", out var errProp))
            {
                errorMessage = errProp.GetString();
            }
        }
        catch (JsonException) { }

        return new WorkflowStartClientResult(false, statusCode, Error: errorMessage ?? response.ReasonPhrase, RetryAfter: retryAfter);
    }

    public async Task<WorkflowStatusClientResult> GetStatusAsync(string requestId, WorkflowOwner? owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var requestUri = BuildUri($"api/workflows/{Uri.EscapeDataString(requestId)}");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);

        await PrepareRequestAsync(httpRequest, owner, cancellationToken);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger?.LogError(ex, "Failed to send GetWorkflowStatus request to Functions.");
            return new WorkflowStatusClientResult(false, 503, Error: "Functions service unreachable.");
        }

        var statusCode = (int)response.StatusCode;
        var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString()
            ?? (response.Headers.TryGetValues("Retry-After", out var vals) && vals is not null ? vals.FirstOrDefault() : null);

        if (response.IsSuccessStatusCode)
        {
            try
            {
                var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var status = await JsonSerializer.DeserializeAsync<WorkflowStatusResponse>(contentStream, JsonOptions, cancellationToken);
                return new WorkflowStatusClientResult(true, statusCode, Status: status);
            }
            catch (JsonException ex)
            {
                logger?.LogError(ex, "Failed to deserialize status response from Functions.");
                return new WorkflowStatusClientResult(false, 502, Error: "Invalid response from upstream Functions service.");
            }
        }

        string? errorMessage = null;
        try
        {
            var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);
            if (doc.RootElement.TryGetProperty("error", out var errProp))
            {
                errorMessage = errProp.GetString();
            }
        }
        catch (JsonException) { }

        return new WorkflowStatusClientResult(false, statusCode, Error: errorMessage ?? response.ReasonPhrase, RetryAfter: retryAfter);
    }

    public async Task<WorkflowCancelClientResult> CancelWorkflowAsync(string requestId, WorkflowOwner? owner, string? reason = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var requestUri = BuildUri($"api/workflows/{Uri.EscapeDataString(requestId)}/cancel");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri);
        var json = JsonSerializer.Serialize(new { reason }, JsonOptions);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        await PrepareRequestAsync(httpRequest, owner, cancellationToken);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger?.LogError(ex, "Failed to send CancelWorkflow request to Functions.");
            return new WorkflowCancelClientResult(false, 503, Error: "Functions service unreachable.");
        }

        var statusCode = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            WorkflowStatusResponse? status = null;
            try
            {
                var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                status = await JsonSerializer.DeserializeAsync<WorkflowStatusResponse>(contentStream, JsonOptions, cancellationToken);
            }
            catch (JsonException) { }

            return new WorkflowCancelClientResult(true, statusCode, Status: status);
        }

        string? errorMessage = null;
        try
        {
            var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);
            if (doc.RootElement.TryGetProperty("error", out var errProp))
            {
                errorMessage = errProp.GetString();
            }
        }
        catch (JsonException) { }

        return new WorkflowCancelClientResult(false, statusCode, Error: errorMessage ?? response.ReasonPhrase);
    }
}
