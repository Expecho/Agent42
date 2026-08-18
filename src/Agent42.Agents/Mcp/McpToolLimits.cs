namespace Agent42.Agents;

/// <summary>
/// The budget an MCP tool's <b>return value</b> must fit in (mirrors <see cref="WebToolLimits"/>).
///
/// Why: a tool's <c>FunctionResultContent</c> is <b>persisted</b>. It stays in the Agent Framework chat
/// history, and <c>AgentGrain</c> re-serializes the whole session into a single <c>agent-state</c> Cosmos
/// item every turn — an item Cosmos hard-caps at <b>2 MB</b> (ADR 0004). A remote MCP server's result is
/// attacker-uncontrolled in size, so an unbudgeted result would sit in history forever, be re-sent every
/// turn, and eventually make <c>WriteStateAsync</c> fail <i>after</i> the model already answered — bricking
/// the conversation, in production only. So each MCP tool is wrapped to truncate its serialized result to
/// this budget while preserving the tool's name and JSON schema (the model still calls it correctly).
/// </summary>
public static class McpToolLimits
{
    /// <summary>Maximum length of a single MCP tool's serialized result returned to the model, in characters.</summary>
    public const int MaxToolResultLength = 8 * 1024;

    /// <summary>Appended to a tool result when it was cut off at <see cref="MaxToolResultLength"/>.</summary>
    public const string TruncationMarker = "\n\n[truncated]";
}
