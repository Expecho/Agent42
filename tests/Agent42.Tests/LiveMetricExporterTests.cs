using System.Diagnostics.Metrics;
using Agent42.Backend;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Agent42.Tests;

/// <summary>
/// The metric half of the live console capture (ADR 0054).
///
/// <para><b><see cref="MetricExporter_HandlesEveryMetricType"/> is the point of this file.</b>
/// <c>MetricType</c> has eight members, not the five that come to mind, and the three extra ones are not
/// exotic: <c>dotnet.process.memory.working_set</c> and two GC instruments from
/// <c>AddRuntimeInstrumentation</c> are all <c>LongSumNonMonotonic</c>. A switch missing those arms
/// compiles and reports <c>NaN</c> for them — data-shaped nonsense rather than an error.</para>
///
/// <para>Every one of the eight is produced here by a <b>real instrument driven through the real SDK</b>
/// — including <c>ExponentialHistogram</c>, which needs a base-2 view to appear at all. That is
/// deliberate: a test that listed the handled members itself would only be testing its own list, and
/// would pass against the very implementation this file exists to reject.</para>
/// </summary>
[Collection("LiveTelemetry")]
public sealed class LiveMetricExporterTests
{
    private sealed class NoopExporter : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch) => ExportResult.Success;
    }

    [Fact]
    public void MetricExporter_HandlesEveryMetricType()
    {
        const string meterName = "Agent42.Tests.Metrics.AllTypes";
        using var meter = new Meter(meterName);
        var buffer = new LiveTelemetryBuffer(DiagnosticsConfig.Default);

        var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(meterName)
            // The only way to get an ExponentialHistogram out of the SDK: a Histogram instrument
            // reshaped by a base-2 view.
            .AddView("exponential.histogram", new Base2ExponentialBucketHistogramConfiguration())
            .AddReader(new PeriodicExportingMetricReader(new LiveMetricExporter(buffer)))
            .Build();

        using (provider)
        {
            meter.CreateCounter<long>("long.sum").Add(3);
            meter.CreateCounter<double>("double.sum").Add(2.5);
            meter.CreateUpDownCounter<long>("long.sum.nonmonotonic").Add(-7);
            meter.CreateUpDownCounter<double>("double.sum.nonmonotonic").Add(-1.5);
            meter.CreateObservableGauge("long.gauge", () => 11L);
            meter.CreateObservableGauge("double.gauge", () => 4.25);
            meter.CreateHistogram<double>("histogram").Record(9.0);
            meter.CreateHistogram<double>("exponential.histogram").Record(6.0);

            provider.ForceFlush();
        }

        var metrics = buffer.Metrics.Metrics.ToDictionary(m => m.Name);

        // EXHAUSTIVE BY CONSTRUCTION: the eight instruments above really did produce eight distinct
        // MetricType members, so the per-metric assertion below covers the whole enum. If a future SDK
        // adds a ninth member this assertion still holds and the new member simply is not exercised —
        // which is why the "unsupported" detail exists rather than a NaN.
        var observedTypes = buffer.Metrics.Metrics.Select(m => m.Type).Distinct().ToArray();
        Assert.Equal(Enum.GetValues<MetricType>().Length, observedTypes.Length);

        // Every captured point has a REAL value. A missing switch arm shows up here as a null value
        // carrying "unsupported MetricType: …" — the honest failure the exporter produces instead of a
        // silent NaN that reads like data.
        foreach (var metric in buffer.Metrics.Metrics)
        {
            var point = Assert.Single(metric.Points);
            Assert.True(
                point.Value is not null && !double.IsNaN(point.Value.Value),
                $"'{metric.Name}' ({metric.Type}) projected no value: {point.Detail}");
        }

        // The arms a five-member switch gets wrong, asserted by value rather than merely by not-NaN.
        Assert.Equal(3d, metrics["long.sum"].Points.Single().Value);
        Assert.Equal(2.5d, metrics["double.sum"].Points.Single().Value);
        Assert.Equal(-7d, metrics["long.sum.nonmonotonic"].Points.Single().Value);
        Assert.Equal(-1.5d, metrics["double.sum.nonmonotonic"].Points.Single().Value);
        Assert.Equal(11d, metrics["long.gauge"].Points.Single().Value);
        Assert.Equal(4.25d, metrics["double.gauge"].Points.Single().Value);
        Assert.Equal(9d, metrics["histogram"].Points.Single().Value);
        Assert.Equal(6d, metrics["exponential.histogram"].Points.Single().Value);
        Assert.Equal("count=1", metrics["histogram"].Points.Single().Detail);

        // The non-monotonic sums really are DIFFERENT enum members, not the monotonic ones read twice —
        // without this, the value assertions above would pass on a switch that never distinguished them.
        Assert.Equal(MetricType.LongSumNonMonotonic.ToString(), metrics["long.sum.nonmonotonic"].Type);
        Assert.Equal(MetricType.DoubleSumNonMonotonic.ToString(), metrics["double.sum.nonmonotonic"].Type);
        Assert.Equal(MetricType.ExponentialHistogram.ToString(), metrics["exponential.histogram"].Type);
    }

    [Fact]
    public void MetricExporter_CoexistsWithAnotherReader()
    {
        const string meterName = "Agent42.Tests.Metrics.TwoReaders";
        using var meter = new Meter(meterName);
        var buffer = new LiveTelemetryBuffer(DiagnosticsConfig.Default);

        var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(meterName)
            // The pre-existing OTLP-shaped reader ServiceDefaults registers...
            .AddReader(new PeriodicExportingMetricReader(new NoopExporter()))
            // ...and the capture reader added alongside it. Their coexistence is the premise of the
            // whole feature; if a first reader starved a second, this is where it would show.
            .AddReader(new PeriodicExportingMetricReader(new LiveMetricExporter(buffer)))
            .Build();

        using (provider)
        {
            meter.CreateCounter<long>("requests").Add(5);

            // Driven by ForceFlush, never by waiting out an export interval — a timing wait is how a
            // metric test becomes flaky in CI.
            provider.ForceFlush();
        }

        var metric = Assert.Single(buffer.Metrics.Metrics);
        Assert.Equal("requests", metric.Name);
        Assert.Equal(5d, metric.Points.Single().Value);
        Assert.NotNull(buffer.Metrics.CapturedAt);
    }

    [Fact]
    public void MetricExporter_ProjectsTagSetsPerPoint()
    {
        const string meterName = "Agent42.Tests.Metrics.Tags";
        using var meter = new Meter(meterName);
        var buffer = new LiveTelemetryBuffer(DiagnosticsConfig.Default);

        var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(meterName)
            .AddReader(new PeriodicExportingMetricReader(new LiveMetricExporter(buffer)))
            .Build();

        using (provider)
        {
            var counter = meter.CreateCounter<long>("by.route");
            counter.Add(1, new KeyValuePair<string, object?>("http.route", "/api/chat"));
            counter.Add(2, new KeyValuePair<string, object?>("http.route", "/api/agents"));
            provider.ForceFlush();
        }

        var points = Assert.Single(buffer.Metrics.Metrics).Points;

        Assert.Equal(2, points.Count);
        Assert.Contains(points, p => p.Tags.Any(t => t.Value == "/api/chat") && p.Value == 1d);
        Assert.Contains(points, p => p.Tags.Any(t => t.Value == "/api/agents") && p.Value == 2d);
    }
}
