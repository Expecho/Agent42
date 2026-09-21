using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent42.Backend;

/// <summary>
/// PKCE (RFC 7636) helpers for the interactive-OAuth MCP auth flow (config-driven remote MCP servers,
/// Phase 3). Driven by the app itself — the SDK's <c>ClientOAuthOptions</c> assumes a BLOCKING redirect
/// delegate that cannot bridge a stateless two-request web flow (Step 0 spike), so this app runs the
/// authorization-code + PKCE dance directly across <c>/api/mcp/oauth/start</c> and <c>/callback</c>.
/// </summary>
public static class McpPkce
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

/// <summary>The authorization-server endpoints resolved by discovery for an MCP server.</summary>
public sealed record McpOAuthServerConfig(
    Uri AuthorizationEndpoint, Uri TokenEndpoint, Uri? RegistrationEndpoint, IReadOnlyList<string> ScopesSupported);

/// <summary>
/// The OAuth runtime state persisted (encrypted) for a connected server — everything the connection and a
/// later refresh need. Stored as JSON in <c>StoredMcpServer.TokenCiphertext</c> (no new grain field): the
/// refresh token is a secret, so the whole blob is encrypted; the non-secret endpoint/clientId ride along.
/// </summary>
public sealed record McpOAuthTokens(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes,
    // Set when the authorization server answered a refresh with invalid_grant (ADR 0081): a human must sign in
    // again. Trailing and optional, so an older bundle deserializes it as null; a fresh consent writes a new
    // bundle without it, which clears the mark for free.
    [property: JsonPropertyName("reauth_required_at")] DateTimeOffset? ReauthRequiredAt = null)
{
    /// <summary>
    /// Whether a human must sign in again (ADR 0081): the grant was rejected (<see cref="ReauthRequiredAt"/>), or
    /// the access token has expired with no refresh token to renew it. The ONE rule — the resolver, the provider
    /// skip, the Test reason and the settings row's badge all read it, so they cannot disagree.
    /// </summary>
    public bool RequiresReauth(DateTimeOffset now) =>
        ReauthRequiredAt is not null
        || (string.IsNullOrEmpty(RefreshToken) && ExpiresAt is { } expiresAt && expiresAt <= now);

    public static McpOAuthTokens? Deserialize(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<McpOAuthTokens>(json);

    public string Serialize() => JsonSerializer.Serialize(this);
}

/// <summary>
/// The token endpoint answered with <c>invalid_grant</c> (RFC 6749 §5.2): the refresh token is revoked, expired
/// or otherwise dead, so a human must sign in again (ADR 0081). Carries the status only, never the response body.
/// </summary>
public sealed class McpOAuthRefreshRejectedException(int statusCode)
    : Exception($"Token endpoint rejected the grant (HTTP {statusCode}, invalid_grant).")
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Drives the OAuth HTTP exchanges — PRM/authorization-server discovery, code→token exchange, and refresh —
/// over an injected <see cref="HttpClient"/> so the whole flow is testable against a fake authorization
/// server (the live interactive dance is deploy-smoke).
///
/// <para><b>Outbound-target note (not a request-driven SSRF sink, but response-controlled):</b> the starting
/// point is the user's OWN configured MCP endpoint (never a model or request body). The authorization-server,
/// token, and registration URLs are then DISCOVERED from that server's HTTP responses, so a compromised MCP
/// server could steer these POSTs at another host. This is accepted under the same "the user configured their
/// own server" rationale as a user provider endpoint (ADR 0018) — a trusted, self-directed action in a
/// single-tenant app — and the caller never reflects an upstream error body back to the browser. Revisit if
/// the tenant model ever broadens.</para>
/// </summary>
public sealed class McpOAuthClient(HttpClient http)
{
    // PRM (RFC 9728): the MCP server advertises its authorization server(s). We fetch the well-known
    // document at the endpoint's origin; a server MAY also point at it via a 401 WWW-Authenticate
    // resource_metadata hint, but the origin well-known is the common, sufficient case for a first cut.
    public async Task<McpOAuthServerConfig> DiscoverAsync(Uri mcpEndpoint, CancellationToken cancellationToken)
    {
        var origin = new Uri(mcpEndpoint.GetLeftPart(UriPartial.Authority));
        var prmUrl = new Uri(origin, "/.well-known/oauth-protected-resource");
        using var prmDoc = await GetJsonAsync(prmUrl, cancellationToken)
            ?? throw new InvalidOperationException($"No protected-resource metadata at {prmUrl}.");

        var authServer = prmDoc.RootElement.TryGetProperty("authorization_servers", out var servers)
            && servers.ValueKind == JsonValueKind.Array && servers.GetArrayLength() > 0
                ? servers[0].GetString()
                : null;
        if (string.IsNullOrEmpty(authServer))
        {
            throw new InvalidOperationException("Protected-resource metadata declares no authorization_servers.");
        }

        var scopesSupported = ReadStringArray(prmDoc.RootElement, "scopes_supported");
        var asMeta = await DiscoverAuthorizationServerAsync(new Uri(authServer), cancellationToken);

        return asMeta with { ScopesSupported = asMeta.ScopesSupported.Count > 0 ? asMeta.ScopesSupported : scopesSupported };
    }

    // Authorization-server metadata (RFC 8414 / OIDC). An issuer may carry a path (e.g. Entra's
    // login.microsoftonline.com/{tenant}), so try the RFC 8414 path-insertion form and the append form,
    // each for oauth-authorization-server then openid-configuration, until one yields the endpoints.
    private async Task<McpOAuthServerConfig> DiscoverAuthorizationServerAsync(Uri issuer, CancellationToken cancellationToken)
    {
        foreach (var url in MetadataCandidates(issuer))
        {
            using var doc = await GetJsonAsync(url, cancellationToken);
            if (doc is null) continue;

            var authz = doc.RootElement.TryGetProperty("authorization_endpoint", out var a) ? a.GetString() : null;
            var token = doc.RootElement.TryGetProperty("token_endpoint", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(authz) || string.IsNullOrEmpty(token)) continue;

            var registration = doc.RootElement.TryGetProperty("registration_endpoint", out var r) ? r.GetString() : null;
            return new McpOAuthServerConfig(
                new Uri(authz), new Uri(token),
                string.IsNullOrEmpty(registration) ? null : new Uri(registration),
                ReadStringArray(doc.RootElement, "scopes_supported"));
        }

        throw new InvalidOperationException($"Could not resolve authorization-server metadata for {issuer}.");
    }

    private static IEnumerable<Uri> MetadataCandidates(Uri issuer)
    {
        var origin = issuer.GetLeftPart(UriPartial.Authority);
        var path = issuer.AbsolutePath.TrimEnd('/'); // "" or "/tenant"
        // RFC 8414 path-insertion: {origin}/.well-known/<doc>{path}
        yield return new Uri($"{origin}/.well-known/oauth-authorization-server{path}");
        yield return new Uri($"{origin}/.well-known/openid-configuration{path}");
        // Append form: {issuer}/.well-known/<doc>
        var baseUri = issuer.AbsoluteUri.TrimEnd('/');
        yield return new Uri($"{baseUri}/.well-known/oauth-authorization-server");
        yield return new Uri($"{baseUri}/.well-known/openid-configuration");
    }

    /// <summary>Dynamic Client Registration (RFC 7591) — used only when the user provided no client id.</summary>
    public async Task<string> RegisterClientAsync(Uri registrationEndpoint, Uri redirectUri, CancellationToken cancellationToken)
    {
        var body = new
        {
            client_name = "Agent 42",
            redirect_uris = new[] { redirectUri.AbsoluteUri },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none", // public client (PKCE)
        };
        using var response = await http.PostAsJsonAsync(registrationEndpoint, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return doc.RootElement.GetProperty("client_id").GetString()
            ?? throw new InvalidOperationException("Dynamic client registration returned no client_id.");
    }

    /// <summary>Exchanges an authorization code for tokens (authorization_code grant + PKCE verifier).</summary>
    public Task<McpOAuthTokens> ExchangeCodeAsync(
        Uri tokenEndpoint, string clientId, string code, string codeVerifier, Uri redirectUri,
        Uri resource, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["client_id"] = clientId,
            ["code_verifier"] = codeVerifier,
            ["resource"] = resource.AbsoluteUri, // RFC 8707 resource indicator (the MCP server)
        };
        return PostTokenAsync(tokenEndpoint, clientId, form, scopes, cancellationToken);
    }

    /// <summary>Refreshes an access token (refresh_token grant). Persists nothing — the caller stores it.</summary>
    public Task<McpOAuthTokens> RefreshAsync(McpOAuthTokens tokens, Uri resource, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken ?? throw new InvalidOperationException("No refresh token."),
            ["client_id"] = tokens.ClientId,
            ["resource"] = resource.AbsoluteUri,
        };
        return PostTokenAsync(new Uri(tokens.TokenEndpoint), tokens.ClientId, form, tokens.Scopes, cancellationToken);
    }

    private async Task<McpOAuthTokens> PostTokenAsync(
        Uri tokenEndpoint, string clientId, Dictionary<string, string> form, IReadOnlyList<string> scopes,
        CancellationToken cancellationToken)
    {
        if (scopes.Count > 0)
        {
            form["scope"] = string.Join(' ', scopes);
        }

        using var response = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // invalid_grant is the ONE failure that means a human must sign in again (RFC 6749 §5.2, ADR 0081) —
            // typed so the resolver can tell it from a 5xx/timeout/DNS blip, which must never mark the server.
            if ((int)response.StatusCode is >= 400 and < 500 && IsInvalidGrant(payload))
            {
                throw new McpOAuthRefreshRejectedException((int)response.StatusCode);
            }

            throw new InvalidOperationException($"Token endpoint returned {(int)response.StatusCode}: {payload}");
        }

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Token response had no access_token.");
        var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : (form.GetValueOrDefault("refresh_token"));
        DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var seconds)
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : null;

        return new McpOAuthTokens(access, refresh, expiresAt, tokenEndpoint.AbsoluteUri, clientId, scopes);
    }

    private static bool IsInvalidGrant(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() == "invalid_grant";
        }
        catch (JsonException)
        {
            return false; // a non-JSON error body is not a classified rejection
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? [.. arr.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrEmpty(s)).Cast<string>()]
            : [];
}
