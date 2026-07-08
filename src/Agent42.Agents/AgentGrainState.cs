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

    /// <summary>
    /// Attachments per user turn, in turn order — one entry (possibly empty) for every user
    /// turn, including reminder-fired turns, so <c>GetHistoryAsync</c> can join a turn to its
    /// attachments by index. The serialized session stores each image only as a short URL (the
    /// bytes live in blob storage, ADR 0004); this list carries the stable attachment ids and
    /// blob paths the browser and rehydrate-on-read need. Old Cosmos state deserializes this as
    /// an empty list, so pre-attachment conversations simply have no refs (the grain back-fills
    /// the alignment on the next turn).
    /// </summary>
    [Id(6)]
    public List<List<AttachmentRef>> TurnAttachments { get; set; } = [];
}
