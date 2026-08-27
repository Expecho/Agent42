using System.Reflection;

namespace Agent42.Tests;

/// <summary>
/// Holds the one line <see cref="SharedClusterTests{TFixture}"/> cannot hold on its own.
///
/// <para>A shared cluster is safe only while the mutable per-silo fakes are returned to their initial state
/// between tests, and that reset is a call, not a property of the cluster — so nothing in the type system
/// stops a new class from taking a <see cref="SharedClusterFixture"/> directly and skipping it. Such a class
/// compiles, and passes, right up until someone adds a test to it that reads a counter. This asserts the
/// convention instead: reach a shared cluster through the resetting base, or not at all.</para>
/// </summary>
public sealed class SharedClusterSeamTests
{
    [Fact]
    public void EveryClassSharingTheCluster_ReachesItThroughTheResettingBase()
    {
        var sharing = typeof(SharedClusterSeamTests).Assembly
            .GetTypes()
            // Concrete only: xUnit instantiates test classes, so the abstract seam types themselves are not
            // candidates — SharedClusterTests<TFixture> declares the interface but cannot derive from itself.
            .Where(type => !type.IsAbstract)
            .Where(type => type.GetInterfaces().Any(SharesACluster))
            .ToArray();

        // Non-vacuity. Without this the assertion below passes identically whether it found thirty
        // cluster-sharing classes or none, so a refactor that changed the fixture shape would silently
        // turn the guard off instead of failing.
        Assert.True(sharing.Length > 20, $"the guard matched only {sharing.Length} cluster-sharing classes");

        // And specifically that it reaches the API classes, whose fixture is a SUBCLASS of
        // SharedClusterFixture. An identity comparison against IClassFixture<SharedClusterFixture> matches
        // only the plain-grain shape and is silently blind to every one of them.
        Assert.Contains(sharing, type => type.GetInterfaces().Any(i => i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IClassFixture<>)
            && i.GetGenericArguments()[0] != typeof(SharedClusterFixture)));

        var strays = sharing
            .Where(type => !ReachesItThroughTheBase(type))
            .Select(type => type.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.True(
            strays.Length == 0,
            "These classes take a cluster fixture without deriving from SharedClusterTests, so they never "
                + $"call ResetSiloState() between tests: {string.Join(", ", strays)}. Derive from "
                + "SharedClusterTests and move any per-test setup into OnInitializeAsync.");
    }

    // The reset is only unavoidable while InitializeAsync cannot be overridden: an override would take the
    // interface slot and silently drop the reset, while every derived class still looked converted.
    [Fact]
    public void TheResettingInitialize_CannotBeOverridden()
    {
        var initialize = typeof(SharedClusterTests<SharedClusterFixture>).GetMethod(
            nameof(IAsyncLifetime.InitializeAsync), BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(initialize);

        // An implicit interface implementation is "virtual final" in IL — virtual so the interface slot
        // binds, final so no derived class can take that slot over. Sealed is the property asserted here.
        Assert.True(
            initialize!.IsFinal || !initialize.IsVirtual,
            "SharedClusterTests.InitializeAsync must not be overridable.");
    }

    /// <summary>
    /// Does this interface say "I take a fixture that deploys a shared cluster"?
    ///
    /// <para><c>GetInterfaces()</c> returns CLOSED generic types, so a class taking
    /// <c>IClassFixture&lt;AgentApiFixture&gt;</c> yields exactly that and never
    /// <c>IClassFixture&lt;SharedClusterFixture&gt;</c>. Comparing against the latter by identity would
    /// therefore see the plain-grain classes and be blind to every API class — which is precisely where the
    /// fixture surface is. Open the generic and test the argument instead.</para>
    /// </summary>
    private static bool SharesACluster(Type contract) =>
        contract.IsGenericType
            && contract.GetGenericTypeDefinition() == typeof(IClassFixture<>)
            && typeof(SharedClusterFixture).IsAssignableFrom(contract.GetGenericArguments()[0]);

    /// <summary>
    /// <c>SharedClusterTests&lt;TFixture&gt;</c> is the only type that both implements IClassFixture over a
    /// cluster fixture and resets the silos, so inheriting some closed form of it is the property to check.
    /// </summary>
    private static bool ReachesItThroughTheBase(Type candidate)
    {
        for (var type = candidate.BaseType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SharedClusterTests<>))
            {
                return true;
            }
        }

        return false;
    }
}
