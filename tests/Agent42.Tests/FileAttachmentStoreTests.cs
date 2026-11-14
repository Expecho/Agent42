using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>AC8 (store half): the local attachment store, and the constant reference URL it hands the grain.</summary>
public sealed class FileAttachmentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent42-att-" + Guid.NewGuid().ToString("N"));
    private readonly FileAttachmentStore _store;

    public FileAttachmentStoreTests() => _store = new FileAttachmentStore(new LocalFileStore(_root));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveThenRead_ReturnsTheBytes_TheStoredMediaType_AndTheFileName()
    {
        var saved = await _store.SaveAsync("user-1/conv-1", "café photo.png", "image/png", [1, 2, 3]);

        Assert.Equal("user-1/conv-1/" + saved.Id, saved.BlobPath);
        var content = await _store.OpenReadAsync("user-1/conv-1", saved.Id);
        Assert.Equal(new byte[] { 1, 2, 3 }, content!.Bytes);
        Assert.Equal("image/png", content.MediaType);
        Assert.Equal("café photo.png", content.FileName);

        var reference = await _store.GetAsync("user-1/conv-1", saved.Id);
        Assert.Equal(saved, reference);
    }

    [Fact]
    public async Task TheStoredMediaType_IsNeverInferredFromTheFileName()
    {
        // Artifact bytes are saved as application/octet-stream (gotchas): the name says html, the type must stay.
        var saved = await _store.SaveAsync("u/c", "page.html", "application/octet-stream", "<b>x</b>"u8.ToArray());
        Assert.Equal("application/octet-stream", (await _store.OpenReadAsync("u/c", saved.Id))!.MediaType);
        Assert.Equal("application/octet-stream", (await _store.GetAsync("u/c", saved.Id))!.MediaType);
    }

    [Fact]
    public async Task AnotherOwner_CannotReachTheBytes_AndAMissingIdIsNull()
    {
        var saved = await _store.SaveAsync("user-1/conv-1", "a.png", "image/png", [1]);

        Assert.Null(await _store.OpenReadAsync("user-2/conv-1", saved.Id));
        Assert.Null(await _store.GetAsync("user-1/conv-2", saved.Id));
        Assert.Null(await _store.OpenReadAsync("user-1/conv-1", "nope"));
    }

    [Fact]
    public async Task AnIdOrOwnerThatTriesToTraverse_NeverLeavesItsFolder()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.OpenReadAsync("user-1/..", "x"));
        // An id of ".." only ever reaches the sidecar name "...json", which is an ordinary missing file.
        Assert.Null(await _store.OpenReadAsync("user-1/conv", ".."));

        // A sidecar can never be read as content: the id is escaped, and the sidecars live in their own folder.
        var saved = await _store.SaveAsync("u/c", "a.png", "image/png", [5]);
        Assert.Null(await _store.OpenReadAsync("u/c", saved.Id + ".json"));
        Assert.Null(await _store.OpenReadAsync("u/c", "../" + saved.Id));
    }

    [Fact]
    public async Task ReadUrl_IsAConstantReference_NotAFetchableOrInlineUrl()
    {
        var saved = await _store.SaveAsync("user-1/conv 1", "a.png", "image/png", new byte[100_000]);

        var first = await _store.GetReadUrlAsync(saved);
        var second = await _store.GetReadUrlAsync(saved);

        Assert.Equal(first, second);
        Assert.Equal(FileAttachmentStore.ReferenceScheme, first.Scheme);
        Assert.True(first.ToString().Length < 200, "the URL is persisted in the 2 MB session, so it must stay tiny");
        Assert.DoesNotContain("data:", first.ToString());
    }

    [Fact]
    public async Task ReferenceUrl_ParsesBackToOwnerAndId()
    {
        var saved = await _store.SaveAsync("user-1/conv/with/slashes and spaces", "a.png", "image/png", [1]);
        var url = await _store.GetReadUrlAsync(saved);

        Assert.True(FileAttachmentStore.TryParseReference(url, out var owner, out var id));
        Assert.Equal("user-1/conv/with/slashes and spaces", owner);
        Assert.Equal(saved.Id, id);
        Assert.NotNull(await _store.OpenReadAsync(owner, id));
    }

    [Theory]
    [InlineData("https://example.com/a.png")]
    [InlineData("inmemory://attachment/abc")]
    [InlineData("agent42-attachment://local/onlyone")]
    public void TryParseReference_RejectsAnythingElse(string url)
    {
        Assert.False(FileAttachmentStore.TryParseReference(new Uri(url), out _, out _));
    }

    [Fact]
    public async Task Copy_KeepsTheIdTypeAndName_AndLeavesTheSource()
    {
        var source = await _store.SaveAsync("user-1/conv-1", "pic.png", "image/png", [8, 9]);

        var copy = await _store.CopyAsync(source, "user-1/conv-2");

        Assert.Equal(source.Id, copy.Id);
        Assert.Equal("user-1/conv-2/" + source.Id, copy.BlobPath);
        var content = await _store.OpenReadAsync("user-1/conv-2", source.Id);
        Assert.Equal(new byte[] { 8, 9 }, content!.Bytes);
        Assert.Equal("image/png", content.MediaType);
        Assert.Equal("pic.png", content.FileName);
        Assert.NotNull(await _store.OpenReadAsync("user-1/conv-1", source.Id));
    }

    [Fact]
    public async Task Copy_OfAMissingSource_Throws()
    {
        var ghost = new AttachmentRef("ghost", "user-1/conv-1/ghost", "image/png", "g.png");
        await Assert.ThrowsAsync<FileNotFoundException>(() => _store.CopyAsync(ghost, "user-1/conv-2"));
    }

    [Fact]
    public async Task ANewStoreOverTheSameVolume_SeesTheAttachment()
    {
        var saved = await _store.SaveAsync("u/c", "a.png", "image/png", [4]);
        var restarted = new FileAttachmentStore(new LocalFileStore(_root));
        Assert.Equal(new byte[] { 4 }, (await restarted.OpenReadAsync("u/c", saved.Id))!.Bytes);
    }
}
