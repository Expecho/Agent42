using Agent42.Agents;
using Orleans.Core.Internal;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// The global usage rollup grain (ADR 0031) over a TestCluster — it persists to the in-memory
/// stand-in for the "agent-state" provider and must survive reactivation (the UserPushGrain shape).
/// </summary>
public sealed class UsageRollupGrainTests : IAsyncLifetime
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

    private IUsageRollupGrain Global() =>
        _cluster.GrainFactory.GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey);

    private static UsageEvent Token(string user, long input, long output) =>
        new(UsageEventKind.Token, DateTimeOffset.UtcNow, "Agent42", "foundry", "gpt-5.4", user, "", input, output, 0, 0, false);

    // AC10: applied rollups persist and are still queryable after the grain deactivates and re-reads state.
    [Fact]
    public async Task Rollup_SurvivesReactivation()
    {
        var grain = Global();
        await grain.ApplyAsync([Token("ada", 100, 20), Token("bob", 200, 40)]);

        await grain.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var report = await Global().QueryAsync("30d", null, null);
        Assert.Equal(300, report.Totals.InputTokens);
        Assert.Equal(60, report.Totals.OutputTokens);
        Assert.Equal(2, report.Users.Count);
    }

    // An empty batch is a no-op (no write, no throw).
    [Fact]
    public async Task ApplyAsync_EmptyBatch_IsNoOp()
    {
        var grain = Global();
        await grain.ApplyAsync([]);
        var report = await grain.QueryAsync("all", null, null);
        Assert.Equal(0, report.Totals.CallCount);
    }

    // The dropdown lists reflect what was applied.
    [Fact]
    public async Task ListUsers_ReflectsApplied()
    {
        var grain = Global();
        await grain.ApplyAsync([Token("ada", 1, 1), Token("bob", 1, 1)]);

        var users = await grain.ListUsersAsync();
        Assert.Contains("ada", users);
        Assert.Contains("bob", users);
    }
}
