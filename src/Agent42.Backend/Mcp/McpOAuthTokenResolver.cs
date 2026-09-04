using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Resolves the current InteractiveOAuth access token for one stored MCP server: returns the stored access
/// token, refreshing it (app-owned) when it is within <see cref="RefreshSkew"/> of expiry and a refresh token
/// is available, and persisting the refreshed bundle.
///
/// <para>Extracted from <see cref="UserMcpToolProvider"/> so the connection <b>test</b> path
/// (<see cref="McpConnectionTester"/>) shares the identical resolution. A test path that skipped the refresh
/// would report failure on a server the chat path connects to fine — a false negative in a diagnostic tool.
/// There must be exactly one implementation of this rule.</para>
/// </summary>
public sealed class McpOAuthTokenResolver
{
    // Refresh an InteractiveOAuth access token at connect if it expires within this window (app-owned refresh).
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    private readonly IGrainFactory _grains;
    private readonly IMcpSecretProtector _protector;
    private readonly McpOAuthClient _oauth;
    private readonly ILogger<McpOAuthTokenResolver> _logger;

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
    /// Returns the access token to connect with, or <c>null</c> when the server has no stored token (the
    /// Connect flow hasn't run) → the caller reports/skips it as "not connected".
    /// </summary>
    public async Task<string?> ResolveAsync(string userId, StoredMcpServer server, CancellationToken cancellationToken)
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
}
