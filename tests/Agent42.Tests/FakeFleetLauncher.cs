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

    // The analyzer half (ADR 0122): every call is recorded, and each read answers a canned text tests can read back.
    private readonly ConcurrentQueue<string> _calls = new();
    private readonly ConcurrentQueue<Rerun> _reruns = new();

    public sealed record Rerun(string UserId, string TaskId, string? Note, IReadOnlyList<string>? DisableTools, long? MaxTokens);

    /// <summary>Every analyzer call as "method:userId:arg", in order.</summary>
    public IReadOnlyList<string> Calls => [.. _calls];

    /// <summary>Every re-run, in order.</summary>
    public IReadOnlyList<Rerun> Reruns => [.. _reruns];

    /// <summary>When set, a re-run is refused with this text; a stop answers it too.</summary>
    public string? AnalyzerError { get; set; }

    /// <summary>When set, every analyzer call throws it (a tool must turn that into text).</summary>
    public Exception? AnalyzerThrows { get; set; }

    public void Reset()
    {
        _starts.Clear();
        _calls.Clear();
        _reruns.Clear();
        Tasks.Clear();
        NextError = null;
        AnalyzerError = null;
        AnalyzerThrows = null;
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

    private Task<string> Record(string call)
    {
        _calls.Enqueue(call);
        return AnalyzerThrows is { } ex ? Task.FromException<string>(ex) : Task.FromResult("fake " + call);
    }

    public Task<string> ListRecentAsync(string userId, int limit, CancellationToken cancellationToken = default)
        => Record($"list:{userId}:{limit}");

    public Task<string> AnalyzeAsync(string userId, string taskId, CancellationToken cancellationToken = default)
        => Record($"analyze:{userId}:{taskId}");

    public Task<string> ReadEventsAsync(string userId, string taskId, string? types, long afterSeq, int limit, CancellationToken cancellationToken = default)
        => Record($"events:{userId}:{taskId}:{types}:{afterSeq}:{limit}");

    public Task<string> ReadWorkspaceAsync(string userId, string taskId, string? path, CancellationToken cancellationToken = default)
        => Record($"workspace:{userId}:{taskId}:{path}");

    public string DescribeConfig() => "fake config";

    public Task<string> ReadSpansAsync(string userId, string? search, int limit, CancellationToken cancellationToken = default)
        => Record($"spans:{userId}:{search}:{limit}");

    public Task<string> StopAsync(string userId, string taskId, CancellationToken cancellationToken = default)
        => AnalyzerError is { } error ? Task.FromResult(error) : Record($"stop:{userId}:{taskId}");

    public Task<FleetLaunchResult> RerunAsync(
        string userId, string taskId, string? note, IReadOnlyList<string>? disableTools, long? maxTokens,
        CancellationToken cancellationToken = default)
    {
        _reruns.Enqueue(new Rerun(userId, taskId, note, disableTools, maxTokens));
        if (AnalyzerThrows is { } ex)
        {
            return Task.FromException<FleetLaunchResult>(ex);
        }

        return Task.FromResult(AnalyzerError is { } error
            ? new FleetLaunchResult(null, error)
            : new FleetLaunchResult("rerun-" + _reruns.Count, null));
    }
}
