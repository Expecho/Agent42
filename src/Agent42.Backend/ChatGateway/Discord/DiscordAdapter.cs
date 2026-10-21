using System.Collections.Concurrent;
using Agent42.Agents;
using Discord;
using Discord.Net;
using Discord.WebSocket;

namespace Agent42.Backend;

/// <summary>
/// Discord over the gateway websocket (ADR 0111). Discord offers no other way to receive ordinary DMs, and the gateway
/// protocol (heartbeat, identify, resume, compression, close codes) is the one part of this feature not worth
/// hand-rolling, so <c>Discord.Net.WebSocket</c> holds the socket (C10, Spike findings (c)). Replies go through the
/// hand-rolled <see cref="DiscordRestApi"/>.
///
/// <list type="bullet">
/// <item><b>Intents:</b> <c>Guilds | GuildMessages | DirectMessages</c>, never the privileged MESSAGE_CONTENT. Discord
/// still sends the content of DMs and of messages that mention the bot, which is all the owner gate lets through.</item>
/// <item><b>A non-reconnectable close code faults the connection.</b> Discord.Net reconnects forever on every close,
/// 4004 "Authentication failed" included, so the adapter watches <c>Disconnected</c> and ends the session itself
/// on 4004 and 4010–4014. A 401 from the library's one REST call (<c>/gateway/bot</c>) is treated the same; that it
/// arrives through <c>Disconnected</c> is read from the library's source (its reconnect loop raises it), not tested.</item>
/// <item>In a guild, a reply starts a thread on the owner's message (Discord gives the thread the message's id), so
/// one mention is one conversation with its follow-ups in the thread.</item>
/// <item>Messages sent while the bot was disconnected are not replayed: Discord has no backlog for bots.</item>
/// </list>
/// </summary>
public sealed class DiscordAdapter(DiscordRestApi api, DiscordOptions options, ILoggerFactory loggerFactory) : IChatPlatformAdapter
{
    /// <summary>The gateway intents the bot asks for. Only unprivileged ones, so the bot needs no portal toggle.</summary>
    public const GatewayIntents Intents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.DirectMessages;

    // 2000 characters a message, a reply split into at most 8 (Hermes' cap, so a runaway answer can't flood a channel).
    // Discord allows about 5 edits per 5 seconds per channel, so edits are paced a little over a second. Typing lasts
    // about 10 seconds, refreshed at 9.
    private static readonly ChatPlatformCapabilities DiscordCapabilities = new(
        MaxMessageLength: 2000,
        MaxChunks: 8,
        SupportsEdit: true,
        MinEditInterval: TimeSpan.FromMilliseconds(1200),
        TypingRefresh: TimeSpan.FromSeconds(9));

    private readonly ILogger _logger = loggerFactory.CreateLogger<DiscordAdapter>();

    public string Kind => ChatPlatformKind.Discord;

    public ChatPlatformCapabilities Capabilities => DiscordCapabilities;

    public async Task<ChatBotIdentity> ValidateAsync(ChatCredentials credentials, CancellationToken cancellationToken)
    {
        if (!DiscordMessages.LooksLikeToken(credentials.BotToken))
        {
            throw new ChatAuthException("That doesn't look like a Discord bot token. Copy it from the Developer Portal: your app → Bot → Reset Token.");
        }

        var me = await api.GetMeAsync(credentials.BotToken, cancellationToken);
        if (!me.Ok)
        {
            throw me.Status == 401
                ? new ChatAuthException("Discord rejected that bot token. Reset it in the Developer Portal and paste the new one.")
                : new HttpRequestException($"Discord answered {me.Status}.");
        }

        return new ChatBotIdentity(me.Body.GetProperty("id").GetString()!, me.Body.GetProperty("username").GetString() ?? "");
    }

    public Task<IChatPlatformSession> ConnectAsync(ChatConnectionRuntime runtime, IChatInboundSink sink, CancellationToken cancellationToken)
        => Task.FromResult<IChatPlatformSession>(new DiscordSession(api, options, runtime, sink, _logger, cancellationToken));

