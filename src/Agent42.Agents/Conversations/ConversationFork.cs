namespace Agent42.Agents;

/// <summary>
/// How an edit, regenerate or fork request against a conversation's history ended (ADR 0092). The endpoints map
/// every value but <see cref="Ok"/> to a refusal with no change made.
/// </summary>
public enum HistoryEditOutcome
{
    /// <summary>Done: the last turn was removed, or the fork payload was built.</summary>
    Ok,

    /// <summary>The concurrency token (projected user-turn count + last user text hash) no longer matches (409).</summary>
    Stale,

    /// <summary>A model run is in flight on this conversation (409).</summary>
    Busy,

    /// <summary>A Copilot-kind chat, whose real transcript lives outside the grain session (409).</summary>
    Unsupported,

    /// <summary>There is no visible last user turn to remove, or the fork position is out of range (409).</summary>
    Nothing,

    /// <summary>No model provider is configured, so the re-run that follows a truncation could not run (409).</summary>
    NoModel,
}

/// <summary>
/// Everything a fork carries from its source conversation into a new one (ADR 0092): the persona snapshot verbatim
/// (never re-resolved from the registry, ADR 0003), the truncated session, and the attachment, image and artifact
/// metadata its kept messages reference. Crosses the grain boundary only and is never persisted, so it needs no
/// golden document. Blob paths still point at <see cref="SourceOwnerKey"/>; the target copies every blob under its
/// own key on <c>InitializeFromForkAsync</c>.
/// </summary>
// ADR 0039: flat namespace, like every type that crosses the grain boundary.
[GenerateSerializer]
[Alias("Agent42.Agents.ConversationFork")]
public sealed record ConversationFork(
    [property: Id(0)] string SourceOwnerKey,
    [property: Id(1)] string? AgentId,
    [property: Id(2)] string? AgentName,
    [property: Id(3)] string? SystemPrompt,
    [property: Id(4)] string? AgentKind,
    [property: Id(5)] string? DeploymentName,
    [property: Id(6)] List<string>? EnabledTools,
    [property: Id(7)] string SerializedSession,
    [property: Id(8)] List<List<AttachmentRef>> TurnAttachments,
    [property: Id(9)] Dictionary<string, AttachmentRef> GeneratedImages,
    [property: Id(10)] List<ArtifactRecord> Artifacts);

/// <summary>The outcome of <c>IAgentGrain.CreateForkAsync</c>: the payload when <see cref="Outcome"/> is Ok.</summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ConversationForkResult")]
public sealed record ConversationForkResult(
    [property: Id(0)] HistoryEditOutcome Outcome,
    [property: Id(1)] ConversationFork? Fork = null);
