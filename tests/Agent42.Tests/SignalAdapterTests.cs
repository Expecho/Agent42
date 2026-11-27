using System.Collections.Concurrent;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agent42.Tests;

/// <summary>
/// The Signal adapter and its session (ADR 0111 phase 3; AC29) over a scripted daemon: one event stream per number,
/// parsed messages reach the sink, a stream that ends is reopened, a daemon that no longer knows the number faults the
/// connection, replies are final-only with typing, images go out as data URIs, files come in through getAttachment,
/// and no log line ever carries a number.
/// </summary>
public sealed class SignalAdapterTests
{
    private const string Number = "+31600000077";
    private const string BotAci = "bbbbbbbb-0000-4000-8000-000000000077";
    private const string Owner = "aaaaaaaa-0000-4000-8000-000000000001";

    private readonly SignalDaemonStub _daemon = new();
    private readonly CapturingLoggerProvider _logs = new();

    [Fact]
    public void Capabilities_AreFinalOnly_WithTyping()
    {
        var capabilities = Adapter().Capabilities;

        Assert.False(capabilities.SupportsEdit);
        Assert.Equal('/', capabilities.CommandPrefix);
        Assert.True(capabilities.TypingRefresh < TimeSpan.FromSeconds(15)); // Signal clears typing after 15 s
    }

    [Fact]
    public async Task Validate_ReturnsTheAci_OrFaultsAnUnknownNumber()
    {
        _daemon.Accounts[Number] = BotAci;

        var identity = await Adapter().ValidateAsync(new ChatCredentials(Number), CancellationToken.None);
        var gone = await Assert.ThrowsAsync<ChatAuthException>(
            () => Adapter().ValidateAsync(new ChatCredentials("+31600000078"), CancellationToken.None));

        Assert.Equal((BotAci, Number), (identity.BotUserId, identity.BotUserName));
        Assert.Equal(SignalAdapter.UnknownAccountReason, gone.Message);
    }

    [Fact]
    public async Task AnOwnerDm_ReachesTheSink_FromThisNumbersOwnStream()
    {
        var sink = new RecordingSink();
        await using var session = await ConnectAsync(sink);

        await _daemon.PushAsync(Number, Dm("hello agent"));

        var message = await sink.NextAsync();
        Assert.Equal(("hello agent", Owner, ChatType.Direct), (message.Text, message.SenderId, message.ChatType));
        Assert.Equal(1, _daemon.StreamOpens[Number]);
    }

    [Fact]
    public async Task AStreamThatEnds_IsReopened_InsideTheSession()
    {
        var sink = new RecordingSink();
        await using var session = await ConnectAsync(sink);
        await TestWait.WaitUntilAsync(() => _daemon.StreamOpens.GetValueOrDefault(Number) >= 1, "the daemon stream to open 1 time(s)");

        await _daemon.EndStreamAsync(Number);
        await TestWait.WaitUntilAsync(() => _daemon.StreamOpens.GetValueOrDefault(Number) >= 2, "the daemon stream to open 2 time(s)");
        await _daemon.PushAsync(Number, Dm("after the restart"));

        Assert.Equal("after the restart", (await sink.NextAsync()).Text);
        Assert.False(session.Completion.IsCompleted);
    }