    /// <summary>
    /// Whether <paramref name="exception"/> (or anything inside it) is a permanent gateway failure: a close code Discord
    /// documents as "Reconnect: false", or the library's <c>/gateway/bot</c> call refused with 401.
    /// </summary>
    public static bool IsPermanent(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is WebSocketClosedException { CloseCode: 4004 or (>= 4010 and <= 4014) }
                || current is HttpException { HttpCode: System.Net.HttpStatusCode.Unauthorized })
            {
                return true;
            }
        }

        return false;
    }

    public static string PermanentReason(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case WebSocketClosedException { CloseCode: 4013 or 4014 }:
                    return "Discord refused the bot's gateway intents. Check the bot's settings in the Developer Portal, then save the connection again.";
                case WebSocketClosedException { CloseCode: >= 4010 and <= 4012 } closed:
                    return $"Discord refused the gateway session (close code {closed.CloseCode}). Save the connection again to retry.";
            }
        }

        return "Discord rejected the bot token. Reset it in the Developer Portal and paste the new one.";
    }
}

/// <summary>A live Discord connection: one <see cref="DiscordSocketClient"/>, whose life is <see cref="Completion"/>.</summary>
internal sealed class DiscordSession : IChatPlatformSession
{
    // Threads this session started (or could not start: no permission), so a second send doesn't try again (bounded,
    // in-process).
    private const int MaxKnownThreads = 1000;
    private const string ThreadName = "Agent 42";

    private readonly DiscordRestApi _api;
    private readonly DiscordOptions _options;
    private readonly ChatConnectionRuntime _runtime;
    private readonly IChatInboundSink _sink;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, byte> _knownThreads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _threadless = new(StringComparer.Ordinal);

    public DiscordSession(
        DiscordRestApi api, DiscordOptions options, ChatConnectionRuntime runtime, IChatInboundSink sink, ILogger logger, CancellationToken cancellationToken)
    {
        _api = api;
        _options = options;
        _runtime = runtime;
        _sink = sink;
        _logger = logger;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Completion = Task.Run(async () =>
        {
            await _started.Task.WaitAsync(_stop.Token);
            await RunAsync(_stop.Token);
        });
    }

    public Task Completion { get; }

    public void Start() => _started.TrySetResult();

    private string Token => _runtime.Credentials.BotToken;

    public async Task<ChatSendResult> SendAsync(ChatTarget target, string markdown, ChatTextFormat format, CancellationToken cancellationToken)
    {
        var content = format == ChatTextFormat.Formatted ? DiscordText.ToDiscord(markdown) : markdown;
        // The message a thread starts on is in the parent channel, not the thread, so it can't be replied to there.
        var replyTo = target.ReplyToMessageId == target.ThreadId ? null : target.ReplyToMessageId;
        if (target.ThreadId is not { } thread || thread == target.ChatId)
        {
            return (await _api.CreateMessageAsync(Token, target.ChatId, content, replyTo, cancellationToken)).ToSendResult();
        }

        if (!_knownThreads.ContainsKey(thread) && !_threadless.ContainsKey(thread))
        {
            // The first reply to a top-level mention: start the thread on it. "Already has a thread" is fine too.
            var started = await _api.StartThreadAsync(Token, target.ChatId, thread, ThreadName, cancellationToken);
            if (started.Ok || started.Status == 400)
            {
                Remember(_knownThreads, thread);
            }
            else if (started.Status == 403)
            {
                // No permission to start threads here: answer in the channel, as a reply to the mention, rather than
                // posting into a thread that doesn't exist on every message.
                Remember(_threadless, thread);
            }
        }

        return _threadless.ContainsKey(thread)
            ? (await _api.CreateMessageAsync(Token, target.ChatId, content, thread, cancellationToken)).ToSendResult()
            : (await _api.CreateMessageAsync(Token, thread, content, replyTo, cancellationToken)).ToSendResult();
    }

    // The channel a target's messages live in: its thread, unless that thread could not be started.
    private string ChannelFor(ChatTarget target)
        => target.ThreadId is { } thread && !_threadless.ContainsKey(thread) ? thread : target.ChatId;

    public async Task<ChatSendResult> EditAsync(
        ChatTarget target, string messageId, string markdown, ChatTextFormat format, CancellationToken cancellationToken)
    {
        var content = format == ChatTextFormat.Formatted ? DiscordText.ToDiscord(markdown) : markdown;
        var result = (await _api.EditMessageAsync(Token, ChannelFor(target), messageId, content, cancellationToken)).ToSendResult();
        return result.Success ? ChatSendResult.Ok(messageId) : result;
    }

    // Typing in a thread that doesn't exist yet fails harmlessly; the first send starts it.
    public Task SendTypingAsync(ChatTarget target, CancellationToken cancellationToken)
        => _api.TriggerTypingAsync(Token, ChannelFor(target), cancellationToken);

