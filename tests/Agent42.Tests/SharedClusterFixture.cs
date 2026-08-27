using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// A <see cref="TestCluster"/> deployed ONCE for a whole test class, shared by every test in it.
///
/// The alternative — a class implementing <see cref="IAsyncLifetime"/> and building its own cluster in
/// InitializeAsync — deploys a fresh two-silo cluster per TEST, because xUnit constructs a new instance of
/// the test class for every test method. That is the single largest cost in this suite: AgentGrainTests
/// alone deployed 109 clusters (~2m17s) where one suffices.
///
/// Sharing is safe because grain isolation here comes from unique grain KEYS per test ("web-search-
/// conversation", "tools-override-conversation", …), not from cluster freshness. What sharing does break is
/// the mutable per-silo fakes, whose counters would otherwise accumulate across tests — hence
/// <see cref="ResetSiloState"/>, which the test class calls between tests.
///
/// Deliberately a CLASS fixture, not a collection fixture: xUnit runs each class as its own collection, so
/// per-class keeps the cluster classes running in parallel. One shared collection-level cluster would
/// serialize them and trade one bottleneck for another.
/// </summary>
public class SharedClusterFixture : IAsyncLifetime
{
    public TestCluster Cluster { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await Cluster.DisposeAsync();

    /// <summary>
    /// Returns the mutable per-silo fakes to their initial state, restoring the per-test isolation the
    /// old cluster-per-test shape gave for free. Tests within a class run serially (one collection), so
    /// this cannot race a test still in flight.
    /// </summary>
    public void ResetSiloState()
    {
        foreach (var silo in Cluster.Silos)
        {
            var services = Cluster.GetSiloServiceProvider(silo.SiloAddress);
            services.GetRequiredService<FakeChatClient>().Reset();
            services.GetRequiredService<RecordingPushNotificationSender>().Reset();
        }
    }
}
