using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// <see cref="IFleetBlobs"/> on the local volume (<c>Storage:Provider = local</c>): the fleet store's already-escaped keys
/// become files under the agent-fleet folder. It holds no logic, like <see cref="AzureFleetBlobs"/>.
/// </summary>
public sealed class FileFleetBlobs(LocalFileStore files) : IFleetBlobs
{
    private const string Folder = "agent-fleet/";

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
        => files.ReadAsync(Folder + key, cancellationToken);

    public Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default)
        => files.WriteAsync(Folder + key, content, cancellationToken);

    public Task AppendAsync(string key, byte[] content, CancellationToken cancellationToken = default)
        => files.AppendAsync(Folder + key, content, cancellationToken);

    public Task<IReadOnlyList<FleetBlobItem>> ListAsync(string prefix, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FleetBlobItem>>(
            [.. files.List(Folder + prefix).Select(i => new FleetBlobItem(i.Key[Folder.Length..], i.Size))]);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        => files.DeleteAsync(Folder + key);
}
