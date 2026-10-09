using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// AC4–AC7 and AC14 (ADR 0061): what the grain writes into the chat search index, what it must NOT
/// write, that a failing index cannot break a turn, and that live indexing followed by a reindex
/// produces no duplicates.
///
/// <para>Assertions read the process-global store through a plain
/// <see cref="InMemoryChatSearchIndex"/> — the same store the silo's
/// <see cref="TestChatSearchIndex"/> writes through — so no seam into the silo container is needed.
/// User ids are unique per test, which is what makes that safe (see
/// <see cref="TestChatSearchIndex"/>).</para>
/// </summary>
public sealed class ChatSearchIndexingTests(SharedClusterFixture fixture) : SharedClusterTests<SharedClusterFixture>(fixture)
{
    private readonly InMemoryChatSearchIndex _reader = new();

    // AC4: an incognito turn writes NOTHING to the index. The three write sites have three different
    // _ephemeral shapes (an early return, a no-guard-by-design reminder path, and a real
    // if (!_ephemeral) block), so this covers the one a chat turn actually takes.
    [Fact]
    public async Task EphemeralTurn_IsNotIndexed()
    {
        const string user = "search-ephemeral-user";
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/search-ephemeral-conv");

        await CollectAsync(agent.ChatAsync("off the record needle", null, ephemeral: true));

        Assert.Empty(await _reader.SearchAsync(user, "needle", 10));
    }

    // AC5: the load-bearing half of the pair. EphemeralTurn_IsNotIndexed asserts that nothing was
    // written, which an indexing path that is broken outright also satisfies — trivially. This one
    // fails both when the gate is stuck OFF and when _ephemeral is wrongly captured, so it is the
    // test that actually proves the flag is turn-scoped rather than activation-scoped.
    [Fact]
    public async Task Indexing_ResumesOnTheNormalTurnAfterAnEphemeralOne()
    {
        const string user = "search-resume-user";
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/search-resume-conv");

        await CollectAsync(agent.ChatAsync("incognito marker", null, ephemeral: true));
        await CollectAsync(agent.ChatAsync("recorded marker", null, ephemeral: false));

        var hits = await _reader.SearchAsync(user, "marker", 20);

        Assert.NotEmpty(hits);
        Assert.Contains(hits, hit => hit.Text.Contains("recorded marker"));
        // The incognito turn stayed out even though the later turn re-projected the conversation.
        // It IS still in the in-memory session (ADR 0034 promises "not persisted", not "not in
        // memory"), so this only holds because the ephemeral path advances the index cursor past it.
        // Measured: without SkipIndexingForEphemeralTurnAsync this assertion fails, with the
        // incognito text indexed at ordinal 0.
        Assert.DoesNotContain(hits, hit => hit.Text.Contains("incognito marker"));
    }

    // AC1/AC2 through the real grain rather than the index in isolation: both sides of the turn land.
    [Fact]
    public async Task NormalTurn_IndexesBothTheUserAndAssistantText()
    {
        const string user = "search-bothsides-user";
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/search-bothsides-conv");

        await CollectAsync(agent.ChatAsync("distinctive question text", null));

        var userHits = await _reader.SearchAsync(user, "distinctive question text", 10);
        Assert.Contains(userHits, hit => hit.Role == "user");

        // The fake chat client does NOT echo the prompt — it answers with a fixed greeting — so the
        // assistant side has to be searched for on its own text. That is the point of the test: a
        // user-text-only index would find the question and never the answer (AC2).
        var assistantHits = await _reader.SearchAsync(user, "how can i help", 10);
        Assert.Contains(assistantHits, hit => hit.Role == "assistant");
    }

    // AC6: deleting a conversation removes its indexed messages, and leaves the user's OTHER
    // conversations alone — the property that a whole-partition delete would silently break, since
    // this container partitions by user rather than by conversation.
    [Fact]
    public async Task PurgeAsync_DeletesIndexedMessages()
    {
        const string user = "search-purge-user";
        var doomed = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/search-purge-doomed");
        var kept = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/search-purge-kept");

        await CollectAsync(doomed.ChatAsync("purgeable needle", null));
        await CollectAsync(kept.ChatAsync("surviving needle", null));
        Assert.NotEmpty(await _reader.SearchAsync(user, "purgeable needle", 10));

        await doomed.PurgeAsync();

        Assert.Empty(await _reader.SearchAsync(user, "purgeable needle", 10));
        Assert.NotEmpty(await _reader.SearchAsync(user, "surviving needle", 10));
    }

