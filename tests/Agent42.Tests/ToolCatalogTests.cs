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

    // ── Calendar is GONE (ADR 0063, supersedes 0053) ────────────────────────────────────────────────

    // AC15. The six tools moved to a standalone MCP server outside this repo, so nothing here may still
    // claim to gate them. This is deliberately an assertion about the CATALOG, not about the deleted
    // folders: ProjectLayoutTests already pins the folder sets, and a branch that deleted both Calendar/
    // folders while leaving the six ToolNameToKey rows or the `calendar` descriptor behind would satisfy
    // every structural criterion in this suite and still ship a key that gates nothing. The calendar is
    // reached now the way any other remote MCP server is (ADR 0024), under its own `mcp:{id}` key.
    [Fact]
    public void CalendarKeyAndItsToolNames_AreGoneFromTheCatalog()
    {
        Assert.DoesNotContain("calendar", ToolCatalog.AllSelectableToolKeys);
        Assert.DoesNotContain(ToolCatalog.Descriptors, d => d.Key == "calendar");

        foreach (var tool in new[]
                 {
                     "list_calendars", "list_calendar_events", "get_calendar_event",
                     "create_calendar_event", "update_calendar_event", "delete_calendar_event",
                 })
        {
            Assert.Null(ToolCatalog.KeyFor(tool));
        }
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
    // The no-migration half is NOT asserted here on purpose. Memory owns no tool name, so a catalog-level
    // version of it degenerates into asserting that a hardcoded literal set lacks a string — a tautology
    // over test data, not over the feature. (The calendar key's equivalent test lived here until ADR 0063
    // deleted that key.) It is proved at grain level instead, by LegacyAgentSnapshot_DoesNotEnableMemory.
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

    // AC8 (preferences redesign) — the picker renders a description line under each built-in's label, and the
    // copy is server-owned so it cannot drift away from the catalog it describes. Every STATIC descriptor must
    // carry one: the per-user skill and MCP descriptors appended by GET /api/tools leave Description null (they
    // are user- and server-supplied names), and those are unreachable from here — ToolCatalog.Descriptors is
    // exactly the static set, which is why this assertion can be unconditional.
    [Fact]
    public void EveryStaticDescriptor_HasADescription()
    {
        Assert.NotEmpty(ToolCatalog.Descriptors);

        foreach (var descriptor in ToolCatalog.Descriptors)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(descriptor.Description),
                $"Built-in tool '{descriptor.Key}' has no description; the picker would render it label-only.");
        }
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

    // ── Client tools and shared state (ADR 0089) ───────────────────────────────────────────────────

    // AC9 — both keys were appended (order stability for every existing key), right before past_chats (ADR 0112)
    // and fleet (ADR 0115) which came after them; each tool maps to its key, and both are rendered checkboxes.
    [Fact]
    public void BrowserToolsAndSharedState_AreAppendedLast_AndMapTheirTools()
    {
        Assert.Equal(
            [ToolCatalog.BrowserTools, ToolCatalog.SharedState, ToolCatalog.PastChats, ToolCatalog.Fleet, ToolCatalog.FleetAnalyzer],
            ToolCatalog.AllSelectableToolKeys.TakeLast(5));
        Assert.Equal(
            [ToolCatalog.BrowserTools, ToolCatalog.SharedState, ToolCatalog.PastChats, ToolCatalog.Fleet, ToolCatalog.FleetAnalyzer],
            ToolCatalog.Descriptors.TakeLast(5).Select(d => d.Key));

        foreach (var tool in BrowserToolNames)
        {
            Assert.Equal(ToolCatalog.BrowserTools, ToolCatalog.KeyFor(tool));
        }

        Assert.Equal(ToolCatalog.SharedState, ToolCatalog.KeyFor(SharedStateLimits.UpdateToolName));
    }

    // AC10 (ADR 0098): read_document is inserted just before browser_tools, in both lists, with a description.
    [Fact]
    public void ReadDocument_SitsBeforeBrowserTools_AndIsDescribed()
    {
        // Counted from the end, so each key appended after it (fleet, the analyzer) moves it one further in.
        Assert.Equal(ToolCatalog.ReadDocument, ToolCatalog.AllSelectableToolKeys[^6]);
        var descriptor = ToolCatalog.Descriptors[^6];
        Assert.Equal(ToolCatalog.ReadDocument, descriptor.Key);
        Assert.False(descriptor.RequiresHostedTools);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Description));
    }

    // AC9 — ON for the null (== all) selection, OFF for a selection saved before the keys existed (ADR 0021).
    [Fact]
    public void BrowserToolsAndSharedState_AreOnForNull_AndOffForAnOlderSavedSelection()
    {
        var all = ToolCatalog.AllSelectableToolKeys.ToHashSet();
        var saved = new HashSet<string> { ToolCatalog.WebSearch, ToolCatalog.Artifacts };

        foreach (var tool in BrowserToolNames.Append(SharedStateLimits.UpdateToolName))
        {
            Assert.True(ToolCatalog.IsToolEnabled(tool, all), tool);
            Assert.False(ToolCatalog.IsToolEnabled(tool, saved), tool);
        }
    }

    // ── Past chats (ADR 0112) ──────────────────────────────────────────────────────────────────────────

    // AC21 — appended in both lists (last until fleet, ADR 0115, came after it), described, not a hosted tool, and
    // the key for BOTH tools.
    [Fact]
    public void PastChats_IsSelectable_Described_AndMapsBothTools()
    {
        Assert.Equal(ToolCatalog.PastChats, ToolCatalog.AllSelectableToolKeys[^3]);
        var descriptor = ToolCatalog.Descriptors[^3];
        Assert.Equal(ToolCatalog.PastChats, descriptor.Key);
        Assert.False(descriptor.RequiresHostedTools);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Description));

        Assert.Equal(ToolCatalog.PastChats, ToolCatalog.KeyFor(PastChatLimits.SearchToolName));
        Assert.Equal(ToolCatalog.PastChats, ToolCatalog.KeyFor(PastChatLimits.ReadToolName));
    }

    // AC22 — ON for the null (== all) selection, OFF for a selection saved before the key existed (ADR 0021).
    [Fact]
    public void PastChats_IsOnForNull_AndOffForAnOlderSavedSelection()
    {
        var all = ToolCatalog.AllSelectableToolKeys.ToHashSet();
        var saved = new HashSet<string> { ToolCatalog.WebSearch, ToolCatalog.Memory };

        foreach (var tool in new[] { PastChatLimits.SearchToolName, PastChatLimits.ReadToolName })
        {
            Assert.True(ToolCatalog.IsToolEnabled(tool, all), tool);
            Assert.False(ToolCatalog.IsToolEnabled(tool, saved), tool);
        }
    }

    // ── Fleet (ADR 0115) ───────────────────────────────────────────────────────────────────────────────

    // AC11 — appended LAST in both lists, described, not a hosted tool, and the key for BOTH tools.
    [Fact]
    public void Fleet_IsAppendedLast_Described_AndMapsBothTools()
    {
        // The analyzer (ADR 0122) came after it, so fleet is now second to last in both lists.
        Assert.Equal(ToolCatalog.Fleet, ToolCatalog.AllSelectableToolKeys[^2]);
        var descriptor = ToolCatalog.Descriptors[^2];
        Assert.Equal(ToolCatalog.Fleet, descriptor.Key);
        Assert.False(descriptor.RequiresHostedTools);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Description));

        Assert.Equal(ToolCatalog.Fleet, ToolCatalog.KeyFor(FleetToolLimits.StartToolName));
        Assert.Equal(ToolCatalog.Fleet, ToolCatalog.KeyFor(FleetToolLimits.GetToolName));
    }

    // AC11 — ON for the null (== all) selection, OFF for a selection saved before the key existed (ADR 0021).
    [Fact]
    public void Fleet_IsOnForNull_AndOffForAnOlderSavedSelection()
    {
        var all = ToolCatalog.AllSelectableToolKeys.ToHashSet();
        var saved = new HashSet<string> { ToolCatalog.WebSearch, ToolCatalog.PastChats };

        foreach (var tool in new[] { FleetToolLimits.StartToolName, FleetToolLimits.GetToolName })
        {
            Assert.True(ToolCatalog.IsToolEnabled(tool, all), tool);
            Assert.False(ToolCatalog.IsToolEnabled(tool, saved), tool);
        }
    }

    // ADR 0122 — the analyzer is appended LAST, described, not hosted, the key for ALL eight tools, ON for null and OFF
    // for a selection saved before it existed.
    [Fact]
    public void FleetAnalyzer_IsAppendedLast_MapsAllTools_AndIsOffForAnOlderSavedSelection()
    {
        Assert.Equal(ToolCatalog.FleetAnalyzer, ToolCatalog.AllSelectableToolKeys[^1]);
        var descriptor = ToolCatalog.Descriptors[^1];
        Assert.Equal(ToolCatalog.FleetAnalyzer, descriptor.Key);
        Assert.False(descriptor.RequiresHostedTools);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Description));

        var all = ToolCatalog.AllSelectableToolKeys.ToHashSet();
        var saved = new HashSet<string> { ToolCatalog.WebSearch, ToolCatalog.PastChats, ToolCatalog.Fleet };
        Assert.Equal(8, FleetAnalyzerLimits.ToolNames.Count);
        foreach (var tool in FleetAnalyzerLimits.ToolNames)
        {
            Assert.Equal(ToolCatalog.FleetAnalyzer, ToolCatalog.KeyFor(tool));
            Assert.True(ToolCatalog.IsToolEnabled(tool, all), tool);
            Assert.False(ToolCatalog.IsToolEnabled(tool, saved), tool);
        }
    }

    // The client tools under browser_tools: every one except get_current_location, which is a client tool (ADR
    // 0093) but keeps its own location key (Location_IsAClientTool_ButKeepsItsOwnKey).
    private static IEnumerable<string> BrowserToolNames
        => ClientToolLimits.ToolNames.Where(t => t != ClientToolLimits.GetCurrentLocation);

    // AC8 (ADR 0093) — get_current_location became a client tool but did NOT move to browser_tools: that would
    // turn location OFF for everyone who ever pressed Save (ADR 0021). It stays on its own key, ON for an older
    // saved selection that includes that key.
    [Fact]
    public void Location_IsAClientTool_ButKeepsItsOwnKey()
    {
        Assert.Contains(ClientToolLimits.GetCurrentLocation, ClientToolLimits.ToolNames);
        Assert.Equal(ToolCatalog.Location, ToolCatalog.KeyFor(ClientToolLimits.GetCurrentLocation));

        var saved = new HashSet<string> { ToolCatalog.WebSearch, ToolCatalog.Location };
        Assert.True(ToolCatalog.IsToolEnabled(ClientToolLimits.GetCurrentLocation, saved));
        Assert.False(ToolCatalog.IsToolEnabled(
            ClientToolLimits.GetCurrentLocation, new HashSet<string> { ToolCatalog.WebSearch }));
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
