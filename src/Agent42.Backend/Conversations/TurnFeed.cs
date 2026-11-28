using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>One event of a turn with the sequence number the pump gave it (gapless from 1).</summary>
public sealed record LoggedEvent(long Seq, AgentStreamEvent Event);

/// <summary>
/// A reader whose resume point fell off the front of the in-process ring (ADR 0153, D14). The tail catches it and carries on
/// from the store, which is the truth.
/// </summary>
public sealed class TurnFeedBehindException() : Exception("The reader fell behind the in-process feed ring.");

/// <summary>
/// The in-process, append-only view of ONE running turn: a bounded ring of its newest events plus a wake signal (ADR 0153,
/// D2, D14). <b>An optimisation only</b>: the originating request reads it for zero added latency and so that live streaming
/// never depends on the store being up, but every fact in it is also in the store, so deleting this type changes latency and
/// resilience and never correctness. Memory per running turn is bounded by <c>maxBytes</c> (always at least the newest event).
/// </summary>
public sealed class TurnFeed(long maxBytes)
{
    private readonly Lock _gate = new();
    private readonly Queue<Entry> _events = new();
    private long _bytes;
    private TaskCompletionSource _signal = NewSignal();
    private ChatTurnStatus? _endStatus;
    private long _lastSeq;

    /// <summary>How the turn ended, or null while it runs.</summary>
    public ChatTurnStatus? EndStatus
    {
        get
        {
            lock (_gate)
            {
                return _endStatus;
            }
        }
    }

    /// <summary>The highest sequence number appended so far, or 0 before the first event.</summary>
    public long LastSeq
    {
        get
        {
            lock (_gate)
            {
                return _lastSeq;
            }
        }
    }

    /// <summary>Adds an event, drops the oldest ones past the byte cap, and wakes every waiting reader.</summary>
    public void Append(LoggedEvent logged, int size)
    {
        TaskCompletionSource woken;
        lock (_gate)
        {
            _events.Enqueue(new Entry(logged, size));
            _lastSeq = logged.Seq;
            _bytes += size;
            while (_bytes > maxBytes && _events.Count > 1)
            {
                _bytes -= _events.Dequeue().Size;
            }

            woken = Swap();
        }

        woken.TrySetResult();
    }

    /// <summary>Marks the end of the turn and wakes every waiting reader. Idempotent.</summary>
    public void Complete(ChatTurnStatus status)
    {
        TaskCompletionSource woken;
        lock (_gate)
        {
            _endStatus ??= status;
            woken = Swap();
        }

        woken.TrySetResult();
    }

    /// <summary>
    /// The events with <c>Seq &gt; after</c> still in the ring, whether the turn has ended, and a task that completes at the
    /// next append or completion. Throws <see cref="TurnFeedBehindException"/> when events above <paramref name="after"/> were
    /// already dropped from the ring.
    /// </summary>
    public FeedSnapshot Read(long after)
    {
        lock (_gate)
        {
            if (_events.Count > 0 && _events.Peek().Logged.Seq > after + 1)
            {
                throw new TurnFeedBehindException();
            }

            return new FeedSnapshot([.. _events.Where(e => e.Logged.Seq > after).Select(e => e.Logged)], _endStatus, _signal.Task);
        }
    }

    // Under the lock: hands out the completed-old signal and installs a fresh one for the next wait.
    private TaskCompletionSource Swap()
    {
        var old = _signal;
        _signal = NewSignal();
        return old;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly record struct Entry(LoggedEvent Logged, int Size);
}

/// <summary>What <see cref="TurnFeed.Read"/> saw under one lock.</summary>
public sealed record FeedSnapshot(IReadOnlyList<LoggedEvent> Events, ChatTurnStatus? EndStatus, Task Wake);
