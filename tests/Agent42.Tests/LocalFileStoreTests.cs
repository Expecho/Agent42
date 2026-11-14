using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>AC7: no key can escape the storage root, and the primitive behaves like the blob calls it stands in for.</summary>
public sealed class LocalFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent42-fs-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileStore _store;

    public LocalFileStoreTests() => _store = new LocalFileStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32")]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("a/../../outside")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("a/b/")]
    [InlineData("a\\b")]
    [InlineData("C:/windows/win.ini")]
    [InlineData("a/b:stream")]
    [InlineData("a/\0b")]
    [InlineData("a/con")]
    [InlineData("a/NUL.txt")]
    [InlineData("a/lpt1")]
    [InlineData("a/b.")]
    [InlineData("a/b ")]
    [InlineData("a/x.agent42tmp")]
    public void Resolve_RejectsKeysThatCouldLeaveTheRootOrConfuseTheFileSystem(string key)
    {
        Assert.Throws<ArgumentException>(() => _store.Resolve(key));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("user-1/conv-2/files/notes.txt")]
    [InlineData("0f3c/9a.zip")]
    [InlineData("a/.hidden")]
    [InlineData("a/b..c")]
    public void Resolve_AcceptsOrdinaryKeys_UnderTheRoot(string key)
    {
        var path = _store.Resolve(key);
        Assert.StartsWith(_store.Root + Path.DirectorySeparatorChar, path);
    }

    [Fact]
    public async Task EveryOperation_RefusesATraversalKey_AndTouchesNothingOutside()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "agent42-escape-" + Guid.NewGuid().ToString("N"));
        var key = "../" + Path.GetFileName(outside);

        await Assert.ThrowsAsync<ArgumentException>(() => _store.WriteAsync(key, new byte[] { 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.AppendAsync(key, new byte[] { 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.ReadAsync(key));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteAsync(key));
        Assert.Throws<ArgumentException>(() => _store.GetInfo(key));
        Assert.Throws<ArgumentException>(() => _store.Copy("ok", key));
        Assert.Throws<ArgumentException>(() => _store.List("../"));
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task Write_ThenRead_RoundTrips_AndReplacesAtomically()
    {
        await _store.WriteAsync("a/b/c.bin", new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, await _store.ReadAsync("a/b/c.bin"));

        await _store.WriteAsync("a/b/c.bin", new byte[] { 9 });
        Assert.Equal(new byte[] { 9 }, await _store.ReadAsync("a/b/c.bin"));
        Assert.Empty(Directory.GetFiles(_root, "*" + LocalFileStore.TempSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Read_OfAMissingKey_IsNull_EvenWhenTheFolderIsMissing()
    {
        Assert.Null(await _store.ReadAsync("nope/at/all"));
        Assert.Null(_store.GetInfo("nope/at/all"));
        Assert.False(_store.Exists("nope/at/all"));
    }

    [Fact]
    public async Task Append_CreatesTheFile_ThenAdds()
    {
        await _store.AppendAsync("log/1", "ab"u8.ToArray());
        await _store.AppendAsync("log/1", "cd"u8.ToArray());
        Assert.Equal("abcd"u8.ToArray(), await _store.ReadAsync("log/1"));
        Assert.Equal(4, _store.GetInfo("log/1")!.Size);
    }

    [Fact]
    public async Task ConcurrentAppends_AreAllKept()
    {
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => _store.AppendAsync("log/c", "x"u8.ToArray())));
        Assert.Equal(50, (await _store.ReadAsync("log/c"))!.Length);
    }

    [Fact]
    public async Task List_MatchesARawPrefix_AtAnyDepth_AndSkipsTempFiles()
    {
        await _store.WriteAsync("u1/c1/a.txt", new byte[] { 1 });
        await _store.WriteAsync("u1/c1/sub/b.txt", new byte[] { 1, 2 });
        await _store.WriteAsync("u1/c10/z.txt", new byte[] { 1 });
        await _store.WriteAsync("u2/c1/a.txt", new byte[] { 1 });
        await File.WriteAllBytesAsync(_store.Resolve("u1/c1/half") + ".x" + LocalFileStore.TempSuffix, new byte[] { 7 });

        var folder = _store.List("u1/c1/").Select(i => i.Key).Order().ToArray();
        Assert.Equal(["u1/c1/a.txt", "u1/c1/sub/b.txt"], folder);

        var raw = _store.List("u1/c1").Select(i => i.Key).Order().ToArray();
        Assert.Equal(["u1/c1/a.txt", "u1/c1/sub/b.txt", "u1/c10/z.txt"], raw);

        Assert.Equal(2, _store.List("u1/c1/sub/b.txt").Single().Size);
        Assert.Equal(4, _store.List("").Count);
    }

    [Fact]
    public void List_OfAPrefixWithNoFolder_IsEmpty()
    {
        Assert.Empty(_store.List("ghost/"));
        Assert.Empty(_store.List("ghost/prefix"));
    }

    [Fact]
    public async Task Delete_RemovesTheFile_AndAMissingOneIsNotAnError()
    {
        await _store.WriteAsync("d/x", new byte[] { 1 });
        await _store.DeleteAsync("d/x");
        await _store.DeleteAsync("d/x");
        await _store.DeleteAsync("never/was");
        Assert.Null(await _store.ReadAsync("d/x"));
    }

    [Fact]
    public async Task Copy_DuplicatesTheBytes_AndReportsAMissingSource()
    {
        await _store.WriteAsync("s/a", new byte[] { 4, 5 });
        Assert.True(_store.Copy("s/a", "t/deep/a"));
        Assert.Equal(new byte[] { 4, 5 }, await _store.ReadAsync("t/deep/a"));
        Assert.Equal(new byte[] { 4, 5 }, await _store.ReadAsync("s/a"));
        Assert.False(_store.Copy("s/missing", "t/x"));
    }

    [Fact]
    public async Task ANewStoreOverTheSameDirectory_SeesTheData()
    {
        await _store.WriteAsync("keep/me", new byte[] { 1 });
        var second = new LocalFileStore(_root);
        Assert.Equal(new byte[] { 1 }, await second.ReadAsync("keep/me"));
    }
}
