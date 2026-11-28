using System.Runtime.CompilerServices;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Orleans.Runtime;

namespace Agent42.Tests;

/// <summary>ADR 0153: the pump drains a grain stream on a detached task, stamps seq, and ends the turn honestly.</summary>
public sealed class ChatTurnPumpTests
{
    private readonly ChatTurnOptions _options = ChatTurnTestOptions.Fast();

    private static async IAsyncEnumerable<AgentStreamEvent> Events(int count, string prefix = "t")
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Yield();
            yield return new AgentStreamEvent(AgentStreamEventType.TextDelta, Text: prefix + i);
        }

        yield return new AgentStreamEvent(AgentStreamEventType.Done);
    }

    private static async IAsyncEnumerable<AgentStreamEvent> ThrowsAfter(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return new AgentStreamEvent(AgentStreamEventType.TextDelta, Text: "x" + i);
        }

        await Task.Yield();
        throw new InvalidOperationException("the grain broke");
    }

    private static async IAsyncEnumerable<AgentStreamEvent> BlocksUntilCancelled([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new AgentStreamEvent(AgentStreamEventType.TextDelta, Text: "first");
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    [Fact]
    public async Task Seq_IsGaplessAndMonotonic_InTheFeedAndInTheLog()
    {
        var harness = new PumpHarness(_options);

        await harness.Pump.RunAsync(Events(20), CancellationToken.None);

        var fromFeed = harness.Feed.Read(0).Events;
        Assert.Equal(Enumerable.Range(1, 21).Select(i => (long)i), fromFeed.Select(e => e.Seq));
        Assert.All(fromFeed, e => Assert.Equal(e.Seq, e.Event.Seq));

        var fromLog = (await harness.LogLinesAsync())
            .Select(l => JsonSerializer.Deserialize<AgentStreamEvent>(l, ChatStreamJson.Options)!.Seq);
        Assert.Equal(fromFeed.Select(e => (long?)e.Seq), fromLog);
    }

    [Fact]
    public async Task ACompletedTurn_EndsCompleted_ForTheFeedAndTheMeta()
    {
        var harness = new PumpHarness(_options);

        await harness.Pump.RunAsync(Events(3), CancellationToken.None);

        Assert.Equal(ChatTurnStatus.Completed, harness.Feed.EndStatus);
        Assert.Equal(ChatTurnStatus.Completed, (await harness.MetaAsync())!.Status);
    }

    [Fact]
    public async Task GrainThrows_EndsFailed_ForAllReaders()
    {
        var harness = new PumpHarness(_options);

        await harness.Pump.RunAsync(ThrowsAfter(3), CancellationToken.None);

        Assert.Equal(ChatTurnStatus.Failed, harness.Feed.EndStatus);
        var meta = await harness.MetaAsync();
        Assert.Equal(ChatTurnStatus.Failed, meta!.Status);
        Assert.Equal(ChatTurnStatus.Failed, harness.Store.StatusOf(meta));
        Assert.Equal(3, (await harness.LogLinesAsync()).Count);
    }

    [Fact]
    public async Task HostStopping_MarksInterrupted()
    {
        var harness = new PumpHarness(_options);
        using var stopping = new CancellationTokenSource();

        var run = harness.Pump.RunAsync(BlocksUntilCancelled(), stopping.Token);
        await Task.Delay(50);
        await stopping.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ChatTurnStatus.Interrupted, harness.Feed.EndStatus);
        Assert.Equal(ChatTurnStatus.Interrupted, (await harness.MetaAsync())!.Status);
    }

    [Fact]
    public async Task RequestAbort_DoesNotCancelPump()
    {
        var harness = new PumpHarness(_options);
        using var requestAborted = new CancellationTokenSource();
        await requestAborted.CancelAsync(); // the client is already gone; the pump has no way to observe that, by design

        await harness.Pump.RunAsync(Events(10), CancellationToken.None);

        Assert.Equal(ChatTurnStatus.Completed, harness.Feed.EndStatus);
        Assert.Equal(11, harness.Feed.Read(0).Events.Count);
    }

    [Fact]
    public async Task StoreFailure_DoesNotStopDrain_OrLiveStream()
    {
        var faulty = new FaultyChatTurnBlobs(new InMemoryChatTurnBlobs());
        var harness = new PumpHarness(_options, faulty);
        faulty.AlwaysFailWrites = true;

        await harness.Pump.RunAsync(Events(30), CancellationToken.None);

        Assert.Equal(ChatTurnStatus.Completed, harness.Feed.EndStatus);
        Assert.Equal(31, harness.Feed.Read(0).Events.Count);
        faulty.AlwaysFailWrites = false;
        // The store never heard the end of it, so another device sees a running turn go stale: D15, reported honestly.
        Assert.Equal(ChatTurnStatus.Running, (await harness.MetaAsync())!.Status);
    }

    [Fact]
    public async Task AFailingAppend_MarksTheTurnDegraded_ButItStillCompletes()
    {
        var faulty = new FaultyChatTurnBlobs(new InMemoryChatTurnBlobs());
        var harness = new PumpHarness(_options, faulty);
        faulty.AlwaysFailAppends = true;

        await harness.Pump.RunAsync(Events(5), CancellationToken.None);

        var meta = await harness.MetaAsync();
        Assert.True(meta!.Degraded);
        Assert.Equal(ChatTurnStatus.Completed, meta.Status);
    }

    [Fact]
    public async Task Pump_PublishesTheCapturedIdentity_ToTheDetachedTask()
    {
        var telemetry = new UserTelemetryContext("oid-pump", null, null, null, null, null);
        var harness = new PumpHarness(_options, telemetry: telemetry, application: "agent42-chat");
        RequestContext.Clear();
        UserTelemetryContext? seen = null;
        string? seenApplication = null;

        async IAsyncEnumerable<AgentStreamEvent> Observe()
        {
            seen = RequestContext.Get(UserTelemetryContext.RequestContextKey) as UserTelemetryContext;
            seenApplication = RequestContext.Get(UsageSpanProjector.ApplicationContextKey) as string;
            await Task.Yield();
            yield return new AgentStreamEvent(AgentStreamEventType.Done);
        }

        await Task.Run(() => harness.Pump.RunAsync(Observe(), CancellationToken.None));

        Assert.Equal(telemetry, seen);
        Assert.Equal("agent42-chat", seenApplication);
    }

    [Fact]
    public async Task Feed_IsABoundedRing_AndAReaderBehindItIsToldSo()
    {
        var feed = new TurnFeed(maxBytes: 100);
        for (var i = 1; i <= 50; i++)
        {
            feed.Append(new LoggedEvent(i, new AgentStreamEvent(AgentStreamEventType.TextDelta, Text: "x", Seq: i)), 20);
        }

        var newest = feed.Read(45).Events;
        Assert.Equal([46L, 47, 48, 49, 50], newest.Select(e => e.Seq));
        Assert.Throws<TurnFeedBehindException>(() => feed.Read(0));
        await Task.CompletedTask;
    }

    [Fact]
    public void Feed_AlwaysKeepsTheNewestEvent_EvenWhenItAloneExceedsTheCap()
    {
        var feed = new TurnFeed(maxBytes: 10);

        feed.Append(new LoggedEvent(1, new AgentStreamEvent(AgentStreamEventType.TextDelta, Seq: 1)), 500);

        Assert.Single(feed.Read(0).Events);
    }

    [Fact]
    public async Task Feed_WakesAWaitingReader_OnAppendAndOnComplete()
    {
        var feed = new TurnFeed(1024);
        var snapshot = feed.Read(0);
        Assert.False(snapshot.Wake.IsCompleted);

        feed.Append(new LoggedEvent(1, new AgentStreamEvent(AgentStreamEventType.TextDelta, Seq: 1)), 10);
        await snapshot.Wake.WaitAsync(TimeSpan.FromSeconds(2));

        var next = feed.Read(1);
        feed.Complete(ChatTurnStatus.Completed);
        await next.Wake.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ChatTurnStatus.Completed, feed.EndStatus);
    }

    [Fact]
    public async Task Registry_EvictsAFeed_AfterTheGrace_AndKeysItByUser()
    {
        var registry = new ChatTurnRegistry(_options, TimeProvider.System);
        registry.Add("alice", "c", "t");

        Assert.NotNull(registry.TryGet("alice", "c", "t"));
        Assert.Null(registry.TryGet("bob", "c", "t"));

        registry.EvictAfterGrace("alice", "c", "t");
        await TestWait.WaitUntilAsync(() => registry.TryGet("alice", "c", "t") is null, "the feed to be evicted");
    }
}
