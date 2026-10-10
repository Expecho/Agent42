namespace Agent42.Backend;

/// <summary>
/// The bounds of the owner's conversation export (ADR 0091).
/// </summary>
public static class ExportLimits
{
    /// <summary>
    /// The most conversations one bulk-export part holds. Each one costs a grain activation, and activation connects
    /// the user's MCP servers sequentially, so one request cannot hold every conversation: the same bound as
    /// <c>ChatSearchLimits.ReindexBatchSize</c>, for the same reason, kept well inside the 240 s ingress timeout.
    /// </summary>
    public const int BulkBatchSize = 25;

    /// <summary>
    /// The most of a tool result the Markdown transcript shows. A transcript is for reading; the JSON export keeps
    /// every result whole.
    /// </summary>
    public const int MarkdownToolResultChars = 2_000;

    /// <summary>The longest title slug in a file or folder name.</summary>
    public const int MaxSlugChars = 80;

    /// <summary>The longest attachment file name inside an archive.</summary>
    public const int MaxFileNameChars = 100;

    /// <summary>
    /// How much of an archive is buffered in memory before the rest spills to a temp file. The archive is built in a
    /// buffer, not straight into the response, because <c>ZipArchive</c> disposes each entry synchronously.
    /// </summary>
    public const int ZipMemoryThresholdBytes = 1024 * 1024;
}
