using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The per-user picker (ADR 0018): a user's added provider's declared models appear in their catalog.
/// Uses a fake resolver — no grain, and (with no discovering providers) no ArmClient.
/// </summary>
public class UserModelCatalogTests
{
    private sealed class FakeResolver(IReadOnlyList<ModelInfo> declared, string defaultModelId) : IUserProviderResolver
    {
        public Task<ResolvedProviderSet> ResolveAsync(string userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ResolvedProviderSet(
                new Dictionary<string, MultiProviderChatClientFactory.Provider>(), [], declared, defaultModelId, 0));
    }

    // AC1: the models a user's provider declares list in that user's catalog.
    [Fact]
    public async Task ListsUserProviderModels()
    {
        var declared = new List<ModelInfo>
        {
            new("ollama:llama3.1", "ollama", "Llama 3.1", "llama3.1", null, SupportsTools: true, SupportsVision: false),
        };
        var provider = new UserModelCatalogProvider(
            new FakeResolver(declared, "foundry:gpt-5.4"), armClient: null!, NullLoggerFactory.Instance);

        var result = await (await provider.GetForUserAsync("alice")).GetAsync();

        Assert.Contains(result.Models, m => m.Id == "ollama:llama3.1");
        Assert.Equal("foundry:gpt-5.4", result.Default);
    }

    // AC9: the chain that makes the user-settable default real — the resolver's EffectiveDefault (which
    // prefers the user's overlay Default) surfaces unchanged as /api/models' `default`, so the composer
    // picks it up. Deliberately NOT declared[0]: an insertion-order default would pass by accident.
    // (The overlay-wins and deleted-provider-fallback halves of EffectiveDefault live in
    // UserProviderResolverTests; this covers the catalog leg.)
    [Fact]
    public async Task OverlayDefault_SurfacesAsTheCatalogDefault()
    {
        var declared = new List<ModelInfo>
        {
            new("foundry:gpt-5.4", "foundry", "GPT-5.4", "gpt-5.4", null, SupportsTools: true, SupportsVision: true),
            new("ollama:llama3.1", "ollama", "Llama 3.1", "llama3.1", null, SupportsTools: true, SupportsVision: false),
        };
        var provider = new UserModelCatalogProvider(
            new FakeResolver(declared, "ollama:llama3.1"), armClient: null!, NullLoggerFactory.Instance);

        var result = await (await provider.GetForUserAsync("alice")).GetAsync();

        Assert.Equal("ollama:llama3.1", result.Default);
        Assert.NotEqual(declared[0].Id, result.Default);
    }
}
