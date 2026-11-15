namespace Agent42.Agents;

/// <summary>
/// Where one MCP tool's interactive view lives (SEP-1865, ADR 0133): the server, the tool, and the <c>ui://</c>
/// resource its <c>_meta.ui.resourceUri</c> names. Plain data, because the grain has no MCP package: it only
/// forwards this to the browser, and the Backend resolves the live connection behind it.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.McpAppRef")]
public sealed record McpAppRef(
    [property: Id(0)] string ServerId,
    [property: Id(1)] string ToolName,
    [property: Id(2)] string ResourceUri);

/// <summary>
/// One MCP App tool call as the browser needs it to render the view (ADR 0133): which view, and the tool's RAW
/// <c>CallToolResult</c> as JSON text (content + structuredContent). This is NOT what the model saw: the model gets
/// the shaped, bounded text, and this travels beside it on the stream event and into the replay blob, never into the
/// persisted session (ADR 0004).
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.McpAppResult")]
public sealed record McpAppResult(
    [property: Id(0)] McpAppRef App,
    [property: Id(1)] string ResultJson);
