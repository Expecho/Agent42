using System.Text;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>ADR 0153: reading a turn from the feed or the store: replay, coalescing, the ring fallback and the truncated end.</summary>
public sealed class ChatTurnTailTests
{
    private const string User = "u1";
    private const string Conversation = "c1";
    private const string Turn = "t1";

    private readonly ChatTurnOptions _options = ChatTurnTestOptions.Fast();
    private readonly InMemoryChatTurnBlobs _blobs = new();

    private static async IAsyncEnumerable<AgentStreamEvent> Text(params string[] pieces)
    {
        foreach (var piece in pieces)
        {
            await Task.Yield();
            yield return new AgentStreamEvent(AgentStreamEventType.TextDelta, Text: piece);
        }

        yield return new AgentStreamEvent(AgentStreamEventType.Done);
    }

    // Runs one finished turn through a real pump, with its feed registered, and returns the pieces a reader needs.
    private async Task<(ChatTurnStores Stores, ChatTurnRegistry Registry, TurnFeed Feed)> RunTurnAsync(IAsyncEnumerable<AgentStreamEvent> source)
    {
        var stores = new ChatTurnStores(_blobs, _options, TimeProvider.System);
        var registry = new ChatTurnRegistry(_options, TimeProvider.System);
        var meta = await stores.Durable.StartAsync(User, Conversation, Turn, "r");
        var feed = registry.Add(User, Conversation, Turn);
        var writer = new ChatTurnWriter(stores.Durable, User, Conversation, meta, _options, TimeProvider.System, NullLogger.Instance);
        await new ChatTurnPump(writer, feed, NullLogger<ChatTurnPump>.Instance, null, null).RunAsync(source, CancellationToken.None);
        return (stores, registry, feed);
    }

    private ChatTurnTail TailOf(ChatTurnStores stores, ChatTurnRegistry registry)
        => new(registry, stores, _options, TimeProvider.System);

    // A tail on a replica that does not own the turn: an empty registry, so only the store can answer.
    private ChatTurnTail StoreOnlyTail(ChatTurnStores stores)
        => TailOf(stores, new ChatTurnRegistry(_options, TimeProvider.System));

    private static async Task<(List<LoggedEvent> Events, TurnEnd? End)> CollectAsync(ChatTurnTailSession session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<LoggedEvent>();
        await foreach (var batch in session.ReadAsync(timeout.Token))
        {
            events.AddRange(batch);
        }

        return (events, session.End);
    }

    private static string AllText(IEnumerable<LoggedEvent> events)
        => string.Concat(events.Where(e => e.Event.Type == AgentStreamEventType.TextDelta).Select(e => e.Event.Text));

    [Fact]
    public async Task Replay_CoalescesOnlyAboveAfter_NoDuplicateText()
    {
        var (stores, _, _) = await RunTurnAsync(Text("a", "b", "c", "d"));
        var tail = StoreOnlyTail(stores);

        var whole = (await CollectAsync(tail.Open(User, Conversation, Turn, false, 0))).Events;
        // A client that saw seq 1 and 2 ("ab") reconnects "after 2": it gets "cd" and nothing it already shows.
        var resumed = (await CollectAsync(tail.Open(User, Conversation, Turn, false, 2))).Events;

        Assert.Equal("abcd", AllText(whole));
        Assert.Equal([4L, 5L], whole.Select(e => e.Seq)); // the four deltas became one event, Done follows
        Assert.Equal("cd", AllText(resumed));
        Assert.All(resumed, e => Assert.True(e.Seq > 2));
    }

    [Fact]
    public async Task Reattach_AfterTheLastSeenEvent_NeverRepeatsOrSkips()
    {
        var (stores, _, _) = await RunTurnAsync(Text("one ", "two ", "three"));
        var tail = StoreOnlyTail(stores);
        var seen = new StringBuilder();

        // Walk the turn one seq at a time, like a client that keeps losing its socket: each resume point is the last seq it saw.
        long cursor = 0;
        while (true)
        {
            var next = (await CollectAsync(tail.Open(User, Conversation, Turn, false, cursor))).Events;
            if (next.Count == 0)
            {
                break;
            }

            seen.Append(AllText(next.Take(1)));
            cursor = next[0].Seq;
        }

        Assert.Equal("one two three", seen.ToString());
    }

