using System.Text;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>One attached file's outcome at run start, as the <c>documents.ingested</c> event carries it.
/// <see cref="TextCopy"/> is the workspace path of its generated text copy, and <see cref="TextCopySkipped"/> why none
/// was written although one was due.</summary>
public sealed record FleetIngestedFile(string Name, string Status, int Chunks, string? Reason, string? TextCopy = null, string? TextCopySkipped = null);

/// <summary>The run-start ingest's outcome: per file, and the record's new <c>Documents</c> and <c>TextCopies</c>.</summary>
public sealed record FleetIngestResult(
    IReadOnlyList<FleetIngestedFile> Files, IReadOnlyList<string> Documents, IReadOnlyList<string> TextCopies);

/// <summary>
/// Indexes a fleet task's attached files when its run starts (ADR 0100), into the workspace's vector store
/// (<see cref="FleetTaskService.DocumentOwnerKey"/>). Per document: extract, write a <c>.txt</c> copy next to it when
/// the original is not text already (so every agent, Copilot ones included, can read it from disk), then chunk, embed
/// and index. <b>Nothing here fails the task</b>: any error but the task's own cancellation becomes that file's reason.
/// A document that was indexed before (a follow-up re-uploading it) and now fails loses its old chunks too, so no
/// tool ever returns a stale version of changed bytes.
/// </summary>
public sealed class FleetDocumentIngest(DocumentIngestionService ingestion, IDocumentIndex index, FleetOptions options, ILogger logger)
{
    public const string Indexed = "indexed";
    public const string NotIndexed = "not indexed";
    public const string StoredOnly = "stored only";

    private const string TextCopyCut = "\n\n[text copy cut off at the file size limit]";

    /// <summary>The outcome without an index: every file stored only, the record's lists unchanged.</summary>
    public static FleetIngestResult FilesOnly(FleetTaskRecord record, string reason)
        => new([.. record.Inputs.Select(name => new FleetIngestedFile(name, StoredOnly, 0, reason))], record.Documents, record.TextCopies);

    public async Task<FleetIngestResult> IngestAsync(FleetTaskRecord record, FleetWorkspace workspace, CancellationToken cancellationToken)
    {
        var ownerKey = FleetTaskService.DocumentOwnerKey(record.UserId, record.WorkspaceId);
        var documents = record.Documents.ToList();
        // A user file that now holds a carried-forward copy's path is the user's, never a generated copy.
        var textCopies = record.TextCopies.Where(c => !record.Inputs.Any(i => FleetTaskService.InputPath(i) == c)).ToList();
        var files = new List<FleetIngestedFile>();
        foreach (var name in record.Inputs)
        {
            var mediaType = DocumentMediaTypes.ResolveMediaType(name, null);
            if (!DocumentMediaTypes.IsSupported(mediaType))
            {
                files.Add(new FleetIngestedFile(name, StoredOnly, 0, null));
                continue;
            }

            var file = await IngestOneAsync(record, workspace, ownerKey, name, mediaType, cancellationToken);
            files.Add(file);
            UpdateTextCopy(workspace, textCopies, name, file);

            if (file.Status == Indexed)
            {
                if (!documents.Contains(name, StringComparer.Ordinal))
                {
                    documents.Add(name);
                }
            }
            else if (documents.Remove(name))
            {
                await DropStaleAsync(ownerKey, name);
            }
        }

        return new FleetIngestResult(files, documents, textCopies);
    }

    // Records the document's new copy. A re-uploaded document whose copy wasn't rewritten (extraction failed, or no
    // room) loses its old copy, which no longer matches its bytes; the next sync removes it from Blob too.
    private static void UpdateTextCopy(FleetWorkspace workspace, List<string> textCopies, string name, FleetIngestedFile file)
    {
        var path = FleetTaskService.InputPath(name + ".txt");
        if (file.TextCopy is not null)
        {
            if (!textCopies.Contains(path, StringComparer.Ordinal))
            {
                textCopies.Add(path);
            }
        }
        else if (textCopies.Remove(path))
        {
            File.Delete(workspace.Resolve(path));
        }
    }

    private async Task<FleetIngestedFile> IngestOneAsync(
        FleetTaskRecord record, FleetWorkspace workspace, string ownerKey, string name, string mediaType, CancellationToken cancellationToken)
    {
        (string? Path, string? Skipped) copy = default;
        try
        {
            var content = await File.ReadAllBytesAsync(workspace.Resolve(FleetTaskService.InputPath(name)), cancellationToken);
            var segments = ingestion.Extract(name, mediaType, content);
            copy = await WriteTextCopyAsync(record, workspace, name, mediaType, segments, cancellationToken);
            var ingested = await ingestion.IngestSegmentsAsync(ownerKey, name, segments, maxChunks: null, cancellationToken);
            return ingested.ChunkCount == 0
                ? new FleetIngestedFile(name, NotIndexed, 0, "no extractable text", copy.Path, copy.Skipped)
                : new FleetIngestedFile(name, Indexed, ingested.ChunkCount, null, copy.Path, copy.Skipped);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fleet task {TaskId}: '{File}' could not be indexed", record.Id, name);
            return new FleetIngestedFile(name, NotIndexed, 0, ex.Message, copy.Path, copy.Skipped);
        }
    }

    // The .txt copy of a PDF or Office document; none for a text file, which is readable as it is. Skipped, and said
    // so, when its name is one of the inputs or it doesn't fit the workspace caps the sync enforces.
    private async Task<(string? Path, string? Skipped)> WriteTextCopyAsync(
        FleetTaskRecord record, FleetWorkspace workspace, string name, string mediaType,
        IReadOnlyList<DocumentSegment> segments, CancellationToken cancellationToken)
    {
        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || segments.Count == 0)
        {
            return default;
        }

        if (record.Inputs.Contains(name + ".txt", StringComparer.OrdinalIgnoreCase))
        {
            return (null, "an attached file has its name");
        }

        var path = FleetTaskService.InputPath(name + ".txt");
        var content = TextCopy(segments);
        var fullPath = workspace.Resolve(path);
        var files = workspace.ListFiles().Where(f => f != path).ToList();
        var bytes = files.Sum(f => new FileInfo(workspace.Resolve(f)).Length);
        if (files.Count + 1 > options.MaxWorkspaceFiles || bytes + content.LongLength > options.MaxWorkspaceBytes)
        {
            return (null, "workspace full");
        }

        await File.WriteAllBytesAsync(fullPath, content, cancellationToken);
        return (path, null);
    }

    // The segments as UTF-8 text, with a page line for each new PDF page, cut at MaxInputFileBytes.
    private byte[] TextCopy(IReadOnlyList<DocumentSegment> segments)
    {
        var text = new StringBuilder();
        int? page = null;
        foreach (var segment in segments)
        {
            if (segment.Page is { } current && current != page)
            {
                text.Append($"[page {current}]\n");
                page = current;
            }

            text.Append(segment.Text).Append("\n\n");
        }

        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        if (bytes.LongLength <= options.MaxInputFileBytes)
        {
            return bytes;
        }

        // Back up to a character boundary, so the copy stays valid UTF-8 (the download shows it as text).
        var cut = Encoding.UTF8.GetBytes(TextCopyCut);
        var length = (int)Math.Max(options.MaxInputFileBytes - cut.Length, 0);
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
        {
            length--;
        }

        return [.. bytes.AsSpan(0, length), .. cut];
    }

    private async Task DropStaleAsync(string ownerKey, string name)
    {
        try
        {
            await index.DeleteFileAsync(ownerKey, name, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fleet: the stale chunks of '{File}' could not be deleted", name);
        }
    }
}
