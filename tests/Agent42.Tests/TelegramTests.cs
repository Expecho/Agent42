using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Agent42.Tests;

/// <summary>
/// Telegram's update projection (ADR 0111), against fixtures built from the documented Bot API field names (Step 1
/// spike, 2026-10-21): DMs, groups, mentions by entity, replies to the bot, captions, files and voice notes.
/// </summary>
public class TelegramUpdatesTests
{
    private const string BotId = "7000000001";
    private const string BotName = "agent42_test_bot";

    [Fact]
    public void APrivateTextMessage_IsADirectMessage()
    {
        var message = Parse("""{"update_id":500000001,"message":{"message_id":10,"from":{"id":111111111,"is_bot":false,"first_name":"Peter"},"chat":{"id":111111111,"type":"private"},"date":1792569600,"text":"What's on my calendar today?"}}""");

        Assert.NotNull(message);
        Assert.Equal(("10", "111111111", ChatType.Direct, "111111111"), (message.PlatformMessageId, message.ChatId, message.ChatType, message.SenderId));
        Assert.Equal("What's on my calendar today?", message.Text);
        Assert.False(message.SenderIsBot);
        Assert.False(message.MentionsBot);
        Assert.Null(message.ThreadId);
        Assert.Empty(message.Files);
    }

    [Fact]
    public void AGroupMention_IsFoundByEntity_NotBySubstring()
    {
        var mentioned = Parse("""{"update_id":2,"message":{"message_id":311,"from":{"id":222222222,"is_bot":false,"first_name":"Sam"},"chat":{"id":-1001234567890,"type":"supergroup","title":"T"},"date":1792569660,"text":"@agent42_test_bot summarize the thread","entities":[{"type":"mention","offset":0,"length":17}]}}""");
        var textOnly = Parse("""{"update_id":3,"message":{"message_id":312,"from":{"id":222222222,"is_bot":false,"first_name":"Sam"},"chat":{"id":-1001234567890,"type":"supergroup","title":"T"},"date":1792569660,"text":"ask @agent42_test_bot later"}}""");
        var someoneElse = Parse("""{"update_id":4,"message":{"message_id":313,"from":{"id":222222222,"is_bot":false,"first_name":"Sam"},"chat":{"id":-1001234567890,"type":"supergroup","title":"T"},"date":1792569660,"text":"@other_bot hi","entities":[{"type":"mention","offset":0,"length":10}]}}""");

        Assert.Equal((ChatType.Group, true), (mentioned!.ChatType, mentioned.MentionsBot));
        Assert.False(textOnly!.MentionsBot);
        Assert.False(someoneElse!.MentionsBot);
    }

    [Fact]
    public void AReplyToTheBot_AndATextMention_Count()
    {
        var reply = Parse("""{"update_id":5,"message":{"message_id":313,"from":{"id":111111111,"is_bot":false,"first_name":"P"},"chat":{"id":-100,"type":"supergroup","title":"T"},"date":1,"text":"Expand on point 2?","reply_to_message":{"message_id":312,"from":{"id":7000000001,"is_bot":true,"first_name":"Agent 42"},"chat":{"id":-100,"type":"supergroup"},"date":1,"text":"Summary"}}}""");
        var textMention = Parse("""{"update_id":6,"message":{"message_id":314,"from":{"id":111111111,"is_bot":false,"first_name":"P"},"chat":{"id":-100,"type":"group","title":"T"},"date":1,"text":"Agent 42 hi","entities":[{"type":"text_mention","offset":0,"length":8,"user":{"id":7000000001,"is_bot":true,"first_name":"Agent 42"}}]}}""");
        var command = Parse("""{"update_id":7,"message":{"message_id":315,"from":{"id":111111111,"is_bot":false,"first_name":"P"},"chat":{"id":-100,"type":"group","title":"T"},"date":1,"text":"/new@agent42_test_bot","entities":[{"type":"bot_command","offset":0,"length":21}]}}""");

        Assert.True(reply!.RepliesToBot);
        Assert.True(textMention!.MentionsBot);
        Assert.True(command!.MentionsBot);
    }

