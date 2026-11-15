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

    /// <summary>
    /// Images a run may show the model (the Chrome-extension ADR). Tool images are request-only: they are never
    /// persisted, and only the latest ones are kept (evicted at record time), because each one is re-sent on every
    /// model call of the loop.
    /// </summary>
    public const int MaxImagesPerRun = 3;

    /// <summary>Largest decoded image an MCP tool may show the model. A bigger one is dropped with a note.</summary>
    public const int MaxImageBytes = 1024 * 1024;

    /// <summary>
    /// The most of an MCP App tool's RAW result (JSON text) that travels to the browser and into the replay blob
    /// (ADR 0133). Over it the call renders no view and shows the plain tool chip, rather than a truncated result a
    /// view could misread as complete.
    /// </summary>
    public const int MaxAppResultBytes = 64 * 1024;

    /// <summary>The most app-supplied context (<c>ui/update-model-context</c>) a chat request may carry (ADR 0133).</summary>
    public const int MaxAppContextChars = 16 * 1024;

    /// <summary>App results one run keeps for the browser (ADR 0133).</summary>
    public const int MaxAppResultsPerRun = 20;

    /// <summary>
    /// Stored app calls one conversation keeps for replay (ADR 0133). Past it the OLDEST record is dropped and that card
    /// degrades to the plain tool chip.
    /// </summary>
    public const int MaxAppsPerConversation = 20;

    /// <summary>The image types shown to the model, each checked against its magic bytes. Anything else is dropped.</summary>
    public static readonly IReadOnlySet<string> ImageMediaTypes =
        new HashSet<string>(["image/png", "image/jpeg", "image/webp", "image/gif"], StringComparer.OrdinalIgnoreCase);
}
