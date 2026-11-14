using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// <see cref="IAttachmentStore"/> on the local volume (<c>Storage:Provider = local</c>, ADR 0004 amended): the bytes under
/// <c>attachments/{ownerKey}/{id}</c> and a small sidecar record (the STORED media type and the file name) under
/// <c>attachment-meta/</c>, a separate folder so no client-chosen id can ever read a sidecar as content.
///
/// <para>The stored media type is persisted and returned as given and never inferred from the file name, so an artifact
/// saved as <c>application/octet-stream</c> stays that (gotchas: artifact blobs are octet-stream).</para>
///
/// <para><b>The model read URL is a constant reference, not a fetchable URL.</b> The session persists the URL in grain
/// state (2 MB cap, ADR 0004), so a <c>data:</c> URL could not live there, and a hosted model cannot reach this
/// host. <see cref="GetReadUrlAsync"/> returns <c>agent42-attachment://local/{ownerKey}/{id}</c>, tiny and the same every
/// call (so ADR 0104's memoization holds, and a prompt-prefix cache is never invalidated by a re-signed URL).
/// <see cref="AttachmentInlineChatClient"/> swaps it for the image bytes at request time.</para>
/// </summary>
public sealed class FileAttachmentStore(LocalFileStore files) : IAttachmentStore
{
    private const string ContentFolder = "attachments/";
    private const string MetaFolder = "attachment-meta/";

    /// <summary>The scheme of the reference URL <see cref="GetReadUrlAsync"/> returns.</summary>
    public const string ReferenceScheme = "agent42-attachment";

    private sealed record Meta(string MediaType, string FileName);

    public async Task<AttachmentRef> SaveAsync(
        string ownerKey, string fileName, string mediaType, byte[] content, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("n");
        await WriteAsync(ownerKey, id, content, new Meta(mediaType, fileName), cancellationToken);
        return new AttachmentRef(id, $"{ownerKey}/{id}", mediaType, fileName);
    }

    public async Task<AttachmentRef?> GetAsync(string ownerKey, string attachmentId, CancellationToken cancellationToken = default)
    {
        var meta = await ReadMetaAsync(ownerKey, attachmentId, cancellationToken);
        return meta is null || !files.Exists(ContentKey(ownerKey, attachmentId))
            ? null
            : new AttachmentRef(attachmentId, $"{ownerKey}/{attachmentId}", meta.MediaType, meta.FileName);
    }

    public async Task<AttachmentContent?> OpenReadAsync(string ownerKey, string attachmentId, CancellationToken cancellationToken = default)
    {
        var meta = await ReadMetaAsync(ownerKey, attachmentId, cancellationToken);
        var bytes = meta is null ? null : await files.ReadAsync(ContentKey(ownerKey, attachmentId), cancellationToken);
        return bytes is null ? null : new AttachmentContent(bytes, meta!.MediaType, meta.FileName);
    }

    public Task<Uri> GetReadUrlAsync(AttachmentRef reference, CancellationToken cancellationToken = default)
        => Task.FromResult(ReferenceUrl(reference.BlobPath));

    public async Task<AttachmentRef> CopyAsync(AttachmentRef source, string targetOwnerKey, CancellationToken cancellationToken = default)
    {
        var sourceOwner = OwnerOf(source);
        var content = await OpenReadAsync(sourceOwner, source.Id, cancellationToken)
            ?? throw new FileNotFoundException($"Attachment '{source.Id}' does not exist.");
        await WriteAsync(targetOwnerKey, source.Id, content.Bytes, new Meta(content.MediaType, content.FileName), cancellationToken);
        return new AttachmentRef(source.Id, $"{targetOwnerKey}/{source.Id}", content.MediaType, content.FileName);
    }

    /// <summary>The reference URL for a blob path (<c>{ownerKey}/{id}</c>), each segment escaped.</summary>
    public static Uri ReferenceUrl(string blobPath)
        => new($"{ReferenceScheme}://local/{string.Join('/', blobPath.Split('/').Select(Uri.EscapeDataString))}");

    /// <summary>Splits a reference URL back into its owner key and attachment id; false for any other URL.</summary>
    public static bool TryParseReference(Uri url, out string ownerKey, out string attachmentId)
    {
        ownerKey = attachmentId = "";
        if (!url.Scheme.Equals(ReferenceScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (segments.Length < 2)
        {
            return false;
        }

        attachmentId = segments[^1];
        ownerKey = string.Join('/', segments[..^1]);
        return true;
    }

    private static string OwnerOf(AttachmentRef reference)
        => reference.BlobPath.EndsWith("/" + reference.Id, StringComparison.Ordinal)
            ? reference.BlobPath[..^(reference.Id.Length + 1)]
            : throw new ArgumentException("The attachment's blob path does not end in its id.", nameof(reference));

    // Each owner-key segment is escaped (a conversation id is caller-supplied on some surfaces and may hold a '/'), so
    // neither the owner nor the id can add a level or step out of its own folder; LocalFileStore refuses '..' on top.
    private static string Escaped(string ownerKey, string id)
        => string.Join('/', ownerKey.Split('/').Append(id).Select(Uri.EscapeDataString));

    private static string ContentKey(string ownerKey, string id) => ContentFolder + Escaped(ownerKey, id);

    private static string MetaKey(string ownerKey, string id) => MetaFolder + Escaped(ownerKey, id) + ".json";

    private async Task WriteAsync(string ownerKey, string id, byte[] content, Meta meta, CancellationToken cancellationToken)
    {
        await files.WriteAsync(ContentKey(ownerKey, id), content, cancellationToken);
        await files.WriteAsync(MetaKey(ownerKey, id), JsonSerializer.SerializeToUtf8Bytes(meta), cancellationToken);
    }

    private async Task<Meta?> ReadMetaAsync(string ownerKey, string id, CancellationToken cancellationToken)
    {
        var bytes = await files.ReadAsync(MetaKey(ownerKey, id), cancellationToken);
        return bytes is null ? null : JsonSerializer.Deserialize<Meta>(bytes);
    }
}
