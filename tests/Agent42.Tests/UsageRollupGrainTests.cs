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
        new(UsageEventKind.Token, DateTimeOffset.UtcNow, "Agent42", "foundry", "gpt-5.4", user, "", input, output, 0, 0, false, UserId: "oid-" + user);

    // AC10: applied rollups persist and are still queryable after the grain deactivates and re-reads state.
    [Fact]
    public async Task Rollup_SurvivesReactivation()
    {
        var grain = Global();
        await grain.ApplyAsync([Token("ada", 100, 20), Token("bob", 200, 40)]);

        await grain.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var ada = await Global().QueryForUserAsync("30d", null, "oid-ada", null);
        Assert.Equal(100, ada.Totals.InputTokens);
        Assert.Equal(20, ada.Totals.OutputTokens);
        Assert.Single(ada.Users);

        var bob = await Global().QueryForUserAsync("30d", null, "oid-bob", null);
        Assert.Equal(200, bob.Totals.InputTokens);
    }

    // An empty batch is a no-op (no write, no throw).
    [Fact]
    public async Task ApplyAsync_EmptyBatch_IsNoOp()
    {
        var grain = Global();
        await grain.ApplyAsync([]);
        var report = await grain.QueryForUserAsync("all", null, "oid-ada", null);
        Assert.Equal(0, report.Totals.CallCount);
    }

    // The application dropdown lists only what the caller's scope contains.
    [Fact]
    public async Task ListApplications_IsScopedToTheCaller()
    {
        var grain = Global();
        await grain.ApplyAsync([Token("ada", 1, 1) with { Application = "AdaApp" }, Token("bob", 1, 1) with { Application = "BobApp" }]);

        Assert.Equal(["AdaApp"], await grain.ListApplicationsForUserAsync("oid-ada", null));
        Assert.Empty(await grain.ListApplicationsForUserAsync("", null));
    }
}
