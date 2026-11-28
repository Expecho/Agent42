using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Builds the web-search chain (ADR 0155): Tavily and Firecrawl when their operator keys are set, then the SearXNG
/// sidecar. The <see cref="HindsightRegistration"/> shape: a key-gated named client per provider, in its own file because
/// <c>Program.cs</c> is at the complexity ceiling (ADR 0050).
///
/// <para>Keys are optional. An unset value reaches the app as an <b>empty string</b> through Bicep and env vars, so
/// <see cref="string.IsNullOrWhiteSpace(string?)"/> decides, and with no key at all <c>IWebSearch</c> is the bare
/// <see cref="SearxngWebSearch"/>: no keys behaves exactly as before.</para>
/// </summary>
public static class WebSearchChainRegistration
{
    /// <summary>The chain order when <c>WebSearch:Providers</c> is blank.</summary>
    public const string DefaultOrder = "tavily,firecrawl,searxng";

    // A client allows slightly longer than the chain's per-attempt cap, so the chain's own timer decides.
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// The effective order from <c>WebSearch:Providers</c> (comma-separated, so it works as an env var). Blank means the
    /// default. An unknown name is dropped and returned in <paramref name="unknown"/> (a typo must not take the app
    /// down). <c>searxng</c> is appended last when the list omits it, and kept last when it appears mid-list.
    /// </summary>
    public static IReadOnlyList<string> ParseOrder(string? raw, out IReadOnlyList<string> unknown)
    {
        var known = new[] { TavilyWebSearch.Provider, FirecrawlWebSearch.Provider };
        var order = new List<string>();
        var bad = new List<string>();
        var names = (string.IsNullOrWhiteSpace(raw) ? DefaultOrder : raw)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in names.Select(n => n.ToLowerInvariant()))
        {
            if (name == SearxngSearchProvider.Provider)
            {
                continue; // always appended last below
            }

            if (!known.Contains(name))
            {
                bad.Add(name);
            }
            else if (!order.Contains(name))
            {
                order.Add(name);
            }
        }

        order.Add(SearxngSearchProvider.Provider);
        unknown = bad;
        return order;
    }

    public static void AddWebSearchChain(this IServiceCollection services, IConfiguration configuration)
    {
        var order = ParseOrder(configuration["WebSearch:Providers"], out var unknown);
        var keys = ReadKeys(order, configuration);

        services.AddSingleton<WebSearchCooldowns>();
        AddKeyedClients(services, keys.Keys);

        services.AddSingleton<IWebSearch>(sp =>
        {
            var searxng = new SearxngWebSearch(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(SearxngWebSearch.HttpClientName),
                sp.GetRequiredService<ILogger<SearxngWebSearch>>());
            var logger = sp.GetRequiredService<ILogger<FallbackWebSearch>>();
            foreach (var name in unknown)
            {
                logger.LogWarning("WebSearch:Providers names an unknown provider {Provider}; it is ignored.", name);
            }

            if (keys.Count == 0)
            {
                return searxng;
            }

            var providers = CreateProviders(order, keys, searxng, sp.GetRequiredService<IHttpClientFactory>());
            logger.LogInformation("Web search chain: {Order}.", string.Join(" > ", providers.Select(p => p.Name)));
            return new FallbackWebSearch(providers, sp.GetRequiredService<WebSearchCooldowns>(), logger);
        });
    }

    /// <summary>The non-blank operator keys of the providers in the order, by provider name.</summary>
    private static Dictionary<string, string> ReadKeys(IReadOnlyList<string> order, IConfiguration configuration)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in order.Where(n => n != SearxngSearchProvider.Provider))
        {
            var key = configuration[$"WebSearch:{char.ToUpperInvariant(name[0])}{name[1..]}:ApiKey"];
            if (!string.IsNullOrWhiteSpace(key))
            {
                keys[name] = key.Trim();
            }
        }

        return keys;
    }

    private static List<IWebSearchProvider> CreateProviders(
        IReadOnlyList<string> order, Dictionary<string, string> keys, IWebSearch searxng, IHttpClientFactory factory)
    {
        var providers = new List<IWebSearchProvider>();
        foreach (var name in order)
        {
            if (name == SearxngSearchProvider.Provider)
            {
                providers.Add(new SearxngSearchProvider(searxng));
            }
            else if (keys.TryGetValue(name, out var key))
            {
                providers.Add(name == TavilyWebSearch.Provider
                    ? new TavilyWebSearch(factory.CreateClient(TavilyWebSearch.HttpClientName), key)
                    : new FirecrawlWebSearch(factory.CreateClient(FirecrawlWebSearch.HttpClientName), key));
            }
        }

        return providers;
    }

    // A client exists only when its key does, so CreateClient can never hand back a client that silently inherited the
    // resilience pipeline AddServiceDefaults applies to every other one (the Hindsight and geocoding reason). Retries
    // would multiply the stall on the non-reentrant grain; the chain is the retry. See WebRegistration for EXTEXP0001.
    private static void AddKeyedClients(IServiceCollection services, IEnumerable<string> configured)
    {
#pragma warning disable EXTEXP0001
        foreach (var name in configured)
        {
            var (clientName, baseAddress) = name == TavilyWebSearch.Provider
                ? (TavilyWebSearch.HttpClientName, "https://api.tavily.com/")
                : (FirecrawlWebSearch.HttpClientName, "https://api.firecrawl.dev/");
            services.AddHttpClient(clientName, http =>
            {
                http.BaseAddress = new Uri(baseAddress);
                http.Timeout = ClientTimeout;
            })
                .RemoveAllResilienceHandlers();
        }
#pragma warning restore EXTEXP0001
    }
}
