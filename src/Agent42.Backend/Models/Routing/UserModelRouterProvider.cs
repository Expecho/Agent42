using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The backend <see cref="IUserModelRouterProvider"/> (ADR 0137): a router exists only for a user with an enabled
/// DecisionModel provider. The candidates are read from the user's own catalog on each turn (so a new provider or a
/// changed hint applies at once); the router holds no per-turn state (ADR 0106).
/// </summary>
public sealed class UserModelRouterProvider(
    IUserProviderResolver resolver,
    IUserModelCatalogProvider catalogs,
    IHttpClientFactory httpClients,
    ILoggerFactory loggers,
    IModelCardSource? cards = null) : IUserModelRouterProvider
{
    public async Task<IModelRouter?> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var resolved = await resolver.ResolveAsync(userId, cancellationToken);
        if (resolved.Decision is not { } settings)
        {
            return null;
        }

        var client = new FoundryDecisionClient(
            httpClients.CreateClient(FoundryDecisionClient.HttpClientName), settings, loggers.CreateLogger<FoundryDecisionClient>());
        return new CatalogModelRouter(userId, catalogs, client, loggers.CreateLogger<CatalogModelRouter>(), cards);
    }

    /// <summary>Registers the named client and the provider. Backend-only: DevHost and test silos register their own port (ADR 0105).</summary>
    public static void AddModelRouter(IServiceCollection services)
    {
        FoundryDecisionClient.AddHttpClient(services);
        OpenRouterModelCardSource.AddHttpClient(services);
        services.AddSingleton<IModelCardSource>(sp => new OpenRouterModelCardSource(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(OpenRouterModelCardSource.HttpClientName),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<OpenRouterModelCardSource>>()));
        services.AddSingleton<IUserModelRouterProvider>(sp => new UserModelRouterProvider(
            sp.GetRequiredService<IUserProviderResolver>(),
            sp.GetRequiredService<IUserModelCatalogProvider>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetService<IModelCardSource>()));
    }
}

/// <summary>Builds the candidate list from the user's catalog for each turn, then delegates to <see cref="ModelRouterService"/>.</summary>
public sealed class CatalogModelRouter(
    string userId, IUserModelCatalogProvider catalogs, IDecisionClient client, ILogger logger, IModelCardSource? cards = null) : IModelRouter
{
    private readonly ModelRouterOptions _options = new();

    public async Task<RouterDecision?> RouteAsync(RouterTurn turn, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ModelInfo> models;
        try
        {
            models = (await (await catalogs.GetForUserAsync(userId, cancellationToken)).GetAsync(cancellationToken)).Models;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Model router could not read the catalog; keeping the current model");
            return null;
        }

        var candidates = models.Select(m => ToCandidate(m, cards)).ToList();
        return await new ModelRouterService(candidates, client, _options, logger).RouteAsync(turn, cancellationToken);
    }

    public static RouterCandidate ToCandidate(ModelInfo m, IModelCardSource? cards = null) => new(
        m.Id,
        m.DisplayName ?? m.Model ?? m.Id,
        m.SupportsTools,
        m.SupportsVision,
        m.CopilotOnly,
        IsChat: !(m.SupportsImageGeneration || m.SupportsEmbeddings || m.SupportsSpeech || m.SupportsTranscription),
        Hint: m.Hint,
        // Layer two (ADR 0137): only when the user wrote no hint, by the provider's own model name, from cache.
        CardText: string.IsNullOrWhiteSpace(m.Hint) ? cards?.TryGetDescription(m.Model ?? m.Id) : null);
}
