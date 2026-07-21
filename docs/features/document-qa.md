# Document Q&A (RAG)

> Drop in PDFs, Office files, or text; the agent automatically pulls the most relevant excerpts
> into its answers (retrieval-augmented), so you can ask questions about your own documents.
> ADRs 0007 · 0008.

## What it is

Upload a document to a conversation and it is extracted, chunked, embedded, and stored. From
then on, every reply is preceded by an automatic retrieval step that injects the most relevant
excerpts (with citations) — you never call a "search my docs" tool; it's **always-on** for any
conversation that has an ingested document.

Uploaded documents are **ingest-only** — they never become chat attachments (they're off the
image `UriContent` path).

### Prerequisite: a default embedding model (ADR 0030)

RAG needs an **embedding model**, and it is **per-user** — not an operator prerequisite. Add a model
to a provider, tick **embeddings** on it, then pick it as your **default embedding model** in
Preferences → Model providers. Until you do, the composer **blocks attaching documents** (images are
unaffected). If you clear the embedding model on a conversation that already has documents, replies
still work — retrieval just **degrades to empty context, never errors**. The model must produce
**3072-dimension** vectors (the fixed `doc-chunks` container — ingest rejects a mismatch with a clear
message); switching embedding models means **deleting and re-uploading** existing documents.

## How it works

Ingestion (extract → chunk → embed → upsert) happens in the backend when you upload.
Retrieval is a **Microsoft Agent Framework `TextSearchProvider`** that runs `BeforeAIInvoke`
and injects excerpts as a context message the model sees but that is kept out of the persisted
session.

```mermaid
flowchart TD
    subgraph Ingest["Upload → DocumentIngestionService"]
      EX[Extract text: PDF/docx/pptx/xlsx/text] --> CH[Chunk 1000/overlap 200]
      CH --> EMB["Embed (your default embedding model — GetEmbeddingGenerator)"]
      EMB --> UP["Upsert → Cosmos vector container (doc-chunks)"]
    end
    UP --> REG["RegisterDocumentAsync → DocumentNames++ → rebuild agent"]
    subgraph Turn["Every subsequent turn"]
      TSP["TextSearchProvider (BeforeAIInvoke)"] --> SRCH["SearchDocumentContextAsync: embed query → top-5"]
      SRCH --> INJ["inject cited excerpts as AIContextProvider message"]
      INJ --> MODEL[model reply]
    end
```

### Ingestion pipeline

```csharp
public async Task<int> IngestAsync(
    string ownerKey, string fileName, string mediaType, byte[] content, CancellationToken cancellationToken = default)
{
    var segments = extractor.Extract(fileName, mediaType, content);
    var chunks = chunker.Chunk(fileName, segments);
    if (chunks.Count == 0) { return 0; }

    // The OWNER's default embedding model (ADR 0030): userId from the ownerKey grain key, never GetUserId
    // (the /api/agent-run path has no principal). No model → EmbeddingModelNotConfiguredException → 4xx.
    var userId = UserIdFromOwnerKey(ownerKey);
    var factory = await chatClientFactoryProvider.GetForUserAsync(userId, cancellationToken);
    if (string.IsNullOrEmpty(factory.DefaultEmbeddingModelId)) { throw new EmbeddingModelNotConfiguredException(); }

    var generated = await factory.GetEmbeddingGenerator().GenerateAsync(
        chunks.Select(c => c.Text), cancellationToken: cancellationToken);

    // The prod container is fixed-dimension (3072) — reject a mismatch before any upsert (ADR 0007/0030).
    if (index.ExpectedDimensions is int expected && generated.Count > 0 && generated[0].Vector.Length != expected)
    { throw new EmbeddingDimensionMismatchException(expected, generated[0].Vector.Length); }

    var embedded = chunks
        .Zip(generated, (chunk, embedding) => new EmbeddedChunk(chunk, embedding.Vector)).ToList();

    await index.UpsertChunksAsync(ownerKey, embedded, cancellationToken);
    return chunks.Count;
}
```

### The always-on retrieval provider

Built only when the conversation has documents; runs before every model call:

```csharp
private TextSearchProvider BuildDocumentSearchProvider() =>
    new(
        SearchDocumentContextAsync,
        new TextSearchProviderOptions
        {
            SearchTime = TextSearchProviderOptions.TextSearchBehavior.BeforeAIInvoke,
            RecentMessageMemoryLimit = 5,
            CitationsPrompt = "When you use an excerpt, cite the source file and the page or chunk shown with it.",
        },
        loggerFactory: null);
```

The search callback degrades to empty when no embedding model is configured (never throws), else
embeds the query, retrieves top-5, and maps to cited results:

```csharp
// ADR 0030: no embedding model → empty context, never a thrown turn (the no-model-chat discipline).
if (string.IsNullOrEmpty(_chatClientFactory.DefaultEmbeddingModelId)) { return []; }

var generated = await _chatClientFactory.GetEmbeddingGenerator()
    .GenerateAsync([query], cancellationToken: cancellationToken);
var results = await _documentIndex.SearchAsync(
    this.GetPrimaryKeyString(), generated[0].Vector, topK: 5, cancellationToken);
...
return results.Select(chunk => new TextSearchProvider.TextSearchResult
{
    Text = chunk.Text,
    SourceName = chunk.Page is int page ? $"{chunk.FileName}, p.{page}" : $"{chunk.FileName}, chunk {chunk.ChunkIndex}",
    RawRepresentation = chunk,
});
```

### The vector store

Cosmos NoSQL `VectorDistance` top-k, partitioned by the owner (grain) key:

```csharp
var query = new QueryDefinition(
        $"SELECT TOP {topK} c.fileName, c.page, c.chunkIndex, c.text " +
        "FROM c ORDER BY VectorDistance(c.embedding, @vector)")
    .WithParameter("@vector", queryEmbedding.ToArray());
var options = new QueryRequestOptions { PartitionKey = new PartitionKey(ownerKey) };
```

The index is prod-only; offline/tests use an in-memory cosine-similarity index:

```csharp
var docChunksContainer = builder.Configuration["Cosmos:DocChunksContainerName"];
if (!string.IsNullOrWhiteSpace(docChunksContainer))
    builder.Services.AddSingleton<IDocumentIndex>(sp => new CosmosDocumentIndex(
        sp.GetRequiredService<CosmosClient>(), "agent42", docChunksContainer));
else
    builder.Services.AddSingleton<IDocumentIndex, InMemoryDocumentIndex>();
```

## How the Agent Framework fits

- `TextSearchProvider` (+ `TextSearchProviderOptions`, `TextSearchProvider.TextSearchResult`)
  is a framework `AIContextProvider`. Agent42 supplies the search delegate; the framework calls
  it `BeforeAIInvoke`, formats the excerpts, injects them tagged as an AI-context-provider
  message, and applies the `CitationsPrompt`.
- `IEmbeddingGenerator<string, Embedding<float>>` is a `Microsoft.Extensions.AI` abstraction.
- The `InMemoryChatHistoryProvider` with a storage-input filter keeps the injected excerpts out
  of the persisted session — so retrieval is cheap and the 2 MB session stays small.

## How Orleans fits

- **Retrieval scope = the grain key.** The `ownerKey` for both ingestion and search is
  `this.GetPrimaryKeyString()` = `{userId}/{conversationId}`, so a conversation can never read
  another's chunks (single-partition Cosmos queries).
- `RegisterDocumentAsync` mutates grain state (`DocumentNames`) and persists, then rebuilds the
  agent so the provider is attached.
- The vector store is **not** a grain — `DocumentChunk` never crosses the Orleans serialization
  boundary; `IDocumentIndex` is an in-process singleton (Cosmos or in-memory).
- Grain lifecycle drives cleanup: `PurgeAsync` deletes the conversation's chunks so document
  text doesn't outlive it.

## Key files

| Concern | File |
|---------|------|
| Ingestion | `src/Agent42.Backend/DocumentIngestionService.cs` |
| Extract / chunk | `src/Agent42.Agents/DocumentTextExtractor.cs`, `DocumentChunker.cs` |
| Retrieval provider | `src/Agent42.Agents/AgentGrain.cs` (`BuildDocumentSearchProvider`, `SearchDocumentContextAsync`) |
| Vector store | `src/Agent42.Backend/CosmosDocumentIndex.cs`, `src/Agent42.Agents/InMemoryDocumentIndex.cs` |
| Wiring | `src/Agent42.Backend/Program.cs` |