    [Fact]
    public void APhotoWithACaption_TakesTheLargestSize_AndTheCaptionAsText()
    {
        var message = Parse("""{"update_id":8,"message":{"message_id":11,"from":{"id":111111111,"is_bot":false,"first_name":"P"},"chat":{"id":111111111,"type":"private"},"date":1,"photo":[{"file_id":"small","file_unique_id":"a","width":90,"height":68,"file_size":1450},{"file_id":"large","file_unique_id":"c","width":1280,"height":960,"file_size":184220}],"caption":"What plant is this?"}}""");

        var file = Assert.Single(message!.Files);
        Assert.Equal(("large", "image/jpeg", 184220L, false), (file.FileRef, file.DeclaredMediaType, file.Size, file.IsVoiceNote));
        Assert.Equal("What plant is this?", message.Text);
    }

    [Fact]
    public void ADocument_AndAVoiceNote_AreFiles()
    {
        var document = Parse("""{"update_id":9,"message":{"message_id":12,"from":{"id":111111111,"is_bot":false,"first_name":"P"},"chat":{"id":111111111,"type":"private"},"date":1,"document":{"file_id":"BQAC","file_unique_id":"x","file_name":"invoice-2026-10.pdf","mime_type":"application/pdf","file_size":248331},"caption":"Extract the total"}}""");
        var voice = Parse("""{"update_id":10,"message":{"message_id":13,"from":{"id":111111111,"is_bot":false,"first_name":"P"},"chat":{"id":111111111,"type":"private"},"date":1,"voice":{"file_id":"AwAC","file_unique_id":"v","duration":7,"mime_type":"audio/ogg","file_size":28410}}}""");

        Assert.Equal(("invoice-2026-10.pdf", "application/pdf", false), (document!.Files[0].FileName, document.Files[0].DeclaredMediaType, document.Files[0].IsVoiceNote));
        Assert.Equal(("voice.ogg", "audio/ogg", true), (voice!.Files[0].FileName, voice.Files[0].DeclaredMediaType, voice.Files[0].IsVoiceNote));
        Assert.Equal("", voice.Text);
    }

    [Theory]
    [InlineData("""{"update_id":11,"edited_message":{"message_id":1,"from":{"id":1,"is_bot":false,"first_name":"P"},"chat":{"id":1,"type":"private"},"date":1,"text":"edited"}}""")]
    [InlineData("""{"update_id":12,"message":{"message_id":1,"chat":{"id":-100,"type":"supergroup"},"date":1,"text":"an anonymous admin has no from"}}""")]
    [InlineData("""{"update_id":13,"message":{"message_id":1,"from":{"id":1,"is_bot":false,"first_name":"P"},"chat":{"id":-100,"type":"channel"},"date":1,"text":"a channel post"}}""")]
    public void NotATurn(string json) => Assert.Null(Parse(json));

    [Fact]
    public void ABotSender_IsFlagged()
        => Assert.True(Parse("""{"update_id":14,"message":{"message_id":1,"from":{"id":5,"is_bot":true,"first_name":"B"},"chat":{"id":1,"type":"private"},"date":1,"text":"x"}}""")!.SenderIsBot);

    [Fact]
    public void AForumTopic_IsAThread()
        => Assert.Equal("77", Parse("""{"update_id":15,"message":{"message_id":1,"message_thread_id":77,"is_topic_message":true,"from":{"id":1,"is_bot":false,"first_name":"P"},"chat":{"id":-100,"type":"supergroup"},"date":1,"text":"x"}}""")!.ThreadId);

    [Theory]
    [InlineData("123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw0", true)]
    [InlineData("not a token", false)]
    [InlineData("123:short", false)]
    public void TokenShape(string token, bool expected) => Assert.Equal(expected, TelegramUpdates.LooksLikeToken(token));

