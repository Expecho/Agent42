using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// ADR 0111 AC10, AC11 and the selection/format rules of the past-chat tools, without a silo: keywords from the
/// model's query, which hits survive, and what a result says about a window the index has gaps in.
/// </summary>
public sealed class PastChatQueryTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, ConversationInfo> Known(params string[] ids)
        => ids.ToDictionary(id => id, id => new ConversationInfo(id, $"title {id}", Day), StringComparer.Ordinal);

    // AC10.
    [Fact]
    public void Terms_SplitOnPunctuation_LowerCase_AndDeduplicate()
    {
        Assert.Equal(["kubernetes", "ingress", "tls"], PastChatQuery.Terms("Kubernetes, ingress/TLS? ingress!"));
    }

    [Fact]
    public void Terms_DropSingleCharacters_AndKeepNonAsciiLetters()
    {
        Assert.Equal(["vergadering", "café"], PastChatQuery.Terms("a vergadering x café"));
    }

    [Fact]
    public void Terms_CapTheCountAndTheLength()
    {
        var many = string.Join(' ', Enumerable.Range(0, 20).Select(i => $"word{i}"));
        Assert.Equal(PastChatLimits.MaxTerms, PastChatQuery.Terms(many).Count);

        var term = Assert.Single(PastChatQuery.Terms(new string('x', 200)));
        Assert.Equal(PastChatLimits.MaxTermLength, term.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("? ! a")]
    public void Terms_OfNothingUsable_AreEmpty(string? query)
    {
        Assert.Empty(PastChatQuery.Terms(query));
    }

    // AC11: the read's total is bounded by construction, never by a cut of the last turn.
    [Fact]
    public void ReadWindow_TimesPerTurnCap_FitsTheTotalCap()
    {
        Assert.True(PastChatLimits.ReadWindow * PastChatLimits.MaxReadTurnTextLength <= PastChatLimits.MaxReadTextLength);
    }

    [Fact]
    public void Select_DropsTheCurrentConversation_AndUnknownOnes()
    {
        ChatSearchHit[] ranked = [new("current", "user", "x", 0), new("gone", "user", "x", 0), new("kept", "user", "x", 0)];

        var selected = PastChatQuery.Select(ranked, Known("current", "kept"), currentConversationId: "current");

        Assert.Equal("kept", Assert.Single(selected).ConversationId);
    }

    // AC27's pure half.
    [Fact]
    public void Select_CapsHitsPerConversation_KeepingTheRelevanceOrder()
    {
        ChatSearchHit[] ranked =
        [
            new("busy", "user", "x", 0), new("busy", "user", "x", 1), new("busy", "user", "x", 2),
            new("other", "user", "x", 0),
        ];

        var selected = PastChatQuery.Select(ranked, Known("busy", "other"), currentConversationId: "none");

        Assert.Equal(["busy:0", "busy:1", "other:0"], selected.Select(hit => $"{hit.ConversationId}:{hit.Ordinal}"));
    }

    [Fact]
    public void Select_StopsAtTheHitCap()
    {
        var ids = Enumerable.Range(0, 20).Select(i => $"c{i}").ToArray();
        var ranked = ids.Select(id => new ChatSearchHit(id, "user", "x", 0));

        Assert.Equal(PastChatLimits.MaxHits, PastChatQuery.Select(ranked, Known(ids), "none").Count);
    }

    [Fact]
    public void FormatSearch_StartsWithTheHeader_AndNamesTitleDateIdAndOrdinal()
    {
        var known = Known("c1");
        var text = PastChatQuery.FormatSearch(
            "deploy keys", ["deploy", "keys"], [new("c1", "assistant", "rotate the deploy keys monthly", 3)], known);

        Assert.StartsWith(PastChatLimits.SearchHeaderPrefix, text);
        Assert.Contains("'title c1' (2026-10-01) conversationId=c1 ordinal=3 assistant: rotate the deploy keys monthly", text);
    }

    [Fact]
    public void Window_CoversBothSides_AndNeverGoesNegative()
    {
        Assert.Equal((3, 8), PastChatQuery.Window(5));
        Assert.Equal((0, 3), PastChatQuery.Window(0));
        Assert.Equal((0, 3), PastChatQuery.Window(-7));

        var (from, to) = PastChatQuery.Window(int.MaxValue);
        Assert.True(from >= 0 && to >= from, $"{from}..{to}");
    }

    [Fact]
    public void FormatRead_SaysSoWhenTheIndexHasAGap()
    {
        var info = new ConversationInfo("c1", "t", Day);

        var complete = PastChatQuery.FormatRead(info, 3, [new(3, "user", "a"), new(4, "assistant", "b")]);
        var gapped = PastChatQuery.FormatRead(info, 3, [new(3, "user", "a"), new(5, "assistant", "b")]);

        Assert.StartsWith(PastChatLimits.ReadHeaderPrefix, complete);
        Assert.DoesNotContain("missing", complete);
        Assert.Contains("missing from the search index", gapped);
        Assert.Contains("[5] assistant: b", gapped);
    }

    [Fact]
    public void FormatRead_CutsAnOversizedTurn()
    {
        var info = new ConversationInfo("c1", "t", Day);

        var text = PastChatQuery.FormatRead(info, 0, [new(0, "user", new string('x', PastChatLimits.MaxReadTurnTextLength + 50))]);

        Assert.Contains(PastChatLimits.TruncationMarker, text);
    }
}
