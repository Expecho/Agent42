using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// Which tools, skills and MCP tools a conversation's selection grants (ADR 0021/0026/0048/0049), resolved from a
/// <see cref="ToolSelectionContext"/> on every call (ADR 0106). STATELESS on purpose: the inputs change at activation,
/// bind, MCP re-resolve, <c>SetEnabledToolsAsync</c> and fork, and a cached selection missing one of those would leave
/// a tool — or memory's egress — on after the user turned it off. The grain still binds the tool functions.
/// </summary>
public interface IToolSelection
{
    /// <summary>
    /// The three-layer resolution: the conversation's override or agent snapshot, else the user's default, else all
    /// selectable keys (the <c>null == all</c> sentinel). INTENT only: for tools it is still intersected with model
    /// capability.
    /// </summary>
    IReadOnlyList<string> EffectiveEnabledTools(ToolSelectionContext context);

    /// <summary>The effective selection with both wildcards (<c>mcp</c>, <c>skills</c>) expanded, for the picker.</summary>
    IReadOnlyList<string> ExpandedEnabledTools(ToolSelectionContext context);

    /// <summary>The shipped skill catalog overlaid with the user's uploads (an upload wins on a name clash).</summary>
    IReadOnlyList<SkillInfo> MergedSkills(ToolSelectionContext context);

    /// <summary>The skills that pass the per-skill selection, or none on a no-tools model.</summary>
    List<SkillInfo> SelectedSkills(ToolSelectionContext context, ModelCapabilities capabilities, ISet<string> enabled);

    /// <summary>Whether a built-in tool's catalog key is selected (a tool with no key is always kept).</summary>
    bool IsSelected(string toolName, ISet<string> enabled);

    /// <summary>
    /// The selected MCP tools, per server in stored order and per tool sorted by name (ADR 0104), or none on a
    /// no-tools model. Unwrapped: the grain applies its per-conversation wrappers.
    /// </summary>
    IEnumerable<(McpServerTools Server, AITool Tool)> SelectedMcpTools(
        ToolSelectionContext context, ModelCapabilities capabilities, ISet<string> enabled);
}

/// <summary>
/// The selection inputs, built fresh by the grain at each use: the conversation's persisted selection, the user's
/// default (read on activation), and the user's MCP servers and uploaded skills (resolved on activation).
/// </summary>
public sealed record ToolSelectionContext(
    IReadOnlyList<string>? ConversationTools,
    IReadOnlyList<string>? UserDefaultTools,
    IReadOnlyList<McpServerTools> McpServers,
    IReadOnlyList<SkillInfo> UserSkills);
