using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// One chat platform's transport and formatting (ADR 0111), the gateway's single seam per platform. Borrowed from Hermes
/// Agent's <c>BasePlatformAdapter</c>, trimmed, with its central rule kept: <b>an adapter owns no auth and no profile
/// state.</b> It connects, parses, sends and formats; who may talk, which conversation a message lands in and what runs
/// is the <see cref="ChatTurnPipeline"/>'s business. One singleton per platform kind, registered in DI.
/// </summary>
public interface IChatPlatformAdapter
{
    /// <summary>The <see cref="ChatPlatformKind"/> this adapter serves.</summary>
    string Kind { get; }

    ChatPlatformCapabilities Capabilities { get; }

    /// <summary>
    /// Checks credentials against the platform and returns the bot identity they belong to. Throws
    /// <see cref="ChatAuthException"/> when the platform rejects them.
    /// </summary>
    Task<ChatBotIdentity> ValidateAsync(ChatCredentials credentials, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a live session for one connection and starts delivering its inbound messages to
    /// <paramref name="sink"/>. The returned session's <see cref="IChatPlatformSession.Completion"/> ends when the
    /// connection drops: faulted with <see cref="ChatAuthException"/> for a permanent failure (the supervisor stops),
    /// anything else for a transient one (the supervisor reconnects with backoff).
    /// </summary>
    Task<IChatPlatformSession> ConnectAsync(ChatConnectionRuntime runtime, IChatInboundSink sink, CancellationToken cancellationToken);
}

/// <summary>A live connection to one bot. Every send reports a <see cref="ChatSendResult"/> instead of throwing.</summary>
public interface IChatPlatformSession : IAsyncDisposable
{
    /// <summary>Completes when the session ends; see <see cref="IChatPlatformAdapter.ConnectAsync"/>.</summary>
    Task Completion { get; }

    /// <summary>
    /// Sends <paramref name="markdown"/>. <see cref="ChatTextFormat.Formatted"/> asks the adapter to convert the
    /// Markdown to the platform's markup; <see cref="ChatTextFormat.Plain"/> sends it as literal text.
    /// </summary>
    Task<ChatSendResult> SendAsync(ChatTarget target, string markdown, ChatTextFormat format, CancellationToken cancellationToken);

    /// <summary>Replaces a sent message's text. Only called when <see cref="ChatPlatformCapabilities.SupportsEdit"/>.</summary>
    Task<ChatSendResult> EditAsync(
        ChatTarget target, string messageId, string markdown, ChatTextFormat format, CancellationToken cancellationToken);

    /// <summary>Shows "typing…" once; the platform clears it after a few seconds, so the caller refreshes it.</summary>
    Task SendTypingAsync(ChatTarget target, CancellationToken cancellationToken);

