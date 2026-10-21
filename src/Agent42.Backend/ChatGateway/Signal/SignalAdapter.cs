using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Signal through a signal-cli daemon (ADR 0111, phase 3). Each user registers a DEDICATED number of their own in the
/// daemon (never a linked device of their own account: its replies would be sent as them, and the daemon would hold all
/// their Signal traffic). The bot is that number's own Signal account, so pairing, the owner check, DMs and groups work
/// as on the other platforms. The credential is the E.164 number, and it is only ever stored by the registration
/// endpoints (<c>AgentApi.ChatGatewaySignal</c>), never pasted.
///
/// <list type="bullet">
/// <item><b>Final-only.</b> Signal can edit a message, but its limits aren't documented and every edit notifies again,
/// so the streamer sends typing and then the final text.</item>
/// <item><b>One event stream per number</b> (<c>/api/v1/events?account=</c>): a stream without it captures the account
/// list at connect time. The daemon runs <c>--receive-mode=on-connection</c>, so messages wait on Signal's server while
/// no stream is open. A stream that ends (the daemon restarted) reopens inside the session; a daemon that no longer
/// knows the number ends it with <see cref="ChatAuthException"/>.</item>
/// <item>A send Signal answers with a proof-required token (a rate-limit captcha) or "unknown account" also ends the
/// session as a fault, so the panel shows why instead of failing every reply.</item>
/// </list>
/// </summary>
public sealed class SignalAdapter(SignalRpcClient rpc, TimeProvider timeProvider, ILogger<SignalAdapter> logger) : IChatPlatformAdapter
{
    // Signal cuts a body past 2000 UTF-8 bytes into a long-text attachment; 2000 UTF-16 units keeps ASCII replies inline,
    // and the attachment covers multibyte overflow. Typing lasts 15 s on Signal, so it is refreshed well inside that.
    private static readonly ChatPlatformCapabilities SignalCapabilities = new(
        MaxMessageLength: 2000,
        MaxChunks: 8,
        SupportsEdit: false,
        MinEditInterval: TimeSpan.FromSeconds(1),
        TypingRefresh: TimeSpan.FromSeconds(10));

    /// <summary>The message a connection is faulted with when the daemon no longer holds its number.</summary>
    public const string UnknownAccountReason = "Signal no longer has this number registered here. Register it again in Agent42.";

    public string Kind => ChatPlatformKind.Signal;

    public ChatPlatformCapabilities Capabilities => SignalCapabilities;

    // The supervisor's "does the daemon still hold it" check; the registration endpoints are the only way in.
    public async Task<ChatBotIdentity> ValidateAsync(ChatCredentials credentials, CancellationToken cancellationToken)
    {
        var accounts = await rpc.ListAccountsAsync(cancellationToken)
            ?? throw new HttpRequestException("The Signal daemon did not answer.");
        var account = accounts.FirstOrDefault(a => a.Number == credentials.BotToken);
        if (account?.Aci is not { Length: > 0 } aci)
        {
            throw new ChatAuthException(UnknownAccountReason);
        }

        return new ChatBotIdentity(aci, account.Number);
    }

    public Task<IChatPlatformSession> ConnectAsync(ChatConnectionRuntime runtime, IChatInboundSink sink, CancellationToken cancellationToken)
        => Task.FromResult<IChatPlatformSession>(new SignalSession(rpc, runtime, sink, timeProvider, logger, cancellationToken));
}

/// <summary>A live Signal connection: its event-stream loop is <see cref="Completion"/>.</summary>
internal sealed class SignalSession : IChatPlatformSession
{
    // A stream that ends sooner than this counts as a quick drop; repeated ones space the reopening out.
    private static readonly TimeSpan QuickDrop = TimeSpan.FromSeconds(30);

    private readonly SignalRpcClient _rpc;
    private readonly ChatConnectionRuntime _runtime;
    private readonly IChatInboundSink _sink;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SignalSession(
        SignalRpcClient rpc, ChatConnectionRuntime runtime, IChatInboundSink sink, TimeProvider time, ILogger logger, CancellationToken cancellationToken)
    {
        _rpc = rpc;
        _runtime = runtime;
        _sink = sink;
        _time = time;
        _logger = logger;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Completion = Task.Run(async () =>
        {
            await _started.Task.WaitAsync(_stop.Token);
            // Whichever ends first: the stream loop, or a send that found the account gone or a captcha demanded.
            var ended = await Task.WhenAny(RunAsync(_stop.Token), _faulted.Task);
            await ended;
        });
    }

    public Task Completion { get; }

    public void Start() => _started.TrySetResult();

