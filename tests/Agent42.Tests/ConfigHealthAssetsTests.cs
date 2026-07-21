namespace Agent42.Tests;

/// <summary>
/// Guards the Configuration-tab wiring in Preferences. Serves the real Backend <c>wwwroot</c> through the
/// shared <see cref="StaticAssetsServerFixture"/> and asserts on the key source strings — the same
/// source-text-heuristic shape as <see cref="LocationAssetsTests"/> (the repo has no JS runner by design,
/// so these prove the code <i>says</i> the right thing, not that a browser <i>does</i> it).
/// </summary>
[Collection("StaticAssets")]
public sealed class ConfigHealthAssetsTests
{
    private readonly HttpClient _client;

    public ConfigHealthAssetsTests(StaticAssetsServerFixture fixture) => _client = fixture.Client;

    // AC8: the Configuration tab is present in the markup and its panel/probe button exist.
    [Fact]
    public async Task ConfigTab_IsPresentInMarkup()
    {
        var index = await _client.GetStringAsync("/");

        Assert.Contains("id=\"tabConfig\"", index);
        Assert.Contains("id=\"panelConfig\"", index);
        Assert.Contains("id=\"configHealthList\"", index);
        Assert.Contains("id=\"runConfigProbesBtn\"", index);
    }

    // AC8: the tab is wired into profileTabs and appended LAST — MCP must stay index 3 (the OAuth return
    // deep-links to select(3)), so the Configuration pair must sit AFTER the MCP pair in the wireTabs array.
    [Fact]
    public async Task ConfigTab_IsWiredIntoPreferences_AppendedLast()
    {
        var app = await _client.GetStringAsync("/app.js");

        // The profileTabs = wireTabs([...]) array, from the call to its closing "]);".
        var start = app.IndexOf("const profileTabs = wireTabs(", StringComparison.Ordinal);
        Assert.True(start >= 0, "profileTabs wireTabs call not found — renamed?");
        var end = app.IndexOf("]);", start, StringComparison.Ordinal);
        Assert.True(end > start, "profileTabs array not terminated");
        var wire = app[start..end];

        var mcpAt = wire.IndexOf("tabMcp", StringComparison.Ordinal);
        var configAt = wire.IndexOf("tabConfig", StringComparison.Ordinal);
        Assert.True(mcpAt >= 0, "tabMcp missing from profileTabs");
        Assert.True(configAt > mcpAt, "tabConfig must be appended AFTER tabMcp so MCP stays index 3");

        // The MCP-OAuth return still deep-links to index 3 (unchanged by this tab).
        Assert.Contains("profileTabs.select(3)", app);
    }

    // AC8: the static fetch is wired on tab open, and the live-checks button re-fetches with ?probe=true.
    [Fact]
    public async Task ConfigHealth_FetchesStaticOnOpen_AndProbesOnDemand()
    {
        var app = await _client.GetStringAsync("/app.js");

        // Opened alongside the other loaders in openProfileDialog — the STATIC (no-probe) call.
        var open = Body(app, "async function openProfileDialog()");
        Assert.Contains("loadConfigHealth()", open);

        // The loader hits /api/config-health, and only appends ?probe=true when explicitly asked.
        var loader = Body(app, "async function loadConfigHealth(probe = false)");
        Assert.Contains("/api/config-health", loader);
        Assert.Contains("?probe=true", loader);
    }

    // The source of one function, from its declaration to the brace that closes its body — copied from
    // LocationAssetsTests.Body so an assertion can scope to one function rather than the whole file.
    private static string Body(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{declaration}' not found in app.js — did it get renamed?");

        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"no body found for '{declaration}'");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        Assert.Fail($"unbalanced braces after '{declaration}'");
        return string.Empty;
    }
}
