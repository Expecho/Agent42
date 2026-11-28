using System.Diagnostics;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// <see cref="IWebSearch"/> over an ordered list of providers (ADR 0155): the first success wins, a failing keyed
/// provider is benched by kind, and the keyless SearXNG member is last and never benched. Zero results is a success.
///
/// <para>Bounds: keyed providers share a <see cref="WebSearchTimeouts.HostedBudget"/>, each attempt is capped by
/// <see cref="WebSearchTimeouts.PerAttempt"/>, and running out of budget falls through to the last resort rather than
/// to an error. The final exception message is <b>curated</b> (names and kinds only): <c>WebTools</c> shows it to the
/// model.</para>
/// </summary>
public sealed class FallbackWebSearch(
    IReadOnlyList<IWebSearchProvider> providers,
    WebSearchCooldowns cooldowns,
    ILogger<FallbackWebSearch> logger,
    WebSearchTimeouts? timeouts = null) : IWebSearch
{
    /// <summary>Activity tag: the provider that answered.</summary>
    public const string ProviderTag = "websearch.provider";

    /// <summary>Activity tag: comma-separated <c>name:reason</c> of providers skipped or failed.</summary>
    public const string SkippedTag = "websearch.skipped";

    private readonly WebSearchTimeouts _timeouts = timeouts ?? WebSearchTimeouts.Default;

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default)
    {
        var skipped = new List<string>();
        using var hostedBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var budgetStarted = false;

        foreach (var provider in providers)
        {
            if (provider.CanBeBenched && !TryEnterHostedPhase(provider, hostedBudget, ref budgetStarted, skipped))
            {
                continue;
            }

            var results = await TryAsync(provider, query, maxResults, hostedBudget.Token, cancellationToken, skipped);
            if (results is not null)
            {
                Record(provider.Name, skipped);
                return results;
            }
        }

        Record(null, skipped);
        throw new InvalidOperationException(
            $"The web search failed: no provider could answer ({string.Join(", ", skipped)}).");
    }

    // A keyed provider is skipped while benched or once the shared hosted budget is spent. The budget clock starts at
    // the first keyed attempt, so a call that skips every keyed provider pays nothing.
    private bool TryEnterHostedPhase(
        IWebSearchProvider provider, CancellationTokenSource hostedBudget, ref bool budgetStarted, List<string> skipped)
    {
        if (cooldowns.TryGetBenched(provider.Name, out var benched))
        {
            skipped.Add($"{provider.Name}:benched-{Describe(benched.Kind)}");
            return false;
        }

        if (hostedBudget.IsCancellationRequested)
        {
            skipped.Add($"{provider.Name}:budget");
            return false;
        }

        if (!budgetStarted)
        {
            hostedBudget.CancelAfter(_timeouts.HostedBudget);
            budgetStarted = true;
        }

        return true;
    }

    private async Task<IReadOnlyList<WebSearchResult>?> TryAsync(
        IWebSearchProvider provider, string query, int maxResults,
        CancellationToken budget, CancellationToken caller, List<string> skipped)
    {
        // The last resort has its own client timeout and must still run after the hosted budget is spent.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(provider.CanBeBenched ? budget : caller);
        if (provider.CanBeBenched)
        {
            attempt.CancelAfter(_timeouts.PerAttempt);
        }

        try
        {
            return await provider.SearchAsync(query, maxResults, attempt.Token);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            throw; // The caller left. Not a provider failure, so nothing is benched.
        }
        catch (WebSearchProviderException ex)
        {
            Fail(provider, ex.Kind, ex.RetryAfter, skipped);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Timeout, network, parse: a provider we cannot classify is transient. The message is dropped on purpose.
            Fail(provider, WebSearchFailureKind.Transient, null, skipped);
        }

        return null;
    }

    private void Fail(IWebSearchProvider provider, WebSearchFailureKind kind, TimeSpan? retryAfter, List<string> skipped)
    {
        skipped.Add($"{provider.Name}:{Describe(kind)}");
        if (!provider.CanBeBenched)
        {
            logger.LogWarning(
                "Web search provider {Provider} failed ({Kind}); it is the last resort and is not benched.",
                provider.Name, kind);
            return;
        }

        cooldowns.Bench(provider.Name, kind, retryAfter);
        if (kind == WebSearchFailureKind.Unauthorized)
        {
            logger.LogError("Web search provider {Provider} rejected its API key; benched. Check the operator key.",
                provider.Name);
        }
        else
        {
            logger.LogWarning("Web search provider {Provider} failed ({Kind}); benched.", provider.Name, kind);
        }
    }

    // Kinds and names only: no query text, no keys (ADR 0155).
    private void Record(string? answered, List<string> skipped)
    {
        var activity = Activity.Current;
        if (answered is not null)
        {
            logger.LogDebug("Web search answered by {Provider}; skipped: {Skipped}.", answered, string.Join(",", skipped));
            activity?.SetTag(ProviderTag, answered);
        }

        if (skipped.Count > 0)
        {
            activity?.SetTag(SkippedTag, string.Join(",", skipped));
        }
    }

    public static string Describe(WebSearchFailureKind kind) => kind switch
    {
        WebSearchFailureKind.Transient => "unavailable",
        WebSearchFailureKind.RateLimited => "rate-limited",
        WebSearchFailureKind.Exhausted => "credits-exhausted",
        _ => "key-rejected",
    };
}
