using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>ADR 0153: the single writer loop of one turn: flushes, heartbeat, retries, the fence and the terminal status.</summary>
public sealed class ChatTurnWriterTests
{
    private const string User = "u";
    private const string Conversation = "c";
    private const string Turn = "t";

    private readonly ChatTurnOptions _options = ChatTurnTestOptions.Fast();
    private readonly InMemoryChatTurnBlobs _memory = new();

    private async Task<(ChatTurnStore Store, ChatTurnWriter Writer)> StartAsync(IChatTurnBlobs? blobs = null)
    {
        var store = new ChatTurnStore(blobs ?? _memory, _options, TimeProvider.System);
        var meta = await store.StartAsync(User, Conversation, Turn, "r");
        return (store, new ChatTurnWriter(store, User, Conversation, meta, _options, TimeProvider.System, NullLogger.Instance));
    }

    private static async Task<long> LengthAsync(ChatTurnStore store)
        => (await store.ReadFromAsync(User, Conversation, Turn, 0))?.Length ?? -1;

    private static Task EventuallyAsync(Func<Task<bool>> condition) => TestWait.WaitUntilAsync(condition, "the writer's store write");

    [Fact]
    public async Task Batches_FlushOnTheInterval_BeforeTheTurnEnds()
    {
        var (store, writer) = await StartAsync();
        var run = writer.RunAsync();

        writer.Enqueue("{\"a\":1}");
        await EventuallyAsync(async () => await LengthAsync(store) > 0);
        Assert.False(run.IsCompleted);

        writer.Complete(ChatTurnStatus.Completed);
        await run;
    }

    [Fact]
    public async Task OversizedBatch_FlushesAtOnce_WithoutWaitingForTheInterval()
    {
        _options.FlushInterval = TimeSpan.FromMinutes(5);
        _options.FlushBytes = 100;
        var (store, writer) = await StartAsync();
        var run = writer.RunAsync();

        writer.Enqueue(new string('x', 200));
        await EventuallyAsync(async () => await LengthAsync(store) > 0);

        writer.Complete(ChatTurnStatus.Completed);
        await run;
    }

    [Fact]
    public async Task Heartbeat_TicksWithoutEvents()
    {
        var (store, writer) = await StartAsync();
        var started = (await store.GetMetaAsync(User, Conversation, Turn))!.LastHeartbeatUtc;
        var run = writer.RunAsync();

        await EventuallyAsync(async () => (await store.GetMetaAsync(User, Conversation, Turn))!.LastHeartbeatUtc > started);

        writer.Complete(ChatTurnStatus.Completed);
        await run;
    }

    [Fact]
    public async Task Heartbeat_CannotOverwriteTerminal()
    {
        var (store, writer) = await StartAsync();
        var run = writer.RunAsync();
        await Task.Delay(80); // several heartbeats

        writer.Complete(ChatTurnStatus.Completed);
        await run;
        var ended = await store.GetMetaAsync(User, Conversation, Turn);
        await Task.Delay(120); // a stray heartbeat would land in here

        var later = await store.GetMetaAsync(User, Conversation, Turn);
        Assert.Equal(ChatTurnStatus.Completed, ended!.Status);
        Assert.Equal(ended, later);
    }

    [Theory]
    [InlineData(ChatTurnStatus.Failed)]
    [InlineData(ChatTurnStatus.Interrupted)]
    public async Task Complete_WritesTheTerminalStatusItWasGiven(ChatTurnStatus status)
    {
        var (store, writer) = await StartAsync();
        var run = writer.RunAsync();

        writer.Complete(status);
        await run;

        var meta = await store.GetMetaAsync(User, Conversation, Turn);
        Assert.Equal(status, meta!.Status);
        Assert.NotNull(meta.EndedUtc);
    }

    [Fact]
    public async Task Complete_FlushesWhatIsStillQueued()
    {
        var (store, writer) = await StartAsync();
        var run = writer.RunAsync();

        writer.Enqueue("one");
        writer.Enqueue("two");
        writer.Complete(ChatTurnStatus.Completed);
        await run;

        Assert.Equal(8, await LengthAsync(store));
    }

    [Fact]
    public async Task ABatchThatFailsOnce_IsRetried_AndTheTurnIsNotDegraded()
    {
        var faulty = new FaultyChatTurnBlobs(_memory);
        var (store, writer) = await StartAsync(faulty);
        faulty.FailNextAppends(1);
        var run = writer.RunAsync();

        writer.Enqueue("kept");
        writer.Complete(ChatTurnStatus.Completed);
        await run;

        Assert.Equal(5, await LengthAsync(store));
        Assert.False((await store.GetMetaAsync(User, Conversation, Turn))!.Degraded);
    }

    [Fact]
    public async Task ABatchThatKeepsFailing_MarksTheTurnDegraded_AfterMaxAttempts_AndTheWriterStillEnds()
    {
        var faulty = new FaultyChatTurnBlobs(_memory);
        var (store, writer) = await StartAsync(faulty);
        faulty.AlwaysFailAppends = true;
        var run = writer.RunAsync();

        writer.Enqueue("lost");
        writer.Complete(ChatTurnStatus.Completed);
        await run;

        var meta = await store.GetMetaAsync(User, Conversation, Turn);
        Assert.True(meta!.Degraded);
        Assert.Equal(ChatTurnStatus.Completed, meta.Status);
        Assert.Equal(_options.MaxAppendAttempts, faulty.AppendAttempts);
    }

    [Fact]
    public async Task AFullyDeadStore_NeverStopsTheWriter_OrThrows()
    {
        var faulty = new FaultyChatTurnBlobs(_memory);
        var (_, writer) = await StartAsync(faulty);
        faulty.AlwaysFailWrites = true;
        var run = writer.RunAsync();

        for (var i = 0; i < 50; i++)
        {
            writer.Enqueue("event " + i);
        }

        writer.Complete(ChatTurnStatus.Completed);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task APurgedLog_StopsTheWriter_AndNothingIsRecreated()
    {
        var (store, writer) = await StartAsync();
        var run = writer.RunAsync();
        writer.Enqueue("before");
        await EventuallyAsync(async () => await LengthAsync(store) > 0);

        await store.PurgeAsync(User, Conversation);
        writer.Enqueue("after the delete");
        await Task.Delay(120);
        writer.Complete(ChatTurnStatus.Completed);
        await run;

        Assert.Empty(await _memory.ListAsync(string.Empty));
    }

    [Fact]
    public async Task APastTheCapLog_IsTruncated_AndTheTurnStillCompletes()
    {
        _options.MaxLogBytes = 20;
        var (store, writer) = await StartAsync();
        var run = writer.RunAsync();

        writer.Enqueue("0123456789"); // 11 bytes with the newline
        await EventuallyAsync(async () => await LengthAsync(store) == 11);
        writer.Enqueue("0123456789");
        writer.Complete(ChatTurnStatus.Completed);
        await run;

        var meta = await store.GetMetaAsync(User, Conversation, Turn);
        Assert.True(meta!.Truncated);
        Assert.Equal(ChatTurnStatus.Completed, meta.Status);
        Assert.Equal(11, await LengthAsync(store));
    }
}
