using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The selectable-tools catalog contract around the two wildcard keys — per-server MCP (<c>mcp</c>, ADR 0026)
/// and per-skill (<c>skills</c>, per-skill selectable tools; both amend 0021). The load-bearing invariant is
/// the same for each: the bare key must stay in <see cref="ToolCatalog.AllSelectableToolKeys"/> (so
/// <c>null == all</c> and legacy selections keep enabling everything, no migration) yet be absent from
/// <see cref="ToolCatalog.Descriptors"/> (it is no longer a rendered checkbox), and
/// <see cref="ToolCatalog.Sanitize"/> must preserve the per-entry <c>mcp:{id}</c> / <c>skill:{name}</c> keys
/// it would otherwise drop.
/// </summary>
public class ToolCatalogTests
{
    [Fact] // AC6 — the wildcard is a selection sentinel, not a UI checkbox.
    public void Mcp_Wildcard_IsInAllSelectableKeys_ButNotInDescriptors()
    {
        Assert.Contains(ToolCatalog.Mcp, ToolCatalog.AllSelectableToolKeys);
        Assert.DoesNotContain(ToolCatalog.Descriptors, d => d.Key == ToolCatalog.Mcp);
    }

    [Fact] // AC8 — the skills wildcard is a selection sentinel, not a UI checkbox.
    public void Skills_Wildcard_IsInAllSelectableKeys_ButNotInDescriptors()
    {
        Assert.Contains(ToolCatalog.Skills, ToolCatalog.AllSelectableToolKeys);
        Assert.DoesNotContain(ToolCatalog.Descriptors, d => d.Key == ToolCatalog.Skills);
    }

    // ── Calendar (ADR 0053) ─────────────────────────────────────────────────────────────────────────

    [Fact] // AC10 — one key gates all six calendar tools, and it IS a rendered checkbox (unlike the wildcards).
    public void Calendar_IsSelectable_AndGatesItsTools()
    {
        Assert.Contains(ToolCatalog.Calendar, ToolCatalog.AllSelectableToolKeys);
        Assert.Contains(ToolCatalog.Descriptors, d => d.Key == ToolCatalog.Calendar);
        Assert.False(
            ToolCatalog.Descriptors.Single(d => d.Key == ToolCatalog.Calendar).RequiresHostedTools,
            "The calendar tools are ordinary function tools — they do not need a hosted-tools model.");

        string[] tools =
        [
            "list_calendars", "list_calendar_events", "get_calendar_event",
            "create_calendar_event", "update_calendar_event", "delete_calendar_event",
        ];

        var off = new HashSet<string>(ToolCatalog.AllSelectableToolKeys.Where(k => k != ToolCatalog.Calendar));
        var on = new HashSet<string>(ToolCatalog.AllSelectableToolKeys);

        foreach (var tool in tools)
        {
            Assert.Equal(ToolCatalog.Calendar, ToolCatalog.KeyFor(tool));
            Assert.False(ToolCatalog.IsToolEnabled(tool, off), $"{tool} must be gated by the calendar key.");
            Assert.True(ToolCatalog.IsToolEnabled(tool, on), $"{tool} must be enabled by the calendar key.");
        }
    }

    // AC17. Derived decision (l), stated as a test because the behaviour looks like a bug and is not:
    // `null == all` only helps a user who has NEVER narrowed a selection. The picker materialises "all"
    // as the full EXPLICIT list, so anyone who has ever pressed Save in Preferences → Tools holds a
    // non-null selection that cannot contain `calendar` — the key did not exist when they saved. There is
    // deliberately no migration; the remedy is one more press of Save, and it is documented.
    [Fact]
    public void LegacyStoredSelection_DoesNotEnableCalendar_ButNullDoes()
    {
        // What a Save looked like BEFORE this feature shipped: every key that existed then, explicitly.
        var legacy = new HashSet<string>(
            ["web_search", "location", "scheduling", "skills", "code_interpreter", "mcp"]);

        Assert.False(
            ToolCatalog.IsToolEnabled("list_calendar_events", legacy),
            "A selection saved before the calendar key existed cannot contain it — that is the ADR 0048 "
                + "divergence, and it is why the MR says to press Save once after deploying.");

        // …while a user who never narrowed anything resolves through the null == all fallback and DOES
        // get it. Both halves matter: the first is the trap, the second is why there is no migration.
        var never = new HashSet<string>(ToolCatalog.AllSelectableToolKeys);
        Assert.True(ToolCatalog.IsToolEnabled("list_calendar_events", never));
    }

    // ── Long-term memory (ADR 0057) ─────────────────────────────────────────────────────────────────

