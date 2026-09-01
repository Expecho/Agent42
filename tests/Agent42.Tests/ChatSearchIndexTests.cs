using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// AC1, AC2, AC3, AC13, AC15: the in-memory <see cref="IChatSearchIndex"/> matches both roles'
/// text, is owner-scoped, deletes by conversation without touching the user's other chats, caps
/// oversized text, and matches case-insensitively (ADR 0061).
///
/// <para>This is also the only executable half of the case-insensitivity contract — the Cosmos
/// <c>CONTAINS(c.text, @q, true)</c> side is prod-only and unreachable by test — so
/// <see cref="Search_IsCaseInsensitive"/> is what keeps the two implementations from silently
/// diverging.</para>
///
/// <para>User ids are unique per test because the backing store is process-global, the same
/// convention as <see cref="DocumentIndexTests"/>.</para>
/// </summary>
public sealed class ChatSearchIndexTests
{
    private readonly InMemoryChatSearchIndex _index = new();

    private static ChatSearchTurn Turn(int ordinal, string role, string text) => new(ordinal, role, text);

    [Fact]
    public async Task Search_ReturnsMatchingTurns()
    {
        const string user = "search-match-user";
        await _index.IndexTurnsAsync(user, "conv-1",
        [
            Turn(0, "user", "how do I rotate the deploy credentials"),
            Turn(1, "assistant", "you rotate them in the portal"),
        ]);
        await _index.IndexTurnsAsync(user, "conv-2", [Turn(0, "user", "something entirely unrelated")]);

        var hits = await _index.SearchAsync(user, "rotate", maxResults: 10);

        Assert.Equal(2, hits.Count);
        Assert.All(hits, hit => Assert.Equal("conv-1", hit.ConversationId));
        Assert.Contains(hits, hit => hit.Ordinal == 0);
        Assert.Contains(hits, hit => hit.Ordinal == 1);
    }

    // AC2: the corpus is BOTH sides of a turn. A user-text-only index would find the question and
    // never the answer, which is usually the half a user actually remembers (ADR 0061).
    [Fact]
    public async Task Search_MatchesAssistantText()
    {
        const string user = "search-assistant-user";
        await _index.IndexTurnsAsync(user, "conv-1",
        [
            Turn(0, "user", "what is the capital"),
            Turn(1, "assistant", "the capital is Reykjavik"),
        ]);

        var hits = await _index.SearchAsync(user, "Reykjavik", maxResults: 10);

        var hit = Assert.Single(hits);
        Assert.Equal("assistant", hit.Role);
        Assert.Equal(1, hit.Ordinal);
    }

    // AC3: the partition key IS the owner, so this is structural rather than a WHERE clause.
    [Fact]
    public async Task Search_IsOwnerScoped()
    {
        await _index.IndexTurnsAsync("search-owner-a", "conv-a", [Turn(0, "user", "alice private note")]);
        await _index.IndexTurnsAsync("search-owner-b", "conv-b", [Turn(0, "user", "bob private note")]);

        var hits = await _index.SearchAsync("search-owner-b", "private note", maxResults: 10);

        var hit = Assert.Single(hits);
        Assert.Equal("conv-b", hit.ConversationId);
        Assert.Contains("bob", hit.Text);
    }

    // AC15. Neither implementation is case-insensitive by default: Cosmos CONTAINS needs its third
    // argument and .NET needs OrdinalIgnoreCase. Pins the half that can actually run.
    [Fact]
    public async Task Search_IsCaseInsensitive()
    {
        const string user = "search-case-user";
        await _index.IndexTurnsAsync(user, "conv-1", [Turn(0, "user", "The Quick Brown Fox")]);

        Assert.Single(await _index.SearchAsync(user, "quick brown", maxResults: 10));
        Assert.Single(await _index.SearchAsync(user, "QUICK BROWN", maxResults: 10));
        Assert.Single(await _index.SearchAsync(user, "qUiCk BrOwN", maxResults: 10));
    }

    [Fact]
    public async Task Search_WithBlankQuery_ReturnsNothing()
    {
        const string user = "search-blank-user";
        await _index.IndexTurnsAsync(user, "conv-1", [Turn(0, "user", "some indexed text")]);

        Assert.Empty(await _index.SearchAsync(user, "", maxResults: 10));
        Assert.Empty(await _index.SearchAsync(user, "   ", maxResults: 10));
    }

