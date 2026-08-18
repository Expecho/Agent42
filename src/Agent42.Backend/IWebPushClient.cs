using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Thin testable seam over the web-push library's raw single-subscription send (ADR 0028). Isolating the
/// library behind one interface keeps the choice of package in <see cref="WebPushClientAdapter"/> alone
/// and lets <see cref="WebPushNotificationSender"/>'s fan-out / prune logic be unit-tested with a fake —
/// no real push service, no VAPID keys.
/// </summary>
public interface IWebPushClient
{
    /// <summary>
    /// Sends one RFC 8291-encrypted <paramref name="payload"/> to <paramref name="subscription"/>.
    /// Throws <see cref="PushDeliveryException"/> (carrying the HTTP status) when the push service rejects
    /// the send — the sender uses that status to decide whether to prune (404/410 = gone) or just log.
    /// </summary>
    Task SendAsync(PushSubscription subscription, string payload, CancellationToken cancellationToken);
}

/// <summary>
/// A push send failed with an HTTP status. Carries <see cref="StatusCode"/> so the sender can distinguish
/// a <b>gone</b> subscription (404/410 → prune) from a transient failure (log and move on). The library's
/// own exception type stays inside <see cref="WebPushClientAdapter"/>.
/// </summary>
public sealed class PushDeliveryException : Exception
{
    public PushDeliveryException(int statusCode, Exception innerException)
        : base($"Push delivery failed with HTTP {statusCode}.", innerException) => StatusCode = statusCode;

    /// <summary>The HTTP status the push service returned (404/410 mean the subscription is gone).</summary>
    public int StatusCode { get; }
}
