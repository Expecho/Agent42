using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The selectable-tools catalog contract around per-server MCP keys (ADR 0026, amends 0021/0024). The
/// load-bearing invariant is the wildcard: the bare <c>mcp</c> key must stay in <see cref="ToolCatalog.AllSelectableToolKeys"/>
/// (so <c>null == all</c> and legacy selections keep enabling every server, no migration) yet be absent from
/// <see cref="ToolCatalog.Descriptors"/> (it is no longer a rendered checkbox), and <see cref="ToolCatalog.Sanitize"/>
/// must preserve per-server <c>mcp:{id}</c> keys it would otherwise drop.
/// </summary>
public class ToolCatalogTests
{
    [Fact] // AC6 — the wildcard is a selection sentinel, not a UI checkbox.
    public void Mcp_Wildcard_IsInAllSelectableKeys_ButNotInDescriptors()
    {
        Assert.Contains(ToolCatalog.Mcp, ToolCatalog.AllSelectableToolKeys);
        Assert.DoesNotContain(ToolCatalog.Descriptors, d => d.Key == ToolCatalog.Mcp);
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
