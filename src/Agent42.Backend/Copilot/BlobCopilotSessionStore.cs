using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace Agent42.Backend;

/// <summary>
/// Blob-backed <see cref="ICopilotSessionStore"/> for production: the Copilot runtime's session files in a private
/// <c>copilot-sessions</c> container, keyed <c>{userId}/{conversationId}/{path}</c>. Never handed a read URL.
///
/// <para><b>Every file is an APPEND blob.</b> The runtime appends to its transcript several times per turn
/// (measured: <c>append=5</c> against <c>write=2</c> for one tool turn). That is one Append Block call each. On a
/// block blob it would be a download-and-reupload of a file that grows about 26 KB per turn. A write replaces the
/// blob with a fresh append blob. A file holds at most 50,000 appends, which at about five per turn is roughly
/// 10,000 turns of one chat.</para>
///
/// <para>The container's Storage Blob Data Contributor grant is account-scoped
/// (<c>infra/modules/storage-role-assignment.bicep</c>), like <see cref="BlobSkillPackageStore"/>'s, so this needed
/// a Bicep container and no new role assignment.</para>
/// </summary>
public sealed class BlobCopilotSessionStore : ICopilotSessionStore
{
    private const string ContainerName = "copilot-sessions";

    // Comfortably under every service version's Append Block limit.
    private const int MaxAppendBytes = 4 * 1024 * 1024;

    private readonly BlobContainerClient _container;

    public BlobCopilotSessionStore(BlobServiceClient serviceClient)
        => _container = serviceClient.GetBlobContainerClient(ContainerName);

    public async Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _container.GetBlobClient(key).DownloadContentAsync(cancellationToken);
            return response.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task WriteAsync(string key, string content, CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        var blob = _container.GetAppendBlobClient(key);
        await blob.CreateAsync(new AppendBlobCreateOptions(), cancellationToken);
        await AppendBlocksAsync(blob, content, cancellationToken);
    }

    public async Task AppendAsync(string key, string content, CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        var blob = _container.GetAppendBlobClient(key);
        await blob.CreateIfNotExistsAsync(new AppendBlobCreateOptions(), cancellationToken);
        await AppendBlocksAsync(blob, content, cancellationToken);
    }

    public async Task<CopilotFileInfo?> GetInfoAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var properties = (await _container.GetBlobClient(key).GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
            return new CopilotFileInfo(properties.ContentLength, properties.LastModified);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var keys = new List<string>();
        try
        {
            await foreach (var item in _container.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, cancellationToken))
            {
                keys.Add(item.Name);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // No container yet (local Azurite before the first write): nothing is stored.
        }

        return keys;
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        => await _container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: cancellationToken);

    // The text in blocks of at most MaxAppendBytes. An empty write still leaves the (empty) blob created above.
    private static async Task AppendBlocksAsync(AppendBlobClient blob, string content, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        for (var offset = 0; offset < bytes.Length; offset += MaxAppendBytes)
        {
            using var block = new MemoryStream(bytes, offset, Math.Min(MaxAppendBytes, bytes.Length - offset), writable: false);
            await blob.AppendBlockAsync(block, cancellationToken: cancellationToken);
        }
    }

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
