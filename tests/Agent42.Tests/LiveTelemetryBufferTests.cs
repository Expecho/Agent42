using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The bounded rings behind <c>/logs</c> (ADR 0054). Two properties here are the ones that keep a
/// diagnostics console from becoming an outage: the rings are bounded by <b>bytes</b> as well as by item
/// count — a gen_ai span carries the whole conversation as a tag — and the sequence stays monotonic
/// across an eviction, so a poller that fell behind skips the window it missed instead of replaying the
/// ring from the start forever.
/// </summary>
public sealed class LiveTelemetryBufferTests
{
    private static DiagnosticsConfig Config(
        int maxSpans = 100, int maxLogs = 100, int maxTagValue = 1024,
        long maxBytes = 1024 * 1024, int maxPerPoll = 200) =>
        new(maxSpans, maxLogs, maxTagValue, maxBytes, maxPerPoll,
            DiagnosticsConfig.Default.MetricExportIntervalMs);

    private static CapturedSpan Span(string name, params CapturedTag[] tags) =>
        new(0, DateTimeOffset.UnixEpoch, "trace-1", "span-" + name, null, name, "Internal", 1.0,
            "Unset", null, "Agent42.Backend", tags, []);

    private static CapturedLog Log(string message, string level = "Information") =>
        new(0, DateTimeOffset.UnixEpoch, "Test.Category", level, message, null, null, null, [], []);

    [Fact]
    public void Buffer_DropsOldestAndKeepsSequenceMonotonic()
    {
        var buffer = new LiveTelemetryBuffer(Config(maxSpans: 3));

        for (var i = 1; i <= 5; i++)
        {
            buffer.AppendSpan(Span($"s{i}"));
        }

        var page = buffer.SpansSince(0, limit: null);

        // Bounded to three, and it is the OLDEST two that went.
        Assert.Equal(3, page.Spans.Count);
        Assert.Equal(["s3", "s4", "s5"], page.Spans.Select(s => s.Name));

        // The sequence counted straight through the eviction — it is a cursor, never an index into the
        // ring. If it restarted or reused numbers, a poller holding cursor 3 would silently re-read
        // entries it had already seen.
        Assert.Equal([3L, 4L, 5L], page.Spans.Select(s => s.Seq));
        Assert.Equal(5L, page.Cursor);
        Assert.Equal(2L, page.Dropped);
    }

    [Fact]
    public void Since_ReturnsOnlyNewerItems()
    {
        var buffer = new LiveTelemetryBuffer(Config());
        buffer.AppendSpan(Span("first"));
        buffer.AppendSpan(Span("second"));

        var first = buffer.SpansSince(0, limit: null);
        Assert.Equal(["first", "second"], first.Spans.Select(s => s.Name));

        // Polling again with the returned cursor yields nothing — the console is cheap at rest.
        var caughtUp = buffer.SpansSince(first.Cursor, limit: null);
        Assert.Empty(caughtUp.Spans);
        Assert.Equal(first.Cursor, caughtUp.Cursor);

        // ...and picks up only what arrived since.
        buffer.AppendSpan(Span("third"));
        var next = buffer.SpansSince(caughtUp.Cursor, limit: null);
        Assert.Equal(["third"], next.Spans.Select(s => s.Name));
    }

    [Fact]
    public void Since_ClampsTheCallerSuppliedLimitToTheConfiguredCap()
    {
        var buffer = new LiveTelemetryBuffer(Config(maxPerPoll: 2));
        for (var i = 1; i <= 5; i++)
        {
            buffer.AppendSpan(Span($"s{i}"));
        }

        // A caller may narrow the page, never widen it past the configured budget.
        Assert.Equal(2, buffer.SpansSince(0, limit: 50).Spans.Count);
        Assert.Single(buffer.SpansSince(0, limit: 1).Spans);
    }

