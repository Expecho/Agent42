using System.Text;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Merges runs of consecutive text deltas in a batch about to be sent, keeping the LAST sequence number (ADR 0153, D3). This
/// only ever happens on the way OUT: the store keeps raw events, because a client's resume point is the last <c>seq</c> it saw,
/// and a store that coalesced on write would replay text the client already shows after a reconnect. A client sees a merged
/// event whole or not at all, so its last seq is always a real boundary and resuming "after N" never duplicates text.
/// </summary>
public static class ReplayCoalescer
{
    /// <summary>The events with <c>Seq &gt; after</c>, with consecutive text (or reasoning) deltas of the same kind merged into one.</summary>
    public static IReadOnlyList<LoggedEvent> Coalesce(IReadOnlyList<LoggedEvent> events, long after)
    {
        var merged = new List<LoggedEvent>();
        StringBuilder? run = null;
        LoggedEvent? runHead = null;

        foreach (var logged in events.Where(e => e.Seq > after))
        {
            if (IsPlainDelta(logged.Event) && runHead is { } head && head.Event.Type == logged.Event.Type)
            {
                run!.Append(logged.Event.Text);
                runHead = logged;
                continue;
            }

            Flush(merged, run, runHead);
            if (IsPlainDelta(logged.Event))
            {
                run = new StringBuilder(logged.Event.Text);
                runHead = logged;
            }
            else
            {
                run = null;
                runHead = null;
                merged.Add(logged);
            }
        }

        Flush(merged, run, runHead);
        return merged;
    }

    // A delta that carries nothing but text, so merging two cannot lose a field.
    private static bool IsPlainDelta(AgentStreamEvent evt)
        => evt.Type is AgentStreamEventType.TextDelta or AgentStreamEventType.Reasoning;

    private static void Flush(List<LoggedEvent> merged, StringBuilder? run, LoggedEvent? last)
    {
        if (run is not null && last is { } tail)
        {
            merged.Add(new LoggedEvent(tail.Seq, tail.Event with { Text = run.ToString() }));
        }
    }
}
