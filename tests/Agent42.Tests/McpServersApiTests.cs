using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The /api/mcp-servers CRUD contract (config-driven remote MCP servers), hosted in-memory (TestServer)
/// against a real Orleans test cluster — so the encrypt-before-grain, mask-on-read, and per-user isolation
/// guarantees are exercised end to end. Mirrors <see cref="ProvidersApiTests"/> (ADR 0018).
/// </summary>
public sealed class McpServersApiTests(SharedApiFixture fixture) : SharedClusterTests<SharedApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    // The host, the cluster and the client are all the fixture's, deployed once for the class.
    private HttpClient Client => Fixture.Client;

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

    private static object HeaderServerBody(string? headerValue = "secret-token") => new
    {
        name = "Dataverse",
        endpoint = "https://example.crm4.dynamics.com/mcp",
        auth = "Header",
        enabled = true,
        headerName = "Authorization",
        headerValue,
    };

    private static async Task<JsonElement> SingleServer(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("servers").EnumerateArray().Single().Clone();
    }

    // AC5: a stored secret is never returned — GET masks it to hasHeaderValue, plaintext appears nowhere.
    [Fact]
    public async Task Get_NeverReturnsSecret()
    {
        (await Client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", "user-a", HeaderServerBody("super-secret-value"))))
            .EnsureSuccessStatusCode();

        var response = await Client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", "user-a"));
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("super-secret-value", raw);
        using var doc = JsonDocument.Parse(raw);
        var server = doc.RootElement.GetProperty("servers").EnumerateArray().Single();
        Assert.Equal("Dataverse", server.GetProperty("name").GetString());
        Assert.True(server.GetProperty("hasHeaderValue").GetBoolean());
        Assert.False(server.TryGetProperty("headerValue", out _));
        Assert.False(server.TryGetProperty("headerValueCiphertext", out _));
    }

    // AC6: a localhost endpoint is accepted (a self-hosted MCP server is a valid target); a malformed
    // endpoint and an unsupported auth mode are each rejected with 400.
    [Fact]
    public async Task AcceptsLocalhost_RejectsMalformed()
    {
        var ok = await Client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", "user-b", new
        {
            name = "Local",
            endpoint = "http://localhost:8080/mcp",
            auth = "None",
            enabled = true,
        }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var badUrl = await Client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", "user-b", new
        {
            name = "Broken",
            endpoint = "not-a-valid-url",
            auth = "None",
            enabled = true,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, badUrl.StatusCode);

        var badMode = await Client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", "user-b", new
        {
            name = "Weird",
            endpoint = "https://example.com/mcp",
            auth = "Nonsense",
            enabled = true,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);

        // Header auth with no header name is rejected.
        var noHeaderName = await Client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", "user-b", new
        {
            name = "HeaderNoName",
            endpoint = "https://example.com/mcp",
            auth = "Header",
            enabled = true,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, noHeaderName.StatusCode);
    }

    // AC5 (over HTTP): editing without re-entering the secret keeps it (blank headerValue = keep).
    [Fact]
    public async Task Put_BlankSecret_KeepsStoredSecret()
    {
        var post = await Client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", "user-c", HeaderServerBody("original-secret")));
        post.EnsureSuccessStatusCode();
        var id = (await SingleServer(await Client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", "user-c"))))
            .GetProperty("id").GetString();

        // Rename, sending NO secret (blank headerValue).
        var put = await Client.SendAsync(Request(HttpMethod.Put, $"/api/mcp-servers/{id}", "user-c", new
        {
            name = "Renamed",
            endpoint = "https://example.crm4.dynamics.com/mcp",
            auth = "Header",
            enabled = false,
            headerName = "Authorization",
            headerValue = (string?)null,
        }));
        put.EnsureSuccessStatusCode();

        var server = await SingleServer(await Client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", "user-c")));
        Assert.Equal("Renamed", server.GetProperty("name").GetString());
        Assert.False(server.GetProperty("enabled").GetBoolean());
        Assert.True(server.GetProperty("hasHeaderValue").GetBoolean()); // secret survived the secretless edit
    }

    private static object ClientCredentialsBody(string? clientSecret = "cc-secret") => new
    {
        name = "Dataverse CC",
        endpoint = "https://org.crm4.dynamics.com/mcp",
        auth = "ClientCredentials",
        enabled = true,
        authority = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000",
        clientId = "11111111-1111-1111-1111-111111111111",
        clientSecret,
        scopes = new[] { "https://org.crm4.dynamics.com/.default" },
    };

    // AC11 (API): a ClientCredentials server round-trips authority/clientId/scopes and masks the secret to
    // hasClientSecret — the ciphertext never leaves the server.
    [Fact]
    public async Task ClientCredentials_RoundTrips_MasksSecret()
    {
        (await Client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", "user-cc", ClientCredentialsBody("super-cc-secret"))))
            .EnsureSuccessStatusCode();

        var response = await Client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", "user-cc"));
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-cc-secret", raw);

        var server = await SingleServer(await Client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", "user-cc")));
        Assert.Equal("ClientCredentials", server.GetProperty("auth").GetString());
        Assert.Equal("https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000", server.GetProperty("authority").GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", server.GetProperty("clientId").GetString());
        Assert.Equal("https://org.crm4.dynamics.com/.default", server.GetProperty("scopes").EnumerateArray().Single().GetString());
        Assert.True(server.GetProperty("hasClientSecret").GetBoolean());
        Assert.False(server.TryGetProperty("clientSecret", out _));
    }

    // AC11 (validation): ClientCredentials requires an https authority, a client id, and ≥1 scope.
    [Fact]
    public async Task ClientCredentials_Validation_RejectsMissingFields()
    {
        async Task<HttpStatusCode> Post(object body) =>
            (await Client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", "user-ccv", body))).StatusCode;

        // No authority.
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new
        {
            name = "x",
            endpoint = "https://org.crm4.dynamics.com/mcp",
            auth = "ClientCredentials",
            enabled = true,
            clientId = "cid",
            scopes = new[] { "s/.default" },
        }));
        // Non-https authority.
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new
        {
            name = "x",
            endpoint = "https://org.crm4.dynamics.com/mcp",
            auth = "ClientCredentials",
            enabled = true,
            authority = "http://login.microsoftonline.com/tenant",
            clientId = "cid",
            scopes = new[] { "s/.default" },
        }));
        // No client id.
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new
        {
            name = "x",
            endpoint = "https://org.crm4.dynamics.com/mcp",
            auth = "ClientCredentials",
            enabled = true,
            authority = "https://login.microsoftonline.com/tenant",
            scopes = new[] { "s/.default" },
        }));
        // No scopes.
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new
        {
            name = "x",
            endpoint = "https://org.crm4.dynamics.com/mcp",
            auth = "ClientCredentials",
            enabled = true,
            authority = "https://login.microsoftonline.com/tenant",
            clientId = "cid",
        }));
        // A complete ClientCredentials body is accepted.
        Assert.Equal(HttpStatusCode.OK, await Post(ClientCredentialsBody()));
    }

    // AC11 (blank-keep): editing a ClientCredentials server without re-entering the secret keeps it.
    [Fact]
    public async Task ClientCredentials_Put_BlankSecret_KeepsStoredSecret()
    {
        (await Client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", "user-cck", ClientCredentialsBody("original-cc"))))
            .EnsureSuccessStatusCode();
        var id = (await SingleServer(await Client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", "user-cck"))))
            .GetProperty("id").GetString();

        var put = await Client.SendAsync(Request(HttpMethod.Put, $"/api/mcp-servers/{id}", "user-cck", new
        {
            name = "Renamed CC",
            endpoint = "https://org.crm4.dynamics.com/mcp",
            auth = "ClientCredentials",
            enabled = true,
            authority = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000",
            clientId = "11111111-1111-1111-1111-111111111111",
            clientSecret = (string?)null,
            scopes = new[] { "https://org.crm4.dynamics.com/.default" },
        }));
        put.EnsureSuccessStatusCode();

        var server = await SingleServer(await Client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", "user-cck")));
        Assert.Equal("Renamed CC", server.GetProperty("name").GetString());
        Assert.True(server.GetProperty("hasClientSecret").GetBoolean()); // secret survived the secretless edit
    }

    // Phase 3: an InteractiveOAuth server is accepted (structural validation only — endpoints are discovered
    // and tokens obtained later via the Connect flow) and reports hasToken=false until connected.
    [Fact]
    public async Task InteractiveOAuth_IsAccepted_HasTokenFalseUntilConnected()
    {
        var post = await Client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", "user-oauth", new
        {
            name = "OAuth server",
            endpoint = "https://mcp.example.com/mcp",
            auth = "InteractiveOAuth",
            enabled = true,
            clientId = "client-1",
        }));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var server = await SingleServer(await Client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", "user-oauth")));
        Assert.Equal("InteractiveOAuth", server.GetProperty("auth").GetString());
        Assert.False(server.GetProperty("hasToken").GetBoolean());
    }

    // AC11 (ADR 0081) — an InteractiveOAuth server whose bundle carries the invalid_grant mark reports needsReauth, and
    // the response still leaks nothing of the bundle: no ciphertext, no access or refresh token.
    [Fact]
    public async Task NeedsReauth_IsReported_AndNoSecretLeaks()
    {
        const string user = "user-oauth-reauth";
        var protector = Fixture.App.Services.GetRequiredService<IMcpSecretProtector>();
        var marked = new McpOAuthTokens("access-secret-value", "refresh-secret-value", DateTimeOffset.UtcNow.AddHours(1),
            "https://as.example.com/token", "client-1", [])
        { ReauthRequiredAt = DateTimeOffset.UtcNow };
        var ciphertext = protector.Protect(marked.Serialize());
        var config = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        await config.SaveAsync(new StoredMcpServer(
            "o1", "Calendar", "https://mcp.example.com/mcp", nameof(McpAuthMode.InteractiveOAuth), true, TokenCiphertext: ciphertext));
        await config.SaveAsync(new StoredMcpServer(
            "o2", "Mail", "https://mail.example.com/mcp", nameof(McpAuthMode.InteractiveOAuth), true,
            TokenCiphertext: protector.Protect((marked with { ReauthRequiredAt = null }).Serialize())));

        // Expired with no refresh token: the same "needs reauth" rule the resolver applies, so the row agrees with
        // the transcript's Reconnect notice.
        await config.SaveAsync(new StoredMcpServer(
            "o3", "Drive", "https://drive.example.com/mcp", nameof(McpAuthMode.InteractiveOAuth), true,
            TokenCiphertext: protector.Protect((marked with
            {
                ReauthRequiredAt = null,
                RefreshToken = null,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            }).Serialize())));

        var response = await Client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", user));
        var raw = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(raw);
        var servers = doc.RootElement.GetProperty("servers").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        Assert.True(servers["o1"].GetProperty("needsReauth").GetBoolean());
        Assert.False(servers["o2"].GetProperty("needsReauth").GetBoolean());
        Assert.True(servers["o3"].GetProperty("needsReauth").GetBoolean());
        Assert.True(servers["o1"].GetProperty("hasToken").GetBoolean());
        Assert.DoesNotContain(ciphertext, raw);
        Assert.DoesNotContain("access-secret-value", raw);
        Assert.DoesNotContain("refresh-secret-value", raw);
    }

    // CRUD is per-user — one user cannot see another's servers, and delete only affects the owner.
    [Fact]
    public async Task Crud_IsPerUser()
    {
        var post = await Client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", "alice", HeaderServerBody()));
        post.EnsureSuccessStatusCode();
        var id = (await SingleServer(await Client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", "alice"))))
            .GetProperty("id").GetString();

        // Bob sees nothing.
        var bob = await Client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", "bob"));
        using (var bobDoc = JsonDocument.Parse(await bob.Content.ReadAsStringAsync()))
        {
            Assert.Empty(bobDoc.RootElement.GetProperty("servers").EnumerateArray());
        }

        // Bob deleting Alice's id does not touch Alice's overlay.
        (await Client.SendAsync(Request(HttpMethod.Delete, $"/api/mcp-servers/{id}", "bob"))).EnsureSuccessStatusCode();
        var aliceStill = await Client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", "alice"));
        using (var aliceDoc = JsonDocument.Parse(await aliceStill.Content.ReadAsStringAsync()))
        {
            Assert.Single(aliceDoc.RootElement.GetProperty("servers").EnumerateArray());
        }

        // Alice deleting it removes it.
        (await Client.SendAsync(Request(HttpMethod.Delete, $"/api/mcp-servers/{id}", "alice"))).EnsureSuccessStatusCode();
        var aliceAfter = await Client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", "alice"));
        using var afterDoc = JsonDocument.Parse(await aliceAfter.Content.ReadAsStringAsync());
        Assert.Empty(afterDoc.RootElement.GetProperty("servers").EnumerateArray());
    }

    // AC12 — the single easiest way to ship per-tool selection broken. KnownToolNames is DISCOVERED metadata
    // that no request carries, and PUT /api/mcp-servers/{id} rebuilds the record from scratch via ToStored,
    // so nothing on the request path can carry it forward: only UserMcpConfigGrain.SaveAsync's Keep clause
    // does. Drop that clause and every rename, header rotation or enable/disable round-trip silently wipes a
    // server's per-tool checkboxes — turning the "children not rendered" state into the ROUTINE state
    // immediately after an MCP edit, which is exactly when a user opens the Tools picker.
    //
    // Exercised through the real endpoint on purpose: a grain-level test cannot see ToStored, which is the
    // thing that actually drops the field.
    [Fact]
    public async Task Edit_PreservesKnownToolNames()
    {
        const string user = "user-known-tools";

        (await Client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", user, HeaderServerBody())))
            .EnsureSuccessStatusCode();
        var id = (await SingleServer(await Client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", user))))
            .GetProperty("id").GetString()!;

        // Stand in for a successful connect: the provider records what it discovered.
        var config = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        await config.RecordToolNamesAsync(id, ["jira_get_issue", "jira_delete_issue"]);

        var put = await Client.SendAsync(Request(HttpMethod.Put, $"/api/mcp-servers/{id}", user, new
        {
            name = "Renamed",
            endpoint = "https://example.crm4.dynamics.com/mcp",
            auth = "Header",
            enabled = true,
            headerName = "Authorization",
            headerValue = (string?)null,
        }));
        put.EnsureSuccessStatusCode();

        var stored = Assert.Single((await config.GetAsync()).Servers);
        Assert.Equal("Renamed", stored.Name); // the edit really happened
        Assert.Equal(["jira_get_issue", "jira_delete_issue"], stored.KnownToolNames);
    }

    // AC12 (ADR 0067): the URL-seeding opt-in survives POST → GET → PUT → GET. Exercised through the real
    // endpoints because ToStored and Mask are the two places it can be dropped, and neither is reachable from
    // a grain-level test. Note this is the OPPOSITE property to KnownToolNames above: that one must survive an
    // edit that does not mention it, this one must follow what the request says — including "off".
    [Fact]
    public async Task Server_SeedsFetchableUrls_RoundTrips()
    {
        const string user = "user-seeding-roundtrip";

        var created = await Client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", user, new
        {
            name = "Scraper",
            endpoint = "https://scraper.example/mcp",
            auth = "None",
            enabled = true,
            seedsFetchableUrls = true,
        }));
        created.EnsureSuccessStatusCode();
        Assert.True((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("seedsFetchableUrls").GetBoolean());

        var listed = await SingleServer(await Client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", user)));
        Assert.True(listed.GetProperty("seedsFetchableUrls").GetBoolean());
        var id = listed.GetProperty("id").GetString()!;

        // And the PUT can turn it back off — the field is a SETTING, so an absent/false value means off.
        var put = await Client.SendAsync(Request(HttpMethod.Put, $"/api/mcp-servers/{id}", user, new
        {
            name = "Scraper",
            endpoint = "https://scraper.example/mcp",
            auth = "None",
            enabled = true,
            seedsFetchableUrls = false,
        }));
        put.EnsureSuccessStatusCode();

        Assert.False((await SingleServer(await Client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", user))))
            .GetProperty("seedsFetchableUrls").GetBoolean());
    }
}