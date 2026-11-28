using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// ADR 0153 through real Kestrel: the originating POST is only a reader of a detached pump, and a reattach reads the same turn.
/// Real Kestrel (not TestServer) because the claim is about a response socket that dies under the writer, like
/// <see cref="ChatStreamDisconnectTests"/>.
/// </summary>
public sealed class ChatTurnApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = ChatStreamJson.Options;

    private TestCluster _cluster = null!;
    private AppHandle _app = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
        _app = await StartAppAsync(_cluster, new Dictionary<string, string?>());
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _cluster.DisposeAsync();
    }

    private sealed class AppHandle(WebApplication app, HttpClient client, InMemoryChatTurnBlobs durable) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public ChatTurnRunner Runner { get; } = app.Services.GetRequiredService<ChatTurnRunner>();

        /// <summary>The durable store's blobs, so a test can see what was (and was not) written.</summary>
        public InMemoryChatTurnBlobs Durable { get; } = durable;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    // The host of ChatStreamDisconnectTests, with the chat-turn registration and an inspectable durable store.
    private static async Task<AppHandle> StartAppAsync(TestCluster cluster, Dictionary<string, string?> config)
    {
        var appBuilder = WebApplication.CreateSlimBuilder();
        appBuilder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, port: 0));
        appBuilder.Services.AddSingleton<IGrainFactory>(cluster.GrainFactory);
        appBuilder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog("foundry:gpt-4o",
        [
            new ModelInfo("foundry:gpt-4o", "foundry", "GPT-4o", "gpt-4o", "2024-08-06", true, true),
        ]));
        appBuilder.Services.AddSingleton<IUserModelCatalogProvider>(sp =>
            new SingleUserModelCatalogProvider(sp.GetRequiredService<IModelCatalog>()));
        appBuilder.Services.AddSingleton<IProviderSecretProtector>(
            new ProviderSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<ProviderSecretProtector>.Instance));
        appBuilder.Services.AddSingleton<IMcpSecretProtector>(
            new McpSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance));
        appBuilder.Services.AddSingleton(new McpOAuthClient(new HttpClient()));
        appBuilder.Services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
        appBuilder.Services.AddDocumentRagFakes();
        appBuilder.Services.AddSingleton(new PushConfig(Enabled: false, PublicKey: "", Subject: ""));

        var durable = new InMemoryChatTurnBlobs();
        appBuilder.Services.AddSingleton<IChatTurnBlobs>(durable);
        appBuilder.Services.AddChatTurnsInMemory(new ConfigurationBuilder().AddInMemoryCollection(config).Build());

        var app = appBuilder.Build();
        app.MapAgentApi();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new AppHandle(app, new HttpClient { BaseAddress = new Uri(address) }, durable);
    }

    private static HttpRequestMessage Post(string id, string prompt, bool ephemeral = false, string? user = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/chat/{id}")
        {
            Content = JsonContent.Create(new ChatRequest(prompt, null, Ephemeral: ephemeral)),
        };
        if (user is not null)
        {
            request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", user);
        }

        return request;
    }

    private static HttpRequestMessage Get(string url, string? user = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (user is not null)
        {
            request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", user);
        }

        return request;
    }

    private static async Task<List<AgentStreamEvent>> ReadAllAsync(HttpResponseMessage response)
    {
        var events = new List<AgentStreamEvent>();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                events.Add(JsonSerializer.Deserialize<AgentStreamEvent>(line, Json)!);
            }
        }

        return events;
    }

    private static string TextOf(IEnumerable<AgentStreamEvent> events)
        => string.Concat(events.Where(e => e.Type == AgentStreamEventType.TextDelta).Select(e => e.Text));

    private static string SlowPrompt => $"hi, {FakeChatClient.SlowStreamMarker}";

    [Fact]
    public async Task Post_AnswersWithTheTurnId_AndEveryEventCarriesASeq()
    {
        using var response = await _app.Client.SendAsync(Post("seq-conversation", "hello"), HttpCompletionOption.ResponseHeadersRead);
        var events = await ReadAllAsync(response);

        Assert.True(response.Headers.TryGetValues("X-Turn-Id", out var turnId));
        Assert.False(string.IsNullOrEmpty(turnId!.Single()));
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long?)i), events.Select(e => e.Seq));
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
    }

    [Fact]
    public async Task Reattach_AfterReset_ReplaysThenContinuesExactlyOnce()
    {
        const string id = "reattach-conversation";
        var seen = new List<AgentStreamEvent>();
        string turnId;

        // The page reads two text deltas and then loses its socket mid-reply.
        using (var response = await _app.Client.SendAsync(Post(id, SlowPrompt), HttpCompletionOption.ResponseHeadersRead))
        {
            turnId = response.Headers.GetValues("X-Turn-Id").Single();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            while (seen.Count(e => e.Type == AgentStreamEventType.TextDelta) < 2 && await reader.ReadLineAsync() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    seen.Add(JsonSerializer.Deserialize<AgentStreamEvent>(line, Json)!);
                }
            }
        }

        var lastSeen = seen[^1].Seq!.Value;

        // It comes back with the last seq it saw: the rest arrives once, in order, and the turn ends with one `done`.
        using var reattach = await _app.Client.SendAsync(
            Get($"/api/chat/{id}/turns/{turnId}/stream?after={lastSeen}"), HttpCompletionOption.ResponseHeadersRead);
        var rest = await ReadAllAsync(reattach);

        Assert.Equal(HttpStatusCode.OK, reattach.StatusCode);
        Assert.All(rest, e => Assert.True(e.Seq > lastSeen));
        Assert.Equal(rest.Select(e => e.Seq).OrderBy(s => s), rest.Select(e => e.Seq));
        Assert.Single(rest, e => e.Type == AgentStreamEventType.Done);

        var turns = await _app.Client.GetFromJsonAsync<List<ChatTurn>>($"/api/conversations/{id}/messages");
        var reply = Assert.Single(turns!, t => t.Role == "assistant");
        Assert.Equal(reply.Text, TextOf(seen) + TextOf(rest));
    }

    [Fact]
    public async Task Reattach_AfterTheTurnFinished_ReplaysThroughDone_AndTheTurnReportsCompleted()
    {
        const string id = "finished-while-away-conversation";
        using var post = await _app.Client.SendAsync(Post(id, "hello"), HttpCompletionOption.ResponseHeadersRead);
        var turnId = post.Headers.GetValues("X-Turn-Id").Single();
        var original = await ReadAllAsync(post);

        using var reattach = await _app.Client.SendAsync(Get($"/api/chat/{id}/turns/{turnId}/stream?after=0"), HttpCompletionOption.ResponseHeadersRead);
        var replay = await ReadAllAsync(reattach);

        Assert.Equal(TextOf(original), TextOf(replay));
        Assert.Single(replay, e => e.Type == AgentStreamEventType.Done);
        Assert.Equal(original[^1].Seq.ToString(), reattach.Headers.GetValues("X-Turn-Replay-Through").Single());

        var active = await _app.Client.GetFromJsonAsync<JsonElement>($"/api/chat/{id}/turn");
        Assert.Equal(turnId, active.GetProperty("turnId").GetString());
        Assert.Equal("completed", active.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Turn_IsNoContent_ForAConversationWithoutOne()
    {
        using var response = await _app.Client.SendAsync(Get("/api/chat/never-chatted-conversation/turn"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task OtherUser_Gets404()
    {
        const string id = "private-conversation";
        using var post = await _app.Client.SendAsync(Post(id, "hello", user: "alice"), HttpCompletionOption.ResponseHeadersRead);
        var turnId = post.Headers.GetValues("X-Turn-Id").Single();
        await ReadAllAsync(post);

        using var stream = await _app.Client.SendAsync(Get($"/api/chat/{id}/turns/{turnId}/stream?after=0", user: "bob"));
        using var active = await _app.Client.SendAsync(Get($"/api/chat/{id}/turn", user: "bob"));
        using var owner = await _app.Client.SendAsync(Get($"/api/chat/{id}/turns/{turnId}/stream?after=0", user: "alice"));

        Assert.Equal(HttpStatusCode.NotFound, stream.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, active.StatusCode);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    [Fact]
    public async Task Incognito_UsesMemoryStoreOnly()
    {
        const string id = "incognito-conversation";
        using var post = await _app.Client.SendAsync(Post(id, "hello", ephemeral: true), HttpCompletionOption.ResponseHeadersRead);
        var turnId = post.Headers.GetValues("X-Turn-Id").Single();
        await ReadAllAsync(post);

        Assert.Empty(await _app.Durable.ListAsync(string.Empty));

        using var withFlag = await _app.Client.SendAsync(Get($"/api/chat/{id}/turns/{turnId}/stream?after=0&ephemeral=true"));
        Assert.Equal(HttpStatusCode.OK, withFlag.StatusCode);
    }

    [Fact]
    public async Task Turn_Incognito_NotVisibleWithoutFlag()
    {
        const string id = "incognito-hidden-conversation";
        using var post = await _app.Client.SendAsync(Post(id, "hello", ephemeral: true), HttpCompletionOption.ResponseHeadersRead);
        var turnId = post.Headers.GetValues("X-Turn-Id").Single();
        await ReadAllAsync(post);

        using var stream = await _app.Client.SendAsync(Get($"/api/chat/{id}/turns/{turnId}/stream?after=0"));
        using var active = await _app.Client.SendAsync(Get($"/api/chat/{id}/turn"));
        using var activeWithFlag = await _app.Client.SendAsync(Get($"/api/chat/{id}/turn?ephemeral=true"));

        Assert.Equal(HttpStatusCode.NotFound, stream.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, active.StatusCode);
        Assert.Equal(HttpStatusCode.OK, activeWithFlag.StatusCode);
    }

    [Fact]
    public async Task DeleteConversation_PurgesTurnBlobs()
    {
        const string id = "delete-purges-conversation";
        using var post = await _app.Client.SendAsync(Post(id, "hello"), HttpCompletionOption.ResponseHeadersRead);
        await ReadAllAsync(post);
        Assert.NotEmpty(await _app.Durable.ListAsync(string.Empty));

        var delete = await _app.Client.DeleteAsync($"/api/conversations/{id}");

        Assert.True(delete.IsSuccessStatusCode);
        Assert.Empty(await _app.Durable.ListAsync(string.Empty));
    }

    // D11: purging the turn's blobs under a running turn fences its writer. The originating stream, which reads the in-process feed,
    // is unaffected and still ends with a `done`; the writer finds its log missing and recreates nothing.
    [Fact]
    public async Task DeleteWhileRunning_DoesNotRecreateBlobs()
    {
        const string id = "purge-while-running-conversation";
        using var post = await _app.Client.SendAsync(Post(id, SlowPrompt), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await post.Content.ReadAsStreamAsync());
        var events = new List<AgentStreamEvent>
        {
            JsonSerializer.Deserialize<AgentStreamEvent>((await reader.ReadLineAsync())!, Json)!,
        };

        await _app.Runner.PurgeAsync("local-dev", id);

        while (await reader.ReadLineAsync() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                events.Add(JsonSerializer.Deserialize<AgentStreamEvent>(line, Json)!);
            }
        }

        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
        // The pump has now written its terminal state or given up. Wait for it, then look at the store.
        await TestWait.WaitUntilAsync(() => _app.Runner.RunningTurns == 0, "the pump to finish");
        Assert.Empty(await _app.Durable.ListAsync(string.Empty));
    }

    // The real DELETE also purges the grain, which fails a turn running on it: the originating stream ends (completed or cut
    // short, never hangs), and either way no turn blob is left behind.
    [Fact]
    public async Task DeleteEndpointWhileRunning_LeavesNoBlobs_AndTheStreamEnds()
    {
        const string id = "delete-endpoint-while-running-conversation";
        using var post = await _app.Client.SendAsync(Post(id, SlowPrompt), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await post.Content.ReadAsStreamAsync());
        await reader.ReadLineAsync();

        var delete = await _app.Client.DeleteAsync($"/api/conversations/{id}");
        Assert.True(delete.IsSuccessStatusCode);

        try
        {
            while (await reader.ReadLineAsync() is not null)
            {
            }
        }
        catch (IOException)
        {
            // A turn that died with its grain resets the response, like it always did.
        }

        await TestWait.WaitUntilAsync(() => _app.Runner.RunningTurns == 0, "the pump to finish");
        Assert.Empty(await _app.Durable.ListAsync(string.Empty));
    }

    [Fact]
    public async Task KillSwitch_StreamsDirectly()
    {
        await using var off = await StartAppAsync(_cluster, new Dictionary<string, string?> { ["ChatTurns:Enabled"] = "false" });

        using var post = await off.Client.SendAsync(Post("kill-switch-conversation", "hello"), HttpCompletionOption.ResponseHeadersRead);
        var events = await ReadAllAsync(post);

        Assert.False(post.Headers.Contains("X-Turn-Id"));
        Assert.All(events, e => Assert.Null(e.Seq));
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
        Assert.Empty(await off.Durable.ListAsync(string.Empty));

        using var active = await off.Client.SendAsync(Get("/api/chat/kill-switch-conversation/turn"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, active.StatusCode);
    }

    [Fact]
    public async Task AHostWithoutTheRegistration_KeepsTheOldDirectStream_AndAnswers503OnTheNewRoutes()
    {
        var appBuilder = WebApplication.CreateSlimBuilder();
        appBuilder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, port: 0));
        appBuilder.Services.AddSingleton<IGrainFactory>(_cluster.GrainFactory);
        appBuilder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog("fake-model", []));
        appBuilder.Services.AddSingleton<IUserModelCatalogProvider>(sp =>
            new SingleUserModelCatalogProvider(sp.GetRequiredService<IModelCatalog>()));
        appBuilder.Services.AddSingleton<IProviderSecretProtector>(
            new ProviderSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<ProviderSecretProtector>.Instance));
        appBuilder.Services.AddSingleton<IMcpSecretProtector>(
            new McpSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance));
        appBuilder.Services.AddSingleton(new McpOAuthClient(new HttpClient()));
        appBuilder.Services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
        appBuilder.Services.AddDocumentRagFakes();
        appBuilder.Services.AddSingleton(new PushConfig(Enabled: false, PublicKey: "", Subject: ""));
        await using var app = appBuilder.Build();
        app.MapAgentApi();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        using var post = await client.SendAsync(Post("no-registration-conversation", "hello"), HttpCompletionOption.ResponseHeadersRead);
        var events = await ReadAllAsync(post);
        using var active = await client.SendAsync(Get("/api/chat/no-registration-conversation/turn"));

        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
        Assert.False(post.Headers.Contains("X-Turn-Id"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, active.StatusCode);
        _ = Encoding.UTF8;
    }
}
