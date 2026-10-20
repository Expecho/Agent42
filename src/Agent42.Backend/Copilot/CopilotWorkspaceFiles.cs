using GitHub.Copilot.Rpc;

namespace Agent42.Backend;

// GHCP001: the SDK marks its SessionFs RPC types [Experimental]; the copilot agent kind accepts that (ADR 0085).
#pragma warning disable GHCP001

/// <summary>
/// The disk side of <see cref="CopilotSessionFsProvider"/> when built-in tools are on: one conversation's real
/// workspace directory (on the Azure Files share in production). The runtime's built-in file tools reach the disk
/// through here, and its shell works in the same directory, so the two agree.
///
/// <para><b>Nothing outside the workspace.</b> The permission policy already refuses file tools outside it, but a
/// create can reach SessionFs without raising a permission at all (measured for the state path), so this is the
/// backstop. A path outside the workspace (after resolving symlinks) does not exist to a read, which is also how a
/// resumed pre-built-ins chat's <c>/work</c> cwd is answered (measured: the runtime then uses the session's
/// <c>WorkingDirectory</c>), and is refused to a write.</para>
/// </summary>
internal sealed class CopilotWorkspaceFiles(string workspace)
{
    private readonly string _root = CopilotWorkspacePaths.Root(workspace);

    public Task<string> ReadFileAsync(string path, CancellationToken cancellationToken)
        => NotFoundIfMissing(path, () => File.ReadAllTextAsync(Readable(path), cancellationToken));

    public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken)
        => NotFoundIfMissing(path, () => File.WriteAllTextAsync(Writable(path), content, cancellationToken));

    public Task AppendFileAsync(string path, string content, CancellationToken cancellationToken)
        => NotFoundIfMissing(path, () => File.AppendAllTextAsync(Writable(path), content, cancellationToken));

    public bool Exists(string path) => TryResolve(path) is { } full && Path.Exists(full);

    public SessionFsStatResult Stat(string path)
    {
        var full = Readable(path);
        FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        return info.Exists
            ? new SessionFsStatResult
            {
                IsFile = info is FileInfo,
                IsDirectory = info is DirectoryInfo,
                Size = info is FileInfo file ? file.Length : 0,
                Mtime = info.LastWriteTimeUtc,
                Birthtime = info.CreationTimeUtc,
            }
            : throw new FileNotFoundException(path);
    }

    public void MakeDirectory(string path) => Directory.CreateDirectory(Writable(path));

    public IList<SessionFsReaddirWithTypesEntry> ReadDirectory(string path)
    {
        var directory = new DirectoryInfo(Readable(path));
        return directory.Exists
            ? [.. directory.EnumerateFileSystemInfos().Select(i => new SessionFsReaddirWithTypesEntry
            {
                Name = i.Name,
                Type = i is DirectoryInfo ? SessionFsReaddirWithTypesEntryType.Directory : SessionFsReaddirWithTypesEntryType.File,
            })]
            : throw new FileNotFoundException(path);
    }

    public void Remove(string path, bool recursive, bool force)
    {
        var full = Below(path);
        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive);
        }
        else if (File.Exists(full))
        {
            File.Delete(full);
        }
        else if (!force)
        {
            throw new FileNotFoundException(path);
        }
    }

    public void Rename(string src, string dest)
    {
        var (from, to) = (Below(src), Below(dest));
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            MoveFile(src, () => File.Move(from, to, overwrite: true));
        }
    }

    // A read outside the workspace is a not-found: to the runtime, there is nothing there.
    private string Readable(string path) => TryResolve(path) ?? throw new FileNotFoundException(path);

    private string Writable(string path)
        => TryResolve(path) ?? throw new UnauthorizedAccessException($"'{path}' is outside this conversation's workspace {_root}.");

    // A path strictly below the workspace: the workspace itself is the session's working directory, never removed or
    // renamed by the runtime (deleting a chat removes it).
    private string Below(string path)
    {
        var full = Writable(path);
        return full.Equals(_root, CopilotWorkspacePaths.Comparison)
            ? throw new UnauthorizedAccessException($"'{path}' is this conversation's workspace itself.")
            : full;
    }

    // The full path when it lies inside the workspace, and so does every symlink on the way to it. SMB has no
    // symlinks, but a local root (dev, live tests) can, and a link must not lead the runtime out.
    private string? TryResolve(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, _root));
        if (!CopilotWorkspacePaths.IsInside(_root, full))
        {
            return null;
        }

        for (var current = full; current.Length > _root.Length; current = Path.GetDirectoryName(current)!)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null
                && (info.ResolveLinkTarget(returnFinalTarget: true) is not { } target
                    || !CopilotWorkspacePaths.IsInside(_root, target.FullName)))
            {
                return null;
            }
        }

        return full;
    }

    // The runtime understands a FileNotFoundException as "no such file"; .NET throws DirectoryNotFoundException when
    // a parent is missing, which means the same thing here.
    private static async Task<T> NotFoundIfMissing<T>(string path, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (DirectoryNotFoundException)
        {
            throw new FileNotFoundException(path);
        }
    }

    private static async Task NotFoundIfMissing(string path, Func<Task> action)
        => await NotFoundIfMissing(path, async () => { await action(); return true; });

    private static void MoveFile(string path, Action action)
    {
        try
        {
            action();
        }
        catch (DirectoryNotFoundException)
        {
            throw new FileNotFoundException(path);
        }
    }
}
