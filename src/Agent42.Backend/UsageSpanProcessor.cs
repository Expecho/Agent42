using System.Diagnostics;
using System.Threading.Channels;
using Agent42.Agents;
using OpenTelemetry;
using Agent42.Agents.Telemetry;

namespace Agent42.Backend;

/// <summary>
/// Ingests the OpenTelemetry gen_ai spans Agent42 already emits into the usage rollups (ADR 0031).
/// Overrides <see cref="OnEnd"/> (usage tags are set at span completion — unlike
/// <see cref="UserTelemetrySpanProcessor"/>, which stamps identity at <c>OnStart</c>), projects the
/// span via <see cref="UsageSpanProjector"/>, and drops the result onto a bounded channel a
/// background service drains.
///
/// <para><b>OnEnd must not block or do I/O</b> — it runs inline on the export path — so this only
/// projects and <see cref="ChannelWriter{T}.TryWrite"/>s. The channel is bounded with drop-on-full:
/// usage data is best-effort telemetry and must never back-pressure a chat turn.</para>
/// </summary>
public sealed class UsageSpanProcessor : BaseProcessor<Activity>
{
    private readonly ChannelWriter<UsageEvent> _writer;
    private readonly string _application;

    public UsageSpanProcessor(ChannelWriter<UsageEvent> writer, string application)
    {
        _writer = writer;
        _application = application;
    }

    public override void OnEnd(Activity activity)
    {
        var usage = UsageSpanProjector.FromActivity(activity, _application);
        if (usage is not null)
        {
            _writer.TryWrite(usage); // bounded + DropWrite: a full channel silently drops (best-effort)
        }
    }
}
