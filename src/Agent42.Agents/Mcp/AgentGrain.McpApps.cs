using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// The grain-side seam of MCP Apps replay (ADR 0133). The store is <see cref="McpAppStore"/>; this half stays because it
/// reads the live <c>_session</c> (ADR 0106) and is the public <see cref="IAgentGrain"/> entry.
/// </summary>
public sealed partial class AgentGrain
{
    public Task<string?> GetMcpAppResultAsync(string callId) => _mcpApps.GetResultAsync(callId);

    // The call ids still in the session, or null when the history is unreadable (the store then drops nothing).
    private IReadOnlySet<string>? LiveSessionCallIds()
        => _session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) && messages is not null
            ? messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId).ToHashSet(StringComparer.Ordinal)
            : null;
}
