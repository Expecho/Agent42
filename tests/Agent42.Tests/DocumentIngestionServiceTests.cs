using System.Text;
using Agent42.Backend;
using Agent42.Agents;

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

        var count = await Service(index, EmbeddingProvider()).IngestAsync(
            OwnerKey, "guide.txt", DocumentMediaTypes.PlainText,
            TextDocument("Magrathea is a legendary planet where dolphins once left a message."));

        Assert.True(count > 0);
        Assert.NotEmpty(index.Upserted);
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
