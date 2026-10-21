using Agent42.Agents;
using Microsoft.Azure.Cosmos;

namespace Agent42.Backend;

/// <summary>
/// Production <see cref="IChatSearchIndex"/> backed by Azure Cosmos DB for NoSQL (ADR 0061). Each
/// message is one item partitioned by <c>/userId</c>, so every query and delete is single-partition
/// and owner-scoped by construction rather than by a <c>WHERE</c> clause a later edit could drop.
///
/// <para>The <c>chat-messages</c> container is pre-created in Bicep with its full-text policy and
/// index: the account has <c>disableLocalAuth: true</c> and Cosmos AAD data-plane RBAC cannot create
/// containers, so this class NEVER creates on use — the same wall as the Orleans providers and
/// <see cref="CosmosDocumentIndex"/>.</para>
///
/// <para><b>The sidebar search queries with <c>CONTAINS</c>, not the full-text functions.</b> The
/// container's full-text policy was declared at creation because it cannot be added afterwards, and ADR 0111
/// uses it: the model's <c>search_past_chats</c> ranks by BM25 (<c>ORDER BY RANK FullTextScore</c>) through
/// <see cref="SearchRankedAsync"/>, a query change with no backfill.</para>
///
/// <para>This is a prod-only path — the offline harness uses <see cref="InMemoryChatSearchIndex"/> —
/// so it has no unit test, exactly as <see cref="CosmosDocumentIndex"/> has none and for the same
/// reason. It is covered by a prod smoke after deploy.</para>
/// </summary>
public sealed class CosmosChatSearchIndex(CosmosClient cosmosClient, string databaseName, string containerName)
    : IChatSearchIndex
{
    private readonly Container _container = cosmosClient.GetContainer(databaseName, containerName);

    public async Task IndexTurnsAsync(
        string userId,
        string conversationId,
        IReadOnlyList<ChatSearchTurn> turns,
        CancellationToken cancellationToken = default)
    {
        var partition = new PartitionKey(userId);
        foreach (var turn in turns)
        {
            var document = new MessageDocument(
                DocumentId(conversationId, turn.Ordinal),
                userId,
                conversationId,
                turn.Role,
                ChatSearchLimits.CapText(turn.Text),
                turn.Ordinal);

            await _container.UpsertItemAsync(document, partition, cancellationToken: cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ChatSearchHit>> SearchAsync(
        string userId,
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
        {
            return [];
        }

        // The query term is a PARAMETER — never interpolated. `take` is an application-clamped int
        // (not user input), so interpolating it into TOP is injection-safe, the same reasoning as
        // CosmosDocumentIndex's topK; Cosmos does not accept a parameter in a TOP clause.
        //
        // The third argument of CONTAINS is REQUIRED for case-insensitivity — the default is
        // case-sensitive. Its counterpart is OrdinalIgnoreCase in the in-memory twin, and because
        // this half is unreachable by test the two can only be kept in step deliberately (ADR 0061).
        var take = Math.Min(maxResults, ChatSearchLimits.MaxSearchResults);
        var definition = new QueryDefinition(BuildSearchSql(take)).WithParameter("@q", query);

        return [.. (await QueryAsync(userId, definition, cancellationToken))
            .Select(hit => new ChatSearchHit(hit.conversationId, hit.role, hit.text, hit.ordinal))];
    }

    public async Task<IReadOnlyList<ChatSearchHit>> SearchRankedAsync(
        string userId,
        IReadOnlyList<string> terms,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (terms.Count == 0 || maxResults <= 0)
        {
            return [];
        }

        // The terms are INLINED as escaped literals, not parameters: FullTextScore takes an array of string
        // literals (ADR 0111). BuildRankedSearchSql JSON-escapes each one, and a test pins that a quote, a
        // backslash or a bracket cannot leave its literal.
        var take = Math.Min(maxResults, ChatSearchLimits.MaxSearchResults);
        var definition = new QueryDefinition(BuildRankedSearchSql(terms, take));

        return [.. (await QueryAsync(userId, definition, cancellationToken))
            .Select(hit => new ChatSearchHit(hit.conversationId, hit.role, hit.text, hit.ordinal))];
    }

    public async Task<IReadOnlyList<ChatSearchTurn>> GetTurnsAsync(
        string userId,
        string conversationId,
        int fromOrdinal,
        int toOrdinal,
        CancellationToken cancellationToken = default)
    {
        var definition = new QueryDefinition(TurnsSql)
            .WithParameter("@conversationId", conversationId)
            .WithParameter("@from", fromOrdinal)
            .WithParameter("@to", toOrdinal);

        return [.. (await QueryAsync(userId, definition, cancellationToken))
            .Select(hit => new ChatSearchTurn(hit.ordinal, hit.role, hit.text))];
    }

    // One single-partition query, drained. The partition is the user, so every read is owner-scoped by
    // construction.
    private async Task<List<MessageHit>> QueryAsync(
        string userId, QueryDefinition definition, CancellationToken cancellationToken)
    {
        var options = new QueryRequestOptions { PartitionKey = new PartitionKey(userId) };
        using var iterator = _container.GetItemQueryIterator<MessageHit>(definition, requestOptions: options);

        var results = new List<MessageHit>();
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(cancellationToken));
        }

        return results;
    }

    public async Task DeleteConversationAsync(
        string userId, string conversationId, CancellationToken cancellationToken = default)
    {
        // NOT a whole-partition sweep. CosmosDocumentIndex can drop its partition because doc-chunks
        // partitions by {userId}/{conversationId}; this container partitions by /userId, so dropping
        // the partition would delete every other conversation the user owns. Filter within it.
        var partition = new PartitionKey(userId);
        var definition = new QueryDefinition(DeleteScanSql).WithParameter("@conversationId", conversationId);

        var options = new QueryRequestOptions { PartitionKey = partition };
        using var iterator = _container.GetItemQueryIterator<IdOnly>(definition, requestOptions: options);

        // Bounded concurrency: this runs inside a NON-REENTRANT grain that
        // DELETE /api/conversations/{id} awaits synchronously, so a long chat must not delete one
        // item at a time — and must not saturate the Cosmos client either.
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            foreach (var batch in page.Chunk(ChatSearchLimits.DeleteConcurrency))
            {
                await Task.WhenAll(batch.Select(item => DeleteOneAsync(item.id, partition, cancellationToken)));
            }
        }
    }

    // Each delete swallows its own failure so ONE bad item cannot abort the sweep.
    //
    // This matters more than the usual best-effort argument. The grain's catch would swallow a
    // faulted Task.WhenAll, PurgeAsync then clears the conversation from agent-state, and the
    // surviving message documents become unreachable — PurgeAsync is the only delete path, and
    // ReindexAsync only ever writes. Deleted conversation text would stay searchable forever. So the
    // "losing a write only costs search results" reasoning does NOT extend to a lost delete, and the
    // blast radius is kept to the single item that failed rather than the rest of the conversation.
    private async Task DeleteOneAsync(string id, PartitionKey partition, CancellationToken cancellationToken)
    {
        try
        {
            await _container.DeleteItemAsync<MessageDocument>(id, partition, cancellationToken: cancellationToken);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone — the desired end state.
        }
    }

    /// <summary>
    /// The search query text. <b>The third argument of <c>CONTAINS</c> is what makes matching
    /// case-insensitive</b> — the default is case-sensitive — and its counterpart is
    /// <c>OrdinalIgnoreCase</c> in <see cref="InMemoryChatSearchIndex"/>. The rest of this class
    /// cannot be reached by a test (no Cosmos offline), so this is split out to make the one rule
    /// that would otherwise diverge silently assertable. <paramref name="take"/> is an
    /// application-clamped int, never user input, so interpolating it into TOP is injection-safe —
    /// Cosmos does not accept a parameter there.
    ///
    /// <para>Public rather than internal because this repo has no <c>InternalsVisibleTo</c> — the
    /// same reason as <c>SkillCatalog</c> and <c>TruncatingSkillsProvider</c>.</para>
    /// </summary>
    public static string BuildSearchSql(int take) =>
        $"SELECT TOP {take} c.conversationId, c.role, c.text, c.ordinal "
        + "FROM c WHERE CONTAINS(c.text, @q, true) "
        // ORDER BY is not decoration: without it TOP returns an ARBITRARY n over the cap while the
        // in-memory twin returns a stable one, so the pair would diverge exactly where the ADR says
        // they can only be kept in step deliberately. Matches InMemoryChatSearchIndex's ordering.
        + "ORDER BY c.conversationId, c.ordinal";

    /// <summary>
    /// The ranked any-term query of <c>search_past_chats</c> (ADR 0111): BM25 over the full-text index that
    /// ADR 0061 declared at container creation for exactly this switch. <c>ORDER BY RANK</c> is a rank
    /// clause, not a property list, so it needs no composite index.
    ///
    /// <para><b>Each term is inlined as a JSON string literal</b> (<see cref="System.Text.Json.JsonSerializer"/>
    /// escapes quotes, backslashes and control characters), because <c>FullTextScore</c> takes an array of
    /// string literals. <paramref name="take"/> is application-clamped, as in <see cref="BuildSearchSql"/>.</para>
    /// </summary>
    public static string BuildRankedSearchSql(IReadOnlyList<string> terms, int take)
    {
        var literals = string.Join(", ", terms.Select(term => System.Text.Json.JsonSerializer.Serialize(term, LiteralOptions)));
        return $"SELECT TOP {take} c.conversationId, c.role, c.text, c.ordinal "
            + $"FROM c WHERE FullTextContainsAny(c.text, {literals}) "
            + $"ORDER BY RANK FullTextScore(c.text, [{literals}])";
    }

    // The relaxed encoder escapes a quote as \" and a backslash as \\ (both Cosmos string-literal escapes) instead
    // of the default's "-style escaping of HTML-sensitive characters. It is "unsafe" only for embedding in
    // HTML, which a SQL string never is; quotes, backslashes and control characters are still escaped.
    private static readonly System.Text.Json.JsonSerializerOptions LiteralOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The window read of <c>read_past_chat</c> (ADR 0111): one conversation's turns in an ordinal range. A
    /// single-property <c>ORDER BY</c> is served by the default range index (only <c>/text</c> is excluded),
    /// so it needs no composite index either.
    /// </summary>
    public const string TurnsSql =
        "SELECT c.conversationId, c.role, c.text, c.ordinal FROM c "
        + "WHERE c.conversationId = @conversationId AND c.ordinal >= @from AND c.ordinal <= @to "
        + "ORDER BY c.ordinal";

    /// <summary>
    /// The delete scan. It <b>filters by conversationId</b> rather than sweeping the partition:
    /// <c>doc-chunks</c> partitions by <c>{userId}/{conversationId}</c> and so can drop a whole
    /// partition, but this container partitions by <c>/userId</c>, where that would delete every
    /// other conversation the user owns.
    /// </summary>
    public const string DeleteScanSql = "SELECT c.id FROM c WHERE c.conversationId = @conversationId";

    /// <summary>
    /// <c>{conversationId}:{ordinal}</c> — the ordinal comes from <c>GetHistoryAsync()</c>'s
    /// projection on BOTH the live path and the backfill, which is what makes an upsert overwrite
    /// rather than duplicate.
    ///
    /// <para>A Cosmos item id may not contain <c>/</c>, <c>\</c>, <c>?</c> or <c>#</c>. Conversation
    /// ids are <c>crypto.randomUUID()</c> or <c>agent-{guid}</c> so none of these occur in practice,
    /// but the id reaches the grain from the client, so it is sanitized rather than trusted.
    /// Collisions are irrelevant: the mapping is deterministic, so the same conversation always
    /// resolves to the same id.</para>
    /// </summary>
    public static string DocumentId(string conversationId, int ordinal)
    {
        var safe = conversationId
            .Replace('/', '_').Replace('\\', '_').Replace('?', '_').Replace('#', '_');
        return $"{safe}:{ordinal}";
    }

    // Item shape. Lowercase 'id' is the Cosmos item id; 'userId' is the partition key path (/userId);
    // 'text' is the path declared in the Bicep full-text policy and index (/text).
    private sealed record MessageDocument(
        string id, string userId, string conversationId, string role, string text, int ordinal);

    private sealed record MessageHit(string conversationId, string role, string text, int ordinal);

    private sealed record IdOnly(string id);
}
