namespace Agent42.Tests;

/// <summary>
/// Guards the per-skill tool-picker wiring in the frontend (per-skill selectable tools, amends ADR 0021/0026).
/// Serves the real Backend <c>wwwroot</c> through the shared <see cref="StaticAssetsServerFixture"/> and
/// asserts on the key source strings — the same shape as <see cref="McpServersAssetsTests"/>.
///
/// <para><b>Source-text heuristics.</b> The repo has no JS test runner by design, so these prove the code
/// <i>says</i> the right thing, not that a browser renders the checkboxes. Exercise it by hand in DevHost
/// (Preferences → Tools, and the composer Tools popover).</para>
/// </summary>
[Collection("StaticAssets")]
public sealed class SkillPickerAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;

    public SkillPickerAssetsTests(StaticAssetsServerFixture fixture) => _fixture = fixture;

    // AC12: the built-ins group must exclude BOTH prefixes. Missing the `skill:` half is the likely bug —
    // every skill would render twice, once ungrouped among the built-ins and once under its heading.
    [Fact]
    public async Task BuiltinsGroup_ExcludesBothPrefixedGroups()
    {
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains(
            "toolCatalog.filter((t) => !t.key.startsWith(\"mcp:\") && !t.key.startsWith(\"skill:\"))",
            app);
        Assert.Contains("toolCatalog.filter((t) => t.key.startsWith(\"skill:\"))", app);

        // The MCP filter must ALSO exclude per-tool rows, which start with the same prefix as their server.
        // A bare startsWith("mcp:") here would render every tool a second time as a top-level server row.
        Assert.Contains(
            "toolCatalog.filter((t) => t.key.startsWith(\"mcp:\") && !t.parentKey)",
            app);
    }

    // AC12: skills render under their own heading, reusing the existing .tool-check__heading class the MCP
    // group already styles (no new CSS).
    //
    // The MCP half no longer goes through addGroup: per-MCP-tool selection renders each server as a nested
    // master + children row, so it emits the heading itself and then walks addServer. The assertion follows
    // the code rather than being deleted — the point it guards is that BOTH groups still get a heading.
    [Fact]
    public async Task SkillsRenderUnderTheirOwnHeading()
    {
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("tool-check__heading", app);
        Assert.Contains("addGroup(\"Skills\", skills)", app);
        Assert.Contains("addHeading(\"MCP servers\")", app);       // the MCP group still gets its heading
        Assert.Contains("for (const server of mcpServers) addServer(server);", app);
    }

    // AC19 — the one assertion nothing else in the repo makes. `cb.checked` must be a FOUR-way OR: an
    // explicit key, the `mcp` wildcard, the `skills` wildcard, or (new with per-MCP-tool selection) the row's
    // own PARENT key being selected.
    //
    // Dropping either wildcard term re-opens the ADR 0026 trap — a profile or agent storing the bare
    // `"mcp"`/`"skills"` would render that whole group unchecked, and the next Save would silently disable
    // every server / every skill. Dropping the fourth term re-opens it one level down and is subtler: a
    // stored `mcp:{id}` (the ordinary ADR 0026 selection, and what the collapse rule emits) would render the
    // server's tool rows all UNCHECKED, so the next Save would emit the server key with no tools ticked —
    // reading back as "this server, none of its tools".
    //
    // Pinned by source text because the alternative (a JS runner) does not exist here by design; whitespace
    // is collapsed so the assertion survives reformatting but not the deletion of a term.
    [Fact]
    public async Task CheckedState_KeepsTheFourWayWildcardOr()
    {
        var app = Collapse(await _fixture.GetAppScriptAsync());

        Assert.Contains(
            Collapse("""
                const isChecked = (tool) => selected.has(tool.key)
                  || (mcpWildcard && tool.key.startsWith("mcp:"))
                  || (skillsWildcard && tool.key.startsWith("skill:"))
                  || (!!tool.parentKey && selected.has(tool.parentKey));
                """),
            app);

        Assert.Contains(Collapse("""const skillsWildcard = selected.has("skills");"""), app);
        Assert.Contains(Collapse("""const mcpWildcard = selected.has("mcp");"""), app);
    }

    private static string Collapse(string source) =>
        System.Text.RegularExpressions.Regex.Replace(source, @"\s+", " ").Trim();
}
