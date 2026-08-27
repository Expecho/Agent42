namespace Agent42.Backend;

/// <summary>
/// One projected attribute — a span tag, a log attribute, or a metric point's dimension. Name/value
/// pairs are projected to strings at capture time rather than kept as <c>object?</c>: the captured
/// value must not alias anything the OpenTelemetry SDK may reuse (see <see cref="LiveLogProcessor"/>),
/// and the console renders strings anyway.
/// </summary>
public sealed record CapturedTag(string Name, string Value);

/// <summary>
/// An <c>ActivityEvent</c>, projected. Without this an exception recorded as an event
/// (<c>Activity.AddException</c>, which is how most exceptions reach a span) is silently dropped —
/// status and description alone are not enough to debug one.
/// </summary>
public sealed record CapturedSpanEvent(DateTimeOffset Timestamp, string Name, IReadOnlyList<CapturedTag> Tags);

/// <summary>
/// A finished <c>Activity</c>, projected into an immutable POCO (ADR 0054).
///
/// <para><see cref="ParentSpanId"/> is what makes a trace tree reconstructable in the viewer, and it is
/// load-bearing rather than decorative: spans arrive <b>child-first</b> — a child completes, and is
/// therefore captured, before its parent — so the console must build the tree from the buffer and must
/// never assume arrival order.</para>
/// </summary>
public sealed record CapturedSpan(
    long Seq,
    DateTimeOffset Timestamp,
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Name,
    string Kind,
    double DurationMs,
    string Status,
    string? StatusDescription,
    string Source,
    IReadOnlyList<CapturedTag> Tags,
    IReadOnlyList<CapturedSpanEvent> Events);

/// <summary>
/// A log record, projected into an immutable POCO.
///
/// <para><b>Every field here is a copy, made inside <c>OnEnd</c>.</b> The OpenTelemetry SDK pools and
/// reuses <c>LogRecord</c> instances, so retaining one hands the buffer a reference whose contents are
/// overwritten by the next log line — measured, and silent (see <see cref="LiveLogProcessor"/>).</para>
/// </summary>
public sealed record CapturedLog(
    long Seq,
    DateTimeOffset Timestamp,
    string Category,
    string Level,
    string Message,
    string? Exception,
    string? TraceId,
    string? SpanId,
    IReadOnlyList<CapturedTag> Attributes,
    IReadOnlyList<string> Scopes);

/// <summary>
/// One data point of a metric stream: the tag set that identifies it plus its current value.
/// <see cref="Value"/> is nullable on purpose — an unrecognised <c>MetricType</c> is recorded as a null
/// value with a <see cref="Detail"/> naming it, never as a silent <c>NaN</c>.
/// </summary>
public sealed record CapturedMetricPoint(IReadOnlyList<CapturedTag> Tags, double? Value, string? Detail);

/// <summary>An instrument's current state, as of the last export interval.</summary>
public sealed record CapturedMetric(
    string Name,
    string Meter,
    string? Unit,
    string? Description,
    string Type,
    string Temporality,
    IReadOnlyList<CapturedMetricPoint> Points);

/// <summary>
/// Spans newer than the requested cursor, plus the cursor to send next. <see cref="Dropped"/> is the
/// number of spans the ring has evicted since capture began, so the console can say "you are seeing a
/// window" rather than implying completeness.
/// </summary>
public sealed record TracesResponse(IReadOnlyList<CapturedSpan> Spans, long Cursor, long Dropped);

/// <summary>
/// Logs newer than the requested cursor, plus the cursor to send next.
///
/// <para><see cref="EffectiveMinimumLevel"/> exists because the Logs tab is <b>legitimately empty in
/// production</b>: <c>appsettings.json</c> sets <c>Logging:LogLevel:Default</c> to <c>Warning</c>, and
/// <c>ILogger</c> category filtering runs <i>before</i> the OpenTelemetry logging processor — so
/// Information and Debug records never reach <see cref="LiveLogProcessor"/> at all and no buffer setting
/// can recover them. Reporting the level lets an empty tab read as correct rather than broken (ADR 0054).</para>
/// </summary>
public sealed record LogsResponse(
    IReadOnlyList<CapturedLog> Logs,
    long Cursor,
    long Dropped,
    string EffectiveMinimumLevel);

/// <summary>
/// The current metric snapshot. Metrics are replaced wholesale each export interval rather than
/// accumulated in a ring: the console wants current values, not a history of them.
/// </summary>
public sealed record MetricsResponse(IReadOnlyList<CapturedMetric> Metrics, DateTimeOffset? CapturedAt);

/// <summary>
/// The budgets the live telemetry buffer is held to, bound from the <c>Diagnostics</c> section of
/// configuration (the <see cref="PushConfig"/> shape: a record built once at startup and registered as
/// a singleton, never re-read at the call site).
///
/// <para><b>The byte budget is the one that matters, and it is a real OOM bound rather than tidiness.</b>
/// The chat client's OpenTelemetry wrapper runs with <c>EnableSensitiveData = true</c>, so a gen_ai span
/// carries <c>gen_ai.input.messages</c> — the <i>entire conversation history</i>, serialised to JSON, on
/// every single turn — plus <c>gen_ai.tool.definitions</c> (every MCP and skill schema). A ring bounded
/// by item count alone would hold tens to hundreds of megabytes on a long RAG conversation, inside a
/// backend container capped at 1 GiB that also shares its replica budget with the SearXNG sidecar. So the
/// ring is bounded by <b>both</b> a count and a byte total, and an oversize single value is truncated on
/// the way in — the same discipline as <c>WebToolLimits</c> and <c>SkillToolLimits</c>.</para>
/// </summary>
public sealed record DiagnosticsConfig(
    int MaxSpans,
    int MaxLogs,
    int MaxTagValueLength,
    long MaxBufferBytes,
    int MaxItemsPerPoll,
    int MetricExportIntervalMs)
{
    /// <summary>The configuration section these values are bound from.</summary>
    public const string SectionName = "Diagnostics";

    /// <summary>Appended to a captured value that was cut off at <see cref="MaxTagValueLength"/>.</summary>
    public const string TruncationMarker = "…[truncated]";

    /// <summary>
    /// The defaults, used when the <c>Diagnostics</c> section is absent — which is the normal state
    /// offline, since <c>tools/Agent42.DevHost</c> has no <c>appsettings.json</c> at all.
    /// </summary>
    public static DiagnosticsConfig Default { get; } = new(
        MaxSpans: 500,
        MaxLogs: 1000,
        MaxTagValueLength: 4 * 1024,
        MaxBufferBytes: 16L * 1024 * 1024,
        MaxItemsPerPoll: 200,
        // The SDK default is 60s, which leaves a LIVE console's Metrics tab blank for a minute after
        // start (measured offline: 28 instruments, first export at ~60s). This reader is in-process and
        // exports nowhere, so a shorter interval costs a projection and no I/O. It does NOT affect the
        // OTLP reader ServiceDefaults registers, which keeps its own interval.
        MetricExportIntervalMs: 10_000);
}