    // Phase 1c (ADR 0111): outbound images are uploaded there.
    public Task<ChatSendResult> UploadImageAsync(
        ChatTarget target, byte[] bytes, string fileName, string mediaType, CancellationToken cancellationToken)
        => Task.FromResult(ChatSendResult.Fail(ChatSendErrorKind.Unknown));

    public async Task<ChatDownloadedFile?> DownloadAsync(ChatInboundFile file, long maxBytes, CancellationToken cancellationToken)
        => await _api.DownloadAttachmentAsync(file.FileRef, maxBytes, cancellationToken) is { } bytes
            ? new ChatDownloadedFile(bytes, file.FileName ?? "file", file.DeclaredMediaType)
            : null;

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

    private static void Remember(ConcurrentDictionary<string, byte> threads, string thread)
    {
        if (threads.Count >= MaxKnownThreads)
        {
            threads.Clear();
        }

        threads.TryAdd(thread, 0);
    }

    // The client reconnects and resumes by itself; this only ends on cancellation or a permanent fault.
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = DiscordAdapter.Intents,
            GatewayHost = _options.GatewayUrl,
            MessageCacheSize = 0,
            AlwaysDownloadUsers = false,
            LogGatewayIntentWarnings = false,
            LogLevel = LogSeverity.Info,
        });
        client.Log += LogAsync;
        client.MessageReceived += OnMessageAsync;
        client.Disconnected += OnDisconnectedAsync;
        try
        {
            await client.LoginAsync(TokenType.Bot, Token, validateToken: false);
            await client.StartAsync();
            await _faulted.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            client.MessageReceived -= OnMessageAsync;
            client.Disconnected -= OnDisconnectedAsync;
            await StopQuietlyAsync(client);
            client.Log -= LogAsync;
        }
    }

    private Task OnDisconnectedAsync(Exception? exception)
    {
        if (DiscordAdapter.IsPermanent(exception))
        {
            _faulted.TrySetException(new ChatAuthException(DiscordAdapter.PermanentReason(exception)));
        }

        return Task.CompletedTask;
    }

    // One message that cannot be projected or handled is logged and skipped, never thrown into the library's dispatch.
    private async Task OnMessageAsync(SocketMessage message)
    {
        try
        {
            if (DiscordMessages.Parse(View(message), _runtime.Connection.BotUserId) is { } inbound)
            {
                await _sink.AcceptAsync(_runtime, inbound, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "A Discord message on connection {ConnectionId} could not be handled; skipping it.", _runtime.ConnectionId);
        }
    }

    internal static DiscordMessageView View(SocketMessage message)
    {
        var (kind, parent) = message.Channel switch
        {
            SocketThreadChannel thread => (DiscordChannelKind.Thread, thread.ParentChannel?.Id.ToString()),
            IPrivateChannel => (DiscordChannelKind.Direct, (string?)null),
            _ => (DiscordChannelKind.GuildChannel, null),
        };
        return new DiscordMessageView(
            Id: message.Id.ToString(),
            ChannelId: message.Channel.Id.ToString(),
            ChannelKind: kind,
            ParentChannelId: parent,
            AuthorId: message.Author.Id.ToString(),
            AuthorIsBot: message.Author.IsBot || message.Author.IsWebhook,
            IsUserMessage: message is SocketUserMessage && message.Type is MessageType.Default or MessageType.Reply,
            Content: message.Content ?? "",
            MentionedUserIds: message.MentionedUsers.Select(u => u.Id.ToString()).ToList(),
            Attachments: message.Attachments.Select(a => new ChatInboundFile(
                a.Url, a.Filename, a.ContentType, a.Size, message.Flags?.HasFlag(MessageFlags.VoiceMessage) == true)).ToList(),
            Timestamp: message.Timestamp);
    }

    private Task LogAsync(LogMessage message)
    {
        // The library's own diagnostics: a reconnect is routine, so it stays below Warning unless the library says so.
        var level = message.Severity switch
        {
            LogSeverity.Critical or LogSeverity.Error => LogLevel.Warning,
            LogSeverity.Warning => LogLevel.Information,
            _ => LogLevel.Debug,
        };
        _logger.Log(level, message.Exception, "Discord {Source} on connection {ConnectionId}: {Message}", message.Source, _runtime.ConnectionId, message.Message);
        return Task.CompletedTask;
    }

    private static async Task StopQuietlyAsync(DiscordSocketClient client)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.StopAsync().WaitAsync(timeout.Token);
            await client.LogoutAsync().WaitAsync(timeout.Token);
        }
        catch
        {
            // Shutting down; the socket goes with the client either way.
        }
    }
}
