using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// <see cref="ISkillPackageStore"/> on the local volume (<c>Storage:Provider = local</c>): the same
/// <c>{userId}/{skillId}.zip</c> keys as <see cref="BlobSkillPackageStore"/>, one file each, under the skill-packages folder.
/// Like the Blob store it hands out no read URL: a package is only read back by this process.
/// </summary>
public sealed class FileSkillPackageStore(LocalFileStore files) : ISkillPackageStore
{
    private const string Folder = "skill-packages/";

    public Task SaveAsync(string userId, string skillId, byte[] zip, CancellationToken cancellationToken = default)
        => files.WriteAsync(Key(userId, skillId), zip, cancellationToken);

    public Task<byte[]?> ReadAsync(string userId, string skillId, CancellationToken cancellationToken = default)
        => files.ReadAsync(Key(userId, skillId), cancellationToken);

    public Task DeleteAsync(string userId, string skillId, CancellationToken cancellationToken = default)
        => files.DeleteAsync(Key(userId, skillId));

    private static string Key(string userId, string skillId)
        => $"{Folder}{Uri.EscapeDataString(userId)}/{Uri.EscapeDataString(skillId)}.zip";
}
