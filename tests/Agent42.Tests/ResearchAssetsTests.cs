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
    // AC19: SHELL is all-or-nothing, so the new module must be listed, and the cache version moved on with it.
    [Fact]
    public async Task ServiceWorker_ListsTheResearchModule()
    {
        var sw = await fixture.Client.GetStringAsync("/sw.js");

        Assert.Contains("\"/js/research.js\"", sw);
        Assert.Contains("agent42-shell-v93", sw);
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
