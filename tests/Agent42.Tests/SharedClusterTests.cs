using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// Base for every test class that shares one <see cref="SharedClusterFixture"/> cluster across its tests.
///
/// <para><b>Why a base class and not just the fixture.</b> Sharing a cluster trades cluster freshness for
/// three narrower properties, one of which — "the mutable per-silo fakes are back at their initial state" —
/// is not a property of the cluster at all but of a call the test class has to remember to make. A class
/// needing no other per-test setup would otherwise carry no <see cref="IAsyncLifetime"/> at all and so
/// never call <see cref="SharedClusterFixture.ResetSiloState"/>: correct on the day it is written, silently
/// wrong the day someone adds a test to it that reads a counter. Taking the cluster from this base is the
/// only sanctioned way to reach it, so the reset comes with it — <see cref="SharedClusterSeamTests"/> holds
/// that line.</para>
///
/// <para>Per-test work a derived class still needs goes in <see cref="OnInitializeAsync"/> /
/// <see cref="OnDisposeAsync"/>; <see cref="InitializeAsync"/> is deliberately not overridable so an
/// override cannot drop the reset on its way past.</para>
///
/// <para><typeparamref name="TFixture"/> is what lets an API class bring a host with it
/// (<see cref="SharedApiFixture"/> and its subclasses) without a second, parallel seam.</para>
/// </summary>
public abstract class SharedClusterTests<TFixture> : IClassFixture<TFixture>, IAsyncLifetime
    where TFixture : SharedClusterFixture
{
    protected SharedClusterTests(TFixture fixture)
    {
        Fixture = fixture;
        Cluster = fixture.Cluster;
    }

    /// <summary>The cluster deployed once for this class, shared by every test in it.</summary>
    protected TestCluster Cluster { get; }

    protected TFixture Fixture { get; }

    public Task InitializeAsync()
    {
        // The counters and recorders are per-silo singletons that now outlive a single test.
        Fixture.ResetSiloState();
        return OnInitializeAsync();
    }

    public Task DisposeAsync() => OnDisposeAsync();

    /// <summary>Per-test setup, run after the shared silos have been reset.</summary>
    protected virtual Task OnInitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Per-test teardown. The cluster belongs to the fixture and outlives the test — never dispose it here.
    /// </summary>
    protected virtual Task OnDisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// The plain-grain case: a shared cluster and nothing else. Most converted classes want this one.
/// </summary>
public abstract class SharedClusterTests(SharedClusterFixture fixture)
    : SharedClusterTests<SharedClusterFixture>(fixture);