    // AC7: the best-effort rule (ADR 0061). An index that throws on every call must not fail the
    // turn — the reply still streams and the session is still persisted. Asserted on the persisted
    // session too, because a reply that streams but is not written would also look like a pass here.
    [Fact]
    public async Task ThrowingIndex_DoesNotBreakTheReply()
    {
        const string user = "search-throwing-user";
        TestChatSearchIndex.FailFor(user, new InvalidOperationException("chat search index is down"));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/search-throwing-conv");

        var events = await CollectAsync(agent.ChatAsync("still answer me", null));

        var answer = string.Concat(events
            .Where(e => e.Type == AgentStreamEventType.TextDelta)
            .Select(e => e.Text));
        Assert.False(string.IsNullOrWhiteSpace(answer), "a failing search index must not swallow the reply.");
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);

        var history = await agent.GetHistoryAsync();
        Assert.NotEmpty(history);
    }

    // AC7's sibling: a failing index must not fail the user's DELETE either. PurgeAsync runs inside a
    // non-reentrant grain that the endpoint awaits synchronously.
    [Fact]
    public async Task ThrowingIndex_DoesNotBreakPurge()
    {
        const string user = "search-throwing-purge-user";
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/search-throwing-purge-conv");
        await CollectAsync(agent.ChatAsync("some content", null));

        TestChatSearchIndex.FailFor(user, new InvalidOperationException("chat search index is down"));

        await agent.PurgeAsync();
        Assert.Empty(await agent.GetHistoryAsync());
    }

    // AC14 — the ordinal-divergence trap, and the single easiest thing in this feature to ship
    // subtly broken. Live indexing and the backfill must derive ordinals from the SAME
    // GetHistoryAsync() projection; if one counted raw session messages instead, this conversation
    // would end up indexed twice under different ids and the hit count would double.
    [Fact]
    public async Task Reindex_AfterLiveIndexing_ProducesNoDuplicates()
    {
        const string user = "search-noduplicates-user";
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/search-noduplicates-conv");

        await CollectAsync(agent.ChatAsync("first duplicable turn", null));
        // A TOOL-CALLING turn in the middle (ADR 0080). Tool calls are projected onto an existing
        // assistant turn and must add no turn of their own — if they ever did, the live path and
        // ReindexAsync would number everything after this point differently and, because the ordinal is
        // half of the Cosmos document id (ADR 0061), silently double-index forever without throwing.
        await CollectAsync(agent.ChatAsync("look up a duplicable fact", null));
        await CollectAsync(agent.ChatAsync("second duplicable turn", null));

        var afterLive = await _reader.SearchAsync(user, "duplicable", 100);
        Assert.NotEmpty(afterLive);

        await agent.ReindexAsync();

        var afterReindex = await _reader.SearchAsync(user, "duplicable", 100);

        // The exact ordinal SET, not just its size: a count comparison is also satisfied by a
        // reindex that writes nothing, which is the failure this test exists to catch.
        Assert.Equal(
            afterLive.Select(h => h.Ordinal).Order(),
            afterReindex.Select(h => h.Ordinal).Order());
    }

    // The other half of the incognito guarantee, and the one a cursor alone does not give:
    // ReindexAsync re-projects from ordinal 0, so without the ephemeral-ordinal set it would write
    // exactly the text the live path deliberately skipped. Fails if ReindexAsync ignores that set.
    [Fact]
    public async Task Reindex_DoesNotResurrectAnEphemeralTurn()
    {
        const string user = "search-reindex-ephemeral-user";
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/search-reindex-ephemeral-conv");

        await CollectAsync(agent.ChatAsync("secret incognito phrase", null, ephemeral: true));
        await CollectAsync(agent.ChatAsync("public recorded phrase", null, ephemeral: false));

        await agent.ReindexAsync();

        var hits = await _reader.SearchAsync(user, "phrase", 50);
        Assert.Contains(hits, hit => hit.Text.Contains("public recorded"));
        Assert.DoesNotContain(hits, hit => hit.Text.Contains("secret incognito"));
    }

    // Reindex is idempotent on its own terms too: running it twice changes nothing.
    [Fact]
    public async Task Reindex_IsIdempotent()
    {
        const string user = "search-idempotent-user";
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/search-idempotent-conv");
        await CollectAsync(agent.ChatAsync("idempotent needle", null));

        var first = await agent.ReindexAsync();
        var countAfterFirst = (await _reader.SearchAsync(user, "idempotent needle", 100)).Count;

        var second = await agent.ReindexAsync();
        var countAfterSecond = (await _reader.SearchAsync(user, "idempotent needle", 100)).Count;

        Assert.Equal(first, second);
        Assert.Equal(countAfterFirst, countAfterSecond);
    }

    // A conversation indexed under one user is not reachable by another, through the real grain path.
    [Fact]
    public async Task IndexedTurns_AreScopedToTheOwningUser()
    {
        var alice = Cluster.GrainFactory.GetGrain<IAgentGrain>("search-scope-alice/conv");
        var bob = Cluster.GrainFactory.GetGrain<IAgentGrain>("search-scope-bob/conv");

        await CollectAsync(alice.ChatAsync("alice confidential needle", null));
        await CollectAsync(bob.ChatAsync("bob confidential needle", null));

        var bobsHits = await _reader.SearchAsync("search-scope-bob", "confidential needle", 50);

        Assert.NotEmpty(bobsHits);
        Assert.DoesNotContain(bobsHits, hit => hit.Text.Contains("alice"));
    }

    // AC4 at the THIRD write site. The image-generation path is the only one with a genuine
    // `if (!_ephemeral)` block — the other two are an early return and a no-guard-by-design reminder
    // — and the ADR calls that asymmetry the thing most likely to attract a wrong guard, so both
    // arms are exercised rather than only the chat path's.
    [Fact]
    public async Task ImageTurn_IndexesItsPromptWhenNormal_AndNotWhenEphemeral()
    {
        const string user = "search-imageturn-user";

        var normal = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/search-imageturn-normal");
        await CollectAsync(normal.ChatAsync("draw a heraldic wyvern", TestSiloConfigurator.ImageModel));

        var incognito = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/search-imageturn-incognito");
        await CollectAsync(incognito.ChatAsync(
            "draw a secret wyvern", TestSiloConfigurator.ImageModel, null, ephemeral: true));

        var hits = await _reader.SearchAsync(user, "wyvern", 50);

        Assert.Contains(hits, hit => hit.Text.Contains("heraldic"));
        Assert.DoesNotContain(hits, hit => hit.Text.Contains("secret"));
    }

    // ---- Retention prune reconciliation (ADR 0016 meeting ADR 0061) --------------------------------
    //
    // A prune renumbers every surviving turn, and IChatSearchIndex has no delete-by-ordinal-range: the only
    // removal is DeleteConversationAsync. So a prune that did nothing here would upsert over the low
    // ordinals and leave the TAIL documents behind — a three-turn conversation returning five hits, two of
    // them text that no longer exists anywhere.

    private static readonly TimeSpan PastTheRetentionWindow = RetentionLimits.Window + TimeSpan.FromHours(1);

    // AC13.
    [Fact]
    public async Task Prune_LeavesNoOrphanedSearchDocuments()
    {
        const string user = "search-prune-orphans-user";
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/agent-prune-orphans");

        // TWO turns before the window, one after, so the prune SHORTENS the conversation. That is what
        // makes this test non-vacuous: with equal lengths the re-projection would upsert over every old
        // ordinal and an orphan could not exist whatever the code did. Here ordinals 2 and 3 have nothing
        // to overwrite them and survive unless they are deleted.
        await CollectAsync(agent.ChatAsync("the ancient needle", null));
        await CollectAsync(agent.ChatAsync("the elderly needle", null));
        Assert.NotEmpty(await _reader.SearchAsync(user, "elderly", 50));

        Fixture.AdvanceClock(PastTheRetentionWindow);
        await CollectAsync(agent.ChatAsync("the fresh needle", null));

        var hits = await _reader.SearchAsync(user, "needle", 50);
        Assert.Contains(hits, hit => hit.Text.Contains("fresh"));
        Assert.DoesNotContain(hits, hit => hit.Text.Contains("ancient"));
        Assert.DoesNotContain(hits, hit => hit.Text.Contains("elderly"));

        // Exactly one user turn is left in the conversation, so exactly one document may mention the
        // needle. A stale tail would show up here as a second hit at an ordinal past the end.
        Assert.Single(hits);
    }

    // AC14: the surviving turns are re-indexed at their NEW ordinals, contiguously from zero — the
    // property that makes a later live turn append rather than collide, and a reindex idempotent.
    [Fact]
    public async Task Prune_KeepsSearchOrdinalsAnchored()
    {
        const string user = "search-prune-ordinals-user";
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/agent-prune-ordinals");

        await CollectAsync(agent.ChatAsync("the ancient anchor", null));

        Fixture.AdvanceClock(PastTheRetentionWindow);
        await CollectAsync(agent.ChatAsync("the fresh anchor", null));

        var hits = (await _reader.SearchAsync(user, "anchor", 50)).OrderBy(hit => hit.Ordinal).ToList();

        var fresh = Assert.Single(hits, hit => hit.Text.Contains("fresh anchor"));
        Assert.Equal("user", fresh.Role);

        // The surviving user turn is now ordinal 0 — it was ordinal 2 before the prune — and the ordinals
        // still line up one-for-one with GetHistoryAsync's projection, which is where they come from.
        Assert.Equal(0, fresh.Ordinal);
        Assert.Equal(Enumerable.Range(0, hits.Count), hits.Select(hit => hit.Ordinal));
    }

    // AC15, and the load-bearing one of the four: it is the only test that fails if the reconcile CLEARS
    // _ephemeralOrdinals instead of remapping them.
    //
    // The incognito turn is taken AFTER the clock moves, so the prune drops the older turn and leaves the
    // incognito one resident — an ordinal shift underneath a set of recorded ordinals. Clearing the set
    // would then hand the re-projection exactly the text ADR 0034 exists to keep out of Cosmos, at its new
    // ordinal, and nothing else in the suite would notice.
    //
    // Driven through the grain rather than POST /api/chat/{id}: the endpoint passes its Ephemeral flag
    // straight into this call, so the state constructed is identical, and this class has no API host. What
    // matters is that it is NOT the agent-run path, which sets _ephemeral = false on entry and mints a GUID
    // conversation id — it can never construct this state, and a test written against it would be green
    // whatever the reconcile did.
    [Fact]
    public async Task Prune_DoesNotIndexAnEphemeralTurn_AfterOrdinalsShift()
    {
        const string user = "search-prune-ephemeral-user";
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/agent-prune-ephemeral");

        await CollectAsync(agent.ChatAsync("the ancient marker", null));

        Fixture.AdvanceClock(PastTheRetentionWindow);
        await CollectAsync(agent.ChatAsync("the incognito marker", null, ephemeral: true));
        await CollectAsync(agent.ChatAsync("the fresh marker", null));

        var hits = await _reader.SearchAsync(user, "marker", 50);

        Assert.Contains(hits, hit => hit.Text.Contains("fresh marker"));
        Assert.DoesNotContain(hits, hit => hit.Text.Contains("ancient marker"));
        Assert.DoesNotContain(hits, hit => hit.Text.Contains("incognito marker"));
    }

    // The regression test for the defect this MR's review caught: an ephemeral turn taken BETWEEN a
    // failed reconcile and its retry.
    //
    // Its ordinals are recorded against the ALREADY-PRUNED projection, so they are in the new frame
    // before the retry runs. A reconcile that carried the earlier prune's delta forward and remapped at
    // retry time would subtract that shift a second time, walking the recorded ordinals backwards onto
    // two real turns — with the result that the incognito text is indexed into Cosmos and two genuine
    // turns are silently skipped. That is precisely the failure ADR 0034 exists to prevent, reached
    // through the machinery added to protect it.
    //
    // The fix is that the remap happens where the shift happens (in PruneExpiredTurnsAsync) and the
    // retry carries a bare bool. Neither AC15 (one prune, delete succeeds) nor AC16 (no ephemeral turn)
    // can reach this state, which is why it needs its own test.
    [Fact]
    public async Task Prune_DoesNotIndexAnEphemeralTurn_TakenBetweenAFailedReconcileAndItsRetry()
    {
        const string user = "search-prune-retry-ephemeral-user";
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/agent-prune-retry-ephemeral");

        await CollectAsync(agent.ChatAsync("the ancient token", null));
        TestChatSearchIndex.FailNextDeletesFor(user, 1);

        // Prunes the ancient turn; the reconcile's delete throws, so the retry is left outstanding.
        Fixture.AdvanceClock(PastTheRetentionWindow);
        await CollectAsync(agent.ChatAsync("the surviving token", null));

        // Recorded in the POST-prune frame — this is the whole point of the test.
        await CollectAsync(agent.ChatAsync("the incognito token", null, ephemeral: true));

        // Prunes nothing (everything left is fresh) but retries the outstanding reconcile, which resets
        // the cursor and re-projects the conversation from ordinal 0.
        await CollectAsync(agent.ChatAsync("the third token", null));

        var hits = await _reader.SearchAsync(user, "token", 50);

        Assert.DoesNotContain(hits, hit => hit.Text.Contains("incognito"));
        Assert.DoesNotContain(hits, hit => hit.Text.Contains("ancient"));
        // And the two real turns the double-shift would have skipped are present.
        Assert.Contains(hits, hit => hit.Text.Contains("surviving"));
        Assert.Contains(hits, hit => hit.Text.Contains("third"));
    }

    // AC16. ADR 0061's best-effort licence rests explicitly on ReindexAsync being a user-invocable repair,
    // and that repair only UPSERTS — DeleteConversationAsync is the sole removal. So a swallowed delete
    // after a destructive prune is the one index failure nothing can ever clean up, and it has to be
    // retried rather than logged and forgotten.
    [Fact]
    public async Task Prune_RetriesTheSearchReconcile_AfterAFailedDelete()
    {
        const string user = "search-prune-retry-user";
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/agent-prune-retry");

        await CollectAsync(agent.ChatAsync("the doomed needle", null));
        TestChatSearchIndex.FailNextDeletesFor(user, 1);

        Fixture.AdvanceClock(PastTheRetentionWindow);
        await CollectAsync(agent.ChatAsync("the surviving needle", null));

        // The turn itself succeeded — search never fails a turn — but the index is now stale in BOTH
        // directions, and that is the measured cost of a failed reconcile: the pruned text is still there,
        // and the surviving turn was not written either, because the cursor was deliberately NOT reset
        // (it sits past the shortened history, so the re-projection has nothing to add). Resetting it
        // anyway would have re-projected over documents that were never removed.
        var afterFailure = await _reader.SearchAsync(user, "needle", 50);
        Assert.Contains(afterFailure, hit => hit.Text.Contains("doomed"));
        Assert.DoesNotContain(afterFailure, hit => hit.Text.Contains("surviving"));

        // A later persisted turn that prunes NOTHING still retries the outstanding reconcile. The pending
        // state is a bare bool: the delete removes the whole conversation whatever was pruned, and the
        // ordinal shift was already applied at prune time — carrying a count across turns here is what
        // put it in the wrong frame (see the between-a-failure-and-its-retry test above).
        await CollectAsync(agent.ChatAsync("a third needle", null));

        var afterRetry = await _reader.SearchAsync(user, "needle", 50);
        Assert.Contains(afterRetry, hit => hit.Text.Contains("surviving"));
        Assert.Contains(afterRetry, hit => hit.Text.Contains("third"));
        Assert.DoesNotContain(afterRetry, hit => hit.Text.Contains("doomed"));
    }

    private static async Task<List<AgentStreamEvent>> CollectAsync(IAsyncEnumerable<AgentStreamEvent> stream)
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        return events;
    }
}
