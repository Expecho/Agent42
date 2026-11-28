using System.Runtime.CompilerServices;

namespace Agent42.Backend;

/// <summary>
/// Reads a turn from its owner's in-process <see cref="TurnFeed"/> (ADR 0153, D2): no store round trip, so the originating
/// request streams with zero added latency and does not depend on the store being up. Throws
/// <see cref="TurnFeedBehindException"/> when the ring no longer holds what the reader needs, and the tail carries on from the store.
/// </summary>
public sealed class FeedReader(TurnFeed feed) : ITurnEventReader
{
    public TurnEnd? End { get; private set; }

    public async IAsyncEnumerable<IReadOnlyList<LoggedEvent>> ReadAsync(
        long after, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = after;
        while (true)
        {
            var snapshot = feed.Read(cursor);
            if (snapshot.Events.Count > 0)
            {
                cursor = snapshot.Events[^1].Seq;
                yield return snapshot.Events;
                continue;
            }

            if (snapshot.EndStatus is { } status)
            {
                // The feed holds only what reached memory; it is the originating request's view, and whether the STORE kept up is
                // not its business. A clean end here means the grain stream ended cleanly.
                End = new TurnEnd(status);
                yield break;
            }

            await snapshot.Wake.WaitAsync(cancellationToken);
        }
    }
}
