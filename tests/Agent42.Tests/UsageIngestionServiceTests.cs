using System.Threading.Channels;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.TestingHost;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The background ingestion service (ADR 0031) drives its real drain-and-flush loop against the real
/// global grain over a TestCluster — a short flush interval avoids the 30 s production cadence. Proves
/// enqueued events reach the rollup and that an idle tick is a harmless no-op.
/// </summary>
public sealed class UsageIngestionServiceTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.DisposeAsync();

    private static UsageEvent Token(string user, long input, long output) =>
        new(UsageEventKind.Token, DateTimeOffset.UtcNow, "Agent42", "foundry", "gpt-5.4", user, "", input, output, 0, 0, false);

    // Enqueued events are drained on the timer and applied to the global grain.
    [Fact]
    public async Task Drains_ChannelIntoRollupGrain()
    {
        var channel = Channel.CreateUnbounded<UsageEvent>();
        channel.Writer.TryWrite(Token("ada", 100, 20));
        channel.Writer.TryWrite(Token("bob", 200, 40));

        var service = new UsageIngestionService(
            channel.Reader, _cluster.GrainFactory, NullLogger<UsageIngestionService>.Instance,
            flushInterval: TimeSpan.FromMilliseconds(50));

        await service.StartAsync(CancellationToken.None);
        try
        {
            var grain = _cluster.GrainFactory.GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey);
            await WaitUntilAsync(async () => (await grain.QueryAsync("30d", null, null)).Totals.InputTokens == 300);

            var report = await grain.QueryAsync("30d", null, null);
            Assert.Equal(300, report.Totals.InputTokens);
            Assert.Equal(2, report.Users.Count);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // An empty channel over several ticks never throws and writes nothing.
    [Fact]
    public async Task IdleTicks_AreNoOp()
    {
        var channel = Channel.CreateUnbounded<UsageEvent>();
        var service = new UsageIngestionService(
            channel.Reader, _cluster.GrainFactory, NullLogger<UsageIngestionService>.Instance,
            flushInterval: TimeSpan.FromMilliseconds(20));

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(120);
        await service.StopAsync(CancellationToken.None);

        var report = await _cluster.GrainFactory.GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey)
            .QueryAsync("all", null, null);
        Assert.Equal(0, report.Totals.CallCount);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Condition not met within the timeout.");
    }
}
