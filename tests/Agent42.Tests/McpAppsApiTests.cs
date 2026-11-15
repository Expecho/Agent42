using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>A host whose MCP App registry is a scripted stub: the routes are under test, not the connection behind them.</summary>
public sealed class McpAppsApiFixture : SharedApiFixture
{
    public StubMcpAppRegistry Registry { get; } = new();

    protected override void Configure(WebApplicationBuilder builder)
        => builder.Services.AddSingleton<IMcpAppRegistry>(Registry);
}

public sealed class StubMcpAppRegistry : IMcpAppRegistry
{
    public List<(string User, string Server, string What)> Calls { get; } = [];

    public Task<McpAppOutcome<McpAppView>> ReadUiResourceAsync(string userId, string serverId, string uri, CancellationToken cancellationToken)
    {
        Calls.Add((userId, serverId, uri));
        return Task.FromResult(uri switch
        {
            "ui://t/plain" => McpAppOutcome<McpAppView>.Success(new McpAppView("<p>plain</p>", null)),
            "ui://t/declared" => McpAppOutcome<McpAppView>.Success(new McpAppView(
                "<p>declared</p>",
                """
                {"csp":{"connectDomains":["https://api.example.com","https://evil.example.com; script-src *","https://localhost","https://*.com"],
                        "resourceDomains":["https://cdn.example.com"]}}
                """)),
            "ui://t/big" => McpAppOutcome<McpAppView>.Fail(McpAppError.TooLarge),
            "ui://t/bad" => McpAppOutcome<McpAppView>.Fail(McpAppError.BadRequest),
            "ui://t/broken" => McpAppOutcome<McpAppView>.Fail(McpAppError.Failed),
            _ => McpAppOutcome<McpAppView>.Fail(McpAppError.Unavailable),
        });
    }

    public Task<McpAppOutcome<JsonElement>> CallToolAsync(
        string userId, string serverId, string tool, JsonElement? arguments, CancellationToken cancellationToken)
    {
        Calls.Add((userId, serverId, $"{tool}:{arguments?.GetRawText()}"));
        return Task.FromResult(tool switch
        {
            "refresh" => McpAppOutcome<JsonElement>.Success(JsonDocument.Parse("""{"content":[{"type":"text","text":"fresh"}]}""").RootElement),
            "model_only" => McpAppOutcome<JsonElement>.Fail(McpAppError.Forbidden),
            _ => McpAppOutcome<JsonElement>.Fail(McpAppError.Unavailable),
        });
    }
}

