using System.Text;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// AC5: <see cref="DocumentIngestionService"/> resolves the OWNER's embedding generator (userId derived from
/// the ownerKey grain key, never an ambient principal — the /api/agent-run path has none), rejects a model
/// whose vectors don't fit the store's fixed dimension, and rejects ingest entirely when no embedding model
/// is configured. The offline deterministic generator is 128-dim.
/// </summary>
public sealed class DocumentIngestionServiceTests
{
    private const string OwnerKey = "alice/conversation-1";

    private static byte[] TextDocument(string text) => Encoding.UTF8.GetBytes(text);

    private static DocumentIngestionService Service(IDocumentIndex index, IUserChatClientFactoryProvider provider) =>
        new(new DocumentTextExtractor(), new DocumentChunker(), provider, index);

    private static IUserChatClientFactoryProvider EmbeddingProvider() =>
        new SingleUserChatClientFactoryProvider(new SingleChatClientFactory(
            NoModelChatClient.Instance,
            embeddingGenerator: new DeterministicEmbeddingGenerator(),
            defaultEmbeddingModelId: "offline-embed"));

    // Happy path: a store whose expected dimension matches the generator's (128) ingests and upserts.
    [Fact]
    public async Task Ingest_MatchingDimension_UpsertsChunks()
    {
        var index = new FakeIndex(expectedDimensions: DeterministicEmbeddingGenerator.Dimensions);

        var result = await Service(index, EmbeddingProvider()).IngestAsync(
            OwnerKey, "guide.txt", DocumentMediaTypes.PlainText,
            TextDocument("Magrathea is a legendary planet where dolphins once left a message."));

        Assert.True(result.ChunkCount > 0);
        Assert.False(result.Truncated);
        Assert.NotEmpty(index.Upserted);

        // The chunks come back so the read_web_document tool can rank them IN PROCESS (ADR 0066)
        // rather than reading a top-K back out of the prod-only Cosmos vector index.
        Assert.Equal(index.Upserted.Count, result.Chunks.Count);
    }

    // AC5: a store whose fixed dimension (3072, the prod container) doesn't match the model's vectors rejects
    // the ingest with a clear message BEFORE any upsert — not an opaque Cosmos error.
    [Fact]
    public async Task Ingest_DimensionMismatch_ThrowsClearError_AndDoesNotUpsert()
    {
        var index = new FakeIndex(expectedDimensions: 3072); // deterministic generator is 128-dim → mismatch

        var ex = await Assert.ThrowsAsync<EmbeddingDimensionMismatchException>(() =>
            Service(index, EmbeddingProvider()).IngestAsync(
                OwnerKey, "guide.txt", DocumentMediaTypes.PlainText, TextDocument("some searchable text here")));

        Assert.Equal(3072, ex.Expected);
        Assert.Equal(DeterministicEmbeddingGenerator.Dimensions, ex.Actual);
        Assert.Empty(index.Upserted);
    }

    // AC5: no embedding model configured (DefaultEmbeddingModelId == "") rejects ingest with the actionable
    // "configure an embedding model" error — the server-side defense the composer gate backs up.
    [Fact]
    public async Task Ingest_NoEmbeddingModel_ThrowsNotConfigured()
    {
        var noEmbeddingProvider = new SingleUserChatClientFactoryProvider(
            new SingleChatClientFactory(NoModelChatClient.Instance)); // no embedding generator → id ""
        var index = new FakeIndex(expectedDimensions: null);

        await Assert.ThrowsAsync<EmbeddingModelNotConfiguredException>(() =>
            Service(index, noEmbeddingProvider).IngestAsync(
                OwnerKey, "guide.txt", DocumentMediaTypes.PlainText, TextDocument("text")));

        Assert.Empty(index.Upserted);
    }

    // HIGH 1: the userId is taken from the ownerKey ({userId}/{conversationId}), never GetUserId — so the
    // /api/agent-run path (which has no Easy-Auth principal) resolves the right user's embedding model.
    [Fact]
    public async Task Ingest_ResolvesEmbeddingGenerator_ForOwnerKeyUser()
    {
        var recording = new RecordingFactoryProvider(new SingleChatClientFactory(
            NoModelChatClient.Instance,
            embeddingGenerator: new DeterministicEmbeddingGenerator(),
            defaultEmbeddingModelId: "offline-embed"));

        await Service(new FakeIndex(DeterministicEmbeddingGenerator.Dimensions), recording).IngestAsync(
            "bob/conversation-9", "guide.txt", DocumentMediaTypes.PlainText, TextDocument("text content"));

        Assert.Equal("bob", recording.LastUserId);
    }

    // AC20 (ADR 0066): maxChunks bounds the embedding work — every chunk is a round trip on a
    // NON-REENTRANT grain, and a model-chosen URL's byte size says nothing about how much extracted text
    // it holds. The tail is dropped and the truncation is REPORTED, so the model knows it is reasoning
    // over part of the document rather than being handed a silent lie.
    [Fact]
    public async Task Ingest_RespectsMaxChunksAndReportsTruncation()
    {
        var index = new FakeIndex(DeterministicEmbeddingGenerator.Dimensions);
        var service = Service(index, EmbeddingProvider());

        // Long enough to chunk well past the cap the test applies.
        var long_ = TextDocument(string.Join("\n\n", Enumerable.Range(0, 400).Select(
            i => $"Paragraph {i}. " + string.Join(" ", Enumerable.Repeat("magrathea", 60)))));

        var uncapped = await service.IngestAsync(OwnerKey, "long.txt", DocumentMediaTypes.PlainText, long_);
        Assert.True(uncapped.ChunkCount > 3, "the fixture must chunk past the cap, or this proves nothing");
        Assert.False(uncapped.Truncated);

        index.Upserted.Clear();
        var capped = await service.IngestAsync(
            OwnerKey, "long.txt", DocumentMediaTypes.PlainText, long_, maxChunks: 3);

        Assert.Equal(3, capped.ChunkCount);
        Assert.True(capped.Truncated);

        // The cap bounds the EMBEDDING and the upsert, not just the returned list — that is the whole
        // point. Capping only the return would leave the grain stall unbounded.
        Assert.Equal(3, index.Upserted.Count);
    }

    // A document that yields no extractable text (a scanned, image-only PDF) is not an error: it is an
    // empty ingest the caller surfaces to the user.
    [Fact]
    public async Task Ingest_NoExtractableText_ReturnsEmpty()
    {
        var index = new FakeIndex(DeterministicEmbeddingGenerator.Dimensions);

        var result = await Service(index, EmbeddingProvider()).IngestAsync(
            OwnerKey, "blank.txt", DocumentMediaTypes.PlainText, TextDocument("   \n\n  "));

        Assert.Equal(0, result.ChunkCount);
        Assert.False(result.Truncated);
        Assert.Empty(index.Upserted);
    }

    private sealed class FakeIndex(int? expectedDimensions) : IDocumentIndex
    {
        public int? ExpectedDimensions => expectedDimensions;

        public List<EmbeddedChunk> Upserted { get; } = [];

        public Task UpsertChunksAsync(
            string ownerKey, IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
        {
            Upserted.AddRange(chunks);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DocumentChunk>> SearchAsync(
            string ownerKey, ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DocumentChunk>>([]);

        public Task DeleteAsync(string ownerKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingFactoryProvider(IChatClientFactory factory) : IUserChatClientFactoryProvider
    {
        public string? LastUserId { get; private set; }

        public Task<IChatClientFactory> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
        {
            LastUserId = userId;
            return Task.FromResult(factory);
        }
    }
}
