using Microsoft.Identity.Client;

namespace Agent42.Backend;

/// <summary>
/// Supplies the value for a single auth header on each outgoing MCP request (config-driven remote MCP
/// servers, Phase 2). A provider abstraction so a static value and a dynamically-refreshed token share one
/// <see cref="AuthHeaderHandler"/>.
/// </summary>
public interface IAuthHeaderProvider
{
    /// <summary>The header to stamp (e.g. <c>Authorization</c>).</summary>
    string HeaderName { get; }

    /// <summary>The header value for this request (a token provider may refresh here).</summary>
    ValueTask<string> GetHeaderValueAsync(CancellationToken cancellationToken);
}

/// <summary>
/// OAuth2 <c>client_credentials</c> via MSAL — the non-interactive service-principal path (Entra ID →
/// Dataverse). MSAL caches and silently refreshes the token, so calling <c>AcquireTokenForClient</c> per
/// request is cheap (it returns the cached token until near expiry).
/// </summary>
public sealed class ClientCredentialsTokenProvider : IAuthHeaderProvider
{
    private readonly IConfidentialClientApplication _app;
    private readonly string[] _scopes;

    public string HeaderName => "Authorization";

    public ClientCredentialsTokenProvider(string authority, string clientId, string clientSecret, string[] scopes)
    {
        _app = ConfidentialClientApplicationBuilder.Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority(authority)
            .Build();
        _scopes = scopes;
    }

    public async ValueTask<string> GetHeaderValueAsync(CancellationToken cancellationToken)
    {
        var result = await _app.AcquireTokenForClient(_scopes).ExecuteAsync(cancellationToken);
        return $"Bearer {result.AccessToken}";
    }
}

/// <summary>
/// An <see cref="IAuthHeaderProvider"/> that can renew its value after the server rejected it — the opt-in to
/// <see cref="AuthHeaderHandler"/>'s one retry on a 401 (ADR 0081). ClientCredentials does not opt in: MSAL owns
/// its cache, and a 401 there is a configuration fault a retry cannot fix.
/// </summary>
public interface IRenewableAuthHeaderProvider : IAuthHeaderProvider
{
    /// <summary>
    /// Called after a request stamped with <paramref name="rejectedValue"/> got a 401. Returns a DIFFERENT value
    /// to resend with, or <c>null</c> when there is nothing better (the 401 then stands).
    /// </summary>
    ValueTask<string?> RenewAfterUnauthorizedAsync(string rejectedValue, CancellationToken cancellationToken);
}

/// <summary>
/// The InteractiveOAuth bearer, resolved <b>per request</b> (ADR 0081, amending ADR 0025's connect-time static
/// bearer). The caller — <see cref="UserMcpToolProvider"/> or <see cref="McpConnectionTester"/>, which already
/// hold the resolver — builds it, so <see cref="McpConnectionFactory"/> stays grain-free.
///
/// <para>It caches the last resolution in memory and returns it while it is <see cref="McpOAuthState.Connected"/>
/// and outside the refresh skew; otherwise it asks <see cref="McpOAuthTokenResolver"/>, which owns the refresh,
/// the single-flight gate and the persistence. While the server is <see cref="McpOAuthState.ReauthRequired"/> it
/// asks on <b>every</b> request (a grain read, no authorization-server call), so a fresh consent written by the
/// OAuth callback is picked up by a <i>live</i> connection; meanwhile it sends the last known token and lets the
/// server answer 401.</para>
/// </summary>
public sealed class InteractiveOAuthTokenProvider(
    string userId, string serverId, McpOAuthTokenResolver resolver, McpOAuthResolution? initial = null)
    : IRenewableAuthHeaderProvider
{
    // Ask the resolver again this long before expiry. Deliberately no wider than the resolver's own refresh skew,
    // so asking early never finds a token the resolver would decline to refresh.
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(1);

    // One immutable snapshot, swapped atomically: the transport may send concurrently (a GET stream + POSTs).
    private McpOAuthResolution? _current = initial;

    public string HeaderName => "Authorization";

    /// <summary>The state of the most recent resolution (null before the first one).</summary>
    public McpOAuthState? State => Volatile.Read(ref _current)?.State;

    public async ValueTask<string> GetHeaderValueAsync(CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref _current);
        if (current is not { State: McpOAuthState.Connected } || IsNearExpiry(current))
        {
            current = await resolver.ResolveAsync(userId, serverId, cancellationToken: cancellationToken);
            Volatile.Write(ref _current, current);
        }

        return Bearer(current.AccessToken);
    }

    public async ValueTask<string?> RenewAfterUnauthorizedAsync(string rejectedValue, CancellationToken cancellationToken)
    {
        var rejected = rejectedValue.StartsWith("Bearer ", StringComparison.Ordinal) ? rejectedValue[7..] : rejectedValue;

        // The resolver re-reads the stored bundle: a newer token (a re-consent elsewhere, another connection's
        // refresh) is simply returned; the same token is refreshed now, whatever its expiry says.
        var renewed = await resolver.ResolveAsync(userId, serverId, rejectedAccessToken: rejected, cancellationToken: cancellationToken);
        Volatile.Write(ref _current, renewed);
        return renewed.AccessToken is { Length: > 0 } token && !string.Equals(token, rejected, StringComparison.Ordinal)
            ? Bearer(token)
            : null;
    }

    private static bool IsNearExpiry(McpOAuthResolution resolution) =>
        resolution.ExpiresAt is { } expiresAt && expiresAt - DateTimeOffset.UtcNow <= Skew;

    private static string Bearer(string? token) => $"Bearer {token}";
}

/// <summary>
/// Stamps the auth header on every outgoing MCP request from an <see cref="IAuthHeaderProvider"/>. Used only
/// for auth modes whose value is dynamic (ClientCredentials via MSAL, InteractiveOAuth via the resolver); the
/// static Header mode uses the transport's <c>AdditionalHeaders</c> instead and needs no handler.
///
/// <para><b>401 → renew → resend once</b>, for an <see cref="IRenewableAuthHeaderProvider"/> only (ADR 0081). The
/// same <see cref="HttpRequestMessage"/> is resent: the SDK's Streamable HTTP transport sends
/// <c>JsonContent</c>, which re-serializes (verified against <c>ModelContextProtocol</c> 2.2.0 on 2026-09-21).
/// Exactly one resend — a server that keeps answering 401 gets its 401 back, never a loop.</para>
/// </summary>
public sealed class AuthHeaderHandler(IAuthHeaderProvider provider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var value = await provider.GetHeaderValueAsync(cancellationToken);
        Stamp(request, value);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized
            || provider is not IRenewableAuthHeaderProvider renewable)
        {
            return response;
        }

        var renewed = await renewable.RenewAfterUnauthorizedAsync(value, cancellationToken);
        if (renewed is null)
        {
            return response;
        }

        response.Dispose();
        Stamp(request, renewed);
        return await base.SendAsync(request, cancellationToken);
    }

    private void Stamp(HttpRequestMessage request, string value)
    {
        request.Headers.Remove(provider.HeaderName);
        request.Headers.TryAddWithoutValidation(provider.HeaderName, value);
    }
}
