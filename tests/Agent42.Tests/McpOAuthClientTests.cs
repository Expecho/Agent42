using System.Net;
using System.Text;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The interactive-OAuth mechanics (config-driven remote MCP servers, Phase 3): PKCE, PRM/authorization-
/// server discovery, and the code/refresh token exchanges — unit-tested against a fake HTTP handler. The
/// live interactive dance is deploy-smoke; these own OUR code.
/// </summary>
public sealed class McpOAuthClientTests
{
    // RFC 7636 Appendix B test vector — proves the S256 challenge is computed correctly.
    [Fact]
    public void Pkce_Challenge_MatchesRfc7636Vector()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", McpPkce.Challenge(verifier));
    }

    [Fact]
    public void Pkce_VerifierAndState_AreUrlSafeAndUnique()
    {
        var v1 = McpPkce.NewVerifier();
        var v2 = McpPkce.NewVerifier();
        Assert.NotEqual(v1, v2);
        Assert.True(v1.Length >= 43); // RFC 7636 §4.1 minimum
        Assert.DoesNotContain('+', v1);
        Assert.DoesNotContain('/', v1);
        Assert.DoesNotContain('=', v1);
        Assert.NotEqual(McpPkce.NewState(), McpPkce.NewState());
    }

    [Fact]
    public void Tokens_SerializeRoundTrips()
    {
        var tokens = new McpOAuthTokens("at", "rt", DateTimeOffset.UtcNow.AddHours(1), "https://as/token", "client-1", ["s1", "s2"]);
        var back = McpOAuthTokens.Deserialize(tokens.Serialize());
        Assert.NotNull(back);
        Assert.Equal("at", back!.AccessToken);
        Assert.Equal("rt", back.RefreshToken);
        Assert.Equal("client-1", back.ClientId);
        Assert.Equal(["s1", "s2"], back.Scopes);
        Assert.Null(back.ReauthRequiredAt);
    }

    // ADR 0081 — the re-auth mark rides the bundle: it round-trips, and a bundle written before the field existed
    // deserializes it as null (no migration).
    [Fact]
    public void Tokens_ReauthMark_RoundTrips_AndIsAbsentFromOlderBundles()
    {
        var at = DateTimeOffset.Parse("2026-09-21T10:00:00Z");
        var marked = new McpOAuthTokens("at", "rt", null, "https://as/token", "c", []) { ReauthRequiredAt = at };
        Assert.Equal(at, McpOAuthTokens.Deserialize(marked.Serialize())!.ReauthRequiredAt);

        const string older = "{\"access_token\":\"at\",\"refresh_token\":\"rt\",\"expires_at\":null,\"token_endpoint\":\"https://as/token\",\"client_id\":\"c\",\"scopes\":[]}";
        Assert.Null(McpOAuthTokens.Deserialize(older)!.ReauthRequiredAt);
    }

    [Fact]
    public async Task Discover_ResolvesEndpoints_FromPrmAndAsMetadata()
    {
        var handler = new FakeHttpHandler
        {
            Responder = (req, _) =>
            {
                var url = req.RequestUri!.AbsoluteUri;
                if (url == "https://mcp.example.com/.well-known/oauth-protected-resource")
                {
                    return Json("""{"authorization_servers":["https://as.example.com"],"scopes_supported":["mcp.read"]}""");
                }

                if (url.StartsWith("https://as.example.com/.well-known/oauth-authorization-server"))
                {
                    return Json("""{"authorization_endpoint":"https://as.example.com/authorize","token_endpoint":"https://as.example.com/token","registration_endpoint":"https://as.example.com/register"}""");
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            },
        };

        var config = await new McpOAuthClient(new HttpClient(handler))
            .DiscoverAsync(new Uri("https://mcp.example.com/mcp"), CancellationToken.None);

        Assert.Equal("https://as.example.com/authorize", config.AuthorizationEndpoint.AbsoluteUri);
        Assert.Equal("https://as.example.com/token", config.TokenEndpoint.AbsoluteUri);
        Assert.Equal("https://as.example.com/register", config.RegistrationEndpoint!.AbsoluteUri);
    }

    [Fact]
    public async Task ExchangeCode_PostsAuthCodeGrantWithPkce_ParsesTokens()
    {
        var handler = new FakeHttpHandler
        {
            Responder = (req, _) => req.RequestUri!.AbsoluteUri == "https://as.example.com/token"
                ? Json("""{"access_token":"at-123","refresh_token":"rt-456","expires_in":3600}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        var tokens = await new McpOAuthClient(new HttpClient(handler)).ExchangeCodeAsync(
            new Uri("https://as.example.com/token"), "client-1", "auth-code", "verifier-xyz",
            new Uri("https://app.example/callback"), new Uri("https://mcp.example.com/mcp"), ["mcp.read"], CancellationToken.None);

        Assert.Equal("at-123", tokens.AccessToken);
        Assert.Equal("rt-456", tokens.RefreshToken);
        Assert.NotNull(tokens.ExpiresAt);
        Assert.Equal("client-1", tokens.ClientId);
        Assert.Contains("grant_type=authorization_code", handler.LastBody);
        Assert.Contains("code=auth-code", handler.LastBody);
        Assert.Contains("code_verifier=verifier-xyz", handler.LastBody);
        Assert.Contains("resource=", handler.LastBody);
    }

    [Fact]
    public async Task Refresh_PostsRefreshGrant_ParsesNewTokens()
    {
        var handler = new FakeHttpHandler
        {
            Responder = (req, _) => req.RequestUri!.AbsoluteUri == "https://as.example.com/token"
                ? Json("""{"access_token":"at-new","refresh_token":"rt-new","expires_in":3600}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var existing = new McpOAuthTokens("at-old", "rt-old", DateTimeOffset.UtcNow, "https://as.example.com/token", "client-1", ["mcp.read"]);

        var refreshed = await new McpOAuthClient(new HttpClient(handler))
            .RefreshAsync(existing, new Uri("https://mcp.example.com/mcp"), CancellationToken.None);

        Assert.Equal("at-new", refreshed.AccessToken);
        Assert.Contains("grant_type=refresh_token", handler.LastBody);
        Assert.Contains("refresh_token=rt-old", handler.LastBody);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, string, HttpResponseMessage> Responder { get; set; } =
            (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return Responder(request, LastBody ?? "");
        }
    }
}