    private static ChatInboundMessage? Parse(string json) => TelegramUpdates.Parse(JsonDocument.Parse(json).RootElement, BotId, BotName);
}

/// <summary>Markdown to Telegram HTML (ADR 0111): escaping is total, only paired markers convert, and nothing throws.</summary>
public class TelegramTextTests
{
    [Theory]
    [InlineData("**bold** and __also__", "<b>bold</b> and <b>also</b>")]
    [InlineData("*it* and _it_", "<i>it</i> and <i>it</i>")]
    [InlineData("~~gone~~", "<s>gone</s>")]
    [InlineData("a < b && c > d", "a &lt; b &amp;&amp; c &gt; d")]
    [InlineData("<script>alert(1)</script>", "&lt;script&gt;alert(1)&lt;/script&gt;")]
    [InlineData("[docs](https://example.com/a?b=1&c=2)", "<a href=\"https://example.com/a?b=1&amp;c=2\">docs</a>")]
    [InlineData("# Title", "<b>Title</b>")]
    [InlineData("- one\n- two", "• one\n• two")]
    [InlineData("> quoted\n> lines", "<blockquote>quoted\nlines</blockquote>")]
    [InlineData("use `a<b>` here", "use <code>a&lt;b&gt;</code> here")]
    [InlineData("snake_case_name stays", "snake_case_name stays")]
    [InlineData("an **unbalanced marker", "an **unbalanced marker")]
    [InlineData("[not a link](javascript:alert(1))", "[not a link](javascript:alert(1))")]
    public void Converts(string markdown, string html) => Assert.Equal(html, TelegramText.ToHtml(markdown));

    [Fact]
    public void AFencedBlock_KeepsItsLanguage_AndIsNotFormattedInside()
    {
        var html = TelegramText.ToHtml("Run:\n```python\nprint(\"**x** < 1\")\n```\nDone.");

        Assert.Equal("Run:\n<pre><code class=\"language-python\">print(&quot;**x** &lt; 1&quot;)</code></pre>\nDone.", html);
    }

    // Found in the DevHost exercise: model output with \r\n left "<b>Title\r</b>" in the message.
    [Fact]
    public void WindowsLineEndings_AreNormalised()
        => Assert.Equal("<b>The Answer</b>\n\nbody", TelegramText.ToHtml("## The Answer\r\n\r\nbody"));

    [Fact]
    public void NeverThrows_OnGarbage()
    {
        var garbage = "```\n**_~~[](<>`" + new string('*', 50) + "\n> > >\n```";

        var html = TelegramText.ToHtml(garbage);

        Assert.DoesNotContain("<script", html);
    }
}

