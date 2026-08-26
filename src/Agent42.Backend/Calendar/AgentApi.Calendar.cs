using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The per-user Google connection (ADR 0053): status, the OAuth start/callback pair, and disconnect.
///
/// <para><b>All four routes stay BEHIND Easy Auth (ADR 0002) — never in <c>excludedPaths</c>.</b> An
/// OAuth callback reachable without authentication would make <c>GetUserId()</c> return the local-dev
/// principal and bind every user's Google tokens to one phantom identity.
/// <c>EasyAuthExclusionTests.Exclusions_DoNotCoverAnyAuthenticatedPath</c> names all three
/// <c>/api/google/*</c> paths so that stays asserted rather than assumed.</para>
///
/// <para><b><see cref="IGoogleSecretProtector"/> and <see cref="GoogleOAuthClient"/> are resolved
/// optionally through <see cref="IServiceProvider"/>, never injected as handler parameters.</b> This is
/// the <c>/api/speech</c> pattern, and the reason is concrete: <c>MapAgentApi</c> infers every
/// endpoint's parameters up front, so a parameter a host does not register is treated as a request body
/// and breaks routing for that <b>whole host</b>. Thirteen files call <c>MapAgentApi</c> — two real
/// hosts and eleven test fixtures — and <c>IMcpSecretProtector</c> broke all of them at once by being a
/// mandatory route-matcher dependency. This feature adds zero new mandatory registrations on the API
/// side.</para>
///
/// <para>A minimal-API handler returning a bare string serves <c>text/plain</c>, not JSON, so every
/// response below is an explicit <c>Results.Json</c>/<c>Results.Text</c>/<c>Results.Redirect</c>.</para>
/// </summary>
public static partial class AgentApi
{
    private static void MapCalendarEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/google/status", GetGoogleStatusAsync);
        app.MapGet("/api/google/oauth/start", StartGoogleOAuthAsync);
        app.MapGet("/api/google/oauth/callback", CompleteGoogleOAuthAsync);
        app.MapDelete("/api/google/connection", DisconnectGoogleAsync);
    }

    // What the Calendar tab renders. Never a token — only whether one exists.
    private static async Task<IResult> GetGoogleStatusAsync(
        IServiceProvider services, IGrainFactory grains, IConfiguration configuration, HttpContext http)
    {
        var connection = await grains.GetGrain<IUserGoogleGrain>(http.GetUserId()).GetAsync();

        return Results.Json(new GoogleStatusResponse(
            Configured: GoogleClientConfigured(configuration) && services.GetService<GoogleOAuthClient>() is not null,
            Connected: connection is not null,
            AccountEmail: connection?.AccountEmail,
            ConnectedAt: connection?.ConnectedAt));
    }

    // Begins the flow: mint PKCE + state, persist them per-user, and 302 the browser to Google. There is
    // no discovery step (Google's endpoints are constants), which is the one part of the MCP flow that
    // does not carry over.
    private static async Task<IResult> StartGoogleOAuthAsync(
        IServiceProvider services, IGrainFactory grains, IConfiguration configuration, HttpContext http)
    {
        var oauth = services.GetService<GoogleOAuthClient>();
        var clientId = configuration["GoogleCalendar:ClientId"];
        if (oauth is null || !GoogleClientConfigured(configuration))
        {
            return Results.Text(
                "Google Calendar is not configured on this deployment.",
                "text/plain", statusCode: StatusCodes.Status409Conflict);
        }

        var verifier = GooglePkce.NewVerifier();
        var state = GooglePkce.NewState();
        var redirectUri = GoogleRedirectUri(http);

        await grains.GetGrain<IUserGoogleOAuthPendingGrain>(http.GetUserId())
            .PutAsync(new GoogleOAuthPending(state, verifier, redirectUri, DateTimeOffset.UtcNow));

        return Results.Redirect(oauth.BuildAuthorizationUrl(
            clientId!, redirectUri, state, GooglePkce.Challenge(verifier)));
    }

    // Completes the flow: validate the one-time state, exchange the code, persist the tokens ENCRYPTED,
    // and redirect back to the app. A replayed callback finds no pending entry.
    private static async Task<IResult> CompleteGoogleOAuthAsync(
        IServiceProvider services, IGrainFactory grains, IConfiguration configuration, HttpContext http)
    {
        var oauth = services.GetService<GoogleOAuthClient>();
        var protector = services.GetService<IGoogleSecretProtector>();
        if (oauth is null || protector is null || !GoogleClientConfigured(configuration))
        {
            return Results.Redirect("/?google_oauth=error&reason=not_configured");
        }

        var query = http.Request.Query;

        // Google's own error (the user pressed Cancel, or the client is misconfigured) is reported by
        // CATEGORY, not by echoing its text — an error_description carries the client id and often the
        // redirect URI, and this string lands in the user's address bar.
        if (query.ContainsKey("error"))
        {
            return Results.Redirect("/?google_oauth=error&reason=denied");
        }

        var code = query["code"].ToString();
        var state = query["state"].ToString();
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return Results.Redirect("/?google_oauth=error&reason=missing_code_or_state");
        }

        var userId = http.GetUserId();
        var pending = await grains.GetGrain<IUserGoogleOAuthPendingGrain>(userId).TakeAsync(state);
        if (pending is null)
        {
            return Results.Redirect("/?google_oauth=error&reason=unknown_state");
        }

        try
        {
            var tokens = await oauth.ExchangeCodeAsync(
                configuration["GoogleCalendar:ClientId"]!, configuration["GoogleCalendar:ClientSecret"]!,
                code, pending.CodeVerifier, pending.RedirectUri, http.RequestAborted);

            // No refresh token means the consent did not carry access_type=offline + prompt=consent, or
            // Google reused a prior grant. Reporting success here would produce a connection that dies
            // silently an hour later, so it is refused now, while the user is still looking.
            if (string.IsNullOrEmpty(tokens.RefreshToken))
            {
                return Results.Redirect("/?google_oauth=error&reason=no_refresh_token");
            }

            // STORE FIRST, then look up the identity, then update. The order is load-bearing and the
            // obvious one-expression version is WRONG: ResolvePrimaryCalendarIdAsync goes out through
            // ICalendarService, and the adapter resolves the caller's token from THIS grain — so
            // evaluating it as an argument to SetAsync runs it while the grain still holds nothing, and
            // every first connect would store AccountEmail: null. The UI then silently falls back to a
            // bare "connected" and the user never sees which account they linked.
            var grain = grains.GetGrain<IUserGoogleGrain>(userId);
            var connection = new GoogleConnection(
                AccessTokenCiphertext: protector.Protect(tokens.AccessToken),
                RefreshTokenCiphertext: protector.Protect(tokens.RefreshToken),
                AccessTokenExpiresAt: tokens.ExpiresAt,
                AccountEmail: null,
                ConnectedAt: DateTimeOffset.UtcNow);

            await grain.SetAsync(connection);

            // Best-effort: a failure here costs the tab a label, never the connection — which is why
            // the connection is already durable by this point.
            var accountEmail = await ResolvePrimaryCalendarIdAsync(services, userId, http.RequestAborted);
            if (accountEmail is not null)
            {
                await grain.SetAsync(connection with { AccountEmail = accountEmail });
            }

            return Results.Redirect("/?google_oauth=connected");
        }
        catch (Exception)
        {
            // The exception is discarded, not logged with its message: a token-error path can carry the
            // client id and the redirect URI. The user gets a category they can act on.
            return Results.Redirect("/?google_oauth=error&reason=exchange_failed");
        }
    }

    // Removes the stored tokens. Idempotent, and deliberately does NOT revoke the grant at Google's end
    // — the user does that at myaccount.google.com/permissions, which the UI says.
    private static async Task<IResult> DisconnectGoogleAsync(IGrainFactory grains, HttpContext http)
    {
        await grains.GetGrain<IUserGoogleGrain>(http.GetUserId()).ClearAsync();
        return Results.NoContent();
    }

    // The connected identity is the PRIMARY CALENDAR'S ID, which for a Google account is the account
    // address — which is why no openid/email scope is requested. Best-effort: a failure here costs the
    // tab a label, never the connection.
    private static async Task<string?> ResolvePrimaryCalendarIdAsync(
        IServiceProvider services, string userId, CancellationToken cancellationToken)
    {
        var calendar = services.GetService<ICalendarService>();
        if (calendar is null)
        {
            return null;
        }

        var result = await calendar.ListCalendarsAsync(userId, cancellationToken);
        return result.Value?.FirstOrDefault(c => c.IsPrimary)?.Id;
    }

    // The redirect URI must match what is registered in Google Cloud EXACTLY, and it must be https:
    // behind the ACA ingress Request.Scheme is http unless UseForwardedHeaders runs first (ADR 0042),
    // which would mint an http:// URI and earn a redirect_uri_mismatch on every connect.
    private static string GoogleRedirectUri(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}/api/google/oauth/callback";

    private static bool GoogleClientConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["GoogleCalendar:ClientId"])
        && !string.IsNullOrWhiteSpace(configuration["GoogleCalendar:ClientSecret"]);
}
