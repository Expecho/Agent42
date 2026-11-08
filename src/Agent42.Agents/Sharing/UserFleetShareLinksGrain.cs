using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.UserFleetShareLinksState")]
public sealed class UserFleetShareLinksState
{
    [Id(0)]
    public List<FleetShareLink> Links { get; set; } = [];
}

/// <summary>
/// Per-user fleet share-link store (ADR 0120), the twin of <see cref="UserShareLinksGrain"/> with its own state name. It
/// keeps <see cref="IFleetShareLinkDirectoryGrain"/> in sync on every change, in the same orders as ADR 0035: create
/// writes the list first and registers second (a crash leaves a dead link), revoke deregisters first (a crash leaves a
/// dead link, never a live revoked one).
/// </summary>
public sealed class UserFleetShareLinksGrain : Grain, IUserFleetShareLinksGrain
{
    private readonly IPersistentState<UserFleetShareLinksState> _state;

    public UserFleetShareLinksGrain(
        [PersistentState("fleet-share-links", AgentGrain.StorageProviderName)] IPersistentState<UserFleetShareLinksState> state)
    {
        _state = state;
    }

    public Task<FleetShareLink?> GetAsync(string taskId)
        => Task.FromResult(_state.State.Links.FirstOrDefault(l => l.TaskId == taskId));

    public async Task<FleetShareLink> CreateOrGetAsync(string taskId)
    {
        if (_state.State.Links.FirstOrDefault(l => l.TaskId == taskId) is { } existing)
        {
            return existing;
        }

        var (raw, hash) = ShareToken.Generate();
        var link = new FleetShareLink(taskId, raw, DateTimeOffset.UtcNow);

        _state.State.Links.Add(link);
        await _state.WriteStateAsync();

        await Directory(hash).RegisterAsync(this.GetPrimaryKeyString(), taskId);
        return link;
    }

    public async Task RemoveAsync(string taskId)
    {
        if (_state.State.Links.FirstOrDefault(l => l.TaskId == taskId) is not { } existing)
        {
            return;
        }

        await Directory(ShareToken.Hash(existing.Token)).DeregisterAsync();

        _state.State.Links.RemoveAll(l => l.TaskId == taskId);
        await _state.WriteStateAsync();
    }

    private IFleetShareLinkDirectoryGrain Directory(string hash)
        => GrainFactory.GetGrain<IFleetShareLinkDirectoryGrain>(hash);
}
