using Orleans.Concurrency;

namespace Agent42.Agents;

/// <summary>
/// The outbound webhook of one stored agent (ADR 0094), keyed <c>{userId}/{agentId}</c> (<see cref="AgentWebhookKeys.KeyFor"/>).
/// Its own grain rather than fields on <see cref="AgentDefinition"/>, because <c>GET /api/agents</c> serializes that
/// record verbatim and would echo the secret. Configuration is live: a conversation never stores the URL and fires to
/// whatever this grain holds at delivery time.
///
/// <para><b>The grain is reentrant on purpose.</b> One delivery can take ~40 s, past the 30 s call timeout, so the
/// dialog, Save and the agent delete's awaited <see cref="ClearAsync"/> must never queue behind it. Sends are
/// serialized inside the grain instead, which is also what lets it count its backlog
/// (<see cref="WebhookLimits.MaxPendingDeliveries"/>) — calls still waiting in a non-reentrant mailbox are invisible.</para>
/// </summary>
[Alias("Agent42.Agents.IAgentWebhookGrain")]
public interface IAgentWebhookGrain : IGrainWithStringKey
{
    /// <summary>The URL, whether a secret is stored, and the delivery log newest first. Never the ciphertext.</summary>
    [Alias("GetAsync")]
    Task<AgentWebhookStatus> GetAsync();

    /// <summary>
    /// Stores <paramref name="url"/>, and <paramref name="secretCiphertext"/> when it is non-null (null keeps the
    /// stored secret). The endpoint validates the URL and protects the secret; the grain trusts neither less.
    /// </summary>
    [Alias("SaveAsync")]
    Task SaveAsync(string url, string? secretCiphertext);

    /// <summary>Removes the URL, the secret and the log. A delivery in flight then drops its log write.</summary>
    [Alias("ClearAsync")]
    Task ClearAsync();

    /// <summary>
    /// Delivers one turn event (ADR 0094). <see cref="OneWay"/>: the conversation grain must never wait on a third
    /// party. A no-op when no webhook is configured.
    /// </summary>
    [Alias("DeliverAsync")]
    [OneWay]
    Task DeliverAsync(AgentWebhookEvent evt);

    /// <summary>
    /// Sends a content-free <c>webhook.test</c> event now and returns the result (also logged). Null when no webhook
    /// is configured.
    /// </summary>
    [Alias("SendTestAsync")]
    Task<WebhookDeliveryResult?> SendTestAsync(string agentName);
}

/// <summary>Key helpers for <see cref="IAgentWebhookGrain"/>.</summary>
public static class AgentWebhookKeys
{
    /// <summary>The webhook grain key of <paramref name="agentId"/> owned by <paramref name="userId"/>.</summary>
    public static string KeyFor(string userId, string agentId) => $"{userId}/{agentId}";
}
