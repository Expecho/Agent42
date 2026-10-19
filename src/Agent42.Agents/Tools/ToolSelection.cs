using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// The stateless tool-selection resolution of <see cref="IToolSelection"/> (ADR 0106). Extracted unchanged from the
/// grain; the remarks on each member are the ones that guarded it there.
/// </summary>
public sealed class ToolSelection : IToolSelection
{
    // A per-conversation override or an agent snapshot wins; else the user's default read on activation; else all
    // selectable tools (the null==all sentinel). ToolCatalog.Memory is the one key exempt from the capability
    // intersection (ADR 0057): it gates a context provider, not a tool, so it never passes through the Tools filter.
    public IReadOnlyList<string> EffectiveEnabledTools(ToolSelectionContext context)
        => context.ConversationTools
            ?? context.UserDefaultTools
            ?? ToolCatalog.AllSelectableToolKeys;

    // Both wildcards are expanded before the effective selection reaches the picker. Each expansion appends
    // its keys at the TAIL, so composing skills-outermost projects a null (all) selection as
    // [static keys..., mcp:{id}..., skill:{name}...]. Note this is NOT the order Sanitize produces
    // (static, then skill:, then mcp:) — the two are independent projections of an unordered set, one for the
    // picker and one for persistence, and nothing depends on them agreeing. What DOES depend on this order:
    // BindAgent_SnapshotsEnabledTools and LegacyState_NullEnabledTools_MeansAll use an order-sensitive
    // Assert.Equal. Both run with zero MCP servers, so they only pin the [static..., skill:{name}...] tail —
    // which is exactly why the mcp:/skill: relative order has no test and this comment has to be right.
    public IReadOnlyList<string> ExpandedEnabledTools(ToolSelectionContext context)
        => ExpandSkillsWildcard(context, ExpandMcpWildcard(context, EffectiveEnabledTools(context)));