    // Deleting one conversation must NOT drop the user's partition — the trap that makes
    // CosmosDocumentIndex's whole-partition sweep the wrong shape to copy here (ADR 0061).
    [Fact]
    public async Task DeleteConversation_RemovesOnlyThatConversation()
    {
        const string user = "search-delete-user";
        await _index.IndexTurnsAsync(user, "conv-doomed", [Turn(0, "user", "shared keyword here")]);
        await _index.IndexTurnsAsync(user, "conv-kept", [Turn(0, "user", "shared keyword too")]);

        await _index.DeleteConversationAsync(user, "conv-doomed");

        var hits = await _index.SearchAsync(user, "shared keyword", maxResults: 10);
        var hit = Assert.Single(hits);
        Assert.Equal("conv-kept", hit.ConversationId);
    }

    // AC13: one pathological message must not be able to fail a write, and because the write is
    // best-effort such a failure would be silent.
    [Fact]
    public async Task IndexTurns_CapsOversizedText()
    {
        const string user = "search-cap-user";
        var oversized = new string('a', ChatSearchLimits.MaxIndexedTextLength + 5_000) + "needle";
        await _index.IndexTurnsAsync(user, "conv-1", [Turn(0, "user", oversized)]);

        var hits = await _index.SearchAsync(user, "aaaa", maxResults: 10);

        var hit = Assert.Single(hits);
        Assert.Equal(ChatSearchLimits.MaxIndexedTextLength, hit.Text.Length);
        // The tail past the cap is genuinely not indexed — the truncation is real, not cosmetic.
        Assert.Empty(await _index.SearchAsync(user, "needle", maxResults: 10));
    }

    // Re-indexing the same ordinals overwrites. This is the in-memory half of the property that
    // makes ReindexAsync idempotent; AC9/AC14 cover it end to end through the grain.
    [Fact]
    public async Task IndexTurns_SameOrdinal_Overwrites_RatherThanDuplicating()
    {
        const string user = "search-upsert-user";
        await _index.IndexTurnsAsync(user, "conv-1", [Turn(0, "user", "shared marker original")]);
        await _index.IndexTurnsAsync(user, "conv-1", [Turn(0, "user", "shared marker rewritten")]);

        var hits = await _index.SearchAsync(user, "shared marker", maxResults: 10);

        var hit = Assert.Single(hits);
        Assert.Contains("rewritten", hit.Text);
    }

    // Empty input is a no-op rather than an empty write. The live path guards on projected.Count > 0
    // so it never sends an empty batch, but the port must tolerate one regardless — it is public
    // surface, and the guard is the caller's, not the contract's.
    [Fact]
    public async Task IndexTurns_WithNoTurns_WritesNothing()
    {
        const string user = "search-emptybatch-user";
        await _index.IndexTurnsAsync(user, "conv-1", []);

        Assert.Empty(await _index.SearchAsync(user, "anything", 10));
    }

    // CapText is the shared truncation both implementations call, so a null or empty message must
    // not become a null document field on either side.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CapText_TurnsMissingTextIntoEmpty_NotNull(string? text)
    {
        Assert.Equal("", ChatSearchLimits.CapText(text));
    }

    [Fact]
    public void CapText_LeavesTextUnderTheCapUntouched()
    {
        Assert.Equal("short", ChatSearchLimits.CapText("short"));
    }

    // The snippet is built on the SERVER so a search response is not the full text of 50 messages.
    [Fact]
    public void SnippetAround_WindowsTheMatch_AndEllipsisesWhatItTrimmed()
    {
        var text = new string('x', 500) + "NEEDLE" + new string('y', 500);

        var snippet = ChatSearchLimits.SnippetAround(text, "needle");

        Assert.Contains("NEEDLE", snippet);
        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        Assert.True(snippet.Length < text.Length / 2, $"snippet was {snippet.Length} chars");
    }

    [Fact]
    public void SnippetAround_LeavesAShortMessageWhole()
    {
        Assert.Equal("a short reply", ChatSearchLimits.SnippetAround("a short reply", "short"));
    }

    // A hit whose text does not literally contain the query (possible once the corpus is stemmed by
    // full-text search) still returns a readable opening rather than an empty string.
    [Fact]
    public void SnippetAround_WithNoMatch_FallsBackToTheOpening()
    {
        Assert.Equal("no match here", ChatSearchLimits.SnippetAround("no match here", "absent"));
        Assert.Equal("", ChatSearchLimits.SnippetAround("", "absent"));
    }

    [Fact]
    public async Task Search_ClampsToTheResultCeiling()
    {
        const string user = "search-ceiling-user";
        var turns = Enumerable
            .Range(0, ChatSearchLimits.MaxSearchResults + 20)
            .Select(i => Turn(i, "user", $"repeated needle {i}"))
            .ToArray();
        await _index.IndexTurnsAsync(user, "conv-1", turns);

        var hits = await _index.SearchAsync(user, "repeated needle", maxResults: int.MaxValue);

        Assert.Equal(ChatSearchLimits.MaxSearchResults, hits.Count);
    }
}
