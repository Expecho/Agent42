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
/// <para><b>v1 queries with <c>CONTAINS</c>, not the full-text functions.</b> The container's
/// full-text policy is declared anyway because it cannot be added after creation, which makes the
/// later switch to BM25 (<c>ORDER BY RANK FullTextScore</c>) a query change rather than a second
/// backfill.</para>
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
        var definition = new QueryDefinition(
                $"SELECT TOP {take} c.conversationId, c.role, c.text, c.ordinal "
                + "FROM c WHERE CONTAINS(c.text, @q, true)")
            .WithParameter("@q", query);

        var options = new QueryRequestOptions { PartitionKey = new PartitionKey(userId) };
        using var iterator = _container.GetItemQueryIterator<MessageHit>(definition, requestOptions: options);

        var results = new List<ChatSearchHit>();
        while (iterator.HasMoreResults)
        {
            foreach (var hit in await iterator.ReadNextAsync(cancellationToken))
            {
                results.Add(new ChatSearchHit(hit.conversationId, hit.role, hit.text, hit.ordinal));
            }
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
        var definition = new QueryDefinition("SELECT c.id FROM c WHERE c.conversationId = @conversationId")
            .WithParameter("@conversationId", conversationId);

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
                await Task.WhenAll(batch.Select(item =>
                    _container.DeleteItemAsync<MessageDocument>(
                        item.id, partition, cancellationToken: cancellationToken)));
            }
        }
    }

    // {conversationId}:{ordinal} — the ordinal comes from GetHistoryAsync()'s projection on BOTH the
    // live path and the backfill, which is what makes an upsert overwrite rather than duplicate.
    //
    // A Cosmos item id may not contain '/', '\', '?' or '#'. Conversation ids are crypto.randomUUID()
    // or `agent-{guid}` so none of these occur in practice, but the id reaches the grain from the
    // client, so it is sanitized rather than trusted. Collisions are irrelevant: the mapping is
    // deterministic, so the same conversation always resolves to the same id.
    private static string DocumentId(string conversationId, int ordinal)
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
