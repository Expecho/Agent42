using System.Net;
using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// The tool result an InteractiveOAuth MCP server's call is replaced with when the server answered 401 after the
/// per-request bearer's own renew-and-retry (ADR 0081): the grant is dead and a human must reconnect. It begins
/// with <see cref="ResultMarker"/> (the <see cref="McpToolLimits.TruncationMarker"/> idiom — a fixed constant
/// both sides share), which is how the turn spots it in the <c>ToolCallEnd</c> stream it already walks, with no
/// callback and no turn-scoped field.
/// </summary>
public static class McpReauthNotice
{
    /// <summary>Prefix of every notice; the server id follows up to the closing bracket.</summary>
    public const string ResultMarker = "[mcp-reauth-required:";

    /// <summary>The model-readable notice for <paramref name="serverId"/>.</summary>
    public static string For(string serverId, string? serverName) =>
        $"{ResultMarker}{serverId}]\n"
        + $"The MCP server \"{serverName ?? serverId}\" is disconnected: its sign-in has expired, so this tool "
        + "could not run. The user has been shown a Reconnect button in the chat. Ask them to press it and then "
        + "try again. Do not offer a link of your own.";

    /// <summary>The server id a notice names, or <c>null</c> when <paramref name="result"/> is not a notice.</summary>
    public static string? ServerIdOf(string? result)
    {
        if (result is null || !result.StartsWith(ResultMarker, StringComparison.Ordinal))
        {
            return null;
        }

        var end = result.IndexOf(']', ResultMarker.Length);
        return end > ResultMarker.Length ? result[ResultMarker.Length..end] : null;
    }
}

/// <summary>
/// Wraps one tool of an InteractiveOAuth MCP server so a 401 becomes a <see cref="McpReauthNotice"/> result
/// instead of a tool failure (ADR 0081). Shaped like <see cref="UrlHarvestingMcpTool"/>: a grain-constructed
/// <see cref="DelegatingAIFunction"/> — the Backend's shared per-user tool instances are the wrong seam for
/// anything the turn must see (ADR 0067's reason).
///
/// <para><b>What it matches, and why only that.</b> Measured against <c>ModelContextProtocol</c> 2.2.0 on
/// 2026-09-21: a <c>tools/call</c> answered with 401 throws a bare <see cref="HttpRequestException"/> whose
/// <see cref="HttpRequestException.StatusCode"/> is <see cref="HttpStatusCode.Unauthorized"/>. The chain is walked
/// in case a later SDK wraps it. Every other exception passes through untouched — an unreachable server is not a
/// reason to ask for a sign-in.</para>
///
/// <para><b>It must be the OUTERMOST wrapper</b>, outside <see cref="UrlHarvestingMcpTool"/>. Nested inside, the
/// harvester would treat the notice as a successful result, breaking ADR 0067's "a failing call seeds nothing".</para>
///
/// <para>Public rather than internal because this repo has no <c>InternalsVisibleTo</c>, and the wrapper's own
/// contract (which exception becomes a notice, which passes through) is unit-tested directly.</para>
/// </summary>
public sealed class ReauthNoticeMcpTool(AIFunction inner, string serverId, string? serverName) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await base.InvokeCoreAsync(arguments, cancellationToken);
        }
        catch (Exception ex) when (IsUnauthorized(ex))
        {
            return McpReauthNotice.For(serverId, serverName);
        }
    }

    private static bool IsUnauthorized(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Collects, for ONE run of the turn core, the InteractiveOAuth servers whose tool call came back as a
/// <see cref="McpReauthNotice"/> (ADR 0081). A local in <c>ChatCoreAsync</c>, never a grain field: two runs can be
/// in flight on one activation (an <c>EnqueuePromptAsync</c> drain executes while a browser turn is suspended at a
/// yield), and a field would be wiped or polluted by the other one.
///
/// <para><b>The marker is trusted only from a tool of the server it names.</b> A notice is just text, and a
/// fetched page or another tool's result could begin with the same prefix; the call id is matched back to the tool
/// name from its <c>ToolCallStart</c>, and that name must belong to that InteractiveOAuth server's group.</para>
/// </summary>
public sealed class McpReauthTurnObserver(IReadOnlyList<McpServerTools> servers)
{
    private readonly Dictionary<string, string> _toolByCallId = new(StringComparer.Ordinal);

    /// <summary>The servers that answered 401 during this run.</summary>
    public HashSet<string> ServerIds { get; } = new(StringComparer.Ordinal);

    public void Observe(AgentStreamEvent evt)
    {
        if (evt is { Type: AgentStreamEventType.ToolCallStart, ToolCallId: { } startId, ToolName: { } name })
        {
            _toolByCallId[startId] = name;
            return;
        }

        if (evt is not { Type: AgentStreamEventType.ToolCallEnd, ToolCallId: { } endId }
            || McpReauthNotice.ServerIdOf(ReadJsonString(evt.ToolResult)) is not { } serverId
            || !_toolByCallId.TryGetValue(endId, out var tool))
        {
            return;
        }

        if (servers.Any(s => s.InteractiveOAuth && s.ServerId == serverId && s.Tools.Any(t => t.Name == tool)))
        {
            ServerIds.Add(serverId);
        }
    }

    // ToolCallEnd carries the result JSON-serialized; the notice is a string result, so it arrives as a JSON string.
    private static string? ReadJsonString(string? json)
    {
        if (json is null || !json.StartsWith('"'))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
