using System.Text;
using Agent42.Agents;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Caching.Memory;

namespace Agent42.Backend;

/// <summary>
/// Blob-backed <see cref="IAttachmentStore"/> for production (ADR 0004). Durable image bytes
/// live in a private container, addressed by the per-user, per-conversation grain key, so they
/// never enter the serialized Agent Framework session (which is written to a single 2 MB-capped
/// Cosmos item every turn).
///
/// The model receives an image as a short-lived <b>user-delegation SAS</b> URL — key-free access
/// enabled by the container app's Storage Blob Data Contributor role. Azure OpenAI re-fetches a
/// prior turn's image URL on every later turn (Step 1 spike), so <see cref="AgentGrain"/> refreshes
/// these on activation; a SAS only needs to outlive a single turn.
/// </summary>
public sealed class BlobAttachmentStore : IAttachmentStore
{
    private const string ContainerName = "attachments";
    private const string FileNameMetadataKey = "filename";

    // A SAS only has to outlive one turn; the delegation key is refreshed well before it lapses
    // so a freshly-minted SAS can never outlive the key that signed it.
    private static readonly TimeSpan ReadUrlLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan DelegationKeyLifetime = TimeSpan.FromDays(1);

    private readonly BlobServiceClient _serviceClient;
    private readonly BlobContainerClient _container;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private UserDelegationKey? _delegationKey;
    private DateTimeOffset _delegationKeyExpiry;

    // The read URL last handed out per blob, with its expiry (ADR 0104). The grain re-signs every image in history
    // on every turn, and a new SAS query string changes the request's history segment, so every turn after the
    // first image would be a prompt-cache miss. Bounded by its own (real-clock) expiry; one replica (rule 12).
    private readonly MemoryCache _readUrls = new(new MemoryCacheOptions());

    public BlobAttachmentStore(BlobServiceClient serviceClient, TimeProvider? timeProvider = null)
    {
        _serviceClient = serviceClient;
        _container = serviceClient.GetBlobContainerClient(ContainerName);
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<AttachmentRef> SaveAsync(
        string ownerKey, string fileName, string mediaType, byte[] content,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("n");
        var blobPath = $"{ownerKey}/{id}";
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = mediaType },
            // Blob metadata values must be ASCII, but a filename may not be — store it encoded.
            Metadata = new Dictionary<string, string> { [FileNameMetadataKey] = Encode(fileName) },
        };

        // Local Azurite (shared-key client) has no pre-created container; ensure it. Production
        // pre-creates it in Bicep and uses an identity client, so this extra call is skipped there.
        if (_container.CanGenerateSasUri)
        {
            await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        }

        await _container.GetBlobClient(blobPath)
            .UploadAsync(BinaryData.FromBytes(content), options, cancellationToken);

