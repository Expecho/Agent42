using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Agent42.Backend;

/// <summary>
/// Projects each metric export into <see cref="LiveTelemetryBuffer"/>'s snapshot for the live console at
/// <c>/logs</c> (ADR 0054). Runs on its own <c>PeriodicExportingMetricReader</c>, alongside the OTLP
/// reader ServiceDefaults registers — measured: a second reader captures the full instrument set
/// (19 streams, including the <c>AddRuntimeInstrumentation</c> ones) while the first is also running.
///
/// <para>The snapshot is replaced wholesale rather than accumulated: the console wants an instrument's
/// current value, not a history of its exports.</para>
///
/// <para><b>THE TRAP THIS CLASS EXISTS TO AVOID: <c>MetricType</c> has EIGHT members, not five.</b>
/// Beyond the obvious <c>LongSum, DoubleSum, LongGauge, DoubleGauge, Histogram</c> there are
/// <c>ExponentialHistogram, LongSumNonMonotonic</c> and <c>DoubleSumNonMonotonic</c>. A switch covering
/// only the obvious ones falls through to a default and reads <c>NaN</c> for real, useful instruments —
/// measured, <c>dotnet.process.memory.working_set</c>, <c>dotnet.gc.last_collection.heap.size</c> and
/// <c>dotnet.gc.last_collection.memory.committed_size</c> are all <c>LongSumNonMonotonic</c>. A
/// non-monotonic sum is read with the <i>same</i> <c>GetSumLong()</c> / <c>GetSumDouble()</c> accessors;
/// the bug is a missing switch arm, not a missing API. An unrecognised member is recorded as a
/// <b>null value with a detail naming it</b> — never as a silent <c>NaN</c> that reads like data.</para>
/// </summary>
public sealed class LiveMetricExporter(LiveTelemetryBuffer buffer) : BaseExporter<Metric>
{
    public override ExportResult Export(in Batch<Metric> batch)
    {
        var snapshot = new List<CapturedMetric>();
        foreach (var metric in batch)
        {
            snapshot.Add(Project(metric));
        }

        buffer.ReplaceMetrics(snapshot, DateTimeOffset.UtcNow);
        return ExportResult.Success;
    }

    private static CapturedMetric Project(Metric metric) => new(
        metric.Name,
        metric.MeterName,
        metric.Unit,
        metric.Description,
        metric.MetricType.ToString(),
        metric.Temporality.ToString(),
        ProjectPoints(metric));

    // MetricPoint is a ref struct-like readonly struct enumerated by reference, so this is a plain loop
    // rather than LINQ — the point cannot be captured by a lambda.
    private static IReadOnlyList<CapturedMetricPoint> ProjectPoints(Metric metric)
    {
        var points = new List<CapturedMetricPoint>();
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            var (value, detail) = ReadValue(metric.MetricType, in point);
            points.Add(new CapturedMetricPoint(ProjectTags(in point), value, detail));
        }

        return points;
    }

    /// <summary>
    /// Reads a point's current value. <b>Every one of the eight <c>MetricType</c> members has an arm
    /// here</b>; see the class remarks for what a missing arm silently costs.
    /// </summary>
    private static (double? Value, string? Detail) ReadValue(MetricType type, in MetricPoint point) => type switch
    {
        MetricType.LongSum or MetricType.LongSumNonMonotonic => (point.GetSumLong(), null),
        MetricType.DoubleSum or MetricType.DoubleSumNonMonotonic => (point.GetSumDouble(), null),
        MetricType.LongGauge => (point.GetGaugeLastValueLong(), null),
        MetricType.DoubleGauge => (point.GetGaugeLastValueDouble(), null),
        // A histogram has no single current value; the sum is the useful scalar and the count gives it
        // meaning, so the count rides in the detail rather than being dropped.
        MetricType.Histogram or MetricType.ExponentialHistogram =>
            (point.GetHistogramSum(), $"count={point.GetHistogramCount()}"),
        _ => (null, $"unsupported MetricType: {type}"),
    };

    private static IReadOnlyList<CapturedTag> ProjectTags(in MetricPoint point)
    {
        var tags = new List<CapturedTag>();
        foreach (var tag in point.Tags)
        {
            tags.Add(new CapturedTag(tag.Key, tag.Value?.ToString() ?? ""));
        }

        return tags;
    }
}
