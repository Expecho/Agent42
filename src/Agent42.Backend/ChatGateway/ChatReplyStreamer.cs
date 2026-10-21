using System.Text;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// A turn's reply as it accumulates (ADR 0111). The drain task writes it; the <see cref="ChatReplyStreamer"/> reads
/// snapshots on its own schedule. That separation is the point: the enumeration of <c>ChatAsync</c> never waits on a
/// platform call, so a hung platform can never keep a turn from persisting (the gotcha "Bounding a drained agent turn:
/// cap the WAIT, never the enumeration").
///
/// <para>Only allow-listed events reach it: text, <see cref="AgentStreamEventType.TextReset"/> and images. Reasoning,
/// todos, suggestions, tool calls and the rest never reach a chat: writing them there is egress ADR 0111 does not
/// cover.</para>
/// </summary>
public sealed class ChatReplyBuffer
{
    private readonly StringBuilder _text = new();
    private readonly List<string> _imageIds = [];
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _version;

    /// <summary>Completes when the drain finished, whether or not it succeeded.</summary>
    public Task Completed => _completed.Task;

    /// <summary>The curated failure text, when the turn failed.</summary>
    public string? Failure { get; private set; }

    public void Apply(AgentStreamEvent evt)
    {
        lock (_gate)
        {
            switch (evt.Type)
            {
                case AgentStreamEventType.TextDelta when evt.Text is { Length: > 0 } text:
                    _text.Append(text);
                    _version++;
                    break;
                case AgentStreamEventType.TextReset:
                    // A research loop's later iteration started its answer (ADR 0082): the text so far was a draft.
                    _text.Clear();
                    _version++;
                    break;
                case AgentStreamEventType.Image when evt.AttachmentId is { Length: > 0 } id:
                    _imageIds.Add(id);
                    break;
            }
        }
    }

    public void Complete() => _completed.TrySetResult();

    /// <summary>Ends the reply with curated text instead of the model's. First ending wins (a timeout vs. a late finish).</summary>
    public void Fail(string curatedText)
    {
        lock (_gate)
        {
            if (_completed.Task.IsCompleted)
            {
                return;
            }

            Failure = curatedText;
            _completed.TrySetResult();
        }
    }

    public (string Text, int Version) Snapshot()
    {
        lock (_gate)
        {
            return (_text.ToString(), _version);
        }
    }

    public IReadOnlyList<string> ImageIds()
    {
        lock (_gate)
        {
            return _imageIds.ToList();
        }
    }
}

/// <summary>Where the streamer sends: the connection's live session, resolved per call (it can change on reconnect).</summary>
public sealed record ChatReplyDestination(
    Func<IChatPlatformSession?> Session,
    ChatTarget Target,
    ChatPlatformCapabilities Capabilities);

/// <summary>
/// Turns a <see cref="ChatReplyBuffer"/> into chat messages (ADR 0111), with Hermes' stream-consumer rules:
/// <list type="bullet">
/// <item>Edit-capable platforms get a placeholder at once, then edits of the accumulated text no faster than the
/// platform's interval, skipping an unchanged frame. Intermediate frames are plain text: formatting a half-written
/// <c>**</c> would fail the edit. Only the final frame is formatted, and a format the platform rejects is resent
/// plain.</item>
/// <item>Only the streamer declares the final, and it reconciles by editing the placeholder, never by a fresh send, so
/// a reply never appears twice.</item>
/// <item>An intermediate edit that is rate limited is skipped; three in a row stop the streaming. The final retries a
/// few times, honouring the platform's wait, but never sleeps past <see cref="MaxRetryWait"/>: Hermes once slept a
/// 97-minute flood wait verbatim and froze every platform.</item>
/// <item>"typing…" is refreshed while the turn runs and stopped before the final send.</item>
/// <item>Platforms that cannot edit get typing, then the final text only.</item>
/// <item>Every platform call is time-boxed, so a hung platform delays one reply, never the chat's whole queue.</item>
/// </list>
/// </summary>
public sealed class ChatReplyStreamer(TimeProvider timeProvider, ILogger<ChatReplyStreamer> logger, TimeSpan? callTimeout = null)
{
    /// <summary>The longest the final send will wait for a rate limit before giving up.</summary>
    public static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(30);

