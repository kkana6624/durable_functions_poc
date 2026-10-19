using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Poc.AppService.Tests;

public class WorkflowConfigurationIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public WorkflowConfigurationIntegrationTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
    }

    [Theory]
    [InlineData("WORKFLOW_FUNCTION_KEY", "@Microsoft.KeyVault(SecretUri=https://private-vault.vault.azure.net/secrets/private-key)")]
    [InlineData("WorkflowClient:FunctionKey", "@Microsoft.KeyVault(SecretUri=https://private-vault.vault.azure.net/secrets/private-key)")]
    [InlineData("WORKFLOW_FUNCTION_KEY", "  @microsoft.keyvault(VaultName=private-vault;SecretName=private-key)")]
    public void UnresolvedKeyVaultReference_RefusesStartupWithoutExposingReference(string setting, string reference)
    {
        using var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting(setting, reference);
        });

        var exception = Assert.Throws<OptionsValidationException>(() => configuredFactory.CreateClient());

        Assert.Contains("The workflow Function key reference has not been resolved.", exception.Message);
        Assert.DoesNotContain(reference, exception.ToString());
        Assert.DoesNotContain("private-vault", exception.ToString());
        Assert.DoesNotContain("private-key", exception.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("resolved-test-function-key")]
    public async Task MissingOrResolvedFunctionKey_AllowsStartup(string? key)
    {
        using var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            if (key is not null)
                builder.UseSetting("WORKFLOW_FUNCTION_KEY", key);
        });
        using var client = configuredFactory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
