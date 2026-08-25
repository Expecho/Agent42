namespace Agent42.Agents;

/// <summary>
/// Stores the durable bytes of a user's uploaded skill package — one normalized zip per installed skill,
/// addressed by <c>{userId}/{skillId}.zip</c>. The <c>UserSkillsGrain</c> row is the index; this is the
/// content, kept out of grain state for the same reason attachments are (ADR 0004: the session is one
/// Cosmos item, hard-capped at 2 MB).
///
/// <para><b>Deliberately narrower than <see cref="IAttachmentStore"/>.</b> There is no
/// <c>GetReadUrlAsync</c>: an attachment exists to be handed to a model as a short-lived SAS URL, while a
/// skill package is only ever read back <i>by this process</i>, to be materialized on local disk. Minting a
/// SAS for one would put a user's uploaded instructions on a URL that leaves the tenant, which nothing in
/// this feature needs.</para>
///
/// <para>Production is blob-backed (<c>BlobSkillPackageStore</c>, container <c>skill-packages</c>); local
/// dev and tests use <see cref="InMemorySkillPackageStore"/>, mirroring <see cref="IAttachmentStore"/>.</para>
/// </summary>
public interface ISkillPackageStore
{
    /// <summary>Persists a normalized skill zip for a user under <c>{userId}/{skillId}.zip</c>.</summary>
    Task SaveAsync(string userId, string skillId, byte[] zip, CancellationToken cancellationToken = default);

    /// <summary>Reads a stored package back, or null when it does not exist (a deleted or never-written skill).</summary>
    Task<byte[]?> ReadAsync(string userId, string skillId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a stored package. Idempotent — deleting a package that is already gone is a success, so the
    /// caller can clean up a superseded skill without first proving it exists.
    /// </summary>
    Task DeleteAsync(string userId, string skillId, CancellationToken cancellationToken = default);
}
