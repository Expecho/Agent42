using Agent42.Agents;
using Npgsql;

namespace Agent42.Backend;

/// <summary>
/// <see cref="IChatSearchIndex"/> on Postgres (<c>Storage:Provider = local</c>, ADR 0127), the counterpart of
/// <see cref="CosmosChatSearchIndex"/>. One row per turn in <c>agent42_chat_messages</c>, keyed
/// <c>(user_id, conversation_id, ordinal)</c> so re-indexing a turn overwrites it. Every statement filters by
/// <c>user_id</c> and every value is a parameter.
///
/// <para><b>Sidebar search</b> is a case-insensitive substring match, done with <c>position(lower(q) in lower(text))</c>
/// rather than <c>LIKE</c>, so a query containing <c>%</c> or <c>_</c> needs no escaping. <b>Ranked search</b> (the model's
/// <c>search_past_chats</c>) is <c>ts_rank_cd</c> over a <c>simple</c>-configuration <c>tsvector</c> (no stemming, no
/// language assumption). It ranks differently from Cosmos BM25, which is accepted: the contract is "relevant", not a score.
/// Each caller term goes through <c>plainto_tsquery</c> as a PARAMETER, never into the query text, so tsquery operators in a
/// term (<c>!</c>, <c>|</c>, <c>:*</c>, quotes) are plain text and cannot change the query.</para>
/// </summary>
public sealed class PostgresChatSearchIndex(NpgsqlDataSource dataSource) : IChatSearchIndex
{
    public async Task IndexTurnsAsync(
        string userId, string conversationId, IReadOnlyList<ChatSearchTurn> turns, CancellationToken cancellationToken = default)
    {
        if (turns.Count == 0)
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var turn in turns)
        {
            await using var command = new NpgsqlCommand(
                "INSERT INTO agent42_chat_messages (user_id, conversation_id, ordinal, role, text) " +
                "VALUES (@user, @conversation, @ordinal, @role, @text) " +
                "ON CONFLICT (user_id, conversation_id, ordinal) DO UPDATE SET role = EXCLUDED.role, text = EXCLUDED.text",
                connection, transaction);
            command.Parameters.AddWithValue("user", userId);
            command.Parameters.AddWithValue("conversation", conversationId);
            command.Parameters.AddWithValue("ordinal", turn.Ordinal);
            command.Parameters.AddWithValue("role", turn.Role);
            command.Parameters.AddWithValue("text", ChatSearchLimits.CapText(turn.Text));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChatSearchHit>> SearchAsync(
        string userId, string query, int maxResults, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
        {
            return [];
        }

        await using var command = dataSource.CreateCommand(
            "SELECT conversation_id, role, text, ordinal FROM agent42_chat_messages " +
            "WHERE user_id = @user AND position(lower(@q) in lower(text)) > 0 " +
            "ORDER BY conversation_id, ordinal LIMIT @take");
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("q", query);
        command.Parameters.AddWithValue("take", Math.Min(maxResults, ChatSearchLimits.MaxSearchResults));
        return await ReadHitsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<ChatSearchHit>> SearchRankedAsync(
        string userId, IReadOnlyList<string> terms, int maxResults, CancellationToken cancellationToken = default)
    {
        if (terms.Count == 0 || maxResults <= 0)
        {
            return [];
        }

        // One plainto_tsquery per term, OR-ed with `||`. Only the loop index reaches the SQL text; the terms are parameters.
        var query = string.Join(" || ", terms.Select((_, i) => $"plainto_tsquery('simple', @t{i})"));
        await using var command = dataSource.CreateCommand(
            "SELECT conversation_id, role, text, ordinal FROM agent42_chat_messages, " +
            $"LATERAL (SELECT {query} AS q) AS search " +
            "WHERE user_id = @user AND to_tsvector('simple', text) @@ search.q " +
            "ORDER BY ts_rank_cd(to_tsvector('simple', text), search.q) DESC, conversation_id, ordinal LIMIT @take");
        command.Parameters.AddWithValue("user", userId);
        for (var i = 0; i < terms.Count; i++)
        {
            command.Parameters.AddWithValue($"t{i}", terms[i]);
        }

        command.Parameters.AddWithValue("take", Math.Min(maxResults, ChatSearchLimits.MaxSearchResults));
        return await ReadHitsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<ChatSearchTurn>> GetTurnsAsync(
        string userId, string conversationId, int fromOrdinal, int toOrdinal, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT ordinal, role, text FROM agent42_chat_messages " +
            "WHERE user_id = @user AND conversation_id = @conversation AND ordinal >= @from AND ordinal <= @to ORDER BY ordinal");
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("conversation", conversationId);
        command.Parameters.AddWithValue("from", fromOrdinal);
        command.Parameters.AddWithValue("to", toOrdinal);

        var turns = new List<ChatSearchTurn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            turns.Add(new ChatSearchTurn(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        return turns;
    }

    public async Task DeleteConversationAsync(string userId, string conversationId, CancellationToken cancellationToken = default)
    {
        // Filtered by conversation: the table is keyed by user, so a user-wide delete would drop every other conversation.
        await using var command = dataSource.CreateCommand(
            "DELETE FROM agent42_chat_messages WHERE user_id = @user AND conversation_id = @conversation");
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("conversation", conversationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<ChatSearchHit>> ReadHitsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var hits = new List<ChatSearchHit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hits.Add(new ChatSearchHit(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        return hits;
    }
}
