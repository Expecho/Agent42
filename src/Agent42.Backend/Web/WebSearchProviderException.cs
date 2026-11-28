namespace Agent42.Backend;

/// <summary>Why a keyed web-search provider failed. Drives how long it is benched (ADR 0155).</summary>
public enum WebSearchFailureKind
{
    /// <summary>Timeout, network error or 5xx. Short cooldown.</summary>
    Transient,

    /// <summary>HTTP 429. Short cooldown, <c>Retry-After</c> honoured (capped).</summary>
    RateLimited,

    /// <summary>Credits or plan limit used up. Long cooldown.</summary>
    Exhausted,

    /// <summary>401/403, a bad or revoked key. Long cooldown and an Error-level log.</summary>
    Unauthorized,
}

/// <summary>
/// A classified provider failure. The message is <b>curated</b>: <c>WebTools</c> hands the chain's final message to the
/// model, so a provider must never put a response body, URL or key into it (ADR 0155).
/// </summary>
public sealed class WebSearchProviderException(WebSearchFailureKind kind, TimeSpan? retryAfter = null)
    : Exception($"web search provider failed ({kind})")
{
    public WebSearchFailureKind Kind { get; } = kind;

    /// <summary>The vendor's own <c>Retry-After</c>, when it sent one.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