    /// <summary>The longest any single platform call may take (30 seconds; tests shrink it).</summary>
    public TimeSpan CallTimeout { get; } = callTimeout ?? TimeSpan.FromSeconds(30);

    private const string Placeholder = "…";
    private const string Cursor = " ▍";
    private const int MaxFinalAttempts = 3;
    private const int MaxConsecutiveFloods = 3;

    public async Task StreamAsync(ChatReplyDestination destination, ChatReplyBuffer buffer, CancellationToken cancellationToken)
    {
        using var typing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // A reply that is already complete (a command answer, a notice) goes straight out: "typing…" would be noise.
        var typingLoop = buffer.Completed.IsCompleted ? Task.CompletedTask : TypeWhileRunningAsync(destination, typing.Token);
        string? messageId = null;
        try
        {
            if (destination.Capabilities.SupportsEdit)
            {
                messageId = await StreamEditsAsync(destination, buffer, cancellationToken);
            }

            await buffer.Completed.WaitAsync(cancellationToken);
        }
        finally
        {
            // Typing stops BEFORE the final send: a stalled acknowledgement otherwise leaves "typing…" on forever.
            await typing.CancelAsync();
            await typingLoop;
        }

        await SendFinalAsync(destination, buffer, messageId, cancellationToken);
    }

    // Sends the placeholder, then edits it while the drain runs. Returns the placeholder's id (null when it could not
    // be sent, in which case the final is sent fresh).
    private async Task<string?> StreamEditsAsync(ChatReplyDestination destination, ChatReplyBuffer buffer, CancellationToken cancellationToken)
    {
        var placeholder = await CallAsync(destination, (s, t) => s.SendAsync(destination.Target, Placeholder, ChatTextFormat.Plain, t), cancellationToken);
        if (!placeholder.Success || placeholder.MessageId is null)
        {
            return null;
        }

        var limit = destination.Capabilities.MaxMessageLength - Cursor.Length - 1;
        var lastVersion = -1;
        var floods = 0;
        while (!buffer.Completed.IsCompleted && floods < MaxConsecutiveFloods)
        {
            await Task.WhenAny(buffer.Completed, Task.Delay(destination.Capabilities.MinEditInterval, timeProvider, cancellationToken));
            var (text, version) = buffer.Snapshot();
            if (buffer.Completed.IsCompleted || version == lastVersion || text.Length == 0)
            {
                continue;
            }

            lastVersion = version;
            var frame = (text.Length > limit ? text[..limit] : text) + Cursor;
            var edited = await CallAsync(destination, (s, t) => s.EditAsync(destination.Target, placeholder.MessageId, frame, ChatTextFormat.Plain, t), cancellationToken);
            floods = edited.ErrorKind == ChatSendErrorKind.RateLimited ? floods + 1 : 0;
            if (!edited.Success && edited.ErrorKind is ChatSendErrorKind.NotFound or ChatSendErrorKind.Forbidden)
            {
                // The placeholder is gone (deleted by the user): stop editing and send the final fresh.
                return null;
            }
        }

        return placeholder.MessageId;
    }

