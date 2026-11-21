using Agent42.Agents;
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

    public virtual async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public virtual async Task DisposeAsync() => await Cluster.DisposeAsync();

    /// <summary>
    /// Moves every silo's retention clock forward by <paramref name="by"/>, so a turn already in a
    /// session reads as that much older (ADR 0016's window). Reset to zero before each test by
    /// <see cref="ResetSiloState"/>.
    ///
    /// <para>Applied to every silo because a grain may activate on any of them, and the offset must
    /// be the same wherever it lands.</para>
    /// </summary>
    public void AdvanceClock(TimeSpan by)
    {
        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<TestTimeProvider>().Offset += by;
        }
    }

    /// <summary>
    /// Returns the mutable per-silo fakes to their initial state, restoring the per-test isolation the
    /// old cluster-per-test shape gave for free. Tests within a class run serially (one collection), so
    /// this cannot race a test still in flight.
    ///
    /// <para>The list is every per-silo singleton a test can WRITE — not just the two that own a
    /// <c>Reset()</c>. <see cref="SingleUserMcpToolProvider"/> has none of its own (it lives in <c>src/</c>
    /// and serves offline hosts as well as tests), so its settable state is cleared from here instead. The
    /// in-memory attachment, document and skill stores are deliberately absent: they are
    /// process-global rather than per-silo and already outlive every cluster, so a fresh cluster was never
    /// their isolation boundary and sharing one changes nothing for them.</para>
    /// </summary>
    public void ResetSiloState()
    {
        foreach (var silo in Cluster.Silos)
        {
            var services = Cluster.GetSiloServiceProvider(silo.SiloAddress);
            services.GetRequiredService<FakeChatClient>().Reset();
            services.GetRequiredService<FakeModelRouterProvider>().Reset();
            services.GetRequiredService<CountingChatClientFactoryProvider>().Reset();
            services.GetRequiredService<RecordingPushNotificationSender>().Reset();
            services.GetRequiredService<RecordingAgentWebhookSender>().Reset();
            services.GetRequiredService<RecordingChatDeliverySender>().Reset();
            services.GetRequiredService<FakeFleetLauncher>().Reset();
            services.GetRequiredService<RecordingHindsightClient>().Reset();
            var mcp = services.GetRequiredService<SingleUserMcpToolProvider>();
            mcp.Servers = [];
            mcp.Version = 0;
            services.GetRequiredService<TestTimeProvider>().Offset = TimeSpan.Zero;
        }
    }
}
