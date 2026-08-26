using System.Net;
using System.Web;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The Google OAuth mechanics (ADR 0053): PKCE, the authorization URL, and the code/refresh exchanges —
/// unit-tested against a fake authorization server. The live interactive dance is deploy-smoke; these
/// own OUR code.
/// </summary>
public sealed class GoogleOAuthClientTests
{
    private const string FakeAuthorize = "https://as.test/authorize";
    private const string FakeToken = "https://as.test/token";

    private static GoogleOAuthClient Client(RecordingHttpHandler handler) =>
        new(new HttpClient(handler), FakeAuthorize, FakeToken);

    // RFC 7636 Appendix B test vector — proves the S256 challenge is computed correctly. The same
    // vector McpOAuthClientTests uses, because GooglePkce is a deliberate copy of McpPkce and a copy
    // that quietly broke would otherwise be invisible.
    [Fact]
    public void Pkce_Challenge_MatchesRfc7636Vector()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", GooglePkce.Challenge(verifier));
    }

    [Fact]
    public void Pkce_VerifierAndState_AreUrlSafeAndUnique()
    {
        var v1 = GooglePkce.NewVerifier();
        var v2 = GooglePkce.NewVerifier();

        Assert.NotEqual(v1, v2);
        Assert.True(v1.Length >= 43); // RFC 7636 §4.1 minimum
        Assert.DoesNotContain('+', v1);
        Assert.DoesNotContain('/', v1);
        Assert.DoesNotContain('=', v1);
        Assert.NotEqual(GooglePkce.NewState(), GooglePkce.NewState());
    }

    // AC7. The two parameters this asserts are the ones whose absence fails INVISIBLY: without
    // access_type=offline and prompt=consent Google returns no refresh token, the connect flow still
    // reports success, and the calendar goes quiet an hour later.
    [Fact]
    public void AuthorizationUrl_RequestsOfflineAccessAndConsent()
    {
        var url = Client(new RecordingHttpHandler()).BuildAuthorizationUrl(
            clientId: "client-1",
            redirectUri: "https://chat.agent42.nl/api/google/oauth/callback",
            state: "state-1",
            codeChallenge: "challenge-1");

        var query = HttpUtility.ParseQueryString(new Uri(url).Query);

        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("consent", query["prompt"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("challenge-1", query["code_challenge"]);
        Assert.Equal("state-1", query["state"]);

        // The narrow scope PAIR, not the broad .../auth/calendar. Widening this would let an injected
        // instruction delete a whole calendar or change its sharing — see ADR 0053.
        var scopes = (query["scope"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            ["https://www.googleapis.com/auth/calendar.events",
             "https://www.googleapis.com/auth/calendar.calendarlist.readonly"],
            scopes);

        // The redirect must be https, or Google rejects it and the token would travel in the clear.
        Assert.Equal("https", new Uri(query["redirect_uri"]!).Scheme);
    }

    // Google's Web application client is CONFIDENTIAL: the exchange carries client_secret as well as
    // the PKCE verifier. This is the one place the public-client MCP flow does not carry over, so it is
    // asserted rather than assumed.
    [Fact]
    public async Task ExchangeCode_PostsConfidentialClientGrantWithPkce()
    {
        var handler = new RecordingHttpHandler
        {
            Responder = _ => RecordingHttpHandler.Json(
                """{"access_token":"at-1","refresh_token":"rt-1","expires_in":3600}"""),
        };

        var tokens = await Client(handler).ExchangeCodeAsync(
            "client-1", "secret-1", "code-1", "verifier-1",
            "https://chat.agent42.nl/api/google/oauth/callback", CancellationToken.None);

        var call = Assert.Single(handler.Recorded);
        Assert.Equal("POST", call.Method);
        Assert.Equal(FakeToken, call.Url);
        Assert.Contains("grant_type=authorization_code", call.Body, StringComparison.Ordinal);
        Assert.Contains("client_secret=secret-1", call.Body, StringComparison.Ordinal);
        Assert.Contains("code_verifier=verifier-1", call.Body, StringComparison.Ordinal);

        Assert.Equal("at-1", tokens.AccessToken);
        Assert.Equal("rt-1", tokens.RefreshToken);
        Assert.True(tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));
    }

    // A refresh response carries no refresh_token. Overwriting the stored one with null would end the
    // connection at the next expiry — the same silent death as a missing access_type=offline.
    [Fact]
    public async Task Refresh_CarriesTheExistingRefreshTokenForward()
    {
        var handler = new RecordingHttpHandler
        {
            Responder = _ => RecordingHttpHandler.Json("""{"access_token":"at-2","expires_in":3600}"""),
        };

        var tokens = await Client(handler).RefreshAsync(
            "client-1", "secret-1", "rt-original", CancellationToken.None);

        Assert.Contains("grant_type=refresh_token", Assert.Single(handler.Recorded).Body, StringComparison.Ordinal);
        Assert.Equal("at-2", tokens.AccessToken);
        Assert.Equal("rt-original", tokens.RefreshToken);
    }

    // A Google token-error body echoes the client id and often the redirect URI. The exception carries
    // the status code and nothing else — the endpoint turns it into a curated redirect reason.
    [Fact]
    public async Task TokenError_DoesNotCarryTheProviderBody()
    {
        var handler = new RecordingHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"error":"invalid_grant","error_description":"client 123.apps.googleusercontent.com"}"""),
            },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).ExchangeCodeAsync(
            "client-1", "secret-1", "code-1", "verifier-1", "https://app/callback", CancellationToken.None));

        Assert.DoesNotContain("invalid_grant", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("googleusercontent", ex.Message, StringComparison.Ordinal);
        Assert.Contains("400", ex.Message, StringComparison.Ordinal);
    }

    // The endpoints are constructor values so the flow above is testable at all — but production must
    // pass nothing and get Google. A typo in either default would send a real user's consent elsewhere.
    [Fact]
    public void DefaultEndpoints_AreGoogles()
    {
        Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth", GoogleOAuthClient.DefaultAuthorizationEndpoint);
        Assert.Equal("https://oauth2.googleapis.com/token", GoogleOAuthClient.DefaultTokenEndpoint);

        var url = new GoogleOAuthClient(new HttpClient(new RecordingHttpHandler()))
            .BuildAuthorizationUrl("c", "https://app/callback", "s", "ch");

        Assert.StartsWith(GoogleOAuthClient.DefaultAuthorizationEndpoint, url, StringComparison.Ordinal);
    }
}
