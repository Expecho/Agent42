using System.Net;
using System.Text.Json;
using System.Web;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// The Google connection endpoints end to end (ADR 0053): <c>/api/google/oauth/start</c> builds the PKCE
/// authorization redirect and persists pending state; <c>/callback</c> exchanges the code and persists
/// <b>encrypted</b> tokens; <c>/status</c> reports the connection without ever echoing one. Runs against
/// a real Orleans cluster plus a fake authorization server. The live interactive dance is deploy-smoke.
/// </summary>
public sealed class GoogleOAuthEndpointTests : IAsyncLifetime
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";
    private const string User = "google-endpoint-user";

    private TestCluster _cluster = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!; // does NOT auto-follow redirects (we inspect the 302 Location)
    private IGoogleSecretProtector _protector = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();

        _protector = new GoogleSecretProtector(
            new EphemeralDataProtectionProvider(), NullLogger<GoogleSecretProtector>.Instance);

        var appBuilder = WebApplication.CreateSlimBuilder();
        appBuilder.WebHost.UseTestServer();
        appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GoogleCalendar:ClientId"] = "client-1",
            ["GoogleCalendar:ClientSecret"] = "secret-1",
        });

        appBuilder.Services.AddSingleton<IGrainFactory>(_cluster.GrainFactory);
        appBuilder.Services.AddSingleton(_protector);
        appBuilder.Services.AddSingleton(new GoogleOAuthClient(
            new HttpClient(new RecordingHttpHandler
            {
                Responder = _ => RecordingHttpHandler.Json(
                    """{"access_token":"at-live","refresh_token":"rt-live","expires_in":3600}"""),
            }),
            "https://accounts.test/authorize",
            "https://oauth2.test/token"));
        // A CONNECTION-AWARE double, deliberately not the bare InMemoryCalendarService. The callback
        // reads the connected identity back through ICalendarService, and production resolves the
        // caller's token from the very grain the callback is writing — so a double that answers
        // regardless of connection state would keep OAuthCallback_PersistsEncryptedTokens green even
        // if the endpoint asked for the identity BEFORE storing the tokens, which is exactly the bug
        // that ordering once had.
        appBuilder.Services.AddSingleton<ICalendarService>(sp =>
            new ConnectionAwareCalendarService(sp.GetRequiredService<IGrainFactory>()));

        // The registrations every MapAgentApi host needs — a param a host cannot resolve is treated as a
        // body and breaks routing for the WHOLE host, which is exactly the trap this feature avoids
        // adding to (see AgentApi.Calendar.cs).
        appBuilder.Services.AddSingleton<IMcpSecretProtector>(
            new McpSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance));
        // /api/mcp/oauth/start takes McpOAuthClient as a HANDLER parameter, so it is mandatory for every
        // MapAgentApi host — this is the trap from the other side, and the reason the calendar endpoints
        // resolve their own dependencies through IServiceProvider instead.
        appBuilder.Services.AddSingleton(new McpOAuthClient(new HttpClient()));
        appBuilder.Services.AddSingleton<IProviderSecretProtector>(
            new ProviderSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<ProviderSecretProtector>.Instance));
        appBuilder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog("fake-model", []));
        appBuilder.Services.AddSingleton<IUserModelCatalogProvider>(sp =>
            new SingleUserModelCatalogProvider(sp.GetRequiredService<IModelCatalog>()));
        appBuilder.Services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
        appBuilder.Services.AddDocumentRagFakes();
        appBuilder.Services.AddSingleton(new PushConfig(Enabled: false, PublicKey: "", Subject: ""));

        _app = appBuilder.Build();
        _app.MapAgentApi();
        await _app.StartAsync();
        _client = new HttpClient(_app.GetTestServer().CreateHandler()) { BaseAddress = new Uri("http://localhost") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _cluster.DisposeAsync();
    }

    private HttpRequestMessage Request(HttpMethod method, string uri, string principalId)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(IdHeader, principalId);
        return request;
    }

    // Runs /start and returns the state it minted, read back out of the redirect.
    private async Task<string> StartAsync(string user)
    {
        using var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/google/oauth/start", user));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var query = HttpUtility.ParseQueryString(new Uri(response.Headers.Location!.ToString()).Query);
        return query["state"]!;
    }

    [Fact]
    public async Task Start_RedirectsToTheAuthorizationEndpoint_WithPkceAndOfflineAccess()
    {
        using var response = await _client.SendAsync(
            Request(HttpMethod.Get, "/api/google/oauth/start", "start-user"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("https://accounts.test/authorize?", location, StringComparison.Ordinal);

        var query = HttpUtility.ParseQueryString(new Uri(location).Query);
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("consent", query["prompt"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(query["state"]));
        Assert.EndsWith("/api/google/oauth/callback", query["redirect_uri"]!, StringComparison.Ordinal);
    }

    // AC6. The tokens the exchange returned are persisted as CIPHERTEXT, and the connected identity is
    // the primary calendar's id — which is why no openid/email scope is requested.
    [Fact]
    public async Task OAuthCallback_PersistsEncryptedTokens()
    {
        var state = await StartAsync(User);

        using var response = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/google/oauth/callback?code=code-1&state={state}", User));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?google_oauth=connected", response.Headers.Location!.ToString());

        var stored = await _cluster.GrainFactory.GetGrain<IUserGoogleGrain>(User).GetAsync();
        Assert.NotNull(stored);

        // Ciphertext, not the token: the grain must never hold a readable credential.
        Assert.NotEqual("rt-live", stored!.RefreshTokenCiphertext);
        Assert.DoesNotContain("rt-live", stored.RefreshTokenCiphertext, StringComparison.Ordinal);
        Assert.Equal("rt-live", _protector.Unprotect(stored.RefreshTokenCiphertext));
        Assert.Equal("at-live", _protector.Unprotect(stored.AccessTokenCiphertext));
        Assert.Equal(InMemoryCalendarService.PrimaryCalendarAddress, stored.AccountEmail);
    }

    // AC8's endpoint half: the state is one-time, so a replayed callback (a bookmarked URL, a shoulder
    // surfer) cannot re-drive the exchange.
    [Fact]
    public async Task OAuthCallback_ReplayedState_IsRejected()
    {
        const string user = "replay-user";
        var state = await StartAsync(user);

        using var first = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/google/oauth/callback?code=c&state={state}", user));
        Assert.Equal("/?google_oauth=connected", first.Headers.Location!.ToString());

        using var replay = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/google/oauth/callback?code=c&state={state}", user));
        Assert.Equal("/?google_oauth=error&reason=unknown_state", replay.Headers.Location!.ToString());
    }

    // One user's pending state cannot be completed by another — the pending grain is keyed per user.
    [Fact]
    public async Task OAuthCallback_StateFromAnotherUser_IsRejected()
    {
        var state = await StartAsync("alice-user");

        using var response = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/google/oauth/callback?code=c&state={state}", "bob-user"));

        Assert.Equal("/?google_oauth=error&reason=unknown_state", response.Headers.Location!.ToString());
        Assert.Null(await _cluster.GrainFactory.GetGrain<IUserGoogleGrain>("bob-user").GetAsync());
    }

    // Google's own error text carries the client id and often the redirect URI, and this string lands in
    // the user's address bar. Only a category travels.
    [Fact]
    public async Task OAuthCallback_ProviderError_IsReportedByCategory_NotEchoed()
    {
        using var response = await _client.SendAsync(Request(
            HttpMethod.Get,
            "/api/google/oauth/callback?error=access_denied&error_description="
                + "client%20123.apps.googleusercontent.com%20was%20denied",
            "denied-user"));

        var location = response.Headers.Location!.ToString();
        Assert.Equal("/?google_oauth=error&reason=denied", location);
        Assert.DoesNotContain("googleusercontent", location, StringComparison.Ordinal);
        Assert.DoesNotContain("access_denied", location, StringComparison.Ordinal);
    }

    // AC6. The status payload is the one thing a browser sees about the connection, so it is asserted on
    // the RAW JSON: a future field carrying a token would slip past a typed assertion.
    [Fact]
    public async Task Status_NeverEchoesTokens()
    {
        var state = await StartAsync("status-user");
        using var callback = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/google/oauth/callback?code=c&state={state}", "status-user"));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);

        using var response = await _client.SendAsync(
            Request(HttpMethod.Get, "/api/google/status", "status-user"));
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("at-live", json, StringComparison.Ordinal);
        Assert.DoesNotContain("rt-live", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Ciphertext", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("configured").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("connected").GetBoolean());
        Assert.Equal(
            InMemoryCalendarService.PrimaryCalendarAddress,
            doc.RootElement.GetProperty("accountEmail").GetString());
    }

    [Fact]
    public async Task Status_WhenNeverConnected_ReportsConfiguredButNotConnected()
    {
        using var response = await _client.SendAsync(
            Request(HttpMethod.Get, "/api/google/status", "fresh-user"));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("configured").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("connected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("accountEmail").ValueKind);
    }

    [Fact]
    public async Task Disconnect_RemovesTheConnection_AndIsIdempotent()
    {
        const string user = "disconnect-user";
        var state = await StartAsync(user);
        using var callback = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/google/oauth/callback?code=c&state={state}", user));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);

        using var first = await _client.SendAsync(Request(HttpMethod.Delete, "/api/google/connection", user));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Null(await _cluster.GrainFactory.GetGrain<IUserGoogleGrain>(user).GetAsync());

        using var second = await _client.SendAsync(Request(HttpMethod.Delete, "/api/google/connection", user));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    // A connection is per user. Alice connecting must not make Bob's status say "connected".
    [Fact]
    public async Task Status_IsPerUser()
    {
        var state = await StartAsync("iso-alice");
        using var callback = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/google/oauth/callback?code=c&state={state}", "iso-alice"));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);

        using var bob = await _client.SendAsync(Request(HttpMethod.Get, "/api/google/status", "iso-bob"));
        using var doc = JsonDocument.Parse(await bob.Content.ReadAsStringAsync());

        Assert.False(doc.RootElement.GetProperty("connected").GetBoolean());
    }

    /// <summary>
    /// <see cref="InMemoryCalendarService"/>, but only for a user who actually has a stored
    /// <see cref="GoogleConnection"/> — which is what the production adapter does, and what makes the
    /// store-then-resolve ordering in the callback falsifiable.
    /// </summary>
    private sealed class ConnectionAwareCalendarService(IGrainFactory grains) : ICalendarService
    {
        private readonly InMemoryCalendarService _inner = new();

        private async Task<bool> ConnectedAsync(string userId) =>
            await grains.GetGrain<IUserGoogleGrain>(userId).GetAsync() is not null;

        public async Task<CalendarResult<IReadOnlyList<CalendarSummary>>> ListCalendarsAsync(
            string userId, CancellationToken cancellationToken = default) =>
            await ConnectedAsync(userId)
                ? await _inner.ListCalendarsAsync(userId, cancellationToken)
                : CalendarResult<IReadOnlyList<CalendarSummary>>.NotConnected();

        public async Task<CalendarResult<IReadOnlyList<CalendarEvent>>> ListEventsAsync(
            string userId, string? calendarId, DateTimeOffset timeMin, DateTimeOffset timeMax,
            int maxResults, CancellationToken cancellationToken = default) =>
            await ConnectedAsync(userId)
                ? await _inner.ListEventsAsync(userId, calendarId, timeMin, timeMax, maxResults, cancellationToken)
                : CalendarResult<IReadOnlyList<CalendarEvent>>.NotConnected();

        public async Task<CalendarResult<CalendarEvent>> GetEventAsync(
            string userId, string? calendarId, string eventId, CancellationToken cancellationToken = default) =>
            await ConnectedAsync(userId)
                ? await _inner.GetEventAsync(userId, calendarId, eventId, cancellationToken)
                : CalendarResult<CalendarEvent>.NotConnected();

        public async Task<CalendarResult<CalendarEvent>> CreateEventAsync(
            string userId, string? calendarId, CalendarEventDraft draft,
            CancellationToken cancellationToken = default) =>
            await ConnectedAsync(userId)
                ? await _inner.CreateEventAsync(userId, calendarId, draft, cancellationToken)
                : CalendarResult<CalendarEvent>.NotConnected();

        public async Task<CalendarResult<CalendarEvent>> UpdateEventAsync(
            string userId, string? calendarId, string eventId, CalendarEventDraft draft,
            CancellationToken cancellationToken = default) =>
            await ConnectedAsync(userId)
                ? await _inner.UpdateEventAsync(userId, calendarId, eventId, draft, cancellationToken)
                : CalendarResult<CalendarEvent>.NotConnected();

        public async Task<CalendarResult<bool>> DeleteEventAsync(
            string userId, string? calendarId, string eventId, CancellationToken cancellationToken = default) =>
            await ConnectedAsync(userId)
                ? await _inner.DeleteEventAsync(userId, calendarId, eventId, cancellationToken)
                : CalendarResult<bool>.NotConnected();
    }
}
