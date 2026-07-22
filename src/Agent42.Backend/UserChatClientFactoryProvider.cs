using System.Collections.Concurrent;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The backend <see cref="IUserChatClientFactoryProvider"/> (ADR 0018): builds a per-user
/// <see cref="MultiProviderChatClientFactory"/> from the user's resolved (baseline ⊕ overlay) provider
/// set, cached by a <b>version stamp</b> the overlay grain bumps on every save.
///
/// <para>Effect: an activation on an unchanged overlay reuses the cached factory (no HTTP pipelines
/// rebuilt); a save bumps the version, so the next activation rebuilds and the change takes effect with
/// no app restart. Full live-invalidation of already-activated grains is out of scope (ADR 0018) — they
/// pick it up on next activation.</para>
/// </summary>
public sealed class UserChatClientFactoryProvider : IUserChatClientFactoryProvider
{
    private readonly IUserProviderResolver _resolver;
    private readonly string _otelSourceName;
    private readonly ILogger<MultiProviderChatClientFactory> _logger;

    // The cache is never evicted — negligible for a one-user app; revisit if it grows multi-user
    // (the non-blocking note the plan's critic flagged).
    private readonly ConcurrentDictionary<string, CachedFactory> _cache = new(StringComparer.Ordinal);

    public UserChatClientFactoryProvider(
        IUserProviderResolver resolver,
        string otelSourceName,
        ILogger<MultiProviderChatClientFactory> logger)
    {
        _resolver = resolver;
        _otelSourceName = otelSourceName;
        _logger = logger;
    }

    public async Task<IChatClientFactory> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var resolved = await _resolver.ResolveAsync(userId, cancellationToken);

        // A cache hit is same-version only: a save bumps the version, so a stale factory is never served.
        if (_cache.TryGetValue(userId, out var cached) && cached.Version == resolved.Version)
        {
            return cached.Factory;
        }

        var factory = new MultiProviderChatClientFactory(
            resolved.Providers, resolved.DefaultModelId, _otelSourceName, _logger,
            resolved.EmbeddingDefaultModelId);
        _cache[userId] = new CachedFactory(resolved.Version, factory);
        return factory;
    }

    private sealed record CachedFactory(int Version, IChatClientFactory Factory);
}
