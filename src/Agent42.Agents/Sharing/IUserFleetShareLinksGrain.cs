namespace Agent42.Agents;

/// <summary>
/// A read-only share link the owner minted for one of their fleet tasks (ADR 0120). Like <see cref="ShareLink"/>, the RAW
/// token is persisted: the owner re-displays and copies the link, and it is a read-only capability they control.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.FleetShareLink")]
public sealed record FleetShareLink(
    [property: Id(0)] string TaskId,
    [property: Id(1)] string Token,
    [property: Id(2)] DateTimeOffset CreatedAt);

/// <summary>
/// Per-user store of the fleet share links the user has minted (ADR 0120), keyed by the user id: the owner-facing half
/// (get, create, revoke). The public resolve path goes through <see cref="IFleetShareLinkDirectoryGrain"/>, which this
/// grain keeps in sync per operation.
/// </summary>
[Alias("Agent42.Agents.IUserFleetShareLinksGrain")]
public interface IUserFleetShareLinksGrain : IGrainWithStringKey
{
    /// <summary>The link for <paramref name="taskId"/>, or <c>null</c> when it is not shared.</summary>
    [Alias("GetAsync")]
    Task<FleetShareLink?> GetAsync(string taskId);

    /// <summary>The existing link for <paramref name="taskId"/>, or a new one (registering the directory). Idempotent.</summary>
    [Alias("CreateOrGetAsync")]
    Task<FleetShareLink> CreateOrGetAsync(string taskId);

    /// <summary>Revokes the link (no-op if not shared): deregisters the directory FIRST, then drops the entry.</summary>
    [Alias("RemoveAsync")]
    Task RemoveAsync(string taskId);
}
