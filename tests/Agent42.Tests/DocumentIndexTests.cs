using Agent42.Agents.Documents;

namespace Agent42.Tests;

/// <summary>
/// AC4: retrieval returns the most-similar chunks (cosine ranking) and is scoped to the
/// conversation — one owner can never read or delete another's chunks. The in-memory index is the
/// offline/test stand-in for Cosmos vector search; its ranking must match. Owner keys are unique
/// per test because the backing store is process-global.
/// </summary>
public sealed class DocumentIndexTests
{
    private readonly InMemoryDocumentIndex _index = new();

    private static EmbeddedChunk Chunk(string text, params float[] embedding)
        => new(new DocumentChunk("doc.txt", null, 0, text), embedding);

    [Fact]
    public async Task Search_RanksByCosineSimilarity_MostSimilarFirst()
    {
        const string owner = "index-rank-owner";
        await _index.UpsertChunksAsync(owner,
        [
            Chunk("orthogonal", 0, 1, 0),
            Chunk("exact match", 1, 0, 0),
            Chunk("near match", 0.9f, 0.1f, 0),
        ]);

        var hits = await _index.SearchAsync(owner, new float[] { 1, 0, 0 }, topK: 2);

        Assert.Equal(2, hits.Count);
        Assert.Equal("exact match", hits[0].Text);
        Assert.Equal("near match", hits[1].Text);
    }

    [Fact]
    public async Task Search_IsScopedByOwner_OneConversationCannotReadAnothers()
    {
        await _index.UpsertChunksAsync("index-owner-a", [Chunk("alice secret", 1, 0, 0)]);

        var otherOwnerHits = await _index.SearchAsync("index-owner-b", new float[] { 1, 0, 0 }, topK: 5);
        Assert.Empty(otherOwnerHits);

        var ownHits = await _index.SearchAsync("index-owner-a", new float[] { 1, 0, 0 }, topK: 5);
        Assert.Equal("alice secret", Assert.Single(ownHits).Text);
    }

    [Fact]
    public async Task Delete_RemovesOnlyThatOwnersChunks()
    {
        await _index.UpsertChunksAsync("index-del-a", [Chunk("gone", 1, 0, 0)]);
        await _index.UpsertChunksAsync("index-del-b", [Chunk("kept", 1, 0, 0)]);

        await _index.DeleteAsync("index-del-a");

        Assert.Empty(await _index.SearchAsync("index-del-a", new float[] { 1, 0, 0 }, topK: 5));
        Assert.Single(await _index.SearchAsync("index-del-b", new float[] { 1, 0, 0 }, topK: 5));
    }

    [Fact]
    public async Task Search_EmptyWhenNoDocumentsIndexed()
        => Assert.Empty(await _index.SearchAsync("index-empty-owner", new float[] { 1, 0, 0 }, topK: 5));

    [Fact]
    public async Task Search_TopKZero_ReturnsEmpty()
    {
        const string owner = "index-topk-owner";
        await _index.UpsertChunksAsync(owner, [Chunk("something", 1, 0, 0)]);

        Assert.Empty(await _index.SearchAsync(owner, new float[] { 1, 0, 0 }, topK: 0));
    }
}
