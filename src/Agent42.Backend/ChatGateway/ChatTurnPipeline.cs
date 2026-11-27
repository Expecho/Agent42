using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Everything between a parsed chat message and an Agent42 turn (ADR 0111): dedupe, who may talk, commands, which
/// conversation, the per-conversation queue, the detached run, and the streamed reply. Adapters hand messages in
/// through <see cref="IChatInboundSink"/> and own none of this (Hermes' runner/adapter boundary).
///
/// <para>The order of the gates is the design:</para>
/// <list type="number">
/// <item>Bots and the bot's own echoes are dropped (the self-reply loop).</item>
/// <item>The message id is claimed, so a redelivery never runs twice.</item>
/// <item>Before pairing, only a DM carrying the code is acted on. After pairing, <b>only the owner's messages</b> start
/// anything, in DMs and groups alike; everyone else is ignored silently and creates no state (Hermes #9337: answering
/// strangers is noise and an info leak).</item>
/// <item>In a group, the bot must be mentioned or replied to (and groups must be enabled on the connection).</item>
/// <item>A command runs inline, ahead of any queued turn, so <c>/stop</c> never waits behind the turn it stops.</item>
/// <item>Anything else is queued on its conversation's FIFO. The queue slot is claimed synchronously, before any
/// await, so a second message cannot race the first (Hermes' busy-guard rule); different conversations run in
/// parallel up to <see cref="MaxConcurrentTurns"/>.</item>
/// </list>
///
/// <para>The queue, the guards and the dedupe are in-process: correct only on a single replica (CLAUDE.md rule 12).</para>
/// </summary>
public sealed partial class ChatTurnPipeline : IChatInboundSink
{
    /// <summary>Turns running at once across every connection.</summary>
    public const int MaxConcurrentTurns = 8;

    /// <summary>Messages one conversation may have waiting behind its running turn.</summary>
    public const int MaxQueuedPerRoute = 20;

    private const string LinkedText = "Linked to Agent42. Send a message to start chatting, or {0}help for the commands.";
    private const string BusyText = "I'm still working through your earlier messages. Send this again in a moment.";
    private const string TextOnlyText = "I can read text, images and documents here, but not that kind of message.";

    /// <summary>
    /// How long a chat waits for one turn before it moves on (the gotcha "Bounding a drained agent turn: cap the WAIT,
    /// never the enumeration"). The turn itself keeps running and persists; the chat is told where its reply will be,
    /// and the conversation's next message and the global slot are freed. Long enough for a research loop (ADR 0082).
    /// </summary>
    public static readonly TimeSpan DefaultMaxTurnWait = TimeSpan.FromMinutes(15);

