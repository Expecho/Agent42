namespace Agent42.Agents;

/// <summary>
/// One client tool call the interactive turn is waiting on (ADR 0089), as the browser needs it to answer the call
/// after its stream died: the same <c>toolCallId</c>/<c>toolName</c>/<c>toolInput</c> shape as the live
/// <c>toolCallStart</c> chunk, so the frontend handles both through one path. Not grain state.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.PendingClientToolCall")]
public sealed record PendingClientToolCall(
    [property: Id(0)] string ToolCallId,
    [property: Id(1)] string ToolName,
    [property: Id(2)] string? ToolInput);
