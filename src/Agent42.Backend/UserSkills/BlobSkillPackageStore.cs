using Agent42.Agents;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Agent42.Backend;

/// <summary>
/// Blob-backed <see cref="ISkillPackageStore"/> for production (ADR 0049). One normalized zip per installed
/// skill lives in a private <c>skill-packages</c> container at <c>{userId}/{skillId}.zip</c>, so the durable
/// record of a user's uploaded skills is storage, and the container filesystem is only ever a cache of it.
///
/// <para>Copied from <see cref="BlobAttachmentStore"/> minus everything SAS-related — a skill package must
/// never be handed a read URL (see <see cref="ISkillPackageStore"/>). The one piece that <i>looks</i>
/// SAS-related and stays is the <c>CanGenerateSasUri</c> check below; read its comment before removing it.</para>
///
/// <para>The container's Storage Blob Data Contributor grant is account-scoped
/// (<c>infra/modules/storage-role-assignment.bicep</c>), so adding this container needed a Bicep resource
/// and <b>no</b> new role assignment.</para>
/// </summary>
public sealed class BlobSkillPackageStore : ISkillPackageStore
{
    private const string ContainerName = "skill-packages";

    private readonly BlobContainerClient _container;

    public BlobSkillPackageStore(BlobServiceClient serviceClient)
        => _container = serviceClient.GetBlobContainerClient(ContainerName);

    public async Task SaveAsync(string userId, string skillId, byte[] zip, CancellationToken cancellationToken = default)
    {
        // NOT SAS code, despite the property name: it is how this distinguishes a shared-key client (local
        // Azurite / AppHost, where no container is pre-created and AppHost.cs declares no container list)
        // from an identity client (production, where Bicep pre-creates it). Dropping it as "SAS-related"
        // fails the Aspire path with ContainerNotFound.
        if (_container.CanGenerateSasUri)
        {
            await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        }

        await _container.GetBlobClient(BlobPath(userId, skillId)).UploadAsync(
            BinaryData.FromBytes(zip),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/zip" } },
            cancellationToken);
    }

    public async Task<byte[]?> ReadAsync(string userId, string skillId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _container.GetBlobClient(BlobPath(userId, skillId))
                .DownloadContentAsync(cancellationToken);
            return response.Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string userId, string skillId, CancellationToken cancellationToken = default)
        => await _container.GetBlobClient(BlobPath(userId, skillId))
            .DeleteIfExistsAsync(cancellationToken: cancellationToken);

    private static string BlobPath(string userId, string skillId) => $"{userId}/{skillId}.zip";
}