    [Fact]
    public async Task StoreReader_EqualsFeedReader_ForTheSameTurn()
    {
        var (stores, registry, _) = await RunTurnAsync(Text("x", "y", "z"));

        var fromFeed = (await CollectAsync(TailOf(stores, registry).Open(User, Conversation, Turn, false, 0))).Events;
        var fromStore = (await CollectAsync(StoreOnlyTail(stores).Open(User, Conversation, Turn, false, 0))).Events;

        Assert.Equal(fromFeed.Select(e => (e.Seq, e.Event.Type, e.Event.Text)), fromStore.Select(e => (e.Seq, e.Event.Type, e.Event.Text)));
    }

    [Fact]
    public async Task StoreReader_DedupesBySeq()
    {
        var stores = new ChatTurnStores(_blobs, _options, TimeProvider.System);
        await stores.Durable.StartAsync(User, Conversation, Turn, "r");
        var line = "{\"type\":\"textDelta\",\"text\":\"once\",\"seq\":1}\n";
        var bytes = Encoding.UTF8.GetBytes(line + line + "{\"type\":\"done\",\"seq\":2}\n");
        await stores.Durable.AppendAsync(User, Conversation, Turn, bytes, 0);
        var meta = (await stores.Durable.GetMetaAsync(User, Conversation, Turn))!;
        await stores.Durable.WriteMetaAsync(User, Conversation, meta with { Status = ChatTurnStatus.Completed });

        var events = (await CollectAsync(StoreOnlyTail(stores).Open(User, Conversation, Turn, false, 0))).Events;

        Assert.Equal([1L, 2L], events.Select(e => e.Seq));
    }

    [Fact]
    public async Task StoreReader_AHalfWrittenLine_WaitsForTheRestOfIt()
    {
        var stores = new ChatTurnStores(_blobs, _options, TimeProvider.System);
        await stores.Durable.StartAsync(User, Conversation, Turn, "r");
        var half = Encoding.UTF8.GetBytes("{\"type\":\"textDelta\",\"te");
        var rest = Encoding.UTF8.GetBytes("xt\":\"whole\",\"seq\":1}\n");
        await stores.Durable.AppendAsync(User, Conversation, Turn, half, 0);
        var session = StoreOnlyTail(stores).Open(User, Conversation, Turn, false, 0);

        var reading = CollectAsync(session);
        await Task.Delay(60);
        Assert.False(reading.IsCompleted);
        await stores.Durable.AppendAsync(User, Conversation, Turn, rest, half.Length);
        var meta = (await stores.Durable.GetMetaAsync(User, Conversation, Turn))!;
        await stores.Durable.WriteMetaAsync(User, Conversation, meta with { Status = ChatTurnStatus.Completed });

        var (events, end) = await reading;
        Assert.Equal("whole", AllText(events));
        Assert.True(end!.IsClean);
    }

    [Fact]
    public async Task ReplayThenLive_IsExactlyOnce_AcrossTheBoundary()
    {
        var stores = new ChatTurnStores(_blobs, _options, TimeProvider.System);
        var registry = new ChatTurnRegistry(_options, TimeProvider.System);
        var meta = await stores.Durable.StartAsync(User, Conversation, Turn, "r");
        var feed = registry.Add(User, Conversation, Turn);
        var gate = new SemaphoreSlim(0);
        var writer = new ChatTurnWriter(stores.Durable, User, Conversation, meta, _options, TimeProvider.System, NullLogger.Instance);
        var pump = new ChatTurnPump(writer, feed, NullLogger<ChatTurnPump>.Instance, null, null);

        async IAsyncEnumerable<AgentStreamEvent> Gated()
        {
            for (var i = 0; i < 6; i++)
            {
                await gate.WaitAsync();
                yield return new AgentStreamEvent(AgentStreamEventType.ToolCallStart, ToolCallId: "call" + i);
            }
        }

        var running = pump.RunAsync(Gated(), CancellationToken.None);
        gate.Release(3);
        await Task.Delay(50); // three events are in the feed: the replay
        var reading = CollectAsync(TailOf(stores, registry).Open(User, Conversation, Turn, false, 0));
        await Task.Delay(30);
        gate.Release(3); // three more arrive live
        await running;

        var (events, end) = await reading;
        Assert.Equal(Enumerable.Range(1, 6).Select(i => (long)i), events.Select(e => e.Seq));
        Assert.True(end!.IsClean);
    }

