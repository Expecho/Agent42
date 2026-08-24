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
}
