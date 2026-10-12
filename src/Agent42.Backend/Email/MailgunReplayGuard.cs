using System.Collections.Concurrent;

namespace Agent42.Backend;

/// <summary>
/// Remembers the Mailgun <c>token</c>s already accepted inside <see cref="MailgunWebhook.MaxAge"/>, so a captured
/// signed triple cannot be replayed with a different body (the HMAC does not cover the body — see
/// <see cref="MailgunWebhook"/>). Mailgun's docs recommend exactly this.
///
/// <para>In-process on purpose, and only sound because the app is a single replica (rule 12). It is empty after a
/// restart, which leaves the address token as the remaining gate. Bounded: when full after pruning it refuses, so a
/// flood fails closed rather than growing memory.</para>
/// </summary>
public sealed class MailgunReplayGuard
{
    /// <summary>The most tokens held at once. Personal mail volume is far below this.</summary>
    public const int Capacity = 10_000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    /// <summary>
    /// Claims <paramref name="token"/>: true the first time inside the window, false for a replay (or when full).
    /// </summary>
    public bool TryClaim(string token, DateTimeOffset now)
    {
        if (_seen.Count >= Capacity / 2)
        {
            Prune(now);
        }

        return _seen.Count < Capacity && _seen.TryAdd(token, now + MailgunWebhook.MaxAge + MailgunWebhook.MaxSkew);
    }

    /// <summary>
    /// Gives a claimed token back after an unexpected failure, so Mailgun's retry of the same message is not refused
    /// as a replay. Deliberate rejections (406) keep it: Mailgun never retries those.
    /// </summary>
    public void Release(string token) => _seen.TryRemove(token, out _);

    private void Prune(DateTimeOffset now)
    {
        foreach (var (token, expires) in _seen)
        {
            if (expires <= now)
            {
                _seen.TryRemove(token, out _);
            }
        }
    }
}
