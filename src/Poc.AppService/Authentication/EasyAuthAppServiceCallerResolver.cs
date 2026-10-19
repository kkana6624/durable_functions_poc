using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Poc.Core;

namespace Poc.AppService.Authentication;

/// <summary>
/// Resolves workflow caller from Azure App Service built-in EasyAuth (X-MS-CLIENT-PRINCIPAL header).
/// Parses client claims from base64 JSON header and prevents client header spoofing.
/// </summary>
public sealed class EasyAuthAppServiceCallerResolver : IAppServiceCallerResolver
{
    public WorkflowOwner? Resolve(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (!httpContext.Request.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var principalHeaders))
        {
            return null;
        }

        var header = principalHeaders.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        string? ownerId = null;
        string? orgId = null;

        try
        {
            var jsonBytes = Convert.FromBase64String(header);
            using var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;

            if (root.TryGetProperty("claims", out var claimsElement) && claimsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var claim in claimsElement.EnumerateArray())
                {
                    if (claim.TryGetProperty("typ", out var typEl) && claim.TryGetProperty("val", out var valEl))
                    {
                        var typ = typEl.GetString();
                        var val = valEl.GetString();
                        if (string.IsNullOrWhiteSpace(typ) || string.IsNullOrWhiteSpace(val)) continue;

                        if (ownerId is null && (
                            typ is "http://schemas.microsoft.com/identity/claims/objectidentifier" or "oid" or "sub" or "preferred_username"))
                        {
                            ownerId = val;
                        }
                        if (orgId is null && (
                            typ is "http://schemas.microsoft.com/identity/claims/tenantid" or "tid" or "tenant_id"))
                        {
                            orgId = val;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return null;
        }

        // Anti-impersonation check
        if (httpContext.Request.Headers.TryGetValue("X-Poc-Owner-Id", out var spoofedOwners))
        {
            var spoofed = spoofedOwners.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(spoofed) && !string.Equals(spoofed, ownerId, StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkflowForbiddenException("X-Poc-Owner-Id does not match EasyAuth identity.");
            }
        }

        var owner = new WorkflowOwner(ownerId, orgId);
        owner.Validate();
        return owner;
    }
}
