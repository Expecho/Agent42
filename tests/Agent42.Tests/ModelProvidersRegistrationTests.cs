using Agent42.Agents;
using Agent42.Backend;
using Azure.Core;
using Azure.ResourceManager;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// What the Backend host registers for model providers (ADR 0013/0018/0019), resolved from a real container. Before
/// cc-06 this lived inline in <c>Program.cs</c>, which no test runs.
/// </summary>
public sealed class ModelProvidersRegistrationTests
{
    private sealed class NoTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private static (ServiceProvider Sp, ModelProviderSetup Models) Build(params (string Key, string? Value)[] entries)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        var models = services.AddModelProviders(config, new NoTokenCredential());
        return (services.BuildServiceProvider(), models);
    }

    // The shipped baseline: no providers, so chat is entirely the per-user overlay (ADR 0019).
    [Fact]
    public void AnEmptyBaseline_RegistersNoProviders_AndAnEmptyDefault()
    {
        var (sp, models) = Build();
        using var _ = sp;

        Assert.Empty(models.Providers);
        Assert.Empty(models.SkippedProviders);
        Assert.Equal("", sp.GetRequiredService<IChatClientFactory>().DefaultModelId);
        Assert.NotNull(sp.GetRequiredService<ArmClient>());
        Assert.NotNull(sp.GetRequiredService<IModelCatalog>());
    }

    [Fact]
    public void AProviderWithoutAKey_IsSkippedAndReported_NotFatal()
    {
        var (sp, models) = Build(
            ("Models:Providers:github:Kind", "OpenAICompatible"),
            ("Models:Providers:github:Endpoint", "https://models.example/v1"),
            ("Models:Providers:github:ApiKey", ""));
        using var _ = sp;

        Assert.Equal(["github"], models.SkippedProviders);
        Assert.DoesNotContain("github", models.Providers.Keys);
    }

    [Fact]
    public void AProviderWithAKey_IsBuilt_AndTheDefaultModelFlowsToTheFactory()
    {
        var (sp, models) = Build(
            ("Models:Default", "mine:thinker"),
            ("Models:Providers:mine:Kind", "OpenAICompatible"),
            ("Models:Providers:mine:Endpoint", "https://api.example/v1"),
            ("Models:Providers:mine:ApiKey", "key"),
            ("Models:Providers:mine:Models:0:Id", "thinker"));
        using var _ = sp;

        Assert.Contains("mine", models.Providers.Keys);
        Assert.Equal("mine:thinker", sp.GetRequiredService<IChatClientFactory>().DefaultModelId);
    }

    // The per-user layer and the Auto router are what the grain and the endpoints resolve; a registration that drops
    // one fails activation or /api/models at runtime, in a host no test starts.
    [Fact]
    public void RegistersThePerUserLayer_AndTheRouter()
    {
        var (sp, _) = Build();
        using var _ = sp;
        var services = sp.GetRequiredService<IServiceProviderIsService>();

        Assert.True(services.IsService(typeof(IUserProviderResolver)));
        Assert.True(services.IsService(typeof(IUserChatClientFactoryProvider)));
        Assert.True(services.IsService(typeof(IUserModelCatalogProvider)));
        Assert.True(services.IsService(typeof(IUserModelRouterProvider)));
    }
}
