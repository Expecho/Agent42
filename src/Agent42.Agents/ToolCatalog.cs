namespace Agent42.Agents;

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

    /// <summary>The always-on date tool that no selection can disable.</summary>
    public const string AlwaysOnDateTool = "get_date";

    /// <summary>
    /// Every selectable key, in display order. This is the <c>null == all</c> fallback set: a null
    /// <c>EnabledTools</c> anywhere (user / agent / conversation) resolves to exactly this, preserving
    /// today's "every tool on" behaviour with no migration.
    /// </summary>
    public static IReadOnlyList<string> AllSelectableToolKeys { get; } =
        [WebSearch, Location, Scheduling, Skills, CodeInterpreter];

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
    ];

    /// <summary>The logical key for a registered tool name, or null (e.g. <c>get_date</c>, or an unmapped tool).</summary>
    public static string? KeyFor(string toolName) => ToolNameToKey.TryGetValue(toolName, out var key) ? key : null;

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
    /// </summary>
    public static IReadOnlyList<string>? Sanitize(IReadOnlyList<string>? tools)
        => tools is null
            ? null
            : [.. AllSelectableToolKeys.Where(tools.Contains)];
}

/// <summary>
/// One entry in the selectable-tools catalog surfaced by <c>GET /api/tools</c> (ADR 0021):
/// the logical key, a display label, and whether the tool requires a hosted-tools model.
/// </summary>
public sealed record ToolDescriptor(string Key, string Label, bool RequiresHostedTools);
