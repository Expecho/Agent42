using System.Collections.Concurrent;
using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// The backend <see cref="IUserMcpToolProvider"/> (config-driven remote MCP servers): connects to a user's
/// enabled MCP servers and returns the union of their tools, cached by the overlay <b>version</b> the config
/// grain bumps on every save. Mirrors <see cref="UserChatClientFactoryProvider"/>'s versioned cache — an
/// activation on an unchanged overlay reuses the cached tools; a save bumps the version, so the next grain
/// activation reconnects and the change takes effect with no app restart (in-flight grains pick it up on
/// their next activation — accepted staleness).
///
/// <para><b>Best-effort:</b> a dead or slow MCP server is connected with a timeout and skipped on failure —
/// it never breaks grain activation or the rest of the tool set.</para>
///
/// <para><b>Bounded lifetime:</b> unlike the chat-client factory (a cheap object), an MCP connection is a
/// live socket. A never-evicted version cache would leak one open connection per config change, but disposing
/// a version's connections while a warm grain still holds its tools is unsafe. So the cache retains the most
/// recent <see cref="RetainedVersionsPerUser"/> versions per user and disposes older ones on eviction — a
/// grain on an evicted version sees a disposed client as a skipped tool (tolerated).</para>
/// </summary>
public sealed class UserMcpToolProvider : IUserMcpToolProvider
{
    // Retain the current version plus one prior, so a grain still on the previous version keeps working
    // until it too is evicted. Bounds the open-connection count to N per user with a config change.
    private const int RetainedVersionsPerUser = 2;

    // A dead server must not stall the non-reentrant OnActivateAsync that awaits this — cap each server's
    // connect + ListTools, then skip it. Belt-and-suspenders with the transport's own ConnectionTimeout.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    // Refresh an InteractiveOAuth access token at connect if it expires within this window (app-owned refresh).
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    private readonly IGrainFactory _grains;
    private readonly IMcpConnectionFactory _connectionFactory;
    private readonly IMcpSecretProtector _protector;
    private readonly McpOAuthClient _oauth;
    private readonly ILogger<UserMcpToolProvider> _logger;

    private readonly ConcurrentDictionary<string, UserCache> _caches = new(StringComparer.Ordinal);

    public UserMcpToolProvider(
        IGrainFactory grains,
        IMcpConnectionFactory connectionFactory,
        IMcpSecretProtector protector,
        McpOAuthClient oauth,
        ILogger<UserMcpToolProvider> logger)
    {
        _grains = grains;
        _connectionFactory = connectionFactory;
        _protector = protector;
        _oauth = oauth;
        _logger = logger;
    }

    public async Task<McpToolSet> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var version = await _grains.GetGrain<IUserMcpConfigGrain>(userId).GetVersionAsync();
        var cache = _caches.GetOrAdd(userId, _ => new UserCache());

