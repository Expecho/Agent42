using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.UserProfileState")]
public sealed class UserProfileState
{
    // UserProfile.Empty is a factory (returns a fresh instance), so this is a per-state
    // default, never a shared mutable static — see the note on UserProfile.Empty.
    [Id(0)]
    public UserProfile Profile { get; set; } = UserProfile.Empty;
}

/// <summary>
/// Per-user profile grain. Rides the shared "agent-state" storage provider (Orleans
/// partitions by grain type, so no dedicated Cosmos container is needed — see ADR 0001
/// and the pre-declared-containers gotcha). Uses a distinct "profile" state name,
/// alongside the registry's "agents" and the index's "conversations".
/// </summary>
public sealed class UserProfileGrain : Grain, IUserProfileGrain
{
    // BCP-47 tags are short; the curated dropdown is the real gate, this is defense-in-depth.
    private const int MaxLanguageTagLength = 35;

    private readonly IPersistentState<UserProfileState> _state;

    public UserProfileGrain(
        [PersistentState("profile", AgentGrain.StorageProviderName)] IPersistentState<UserProfileState> state)
    {
        _state = state;
    }

    public Task<UserProfile> GetAsync() => Task.FromResult(_state.State.Profile);

    public async Task<UserProfile> SaveAsync(UserProfile profile)
    {
        var language = (profile.PreferredLanguage ?? "").Trim();
        if (language.Length > MaxLanguageTagLength)
        {
            language = language[..MaxLanguageTagLength];
        }

        var stored = profile with { PreferredLanguage = language };
        _state.State.Profile = stored;
        await _state.WriteStateAsync();
        return stored;
    }
}
