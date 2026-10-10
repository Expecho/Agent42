using System.Collections.Concurrent;

namespace Agent42.Agents;

/// <summary>
/// In-memory <see cref="IAttachmentStore"/> for local dev (DevHost) and tests, where no
/// Azure Storage is available.
///
/// The backing store is <b>static / process-global</b> on purpose. In production the Backend
/// co-hosts the API endpoints and the Orleans silo in one process, so a singleton store is
/// naturally shared. In tests they are two separate DI containers — the WebApplication that
/// serves the endpoints and the TestCluster silo that runs the grain — so an instance-field
/// store registered in each would be two different stores, and the endpoint→grain path (upload
/// via the endpoint, read from the grain) would silently break. Static state keeps them one store.
/// </summary>
public sealed class InMemoryAttachmentStore : IAttachmentStore
{
    private static readonly ConcurrentDictionary<string, StoredBlob> Blobs = new();

    private sealed record StoredBlob(byte[] Bytes, string MediaType, string FileName);

    public Task<AttachmentRef> SaveAsync(
        string ownerKey, string fileName, string mediaType, byte[] content,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("n");
        var blobPath = $"{ownerKey}/{id}";
        Blobs[blobPath] = new StoredBlob(content, mediaType, fileName);
        return Task.FromResult(new AttachmentRef(id, blobPath, mediaType, fileName));
    }

    public Task<AttachmentRef?> GetAsync(
        string ownerKey, string attachmentId, CancellationToken cancellationToken = default)
    {
        var blobPath = $"{ownerKey}/{attachmentId}";
        var reference = Blobs.TryGetValue(blobPath, out var blob)
            ? new AttachmentRef(attachmentId, blobPath, blob.MediaType, blob.FileName)
            : null;
        return Task.FromResult(reference);
    }

    public Task<AttachmentContent?> OpenReadAsync(
        string ownerKey, string attachmentId, CancellationToken cancellationToken = default)
    {
        var blobPath = $"{ownerKey}/{attachmentId}";
        var content = Blobs.TryGetValue(blobPath, out var blob)
            ? new AttachmentContent(blob.Bytes, blob.MediaType, blob.FileName)
            : null;
        return Task.FromResult(content);
    }

    public Task<AttachmentRef> CopyAsync(
        AttachmentRef source, string targetOwnerKey, CancellationToken cancellationToken = default)
    {
        if (!Blobs.TryGetValue(source.BlobPath, out var blob))
        {
            throw new InvalidOperationException($"Attachment {source.Id} is not in the store.");
        }

        var blobPath = $"{targetOwnerKey}/{source.Id}";
        Blobs[blobPath] = blob;
        return Task.FromResult(new AttachmentRef(source.Id, blobPath, blob.MediaType, blob.FileName));
    }

    // Offline the model URL is never actually fetched (the canned/fake clients ignore image
    // content) and the browser renders via the bytes proxy, not this URL — so a short, stable
    // placeholder is enough. It still travels as a UriContent, so the fake can count it.
    public Task<Uri> GetReadUrlAsync(AttachmentRef reference, CancellationToken cancellationToken = default)
        => Task.FromResult(new Uri($"inmemory://attachment/{reference.Id}"));
}
