using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agent42.Backend;

/// <summary>
/// PKCE (RFC 7636) helpers for the Google authorization-code flow (ADR 0053).
///
/// <para>A deliberate <b>copy</b> of <see cref="McpPkce"/> rather than a reuse: coupling
/// <c>Calendar/</c> to an <c>Mcp</c>-named type to save thirty lines would make the MCP flow's
/// evolution a constraint on this one, for no benefit. The repo already prefers the copy where the
/// two lifecycles are independent — ADR 0035's <c>ShareToken</c> is a copy of <c>ApiKey</c> for the
/// same reason.</para>
///
/// <para>Google is a <b>confidential</b> client (a Web application OAuth client, which requires a
/// <c>client_secret</c> on the token exchange), so PKCE here is defence in depth rather than the
/// primary protection it is for the public MCP client.</para>
/// </summary>
public static class GooglePkce
{
    /// <summary>A high-entropy code verifier (RFC 7636 §4.1) — 32 random bytes, base64url, no padding.</summary>
    public static string NewVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    /// <summary>The S256 challenge for a verifier: base64url(SHA256(ASCII(verifier))).</summary>
    public static string Challenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    /// <summary>An opaque, unguessable value for the OAuth <c>state</c> (CSRF binding).</summary>
    public static string NewState()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// What a token exchange or refresh returned. <see cref="RefreshToken"/> is the whole connection — see
/// <c>GoogleConnection</c> — and Google returns one only on a consent that carried
/// <c>access_type=offline</c> and <c>prompt=consent</c>.
/// </summary>
/// <param name="AccessToken">The bearer token for Calendar API calls.</param>
/// <param name="RefreshToken">The long-lived token, present on the first exchange and absent from a refresh response.</param>
/// <param name="ExpiresAt">When <paramref name="AccessToken"/> stops working.</param>
public sealed record GoogleOAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Drives the Google OAuth HTTP exchanges (ADR 0053): the authorization URL, the code→token exchange
/// and the refresh, over an injected <see cref="HttpClient"/>.
///
/// <para><b>No discovery.</b> Unlike <see cref="McpOAuthClient"/>, Google's endpoints are fixed and
/// documented, so there is no PRM/RFC-8414 step — which also means there is no response-controlled
/// outbound target here.</para>
///
/// <para><b>The endpoints are constructor values with the Google URLs as defaults</b>, the
/// <c>GoogleGeocoder.DefaultBaseAddress</c> shape. That is what keeps the flow testable against a fake
/// authorization server: hard-coding them would leave the exchange and refresh paths either untested or
/// dependent on the live internet from CI. Production passes nothing and gets Google.</para>
/// </summary>
public sealed class GoogleOAuthClient
{
    /// <summary>Google's authorization endpoint (the page the user's browser is sent to).</summary>
    public const string DefaultAuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";

    /// <summary>Google's token endpoint (code exchange and refresh).</summary>
    public const string DefaultTokenEndpoint = "https://oauth2.googleapis.com/token";

    /// <summary>
    /// Read and write events on <b>all</b> the user's calendars. Deliberately narrower than
    /// <c>.../auth/calendar</c>, which would additionally permit creating and deleting whole calendars
    /// and changing ACL/sharing — three things this feature puts out of scope. Given that writes carry
    /// no confirmation gate and prompt injection is an acknowledged channel (ADR 0011/0027/0049), the
    /// difference is the difference between an injected "delete that meeting" and an injected "delete
    /// that calendar". Do not widen it without recording why.
    /// </summary>
    public const string EventsScope = "https://www.googleapis.com/auth/calendar.events";

    /// <summary>Enumerate the user's calendars. Read-only, and the other half of the narrow pair.</summary>
    public const string CalendarListScope = "https://www.googleapis.com/auth/calendar.calendarlist.readonly";

    /// <summary>The exact scope pair requested. No <c>openid</c>/<c>email</c>: the connected identity is
    /// read from the primary calendar's id, which for a Google account is the account address.</summary>
    public static readonly IReadOnlyList<string> Scopes = [EventsScope, CalendarListScope];

    private readonly HttpClient _http;
    private readonly string _authorizationEndpoint;
    private readonly string _tokenEndpoint;

    public GoogleOAuthClient(
        HttpClient http,
        string? authorizationEndpoint = null,
        string? tokenEndpoint = null)
    {
        _http = http;
        _authorizationEndpoint = string.IsNullOrWhiteSpace(authorizationEndpoint)
            ? DefaultAuthorizationEndpoint
            : authorizationEndpoint;
        _tokenEndpoint = string.IsNullOrWhiteSpace(tokenEndpoint) ? DefaultTokenEndpoint : tokenEndpoint;
    }

    /// <summary>
    /// Builds the URL the user's browser is sent to.
    ///
    /// <para><b><c>access_type=offline</c> and <c>prompt=consent</c> are both mandatory.</b> Without
    /// them Google returns no refresh token on a re-consent, and the connection dies at the first
    /// access-token expiry — silently, an hour later, not at connect time. That failure is invisible in
    /// testing and obvious only in production, which is why
    /// <c>AuthorizationUrl_RequestsOfflineAccessAndConsent</c> pins both.</para>
    /// </summary>
    public string BuildAuthorizationUrl(string clientId, string redirectUri, string state, string codeChallenge)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', Scopes),
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "true",
        };

        var encoded = string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{_authorizationEndpoint}?{encoded}";
    }

    /// <summary>Exchanges an authorization code for tokens (authorization_code grant + PKCE verifier).</summary>
    public Task<GoogleOAuthTokens> ExchangeCodeAsync(
        string clientId, string clientSecret, string code, string codeVerifier, string redirectUri,
        CancellationToken cancellationToken) =>
        PostTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = clientId,
                // Google's Web application client type is CONFIDENTIAL: PKCE alone is not accepted,
                // the secret is required on the exchange. This is the one place the MCP flow's
                // public-client shape does not carry over.
                ["client_secret"] = clientSecret,
                ["code_verifier"] = codeVerifier,
            },
            fallbackRefreshToken: null,
            cancellationToken);

    /// <summary>
    /// Refreshes an access token. Persists nothing — the caller stores the new bundle.
    ///
    /// <b>A refresh response carries no <c>refresh_token</c></b>, so the caller's existing one is
    /// carried forward rather than being overwritten with null.
    /// </summary>
    public Task<GoogleOAuthTokens> RefreshAsync(
        string clientId, string clientSecret, string refreshToken, CancellationToken cancellationToken) =>
        PostTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            },
            fallbackRefreshToken: refreshToken,
            cancellationToken);

    private async Task<GoogleOAuthTokens> PostTokenAsync(
        Dictionary<string, string> form, string? fallbackRefreshToken, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(
            _tokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // The status code alone. A Google token-error body echoes the client id and often the
            // redirect URI, and this exception's message reaches a log — the caller turns it into a
            // curated redirect reason, never a passthrough.
            throw new InvalidOperationException(
                $"Google token endpoint returned {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var access = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrEmpty(access))
        {
            throw new InvalidOperationException("Google token response carried no access_token.");
        }

        var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var seconds)
            ? seconds
            : 3600;

        return new GoogleOAuthTokens(
            access,
            string.IsNullOrEmpty(refresh) ? fallbackRefreshToken : refresh,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }
}
