using Microsoft.AspNetCore.Http;
using Poc.Core;

namespace Poc.AppService.Authentication;

/// <summary>
/// Resolves the authenticated workflow owner from the current HTTP request.
/// Prevents client impersonation by deriving identity from validated credentials/claims.
/// </summary>
public interface IAppServiceCallerResolver
{
    WorkflowOwner? Resolve(HttpContext httpContext);
}
