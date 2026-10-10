using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Writes conversations into a ZIP (ADR 0091): each image attachment and artifact version, then
/// <c>conversation.md</c> and <c>conversation.json</c> (last, so they link only files that made it in), and finally
/// one <c>manifest.json</c>. The archive's stream is the endpoint's disk-backed buffer, never the response body:
/// <see cref="ZipArchive"/> disposes each entry synchronously even through its async APIs.
/// </summary>
public static class ConversationZipWriter
{
    public static async Task WriteConversationAsync(
        ZipArchive zip, string folder, ConversationExport export, IAttachmentStore store, string ownerKey,
        DateTimeOffset exportedAt, ExportManifest manifest, CancellationToken ct)
    {
        var attachments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attachment in export.Turns.SelectMany(t => t.Attachments ?? []).DistinctBy(a => a.Id))
        {
            var path = ExportFileNames.Attachment(attachment);
            if (await TryCopyBlobAsync(zip, folder + path, store, ownerKey, attachment.Id, manifest, ct))
            {
                attachments[attachment.Id] = path;
            }
        }

        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var artifact in export.Artifacts)
        {
            foreach (var version in artifact.Versions)
            {
                var path = ExportFileNames.ArtifactVersion(artifact, version.Version);
                if (await TryCopyBlobAsync(zip, folder + path, store, ownerKey, version.AttachmentId, manifest, ct))
                {
                    artifacts[ExportFiles.ArtifactKey(artifact.Id, version.Version)] = path;
                }
            }
        }

        var files = new ExportFiles(attachments, artifacts);
        await WriteEntryAsync(zip, folder + "conversation.md",
            Encoding.UTF8.GetBytes(ConversationMarkdown.Render(export, exportedAt, files)), CompressionLevel.Optimal, ct);
        await WriteEntryAsync(zip, folder + "conversation.json",
            JsonSerializer.SerializeToUtf8Bytes(ConversationExportDocument.From(export, exportedAt, files), ConversationExportDocument.Json),
            CompressionLevel.Optimal, ct);
        manifest.Conversations.Add(new ExportManifestEntry(export.Conversation.Id, export.Conversation.Title, folder));
    }

    public static Task WriteManifestAsync(ZipArchive zip, ExportManifest manifest, CancellationToken ct)
        => WriteEntryAsync(zip, "manifest.json",
            JsonSerializer.SerializeToUtf8Bytes(manifest, ExportManifest.Json), CompressionLevel.Optimal, ct);

    // A blob that is gone, or whose read fails, is listed under `missing` and skipped: the read happens before the
    // entry is created, so the archive stays valid without it, and one bad blob never fails a whole bulk part.
    private static async Task<bool> TryCopyBlobAsync(
        ZipArchive zip, string entryName, IAttachmentStore store, string ownerKey, string attachmentId,
        ExportManifest manifest, CancellationToken ct)
    {
        AttachmentContent? stored;
        try
        {
            stored = await store.OpenReadAsync(ownerKey, attachmentId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stored = null;
        }

        if (stored is null)
        {
            manifest.Missing.Add(entryName);
            return false;
        }

        // Images are compressed already; artifact sources are text.
        await WriteEntryAsync(zip, entryName, stored.Bytes, CompressionLevel.Fastest, ct);
        return true;
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, byte[] bytes, CompressionLevel level, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, level);
        await using var stream = await entry.OpenAsync(ct);
        await stream.WriteAsync(bytes, ct);
    }
}

/// <summary>
/// The archive's <c>manifest.json</c>: what it holds, which files were missing, and (bulk) which conversations could
/// not be read and the part's place in the walk.
/// </summary>
public sealed class ExportManifest
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Schema { get; init; } = "agent42.export-manifest/1";

    public DateTimeOffset ExportedAt { get; init; }

    /// <summary>Bulk only: the part's 1-based number, the part count and the listed conversation count.</summary>
    public int? Part { get; init; }

    public int? Parts { get; init; }

    public int? Total { get; init; }

    public List<ExportManifestEntry> Conversations { get; } = [];

    /// <summary>Entries whose blob was gone when the archive was written.</summary>
    public List<string> Missing { get; } = [];

    /// <summary>Bulk only: conversations whose gather failed; the part carried on without them.</summary>
    public List<ExportManifestError> Errors { get; } = [];
}

public sealed record ExportManifestEntry(string Id, string Title, string Path);

public sealed record ExportManifestError(string Id, string Title, string Error);
