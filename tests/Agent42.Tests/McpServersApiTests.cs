using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.TestHost;

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
}
