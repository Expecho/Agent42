using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Agent42.Agents;
using Agent42.Backend;
using Discord.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agent42.Tests;

/// <summary>
/// Discord's message projection (ADR 0111) over the <see cref="DiscordMessageView"/> seam: DMs, guild channels, threads,
/// mentions from Discord's own <c>mentions</c> array, and every echo shape (Spike findings (c), 2026-10-21).
/// </summary>
public class DiscordMessagesTests
{
    private const string BotId = "1100000000000000042";

    [Fact]
    public void ADirectMessage_IsTheChat()
    {
        var message = DiscordMessages.Parse(View("hello"), BotId);

        Assert.NotNull(message);
        Assert.Equal(("1300000000000000001", "1200000000000000001", ChatType.Direct, "1000000000000000007"),
            (message.PlatformMessageId, message.ChatId, message.ChatType, message.SenderId));
        Assert.Null(message.ThreadId);
        Assert.Equal("hello", message.Text);
    }

    // Step 12: a top-level mention in a guild channel is the thread the reply starts on it (the message's own id), and a
    // message inside a thread is that thread under its parent, so both land on one route.
    [Fact]
    public void AGuildMention_IsAThreadOnTheMessage_AndAThreadMessage_IsThatThread()
    {
        var topLevel = DiscordMessages.Parse(View($"<@{BotId}> /new", DiscordChannelKind.GuildChannel, mentions: [BotId]), BotId);
        var inThread = DiscordMessages.Parse(
            View($"<@!{BotId}> and also", DiscordChannelKind.Thread, mentions: [BotId]) with
            {
                Id = "1300000000000000009",
                ChannelId = "1300000000000000001",
                ParentChannelId = "1200000000000000001",
            },
            BotId);

        Assert.Equal((ChatType.Group, "1200000000000000001", "1300000000000000001", true, "/new"),
            (topLevel!.ChatType, topLevel.ChatId, topLevel.ThreadId, topLevel.MentionsBot, topLevel.Text));
        Assert.Equal(("1200000000000000001", "1300000000000000001", "and also"), (inThread!.ChatId, inThread.ThreadId, inThread.Text));
        Assert.Equal(ChatRouteKey.For(topLevel.ChatId, topLevel.ThreadId), ChatRouteKey.For(inThread.ChatId, inThread.ThreadId));
    }

    [Fact]
    public void AMention_IsDiscordsMentionsArray_NeverANameSubstring()
    {
        Assert.False(DiscordMessages.Parse(View("hey Agent42 and @agent42", DiscordChannelKind.GuildChannel), BotId)!.MentionsBot);
        Assert.False(DiscordMessages.Parse(View("<@99> hi", DiscordChannelKind.GuildChannel, mentions: ["99"]), BotId)!.MentionsBot);
        // A reply that pings the bot puts it in the mentions array; one that doesn't arrives with no content anyway.
        Assert.True(DiscordMessages.Parse(View("more please", DiscordChannelKind.GuildChannel, mentions: [BotId]), BotId)!.MentionsBot);
    }

    // AC3: our own message is dropped here; another bot or a webhook is flagged for the pipeline; system messages
    // (joins, pins, "thread created") are never turns.
    [Fact]
    public void Echoes_Bots_AndSystemMessages()
    {
        Assert.Null(DiscordMessages.Parse(View("my own reply") with { AuthorId = BotId, AuthorIsBot = true }, BotId));
        Assert.True(DiscordMessages.Parse(View("beep") with { AuthorIsBot = true }, BotId)!.SenderIsBot);
        Assert.Null(DiscordMessages.Parse(View("") with { IsUserMessage = false }, BotId));
    }

    [Fact]
    public void Attachments_RideAlong()
    {
        var file = new ChatInboundFile("https://cdn.discordapp.com/attachments/1/2/voice-message.ogg", "voice-message.ogg", "audio/ogg", 2048, true);
        Assert.Equal(file, Assert.Single(DiscordMessages.Parse(View("") with { Attachments = [file] }, BotId)!.Files));
    }

