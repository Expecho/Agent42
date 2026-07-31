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

    /// <summary>
    /// The assistant produced a generated image (ADR 0020). Image generation is not token-streamed —
    /// one <see cref="Image"/> event carrying the stored attachment id + media type, then <see cref="Done"/>.
    /// The client renders it through the attachment proxy (by id), exactly like a user-uploaded image.
    /// </summary>
    Image,

    /// <summary>The turn finished; carries the aggregated token usage.</summary>
    Done,

    /// <summary>
    /// Up to 3 suggested follow-up prompts the user can click to continue the conversation. Generated
    /// best-effort by a second, non-streaming model call after the reply (interactive path only), streamed
    /// just before <see cref="Done"/>. The list rides on <see cref="AgentStreamEvent.Suggestions"/>.
    /// </summary>
    Suggestions,
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
    [property: Id(8)] long? CachedTokens = null,
    // Set on an Image event (ADR 0020): the stored attachment id the browser fetches via the proxy, plus
    // its media type. Trailing defaults so every existing positional construction keeps compiling.
    [property: Id(9)] string? AttachmentId = null,
    [property: Id(10)] string? MediaType = null,
    // Set on a Suggestions event: up to 3 short follow-up prompts. Trailing default so every existing
    // positional construction keeps compiling.
    [property: Id(11)] IReadOnlyList<string>? Suggestions = null);
