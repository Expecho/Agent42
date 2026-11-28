using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// The limits of ONE fleet task, shared by all its teams (ADR 0097): the tokens they use, the agents they spawn, and
/// how many sub-teams run at once per level. Using up the tokens cancels the task. One instance per task, never a
/// global, so tasks of different users run side by side.
/// </summary>
public sealed class FleetBudget : IDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly SemaphoreSlim[] _subTeamSlots;
    private readonly double _softLimit;
    private readonly Lock _gate = new();
    private long _inputTokens;
    private long _outputTokens;
    private long _cachedInputTokens;
    private int _agents;
    private int _deepestLevel;

    public FleetBudget(long maxTokens, int maxAgents, double softLimit, int maxConcurrentSubTeamsPerLevel, int maxDepth,
        CancellationToken stopping = default)
    {
        MaxTokens = maxTokens;
        MaxAgents = maxAgents;
        _softLimit = softLimit;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        // A semaphore per level, so a sub-team waiting for a slot never waits for one held by its own parent teams.
        _subTeamSlots = [.. Enumerable.Range(0, maxDepth + 2).Select(_ => new SemaphoreSlim(Math.Max(1, maxConcurrentSubTeamsPerLevel)))];
    }

    /// <summary>0 means no token limit; the service always sets one.</summary>
    public long MaxTokens { get; }
    public bool HasTokenLimit => MaxTokens > 0;
    public int MaxAgents { get; }
    public long InputTokens => Interlocked.Read(ref _inputTokens);
    public long OutputTokens => Interlocked.Read(ref _outputTokens);

    /// <summary>
    /// Cached input reads (ADR 0104): a SUBSET of <see cref="InputTokens"/>, shown on the page and never added to
    /// <see cref="Tokens"/>, so the budget limit does not count them twice.
    /// </summary>
    public long CachedInputTokens => Interlocked.Read(ref _cachedInputTokens);

    public long Tokens => InputTokens + OutputTokens;
    public int Agents => Volatile.Read(ref _agents);
    public int DeepestLevel => Volatile.Read(ref _deepestLevel);
    public int RemainingAgents => Math.Max(0, MaxAgents - Agents);

    public bool IsSoftLimitReached
        => (HasTokenLimit && Tokens >= MaxTokens * _softLimit) || Agents >= MaxAgents * _softLimit;

    public CancellationToken Token => _cancellation.Token;
    public bool IsCancelled => _cancellation.IsCancellationRequested;
    public string? CancelReason { get; private set; }

    public SemaphoreSlim SubTeamSlots(int level) => _subTeamSlots[Math.Clamp(level, 0, _subTeamSlots.Length - 1)];

    public void AddTokens(UsageDetails? usage)
    {
        if (usage is null)
        {
            return;
        }

        Interlocked.Add(ref _inputTokens, usage.InputTokenCount ?? 0);
        Interlocked.Add(ref _outputTokens, usage.OutputTokenCount ?? 0);
        Interlocked.Add(ref _cachedInputTokens, usage.CachedInputTokenCount ?? 0);
        if (HasTokenLimit && Tokens >= MaxTokens)
        {
            Cancel($"the token budget of {MaxTokens:N0} tokens is used up");
        }
    }

    /// <summary>Reserves the agents of a team at <paramref name="level"/>; false when they don't fit.</summary>
    public bool TryReserveAgents(int count, int level)
    {
        int current;
        do
        {
            current = Volatile.Read(ref _agents);
            if (current + count > MaxAgents)
            {
                return false;
            }
        }
        while (Interlocked.CompareExchange(ref _agents, current + count, current) != current);

        lock (_gate)
        {
            _deepestLevel = Math.Max(_deepestLevel, level);
        }

        return true;
    }

    /// <summary>The cancel reason of a stop the user asked for: the stop button and declining the plan (ADR 0154).</summary>
    public const string UserStopReason = "stopped by the user";

    /// <summary>Cancels the task with a reason; the first reason wins.</summary>
    public void Cancel(string reason)
    {
        lock (_gate)
        {
            if (IsCancelled)
            {
                return;
            }

            CancelReason = reason;
        }

        _cancellation.Cancel();
    }

    public void Dispose()
    {
        _cancellation.Dispose();
        foreach (var slot in _subTeamSlots)
        {
            slot.Dispose();
        }
    }
}
