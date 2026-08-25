namespace Agent42.Agents;

/// <summary>
/// Stores a signed-in user's uploaded-skill overlay (ADR 0049 — the skills analogue of ADR 0018's provider
/// overlay and ADR 0024's MCP one). The grain key is the user id. A singleton per user, so it exposes
/// upsert/delete plus a version read the per-user skill provider polls to invalidate its cache.
///
/// <para><b>The grain does no blob or disk IO.</b> It holds the index only; the caller owns the package
/// store and the materialized directory. That is why <see cref="SaveAsync"/> hands back the row it
/// superseded: only the caller can delete the old blob and directory, and it needs the old id to do it.</para>
/// </summary>
[Alias("Agent42.Agents.IUserSkillsGrain")]
public interface IUserSkillsGrain : IGrainWithStringKey
{
    /// <summary>Returns the user's overlay, or <see cref="UserSkillConfig.Empty"/> when never saved.</summary>
    [Alias("GetAsync")]
    Task<UserSkillConfig> GetAsync();

    /// <summary>The overlay's current version — bumped on every write. Cheap poll for cache invalidation.</summary>
    [Alias("GetVersionAsync")]
    Task<int> GetVersionAsync();

    /// <summary>
    /// Upserts one skill <b>by name</b> (case-insensitively — see <see cref="StoredUserSkill"/>) and bumps
    /// the version. Returns the row it replaced, or null when the skill is new, so the caller can delete
    /// the superseded package and its materialized directory.
    /// </summary>
    [Alias("SaveAsync")]
    Task<StoredUserSkill?> SaveAsync(StoredUserSkill skill);

    /// <summary>
    /// Removes the skill with <paramref name="id"/>, bumping the version only if one was removed. Returns
    /// the removed row (so the caller can clean up its blob and directory), or null if there was none.
    /// </summary>
    [Alias("DeleteAsync")]
    Task<StoredUserSkill?> DeleteAsync(string id);
}
