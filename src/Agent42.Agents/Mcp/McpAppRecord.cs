using System.Text.Json.Serialization;

namespace Agent42.Agents;

/// <summary>
/// One stored MCP App call (ADR 0133), kept in <see cref="AgentGrainState.McpApps"/>: which view a tool call rendered
/// and where its raw result sits, so a reloaded conversation can draw the card again. Only metadata lives here: the
/// raw <c>CallToolResult</c> is a blob (ADR 0004), stored as <c>application/octet-stream</c> because the attachment
/// proxies would serve any other type on the app origin.
/// </summary>
// ADR 0039: flat namespace, frozen. Cosmos writes this type's full CLR name into the stored state.
[GenerateSerializer]
[Alias("Agent42.Agents.McpAppRecord")]
public sealed record McpAppRecord(
    [property: Id(0)] string CallId,
    [property: Id(1)] string ServerId,
    [property: Id(2)] string ToolName,
    [property: Id(3)] string ResourceUri,
    // Kept out of any HTTP JSON like ArtifactVersionRef.AttachmentId: the browser reads the result through the owner-only
    // result route, never through the raw attachment proxy.
    [property: Id(4)][property: JsonIgnore] string AttachmentId,
    [property: Id(5)] int SizeBytes)
{
    /// <summary>The view reference history replays onto the tool call.</summary>
    public McpAppRef ToRef() => new(ServerId, ToolName, ResourceUri);
}
