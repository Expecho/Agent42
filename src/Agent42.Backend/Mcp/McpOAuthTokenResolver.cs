using System.Collections.Concurrent;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>Where an InteractiveOAuth server stands for one user (ADR 0081).</summary>
public enum McpOAuthState
{
    /// <summary>No stored token — the Connect flow has not run.</summary>
    NotConnected,

    /// <summary>A usable (or at least not known-dead) access token.</summary>
    Connected,

    /// <summary>The grant is dead (<c>invalid_grant</c>, or expired with no refresh token): a human must sign in again.</summary>
    ReauthRequired,
}

/// <summary>
/// One resolution. <see cref="AccessToken"/> is the last known token even when <see cref="State"/> is
/// <see cref="McpOAuthState.ReauthRequired"/> — the per-request bearer sends it and lets the server answer 401.
/// </summary>
public sealed record McpOAuthResolution(McpOAuthState State, string? AccessToken = null, DateTimeOffset? ExpiresAt = null)
{
    public static McpOAuthResolution NotConnected { get; } = new(McpOAuthState.NotConnected);
}

/// <summary>
/// Resolves the current InteractiveOAuth access token for one stored MCP server: returns the stored access
/// token, refreshing it (app-owned) when it is within <see cref="RefreshSkew"/> of expiry — or a request was just
/// rejected with it — and a refresh token is available, and persisting the refreshed bundle.
///
/// <para>There must be exactly one implementation of this rule: the per-request bearer
/// (<see cref="InteractiveOAuthTokenProvider"/>), the activation path (<see cref="UserMcpToolProvider"/>) and the
/// connection <b>test</b> (<see cref="McpConnectionTester"/>) all come here. A test path that skipped the refresh
/// would report failure on a server the chat path connects to fine — a false negative in a diagnostic tool.</para>
///
/// <para><b>ADR 0081 rules.</b> (1) It always re-reads the <b>current</b> bundle from the config grain — two
/// retained cache versions can hold two live connections per server, and refreshing from a connect-time
/// snapshot would present a rotated-away refresh token and get a false <c>invalid_grant</c>. (2) Refreshes are
/// single-flighted per (user, server) in this process singleton, and a waiter re-reads inside the gate, usually
/// finding the token already fresh. (3) Only <c>invalid_grant</c> marks the bundle; any other refresh failure
/// logs and returns the existing token. (4) Every write is a compare-and-swap against the ciphertext read inside
/// the gate; a lost swap means someone else wrote first, so the current bundle is re-read and used.</para>
/// </summary>
public sealed class McpOAuthTokenResolver
{
    // Refresh an InteractiveOAuth access token if it expires within this window (app-owned refresh).
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    private readonly IGrainFactory _grains;
    private readonly IMcpSecretProtector _protector;
    private readonly McpOAuthClient _oauth;
    private readonly ILogger<McpOAuthTokenResolver> _logger;

    // One gate per (user, server): grows by one small semaphore per OAuth server ever resolved — bounded by config.
    private readonly ConcurrentDictionary<(string UserId, string ServerId), SemaphoreSlim> _gates = new();

