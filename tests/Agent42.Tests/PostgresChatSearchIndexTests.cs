using Agent42.Agents;
using Agent42.Backend;
using Npgsql;

namespace Agent42.Tests;

/// <summary>
/// AC11: the Postgres chat search index behaves like <see cref="InMemoryChatSearchIndex"/> (the cases of
/// <c>ChatSearchIndexTests</c>, re-run against a real Postgres): substring and ranked search, owner scoping, ordinal
/// windows, per-conversation delete, text cap, overwrite on re-index. The ranked search is also injection-safe.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresChatSearchIndexTests(PostgresFixture postgres)
{
    private async Task<PostgresChatSearchIndex> IndexAsync()
    {
        var source = new NpgsqlDataSourceBuilder(await postgres.CreateDatabaseAsync()).Build();
        await PostgresSchema.EnsureAsync(source, 3);
        return new PostgresChatSearchIndex(source);
    }

    private static ChatSearchTurn Turn(int ordinal, string role, string text) => new(ordinal, role, text);

    [DockerFact]
    public async Task Search_ReturnsMatchingTurns_OfBothRoles()
    {
        var index = await IndexAsync();
        await index.IndexTurnsAsync("u", "conv-1",
        [
            Turn(0, "user", "how do I rotate the deploy credentials"),
            Turn(1, "assistant", "you rotate them in the portal"),
        ]);
        await index.IndexTurnsAsync("u", "conv-2", [Turn(0, "user", "something entirely unrelated")]);

        var hits = await index.SearchAsync("u", "rotate", maxResults: 10);

        Assert.Equal(2, hits.Count);
        Assert.All(hits, hit => Assert.Equal("conv-1", hit.ConversationId));
        Assert.Equal(["user", "assistant"], hits.Select(h => h.Role));
    }

    [DockerFact]
    public async Task Search_IsOwnerScoped_AndCaseInsensitive()
    {
        var index = await IndexAsync();
        await index.IndexTurnsAsync("owner-a", "conv-a", [Turn(0, "user", "alice private note")]);
        await index.IndexTurnsAsync("owner-b", "conv-b", [Turn(0, "user", "The Quick Brown Fox")]);

        Assert.Empty(await index.SearchAsync("owner-b", "alice", maxResults: 10));
        Assert.Single(await index.SearchAsync("owner-b", "quick brown", maxResults: 10));
        Assert.Single(await index.SearchAsync("owner-b", "QUICK BROWN", maxResults: 10));
        Assert.Single(await index.SearchAsync("owner-b", "qUiCk BrOwN", maxResults: 10));
    }

    [DockerFact]
    public async Task Search_TreatsLikeWildcards_AsPlainCharacters()
    {
        var index = await IndexAsync();
        await index.IndexTurnsAsync("u", "c", [Turn(0, "user", "discount is 50% off"), Turn(1, "user", "snake_case name"), Turn(2, "user", "unrelated")]);

        Assert.Equal(0, (await index.SearchAsync("u", "50% o", 10)).Single().Ordinal);
        Assert.Equal(1, (await index.SearchAsync("u", "snake_case", 10)).Single().Ordinal);
        Assert.Equal(0, Assert.Single(await index.SearchAsync("u", "%", 10)).Ordinal);
    }

    [DockerFact]
    public async Task Search_WithBlankQuery_ReturnsNothing()
    {
        var index = await IndexAsync();
        await index.IndexTurnsAsync("u", "conv-1", [Turn(0, "user", "some indexed text")]);

        Assert.Empty(await index.SearchAsync("u", "", maxResults: 10));
        Assert.Empty(await index.SearchAsync("u", "   ", maxResults: 10));
    }

    [DockerFact]
    public async Task DeleteConversation_RemovesOnlyThatConversation()
    {
        var index = await IndexAsync();
        await index.IndexTurnsAsync("u", "conv-doomed", [Turn(0, "user", "shared keyword here")]);
        await index.IndexTurnsAsync("u", "conv-kept", [Turn(0, "user", "shared keyword too")]);

        await index.DeleteConversationAsync("u", "conv-doomed");

        Assert.Equal("conv-kept", Assert.Single(await index.SearchAsync("u", "shared keyword", maxResults: 10)).ConversationId);
    }

    [DockerFact]
    public async Task IndexTurns_CapsOversizedText_Overwrites_AndToleratesAnEmptyBatch()
    {
        var index = await IndexAsync();
        var oversized = new string('a', ChatSearchLimits.MaxIndexedTextLength + 5_000) + "needle";
        await index.IndexTurnsAsync("cap", "c", [Turn(0, "user", oversized)]);
        var hit = Assert.Single(await index.SearchAsync("cap", "aaaa", maxResults: 10));
        Assert.Equal(ChatSearchLimits.MaxIndexedTextLength, hit.Text.Length);
        Assert.Empty(await index.SearchAsync("cap", "needle", maxResults: 10));

        await index.IndexTurnsAsync("up", "c", [Turn(0, "user", "shared marker original")]);
        await index.IndexTurnsAsync("up", "c", [Turn(0, "user", "shared marker rewritten")]);
        Assert.Contains("rewritten", Assert.Single(await index.SearchAsync("up", "shared marker", maxResults: 10)).Text);

        await index.IndexTurnsAsync("empty", "c", []);
        Assert.Empty(await index.SearchAsync("empty", "anything", 10));
    }

    [DockerFact]
    public async Task Search_ClampsToTheResultCeiling()
    {
        var index = await IndexAsync();
        await index.IndexTurnsAsync("u", "c", [.. Enumerable.Range(0, ChatSearchLimits.MaxSearchResults + 10).Select(i => Turn(i, "user", "common word"))]);

        Assert.Equal(ChatSearchLimits.MaxSearchResults, (await index.SearchAsync("u", "common", 10_000)).Count);
        Assert.Equal(ChatSearchLimits.MaxSearchResults, (await index.SearchRankedAsync("u", ["common"], 10_000)).Count);
    }

    [DockerFact]
    public async Task SearchRanked_MatchesAnyTerm_CaseInsensitive_AndRanksMoreMatchedTermsFirst()
    {
        var index = await IndexAsync();
        await index.IndexTurnsAsync("u", "c",
        [
            Turn(0, "user", "only the Alpha here"),
            Turn(1, "user", "alpha and beta together"),
            Turn(2, "user", "nothing relevant"),
        ]);

        var hits = await index.SearchRankedAsync("u", ["ALPHA", "beta"], maxResults: 10);

        Assert.Equal([1, 0], hits.Select(h => h.Ordinal));
    }

    [DockerFact]
    public async Task SearchRanked_IsOwnerScoped_AndEmptyTermsReturnNothing()
    {
        var index = await IndexAsync();
        await index.IndexTurnsAsync("owner-a", "c", [Turn(0, "user", "alice secret")]);
        await index.IndexTurnsAsync("owner-b", "c", [Turn(0, "user", "bob note")]);

        Assert.Empty(await index.SearchRankedAsync("owner-b", ["alice"], 10));
        Assert.Single(await index.SearchRankedAsync("owner-b", ["bob"], 10));
        Assert.Empty(await index.SearchRankedAsync("owner-b", [], 10));
    }

    [DockerTheory]
    [InlineData("a' | b")]
    [InlineData("!")]
    [InlineData(":*")]
    [InlineData("alpha:*")]
    [InlineData("'")]
    [InlineData("&")]
    [InlineData("(((")]
    [InlineData("alpha <-> beta")]
    [InlineData("x'); DROP TABLE agent42_chat_messages; --")]
    public async Task SearchRanked_TsqueryOperatorsInATerm_ArePlainText_NeverAnError(string term)
    {
        var index = await IndexAsync();
        await index.IndexTurnsAsync("u", "c", [Turn(0, "user", "alpha beta gamma"), Turn(1, "user", "delta")]);

        // Must not throw (a syntax error would mean the term reached the tsquery parser as operators) ...
        var hits = await index.SearchRankedAsync("u", [term], 10);

        // ... must not have acted as an OR/NOT over the corpus ("a' | b" must not match every row) ...
        Assert.True(hits.Count <= 1, $"'{term}' matched {hits.Count} turns");
        // ... and the table is still there.
        Assert.Equal(2, (await index.GetTurnsAsync("u", "c", 0, 10)).Count);
    }

    [DockerFact]
    public async Task GetTurns_ReturnsTheRangeInOrdinalOrder_SkippingGaps_AndIsOwnerScoped()
    {
        var index = await IndexAsync();
        await index.IndexTurnsAsync("u", "c", [Turn(5, "assistant", "e"), Turn(0, "user", "a"), Turn(2, "user", "c")]);
        await index.IndexTurnsAsync("other", "c", [Turn(1, "user", "not yours")]);

        var turns = await index.GetTurnsAsync("u", "c", 0, 4);

        Assert.Equal([0, 2], turns.Select(t => t.Ordinal));
        Assert.Empty(await index.GetTurnsAsync("stranger", "c", 0, 10));
    }
}
