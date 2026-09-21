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

    private readonly IGrainFactory _grains;
    private readonly IMcpConnectionFactory _connectionFactory;
    private readonly IMcpSecretProtector _protector;
    private readonly McpOAuthTokenResolver _oauthTokens;
    private readonly ILogger<UserMcpToolProvider> _logger;

    private readonly ConcurrentDictionary<string, UserCache> _caches = new(StringComparer.Ordinal);

    public UserMcpToolProvider(
        IGrainFactory grains,
        IMcpConnectionFactory connectionFactory,
        IMcpSecretProtector protector,
        McpOAuthTokenResolver oauthTokens,
        ILogger<UserMcpToolProvider> logger)
    {
        _grains = grains;
        _connectionFactory = connectionFactory;
        _protector = protector;
        _oauthTokens = oauthTokens;
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
            var (servers, connections) = await ConnectAllAsync(userId, config, cancellationToken);
            var set = new McpToolSet(servers, version);

            cache.Store(version, set, connections);
            await cache.EvictBeyondAsync(RetainedVersionsPerUser, _logger);
            return set;
        }
        finally
        {
            cache.Gate.Release();
        }
    }

    private async Task<(IReadOnlyList<McpServerTools> Servers, List<IAsyncDisposable> Connections)> ConnectAllAsync(
        string userId, UserMcpConfig config, CancellationToken cancellationToken)
    {
        var servers = new List<McpServerTools>();
        var connections = new List<IAsyncDisposable>();

        foreach (var server in config.Servers.Where(s => s.Enabled))
        {
            // Every management-enabled server yields a group, even when it contributes no tools: the grain
            // needs the id to expand the `mcp` wildcard into the complete per-server key set (ADR 0026). A
            // skipped/unreachable server is recorded as an empty group here (see the Skip local).
            //
            // A skipped group still carries the server's PREVIOUSLY discovered KnownToolNames. This is what
            // makes "names are never erased" true: a server that is briefly unreachable keeps its per-tool
            // checkboxes, so a user's partial per-tool selection survives the outage instead of being dropped
            // by the next picker Save. It is also why KnownToolNames is not derived from Tools — here Tools is
            // empty and the names are not.
            void Skip(string reason, bool reauthRequired = false)
            {
                _logger.LogInformation("MCP server {Server} for user {User}: {Reason}", server.Name, userId, reason);
                servers.Add(EmptyGroup(server, reauthRequired));
            }

            // None/Header (Phase 1), ClientCredentials (Phase 2), InteractiveOAuth (Phase 3) and LocalBridge
            // (ADR 0076 — the decorated factory throws at once when its helper is not connected) connect.
            if (!Enum.TryParse<McpAuthMode>(server.Auth, ignoreCase: true, out var mode)
                || mode is not (McpAuthMode.None or McpAuthMode.Header or McpAuthMode.ClientCredentials or McpAuthMode.InteractiveOAuth or McpAuthMode.LocalBridge))
            {
                Skip($"auth mode {server.Auth} is not yet supported");
                continue;
            }

            InteractiveOAuthTokenProvider? oauthBearer = null;
            if (mode == McpAuthMode.InteractiveOAuth)
            {
                (oauthBearer, var skipReason) = await ResolveOAuthBearerAsync(userId, server, cancellationToken);
                if (oauthBearer is null)
                {
                    Skip(skipReason!, reauthRequired: skipReason == ReauthSkipReason);
                    continue;
                }
            }

            // Decrypt every secret the mode might need, before the grain-free connection factory sees it.
            var secrets = new McpConnectionSecrets(
                _protector.Unprotect(server.HeaderValueCiphertext),
                _protector.Unprotect(server.ClientSecretCiphertext),
                oauthBearer);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                var connection = await _connectionFactory.ConnectAsync(server, secrets, timeout.Token);
                // Wrap each tool so its persisted result is bounded (ADR 0004), preserving name + schema.
                var tools = connection.Tools.Select(WrapForTruncation).ToList();

                // Names are learned on a SUCCESSFUL connect. This is one of TWO writers — McpConnectionTester
                // (the user-pressed POST /api/mcp-servers/{id}/test) records them too, and the two must agree.
                // Persist them (non-bumping, and a no-op when unchanged — see
                // IUserMcpConfigGrain.RecordToolNamesAsync) so GET /api/tools can render per-tool checkboxes
                // without ever connecting, and hand the SAME list to the grain on the group. One source of
                // names for the picker and the gate: if these two ever diverge, the picker mints per-tool keys
                // with no checkbox and the next Save silently drops them.
                var discovered = tools.Select(t => t.Name).ToList();
                await RecordToolNamesAsync(userId, server, discovered);

                // The URL-seeding opt-in (ADR 0067) is copied verbatim off the stored server — it is the
                // user's setting, so the provider never derives or defaults it. The harvest itself happens in
                // the GRAIN, not here: these tool instances are shared across every conversation of this
                // user, and FetchableUrls is per-conversation state.
                servers.Add(new McpServerTools(
                    server.Id, tools, discovered, server.SeedsFetchableUrls, server.Name,
                    InteractiveOAuth: oauthBearer is not null));
                connections.Add(connection.Connection);
            }
            catch (LocalBridgeNotConnectedException)
            {
                // The expected state whenever the user's machine is off (ADR 0076) — an information line, not a
                // warning with a stack trace on every activation.
                Skip("local bridge not connected");
            }
            catch (Exception ex)
            {
                // Best-effort: a dead/slow/misconfigured server is skipped, never fatal to activation or the
                // rest of the tool set (the overlay best-effort rule, ADR 0018). Still recorded as an empty
                // group so its id survives for wildcard expansion.
                _logger.LogWarning(ex,
                    "MCP server {Server} for user {User} could not be reached; skipping its tools.",
                    server.Name, userId);

                // The connect's own 401 → renew → retry may have found the grant dead just now (invalid_grant).
                servers.Add(EmptyGroup(server, reauthRequired: oauthBearer?.State == McpOAuthState.ReauthRequired));
            }
        }

        return (servers, connections);
    }

    private const string ReauthSkipReason = "OAuth authorization expired; reconnect needed";

    // InteractiveOAuth: resolve once now, then hand the connection a PER-REQUEST bearer seeded with this resolution
    // (ADR 0081) — the connection outlives the access token in the version cache, so a static bearer would go stale
    // there. No bearer means skip, with the reason: never connected (no token yet), or a dead grant, which is
    // skipped WITHOUT connecting (a connect would only earn a 401) and flagged so the grain can offer Reconnect.
    private async Task<(InteractiveOAuthTokenProvider? Bearer, string? SkipReason)> ResolveOAuthBearerAsync(
        string userId, StoredMcpServer server, CancellationToken cancellationToken)
    {
        var resolution = await _oauthTokens.ResolveAsync(userId, server.Id, cancellationToken: cancellationToken);
        return resolution.State switch
        {
            McpOAuthState.NotConnected => (null, "not connected (no OAuth token yet)"),
            McpOAuthState.ReauthRequired => (null, ReauthSkipReason),
            _ => (new InteractiveOAuthTokenProvider(userId, server.Id, _oauthTokens, resolution), null),
        };
    }

    // The group of a server that contributes no tools this activation: it keeps its id (wildcard expansion) and its
    // previously discovered names (never erased).
    private static McpServerTools EmptyGroup(StoredMcpServer server, bool reauthRequired = false) =>
        new(server.Id, [], server.KnownToolNames, server.SeedsFetchableUrls, server.Name, reauthRequired,
            string.Equals(server.Auth, nameof(McpAuthMode.InteractiveOAuth), StringComparison.OrdinalIgnoreCase));

    // Persists the tool names just discovered from a server. Best-effort by design: this is picker metadata,
    // so a storage hiccup must not fail an activation that otherwise has working tools — the names simply stay
    // as they were and are re-recorded on the next successful connect.
    private async Task RecordToolNamesAsync(string userId, StoredMcpServer server, IReadOnlyList<string> names)
    {
        try
        {
            await _grains.GetGrain<IUserMcpConfigGrain>(userId).RecordToolNamesAsync(server.Id, names);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Recording discovered tool names for MCP server {Server} (user {User}) failed; the picker keeps the previous names.",
                server.Name, userId);
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
