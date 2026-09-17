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
using ModelContextProtocol.Protocol;

namespace Agent42.Tests;

/// <summary>
/// The management half of the local bridge (ADR 0076): a LocalBridge server needs no endpoint, its key is
/// shown exactly once, regenerate revokes the old key, and every write that revokes access also closes the
/// live socket. One cluster, a fresh <c>MapAgentApi</c> host per test with the registry registered or not —
/// the <see cref="McpConnectionTestApiTests"/> shape.
/// </summary>
public sealed class LocalBridgeApiTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private readonly IMcpSecretProtector _protector =
        new McpSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance);

    private async Task<(WebApplication App, HttpClient Client)> HostAsync(LocalBridgeRegistry? registry)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IGrainFactory>(Cluster.GrainFactory);
        if (registry is not null)
        {
            builder.Services.AddSingleton(registry);
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

    private static HttpRequestMessage Request(HttpMethod method, string uri, string userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(IdHeader, userId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static object BridgeBody(bool enabled = true, string name = "Neo", string auth = "LocalBridge") =>
        new { name, auth, enabled };

    private static async Task<McpServerResponse> CreateBridgeAsync(HttpClient client, string userId)
    {
        var response = await client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", userId, BridgeBody()));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<McpServerResponse>())!;
    }

    private ILocalBridgeKeyDirectoryGrain Directory(string raw) =>
        Cluster.GrainFactory.GetGrain<ILocalBridgeKeyDirectoryGrain>(BridgeKey.Hash(raw));

    // AC7
    [Fact]
    public async Task CreateLocalBridge_ReturnsKeyOnce()
    {
        const string user = "bridge-api-create";
        var (app, client) = await HostAsync(registry: null);
        await using var _ = app;

        var created = await CreateBridgeAsync(client, user);

        Assert.Equal("LocalBridge", created.Auth);
        Assert.Equal("", created.Endpoint);
        Assert.True(created.HasBridgeKey);
        Assert.StartsWith("sk_bridge_", created.BridgeKey);
        Assert.Equal(new LocalBridgeKeyOwner(user, created.Id), await Directory(created.BridgeKey!).ResolveAsync());

        var listed = await client.SendAsync(Request(HttpMethod.Get, "/api/mcp-servers", user));
        var raw = await listed.Content.ReadAsStringAsync();
        Assert.DoesNotContain(created.BridgeKey!, raw, StringComparison.Ordinal);
        Assert.Contains("\"hasBridgeKey\":true", raw, StringComparison.Ordinal);

        // An edit does not mint again (and so does not show a key again); the key keeps resolving.
        var renamed = await client.SendAsync(Request(HttpMethod.Put, $"/api/mcp-servers/{created.Id}", user, BridgeBody(name: "Renamed")));
        var renamedBody = (await renamed.Content.ReadFromJsonAsync<McpServerResponse>())!;
        Assert.Null(renamedBody.BridgeKey);
        Assert.True(renamedBody.HasBridgeKey);
        Assert.NotNull(await Directory(created.BridgeKey!).ResolveAsync());
    }

    // AC7: the blank endpoint is a LocalBridge exemption only.
    [Theory]
    [InlineData("None")]
    [InlineData("Header")]
    [InlineData("InteractiveOAuth")]
    public async Task NonBridge_StillRequiresEndpoint(string auth)
    {
        var (app, client) = await HostAsync(registry: null);
        await using var _ = app;

        var response = await client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", "bridge-api-nonbridge",
            new { name = "x", auth, enabled = true, headerName = "X-Key" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Endpoint", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // AC8
    [Fact]
    public async Task Regenerate_RevokesOldKey()
    {
        const string user = "bridge-api-regen";
        var (app, client) = await HostAsync(registry: null);
        await using var _ = app;
        var created = await CreateBridgeAsync(client, user);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/mcp-servers/{created.Id}/bridge-key", user));
        response.EnsureSuccessStatusCode();
        var regenerated = (await response.Content.ReadFromJsonAsync<McpServerResponse>())!;

        Assert.StartsWith("sk_bridge_", regenerated.BridgeKey);
        Assert.NotEqual(created.BridgeKey, regenerated.BridgeKey);
        Assert.Null(await Directory(created.BridgeKey!).ResolveAsync());
        Assert.NotNull(await Directory(regenerated.BridgeKey!).ResolveAsync());

        // Another user's id, an unknown id and a non-bridge server are all a uniform 404.
        var foreign = await client.SendAsync(Request(HttpMethod.Post, $"/api/mcp-servers/{created.Id}/bridge-key", "someone-else"));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var unknown = await client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers/nope/bridge-key", user));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // AC8: switching away clears (and deregisters) the key; switching back mints a fresh one.
    [Fact]
    public async Task SwitchAway_ClearsKey()
    {
        const string user = "bridge-api-switch";
        var (app, client) = await HostAsync(registry: null);
        await using var _ = app;
        var created = await CreateBridgeAsync(client, user);

        var away = await client.SendAsync(Request(HttpMethod.Put, $"/api/mcp-servers/{created.Id}", user,
            new { name = "Neo", auth = "None", enabled = true, endpoint = "http://127.0.0.1:9210/mcp" }));
        var awayBody = (await away.Content.ReadFromJsonAsync<McpServerResponse>())!;
        Assert.False(awayBody.HasBridgeKey);
        Assert.Null(await Directory(created.BridgeKey!).ResolveAsync());

        var back = await client.SendAsync(Request(HttpMethod.Put, $"/api/mcp-servers/{created.Id}", user, BridgeBody()));
        var backBody = (await back.Content.ReadFromJsonAsync<McpServerResponse>())!;
        Assert.StartsWith("sk_bridge_", backBody.BridgeKey);
        Assert.Equal("", backBody.Endpoint);
        Assert.NotNull(await Directory(backBody.BridgeKey!).ResolveAsync());
    }

    public static TheoryData<string> RevokingWrites() => ["delete", "disable", "switch-away", "regenerate"];

    // AC9: every write that revokes access closes the live socket. A rename of an enabled bridge server revokes
    // nothing and deliberately keeps it — a divergence from the plan's wording, recorded as divergence 2 in
    // .agentic/specs/completed/plan-local-browser-bridge.md.
    [Theory]
    [MemberData(nameof(RevokingWrites))]
    public async Task ManagementWrites_CloseLiveSocket(string write)
    {
        var user = $"bridge-api-evict-{write}";
        var registry = new LocalBridgeRegistry();
        var (app, client) = await HostAsync(registry);
        await using var _ = app;
        var created = await CreateBridgeAsync(client, user);
        var socket = new RecordingConnection();
        registry.Register(user, created.Id, BridgeKey.Hash(created.BridgeKey!), socket);

        // A rename first: must NOT close.
        (await client.SendAsync(Request(HttpMethod.Put, $"/api/mcp-servers/{created.Id}", user, BridgeBody(name: "Renamed"))))
            .EnsureSuccessStatusCode();
        Assert.Equal(0, socket.Closes);

        var response = await client.SendAsync(write switch
        {
            "delete" => Request(HttpMethod.Delete, $"/api/mcp-servers/{created.Id}", user),
            "disable" => Request(HttpMethod.Put, $"/api/mcp-servers/{created.Id}", user, BridgeBody(enabled: false)),
            "switch-away" => Request(HttpMethod.Put, $"/api/mcp-servers/{created.Id}", user,
                new { name = "Neo", auth = "None", enabled = true, endpoint = "http://127.0.0.1:9210/mcp" }),
            _ => Request(HttpMethod.Post, $"/api/mcp-servers/{created.Id}/bridge-key", user),
        });

        response.EnsureSuccessStatusCode();
        Assert.Equal(1, socket.Closes);
        Assert.Null(registry.FindByKeyHash(BridgeKey.Hash(created.BridgeKey!)));
    }

    // AC10: on a host with no registry (most MapAgentApi hosts), every write still succeeds.
    [Fact]
    public async Task ManagementWrites_WithoutRegistry_Succeed()
    {
        const string user = "bridge-api-noregistry";
        var (app, client) = await HostAsync(registry: null);
        await using var _ = app;
        var created = await CreateBridgeAsync(client, user);

        (await client.SendAsync(Request(HttpMethod.Put, $"/api/mcp-servers/{created.Id}", user, BridgeBody(enabled: false))))
            .EnsureSuccessStatusCode();
        (await client.SendAsync(Request(HttpMethod.Post, $"/api/mcp-servers/{created.Id}/bridge-key", user)))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.SendAsync(Request(HttpMethod.Delete, $"/api/mcp-servers/{created.Id}", user))).StatusCode);
    }

    private sealed class RecordingConnection : ILocalBridgeConnection
    {
        public int Closes { get; private set; }

        public Task<ITransport> OpenChannelAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CloseAsync(string reason)
        {
            Closes++;
            return Task.CompletedTask;
        }
    }
}