        // Serialize resolution per user so two concurrent activations don't both open connection storms;
        // a cache hit on the current version returns without connecting.
        await cache.Gate.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGet(version, out var hit))
            {
                return hit;
            }

            var config = await _grains.GetGrain<IUserMcpConfigGrain>(userId).GetAsync();
            var (tools, connections) = await ConnectAllAsync(userId, config, cancellationToken);
            var set = new McpToolSet(tools, version);

            cache.Store(version, set, connections);
            await cache.EvictBeyondAsync(RetainedVersionsPerUser, _logger);
            return set;
        }
        finally
        {
            cache.Gate.Release();
        }
    }

    private async Task<(IReadOnlyList<AITool> Tools, List<IAsyncDisposable> Connections)> ConnectAllAsync(
        string userId, UserMcpConfig config, CancellationToken cancellationToken)
    {
        var tools = new List<AITool>();
        var connections = new List<IAsyncDisposable>();

        foreach (var server in config.Servers.Where(s => s.Enabled))
        {
            // None/Header (Phase 1), ClientCredentials (Phase 2), and InteractiveOAuth (Phase 3) connect.
            if (!Enum.TryParse<McpAuthMode>(server.Auth, ignoreCase: true, out var mode)
                || mode is not (McpAuthMode.None or McpAuthMode.Header or McpAuthMode.ClientCredentials or McpAuthMode.InteractiveOAuth))
            {
                _logger.LogInformation(
                    "Skipping MCP server {Server} for user {User}: auth mode {Mode} is not yet supported.",
                    server.Name, userId, server.Auth);
                continue;
            }

            // InteractiveOAuth: resolve the current access token (refreshing at connect if near expiry). A
            // server that has not completed the Connect flow yet has no token — skip it (not connected).
            string? oauthAccessToken = null;
            if (mode == McpAuthMode.InteractiveOAuth)
            {
                oauthAccessToken = await ResolveOAuthAccessTokenAsync(userId, server, cancellationToken);
                if (oauthAccessToken is null)
                {
                    _logger.LogInformation(
                        "Skipping MCP server {Server} for user {User}: not connected (no OAuth token yet).",
                        server.Name, userId);
                    continue;
                }
            }

            // Decrypt every secret the mode might need, before the grain-free connection factory sees it.
            var secrets = new McpConnectionSecrets(
                _protector.Unprotect(server.HeaderValueCiphertext),
                _protector.Unprotect(server.ClientSecretCiphertext),
                oauthAccessToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                var connection = await _connectionFactory.ConnectAsync(server, secrets, timeout.Token);
                // Wrap each tool so its persisted result is bounded (ADR 0004), preserving name + schema.
                tools.AddRange(connection.Tools.Select(WrapForTruncation));
                connections.Add(connection.Connection);
            }
            catch (Exception ex)
            {
                // Best-effort: a dead/slow/misconfigured server is skipped, never fatal to activation or the
                // rest of the tool set (the overlay best-effort rule, ADR 0018).
                _logger.LogWarning(ex,
                    "MCP server {Server} for user {User} could not be reached; skipping its tools.",
                    server.Name, userId);
            }
        }

        return (tools, connections);
    }

    // Resolves the current InteractiveOAuth access token for a server, refreshing it (app-owned) when it is
    // within the skew of expiry and a refresh token is available, and persisting the refreshed bundle. Returns
    // null when the server has no stored token (the Connect flow hasn't run) → the caller skips it.
    private async Task<string?> ResolveOAuthAccessTokenAsync(string userId, StoredMcpServer server, CancellationToken cancellationToken)
    {
        var tokens = McpOAuthTokens.Deserialize(_protector.Unprotect(server.TokenCiphertext));
        if (tokens is null)
        {
            return null; // not connected yet
        }

        var nearExpiry = tokens.ExpiresAt is { } expiresAt && expiresAt - DateTimeOffset.UtcNow <= RefreshSkew;
        if (!nearExpiry || string.IsNullOrEmpty(tokens.RefreshToken))
        {
            return tokens.AccessToken;
        }

        try
        {
            var refreshed = await _oauth.RefreshAsync(tokens, new Uri(server.Endpoint), cancellationToken);
            // Persist the refreshed bundle (bumps the overlay version, so the next activation re-reads it).
            await _grains.GetGrain<IUserMcpConfigGrain>(userId)
                .SaveAsync(server with { TokenCiphertext = _protector.Protect(refreshed.Serialize()) });
            return refreshed.AccessToken;
        }
        catch (Exception ex)
        {
            // A failed refresh is best-effort: try the existing token (it may still be within skew), and if it
            // too is rejected the connect fails and the server is skipped.
            _logger.LogWarning(ex,
                "MCP OAuth token refresh failed for server {Server} (user {User}); using the existing token.",
                server.Name, userId);
            return tokens.AccessToken;
        }
    }

    // An MCP tool is an AIFunction (Step 0 spike), so it can be truncation-wrapped; anything else (there is
    // none today) passes through unchanged.
    private static AITool WrapForTruncation(AITool tool) =>
        tool is AIFunction fn ? new TruncatingMcpTool(fn) : tool;

    /// <summary>Per-user cache of the most recent versions' tool sets and their live connections.</summary>
    private sealed class UserCache
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        // Ordered oldest→newest; small (bounded by RetainedVersionsPerUser).
        private readonly List<Entry> _entries = [];

        public bool TryGet(int version, out McpToolSet set)
        {
            foreach (var e in _entries)
            {
                if (e.Version == version)
                {
                    set = e.Set;
                    return true;
                }
            }

            set = McpToolSet.Empty;
            return false;
        }

        public void Store(int version, McpToolSet set, List<IAsyncDisposable> connections)
        {
            _entries.RemoveAll(e => e.Version == version);
            _entries.Add(new Entry(version, set, connections));
        }

        public async Task EvictBeyondAsync(int retain, ILogger logger)
        {
            while (_entries.Count > retain)
            {
                var oldest = _entries[0];
                _entries.RemoveAt(0);
                foreach (var connection in oldest.Connections)
                {
                    try
                    {
                        await connection.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Disposing an evicted MCP connection (version {Version}) failed.", oldest.Version);
                    }
                }
            }
        }

        private sealed record Entry(int Version, McpToolSet Set, List<IAsyncDisposable> Connections);
    }
}