    [Fact]
    public async Task FeedRing_FallsBackToStore()
    {
        _options.FeedMaxBytes = 30; // a few events at most
        var (stores, registry, feed) = await RunTurnAsync(Text("a", "b", "c", "d", "e", "f", "g", "h"));
        Assert.Throws<TurnFeedBehindException>(() => feed.Read(0));

        var (events, end) = await CollectAsync(TailOf(stores, registry).Open(User, Conversation, Turn, false, 0));

        Assert.Equal("abcdefgh", AllText(events));
        Assert.True(end!.IsClean);
    }

    [Fact]
    public async Task BothTruncated_EndsTruncated()
    {
        _options.FeedMaxBytes = 30;
        _options.MaxLogBytes = 120; // room for the first few events only
        var (stores, registry, _) = await RunTurnAsync(Text("a", "b", "c", "d", "e", "f", "g", "h", "i", "j"));

        var (events, end) = await CollectAsync(TailOf(stores, registry).Open(User, Conversation, Turn, false, 0));

        Assert.True(end!.Truncated);
        Assert.False(end.IsClean);
        Assert.True(events.Count > 0);
        Assert.StartsWith(AllText(events), "abcdefghij"); // what it did deliver is a true prefix
        Assert.NotEqual("abcdefghij", AllText(events));
    }

    [Fact]
    public async Task ADeadOwner_ReadsInterrupted_AndTheReaderWritesNothing()
    {
        var time = new TestTimeProvider();
        var stores = new ChatTurnStores(_blobs, _options, time);
        await stores.Durable.StartAsync(User, Conversation, Turn, "dead-replica");
        time.Offset = _options.StaleAfter + TimeSpan.FromSeconds(1);
        var before = await _blobs.GetAsync($"{User}/{Conversation}/{Turn}.meta.json");

        var (events, end) = await CollectAsync(StoreOnlyTail(stores).Open(User, Conversation, Turn, false, 0));

        Assert.Empty(events);
        Assert.Equal(ChatTurnStatus.Interrupted, end!.Status);
        Assert.Equal(before, await _blobs.GetAsync($"{User}/{Conversation}/{Turn}.meta.json"));
    }

    [Fact]
    public async Task APurgedTurn_EndsInterrupted_NotHung()
    {
        var stores = new ChatTurnStores(_blobs, _options, TimeProvider.System);
        await stores.Durable.StartAsync(User, Conversation, Turn, "r");
        var reading = CollectAsync(StoreOnlyTail(stores).Open(User, Conversation, Turn, false, 0));
        await Task.Delay(40);

        await stores.Durable.PurgeAsync(User, Conversation);

        var (_, end) = await reading;
        Assert.Equal(ChatTurnStatus.Interrupted, end!.Status);
    }

    [Fact]
    public async Task LastSeq_IsTheSameFromTheFeedAndFromTheLog()
    {
        var (stores, registry, _) = await RunTurnAsync(Text("a", "b", "c"));

        var fromFeed = await TailOf(stores, registry).LastSeqAsync(User, Conversation, Turn, false);
        var fromLog = await StoreOnlyTail(stores).LastSeqAsync(User, Conversation, Turn, false);

        Assert.Equal(4, fromFeed);
        Assert.Equal(fromFeed, fromLog);
    }

    [Fact]
    public void Coalescer_MergesRunsOfTheSameKind_AndNeverAcrossAnotherEvent()
    {
        LoggedEvent E(long seq, AgentStreamEventType type, string? text = null)
            => new(seq, new AgentStreamEvent(type, Text: text, Seq: seq));

        var merged = ReplayCoalescer.Coalesce(
        [
            E(1, AgentStreamEventType.TextDelta, "a"), E(2, AgentStreamEventType.TextDelta, "b"),
            E(3, AgentStreamEventType.ToolCallStart),
            E(4, AgentStreamEventType.TextDelta, "c"),
            E(5, AgentStreamEventType.Reasoning, "r1"), E(6, AgentStreamEventType.Reasoning, "r2"),
        ], 0);

        Assert.Equal([2L, 3L, 4L, 6L], merged.Select(e => e.Seq));
        Assert.Equal("ab", merged[0].Event.Text);
        Assert.Equal("r1r2", merged[3].Event.Text);
    }
}
