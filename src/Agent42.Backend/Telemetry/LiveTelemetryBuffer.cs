namespace Agent42.Backend;

/// <summary>
/// The in-memory store behind the live telemetry console at <c>/logs</c> (ADR 0054): two bounded rings
/// (spans, logs) plus a wholesale-replaced metric snapshot, read incrementally through a monotonic
/// cursor.
///
/// <para><b>Nothing here may block, allocate unboundedly, or throw.</b> Every writer is an OpenTelemetry
/// <c>OnEnd</c> / <c>Export</c> callback running inline on the export path, so a slow buffer would
/// back-pressure a chat turn — the same rule <c>UsageSpanProcessor</c> follows. Appends take a short lock
/// and drop the oldest entry on overflow.</para>
///
/// <para><b>It is bounded by bytes as well as by count</b>, and truncates an oversize value on the way in
/// — see <see cref="DiagnosticsConfig"/> for why a count-only bound is an out-of-memory bug rather than
/// an untidiness. This is also a single-replica structure: <c>infra/main.bicep</c> pins
/// <c>minReplicas = maxReplicas = 1</c>, so one buffer is the whole cluster's view. A future scale-out
/// would make <c>/logs</c> show whichever replica answered the poll.</para>
/// </summary>
public sealed class LiveTelemetryBuffer
{
    private readonly Ring<CapturedSpan> _spans;
    private readonly Ring<CapturedLog> _logs;
    private volatile MetricsResponse _metrics = new([], null);

    public LiveTelemetryBuffer(DiagnosticsConfig config)
    {
        Config = config;

        // The byte budget is shared between the two rings by splitting it, so neither can starve the
        // other: a chatty log stream must not evict the gen_ai spans the console exists to show.
        var halfBudget = Math.Max(1, config.MaxBufferBytes / 2);
        _spans = new Ring<CapturedSpan>(config.MaxSpans, halfBudget, static (span, seq) => span with { Seq = seq });
        _logs = new Ring<CapturedLog>(config.MaxLogs, halfBudget, static (log, seq) => log with { Seq = seq });
    }

    /// <summary>The budgets this buffer enforces. Read by the endpoints so no limit is written twice.</summary>
    public DiagnosticsConfig Config { get; }

    public void AppendSpan(CapturedSpan span)
    {
        var capped = Truncate(span);
        _spans.Append(capped, EstimateBytes(capped));
    }

    public void AppendLog(CapturedLog log)
    {
        var capped = Truncate(log);
        _logs.Append(capped, EstimateBytes(capped));
    }

    /// <summary>Replaces the metric snapshot wholesale — the viewer wants current values, not a history.</summary>
    public void ReplaceMetrics(IReadOnlyList<CapturedMetric> metrics, DateTimeOffset capturedAt) =>
        _metrics = new MetricsResponse(metrics, capturedAt);

    /// <summary>The most recent metric snapshot; empty until the first export interval elapses.</summary>
    public MetricsResponse Metrics => _metrics;

    public TracesResponse SpansSince(long since, int? limit)
    {
        var (items, cursor, dropped) = _spans.Since(since, Clamp(limit), filter: null);
        return new TracesResponse(items, cursor, dropped);
    }

    public LogsResponse LogsSince(long since, int? limit, string? minLevel, string effectiveMinimumLevel)
    {
        var floor = LevelRank(minLevel);
        var (items, cursor, dropped) = _logs.Since(
            since, Clamp(limit), filter: floor is null ? null : log => LevelRank(log.Level) >= floor);
        return new LogsResponse(items, cursor, dropped, effectiveMinimumLevel);
    }

    // A caller-supplied limit may only narrow the configured per-poll cap, never raise it — the same
    // clamp-don't-trust rule as WebToolLimits.MaxFollowedLinks.
    private int Clamp(int? limit) =>
        limit is null or <= 0 ? Config.MaxItemsPerPoll : Math.Min(limit.Value, Config.MaxItemsPerPoll);

    /// <summary>
    /// Severity order, so <c>minLevel</c> filters by rank rather than by string equality. Returns null for
    /// an absent or unrecognised name, which means "no floor" — a diagnostics console must not hide
    /// records because a query parameter was misspelled.
    /// </summary>
    private static int? LevelRank(string? level) => level?.ToLowerInvariant() switch
    {
        "trace" => 0,
        "debug" => 1,
        "information" or "info" => 2,
        "warning" or "warn" => 3,
        "error" => 4,
        "critical" or "fatal" => 5,
        _ => null,
    };

