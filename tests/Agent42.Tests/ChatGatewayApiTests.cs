using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// <see cref="SharedApiFixture"/> with the chat gateway's endpoint services registered against a fake platform and a
/// recording gateway control (ADR 0111). The base fixture registers none, which is the 503 case.
/// </summary>
public sealed class ChatGatewayApiFixture : SharedApiFixture
{
    public FakeChatPlatformAdapter Adapter { get; } = new();

    public RecordingChatGatewayControl Control { get; } = new();

    protected override void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IChatGatewaySecretProtector>(new ChatGatewaySecretProtector(
            new EphemeralDataProtectionProvider(), NullLogger<ChatGatewaySecretProtector>.Instance));
        builder.Services.AddSingleton<IChatPlatformAdapter>(Adapter);
        builder.Services.AddSingleton<IChatGatewayControl>(Control);
    }
}

/// <summary>
/// The /api/chat-connections contract (ADR 0111): credentials are validated on save and never echoed, a bad token
/// changes nothing, the pairing code is shown while pairing is open, every save restarts the session, and a delete
/// leaves the run list before the session is stopped.
/// </summary>
public sealed class ChatGatewayApiTests(ChatGatewayApiFixture fixture) : SharedClusterTests<ChatGatewayApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";
    private const string GoodToken = "1234567:good-token-value";

    private HttpClient Client => Fixture.Client;

    [Fact]
    public async Task Create_WithCredentials_ValidatesStoresAndOpensPairing()
    {
        var user = NewUser();

        var created = await SendAsync(HttpMethod.Post, "/api/chat-connections", user,
            new { platform = "Telegram", name = "Phone", credentials = new { botToken = GoodToken } });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var view = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ChatConnectionStatus.AwaitingPairing, view.GetProperty("status").GetString());
        Assert.True(view.GetProperty("hasCredentials").GetBoolean());
        Assert.Equal(FakeChatPlatformAdapter.BotUserName, view.GetProperty("botUserName").GetString());
        Assert.Matches("^[A-Z2-9]{10}$", view.GetProperty("pairingCode").GetString());
        Assert.Contains($"restart:{view.GetProperty("id").GetString()}", Fixture.Control.Calls);
    }

    // AC12: the token is write-only. Neither it nor its ciphertext ever appears in a response.
    [Fact]
    public async Task Get_NeverEchoesCredentials()
    {
        var user = NewUser();
        await SendAsync(HttpMethod.Post, "/api/chat-connections", user,
            new { platform = "Telegram", name = "Phone", credentials = new { botToken = GoodToken } });

        var list = await SendAsync(HttpMethod.Get, "/api/chat-connections", user);
        var raw = await list.Content.ReadAsStringAsync();

        Assert.DoesNotContain(GoodToken, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("ciphertext", raw, StringComparison.OrdinalIgnoreCase);
        var json = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(["Telegram"], json.GetProperty("platforms").EnumerateArray().Select(p => p.GetString()));
        Assert.Single(json.GetProperty("connections").EnumerateArray());
    }

    // AC12: a token the platform rejects is a 400 and changes nothing; an unreachable platform is a 502.
    [Fact]
    public async Task Update_WithRejectedToken_Returns400AndKeepsTheOldCredentials()
    {
        var user = NewUser();
        var id = await CreateAsync(user, GoodToken);

        var rejected = await SendAsync(HttpMethod.Put, $"/api/chat-connections/{id}", user,
            new { name = "Renamed", credentials = new { botToken = FakeChatPlatformAdapter.BadToken } });
        var unreachable = await SendAsync(HttpMethod.Put, $"/api/chat-connections/{id}", user,
            new { name = "Renamed", credentials = new { botToken = FakeChatPlatformAdapter.UnreachableToken } });

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("The platform rejected that bot token.", await rejected.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadGateway, unreachable.StatusCode);
        var stored = await Connections(user).GetAsync(id);
        Assert.Equal("Phone", stored!.Name);
        Assert.NotNull(stored.CredentialsCiphertext);
    }

    // Found in the DevHost exercise: a create with a rejected token used to leave a half-made bot in the list.
    [Fact]
    public async Task Create_WithRejectedToken_CreatesNothing()
    {
        var user = NewUser();

        var rejected = await SendAsync(HttpMethod.Post, "/api/chat-connections", user,
            new { platform = "Telegram", name = "x", credentials = new { botToken = FakeChatPlatformAdapter.BadToken } });

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Empty(await Connections(user).ListAsync());
    }

    [Theory]
    [InlineData("Myspace")]
    [InlineData("Slack")] // a known platform with no adapter registered in this deployment
    [InlineData(null)]
    public async Task Create_UnsupportedPlatform_Returns400(string? platform)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/chat-connections", NewUser(), new { platform, name = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithSomeoneElsesAgent_Returns400()
    {
        var response = await SendAsync(HttpMethod.Post, "/api/chat-connections", NewUser(),
            new { platform = "Telegram", name = "x", targetAgentId = "no-such-agent" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Repair_OpensANewPairingWindow()
    {
        var user = NewUser();
        var id = await CreateAsync(user, GoodToken);
        var before = (await Connections(user).GetAsync(id))!.PairingCode;

        var repaired = await SendAsync(HttpMethod.Post, $"/api/chat-connections/{id}/repair", user);

        repaired.EnsureSuccessStatusCode();
        var code = (await repaired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pairingCode").GetString();
        Assert.NotEqual(before, code);
    }

    // AC13c: delete leaves the run list (the grain deregisters first), then the live session is stopped.
    [Fact]
    public async Task Delete_DeregistersThenStopsTheSession()
    {
        var user = NewUser();
        var id = await CreateAsync(user, GoodToken);

        var deleted = await SendAsync(HttpMethod.Delete, $"/api/chat-connections/{id}", user);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Contains($"stop:{id}", Fixture.Control.Calls);
        var runList = await Cluster.GrainFactory.GetGrain<IChatConnectionDirectoryGrain>(IChatConnectionDirectoryGrain.Key).ListAsync();
        Assert.DoesNotContain(new ChatConnectionRef(user, id), runList);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/api/chat-connections/{id}", user)).StatusCode);
    }

    [Fact]
    public async Task AnotherUsersConnection_Is404()
    {
        var id = await CreateAsync(NewUser(), GoodToken);
        var stranger = NewUser();

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"/api/chat-connections/{id}", stranger, new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/api/chat-connections/{id}/repair", stranger)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/api/chat-connections/{id}", stranger)).StatusCode);
    }

    [Fact]
    public void Protector_PurposeIsIsolatedFromTheOtherSecretFamilies()
    {
        var provider = new EphemeralDataProtectionProvider();
        var chat = new ChatGatewaySecretProtector(provider, NullLogger<ChatGatewaySecretProtector>.Instance);
        var webhook = new WebhookSecretProtector(provider, NullLogger<WebhookSecretProtector>.Instance);

        var ciphertext = chat.Protect(new ChatCredentials("bot", "app"));

        Assert.Equal(new ChatCredentials("bot", "app"), chat.Unprotect(ciphertext));
        Assert.Null(chat.Unprotect(webhook.Protect("secret")));
        Assert.Null(webhook.Unprotect(ciphertext));
        Assert.Null(chat.Unprotect(null));
    }

    private async Task<string> CreateAsync(string user, string token)
    {
        var created = await SendAsync(HttpMethod.Post, "/api/chat-connections", user,
            new { platform = "Telegram", name = "Phone", credentials = new { botToken = token } });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private IUserChatConnectionsGrain Connections(string user) => Cluster.GrainFactory.GetGrain<IUserChatConnectionsGrain>(user);

    private static string NewUser() => "chat-api-" + Guid.NewGuid().ToString("N");

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string user, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(IdHeader, user);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return Client.SendAsync(request);
    }
}

/// <summary>A host without <c>AddChatGateway</c> still routes, and the chat endpoints answer 503.</summary>
public sealed class ChatGatewayApiAbsentTests(SharedApiFixture fixture) : SharedClusterTests<SharedApiFixture>(fixture)
{
    [Fact]
    public async Task WithoutTheGateway_Returns503()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/chat-connections");
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", "chat-absent-user");

        var response = await Fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
