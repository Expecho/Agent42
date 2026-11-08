namespace Agent42.Tests;

/// <summary>
/// Guards the frontend half of MCP elicitation (ADR 0095). The repo has no JS test runner, so these assert on the
/// source of the real <c>wwwroot</c> module graph (everything reachable from <c>main.js</c>), as
/// <see cref="ClientToolsAssetsTests"/> does. The behaviour in a browser is checked by hand in DevHost.
/// </summary>
[Collection("StaticAssets")]
public sealed class McpElicitationAssetsTests(StaticAssetsServerFixture fixture)
{
    [Fact]
    public async Task ElicitationModule_IsReachableFromMain()
    {
        var app = await fixture.GetAppScriptAsync();

        Assert.Contains("export function showElicitation", app, StringComparison.Ordinal);
        Assert.Contains("mcp_elicitation: (input) => showElicitation(input)", app, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShellListsElicitationModule()
    {
        var sw = await fixture.Client.GetStringAsync("/sw.js");

        Assert.Contains("\"/js/elicitation.js\"", sw, StringComparison.Ordinal);
    }

    private async Task<string> ModuleAsync() => await fixture.Client.GetStringAsync("/js/elicitation.js");

    private static string CodeOnly(string source) =>
        string.Join('\n', source
            .Split('\n')
            .Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment >= 0 ? line[..comment] : line;
            }));
}