    private CapturedSpan Truncate(CapturedSpan span) => span with
    {
        Tags = Truncate(span.Tags),
        Events = [.. span.Events.Select(evt => evt with { Tags = Truncate(evt.Tags) })],
    };

    private CapturedLog Truncate(CapturedLog log) => log with
    {
        Message = TruncateValue(log.Message),
        Exception = log.Exception is null ? null : TruncateValue(log.Exception),
        Attributes = Truncate(log.Attributes),
        Scopes = [.. log.Scopes.Select(TruncateValue)],
    };

    private IReadOnlyList<CapturedTag> Truncate(IReadOnlyList<CapturedTag> tags) =>
        [.. tags.Select(tag => tag with { Value = TruncateValue(tag.Value) })];

    private string TruncateValue(string value) =>
        value.Length <= Config.MaxTagValueLength
            ? value
            : string.Concat(value.AsSpan(0, Config.MaxTagValueLength), DiagnosticsConfig.TruncationMarker);

    // An estimate, not a measurement: chars x 2 for UTF-16 plus a flat per-entry allowance for the object
    // headers, timestamps and numbers. The budget it feeds is a safety bound, so being roughly right and
    // cheap beats being exact and slow on the export path.
    private const int PerEntryOverheadBytes = 256;

    private static int EstimateBytes(CapturedSpan span) =>
        PerEntryOverheadBytes
        + Weigh(span.Name) + Weigh(span.TraceId) + Weigh(span.SpanId) + Weigh(span.Source)
        + Weigh(span.StatusDescription)
        + span.Tags.Sum(Weigh)
        + span.Events.Sum(evt => Weigh(evt.Name) + evt.Tags.Sum(Weigh));

    private static int EstimateBytes(CapturedLog log) =>
        PerEntryOverheadBytes
        + Weigh(log.Category) + Weigh(log.Message) + Weigh(log.Exception)
        + log.Attributes.Sum(Weigh)
        + log.Scopes.Sum(Weigh);

    private static int Weigh(CapturedTag tag) => Weigh(tag.Name) + Weigh(tag.Value);

    private static int Weigh(string? value) => value is null ? 0 : value.Length * 2;

    /// <summary>
    /// A bounded FIFO with a monotonic sequence. The sequence keeps counting across an eviction, so a
    /// cursor held by a poller that fell behind stays valid and simply skips the window it missed — it is
    /// never an index into the ring.
    /// </summary>
    private sealed class Ring<T>(int maxItems, long maxBytes, Func<T, long, T> stamp)
    {
        private readonly Queue<Entry> _entries = new();
        private readonly Lock _gate = new();
        private long _seq;
        private long _bytes;
        private long _dropped;

        private readonly record struct Entry(long Seq, int Bytes, T Item);

        public void Append(T item, int bytes)
        {
            lock (_gate)
            {
                _seq++;
                _entries.Enqueue(new Entry(_seq, bytes, stamp(item, _seq)));
                _bytes += bytes;
                Evict();
            }
        }

        // Caller holds the lock. Drops oldest-first until BOTH bounds are satisfied; the single-entry
        // guard keeps one oversize entry from emptying the ring (it was already truncated on the way in).
        private void Evict()
        {
            while (_entries.Count > maxItems || (_bytes > maxBytes && _entries.Count > 1))
            {
                _bytes -= _entries.Dequeue().Bytes;
                _dropped++;
            }
        }

        public (IReadOnlyList<T> Items, long Cursor, long Dropped) Since(long since, int limit, Func<T, bool>? filter)
        {
            var items = new List<T>();
            lock (_gate)
            {
                var cursor = since;
                foreach (var entry in _entries)
                {
                    if (entry.Seq <= since)
                    {
                        continue;
                    }

                    if (items.Count >= limit)
                    {
                        break;
                    }

                    // Advance past a filtered-out entry too, so a narrow minLevel does not make every poll
                    // re-scan the same records forever.
                    cursor = entry.Seq;
                    if (filter is null || filter(entry.Item))
                    {
                        items.Add(entry.Item);
                    }
                }

                return (items, Math.Max(cursor, since), _dropped);
            }
        }
    }
}
