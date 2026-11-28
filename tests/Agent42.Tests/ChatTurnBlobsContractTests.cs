using System.Text;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// One contract for every <see cref="IChatTurnBlobs"/> twin we can run offline (ADR 0153): the in-memory one and the file
/// one. The Azure adapter holds the same promises but cannot run here (its post-deploy check is listed in the ADR).
/// </summary>
public sealed class ChatTurnBlobsContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent42-chatturn-" + Guid.NewGuid().ToString("N"));

    public static TheoryData<string> Twins => ["memory", "file"];

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private IChatTurnBlobs Create(string twin)
        => twin == "memory" ? new InMemoryChatTurnBlobs() : new FileChatTurnBlobs(new LocalFileStore(_root));

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Theory]
    [MemberData(nameof(Twins))]
    public async Task Create_MakesAnEmptyLog_AndLeavesAnExistingOneAlone(string twin)
    {
        var blobs = Create(twin);

        await blobs.CreateAsync("u/c/t.ndjson");
        Assert.Equal(0, await blobs.GetLengthAsync("u/c/t.ndjson"));
        await blobs.AppendAsync("u/c/t.ndjson", Bytes("abc"), 0);
        await blobs.CreateAsync("u/c/t.ndjson");

        Assert.Equal(3, await blobs.GetLengthAsync("u/c/t.ndjson"));
    }

    [Theory]
    [MemberData(nameof(Twins))]
    public async Task Append_IsConditionalOnTheOffset(string twin)
    {
        var blobs = Create(twin);
        await blobs.CreateAsync("u/c/t.ndjson");
        await blobs.AppendAsync("u/c/t.ndjson", Bytes("abc"), 0);

        var wrong = await blobs.AppendAsync("u/c/t.ndjson", Bytes("zzz"), 1);
        var right = await blobs.AppendAsync("u/c/t.ndjson", Bytes("def"), 3);

        Assert.Equal(ChatTurnAppendOutcome.OffsetMismatch, wrong.Outcome);
        Assert.Equal(3, wrong.Length);
        Assert.Equal(ChatTurnAppendOutcome.Appended, right.Outcome);
        Assert.Equal("abcdef", Encoding.UTF8.GetString((await blobs.GetAsync("u/c/t.ndjson"))!));
    }

    [Theory]
    [MemberData(nameof(Twins))]
    public async Task Append_ToAMissingBlob_IsAFence_AtAnyOffset_AndNeverCreatesIt(string twin)
    {
        var blobs = Create(twin);

        var above = await blobs.AppendAsync("u/c/t.ndjson", Bytes("abc"), 5);
        var atZero = await blobs.AppendAsync("u/c/t.ndjson", Bytes("abc"), 0);

        Assert.Equal(ChatTurnAppendOutcome.Missing, above.Outcome);
        Assert.Equal(ChatTurnAppendOutcome.Missing, atZero.Outcome);
        Assert.Null(await blobs.GetLengthAsync("u/c/t.ndjson"));
    }

    [Theory]
    [MemberData(nameof(Twins))]
    public async Task GetRange_ReturnsTheTail_AndAnEmptySliceWithoutGrowth(string twin)
    {
        var blobs = Create(twin);
        await blobs.CreateAsync("u/c/t.ndjson");
        await blobs.AppendAsync("u/c/t.ndjson", Bytes("abcdef"), 0);

        var tail = await blobs.GetRangeAsync("u/c/t.ndjson", 4);
        var none = await blobs.GetRangeAsync("u/c/t.ndjson", 6);

        Assert.Equal("ef", Encoding.UTF8.GetString(tail!.Bytes));
        Assert.Equal(6, tail.Length);
        Assert.Empty(none!.Bytes);
        Assert.Equal(6, none.Length);
        Assert.Null(await blobs.GetRangeAsync("u/c/missing.ndjson", 0));
    }

    [Theory]
    [MemberData(nameof(Twins))]
    public async Task List_ReturnsKeysUnderThePrefix_WithSizeAndAge(string twin)
    {
        var blobs = Create(twin);
        await blobs.PutAsync("u/c/a.meta.json", Bytes("{}"));
        await blobs.PutAsync("u/other/b.meta.json", Bytes("{}"));

        var listed = await blobs.ListAsync("u/c/");

        var item = Assert.Single(listed);
        Assert.Equal("u/c/a.meta.json", item.Key);
        Assert.Equal(2, item.Size);
        Assert.True(DateTimeOffset.UtcNow - item.LastModified < TimeSpan.FromMinutes(5));
    }

    [Theory]
    [MemberData(nameof(Twins))]
    public async Task Delete_OfAMissingBlob_IsNotAnError(string twin)
    {
        var blobs = Create(twin);
        await blobs.PutAsync("u/c/a.meta.json", Bytes("{}"));

        await blobs.DeleteAsync("u/c/a.meta.json");
        await blobs.DeleteAsync("u/c/a.meta.json");

        Assert.Null(await blobs.GetAsync("u/c/a.meta.json"));
    }
}
