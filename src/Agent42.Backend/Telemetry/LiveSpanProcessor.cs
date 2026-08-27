using System.Diagnostics;
using OpenTelemetry;

namespace Agent42.Backend;

/// <summary>
/// Captures finished <see cref="Activity"/> instances into <see cref="LiveTelemetryBuffer"/> for the
/// live console at <c>/logs</c> (ADR 0054).
///
/// <para>Overrides <see cref="OnEnd"/> only — like <see cref="UsageSpanProcessor"/>, and unlike
/// <see cref="UserTelemetrySpanProcessor"/>, which stamps identity at <c>OnStart</c>. <c>OnEnd</c> runs
/// inline on the export path, so this projects and appends and does nothing else.</para>
///
/// <para><b>Why it projects at all:</b> unlike <c>LogRecord</c>, <c>Activity</c> is <i>not</i> pooled
/// (measured: six captured activities were six distinct references), so holding one would be correct.
/// It is projected anyway, because an <c>Activity</c> retains its whole tag/event/link graph and the
/// buffer is byte-budgeted — a projection is what makes that budget computable.</para>
/// </summary>
public sealed class LiveSpanProcessor(LiveTelemetryBuffer buffer) : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        // The console polling its own API produces a server span per tick, which would fill the buffer
        // with the console watching itself. Mirrors the HealthEndpointPath filter in ServiceDefaults.
        if (LiveTelemetryFilter.IsDiagnosticsActivity(activity))
        {
            return;
        }

        buffer.AppendSpan(Project(activity));
    }

    private static CapturedSpan Project(Activity activity) => new(
        Seq: 0, // stamped by the buffer, which owns the monotonic cursor
        Timestamp: activity.StartTimeUtc,
        TraceId: activity.TraceId.ToString(),
        SpanId: activity.SpanId.ToString(),
        // Null rather than the all-zero span id when there is no parent, so the viewer's tree builder
        // has one unambiguous "this is a root" test instead of two.
        ParentSpanId: activity.ParentSpanId == default ? null : activity.ParentSpanId.ToString(),
        Name: activity.DisplayName,
        Kind: activity.Kind.ToString(),
        DurationMs: activity.Duration.TotalMilliseconds,
        Status: activity.Status.ToString(),
        StatusDescription: activity.StatusDescription,
        Source: activity.Source.Name,
        Tags: Project(activity.TagObjects),
        Events: [.. activity.Events.Select(Project)]);

    // An exception usually reaches a span as an EVENT (Activity.AddException), not as a tag: without
    // this projection the status says "Error" and the reason is gone.
    private static CapturedSpanEvent Project(ActivityEvent evt) =>
        new(evt.Timestamp, evt.Name, Project(evt.Tags));

    private static IReadOnlyList<CapturedTag> Project(IEnumerable<KeyValuePair<string, object?>> tags) =>
        [.. tags.Select(tag => new CapturedTag(tag.Key, tag.Value?.ToString() ?? ""))];
}
