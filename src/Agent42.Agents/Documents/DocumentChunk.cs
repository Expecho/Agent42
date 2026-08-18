namespace Agent42.Agents;

/// <summary>
/// A retrievable slice of an ingested document plus the metadata needed to cite it:
/// the source <paramref name="FileName"/>, the <paramref name="Page"/> when the format has
/// real pages (else <c>null</c>), and the sequential <paramref name="ChunkIndex"/> within the
/// document. The <paramref name="Text"/> is what the retrieval tool returns to the model.
///
/// Only chunk text + citation metadata are persisted in the vector store — never the raw
/// document bytes (those follow the same blob rule as image attachments, ADR 0004). This is a
/// plain record: the document index is an in-process service (like <see cref="IAttachmentStore"/>),
/// not a grain interface, so it never crosses the Orleans serialization boundary.
/// </summary>
public sealed record DocumentChunk(string FileName, int? Page, int ChunkIndex, string Text);

/// <summary>A <see cref="DocumentChunk"/> paired with its embedding vector, ready to upsert.</summary>
public sealed record EmbeddedChunk(DocumentChunk Chunk, ReadOnlyMemory<float> Embedding);
