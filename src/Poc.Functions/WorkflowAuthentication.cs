using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Poc.Core;

namespace Poc.Functions;

public interface IWorkflowCallerResolver
{
    WorkflowOwner? Resolve(HttpRequestData request);
}

public sealed class WorkflowCallerAuthenticationException(HttpStatusCode statusCode) : Exception("Workflow caller authorization failed.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class WorkflowAuthenticationOptions
{
    public string Mode { get; private init; } = "PocFunctionKey";
    public string TenantId { get; private init; } = "";
    public string Audience { get; private init; } = "";
    public IReadOnlySet<string> AllowedBackendAppIds { get; private init; } = new HashSet<string>();
    public string RequiredAppRole { get; private init; } = "";
    internal bool PlatformConfigured { get; private init; }

    public static WorkflowAuthenticationOptions FromConfiguration(IConfiguration configuration)
    {
        var mode = configuration["WORKFLOW_AUTH_MODE"] ?? "PocFunctionKey";
        if (mode == "PocFunctionKey") return new();
        if (mode != "EntraEasyAuth") throw new InvalidOperationException("WORKFLOW_AUTH_MODE is invalid.");
        string Required(string key) => !string.IsNullOrWhiteSpace(configuration[key]) ? configuration[key]!.Trim()
            : throw new InvalidOperationException($"Required setting {key} is missing.");
        string GuidSetting(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id.ToString()
            : throw new InvalidOperationException("Workflow authentication requires valid nonempty GUID identifiers.");
        var tenant = GuidSetting(Required("WORKFLOW_AUTH_TENANT_ID"));
        var apps = Required("WORKFLOW_AUTH_ALLOWED_BACKEND_APP_IDS").Split(',').Select(x => GuidSetting(x.Trim())).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.Equals(configuration["WEBSITE_AUTH_ENABLED"], "true", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(configuration["WEBSITE_SITE_NAME"])
            || string.IsNullOrWhiteSpace(configuration["WEBSITE_INSTANCE_ID"]))
            throw new InvalidOperationException("EntraEasyAuth requires the configured Azure App Service authentication platform.");
        return new() { Mode = mode, TenantId = tenant, Audience = Required("WORKFLOW_AUTH_AUDIENCE"),
            AllowedBackendAppIds = apps, RequiredAppRole = Required("WORKFLOW_AUTH_REQUIRED_APP_ROLE"), PlatformConfigured = true };
    }
}

// This mode preserves the existing PoC contract; these owner headers do not prove identity.
public sealed class PocFunctionKeyCallerResolver : IWorkflowCallerResolver
{
    public WorkflowOwner? Resolve(HttpRequestData request)
    {
        var ownerId = request.Headers.TryGetValues("X-Poc-Owner-Id", out var owners) ? owners.FirstOrDefault() : null;
        var orgId = request.Headers.TryGetValues("X-Poc-Org-Id", out var orgs) ? orgs.FirstOrDefault() : null;
        if (ownerId is null)
        {
            if (orgId is not null) throw new ArgumentException("Owner is required.");
            return null;
        }
        var owner = new WorkflowOwner(ownerId, orgId);
        owner.Validate();
        return owner;
    }
}

// Trust in this header comes from the deployed EasyAuth boundary, not from this parser.
// Environment checks prevent accidental local use; they are not cryptographic attestation.
public sealed class EntraEasyAuthCallerResolver : IWorkflowCallerResolver
{
    private readonly WorkflowAuthenticationOptions options;
    public EntraEasyAuthCallerResolver(WorkflowAuthenticationOptions options)
    {
        if (options.Mode != "EntraEasyAuth" || !options.PlatformConfigured) throw new InvalidOperationException("EasyAuth platform configuration is required.");
        this.options = options;
    }

    public WorkflowOwner Resolve(HttpRequestData request)
    {
        var principal = Header(request, "X-MS-CLIENT-PRINCIPAL", 32768, HttpStatusCode.Unauthorized);
        Dictionary<string, List<string>> claims;
        try
        {
            using var json = JsonDocument.Parse(Convert.FromBase64String(principal));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)
                || root.GetProperty("auth_typ").GetString() != "aad") throw new FormatException();
            var entries = root.GetProperty("claims");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 256) throw new FormatException();
            claims = new(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)) throw new FormatException();
                var type = entry.GetProperty("typ").GetString();
                var value = entry.GetProperty("val").GetString();
                if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(value) || type.Length > 512 || value.Length > 4096) throw new FormatException();
                if (!claims.TryGetValue(type, out var values)) claims[type] = values = [];
                values.Add(value);
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        { throw new WorkflowCallerAuthenticationException(HttpStatusCode.Unauthorized); }

        string Single(params string[] aliases)
        {
            var matches = aliases.Where(claims.ContainsKey).ToArray();
            if (matches.Length != 1 || claims[matches[0]].Count != 1) throw new WorkflowCallerAuthenticationException(HttpStatusCode.Forbidden);
            return claims[matches[0]][0];
        }
        var tenant = Single("tid", "http://schemas.microsoft.com/identity/claims/tenantid");
        var issuer = Single("iss");
        if (!Guid.TryParse(tenant, out var tenantId) || tenantId.ToString() != options.TenantId
            || (issuer != $"https://sts.windows.net/{options.TenantId}/" && issuer != $"https://login.microsoftonline.com/{options.TenantId}/v2.0")
            || Single("aud") != options.Audience || Single("idtyp") != "app"
            || claims.ContainsKey("scp") || claims.ContainsKey("http://schemas.microsoft.com/identity/claims/scope"))
            throw new WorkflowCallerAuthenticationException(HttpStatusCode.Forbidden);
        if (!Guid.TryParse(Single("appid", "azp"), out var appId) || !options.AllowedBackendAppIds.Contains(appId.ToString()))
            throw new WorkflowCallerAuthenticationException(HttpStatusCode.Forbidden);
        var roleTypes = new[] { "roles", "http://schemas.microsoft.com/ws/2008/06/identity/claims/role" }.Where(claims.ContainsKey).ToArray();
        if (roleTypes.Length != 1 || claims[roleTypes[0]].Distinct(StringComparer.Ordinal).Count() != claims[roleTypes[0]].Count
            || !claims[roleTypes[0]].Contains(options.RequiredAppRole, StringComparer.Ordinal))
            throw new WorkflowCallerAuthenticationException(HttpStatusCode.Forbidden);

        // The authorized backend, not its service-principal oid, supplies the end-user identity.
        var owner = new WorkflowOwner(Header(request, "X-Poc-Owner-Id", 128, HttpStatusCode.Forbidden),
            Header(request, "X-Poc-Org-Id", 128, HttpStatusCode.Forbidden));
        try { owner.Validate(); }
        catch (ArgumentException) { throw new WorkflowCallerAuthenticationException(HttpStatusCode.Forbidden); }
        return owner;
    }

    private static string Header(HttpRequestData request, string name, int maxLength, HttpStatusCode failure)
    {
        if (!request.Headers.TryGetValues(name, out var values)) throw new WorkflowCallerAuthenticationException(failure);
        var entries = values.ToArray();
        if (entries.Length != 1 || string.IsNullOrWhiteSpace(entries[0]) || entries[0].Length > maxLength)
            throw new WorkflowCallerAuthenticationException(failure);
        return entries[0];
    }
}