    /// <summary>
    /// The skills this conversation can see: the shipped catalog OVERLAID with this user's uploads (ADR
    /// 0049). An upload with the same name as a shipped skill <b>wins</b> — it shadows the built-in
    /// entirely: name, description, guidance and all.
    ///
    /// <para>The comparison is <see cref="StringComparer.OrdinalIgnoreCase"/> even though the framework's own
    /// name rule is lowercase-only. Two names differing only in case are ONE directory on NTFS and TWO on
    /// ext4, and a merge that produced a different set on the two would be the same platform-dependence bug
    /// the shipped catalog's de-dupe tie-break already had to fix — green on Windows, red on Linux. The
    /// surviving entry's name is emitted verbatim, so a shadowing upload also supplies the key's spelling.</para>
    ///
    /// <para>Used by BOTH the provider gate and the <c>skills</c> wildcard expansion, deliberately: the picker
    /// and the gate must agree about which skills exist, or a key the user can tick would gate nothing.</para>
    /// </summary>
    public IReadOnlyList<SkillInfo> MergedSkills(ToolSelectionContext context)
    {
        if (context.UserSkills.Count == 0)
        {
            return SkillCatalog.Skills;
        }

        var shadowed = context.UserSkills.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return
        [
            .. context.UserSkills
                .Concat(SkillCatalog.Skills.Where(s => !shadowed.Contains(s.Name)))
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    // Shared by both compositions: the ChatClientAgent hands their paths to AgentSkillsProvider and a copilot chat
    // hands them to Copilot's native skill loader.
    public List<SkillInfo> SelectedSkills(
        ToolSelectionContext context, ModelCapabilities capabilities, ISet<string> enabled)
        => capabilities.SupportsTools
            ? [.. MergedSkills(context).Where(s => ToolCatalog.IsSkillEnabled(s.Name, enabled))]
            : [];

    public bool IsSelected(string toolName, ISet<string> enabled) => ToolCatalog.IsToolEnabled(toolName, enabled);

    // Gated on their OWN three-level hierarchy — `mcp` (all servers) ⊃ `mcp:{id}` (all tools of one server, ADR 0026)
    // ⊃ `mcp:{id}:{toolName}` (one tool) — not by the catalog map: MCP tool names are user-defined and dynamic, so
    // they are absent from it and IsToolEnabled would keep them UNCONDITIONALLY (the unmapped-name rule), bypassing
    // the picker. The key is built from (server.ServerId, tool.Name); no key is ever parsed. Each server's tools are
    // sorted by name (ADR 0104): ListToolsAsync guarantees no order, and the tool array sits in the cached request
    // prefix, so a reshuffle between activations would cost a full cache miss. Servers keep their stored config
    // order, which only the user changes.
    public IEnumerable<(McpServerTools Server, AITool Tool)> SelectedMcpTools(
        ToolSelectionContext context, ModelCapabilities capabilities, ISet<string> enabled)
    {
        if (!capabilities.SupportsTools)
        {
            return [];
        }

        return context.McpServers.SelectMany(server => server.Tools
            .Where(t => ToolCatalog.IsMcpToolEnabled(server.ServerId, t.Name, enabled))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => (server, t)));
    }

    // Expands the MCP wildcards into the explicit keys the picker actually renders, before the effective
    // selection reaches it (ADR 0026, extended one level down by per-tool selection). TWO levels now:
    //
    //   `mcp`            → mcp:{id}          for every server, then each of those in turn →
    //   `mcp:{id}`       → mcp:{id}:{tool}   for every tool in that server's KnownToolNames
    //
    // WHY this is load-bearing: EffectiveEnabledTools materializes AllSelectableToolKeys (which carries the
    // bare `mcp` wildcard, NOT any mcp:{id}) for every null/all conversation. The picker renders only keys
    // present in the catalog, and `mcp` has no checkbox there — so a picker Save would drop the wildcard and
    // add no per-server key, silently disabling EVERY MCP server. The same argument now applies one level
    // down. The gate (EffectiveEnabledTools, via IsMcpToolEnabled) still honours both raw wildcards, so this
    // stays a read-only, client-facing projection that never changes what is persisted.
    //
    // ⚠ THE ONE RULE THAT MUST NOT BE SIMPLIFIED: a server whose KnownToolNames is empty keeps its bare
    // mcp:{id} key, UNEXPANDED. Expanding it into nothing would emit a master checkbox with zero children, and
    // both master states are then wrong — unchecked drops a stored partial selection and silently disables the
    // server; checked ("all children ticked" being vacuously true over zero children) collapses back to
    // mcp:{id} and silently re-grants tools the user deliberately deselected. That is the exact failure this
    // feature exists to avoid, reproduced by the rule meant to close it.
    // GetEnabledTools_UnknownToolList_KeepsServerKeyIntact pins it.
    //
    // Names come from the GROUP's KnownToolNames — never from live Tools. The catalog endpoint reads the same
    // persisted list, so the picker and this projection cannot disagree; deriving from Tools would diverge the
    // moment a server is briefly unreachable (empty Tools, names intact) and mint keys with no checkbox.
    private static IReadOnlyList<string> ExpandMcpWildcard(ToolSelectionContext context, IReadOnlyList<string> effective)
    {
        var expanded = effective.Contains(ToolCatalog.Mcp)
            ? effective
                .Where(k => k != ToolCatalog.Mcp)
                .Concat(context.McpServers.Select(s => ToolCatalog.McpServerKey(s.ServerId)))
            : effective;

        return [.. expanded.SelectMany(key => ExpandOneServerKey(context, key)).Distinct(StringComparer.Ordinal)];
    }

    // One key's projection: a per-server key becomes its per-tool keys when that server's tools are known,
    // and stays itself otherwise (see the rule above). Anything that is not a per-server key passes through.
    // Extracted rather than nested so ExpandMcpWildcard stays under the cognitive-complexity gate (ADR 0050).
    private static IEnumerable<string> ExpandOneServerKey(ToolSelectionContext context, string key)
    {
        if (!ToolCatalog.IsMcpServerKey(key))
        {
            return [key];
        }

        var group = context.McpServers.FirstOrDefault(s => key == ToolCatalog.McpServerKey(s.ServerId));
        return group?.KnownToolNames is { Count: > 0 } names
            ? names.Select(n => ToolCatalog.McpToolKey(group.ServerId, n))
            : [key];
    }

    // Expands the bare `skills` wildcard into a per-skill key (skill:{name}) for each skill in the MERGED
    // catalog before the effective selection reaches the picker — the exact hazard ExpandMcpWildcard closes
    // for MCP servers, for the exact same reason: EffectiveEnabledTools materializes AllSelectableToolKeys
    // (which carries the bare `skills` wildcard, NOT any skill:{name}) for every null/all conversation, the
    // picker renders only keys present in the catalog, and `skills` has no checkbox there — so a picker Save
    // would drop the wildcard and add no per-skill key, silently disabling EVERY skill. The gate (via
    // IsSkillEnabled) still honours the raw wildcard, so this is a read-only, client-facing projection that
    // never changes what is persisted. It reads the user's uploads (ADR 0049), which is why the context carries them.
    private IReadOnlyList<string> ExpandSkillsWildcard(ToolSelectionContext context, IReadOnlyList<string> effective)
    {
        if (!effective.Contains(ToolCatalog.Skills))
        {
            return effective;
        }

        return
        [
            .. effective
                .Where(k => k != ToolCatalog.Skills)
                .Concat(MergedSkills(context).Select(s => ToolCatalog.SkillKey(s.Name)))
                .Distinct(StringComparer.Ordinal),
        ];
    }
}
