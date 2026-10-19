using Agent42.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// AC7 (ADR 0106): the search projection's logic without a silo. The grain-level cases in
/// <see cref="ChatSearchIndexingTests"/> still pin the end-to-end behaviour; these pin the bookkeeping they rest on —
/// the cursor, the incognito ordinals, and the reconcile retry — one rule per test.
/// </summary>
public sealed class ChatSearchProjectionTests
{
    private const string GrainKey = "projection-user/projection-conv";

    private readonly List<ChatTurn> _history = [];
    private readonly RecordingIndex _index = new();

    private ChatSearchProjection NewProjection()
        => new(_index, NullLogger.Instance, GrainKey, () => [.. _history]);

    private void Turns(params string[] texts)
    {
        foreach (var text in texts)
        {
            _history.Add(new ChatTurn(_history.Count % 2 == 0 ? "user" : "assistant", text));
        }
    }

    [Fact]
    public async Task IndexNewTurns_WritesOnlyWhatIsNew_UnderTheConversationsIds()
    {
        var projection = NewProjection();
        Turns("q1", "a1");
        await projection.IndexNewTurnsAsync();
        Turns("q2", "a2");
        await projection.IndexNewTurnsAsync();

        Assert.Equal([[0, 1], [2, 3]], _index.Writes.Select(w => w.Ordinals));
        Assert.All(_index.Writes, w => Assert.Equal(("projection-user", "projection-conv"), (w.UserId, w.ConversationId)));
    }

    [Fact]
    public async Task EphemeralTurn_IsSkipped_AndIndexingResumesAfterIt()
    {
        var projection = NewProjection();
        Turns("q1", "a1");
        await projection.IndexNewTurnsAsync();
        Turns("secret", "secret reply");
        await projection.SkipEphemeralTurnAsync();
        Turns("q3", "a3");
        await projection.IndexNewTurnsAsync();

        Assert.True(projection.HasEphemeralTurns);
        Assert.Equal([0, 1, 4, 5], _index.Writes.SelectMany(w => w.Ordinals));
    }

    // The backfill re-projects from 0 by design, and must still leave the incognito ordinals out.
    [Fact]
    public async Task Reindex_SkipsTheIncognitoOrdinals()
    {
        var projection = NewProjection();
        Turns("q1", "a1");
        await projection.IndexNewTurnsAsync();
        Turns("secret", "secret reply");
        await projection.SkipEphemeralTurnAsync();

        var indexed = await projection.ReindexAsync();

        Assert.Equal(2, indexed);
        Assert.Equal([0, 1], _index.Writes[^1].Ordinals);
    }

    // A prune shifts every ordinal down: the incognito ones move with it (or fall off the front), and the reconcile
    // drops the index and re-projects from 0.
    [Fact]
    public async Task Prune_RemapsTheIncognitoOrdinals_AndTheReconcileReprojects()
    {
        var projection = NewProjection();
        Turns("q1", "a1");
        await projection.IndexNewTurnsAsync();
        Turns("secret", "secret reply");
        await projection.SkipEphemeralTurnAsync();
        Turns("q3", "a3");
        await projection.IndexNewTurnsAsync();

        _history.RemoveRange(0, 2);
        projection.RemapAfterPrune(droppedProjectedTurns: 2);
        await projection.ReconcileAfterPruneAsync(pruned: true);
        await projection.IndexNewTurnsAsync();

        Assert.Equal(1, _index.Deletes);
        Assert.Equal([2, 3], _index.Writes[^1].Ordinals);
        Assert.DoesNotContain("secret", _index.Writes[^1].Texts);
    }

    [Fact]
    public async Task Reconcile_WithoutAPruneOrAPendingRetry_DoesNothing()
    {
        await NewProjection().ReconcileAfterPruneAsync(pruned: false);

        Assert.Equal(0, _index.Deletes);
    }

    // A failed re-anchor is retried on the next persisted turn, even one that pruned nothing, and the cursor is only
    // reset once the delete has succeeded.
    [Fact]
    public async Task FailedReconcile_IsRetriedOnTheNextTurn()
    {
        var projection = NewProjection();
        Turns("q1", "a1");
        await projection.IndexNewTurnsAsync();
        _index.FailNextDeletes = 1;

        await projection.ReconcileAfterPruneAsync(pruned: true);
        await projection.IndexNewTurnsAsync();
        Assert.Single(_index.Writes);

        await projection.ReconcileAfterPruneAsync(pruned: false);
        await projection.IndexNewTurnsAsync();

        Assert.Equal(1, _index.Deletes);
        Assert.Equal([0, 1], _index.Writes[^1].Ordinals);
    }

