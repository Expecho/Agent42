using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// The shared workspace of a fleet task (ADR 0097): a local directory and a set of notes that all agents and teams of
/// the task share, plus the tools for them. The directory is <c>{temp}/agent42-fleet/{workspaceId}</c> (a GUID; the
/// user id never appears on disk). It is synced to Blob after every attempt and at the end, materialized from Blob
/// when a follow-up starts, and deleted when the task ends (<see cref="Dispose"/>).
/// </summary>
public sealed class FleetWorkspace : IDisposable
{
    /// <summary>How much of a file <c>read_artifact</c> returns, so one call can't flood an agent's context.</summary>
    public const int MaxReadChars = 100_000;

    private readonly ConcurrentDictionary<string, string> _notes = new(StringComparer.OrdinalIgnoreCase);

    // What the last sync uploaded, by path: its size and write time, so an unchanged file isn't uploaded again.
    private readonly Dictionary<string, (long Size, DateTime Written)> _synced = new(StringComparer.Ordinal);

    // Sub-teams finish attempts concurrently, so syncs take turns.
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    public FleetWorkspace(string workspaceId, IReadOnlyDictionary<string, string>? notes = null, string? root = null)
    {
        WorkspaceId = workspaceId;
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root ?? Path.Combine(Path.GetTempPath(), "agent42-fleet", workspaceId)));
        Directory.CreateDirectory(Root);
        foreach (var (key, value) in notes ?? new Dictionary<string, string>())
        {
            _notes[key] = value;
        }

        Tools =
        [
            AIFunctionFactory.Create(WriteFile, "write_artifact", "Write a text file to the shared workspace of the task, where all agents and sub-teams can read it."),
            AIFunctionFactory.Create(ReadFile, "read_artifact", "Read a text file from the shared workspace of the task."),
            AIFunctionFactory.Create(() => string.Join("\n", ListFiles()), "list_artifacts", "List the files in the shared workspace of the task."),
            AIFunctionFactory.Create(SetNote, "note_set", "Store a small fact under a key, for all agents and sub-teams of the task."),
            AIFunctionFactory.Create(GetNote, "note_get", "Get the fact stored under a key, or all keys when the key is empty."),
        ];
    }

    public string WorkspaceId { get; }
    public string Root { get; }
    public IReadOnlyList<AIFunction> Tools { get; }
    public IReadOnlyDictionary<string, string> Notes => _notes;

    public List<string> ListFiles() => Directory.Exists(Root)
        ? [.. Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(Root, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)]
        : [];

    /// <summary>
    /// The files with the content of text files, for the judge. Each file is cut off after
    /// <paramref name="maxCharsPerFile"/>, and contents stop after <paramref name="maxTotalChars"/>; binary files are
    /// only listed.
    /// </summary>
    public string Describe(int maxCharsPerFile = 8_000, int maxTotalChars = 40_000)
    {
        var files = ListFiles();
        if (files.Count == 0)
        {
            return "The shared workspace has no files.";
        }

        var description = new StringBuilder($"Files in the shared workspace ({files.Count}):\n");
        var remaining = maxTotalChars;
        foreach (var file in files)
        {
            var fullPath = Path.Combine(Root, file);
            var size = new FileInfo(fullPath).Length;
            var content = remaining > 0 && size <= 1_000_000 ? File.ReadAllText(fullPath) : null;
            if (content is null || content.Contains('\0'))
            {
                description.Append($"\n--- {file} ({size:N0} bytes, content not shown)\n");
                continue;
            }

            var shown = content[..Math.Min(content.Length, Math.Min(maxCharsPerFile, remaining))];
            remaining -= shown.Length;
            var cut = shown.Length < content.Length ? $", first {shown.Length:N0} of {content.Length:N0} characters" : "";
            description.Append($"\n--- {file} ({size:N0} bytes{cut})\n{shown}\n");
        }

        return description.ToString();
    }

    /// <summary>
    /// Uploads new and changed files and removes from Blob what is gone locally. Files past
    /// <paramref name="maxFiles"/> or <paramref name="maxBytes"/> are skipped, in path order, and returned so the
    /// caller can log them.
    /// </summary>
    public async Task<IReadOnlyList<string>> SyncToStoreAsync(
        IFleetStore store, string userId, long maxBytes, int maxFiles, CancellationToken cancellationToken)
    {
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            return await SyncCoreAsync(store, userId, maxBytes, maxFiles, cancellationToken);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task<IReadOnlyList<string>> SyncCoreAsync(
        IFleetStore store, string userId, long maxBytes, int maxFiles, CancellationToken cancellationToken)
    {
        var skipped = new List<string>();
        var kept = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var path in ListFiles())
        {
            var info = new FileInfo(Path.Combine(Root, path));
            if (kept.Count >= maxFiles || total + info.Length > maxBytes)
            {
                skipped.Add(path);
                continue;
            }

            kept.Add(path);
            total += info.Length;
            if (_synced.TryGetValue(path, out var last) && last == (info.Length, info.LastWriteTimeUtc))
            {
                continue;
            }

            await store.PutWorkspaceFileAsync(userId, WorkspaceId, path, await File.ReadAllBytesAsync(info.FullName, cancellationToken), cancellationToken);
            _synced[path] = (info.Length, info.LastWriteTimeUtc);
        }

        foreach (var stored in await store.ListWorkspaceAsync(userId, WorkspaceId, cancellationToken))
        {
            if (!kept.Contains(stored.Path) && !skipped.Contains(stored.Path))
            {
                await store.DeleteWorkspaceFileAsync(userId, WorkspaceId, stored.Path, cancellationToken);
            }
        }

        foreach (var path in _synced.Keys.Where(p => !kept.Contains(p)).ToList())
        {
            _synced.Remove(path);
        }

        return skipped;
    }

    /// <summary>Writes the workspace's stored files into the local directory, for a follow-up.</summary>
    public async Task MaterializeAsync(IFleetStore store, string userId, CancellationToken cancellationToken)
    {
        foreach (var file in await store.ListWorkspaceAsync(userId, WorkspaceId, cancellationToken))
        {
            if (await store.GetWorkspaceFileAsync(userId, WorkspaceId, file.Path, cancellationToken) is not { } content)
            {
                continue;
            }

            var fullPath = Resolve(file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, content, cancellationToken);
            var info = new FileInfo(fullPath);
            _synced[file.Path] = (info.Length, info.LastWriteTimeUtc);
        }
    }

    /// <summary>The full path of a workspace-relative path; anything that leaves the workspace is refused.</summary>
    public string Resolve(string path)
    {
        var fullPath = Path.GetFullPath(path, Root);
        return fullPath.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : throw new InvalidOperationException($"'{path}' is outside the workspace, use a path relative to the workspace.");
    }

    public void Dispose()
    {
        _syncGate.Dispose();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a file still held open (a Copilot process shutting down) leaves the temp directory behind.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: the OS cleans the temp directory eventually.
        }
    }

    private string WriteFile(
        [Description("Path relative to the workspace, for example 'research/lisbon.md'.")] string path,
        string content)
    {
        var fullPath = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return $"Wrote {path} ({content.Length} characters).";
    }

    private string ReadFile([Description("Path relative to the workspace.")] string path)
    {
        var fullPath = Resolve(path);
        if (!File.Exists(fullPath))
        {
            return $"{path} does not exist, the workspace has: {string.Join(", ", ListFiles())}";
        }

        var content = File.ReadAllText(fullPath);
        return content.Length <= MaxReadChars ? content : $"{content[..MaxReadChars]}\n… (cut off, {content.Length:N0} characters in total)";
    }

    private string SetNote(string key, string value)
    {
        _notes[key] = value;
        return $"Stored '{key}'.";
    }

    private string GetNote([Description("The key, or empty for all keys.")] string key) =>
        string.IsNullOrWhiteSpace(key) ? $"Keys: {string.Join(", ", _notes.Keys)}"
        : _notes.TryGetValue(key, out var value) ? value
        : $"No fact stored under '{key}', the keys are: {string.Join(", ", _notes.Keys)}";
}
