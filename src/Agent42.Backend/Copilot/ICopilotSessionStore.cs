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
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != ".")
            .ToList();
        if (segments.Contains(".."))
        {
            throw new UnauthorizedAccessException($"Path '{path}' leaves the session.");
        }

        return prefix + string.Join('/', segments);
    }
}
