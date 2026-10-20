using System.Collections.Concurrent;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace Agent42.Backend;

// GHCP001: the SDK marks its SessionFs and permission RPC types [Experimental]. The copilot agent kind accepts that
// exposure (it pins the SDK version and its ADRs record the scope); the suppression covers this file only.
#pragma warning disable GHCP001

/// <summary>
/// The Copilot runtime's view of one conversation's files (<c>SessionFs</c>), over an
/// <see cref="ICopilotSessionStore"/>. One instance per runtime session. It holds no grain state, because the
/// runtime calls it from its own RPC thread.
///
/// <para><b>Directories are implied.</b> The store keeps files only. A directory exists when the runtime made it on
/// this instance, or when any stored file lies under it, which survives a restart. Measured for one tool turn:
/// <c>append=5, exists=1, mkdir=6, read=5, readdirT=1, rename=1, stat=2, write=2</c>, and no SQLite, so the
/// <c>ISessionFsSqliteProvider</c> capability is not offered.</para>
///
/// <para><b>Writers are serialized per conversation</b> through <paramref name="writeGate"/>. The SDK requires a
/// custom provider to serialize writers that touch the same session, and two runs can be in flight on one
/// conversation (a reminder firing during a chat turn). One replica (rule 12) and one runtime make an in-process
/// gate enough.</para>
///
/// <para><b>With built-in tools on, it routes.</b> Given a <paramref name="workspace"/>, only paths under the state
/// path (<c>/state</c>, <see cref="CopilotSessionPaths.IsStatePath"/>) go to the store, under exactly the keys they
/// had before, so existing chats resume. Every other path is the conversation's real workspace directory
/// (<see cref="CopilotWorkspaceFiles"/>), where the runtime's shell also works: a virtual working directory breaks the
/// shell (measured: every command failed with <c>os error 267</c>). Without one, every path goes to the store, as in
/// <c>Mode.Empty</c>.</para>
/// </summary>
public sealed class CopilotSessionFsProvider(
    ICopilotSessionStore store, string prefix, SemaphoreSlim writeGate, string? workspace = null)
    : SessionFsProvider
{
    private readonly ConcurrentDictionary<string, byte> _directories = new(StringComparer.Ordinal);
    private readonly CopilotWorkspaceFiles? _disk = workspace is null ? null : new CopilotWorkspaceFiles(workspace);

    protected override async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken)
        => Disk(path) is { } disk
            ? await disk.ReadFileAsync(path, cancellationToken)
            : await store.ReadAsync(Key(path), cancellationToken) ?? throw new FileNotFoundException(path);

    protected override Task WriteFileAsync(string path, string content, int? mode, CancellationToken cancellationToken)
        => Disk(path) is { } disk
            ? disk.WriteFileAsync(path, content, cancellationToken)
            : Gated(() => store.WriteAsync(Key(path), content, cancellationToken), cancellationToken);

    protected override Task AppendFileAsync(string path, string content, int? mode, CancellationToken cancellationToken)
        => Disk(path) is { } disk
            ? disk.AppendFileAsync(path, content, cancellationToken)
            : Gated(() => store.AppendAsync(Key(path), content, cancellationToken), cancellationToken);

    protected override async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
        => Disk(path) is { } disk
            ? disk.Exists(path)
            : await store.GetInfoAsync(Key(path), cancellationToken) is not null || await IsDirectoryAsync(path, cancellationToken);

    protected override async Task<SessionFsStatResult> StatAsync(string path, CancellationToken cancellationToken)
    {
        if (Disk(path) is { } disk)
        {
            return disk.Stat(path);
        }

        if (await store.GetInfoAsync(Key(path), cancellationToken) is { } file)
        {
            return new SessionFsStatResult
            {
                IsFile = true,
                IsDirectory = false,
                Size = file.Size,
                Mtime = file.Modified,
                Birthtime = file.Modified,
            };
        }

        if (await IsDirectoryAsync(path, cancellationToken))
        {
            var now = DateTimeOffset.UtcNow;
            return new SessionFsStatResult { IsFile = false, IsDirectory = true, Size = 0, Mtime = now, Birthtime = now };
        }

        throw new FileNotFoundException(path);
    }

    protected override Task MakeDirectoryAsync(string path, bool recursive, int? mode, CancellationToken cancellationToken)
    {
        if (Disk(path) is { } disk)
        {
            disk.MakeDirectory(path);
        }
        else
        {
            _directories[Key(path)] = 0;
        }

        return Task.CompletedTask;
    }

    protected override async Task<IList<string>> ReadDirectoryAsync(string path, CancellationToken cancellationToken)
        => [.. (await ReadDirectoryWithTypesAsync(path, cancellationToken)).Select(e => e.Name)];

    protected override async Task<IList<SessionFsReaddirWithTypesEntry>> ReadDirectoryWithTypesAsync(
        string path, CancellationToken cancellationToken)
        => Disk(path) is { } disk
            ? disk.ReadDirectory(path)
            : [.. (await ChildrenAsync(path, cancellationToken)).Select(c => new SessionFsReaddirWithTypesEntry
            {
                Name = c.Name,
                Type = c.IsDirectory ? SessionFsReaddirWithTypesEntryType.Directory : SessionFsReaddirWithTypesEntryType.File,
            })];

    protected override Task RemoveAsync(string path, bool recursive, bool force, CancellationToken cancellationToken)
    {
        if (Disk(path) is { } disk)
        {
            disk.Remove(path, recursive, force);
            return Task.CompletedTask;
        }

        return Gated(async () =>
        {
            var key = Key(path);
            await store.DeleteAsync(key, cancellationToken);
            foreach (var child in recursive ? await store.ListAsync(key + "/", cancellationToken) : [])
            {
                await store.DeleteAsync(child, cancellationToken);
            }

            _directories.TryRemove(key, out _);
        }, cancellationToken);
    }

    // Copy, then delete: a store has no rename. Under the gate, so no writer sees the file twice or not at all. A
    // rename between the store and the disk is refused: the transcript never leaves the store.
    protected override Task RenameAsync(string src, string dest, CancellationToken cancellationToken)
    {
        if (_disk is not null && CopilotSessionPaths.IsStatePath(src) != CopilotSessionPaths.IsStatePath(dest))
        {
            throw new UnauthorizedAccessException($"Renaming '{src}' to '{dest}' moves a file in or out of the session state.");
        }

        if (Disk(src) is { } disk)
        {
            disk.Rename(src, dest);
            return Task.CompletedTask;
        }

        return Gated(async () =>
        {
            var content = await store.ReadAsync(Key(src), cancellationToken) ?? throw new FileNotFoundException(src);
            await store.WriteAsync(Key(dest), content, cancellationToken);
            await store.DeleteAsync(Key(src), cancellationToken);
        }, cancellationToken);
    }

    // The workspace when the path belongs on disk; null when it belongs in the store.
    private CopilotWorkspaceFiles? Disk(string path) => CopilotSessionPaths.IsStatePath(path) ? null : _disk;

    private string Key(string path) => CopilotSessionPaths.KeyFor(prefix, path);

    private async Task<bool> IsDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var key = Key(path);
        return key == prefix || _directories.ContainsKey(key) || (await store.ListAsync(key + "/", cancellationToken)).Count > 0;
    }

    // The immediate children of a directory: the first segment of every stored key under it, plus the directories
    // made on this instance. A child is a directory when anything lies below it.
    private async Task<List<(string Name, bool IsDirectory)>> ChildrenAsync(string path, CancellationToken cancellationToken)
    {
        var key = Key(path);
        var under = key.EndsWith('/') ? key : key + "/";
        var relative = (await store.ListAsync(under, cancellationToken)).Select(k => k[under.Length..])
            .Concat(_directories.Keys.Where(d => d.StartsWith(under, StringComparison.Ordinal)).Select(d => d[under.Length..] + "/"));

        return [.. relative
            .Where(r => r.Length > 0)
            .GroupBy(r => r.Split('/')[0], StringComparer.Ordinal)
            .Select(g => (g.Key, g.Any(r => r.Contains('/'))))];
    }

    private async Task Gated(Func<Task> write, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await write();
        }
        finally
        {
            writeGate.Release();
        }
    }
}
