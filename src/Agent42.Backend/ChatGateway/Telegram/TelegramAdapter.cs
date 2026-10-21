using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Telegram over the Bot API with long polling (ADR 0111): the gateway connects OUT, so there is no webhook, no public
/// endpoint and no secret-token header to verify. Each user brings their own bot (from @BotFather).
///
/// <list type="bullet">
/// <item>Connect deletes any webhook (Telegram refuses <c>getUpdates</c> while one is set) WITHOUT dropping pending
/// updates, and registers the command menu. Hermes drops pending updates on a cold boot; we keep them, because the
/// owner expects every message they sent to be answered.</item>
/// <item>The poll cursor (<c>update_id</c> + 1) is committed after each batch is handed to the pipeline, so a restart
/// neither replays nor loses updates (Telegram keeps unconfirmed ones for 24 hours).</item>
/// <item>A 409 means another program polls the same bot (Telegram allows one); a 401/404 means the token is dead.
/// Both are permanent: <see cref="ChatAuthException"/>, so the connection is faulted rather than retried.</item>
/// </list>
/// </summary>
public sealed class TelegramAdapter(TelegramBotApi api, ILogger<TelegramAdapter> logger) : IChatPlatformAdapter
{
    // A 4096-character message, but at most one action per second per chat across sends and edits (Telegram's FAQ),
    // so edits are paced at a little over a second; "typing" lasts five seconds, refreshed at four.
    private static readonly ChatPlatformCapabilities TelegramCapabilities = new(
        MaxMessageLength: 4096,
        MaxChunks: 10,
        SupportsEdit: true,
        MinEditInterval: TimeSpan.FromMilliseconds(1200),
        TypingRefresh: TimeSpan.FromSeconds(4));

    public string Kind => ChatPlatformKind.Telegram;

    public ChatPlatformCapabilities Capabilities => TelegramCapabilities;

    public async Task<ChatBotIdentity> ValidateAsync(ChatCredentials credentials, CancellationToken cancellationToken)
    {
        if (!TelegramUpdates.LooksLikeToken(credentials.BotToken))
        {
            throw new ChatAuthException("That doesn't look like a Telegram bot token. Copy it from @BotFather; it looks like 123456789:ABC-def…");
        }

        var me = await api.CallAsync(credentials.BotToken, "getMe", null, cancellationToken);
        if (!me.Ok)
        {
            throw me.ErrorCode is 401 or 404
                ? new ChatAuthException("Telegram rejected that bot token. Copy it again from @BotFather.")
                : new HttpRequestException($"Telegram answered {me.ErrorCode}.");
        }

        return new ChatBotIdentity(me.Result.GetProperty("id").GetRawText(), me.Result.GetProperty("username").GetString() ?? "");
    }

    public async Task<IChatPlatformSession> ConnectAsync(
        ChatConnectionRuntime runtime, IChatInboundSink sink, CancellationToken cancellationToken)
    {
        var token = runtime.Credentials.BotToken;
        Throw(await api.CallAsync(token, "deleteWebhook", new { drop_pending_updates = false }, cancellationToken));

        // Best effort: the menu is a convenience, and a failure here must not keep the bot offline.
        var commands = ChatCommands.All.Select(c => (c.Name, Description: ChatCommands.Describe(c.Description, '/')))
            .Select(c => new { command = c.Name, description = c.Description[..Math.Min(c.Description.Length, 256)] });
        var menu = await api.CallAsync(token, "setMyCommands", new { commands }, cancellationToken);
        if (!menu.Ok)
        {
            logger.LogInformation("Telegram did not take the command menu ({Code}); continuing.", menu.ErrorCode);
        }

        return new TelegramSession(api, runtime, sink, logger, cancellationToken);
    }

    // Maps a failed management call during connect: permanent failures fault, everything else is retried.
    internal static void Throw(TelegramResponse response)
    {
        if (response.Ok)
        {
            return;
        }

        throw response.ErrorCode switch
        {
            401 or 404 => new ChatAuthException("Telegram rejected the bot token. Paste a new one from @BotFather."),
            409 => new ChatAuthException(
                "Another program is reading this bot's messages, and Telegram allows only one. Stop it, then save the connection again."),
            _ => new HttpRequestException($"Telegram answered {response.ErrorCode}."),
        };
    }
}

/// <summary>A live Telegram connection: its long-poll loop is <see cref="Completion"/>.</summary>
internal sealed class TelegramSession : IChatPlatformSession
{
    private readonly TelegramBotApi _api;
    private readonly ChatConnectionRuntime _runtime;
    private readonly IChatInboundSink _sink;
    private readonly CancellationTokenSource _stop;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TelegramSession(
        TelegramBotApi api, ChatConnectionRuntime runtime, IChatInboundSink sink, ILogger logger, CancellationToken cancellationToken)
    {
        _api = api;
        _runtime = runtime;
        _sink = sink;
        _logger = logger;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Completion = Task.Run(async () =>
        {
            await _started.Task.WaitAsync(_stop.Token);
            await PollAsync(_stop.Token);
        });
    }

