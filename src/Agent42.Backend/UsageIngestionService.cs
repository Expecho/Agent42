using System.Threading.Channels;
using Agent42.Agents.Telemetry;

namespace Agent42.Backend;

/// <summary>
/// Drains the usage-event channel (<see cref="UsageSpanProcessor"/>) and folds batches into the
/// global rollup grain (ADR 0031). Decouples the OTel export hot path from Cosmos I/O: the processor
/// only enqueues; this service flushes on a fixed cadence so each <c>ApplyAsync</c> (which re-serializes
/// the whole ≤2 MB grain item) is amortized over many spans rather than run per span.
///
/// <para>Backend-only — DevHost has no OTel pipeline, so nothing enqueues offline and the dashboard
/// renders empty there (expected). Best-effort throughout: a failed flush drops its batch and logs,
/// never throwing, so telemetry never destabilizes the host.</para>
/// </summary>
public sealed class UsageIngestionService : BackgroundService
{
    // Flush cadence: comfortably larger than the UI auto-refresh floor, so writes to the single
    // non-reentrant global grain stay infrequent (see the grain's contention note).
    private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(30);
    private const int MaxBatch = 2000;

    private readonly ChannelReader<UsageEvent> _reader;
    private readonly IGrainFactory _grains;
    private readonly ILogger<UsageIngestionService> _logger;
    private readonly TimeSpan _flushInterval;

    // DI uses this constructor (TimeSpan isn't registered, so the greediest resolvable ctor wins).
    public UsageIngestionService(
        ChannelReader<UsageEvent> reader, IGrainFactory grains, ILogger<UsageIngestionService> logger)
        : this(reader, grains, logger, DefaultFlushInterval)
    {
    }

    // A short flush interval makes the drain loop testable without a 30 s wait.
    public UsageIngestionService(
        ChannelReader<UsageEvent> reader, IGrainFactory grains, ILogger<UsageIngestionService> logger,
        TimeSpan flushInterval)
    {
        _reader = reader;
        _grains = grains;
        _logger = logger;
        _flushInterval = flushInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_flushInterval);
        var batch = new List<UsageEvent>(capacity: 256);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(batch);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — attempt one final drain so in-flight events aren't silently lost.
            await FlushAsync(batch);
        }
    }

    private async Task FlushAsync(List<UsageEvent> batch)
    {
        batch.Clear();
        while (batch.Count < MaxBatch && _reader.TryRead(out var usage))
        {
            batch.Add(usage);
        }

        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            await _grains.GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey).ApplyAsync(batch);
        }
        catch (Exception ex)
        {
            // Best-effort: drop the batch rather than let a telemetry write take down the host.
            _logger.LogWarning(ex, "Usage rollup flush failed; dropped {Count} events.", batch.Count);
        }
    }
}
