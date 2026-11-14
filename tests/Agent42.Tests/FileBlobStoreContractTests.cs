using System.Text;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// AC6: the file stores satisfy the same contract as the in-memory twins, and survive a restart. Every case runs against
/// both, so a behavior the twin has and the file store lacks (or the reverse) shows up as one failing row.
/// </summary>
public sealed class FileBlobStoreContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent42-contract-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public static TheoryData<string> Backends() => new() { "memory", "file" };

    private IFleetBlobs Fleet(string backend) => backend == "file"
        ? new FileFleetBlobs(new LocalFileStore(_root))
        : new InMemoryFleetBlobs();

    private ICopilotSessionStore Copilot(string backend) => backend == "file"
        ? new FileCopilotSessionStore(new LocalFileStore(_root))
        : new InMemoryCopilotSessionStore();

    private ISkillPackageStore Skills(string backend) => backend == "file"
        ? new FileSkillPackageStore(new LocalFileStore(_root))
        : new InMemorySkillPackageStore();

    private static string Unique() => "u" + Guid.NewGuid().ToString("N");

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Fleet_PutGetAppendListDelete(string backend)
    {
        var blobs = Fleet(backend);
        var u = Unique();

        Assert.Null(await blobs.GetAsync($"{u}/tasks/missing.json"));
        Assert.Empty(await blobs.ListAsync($"{u}/"));

        await blobs.PutAsync($"{u}/tasks/a.json", "one"u8.ToArray());
        await blobs.PutAsync($"{u}/tasks/a.json", "two"u8.ToArray());
        await blobs.AppendAsync($"{u}/events/a.ndjson", "x\n"u8.ToArray());
        await blobs.AppendAsync($"{u}/events/a.ndjson", "y\n"u8.ToArray());

        Assert.Equal("two", Encoding.UTF8.GetString((await blobs.GetAsync($"{u}/tasks/a.json"))!));
        Assert.Equal("x\ny\n", Encoding.UTF8.GetString((await blobs.GetAsync($"{u}/events/a.ndjson"))!));

        var listed = (await blobs.ListAsync($"{u}/")).OrderBy(i => i.Key).ToList();
        Assert.Equal([$"{u}/events/a.ndjson", $"{u}/tasks/a.json"], listed.Select(i => i.Key));
        Assert.Equal([4L, 3L], listed.Select(i => i.Size));
        Assert.Single(await blobs.ListAsync($"{u}/tasks/"));

        await blobs.DeleteAsync($"{u}/tasks/a.json");
        await blobs.DeleteAsync($"{u}/tasks/a.json");
        Assert.Null(await blobs.GetAsync($"{u}/tasks/a.json"));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Copilot_ReadWriteAppendInfoListDelete(string backend)
    {
        var store = Copilot(backend);
        var prefix = CopilotSessionPaths.ConversationPrefix(Unique(), "conv 1");

        Assert.Null(await store.ReadAsync(prefix + "state/events.jsonl"));
        Assert.Null(await store.GetInfoAsync(prefix + "state/events.jsonl"));
        Assert.Empty(await store.ListAsync(prefix));

        await store.WriteAsync(prefix + "state/events.jsonl", "héllo\n");
        await store.AppendAsync(prefix + "state/events.jsonl", "wörld\n");
        await store.AppendAsync(prefix + "state/new.txt", "fresh");

        Assert.Equal("héllo\nwörld\n", await store.ReadAsync(prefix + "state/events.jsonl"));
        var info = await store.GetInfoAsync(prefix + "state/events.jsonl");
        Assert.Equal(Encoding.UTF8.GetByteCount("héllo\nwörld\n"), info!.Size);
        Assert.Equal(2, (await store.ListAsync(prefix)).Count);

        await store.WriteAsync(prefix + "state/events.jsonl", "replaced");
        Assert.Equal("replaced", await store.ReadAsync(prefix + "state/events.jsonl"));

        await store.DeleteAsync(prefix + "state/new.txt");
        await store.DeleteAsync(prefix + "state/new.txt");
        Assert.Single(await store.ListAsync(prefix));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Skills_SaveReadDelete_AreIdempotent(string backend)
    {
        var store = Skills(backend);
        var user = Unique();

        Assert.Null(await store.ReadAsync(user, "s1"));
        await store.SaveAsync(user, "s1", [1, 2, 3]);
        await store.SaveAsync(user, "s1", [4]);
        Assert.Equal(new byte[] { 4 }, await store.ReadAsync(user, "s1"));
        Assert.Null(await store.ReadAsync(Unique(), "s1"));

        await store.DeleteAsync(user, "s1");
        await store.DeleteAsync(user, "s1");
        Assert.Null(await store.ReadAsync(user, "s1"));
    }

    [Fact]
    public async Task FileStores_SurviveARestart()
    {
        var u = Unique();
        await new FileFleetBlobs(new LocalFileStore(_root)).PutAsync($"{u}/k", [7]);
        await new FileCopilotSessionStore(new LocalFileStore(_root)).WriteAsync($"{u}/c/f", "text");
        await new FileSkillPackageStore(new LocalFileStore(_root)).SaveAsync(u, "s", [9]);

        Assert.Equal(new byte[] { 7 }, await new FileFleetBlobs(new LocalFileStore(_root)).GetAsync($"{u}/k"));
        Assert.Equal("text", await new FileCopilotSessionStore(new LocalFileStore(_root)).ReadAsync($"{u}/c/f"));
        Assert.Equal(new byte[] { 9 }, await new FileSkillPackageStore(new LocalFileStore(_root)).ReadAsync(u, "s"));
    }

    [Fact]
    public async Task TheStores_KeepSeparateNamespaces_OnOneRoot()
    {
        var files = new LocalFileStore(_root);
        await new FileFleetBlobs(files).PutAsync("same/key", [1]);
        await new FileCopilotSessionStore(files).WriteAsync("same/key", "copilot");

        Assert.Equal(new byte[] { 1 }, await new FileFleetBlobs(files).GetAsync("same/key"));
        Assert.Equal("copilot", await new FileCopilotSessionStore(files).ReadAsync("same/key"));
        Assert.Single(await new FileFleetBlobs(files).ListAsync(""));
    }

    [Fact]
    public async Task ASkillIdThatLooksLikeATraversal_IsContained()
    {
        var store = new FileSkillPackageStore(new LocalFileStore(_root));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("..", "s", [1]));

        // Slashes are escaped inside a segment, so this stays one folder under the store's own.
        await store.SaveAsync("a/../b", "s", [1]);
        Assert.Equal(new byte[] { 1 }, await store.ReadAsync("a/../b", "s"));
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "skill-packages")));
    }
}