/// <summary>
/// The Telegram adapter against a local fake Bot API on real Kestrel (ADR 0111): token validation, the long poll and
/// its committed cursor, the permanent 409/401 failures, send/edit shapes and error mapping, and the bot token kept
/// out of spans and logs.
/// </summary>
public sealed class TelegramAdapterTests : IAsyncLifetime
{
    private const string Token = "123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw0";
    private readonly FakeTelegramServer _server = new();
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _server.StartAsync();
        _services = Services(_server.BaseUrl, logging: null);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _server.DisposeAsync();
    }

    private TelegramAdapter Adapter => (TelegramAdapter)_services.GetServices<IChatPlatformAdapter>().Single();

    [Fact]
    public async Task Validate_ReturnsTheBot_OrRejectsTheToken()
    {
        var identity = await Adapter.ValidateAsync(new ChatCredentials(Token), CancellationToken.None);
        Assert.Equal(new ChatBotIdentity("7000000001", "agent42_test_bot"), identity);

        _server.Script("getMe", 401, """{"ok":false,"error_code":401,"description":"Unauthorized"}""");
        await Assert.ThrowsAsync<ChatAuthException>(() => Adapter.ValidateAsync(new ChatCredentials(Token), CancellationToken.None));
        await Assert.ThrowsAsync<ChatAuthException>(() => Adapter.ValidateAsync(new ChatCredentials("garbage"), CancellationToken.None));
    }

    // AC14: updates reach the pipeline, and the cursor is committed past them; the next poll confirms them.
    [Fact]
    public async Task Poll_DeliversUpdates_AndCommitsTheCursor()
    {
        _server.Script("getUpdates", 200, """{"ok":true,"result":[{"update_id":41,"message":{"message_id":1,"from":{"id":111,"is_bot":false,"first_name":"P"},"chat":{"id":111,"type":"private"},"date":1,"text":"one"}},{"update_id":42,"channel_post":{}}]}""");
        var sink = new RecordingSink();
        var commits = new ConcurrentQueue<long>();

        await using var session = await Adapter.ConnectAsync(Runtime(offset: 40, commits), sink, CancellationToken.None);
        await Task.Delay(150);
        // Nothing is polled until the supervisor has registered the session and calls Start.
        Assert.DoesNotContain(_server.Requests, r => r.Method == "getUpdates");
        session.Start();

        await TestWait.WaitUntilAsync(() => commits.Contains(43) && _server.Requests.Count(r => r.Method == "getUpdates") >= 2);
        Assert.Equal("one", Assert.Single(sink.Messages).Text);
        var polls = _server.Requests.Where(r => r.Method == "getUpdates").Select(r => r.Body["offset"]!.GetValue<long>()).ToList();
        Assert.Equal([40, 43], polls.Take(2));
        // Connect removed any webhook WITHOUT dropping pending updates, and set the command menu.
        Assert.False(_server.Requests.Single(r => r.Method == "deleteWebhook").Body["drop_pending_updates"]!.GetValue<bool>());
        Assert.Contains(_server.Requests, r => r.Method == "setMyCommands");
    }

    // A poison update (here: a message with no message_id) is skipped and the cursor still moves past it, so it cannot
    // crash-loop the session on every reconnect.
    [Fact]
    public async Task Poll_SkipsAPoisonUpdate_AndKeepsGoing()
    {
        _server.Script("getUpdates", 200, """{"ok":true,"result":[{"update_id":50,"message":{"from":{"id":111,"is_bot":false,"first_name":"P"},"chat":{"id":111,"type":"private"},"date":1,"text":"broken"}},{"update_id":51,"message":{"message_id":2,"from":{"id":111,"is_bot":false,"first_name":"P"},"chat":{"id":111,"type":"private"},"date":1,"text":"fine"}}]}""");
        var sink = new RecordingSink();
        var commits = new ConcurrentQueue<long>();

        await using var session = await Adapter.ConnectAsync(Runtime(offset: 50, commits), sink, CancellationToken.None);
        session.Start();

        await TestWait.WaitUntilAsync(() => commits.Contains(52));
        Assert.Equal("fine", Assert.Single(sink.Messages).Text);
        Assert.False(session.Completion.IsCompleted);
    }

    // AC14: a second poller (409) and a dead token (401) end the session with a permanent failure.
    [Theory]
    [InlineData(409, "Another program is reading this bot's messages")]
    [InlineData(401, "Telegram rejected the bot token")]
    public async Task Poll_PermanentFailure_FaultsTheSession(int status, string reason)
    {
        _server.Script("getUpdates", status, $$"""{"ok":false,"error_code":{{status}},"description":"x"}""");

        await using var session = await Adapter.ConnectAsync(Runtime(), new RecordingSink(), CancellationToken.None);
        session.Start();

        var failure = await Assert.ThrowsAsync<ChatAuthException>(() => session.Completion);
        Assert.StartsWith(reason, failure.Message);
    }

    [Fact]
    public async Task Send_FormattedAsHtml_InTheThread_AsAReply()
    {
        await using var session = await Adapter.ConnectAsync(Runtime(), new RecordingSink(), CancellationToken.None);
        _server.Script("sendMessage", 200, """{"ok":true,"result":{"message_id":99}}""");

        var result = await session.SendAsync(new ChatTarget("-100", "77", "313"), "**hi** <b>", ChatTextFormat.Formatted, CancellationToken.None);

        Assert.Equal(ChatSendResult.Ok("99"), result);
        var body = _server.Requests.Last(r => r.Method == "sendMessage").Body;
        Assert.Equal(("-100", 77L, "HTML", "<b>hi</b> &lt;b&gt;"),
            (body["chat_id"]!.GetValue<string>(), body["message_thread_id"]!.GetValue<long>(), body["parse_mode"]!.GetValue<string>(), body["text"]!.GetValue<string>()));
        Assert.Equal(313L, body["reply_parameters"]!["message_id"]!.GetValue<long>());
    }

    [Theory]
    [InlineData(429, """{"ok":false,"error_code":429,"description":"Too Many Requests: retry after 7","parameters":{"retry_after":7}}""", ChatSendErrorKind.RateLimited)]
    [InlineData(400, """{"ok":false,"error_code":400,"description":"Bad Request: can't parse entities: unsupported start tag"}""", ChatSendErrorKind.BadFormat)]
    [InlineData(400, """{"ok":false,"error_code":400,"description":"Bad Request: message to edit not found"}""", ChatSendErrorKind.NotFound)]
    [InlineData(403, """{"ok":false,"error_code":403,"description":"Forbidden: bot was blocked by the user"}""", ChatSendErrorKind.Forbidden)]
    [InlineData(502, """{"ok":false,"error_code":502,"description":"Bad Gateway"}""", ChatSendErrorKind.Transient)]
    [InlineData(400, """{"ok":false,"error_code":400,"description":"Bad Request: message is not modified"}""", ChatSendErrorKind.None)]
    public async Task Edit_MapsTelegramErrors(int status, string json, ChatSendErrorKind kind)
    {
        await using var session = await Adapter.ConnectAsync(Runtime(), new RecordingSink(), CancellationToken.None);
        _server.Script("editMessageText", status, json);

        var result = await session.EditAsync(new ChatTarget("111"), "99", "x", ChatTextFormat.Plain, CancellationToken.None);

        Assert.Equal(kind, result.ErrorKind);
        if (kind == ChatSendErrorKind.RateLimited)
        {
            Assert.Equal(TimeSpan.FromSeconds(7), result.RetryAfter);
        }
    }

    // AC17: getFile resolves the path, and the read is capped; a file getFile says is too large is never fetched.
    [Fact]
    public async Task Download_ResolvesThePath_AndNeverFetchesAnOversizedFile()
    {
        await using var session = await Adapter.ConnectAsync(Runtime(), new RecordingSink(), CancellationToken.None);
        _server.Files["documents/file_1.pdf"] = [1, 2, 3, 4, 5];
        _server.Script("getFile", 200, """{"ok":true,"result":{"file_id":"F1","file_path":"documents/file_1.pdf","file_size":5}}""");
        _server.Script("getFile", 200, """{"ok":true,"result":{"file_id":"F2","file_path":"documents/big.pdf","file_size":5000}}""");

        var small = await session.DownloadAsync(new ChatInboundFile("F1", "guide.pdf", "application/pdf", 5, false), 100, CancellationToken.None);
        var big = await session.DownloadAsync(new ChatInboundFile("F2", "big.pdf", "application/pdf", null, false), 100, CancellationToken.None);

        Assert.Equal([1, 2, 3, 4, 5], small!.Bytes);
        Assert.Equal("guide.pdf", small.FileName);
        Assert.Null(big);
        Assert.Equal(["documents/file_1.pdf"], _server.FileRequests);
        Assert.Equal("F1", _server.Requests.First(r => r.Method == "getFile").Body["file_id"]!.GetValue<string>());
    }

    // AC17: a generated image goes out as a photo in the same chat, topic and reply chain as the text.
    [Fact]
    public async Task UploadImage_SendsAPhoto_InTheThread_AsAReply()
    {
        await using var session = await Adapter.ConnectAsync(Runtime(), new RecordingSink(), CancellationToken.None);
        _server.Script("sendPhoto", 200, """{"ok":true,"result":{"message_id":120}}""");

        var result = await session.UploadImageAsync(new ChatTarget("-100", "77", "313"), [1, 2, 3], "chart.png", "image/png", CancellationToken.None);

        Assert.Equal(ChatSendResult.Ok("120"), result);
        var body = _server.Requests.Last(r => r.Method == "sendPhoto").Body;
        Assert.Equal(("-100", "77", "chart.png", 3L),
            (body["chat_id"]!.GetValue<string>(), body["message_thread_id"]!.GetValue<string>(),
                body["photo.filename"]!.GetValue<string>(), body["photo.length"]!.GetValue<long>()));
        Assert.Equal(313L, JsonNode.Parse(body["reply_parameters"]!.GetValue<string>())!["message_id"]!.GetValue<long>());
    }

    internal static ServiceProvider Services(Uri baseUrl, CapturingLoggerProvider? logging)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            if (logging is not null)
            {
                builder.AddProvider(logging);
            }
        });
        services.AddSingleton(new TelegramOptions(baseUrl));
        services.AddTelegramChat();
        services.AddHttpClient("telegram-control");
        return services.BuildServiceProvider();
    }

    private static ChatConnectionRuntime Runtime(long offset = 0, ConcurrentQueue<long>? commits = null)
        => new("u", new ChatConnection { Id = "c", BotUserId = "7000000001", BotUserName = "agent42_test_bot", PollOffset = offset },
            new ChatCredentials(Token), o =>
            {
                commits?.Enqueue(o);
                return Task.CompletedTask;
            });

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
/// Runs alone: an <c>ActivityListener</c> is process-global, so a span-capture test shares its view with every class
/// running at the same time (the <see cref="LiveTelemetryCollection"/> lesson). <c>DisableParallelization</c> runs this
/// collection after everything else, with nothing beside it.
/// </summary>
[CollectionDefinition(nameof(TelegramTokenLeakCollection), DisableParallelization = true)]
public sealed class TelegramTokenLeakCollection;

