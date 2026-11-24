using System.Collections.Concurrent;

namespace Agent42.Backend;

/// <summary>
/// A per-agent sliding-window cap on inbound mail to a plain-name address (ADR 0141). Only plain-name agents are
/// limited: their address is guessable, so anyone can send to it, while a random address is a credential.
///
/// <para><b>What it bounds, honestly:</b> cost and noise. It does not bound injection (one email can instruct the
/// agent), and it adds a denial channel: an attacker can use up the hour's quota so the owner's own forwards get 406.
/// In-process like <see cref="MailgunReplayGuard"/> and only sound as a single replica (rule 12); it resets on
/// restart. A limit of 0 means unlimited.</para>
/// </summary>
public sealed class PlainEmailRateLimiter(int maxPerHour)
{
    /// <summary>The default cap per agent per hour.</summary>
    public const int DefaultMaxPerHour = 30;

    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new(StringComparer.Ordinal);

    /// <summary>Counts one message for <paramref name="agentKey"/>: true while under the cap, false once it is reached.</summary>
    public bool TryAcquire(string agentKey, DateTimeOffset now)
    {
        if (maxPerHour <= 0)
        {
            return true;
        }

        var queue = _hits.GetOrAdd(agentKey, static _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() <= now - Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= maxPerHour)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}
