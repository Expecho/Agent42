using System.Collections.Concurrent;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The <see cref="IChatSearchIndex"/> every test host registers (ADR 0061): the real
/// <see cref="InMemoryChatSearchIndex"/> plus per-user failure injection, so the best-effort rule
/// (AC7) can be exercised without a second silo configurator.
///
/// <para><b>Failure is keyed by userId, not by a global switch, and that is deliberate.</b> The
/// backing store is process-global, test classes run in parallel, and the plan forbids adding a row
/// to <c>SharedClusterFixture.ResetSiloState()</c> (a class fixture cannot reset state shared across
/// concurrently-running classes). A global "throw now" flag would therefore leak into unrelated
/// tests non-deterministically. Keying on the user makes the existing convention — a unique userId
/// per test, and no reset — sufficient on its own.</para>
/// </summary>
public sealed class TestChatSearchIndex : IChatSearchIndex
{
    private static readonly ConcurrentDictionary<string, Exception> Failures = new();

    private readonly InMemoryChatSearchIndex _inner = new();

    /// <summary>Makes every index call for <paramref name="userId"/> throw <paramref name="error"/>.</summary>
    public static void FailFor(string userId, Exception error) => Failures[userId] = error;

    public Task IndexTurnsAsync(
        string userId,
        string conversationId,
        IReadOnlyList<ChatSearchTurn> turns,
        CancellationToken cancellationToken = default)
        => Failures.TryGetValue(userId, out var error)
            ? Task.FromException(error)
            : _inner.IndexTurnsAsync(userId, conversationId, turns, cancellationToken);

    public Task<IReadOnlyList<ChatSearchHit>> SearchAsync(
        string userId, string query, int maxResults, CancellationToken cancellationToken = default)
        => Failures.TryGetValue(userId, out var error)
            ? Task.FromException<IReadOnlyList<ChatSearchHit>>(error)
            : _inner.SearchAsync(userId, query, maxResults, cancellationToken);

    public Task DeleteConversationAsync(
        string userId, string conversationId, CancellationToken cancellationToken = default)
        => Failures.TryGetValue(userId, out var error)
            ? Task.FromException(error)
            : _inner.DeleteConversationAsync(userId, conversationId, cancellationToken);
}
