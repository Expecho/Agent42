using System.Collections.Concurrent;

namespace Agent42.Backend;

/// <summary>A provider currently benched, for the chain and for config health.</summary>
public sealed record BenchedProvider(string Name, WebSearchFailureKind Kind, DateTimeOffset Until);

/// <summary>
/// In-memory, per-process record of which keyed providers are benched and until when (ADR 0155). Single replica is
/// load-bearing (CLAUDE.md rule 12), so a restart relearns it. Takes a <see cref="TimeProvider"/> so tests control time.
/// </summary>
public sealed class WebSearchCooldowns(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, BenchedProvider> _benched = new(StringComparer.OrdinalIgnoreCase);

    public void Bench(string name, WebSearchFailureKind kind, TimeSpan? retryAfter = null) =>
        _benched[name] = new BenchedProvider(
            name, kind, _time.GetUtcNow() + WebSearchOptions.CooldownFor(kind, retryAfter));

    /// <summary>The bench entry while it is still in force; an expired one is removed and reported as not benched.</summary>
    public bool TryGetBenched(string name, out BenchedProvider benched)
    {
        if (_benched.TryGetValue(name, out var entry))
        {
            if (entry.Until > _time.GetUtcNow())
            {
                benched = entry;
                return true;
            }

            _benched.TryRemove(new KeyValuePair<string, BenchedProvider>(name, entry));
        }

        benched = null!;
        return false;
    }

    public IReadOnlyList<BenchedProvider> Snapshot()
    {
        var now = _time.GetUtcNow();
        return [.. _benched.Values.Where(b => b.Until > now).OrderBy(b => b.Name, StringComparer.Ordinal)];
    }
}
