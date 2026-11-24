namespace Agent42.Tests;

/// <summary>
/// Guards the frontend half of client tools and shared state (ADR 0089 AC5/AC17). The repo has no JS test runner,
/// so these assert on the source of the real <c>wwwroot</c> module graph (everything reachable from
/// <c>main.js</c>) and prove the wiring exists; the behaviour in a browser is checked by hand in DevHost.
/// </summary>
[Collection("StaticAssets")]
public sealed class ClientToolsAssetsTests(StaticAssetsServerFixture fixture)
{
    // The service worker lists both modules (SHELL is all-or-nothing) and moved its cache version on.
    [Fact]
    public async Task ServiceWorker_ListsBothModules()
    {
        var sw = await fixture.Client.GetStringAsync("/sw.js");

        Assert.Contains("\"/js/clienttools.js\"", sw, StringComparison.Ordinal);
        Assert.Contains("\"/js/sharedstate.js\"", sw, StringComparison.Ordinal);
        Assert.Contains("agent42-shell-v89", sw, StringComparison.Ordinal);
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
