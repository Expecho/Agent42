using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The web-document port (plan cc-12; ADR 0066): names and URLs are appended and trimmed in lockstep and only here, and
/// the port reads state live (a replaced state object, as <c>ClearStateAsync</c> does, must not leave a stale list).
/// </summary>
public sealed class WebDocumentListTests
{
    private static (WebDocumentList List, AgentGrainState State) Make()
    {
        var state = new AgentGrainState();
        return (new WebDocumentList(() => state), state);
    }

    [Fact]
    public void Remember_AppendsNameAndUrlAtTheSamePosition()
    {
        var (list, state) = Make();

        list.Remember("a.pdf", "https://example.com/a.pdf");

        Assert.Equal(["a.pdf"], state.WebDocumentNames);
        Assert.Equal(["https://example.com/a.pdf"], state.WebDocumentUrls);
        Assert.True(list.ContainsUrl("https://example.com/a.pdf"));
        Assert.False(list.ContainsUrl("https://example.com/A.pdf"));
        Assert.Equal(1, list.Count);
    }

    [Fact]
    public void Remember_TrimsOldestFirst_AndKeepsTheListsAligned()
    {
        var (list, state) = Make();
        const int Extra = 3;

        for (var i = 0; i < WebDocumentLimits.MaxWebDocuments + Extra; i++)
        {
            list.Remember($"doc-{i}", $"https://example.com/{i}");
        }

        Assert.Equal(WebDocumentLimits.MaxWebDocuments, list.Count);
        Assert.Equal(WebDocumentLimits.MaxWebDocuments, state.WebDocumentUrls.Count);
        for (var i = 0; i < state.WebDocumentNames.Count; i++)
        {
            Assert.Equal($"doc-{i + Extra}", state.WebDocumentNames[i]);
            Assert.Equal($"https://example.com/{i + Extra}", state.WebDocumentUrls[i]);
        }

        Assert.False(list.ContainsUrl("https://example.com/0"));
    }

    [Fact]
    public void Clear_EmptiesBothLists()
    {
        var (list, state) = Make();
        list.Remember("a.pdf", "https://example.com/a.pdf");

        list.Clear();

        Assert.Empty(state.WebDocumentNames);
        Assert.Empty(state.WebDocumentUrls);
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void ReadsStateLive_AfterTheStateObjectIsReplaced()
    {
        var state = new AgentGrainState();
        var list = new WebDocumentList(() => state);
        list.Remember("a.pdf", "https://example.com/a.pdf");

        state = new AgentGrainState();

        Assert.Equal(0, list.Count);
        Assert.False(list.ContainsUrl("https://example.com/a.pdf"));
        list.Remember("b.pdf", "https://example.com/b.pdf");
        Assert.Equal(["b.pdf"], state.WebDocumentNames);
    }
}