    private async Task SendFinalAsync(ChatReplyDestination destination, ChatReplyBuffer buffer, string? messageId, CancellationToken cancellationToken)
    {
        var text = FinalText(buffer);
        var chunks = ChatMessageChunker.Split(text, destination.Capabilities.MaxMessageLength, destination.Capabilities.MaxChunks);
        for (var i = 0; i < chunks.Count; i++)
        {
            var editTarget = i == 0 ? messageId : null;
            var result = await FinalWithRetryAsync(destination, chunks[i], editTarget, cancellationToken);
            if (!result.Success && editTarget is not null)
            {
                // The placeholder could not take the final: send it as a new message rather than lose it.
                result = await FinalWithRetryAsync(destination, chunks[i], null, cancellationToken);
            }

            if (!result.Success)
            {
                logger.LogWarning("A chat reply part could not be delivered ({Kind}); the rest is dropped.", result.ErrorKind);
                return;
            }
        }
    }

    private static string FinalText(ChatReplyBuffer buffer)
    {
        if (buffer.Failure is { } failure)
        {
            return failure;
        }

        var (text, _) = buffer.Snapshot();
        var images = buffer.ImageIds().Count;
        var note = images == 0 ? "" : $"\n\n_{(images == 1 ? "An image was" : $"{images} images were")} generated. Open the conversation in Agent42 to see {(images == 1 ? "it" : "them")}._";
        return text.Trim().Length == 0 && note.Length == 0 ? "(The agent finished without a reply.)" : (text + note).Trim();
    }

    // Sends or edits one final part: formatted first, plain if the platform rejects the markup, waiting out a rate limit
    // only while the wait is short.
    private async Task<ChatSendResult> FinalWithRetryAsync(
        ChatReplyDestination destination, string text, string? messageId, CancellationToken cancellationToken)
    {
        var format = ChatTextFormat.Formatted;
        var result = ChatSendResult.Fail(ChatSendErrorKind.Unknown);
        for (var attempt = 0; attempt < MaxFinalAttempts; attempt++)
        {
            var currentFormat = format;
            result = await CallAsync(destination, (s, t) => messageId is null
                ? s.SendAsync(destination.Target, text, currentFormat, t)
                : s.EditAsync(destination.Target, messageId, text, currentFormat, t), cancellationToken);
            if (result.Success)
            {
                return result;
            }

            if (result.ErrorKind == ChatSendErrorKind.BadFormat && format == ChatTextFormat.Formatted)
            {
                format = ChatTextFormat.Plain;
                attempt--;
                continue;
            }

            var wait = result.ErrorKind switch
            {
                ChatSendErrorKind.RateLimited => result.RetryAfter ?? TimeSpan.FromSeconds(1),
                ChatSendErrorKind.Transient => TimeSpan.FromSeconds(1),
                _ => (TimeSpan?)null,
            };
            if (wait is null || wait > MaxRetryWait)
            {
                return result;
            }

            await Task.Delay(wait.Value, timeProvider, cancellationToken);
        }

        return result;
    }

    private async Task TypeWhileRunningAsync(ChatReplyDestination destination, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (destination.Session() is { } session)
                {
                    await session.SendTypingAsync(destination.Target, cancellationToken).WaitAsync(CallTimeout, timeProvider, cancellationToken);
                }

                await Task.Delay(destination.Capabilities.TypingRefresh, timeProvider, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Stopped (or the platform hung): typing is cosmetic.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "A typing indicator failed; ignoring.");
        }
    }

    // One time-boxed platform call through the connection's CURRENT session. No session, a timeout and an exception all
    // become a transient failure: the caller decides what that means.
    private async Task<ChatSendResult> CallAsync(
        ChatReplyDestination destination, Func<IChatPlatformSession, CancellationToken, Task<ChatSendResult>> call, CancellationToken cancellationToken)
    {
        if (destination.Session() is not { } session)
        {
            return ChatSendResult.Fail(ChatSendErrorKind.Transient);
        }

        try
        {
            return await call(session, cancellationToken).WaitAsync(CallTimeout, timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("A chat platform call took longer than {Timeout}; giving up on it.", CallTimeout);
            return ChatSendResult.Fail(ChatSendErrorKind.Transient);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A chat platform call failed.");
            return ChatSendResult.Fail(ChatSendErrorKind.Transient);
        }
    }
}
