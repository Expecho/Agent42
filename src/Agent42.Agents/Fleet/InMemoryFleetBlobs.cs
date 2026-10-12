using System.Collections.Concurrent;

namespace Agent42.Agents;

/// <summary>
/// In-memory <see cref="IFleetBlobs"/> for DevHost and tests: everything is lost on restart, which is fine offline.
/// </summary>
public sealed class InMemoryFleetBlobs : IFleetBlobs
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
    private readonly Lock _appendGate = new();

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_blobs.TryGetValue(key, out var content) ? content.ToArray() : null);

    public Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default)
    {
        _blobs[key] = content.ToArray();
        return Task.CompletedTask;
    }

    public Task AppendAsync(string key, byte[] content, CancellationToken cancellationToken = default)
    {
        lock (_appendGate)
        {
            _blobs[key] = _blobs.TryGetValue(key, out var existing) ? [.. existing, .. content] : content.ToArray();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FleetBlobItem>> ListAsync(string prefix, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FleetBlobItem>>(
            [.. _blobs.Where(b => b.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(b => b.Key, StringComparer.Ordinal)
                .Select(b => new FleetBlobItem(b.Key, b.Value.Length))]);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _blobs.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
