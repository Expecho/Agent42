using System.Collections.Concurrent;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>Backoff and timing knobs for <see cref="ChatGatewayService"/>; tests shrink them.</summary>
public sealed record ChatGatewayTimings(
    TimeSpan BackoffBase, TimeSpan BackoffMax, TimeSpan HealthySessionAge, TimeSpan? ParticipantTimeout = null)
{
    public static readonly ChatGatewayTimings Default = new(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
}

/// <summary>
/// The chat gateway's supervisor (ADR 0111): one per process, it holds the gateway lease and one supervised session
/// per runnable chat connection. The Hermes <c>GatewayRunner</c> shape, minus everything the
/// <see cref="ChatTurnPipeline"/> owns.
///
/// <list type="bullet">
/// <item>Connects nothing until it holds <see cref="IChatGatewayLease"/>, and drops every session the moment a renewal
/// fails (the revision-overlap rule).</item>
/// <item>Starts every connection on <see cref="IChatConnectionDirectoryGrain"/> at boot; the management endpoints call
/// <see cref="RestartAsync"/> / <see cref="StopAsync"/> after each change.</item>
/// <item>A session that drops reconnects with jittered exponential backoff. A <see cref="ChatAuthException"/>, at
/// connect time or as the session's end, is permanent: the connection is faulted with that reason and stops, never
/// retried in a loop against a platform that already said no.</item>
/// </list>
///
/// <para>In-process and single-instance by design (CLAUDE.md rule 12). Turns in flight at shutdown are lost; the
/// stopping token is deliberately not wired into turns (ADR 0111).</para>
/// </summary>
public sealed class ChatGatewayService(
    IGrainFactory grains,
    IEnumerable<IChatPlatformAdapter> adapters,
    IChatGatewaySecretProtector protector,
    IChatInboundSink sink,
    IChatGatewayLease lease,
    IEnumerable<IChatGatewayLeaseParticipant> participants,
    ChatSessionRegistry sessions,
    TimeProvider timeProvider,
    ILogger<ChatGatewayService> logger,
    ChatGatewayTimings? timings = null) : BackgroundService, IChatGatewayControl
{
    private readonly Dictionary<string, IChatPlatformAdapter> _adapters = adapters.ToDictionary(a => a.Kind, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConnectionRunner> _runners = new(StringComparer.Ordinal);
    private readonly ChatGatewayTimings _timings = timings ?? ChatGatewayTimings.Default;
    private readonly IChatGatewayLeaseParticipant[] _participants = participants.ToArray();
    private readonly TimeSpan _participantTimeout = (timings ?? ChatGatewayTimings.Default).ParticipantTimeout ?? TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _holdsLease;
    private volatile bool _acquired;
    private CancellationToken _stopping;

    /// <summary>Whether this instance currently holds the lease and runs the gateway.</summary>
    public bool HoldsLease => _holdsLease;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await lease.AcquireAsync(stoppingToken);
                _acquired = true;
                await RunWhileLeasedAsync(stoppingToken);
                // Renewal failed: the lease is gone, so there is nothing to release. Go back to waiting for it.
                _acquired = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The chat gateway failed; restarting it.");
                await NotifyParticipantsAsync(renewed: false);
                await StopAllAsync();
                await Task.Delay(Backoff(5), timeProvider, stoppingToken);
            }
        }
    }

    // Graceful shutdown releases the lease, so the next revision takes over at once instead of after it expires.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await NotifyParticipantsAsync(renewed: false);
        await StopAllAsync();
        if (_acquired)
        {
            _acquired = false;
            await lease.ReleaseAsync(cancellationToken);
        }
    }

    public async Task RestartAsync(string userId, string connectionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopRunnerAsync(connectionId);
            if (_holdsLease)
            {
                StartRunner(new ChatConnectionRef(userId, connectionId));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopRunnerAsync(connectionId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool IsLive(string connectionId) => _runners.TryGetValue(connectionId, out var runner) && runner.Live;

    private async Task RunWhileLeasedAsync(CancellationToken stoppingToken)
    {
        _holdsLease = true;
        try
        {
            await NotifyParticipantsAsync(renewed: true);
            await StartAllAsync();
            while (await RenewAfterIntervalAsync(stoppingToken))
            {
            }
        }
        finally
        {
            _holdsLease = false;

            // The participants first: a session whose dispose hangs must not delay stopping what guards the lease.
            await NotifyParticipantsAsync(renewed: false);
            await StopAllAsync();
        }
    }

    private async Task<bool> RenewAfterIntervalAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(lease.RenewInterval, timeProvider, stoppingToken);
        if (!await lease.RenewAsync(stoppingToken))
        {
            return false;
        }

        await NotifyParticipantsAsync(renewed: true);
        return true;
    }

    // Concurrent, each bounded, each caught: one hung or throwing participant must not block the others or the supervisor.
    private Task NotifyParticipantsAsync(bool renewed)
        => _participants.Length == 0
            ? Task.CompletedTask
            : Task.WhenAll(_participants.Select(participant => NotifyAsync(participant, renewed)));

    private async Task NotifyAsync(IChatGatewayLeaseParticipant participant, bool renewed)
    {
        // Deliberately not the stopping token: the lost notification must run on shutdown.
        using var timeout = new CancellationTokenSource(_participantTimeout, timeProvider);
        try
        {
            var call = Task.Run(
                () => renewed ? participant.OnLeaseRenewedAsync(timeout.Token) : participant.OnLeaseLostAsync(timeout.Token));
            await call.WaitAsync(_participantTimeout, timeProvider);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Chat gateway lease participant {Participant} failed or timed out on lease {Event}.",
                participant.GetType().Name, renewed ? "renewal" : "loss");
        }
    }

    private async Task StartAllAsync()
    {
        var entries = await grains.GetGrain<IChatConnectionDirectoryGrain>(IChatConnectionDirectoryGrain.Key).ListAsync();
        await _gate.WaitAsync(_stopping);
        try
        {
            foreach (var entry in entries)
            {
                StartRunner(entry);
            }
        }
        finally
        {
            _gate.Release();
        }

        logger.LogInformation("Chat gateway started {Count} connection(s).", entries.Count);
    }

    private async Task StopAllAsync()
    {
        foreach (var connectionId in _runners.Keys.ToList())
        {
            await StopRunnerAsync(connectionId);
        }
    }

    // Callers hold _gate. A connection already running is left alone: boot's StartAllAsync can race a RestartAsync, and
    // overwriting the entry would orphan a live runner (two pollers on one bot, the 409 the lease exists to prevent).
    private void StartRunner(ChatConnectionRef entry)
    {
        if (_runners.ContainsKey(entry.ConnectionId))
        {
            return;
        }

        var runner = new ConnectionRunner(entry, CancellationTokenSource.CreateLinkedTokenSource(_stopping));
        _runners[entry.ConnectionId] = runner;
        runner.Task = Task.Run(() => SuperviseAsync(runner));
    }

    private async Task StopRunnerAsync(string connectionId)
    {
        if (!_runners.TryRemove(connectionId, out var runner))
        {
            return;
        }

        await runner.Cancellation.CancelAsync();
        try
        {
            await runner.Task;
        }
        catch (OperationCanceledException)
        {
            // Expected: the runner was cancelled.
        }
        catch (Exception ex)
        {
            // Never rethrow a runner's failure into a restart, a stop, or the shutdown path.
            logger.LogWarning(ex, "Chat connection {ConnectionId}'s supervisor had failed.", connectionId);
        }

        runner.Cancellation.Dispose();
    }

    // One connection's life: connect, wait for the session to end, back off, repeat, until cancelled, faulted, or the
    // connection is no longer runnable.
    private async Task SuperviseAsync(ConnectionRunner runner)
    {
        var token = runner.Cancellation.Token;
        var attempt = 0;
        while (!token.IsCancellationRequested)
        {
            SessionOutcome outcome;
            try
            {
                outcome = await RunOnceAsync(runner, token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // A grain or storage error while preparing or recording a fault: transient. Back off and try again
                // rather than end supervision with nothing logged and the panel stuck on "connecting".
                logger.LogWarning(ex, "Chat connection {ConnectionId} could not be (re)started; retrying.", runner.Entry.ConnectionId);
                outcome = SessionOutcome.Retry;
            }

            if (outcome is SessionOutcome.Stop)
            {
                return;
            }

            attempt = outcome is SessionOutcome.Healthy ? 0 : attempt + 1;
            await Task.Delay(Backoff(attempt), timeProvider, token);
        }
    }

    private async Task<SessionOutcome> RunOnceAsync(ConnectionRunner runner, CancellationToken token)
    {
        var (userId, connectionId) = (runner.Entry.UserId, runner.Entry.ConnectionId);
        var store = grains.GetGrain<IUserChatConnectionsGrain>(userId);
        if (await PrepareAsync(store, runner.Entry) is not { } prepared)
        {
            return SessionOutcome.Stop;
        }

        IChatPlatformSession? session = null;
        var openedAt = timeProvider.GetUtcNow();
        try
        {
            session = await prepared.Adapter.ConnectAsync(prepared.Runtime, sink, token);
            sessions.Set(connectionId, session);
            session.Start();
            runner.Live = true;
            await session.Completion.WaitAsync(token);
            return timeProvider.GetUtcNow() - openedAt >= _timings.HealthySessionAge ? SessionOutcome.Healthy : SessionOutcome.Retry;
        }
        catch (ChatAuthException ex)
        {
            logger.LogWarning("Chat connection {ConnectionId} stopped: {Reason}", connectionId, ex.Message);
            // Not live from the moment it faulted: a reader that sees the fault must never still see it as connected.
            runner.Live = false;
            await store.RecordFaultAsync(connectionId, ex.Message, timeProvider.GetUtcNow());
            return SessionOutcome.Stop;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return SessionOutcome.Stop;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chat connection {ConnectionId} dropped; reconnecting.", connectionId);
            return SessionOutcome.Retry;
        }
        finally
        {
            runner.Live = false;
            if (session is not null)
            {
                sessions.Remove(connectionId, session);
                await session.DisposeAsync();
            }
        }
    }

    // Everything a session needs, or null (after recording why) when the connection must not run.
    private async Task<(IChatPlatformAdapter Adapter, ChatConnectionRuntime Runtime)?> PrepareAsync(
        IUserChatConnectionsGrain store, ChatConnectionRef entry)
    {
        var connection = await store.GetAsync(entry.ConnectionId);
        if (connection is null || !connection.IsRunnable)
        {
            return null;
        }

        var problem = !_adapters.TryGetValue(connection.Platform, out var adapter)
            ? $"{connection.Platform} isn't available on this Agent42."
            : protector.Unprotect(connection.CredentialsCiphertext) is null
                ? "The saved bot token can no longer be read. Paste it again."
                : null;
        if (problem is not null)
        {
            await store.RecordFaultAsync(entry.ConnectionId, problem, timeProvider.GetUtcNow());
            return null;
        }

        var runtime = new ChatConnectionRuntime(
            entry.UserId,
            connection,
            protector.Unprotect(connection.CredentialsCiphertext)!,
            offset => store.SetPollOffsetAsync(entry.ConnectionId, offset));
        return (adapter!, runtime);
    }

    // Exponential, capped, with "equal jitter" (half fixed, half random) so many bots reconnecting after an outage spread out.
    private TimeSpan Backoff(int attempt)
    {
        var exponential = _timings.BackoffBase.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 16));
        var capped = Math.Min(exponential, _timings.BackoffMax.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped / 2 + Random.Shared.NextDouble() * capped / 2);
    }

    private enum SessionOutcome
    {
        Healthy,
        Retry,
        Stop,
    }

    private sealed class ConnectionRunner(ChatConnectionRef entry, CancellationTokenSource cancellation)
    {
        public ChatConnectionRef Entry { get; } = entry;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Task { get; set; } = Task.CompletedTask;

        public volatile bool Live;
    }
}
