namespace Agent42.Tests;

/// <summary>
/// Guards the frontend half of the research agent (ADR 0082): the live checklist module and the two stream arms
/// that feed it. Same discipline as <see cref="ReasoningAssetsTests"/> — the repo has no JS test runner, so these
/// assert on source strings of the real <c>wwwroot</c> and prove the wiring exists; the rendered checklist is
/// checked manually in DevHost.
/// </summary>
[Collection("StaticAssets")]
public sealed class ResearchAssetsTests(StaticAssetsServerFixture fixture)
{
    // AC19: the checklist module ships and is reachable from main.js — a module outside the import graph is
    // invisible to the fixture, so finding it here proves send.js carries it.
    [Fact]
    public async Task Frontend_ShipsTheResearchChecklist()
    {
        var app = await fixture.GetAppScriptAsync();

        Assert.Contains("export function renderResearchTodos", app);
        Assert.Contains("msg__todos", app);
        // Dispatched on the wire types the endpoint emits — pinned by AgentApiTests.ChatEndpoint_StreamsResearchEventsAsNdjson.
        Assert.Contains("chunk.type === \"todos\"", app);
        Assert.Contains("chunk.type === \"textReset\"", app);
    }

    // AC19: SHELL is all-or-nothing, so the new module must be listed, and the cache version moved on with it.
    [Fact]
    public async Task ServiceWorker_ListsTheResearchModule()
    {
        var sw = await fixture.Client.GetStringAsync("/sw.js");

        Assert.Contains("\"/js/research.js\"", sw);
        Assert.Contains("agent42-shell-v68", sw);
    }

    // A todo title is model output that never passes the reply's sanitizer, so the only safe sink is textContent.
    [Fact]
    public async Task ResearchTodos_AreRenderedAsTextNotMarkup()
    {
        var app = await fixture.GetAppScriptAsync();

        var render = CodeOnly(StaticAssetsServerFixture.Body(app, "export function renderResearchTodos"));
        Assert.Contains("textContent", render);
        Assert.DoesNotContain("innerHTML", render);
    }

    // The reset clears the ANSWER accumulator as well as the bubble: clearing only the DOM would let the next delta
    // repaint the draft, because textDelta renders `answer`, not the delta.
    [Fact]
    public async Task TextReset_ClearsTheAnswerAccumulator()
    {
        var app = await fixture.GetAppScriptAsync();

        var start = app.IndexOf("chunk.type === \"textReset\"", StringComparison.Ordinal);
        var arm = CodeOnly(app[start..app.IndexOf("} else if", start, StringComparison.Ordinal)]);
        Assert.Contains("answer = \"\"", arm);
        Assert.Contains("setAgentMarkdown", arm);
    }

    private static string CodeOnly(string source) =>
        string.Join('\n', source
            .Split('\n')
            .Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment >= 0 ? line[..comment] : line;
            }));
}
