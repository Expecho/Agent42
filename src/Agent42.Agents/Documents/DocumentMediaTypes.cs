namespace Agent42.Agents.Documents;

/// <summary>
/// The document MIME types the ingestion path accepts. The upload handler branches on this: an
/// <c>image/*</c> type keeps the existing attachment path (ADR 0004), a supported document type
/// is ingested for retrieval (ADR 0007), anything else is rejected. Kept in one place so the
/// handler, the extractor and the frontend's <c>accept</c> list can't drift apart.
/// </summary>
public static class DocumentMediaTypes
{
    public const string Pdf = "application/pdf";
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string Pptx = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string PlainText = "text/plain";
    public const string Markdown = "text/markdown";

    // The browser-supplied content type is unreliable: Windows registers no MIME type for many
    // document extensions, so browsers send application/octet-stream (or nothing) for .docx/.pptx/
    // .xlsx/.md — and OOXML files sometimes arrive as application/zip. When the supplied type isn't
    // one we recognize, we fall back to the extension, so those uploads still ingest.
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = Pdf,
        [".docx"] = Docx,
        [".pptx"] = Pptx,
        [".xlsx"] = Xlsx,
        [".txt"] = PlainText,
        [".md"] = Markdown,
        [".markdown"] = Markdown,
    };

    /// <summary>
    /// True when <paramref name="mediaType"/> is a document type this pipeline can extract. Any
    /// <c>text/*</c> type is treated as plain UTF-8 text, so an editor sending, say,
    /// <c>text/x-markdown</c> for a <c>.md</c> file still ingests.
    /// </summary>
    public static bool IsSupported(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        // Ignore any "; charset=..." parameter a browser may append.
        var type = mediaType.Split(';', 2)[0].Trim();
        return type switch
        {
            Pdf or Docx or Pptx or Xlsx => true,
            _ => type.StartsWith("text/", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// The effective media type for an upload, rescuing the common case where the browser sends a
    /// generic type (<c>application/octet-stream</c>, <c>application/zip</c>) or none at all for a
    /// document. Returns <paramref name="providedMediaType"/> when it is an <c>image/*</c> or a
    /// supported document type; otherwise falls back to the type implied by <paramref name="fileName"/>'s
    /// extension, or the (unrecognized) provided type when the extension is unknown too. The upload
    /// handler branches on the result, so it must resolve to the extractor's canonical type.
    /// </summary>
    public static string ResolveMediaType(string fileName, string? providedMediaType)
    {
        var type = (providedMediaType ?? "").Split(';', 2)[0].Trim();
        if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || IsSupported(type))
        {
            return type;
        }

        var extension = Path.GetExtension(fileName);
        return ByExtension.TryGetValue(extension, out var mapped) ? mapped : type;
    }
}
