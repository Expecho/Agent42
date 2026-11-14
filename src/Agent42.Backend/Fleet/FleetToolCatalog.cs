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
/// </summary>
public sealed partial class FleetToolCatalog(
    IGrainFactory grains,
    ILogger<FleetToolCatalog> logger,
    IWebSearch? webSearch = null,
    IUserMcpToolProvider? mcpTools = null)
{
    /// <summary>
    /// The selected tools for a task whose request is <paramref name="request"/>: <paramref name="selectionOverride"/>
    /// when the task has one (the profile is then not read), else the user's default selection (ADR 0099).
    /// </summary>
    public async Task<IReadOnlyList<AIFunction>> BuildAsync(
        string userId, string request, IReadOnlyList<string>? selectionOverride, IFleetInteraction interaction,
        CancellationToken cancellationToken, IReadOnlyList<AIFunction>? documentTools = null)
    {
        var selection = selectionOverride ?? (await grains.GetGrain<IUserProfileGrain>(userId).GetAsync()).EnabledTools;
        var servers = mcpTools is null ? [] : (await mcpTools.GetForUserAsync(userId, cancellationToken)).Servers;
        return Build(selection, request, servers, interaction, documentTools);
    }

    /// <summary>
    /// The catalog for an explicit selection (<c>null</c> means all) and MCP server set. Names outside
    /// <c>^[A-Za-z0-9_-]{1,128}$</c>, and a second tool with a name already taken, are skipped with a warning (the
    /// ADR 0085 finding: one bad name fails every model call). The document tools come first and are never gated by
    /// the selection; their names are reserved even without them, so an MCP tool can't take one (ADR 0101).
    /// </summary>
    public IReadOnlyList<AIFunction> Build(
        IReadOnlyList<string>? selection, string request, IReadOnlyList<McpServerTools> servers, IFleetInteraction interaction,
        IReadOnlyList<AIFunction>? documentTools = null)
    {
        var enabled = (selection ?? ToolCatalog.AllSelectableToolKeys).ToHashSet(StringComparer.Ordinal);
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

        return [.. documentTools ?? [], .. Usable(tools)];
    }

    private List<AIFunction> Usable(List<AIFunction> tools)
    {
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "write_artifact", "read_artifact", "list_artifacts", "note_set", "note_get", "delegate_to_team",
            FleetDocumentTools.SearchName, FleetDocumentTools.ReadName,
        };
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
