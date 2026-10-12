using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The Azure Blob adapter under <see cref="FleetStore"/> (ADR 0097): the private <c>agent-fleet</c> container. It holds
/// no logic, because the keys arrive already escaped and checked. Event logs are append blobs, and everything else is a
/// block blob. The container's data role is account-scoped (<c>infra/modules/storage-role-assignment.bicep</c>), so it
/// needed a Bicep container and no new role assignment.
/// </summary>
public sealed class AzureFleetBlobs(BlobServiceClient serviceClient) : IFleetBlobs
{
    // A fixed literal, like BlobCopilotSessionStore's: infra/modules/storage.bicep creates the same name.
    private const string ContainerName = "agent-fleet";

    // Comfortably under every service version's Append Block limit.
    private const int MaxAppendBytes = 4 * 1024 * 1024;

    private readonly BlobContainerClient _container = serviceClient.GetBlobContainerClient(ContainerName);

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await _container.GetBlobClient(key).DownloadContentAsync(cancellationToken)).Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        await _container.GetBlobClient(key).UploadAsync(new BinaryData(content), overwrite: true, cancellationToken);
    }

    public async Task AppendAsync(string key, byte[] content, CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        var blob = _container.GetAppendBlobClient(key);
        await blob.CreateIfNotExistsAsync(new AppendBlobCreateOptions(), cancellationToken);
        for (var offset = 0; offset < content.Length; offset += MaxAppendBytes)
        {
            using var block = new MemoryStream(content, offset, Math.Min(MaxAppendBytes, content.Length - offset), writable: false);
            await blob.AppendBlockAsync(block, cancellationToken: cancellationToken);
        }
    }

    public async Task<IReadOnlyList<FleetBlobItem>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var items = new List<FleetBlobItem>();
        try
        {
            await foreach (var item in _container.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, cancellationToken))
            {
                items.Add(new FleetBlobItem(item.Name, item.Properties.ContentLength ?? 0));
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // No container yet (local Azurite before the first write): nothing is stored.
        }

        return items;
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        => await _container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: cancellationToken);

    // NOT SAS code, despite the property name: a shared-key client is local Azurite / AppHost, where no container is
    // pre-created. In production Bicep creates it and the identity client cannot (BlobSkillPackageStore's comment).
    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_container.CanGenerateSasUri)
        {
            await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        }
    }
}
