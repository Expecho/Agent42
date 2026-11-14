using Agent42.Agents;
using Agent42.Backend;
using Npgsql;

namespace Agent42.Tests;

/// <summary>
/// AC10: the Postgres document index behaves like <see cref="InMemoryDocumentIndex"/> (the cases of
/// <c>DocumentIndexTests</c>, re-run here against a real pgvector): owner-scoped, most-similar-first, ordered sequential
/// reads with first-copy-wins, per-file delete, and a dimension contract the ingest check can rely on.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresDocumentIndexTests(PostgresFixture postgres)
{
    private const int Dimensions = 3;

    private async Task<PostgresDocumentIndex> IndexAsync()
    {
        var builder = new NpgsqlDataSourceBuilder(await postgres.CreateDatabaseAsync());
        builder.UseVector();
        var source = builder.Build();
        await PostgresSchema.EnsureAsync(source, Dimensions);
        return new PostgresDocumentIndex(source, Dimensions);
    }

    private static EmbeddedChunk Chunk(string text, params float[] embedding)
        => new(new DocumentChunk("doc.txt", null, 0, text), embedding);

    private static EmbeddedChunk Indexed(string fileName, int chunkIndex, string text)
        => new(new DocumentChunk(fileName, null, chunkIndex, text), new float[] { 1, 0, 0 });

    [DockerFact]
    public async Task ExpectedDimensions_IsTheColumnSize_SoIngestCanRejectAMismatchBeforeUpsert()
    {
        var index = await IndexAsync();
        Assert.Equal(Dimensions, index.ExpectedDimensions);

        // The database itself refuses a vector of another size, which is why the pre-upsert check exists (ADR 0030).
        await Assert.ThrowsAnyAsync<PostgresException>(() => index.UpsertChunksAsync("o", [Chunk("wrong size", 1, 0)]));
    }

    [DockerFact]
    public async Task Search_RanksByCosineSimilarity_MostSimilarFirst()
    {
        var index = await IndexAsync();
        await index.UpsertChunksAsync("owner",
        [
            Chunk("orthogonal", 0, 1, 0),
            Chunk("exact match", 1, 0, 0),
            Chunk("near match", 0.9f, 0.1f, 0),
        ]);

        var hits = await index.SearchAsync("owner", new float[] { 1, 0, 0 }, topK: 2);

        Assert.Equal(["exact match", "near match"], hits.Select(h => h.Text));
    }

    [DockerFact]
    public async Task Search_IsScopedByOwner_OneConversationCannotReadAnothers()
    {
        var index = await IndexAsync();
        await index.UpsertChunksAsync("owner-a", [Chunk("alice secret", 1, 0, 0)]);

        Assert.Empty(await index.SearchAsync("owner-b", new float[] { 1, 0, 0 }, topK: 5));
        Assert.Equal("alice secret", Assert.Single(await index.SearchAsync("owner-a", new float[] { 1, 0, 0 }, topK: 5)).Text);
    }

    [DockerFact]
    public async Task Delete_RemovesOnlyThatOwnersChunks()
    {
        var index = await IndexAsync();
        await index.UpsertChunksAsync("del-a", [Chunk("gone", 1, 0, 0)]);
        await index.UpsertChunksAsync("del-b", [Chunk("kept", 1, 0, 0)]);

        await index.DeleteAsync("del-a");

        Assert.Empty(await index.SearchAsync("del-a", new float[] { 1, 0, 0 }, topK: 5));
        Assert.Single(await index.SearchAsync("del-b", new float[] { 1, 0, 0 }, topK: 5));
    }

    [DockerFact]
    public async Task Search_EmptyWhenNothingIndexed_AndTopKZeroReturnsEmpty()
    {
        var index = await IndexAsync();
        Assert.Empty(await index.SearchAsync("nobody", new float[] { 1, 0, 0 }, topK: 5));

        await index.UpsertChunksAsync("owner", [Chunk("something", 1, 0, 0)]);
        Assert.Empty(await index.SearchAsync("owner", new float[] { 1, 0, 0 }, topK: 0));
    }

    [DockerFact]
    public async Task Pages_AndNullPage_RoundTrip()
    {
        var index = await IndexAsync();
        await index.UpsertChunksAsync("owner",
        [
            new EmbeddedChunk(new DocumentChunk("a.pdf", 7, 0, "on page seven"), new float[] { 1, 0, 0 }),
            new EmbeddedChunk(new DocumentChunk("a.pdf", null, 1, "no page"), new float[] { 1, 0, 0 }),
        ]);

        var range = await index.ReadChunksAsync("owner", "a.pdf", 0, 10);

        Assert.Equal(7, range.Chunks[0].Page);
        Assert.Null(range.Chunks[1].Page);
    }

    [DockerFact]
    public async Task ReadChunks_ReturnsChunkOrder_AfterAShuffledUpsert()
    {
        var index = await IndexAsync();
        await index.UpsertChunksAsync("owner",
            [Indexed("a.txt", 2, "two"), Indexed("a.txt", 0, "zero"), Indexed("b.txt", 0, "other"), Indexed("a.txt", 1, "one")]);

        var range = await index.ReadChunksAsync("owner", "a.txt", 0, 10);

        Assert.Equal(3, range.TotalChunks);
        Assert.Equal(["zero", "one", "two"], range.Chunks.Select(c => c.Text));
    }

    [DockerFact]
    public async Task ReadChunks_SlicesTheRange_AndReportsTheTotal()
    {
        var index = await IndexAsync();
        await index.UpsertChunksAsync("owner", [.. Enumerable.Range(0, 25).Select(i => Indexed("a.txt", i, $"c{i}"))]);

        var range = await index.ReadChunksAsync("owner", "a.txt", 20, 10);

        Assert.Equal(25, range.TotalChunks);
        Assert.Equal(["c20", "c21", "c22", "c23", "c24"], range.Chunks.Select(c => c.Text));
    }

    [DockerFact]
    public async Task ReadChunks_IsScopedByOwner_AndAnUnknownFileHasZeroTotal()
    {
        var index = await IndexAsync();
        await index.UpsertChunksAsync("owner-a", [Indexed("a.txt", 0, "alice secret")]);

        var other = await index.ReadChunksAsync("owner-b", "a.txt", 0, 10);
        Assert.Equal(0, other.TotalChunks);
        Assert.Empty(other.Chunks);

        var unknown = await index.ReadChunksAsync("owner-a", "missing.txt", 0, 10);
        Assert.Equal(0, unknown.TotalChunks);
        Assert.Empty(unknown.Chunks);
    }

    [DockerFact]
    public async Task ReadChunks_DuplicateChunkIndex_IsReadOnce_FirstWins()
    {
        var index = await IndexAsync();
        await index.UpsertChunksAsync("owner", [Indexed("a.txt", 0, "first"), Indexed("a.txt", 1, "first-1")]);
        await index.UpsertChunksAsync("owner", [Indexed("a.txt", 0, "second"), Indexed("a.txt", 1, "second-1")]);

        var range = await index.ReadChunksAsync("owner", "a.txt", 0, 10);

        Assert.Equal(2, range.TotalChunks);
        Assert.Equal(["first", "first-1"], range.Chunks.Select(c => c.Text));
    }

    [DockerFact]
    public async Task DeleteFile_RemovesOnlyThatFile()
    {
        var index = await IndexAsync();
        await index.UpsertChunksAsync("owner", [Indexed("a.txt", 0, "gone"), Indexed("b.txt", 0, "kept")]);

        await index.DeleteFileAsync("owner", "a.txt");

        Assert.Equal(0, (await index.ReadChunksAsync("owner", "a.txt", 0, 10)).TotalChunks);
        Assert.Equal("kept", Assert.Single((await index.ReadChunksAsync("owner", "b.txt", 0, 10)).Chunks).Text);
    }

    [DockerFact]
    public async Task HostileText_AndNames_AreOnlyEverData()
    {
        var index = await IndexAsync();
        const string hostile = "x'); DROP TABLE agent42_doc_chunks; --";
        await index.UpsertChunksAsync(hostile, [new EmbeddedChunk(new DocumentChunk(hostile, null, 0, hostile), new float[] { 1, 0, 0 })]);

        var hit = Assert.Single(await index.SearchAsync(hostile, new float[] { 1, 0, 0 }, topK: 5));
        Assert.Equal(hostile, hit.Text);
        await index.DeleteFileAsync(hostile, hostile);
        Assert.Empty(await index.SearchAsync(hostile, new float[] { 1, 0, 0 }, topK: 5));
    }
}