    [Fact]
    public void Buffer_TruncatesOversizeTagsAndHonoursTheByteBudget()
    {
        // gen_ai.input.messages is the entire conversation serialised to JSON, on every turn.
        var huge = new string('x', 50_000);
        var buffer = new LiveTelemetryBuffer(Config(maxTagValue: 100, maxBytes: 16 * 1024));

        buffer.AppendSpan(Span("chat", new CapturedTag("gen_ai.input.messages", huge)));

        var value = buffer.SpansSince(0, limit: null).Spans.Single().Tags.Single().Value;

        // Cut to the cap and MARKED, so a reader can tell a truncated payload from a short one.
        Assert.Equal(100 + DiagnosticsConfig.TruncationMarker.Length, value.Length);
        Assert.EndsWith(DiagnosticsConfig.TruncationMarker, value, StringComparison.Ordinal);

        // The byte budget binds independently of the item count: 40 spans of ~2 KB of tag text each is
        // far inside maxSpans (100) and far outside the 16 KB budget (8 KB per ring), so the ring must
        // have evicted on bytes alone.
        var chatty = new LiveTelemetryBuffer(Config(maxSpans: 100, maxTagValue: 4096, maxBytes: 16 * 1024));
        for (var i = 0; i < 40; i++)
        {
            chatty.AppendSpan(Span($"s{i}", new CapturedTag("gen_ai.input.messages", new string('y', 1000))));
        }

        var page = chatty.SpansSince(0, limit: null);
        Assert.True(page.Dropped > 0, "The byte budget never bound — the ring is count-bounded only.");
        Assert.True(page.Spans.Count < 40, $"Expected byte-driven eviction; kept all {page.Spans.Count}.");
    }

    [Fact]
    public void Logs_FilterByMinimumLevelAndAdvanceThroughFilteredRecords()
    {
        var buffer = new LiveTelemetryBuffer(Config());
        buffer.AppendLog(Log("chatty", "Debug"));
        buffer.AppendLog(Log("boom", "Error"));
        buffer.AppendLog(Log("also chatty", "Information"));

        var errors = buffer.LogsSince(0, limit: null, minLevel: "Warning", effectiveMinimumLevel: "Information");

        Assert.Equal(["boom"], errors.Logs.Select(l => l.Message));

        // The cursor advanced past the FILTERED-OUT records too. Without this a narrow minLevel makes
        // every poll re-scan the same records forever.
        Assert.Equal(3L, errors.Cursor);
        Assert.Empty(buffer.LogsSince(errors.Cursor, null, "Warning", "Information").Logs);
    }

    [Fact]
    public void Logs_ReportTheEffectiveMinimumLevelSoAnEmptyTabIsLegible()
    {
        var buffer = new LiveTelemetryBuffer(Config());

        // Production sets Logging:LogLevel:Default to Warning and ILogger filtering runs BEFORE the OTel
        // processor, so Information records never reach the buffer at all. The response has to say so.
        var page = buffer.LogsSince(0, null, null, effectiveMinimumLevel: "Warning");

        Assert.Empty(page.Logs);
        Assert.Equal("Warning", page.EffectiveMinimumLevel);
    }

    [Fact]
    public void Metrics_AreReplacedWholesaleRatherThanAccumulated()
    {
        var buffer = new LiveTelemetryBuffer(Config());
        Assert.Empty(buffer.Metrics.Metrics);
        Assert.Null(buffer.Metrics.CapturedAt);

        var first = new CapturedMetric("a", "M", null, null, "LongSum", "Cumulative", []);
        var second = new CapturedMetric("b", "M", null, null, "LongSum", "Cumulative", []);

        buffer.ReplaceMetrics([first], DateTimeOffset.UnixEpoch);
        buffer.ReplaceMetrics([second], DateTimeOffset.UnixEpoch.AddMinutes(1));

        // The viewer wants CURRENT values: the second export replaces the first, it does not append.
        Assert.Equal(["b"], buffer.Metrics.Metrics.Select(m => m.Name));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(1), buffer.Metrics.CapturedAt);
    }
}
