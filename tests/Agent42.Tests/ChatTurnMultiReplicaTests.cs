using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// ADR 0153, the N-replica requirement: two replicas are two stores, registries and tails over ONE shared blob store. The
/// owner's feed lives in replica A's memory only, so replica B can only read through the store, and what it reads must equal
/// what the owner's own feed reader saw.
/// </summary>
public sealed class ChatTurnMultiReplicaTests
{
    private const string User = "u1";
    private const string Conversation = "c1";
    private const string Turn = "t1";

    private readonly ChatTurnOptions _options = ChatTurnTestOptions.Fast();
    private readonly InMemoryChatTurnBlobs _shared = new();

    private sealed record Replica(ChatTurnStores Stores, ChatTurnRegistry Registry, ChatTurnTail Tail);

    private Replica NewReplica(TimeProvider? time = null)
    {
        var stores = new ChatTurnStores(_shared, _options, time ?? TimeProvider.System);
        var registry = new ChatTurnRegistry(_options, time ?? TimeProvider.System);
        return new Replica(stores, registry, new ChatTurnTail(registry, stores, _options, time ?? TimeProvider.System));
    }

    private static async IAsyncEnumerable<AgentStreamEvent> Reply()
    {
        foreach (var piece in new[] { "The ", "answer ", "is ", "42" })
        {
            await Task.Yield();
            yield return new AgentStreamEvent(AgentStreamEventType.TextDelta, Text: piece);
        }

        yield return new AgentStreamEvent(AgentStreamEventType.Done, OutputTokens: 4);
    }

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

    private async Task RunOnAsync(Replica owner)
    {
        var meta = await owner.Stores.Durable.StartAsync(User, Conversation, Turn, "replica-a");
        var feed = owner.Registry.Add(User, Conversation, Turn);
        var writer = new ChatTurnWriter(owner.Stores.Durable, User, Conversation, meta, _options, TimeProvider.System, NullLogger.Instance);
        await new ChatTurnPump(writer, feed, NullLogger<ChatTurnPump>.Instance, null, null).RunAsync(Reply(), CancellationToken.None);
    }

    [Fact]
    public async Task StoreReader_EqualsFeedReader_ForTheSameTurn()
    {
        var a = NewReplica();
        var b = NewReplica();
        await RunOnAsync(a);

        var viaFeed = await CollectAsync(a.Tail.Open(User, Conversation, Turn, false, 0));
        var viaStoreOnB = await CollectAsync(b.Tail.Open(User, Conversation, Turn, false, 0));

        Assert.Null(b.Registry.TryGet(User, Conversation, Turn));
        Assert.Equal(
            viaFeed.Events.Select(e => (e.Seq, e.Event.Type, e.Event.Text, e.Event.OutputTokens)),
            viaStoreOnB.Events.Select(e => (e.Seq, e.Event.Type, e.Event.Text, e.Event.OutputTokens)));
        Assert.True(viaFeed.End!.IsClean);
        Assert.True(viaStoreOnB.End!.IsClean);
    }

    [Fact]
    public async Task AReplicaThatDoesNotOwnARunningTurn_FollowsItLive_ThroughTheStore()
    {
        var a = NewReplica();
        var b = NewReplica();
        var meta = await a.Stores.Durable.StartAsync(User, Conversation, Turn, "replica-a");
        var feed = a.Registry.Add(User, Conversation, Turn);
        var gate = new SemaphoreSlim(0);
        var writer = new ChatTurnWriter(a.Stores.Durable, User, Conversation, meta, _options, TimeProvider.System, NullLogger.Instance);

        async IAsyncEnumerable<AgentStreamEvent> Slow()
        {
            for (var i = 0; i < 3; i++)
            {
                await gate.WaitAsync();
                yield return new AgentStreamEvent(AgentStreamEventType.ToolCallStart, ToolCallId: "c" + i);
            }
        }

        var running = new ChatTurnPump(writer, feed, NullLogger<ChatTurnPump>.Instance, null, null).RunAsync(Slow(), CancellationToken.None);
        var reading = CollectAsync(b.Tail.Open(User, Conversation, Turn, false, 0));
        gate.Release(1);
        await Task.Delay(80);
        gate.Release(2);
        await running;

        var (events, end) = await reading;
        Assert.Equal([1L, 2L, 3L], events.Select(e => e.Seq));
        Assert.True(end!.IsClean);
    }

    [Fact]
    public async Task ADeadOwnersTurn_IsReportedInterruptedByAnotherReplica_AndNothingIsWritten()
    {
        var time = new TestTimeProvider();
        var a = NewReplica(time);
        var b = NewReplica(time);
        await a.Stores.Durable.StartAsync(User, Conversation, Turn, "replica-a");
        time.Offset = _options.StaleAfter + TimeSpan.FromSeconds(1);
        var metaBefore = await _shared.GetAsync($"{User}/{Conversation}/{Turn}.meta.json");

        var (events, end) = await CollectAsync(b.Tail.Open(User, Conversation, Turn, false, 0));
        var active = await b.Stores.Durable.GetActiveAsync(User, Conversation);

        Assert.Empty(events);
        Assert.Equal(ChatTurnStatus.Interrupted, end!.Status);
        Assert.Equal(ChatTurnStatus.Interrupted, b.Stores.Durable.StatusOf(active!));
        Assert.Equal(metaBefore, await _shared.GetAsync($"{User}/{Conversation}/{Turn}.meta.json"));
    }

    [Fact]
    public async Task ARunningTurnWhoseOwnerHeartbeats_IsNeverReportedInterrupted()
    {
        var a = NewReplica();
        var b = NewReplica();
        var meta = await a.Stores.Durable.StartAsync(User, Conversation, Turn, "replica-a");
        var writer = new ChatTurnWriter(a.Stores.Durable, User, Conversation, meta, _options, TimeProvider.System, NullLogger.Instance);
        var run = writer.RunAsync();

        await Task.Delay(100); // several heartbeats

        var seen = await b.Stores.Durable.GetActiveAsync(User, Conversation);
        Assert.Equal(ChatTurnStatus.Running, b.Stores.Durable.StatusOf(seen!));
        writer.Complete(ChatTurnStatus.Completed);
        await run;
    }
}
