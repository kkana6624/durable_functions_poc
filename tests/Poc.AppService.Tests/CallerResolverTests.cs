using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Poc.AppService.Authentication;
using Poc.Core;
using Xunit;

namespace Poc.AppService.Tests;

public class CallerResolverTests
{
    [Fact]
    public void StubResolver_WithHeaders_ResolvesOwnerAndOrg()
    {
        var resolver = new StubAppServiceCallerResolver();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Poc-Owner-Id"] = "alice";
        context.Request.Headers["X-Poc-Org-Id"] = "engineering";

        var owner = resolver.Resolve(context);

        Assert.NotNull(owner);
        Assert.Equal("alice", owner.OwnerId);
        Assert.Equal("engineering", owner.OrgId);
    }

    [Fact]
    public void StubResolver_WithoutHeaders_ReturnsNullWhenNoDefault()
    {
        var resolver = new StubAppServiceCallerResolver();
        var context = new DefaultHttpContext();

        var owner = resolver.Resolve(context);

        Assert.Null(owner);
    }

    [Fact]
    public void StubResolver_WithoutHeaders_UsesConfiguredDefaults()
    {
        var options = Options.Create(new AppServiceAuthenticationOptions
        {
            DefaultOwnerId = "default-user",
            DefaultOrgId = "default-org"
        });
        var resolver = new StubAppServiceCallerResolver(options);
        var context = new DefaultHttpContext();

        var owner = resolver.Resolve(context);

        Assert.NotNull(owner);
        Assert.Equal("default-user", owner.OwnerId);
        Assert.Equal("default-org", owner.OrgId);
    }

    [Fact]
    public void StubResolver_WithOrgOnly_ThrowsArgumentException()
    {
        var resolver = new StubAppServiceCallerResolver();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Poc-Org-Id"] = "engineering";

        Assert.Throws<ArgumentException>(() => resolver.Resolve(context));
    }

    [Fact]
    public void ClaimsPrincipalResolver_WithValidClaims_ResolvesOwner()
    {
        var resolver = new ClaimsPrincipalAppServiceCallerResolver();
        var context = new DefaultHttpContext();
        var claims = new[]
        {
            new Claim("oid", "user-uuid-1234"),
            new Claim("tid", "tenant-uuid-5678")
        };
        var identity = new ClaimsIdentity(claims, "AuthenticationTypes.Federation");
        context.User = new ClaimsPrincipal(identity);

        var owner = resolver.Resolve(context);

        Assert.NotNull(owner);
        Assert.Equal("user-uuid-1234", owner.OwnerId);
        Assert.Equal("tenant-uuid-5678", owner.OrgId);
    }

    [Fact]
    public void ClaimsPrincipalResolver_Unauthenticated_ReturnsNull()
    {
        var resolver = new ClaimsPrincipalAppServiceCallerResolver();
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity()); // IsAuthenticated = false

        var owner = resolver.Resolve(context);

        Assert.Null(owner);
    }

    [Fact]
    public void ClaimsPrincipalResolver_SpoofedOwnerHeader_ThrowsWorkflowForbiddenException()
    {
        var resolver = new ClaimsPrincipalAppServiceCallerResolver();
        var context = new DefaultHttpContext();
        var claims = new[]
        {
            new Claim("oid", "user-uuid-1234"),
            new Claim("tid", "tenant-uuid-5678")
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        // Attacker attempts to forge X-Poc-Owner-Id to impersonate bob
        context.Request.Headers["X-Poc-Owner-Id"] = "bob";

        Assert.Throws<WorkflowForbiddenException>(() => resolver.Resolve(context));
    }

    [Fact]
    public void EasyAuthResolver_WithValidClientPrincipalHeader_ResolvesOwner()
    {
        var resolver = new EasyAuthAppServiceCallerResolver();
        var context = new DefaultHttpContext();

        var payload = new
        {
            auth_typ = "aad",
            claims = new[]
            {
                new { typ = "oid", val = "easyauth-user-1" },
                new { typ = "tid", val = "easyauth-org-1" }
            }
        };
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        context.Request.Headers["X-MS-CLIENT-PRINCIPAL"] = base64;

        var owner = resolver.Resolve(context);

        Assert.NotNull(owner);
        Assert.Equal("easyauth-user-1", owner.OwnerId);
        Assert.Equal("easyauth-org-1", owner.OrgId);
    }

    [Fact]
    public void EasyAuthResolver_SpoofedOwnerHeader_ThrowsWorkflowForbiddenException()
    {
        var resolver = new EasyAuthAppServiceCallerResolver();
        var context = new DefaultHttpContext();

        var payload = new
        {
            auth_typ = "aad",
            claims = new[]
            {
                new { typ = "oid", val = "easyauth-user-1" }
            }
        };
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        context.Request.Headers["X-MS-CLIENT-PRINCIPAL"] = base64;
        context.Request.Headers["X-Poc-Owner-Id"] = "impersonated-user";

        Assert.Throws<WorkflowForbiddenException>(() => resolver.Resolve(context));
    }
}