    [Theory]
    [InlineData("MTEwMDAwMDAwMDAwMDAwMDA0Mg.GhIjKl.abcdefghijklmnopqrstuvwxyz0123456789AB", true)]
    [InlineData("123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw0", false)]
    [InlineData("xoxb-1-2", false)]
    public void TokenShape(string token, bool expected) => Assert.Equal(expected, DiscordMessages.LooksLikeToken(token));

    private static DiscordMessageView View(string content, DiscordChannelKind kind = DiscordChannelKind.Direct, IReadOnlyCollection<string>? mentions = null)
        => new("1300000000000000001", "1200000000000000001", kind, null, "1000000000000000007", false, true, content, mentions ?? [], [],
            DateTimeOffset.FromUnixTimeSeconds(1792569600));
}

/// <summary>Discord renders Markdown itself; only tables, which it shows as raw pipes, become bullets.</summary>
public class DiscordTextTests
{
    [Fact]
    public void ATable_BecomesBullets_OfHeaderValuePairs()
    {
        const string Markdown = "Plans:\n| Plan | Price |\n|---|:---:|\n| Free | 0 |\n| Pro | 10 |\nDone.";

        Assert.Equal("Plans:\n• Plan: Free, Price: 0\n• Plan: Pro, Price: 10\nDone.", DiscordText.ToDiscord(Markdown));
    }

    [Fact]
    public void Fences_AndOrdinaryMarkdown_PassThrough()
    {
        const string Markdown = "**bold** _it_ ~~s~~ `c`\n```md\n| a | b |\n|---|---|\n| 1 | 2 |\n```\n> quote";

        Assert.Equal(Markdown, DiscordText.ToDiscord(Markdown));
    }

    [Fact]
    public void NeverThrows_OnGarbage()
    {
        foreach (var garbage in new[] { "|", "| a |\n|---|", "```", "|---|\n|", "\r\n", "| x |\n| --- |\n" })
        {
            Assert.NotNull(DiscordText.ToDiscord(garbage));
        }
    }
}

