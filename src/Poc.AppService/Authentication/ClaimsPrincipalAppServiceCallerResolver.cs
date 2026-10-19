using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Poc.Core;

namespace Poc.AppService.Authentication;

/// <summary>
/// Resolves workflow caller from the authenticated ASP.NET Core ClaimsPrincipal (e.g. Entra ID JWT bearer token).
/// Prevents client impersonation by rejecting spoofed X-Poc-Owner-Id / X-Poc-Org-Id headers
/// that conflict with the authenticated identity.
/// </summary>
public sealed class ClaimsPrincipalAppServiceCallerResolver : IAppServiceCallerResolver
{
    public WorkflowOwner? Resolve(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var principal = httpContext.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        // Extract owner ID from claims: oid (object ID) -> sub -> nameidentifier -> upn -> preferred_username
        var ownerId = principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? principal.FindFirst("oid")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.Upn)?.Value
            ?? principal.FindFirst("preferred_username")?.Value;

        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return null;
        }

        // Extract tenant ID / organization ID from claims: tid -> tenantid
        var orgId = principal.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value
            ?? principal.FindFirst("tid")?.Value
            ?? principal.FindFirst("tenant_id")?.Value;

        // Anti-impersonation check: If client sends X-Poc-Owner-Id, verify it matches authenticated identity
        if (httpContext.Request.Headers.TryGetValue("X-Poc-Owner-Id", out var spoofedOwnerValues))
        {
            var spoofedOwner = spoofedOwnerValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(spoofedOwner) && !string.Equals(spoofedOwner, ownerId, StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkflowForbiddenException("X-Poc-Owner-Id does not match authenticated identity.");
            }
        }

        if (httpContext.Request.Headers.TryGetValue("X-Poc-Org-Id", out var spoofedOrgValues))
        {
            var spoofedOrg = spoofedOrgValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(spoofedOrg) && !string.IsNullOrWhiteSpace(orgId) && !string.Equals(spoofedOrg, orgId, StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkflowForbiddenException("X-Poc-Org-Id does not match authenticated tenant.");
            }
        }

        var owner = new WorkflowOwner(ownerId, orgId);
        owner.Validate();
        return owner;
    }
}