    // An incognito turn taken BETWEEN a failed reconcile and its retry records its ordinals in the already-pruned
    // frame; the retry must not shift them a second time (the reason the pending flag carries no delta).
    [Fact]
    public async Task EphemeralTurnBetweenAFailedReconcileAndItsRetry_StaysOutOfTheIndex()
    {
        var projection = NewProjection();
        Turns("q1", "a1", "q2", "a2");
        await projection.IndexNewTurnsAsync();
        _history.RemoveRange(0, 2);
        projection.RemapAfterPrune(droppedProjectedTurns: 2);
        _index.FailNextDeletes = 1;
        // The pruning turn's persist, in the grain's order: reconcile (fails), then index.
        await projection.ReconcileAfterPruneAsync(pruned: true);
        await projection.IndexNewTurnsAsync();

        Turns("secret", "secret reply");
        await projection.SkipEphemeralTurnAsync();
        await projection.ReconcileAfterPruneAsync(pruned: false);
        Turns("q4", "a4");
        await projection.IndexNewTurnsAsync();

        Assert.DoesNotContain(_index.Writes.SelectMany(w => w.Texts), t => t.StartsWith("secret", StringComparison.Ordinal));
        Assert.Equal([0, 1, 4, 5], _index.Writes[^1].Ordinals);
    }

    [Fact]
    public async Task TruncateTo_ForgetsTheRemovedIncognitoOrdinals_AndPullsBackTheCursorOnlyWhenAsked()
    {
        var projection = NewProjection();
        Turns("q1", "a1");
        await projection.IndexNewTurnsAsync();
        Turns("secret", "secret reply");
        await projection.SkipEphemeralTurnAsync();

        _history.RemoveRange(2, 2);
        projection.TruncateTo(projectedCount: 2, pullBackCursor: true);
        Turns("q2", "a2");
        await projection.IndexNewTurnsAsync();

        Assert.False(projection.HasEphemeralTurns);
        Assert.Equal([2, 3], _index.Writes[^1].Ordinals);
    }

    [Fact]
    public async Task TruncateTo_WithoutPullBack_KeepsTheCursor()
    {
        var projection = NewProjection();
        Turns("q1", "a1", "q2", "a2");
        await projection.IndexNewTurnsAsync();

        _history.RemoveRange(2, 2);
        projection.TruncateTo(projectedCount: 2, pullBackCursor: false);
        Turns("q3", "a3");
        await projection.IndexNewTurnsAsync();

        // The cursor still says 4, so the re-added turns at 2 and 3 are not re-written until a reconcile or reindex.
        Assert.Single(_index.Writes);
    }

    // Best-effort: a failing index never surfaces from the live path, but the explicit backfill does throw.
    [Fact]
    public async Task IndexFailure_IsSwallowedOnTheLivePath_ButNotByReindex()
    {
        var projection = NewProjection();
        Turns("q1", "a1");
        _index.FailWrites = true;

        await projection.IndexNewTurnsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(projection.ReindexAsync);
    }

    [Fact]
    public async Task DeleteConversation_ClearsTheIncognitoOrdinals()
    {
        var projection = NewProjection();
        Turns("secret", "secret reply");
        await projection.SkipEphemeralTurnAsync();

        await projection.DeleteConversationAsync();

        Assert.False(projection.HasEphemeralTurns);
        Assert.Equal(1, _index.Deletes);
    }

    [Fact]
    public void Project_SkipsImageOnlyTurnsAndIncognitoOrdinals()
    {
        List<ChatTurn> turns = [new("user", "hello"), new("assistant", " "), new("user", "secret"), new("assistant", "ok")];

        var projected = ChatSearchProjection.Project(turns, fromOrdinal: 0, new HashSet<int> { 2 });

        Assert.Equal([0, 3], projected.Select(t => t.Ordinal));
    }

    private sealed class RecordingIndex : IChatSearchIndex
    {
        public List<(string UserId, string ConversationId, int[] Ordinals, string[] Texts)> Writes { get; } = [];

        public int Deletes { get; private set; }

        public int FailNextDeletes { get; set; }

        public bool FailWrites { get; set; }

        public Task IndexTurnsAsync(
            string userId, string conversationId, IReadOnlyList<ChatSearchTurn> turns,
            CancellationToken cancellationToken = default)
        {
            if (FailWrites)
            {
                throw new InvalidOperationException("index down");
            }

            Writes.Add((userId, conversationId, [.. turns.Select(t => t.Ordinal)], [.. turns.Select(t => t.Text)]));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ChatSearchHit>> SearchAsync(
            string userId, string query, int maxResults, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ChatSearchHit>>([]);

        public Task DeleteConversationAsync(
            string userId, string conversationId, CancellationToken cancellationToken = default)
        {
            if (FailNextDeletes > 0)
            {
                FailNextDeletes--;
                throw new InvalidOperationException("delete down");
            }

            Deletes++;
            return Task.CompletedTask;
        }
    }
}