    public Task Completion { get; }

    public void Start() => _started.TrySetResult();

    private string Token => _runtime.Credentials.BotToken;

    public async Task<ChatSendResult> SendAsync(ChatTarget target, string markdown, ChatTextFormat format, CancellationToken cancellationToken)
    {
        var body = TelegramUpdates.WithTarget(target, Text(markdown, format));
        if (target.ReplyToMessageId is { } replyTo && long.TryParse(replyTo, out var replyId))
        {
            body["reply_parameters"] = new Dictionary<string, object> { ["message_id"] = replyId, ["allow_sending_without_reply"] = true };
        }

        return (await _api.CallAsync(Token, "sendMessage", body, cancellationToken)).ToSendResult();
    }

    public async Task<ChatSendResult> EditAsync(
        ChatTarget target, string messageId, string markdown, ChatTextFormat format, CancellationToken cancellationToken)
    {
        var body = Text(markdown, format);
        body["chat_id"] = target.ChatId;
        body["message_id"] = long.Parse(messageId);
        var result = (await _api.CallAsync(Token, "editMessageText", body, cancellationToken)).ToSendResult();
        return result.Success ? ChatSendResult.Ok(messageId) : result;
    }

    public Task SendTypingAsync(ChatTarget target, CancellationToken cancellationToken)
        => _api.CallAsync(Token, "sendChatAction", TelegramUpdates.WithTarget(target, new Dictionary<string, object> { ["action"] = "typing" }), cancellationToken);

    // A generated image as a photo (sendPhoto takes up to 10 MB), in the same chat, topic and reply chain as the text.
    public async Task<ChatSendResult> UploadImageAsync(
        ChatTarget target, byte[] bytes, string fileName, string mediaType, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string> { ["chat_id"] = target.ChatId };
        if (target.ThreadId is { } thread)
        {
            fields["message_thread_id"] = thread;
        }

        if (target.ReplyToMessageId is { } replyTo && long.TryParse(replyTo, out var replyId))
        {
            fields["reply_parameters"] = JsonSerializer.Serialize(new { message_id = replyId, allow_sending_without_reply = true });
        }

        return (await _api.UploadAsync(Token, "sendPhoto", fields, "photo", bytes, fileName, mediaType, cancellationToken)).ToSendResult();
    }

    // getFile turns the file_id into a path on api.telegram.org (the only host the token ever goes to, since the base URL
    // is fixed), then the download is capped. getFile's own size is checked first, so an oversized file is never fetched.
    public async Task<ChatDownloadedFile?> DownloadAsync(ChatInboundFile file, long maxBytes, CancellationToken cancellationToken)
    {
        var info = await _api.CallAsync(Token, "getFile", new { file_id = file.FileRef }, cancellationToken);
        if (!info.Ok || info.Result.ValueKind != JsonValueKind.Object
            || !info.Result.TryGetProperty("file_path", out var path) || path.GetString() is not { Length: > 0 } filePath
            || (info.Result.TryGetProperty("file_size", out var size) && size.GetInt64() > maxBytes))
        {
            return null;
        }

        return await _api.DownloadFileAsync(Token, filePath, maxBytes, cancellationToken) is { } bytes
            ? new ChatDownloadedFile(bytes, file.FileName ?? Path.GetFileName(filePath), file.DeclaredMediaType)
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

    private static Dictionary<string, object> Text(string markdown, ChatTextFormat format)
        => format == ChatTextFormat.Formatted
            ? new Dictionary<string, object> { ["text"] = TelegramText.ToHtml(markdown), ["parse_mode"] = "HTML" }
            : new Dictionary<string, object> { ["text"] = markdown };

    // The long poll. Ends with an exception the supervisor classifies: ChatAuthException stops, anything else retries.
    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var offset = _runtime.Connection.PollOffset;
        while (!cancellationToken.IsCancellationRequested)
        {
            var response = await _api.GetUpdatesAsync(Token, offset, cancellationToken);
            TelegramAdapter.Throw(response);
            if (response.Result.ValueKind != JsonValueKind.Array || response.Result.GetArrayLength() == 0)
            {
                continue;
            }

            foreach (var update in response.Result.EnumerateArray())
            {
                await AcceptOneAsync(update, cancellationToken);
                offset = update.TryGetProperty("update_id", out var id) ? Math.Max(offset, id.GetInt64() + 1) : offset;
            }

            await _runtime.CommitCursorAsync(offset);
        }
    }

    // One update that cannot be parsed or handled is logged and skipped. Throwing would end the session before the
    // cursor moves, so the same poison update would come back on every reconnect.
    private async Task AcceptOneAsync(JsonElement update, CancellationToken cancellationToken)
    {
        try
        {
            if (TelegramUpdates.Parse(update, _runtime.Connection.BotUserId, _runtime.Connection.BotUserName) is { } message)
            {
                await _sink.AcceptAsync(_runtime, message, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "A Telegram update on connection {ConnectionId} could not be handled; skipping it.", _runtime.ConnectionId);
        }
    }
}
