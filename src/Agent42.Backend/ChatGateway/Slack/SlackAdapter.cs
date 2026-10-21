using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Slack over Socket Mode (ADR 0111): the gateway opens a websocket OUT to Slack, so there is no Events API webhook, no
/// request signature and no <c>url_verification</c> bootstrap. Each user brings their own Slack app: a bot token
/// (<c>xoxb-</c>) for the Web API and an app-level token (<c>xapp-</c>, scope <c>connections:write</c>) for the socket.
///
/// <list type="bullet">
/// <item><b>Every envelope is acked</b>, ignored ones included, before it is looked at. Slack redelivers an unacked
/// envelope and disables the app's event subscription after sustained failures (Hermes learned this the hard way).</item>
/// <item>A <c>disconnect</c> envelope (<c>warning</c>, <c>refresh_requested</c>: Slack recycles a socket about hourly)
/// reconnects inside the session: replies go over the Web API, so a reply mid-stream keeps working. Only
/// <c>link_disabled</c> (Socket Mode turned off) is permanent. A socket that fails ends the session, and the supervisor
/// reconnects it with backoff.</item>
/// <item>Slack has no bot typing indicator outside its AI-apps surface, so typing is a no-op.</item>
/// </list>
/// </summary>
public sealed class SlackAdapter(SlackWebApi api, ILogger<SlackAdapter> logger) : IChatPlatformAdapter
{
    // chat.update caps `text` at 4,000 characters (msg_too_long), which is also Slack's "best results" length, so every
    // frame fits an edit. chat.postMessage allows about one message per second per channel and chat.update is Tier 3,
    // so edits are paced like Telegram's. A leading '/' is taken by Slack's composer as a slash command, hence '!'.
    private static readonly ChatPlatformCapabilities SlackCapabilities = new(
        MaxMessageLength: 4000,
        MaxChunks: 10,
        SupportsEdit: true,
        MinEditInterval: TimeSpan.FromMilliseconds(1200),
        TypingRefresh: TimeSpan.FromSeconds(30),
        CommandPrefix: '!');

    /// <summary>
    /// The Slack app manifest a user pastes into "Create an app → From a manifest". It lives here, next to the parser
    /// that depends on it, and <c>docs/integrations/chat-apps/README.md</c> quotes it verbatim (pinned by
    /// <c>ChatAppsGuide_QuotesTheSlackManifest</c>). Socket Mode on; the Messages tab writable so the owner can DM the
    /// bot; the four message events plus <c>app_mention</c>; <c>files:read</c> for phase 1c's downloads.
    /// </summary>
    public const string AppManifest = """
        display_information:
          name: Agent 42
        features:
          app_home:
            messages_tab_enabled: true
            messages_tab_read_only_enabled: false
          bot_user:
            display_name: Agent 42
            always_online: true
        oauth_config:
          scopes:
            bot:
              - app_mentions:read
              - channels:history
              - chat:write
              - files:read
              - groups:history
              - im:history
              - mpim:history
        settings:
          event_subscriptions:
            bot_events:
              - app_mention
              - message.channels
              - message.groups
              - message.im
              - message.mpim
          interactivity:
            is_enabled: false
          org_deploy_enabled: false
          socket_mode_enabled: true
          token_rotation_enabled: false
        """;

    /// <summary>
    /// A memory guard on one envelope. Slack's event payloads are a few kilobytes; one past this is not read, but it is
    /// still acked, from the <c>envelope_id</c> in its first or last bytes (Slack puts <c>payload</c> first).
    /// </summary>
    public const int MaxEnvelopeBytes = 8 << 20;

    public string Kind => ChatPlatformKind.Slack;

    public ChatPlatformCapabilities Capabilities => SlackCapabilities;

    public async Task<ChatBotIdentity> ValidateAsync(ChatCredentials credentials, CancellationToken cancellationToken)
    {
        if (SlackEvents.LooksLikeUserToken(credentials.BotToken))
        {
            throw new ChatAuthException("That is a user token (xoxp-). Paste the Bot User OAuth Token, which starts with xoxb-.");
        }

        if (!SlackEvents.LooksLikeBotToken(credentials.BotToken))
        {
            throw new ChatAuthException("That doesn't look like a Slack bot token. Copy the Bot User OAuth Token (xoxb-…) from OAuth & Permissions.");
        }

        if (!SlackEvents.LooksLikeAppToken(credentials.AppToken))
        {
            throw new ChatAuthException("Slack also needs an app-level token (xapp-…) with the connections:write scope, from Basic Information → App-Level Tokens.");
        }

        var me = await api.CallAsync(credentials.BotToken, "auth.test", null, cancellationToken);
        if (!me.Ok)
        {
            throw me.IsAuthFailure
                ? new ChatAuthException("Slack rejected the bot token. Reinstall the app to your workspace and copy the token again.")
                : new HttpRequestException($"Slack answered {me.Error ?? me.Status.ToString()}.");
        }

        var socket = await api.CallAsync(credentials.AppToken!, "apps.connections.open", null, cancellationToken);
        if (!socket.Ok)
        {
            throw socket.IsAuthFailure
                ? new ChatAuthException("Slack rejected the app-level token. Check that Socket Mode is on and the token has connections:write.")
                : new HttpRequestException($"Slack answered {socket.Error ?? socket.Status.ToString()}.");
        }

        return new ChatBotIdentity(
            me.Body.GetProperty("user_id").GetString()!,
            me.Body.TryGetProperty("user", out var user) ? user.GetString() ?? "" : "",
            me.Body.TryGetProperty("team_id", out var team) ? team.GetString() : null);
    }

