namespace Agent42.Agents;

/// <summary>
/// The fleet task a share token resolves to: which user owns it and which task to show (ADR 0120). Crosses the grain
/// boundary, so it is Orleans-serializable with explicit member ids.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.FleetShareLinkTarget")]
public sealed record FleetShareLinkTarget(
    [property: Id(0)] string UserId,
    [property: Id(1)] string TaskId);

/// <summary>
/// A global directory mapping a fleet share-token hash to the task it grants read-only access to (ADR 0120). A grain
/// type of its own, never <see cref="IShareLinkDirectoryGrain"/>: a credential namespace gets its own directory, so a
/// chat share token can never open a task and a task token can never open a chat. The grain key is the token hash, so
/// resolving <c>/api/share/fleet/{token}</c> is an O(1) get. <see cref="UserFleetShareLinksGrain"/> keeps it in sync.
/// </summary>
[Alias("Agent42.Agents.IFleetShareLinkDirectoryGrain")]
public interface IFleetShareLinkDirectoryGrain : IGrainWithStringKey
{
    /// <summary>Points this token hash at <paramref name="userId"/>/<paramref name="taskId"/>.</summary>
    [Alias("RegisterAsync")]
    Task RegisterAsync(string userId, string taskId);

    /// <summary>The task this hash points at, or <c>null</c> when it is unregistered (the endpoint then 404s).</summary>
    [Alias("ResolveAsync")]
    Task<FleetShareLinkTarget?> ResolveAsync();

    /// <summary>Removes the registration, so the link stops resolving. This IS the revocation.</summary>
    [Alias("DeregisterAsync")]
    Task DeregisterAsync();
}
