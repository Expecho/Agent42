using System.Collections.Concurrent;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// An <see cref="IFleetLauncher"/> that records every start instead of running the fleet (ADR 0115). A start answers
/// <see cref="NextError"/> when set, else a new task id; <see cref="Tasks"/> is what <c>get_fleet_task</c> reads.
/// Registered as a concrete singleton in <see cref="TestSiloConfigurator"/> and reset per test.
/// </summary>
public sealed class FakeFleetLauncher : IFleetLauncher
{
    private readonly ConcurrentQueue<Start> _starts = new();

    public sealed record Start(string UserId, string ConversationId, string Task, string? Model, string? FollowUpOf);

    /// <summary>Every recorded start, in order.</summary>
    public IReadOnlyList<Start> Starts => [.. _starts];

    /// <summary>The tasks <see cref="GetAsync"/> finds, keyed by <c>{userId}/{taskId}</c>.</summary>
    public ConcurrentDictionary<string, FleetTaskRecord> Tasks { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, every start is refused with this text.</summary>
    public string? NextError { get; set; }

    public void Reset()
    {
        _starts.Clear();
        Tasks.Clear();
        NextError = null;
    }

    public Task<FleetLaunchResult> StartAsync(
        string userId, string conversationId, string task, string? model, string? followUpOf, CancellationToken cancellationToken = default)
    {
        _starts.Enqueue(new Start(userId, conversationId, task, model, followUpOf));
        return Task.FromResult(NextError is { } error
            ? new FleetLaunchResult(null, error)
            : new FleetLaunchResult("task-" + _starts.Count, null));
    }

    public Task<FleetTaskRecord?> GetAsync(string userId, string taskId, CancellationToken cancellationToken = default)
        => Task.FromResult(Tasks.TryGetValue($"{userId}/{taskId}", out var record) ? record : null);
}
