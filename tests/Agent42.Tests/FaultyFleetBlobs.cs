using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>An in-memory fleet blob store that fails on demand: reads of the keys <see cref="FailGetWhen"/> matches and writes
/// <see cref="FailPutWhen"/> matches (given the key and the text written), to test the paths around a failing store.</summary>
public sealed class FaultyFleetBlobs : IFleetBlobs
{
    private readonly InMemoryFleetBlobs _inner = new();

    public Func<string, bool>? FailGetWhen { get; set; }
    public Func<string, string, bool>? FailPutWhen { get; set; }

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
        => FailGetWhen?.Invoke(key) == true ? throw new IOException("blob read failed") : _inner.GetAsync(key, cancellationToken);

    public Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default)
        => FailPutWhen?.Invoke(key, System.Text.Encoding.UTF8.GetString(content)) == true
            ? throw new IOException("blob write failed")
            : _inner.PutAsync(key, content, cancellationToken);

    public Task AppendAsync(string key, byte[] content, CancellationToken cancellationToken = default) => _inner.AppendAsync(key, content, cancellationToken);

    public Task<IReadOnlyList<FleetBlobItem>> ListAsync(string prefix, CancellationToken cancellationToken = default) => _inner.ListAsync(prefix, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);
}