    private readonly IGrainFactory _grains;
    private readonly InboundDeduper _deduper;
    private readonly ChatSessionRegistry _sessions;
    private readonly ChatReplyStreamer _streamer;
    private readonly Dictionary<string, IChatPlatformAdapter> _adapters;
    private readonly ChatModelCommands _modelCommands;
    private readonly ChatInboundFiles _files;
    private readonly ChatVoiceTranscription _voice;
    private readonly TimeSpan _maxTurnWait;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChatTurnPipeline> _logger;
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentTurns, MaxConcurrentTurns);
    private readonly Dictionary<string, Queue<ChatTurnWork>> _queues = new(StringComparer.Ordinal);
    private readonly Lock _queueGate = new();

    /// <summary>
    /// The model/agent commands, inbound files and voice notes are collaborators (cc-05), each owning its own optional
    /// ports; this keeps dispatch, the dedupe, the per-conversation queue and the turn driver, which are the in-process
    /// single-replica state (CLAUDE.md rule 12) and so stay here.
    /// </summary>
    public ChatTurnPipeline(
        IGrainFactory grains,
        InboundDeduper deduper,
        ChatSessionRegistry sessions,
        ChatReplyStreamer streamer,
        IEnumerable<IChatPlatformAdapter> adapters,
        TimeProvider timeProvider,
        ILogger<ChatTurnPipeline> logger,
        ChatModelCommands modelCommands,
        ChatInboundFiles files,
        ChatVoiceTranscription voice,
        TimeSpan? maxTurnWait = null)
    {
        _maxTurnWait = maxTurnWait ?? DefaultMaxTurnWait;
        _grains = grains;
        _deduper = deduper;
        _sessions = sessions;
        _streamer = streamer;
        _adapters = adapters.ToDictionary(a => a.Kind, StringComparer.Ordinal);
        _timeProvider = timeProvider;
        _logger = logger;
        _modelCommands = modelCommands;
        _files = files;
        _voice = voice;
    }

    public async Task AcceptAsync(ChatConnectionRuntime runtime, ChatInboundMessage message, CancellationToken cancellationToken)
    {
        if (message.SenderIsBot || message.SenderId == runtime.Connection.BotUserId
            || !_deduper.TryClaim(runtime.ConnectionId, message.PlatformMessageId))
        {
            return;
        }

        try
        {
            await AcceptClaimedAsync(runtime, message);
        }
        catch (Exception ex)
        {
            // Let the platform's redelivery in: this message was never acted on.
            _deduper.Release(runtime.ConnectionId, message.PlatformMessageId);
            _logger.LogError(ex, "A chat message on connection {ConnectionId} could not be handled.", runtime.ConnectionId);
        }
    }

    private async Task AcceptClaimedAsync(ChatConnectionRuntime runtime, ChatInboundMessage message)
    {
        // Fresh, not the session's snapshot: the owner may have paired, or changed the target, since it connected.
        var store = _grains.GetGrain<IUserChatConnectionsGrain>(runtime.UserId);
        if (await store.GetAsync(runtime.ConnectionId) is not { } connection)
        {
            return;
        }

        if (!connection.IsPaired)
        {
            await TryPairAsync(runtime, connection, store, message);
            return;
        }

        if (!IsOwner(connection, message))
        {
            return;
        }

        var capabilities = Capabilities(connection);
        var command = ChatCommands.Parse(message.Text, capabilities.CommandPrefix, connection.BotUserName);
        if (!PassesGroupGate(connection, message, command))
        {
            return;
        }

        var work = new ChatTurnWork(runtime.UserId, connection, message, ChatRouteKey.For(message.ChatId, message.ThreadId));
        if (command is not null)
        {
            await RunCommandAsync(work, command);
        }
        else if (string.IsNullOrWhiteSpace(message.Text) && message.Files.Count == 0)
        {
            // A sticker, a location, a poll: nothing a turn can read, and an empty prompt would still mint a conversation
            // and spend a model call.
            await ReplyAsync(work, TextOnlyText);
        }
        else if (!TryEnqueue(work))
        {
            await ReplyAsync(work, BusyText);
        }
    }

    private async Task TryPairAsync(
        ChatConnectionRuntime runtime, ChatConnection connection, IUserChatConnectionsGrain store, ChatInboundMessage message)
    {
        // Pairing is DM-only: a code typed in a group is visible to everyone in it.
        if (message.ChatType != ChatType.Direct)
        {
            return;
        }

        var outcome = await store.TryPairAsync(
            runtime.ConnectionId, message.SenderId, message.SenderWorkspaceId, message.Text, _timeProvider.GetUtcNow());
        if (outcome == ChatPairingOutcome.Paired)
        {
            var work = new ChatTurnWork(runtime.UserId, connection, message, ChatRouteKey.For(message.ChatId, message.ThreadId));
            await ReplyAsync(work, string.Format(LinkedText, Capabilities(connection).CommandPrefix));
        }
    }

    private static bool IsOwner(ChatConnection connection, ChatInboundMessage message)
        => message.SenderId == connection.OwnerPlatformUserId
            && (connection.OwnerWorkspaceId is null || message.SenderWorkspaceId == connection.OwnerWorkspaceId);

    // In a group, only a message addressed to the bot is for it. /new, /stop and /help pass without the mention, or
    // they would be unreachable in a busy group (Hermes).
    private static bool PassesGroupGate(ChatConnection connection, ChatInboundMessage message, ChatCommand? command)
    {
        if (message.ChatType == ChatType.Direct)
        {
            return true;
        }

        return connection.GroupsEnabled
            && (message.MentionsBot || message.RepliesToBot
                || (command is not null && ChatCommands.AllowedWithoutMention.Contains(command.Name)));
    }

    // Claims the conversation's slot synchronously: either this message joins the queue behind a running turn, or it
    // starts the queue's worker. False when the queue is full.
    private bool TryEnqueue(ChatTurnWork work)
    {
        bool start;
        lock (_queueGate)
        {
            if (_queues.TryGetValue(work.QueueKey, out var queue))
            {
                // The head is the running turn, so Count - 1 are waiting.
                if (queue.Count - 1 >= MaxQueuedPerRoute)
                {
                    return false;
                }

                queue.Enqueue(work);
                start = false;
            }
            else
            {
                _queues[work.QueueKey] = new Queue<ChatTurnWork>([work]);
                start = true;
            }
        }

        if (start)
        {
            _ = Task.Run(() => DrainQueueAsync(work.QueueKey));
        }

        return true;
    }

    // The head of a queue is the running turn; it is dequeued only after it finished, so /stop can drop everything
    // behind it without touching it. Nothing may escape this loop: it is fire-and-forget, and a worker that died with
    // its key still in _queues would leave that chat silently dead (every later message joins a queue nobody drains).
    private async Task DrainQueueAsync(string queueKey)
    {
        try
        {
            while (TryPeek(queueKey) is { } work)
            {
                await _concurrency.WaitAsync();
                try
                {
                    await RunTurnSafelyAsync(work);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "A chat turn failed outside its own error handling; moving on.");
                }
                finally
                {
                    _concurrency.Release();
                    lock (_queueGate)
                    {
                        if (_queues.TryGetValue(queueKey, out var queue) && queue.Count > 0)
                        {
                            queue.Dequeue();
                        }
                    }
                }
            }
        }
        finally
        {
            // Normally TryPeek already removed the empty queue; this covers an exit any other way.
            lock (_queueGate)
            {
                if (_queues.TryGetValue(queueKey, out var queue) && queue.Count == 0)
                {
                    _queues.Remove(queueKey);
                }
            }
        }
    }

    // The queue's head, or null after removing an empty queue (so the next message starts a new worker).
    private ChatTurnWork? TryPeek(string queueKey)
    {
        lock (_queueGate)
        {
            if (_queues.TryGetValue(queueKey, out var queue) && queue.Count > 0)
            {
                return queue.Peek();
            }

            _queues.Remove(queueKey);
            return null;
        }
    }

    // Drops every message waiting behind the running turn of a conversation. Returns how many were dropped.
    private int ClearWaiting(string queueKey)
    {
        lock (_queueGate)
        {
            if (!_queues.TryGetValue(queueKey, out var queue) || queue.Count <= 1)
            {
                return 0;
            }

            var dropped = queue.Count - 1;
            var running = queue.Dequeue();
            queue.Clear();
            queue.Enqueue(running);
            return dropped;
        }
    }

    // Name → handler, never an if-chain (Hermes' dispatch table). Each returns the reply text.
    private IReadOnlyDictionary<string, Func<ChatTurnWork, string, Task<string>>> CommandHandlers => field ??= new Dictionary<string, Func<ChatTurnWork, string, Task<string>>>(StringComparer.Ordinal)
    {
        [ChatCommands.New] = NewAsync,
        [ChatCommands.Stop] = StopAsync,
        [ChatCommands.Model] = (work, args) => _modelCommands.ModelAsync(work, args, Capabilities(work.Connection).CommandPrefix),
        [ChatCommands.Agent] = (work, args) => _modelCommands.AgentAsync(work, args, Capabilities(work.Connection).CommandPrefix),
        [ChatCommands.SetHome] = (work, _) => _modelCommands.SetHomeAsync(work),
        [ChatCommands.Status] = (work, _) => _modelCommands.StatusAsync(work),
        [ChatCommands.Help] = (work, _) => Task.FromResult(ChatCommands.HelpText(Capabilities(work.Connection).CommandPrefix)),
    };

    private async Task RunCommandAsync(ChatTurnWork work, ChatCommand command)
    {
        string reply;
        try
        {
            reply = await CommandHandlers[command.Name](work, command.Arguments);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat command {Command} failed.", command.Name);
            reply = "That command failed. Try again in a moment.";
        }

        await ReplyAsync(work, reply);
    }

    private async Task<string> NewAsync(ChatTurnWork work, string arguments)
    {
        await work.Route(_grains).StartNewAsync();
        return "Started a new conversation.";
    }

    private async Task<string> StopAsync(ChatTurnWork work, string arguments)
    {
        var dropped = ClearWaiting(work.QueueKey);
        if ((await work.Route(_grains).GetAsync()).ConversationId is { } conversationId)
        {
            await _grains.GetGrain<IAgentGrain>($"{work.UserId}/{conversationId}").StopAsync();
        }

        return dropped == 0 ? "Stopped." : $"Stopped, and dropped {dropped} waiting message{(dropped == 1 ? "" : "s")}.";
    }

    private ChatPlatformCapabilities Capabilities(ChatConnection connection) => _adapters[connection.Platform].Capabilities;

    private ChatReplyDestination Destination(ChatTurnWork work)
        => new(() => _sessions.Get(work.Connection.Id), work.Target, Capabilities(work.Connection));

    // A short, single-message reply (command answers, pairing, notices): formatted, plain if the platform rejects the
    // markup, time-boxed, and never throwing.
    private async Task ReplyAsync(ChatTurnWork work, string text)
    {
        try
        {
            var buffer = new ChatReplyBuffer();
            buffer.Apply(new AgentStreamEvent(AgentStreamEventType.TextDelta, text));
            buffer.Complete();
            var destination = Destination(work) with { Capabilities = Capabilities(work.Connection) with { SupportsEdit = false } };
            await _streamer.StreamAsync(destination, buffer, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A notice that cannot be sent is logged, never rethrown into a queue worker or an adapter's receive loop.
            _logger.LogWarning(ex, "A chat notice could not be sent on connection {ConnectionId}.", work.Connection.Id);
        }
    }
}

/// <summary>One owner message bound for a conversation, with where its reply goes.</summary>
/// <param name="Transcript">A voice note's transcript, once it has been read (phase 1c).</param>
internal sealed record ChatTurnWork(
    string UserId, ChatConnection Connection, ChatInboundMessage Message, string RouteKey, string? Transcript = null)
{
    public string QueueKey => Connection.Id + "\n" + RouteKey;

    /// <summary>
    /// Replies go to the same chat and thread. In a group the reply quotes the triggering message, so the conversation
    /// is legible to everyone there; in a DM it does not need to.
    /// </summary>
    public IChatRouteGrain Route(IGrainFactory grains)
        => grains.GetGrain<IChatRouteGrain>(ChatRouteKey.GrainKey(UserId, Connection.Id, RouteKey));

    public ChatTarget Target => new(
        Message.ChatId,
        Message.ThreadId,
        Message.ChatType == ChatType.Group ? Message.PlatformMessageId : null);
}