    public McpOAuthTokenResolver(
        IGrainFactory grains,
        IMcpSecretProtector protector,
        McpOAuthClient oauth,
        ILogger<McpOAuthTokenResolver> logger)
    {
        _grains = grains;
        _protector = protector;
        _oauth = oauth;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the server's access token. <paramref name="force"/> is the user-pressed Test path: a bundle
    /// already marked <c>ReauthRequired</c> is retried against the authorization server instead of being
    /// reported without contact. <paramref name="rejectedAccessToken"/> is the token a request just got a 401 for:
    /// while the stored token is still that one, it is refreshed regardless of its expiry.
    /// </summary>
    public async Task<McpOAuthResolution> ResolveAsync(
        string userId, string serverId, bool force = false, string? rejectedAccessToken = null,
        CancellationToken cancellationToken = default)
    {
        var (server, tokens) = await ReadAsync(userId, serverId);
        if (server is null || tokens is null)
        {
            return McpOAuthResolution.NotConnected;
        }

        if (!NeedsRefresh(tokens, force, rejectedAccessToken))
        {
            return Current(tokens);
        }

        var gate = _gates.GetOrAdd((userId, serverId), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Re-read inside the gate: whoever held it before us has usually refreshed already.
            (server, tokens) = await ReadAsync(userId, serverId);
            if (server is null || tokens is null)
            {
                return McpOAuthResolution.NotConnected;
            }

            return NeedsRefresh(tokens, force, rejectedAccessToken)
                ? await RefreshAsync(userId, server, tokens, cancellationToken)
                : Current(tokens);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<McpOAuthResolution> RefreshAsync(
        string userId, StoredMcpServer server, McpOAuthTokens tokens, CancellationToken cancellationToken)
    {
        McpOAuthTokens next;
        try
        {
            // A fresh record: ReauthRequiredAt defaults to null, so a successful refresh clears a stale mark.
            next = await _oauth.RefreshAsync(tokens, new Uri(server.Endpoint), cancellationToken);
        }
        catch (McpOAuthRefreshRejectedException ex)
        {
            _logger.LogWarning(
                "MCP OAuth refresh for server {Server} (user {User}) was rejected (HTTP {Status}, invalid_grant); marking it as needing reconnect.",
                server.Name, userId, ex.StatusCode);
            next = tokens with { ReauthRequiredAt = DateTimeOffset.UtcNow };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A transient failure (5xx, timeout, DNS) is never a reason to nag the user: use the existing token.
            _logger.LogWarning(ex,
                "MCP OAuth token refresh failed for server {Server} (user {User}); using the existing token.",
                server.Name, userId);
            return Current(tokens);
        }

        var written = await _grains.GetGrain<IUserMcpConfigGrain>(userId)
            .RecordOAuthTokensAsync(server.Id, server.TokenCiphertext!, _protector.Protect(next.Serialize()));
        if (written)
        {
            return Current(next);
        }

        // Lost the swap: a concurrent writer (typically the OAuth callback's fresh consent) got there first, and
        // theirs is newer than ours — use it, never retry the write.
        var (_, current) = await ReadAsync(userId, server.Id);
        return current is null ? McpOAuthResolution.NotConnected : Current(current);
    }

    private static bool NeedsRefresh(McpOAuthTokens tokens, bool force, string? rejectedAccessToken)
    {
        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            return false;
        }

        if (tokens.ReauthRequiredAt is not null)
        {
            return force; // marked: only a user-pressed Test contacts the authorization server again
        }

        var rejected = rejectedAccessToken is not null
            && string.Equals(tokens.AccessToken, rejectedAccessToken, StringComparison.Ordinal);
        return rejected || IsWithin(tokens, RefreshSkew);
    }

    private static McpOAuthResolution Current(McpOAuthTokens tokens) =>
        new(tokens.RequiresReauth(DateTimeOffset.UtcNow) ? McpOAuthState.ReauthRequired : McpOAuthState.Connected,
            tokens.AccessToken, tokens.ExpiresAt);

    private static bool IsWithin(McpOAuthTokens tokens, TimeSpan window) =>
        tokens.ExpiresAt is { } expiresAt && expiresAt - DateTimeOffset.UtcNow <= window;

    private async Task<(StoredMcpServer? Server, McpOAuthTokens? Tokens)> ReadAsync(string userId, string serverId)
    {
        var config = await _grains.GetGrain<IUserMcpConfigGrain>(userId).GetAsync();
        var server = config.Servers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.Ordinal));
        return server is null
            ? (null, null)
            : (server, McpOAuthTokens.Deserialize(_protector.Unprotect(server.TokenCiphertext)));
    }
}