/// <summary>AC12b (ADR 0111): the Telegram bot token never reaches a span or a log record.</summary>
[Collection(nameof(TelegramTokenLeakCollection))]
public sealed class TelegramTokenLeakTests : IAsyncLifetime
{
    private const string Token = "123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw0";
    private readonly FakeTelegramServer _server = new();

    public Task InitializeAsync() => _server.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    // The token rides in the URL PATH, which OTel never redacts. With the production client wiring no span and no log
    // record carries it. The control client (no suppression, default logging) proves the probe would see it.
    [Fact]
    public async Task TelegramBotApi_NeverLeaksTheToken()
    {
        var spans = new ConcurrentQueue<Activity>();
        var logs = new CapturingLoggerProvider();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddHttpClientInstrumentation()
            .AddProcessor(new CapturingProcessor(spans))
            .Build();
        await using var services = TelegramAdapterTests.Services(_server.BaseUrl, logs);

        var api = services.GetRequiredService<TelegramBotApi>();
        await api.CallAsync(Token, "getMe", null, CancellationToken.None);
        await api.GetUpdatesAsync(Token, 0, CancellationToken.None);
        await api.DownloadFileAsync(Token, "documents/x.pdf", 100, CancellationToken.None);
        await api.UploadAsync(Token, "sendPhoto", new Dictionary<string, string> { ["chat_id"] = "1" }, "photo", [1], "x.png", "image/png", CancellationToken.None);

        Assert.DoesNotContain(spans, s => s.TagObjects.Any(t => t.Value?.ToString()?.Contains(Token, StringComparison.Ordinal) == true)
            || s.DisplayName.Contains(Token, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains(Token, StringComparison.Ordinal));

        // Control: an unprotected named client in the same container leaks on both channels.
        var control = services.GetRequiredService<IHttpClientFactory>().CreateClient("telegram-control");
        await control.PostAsync(new Uri(_server.BaseUrl, $"./bot{Token}/getMe"), new StringContent("{}"));
        Assert.Contains(spans, s => s.TagObjects.Any(t => t.Value?.ToString()?.Contains(Token, StringComparison.Ordinal) == true));
        Assert.Contains(logs.Lines, line => line.Contains(Token, StringComparison.Ordinal));
    }

    private sealed class CapturingProcessor(ConcurrentQueue<Activity> spans) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => spans.Enqueue(data);
    }
}

