using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Poc.Core;

namespace Poc.AppService.Authentication;

/// <summary>
/// Stub caller resolver for local development and unit tests.
/// Reads X-Poc-Owner-Id and X-Poc-Org-Id from headers, or falls back to configured defaults.
/// </summary>
public sealed class StubAppServiceCallerResolver(IOptions<AppServiceAuthenticationOptions>? options = null) : IAppServiceCallerResolver
{
    private readonly AppServiceAuthenticationOptions authOptions = options?.Value ?? new();

    public WorkflowOwner? Resolve(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        string? ownerId = null;
        string? orgId = null;

        if (httpContext.Request.Headers.TryGetValue("X-Poc-Owner-Id", out var ownerValues))
        {
            ownerId = ownerValues.FirstOrDefault();
        }

        if (httpContext.Request.Headers.TryGetValue("X-Poc-Org-Id", out var orgValues))
        {
            orgId = orgValues.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(ownerId))
        {
            if (!string.IsNullOrWhiteSpace(orgId))
                throw new ArgumentException("X-Poc-Org-Id cannot be specified without X-Poc-Owner-Id.");

            if (!string.IsNullOrWhiteSpace(authOptions.DefaultOwnerId))
            {
                ownerId = authOptions.DefaultOwnerId;
                orgId = authOptions.DefaultOrgId;
            }
            else
            {
                return null;
            }
        }

        var owner = new WorkflowOwner(ownerId, orgId);
        owner.Validate();
        return owner;
    }
}
