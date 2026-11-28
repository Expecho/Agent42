using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The Azure Blob adapter under <see cref="ChatTurnStore"/> (ADR 0153): the private <c>chat-turns</c> container. It holds no
/// logic beyond mapping the service's answers onto the port's. The log is an append blob and an append is conditional on its
/// append position, because Azure append blobs are not idempotent on retry. The container's data role is account-scoped
/// (<c>infra/modules/storage-role-assignment.bicep</c>), so it needed a Bicep container and no new role assignment.
/// </summary>
public sealed class AzureChatTurnBlobs(BlobServiceClient serviceClient) : IChatTurnBlobs
{
    /// <summary>A fixed literal, like <c>agent-fleet</c>: <c>infra/modules/storage.bicep</c> creates the same name (pinned by a test).</summary>
    public const string ContainerName = "chat-turns";

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

    public async Task<long?> GetLengthAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await _container.GetBlobClient(key).GetPropertiesAsync(cancellationToken: cancellationToken)).Value.ContentLength;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<ChatTurnSlice?> GetRangeAsync(string key, long offset, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _container.GetBlobClient(key)
                .DownloadStreamingAsync(new BlobDownloadOptions { Range = new HttpRange(offset) }, cancellationToken);
            using var stream = result.Value.Content;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            return new ChatTurnSlice(bytes, TotalLength(result.Value.Details.ContentRange) ?? offset + bytes.Length);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException ex) when (ex.Status == 416)
        {
            // A range at or past the end, or on an empty blob: no growth. The reader only ever asks for the offset it
            // already holds, so the length is that offset.
            return new ChatTurnSlice([], offset);
        }
    }

    public async Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        await _container.GetBlobClient(key).UploadAsync(new BinaryData(content), overwrite: true, cancellationToken);
    }

    public async Task CreateAsync(string key, CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        try
        {
            await _container.GetAppendBlobClient(key).CreateAsync(
                new AppendBlobCreateOptions { Conditions = new AppendBlobRequestConditions { IfNoneMatch = ETag.All } }, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // Already there: left as it is.
        }
    }

    // Never creates the blob: a missing one is the delete fence (D11), whatever the offset.
    public async Task<ChatTurnAppendResult> AppendAsync(string key, byte[] content, long expectedOffset, CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        var blob = _container.GetAppendBlobClient(key);
        if (content.Length == 0)
        {
            return await DescribeAsync(blob, expectedOffset, cancellationToken);
        }

        try
        {
            using var block = new MemoryStream(content, writable: false);
            await blob.AppendBlockAsync(block, new AppendBlobAppendBlockOptions
            {
                Conditions = new AppendBlobRequestConditions { IfAppendPositionEqual = expectedOffset },
            }, cancellationToken);
            return new ChatTurnAppendResult(ChatTurnAppendOutcome.Appended, expectedOffset + content.Length);
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return await DescribeAsync(blob, expectedOffset, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ChatTurnBlobItem>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var items = new List<ChatTurnBlobItem>();
        try
        {
            await foreach (var item in _container.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, cancellationToken))
            {
                items.Add(new ChatTurnBlobItem(item.Name, item.Properties.ContentLength ?? 0, item.Properties.LastModified ?? DateTimeOffset.UtcNow));
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

    // After a refused or empty append: what the blob looks like now, as the port's outcome.
    private static async Task<ChatTurnAppendResult> DescribeAsync(AppendBlobClient blob, long expectedOffset, CancellationToken cancellationToken)
    {
        try
        {
            var length = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)).Value.ContentLength;
            return length == expectedOffset
                ? new ChatTurnAppendResult(ChatTurnAppendOutcome.Appended, length)
                : new ChatTurnAppendResult(ChatTurnAppendOutcome.OffsetMismatch, length);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new ChatTurnAppendResult(ChatTurnAppendOutcome.Missing, null);
        }
    }

    // "bytes 5-9/10" -> 10.
    private static long? TotalLength(string? contentRange)
        => contentRange is not null && contentRange.LastIndexOf('/') is var slash and >= 0
            && long.TryParse(contentRange.AsSpan(slash + 1), out var total)
                ? total
                : null;

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
