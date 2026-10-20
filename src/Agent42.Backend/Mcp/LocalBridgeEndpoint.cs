using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// <c>/api/local-bridge</c> (ADR 0076): where a user's <c>Agent42.LocalBridge</c> helper opens its
/// WebSocket. The path is on Easy Auth's <c>excludedPaths</c>, so this endpoint is publicly reachable and
/// <b>self-enforces, failing closed</b> — the ADR 0022 / ADR 0070 rule.
///
/// <para><b>The key is read from the <c>X-Api-Key</c> header</b>, or, for the Agent42 Chrome extension
/// only, from <c>Sec-WebSocket-Protocol</c> next to <see cref="LocalBridgeLimits.ExtensionSubprotocol"/>.
/// Never add a query-string fallback: besides leaking the key into access logs, it would let a <i>web
/// page</i> open this socket. What keeps every page out is the <b>Origin rule</b>: a request carrying an
/// <c>Origin</c> other than <see cref="LocalBridgeLimits.ExtensionOrigin"/> is refused even with a valid
/// key, and the subprotocol key is read only under that exact Origin. Web pages cannot forge <c>Origin</c>.
/// The console helper sends none. The Origin is not a credential (see its doc).</para>
///
/// <para><b>A directory hit is not enough.</b> The owner is re-read from the user's own overlay and the
/// server must still exist, be enabled, be <see cref="McpAuthMode.LocalBridge"/>, and carry this exact
/// hash — so a stale directory entry (a crash between the grain's write and its deregister) cannot
/// authenticate. Every failure is the same 401, and it is decided <i>before</i> the upgrade check, so a
/// caller without a key cannot learn anything from the status code.</para>
/// </summary>
public static class LocalBridgeEndpoint
{
    public const string Route = "/api/local-bridge";

    /// <summary>The single body every rejection returns. Says nothing about which check failed.</summary>
    public const string RejectionBody = "Missing or invalid X-Api-Key.";

    /// <summary>An authenticated bridge: whose server this socket serves, and the key hash it presented.</summary>
    public sealed record Caller(string UserId, string ServerId, string KeyHash);

    /// <summary>The key a request presents, and whether it came in the extension's subprotocol.</summary>
    public sealed record PresentedKey(string Key, bool ViaSubprotocol);

    /// <summary>
    /// The key a request presents, or null when there is none or when its <c>Origin</c> is refused. Any
    /// <c>Origin</c> other than the extension's is a web page (or something posing as one) and is refused
    /// outright, before the key is even looked at. Under the extension's Origin, the subprotocol list must be
    /// exactly <see cref="LocalBridgeLimits.ExtensionSubprotocol"/> plus one other value, the key.
    /// </summary>
    public static PresentedKey? ResolveKey(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        var fromExtension = string.Equals(origin, LocalBridgeLimits.ExtensionOrigin, StringComparison.Ordinal);
        if (origin.Length > 0 && !fromExtension)
        {
            return null;
        }

        var header = request.Headers["X-Api-Key"].ToString();
        if (header.Length > 0)
        {
            return new PresentedKey(header, ViaSubprotocol: false);
        }

        if (!fromExtension)
        {
            return null;
        }

        var offered = request.Headers.SecWebSocketProtocol
            .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        var others = offered.Where(p => p != LocalBridgeLimits.ExtensionSubprotocol).ToList();
        return offered.Count == 2 && others.Count == 1
            ? new PresentedKey(others[0], ViaSubprotocol: true)
            : null;
    }

    /// <summary>Resolves a presented key to its bridge server, or null — at every failing step.</summary>
    public static async Task<Caller?> AuthenticateAsync(string? apiKey, IGrainFactory grains)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var hash = BridgeKey.Hash(apiKey);
        var owner = await grains.GetGrain<ILocalBridgeKeyDirectoryGrain>(hash).ResolveAsync();
        if (owner is null)
        {
            return null;
        }

        var config = await grains.GetGrain<IUserMcpConfigGrain>(owner.UserId).GetAsync();
        var server = config.Servers.FirstOrDefault(s => string.Equals(s.Id, owner.ServerId, StringComparison.OrdinalIgnoreCase));
        return server is { Enabled: true, Auth: nameof(McpAuthMode.LocalBridge) }
            && string.Equals(server.BridgeKeyHash, hash, StringComparison.Ordinal)
                ? new Caller(owner.UserId, server.Id, hash)
                : null;
    }

    public static async Task HandleAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var grains = services.GetRequiredService<IGrainFactory>();
        var presented = ResolveKey(http.Request);
        var caller = await AuthenticateAsync(presented?.Key, grains);
        if (caller is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            http.Response.ContentType = "text/plain";
            await http.Response.WriteAsync(RejectionBody);
            return;
        }

        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            http.Response.ContentType = "text/plain";
            await http.Response.WriteAsync("This endpoint only accepts a WebSocket upgrade.");
            return;
        }

        var registry = services.GetRequiredService<LocalBridgeRegistry>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger(typeof(LocalBridgeEndpoint));

        using var socket = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = LocalBridgeLimits.KeepAliveInterval,
            KeepAliveTimeout = LocalBridgeLimits.KeepAliveTimeout,
            // A browser fails the handshake unless one offered subprotocol is echoed. Echo the NAME, never the key.
            SubProtocol = presented!.ViaSubprotocol ? LocalBridgeLimits.ExtensionSubprotocol : null,
        });
        var session = new LocalBridgeSession(socket, loggerFactory);

        // A second helper for the same server replaces the first; the displaced helper stops on this reason.
        if (registry.Register(caller.UserId, caller.ServerId, caller.KeyHash, session) is { } replaced)
        {
            await replaced.CloseAsync(LocalBridgeLimits.ReplacedCloseReason);
        }

        // A revoking write whose eviction ran between the check above and Register found nothing to close.
        // Re-checking after registering closes that window: from here on, eviction can see this socket.
        if (await AuthenticateAsync(presented.Key, grains) is null)
        {
            registry.Unregister(caller.UserId, caller.ServerId, session);
            await session.CloseAsync("revoked while connecting");
            return;
        }

        await InvalidateToolCacheAsync(registry, grains, caller, logger);
        logger.LogInformation("Local bridge connected for MCP server {Server} (user {User}).", caller.ServerId, caller.UserId);
        try
        {
            await session.RunAsync(http.RequestAborted);
        }
        finally
        {
            // Never on disconnect: a flapping helper would otherwise bump the overlay on every drop.
            registry.Unregister(caller.UserId, caller.ServerId, session);
            logger.LogInformation("Local bridge disconnected for MCP server {Server} (user {User}).", caller.ServerId, caller.UserId);
        }
    }

    // AgentGrain resolves its MCP tools once, on activation, and the provider caches them by overlay version —
    // so a bridge that connects AFTER that resolution would stay invisible to new activations until the next
    // config write. Invalidating on connect fixes that (ADR 0069's mechanism), rate-limited per server.
    // Best-effort: the socket works either way.
    private static async Task InvalidateToolCacheAsync(
        LocalBridgeRegistry registry, IGrainFactory grains, Caller caller, ILogger logger)
    {
        if (!registry.TryClaimInvalidation(caller.UserId, caller.ServerId))
        {
            return;
        }

        try
        {
            await grains.GetGrain<IUserMcpConfigGrain>(caller.UserId).InvalidateToolCacheAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Invalidating the MCP tool cache on bridge connect failed (user {User}).", caller.UserId);
        }
    }
}
