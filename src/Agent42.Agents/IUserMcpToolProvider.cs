using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// The MCP tools resolved for a user, plus the overlay <see cref="Version"/> they were built from —
/// the cache key the provider stamps on, so a config save is reflected on the next grain activation
/// without an app restart (the MCP analogue of <c>UserChatClientFactoryProvider</c>'s versioned cache).
/// </summary>
public sealed record McpToolSet(IReadOnlyList<AITool> Tools, int Version)
{
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
    /// <summary>The tools handed to every user. Defaults to empty; a test may set it to inject MCP tools.</summary>
    public IReadOnlyList<AITool> Tools { get; set; } = [];

    public Task<McpToolSet> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(new McpToolSet(Tools, 0));
}