        return new AttachmentRef(id, blobPath, mediaType, fileName);
    }

    public async Task<AttachmentRef?> GetAsync(
        string ownerKey, string attachmentId, CancellationToken cancellationToken = default)
    {
        var blobPath = $"{ownerKey}/{attachmentId}";
        try
        {
            var props = await _container.GetBlobClient(blobPath).GetPropertiesAsync(cancellationToken: cancellationToken);
            return new AttachmentRef(attachmentId, blobPath, props.Value.ContentType, ReadFileName(props.Value.Metadata, attachmentId));
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<AttachmentContent?> OpenReadAsync(
        string ownerKey, string attachmentId, CancellationToken cancellationToken = default)
    {
        var blobPath = $"{ownerKey}/{attachmentId}";
        try
        {
            var response = await _container.GetBlobClient(blobPath).DownloadContentAsync(cancellationToken);
            var details = response.Value.Details;
            return new AttachmentContent(
                response.Value.Content.ToArray(), details.ContentType, ReadFileName(details.Metadata, attachmentId));
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    // Download, then upload: no copy-source authorization or SAS is involved, so the same code works against the
    // identity client in production and Azurite locally. A fork copies a bounded set (one conversation's images and
    // artifact versions), so the extra round trip is not worth a server-side copy's auth dependency.
    public async Task<AttachmentRef> CopyAsync(
        AttachmentRef source, string targetOwnerKey, CancellationToken cancellationToken = default)
    {
        var download = await _container.GetBlobClient(source.BlobPath).DownloadContentAsync(cancellationToken);
        var details = download.Value.Details;
        var blobPath = $"{targetOwnerKey}/{source.Id}";
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = details.ContentType },
            // Copied verbatim: the file name is already stored encoded.
            Metadata = details.Metadata,
        };

        if (_container.CanGenerateSasUri)
        {
            await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        }

        await _container.GetBlobClient(blobPath).UploadAsync(download.Value.Content, options, cancellationToken);
        return new AttachmentRef(source.Id, blobPath, details.ContentType, ReadFileName(details.Metadata, source.Id));
    }

    // Reuses a URL while at least half its lifetime remains, so whatever is handed out still has 30 minutes, which
    // covers a turn. Above BOTH signing branches below. The cost: an image-bearing conversation takes one prompt-cache
    // miss roughly every half hour, when the URL renews.
    public async Task<Uri> GetReadUrlAsync(AttachmentRef reference, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        if (_readUrls.TryGetValue(reference.BlobPath, out (Uri Url, DateTimeOffset ExpiresOn) memo)
            && memo.ExpiresOn - now >= ReadUrlLifetime / 2)
        {
            return memo.Url;
        }

        var expiresOn = now.Add(ReadUrlLifetime);
        var url = await SignReadUrlAsync(reference, now, expiresOn, cancellationToken);
        _readUrls.Set(reference.BlobPath, (url, expiresOn), ReadUrlLifetime / 2);
        return url;
    }

    private async Task<Uri> SignReadUrlAsync(
        AttachmentRef reference, DateTimeOffset now, DateTimeOffset expiresOn, CancellationToken cancellationToken)
    {
        var blob = _container.GetBlobClient(reference.BlobPath);
        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = _container.Name,
            BlobName = reference.BlobPath,
            Resource = "b",
            // A little slack in the past absorbs clock skew between the app and storage.
            StartsOn = now.AddMinutes(-5),
            ExpiresOn = expiresOn,
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        // Local Azurite (shared-key client) can sign a service SAS directly. Production uses an
        // identity client with no account key, so it signs a user-delegation SAS instead (the
        // key-free path enabled by the Storage Blob Data Contributor role).
        if (blob.CanGenerateSasUri)
        {
            return blob.GenerateSasUri(sasBuilder);
        }

        var delegationKey = await GetDelegationKeyAsync(cancellationToken);
        var sas = sasBuilder.ToSasQueryParameters(delegationKey, _serviceClient.AccountName);
        return new BlobUriBuilder(blob.Uri) { Sas = sas }.ToUri();
    }

    // Caches the user-delegation key and refreshes it before it can lapse. Signing needs AAD
    // (the managed identity's Storage Blob Data Contributor role), so no account key is used.
    private async Task<UserDelegationKey> GetDelegationKeyAsync(CancellationToken cancellationToken)
    {
        if (IsDelegationKeyFresh())
        {
            return _delegationKey!;
        }

        await _keyLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsDelegationKeyFresh())
            {
                var expiry = DateTimeOffset.UtcNow.Add(DelegationKeyLifetime);
                _delegationKey = (await _serviceClient.GetUserDelegationKeyAsync(
                    DateTimeOffset.UtcNow.AddMinutes(-5), expiry, cancellationToken)).Value;
                _delegationKeyExpiry = expiry;
            }

            return _delegationKey!;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    // Fresh only while a full ReadUrlLifetime still fits before the key expires, so any SAS we
    // mint stays valid for its whole lifetime.
    private bool IsDelegationKeyFresh()
        => _delegationKey is not null && DateTimeOffset.UtcNow < _delegationKeyExpiry - ReadUrlLifetime;

    private static string ReadFileName(IDictionary<string, string> metadata, string fallback)
        => metadata.TryGetValue(FileNameMetadataKey, out var encoded) ? Decode(encoded) : fallback;

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return value;
        }
    }
}
