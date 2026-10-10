namespace Agent42.Agents;

/// <summary>
/// The persisted webhook of one stored agent (ADR 0094). A CLASS with defaults, never a positional record (a Cosmos
/// document missing a constructor parameter binds it to null), and the log is a mutable <see cref="List{T}"/> the
/// grain builds itself — never a collection expression into an <c>IReadOnlyList&lt;T&gt;</c> (rule 2).
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.AgentWebhookState")]
public sealed class AgentWebhookState
{
    /// <summary>The owner's https URL; null when no webhook is configured.</summary>
    [Id(0)]
    public string? Url { get; set; }

    /// <summary>The signing secret, protected by the Backend's <c>WebhookSecretProtector</c>. Never the plaintext.</summary>
    [Id(1)]
    public string? SecretCiphertext { get; set; }

    /// <summary>The last <see cref="WebhookLimits.MaxDeliveryLog"/> deliveries, oldest first.</summary>
    [Id(2)]
    public List<WebhookDeliveryRecord> Deliveries { get; set; } = [];
}

/// <summary>
/// One delivery in the log. A status code and an error class only — <b>never a response body</b>, which would turn
/// the log into a reader for whatever the URL resolves to.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.WebhookDeliveryRecord")]
public sealed class WebhookDeliveryRecord
{
    [Id(0)]
    public DateTimeOffset At { get; set; }

    [Id(1)]
    public string DeliveryId { get; set; } = "";

    [Id(2)]
    public string Type { get; set; } = "";

    [Id(3)]
    public string? Trigger { get; set; }

    [Id(4)]
    public string? ConversationId { get; set; }

    [Id(5)]
    public int? StatusCode { get; set; }

    [Id(6)]
    public string? Error { get; set; }

    [Id(7)]
    public int Attempts { get; set; }

    [Id(8)]
    public long DurationMs { get; set; }
}

/// <summary>What <see cref="IAgentWebhookGrain.GetAsync"/> returns: never the ciphertext, only whether one exists.</summary>
[GenerateSerializer]
[Alias("Agent42.Agents.AgentWebhookStatus")]
public sealed record AgentWebhookStatus(
    [property: Id(0)] string? Url,
    [property: Id(1)] bool HasSecret,
    [property: Id(2)] IReadOnlyList<WebhookDeliveryRecord> Deliveries);
