namespace Agent42.Backend;

/// <summary>
/// Durable storage for the Copilot runtime's session files: its own transcript (<c>events.jsonl</c>), workspace and
/// checkpoints, which the AF session only points at (measured: <c>{"sessionId":...}</c> and nothing else). Without
/// it, a deploy loses every copilot chat's context (measured: resume after wiping the files throws
/// <c>Session not found</c>). Production is Blob (<see cref="BlobCopilotSessionStore"/>, private
/// <c>copilot-sessions</c> container); tests and DevHost use <see cref="InMemoryCopilotSessionStore"/>.
///
/// <para>A flat file store. Keys are <c>{escaped userId}/{escaped conversationId}/{path}</c>
/// (<see cref="CopilotSessionPaths"/>), and directories are implied by the keys under them. The runtime's
/// directory semantics live in <see cref="CopilotSessionFsProvider"/>, so a store has nothing to get wrong about
/// them.</para>
/// </summary>
public interface ICopilotSessionStore
{
    /// <summary>The file's text, or null when it does not exist.</summary>
    Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the file.</summary>
    Task WriteAsync(string key, string content, CancellationToken cancellationToken = default);

    /// <summary>Appends to the file, creating it when it does not exist.</summary>
    Task AppendAsync(string key, string content, CancellationToken cancellationToken = default);

    /// <summary>The file's size in bytes and last write, or null when it does not exist.</summary>
    Task<CopilotFileInfo?> GetInfoAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Every key that starts with <paramref name="prefix"/>, at any depth.</summary>
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>Deletes the file. Deleting a missing file is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>A stored file's size in bytes and last write.</summary>
public sealed record CopilotFileInfo(long Size, DateTimeOffset Modified);

/// <summary>
/// Where one conversation's Copilot files live, and the one place a runtime-supplied path becomes a store key.
/// </summary>
public static class CopilotSessionPaths
{
    /// <summary>
    /// The key prefix of one conversation: <c>{userId}/{conversationId}/</c>, each segment escaped so neither can
    /// add a level or step out of its own (a conversation id is caller-supplied on some surfaces, and the grain key
    /// splits only at the FIRST '/').
    /// </summary>
    public static string ConversationPrefix(string userId, string conversationId)
        => $"{Uri.EscapeDataString(userId)}/{Uri.EscapeDataString(conversationId)}/";

    /// <summary>
    /// The store key for a path the runtime sent (POSIX, e.g. <c>/state/events.jsonl</c>), under
    /// <paramref name="prefix"/>. Empty and <c>.</c> segments are dropped. A <c>..</c> segment is refused, never
    /// resolved: the runtime is third-party code, and its paths are not trusted to stay inside the conversation.
    /// The root is the prefix itself.
    /// </summary>
    public static string KeyFor(string prefix, string path)
    {
        var segments = Segments(path);
        if (segments.Contains(".."))
        {
            throw new UnauthorizedAccessException($"Path '{path}' leaves the session.");
        }

        return prefix + string.Join('/', segments);
    }

    /// <summary>
    /// Whether a runtime path lies under the session state path (<c>/state</c>), whatever the host's conventions
    /// (<c>/state/x</c>, <c>\state\x</c>, <c>C:\state\x</c>). With built-in tools on, everything else is the real
    /// workspace on disk, so a state path classified wrongly would put the transcript on disk and break resume.
    /// </summary>
    public static bool IsStatePath(string path)
        => Segments(path) is [StateSegment, ..] && path.TrimStart() is ['/' or '\\', ..] or [_, ':', ..];

    private const string StateSegment = "state";

    // The path's segments with separators unified, empty and '.' segments dropped, and a leading drive letter removed
    // (a Windows host's runtime may send one; the store key must stay the one a Posix runtime produces).
    private static List<string> Segments(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != ".")
            .ToList();
        if (segments is [{ Length: 2 } drive, ..] && drive[1] == ':' && char.IsAsciiLetter(drive[0]))
        {
            segments.RemoveAt(0);
        }

        return segments;
    }
}

/// <summary>
/// Where one conversation's Copilot workspace lives on disk (the Azure Files share in production), and the one place
/// that path is built. The working directory, the permission policy's root, the SessionFs router's root and the
/// delete target all come from <see cref="For"/>, so no caller builds it itself.
/// </summary>
public static class CopilotWorkspacePaths
{
    /// <summary>Path comparison as the host's filesystem does it: case-insensitive on Windows, ordinal elsewhere.</summary>
    public static StringComparison Comparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// <c>{root}/{userId}/{conversationId}</c>, each segment escaped. <see cref="Uri.EscapeDataString"/> leaves
    /// <c>.</c> and <c>..</c> alone, which is harmless as a Blob key but escapes the root on disk, so those (and a
    /// trailing dot, which Windows and SMB strip) are refused, and the result is checked to lie strictly under the root.
    /// </summary>
    public static string For(string root, string userId, string conversationId)
    {
        var fullRoot = Root(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, Segment(userId), Segment(conversationId)));
        if (!IsInside(fullRoot, path) || path.Equals(fullRoot, Comparison))
        {
            throw new UnauthorizedAccessException($"Workspace '{path}' leaves '{fullRoot}'.");
        }

        return path;
    }

    /// <summary>The canonical form of a root directory: absolute, with no trailing separator.</summary>
    public static string Root(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    /// <summary>Whether <paramref name="path"/> (relative paths resolve against the root) is the root or lies under it.</summary>
    public static bool IsInside(string root, string path)
    {
        var fullRoot = Root(root);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, fullRoot));
        return fullPath.Equals(fullRoot, Comparison)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, Comparison);
    }

    // '*' is the one character EscapeDataString keeps that a Windows or SMB file name cannot hold.
    private static string Segment(string id)
    {
        var segment = Uri.EscapeDataString(id).Replace("*", "%2A", StringComparison.Ordinal);
        return segment is "" or "." or ".." || segment.EndsWith('.')
            ? throw new UnauthorizedAccessException($"'{id}' cannot name a workspace directory.")
            : segment;
    }
}
