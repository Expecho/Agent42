using ModelContextProtocol.Protocol;

namespace Agent42.Backend;

/// <summary>
/// One live bridge socket, as the registry and the connection factory see it (ADR 0076). A seam over
/// <see cref="LocalBridgeSession"/> so the routing and eviction rules are testable without a socket.
/// </summary>
public interface ILocalBridgeConnection
{
    /// <summary>Opens a new MCP channel over this socket and returns it as a transport.</summary>
    Task<ITransport> OpenChannelAsync(CancellationToken cancellationToken);

    /// <summary>Closes the socket with a policy status. Every open channel faults.</summary>
    Task CloseAsync(string reason);
}

/// <summary>
/// In-process map of <c>(userId, serverId)</c> → the live bridge socket for that server, together with the
/// key hash that socket authenticated with (ADR 0076).
///
/// <para><b>Found by key hash, evicted by server.</b> <see cref="IMcpConnectionFactory"/> is handed a
/// <c>StoredMcpServer</c> and no user id, but that record carries its <c>BridgeKeyHash</c> — globally unique
/// and read from the owner's own overlay — so <see cref="FindByKeyHash"/> is the lookup. It also means a
/// socket that authenticated with a key since regenerated is never used, even before it is evicted.</para>
///
/// <para><b>Single replica is load-bearing.</b> A grain activated on another replica could not find a
/// socket held here; <c>infra/main.bicep</c> pins one replica (ADR 0054 already depends on it). Do not
/// add a distributed registry to "fix" that — it is out of scope and recorded in the ADR.</para>
///
/// <para><b>Revocation must reach live sockets.</b> The key directory is only consulted at upgrade time,
/// so every management write to a bridge server calls <see cref="EvictAsync"/> — otherwise a socket that
/// authenticated before a Regenerate, delete, disable or mode switch would keep serving indefinitely.</para>
/// </summary>
public sealed class LocalBridgeRegistry
{
    private sealed record Entry(string KeyHash, ILocalBridgeConnection Connection);

    // One lock, not ConcurrentDictionary.AddOrUpdate: its factories may run more than once under contention,
    // which would misreport both "which socket was replaced" and "was the invalidation claimed".
    private readonly Lock _gate = new();
    private readonly Dictionary<(string UserId, string ServerId), Entry> _live = [];
    private readonly Dictionary<(string UserId, string ServerId), DateTimeOffset> _lastInvalidated = [];
    private readonly TimeProvider _time;

    public LocalBridgeRegistry(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    // Server ids are compared OrdinalIgnoreCase by the config grain; normalise once here so the two agree.
    private static (string, string) Key(string userId, string serverId) => (userId, serverId.ToLowerInvariant());

    /// <summary>
    /// The live socket that authenticated with <paramref name="keyHash"/>, or null when none is connected.
    /// A linear scan: there is one entry per connected helper, a handful at most.
    /// </summary>
    public ILocalBridgeConnection? FindByKeyHash(string keyHash)
    {
        lock (_gate)
        {
            return _live.Values.FirstOrDefault(e => string.Equals(e.KeyHash, keyHash, StringComparison.Ordinal))?.Connection;
        }
    }

    /// <summary>
    /// Makes <paramref name="connection"/> the live socket for this server and returns the one it
    /// replaced, if any — the caller closes that one (a second helper replaces the first).
    /// </summary>
    public ILocalBridgeConnection? Register(string userId, string serverId, string keyHash, ILocalBridgeConnection connection)
    {
        lock (_gate)
        {
            _live.Remove(Key(userId, serverId), out var replaced);
            _live[Key(userId, serverId)] = new Entry(keyHash, connection);
            return replaced is null || ReferenceEquals(replaced.Connection, connection) ? null : replaced.Connection;
        }
    }

    /// <summary>
    /// Removes this server's entry only if it is still <paramref name="connection"/> — a socket that was
    /// already replaced must not unregister its successor when it finally closes.
    /// </summary>
    public void Unregister(string userId, string serverId, ILocalBridgeConnection connection)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(Key(userId, serverId), out var current) && ReferenceEquals(current.Connection, connection))
            {
                _live.Remove(Key(userId, serverId));
            }
        }
    }

    /// <summary>Closes and forgets this server's live socket, if any. Safe to call for any server.</summary>
    public async Task EvictAsync(string userId, string serverId, string reason)
    {
        Entry? entry;
        lock (_gate)
        {
            _live.Remove(Key(userId, serverId), out entry);
        }

        if (entry is not null)
        {
            await entry.Connection.CloseAsync(reason);
        }
    }

    /// <summary>
    /// True at most once per <see cref="LocalBridgeLimits.InvalidateMinInterval"/> per server: whether a
    /// connect should invalidate the user's tool cache. Each invalidation is a Cosmos write and, with two
    /// retained versions, two of them tear down the user's other warm MCP sockets — so a flapping helper
    /// must not turn reconnects into churn.
    /// </summary>
    public bool TryClaimInvalidation(string userId, string serverId)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_lastInvalidated.TryGetValue(Key(userId, serverId), out var last)
                && now - last < LocalBridgeLimits.InvalidateMinInterval)
            {
                return false;
            }

            _lastInvalidated[Key(userId, serverId)] = now;
            return true;
        }
    }
}
