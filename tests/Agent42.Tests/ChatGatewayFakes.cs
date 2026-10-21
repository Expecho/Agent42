using System.Collections.Concurrent;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// An in-memory chat platform (ADR 0111) for the gateway tests: validates tokens by convention, opens sessions that
/// record every send, edit, typing and upload, and lets a test inject inbound messages or end a session.
///
/// <para>Token conventions: <see cref="BadToken"/> is rejected (<see cref="ChatAuthException"/>),
/// <see cref="UnreachableToken"/> cannot be checked (<see cref="HttpRequestException"/>); anything else is valid.</para>
/// </summary>
public sealed class FakeChatPlatformAdapter(string kind = ChatPlatformKind.Telegram) : IChatPlatformAdapter
{
    public const string BadToken = "bad-token";
    public const string UnreachableToken = "unreachable-token";
    public const string BotUserId = "7000000001";
    public const string BotUserName = "agent42_test_bot";

    public string Kind { get; } = kind;

    public ChatPlatformCapabilities Capabilities { get; set; } = new(
        MaxMessageLength: 4096, MaxChunks: 8, SupportsEdit: true,
        MinEditInterval: TimeSpan.FromMilliseconds(50), TypingRefresh: TimeSpan.FromSeconds(4));

    /// <summary>Every session opened, newest last.</summary>
    public ConcurrentQueue<FakeChatSession> Sessions { get; } = new();

    /// <summary>When set, <see cref="ConnectAsync"/> throws it (a connect-time failure).</summary>
    public Exception? ConnectFailure { get; set; }

    public int ConnectAttempts => _connectAttempts;

    private int _connectAttempts;

    public Task<ChatBotIdentity> ValidateAsync(ChatCredentials credentials, CancellationToken cancellationToken)
        => credentials.BotToken switch
        {
            BadToken => throw new ChatAuthException("The platform rejected that bot token."),
            UnreachableToken => throw new HttpRequestException("unreachable"),
            _ => Task.FromResult(new ChatBotIdentity(BotUserId, BotUserName)),
        };

    public Task<IChatPlatformSession> ConnectAsync(
        ChatConnectionRuntime runtime, IChatInboundSink sink, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _connectAttempts);
        if (ConnectFailure is { } failure)
        {
            throw failure;
        }

        var session = new FakeChatSession(runtime, sink);
        Sessions.Enqueue(session);
        return Task.FromResult<IChatPlatformSession>(session);
    }

    /// <summary>The newest session for <paramref name="connectionId"/>.</summary>
    public FakeChatSession? SessionFor(string connectionId)
        => Sessions.LastOrDefault(s => s.Runtime.ConnectionId == connectionId);
}

/// <summary>One recorded outbound operation.</summary>
public sealed record FakeChatSend(string Op, ChatTarget Target, string Text, ChatTextFormat Format, string? MessageId);

/// <summary>A live fake session: records outbound calls; a test injects inbound messages and ends it.</summary>
public sealed class FakeChatSession(ChatConnectionRuntime runtime, IChatInboundSink sink) : IChatPlatformSession
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextMessageId = 1000;

    public ChatConnectionRuntime Runtime { get; } = runtime;

    public ConcurrentQueue<FakeChatSend> Sent { get; } = new();

    public bool Disposed { get; private set; }

    /// <summary>When set, every send/edit waits on it first (a hung platform).</summary>
    public TaskCompletionSource? Hang { get; set; }

    /// <summary>Results to return, in order, before falling back to success.</summary>
    public ConcurrentQueue<ChatSendResult> ScriptedResults { get; } = new();

    public Task Completion => _completion.Task;

    public bool Started { get; private set; }

    public void Start() => Started = true;

    public Task InjectAsync(ChatInboundMessage message) => sink.AcceptAsync(Runtime, message, CancellationToken.None);

    /// <summary>Ends the session the way a dropped connection would.</summary>
    public void Drop(Exception? error = null)
    {
        if (error is null)
        {
            _completion.TrySetResult();
        }
        else
        {
            _completion.TrySetException(error);
        }
    }

    public Task<ChatSendResult> SendAsync(ChatTarget target, string markdown, ChatTextFormat format, CancellationToken cancellationToken)
        => RecordAsync("send", target, markdown, format, null, cancellationToken);

    public Task<ChatSendResult> EditAsync(
        ChatTarget target, string messageId, string markdown, ChatTextFormat format, CancellationToken cancellationToken)
        => RecordAsync("edit", target, markdown, format, messageId, cancellationToken);

    public Task SendTypingAsync(ChatTarget target, CancellationToken cancellationToken)
    {
        Sent.Enqueue(new FakeChatSend("typing", target, "", ChatTextFormat.Plain, null));
        return Task.CompletedTask;
    }

    public Task<ChatSendResult> UploadImageAsync(
        ChatTarget target, byte[] bytes, string fileName, string mediaType, CancellationToken cancellationToken)
        => RecordAsync("image", target, fileName, ChatTextFormat.Plain, null, cancellationToken);

    /// <summary>Inbound files by <see cref="ChatInboundFile.FileRef"/>; a ref not here downloads as <c>null</c>.</summary>
    public ConcurrentDictionary<string, ChatDownloadedFile> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Every <see cref="DownloadAsync"/> call's file ref, in order.</summary>
    public ConcurrentQueue<string> Downloads { get; } = new();

    public Task<ChatDownloadedFile?> DownloadAsync(ChatInboundFile file, long maxBytes, CancellationToken cancellationToken)
    {
        Downloads.Enqueue(file.FileRef);
        return Task.FromResult(Files.TryGetValue(file.FileRef, out var found) && found.Bytes.Length <= maxBytes ? found : null);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _completion.TrySetResult();
        return ValueTask.CompletedTask;
    }

    /// <summary>The visible text messages, in order: each sent message's final text after its edits.</summary>
    public IReadOnlyList<string> VisibleMessages()
    {
        var messages = new List<(string Id, string Text)>();
        foreach (var op in Sent)
        {
            if (op.Op == "send")
            {
                messages.Add((op.MessageId!, op.Text));
            }
            else if (op.Op == "edit")
            {
                var index = messages.FindIndex(m => m.Id == op.MessageId);
                if (index >= 0)
                {
                    messages[index] = (op.MessageId!, op.Text);
                }
            }
        }

        return messages.Select(m => m.Text).ToList();
    }

    private async Task<ChatSendResult> RecordAsync(
        string op, ChatTarget target, string text, ChatTextFormat format, string? messageId, CancellationToken cancellationToken)
    {
        if (Hang is { } hang)
        {
            await hang.Task.WaitAsync(cancellationToken);
        }

        if (ScriptedResults.TryDequeue(out var scripted) && !scripted.Success)
        {
            Sent.Enqueue(new FakeChatSend(op + "-failed", target, text, format, messageId));
            return scripted;
        }

        var id = messageId ?? Interlocked.Increment(ref _nextMessageId).ToString();
        Sent.Enqueue(new FakeChatSend(op, target, text, format, id));
        return ChatSendResult.Ok(id);
    }
}

/// <summary>Records what the management endpoints asked of the gateway.</summary>
public sealed class RecordingChatGatewayControl : IChatGatewayControl
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public HashSet<string> Live { get; } = [];

    public Task RestartAsync(string userId, string connectionId, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue($"restart:{connectionId}");
        return Task.CompletedTask;
    }

    public Task StopAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue($"stop:{connectionId}");
        return Task.CompletedTask;
    }

    public bool IsLive(string connectionId) => Live.Contains(connectionId);
}
