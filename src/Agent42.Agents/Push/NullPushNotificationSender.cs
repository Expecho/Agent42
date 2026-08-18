namespace Agent42.Agents.Push;

/// <summary>
/// No-op <see cref="IPushNotificationSender"/> (ADR 0028). Registered offline (DevHost/tests without the
/// behavioural push test) and in any prod host with no VAPID private key configured — push simply does
/// nothing, and the app boots and runs normally (the VAPID private key is an <b>optional</b> secret, not
/// on the availability path). The same in-memory/degraded shape as <see cref="CoordinatesOnlyGeocoder"/>.
/// </summary>
public sealed class NullPushNotificationSender : IPushNotificationSender
{
    public Task SendAsync(string userId, PushMessage message, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
