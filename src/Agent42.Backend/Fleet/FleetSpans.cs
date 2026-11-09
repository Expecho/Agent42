using System.Text;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The analyzer's view of the live telemetry buffer (ADR 0122). The buffer is GLOBAL and carries every user's chat text
/// (ADR 0054), so this returns a span only when it carries a <c>user.id</c> tag equal to the caller's: a span without the
/// tag is left out (fail closed), never guessed. Log lines have no such tag, so they are not offered at all. Values are
/// cut, and the whole listing is framed as data.
/// </summary>
public static class FleetSpans
{
    // The tag UserTelemetrySpanProcessor stamps on every span started inside a user's run (ADR 0009/0010).
    private const string UserIdTag = "user.id";

    // The buffer's ring is at most a few hundred spans and a poll returns at most MaxItemsPerPoll, so a few polls drain it.
    private const int MaxPolls = 6;

    /// <summary>Everything the buffer holds, oldest first.</summary>
    public static IReadOnlyList<CapturedSpan> Drain(LiveTelemetryBuffer buffer)
    {
        var all = new List<CapturedSpan>();
        long cursor = 0;
        for (var poll = 0; poll < MaxPolls; poll++)
        {
            var page = buffer.SpansSince(cursor, null);
            if (page.Spans.Count == 0)
            {
                break;
            }

            all.AddRange(page.Spans);
            cursor = page.Cursor;
        }

        return all;
    }

    /// <summary>The newest <paramref name="limit"/> spans of <paramref name="userId"/> that contain <paramref name="search"/>.</summary>
    public static string Select(IReadOnlyList<CapturedSpan> spans, string userId, string? search, int limit)
    {
        var mine = spans.Where(s => IsOwnedBy(s, userId)).ToList();
        var matching = string.IsNullOrWhiteSpace(search) ? mine : mine.Where(s => Contains(s, search.Trim())).ToList();
        var shown = matching.TakeLast(limit).ToList();
        var head = $"{shown.Count} of {matching.Count} matching spans of this user ({mine.Count} of theirs are buffered). "
            + "Spans carry no fleet task id, so match on time, model or error text. Logs are not available.";
        return shown.Count == 0
            ? head
            : head + "\n" + FleetToolLimits.WrapAsData(string.Join('\n', shown.Select(Format)), "spans");
    }

    private static bool IsOwnedBy(CapturedSpan span, string userId)
        => span.Tags.Any(t => t.Name == UserIdTag && string.Equals(t.Value, userId, StringComparison.OrdinalIgnoreCase));

    private static bool Contains(CapturedSpan span, string search)
        => span.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (span.StatusDescription?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || span.Tags.Any(t => t.Value.Contains(search, StringComparison.OrdinalIgnoreCase))
            || span.Events.Any(e => e.Tags.Any(t => t.Value.Contains(search, StringComparison.OrdinalIgnoreCase)));

    private static string Format(CapturedSpan span)
    {
        var text = new StringBuilder()
            .Append($"{span.Timestamp:u} {span.Name} [{span.Kind}] {span.Status}")
            .Append(span.StatusDescription is { Length: > 0 } d ? $" ({FleetAnalyzerLimits.Short(d, FleetAnalyzerLimits.SpanValueChars)})" : "")
            .Append($" {span.DurationMs:N0} ms");
        foreach (var tag in span.Tags.Where(t => t.Name != UserIdTag).Take(14))
        {
            text.Append($"\n  {tag.Name}={FleetAnalyzerLimits.Short(tag.Value, FleetAnalyzerLimits.SpanValueChars)}");
        }

        foreach (var e in span.Events.Take(3))
        {
            var values = string.Join("; ", e.Tags.Take(4).Select(t => $"{t.Name}={FleetAnalyzerLimits.Short(t.Value, FleetAnalyzerLimits.SpanValueChars)}"));
            text.Append($"\n  event {e.Name}: {values}");
        }

        return text.ToString();
    }
}
