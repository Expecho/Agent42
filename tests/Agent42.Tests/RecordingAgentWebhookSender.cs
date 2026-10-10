using System.Collections.Concurrent;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// An <see cref="IAgentWebhookSender"/> that records every send instead of POSTing (ADR 0094) — the shape of
/// <see cref="RecordingPushNotificationSender"/>. Registered as a concrete singleton in
/// <see cref="TestSiloConfigurator"/> and <see cref="NoModelSiloConfigurator"/>; the other silo hosts register
/// <see cref="NullAgentWebhookSender"/>.
///
/// <para><see cref="DeliverAsync"/> is <c>[OneWay]</c>, so a test POLLS <see cref="Sends"/> rather than asserting
/// right after the turn. <see cref="Hold"/> makes every send wait until <see cref="Release"/>, which is how a test
/// keeps a delivery in flight while it calls the config methods.</para>
/// </summary>
public sealed class RecordingAgentWebhookSender : IAgentWebhookSender
{
    private readonly ConcurrentQueue<(AgentWebhookTarget Target, string DeliveryId, AgentWebhookEvent Event)> _sends = new();

    private TaskCompletionSource? _hold;

    /// <summary>Every recorded send, in order.</summary>
    public IReadOnlyList<(AgentWebhookTarget Target, string DeliveryId, AgentWebhookEvent Event)> Sends => _sends.ToArray();

    /// <summary>The result every send returns. A 200 unless a test sets it.</summary>
    public WebhookDeliveryResult Result { get; set; } = new(200, null, 1, TimeSpan.FromMilliseconds(5));

    /// <summary>How many sends have started and are waiting on <see cref="Hold"/>.</summary>
    public int Waiting => _waiting;

    private int _waiting;

    /// <summary>Drops every recorded send and any hold. The cluster outlives a single test.</summary>
    public void Reset()
    {
        _sends.Clear();
        Release();
        Result = new(200, null, 1, TimeSpan.FromMilliseconds(5));
    }

    /// <summary>Makes every send wait until <see cref="Release"/>.</summary>
    public void Hold() => _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lets every held send finish.</summary>
    public void Release() => Interlocked.Exchange(ref _hold, null)?.TrySetResult();

    /// <summary>Recorded sends of <paramref name="type"/> for one agent.</summary>
    public IReadOnlyList<AgentWebhookEvent> EventsFor(string agentId, string type = AgentWebhookEvent.TurnCompleted)
        => [.. Sends.Select(s => s.Event).Where(e => e.AgentId == agentId && e.Type == type)];

    public async Task<WebhookDeliveryResult> SendAsync(
        AgentWebhookTarget target, string deliveryId, AgentWebhookEvent evt, CancellationToken cancellationToken = default)
    {
        _sends.Enqueue((target, deliveryId, evt));
        if (_hold is { } hold)
        {
            Interlocked.Increment(ref _waiting);
            try
            {
                await hold.Task;
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }
        }

        return Result;
    }
}
