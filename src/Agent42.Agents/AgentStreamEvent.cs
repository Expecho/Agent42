namespace Agent42.Agents;

/// <summary>
/// The kind of event streamed back while the agent works on a turn.
/// </summary>
public enum AgentStreamEventType
{
    /// <summary>A fragment of the assistant's reply text.</summary>
    TextDelta,

    /// <summary>The model invoked a tool; input is on its way to the function.</summary>
    ToolCallStart,

    /// <summary>A tool finished and returned its result to the model.</summary>
    ToolCallEnd,

    /// <summary>The turn finished; carries the aggregated token usage.</summary>
    Done,
}

/// <summary>
/// A single event in the agent's streamed response: reply text, tool-call
/// progress, or the closing token-usage summary. Only the fields relevant to
/// the <see cref="Type"/> are set; the rest stay null.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.AgentStreamEvent")]
public sealed record AgentStreamEvent(
    [property: Id(0)] AgentStreamEventType Type,
    [property: Id(1)] string? Text = null,
    [property: Id(2)] string? ToolName = null,
    [property: Id(3)] string? ToolCallId = null,
    [property: Id(4)] string? ToolInput = null,
    [property: Id(5)] string? ToolResult = null,
    [property: Id(6)] long? InputTokens = null,
    [property: Id(7)] long? OutputTokens = null,
    [property: Id(8)] long? CachedTokens = null);