/// <summary>
/// The Discord session (ADR 0111): the hand-rolled REST against a local stand-in for discord.com, and the REAL
/// Discord.Net gateway client against a local stand-in gateway (Spike findings (c): with <c>GatewayHost</c> set the
/// library makes no REST call to connect).
/// </summary>
public sealed class DiscordAdapterTests : IAsyncLifetime
{
    private const string Token = "MTEwMDAwMDAwMDAwMDAwMDA0Mg.GhIjKl.abcdefghijklmnopqrstuvwxyz0123456789AB";
    private const string BotId = "1100000000000000042";
    private readonly FakeDiscordServer _server = new();
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _server.StartAsync();
        _services = Services(_server);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _server.DisposeAsync();
    }

    private DiscordAdapter Adapter => (DiscordAdapter)_services.GetServices<IChatPlatformAdapter>().Single();

    [Fact]
    public async Task Validate_ReturnsTheBot_OrRejectsTheToken()
    {
        Assert.Equal(new ChatBotIdentity(BotId, "agent42"), await Adapter.ValidateAsync(new ChatCredentials(Token), CancellationToken.None));
        Assert.Equal("Bot " + Token, _server.Requests.Single().Authorization);

        _server.Script("GET users/@me", 401, """{"message":"401: Unauthorized","code":0}""");
        await Assert.ThrowsAsync<ChatAuthException>(() => Adapter.ValidateAsync(new ChatCredentials(Token), CancellationToken.None));
        await Assert.ThrowsAsync<ChatAuthException>(() => Adapter.ValidateAsync(new ChatCredentials("garbage"), CancellationToken.None));
    }

    // AC16: every create and edit carries allowed_mentions with an empty parse list, so no reply can ping @everyone,
    // @here, a role or a user; a reply's reference never fails the send when the original is gone.
    [Fact]
    public async Task Discord_AllowedMentionsNone()
    {
        await using var session = await Adapter.ConnectAsync(Runtime(), new RecordingSink(), CancellationToken.None);

        var sent = await session.SendAsync(new ChatTarget("1200000000000000001", null, "1300000000000000001"), "@everyone **hi**", ChatTextFormat.Formatted, CancellationToken.None);
        await session.EditAsync(new ChatTarget("1200000000000000001"), sent.MessageId!, "@here edited", ChatTextFormat.Plain, CancellationToken.None);

        Assert.Equal(ChatSendResult.Ok("1400000000000000001"), sent);
        var create = _server.Requests.Single(r => r.Route == "POST channels/1200000000000000001/messages").Body;
        var edit = _server.Requests.Single(r => r.Route == "PATCH channels/1200000000000000001/messages/1400000000000000001").Body;
        foreach (var body in new[] { create, edit })
        {
            Assert.Empty(body["allowed_mentions"]!["parse"]!.AsArray());
        }

        Assert.Equal("@everyone **hi**", create["content"]!.GetValue<string>());
        Assert.Equal(("1300000000000000001", false),
            (create["message_reference"]!["message_id"]!.GetValue<string>(), create["message_reference"]!["fail_if_not_exists"]!.GetValue<bool>()));
    }

    // Step 12: the first reply to a top-level mention starts a thread ON the message and posts into it (the thread has
    // the message's id); the second reply goes straight in; nothing replies across channels to the starter message.
    [Fact]
    public async Task Send_InAGuild_StartsTheThreadOnce_AndPostsIntoIt()
    {
        await using var session = await Adapter.ConnectAsync(Runtime(), new RecordingSink(), CancellationToken.None);
        var target = new ChatTarget("1200000000000000001", "1300000000000000001", "1300000000000000001");

        await session.SendAsync(target, "one", ChatTextFormat.Plain, CancellationToken.None);
        await session.SendAsync(target, "two", ChatTextFormat.Plain, CancellationToken.None);

        Assert.Equal(
            [
                "POST channels/1200000000000000001/messages/1300000000000000001/threads",
                "POST channels/1300000000000000001/messages",
                "POST channels/1300000000000000001/messages",
            ],
            _server.Requests.Select(r => r.Route).ToArray());
        Assert.All(_server.Requests.Skip(1), r => Assert.Null(r.Body["message_reference"]));
    }

    // Critic m7: a bot that may not start threads answers in the channel, as a reply to the mention, and edits that
    // message there; it doesn't retry the failing thread on every send.
    [Fact]
    public async Task Send_WhereThreadsAreForbidden_AnswersInTheChannel()
    {
        await using var session = await Adapter.ConnectAsync(Runtime(), new RecordingSink(), CancellationToken.None);
        _server.Script("POST channels/1200000000000000001/messages/1300000000000000001/threads", 403, """{"message":"Missing Permissions","code":50013}""");
        var target = new ChatTarget("1200000000000000001", "1300000000000000001", "1300000000000000001");

        var sent = await session.SendAsync(target, "one", ChatTextFormat.Plain, CancellationToken.None);
        await session.EditAsync(target, sent.MessageId!, "one, edited", ChatTextFormat.Plain, CancellationToken.None);
        await session.SendAsync(target, "two", ChatTextFormat.Plain, CancellationToken.None);

        Assert.Equal(
            [
                "POST channels/1200000000000000001/messages/1300000000000000001/threads",
                "POST channels/1200000000000000001/messages",
                $"PATCH channels/1200000000000000001/messages/{sent.MessageId}",
                "POST channels/1200000000000000001/messages",
            ],
            _server.Requests.Select(r => r.Route).ToArray());
        Assert.Equal("1300000000000000001", _server.Requests.ElementAt(1).Body["message_reference"]!["message_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(429, """{"message":"You are being rate limited.","retry_after":2.5,"global":false}""", ChatSendErrorKind.RateLimited)]
    [InlineData(404, """{"message":"Unknown Message","code":10008}""", ChatSendErrorKind.NotFound)]
    [InlineData(403, """{"message":"Missing Permissions","code":50013}""", ChatSendErrorKind.Forbidden)]
    [InlineData(400, """{"message":"Invalid Form Body","code":50035}""", ChatSendErrorKind.TooLong)]
    [InlineData(502, "", ChatSendErrorKind.Transient)]
    public async Task Edit_MapsDiscordErrors(int status, string json, ChatSendErrorKind kind)
    {
        await using var session = await Adapter.ConnectAsync(Runtime(), new RecordingSink(), CancellationToken.None);
        _server.Script("PATCH channels/1/messages/2", status, json);

        var result = await session.EditAsync(new ChatTarget("1"), "2", "x", ChatTextFormat.Plain, CancellationToken.None);

        Assert.Equal(kind, result.ErrorKind);
        if (status == 429)
        {
            Assert.Equal(TimeSpan.FromSeconds(2.5), result.RetryAfter);
        }
    }

    // AC16: Discord.Net would reconnect forever on 4004; the session ends with a permanent fault instead, so the
    // supervisor records it and stops. The bot asked only for unprivileged intents (Guilds | GuildMessages | DMs).
    [Fact]
    public async Task Discord_4004_Faults()
    {
        _server.CloseOnIdentify = 4004;
        await using var session = await Adapter.ConnectAsync(Runtime(), new RecordingSink(), CancellationToken.None);
        session.Start();

        var fault = await Assert.ThrowsAsync<ChatAuthException>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("rejected the bot token", fault.Message, StringComparison.Ordinal);
        var identify = Assert.Single(_server.Identifies);
        Assert.Equal(Token, identify["token"]!.GetValue<string>());
        Assert.Equal(1 | (1 << 9) | (1 << 12), identify["intents"]!.GetValue<int>());
    }

    // The real library end to end: nothing connects before Start; a DM dispatched by the gateway reaches the pipeline
    // through the projection; the bot's own message does not.
    [Fact]
    public async Task Gateway_DeliversADirectMessage_ButNotTheBotsOwn()
    {
        var sink = new RecordingSink();
        await using var session = await Adapter.ConnectAsync(Runtime(), sink, CancellationToken.None);
        await Task.Delay(150);
        Assert.Empty(_server.Identifies);
        session.Start();
        await ChatGatewayServiceTests.Until(() => _server.IsReady);

        await _server.DispatchMessageAsync("1300000000000000005", BotId, "my own reply", bot: true);
        await _server.DispatchMessageAsync("1300000000000000006", "1000000000000000007", "hello from discord");

        await ChatGatewayServiceTests.Until(() => !sink.Messages.IsEmpty);
        var message = Assert.Single(sink.Messages);
        Assert.Equal(("1300000000000000006", ChatType.Direct, "hello from discord"), (message.PlatformMessageId, message.ChatType, message.Text));
        Assert.False(session.Completion.IsCompleted);
    }

    [Fact]
    public void PermanentCloseCodes_AreRecognisedAnywhereInTheChain()
    {
        Assert.True(DiscordAdapter.IsPermanent(new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "closed", new WebSocketClosedException(4004))));
        Assert.True(DiscordAdapter.IsPermanent(new WebSocketClosedException(4014)));
        Assert.False(DiscordAdapter.IsPermanent(new WebSocketClosedException(4000)));
        Assert.False(DiscordAdapter.IsPermanent(new WebSocketClosedException(1001)));
        Assert.False(DiscordAdapter.IsPermanent(null));
        Assert.Contains("intents", DiscordAdapter.PermanentReason(new WebSocketClosedException(4014)), StringComparison.Ordinal);
        Assert.Contains("close code 4011", DiscordAdapter.PermanentReason(new WebSocketClosedException(4011)), StringComparison.Ordinal);
        Assert.Contains("rejected the bot token", DiscordAdapter.PermanentReason(new WebSocketClosedException(4004)), StringComparison.Ordinal);
        // The /gateway/bot 401 path (no stand-in can reach the library's own REST client; verified from the Discord.Net
        // source, ConnectionManager raising Disconnected with the exception, and live in Step 19).
        Assert.True(DiscordAdapter.IsPermanent(new HttpException(HttpStatusCode.Unauthorized, null!)));
    }

    // AC17 (host pinning): attachments come from Discord's CDN hosts over HTTPS only, and no token is ever sent there.
    [Fact]
    public void Attachments_ArePinnedToTheCdn()
    {
        Assert.True(DiscordRestApi.IsPinnedAttachmentUrl("https://cdn.discordapp.com/attachments/1/2/a.png", null));
        Assert.True(DiscordRestApi.IsPinnedAttachmentUrl("https://media.discordapp.net/attachments/1/2/a.png", null));
        Assert.False(DiscordRestApi.IsPinnedAttachmentUrl("http://cdn.discordapp.com/a.png", null));
        Assert.False(DiscordRestApi.IsPinnedAttachmentUrl("https://cdn.discordapp.com.evil.example/a.png", null));
        Assert.False(DiscordRestApi.IsPinnedAttachmentUrl("https://discord.com/api/v10/users/@me", null));
    }

    internal static ServiceProvider Services(FakeDiscordServer server)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(new DiscordOptions(new Uri(server.BaseUrl, "api/v10/"), server.GatewayUrl));
        services.AddDiscordChat();
        return services.BuildServiceProvider();
    }

    private static ChatConnectionRuntime Runtime()
        => new("u", new ChatConnection { Id = "c", Platform = ChatPlatformKind.Discord, BotUserId = BotId }, new ChatCredentials(Token), _ => Task.CompletedTask);

    private sealed class RecordingSink : IChatInboundSink
    {
        public ConcurrentQueue<ChatInboundMessage> Messages { get; } = new();

        public Task AcceptAsync(ChatConnectionRuntime runtime, ChatInboundMessage message, CancellationToken cancellationToken)
        {
            Messages.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// A local stand-in for discord.com on real Kestrel: REST under <c>/api/v10/</c> (recorded and scripted) and a gateway
/// websocket at <c>/gateway</c> speaking just enough of the protocol for Discord.Net: Hello, Identify, Heartbeat/ACK,
/// READY and MESSAGE_CREATE, uncompressed (the library also reads plain text frames).
/// </summary>
public sealed class FakeDiscordServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(int Status, string Json)>> _scripts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private WebApplication _app = null!;
    private WebSocket? _gateway;
    private int _sequence;
    private int _messageIds;

    public ConcurrentQueue<(string Route, string Authorization, JsonObject Body)> Requests { get; } = new();

    public ConcurrentQueue<JsonObject> Identifies { get; } = new();

    /// <summary>When set, the gateway answers Identify by closing with this code instead of READY.</summary>
    public int? CloseOnIdentify { get; set; }

    public bool IsReady { get; private set; }

    public Uri BaseUrl { get; private set; } = null!;

    public string GatewayUrl => $"ws://{BaseUrl.Authority}/gateway";

    public void Script(string route, int status, string json) => _scripts.GetOrAdd(route, _ => new()).Enqueue((status, json));

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/gateway", HandleGatewayAsync);
        _app.Map("/api/v10/{**path}", HandleRestAsync);
        await _app.StartAsync();
        BaseUrl = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/");
    }

    public Task DispatchMessageAsync(string id, string authorId, string content, bool bot = false)
        => SendAsync(_gateway!, new JsonObject
        {
            ["op"] = 0,
            ["s"] = Interlocked.Increment(ref _sequence),
            ["t"] = "MESSAGE_CREATE",
            ["d"] = new JsonObject
            {
                ["id"] = id,
                ["channel_id"] = "1200000000000000001",
                ["author"] = new JsonObject { ["id"] = authorId, ["username"] = bot ? "agent42" : "owner", ["discriminator"] = "0", ["bot"] = bot },
                ["content"] = content,
                ["timestamp"] = "2026-10-21T12:00:00.000000+00:00",
                ["tts"] = false,
                ["mention_everyone"] = false,
                ["mentions"] = new JsonArray(),
                ["mention_roles"] = new JsonArray(),
                ["attachments"] = new JsonArray(),
                ["embeds"] = new JsonArray(),
                ["pinned"] = false,
                ["type"] = 0,
                ["flags"] = 0,
            },
        });

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private async Task HandleRestAsync(string path, HttpContext http)
    {
        var route = $"{http.Request.Method} {path}";
        var text = await new StreamReader(http.Request.Body).ReadToEndAsync();
        Requests.Enqueue((route, http.Request.Headers.Authorization.ToString(), text.Length > 0 ? JsonNode.Parse(text)!.AsObject() : []));
        var (status, json) = _scripts.TryGetValue(route, out var queue) && queue.TryDequeue(out var scripted)
            ? scripted
            : (200, Default(http.Request.Method, path));
        http.Response.StatusCode = status;
        if (json.Length > 0)
        {
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync(json);
        }
    }

    private string Default(string method, string path) => (method, path) switch
    {
        ("GET", "users/@me") => """{"id":"1100000000000000042","username":"agent42","discriminator":"0","bot":true}""",
        ("POST", _) when path.EndsWith("/messages", StringComparison.Ordinal)
            => $$"""{"id":"14000000000000000{{Interlocked.Increment(ref _messageIds):00}}","content":"x"}""",
        ("POST", _) when path.EndsWith("/typing", StringComparison.Ordinal) => "",
        _ => """{"id":"1"}""",
    };

    private async Task HandleGatewayAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = 400;
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        _gateway = socket;
        await SendAsync(socket, new JsonObject { ["op"] = 10, ["d"] = new JsonObject { ["heartbeat_interval"] = 41250 } });
        try
        {
            while (await ReceiveAsync(socket) is { } frame)
            {
                await HandleFrameAsync(socket, frame);
            }
        }
        catch (WebSocketException)
        {
            // The client went away.
        }
    }

    private async Task HandleFrameAsync(WebSocket socket, JsonObject frame)
    {
        switch (frame["op"]?.GetValue<int>())
        {
            case 1:
                await SendAsync(socket, new JsonObject { ["op"] = 11 });
                break;
            case 2:
                Identifies.Enqueue(frame["d"]!.AsObject());
                if (CloseOnIdentify is { } code)
                {
                    await socket.CloseOutputAsync((WebSocketCloseStatus)code, "Authentication failed.", CancellationToken.None);
                    return;
                }

                await SendAsync(socket, Ready());
                IsReady = true;
                break;
        }
    }

    private JsonObject Ready() => new()
    {
        ["op"] = 0,
        ["s"] = Interlocked.Increment(ref _sequence),
        ["t"] = "READY",
        ["d"] = new JsonObject
        {
            ["v"] = 10,
            ["user"] = new JsonObject { ["id"] = "1100000000000000042", ["username"] = "agent42", ["discriminator"] = "0", ["bot"] = true },
            ["guilds"] = new JsonArray(),
            ["private_channels"] = new JsonArray(),
            ["relationships"] = new JsonArray(),
            ["session_id"] = "session-1",
            ["resume_gateway_url"] = GatewayUrl,
            ["application"] = new JsonObject { ["id"] = "1100000000000000042", ["flags"] = 0 },
        },
    };

    private async Task SendAsync(WebSocket socket, JsonObject frame)
    {
        await _sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(frame.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<JsonObject?> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray()))?.AsObject();
            }
        }
    }
}
