namespace Agent42.Agents;

/// <summary>
/// Stores the durable per-user profile (the grain key is the user id, like
/// <see cref="IAgentRegistryGrain"/> and <see cref="IConversationIndexGrain"/>). A
/// singleton per user — one profile, not a collection — so it exposes a plain
/// get/save pair rather than CRUD-by-id.
/// </summary>
[Alias("Agent42.Agents.IUserProfileGrain")]
public interface IUserProfileGrain : IGrainWithStringKey
{
    /// <summary>Returns the user's stored profile, or <see cref="UserProfile.Empty"/> when never saved.</summary>
    [Alias("GetAsync")]
    Task<UserProfile> GetAsync();

    /// <summary>Persists the profile and returns the stored value.</summary>
    [Alias("SaveAsync")]
    Task<UserProfile> SaveAsync(UserProfile profile);
}
