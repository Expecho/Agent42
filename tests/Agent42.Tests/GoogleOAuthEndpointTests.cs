using System.Net;
using System.Text.Json;
using System.Web;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The Google connection endpoints end to end (ADR 0053): <c>/api/google/oauth/start</c> builds the PKCE
/// authorization redirect and persists pending state; <c>/callback</c> exchanges the code and persists
/// <b>encrypted</b> tokens; <c>/status</c> reports the connection without ever echoing one. Runs against
/// a real Orleans cluster plus a fake authorization server. The live interactive dance is deploy-smoke.
/// </summary>
public sealed class GoogleOAuthEndpointTests(GoogleOAuthApiFixture fixture) : SharedClusterTests<GoogleOAuthApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";
    private const string User = "google-endpoint-user";

    // The host, the cluster and the key ring are the fixture's, built once for the class. The client does
    // NOT auto-follow redirects — the 302 Location is what these tests inspect.
    private readonly HttpClient _client = fixture.CreateNonRedirectingClient();
    private readonly IGoogleSecretProtector _protector = fixture.Protector;

    protected override Task OnDisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
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

        var stored = await Cluster.GrainFactory.GetGrain<IUserGoogleGrain>(User).GetAsync();
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
        Assert.Null(await Cluster.GrainFactory.GetGrain<IUserGoogleGrain>("bob-user").GetAsync());
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
        Assert.Null(await Cluster.GrainFactory.GetGrain<IUserGoogleGrain>(user).GetAsync());

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
}
