namespace Agent42.Agents.Documents;

/// <summary>
/// Extracts ordered text segments from an uploaded document so it can be chunked, embedded and
/// indexed for retrieval (the "documents" counterpart to the image <see cref="IAttachmentStore"/>
/// path — documents are ingest-only and never become chat attachments, see ADR 0007). Local
/// libraries only: no cloud OCR, so scanned/image-only PDFs yield no text (out of scope).
///
/// Extraction is deterministic and needs no Azure, so the same real implementation runs in
/// production, the offline DevHost and tests — unlike the embedding generator, which is faked
/// offline.
/// </summary>
public interface IDocumentTextExtractor
{
    /// <summary>
    /// Extracts <paramref name="content"/> into ordered segments. PDF segments carry real 1-based
    /// page numbers; Office/text segments carry <c>null</c> pages (chunk-index granularity).
    /// Throws <see cref="NotSupportedException"/> for a media type <see cref="DocumentMediaTypes.IsSupported"/>
    /// rejects.
    /// </summary>
    IReadOnlyList<DocumentSegment> Extract(string fileName, string mediaType, byte[] content);
}
