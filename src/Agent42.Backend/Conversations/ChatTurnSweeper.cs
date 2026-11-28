using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The one provider-neutral retention mechanism for chat-turn logs (ADR 0153, D10): every <see cref="ChatTurnOptions.SweepInterval"/>
/// it deletes the blobs older than <see cref="ChatTurnOptions.Retention"/> from the durable store AND the in-memory incognito
/// twin. It replaces a Bicep lifecycle rule because it also covers self-host, the grain-side retention sweep cannot reach this
/// store, and an incognito turn's text must not sit in memory for good. Deletes are idempotent, so sweeps on several replicas
/// are harmless. A running turn stamps its blobs every heartbeat and flush, so it is never old enough to go.
/// </summary>
public sealed class ChatTurnSweeper(ChatTurnStores stores, ChatTurnOptions options, TimeProvider time, ILogger<ChatTurnSweeper> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.SweepInterval, time);
        try
        {
            do
            {
                await SweepOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Host stopping.
        }
    }

    /// <summary>One sweep of both stores. A failure is logged and never stops the loop. Returns how many blobs went.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = time.GetUtcNow() - options.Retention;
        var deleted = 0;
        foreach (var store in new[] { stores.Durable, stores.Incognito })
        {
            try
            {
                deleted += await store.SweepAsync(cutoff, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Chat turn sweep failed; it will run again at the next interval");
            }
        }

        return deleted;
    }
}
