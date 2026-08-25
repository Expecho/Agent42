using System.Collections.Concurrent;

namespace Agent42.Agents;

/// <summary>
/// In-memory <see cref="ISkillPackageStore"/> for local dev (DevHost) and tests, where no Azure Storage is
/// available.
///
/// The backing store is <b>static / process-global</b> for exactly the reason
/// <see cref="InMemoryAttachmentStore"/>'s is: in production one process co-hosts the endpoints and the
/// silo, but in tests they are two DI containers, so an instance field would be two different stores and
/// the endpoint→provider path (upload via the endpoint, materialize from the grain host) would silently
/// break.
/// </summary>
public sealed class InMemorySkillPackageStore : ISkillPackageStore
{
    private static readonly ConcurrentDictionary<string, byte[]> Packages = new(StringComparer.Ordinal);

    public Task SaveAsync(string userId, string skillId, byte[] zip, CancellationToken cancellationToken = default)
    {
        Packages[Key(userId, skillId)] = zip;
        return Task.CompletedTask;
    }

    public Task<byte[]?> ReadAsync(string userId, string skillId, CancellationToken cancellationToken = default)
        => Task.FromResult(Packages.TryGetValue(Key(userId, skillId), out var zip) ? zip : null);

    public Task DeleteAsync(string userId, string skillId, CancellationToken cancellationToken = default)
    {
        Packages.TryRemove(Key(userId, skillId), out _);
        return Task.CompletedTask;
    }

    private static string Key(string userId, string skillId) => $"{userId}/{skillId}.zip";
}
