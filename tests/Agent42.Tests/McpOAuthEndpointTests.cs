using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.WebUtilities;

namespace Agent42.Tests;

/// <summary>
/// The interactive-OAuth endpoints end to end (config-driven remote MCP servers, Phase 3): /api/mcp/oauth/start
/// builds the PKCE authorization redirect and persists pending state; /callback exchanges the code and persists
/// ENCRYPTED tokens on the server (AC12). Runs against a real Orleans cluster + a fake authorization server
/// (the McpOAuthClient's HttpClient is backed by a routing stub). The live interactive dance is deploy-smoke.
/// </summary>
public sealed class McpOAuthEndpointTests(McpOAuthApiFixture fixture) : SharedClusterTests<McpOAuthApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    // The host and cluster are the fixture's, built once for the class. This client does NOT auto-follow
    // redirects — the 302 Location is what these tests inspect.
    private readonly HttpClient _client = fixture.CreateNonRedirectingClient();

    protected override Task OnDisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    private static HttpRequestMessage Request(HttpMethod method, string uri, string principalId, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(IdHeader, principalId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private async Task<string> CreateOAuthServerAsync(string user)
    {
        var post = await _client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", user, new
        {
            name = "OAuth server",
            endpoint = "https://mcp.example.com/mcp",
            auth = "InteractiveOAuth",
            enabled = true,
            clientId = "client-1", // pre-registered, so /start skips dynamic registration
        }));
        post.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    // /start discovers the authorization server and 302-redirects to a PKCE authorization URL.
    [Fact]
    public async Task Start_RedirectsToAuthorizeUrl_WithPkce()
    {
        const string user = "oauth-start";
        var id = await CreateOAuthServerAsync(user);

        var response = await _client.SendAsync(Request(HttpMethod.Get, $"/api/mcp/oauth/start?serverId={id}", user));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.AbsoluteUri;
        Assert.StartsWith("https://as.example.com/authorize", location);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("client-1", query["client_id"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrEmpty(query["code_challenge"].ToString()));
        Assert.False(string.IsNullOrEmpty(query["state"].ToString()));
        Assert.Equal("https://mcp.example.com/mcp", query["resource"].ToString());
    }

    // AC12: /callback exchanges the code and persists ENCRYPTED tokens (surfaced as hasToken, never the token).
    [Fact]
    public async Task OAuthCallback_PersistsEncryptedTokens()
    {
        const string user = "oauth-callback";
        var id = await CreateOAuthServerAsync(user);

        // Drive /start to mint a real pending state, then feed its state to /callback.
        var start = await _client.SendAsync(Request(HttpMethod.Get, $"/api/mcp/oauth/start?serverId={id}", user));
        var state = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"].ToString();

        var callback = await _client.SendAsync(Request(HttpMethod.Get, $"/api/mcp/oauth/callback?code=auth-code&state={state}", user));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Contains("mcp_oauth=connected", callback.Headers.Location!.ToString());

        // The server now reports a stored token; the token/ciphertext never appears in the payload.
        var list = await _client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", user));
        var raw = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("at-123", raw); // the fake access token
        using var doc = JsonDocument.Parse(raw);
        var server = doc.RootElement.GetProperty("servers").EnumerateArray().Single();
        Assert.True(server.GetProperty("hasToken").GetBoolean());
    }

    // A replayed callback (the pending state already consumed) is rejected, not re-exchanged.
    [Fact]
    public async Task OAuthCallback_ReplayedState_IsRejected()
    {
        const string user = "oauth-replay";
        var id = await CreateOAuthServerAsync(user);
        var start = await _client.SendAsync(Request(HttpMethod.Get, $"/api/mcp/oauth/start?serverId={id}", user));
        var state = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"].ToString();

        (await _client.SendAsync(Request(HttpMethod.Get, $"/api/mcp/oauth/callback?code=c&state={state}", user))).Dispose();
        var replay = await _client.SendAsync(Request(HttpMethod.Get, $"/api/mcp/oauth/callback?code=c&state={state}", user));

        Assert.Contains("mcp_oauth=error", replay.Headers.Location!.ToString());
        Assert.Contains("unknown_state", replay.Headers.Location!.ToString());
    }
}
