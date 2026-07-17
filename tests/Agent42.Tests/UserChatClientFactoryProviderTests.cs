using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The per-user factory cache (ADR 0018): a save bumps the overlay version, which must return a fresh
/// factory, and distinct users must never share one. Uses a fake resolver — no grain, no network.
/// </summary>
public class UserChatClientFactoryProviderTests
{
    private sealed class FakeResolver : IUserProviderResolver
    {
        private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);

        public void SetVersion(string userId, int version) => _versions[userId] = version;

        public Task<ResolvedProviderSet> ResolveAsync(string userId, CancellationToken cancellationToken = default)
        {
            var version = _versions.TryGetValue(userId, out var v) ? v : 0;
            var provider = MultiProviderChatClientFactory.Provider.OpenAICompatible(
                new ProviderOptions { Kind = ProviderKind.OpenAICompatible },
                new Uri("http://localhost:1234"), "key");
            var providers = new Dictionary<string, MultiProviderChatClientFactory.Provider>(StringComparer.OrdinalIgnoreCase)
            {
                ["p"] = provider,
            };
            return Task.FromResult(new ResolvedProviderSet(providers, [], [], "p:model", version));
        }
    }

    private static UserChatClientFactoryProvider NewProvider(IUserProviderResolver resolver) =>
        new(resolver, "test", NullLogger<MultiProviderChatClientFactory>.Instance);

    // AC6: on save (a version bump) the layer returns a fresh factory; an unchanged version is cached.
    [Fact]
    public async Task VersionBump_ReturnsFreshFactory()
    {
        var resolver = new FakeResolver();
        var provider = NewProvider(resolver);

        var first = await provider.GetForUserAsync("alice");
        Assert.Same(first, await provider.GetForUserAsync("alice")); // unchanged version → cached

        resolver.SetVersion("alice", 1); // a save bumped it
        Assert.NotSame(first, await provider.GetForUserAsync("alice")); // rebuilt
    }

    [Fact]
    public async Task DistinctUsers_GetDistinctFactories()
    {
        var provider = NewProvider(new FakeResolver());

        Assert.NotSame(await provider.GetForUserAsync("alice"), await provider.GetForUserAsync("bob"));
    }
}
