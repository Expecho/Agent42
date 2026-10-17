using Agent42.Agents;

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
    // --- ReadChunksAsync / DeleteFileAsync (ADR 0098, AC1) ---

    private static EmbeddedChunk Indexed(string fileName, int chunkIndex, string text)
        => new(new DocumentChunk(fileName, null, chunkIndex, text), new float[] { 1, 0, 0 });

    [Fact]
    public async Task ReadChunks_ReturnsChunkOrder_AfterAShuffledUpsert()
    {
        const string owner = "index-read-order";
        await _index.UpsertChunksAsync(owner,
            [Indexed("a.txt", 2, "two"), Indexed("a.txt", 0, "zero"), Indexed("b.txt", 0, "other"), Indexed("a.txt", 1, "one")]);

        var range = await _index.ReadChunksAsync(owner, "a.txt", 0, 10);

        Assert.Equal(3, range.TotalChunks);
        Assert.Equal(["zero", "one", "two"], range.Chunks.Select(c => c.Text));
    }

    [Fact]
    public async Task ReadChunks_SlicesTheRange_AndReportsTheTotal()
    {
        const string owner = "index-read-slice";
        await _index.UpsertChunksAsync(owner, [.. Enumerable.Range(0, 25).Select(i => Indexed("a.txt", i, $"c{i}"))]);

        var range = await _index.ReadChunksAsync(owner, "a.txt", 20, 10);

        Assert.Equal(25, range.TotalChunks);
        Assert.Equal(["c20", "c21", "c22", "c23", "c24"], range.Chunks.Select(c => c.Text));
    }

    [Fact]
    public async Task ReadChunks_IsScopedByOwner()
    {
        await _index.UpsertChunksAsync("index-read-owner-a", [Indexed("a.txt", 0, "alice secret")]);

        var other = await _index.ReadChunksAsync("index-read-owner-b", "a.txt", 0, 10);

        Assert.Equal(0, other.TotalChunks);
        Assert.Empty(other.Chunks);
    }

    // Legacy data: a re-upload before re-ingest replaced its chunks left two copies with identical indices.
    [Fact]
    public async Task ReadChunks_DuplicateChunkIndex_IsReadOnce_FirstWins()
    {
        const string owner = "index-read-dupes";
        await _index.UpsertChunksAsync(owner, [Indexed("a.txt", 0, "first"), Indexed("a.txt", 1, "first-1")]);
        await _index.UpsertChunksAsync(owner, [Indexed("a.txt", 0, "second"), Indexed("a.txt", 1, "second-1")]);

        var range = await _index.ReadChunksAsync(owner, "a.txt", 0, 10);

        Assert.Equal(2, range.TotalChunks);
        Assert.Equal(["first", "first-1"], range.Chunks.Select(c => c.Text));
    }

    [Fact]
    public async Task ReadChunks_UnknownFile_HasZeroTotal()
    {
        const string owner = "index-read-unknown";
        await _index.UpsertChunksAsync(owner, [Indexed("a.txt", 0, "x")]);

        var range = await _index.ReadChunksAsync(owner, "missing.txt", 0, 10);

        Assert.Equal(0, range.TotalChunks);
        Assert.Empty(range.Chunks);
    }

    [Fact]
    public async Task DeleteFile_RemovesOnlyThatFile()
    {
        const string owner = "index-delete-file";
        await _index.UpsertChunksAsync(owner, [Indexed("a.txt", 0, "gone"), Indexed("b.txt", 0, "kept")]);

        await _index.DeleteFileAsync(owner, "a.txt");

        Assert.Equal(0, (await _index.ReadChunksAsync(owner, "a.txt", 0, 10)).TotalChunks);
        Assert.Equal("kept", Assert.Single((await _index.ReadChunksAsync(owner, "b.txt", 0, 10)).Chunks).Text);
    }
}