    // AC1 — `memory` is a selectable key AND a rendered checkbox, labelled so it cannot be misread as the
    // conversation's own history. It is the FIRST key here that is not a tool: it gates an
    // AIContextProvider, so it deliberately owns no ToolNameToKey entry and must never gain one. There is
    // no reverse guard requiring a descriptor to own tool names — the only guard over that map runs
    // name → key — which is what makes the absence safe rather than merely untested.
    //
    // The KeyFor assertion below is deliberately weaker than that paragraph, and saying so is the point:
    // ToolNameToKey is private and only readable name → key, so this proves no tool is *named* "memory" —
    // it would stay green if someone added ["recall_memory"] = Memory. The "owns no entry" property is
    // not assertable through the public surface; it is held by the docstring on the const and by review.
    //
    // The no-migration half is NOT asserted here on purpose. Memory owns no tool name, so the shape of
    // LegacyStoredSelection_DoesNotEnableCalendar_ButNullDoes above degenerates into asserting that a
    // hardcoded literal set lacks a string — a tautology over test data, not over the feature. It is
    // proved at grain level instead, by LegacyAgentSnapshot_DoesNotEnableMemory.
    [Fact]
    public void Memory_IsSelectable_AndAppearsInDescriptors()
    {
        Assert.Contains(ToolCatalog.Memory, ToolCatalog.AllSelectableToolKeys);

        var descriptor = Assert.Single(ToolCatalog.Descriptors, d => d.Key == ToolCatalog.Memory);
        Assert.Equal("Long-term memory", descriptor.Label);
        Assert.False(
            descriptor.RequiresHostedTools,
            "Memory is not a tool at all, so no model capability can grey it out.");

        Assert.Null(
            ToolCatalog.KeyFor(ToolCatalog.Memory));
    }

    [Fact] // AC7 — a per-skill key round-trips through Sanitize, deduped; garbage is dropped.
    public void Sanitize_PreservesPerSkillKeys_AndDropsGarbage()
    {
        var result = ToolCatalog.Sanitize(
            [ToolCatalog.WebSearch, "not-a-real-tool", "skill:websearch", "skill:scheduledprompt", "skill:websearch"]);

        Assert.Equal(
            [ToolCatalog.WebSearch, "skill:websearch", "skill:scheduledprompt"],
            result);
    }

    [Fact] // AC7 — static keys first, then per-skill, then per-server; the two prefixes never collide.
    public void Sanitize_OrdersStaticFirst_ThenPerSkill_ThenPerServer()
    {
        var result = ToolCatalog.Sanitize(
            ["mcp:s1", "skill:websearch", ToolCatalog.Skills, ToolCatalog.WebSearch]);

        // `skills` is a static key (it survives, in catalog order) and is NOT a `skill:` key — the bare
        // wildcard must not be swept into the per-skill passthrough.
        Assert.Equal(
            [ToolCatalog.WebSearch, ToolCatalog.Skills, "skill:websearch", "mcp:s1"],
            result);
    }

    [Fact] // AC2/AC4 — a skill is enabled by the wildcard OR its own key, and by neither otherwise.
    public void IsSkillEnabled_HonoursWildcardAndPerSkillKey()
    {
        Assert.True(ToolCatalog.IsSkillEnabled("websearch", new HashSet<string> { ToolCatalog.Skills }));
        Assert.True(ToolCatalog.IsSkillEnabled("websearch", new HashSet<string> { "skill:websearch" }));
        Assert.False(ToolCatalog.IsSkillEnabled("websearch", new HashSet<string> { "skill:scheduledprompt" }));
        Assert.False(ToolCatalog.IsSkillEnabled("websearch", new HashSet<string> { ToolCatalog.WebSearch }));
    }

    [Fact] // AC14 — the key helpers agree on the skill:{name} shape.
    public void SkillKey_And_IsSkillKey_Agree()
    {
        var key = ToolCatalog.SkillKey("websearch");

        Assert.Equal("skill:websearch", key);
        Assert.True(ToolCatalog.IsSkillKey(key));
        Assert.False(ToolCatalog.IsSkillKey(ToolCatalog.Skills)); // bare wildcard is not a per-skill key
        Assert.False(ToolCatalog.IsSkillKey(ToolCatalog.WebSearch));
    }

    [Fact] // AC6 — a per-server key round-trips through Sanitize; garbage is dropped.
    public void Sanitize_PreservesPerServerKeys_AndDropsGarbage()
    {
        var result = ToolCatalog.Sanitize(
            [ToolCatalog.WebSearch, "not-a-real-tool", "mcp:abc123", "mcp:def456"]);

        Assert.Equal(
            [ToolCatalog.WebSearch, "mcp:abc123", "mcp:def456"],
            result);
    }

    [Fact] // AC6 — static keys keep catalog order; per-server keys follow in input order, deduped.
    public void Sanitize_OrdersStaticFirst_ThenPerServer_Deduped()
    {
        var result = ToolCatalog.Sanitize(
            ["mcp:s1", ToolCatalog.Scheduling, ToolCatalog.WebSearch, "mcp:s1"]);

        Assert.Equal(
            [ToolCatalog.WebSearch, ToolCatalog.Scheduling, "mcp:s1"],
            result);
    }

