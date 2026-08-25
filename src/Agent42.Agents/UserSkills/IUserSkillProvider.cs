namespace Agent42.Agents;

/// <summary>
/// The uploaded skills resolved for a user, plus the overlay <see cref="Version"/> they were built from —
/// the cache key the provider stamps on, so an upload is reflected on the next grain activation without an
/// app restart (the ADR 0024 <c>McpToolSet</c> shape).
///
/// <para>Entries are <see cref="SkillInfo"/>, exactly like <see cref="SkillCatalog.Skills"/>, because the
/// two layers merge into one catalog in <c>BuildAgent</c> and the gate must not be able to tell them
/// apart — a <c>skill:{name}</c> key means the same thing whichever layer it came from.</para>
/// </summary>
public sealed record UserSkillSet(IReadOnlyList<SkillInfo> Skills, int Version)
{
    /// <summary>The empty set — a user who has uploaded nothing.</summary>
    public static UserSkillSet Empty { get; } = new([], 0);
}

/// <summary>
/// Resolves the skills a specific user has uploaded (ADR 0049). A user installs their own skills, so the
/// set is per-user, not the process-wide <see cref="SkillCatalog"/>. The agent grain resolves this
/// <b>once</b> in <c>OnActivateAsync</c> (async) from its own userId — <b>before</b> the first
/// <c>BuildAgent</c>, the same ordering hazard as <see cref="IUserMcpToolProvider"/> and the chat-client
/// factory — and stores the result, so <c>BuildAgent</c> (sync) can merge the two layers.
///
/// <para>Resolving means <b>materializing</b>: the durable record is a zip in blob storage, but
/// <c>AgentSkillsProvider</c> reads skills from directory paths on disk, so the package is unpacked onto the
/// container filesystem. That filesystem is a <b>cache, not the record</b> — a restart wipes it and the next
/// activation re-materializes.</para>
/// </summary>
public interface IUserSkillProvider
{
    /// <summary>The uploaded skills for <paramref name="userId"/> (empty when none, or when none could be materialized).</summary>
    Task<UserSkillSet> GetForUserAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best-effort removal of a skill's materialized directory, called by the endpoint when a skill is
    /// deleted or superseded by a re-upload. A failure here must never fail the request — the worst case is
    /// a stale directory that the next container restart clears.
    /// </summary>
    void RemoveMaterialized(string skillId);
}
