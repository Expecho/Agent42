using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Agent42.Backend;

/// <summary>
/// The operator's GitHub OAuth App (ADR 0108), the app identity "Sign in with GitHub" runs under. It is not an operator
/// model (ADR 0019): it grants no model access by itself, and every turn runs on the signed-in user's own Copilot plan.
/// Both values blank (DevHost, a deployment without the secret) → the sign-in is unavailable and the app still boots.
/// </summary>
public sealed class GitHubOAuthOptions
{
    /// <summary>The OAuth App's client id (public, committed in appsettings).</summary>
    public string ClientId { get; init; } = "";

    /// <summary>The OAuth App's client secret, from the <c>GH_OAUTH_CLIENT_SECRET</c> deploy secret.</summary>
    public string ClientSecret { get; init; } = "";

    /// <summary>
    /// The OAuth scopes requested, space-separated. Empty on purpose: a Copilot session needs none beyond the user's
    /// identity (Step 0.1 of the plan settles the minimum against GitHub).
    /// </summary>
    public string Scopes { get; init; } = "";

    public bool Configured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>
/// The two GitHub OAuth web-flow calls (ADR 0108): the authorize URL and the code-for-token exchange. GitHub OAuth App
/// tokens don't expire, so there is no refresh (contrast ADR 0025/0081). The endpoints are GitHub's own, fixed here,
/// never from a request. The <see cref="HttpClient"/> is a plain one, not from <c>IHttpClientFactory</c>, so it does
/// not inherit <c>AddServiceDefaults</c>' retry pipeline (CLAUDE.md rule 8): a code is single-use, a retry can't help.
/// </summary>
public sealed class GitHubOAuthClient(HttpClient http, GitHubOAuthOptions options)
{
    public const string AuthorizeEndpoint = "https://github.com/login/oauth/authorize";

    public const string TokenEndpoint = "https://github.com/login/oauth/access_token";

    public GitHubOAuthOptions Options { get; } = options;

    /// <summary>The URL the browser is sent to. PKCE (S256) on top of the state.</summary>
    public string AuthorizeUrl(Uri redirectUri, string state, string codeVerifier)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = Options.ClientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["state"] = state,
            ["code_challenge"] = McpPkce.Challenge(codeVerifier),
            ["code_challenge_method"] = "S256",
            ["allow_signup"] = "false",
        };
        if (!string.IsNullOrWhiteSpace(Options.Scopes))
        {
            parameters["scope"] = Options.Scopes;
        }

        return QueryHelpers.AddQueryString(AuthorizeEndpoint, parameters);
    }

    /// <summary>
    /// Exchanges the code for the user's access token. GitHub answers an error with HTTP 200 and an <c>error</c> member,
    /// so both shapes throw here; the caller never reflects the body to the browser.
    /// </summary>
    public async Task<string> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = Options.ClientId,
                ["client_secret"] = Options.ClientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri.AbsoluteUri,
                ["code_verifier"] = codeVerifier,
            }),
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("access_token", out var token) && token.GetString() is { Length: > 0 } value)
        {
            return value;
        }

        var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "no access_token";
        throw new InvalidOperationException($"GitHub refused the code exchange: {error}.");
    }
}
