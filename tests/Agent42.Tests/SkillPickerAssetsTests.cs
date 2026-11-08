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

    // AC19 — the one assertion nothing else in the repo makes. `cb.checked` must be a FOUR-way OR: an
    // explicit key, the `mcp` wildcard, the `skills` wildcard, or (new with per-MCP-tool selection) the row's
    // own PARENT key being selected.
    //
    // Dropping either wildcard term re-opens the ADR 0026 trap — a profile or agent storing the bare
    // `"mcp"`/`"skills"` would render that whole group unchecked, and the next Save would silently disable
    // every server / every skill.
    //
    // The fourth term is weaker than its siblings and is worth stating precisely, because overstating it
    // invites the wrong later edit. Dropping it does NOT lose data on the next Save: a stored `mcp:{id}`
    // would render the server's tool rows unchecked while the MASTER stays checked, and readToolChecks then
    // collapses to `["mcp:{id}"]` — which IsMcpToolEnabled still reads as every tool of that server, its
    // unchanged ADR 0026 meaning. What it buys is UI consistency, and that is not cosmetic either: from a
    // contradictory render (master ticked, children clear) the user's next child toggle flips the master
    // false and saves a single per-tool key, silently discarding the rest of the server. The loss is one
    // interaction away, not immediate.
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
