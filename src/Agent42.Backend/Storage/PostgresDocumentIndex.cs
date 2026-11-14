using Agent42.Agents;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace Agent42.Backend;

/// <summary>
/// <see cref="IDocumentIndex"/> on Postgres with pgvector (<c>Storage:Provider = local</c>, ADR 0127), the counterpart of
/// <see cref="CosmosDocumentIndex"/>. One row per chunk in <c>agent42_doc_chunks</c>; every statement filters by
/// <c>owner_key</c> and passes every value as a parameter, so one conversation can never read or delete another's chunks.
///
/// <para><b>No ANN index (a known limit).</b> pgvector's HNSW and IVFFlat cannot index a <c>vector</c> above 2000
/// dimensions, and the default is 3072. Search is an exact cosine scan (<c>&lt;=&gt;</c>) over the owner's rows, which is
/// fine for one conversation's documents. <see cref="ExpectedDimensions"/> is the column's size, so ingest rejects a model
/// whose vectors do not fit before anything is written (ADR 0030).</para>
/// </summary>
public sealed class PostgresDocumentIndex(NpgsqlDataSource dataSource, int dimensions) : IDocumentIndex
{
    public int? ExpectedDimensions => dimensions;

    public async Task UpsertChunksAsync(string ownerKey, IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var embedded in chunks)
        {
            var chunk = embedded.Chunk;
            await using var command = new NpgsqlCommand(
                "INSERT INTO agent42_doc_chunks (owner_key, file_name, page, chunk_index, text, embedding) " +
                "VALUES (@owner, @file, @page, @index, @text, @embedding)", connection, transaction);
            command.Parameters.AddWithValue("owner", ownerKey);
            command.Parameters.AddWithValue("file", chunk.FileName);
            command.Parameters.Add(new NpgsqlParameter("page", NpgsqlDbType.Integer) { Value = (object?)chunk.Page ?? DBNull.Value });
            command.Parameters.AddWithValue("index", chunk.ChunkIndex);
            command.Parameters.AddWithValue("text", chunk.Text);
            command.Parameters.AddWithValue("embedding", new Vector(embedded.Embedding));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentChunk>> SearchAsync(
        string ownerKey, ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken cancellationToken = default)
    {
        if (topK <= 0)
        {
            return [];
        }

        await using var command = dataSource.CreateCommand(
            "SELECT file_name, page, chunk_index, text FROM agent42_doc_chunks WHERE owner_key = @owner " +
            "ORDER BY embedding <=> @query, seq LIMIT @topK");
        command.Parameters.AddWithValue("owner", ownerKey);
        command.Parameters.AddWithValue("query", new Vector(queryEmbedding));
        command.Parameters.AddWithValue("topK", topK);
        return await ReadChunksAsync(command, cancellationToken);
    }

    public async Task DeleteAsync(string ownerKey, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM agent42_doc_chunks WHERE owner_key = @owner");
        command.Parameters.AddWithValue("owner", ownerKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DocumentChunkRange> ReadChunksAsync(
        string ownerKey, string fileName, int startChunkIndex, int count, CancellationToken cancellationToken = default)
    {
        // The total is MAX(chunk_index) + 1, not COUNT: indices are contiguous from 0 per ingest, and a count would
        // over-count legacy duplicate copies (the same reading as CosmosDocumentIndex, ADR 0098).
        await using var totalCommand = dataSource.CreateCommand(
            "SELECT MAX(chunk_index) FROM agent42_doc_chunks WHERE owner_key = @owner AND file_name = @file");
        totalCommand.Parameters.AddWithValue("owner", ownerKey);
        totalCommand.Parameters.AddWithValue("file", fileName);
        var max = await totalCommand.ExecuteScalarAsync(cancellationToken);
        var total = max is int value ? value + 1 : 0;
        if (total == 0 || count <= 0)
        {
            return new DocumentChunkRange(total, []);
        }

        // Duplicates of one index (a re-upload before re-ingest replaced) read once, the first inserted winning.
        await using var command = dataSource.CreateCommand(
            "SELECT file_name, page, chunk_index, text FROM " +
            "(SELECT DISTINCT ON (chunk_index) file_name, page, chunk_index, text FROM agent42_doc_chunks " +
            " WHERE owner_key = @owner AND file_name = @file AND chunk_index >= @start AND chunk_index < @end " +
            " ORDER BY chunk_index, seq) first_copy ORDER BY chunk_index");
        command.Parameters.AddWithValue("owner", ownerKey);
        command.Parameters.AddWithValue("file", fileName);
        command.Parameters.AddWithValue("start", startChunkIndex);
        command.Parameters.AddWithValue("end", startChunkIndex + count);
        return new DocumentChunkRange(total, await ReadChunksAsync(command, cancellationToken));
    }

    public async Task DeleteFileAsync(string ownerKey, string fileName, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            "DELETE FROM agent42_doc_chunks WHERE owner_key = @owner AND file_name = @file");
        command.Parameters.AddWithValue("owner", ownerKey);
        command.Parameters.AddWithValue("file", fileName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<DocumentChunk>> ReadChunksAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var results = new List<DocumentChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DocumentChunk(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3)));
        }

        return results;
    }
}
