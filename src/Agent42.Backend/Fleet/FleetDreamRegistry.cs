using System.Collections.Concurrent;

namespace Agent42.Backend;

/// <summary>
/// The dreams running in this process (single replica, CLAUDE.md rule 12): each value completes when its run has written its
/// final state and left the registry. Shared by the lessons store (an entry stored as <c>running</c> that is not here was
/// interrupted) and the dream runner (which registers and removes).
/// </summary>
internal sealed class FleetDreamRegistry
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _running = new(StringComparer.Ordinal);

    public bool IsRunning(string userId) => _running.ContainsKey(userId);

    public bool TryAdd(string userId, TaskCompletionSource run) => _running.TryAdd(userId, run);

    public void Remove(string userId) => _running.TryRemove(userId, out _);

    public Task WhenIdleAsync(string userId) => _running.TryGetValue(userId, out var run) ? run.Task : Task.CompletedTask;
}
