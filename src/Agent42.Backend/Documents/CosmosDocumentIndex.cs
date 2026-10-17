using Agent42.Agents;
using Microsoft.Azure.Cosmos;

namespace Agent42.Backend;

/// <summary>
/// Production <see cref="IDocumentIndex"/> backed by Azure Cosmos DB for NoSQL vector search
/// (ADR 0007). Each chunk is one item partitioned by <c>ownerKey</c> (the
/// <c>{userId}/{conversationId}</c> grain key), so every query and delete is single-partition and
/// owner-scoped by construction. Retrieval is a <c>VectorDistance</c> top-k query over a
/// <c>quantizedFlat</c> index (3072-dim <c>text-embedding-3-large</c> exceeds the 505-dim cap of a
/// <c>flat</c> index; at &lt;1000 vectors <c>quantizedFlat</c> falls back to a full scan, which is
/// correct and cheap at this scale).
///
/// The <c>doc-chunks</c> container is pre-created in Bicep with its vector embedding policy + index:
/// the account has <c>disableLocalAuth: true</c> and Cosmos AAD data-plane RBAC cannot create
/// containers, so this class NEVER creates on use (same wall as the Orleans providers). This is a
/// prod-only path — the offline harness uses <see cref="InMemoryDocumentIndex"/> — so it has no unit
/// test; it is covered by the Step 1 spike + a prod smoke (AC7), like the blob SAS behavior in
/// ADR 0004.
/// </summary>
public sealed class CosmosDocumentIndex(
    CosmosClient cosmosClient, string databaseName, string containerName, int dimensions)
    : IDocumentIndex
{
    private readonly Container _container = cosmosClient.GetContainer(databaseName, containerName);

    // The container's vector policy dimension (Bicep-fixed, default 3072 for text-embedding-3-large). Ingest
    // rejects a user-chosen embedding model whose vectors don't match — Cosmos would otherwise reject or
    // misindex them. See ADR 0007's "Cosmos vector search is prod-only" constraint.
    public int? ExpectedDimensions => dimensions;

    public async Task UpsertChunksAsync(
        string ownerKey, IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
    {
        var partition = new PartitionKey(ownerKey);
        foreach (var embedded in chunks)
        {
            var chunk = embedded.Chunk;
            var document = new ChunkDocument(
                Guid.NewGuid().ToString("n"), ownerKey, chunk.FileName, chunk.Page, chunk.ChunkIndex,
                chunk.Text, embedded.Embedding.ToArray());
            await _container.UpsertItemAsync(document, partition, cancellationToken: cancellationToken);
        }
    }

    public async Task<IReadOnlyList<DocumentChunk>> SearchAsync(
        string ownerKey, ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken cancellationToken = default)
    {
        if (topK <= 0)
        {
            return [];
        }

        // topK is an application-controlled int (not user input), so interpolating it into TOP is
        // injection-safe. ORDER BY VectorDistance returns items most-similar-first.
        var query = new QueryDefinition(
                $"SELECT TOP {topK} c.fileName, c.page, c.chunkIndex, c.text " +
                "FROM c ORDER BY VectorDistance(c.embedding, @vector)")
            .WithParameter("@vector", queryEmbedding.ToArray());

        var options = new QueryRequestOptions { PartitionKey = new PartitionKey(ownerKey) };
        using var iterator = _container.GetItemQueryIterator<ChunkHit>(query, requestOptions: options);

        var results = new List<DocumentChunk>();
        while (iterator.HasMoreResults)
        {
            foreach (var hit in await iterator.ReadNextAsync(cancellationToken))
            {
                results.Add(new DocumentChunk(hit.fileName, hit.page, hit.chunkIndex, hit.text));
            }
        }

        return results;
    }

    public async Task DeleteAsync(string ownerKey, CancellationToken cancellationToken = default)
    {
        var partition = new PartitionKey(ownerKey);
        var query = new QueryDefinition("SELECT c.id FROM c");
        var options = new QueryRequestOptions { PartitionKey = partition };
        using var iterator = _container.GetItemQueryIterator<IdOnly>(query, requestOptions: options);

        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync(cancellationToken))
            {
                await _container.DeleteItemAsync<ChunkDocument>(item.id, partition, cancellationToken: cancellationToken);
            }
        }
    }

    public async Task<DocumentChunkRange> ReadChunksAsync(
        string ownerKey, string fileName, int startChunkIndex, int count, CancellationToken cancellationToken = default)
    {
        var options = new QueryRequestOptions { PartitionKey = new PartitionKey(ownerKey) };

        var maxQuery = new QueryDefinition(MaxChunkIndexSql).WithParameter("@fileName", fileName);
        var maxValues = new List<int?>();
        using (var maxIterator = _container.GetItemQueryIterator<int?>(maxQuery, requestOptions: options))
        {
            while (maxIterator.HasMoreResults)
            {
                maxValues.AddRange(await maxIterator.ReadNextAsync(cancellationToken));
            }
        }

        var total = TotalChunksFromMax(maxValues);
        if (total == 0 || count <= 0)
        {
            return new DocumentChunkRange(total, []);
        }

        var rangeQuery = new QueryDefinition(ReadRangeSql)
            .WithParameter("@fileName", fileName)
            .WithParameter("@start", startChunkIndex)
            .WithParameter("@end", startChunkIndex + count);
        using var iterator = _container.GetItemQueryIterator<ChunkHit>(rangeQuery, requestOptions: options);

        var chunks = new List<DocumentChunk>();
        while (iterator.HasMoreResults)
        {
            foreach (var hit in await iterator.ReadNextAsync(cancellationToken))
            {
                chunks.Add(new DocumentChunk(hit.fileName, hit.page, hit.chunkIndex, hit.text));
            }
        }

        // Legacy duplicates (a re-upload before re-ingest replaced) share indices; first wins (ADR 0098).
        return new DocumentChunkRange(total, [.. chunks.DistinctBy(c => c.ChunkIndex)]);
    }

    public async Task DeleteFileAsync(string ownerKey, string fileName, CancellationToken cancellationToken = default)
    {
        var partition = new PartitionKey(ownerKey);
        var query = new QueryDefinition(DeleteFileScanSql).WithParameter("@fileName", fileName);
        var options = new QueryRequestOptions { PartitionKey = partition };
        using var iterator = _container.GetItemQueryIterator<IdOnly>(query, requestOptions: options);

        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync(cancellationToken))
            {
                await _container.DeleteItemAsync<ChunkDocument>(item.id, partition, cancellationToken: cancellationToken);
            }
        }
    }

    /// <summary>
    /// The file's total chunk count, read as <c>MAX(chunkIndex) + 1</c>: indices are contiguous from 0 per
    /// ingest, and <c>COUNT(1)</c> would over-count legacy duplicate copies. Every query here is
    /// single-partition (the caller sets the <c>ownerKey</c> partition) and takes <c>fileName</c> as a
    /// parameter, never interpolated.
    ///
    /// <para>Public rather than internal because this repo has no <c>InternalsVisibleTo</c> — the same
    /// reason as <c>CosmosChatSearchIndex.BuildSearchSql</c>.</para>
    /// </summary>
    public const string MaxChunkIndexSql = "SELECT VALUE MAX(c.chunkIndex) FROM c WHERE c.fileName = @fileName";

    /// <summary>
    /// One part's chunk range. The ORDER BY names a <b>single</b> property on purpose: that is served by the
    /// default range index, while a multi-property ORDER BY needs a declared composite index or fails with a
    /// 400 in prod only (the ADR 0061 <c>chat-messages</c> bug). <c>doc-chunks</c> declares none.
    /// </summary>
    public const string ReadRangeSql =
        "SELECT c.fileName, c.page, c.chunkIndex, c.text FROM c "
        + "WHERE c.fileName = @fileName AND c.chunkIndex >= @start AND c.chunkIndex < @end "
        + "ORDER BY c.chunkIndex";

    /// <summary>The per-file delete scan behind re-ingest-replaces (ADR 0098).</summary>
    public const string DeleteFileScanSql = "SELECT c.id FROM c WHERE c.fileName = @fileName";

    /// <summary>
    /// Maps the <see cref="MaxChunkIndexSql"/> result to a total. <b>On no matching rows Cosmos returns an
    /// empty result set (or an undefined value), not 0</b>, so both map to 0 — "no readable text".
    /// </summary>
    public static int TotalChunksFromMax(IEnumerable<int?> maxValues)
        => maxValues.FirstOrDefault() is int max ? max + 1 : 0;

    // Item shape. Lowercase 'id' is the Cosmos item id; 'ownerKey' is the partition key path
    // (/ownerKey); 'embedding' is the vector path (/embedding) declared in the Bicep vector policy.
    private sealed record ChunkDocument(
        string id, string ownerKey, string fileName, int? page, int chunkIndex, string text, float[] embedding);

    private sealed record ChunkHit(string fileName, int? page, int chunkIndex, string text);

    private sealed record IdOnly(string id);
}
