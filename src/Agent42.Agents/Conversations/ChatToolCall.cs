namespace Agent42.Agents;

/// <summary>
/// One tool call a turn made, as exposed to clients so a reopened conversation re-renders its
/// chips (ADR 0080). Projected fresh out of the persisted session by <c>GetHistoryAsync</c> —
/// this is not grain state, and nothing new is stored to carry it.
/// <para>
/// The member names are load-bearing: with <c>JsonSerializerOptions.Web</c> they serialize to
/// <c>toolCallId</c>/<c>toolName</c>/<c>toolInput</c>/<c>toolResult</c>, which is exactly the
/// live <c>toolCallStart</c>/<c>toolCallEnd</c> chunk shape the frontend already feeds to
/// <c>addToolCall</c>/<c>completeToolCall</c>, so replay needs no mapping layer.
/// </para>
/// Crosses the grain boundary (returned inside <see cref="ChatTurn"/>), so it is
/// Orleans-serializable with explicit member ids — mirror <see cref="AttachmentRef"/>.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ChatToolCall")]
public sealed record ChatToolCall(
    [property: Id(0)] string ToolCallId,
    [property: Id(1)] string ToolName,
    [property: Id(2)] string? ToolInput,
    // Null when the tool returned null, and defensively whenever no result for this call is in the
    // history at all. NOT a stopped turn: cancelling an Agent Framework run persists nothing — not even
    // the user message — so ADR 0078 cannot leave a dangling call here (measured, gotchas.md). The chip
    // replays completed and without a result body either way, rather than pulsing forever.
    [property: Id(3)] string? ToolResult);
