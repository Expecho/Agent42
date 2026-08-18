namespace Agent42.Agents.Tools;

/// <summary>
/// The curated catalog of user-selectable tool groups (ADR 0021). One logical <b>key</b> maps to one or
/// more registered tools; this is the <b>single source of truth</b> for which tools a selection can turn on
/// or off, shared by the grain's <c>BuildAgent</c> filter and the <c>GET /api/tools</c> catalog endpoint.
///
/// <para><b>Always-on, never selectable:</b> <c>get_date</c> and the document RAG <c>TextSearchProvider</c>.
/// <c>get_date</c> is filtered in as always-enabled here; the RAG provider is not a tool and is gated only by
/// document presence (ADR 0008), so it never appears in this catalog.</para>
///
/// <para><b>Capability is the outer gate.</b> This catalog only expresses <i>user intent</i>. Whether a
/// selected tool is actually registered is still decided by the model's <see cref="ModelCapabilities"/> in
/// <c>BuildAgent</c> (a text-only model gets none; <c>code_interpreter</c> needs a hosted-tools model). The
/// intersection — selection ∩ capability — keeps the ADR 0013/0015 gates authoritative.</para>
/// </summary>
public static class ToolCatalog
{
    // Logical keys — the selectable set. get_date is deliberately NOT here (always-on, see AlwaysOnDateTool).
    public const string WebSearch = "web_search";
    public const string Location = "location";
    public const string Scheduling = "scheduling";
    public const string Skills = "skills";
    public const string CodeInterpreter = "code_interpreter";

    /// <summary>
    /// The remote-MCP-server <b>wildcard</b> ("all servers") meta-key (ADR 0026, amends 0024). Each MCP server
    /// is now individually selectable as a per-server key <c>mcp:{serverId}</c> (see <see cref="McpServerKey"/>);
    /// this bare <c>mcp</c> key is the wildcard the grain honours in <c>BuildAgent</c>
    /// (<c>enabled.Contains(Mcp) || enabled.Contains(mcp:{id})</c>). It is deliberately <b>kept in</b>
    /// <see cref="AllSelectableToolKeys"/> (so the <c>null == all</c> default and any legacy stored selection
    /// carrying literal <c>"mcp"</c> keep enabling every server, with no migration) but <b>removed from</b>
    /// <see cref="Descriptors"/> (it is no longer a single UI checkbox — the picker renders one box per server).
    /// Like the per-server keys, it maps to NO static tool names (MCP tool names are user-defined and dynamic),
    /// so it is absent from <see cref="ToolNameToKey"/>; MCP tools stay the documented group-gated exception to
    /// the "every registered tool maps to a catalog key" guard.
    /// </summary>
    public const string Mcp = "mcp";

    /// <summary>The prefix of a per-server MCP selection key: <c>mcp:{serverId}</c> (ADR 0026).</summary>
    public const string McpServerKeyPrefix = "mcp:";

    /// <summary>The always-on date tool that no selection can disable.</summary>
    public const string AlwaysOnDateTool = "get_date";

    /// <summary>
    /// Every selectable key, in display order. This is the <c>null == all</c> fallback set: a null
    /// <c>EnabledTools</c> anywhere (user / agent / conversation) resolves to exactly this, preserving
    /// today's "every tool on" behaviour with no migration.
    /// </summary>
    public static IReadOnlyList<string> AllSelectableToolKeys { get; } =
        [WebSearch, Location, Scheduling, Skills, CodeInterpreter, Mcp];

    // Maps a REGISTERED tool's Name to its logical key. get_date is deliberately absent (always-on).
    // web_search is intentionally the key for BOTH the SearXNG shims and HostedWebSearchTool — the hosted
    // tool surfaces under the same protocol name `web_search` (ADR 0015), so one key controls both wire
    // realizations. The three AgentSkillsProvider tools (load_skill / read_skill_resource / run_skill_script)
    // are mapped so the key-coverage guard test stays green wherever the provider surfaces them; the provider
    // itself is gated by the Skills arm in BuildAgent, not by this map (they never enter the filtered list).
    private static readonly Dictionary<string, string> ToolNameToKey = new(StringComparer.Ordinal)
    {
        [WebSearch] = WebSearch,
        ["fetch_web_page"] = WebSearch,
        [CodeInterpreter] = CodeInterpreter,
        ["get_current_location"] = Location,
        ["schedule_prompt"] = Scheduling,
        ["cancel_scheduled_prompt"] = Scheduling,
        ["cancel_all_scheduled_prompts"] = Scheduling,
        ["load_skill"] = Skills,
        ["read_skill_resource"] = Skills,
        ["run_skill_script"] = Skills,
    };