    private string Number => _runtime.Credentials.BotToken;

    public async Task<ChatSendResult> SendAsync(ChatTarget target, string markdown, ChatTextFormat format, CancellationToken cancellationToken)
    {
        var (text, styles) = format == ChatTextFormat.Formatted ? SignalText.FromMarkdown(markdown) : SignalText.Plain(markdown);
        var (quoteTimestamp, quoteAuthor) = Quote(target.ReplyToMessageId);
        return Observe(await _rpc.SendAsync(Number, target.ChatId, text, styles, null, quoteTimestamp, quoteAuthor, cancellationToken));
    }

    // Never called: the capability says final-only.
    public Task<ChatSendResult> EditAsync(
        ChatTarget target, string messageId, string markdown, ChatTextFormat format, CancellationToken cancellationToken)
        => Task.FromResult(ChatSendResult.Fail(ChatSendErrorKind.Forbidden));

    public Task SendTypingAsync(ChatTarget target, CancellationToken cancellationToken)
        => _rpc.SendTypingAsync(Number, target.ChatId, stop: false, cancellationToken);

    // signal-cli takes an attachment as an RFC 2397 data URI with a filename, so no file ever touches the share.
    public async Task<ChatSendResult> UploadImageAsync(
        ChatTarget target, byte[] bytes, string fileName, string mediaType, CancellationToken cancellationToken)
    {
        var dataUri = $"data:{mediaType};filename={Uri.EscapeDataString(fileName)};base64,{Convert.ToBase64String(bytes)}";
        return Observe(await _rpc.SendAsync(Number, target.ChatId, "", null, [dataUri], null, null, cancellationToken));
    }

    // The size the envelope declared is checked before anything is fetched; the client caps the decoded bytes again.
    public async Task<ChatDownloadedFile?> DownloadAsync(ChatInboundFile file, long maxBytes, CancellationToken cancellationToken)
    {
        if (file.Size > maxBytes || SignalEvents.ParseAttachmentRef(file.FileRef) is not var (chatId, attachmentId))
        {
            return null;
        }

        return await _rpc.GetAttachmentAsync(Number, attachmentId, chatId, maxBytes, cancellationToken) is { } bytes
            ? new ChatDownloadedFile(bytes, file.FileName ?? attachmentId, file.DeclaredMediaType)
            : null;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await Completion;
        }
        catch
        {
            // The supervisor already observed how the session ended.
        }

        _stop.Dispose();
    }

    // A reply to a group message quotes it: the gateway's message id is "senderAci:timestamp".
    private static (long? Timestamp, string? Author) Quote(string? replyTo)
    {
        var split = replyTo?.LastIndexOf(':') ?? -1;
        return split > 0 && long.TryParse(replyTo![(split + 1)..], out var timestamp)
            ? (timestamp, replyTo[..split])
            : (null, null);
    }

    // The account vanished or Signal wants a captcha: a fault the owner must act on, not a reply to retry.
    private ChatSendResult Observe(SignalSendOutcome outcome)
    {
        if (outcome.IsUnknownAccount)
        {
            _faulted.TrySetException(new ChatAuthException(SignalAdapter.UnknownAccountReason));
        }
        else if (outcome.ProofRequired)
        {
            _faulted.TrySetException(new ChatAuthException(
                "Signal is rate limiting this number and asks for a captcha. Register the number again in Agent42 to continue."));
        }

        return outcome.Result;
    }

    // One event stream after another. A stream that ends (the daemon restarted) is reopened here; repeated quick drops are
    // spaced out. Anything thrown ends the session for the supervisor: ChatAuthException stops it, the rest back off.
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var quickDrops = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var openedAt = _time.GetUtcNow();
            await foreach (var evt in _rpc.ReadEventsAsync(Number, cancellationToken))
            {
                await AcceptOneAsync(evt, cancellationToken);
            }

            quickDrops = _time.GetUtcNow() - openedAt < QuickDrop ? quickDrops + 1 : 0;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 << Math.Min(quickDrops, 5), 30)), _time, cancellationToken);
        }
    }

    // An event that cannot be parsed or handled is logged (never with its content or a number) and skipped.
    private async Task AcceptOneAsync(System.Text.Json.JsonElement evt, CancellationToken cancellationToken)
    {
        try
        {
            if (SignalEvents.Parse(evt, _runtime.Connection.BotUserId) is { } message)
            {
                await _sink.AcceptAsync(_runtime, message, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "A Signal event on connection {ConnectionId} could not be handled; skipping it.", _runtime.ConnectionId);
        }
    }
}
