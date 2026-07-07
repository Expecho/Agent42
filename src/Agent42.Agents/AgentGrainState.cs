namespace Agent42.Agents;

/// <summary>
/// Persisted state of an <see cref="AgentGrain"/>: the serialized Agent Framework
/// conversation session, so a conversation survives grain deactivation.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.AgentGrainState")]
public sealed class AgentGrainState
{
    [Id(0)]
    public string? SerializedSession { get; set; }

    /// <summary>
    /// Reminders scheduled for this conversation, keyed by reminder name. Kept in sync
    /// with the Orleans reminders registered by the grain.
    /// </summary>
    [Id(1)]
    public Dictionary<string, ReminderInfo> ScheduledPrompts { get; set; } = [];

    /// <summary>
    /// The model deployment this conversation is currently using. Null means the
    /// conversation hasn't picked one yet and falls back to the configured default.
    /// </summary>
    [Id(2)]
    public string? DeploymentName { get; set; }

    /// <summary>
    /// The id of the stored agent this conversation was started from, or null for a
    /// normal chat. Once set it never changes — the persona is immutable for the life of
    /// the conversation (ADR 0003). Old Cosmos state without these fields deserializes to
    /// null, so existing conversations fall back to the default persona.
    /// </summary>
    [Id(3)]
    public string? AgentId { get; set; }

    /// <summary>The bound agent's display name, snapshotted at bind time.</summary>
    [Id(4)]
    public string? AgentName { get; set; }

    /// <summary>The bound agent's system prompt, snapshotted at bind time.</summary>
    [Id(5)]
    public string? SystemPrompt { get; set; }
}
