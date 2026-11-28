using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// Pins the guarantee the installed PWA's recovery path is built on: a chat turn whose client vanishes
/// mid-reply still runs to completion and lands in the conversation history.
///
/// <para>This is the everyday mobile failure. The OS freezes a backgrounded PWA and the response socket
/// can die with it, so the browser sees the stream fail — while the turn itself was fine. The grain
/// writes its session only AFTER the last event, so anything that abandoned the turn on disconnect
/// would discard a reply already paid for, and <c>recoverPersistedReply</c> in
/// <c>wwwroot/js/send.js</c> would have nothing to adopt.</para>
///
/// <para><b>Nothing in the endpoint enforces this today — that is the point.</b> Kestrel silently
/// DISCARDS writes to an aborted connection instead of faulting them (measured here: a real RST never
/// produced an exception from <c>Response.WriteAsync</c>), so the <c>await foreach</c> keeps draining on
/// its own and the turn completes. The behaviour is therefore free but load-bearing and entirely
/// implicit, and the obvious "improvement" — honouring <c>HttpContext.RequestAborted</c> so an
/// abandoned turn stops burning tokens — would silently break the recovery path with no other test
/// noticing. This is the test that notices.</para>
///
/// <para><b>Why real Kestrel and a raw socket, rather than the shared TestServer host.</b> The claim is
/// about what happens when the transport dies under the response writer, and TestServer's in-memory
/// pipe does not reproduce it at all: cancelling the client request there leaves the server none the
/// wiser, so the test would be vacuous. A loopback socket closed with <c>SO_LINGER 0</c> sends a real
/// RST, which is the production shape.</para>
/// </summary>
public sealed class ChatStreamDisconnectTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();

        var appBuilder = WebApplication.CreateSlimBuilder();
        appBuilder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port: 0));
        appBuilder.Services.AddSingleton<IGrainFactory>(_cluster.GrainFactory);
        // Everything below exists so MapAgentApi's route matcher can resolve every endpoint's service
        // parameters — an unregistered one is inferred as a body and breaks routing for the whole host
        // (ADR 0040). Only the chat and history routes are exercised here.
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
        // The pump (ADR 0153): the turn must still survive the socket dying, now because the pump is detached from the request.
        appBuilder.Services.AddChatTurnsInMemory(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        appBuilder.Services.AddSingleton(new PushConfig(Enabled: false, PublicKey: "", Subject: ""));
        _app = appBuilder.Build();
        _app.MapAgentApi();
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        _port = new Uri(address).Port;
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _cluster.DisposeAsync();
    }

    [Fact]
    public async Task Chat_PersistsTheTurn_WhenTheConnectionIsResetMidReply()
    {
        const string id = "reset-mid-reply-conversation";
        // Paced, so the reply is genuinely still arriving when the socket dies. With an instant stream
        // every event is already written and there is no mid-reply state left to lose.
        var prompt = $"hi, {FakeChatClient.SlowStreamMarker}";
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ChatRequest(prompt, null)));

        using (var socket = new TcpClient())
        {
            await socket.ConnectAsync(IPAddress.Loopback, _port);
            var stream = socket.GetStream();

            var head = $"POST /api/chat/{id} HTTP/1.1\r\nHost: 127.0.0.1\r\n"
                + $"Content-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(head));
            await stream.WriteAsync(body);
            await stream.FlushAsync();

            // Wait until the reply is demonstrably under way (the first text delta is on the wire),
            // so the reset lands in the middle of the turn rather than before or after it.
            var received = new StringBuilder();
            var buffer = new byte[256];
            while (!received.ToString().Contains("textDelta", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                Assert.NotEqual(0, read); // the server closed first: the turn was never mid-flight
                received.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            // SO_LINGER 0 turns the close into an RST — the abrupt disappearance a frozen phone
            // produces, not a polite shutdown the server could mistake for a finished read.
            socket.Client.LingerState = new LingerOption(enable: true, seconds: 0);
        }

        // Nobody is listening any more, so the turn must now run to completion on its own and land in
        // the history. Polled, because that is the whole claim: it finishes AFTER the client is gone.
        var turns = await TestWait.WaitForAsync(
            async () => (await _client.GetFromJsonAsync<List<ChatTurn>>($"/api/conversations/{id}/messages"))!,
            all => all.Any(t => t.Role == "assistant" && !string.IsNullOrWhiteSpace(t.Text)),
            "the assistant reply to land in the history");

        Assert.NotNull(turns);
        Assert.Contains(turns!, t => t.Role == "user" && t.Text == prompt);
        Assert.Contains(turns!, t => t.Role == "assistant" && !string.IsNullOrWhiteSpace(t.Text));
    }

    // AC4 (ADR 0089): the browser's socket dies while a client tool waits (a frozen PWA during a confirm). The turn
    // keeps waiting, the recovered browser finds the call and answers it over a NEW request, and the turn resumes
    // and persists with that answer.
    [Fact]
    public async Task ClientDisconnectsDuringAClientToolWait_ResultStillResumesAndPersists()
    {
        const string id = "reset-during-client-tool-conversation";
        var prompt = FakeChatClient.CallToolPrompt(ClientToolLimits.ConfirmAction, new { action = "Pay the invoice" });
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new ChatRequest(prompt, null, ClientTools: [ClientToolLimits.ConfirmAction])));

        using (var socket = new TcpClient())
        {
            await socket.ConnectAsync(IPAddress.Loopback, _port);
            var stream = socket.GetStream();

            var head = $"POST /api/chat/{id} HTTP/1.1\r\nHost: 127.0.0.1\r\n"
                + $"Content-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(head));
            await stream.WriteAsync(body);
            await stream.FlushAsync();

            // Until the flagged call is on the wire: the turn is now waiting for this browser.
            var received = new StringBuilder();
            var buffer = new byte[256];
            while (!received.ToString().Contains("\"clientTool\":true", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                Assert.NotEqual(0, read);
                received.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            socket.Client.LingerState = new LingerOption(enable: true, seconds: 0);
        }

        var pending = await _client.GetFromJsonAsync<List<PendingClientToolCall>>(
            $"/api/chat/{id}/pending-tool-calls", JsonSerializerOptions.Web);
        var call = Assert.Single(pending!);
        var post = await _client.PostAsJsonAsync(
            $"/api/chat/{id}/tool-result", new { toolCallId = call.ToolCallId, result = new { approved = true } });
        Assert.Equal(HttpStatusCode.NoContent, post.StatusCode);

        var turns = await TestWait.WaitForAsync(
            async () => (await _client.GetFromJsonAsync<List<ChatTurn>>($"/api/conversations/{id}/messages"))!,
            all => all.Any(t => t.Role == "assistant"),
            "the assistant reply to land in the history");

        var reply = Assert.Single(turns, t => t.Role == "assistant");
        Assert.Contains("approved", reply.ToolCalls![0].ToolResult, StringComparison.Ordinal);
        Assert.Contains("\"approved\":true", reply.Text, StringComparison.Ordinal);
    }
}
