using System.Text.RegularExpressions;
using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// The tools a fleet task's agents can name in their YAML besides the workspace tools and <c>delegate_to_team</c>
/// (ADR 0097): the workspace's document tools when it has documents (ADR 0101, never gated by a selection), then
/// <c>web_search</c>, and the user's own MCP tools. <c>SelectableTools</c> is the
/// grain's and can't be called here, so this is a small catalog of its own. It honours the task's own tool selection,
/// else the user's DEFAULT one (ADR 0099 amending ADR 0097, ADR 0021 <c>null == all</c>), for both, and wraps MCP tools as <c>BuildAgent</c> does: the ADR 0095
/// elicitation wrapper innermost, the ADR 0081 reauth notice outermost. No URL seeding (ADR 0067): the fleet has
/// no <c>read_web_document</c> and, since ADR 0125, no page fetch.
///
/// <para>Since ADR 0144 it also builds the user's personal tools, each fleet-owned and off-grain with its budget on the
/// instance (one per task): skills (<see cref="FleetSkillTools"/>), memory recall (<see cref="FleetMemoryTools"/>), past
/// chats (<see cref="FleetPastChatTools"/>), push (<see cref="FleetPushTools"/>) and <c>generate_image</c>
/// (<see cref="FleetImageTools"/>). Each family has its own gate: the <c>memory</c> key plus the operator's Hindsight client,
/// <see cref="ToolCatalog.IsToolEnabled"/> on the mapped past-chat and push names, <see cref="ToolCatalog.IsSkillEnabled"/>,
/// and for images the capability (an image model exists). Never <c>IsToolEnabled</c> on an unmapped name: it always passes.</para>
/// </summary>
public sealed partial class FleetToolCatalog(
    IGrainFactory grains,
    ILogger<FleetToolCatalog> logger,
    IWebSearch? webSearch = null,
    IUserMcpToolProvider? mcpTools = null,
    IUserSkillProvider? skills = null,
    HindsightIntegration? memory = null,
    IChatSearchIndex? chatSearch = null,
    IPushNotificationSender? push = null)
{
    /// <summary>The names of the personal tools (ADR 0144), reserved even when absent so an MCP tool can't take one.</summary>
    public static readonly IReadOnlyList<string> PersonalToolNames =
    [
        FleetSkillTools.LoadName, ReadSkillResourceTool.Name, "run_skill_script", FleetMemoryTools.Name,
        PastChatLimits.SearchToolName, PastChatLimits.ReadToolName, FleetPushTools.Name, FleetImageTools.Name,
    ];

    /// <summary>
    /// The selected tools for a task whose request is <paramref name="request"/>: <paramref name="selectionOverride"/>
    /// when the task has one (the profile is then not read), else the user's default selection (ADR 0099).
    /// </summary>
    public async Task<IReadOnlyList<AIFunction>> BuildAsync(
        string userId, string request, IReadOnlyList<string>? selectionOverride, IFleetInteraction interaction,
        CancellationToken cancellationToken, IReadOnlyList<AIFunction>? documentTools = null)
        => (await BuildAsync(new FleetToolContext(userId, "", request, selectionOverride, interaction) { DocumentTools = documentTools ?? [] },
            cancellationToken)).Tools;

    /// <summary>
    /// The task's tools and the skills they were built from: Copilot agents get the skill folders for Copilot's own skill
    /// loader (ADR 0144). The selection is the task's override, else the user's default (ADR 0099).
    /// </summary>
    public async Task<FleetToolSet> BuildAsync(FleetToolContext context, CancellationToken cancellationToken)
    {
        var selection = context.SelectionOverride ?? (await grains.GetGrain<IUserProfileGrain>(context.UserId).GetAsync()).EnabledTools;
        var servers = mcpTools is null ? [] : (await mcpTools.GetForUserAsync(context.UserId, cancellationToken)).Servers;
        var enabled = Enabled(selection);
        // A host without the skill provider has no skills at all, shipped ones included. The only companion skill's tool
        // the fleet has is web_search (ADR 0126), and only with a search backend.
        IReadOnlyList<SkillInfo> selectedSkills = skills is null
            ? []
            : FleetSkillTools.Select((await skills.GetForUserAsync(context.UserId, cancellationToken)).Skills, enabled,
                webSearch is null ? new HashSet<string>() : new HashSet<string> { ToolCatalog.WebSearch });
        var personal = PersonalTools(context, enabled, selectedSkills);
        return new(Build(selection, context.Request, servers, context.Interaction, context.DocumentTools, personal), selectedSkills);
    }

    // The personal tools of ADR 0144, each family behind its own gate (see the type's remarks).
    private List<AIFunction> PersonalTools(FleetToolContext context, HashSet<string> enabled, IReadOnlyList<SkillInfo> selectedSkills)
    {
        var tools = new List<AIFunction>(new FleetSkillTools(selectedSkills).Tools);
        if (memory?.Client is { } client && enabled.Contains(ToolCatalog.Memory))
        {
            tools.Add(new FleetMemoryTools(client, context.UserId, logger).Tool);
        }

        if (chatSearch is not null && ToolCatalog.IsToolEnabled(PastChatLimits.SearchToolName, enabled))
        {
            tools.AddRange(new FleetPastChatTools(chatSearch, grains, context.UserId, logger).Tools);
        }

        if (push is not null && ToolCatalog.IsToolEnabled(FleetPushTools.Name, enabled))
        {
            tools.Add(new FleetPushTools(push, grains, context.UserId, context.TaskId, logger).Tool);
        }

        if (context is { Models: { } models, Workspace: { } workspace, ImageModels.Count: > 0 })
        {
            tools.Add(new FleetImageTools(models, context.ImageModels, workspace, logger).Tool);
        }

        return tools;
    }

    private static HashSet<string> Enabled(IReadOnlyList<string>? selection)
        => (selection ?? ToolCatalog.AllSelectableToolKeys).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The catalog for an explicit selection (<c>null</c> means all) and MCP server set. Names outside
    /// <c>^[A-Za-z0-9_-]{1,128}$</c>, and a second tool with a name already taken, are skipped with a warning (the
    /// ADR 0085 finding: one bad name fails every model call). The document tools come first and are never gated by
    /// the selection; their names are reserved even without them, so an MCP tool can't take one (ADR 0101).
    /// </summary>
    public IReadOnlyList<AIFunction> Build(
        IReadOnlyList<string>? selection, string request, IReadOnlyList<McpServerTools> servers, IFleetInteraction interaction,
        IReadOnlyList<AIFunction>? documentTools = null, IReadOnlyList<AIFunction>? personalTools = null)
    {
        var enabled = Enabled(selection);
        var tools = new List<AIFunction>();
        if (webSearch is not null)
        {
            var web = new FleetWebTools(webSearch, logger);
            tools.AddRange(web.Tools.Where(t => ToolCatalog.IsToolEnabled(t.Name, enabled)));
        }

        var sink = new FleetElicitationSink(interaction);
        foreach (var server in servers)
        {
            tools.AddRange(server.Tools
                .Where(t => ToolCatalog.IsMcpToolEnabled(server.ServerId, t.Name, enabled))
                .Select(t => WithReauthNotice(server, WithElicitation(server, t, sink)))
                .OfType<AIFunction>());
        }

        return [.. documentTools ?? [], .. personalTools ?? [], .. Usable(tools)];
    }

    private List<AIFunction> Usable(List<AIFunction> tools)
    {
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "write_artifact", "read_artifact", "list_artifacts", "note_set", "note_get", "delegate_to_team",
            FleetDocumentTools.SearchName, FleetDocumentTools.ReadName,
        };
        names.UnionWith(PersonalToolNames);
        names.UnionWith(FleetCanvas.ToolNames);
        names.UnionWith(FleetTodos.ToolNames);
        var usable = new List<AIFunction>();
        foreach (var tool in tools)
        {
            if (!ToolNameRegex().IsMatch(tool.Name) || !names.Add(tool.Name))
            {
                logger.LogWarning("Fleet tool '{Tool}' skipped: its name is invalid or already taken", tool.Name);
                continue;
            }

            usable.Add(tool);
        }

        return usable;
    }

    private static AITool WithElicitation(McpServerTools server, AITool tool, IMcpElicitationSink sink)
        => server.Elicitation is { } router && tool is AIFunction fn
            ? new ElicitingMcpTool(fn, router, server.ServerName ?? server.ServerId, () => sink)
            : tool;

    private static AITool WithReauthNotice(McpServerTools server, AITool tool)
        => server.InteractiveOAuth && tool is AIFunction fn
            ? new ReauthNoticeMcpTool(fn, server.ServerId, server.ServerName)
            : tool;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex ToolNameRegex();
}

/// <summary>
/// What <see cref="FleetToolCatalog.BuildAsync(FleetToolContext, CancellationToken)"/> needs of one task (ADR 0144). The task
/// id links a push to the task; the workspace, the user's model factory and image models make <c>generate_image</c>.
/// </summary>
public sealed record FleetToolContext(
    string UserId, string TaskId, string Request, IReadOnlyList<string>? SelectionOverride, IFleetInteraction Interaction)
{
    /// <summary>The workspace's document tools (ADR 0101), first in the list and never gated by the selection.</summary>
    public IReadOnlyList<AIFunction> DocumentTools { get; init; } = [];

    public FleetWorkspace? Workspace { get; init; }

    /// <summary>The user's model factory, for the image generators.</summary>
    public IChatClientFactory? Models { get; init; }

    /// <summary>The user's image-generation models, in catalog order. None means no <c>generate_image</c>.</summary>
    public IReadOnlyList<string> ImageModels { get; init; } = [];
}

/// <summary>A task's tools, and the skills among them (for Copilot's native skill loader, ADR 0144).</summary>
public sealed record FleetToolSet(IReadOnlyList<AIFunction> Tools, IReadOnlyList<SkillInfo> Skills);
