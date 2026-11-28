namespace Agent42.Backend;

/// <summary>
/// The time bounds of the web-search chain (ADR 0155). <c>AgentGrain</c> is non-reentrant, so a slow search stalls the
/// turn: every bound is deliberately short, and there are no retries inside a provider (the chain is the retry).
/// </summary>
public sealed record WebSearchTimeouts(TimeSpan PerAttempt, TimeSpan HostedBudget)
{
    /// <summary>Worst case is HostedBudget + SearXNG's own 10 s client timeout, ~18 s, and only while nothing is benched.</summary>
    public static WebSearchTimeouts Default { get; } = new(
        PerAttempt: TimeSpan.FromSeconds(4),
        HostedBudget: TimeSpan.FromSeconds(8));
}

/// <summary>Cooldown durations per failure kind (ADR 0155), each with the reason it is that long.</summary>
public static class WebSearchOptions
{
    /// <summary>A timeout or 5xx is usually a blip: retry soon.</summary>
    public static readonly TimeSpan TransientCooldown = TimeSpan.FromSeconds(30);

    /// <summary>Used for a 429 that carried no <c>Retry-After</c>; per-minute limits reset within a minute.</summary>
    public static readonly TimeSpan RateLimitedCooldown = TimeSpan.FromMinutes(1);

    /// <summary>A vendor can ask for a long wait; the chain has other providers, so never bench for longer than this.</summary>
    public static readonly TimeSpan RetryAfterCap = TimeSpan.FromMinutes(5);

    /// <summary>Credits are monthly or topped up by hand: re-probing hourly costs one failed call per hour.</summary>
    public static readonly TimeSpan ExhaustedCooldown = TimeSpan.FromHours(1);

    /// <summary>A bad key stays bad until the operator fixes it; re-check rarely, and log loudly each time.</summary>
    public static readonly TimeSpan UnauthorizedCooldown = TimeSpan.FromHours(6);

    public static TimeSpan CooldownFor(WebSearchFailureKind kind, TimeSpan? retryAfter) => kind switch
    {
        WebSearchFailureKind.Transient => TransientCooldown,
        WebSearchFailureKind.RateLimited => retryAfter is { } wait && wait > TimeSpan.Zero
            ? (wait > RetryAfterCap ? RetryAfterCap : wait)
            : RateLimitedCooldown,
        WebSearchFailureKind.Exhausted => ExhaustedCooldown,
        _ => UnauthorizedCooldown,
    };
}
