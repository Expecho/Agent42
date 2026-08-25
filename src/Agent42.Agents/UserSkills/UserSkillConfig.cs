namespace Agent42.Agents;

/// <summary>
/// One installed skill in a user's overlay (ADR 0049). The row is the <b>index</b>; the bytes live in
/// <see cref="ISkillPackageStore"/> under <see cref="Id"/>, which keeps the 2 MB session and this state
/// item small no matter how large the upload was (ADR 0004).
///
/// <para><b><see cref="Id"/> is minted fresh on every upload, including a replace.</b> That is what makes
/// the materialized directory <c>{temp}/agent42-skills/{id}/{name}/</c> immutable and so needing no cache
/// invalidation: a replaced skill gets a new id and a new directory, and the endpoint deletes the old
/// blob and directory. The picker key stays <c>skill:{name}</c>, so a user's tool selection survives a
/// re-upload.</para>
///
/// <para><see cref="Name"/> and <see cref="Description"/> are what the framework parsed out of the
/// frontmatter, copied here so <c>GET /api/tools</c> can render the picker from the <b>grain alone</b> —
/// never a blob read and never a disk read.</para>
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.StoredUserSkill")]
public sealed record StoredUserSkill(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    [property: Id(2)] string Description,
    [property: Id(3)] IReadOnlyList<string> Files,
    [property: Id(4)] DateTimeOffset UploadedAt);

/// <summary>
/// A user's uploaded-skill overlay: the skills they installed and a monotonically-increasing
/// <see cref="Version"/> the grain bumps on every write. The version is the cache key
/// <c>UserSkillProvider</c> stamps on (like <c>UserMcpToolProvider</c>), so an upload is reflected on the
/// next grain activation without an app restart. An empty overlay (the default) means "no uploaded
/// skills" — every user starts with the shipped set alone.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.UserSkillConfig")]
public sealed record UserSkillConfig(
    [property: Id(0)] IReadOnlyList<StoredUserSkill> Skills,
    [property: Id(1)] int Version)
{
    /// <summary>
    /// A factory (fresh instance per access), never a cached singleton — a shared static default is
    /// mutated by Orleans' copier and leaks across grains (the same discipline as
    /// <see cref="UserMcpConfig.Empty"/>).
    /// </summary>
    public static UserSkillConfig Empty => new([], 0);
}
