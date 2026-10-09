namespace Agent42.Agents;

/// <summary>
/// A conversation's shared state as the UI reads it (ADR 0089): the JSON object as text (null when the
/// conversation has none) and its version, which the UI sends back as the base of an edit. Not grain state.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.SharedStateSnapshot")]
public sealed record SharedStateSnapshot(
    [property: Id(0)] string? Json,
    [property: Id(1)] long Version);

/// <summary>How a user edit of the shared state ended (ADR 0089). The endpoint maps each to a status code.</summary>
public enum SharedStateWriteStatus
{
    /// <summary>Accepted; <see cref="SharedStateWriteResult.Version"/> is the new version.</summary>
    Ok,

    /// <summary>The edit was based on a version that is no longer current (409).</summary>
    VersionConflict,

    /// <summary>The conversation has no saved record, so it cannot hold state: incognito or never saved (409).</summary>
    NoSavedRecord,

    /// <summary>Not a JSON object, too deep or too large (400). <see cref="SharedStateWriteResult.Error"/> says which.</summary>
    Invalid,
}

/// <summary>The outcome of <c>IAgentGrain.SetSharedStateAsync</c> (ADR 0089).</summary>
[GenerateSerializer]
[Alias("Agent42.Agents.SharedStateWriteResult")]
public sealed record SharedStateWriteResult(
    [property: Id(0)] SharedStateWriteStatus Status,
    [property: Id(1)] long Version,
    [property: Id(2)] string? Error = null);
