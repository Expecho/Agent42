namespace Agent42.Agents;

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
}
