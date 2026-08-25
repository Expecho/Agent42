
using Agent42.Agents;


namespace Agent42.Backend;

/// <summary>
/// Attachment upload and download (ADR 0004): image bytes go to blob storage and never into the session item; documents are ingested for retrieval instead (ADR 0007).
/// </summary>
public static partial class AgentApi
{
    private static void MapAttachmentEndpoints(IEndpointRouteBuilder app)
    {
        // Handler bodies live in named methods below rather than inline lambdas (ADR 0050): cognitive
        // complexity sums a method's nested lambdas, so a registration method accumulates the score of
        // every endpoint it declares even though the endpoints are independent of one another.
        app.MapPost("/api/conversations/{conversationId}/attachments", UploadAttachmentAsync)
            .DisableAntiforgery();
        app.MapGet("/api/conversations/{conversationId}/attachments/{attachmentId}", DownloadAttachmentAsync);
    }

    // Uploads a file for a conversation. Branches on media type: an image becomes a chat
    // attachment (bytes in blob storage, sent to the model as a UriContent SAS — ADR 0004) and
    // returns an id the client sends back on the next turn; a supported document type is
    // ingested for retrieval (extract → chunk → embed → index, ADR 0007) and is NOT registered
    // as an attachment — it returns a document-kind marker with no id, so the client never sends
    // it back as an attachmentId (a document must never enter the image UriContent path).
    // Rejects unsupported and oversize files. Antiforgery is disabled at the registration above:
    // the app is gated by Easy Auth (ADR 0002), not cookies+forms.
    private static async Task<IResult> UploadAttachmentAsync(
        string conversationId, IFormFile file, IAttachmentStore attachments,
        DocumentIngestionService ingestion, IGrainFactory grains, HttpContext http)
    {
        if (file is null || file.Length == 0)
        {
            // text/plain (not Results.BadRequest, which JSON-encodes the string) so the client
            // can read res.text() as a clean, unquoted message for its error chip.
            return Results.Text("No file was uploaded.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > MaxAttachmentBytes)
        {
            return Results.Text($"Upload exceeds the {MaxAttachmentBytes / (1024 * 1024)} MB limit.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        // The browser's content type is unreliable for documents (Windows sends octet-stream or
        // nothing for .docx/.pptx/.xlsx/.md), so resolve it against the file extension before
        // branching — otherwise a real document is rejected as "unsupported" (ADR 0007).
        var contentType = DocumentMediaTypes.ResolveMediaType(file.FileName, file.ContentType);
        var ownerKey = AgentKey(http.GetUserId(), conversationId);

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        var bytes = buffer.ToArray();

        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            var reference = await attachments.SaveAsync(ownerKey, file.FileName, contentType, bytes);
            // Return only what the browser needs (id + display metadata) — the internal blob
            // path stays server-side, addressed by id under the authenticated user's key.
            return Results.Json(new AttachmentMetadata(reference.Id, reference.MediaType, reference.FileName));
        }

        if (DocumentMediaTypes.IsSupported(contentType))
        {
            // `bytes` is passed through, never re-read from the IFormFile: the stream has already been
            // consumed above, and reading it twice would be both wasteful and a behaviour change.
            return await IngestDocumentAsync(file.FileName, bytes, ingestion, grains, ownerKey, contentType);
        }

        return Results.Text("Only image and document (PDF, Office, text) attachments are supported.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
    }

    // The document arm of the upload, split out so the media-type branch above reads as the three-way
    // choice it is (image / document / unsupported) rather than carrying the ingestion detail inline.
    private static async Task<IResult> IngestDocumentAsync(
        string fileName, byte[] bytes, DocumentIngestionService ingestion, IGrainFactory grains,
        string ownerKey, string contentType)
    {
        int chunkCount;
        try
        {
            chunkCount = await ingestion.IngestAsync(ownerKey, fileName, contentType, bytes);
        }
        catch (Exception ex) when (ex is EmbeddingModelNotConfiguredException or EmbeddingDimensionMismatchException)
        {
            // No embedding model, or one whose vectors don't fit the store (ADR 0007). The composer
            // gates document attachment client-side; this is defense-in-depth for the API.
            return Results.Text(ex.Message, "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        // Tell the grain the document exists (only when it yielded searchable text), so the
        // model is made aware of it and automatic retrieval is enabled for the conversation
        // (ADR 0008) — a document is otherwise invisible to the model (ADR 0007).
        if (chunkCount > 0)
        {
            await grains.GetGrain<IAgentGrain>(ownerKey).RegisterDocumentAsync(fileName);
        }

        // No id: a document is retrieval-only, never sent back as an attachment (ADR 0007).
        return Results.Json(new DocumentUploadResult("document", fileName, contentType, chunkCount));
    }

    // Streams a stored attachment's bytes to the browser. First-party proxy (authenticated by
    // Easy Auth, user-scoped by grain key), so the page never holds a blob SAS (constraint 6).
    private static async Task<IResult> DownloadAttachmentAsync(
        string conversationId, string attachmentId, IAttachmentStore attachments, HttpContext http)
    {
        var content = await attachments.OpenReadAsync(AgentKey(http.GetUserId(), conversationId), attachmentId);
        return content is null
            ? Results.NotFound()
            : Results.File(content.Bytes, content.MediaType);
    }
}
