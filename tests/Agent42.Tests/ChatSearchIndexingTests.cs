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
        await CollectAsync(agent.ChatAsync("second duplicable turn", null));

        var afterLive = await _reader.SearchAsync(user, "duplicable", 100);
        Assert.NotEmpty(afterLive);

        await agent.ReindexAsync();

        var afterReindex = await _reader.SearchAsync(user, "duplicable", 100);
        Assert.Equal(afterLive.Count, afterReindex.Count);
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
