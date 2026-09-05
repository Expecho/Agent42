using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// One management-enabled MCP server's contribution to a user's tool set (ADR 0026): the server's
/// <see cref="ServerId"/> and the <see cref="AITool"/>s it exposes. A server that could not be reached (or
/// is not yet OAuth-connected) still appears here with an <b>empty</b> <see cref="Tools"/> list — its id is
/// needed so the grain can expand the <c>mcp</c> wildcard into the complete per-server key set, matching the
/// <c>GET /api/tools</c> catalog (which lists every enabled server, connected or not).
///
/// <para>Not an Orleans-serialized type — it never leaves the silo's process boundary as state and carries
/// <see cref="AITool"/> instances, so it needs no <c>[Id]</c>s and no <c>[GenerateSerializer]</c>.</para>
/// </summary>
/// <param name="KnownToolNames">
/// The names the picker will render checkboxes for — <b>the same list the grain expands
/// <c>mcp:{serverId}</c> into</b>, which is the invariant the per-tool feature rests on. The provider fills
/// it from the persisted <c>StoredMcpServer.KnownToolNames</c>, refreshed with what it just discovered on a
/// successful connect, so the catalog endpoint (which reads the config grain) and the grain (which reads
/// this) can never disagree. Empty when the server has never connected: the grain must then leave
/// <c>mcp:{serverId}</c> UNEXPANDED rather than expand it into nothing.
///
/// <para>Deliberately NOT derived from <see cref="Tools"/>. On a failed connect <see cref="Tools"/> is empty
/// while the previously discovered names still stand, and erasing them there is exactly the bug that makes a
/// partial per-tool selection unrecoverable.</para>
/// </param>
/// <param name="SeedsFetchableUrls">
/// Whether this server's tool results may seed the conversation's fetchable-URL allowlist (ADR 0067) —
/// the user's per-server opt-in, copied straight off <see cref="StoredMcpServer.SeedsFetchableUrls"/>.
/// <c>BuildAgent</c> reads it to decide whether to wrap this server's tools in the harvesting delegate;
/// nothing else does. Defaults to <c>false</c>, so a group built without it seeds nothing.
/// </param>
public sealed record McpServerTools(
    string ServerId,
    IReadOnlyList<AITool> Tools,
    IReadOnlyList<string>? KnownToolNames = null,
    bool SeedsFetchableUrls = false);

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
    ///
    /// <para><see cref="McpServerTools.KnownToolNames"/> is derived from the tools' own names here, which is
    /// the offline analogue of a successful connect — the fake provider genuinely knows its tools. A test
    /// that needs the never-connected shape (known names empty) assigns <see cref="Servers"/> directly.</para>
    ///
    /// <para>The group it builds never seeds fetchable URLs (ADR 0067). There is deliberately no
    /// convenience for that: seeding is a per-server property, so a caller that wants it assigns
    /// <see cref="Servers"/> and says so on the group. A settable shortcut here would have to be applied
    /// at assignment time — making the result depend on the order two properties were set — or by
    /// rewriting the groups behind the caller's back, which leaves the shortcut's own getter disagreeing
    /// with what <see cref="Servers"/> holds. Both were written; neither survived review.</para>
    /// </summary>
    public IReadOnlyList<AITool> Tools
    {
        set => Servers = value.Count == 0
            ? []
            : [new McpServerTools(DefaultServerId, value, [.. value.Select(t => t.Name)])];
    }

    /// <summary>The server id the <see cref="Tools"/> convenience assigns.</summary>
    public const string DefaultServerId = "offline";

    public Task<McpToolSet> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(new McpToolSet(Servers, 0));
}
