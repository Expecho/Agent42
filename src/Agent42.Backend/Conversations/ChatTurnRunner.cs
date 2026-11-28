using System.Collections.Concurrent;
using Agent42.Agents;
using Orleans.Runtime;

namespace Agent42.Backend;

/// <summary>A started turn: its id (the <c>X-Turn-Id</c> header) and the originating request's read of it.</summary>
public sealed record StartedTurn(string TurnId, ChatTurnTailSession Session);

/// <summary>
/// The endpoint-facing facade of the resumable chat turn (ADR 0153): <see cref="StartAsync"/> detaches a pump over the grain's
/// stream and hands the originating request a reader of it, and the reattach endpoints read through the same tail. Resolved
/// optionally by the endpoints (the <c>/api/speech</c> pattern), so a host without it keeps the old direct stream.
///
/// <para>The pump runs on the host's <c>ApplicationStopping</c>, never on <c>RequestAborted</c>: a client that leaves does not
/// stop the turn (ADR 0046). It is also a hosted service only so a shutdown waits for the pumps' last writes (the
/// <c>interrupted</c> marker of a graceful rollover); the cancellation itself has already happened at <c>ApplicationStopping</c>.</para>
/// </summary>
public sealed class ChatTurnRunner(
    ChatTurnStores stores, ChatTurnRegistry registry, ChatTurnTail tail, ChatTurnOptions options,
    IReplicaIdentity replica, TimeProvider time, IHostApplicationLifetime lifetime, ILoggerFactory loggers) : IHostedService
{
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly ILogger _logger = loggers.CreateLogger<ChatTurnRunner>();

    /// <summary>How many pumps are still running in this process (tests and shutdown).</summary>
    public int RunningTurns => _running.Count;

    /// <summary>False when <c>ChatTurns:Enabled</c> is off (D7): the endpoint streams the grain straight into the response.</summary>
    public bool Enabled => options.Enabled;

    /// <summary>How often the originating request writes a keep-alive line when the turn is quiet.</summary>
    public TimeSpan KeepAliveInterval => options.KeepAliveInterval;

    /// <summary>
    /// Starts pumping <paramref name="source"/> (the grain's chat stream, not yet enumerated) on a detached task. A store that
    /// cannot even create the turn does not stop it: live streaming never depends on the store (D2), the turn just can't be
    /// rejoined from elsewhere.
    /// </summary>
    public async Task<StartedTurn> StartAsync(string userId, string conversationId, bool ephemeral, IAsyncEnumerable<AgentStreamEvent> source)
    {
        var turnId = Guid.NewGuid().ToString("N");
        var (meta, persisting) = await TryCreateAsync(stores.For(ephemeral), userId, conversationId, turnId);

        // Captured NOW, on the request: the pump has no request, and the usage dashboard and non-owner span redaction read these.
        var telemetry = RequestContext.Get(UserTelemetryContext.RequestContextKey) as UserTelemetryContext;
        var application = RequestContext.Get(UsageSpanProjector.ApplicationContextKey) as string;

        var feed = registry.Add(userId, conversationId, turnId, ephemeral);
        var writer = new ChatTurnWriter(stores.For(ephemeral), userId, conversationId, meta, options, time, loggers.CreateLogger<ChatTurnWriter>(), persisting);
        var pump = new ChatTurnPump(writer, feed, loggers.CreateLogger<ChatTurnPump>(), telemetry, application);

        var id = Guid.NewGuid();
        _running[id] = Task.Run(async () =>
        {
            try
            {
                await pump.RunAsync(source, lifetime.ApplicationStopping);
            }
            catch (Exception ex)
            {
                // The pump classifies everything the grain throws; this is a bug in the pump or the writer. Nothing more to do for a turn
                // that is already on its own task, and it must never become an unobserved task exception.
                _logger.LogError(ex, "A chat turn's pump faulted");
            }
            finally
            {
                registry.EvictAfterGrace(userId, conversationId, turnId, ephemeral);
                _running.TryRemove(id, out _);
            }
        });

        return new StartedTurn(turnId, tail.Open(userId, conversationId, turnId, ephemeral, 0));
    }

    /// <summary>Opens a read of an existing turn from <paramref name="after"/> on.</summary>
    public ChatTurnTailSession Open(string userId, string conversationId, string turnId, bool ephemeral, long after)
        => tail.Open(userId, conversationId, turnId, ephemeral, after);

    /// <summary>The highest seq the turn has right now (the replay-through value of an attach).</summary>
    public Task<long> LastSeqAsync(string userId, string conversationId, string turnId, bool ephemeral, CancellationToken cancellationToken)
        => tail.LastSeqAsync(userId, conversationId, turnId, ephemeral, cancellationToken);

    /// <summary>True when this process owns the turn live, or the store knows it. Keyed by the authenticated user, so it is only ever their own.</summary>
    public async Task<bool> ExistsAsync(string userId, string conversationId, string turnId, bool ephemeral, CancellationToken cancellationToken)
        => registry.TryGet(userId, conversationId, turnId, ephemeral) is not null
            || await stores.For(ephemeral).GetMetaAsync(userId, conversationId, turnId, cancellationToken) is not null;

    /// <summary>The conversation's most recent turn and the status a reader should report for it, or null.</summary>
    public async Task<ActiveTurn?> GetActiveAsync(string userId, string conversationId, bool ephemeral, CancellationToken cancellationToken)
    {
        var store = stores.For(ephemeral);
        return await store.GetActiveAsync(userId, conversationId, cancellationToken) is { } meta
            ? new ActiveTurn(meta.TurnId, store.StatusOf(meta), meta.StartedUtc, meta.Degraded, meta.Truncated)
            : null;
    }

    /// <summary>Deletes the conversation's turn logs from both stores (D11). Called when the conversation is deleted.</summary>
    public async Task PurgeAsync(string userId, string conversationId, CancellationToken cancellationToken = default)
    {
        await stores.Durable.PurgeAsync(userId, conversationId, cancellationToken);
        await stores.Incognito.PurgeAsync(userId, conversationId, cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(_running.Values).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The host's shutdown budget ran out: a turn that did not get its interrupted marker reads as stale instead.
        }
    }

    private async Task<(ChatTurnMeta Meta, bool Persisting)> TryCreateAsync(ChatTurnStore store, string userId, string conversationId, string turnId)
    {
        try
        {
            return (await store.StartAsync(userId, conversationId, turnId, replica.ReplicaId), true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The chat turn store could not create turn {TurnId}; it streams live but cannot be rejoined", turnId);
            var now = time.GetUtcNow();
            return (new ChatTurnMeta(turnId, ChatTurnStatus.Running, now, replica.ReplicaId, now, Degraded: true), false);
        }
    }
}

/// <summary>What <c>GET /api/chat/{id}/turn</c> reports: the newest turn and its evaluated status.</summary>
public sealed record ActiveTurn(string TurnId, ChatTurnStatus Status, DateTimeOffset StartedUtc, bool Degraded, bool Truncated);