/// <summary>
/// The MCP App routes (ADR 0133): what they serve, to whom, and above all with which headers. The frame route runs
/// third-party script, and the CSP header is what keeps it off the app origin even when someone opens the frame as a
/// top-level page, where the iframe attribute does not exist.
/// </summary>
public sealed class McpAppsApiTests(McpAppsApiFixture fixture) : SharedClusterTests<McpAppsApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";
    private const string Marker = "API-STORED-MARKER";
    private static readonly McpAppRef AppRef = new("srv-app", FakeChatClient.SeedingLinkToolName, "ui://chart/view.html");

    private HttpClient Client => Fixture.Client;

    protected override Task OnInitializeAsync()
    {
        Fixture.Registry.Calls.Clear();
        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress)
                .GetRequiredService<SingleUserMcpToolProvider>().Servers =
                [
                    new McpServerTools("srv-app", [AppTool()], [FakeChatClient.SeedingLinkToolName]),
                ];
        }

        return Task.CompletedTask;
    }

    private static AIFunction AppTool() =>
        new TruncatingMcpTool(
            AIFunctionFactory.Create(
                JsonElement (string filter) => JsonDocument.Parse(
                    $$$"""{"content":[{"type":"text","text":"ok"}],"structuredContent":{"m":"{{{Marker}}}"}}""").RootElement,
                FakeChatClient.SeedingLinkToolName,
                "shows a chart"),
            AppRef);

    private static HttpRequestMessage Request(HttpMethod method, string uri, string user, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Add(IdHeader, user);
        return request;
    }

    private Task<HttpResponseMessage> Get(string uri, string user) => Client.SendAsync(Request(HttpMethod.Get, uri, user));

    private Task<HttpResponseMessage> Post(string uri, string user, object body)
        => Client.SendAsync(Request(HttpMethod.Post, uri, user, JsonContent.Create(body)));

    private static string Csp(HttpResponseMessage response) => string.Join(";", response.Headers.GetValues("Content-Security-Policy"));

    private static void AssertFrameHeaders(HttpResponseMessage response, string expectedCsp)
    {
        Assert.Equal(expectedCsp, Csp(response));
        Assert.DoesNotContain("allow-same-origin", Csp(response), StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("no-store, private", string.Join(", ", response.Headers.CacheControl!.ToString().Split(", ").Order()));
    }

    private async Task<string> ChatWithAppAsync(string user, string conversation)
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/{conversation}");
        await foreach (var _ in agent.ChatAsync(
            $"{FakeChatClient.SeedingChainTrigger} a chart", TestSiloConfigurator.DefaultModel, null, false, true, [ClientToolLimits.McpApp]))
        {
        }

        return "seeding-link-call-1";
    }

    // AC5: a view with no declared domains is served the narrowest policy, as html, with the four headers.
    [Fact]
    public async Task Frame_ServesTheViewUnderTheNarrowestPolicy()
    {
        var response = await Get("/api/mcp-apps/srv/frame?uri=ui%3A%2F%2Ft%2Fplain", "mcp-api-user");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("<p>plain</p>", await response.Content.ReadAsStringAsync());
        AssertFrameHeaders(response, McpAppCsp.Default);
        Assert.Contains(("mcp-api-user", "srv", "ui://t/plain"), Fixture.Registry.Calls);
    }

    // AC5: declared domains reach the header only once validated: the injection attempt, our own host and the global
    // wildcard are dropped, the legitimate origins stay, and the policy is exactly what McpAppCsp builds.
    [Fact]
    public async Task Frame_HonorsValidDeclaredDomains_AndDropsTheRest()
    {
        var response = await Get("/api/mcp-apps/srv/frame?uri=ui%3A%2F%2Ft%2Fdeclared", "mcp-api-user");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csp = Csp(response);
        Assert.Contains("connect-src https://api.example.com;", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'unsafe-inline' https://cdn.example.com;", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("*", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("'self' ", csp.Replace("frame-ancestors 'self'", ""), StringComparison.Ordinal);
        Assert.StartsWith("sandbox allow-scripts;", csp, StringComparison.Ordinal);
        Assert.EndsWith("form-action 'none'; frame-ancestors 'self'", csp, StringComparison.Ordinal);
    }

    // AC5: no path through the route renders without the policy, an error included.
    [Theory]
    [InlineData("ui%3A%2F%2Ft%2Fnothing", HttpStatusCode.NotFound)]
    [InlineData("ui%3A%2F%2Ft%2Fbad", HttpStatusCode.BadRequest)]
    [InlineData("ui%3A%2F%2Ft%2Fbig", HttpStatusCode.RequestEntityTooLarge)]
    [InlineData("ui%3A%2F%2Ft%2Fbroken", HttpStatusCode.BadGateway)]
    public async Task Frame_ErrorsCarryThePolicyToo(string uri, HttpStatusCode status)
    {
        var response = await Get($"/api/mcp-apps/srv/frame?uri={uri}", "mcp-api-user");

        Assert.Equal(status, response.StatusCode);
        AssertFrameHeaders(response, McpAppCsp.Default);
    }

    [Fact]
    public async Task Frame_WithoutAUri_IsRefused_UnderThePolicy()
    {
        var response = await Get("/api/mcp-apps/srv/frame", "mcp-api-user");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        AssertFrameHeaders(response, McpAppCsp.Default);
    }

    // The user id is the principal's, never anything in the request.
    [Fact]
    public async Task Calls_AreMadeAsThePrincipal()
    {
        await Get("/api/mcp-apps/srv/frame?uri=ui%3A%2F%2Ft%2Fplain", "principal-a");
        await Get("/api/mcp-apps/srv/frame?uri=ui%3A%2F%2Ft%2Fplain&userId=principal-b", "principal-a");

        Assert.All(Fixture.Registry.Calls, c => Assert.Equal("principal-a", c.User));
    }

    // AC3: a view's tools/call goes through the registry as the principal, to the route's server, and returns the result.
    [Fact]
    public async Task ToolsCall_ReturnsTheResult_AndMapsRefusals()
    {
        var ok = await Post("/api/mcp-apps/srv/tools/call", "mcp-api-user", new { name = "refresh", arguments = new { page = 2 } });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("fresh", await ok.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains(("mcp-api-user", "srv", "refresh:{\"page\":2}"), Fixture.Registry.Calls);

        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/api/mcp-apps/srv/tools/call", "mcp-api-user", new { name = "model_only" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post("/api/mcp-apps/srv/tools/call", "mcp-api-user", new { name = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post("/api/mcp-apps/srv/tools/call", "mcp-api-user", new { })).StatusCode);
    }

    // AC4: resources/read serves the view as the spec's contents shape, and refuses what the registry refuses.
    [Fact]
    public async Task ResourcesRead_ReturnsTheView_AndMapsRefusals()
    {
        var ok = await Post("/api/mcp-apps/srv/resources/read", "mcp-api-user", new { uri = "ui://t/plain" });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var contents = (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("contents")[0];
        Assert.Equal("ui://t/plain", contents.GetProperty("uri").GetString());
        Assert.Equal("text/html;profile=mcp-app", contents.GetProperty("mimeType").GetString());
        Assert.Equal("<p>plain</p>", contents.GetProperty("text").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/api/mcp-apps/srv/resources/read", "mcp-api-user", new { uri = "ui://t/bad" })).StatusCode);
    }

    // AC8: the stored result is served to its owner, and to nobody else (another principal's key has no such record).
    [Fact]
    public async Task StoredResult_IsServedToItsOwnerOnly()
    {
        const string Owner = "mcp-api-owner";
        var callId = await ChatWithAppAsync(Owner, "conv");

        var mine = await Get($"/api/conversations/conv/mcp-apps/{callId}/result", Owner);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal("application/json", mine.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", Assert.Single(mine.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains(Marker, await mine.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/conversations/conv/mcp-apps/{callId}/result", "someone-else")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/conversations/conv/mcp-apps/other-call/result", Owner)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/conversations/other-conv/mcp-apps/{callId}/result", Owner)).StatusCode);
    }

    // AC8: history replays the view reference to the owner, with no raw result in it.
    [Fact]
    public async Task History_ReplaysTheViewReference_WithoutTheResult()
    {
        const string Owner = "mcp-api-history";
        await ChatWithAppAsync(Owner, "conv");

        var body = await (await Get("/api/conversations/conv/messages", Owner)).Content.ReadAsStringAsync();

        Assert.Contains("\"mcpApp\"", body, StringComparison.Ordinal);
        Assert.Contains("ui://chart/view.html", body, StringComparison.Ordinal);
        // The raw CallToolResult is a blob behind the owner-only route; history carries the reference, not the payload.
        Assert.DoesNotContain("resultJson", body, StringComparison.OrdinalIgnoreCase);
    }

    // AC17: a public share link never carries the card or its result: the tool calls are withheld wholesale (ADR 0080).
    [Fact]
    public async Task ShareLink_CarriesNoMcpApp()
    {
        const string Owner = "mcp-api-share";
        await ChatWithAppAsync(Owner, "conv");
        var created = await (await Client.SendAsync(Request(HttpMethod.Post, "/api/conversations/conv/share", Owner)))
            .Content.ReadFromJsonAsync<ShareLinkResponse>();
        var token = created!.Url!.Split('#', 2)[1];

        var body = await (await Get($"/api/share/{token}", Owner)).Content.ReadAsStringAsync();

        Assert.Contains("turns", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mcpApp", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ui://chart/view.html", body, StringComparison.Ordinal);
        Assert.DoesNotContain("resultJson", body, StringComparison.OrdinalIgnoreCase);
        // Withheld wholesale, as ADR 0080 has it: the field is present and null on every turn.
        Assert.DoesNotContain("\"toolCalls\":[", body, StringComparison.OrdinalIgnoreCase);
    }
}
