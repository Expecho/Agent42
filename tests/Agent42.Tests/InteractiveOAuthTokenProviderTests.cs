using System.Net;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The per-request InteractiveOAuth bearer (ADR 0081) end to end through <see cref="AuthHeaderHandler"/>, against a
/// real config grain (the resolver reads and CAS-writes it) and a fake token endpoint. A "live connection" here is
/// one handler reused across requests — what the version cache holds for as long as the overlay is unchanged.
/// </summary>
public sealed class InteractiveOAuthTokenProviderTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private readonly FakeTokenEndpoint _endpoint = new();
    private readonly IMcpSecretProtector _protector =
        new McpSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance);

    private McpOAuthTokenResolver NewResolver() =>
        new(Cluster.GrainFactory, _protector, new McpOAuthClient(new HttpClient(_endpoint)),
            NullLogger<McpOAuthTokenResolver>.Instance);

    private static McpOAuthTokens Tokens(string access, TimeSpan expiresIn, string refresh = "refresh-1") =>
        new(access, refresh, DateTimeOffset.UtcNow.Add(expiresIn), "https://as.example.com/token", "client-1", []);

    private StoredMcpServer Server(McpOAuthTokens tokens) => new(
        Id: "o1",
        Name: "oauth-o1",
        Endpoint: "https://mcp.example.com/mcp",
        Auth: nameof(McpAuthMode.InteractiveOAuth),
        Enabled: true,
        TokenCiphertext: _protector.Protect(tokens.Serialize()));

    // The MCP server: 200 for the accepted bearer, 401 otherwise. Records every bearer it saw.
    private sealed class FakeMcpServer : HttpMessageHandler
    {
        public string Accepted { get; set; } = "";
        public List<string> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.Headers.GetValues("Authorization").Single());
            return Task.FromResult(new HttpResponseMessage(
                Seen[^1] == $"Bearer {Accepted}" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized));
        }
    }

    private static async Task<HttpStatusCode> CallAsync(HttpMessageInvoker connection)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://mcp.example.com/mcp")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { jsonrpc = "2.0", method = "tools/call" }),
        };
        using var response = await connection.SendAsync(request, CancellationToken.None);
        return response.StatusCode;
    }

    private static HttpMessageInvoker Connect(InteractiveOAuthTokenProvider bearer, FakeMcpServer server) =>
        new(new AuthHeaderHandler(bearer) { InnerHandler = server });

    // AC1 — a connection resolved while the token was valid keeps working after it expires: the bearer notices the
    // expiry on the next request and the resolver refreshes it, with no config write (the version is unchanged).
    [Fact]
    public async Task PerRequestBearer_RefreshesAnExpiredToken_OnALiveConnection()
    {
        const string user = "oauth-bearer-expired";
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        await grain.SaveAsync(Server(Tokens("access-old", TimeSpan.FromSeconds(-30))));
        var version = await grain.GetVersionAsync();
        // The resolution the connection cached at connect time; its token has expired since.
        var atConnect = new McpOAuthResolution(McpOAuthState.Connected, "access-old", DateTimeOffset.UtcNow.AddSeconds(-30));
        var server = new FakeMcpServer { Accepted = "access-new" };
        using var connection = Connect(new InteractiveOAuthTokenProvider(user, "o1", NewResolver(), atConnect), server);

        Assert.Equal(HttpStatusCode.OK, await CallAsync(connection));

        Assert.Equal(["Bearer access-new"], server.Seen); // refreshed BEFORE sending, not after a 401
        Assert.Equal(1, _endpoint.Calls);
        Assert.Equal(version, await grain.GetVersionAsync());
    }

    // AC4c — the user re-consented elsewhere (the OAuth callback wrote a new bundle) while this connection still held
    // the old, unexpired token. The server's 401 makes the bearer re-resolve, find the NEWER stored token, and resend
    // once — the live connection recovers without any refresh call and without a reconnect.
    [Fact]
    public async Task A401_ReResolvesAndRetriesOnce_RecoveringALiveConnection()
    {
        const string user = "oauth-bearer-reconsent";
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        await grain.SaveAsync(Server(Tokens("access-old", TimeSpan.FromHours(1))));
        var resolver = NewResolver();
        var bearer = new InteractiveOAuthTokenProvider(user, "o1", resolver, await resolver.ResolveAsync(user, "o1"));
        var server = new FakeMcpServer { Accepted = "access-old" };
        using var connection = Connect(bearer, server);
        Assert.Equal(HttpStatusCode.OK, await CallAsync(connection));

        // Re-consent elsewhere: a new bundle; the server now only accepts the new token.
        await grain.SaveAsync(Server(Tokens("access-consent", TimeSpan.FromHours(1))));
        server.Accepted = "access-consent";

        Assert.Equal(HttpStatusCode.OK, await CallAsync(connection));

        Assert.Equal(["Bearer access-old", "Bearer access-old", "Bearer access-consent"], server.Seen);
        Assert.Equal(0, _endpoint.Calls);
        Assert.Equal(McpOAuthState.Connected, bearer.State);
    }

    // While the server is marked ReauthRequired, every request re-reads the bundle, so a fresh consent is used from
    // the very next request — no 401 needed first.
    [Fact]
    public async Task WhileReauthRequired_EveryRequestRereads_AndPicksUpAFreshConsent()
    {
        const string user = "oauth-bearer-marked";
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        await grain.SaveAsync(Server(Tokens("access-dead", TimeSpan.FromHours(1)) with { ReauthRequiredAt = DateTimeOffset.UtcNow }));
        var resolver = NewResolver();
        var bearer = new InteractiveOAuthTokenProvider(user, "o1", resolver, await resolver.ResolveAsync(user, "o1"));
        Assert.Equal(McpOAuthState.ReauthRequired, bearer.State);

        await grain.SaveAsync(Server(Tokens("access-consent", TimeSpan.FromHours(1))));

        Assert.Equal("Bearer access-consent", await bearer.GetHeaderValueAsync(default));
        Assert.Equal(McpOAuthState.Connected, bearer.State);
        Assert.Equal(0, _endpoint.Calls);
    }
}
