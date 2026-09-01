using System.Collections.Concurrent;

namespace Agent42.Agents;

/// <summary>
/// In-memory <see cref="IChatSearchIndex"/> for local dev (DevHost) and tests, where no Cosmos
/// container is available (ADR 0061). Matching is a substring scan over the user's messages —
/// correct and fast at personal-corpus scale, and the same operation the production
/// <c>CONTAINS</c> query performs.
///
/// <para>The backing store is <b>static / process-global</b> for the same reason as
/// <see cref="InMemoryDocumentIndex"/> and <see cref="InMemoryAttachmentStore"/>: in tests the
/// search endpoint and the indexing grain run in two separate DI containers, so an instance store
/// would split their state and the index-then-search path would silently miss. The testing
/// convention that follows from it is <b>a unique userId per test and no reset</b> — do not add a
/// row to <c>SharedClusterFixture.ResetSiloState()</c>, which is a class fixture while classes run
/// in parallel.</para>
///
/// <para>Case-insensitivity is <see cref="StringComparison.OrdinalIgnoreCase"/> here and the third
/// argument of Cosmos <c>CONTAINS</c> there. Neither is a default; both are deliberate.</para>
/// </summary>
public sealed class InMemoryChatSearchIndex : IChatSearchIndex
{
    // userId -> documentId ({conversationId}:{ordinal}) -> hit. The inner dictionary is keyed by the
    // same id the Cosmos item uses, so an upsert of an existing ordinal REPLACES it — which is what
    // makes reindex idempotent here exactly as it is in production.
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ChatSearchHit>> Store = new();

    public Task IndexTurnsAsync(
        string userId,
        string conversationId,
        IReadOnlyList<ChatSearchTurn> turns,
        CancellationToken cancellationToken = default)
    {
        if (turns.Count == 0)
        {
            return Task.CompletedTask;
        }

        var bucket = Store.GetOrAdd(userId, _ => new ConcurrentDictionary<string, ChatSearchHit>());
        foreach (var turn in turns)
        {
            var id = DocumentId(conversationId, turn.Ordinal);
            bucket[id] = new ChatSearchHit(
                conversationId, turn.Role, ChatSearchLimits.CapText(turn.Text), turn.Ordinal);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatSearchHit>> SearchAsync(
        string userId,
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0 || !Store.TryGetValue(userId, out var bucket))
        {
            return Task.FromResult<IReadOnlyList<ChatSearchHit>>([]);
        }

        var hits = bucket.Values
            .Where(hit => hit.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(hit => hit.ConversationId, StringComparer.Ordinal)
            .ThenBy(hit => hit.Ordinal)
            .Take(Math.Min(maxResults, ChatSearchLimits.MaxSearchResults));

        return Task.FromResult<IReadOnlyList<ChatSearchHit>>([.. hits]);
    }

    public Task DeleteConversationAsync(
        string userId, string conversationId, CancellationToken cancellationToken = default)
    {
        if (!Store.TryGetValue(userId, out var bucket))
        {
            return Task.CompletedTask;
        }

        // Filter within the user's bucket rather than dropping it: the partition is the USER, so a
        // whole-bucket removal would delete every other conversation this user owns. That is the one
        // place InMemoryDocumentIndex's shape does NOT transfer (ADR 0061).
        foreach (var hit in bucket.Where(entry => entry.Value.ConversationId == conversationId).ToArray())
        {
            bucket.TryRemove(hit.Key, out _);
        }

        return Task.CompletedTask;
    }

    // The Cosmos item id, and the in-memory key, are the same string by construction.
    private static string DocumentId(string conversationId, int ordinal) => $"{conversationId}:{ordinal}";
}
