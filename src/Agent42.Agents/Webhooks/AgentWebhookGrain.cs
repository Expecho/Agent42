using Microsoft.Extensions.Logging;
using Orleans.Concurrency;

namespace Agent42.Agents;

/// <summary>
/// The outbound webhook of one stored agent (ADR 0094). Rides the shared "agent-state" storage provider, so it needs
/// no Cosmos container of its own.
///
/// <para><b>Reentrant, with two gates of its own.</b> See <see cref="IAgentWebhookGrain"/> for why the config calls
/// must never queue behind a ~40 s delivery. Because every call interleaves, the grain serializes what must not
/// overlap itself: <see cref="_sendGate"/> keeps deliveries one at a time (in no promised order: Orleans does not order messages, and the log is in completion order),
/// and <see cref="_writeGate"/> keeps <c>WriteStateAsync</c> calls from racing each other's ETag. A delivery reads its
/// target when its turn at the send gate comes, and re-checks <see cref="_configVersion"/> AFTER the send: a save or
/// clear that interleaved meanwhile wins, and the delivery drops its log write.</para>
/// </summary>
[Reentrant]
public sealed class AgentWebhookGrain : Grain, IAgentWebhookGrain
{
    private readonly IPersistentState<AgentWebhookState> _state;

    private readonly IAgentWebhookSender _sender;

    private readonly ILogger<AgentWebhookGrain> _logger;

    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    // Bumped by every save and clear, so a delivery can tell its target changed under it.
    private int _configVersion;

    // Deliveries accepted and not yet finished, the one in flight included.
    private int _pending;

    public AgentWebhookGrain(
        [PersistentState("agent-webhook", AgentGrain.StorageProviderName)] IPersistentState<AgentWebhookState> state,
        IAgentWebhookSender sender,
        ILogger<AgentWebhookGrain> logger)
    {
        _state = state;
        _sender = sender;
        _logger = logger;
    }

    public Task<AgentWebhookStatus> GetAsync()
        => Task.FromResult(new AgentWebhookStatus(
            _state.State.Url,
            !string.IsNullOrEmpty(_state.State.SecretCiphertext),
            Enumerable.Reverse(_state.State.Deliveries).ToList()));

    public async Task SaveAsync(string url, string? secretCiphertext)
    {
        _configVersion++;
        _state.State.Url = url;
        if (secretCiphertext is not null)
        {
            _state.State.SecretCiphertext = secretCiphertext;
        }

        await WriteAsync();
    }

    public async Task ClearAsync()
    {
        // Every agent delete calls this (ADR 0094), most for agents that never had a webhook: write nothing then.
        if (_state.State is { Url: null, SecretCiphertext: null, Deliveries.Count: 0 })
        {
            return;
        }

        _configVersion++;
        _state.State.Url = null;
        _state.State.SecretCiphertext = null;
        _state.State.Deliveries = [];
        await WriteAsync();
    }

    public async Task DeliverAsync(AgentWebhookEvent evt)
    {
        if (string.IsNullOrEmpty(_state.State.Url))
        {
            return;
        }

        if (_pending >= WebhookLimits.MaxPendingDeliveries)
        {
            _logger.LogWarning(
                "Webhook {GrainKey} dropped a {Type} delivery: {Pending} are already pending",
                this.GetPrimaryKeyString(), evt.Type, _pending);
            await AppendAsync(Record(Guid.NewGuid().ToString(), evt,
                new WebhookDeliveryResult(null, WebhookLimits.DroppedBacklogError, 0, TimeSpan.Zero)));
            return;
        }

        _pending++;
        await _sendGate.WaitAsync();
        try
        {
            await SendAndLogAsync(evt);
        }
        catch (Exception ex)
        {
            // OneWay: there is no caller to surface this to, and the turn it reports on is already persisted.
            _logger.LogError(ex, "Webhook {GrainKey} failed a delivery", this.GetPrimaryKeyString());
        }
        finally
        {
            _sendGate.Release();
            _pending--;
        }
    }

    public async Task<WebhookDeliveryResult?> SendTestAsync(string agentName)
    {
        // Not behind the send gate: the owner is waiting on the button, and a backlog of real deliveries could hold it
        // for minutes. A test carries no conversation content, so its order relative to them does not matter.
        var key = this.GetPrimaryKeyString();
        var agentId = key[(key.LastIndexOf('/') + 1)..];
        var evt = new AgentWebhookEvent(AgentWebhookEvent.Test, agentId, agentName, DateTimeOffset.UtcNow);
        return await SendAndLogAsync(evt);
    }

    // One send to the target as it stands now, logged unless the configuration changed while it was in flight.
    private async Task<WebhookDeliveryResult?> SendAndLogAsync(AgentWebhookEvent evt)
    {
        if (_state.State is not { Url: { Length: > 0 } url, SecretCiphertext: { Length: > 0 } secret })
        {
            return null;
        }

        var version = _configVersion;
        var deliveryId = Guid.NewGuid().ToString();
        var result = await _sender.SendAsync(new AgentWebhookTarget(url, secret), deliveryId, evt);

        if (version != _configVersion)
        {
            _logger.LogInformation(
                "Webhook {GrainKey} was changed during delivery {DeliveryId}; its log entry is dropped",
                this.GetPrimaryKeyString(), deliveryId);
            return result;
        }

        _logger.LogInformation(
            "Webhook {GrainKey} delivered {Type} {DeliveryId}: status {Status}, error {Error}, {Attempts} attempt(s) in {Elapsed} ms",
            this.GetPrimaryKeyString(), evt.Type, deliveryId, result.StatusCode, result.Error, result.Attempts,
            (long)result.Duration.TotalMilliseconds);
        await AppendAsync(Record(deliveryId, evt, result));
        return result;
    }

    private static WebhookDeliveryRecord Record(string deliveryId, AgentWebhookEvent evt, WebhookDeliveryResult result)
        => new()
        {
            At = DateTimeOffset.UtcNow,
            DeliveryId = deliveryId,
            Type = evt.Type,
            Trigger = evt.Trigger,
            ConversationId = evt.ConversationId,
            StatusCode = result.StatusCode,
            Error = result.Error,
            Attempts = result.Attempts,
            DurationMs = (long)result.Duration.TotalMilliseconds,
        };

    // The log is changed INSIDE the write gate, so it never moves under a write another call has in flight.
    private Task AppendAsync(WebhookDeliveryRecord record) => WriteAsync(() =>
    {
        var log = _state.State.Deliveries;
        log.Add(record);
        if (log.Count > WebhookLimits.MaxDeliveryLog)
        {
            log.RemoveRange(0, log.Count - WebhookLimits.MaxDeliveryLog);
        }
    });

    private async Task WriteAsync(Action? change = null)
    {
        await _writeGate.WaitAsync();
        try
        {
            change?.Invoke();
            await _state.WriteStateAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