    /// <summary>Uploads an image as its own message (phase 1c).</summary>
    Task<ChatSendResult> UploadImageAsync(
        ChatTarget target, byte[] bytes, string fileName, string mediaType, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads an inbound file from the platform's pinned host, capped at <paramref name="maxBytes"/>. <c>null</c>
    /// when it is too large, gone, or on a host the adapter does not trust (phase 1c).
    /// </summary>
    Task<ChatDownloadedFile?> DownloadAsync(ChatInboundFile file, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>Where the adapter hands every parsed inbound message. Must return quickly: it never runs the turn inline.</summary>
public interface IChatInboundSink
{
    Task AcceptAsync(ChatConnectionRuntime runtime, ChatInboundMessage message, CancellationToken cancellationToken);
}

/// <summary>Everything an adapter needs to run one connection, resolved by the supervisor at connect time.</summary>
/// <param name="UserId">The Agent42 user who owns the bot.</param>
/// <param name="Connection">A snapshot of the connection when the session was opened.</param>
/// <param name="Credentials">The decrypted credentials. Never logged, never put in a span.</param>
/// <param name="CommitCursorAsync">Persists the platform's resume cursor (Telegram <c>update_id</c> + 1).</param>
public sealed record ChatConnectionRuntime(
    string UserId,
    ChatConnection Connection,
    ChatCredentials Credentials,
    Func<long, Task> CommitCursorAsync)
{
    public string ConnectionId => Connection.Id;
}

/// <summary>A platform's credentials. Serialized to JSON and protected before they reach grain state.</summary>
/// <param name="BotToken">Telegram/Discord bot token, Slack <c>xoxb-</c> bot token.</param>
/// <param name="AppToken">Slack's app-level <c>xapp-</c> token for Socket Mode; unused elsewhere.</param>
public sealed record ChatCredentials(string BotToken, string? AppToken = null);

/// <summary>A credential the platform rejected, or another permanent failure: the supervisor stops, never retries.</summary>
public sealed class ChatAuthException(string message) : Exception(message);

/// <summary>What the gateway needs to know about a platform to stream, split and pace a reply.</summary>
/// <param name="MaxMessageLength">Longest message text, counted in UTF-16 units (<c>string.Length</c>).</param>
/// <param name="MaxChunks">Most messages one reply may be split into before the rest is cut.</param>
/// <param name="SupportsEdit">Whether a sent message can be edited, which is what makes streaming possible.</param>
/// <param name="MinEditInterval">The fastest the streamer may edit one message.</param>
/// <param name="TypingRefresh">How often the "typing…" indicator is refreshed while a turn runs.</param>
/// <param name="CommandPrefix">The character a chat command starts with (<c>/</c>; Slack eats it, so <c>!</c> there).</param>
public sealed record ChatPlatformCapabilities(
    int MaxMessageLength,
    int MaxChunks,
    bool SupportsEdit,
    TimeSpan MinEditInterval,
    TimeSpan TypingRefresh,
    char CommandPrefix = '/');

/// <summary>Whether text goes out converted to the platform's markup or as literal text.</summary>
public enum ChatTextFormat
{
    Plain,
    Formatted,
}

/// <summary>Why a send failed, in the gateway's terms. The streamer reacts to the kind, never to platform error text.</summary>
public enum ChatSendErrorKind
{
    None,
    TooLong,
    BadFormat,
    Forbidden,
    NotFound,
    RateLimited,
    Transient,
    Unknown,
}

/// <summary>The outcome of one send, edit or upload.</summary>
public sealed record ChatSendResult(
    bool Success,
    string? MessageId = null,
    ChatSendErrorKind ErrorKind = ChatSendErrorKind.None,
    TimeSpan? RetryAfter = null)
{
    public static ChatSendResult Ok(string? messageId = null) => new(true, messageId);

    public static ChatSendResult Fail(ChatSendErrorKind kind, TimeSpan? retryAfter = null) => new(false, null, kind, retryAfter);
}

/// <summary>Where a message goes: a chat, optionally a thread in it, optionally as a reply to one message.</summary>
public sealed record ChatTarget(string ChatId, string? ThreadId = null, string? ReplyToMessageId = null);

/// <summary>A direct chat with the bot, or a group/channel it was added to.</summary>
public enum ChatType
{
    Direct,
    Group,
}

/// <summary>
/// One inbound message, normalized (a subset of Hermes' <c>MessageEvent</c> + <c>SessionSource</c>). The adapter fills
/// <see cref="MentionsBot"/> from the platform's own mention entities, never a substring match.
/// </summary>
public sealed record ChatInboundMessage(
    string PlatformMessageId,
    string ChatId,
    ChatType ChatType,
    string? ThreadId,
    string SenderId,
    string? SenderWorkspaceId,
    bool SenderIsBot,
    string Text,
    bool MentionsBot,
    bool RepliesToBot,
    IReadOnlyList<ChatInboundFile> Files,
    DateTimeOffset ReceivedAt);

/// <summary>An inbound attachment as the platform described it; <see cref="FileRef"/> is opaque to everyone but the adapter.</summary>
public sealed record ChatInboundFile(string FileRef, string? FileName, string? DeclaredMediaType, long? Size, bool IsVoiceNote);

/// <summary>A downloaded inbound file.</summary>
public sealed record ChatDownloadedFile(byte[] Bytes, string FileName, string? MediaType);
