namespace Agent42.Agents;

/// <summary>
/// The narrow Blob seam under <see cref="FleetStore"/> (ADR 0097): put, get, append, list by prefix and delete, over
/// keys the store has already escaped. It holds NO logic, so the Azure adapter (<c>AzureFleetBlobs</c>) stays a thin
/// shell and the contract tests run the real <see cref="FleetStore"/> over <see cref="InMemoryFleetBlobs"/>.
/// </summary>
public interface IFleetBlobs
{
    /// <summary>The blob's bytes, or null when it doesn't exist.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a blob.</summary>
    Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>Appends to an append blob, creating it first when it doesn't exist.</summary>
    Task AppendAsync(string key, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>The blobs whose key starts with <paramref name="prefix"/>.</summary>
    Task<IReadOnlyList<FleetBlobItem>> ListAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>Deletes a blob; a missing blob is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>A blob key and its size in bytes.</summary>
public sealed record FleetBlobItem(string Key, long Size);
