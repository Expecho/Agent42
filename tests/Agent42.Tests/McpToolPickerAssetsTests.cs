namespace Agent42.Tests;

/// <summary>
/// Guards the per-MCP-tool picker wiring in the frontend (per-MCP-tool selectable tools, amends ADR
/// 0021/0026). Serves the real Backend <c>wwwroot</c> through the shared
/// <see cref="StaticAssetsServerFixture"/> and asserts on the key source strings — the same shape as
/// <see cref="SkillPickerAssetsTests"/> and <see cref="McpServersAssetsTests"/>.
///
/// <para><b>Source-text heuristics, and the honest limit.</b> The repo has no JS test runner by design, so
/// these prove the code <i>says</i> the right thing, not that a browser renders or saves the checkboxes. The
/// preservation rule below (AC20) is the client-side backstop for the one failure the server-side rule in
/// <c>AgentGrain.ExpandMcpWildcard</c> cannot reach, and it is guarded by <b>nothing but</b> this file. Treat
/// a change to <c>readToolChecks</c> accordingly, and exercise it by hand in DevHost (Preferences → Tools,
/// the agent editor, and the composer Tools popover).</para>
/// </summary>
[Collection("StaticAssets")]
public sealed class McpToolPickerAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;

    public McpToolPickerAssetsTests(StaticAssetsServerFixture fixture) => _fixture = fixture;

    // AC23 — the catalog is partitioned on parentKey, not on the key prefix. A server row and its tool rows
    // both start with "mcp:", so the pre-existing startsWith filter can no longer tell them apart: left
    // alone, every tool would ALSO render as a top-level server row with its own (empty) disclosure.
    [Fact]
    public async Task RenderToolChecks_PartitionsOnParentKey()
    {
        var app = Collapse(await _fixture.GetAppScriptAsync());

        Assert.Contains(
            Collapse("""const mcpServers = toolCatalog.filter((t) => t.key.startsWith("mcp:") && !t.parentKey);"""),
            app);
        Assert.Contains(
            Collapse("""const mcpToolsFor = (serverKey) => toolCatalog.filter((t) => t.parentKey === serverKey);"""),
            app);
    }

    // AC22 — master and children stay in sync BOTH ways, and the master is ALSO derived from the children at
    // render time. The child-to-master direction is the one worth pinning, in both forms.
    //
    // The render-time half was missed on the first pass and shipped broken for one review cycle, so it is
    // worth saying exactly how it failed rather than only what it does. `GetEnabledToolsAsync` expands
    // `mcp:{id}` AWAY into per-tool keys, and that projection is what the composer renders from on every
    // reopen — so the master drew UNCHECKED above a full set of ticked children (measured:
    // `master:false, children:[true,true]`), and `readToolChecks` then declined to collapse, freezing the
    // selection into an explicit list that will not pick up tools the server adds later. It was invisible in
    // testing because a FRESH conversation seeds from `allToolKeys()`, which does contain `mcp:{id}`.
    [Fact]
    public async Task RenderToolChecks_SyncsMasterFromChildren()
    {
        var app = Collapse(await _fixture.GetAppScriptAsync());

        // master → children
        Assert.Contains(Collapse("for (const box of boxes) box.checked = master.checked;"), app);
        // children → master, on change
        Assert.Contains(Collapse("master.checked = boxes.every((b) => b.checked);"), app);
        // ...and children → master at RENDER time, which the change handler cannot cover. Without this the
        // master draws UNCHECKED above a full set of ticked children on every reopen, because
        // GetEnabledToolsAsync expands mcp:{id} AWAY into per-tool keys — measured, and the reason the
        // collapse rule silently stopped firing on existing conversations.
        Assert.Contains(Collapse("if (boxes.every((b) => b.checked)) master.checked = true;"), app);
    }

    // The disclosure must not be a checkbox. readToolChecks collects EVERY checkbox in the container, so a
    // checkbox disclosure would be saved as a phantom key on every Save — one per expanded server.
    [Fact]
    public async Task Disclosure_IsAButton_NotACheckbox()
    {
        var app = Collapse(await _fixture.GetAppScriptAsync());

        Assert.Contains(Collapse("""const toggle = document.createElement("button");"""), app);
        Assert.Contains(Collapse("""toggle.type = "button";"""), app);
        Assert.Contains(Collapse("""toggle.setAttribute("aria-expanded", "false");"""), app);
    }

    // AC21 — the collapse rule: a server whose master is ticked saves as the bare `mcp:{id}`, and its
    // per-tool keys are dropped. That keeps ADR 0026's "all of this server's tools" meaning, including tools
    // the server grows later, rather than freezing today's list into an explicit set.
    //
    // The master is identified by its position (the checkbox inside `.tool-server__head`), never by parsing
    // its key — consistent with the rule that no code in this feature splits a key.
    [Fact]
    public async Task ReadToolChecks_CollapsesFullServerToTheServerKey()
    {
        var app = Collapse(await _fixture.GetAppScriptAsync());

        Assert.Contains(
            Collapse("""if (box.closest(".tool-server__head") !== null) collapsedServers.push(box.value);"""),
            app);
        Assert.Contains(
            Collapse("const collapse = (list) => list.filter((k) => !collapsedServers.some((s) => k.startsWith(`${s}:`)));"),
            app);
    }

    // AC20 — the preservation rule AND the per-container stash it depends on. Two things are pinned here.
    //
    // First, that unrendered per-tool keys are carried forward at all: the picker cannot show a checkbox for
    // a tool it does not know about, and a Save must never delete a selection the user was never given the
    // chance to see. This is the backstop for the one path the server-side rule cannot cover — a server that
    // legitimately drops to ZERO tools after having had some.
    //
    // Second, that the stash is keyed on the CONTAINER. renderToolChecks/readToolChecks serve three
    // containers (profileToolChecks, agentToolChecks, chatToolChecks) plus a round-trip in
    // refreshToolCatalog. A module-level single slot would be correct only under an unstated "every read
    // follows that container's own render" invariant across three dialogs, and would fail silently the day
    // that ordering changed — while an assertion that only checked for preservation still passed. So the
    // WeakMap is asserted explicitly, not merely implied.
    [Fact]
    public async Task ReadToolChecks_PreservesKeysForUnrenderedServers()
    {
        var app = Collapse(await _fixture.GetAppScriptAsync());

        // The stash is per container, and weak so a detached container is collectable.
        Assert.Contains(Collapse("const unrenderedKeys = new WeakMap();"), app);
        Assert.Contains(
            Collapse("const serverRowKeys = new Set(mcpServers.map((t) => t.key));"),
            app);
        // Scoped to keys whose SERVER ROW is present: a key for a deleted or management-disabled server must
        // still drop on the next Save, or stored selections grow forever and never shed dead keys — which
        // would quietly falsify ToolCatalog.Sanitize's "a stale key self-heals" doc comment.
        Assert.Contains(
            Collapse("selectedKeys.filter((k) => !rendered.has(k) && [...serverRowKeys].some((s) => k.startsWith(`${s}:`)))"),
            app);

        // ...and read back for THAT container, never a shared slot.
        Assert.Contains(Collapse("const preserved = unrenderedKeys.get(container) ?? [];"), app);
        Assert.Contains(Collapse("return [...collapse(keys), ...collapse(preserved)];"), app);
    }

    // The cold-start affordance: a server with no discovered tools renders an ordinary checkbox and says so,
    // rather than an empty disclosure that looks like a server with no tools at all.
    [Fact]
    public async Task ServerWithoutKnownTools_RendersAHint_AndNoDisclosure()
    {
        var app = Collapse(await _fixture.GetAppScriptAsync());

        Assert.Contains(Collapse("if (tools.length === 0) {"), app);
        Assert.Contains(Collapse("""hint.textContent = "tools not discovered yet";"""), app);
    }

    private static string Collapse(string source) =>
        System.Text.RegularExpressions.Regex.Replace(source, @"\s+", " ").Trim();
}
