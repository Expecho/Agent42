using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The startup decision the Search feature owns (ADR 0061), plus the two rules of
/// <see cref="CosmosChatSearchIndex"/> that are reachable without a Cosmos account.
///
/// <para>The adapter itself is a prod-only path with no offline stand-in — the same position
/// <c>CosmosDocumentIndex</c> has held since ADR 0007. But two of its rules are exactly the kind the
/// ADR says will "diverge silently": the case-insensitivity argument, whose only other half lives in
/// the in-memory twin, and the filtered delete, which must not become the whole-partition sweep its
/// exemplar uses. Both are pure string construction, so both are pinned here rather than left to a
/// prod smoke.</para>
/// </summary>
public sealed class ChatSearchIndexRegistrationTests
{
    private static IServiceProvider Build(string? containerName)
    {
        var settings = new Dictionary<string, string?>();
        if (containerName is not null)
        {
            settings["Cosmos:ChatMessagesContainerName"] = containerName;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        // Constructing a CosmosClient opens no connection, so a placeholder endpoint is enough for
        // the Cosmos branch to resolve. The emulator's well-known key is used because the client
        // validates that the key is base64, not that it works.
        services.AddSingleton(new CosmosClient(
            "https://localhost:8081/",
            "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="));
        services.AddChatSearchIndex(configuration);
        return services.BuildServiceProvider();
    }

    // Absent the container key — DevHost, the AppHost's Cosmos emulator, every test host — the
    // in-memory twin is registered, so the emulator never receives a query against a container that
    // does not exist there.
    [Fact]
    public void WithoutTheContainerConfigured_RegistersTheInMemoryTwin()
    {
        var index = Build(null).GetRequiredService<IChatSearchIndex>();
        Assert.IsType<InMemoryChatSearchIndex>(index);
    }

    [Fact]
    public void WithTheContainerConfigured_RegistersTheCosmosIndex()
    {
        var index = Build("chat-messages").GetRequiredService<IChatSearchIndex>();
        Assert.IsType<CosmosChatSearchIndex>(index);
    }

    // A blank value is treated as absent rather than as a container literally named "": a
    // half-configured deployment must degrade to the twin, not to a Cosmos client pointed at nothing.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WithABlankContainerName_RegistersTheInMemoryTwin(string containerName)
    {
        var index = Build(containerName).GetRequiredService<IChatSearchIndex>();
        Assert.IsType<InMemoryChatSearchIndex>(index);
    }

    // The load-bearing half of AC15. The in-memory twin's case-insensitivity is pinned by
    // Search_IsCaseInsensitive; this is the only assertion that reaches the Cosmos half. CONTAINS
    // defaults to CASE-SENSITIVE, so dropping the third argument silently makes production behave
    // differently from every test in the suite.
    [Fact]
    public void SearchSql_AsksForCaseInsensitiveMatching()
    {
        var sql = CosmosChatSearchIndex.BuildSearchSql(25);

        Assert.Contains("CONTAINS(c.text, @q, true)", sql);
        // The term is a parameter, never interpolated.
        Assert.Contains("@q", sql);
        Assert.Contains("TOP 25", sql);
    }

    // ADR 0111 AC6: the tool's query is any-term + BM25, a DIFFERENT query from the sidebar's CONTAINS.
    [Fact]
    public void RankedSearchSql_UsesFullTextContainsAnyAndRank()
    {
        var sql = CosmosChatSearchIndex.BuildRankedSearchSql(["ingress", "tls"], 16);

        Assert.Contains("TOP 16", sql);
        Assert.Contains("WHERE FullTextContainsAny(c.text, \"ingress\", \"tls\")", sql);
        // Variadic, NOT an array: the prod account rejects the array form with SC2241 (measured 2026-10-21).
        Assert.Contains("ORDER BY RANK FullTextScore(c.text, \"ingress\", \"tls\")", sql);
        Assert.DoesNotContain("[", sql);
    }

    // AC7: the terms are inlined (FullTextScore takes literals), so each must be unable to leave its literal.
    [Fact]
    public void RankedSearchSql_EscapesQuotesBackslashesAndBrackets()
    {
        var sql = CosmosChatSearchIndex.BuildRankedSearchSql(["a\") OR true OR (\"", "b\\", "c]"], 5);

        Assert.Contains("\"a\\\") OR true OR (\\\"\"", sql);
        Assert.Contains("\"b\\\\\"", sql);
        Assert.Contains("\"c]\"", sql);
        // The call still closes where it should: the last literal, then the FullTextScore paren.
        Assert.EndsWith("\"c]\")", sql);
    }

    // AC8.
    [Fact]
    public void TurnsSql_FiltersConversationAndOrdinalRange()
    {
        Assert.Contains(
            "WHERE c.conversationId = @conversationId AND c.ordinal >= @from AND c.ordinal <= @to",
            CosmosChatSearchIndex.TurnsSql);
        Assert.EndsWith("ORDER BY c.ordinal", CosmosChatSearchIndex.TurnsSql);
    }

    // The delete must filter within the user's partition. This container partitions by /userId, so
    // the whole-partition sweep CosmosDocumentIndex uses would delete every other conversation the
    // user owns — the one place that exemplar deliberately does not transfer.
    [Fact]
    public void DeleteSql_FiltersByConversation_RatherThanSweepingThePartition()
    {
        Assert.Contains("WHERE c.conversationId = @conversationId", CosmosChatSearchIndex.DeleteScanSql);
    }

    // The document id is {conversationId}:{ordinal} — half of what makes a re-index overwrite rather
    // than duplicate.
    [Fact]
    public void DocumentId_CombinesConversationAndOrdinal()
    {
        Assert.Equal("conv-1:0", CosmosChatSearchIndex.DocumentId("conv-1", 0));
        Assert.Equal("conv-1:7", CosmosChatSearchIndex.DocumentId("conv-1", 7));
    }

    // A Cosmos item id may not contain these four characters. Conversation ids never do in practice,
    // but the id reaches the grain from the client, so it is sanitized rather than trusted.
    [Theory]
    [InlineData("a/b", "a_b:0")]
    [InlineData("a\\b", "a_b:0")]
    [InlineData("a?b", "a_b:0")]
    [InlineData("a#b", "a_b:0")]
    public void DocumentId_StripsCharactersCosmosRejectsInAnId(string conversationId, string expected)
    {
        Assert.Equal(expected, CosmosChatSearchIndex.DocumentId(conversationId, 0));
    }
}