/// <summary>A logger provider that keeps every formatted line, including scopes' state.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            owner.Lines.Enqueue($"{category} scope: {state}");
            return NullLogger.Instance.BeginScope(state) ?? new NoopDisposable();
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Lines.Enqueue($"{category}: {formatter(state, exception)}");
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>
/// A local stand-in for <c>api.telegram.org</c> on real Kestrel: records each call's method and JSON body, answers from a
/// per-method script, and otherwise succeeds (getUpdates returns no updates after a short wait, like a quiet poll).
/// </summary>
public sealed class FakeTelegramServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(int Status, string Json)>> _scripts = new();
    private WebApplication _app = null!;

    public ConcurrentQueue<(string Method, JsonObject Body)> Requests { get; } = new();

    public Uri BaseUrl { get; private set; } = null!;

    /// <summary>Files served at <c>/file/bot{token}/{path}</c>, by path.</summary>
    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Every file path requested, in order.</summary>
    public ConcurrentQueue<string> FileRequests { get; } = new();

    public void Script(string method, int status, string json)
        => _scripts.GetOrAdd(method, _ => new ConcurrentQueue<(int, string)>()).Enqueue((status, json));

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapPost("/bot{token}/{method}", HandleAsync);
        _app.MapGet("/file/bot{token}/{**path}", (string path) =>
        {
            FileRequests.Enqueue(path);
            return Files.TryGetValue(path, out var bytes) ? Results.Bytes(bytes, "application/octet-stream") : Results.NotFound();
        });
        await _app.StartAsync();
        BaseUrl = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/");
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private async Task HandleAsync(string token, string method, HttpContext http)
    {
        var body = http.Request.HasFormContentType ? await FormBodyAsync(http.Request) : (await JsonNode.ParseAsync(http.Request.Body))?.AsObject() ?? [];
        Requests.Enqueue((method, body));
        var (status, json) = _scripts.TryGetValue(method, out var queue) && queue.TryDequeue(out var scripted)
            ? scripted
            : (200, await DefaultAsync(method));
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(json);
    }

    // A multipart upload as a JSON object: each field as a string, each file as "<field>.filename" and "<field>.length".
    private static async Task<JsonObject> FormBodyAsync(HttpRequest request)
    {
        var form = await request.ReadFormAsync();
        var body = new JsonObject();
        foreach (var (name, value) in form)
        {
            body[name] = value.ToString();
        }

        foreach (var file in form.Files)
        {
            body[file.Name + ".filename"] = file.FileName;
            body[file.Name + ".length"] = file.Length;
        }

        return body;
    }

    private static async Task<string> DefaultAsync(string method)
    {
        switch (method)
        {
            case "getMe":
                return """{"ok":true,"result":{"id":7000000001,"is_bot":true,"first_name":"Agent 42","username":"agent42_test_bot"}}""";
            case "getUpdates":
                await Task.Delay(50);
                return """{"ok":true,"result":[]}""";
            case "sendMessage":
                return """{"ok":true,"result":{"message_id":1}}""";
            default:
                return """{"ok":true,"result":true}""";
        }
    }
}
