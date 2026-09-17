using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The outcome of one user-pressed connection test. <see cref="Reason"/> is a <b>curated</b> string chosen
/// from a fixed set — never an exception message and never the server's response body, both of which are
/// response-controlled (the <c>ConfigHealth.ProbeAsync</c> discipline). The detail goes to the log instead.
/// </summary>
public sealed record McpTestOutcome(bool Ok, IReadOnlyList<string> ToolNames, string Reason)
{
    public static McpTestOutcome Failure(string reason) => new(false, [], reason);
}

/// <summary>
/// One-shot probe of a single stored MCP server, behind <c>POST /api/mcp-servers/{id}/test</c> (ADR 0064):
/// connects, lists tools, records the discovered names, <b>invalidates that user's cached tool set</b> and
/// disposes the connection.
///
/// <para><b>Why the invalidation lives here and not on a second endpoint (ADR 0069).</b> Without it a
/// successful test reports "connected, 12 tools" while the chat path keeps serving whatever was cached under
/// the current overlay version — the diagnostic and the behaviour disagreeing, both correct. Making Test true
/// is smaller than a <c>/refresh</c> route nobody would know to press. Carve-outs: a failed test and a
/// zero-tool connect both invalidate nothing.</para>
///
/// <para><b>This is not an activation.</b> It never touches <see cref="IUserMcpToolProvider"/> and never
/// populates its version cache — a management action must not open a socket the chat path then holds. The
/// connection is disposed in a <c>finally</c>, so a test costs exactly one connect.</para>
///
/// <para><b>It shares <see cref="McpOAuthTokenResolver"/> with the activation path on purpose:</b> a probe
/// that skipped the near-expiry refresh would report failure on a server chat connects to fine — a false
/// negative in a diagnostic tool, the worst defect this feature could ship.</para>
/// </summary>
public sealed class McpConnectionTester
{
    // Mirrors UserMcpToolProvider.ConnectTimeout deliberately: the test must fail in the same window the
    // activation path would, or "works when tested" and "works in a chat" stop meaning the same thing.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly IGrainFactory _grains;
    private readonly IMcpConnectionFactory _connectionFactory;
    private readonly IMcpSecretProtector _protector;
    private readonly McpOAuthTokenResolver _oauthTokens;
    private readonly ILogger<McpConnectionTester> _logger;

    public McpConnectionTester(
        IGrainFactory grains,
        IMcpConnectionFactory connectionFactory,
        IMcpSecretProtector protector,
        McpOAuthTokenResolver oauthTokens,
        ILogger<McpConnectionTester> logger)
    {
        _grains = grains;
        _connectionFactory = connectionFactory;
        _protector = protector;
        _oauthTokens = oauthTokens;
        _logger = logger;
    }