    public Task<IChatPlatformSession> ConnectAsync(ChatConnectionRuntime runtime, IChatInboundSink sink, CancellationToken cancellationToken)
        => Task.FromResult<IChatPlatformSession>(new SlackSession(api, runtime, sink, logger, cancellationToken));
}

/// <summary>A live Slack connection: its Socket Mode receive loop is <see cref="Completion"/>.</summary>
internal sealed partial class SlackSession : IChatPlatformSession
{

    // A socket Slack refreshes sooner than this counts as a quick refresh; two in a row start spacing reconnects out.
    private static readonly TimeSpan QuickRefresh = TimeSpan.FromSeconds(30);

    // Threads the bot has replied in, so a follow-up there without a mention still reaches it (bounded, in-process).
    private const int MaxTrackedThreads = 1000;

    private readonly SlackWebApi _api;
    private readonly ChatConnectionRuntime _runtime;
    private readonly IChatInboundSink _sink;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, byte> _trackedThreads = new(StringComparer.Ordinal);

    public SlackSession(SlackWebApi api, ChatConnectionRuntime runtime, IChatInboundSink sink, ILogger logger, CancellationToken cancellationToken)
    {
        _api = api;
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

    private string BotToken => _runtime.Credentials.BotToken;

    public async Task<ChatSendResult> SendAsync(ChatTarget target, string markdown, ChatTextFormat format, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object> { ["channel"] = target.ChatId, ["text"] = Text(markdown, format), ["unfurl_links"] = false };
        if (target.ThreadId is { } thread)
        {
            body["thread_ts"] = thread;
            Track(target.ChatId, thread);
        }

        return (await _api.CallAsync(BotToken, "chat.postMessage", body, cancellationToken)).ToSendResult();
    }

    public async Task<ChatSendResult> EditAsync(
        ChatTarget target, string messageId, string markdown, ChatTextFormat format, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object> { ["channel"] = target.ChatId, ["ts"] = messageId, ["text"] = Text(markdown, format) };
        var result = (await _api.CallAsync(BotToken, "chat.update", body, cancellationToken)).ToSendResult();
        return result.Success ? ChatSendResult.Ok(messageId) : result;
    }

    // Slack offers bots no typing indicator over the Web API or Socket Mode.
    public Task SendTypingAsync(ChatTarget target, CancellationToken cancellationToken) => Task.CompletedTask;

    // Phase 1c (ADR 0111): outbound images go through files.getUploadURLExternal there.
    public Task<ChatSendResult> UploadImageAsync(
        ChatTarget target, byte[] bytes, string fileName, string mediaType, CancellationToken cancellationToken)
        => Task.FromResult(ChatSendResult.Fail(ChatSendErrorKind.Unknown));

    public async Task<ChatDownloadedFile?> DownloadAsync(ChatInboundFile file, long maxBytes, CancellationToken cancellationToken)
        => await _api.DownloadFileAsync(BotToken, file.FileRef, maxBytes, cancellationToken) is { } bytes
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

    private static string Text(string markdown, ChatTextFormat format)
        => format == ChatTextFormat.Formatted ? SlackText.ToMrkdwn(markdown) : SlackText.Escape(markdown);

    private void Track(string channel, string thread)
    {
        if (_trackedThreads.Count >= MaxTrackedThreads)
        {
            _trackedThreads.Clear();
        }

        _trackedThreads.TryAdd(channel + ":" + thread, 0);
    }

    private bool IsTracked(string channel, string thread) => _trackedThreads.ContainsKey(channel + ":" + thread);

    // One socket after another, for as long as Slack asks us to refresh. Ends with an exception the supervisor
    // classifies: ChatAuthException stops, anything else reconnects with backoff.
    // A refresh is normally about hourly; back-to-back ones (a flapping workspace) are spaced out so the loop can't
    // hammer apps.connections.open, which the supervisor's backoff never sees because the session doesn't end.
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var quickRefreshes = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var url = await OpenSocketUrlAsync(cancellationToken);
            var openedAt = Environment.TickCount64;
            using (var socket = new ClientWebSocket())
            {
                await socket.ConnectAsync(url, cancellationToken);
                await ReceiveUntilDisconnectAsync(socket, cancellationToken);
                await CloseQuietlyAsync(socket);
            }

            quickRefreshes = Environment.TickCount64 - openedAt < QuickRefresh.TotalMilliseconds ? quickRefreshes + 1 : 0;
            if (quickRefreshes > 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(quickRefreshes * 2, 30)), cancellationToken);
            }
        }
    }

    private async Task<Uri> OpenSocketUrlAsync(CancellationToken cancellationToken)
    {
        var open = await _api.CallAsync(_runtime.Credentials.AppToken ?? "", "apps.connections.open", null, cancellationToken);
        if (open.Ok && open.Body.TryGetProperty("url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri))
        {
            return uri;
        }

        throw open.IsAuthFailure
            ? new ChatAuthException("Slack rejected the app-level token. Paste a new xapp- token with connections:write.")
            : new HttpRequestException($"Slack could not open a socket ({open.Error ?? open.Status.ToString()}).");
    }

    // Returns when Slack sends a disconnect envelope (refresh); throws when the socket fails or closes on its own.
    private async Task ReceiveUntilDisconnectAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await ReceiveFrameAsync(socket, cancellationToken)
                ?? throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "Slack closed the socket.");
            if (frame.Text is null)
            {
                // Too large to keep: still acked, from the id in its first or last bytes, or Slack redelivers it.
                if (EnvelopeIdPattern().Match(frame.Head + "\n" + frame.Tail) is { Success: true } id)
                {
                    await AckAsync(socket, id.Groups["id"].Value, cancellationToken);
                }

                continue;
            }

            using var envelope = ParseOrNull(frame.Text);
            if (envelope is not null && await HandleEnvelopeAsync(socket, envelope.RootElement, cancellationToken))
            {
                return;
            }
        }
    }

    // Acks, then acts on one envelope. True when it was a disconnect (refresh) envelope.
    private async Task<bool> HandleEnvelopeAsync(ClientWebSocket socket, JsonElement root, CancellationToken cancellationToken)
    {
        if (root.TryGetProperty("envelope_id", out var id) && id.GetString() is { Length: > 0 } envelopeId)
        {
            // Ack FIRST and always, ignored envelopes included: Slack redelivers what is not acked, and disables the
            // subscription after sustained failures.
            await AckAsync(socket, envelopeId, cancellationToken);
        }

        switch (root.TryGetProperty("type", out var type) ? type.GetString() : null)
        {
            case "disconnect" when root.TryGetProperty("reason", out var reason) && reason.GetString() == "link_disabled":
                throw new ChatAuthException("Socket Mode was turned off for this Slack app. Turn it on, then save the connection again.");
            case "disconnect":
                return true;
            case "events_api" when root.TryGetProperty("payload", out var payload):
                await AcceptOneAsync(payload, cancellationToken);
                return false;
            default:
                return false;
        }
    }

    // A payload that cannot be parsed or handled is logged and skipped: it is already acked, and throwing would drop
    // the socket for every later message.
    private async Task AcceptOneAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        try
        {
            if (SlackEvents.Parse(payload, _runtime.Connection.BotUserId, IsTracked) is { } message)
            {
                await _sink.AcceptAsync(_runtime, message, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "A Slack event on connection {ConnectionId} could not be handled; skipping it.", _runtime.ConnectionId);
        }
    }

    private static Task AckAsync(ClientWebSocket socket, string envelopeId, CancellationToken cancellationToken)
        => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { envelope_id = envelopeId }), WebSocketMessageType.Text, true, cancellationToken);

    // One whole text message, or (too large to keep) only its first and last bytes; null when the socket closed.
    internal static async Task<SlackFrame?> ReceiveFrameAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        var tail = new Queue<byte[]>();
        var oversized = false;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            oversized |= message.Length + result.Count > SlackAdapter.MaxEnvelopeBytes;
            if (!oversized)
            {
                message.Write(buffer, 0, result.Count);
            }
            else
            {
                tail.Enqueue(buffer[..result.Count]);
                if (tail.Count > 4)
                {
                    tail.Dequeue();
                }
            }

            if (result.EndOfMessage)
            {
                var kept = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                return oversized
                    ? new SlackFrame(null, kept[..Math.Min(kept.Length, 64 * 1024)], Encoding.UTF8.GetString(tail.SelectMany(b => b).ToArray()))
                    : new SlackFrame(kept, "", "");
            }
        }
    }

    private static JsonDocument? ParseOrNull(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("\"envelope_id\"\\s*:\\s*\"(?<id>[^\"]{1,200})\"")]
    private static partial Regex EnvelopeIdPattern();

    private static async Task CloseQuietlyAsync(ClientWebSocket socket)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "refresh", timeout.Token);
        }
        catch
        {
            // The old socket is being replaced; how it closes doesn't matter.
        }
    }
}

/// <summary>One received frame: its text, or for one too large to keep, only its first and last bytes.</summary>
internal sealed record SlackFrame(string? Text, string Head, string Tail);
