
namespace Agent42.Agents;

/// <summary>
/// A single message in a conversation, as exposed to clients. A user turn may carry image
/// <see cref="Attachments"/> so a reopened conversation re-renders them; null for turns
/// (and all assistant turns) without any. An assistant turn may carry the
/// <see cref="ToolCalls"/> it made, so a reopened conversation re-renders its chips too.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ChatTurn")]
public sealed record ChatTurn(
    [property: Id(0)] string Role,
    [property: Id(1)] string Text,
    [property: Id(2)] IReadOnlyList<AttachmentRef>? Attachments = null,
    // Assistant turns only, null when the turn called no tools (ADR 0080). Stripped before the
    // public share response — see AgentApi.Sharing.cs.
    [property: Id(3)] IReadOnlyList<ChatToolCall>? ToolCalls = null);
