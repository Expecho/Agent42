using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// One management-enabled MCP server's contribution to a user's tool set (ADR 0026): the server's
/// <see cref="ServerId"/> and the <see cref="AITool"/>s it exposes. A server that could not be reached (or
/// is not yet OAuth-connected) still appears here with an <b>empty</b> <see cref="Tools"/> list — its id is
/// needed so the grain can expand the <c>mcp</c> wildcard into the complete per-server key set, matching the
/// <c>GET /api/tools</c> catalog (which lists every enabled server, connected or not).
/// </summary>
public sealed record McpServerTools(string ServerId, IReadOnlyList<AITool> Tools);

/// <summary>
/// The MCP tools resolved for a user, grouped per server, plus the overlay <see cref="Version"/> they were
/// built from — the cache key the provider stamps on, so a config save is reflected on the next grain
/// activation without an app restart (the MCP analogue of <c>UserChatClientFactoryProvider</c>'s versioned
/// cache). Per-server grouping (ADR 0026) is what lets <c>BuildAgent</c> gate each server individually.
/// </summary>
public sealed record McpToolSet(IReadOnlyList<McpServerTools> Servers, int Version)
{
    /// <summary>Every server's tools flattened — the whole set, ungated.</summary>
    public IReadOnlyList<AITool> AllTools => [.. Servers.SelectMany(s => s.Tools)];

    /// <summary>The empty set — a user with no configured MCP servers.</summary>
    public static McpToolSet Empty { get; } = new([], 0);
}

/// <summary>
/// Resolves the remote-MCP-server tools a specific user's conversations expose (config-driven remote MCP
/// servers). A user adds their own MCP servers, so the tool set is per-user, not a process singleton. The
/// agent grain resolves this <b>once</b> in <c>OnActivateAsync</c> (async) from its own userId — <b>before</b>
/// the first <c>BuildAgent</c>, the same ordering hazard as the chat-client factory — and stores the result,
/// so <c>BuildAgent</c> (sync) just merges the tools into its <c>List&lt;AITool&gt;</c>. The interface lives in
/// the Agents layer and returns <see cref="AITool"/> (a Microsoft.Extensions.AI type), so this layer needs no
/// MCP package reference — the impl lives in the Backend, exactly like <see cref="IUserChatClientFactoryProvider"/>.
/// </summary>
public interface IUserMcpToolProvider
{
    /// <summary>The MCP tools for <paramref name="userId"/>'s enabled servers (empty when none configured).</summary>
    Task<McpToolSet> GetForUserAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// An <see cref="IUserMcpToolProvider"/> that hands back a FIXED set for every user — DevHost and tests,
/// where there is no per-user MCP store. Empty by default (offline has no MCP servers); a test can set
/// <see cref="Tools"/> to inject a fake tool set. Mirrors how <see cref="SingleUserChatClientFactoryProvider"/>
/// stands in for the per-user chat-client seam offline.
/// </summary>
public sealed class SingleUserMcpToolProvider : IUserMcpToolProvider
{
    /// <summary>
    /// The per-server groups handed to every user. Defaults to empty; a multi-server test sets this directly
    /// to exercise per-server gating (ADR 0026).
    /// </summary>
    public IReadOnlyList<McpServerTools> Servers { get; set; } = [];

    /// <summary>
    /// Convenience for the common single-server test: assigns a lone group under
    /// <see cref="DefaultServerId"/>. Existing tests that set a flat tool list and gate with the
    /// <c>mcp</c> wildcard keep working unchanged.
    /// </summary>
    public IReadOnlyList<AITool> Tools
    {
        set => Servers = value.Count == 0 ? [] : [new McpServerTools(DefaultServerId, value)];
    }

    /// <summary>The server id the <see cref="Tools"/> convenience assigns.</summary>
    public const string DefaultServerId = "offline";

    public Task<McpToolSet> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(new McpToolSet(Servers, 0));
}
