using System.Collections.Concurrent;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The test silo's Auto router port (ADR 0137). No router by default, which is the production shape for a user without a
/// DecisionModel provider; a test sets <see cref="Router"/> on every silo (a grain may activate on any) and the fixture
/// resets it before the next test.
/// </summary>
public sealed class FakeModelRouterProvider : IUserModelRouterProvider
{
    public IModelRouter? Router { get; set; }

    public Task<IModelRouter?> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(Router);

    public void Reset() => Router = null;
}

/// <summary>A router that answers a canned decision (or throws) and records every turn it was asked about.</summary>
public sealed class FakeModelRouter : IModelRouter
{
    public RouterDecision? Decision { get; set; }

    public Exception? Throws { get; set; }

    public ConcurrentQueue<RouterTurn> Calls { get; } = new();

    public Task<RouterDecision?> RouteAsync(RouterTurn turn, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(turn);
        return Throws is null ? Task.FromResult(Decision) : Task.FromException<RouterDecision?>(Throws);
    }
}
