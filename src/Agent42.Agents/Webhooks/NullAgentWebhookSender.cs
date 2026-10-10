namespace Agent42.Agents;

/// <summary>
/// No-op <see cref="IAgentWebhookSender"/> (ADR 0094), registered offline (DevHost, tests that do not observe
/// deliveries). It sends nothing and reports a 200, so the dialog's "Send test" shows a success. DevHost uses it on
/// purpose: the real sender's private-address guard cannot be opted out of for local development.
/// </summary>
public sealed class NullAgentWebhookSender : IAgentWebhookSender
{
    public Task<WebhookDeliveryResult> SendAsync(
        AgentWebhookTarget target, string deliveryId, AgentWebhookEvent evt, CancellationToken cancellationToken = default)
        => Task.FromResult(new WebhookDeliveryResult(200, null, 1, TimeSpan.Zero));
}
