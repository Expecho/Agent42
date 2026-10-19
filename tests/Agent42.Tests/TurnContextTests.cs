using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The per-run context of ADR 0106 on its own, with no silo: the fail-closed default outside a run, and the reach
/// of <see cref="TurnContext.RunInAsync{T}(TurnContext, Func{ValueTask{T}})"/>, which the grain's pulls rely on.
/// </summary>
public sealed class TurnContextTests
{
    // AC4a: a tool reached with no run sees the closed context, which refuses everything that leaves a trace.
    [Fact]
    public void CurrentOrClosed_OutsideARun_IsClosed()
    {
        var context = TurnContext.CurrentOrClosed;

        Assert.Same(TurnContext.Closed, context);
        Assert.True(context.IsClosed);
        Assert.True(context.Ephemeral);
        Assert.False(context.Interactive);
        Assert.Null(context.Broker);
    }

    // AC4a: the closed context is shared, so a stray tool call must not be able to change it: its budgets stay
    // exhausted and nothing it records sticks.
    [Fact]
    public void Closed_IgnoresMutations()
    {
        var closed = TurnContext.Closed;

        closed.RecordPushSent();
        closed.RecordDocumentPartRead();
        closed.RecordWebDocumentIngested();

        Assert.Equal(int.MaxValue, closed.PushesSent);
        Assert.Equal(int.MaxValue, closed.DocumentPartsRead);
        Assert.False(closed.WebDocumentIngested);
        Assert.False(closed.TakeWebDocumentIngested());
    }

    [Fact]
    public void RunContext_CountsItsOwnWork_AndTheIngestIsTakenOnce()
    {
        var run = TurnContext.ForBackground();

        run.RecordPushSent();
        run.RecordPushSent();
        run.RecordDocumentPartRead();
        run.RecordWebDocumentIngested();

        Assert.Equal(2, run.PushesSent);
        Assert.Equal(1, run.DocumentPartsRead);
        Assert.True(run.TakeWebDocumentIngested());
        Assert.False(run.TakeWebDocumentIngested());
        Assert.Equal(0, TurnContext.ForBackground().PushesSent);
    }

    [Fact]
    public async Task RunInAsync_IsVisibleInsideTheBody_AndDoesNotLeakToTheCaller()
    {
        var run = TurnContext.ForBackground();

        var seen = await TurnContext.RunInAsync(run, async () =>
        {
            await Task.Yield();
            return TurnContext.CurrentOrClosed;
        });

        Assert.Same(run, seen);
        Assert.Same(TurnContext.Closed, TurnContext.CurrentOrClosed);
    }

    // The interleaving the grain's fields got wrong: run A pulled once, run B pulled to its end in the gap, then A
    // finished. Each pull sees only its own run.
    [Fact]
    public async Task InterleavedRuns_EachSeeOnlyTheirOwnContext()
    {
        var a = TurnContext.ForChat(ephemeral: true, interactive: true, broker: null);
        var b = TurnContext.ForBackground();
        var seen = new List<(string Run, TurnContext Context)>();
        await using var runA = Observe("A", seen).GetAsyncEnumerator();
        await using var runB = Observe("B", seen).GetAsyncEnumerator();

        await TurnContext.RunInAsync(a, runA.MoveNextAsync);
        while (await TurnContext.RunInAsync(b, runB.MoveNextAsync))
        {
        }

        while (await TurnContext.RunInAsync(a, runA.MoveNextAsync))
        {
        }

        Assert.All(seen.Where(s => s.Run == "A"), s => Assert.Same(a, s.Context));
        Assert.All(seen.Where(s => s.Run == "B"), s => Assert.Same(b, s.Context));
        Assert.Equal(6, seen.Count);
    }

    [Fact]
    public void ForChat_And_ForBackground_CarryTheirFlags()
    {
        var broker = new ClientToolBroker([ClientToolLimits.ConfirmAction]);
        var chat = TurnContext.ForChat(ephemeral: true, interactive: true, broker);
        var background = TurnContext.ForBackground();

        Assert.True(chat.Ephemeral);
        Assert.True(chat.Interactive);
        Assert.Same(broker, chat.Broker);
        Assert.False(chat.IsClosed);
        Assert.False(background.Ephemeral);
        Assert.False(background.Interactive);
        Assert.Null(background.Broker);
        Assert.False(background.IsClosed);
    }

    private static async IAsyncEnumerable<int> Observe(string run, List<(string, TurnContext)> seen)
    {
        for (var i = 0; i < 3; i++)
        {
            await Task.Yield();
            seen.Add((run, TurnContext.CurrentOrClosed));
            yield return i;
        }
    }
}
