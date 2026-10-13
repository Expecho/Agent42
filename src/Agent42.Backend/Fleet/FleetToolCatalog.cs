using System.Text.RegularExpressions;
using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// The tools a fleet task's agents can name in their YAML besides the workspace tools and <c>delegate_to_team</c>
/// (ADR 0097): <c>web_search</c> and <c>fetch_web_page</c>, and the user's own MCP tools. <c>SelectableTools</c> is the
/// grain's and can't be called here, so this is a small catalog of its own. It honours the user's DEFAULT tool
/// selection (ADR 0021, <c>null == all</c>) for both, and wraps MCP tools as <c>BuildAgent</c> does: the ADR 0095
/// elicitation wrapper innermost, the ADR 0081 reauth notice outermost. No URL seeding (ADR 0067): the fleet has
/// no <c>read_web_document</c>.
/// </summary>
public sealed partial class FleetToolCatalog(
    IGrainFactory grains,
    ILogger<FleetToolCatalog> logger,
    IWebSearch? webSearch = null,
    IWebPageFetcher? webPageFetcher = null,
    IUserMcpToolProvider? mcpTools = null)
{
    /// <summary>The user's selected tools for a task whose request is <paramref name="request"/>.</summary>
    public async Task<IReadOnlyList<AIFunction>> BuildAsync(
        string userId, string request, IFleetInteraction interaction, CancellationToken cancellationToken)
    {
        var profile = await grains.GetGrain<IUserProfileGrain>(userId).GetAsync();
        var servers = mcpTools is null ? [] : (await mcpTools.GetForUserAsync(userId, cancellationToken)).Servers;
        return Build(profile.EnabledTools, request, servers, interaction);
    }

    /// <summary>
    /// The catalog for an explicit selection (<c>null</c> means all) and MCP server set. Names outside
    /// <c>^[A-Za-z0-9_-]{1,128}$</c>, and a second tool with a name already taken, are skipped with a warning (the
    /// ADR 0085 finding: one bad name fails every model call).
    /// </summary>
    public IReadOnlyList<AIFunction> Build(
        IReadOnlyList<string>? selection, string request, IReadOnlyList<McpServerTools> servers, IFleetInteraction interaction)
    {
        var enabled = (selection ?? ToolCatalog.AllSelectableToolKeys).ToHashSet(StringComparer.Ordinal);
        var tools = new List<AIFunction>();
        if (webSearch is not null && webPageFetcher is not null)
        {
            var web = new FleetWebTools(webSearch, webPageFetcher, request, logger);
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

        return Usable(tools);
    }

    private List<AIFunction> Usable(List<AIFunction> tools)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { "write_artifact", "read_artifact", "list_artifacts", "note_set", "note_get", "delegate_to_team" };
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
