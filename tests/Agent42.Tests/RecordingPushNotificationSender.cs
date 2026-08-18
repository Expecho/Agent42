using System.Collections.Concurrent;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// An <see cref="IPushNotificationSender"/> that records every send instead of hitting a push service —
/// the behavioural-test analogue of <see cref="FakeGeocoder"/>. Registered as a concrete singleton in
/// <see cref="TestSiloConfigurator"/> so a test can fetch it from the silo container
/// (<c>GetSiloServiceProvider(...).GetRequiredService&lt;RecordingPushNotificationSender&gt;()</c>) and
/// assert what the async reply paths pushed (ADR 0028: reminder + agent-run send; live chat does not).
///
/// Every silo configurator that hosts an <see cref="AgentGrain"/> must register an
/// <see cref="IPushNotificationSender"/>, or that host's grain activations fail — the other four
/// configurators register <see cref="NullPushNotificationSender"/>.
/// </summary>
public sealed class RecordingPushNotificationSender : IPushNotificationSender
{
    private readonly ConcurrentQueue<(string UserId, PushMessage Message)> _sends = new();

    /// <summary>Every recorded send, in order.</summary>
    public IReadOnlyList<(string UserId, PushMessage Message)> Sends => _sends.ToArray();

    public Task SendAsync(string userId, PushMessage message, CancellationToken cancellationToken = default)
    {
        _sends.Enqueue((userId, message));
        return Task.CompletedTask;
    }
}
