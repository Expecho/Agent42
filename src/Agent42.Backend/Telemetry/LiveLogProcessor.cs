using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Agent42.Backend;

/// <summary>
/// Captures log records into <see cref="LiveTelemetryBuffer"/> for the live console at <c>/logs</c>
/// (ADR 0054).
///
/// <para><b>THE TRAP THIS CLASS EXISTS TO AVOID: <c>LogRecord</c> IS POOLED AND REUSED.</b> The naive
/// implementation — keep the <c>LogRecord</c> reference and read its fields later, when the endpoint
/// serialises the buffer — compiles, runs, and is silently, catastrophically wrong. Measured against
/// OpenTelemetry 1.18.0 (then the pin; not re-measured since): two log calls captured by a <c>BaseProcessor&lt;LogRecord&gt;</c> that
/// stored the references yielded <c>count=2 distinctRefs=1</c>, and <i>both</i> buffered entries read
/// back as the <b>second</b> message. Nothing throws; the console just shows the same line twice.</para>
///
/// <para>So every field is <b>copied inside <see cref="OnEnd"/></b>, and <c>Attributes</c> and the scope
/// output are materialised into <i>new</i> lists — the record's own list is part of the pooled buffer,
/// so keeping the <c>IReadOnlyList</c> reference is the same bug wearing a different hat. Values read
/// inside <c>OnEnd</c> are correct; that window is the whole contract.</para>
/// </summary>
public sealed class LiveLogProcessor(LiveTelemetryBuffer buffer) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord record)
    {
        // The console's own polling produces request log lines on every tick — see LiveTelemetryFilter
        // for why this cannot be a category filter.
        if (LiveTelemetryFilter.IsDiagnosticsRecord(record))
        {
            return;
        }

        buffer.AppendLog(Project(record));
    }

    private static CapturedLog Project(LogRecord record) => new(
        Seq: 0, // stamped by the buffer, which owns the monotonic cursor
        Timestamp: record.Timestamp,
        Category: record.CategoryName ?? "",
        Level: record.LogLevel.ToString(),
        // FormattedMessage is null unless IncludeFormattedMessage is set — AddLiveTelemetryCapture sets
        // it, because ServiceDefaults' ConfigureOpenTelemetry (which also sets it) is never called by
        // DevHost. Body is the fallback for a record logged with no template.
        Message: record.FormattedMessage ?? record.Body ?? "",
        Exception: record.Exception?.ToString(),
        // Default (all-zero) ids mean the record was written outside any trace; null keeps the viewer
        // from rendering a jump-to-trace link that leads nowhere.
        TraceId: record.TraceId == default ? null : record.TraceId.ToString(),
        SpanId: record.SpanId == default ? null : record.SpanId.ToString(),
        Attributes: CopyAttributes(record),
        Scopes: CopyScopes(record));

    // A NEW list, not the record's own: record.Attributes points into the pooled buffer.
    private static IReadOnlyList<CapturedTag> CopyAttributes(LogRecord record) =>
        record.Attributes is null
            ? []
            : [.. record.Attributes.Select(a => new CapturedTag(a.Key, a.Value?.ToString() ?? ""))];

    // ForEachScope yields nothing unless IncludeScopes is set (also set by AddLiveTelemetryCapture). The
    // state parameter carries the destination list so the callback stays a static lambda with no capture
    // — allocation on the export path is the thing this whole file is careful about.
    private static IReadOnlyList<string> CopyScopes(LogRecord record)
    {
        var scopes = new List<string>();
        record.ForEachScope(static (scope, state) =>
        {
            var text = scope.Scope?.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                state.Add(text);
            }
        }, scopes);

        return scopes;
    }
}