    /// <summary>
    /// The UI-facing catalog (ADR 0021): each selectable key with a label and whether it needs a
    /// hosted-tools model, so the picker can grey out <c>code_interpreter</c> on a non-hosted model.
    /// </summary>
    public static IReadOnlyList<ToolDescriptor> Descriptors { get; } =
    [
        new(WebSearch, "Web search", RequiresHostedTools: false),
        new(Location, "Location", RequiresHostedTools: false),
        new(Scheduling, "Scheduling", RequiresHostedTools: false),
        new(Skills, "Skills", RequiresHostedTools: false),
        new(CodeInterpreter, "Code interpreter", RequiresHostedTools: true),
        // Mcp is NOT here — MCP servers surface as per-user, per-server descriptors appended by GET /api/tools
        // (ADR 0026). The bare `mcp` wildcard is a selection sentinel only, never a rendered checkbox.
    ];

    /// <summary>The logical key for a registered tool name, or null (e.g. <c>get_date</c>, or an unmapped tool).</summary>
    public static string? KeyFor(string toolName) => ToolNameToKey.TryGetValue(toolName, out var key) ? key : null;

    /// <summary>The per-server selection key for an MCP server id: <c>mcp:{serverId}</c> (ADR 0026).</summary>
    public static string McpServerKey(string serverId) => McpServerKeyPrefix + serverId;

    /// <summary>True when a key is a per-server MCP key (<c>mcp:{id}</c>), not the bare <c>mcp</c> wildcard.</summary>
    public static bool IsMcpServerKey(string key) => key.StartsWith(McpServerKeyPrefix, StringComparison.Ordinal);

    /// <summary>
    /// True when a server's tools should be kept given the effective selection (ADR 0026): the selection
    /// contains either the bare <see cref="Mcp"/> wildcard (the <c>null == all</c> default or a legacy
    /// all-servers selection) OR this server's own <c>mcp:{serverId}</c> key. Capability (<c>SupportsTools</c>)
    /// is the outer gate applied by the caller, exactly as for the static keys.
    /// </summary>
    public static bool IsMcpServerEnabled(string serverId, ISet<string> enabled)
        => enabled.Contains(Mcp) || enabled.Contains(McpServerKey(serverId));

    /// <summary>
    /// True when a tool with this registered name should be kept given the effective selection.
    /// <c>get_date</c> is always kept; every other tool is kept only if its key is in <paramref name="enabled"/>.
    /// A tool whose name is neither <c>get_date</c> nor in the catalog is kept too — an unmapped tool is not a
    /// selectable one and must not be silently stripped (that would be an unnoticed regression).
    /// </summary>
    public static bool IsToolEnabled(string toolName, ISet<string> enabled)
        => KeyFor(toolName) is not { } key || enabled.Contains(key);

    /// <summary>
    /// Server-side authority over a stored selection: drops unknown keys and de-dups, preserving catalog order.
    /// Null passes through unchanged (the <c>null == all</c> sentinel). Called by the grain before persisting a
    /// selection so state can never hold a garbage or client-invented key.
    ///
    /// <para>Per-server MCP keys (<c>mcp:{id}</c>, ADR 0026) are passed through after the static keys, in input
    /// order, deduped — Sanitize is deliberately <b>static</b> (no per-user server list), so it cannot validate
    /// a server id here. A stale <c>mcp:{deletedId}</c> is inert (it gates no server in <c>BuildAgent</c>) and
    /// self-heals when the user next saves from the picker, which only offers current servers.</para>
    /// </summary>
    public static IReadOnlyList<string>? Sanitize(IReadOnlyList<string>? tools)
        => tools is null
            ? null
            : [.. AllSelectableToolKeys.Where(tools.Contains), .. tools.Where(IsMcpServerKey).Distinct()];
}

/// <summary>
/// One entry in the selectable-tools catalog surfaced by <c>GET /api/tools</c> (ADR 0021):
/// the logical key, a display label, and whether the tool requires a hosted-tools model.
/// </summary>
public sealed record ToolDescriptor(string Key, string Label, bool RequiresHostedTools);