    [Fact] // AC6 — null passes through unchanged (the null == all sentinel).
    public void Sanitize_Null_StaysNull()
    {
        Assert.Null(ToolCatalog.Sanitize(null));
    }

    [Fact] // AC1/AC2 — a server is enabled by the wildcard OR its own key, and disabled by neither.
    public void IsMcpServerEnabled_HonoursWildcardAndPerServerKey()
    {
        Assert.True(ToolCatalog.IsMcpServerEnabled("s1", new HashSet<string> { ToolCatalog.Mcp }));
        Assert.True(ToolCatalog.IsMcpServerEnabled("s1", new HashSet<string> { "mcp:s1" }));
        Assert.False(ToolCatalog.IsMcpServerEnabled("s1", new HashSet<string> { "mcp:s2" }));
        Assert.False(ToolCatalog.IsMcpServerEnabled("s1", new HashSet<string> { ToolCatalog.WebSearch }));
    }

    [Fact] // AC1 — the key helpers agree on the mcp:{id} shape.
    public void McpServerKey_And_IsMcpServerKey_Agree()
    {
        var key = ToolCatalog.McpServerKey("abc123");

        Assert.Equal("mcp:abc123", key);
        Assert.True(ToolCatalog.IsMcpServerKey(key));
        Assert.False(ToolCatalog.IsMcpServerKey(ToolCatalog.Mcp)); // bare wildcard is not a per-server key
        Assert.False(ToolCatalog.IsMcpServerKey(ToolCatalog.WebSearch));
    }

    // AC1 — a per-tool key is a per-server key plus a segment, so IsMcpServerKey (a prefix test, deliberately
    // not a parser) already admits it. That is the whole reason Sanitize needed no new clause: if this ever
    // fails, per-tool keys are being stripped from every stored selection.
    [Fact]
    public void McpToolKey_IsAlsoAnMcpServerKey()
    {
        var key = ToolCatalog.McpToolKey("abc123", "jira_get_issue");

        Assert.Equal("mcp:abc123:jira_get_issue", key);
        Assert.True(ToolCatalog.IsMcpServerKey(key));
        Assert.StartsWith(ToolCatalog.McpServerKey("abc123"), key, StringComparison.Ordinal);
    }

    // AC2 — the three-level hierarchy: mcp ⊃ mcp:{id} ⊃ mcp:{id}:{tool}. Any level grants the tool; a
    // selection naming a SIBLING tool of the same server does not.
    [Fact]
    public void IsMcpToolEnabled_HonoursAllThreeLevels()
    {
        Assert.True(ToolCatalog.IsMcpToolEnabled("s1", "get_issue", new HashSet<string> { ToolCatalog.Mcp }));
        Assert.True(ToolCatalog.IsMcpToolEnabled("s1", "get_issue", new HashSet<string> { "mcp:s1" }));
        Assert.True(ToolCatalog.IsMcpToolEnabled("s1", "get_issue", new HashSet<string> { "mcp:s1:get_issue" }));

        // A sibling tool's key grants nothing — this is the narrowing the feature exists for.
        Assert.False(ToolCatalog.IsMcpToolEnabled("s1", "delete_issue", new HashSet<string> { "mcp:s1:get_issue" }));
        Assert.False(ToolCatalog.IsMcpToolEnabled("s1", "get_issue", new HashSet<string> { ToolCatalog.WebSearch }));
    }

    // AC3 — a per-tool key is scoped to its own server. Two servers exposing an identically named tool must
    // not cross-grant, which is what makes the serverId part of the key load-bearing rather than decorative.
    [Fact]
    public void IsMcpToolEnabled_DoesNotLeakAcrossServers()
    {
        var enabled = new HashSet<string> { "mcp:s1:search" };

        Assert.True(ToolCatalog.IsMcpToolEnabled("s1", "search", enabled));
        Assert.False(ToolCatalog.IsMcpToolEnabled("s2", "search", enabled));

        // Nor does a per-SERVER key reach another server's tool of the same name.
        Assert.False(ToolCatalog.IsMcpToolEnabled("s2", "search", new HashSet<string> { "mcp:s1" }));
    }

    // AC4 — per-tool keys survive Sanitize (they ride the IsMcpServerKey prefix clause), keep input order,
    // dedupe, and sit after the static keys; garbage is still dropped.
    [Fact]
    public void Sanitize_PreservesPerToolKeys_AndDropsGarbage()
    {
        var result = ToolCatalog.Sanitize(
            ["mcp:s1:beta", ToolCatalog.WebSearch, "not_a_key", "mcp:s1:alpha", "mcp:s1:beta", "mcp:s1"]);

        Assert.Equal(
            [ToolCatalog.WebSearch, "mcp:s1:beta", "mcp:s1:alpha", "mcp:s1"],
            result);
    }
}
