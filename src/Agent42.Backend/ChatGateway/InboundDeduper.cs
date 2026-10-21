namespace Agent42.Backend;

/// <summary>
/// Remembers recently accepted inbound message ids per connection (ADR 0111), so a platform redelivery (a Slack
/// envelope retry, a Telegram update re-read after a crash, a reconnect replay) never runs a turn twice.
///
/// <para><b>Owned by the gateway, not by an adapter session</b>, so it survives a reconnect: Hermes learned that a cache
/// held by the adapter starts empty on every reconnect, exactly when replays arrive. In-process, so correct only on a
/// single replica (CLAUDE.md rule 12), and lost on restart, which the persisted Telegram cursor covers.</para>
///
/// <para>Claim, then release on failure: the id is recorded <b>before</b> the turn is queued so two concurrent
/// redeliveries cannot both pass, and released again when queuing fails, so the platform's retry is not swallowed.</para>
/// </summary>
public sealed class InboundDeduper(TimeProvider timeProvider)
{
    /// <summary>How long an id is remembered. Longer than any platform's redelivery window we rely on.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    /// <summary>Most ids remembered at once; the oldest are dropped first.</summary>
    public const int MaxEntries = 10_000;

    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTimeOffset At)> _order = new();
    private readonly Lock _gate = new();

    /// <summary>True the first time an id is seen inside <see cref="Window"/>; false for a duplicate.</summary>
    public bool TryClaim(string connectionId, string platformMessageId)
    {
        var key = Key(connectionId, platformMessageId);
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            Evict(now);
            if (_seen.ContainsKey(key))
            {
                return false;
            }

            _seen[key] = now;
            _order.Enqueue((key, now));
            return true;
        }
    }

    /// <summary>Forgets a claimed id, so the platform's retry of a message we failed to queue is accepted.</summary>
    public void Release(string connectionId, string platformMessageId)
    {
        lock (_gate)
        {
            _seen.Remove(Key(connectionId, platformMessageId));
        }
    }

    private void Evict(DateTimeOffset now)
    {
        while (_order.TryPeek(out var oldest) && (now - oldest.At >= Window || _order.Count > MaxEntries))
        {
            _order.Dequeue();
            // Only drop the entry if it is still the one this queue item recorded (a release + re-claim re-adds it).
            if (_seen.TryGetValue(oldest.Key, out var at) && at == oldest.At)
            {
                _seen.Remove(oldest.Key);
            }
        }
    }

    private static string Key(string connectionId, string platformMessageId) => connectionId + "\n" + platformMessageId;
}
