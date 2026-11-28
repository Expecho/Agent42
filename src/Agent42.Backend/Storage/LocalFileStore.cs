using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The filesystem primitive under every local blob store (self-hosting plan, Step 3): keys are the same
/// <c>a/b/c</c> strings the Azure stores use as blob names, and each becomes a file under one root directory.
///
/// <para><b><see cref="Resolve"/> is the ONLY place a key becomes a path.</b> Several keys carry text that is not
/// ours: a Copilot runtime path, a user or conversation id, an uploaded file's name. A key that is rooted, has a
/// <c>..</c> or empty segment, a backslash, a colon or a control character, a Windows device name, or a trailing dot
/// or space is refused with <see cref="ArgumentException"/>, and the resolved full path must still sit under the root.
/// Symbolic links inside the root are not chased: the root is the operator's own volume.</para>
///
/// <para>Writes are atomic (a temp file, then a rename), so a crash never leaves half a file. Appends and writes to
/// one path are serialized in process; the app is a single replica (CLAUDE.md rule 12).</para>
/// </summary>
public sealed class LocalFileStore
{
    /// <summary>Suffix of an in-flight write. Never a valid key segment ending, and never listed.</summary>
    public const string TempSuffix = ".agent42tmp";

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const int LockStripes = 64;

    private readonly string _root;
    private readonly string _rootWithSeparator;
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, LockStripes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public LocalFileStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _rootWithSeparator = _root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_root);
    }

    /// <summary>The absolute root directory.</summary>
    public string Root => _root;

    /// <summary>Turns a key into an absolute path under the root, or throws. The only key-to-path point.</summary>
    public string Resolve(string key)
    {
        ValidateKey(key, allowPartialLastSegment: false);
        return ResolveUnchecked(key);
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Creates or replaces the file atomically.</summary>
    public async Task WriteAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + TempSuffix;
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await stream.WriteAsync(content, cancellationToken);
        }

        var gate = GateFor(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Appends to the file, creating it (and its folders) when it does not exist.</summary>
    public async Task AppendAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var gate = GateFor(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            await stream.WriteAsync(content, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Creates an empty file when none exists (ADR 0153); an existing file is left alone.</summary>
    public async Task CreateEmptyAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        var gate = GateFor(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Appends only when the file exists and is exactly <paramref name="expectedOffset"/> bytes long (ADR 0153). A missing file is
    /// never created here, at any offset: that is the delete fence. The length check and the write are one step under the
    /// file's gate, so two writers cannot both pass it.
    /// </summary>
    public async Task<ChatTurnAppendResult> AppendAtAsync(string key, ReadOnlyMemory<byte> content, long expectedOffset, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        var gate = GateFor(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new ChatTurnAppendResult(ChatTurnAppendOutcome.Missing, null);
            }

            var length = info.Length;
            if (length != expectedOffset)
            {
                return new ChatTurnAppendResult(ChatTurnAppendOutcome.OffsetMismatch, length);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            await stream.WriteAsync(content, cancellationToken);
            return new ChatTurnAppendResult(ChatTurnAppendOutcome.Appended, length + content.Length);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The file's size and last write, or null when it does not exist.</summary>
    public LocalFileInfo? GetInfo(string key)
    {
        var info = new FileInfo(Resolve(key));
        return info.Exists ? new LocalFileInfo(info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)) : null;
    }

    /// <summary>True when the file exists.</summary>
    public bool Exists(string key) => File.Exists(Resolve(key));

    /// <summary>Deletes the file. A missing file is not an error.</summary>
    public Task DeleteAsync(string key)
    {
        var path = Resolve(key);
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // File.Delete is silent on a missing file but throws on a missing folder; both mean "already gone".
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Copies a file to another key, replacing any file already there. Returns false when the source does not exist.
    /// </summary>
    public bool Copy(string sourceKey, string targetKey)
    {
        var source = Resolve(sourceKey);
        var target = Resolve(targetKey);
        if (!File.Exists(source))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + "." + Guid.NewGuid().ToString("N") + TempSuffix;
        File.Copy(source, temp);
        File.Move(temp, target, overwrite: true);
        return true;
    }

    /// <summary>
    /// Every key that starts with <paramref name="prefix"/>, at any depth, with its size. A prefix is a raw string
    /// prefix like a blob prefix, so <c>a/b/c</c> also matches <c>a/b/cd</c>. A prefix with no folder yet is an
    /// empty list, never an error.
    /// </summary>
    public IReadOnlyList<LocalFileItem> List(string prefix)
    {
        ValidateKey(prefix, allowPartialLastSegment: true);
        var slash = prefix.LastIndexOf('/');
        var directory = slash < 0 ? _root : ResolveUnchecked(prefix[..slash]);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var items = new List<LocalFileItem>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (file.Name.EndsWith(TempSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var key = Path.GetRelativePath(_root, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                items.Add(new LocalFileItem(key, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
            }
        }

        return items;
    }

    private string ResolveUnchecked(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_rootWithSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException("The key resolves outside the storage root.", nameof(key));
        }

        return full;
    }

    private SemaphoreSlim GateFor(string path)
        => _locks[(StringComparer.Ordinal.GetHashCode(path) & int.MaxValue) % LockStripes];

    private static void ValidateKey(string key, bool allowPartialLastSegment)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!allowPartialLastSegment && key.Length == 0)
        {
            throw new ArgumentException("A storage key cannot be empty.", nameof(key));
        }

        if (key.Length > 0 && (key[0] == '/' || key[0] == '\\'))
        {
            throw new ArgumentException("A storage key cannot be rooted.", nameof(key));
        }

        var segments = key.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            // A prefix may end in a slash (empty last segment) or in half a name.
            var isPartialLast = allowPartialLastSegment && i == segments.Length - 1;
            ValidateSegment(segments[i], isPartialLast, key);
        }
    }

    private static void ValidateSegment(string segment, bool partial, string key)
    {
        if (segment.Length == 0)
        {
            if (partial)
            {
                return;
            }

            throw new ArgumentException($"A storage key cannot have an empty segment: '{key}'.", nameof(key));
        }

        if (segment is "." or "..")
        {
            throw new ArgumentException($"A storage key cannot contain '{segment}' segments: '{key}'.", nameof(key));
        }

        if (segment.Any(c => c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c)))
        {
            throw new ArgumentException($"A storage key segment has a character no file name may carry: '{key}'.", nameof(key));
        }

        if (!partial && (segment[^1] is '.' or ' ' || segment.EndsWith(TempSuffix, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"A storage key segment cannot end in a dot, a space or '{TempSuffix}': '{key}'.", nameof(key));
        }

        var stem = segment.Split('.')[0].TrimEnd();
        if (DeviceNames.Contains(stem))
        {
            throw new ArgumentException($"A storage key segment cannot be a reserved device name: '{key}'.", nameof(key));
        }
    }
}

/// <summary>A local file's size and last write.</summary>
public sealed record LocalFileInfo(long Size, DateTimeOffset Modified);

/// <summary>A key, its size in bytes and its last write (ADR 0153: the chat-turn sweeper needs the age).</summary>
public sealed record LocalFileItem(string Key, long Size, DateTimeOffset Modified = default);
