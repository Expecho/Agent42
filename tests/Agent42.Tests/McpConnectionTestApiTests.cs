using System.Net;
using System.Net.Http.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The POST /api/mcp-servers/{id}/test contract (ADR 0064). Follows <see cref="SpeechApiTests"/>' shape —
/// one Orleans test cluster, a fresh MapAgentApi host per test with the tester registered or omitted — which
/// is what lets the 404 and the unconfigured-503 legs coexist without a second cluster.
/// </summary>
public sealed class McpConnectionTestApiTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private readonly IMcpSecretProtector _protector =
        new McpSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance);

    // Registers McpConnectionTester only when asked, so the endpoint's optional GetService resolves it (the
    // working paths) or returns null (the curated 503). Everything else is the param-inference floor the other
    // endpoints need (see ProvidersApiTests / SpeechApiTests).
    private async Task<(WebApplication App, HttpClient Client)> HostAsync(bool withTester)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IGrainFactory>(Cluster.GrainFactory);

        if (withTester)
        {
            builder.Services.AddSingleton<IMcpConnectionFactory>(new FakeConnectionFactory());
            builder.Services.AddSingleton(new McpOAuthTokenResolver(
                Cluster.GrainFactory, _protector, new McpOAuthClient(new HttpClient()),
                NullLogger<McpOAuthTokenResolver>.Instance));
            builder.Services.AddSingleton<McpConnectionTester>();
        }

        builder.Services.AddSingleton<IProviderSecretProtector>(
            new ProviderSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<ProviderSecretProtector>.Instance));
        builder.Services.AddSingleton(_protector);
        builder.Services.AddSingleton(new McpOAuthClient(new HttpClient()));
        builder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog("fake-model", []));
        builder.Services.AddSingleton<IUserModelCatalogProvider>(sp =>
            new SingleUserModelCatalogProvider(sp.GetRequiredService<IModelCatalog>()));
        builder.Services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
        builder.Services.AddDocumentRagFakes();
        builder.Services.AddSingleton(new PushConfig(Enabled: false, PublicKey: "", Subject: ""));

        var app = builder.Build();
        app.MapAgentApi();
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static HttpRequestMessage Post(string serverId, string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/mcp-servers/{serverId}/test");
        request.Headers.Add(IdHeader, userId);
        return request;
    }

    private StoredMcpServer Server(string id) => new(
        Id: id,
        Name: $"server-{id}",
        Endpoint: "https://example.com/mcp",
        Auth: nameof(McpAuthMode.None),
        Enabled: true);

    // The happy path end to end through the endpoint: 200 with the count and names in the payload.
    [Fact]
    public async Task Test_ReportsToolsOverTheEndpoint()
    {
        const string user = "mcp-test-api-ok";
        await Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user).SaveAsync(Server("s1"));
        var (app, client) = await HostAsync(withTester: true);
        await using var _ = app;

        var response = await client.SendAsync(Post("s1", user));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<McpServerTestResponse>();
        Assert.True(payload!.Ok);
        Assert.Equal(1, payload.ToolCount);
        Assert.Equal(["tool_s1"], payload.ToolNames);
        Assert.Equal("ok", payload.Reason);
    }

    // A reachable-but-failing test is a 200 with ok:false — the REQUEST succeeded and the diagnosis is the
    // payload (the config-health convention). Only an unknown id or an unconfigured host is an HTTP error.
    [Fact]
    public async Task Test_ReturnsOkFalseWithin200_ForAFailingServer()
    {
        const string user = "mcp-test-api-dead";
        await Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user)
            .SaveAsync(Server("dead") with { Endpoint = FakeConnectionFactory.DeadEndpoint });
        var (app, client) = await HostAsync(withTester: true);
        await using var _ = app;

        var response = await client.SendAsync(Post("dead", user));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<McpServerTestResponse>();
        Assert.False(payload!.Ok);
        Assert.Equal("could not connect", payload.Reason);
    }

    // AC9: an unknown id and ANOTHER USER'S id are deliberately indistinguishable — both 404, so the endpoint
    // never confirms that some other user owns a given server id.
    [Theory]
    [InlineData("no-such-server")]
    [InlineData("owned-by-someone-else")]
    public async Task Test_Returns404ForAnUnknownOrForeignServer(string serverId)
    {
        await Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-test-api-owner")
            .SaveAsync(Server("owned-by-someone-else"));
        var (app, client) = await HostAsync(withTester: true);
        await using var _ = app;

        var response = await client.SendAsync(Post(serverId, "mcp-test-api-stranger"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // AC10: a host with no connection factory (DevHost's shape before its offline fake) degrades to a curated
    // 503 — it must NOT break route matching for the whole host, which is why the endpoint resolves the tester
    // out of IServiceProvider rather than taking it as a parameter.
    [Fact]
    public async Task Test_Returns503WhenUnconfigured()
    {
        const string user = "mcp-test-api-503";
        await Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user).SaveAsync(Server("s1"));
        var (app, client) = await HostAsync(withTester: false);
        await using var _ = app;

        var response = await client.SendAsync(Post("s1", user));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // Routing still works on that host — the sibling MCP route resolves normally.
        var list = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/mcp-servers")
        {
            Headers = { { IdHeader, user } },
        });
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    private sealed class FakeConnectionFactory : IMcpConnectionFactory
    {
        public const string DeadEndpoint = "https://dead.example.com/mcp";

        public Task<McpServerConnection> ConnectAsync(
            StoredMcpServer server, McpConnectionSecrets secrets, CancellationToken cancellationToken)
        {
            if (server.Endpoint == DeadEndpoint)
            {
                throw new InvalidOperationException("simulated dead server");
            }

            IReadOnlyList<AITool> tools = [AIFunctionFactory.Create(() => "ok", $"tool_{server.Id}")];
            return Task.FromResult(new McpServerConnection(tools, new NoopDisposable()));
        }

        private sealed class NoopDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
