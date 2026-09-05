using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The <see cref="IDocumentIngestor"/> port over the real <see cref="DocumentIngestionService"/>
/// (ADR 0066).
///
/// <para>A thin adapter, deliberately: the ingestion pipeline is where the concrete index and embedding
/// clients are wired, so it stays in the Backend, while <c>AgentGrain</c>'s <c>read_web_document</c>
/// tool needs to reach it from <c>Agent42.Agents</c>, which must not reference the Backend. The seam is
/// the point; there is no behaviour here.</para>
///
/// <para>Every host registers this same adapter — including DevHost and the test silos, which differ
/// only in what sits <i>under</i> it (an in-memory index and the deterministic embedding generator).
/// Only <see cref="IWebDocumentFetcher"/> has a genuinely different offline implementation, because
/// only it opens a socket.</para>
/// </summary>
public sealed class WebDocumentIngestor(DocumentIngestionService ingestion) : IDocumentIngestor
{
    public Task<IngestedDocument> IngestAsync(
        string ownerKey,
        string fileName,
        string mediaType,
        byte[] content,
        int? maxChunks = null,
        CancellationToken cancellationToken = default)
        => ingestion.IngestAsync(ownerKey, fileName, mediaType, content, maxChunks, cancellationToken);
}