    [Fact]
    public async Task ADaemonThatNoLongerKnowsTheNumber_FaultsTheSession()
    {
        _daemon.UnknownStreams[Number] = true;
        await using var session = await ConnectAsync(new RecordingSink());

        await Assert.ThrowsAsync<ChatAuthException>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AReply_IsSentFormatted_AsAQuoteOfTheMessage_AndTypingGoesToTheChat()
    {
        await using var session = await ConnectAsync(new RecordingSink());

        await session.SendTypingAsync(new ChatTarget(Owner), CancellationToken.None);
        var sent = await session.SendAsync(
            new ChatTarget("R3JvdXBJZA==", ReplyToMessageId: Owner + ":1700000000123"), "**hi**", ChatTextFormat.Formatted, CancellationToken.None);

        Assert.True(sent.Success);
        Assert.Equal("1700000000999", sent.MessageId);
        var send = _daemon.Calls.Last(c => c.Method == "send").Params;
        Assert.Equal("hi", send.GetProperty("message").GetString());
        Assert.Equal("0:2:BOLD", send.GetProperty("textStyles")[0].GetString());
        Assert.Equal("R3JvdXBJZA==", send.GetProperty("groupId").GetString());
        Assert.Equal((1700000000123L, Owner), (send.GetProperty("quoteTimestamp").GetInt64(), send.GetProperty("quoteAuthor").GetString()));
        Assert.Contains(_daemon.Calls, c => c.Method == "sendTyping");
        Assert.False((await session.EditAsync(new ChatTarget(Owner), "1", "x", ChatTextFormat.Plain, CancellationToken.None)).Success);
    }

    [Fact]
    public async Task AnImage_GoesOutAsADataUri()
    {
        await using var session = await ConnectAsync(new RecordingSink());

        var sent = await session.UploadImageAsync(new ChatTarget(Owner), [1, 2, 3], "chart.png", "image/png", CancellationToken.None);

        Assert.True(sent.Success);
        var attachment = _daemon.Calls.Last(c => c.Method == "send").Params.GetProperty("attachments")[0].GetString();
        Assert.Equal("data:image/png;filename=chart.png;base64,AQID", attachment);
    }

    [Fact]
    public async Task AFile_IsFetchedThroughGetAttachment_ForItsChat_AndAnOversizedOneNeverIs()
    {
        _daemon.Results["getAttachment"] = """{"data":"AQID"}""";
        await using var session = await ConnectAsync(new RecordingSink());
        var reference = SignalEvents.AttachmentRef(Owner, "att1.jpg");

        var file = await session.DownloadAsync(new ChatInboundFile(reference, "cat.jpg", "image/jpeg", 3, false), 100, CancellationToken.None);
        var tooBig = await session.DownloadAsync(new ChatInboundFile(reference, "big.jpg", "image/jpeg", 101, false), 100, CancellationToken.None);

        Assert.Equal([1, 2, 3], file!.Bytes);
        Assert.Null(tooBig);
        var fetch = Assert.Single(_daemon.Calls, c => c.Method == "getAttachment").Params;
        Assert.Equal(("att1.jpg", Owner), (fetch.GetProperty("id").GetString(), fetch.GetProperty("recipient")[0].GetString()));
    }

    // A captcha demand on a send is a fault the owner must act on, not a reply to retry forever.
    [Fact]
    public async Task ASendSignalAnswersWithAProofToken_FaultsTheSession()
    {
        _daemon.Results["send"] = """{"timestamp":1,"results":[{"type":"RATE_LIMIT_FAILURE","token":"proof-token"}]}""";
        await using var session = await ConnectAsync(new RecordingSink());

        var sent = await session.SendAsync(new ChatTarget(Owner), "hi", ChatTextFormat.Plain, CancellationToken.None);

        Assert.Equal(ChatSendErrorKind.RateLimited, sent.ErrorKind);
        var fault = await Assert.ThrowsAsync<ChatAuthException>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("captcha", fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forget_UnregistersAndDeletesTheNumber_FromCredentialsOrThePendingField()
    {
        var adapter = Adapter();

        await adapter.ForgetAsync(new ChatConnection { Id = "c1" }, new ChatCredentials(Number), CancellationToken.None);
        await adapter.ForgetAsync(new ChatConnection { Id = "c2", PendingSignalNumber = "+31600000079" }, null, CancellationToken.None);
        await adapter.ForgetAsync(new ChatConnection { Id = "c3" }, null, CancellationToken.None);

        Assert.Equal(
            ["unregister:" + Number, "deleteLocalAccountData:" + Number, "unregister:+31600000079", "deleteLocalAccountData:+31600000079"],
            _daemon.Calls.Select(c => c.Method + ":" + c.Params.GetProperty("account").GetString()));
    }

    // No log line carries a number: /logs is owner-visible, and App Insights is the operator's.
    [Fact]
    public async Task NoLogRecord_CarriesTheNumber()
    {
        var sink = new RecordingSink { ThrowOnAccept = true };
        _daemon.Errors["unregister"] = (-3, "boom " + Number);
        await using (var session = await ConnectAsync(sink))
        {
            await _daemon.PushAsync(Number, Dm("this handler throws"));
            await sink.NextAsync();
        }

        await Adapter().ForgetAsync(new ChatConnection { Id = "c" }, new ChatCredentials(Number), CancellationToken.None);

        Assert.NotEmpty(_logs.Records);
        Assert.DoesNotContain(_logs.Records, r => r.Contains(Number, StringComparison.Ordinal) || r.Contains("31600000077", StringComparison.Ordinal));
    }

    // AC35: Signal ships dormant. Off, nothing is registered (no adapter offered, no daemon, no lease participant); on,
    // the adapter and the daemon's lease participant are.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddSignalChat_RegistersSignal_OnlyWhenEnabled(bool enabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ChatGateway:Signal:Enabled"] = enabled.ToString() })
            .Build();
        var services = new ServiceCollection();

        services.AddSignalChat(configuration);

        Assert.Equal(enabled, services.Any(d => d.ServiceType == typeof(IChatPlatformAdapter)));
        Assert.Equal(enabled, services.Any(d => d.ServiceType == typeof(IChatGatewayLeaseParticipant)));
        Assert.Equal(enabled, services.Any(d => d.ServiceType == typeof(SignalOptions)));
    }

    private SignalAdapter Adapter()
        => new(_daemon.Client(), TimeProvider.System, LoggerFactory.Create(b => b.AddProvider(_logs)).CreateLogger<SignalAdapter>());

    private async Task<IChatPlatformSession> ConnectAsync(IChatInboundSink sink)
    {
        _daemon.Accounts[Number] = BotAci;
        var runtime = new ChatConnectionRuntime(
            "user-1",
            new ChatConnection { Id = "conn-signal", Platform = ChatPlatformKind.Signal, BotUserId = BotAci },
            new ChatCredentials(Number),
            _ => Task.CompletedTask);
        var session = await Adapter().ConnectAsync(runtime, sink, CancellationToken.None);
        session.Start();
        return session;
    }

    private static string Dm(string text)
        => JsonSerializer.Serialize(new { sourceUuid = Owner, sourceNumber = "+31611111111", sourceDevice = 1, timestamp = 1700000000123, dataMessage = new { message = text } });

    private sealed class RecordingSink : IChatInboundSink
    {
        private readonly ConcurrentQueue<ChatInboundMessage> _messages = new();
        private readonly SemaphoreSlim _arrived = new(0);

        public bool ThrowOnAccept { get; init; }

        public Task AcceptAsync(ChatConnectionRuntime runtime, ChatInboundMessage message, CancellationToken cancellationToken)
        {
            _messages.Enqueue(message);
            _arrived.Release();
            return ThrowOnAccept ? Task.FromException(new InvalidOperationException("sink failed")) : Task.CompletedTask;
        }

        public async Task<ChatInboundMessage> NextAsync()
        {
            Assert.True(await _arrived.WaitAsync(TimeSpan.FromSeconds(10)), "No message reached the sink.");
            _messages.TryDequeue(out var message);
            return message!;
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Records { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capturing(Records);

        public void Dispose()
        {
        }

        private sealed class Capturing(ConcurrentQueue<string> records) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => records.Enqueue(formatter(state, exception) + " " + exception);
        }
    }
}
