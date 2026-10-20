using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// "Sign in with GitHub" for the GitHub Copilot provider (ADR 0108): the app-driven OAuth web flow of ADR 0025's shape.
/// BOTH endpoints stay BEHIND Easy Auth (never in <c>excludedPaths</c>), so <c>GetUserId()</c> binds the token to the
/// signed-in user. The callback is the only writer of the provider, and it never reflects GitHub's response body into
/// the redirect. The OAuth client is resolved optionally (the <c>/api/speech</c> pattern, ADR 0105): a host without it
/// answers 503 and maps every other route normally.
/// </summary>
public static partial class AgentApi
{
    public const string GitHubCallbackPath = "/api/providers/github/callback";

    private static void MapGitHubCopilotEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/providers/github/start", StartGitHubSignInAsync);
        app.MapGet(GitHubCallbackPath, CompleteGitHubSignInAsync);
    }

    private static async Task<IResult> StartGitHubSignInAsync(IServiceProvider services, IGrainFactory grains, HttpContext http)
    {
        if (services.GetService<GitHubOAuthClient>() is not { Options.Configured: true } github)
        {
            return Results.Text("Sign in with GitHub isn't configured on this server.", "text/plain",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var redirectUri = new Uri($"{http.Request.Scheme}://{http.Request.Host}{GitHubCallbackPath}");
        var state = McpPkce.NewState();
        var verifier = McpPkce.NewVerifier();
        await grains.GetGrain<IUserGitHubOAuthPendingGrain>(http.GetUserId())
            .PutAsync(new GitHubOAuthPending(state, verifier, redirectUri.AbsoluteUri));
        return Results.Redirect(github.AuthorizeUrl(redirectUri, state, verifier));
    }

    // Validates the state (one-time), exchanges the code, and stores the token ENCRYPTED as the user's GitHub Copilot
    // provider. Failure redirects carry only a curated reason.
    private static async Task<IResult> CompleteGitHubSignInAsync(
        IServiceProvider services, IProviderSecretProtector protector, IGrainFactory grains, ILoggerFactory loggers,
        HttpContext http)
    {
        if (services.GetService<GitHubOAuthClient>() is not { Options.Configured: true } github)
        {
            return Results.Redirect("/?github=error&reason=not_configured");
        }

        var query = http.Request.Query;
        if (query.ContainsKey("error"))
        {
            // GitHub's own error code (access_denied when the user cancels) is a closed set, but it is still not echoed.
            return Results.Redirect("/?github=error&reason=denied");
        }

        var code = query["code"].ToString();
        var state = query["state"].ToString();
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return Results.Redirect("/?github=error&reason=missing_code_or_state");
        }

        var userId = http.GetUserId();
        if (await grains.GetGrain<IUserGitHubOAuthPendingGrain>(userId).TakeAsync(state) is not { } pending)
        {
            return Results.Redirect("/?github=error&reason=unknown_state");
        }

        try
        {
            var token = await github.ExchangeCodeAsync(code, pending.CodeVerifier, new Uri(pending.RedirectUri), http.RequestAborted);
            await grains.GetGrain<IUserProviderConfigGrain>(userId).ConnectGitHubCopilotAsync(protector.Protect(token));
            return Results.Redirect("/?github=connected");
        }
        catch (Exception ex)
        {
            loggers.CreateLogger("Agent42.GitHubCopilot.OAuth").LogWarning(ex, "GitHub sign-in code exchange failed for user {User}.", userId);
            return Results.Redirect("/?github=error&reason=token_exchange_failed");
        }
    }
}
