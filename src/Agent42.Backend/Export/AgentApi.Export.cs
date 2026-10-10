using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Microsoft.AspNetCore.WebUtilities;

namespace Agent42.Backend;

/// <summary>
/// The owner's export of their conversations (ADR 0091): one conversation as Markdown, JSON or a ZIP, and every
/// conversation as keyset-paged ZIP parts. Behind Easy Auth like every <c>/api/conversations</c> route; nothing here
/// is public, and nothing under <c>/api/share</c> changes.
/// </summary>
public static partial class AgentApi
{
    private static void MapExportEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/conversations/{conversationId}/export", ExportConversationAsync);
        app.MapGet("/api/export", ExportPartAsync);
    }

    // One conversation. Tool calls are KEPT, unlike the public share route's strip: this is the owner's own
    // /messages projection handed to the owner, not third-party egress (ADR 0091 vs ADR 0035/0080).
    private static async Task<IResult> ExportConversationAsync(
        string conversationId, string? format, IGrainFactory grains, IAttachmentStore attachments,
        ILoggerFactory loggers, HttpContext http)
    {
        format = (format ?? "markdown").ToLowerInvariant();
        if (format is not ("markdown" or "json" or "zip"))
        {
            return Results.Text("Unknown format. Use markdown, json or zip.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        // The index gate comes first: an ephemeral chat or an id the user never used is a 404 before any agent grain
        // is activated (ADR 0034, gotcha "A caller-supplied conversation id needs THREE defences").
        var userId = http.GetUserId();
        var conversation = await ConversationExporter.FindListedAsync(grains, userId, conversationId);
        if (conversation is null)
        {
            return Results.NotFound();
        }

        var ownerKey = AgentKey(userId, conversation.Id);
        var export = await ConversationExporter.GatherAsync(grains, ownerKey, conversation);
        var exportedAt = DateTimeOffset.UtcNow;
        AddExportHeaders(http.Response);
        return format switch
        {
            "json" => Results.File(
                JsonSerializer.SerializeToUtf8Bytes(ConversationExportDocument.From(export, exportedAt, null), ConversationExportDocument.Json),
                "application/json; charset=utf-8", ExportFileNames.Download(conversation, "json")),
            "zip" => ZipResult(http, ExportFileNames.Download(conversation, "zip"), loggers,
                (zip, ct) => WriteSingleZipAsync(zip, export, attachments, ownerKey, exportedAt, ct)),
            _ => Results.File(
                Encoding.UTF8.GetBytes(ConversationMarkdown.Render(export, exportedAt)),
                "text/markdown; charset=utf-8", ExportFileNames.Download(conversation, "md")),
        };
    }

    private static async Task WriteSingleZipAsync(
        ZipArchive zip, ConversationExport export, IAttachmentStore attachments, string ownerKey,
        DateTimeOffset exportedAt, CancellationToken ct)
    {
        var manifest = new ExportManifest { ExportedAt = exportedAt };
        await ConversationZipWriter.WriteConversationAsync(zip, "", export, attachments, ownerKey, exportedAt, manifest, ct);
        await ConversationZipWriter.WriteManifestAsync(zip, manifest, ct);
    }

    // One part of "export all". Keyset-paged on the conversation id (`after` = the previous part's last id), not
    // offset-paged like /api/search/reindex: offset paging over a list that changes mid-walk skips a conversation,
    // which a backfill tolerates and an export does not. Part membership is decided up front, so every header is
    // set before the first byte.
    private static async Task<IResult> ExportPartAsync(
        string? after, IGrainFactory grains, IAttachmentStore attachments, ILoggerFactory loggers, HttpContext http)
    {
        var userId = http.GetUserId();
        var listed = (await grains.GetGrain<IConversationIndexGrain>(userId).GetConversationsAsync())
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
        var done = string.IsNullOrEmpty(after) ? 0 : listed.Count(c => string.CompareOrdinal(c.Id, after) <= 0);
        var part = listed.Skip(done).Take(ExportLimits.BulkBatchSize).ToList();
        var parts = Math.Max(1, (listed.Count + ExportLimits.BulkBatchSize - 1) / ExportLimits.BulkBatchSize);
        var manifest = new ExportManifest
        {
            ExportedAt = DateTimeOffset.UtcNow,
            Part = Math.Min(parts, (done / ExportLimits.BulkBatchSize) + 1),
            Parts = parts,
            Total = listed.Count,
        };

        AddExportHeaders(http.Response);
        var headers = http.Response.Headers;
        headers["X-Export-Total"] = listed.Count.ToString(CultureInfo.InvariantCulture);
        headers["X-Export-Part"] = manifest.Part.Value.ToString(CultureInfo.InvariantCulture);
        headers["X-Export-Parts"] = parts.ToString(CultureInfo.InvariantCulture);
        if (done + part.Count < listed.Count)
        {
            headers["X-Export-Next"] = part[^1].Id;
        }

        var fileName = string.Create(CultureInfo.InvariantCulture,
            $"agent42-export-{manifest.ExportedAt.UtcDateTime:yyyy-MM-dd}-part-{manifest.Part}-of-{parts}.zip");
        var logger = loggers.CreateLogger("Agent42.Backend.Export");
        return ZipResult(http, fileName, loggers,
            (zip, ct) => WriteBulkPartAsync(zip, part, userId, grains, attachments, manifest, logger, ct));
    }

    // Each conversation is gathered before its entries are written. One whose gather fails is listed under `errors`
    // and the part carries on: by then the status code is long committed, and one bad grain must not cost the rest.
    private static async Task WriteBulkPartAsync(
        ZipArchive zip, IReadOnlyList<ConversationInfo> part, string userId, IGrainFactory grains,
        IAttachmentStore attachments, ExportManifest manifest, ILogger logger, CancellationToken ct)
    {
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var conversation in part)
        {
            ct.ThrowIfCancellationRequested();
            var ownerKey = AgentKey(userId, conversation.Id);
            ConversationExport export;
            try
            {
                export = await ConversationExporter.GatherAsync(grains, ownerKey, conversation);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Export could not read conversation {ConversationId}", conversation.Id);
                manifest.Errors.Add(new ExportManifestError(conversation.Id, conversation.Title, "The conversation could not be read."));
                continue;
            }

            // The folder name carries only the id's first eight characters; a clash takes the whole id.
            var folder = ExportFileNames.Folder(conversation);
            if (!folders.Add(folder))
            {
                folder = $"{folder.TrimEnd('/')}-{ExportFileNames.Token(conversation.Id)}/";
                folders.Add(folder);
            }

            await ConversationZipWriter.WriteConversationAsync(zip, folder, export, attachments, ownerKey, manifest.ExportedAt, manifest, ct);
        }

        await ConversationZipWriter.WriteManifestAsync(zip, manifest, ct);
    }

    // Writes a ZIP into a disk-backed buffer, then drains it to the response. Not straight into the body: even the
    // async ZipArchive APIs dispose each entry's stream SYNCHRONOUSLY (WrappedStream has no DisposeAsync of its own,
    // so DeflateStream flushes with a sync Write), and Kestrel disallows synchronous IO (gotchas.md). The buffer
    // holds memory up to its threshold and spills to a temp file past it, so a large part never sits in memory.
    //
    // A failure while building throws before a byte is sent, so the client gets a plain 500. A failure while
    // draining aborts the connection, so the browser shows a failed download, never a truncated archive.
    private static IResult ZipResult(
        HttpContext http, string fileName, ILoggerFactory loggers, Func<ZipArchive, CancellationToken, Task> write)
        => Results.Stream(async body =>
        {
            var ct = http.RequestAborted;
            await using var buffer = new FileBufferingWriteStream(memoryThreshold: ExportLimits.ZipMemoryThresholdBytes);
            var zip = await ZipArchive.CreateAsync(buffer, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, ct);
            await write(zip, ct);
            await zip.DisposeAsync();
            try
            {
                await buffer.DrainBufferAsync(body, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loggers.CreateLogger("Agent42.Backend.Export").LogError(ex, "Export failed after the response started");
                http.Abort();
            }
        }, "application/zip", fileName);

    private static void AddExportHeaders(HttpResponse response)
    {
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.CacheControl = "private, no-store";
    }
}
