using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// How a turn ended as a reader saw it (ADR 0153): the evaluated status (a stale heartbeat reads as interrupted), and whether
/// events are missing from what was delivered. Anything but a plain <see cref="ChatTurnStatus.Completed"/> sends the client to
/// history recovery (ADR 0046, D9), and so does a <see cref="Truncated"/> or <see cref="Degraded"/> log.
/// </summary>
public sealed record TurnEnd(ChatTurnStatus Status, bool Truncated = false, bool Degraded = false)
{
    /// <summary>True when every event of the turn reached the reader and the turn finished: nothing more to recover.</summary>
    public bool IsClean => Status == ChatTurnStatus.Completed && !Truncated && !Degraded;
}

/// <summary>
/// One way of reading a turn's events from <c>after</c> on (ADR 0153, D2): the in-process <see cref="FeedReader"/> or the
/// store's <see cref="StoreReader"/>. Both yield the same sequence for the same turn, batch by batch (a batch is whatever became
/// available at once: the whole replay first, then each growth), and both set <see cref="End"/> once the sequence has ended.
/// </summary>
public interface ITurnEventReader
{
    /// <summary>How the turn ended; null until <see cref="ReadAsync"/> has run to its end.</summary>
    TurnEnd? End { get; }

    IAsyncEnumerable<IReadOnlyList<LoggedEvent>> ReadAsync(long after, CancellationToken cancellationToken);
}
