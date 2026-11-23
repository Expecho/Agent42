using Agent42.Backend;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace Agent42.Tests;

/// <summary>
/// <c>appsettings.json</c> ships <c>AzureOpenAI:TenantId</c> as <c>""</c>; Azure.Identity throws on an empty tenant id
/// (<c>ArgumentException: Invalid tenant id provided</c>), which crashed the backend at startup under the AppHost.
/// </summary>
public sealed class AzureCredentialSettingsTests
{
    private const string RealTenant = "11111111-2222-3333-4444-555555555555";

    private static IConfiguration Config(string? tenantId) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(tenantId is null ? [] : [new("AzureOpenAI:TenantId", tenantId)])
            .Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_or_empty_tenant_id_builds_a_credential_instead_of_throwing(string? tenantId)
    {
        var options = AzureCredentialSettings.Options(Config(tenantId));

        Assert.Null(options.TenantId);
        _ = new DefaultAzureCredential(options); // threw "Invalid tenant id provided" with the raw "" before the fix
    }

    [Fact]
    public void A_configured_tenant_id_is_passed_through()
    {
        var options = AzureCredentialSettings.Options(Config(RealTenant));

        Assert.Equal(RealTenant, options.TenantId);
        _ = new DefaultAzureCredential(options);
    }
}
