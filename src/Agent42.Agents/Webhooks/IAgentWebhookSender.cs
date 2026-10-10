namespace Agent42.Agents;

/// <summary>
/// Signs and POSTs one webhook event (ADR 0094). The interface lives here and the HTTP implementation
/// (<c>HttpAgentWebhookSender</c>) in <c>Agent42.Backend</c> — the <see cref="IPushNotificationSender"/> /
/// <see cref="IGeocoder"/> split. Offline hosts register <see cref="NullAgentWebhookSender"/>. <b>Every host that
/// activates <see cref="AgentWebhookGrain"/> must register one</b> (CLAUDE.md rule 7).
/// </summary>
public interface IAgentWebhookSender
{
    /// <summary>
    /// Sends <paramref name="evt"/> to <paramref name="target"/> under <paramref name="deliveryId"/>, with the
    /// sender's own bounded retry. <b>Never throws</b>: every outcome, including a secret that cannot be decrypted,
    /// comes back as a <see cref="WebhookDeliveryResult"/>.
    /// </summary>
    Task<WebhookDeliveryResult> SendAsync(
        AgentWebhookTarget target, string deliveryId, AgentWebhookEvent evt, CancellationToken cancellationToken = default);
}

/// <summary>
/// Where a delivery goes: the owner's URL and the signing secret <b>as ciphertext</b>. The grain never sees the
/// plaintext secret; the Backend sender unprotects it at send time.
/// </summary>
public sealed record AgentWebhookTarget(string Url, string SecretCiphertext);

/// <summary>
/// The outcome of one delivery. <see cref="StatusCode"/> is the receiver's last answer (null when none came back);
/// <see cref="Error"/> is an error CLASS (<c>timeout</c>, <c>network</c>, <c>blocked-address</c>,
/// <see cref="WebhookLimits.SecretUnavailableError"/>…), never a response body. Crosses the grain boundary as the
/// "Send test" result.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.WebhookDeliveryResult")]
public sealed record WebhookDeliveryResult(
    [property: Id(0)] int? StatusCode,
    [property: Id(1)] string? Error,
    [property: Id(2)] int Attempts,
    [property: Id(3)] TimeSpan Duration)
{
    /// <summary>True when the receiver answered 2xx.</summary>
    public bool Succeeded => StatusCode is >= 200 and < 300;
}
