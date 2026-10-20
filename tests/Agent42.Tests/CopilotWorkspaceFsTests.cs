using Agent42.Backend;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

#pragma warning disable GHCP001 // The SDK's SessionFs RPC types are [Experimental]; these tests pin them.

namespace Agent42.Tests;

/// <summary>
/// The SessionFs router of a copilot chat with built-in tools on (ADR 0107): the state path goes to the store under
/// the same keys as before, every other path is the conversation's real workspace on disk, and nothing outside the
/// workspace is reachable. Also the workspace path helper that every caller builds the directory with.
/// </summary>
public sealed class CopilotWorkspaceFsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "copilot-ws-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryCopilotSessionStore _store = new();
    private readonly string _prefix = CopilotSessionPaths.ConversationPrefix("user-1", "conv-1");
    private readonly string _workspace;
    private readonly ISessionFsHandler _fs;

    public CopilotWorkspaceFsTests()
    {
        _workspace = CopilotWorkspacePaths.For(_root, "user-1", "conv-1");
        Directory.CreateDirectory(_workspace);
        _fs = new CopilotSessionFsProvider(_store, _prefix, new SemaphoreSlim(1, 1), _workspace);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // AC4: the transcript lands in the store under exactly the key a Mode.Empty session wrote, and never on disk.
    [Fact]
    public async Task StatePath_UsesTodaysKeys()
    {
        await _fs.MkdirAsync(new SessionFsMkdirRequest { Path = "/state/checkpoints", Recursive = true }, default);
        await _fs.AppendFileAsync(new SessionFsAppendFileRequest { Path = "/state/events.jsonl", Content = "{}\n" }, default);
        await _fs.WriteFileAsync(new SessionFsWriteFileRequest { Path = "/state/workspace.yaml.tmp", Content = "id: 1" }, default);
        await _fs.RenameAsync(new SessionFsRenameRequest { Src = "/state/workspace.yaml.tmp", Dest = "/state/workspace.yaml" }, default);

        Assert.Equal(
            [CopilotSessionPaths.KeyFor(_prefix, "/state/events.jsonl"), CopilotSessionPaths.KeyFor(_prefix, "/state/workspace.yaml")],
            (await _store.ListAsync("")).Order());
        Assert.Equal(_prefix + "state/events.jsonl", CopilotSessionPaths.KeyFor(_prefix, "/state/events.jsonl"));
        Assert.True((await _fs.StatAsync(new SessionFsStatRequest { Path = "/state" }, default)).IsDirectory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, "state", SearchOption.AllDirectories));
    }

    // AC4: every operation on a workspace path hits the real disk, where the shell would see it.
    [Fact]
    public async Task WorkspacePath_AllOperationsHitDisk()
    {
        var dir = Path.Combine(_workspace, "src");
        var file = Path.Combine(dir, "note.txt");

        await _fs.MkdirAsync(new SessionFsMkdirRequest { Path = dir, Recursive = true }, default);
        await _fs.WriteFileAsync(new SessionFsWriteFileRequest { Path = file, Content = "c1" }, default);
        await _fs.AppendFileAsync(new SessionFsAppendFileRequest { Path = file, Content = "+more" }, default);

        Assert.Equal("c1+more", await File.ReadAllTextAsync(file));
        Assert.Equal("c1+more", (await _fs.ReadFileAsync(new SessionFsReadFileRequest { Path = file }, default)).Content);
        Assert.True((await _fs.ExistsAsync(new SessionFsExistsRequest { Path = file }, default)).Exists);
        var stat = await _fs.StatAsync(new SessionFsStatRequest { Path = file }, default);
        Assert.True(stat.IsFile);
        Assert.Equal(7, stat.Size);
        Assert.True((await _fs.StatAsync(new SessionFsStatRequest { Path = dir }, default)).IsDirectory);
        var entries = (await _fs.ReaddirWithTypesAsync(new SessionFsReaddirWithTypesRequest { Path = _workspace }, default)).Entries;
        Assert.Equal(SessionFsReaddirWithTypesEntryType.Directory, Assert.Single(entries, e => e.Name == "src").Type);
        Assert.Equal(["note.txt"], (await _fs.ReaddirAsync(new SessionFsReaddirRequest { Path = dir }, default)).Entries);

        var renamed = Path.Combine(_workspace, "renamed.txt");
        await _fs.RenameAsync(new SessionFsRenameRequest { Src = file, Dest = renamed }, default);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(renamed));

        await _fs.RmAsync(new SessionFsRmRequest { Path = dir, Recursive = true }, default);
        Assert.False(Directory.Exists(dir));
        Assert.NotNull((await _fs.ReadFileAsync(new SessionFsReadFileRequest { Path = file }, default)).Error);
        Assert.Empty(await _store.ListAsync(""));
    }

    // AC4: outside both the workspace and the state path, nothing exists to a read (how a legacy '/work' cwd is
    // answered) and every write is refused, including another conversation's workspace and a rename out of state.
    [Fact]
    public async Task OutsideWorkspace_Refused()
    {
        var other = CopilotWorkspacePaths.For(_root, "user-1", "conv-2");
        Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(Path.Combine(other, "secret.txt"), "theirs");

        foreach (var outside in new[] { Path.Combine(other, "secret.txt"), "/work", _workspace + "-sibling" })
        {
            Assert.False((await _fs.ExistsAsync(new SessionFsExistsRequest { Path = outside }, default)).Exists);
            Assert.NotNull((await _fs.ReadFileAsync(new SessionFsReadFileRequest { Path = outside }, default)).Error);
            Assert.NotNull((await _fs.StatAsync(new SessionFsStatRequest { Path = outside }, default)).Error);
            Assert.NotNull(await _fs.WriteFileAsync(new SessionFsWriteFileRequest { Path = outside, Content = "x" }, default));
            Assert.NotNull(await _fs.RmAsync(new SessionFsRmRequest { Path = outside, Force = true }, default));
        }

        Assert.Equal("theirs", await File.ReadAllTextAsync(Path.Combine(other, "secret.txt")));
        await _fs.WriteFileAsync(new SessionFsWriteFileRequest { Path = "/state/events.jsonl", Content = "{}" }, default);
        Assert.NotNull(await _fs.RenameAsync(
            new SessionFsRenameRequest { Src = "/state/events.jsonl", Dest = Path.Combine(_workspace, "events.jsonl") }, default));
        Assert.False(File.Exists(Path.Combine(_workspace, "events.jsonl")));
    }

    // AC4: an id that would step out of the root on disk ('.', '..', a trailing dot SMB strips) cannot name a
    // workspace, and a separator in an id is escaped into one segment.
    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("x.")]
    [InlineData("")]
    public void Traversal_Refused(string id)
    {
        Assert.Throws<UnauthorizedAccessException>(() => CopilotWorkspacePaths.For(_root, "user-1", id));
        Assert.Throws<UnauthorizedAccessException>(() => CopilotWorkspacePaths.For(_root, id, "conv-1"));
    }

    [Fact]
    public void Separators_StayInOneSegment()
    {
        foreach (var id in new[] { "a/b", "a\\b", "../x", "a*b" })
        {
            var path = CopilotWorkspacePaths.For(_root, "user-1", id);
            Assert.Equal(Path.Combine(CopilotWorkspacePaths.Root(_root), "user-1"), Path.GetDirectoryName(path));
        }
    }

    // AC4: a symlink inside the workspace that points outside it does not lead the runtime out. (SMB has no symlinks,
    // but a local root can.) Skipped quietly where the OS won't let the test make one.
    [Fact]
    public async Task SymlinkOutside_Refused()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "secret");
        var link = Path.Combine(_workspace, "link");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.NotNull((await _fs.ReadFileAsync(new SessionFsReadFileRequest { Path = Path.Combine(link, "secret.txt") }, default)).Error);
        Assert.NotNull(await _fs.WriteFileAsync(new SessionFsWriteFileRequest { Path = Path.Combine(link, "new.txt"), Content = "x" }, default));
        Assert.False(File.Exists(Path.Combine(outside, "new.txt")));
    }

    // AC4: the state path is recognised in every convention a runtime can send it in, and a workspace subdirectory
    // that happens to be called 'state' is not.
    [Fact]
    public void StatePath_WindowsAndPosix_Classified()
    {
        Assert.True(CopilotSessionPaths.IsStatePath("/state"));
        Assert.True(CopilotSessionPaths.IsStatePath("/state/events.jsonl"));
        Assert.True(CopilotSessionPaths.IsStatePath(@"\state\checkpoints\index.md"));
        Assert.True(CopilotSessionPaths.IsStatePath(@"C:\state\events.jsonl"));
        Assert.False(CopilotSessionPaths.IsStatePath("/statefile"));
        Assert.False(CopilotSessionPaths.IsStatePath("state/x"));
        Assert.False(CopilotSessionPaths.IsStatePath(Path.Combine(_workspace, "state", "x")));
        Assert.Equal(_prefix + "state/events.jsonl", CopilotSessionPaths.KeyFor(_prefix, @"C:\state\events.jsonl"));
    }
}