    /// <summary>
    /// Tests the user's server with <paramref name="serverId"/>. Returns <c>null</c> when the user has no such
    /// server — the endpoint turns that into a 404, so an unknown id and another user's id are indistinguishable.
    /// A <b>disabled</b> server is still tested (the user is diagnosing it); the outcome says it is off.
    /// </summary>
    public async Task<McpTestOutcome?> TestAsync(string userId, string serverId, CancellationToken cancellationToken)
    {
        var config = await _grains.GetGrain<IUserMcpConfigGrain>(userId).GetAsync();
        var server = config.Servers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.Ordinal));
        if (server is null)
        {
            return null;
        }

        if (!Enum.TryParse<McpAuthMode>(server.Auth, ignoreCase: true, out var mode)
            || mode is not (McpAuthMode.None or McpAuthMode.Header or McpAuthMode.ClientCredentials or McpAuthMode.InteractiveOAuth or McpAuthMode.LocalBridge))
        {
            return McpTestOutcome.Failure($"auth mode {server.Auth} is not yet supported");
        }

        // InteractiveOAuth with no stored token has not completed the Connect flow. That is a distinct
        // diagnosis from "could not connect" — the user's next action is Connect, not fixing the URL.
        string? oauthAccessToken = null;
        if (mode == McpAuthMode.InteractiveOAuth)
        {
            oauthAccessToken = await _oauthTokens.ResolveAsync(userId, server, cancellationToken);
            if (oauthAccessToken is null)
            {
                return McpTestOutcome.Failure("not connected (no OAuth token yet)");
            }
        }

        var secrets = new McpConnectionSecrets(
            _protector.Unprotect(server.HeaderValueCiphertext),
            _protector.Unprotect(server.ClientSecretCiphertext),
            oauthAccessToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            var connection = await _connectionFactory.ConnectAsync(server, secrets, timeout.Token);
            try
            {
                var names = connection.Tools.Select(t => t.Name).ToList();
                return await RecordAndDescribeAsync(userId, server, names);
            }
            finally
            {
                // A test is not an activation: the socket dies with the request, never entering the provider's
                // version cache (which is what keeps the cache a chat-path concern only).
                await DisposeQuietlyAsync(connection.Connection, server);
            }
        }
        catch (LocalBridgeNotConnectedException)
        {
            // A distinct diagnosis (ADR 0076), like the OAuth one above: the user's next action is to start the
            // helper on their machine, not to fix a URL — "could not connect" would point them the wrong way.
            return McpTestOutcome.Failure("local bridge not connected");
        }
        catch (Exception ex)
        {
            // Curated to the browser, detailed to the log — the server's own response body is untrusted text.
            _logger.LogWarning(ex,
                "Testing MCP server {Server} for user {User} failed.", server.Name, userId);
            return McpTestOutcome.Failure("could not connect");
        }
    }

    // Records the discovered names, but ONLY when the list is non-empty. RecordToolNamesAsync no-ops when the
    // list is UNCHANGED, not when it is empty, so recording [] over [A,B] ERASES them — which would break ADR
    // 0058's "names are never erased" invariant and reach its supposedly-unreachable state: the picker renders
    // the server unchecked and "tools not discovered yet" while the client still holds mcp:{id}:{tool} keys
    // that silently grant a deselected tool. A zero-tool connect is reported to the user and writes nothing.
    private async Task<McpTestOutcome> RecordAndDescribeAsync(
        string userId, StoredMcpServer server, IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return new McpTestOutcome(true, [], "connected, but the server exposes no tools");
        }

        try
        {
            // INVALIDATION FIRST (ADR 0069), and the order is deliberate: the cached tool set is what the user
            // is actually waiting on, so a failure to record names must not be able to skip it. Both calls hit
            // the same grain, which is why one best-effort block covers both. It is NOT conditional on the
            // names having changed — the state it recovers is a server that was unreachable when the version
            // was first resolved, whose group was cached EMPTY while its names were never erased, so on
            // recovery it reports exactly the names it always had.
            await _grains.GetGrain<IUserMcpConfigGrain>(userId).InvalidateToolCacheAsync();

            // Non-bumping (see IUserMcpConfigGrain.RecordToolNamesAsync): recording names must not tear down
            // every warm grain's MCP sockets. Never SaveAsync.
            await _grains.GetGrain<IUserMcpConfigGrain>(userId).RecordToolNamesAsync(server.Id, names);
        }
        catch (Exception ex)
        {
            // Best-effort, exactly as on the activation path: the connection genuinely worked, which is what
            // the user asked. The picker simply keeps its previous names.
            _logger.LogWarning(ex,
                "Recording discovered tool names for MCP server {Server} (user {User}) failed; the picker keeps the previous names.",
                server.Name, userId);
        }

        return new McpTestOutcome(true, names, server.Enabled ? "ok" : "ok, but this server is switched off");
    }

    private async Task DisposeQuietlyAsync(IAsyncDisposable connection, StoredMcpServer server)
    {
        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disposing the test connection to MCP server {Server} failed.", server.Name);
        }
    }
}
