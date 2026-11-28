using System.Text;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>ADR 0153: the chat turn's log logic, run over the in-memory blob twin (the <c>FleetStore</c> contract-test shape).</summary>
public sealed class ChatTurnStoreTests
{
    private const string User = "user/1";
    private const string Conversation = "conv 1";

    private readonly TestTimeProvider _time = new();
    private readonly ChatTurnOptions _options = new();
    private readonly InMemoryChatTurnBlobs _blobs;
    private readonly ChatTurnStore _store;

    public ChatTurnStoreTests()
    {
        _blobs = new InMemoryChatTurnBlobs(_time);
        _store = new ChatTurnStore(_blobs, _options, _time);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task Append_ThenReadFromOffset_ReturnsOnlyTheGrowth()
    {
        await _store.StartAsync(User, Conversation, "t1", "r1");
        var first = await _store.AppendAsync(User, Conversation, "t1", Bytes("aaa\n"), 0);
        var second = await _store.AppendAsync(User, Conversation, "t1", Bytes("bb\n"), first.Length!.Value);

        var slice = await _store.ReadFromAsync(User, Conversation, "t1", first.Length.Value);

        Assert.Equal(ChatTurnAppendOutcome.Appended, second.Outcome);
        Assert.Equal("bb\n", Encoding.UTF8.GetString(slice!.Bytes));
        Assert.Equal(7, slice.Length);
        Assert.Empty((await _store.ReadFromAsync(User, Conversation, "t1", 7))!.Bytes);
    }

    [Fact]
    public async Task Append_AtAWrongOffset_IsRejected_AndWritesNothing()
    {
        await _store.StartAsync(User, Conversation, "t1", "r1");
        await _store.AppendAsync(User, Conversation, "t1", Bytes("aaa\n"), 0);

        // Offset 2 on a 4-byte log, and 2 + 3 != 4, so this is a real mismatch and not a batch that already landed.
        var result = await _store.AppendAsync(User, Conversation, "t1", Bytes("zz\n"), 2);

        Assert.Equal(ChatTurnAppendOutcome.OffsetMismatch, result.Outcome);
        Assert.Equal(4, (await _store.ReadFromAsync(User, Conversation, "t1", 0))!.Length);
    }

    [Fact]
    public async Task AmbiguousAppend_NoDuplicate()
    {
        var flaky = new LandsThenThrowsBlobs(_blobs);
        var store = new ChatTurnStore(flaky, _options, _time);
        await store.StartAsync(User, Conversation, "t1", "r1");
        flaky.ThrowAfterNextAppend = true;

        var result = await store.AppendAsync(User, Conversation, "t1", Bytes("one\n"), 0);
        var retry = await store.AppendAsync(User, Conversation, "t1", Bytes("one\n"), 0);

        Assert.Equal(ChatTurnAppendOutcome.Appended, result.Outcome);
        Assert.Equal(ChatTurnAppendOutcome.Appended, retry.Outcome);
        Assert.Equal("one\n", Encoding.UTF8.GetString((await store.ReadFromAsync(User, Conversation, "t1", 0))!.Bytes));
    }

    [Fact]
    public async Task Append_ToAMissingLog_AboveOffsetZero_IsAFence_AndDoesNotRecreateIt()
    {
        await _store.StartAsync(User, Conversation, "t1", "r1");
        await _store.AppendAsync(User, Conversation, "t1", Bytes("aaa\n"), 0);
        await _store.PurgeAsync(User, Conversation);

        var result = await _store.AppendAsync(User, Conversation, "t1", Bytes("bbb\n"), 4);

        Assert.Equal(ChatTurnAppendOutcome.Missing, result.Outcome);
        Assert.Empty(await _blobs.ListAsync(string.Empty));
    }

    // The first flush appends at offset 0, so a purge BEFORE it must fence that append too, not just later ones.
    [Fact]
    public async Task Purge_BeforeTheFirstFlush_FencesTheFirstAppend()
    {
        await _store.StartAsync(User, Conversation, "t1", "r1");
        await _store.PurgeAsync(User, Conversation);

        var result = await _store.AppendAsync(User, Conversation, "t1", Bytes("aaa\n"), 0);
        var meta = await _store.UpdateMetaAsync(User, Conversation, new ChatTurnMeta("t1", ChatTurnStatus.Running, _time.GetUtcNow(), "r1", _time.GetUtcNow()));

        Assert.Equal(ChatTurnAppendOutcome.Missing, result.Outcome);
        Assert.False(meta);
        Assert.Empty(await _blobs.ListAsync(string.Empty));
    }

    [Fact]
    public async Task LogCap_Truncates_FeedUnaffected()
    {
        _options.MaxLogBytes = 10;
        await _store.StartAsync(User, Conversation, "t1", "r1");
        await _store.AppendAsync(User, Conversation, "t1", Bytes("12345678\n"), 0);

        var result = await _store.AppendAsync(User, Conversation, "t1", Bytes("overflow\n"), 9);

        Assert.Equal(ChatTurnAppendOutcome.Truncated, result.Outcome);
        Assert.Equal(9, (await _store.ReadFromAsync(User, Conversation, "t1", 0))!.Length);
    }

    [Fact]
    public async Task ABatchOverTheCap_LandsAsFarAsItFits_InWholeLines()
    {
        _options.MaxLogBytes = 12;
        await _store.StartAsync(User, Conversation, "t1", "r1");

        var result = await _store.AppendAsync(User, Conversation, "t1", Bytes("aaaa\nbbbb\ncccc\n"), 0);

        Assert.Equal(ChatTurnAppendOutcome.Truncated, result.Outcome);
        Assert.Equal(10, result.Length);
        Assert.Equal("aaaa\nbbbb\n", Encoding.UTF8.GetString((await _store.ReadFromAsync(User, Conversation, "t1", 0))!.Bytes));
    }

    [Fact]
    public async Task StatusOf_FreshHeartbeat_Running()
    {
        var meta = await _store.StartAsync(User, Conversation, "t1", "r1");
        _time.Offset = _options.StaleAfter - TimeSpan.FromSeconds(1);

        Assert.Equal(ChatTurnStatus.Running, _store.StatusOf(meta));
    }

    [Fact]
    public async Task StatusOf_StaleHeartbeat_Interrupted_NotWritten()
    {
        var meta = await _store.StartAsync(User, Conversation, "t1", "r1");
        _time.Offset = _options.StaleAfter + TimeSpan.FromSeconds(1);

        Assert.Equal(ChatTurnStatus.Interrupted, _store.StatusOf(meta));
        Assert.Equal(ChatTurnStatus.Running, (await _store.GetMetaAsync(User, Conversation, "t1"))!.Status);
    }

    [Fact]
    public async Task StatusOf_TerminalStatus_IsNeverStale()
    {
        var meta = await _store.StartAsync(User, Conversation, "t1", "r1");
        _time.Offset = TimeSpan.FromDays(2);

        Assert.Equal(ChatTurnStatus.Completed, _store.StatusOf(meta with { Status = ChatTurnStatus.Completed }));
    }

    [Fact]
    public async Task Purge_RemovesEveryBlobOfTheConversation_AndOnlyThat()
    {
        await _store.StartAsync(User, Conversation, "t1", "r1");
        await _store.StartAsync(User, "other", "t2", "r1");

        await _store.PurgeAsync(User, Conversation);

        Assert.Null(await _store.GetActiveAsync(User, Conversation));
        Assert.NotNull(await _store.GetActiveAsync(User, "other"));
    }

    [Fact]
    public async Task ConcurrentTurns_KeepIndependentLogs()
    {
        await _store.StartAsync(User, Conversation, "a", "r1");
        await _store.AppendAsync(User, Conversation, "a", Bytes("from a\n"), 0);

        await _store.StartAsync(User, Conversation, "b", "r1");
        await _store.AppendAsync(User, Conversation, "b", Bytes("from b\n"), 0);

        Assert.Equal("b", (await _store.GetActiveAsync(User, Conversation))!.TurnId);
        Assert.Equal(ChatTurnStatus.Running, (await _store.GetMetaAsync(User, Conversation, "a"))!.Status);
        Assert.Equal("from a\n", Encoding.UTF8.GetString((await _store.ReadFromAsync(User, Conversation, "a", 0))!.Bytes));
    }

    [Fact]
    public async Task StartingATurn_DeletesTerminalTurnsOfTheConversation_ButNotARunningOne()
    {
        var done = await _store.StartAsync(User, Conversation, "done", "r1");
        await _store.WriteMetaAsync(User, Conversation, done with { Status = ChatTurnStatus.Completed });
        await _store.StartAsync(User, Conversation, "running", "r1");

        await _store.StartAsync(User, Conversation, "next", "r1");

        Assert.Null(await _store.GetMetaAsync(User, Conversation, "done"));
        Assert.Null(await _store.ReadFromAsync(User, Conversation, "done", 0));
        Assert.NotNull(await _store.GetMetaAsync(User, Conversation, "running"));
    }

    [Fact]
    public async Task Keys_AreEscaped_SoAnIdCannotStepIntoAnotherConversation()
    {
        await _store.StartAsync("u", "a/b", "t", "r1");

        Assert.Null(await _store.GetActiveAsync("u/a", "b"));
        Assert.NotNull(await _store.GetActiveAsync("u", "a/b"));
    }

    [Fact]
    public async Task Sweep_DeletesOnlyBlobsOlderThanTheCutoff()
    {
        await _store.StartAsync(User, "old", "t1", "r1");
        _time.Offset = TimeSpan.FromHours(30);
        await _store.StartAsync(User, "young", "t2", "r1");

        var deleted = await _store.SweepAsync(_time.GetUtcNow() - _options.Retention);

        Assert.True(deleted > 0);
        Assert.Null(await _store.GetActiveAsync(User, "old"));
        Assert.NotNull(await _store.GetActiveAsync(User, "young"));
    }

    // Lands the append, then throws: the "timed-out AppendBlock that actually landed" of D12.
    private sealed class LandsThenThrowsBlobs(IChatTurnBlobs inner) : IChatTurnBlobs
    {
        public bool ThrowAfterNextAppend { get; set; }

        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) => inner.GetAsync(key, cancellationToken);

        public Task<long?> GetLengthAsync(string key, CancellationToken cancellationToken = default) => inner.GetLengthAsync(key, cancellationToken);

        public Task<ChatTurnSlice?> GetRangeAsync(string key, long offset, CancellationToken cancellationToken = default)
            => inner.GetRangeAsync(key, offset, cancellationToken);

        public Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default) => inner.PutAsync(key, content, cancellationToken);

        public Task CreateAsync(string key, CancellationToken cancellationToken = default) => inner.CreateAsync(key, cancellationToken);

        public async Task<ChatTurnAppendResult> AppendAsync(string key, byte[] content, long expectedOffset, CancellationToken cancellationToken = default)
        {
            var result = await inner.AppendAsync(key, content, expectedOffset, cancellationToken);
            if (ThrowAfterNextAppend)
            {
                ThrowAfterNextAppend = false;
                throw new TimeoutException("the answer was lost");
            }

            return result;
        }

        public Task<IReadOnlyList<ChatTurnBlobItem>> ListAsync(string prefix, CancellationToken cancellationToken = default)
            => inner.ListAsync(prefix, cancellationToken);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, cancellationToken);
    }
}
