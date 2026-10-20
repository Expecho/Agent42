using Agent42.Agents;
using Azure.ResourceManager;

namespace Agent42.Backend;

/// <summary>
/// Resolves the <see cref="IModelCatalog"/> a specific user's picker is built from (ADR 0018): the
/// models of their effective (baseline ⊕ overlay) provider set. The <c>/api/models</c> endpoint resolves
/// the userId from the request and asks this, so an added provider's models show up in the picker with
/// no app restart.
/// </summary>
public interface IUserModelCatalogProvider
{
    Task<IModelCatalog> GetForUserAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds a per-user <see cref="CompositeModelCatalog"/> from the resolved provider set: the declared
/// models directly, plus an <see cref="AzureModelCatalog"/> per discovering Foundry provider. Built
/// fresh per call (construction does no I/O — only <see cref="IModelCatalog.GetAsync"/> queries ARM),
/// so it always reflects the latest overlay.
/// </summary>
public sealed class UserModelCatalogProvider : IUserModelCatalogProvider
{
    private readonly IUserProviderResolver _resolver;
    private readonly ArmClient _armClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CopilotModelDirectory? _copilotModels;

    // The Copilot model directory is optional: a host without the Copilot runtime simply lists no GitHub Copilot models.
    public UserModelCatalogProvider(
        IUserProviderResolver resolver, ArmClient armClient, ILoggerFactory loggerFactory,
        CopilotModelDirectory? copilotModels = null)
    {
        _resolver = resolver;
        _armClient = armClient;
        _loggerFactory = loggerFactory;
        _copilotModels = copilotModels;
    }

    public async Task<IModelCatalog> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var resolved = await _resolver.ResolveAsync(userId, cancellationToken);

        var discovering = resolved.Discovering
            .Select(d => (IModelCatalog)new AzureModelCatalog(
                _armClient, d.Key, d.DisplayName, d.Settings, resolved.DefaultModelId,
                _loggerFactory.CreateLogger<AzureModelCatalog>()))
            .ToList();

        // A connected GitHub Copilot provider (ADR 0108) discovers its plan's models through the Copilot runtime. Its
        // token is carried only by the provider's Copilot settings.
        if (_copilotModels is not null
            && resolved.Providers.TryGetValue(GitHubCopilotProvider.Key, out var github)
            && github.Copilot is { UsesGitHubAccount: true } settings)
        {
            discovering.Add(new CopilotModelCatalog(_copilotModels, settings.ApiKey));
        }

        return new CompositeModelCatalog(
            discovering, resolved.DeclaredModels, resolved.DefaultModelId,
            _loggerFactory.CreateLogger<CompositeModelCatalog>());
    }
}

/// <summary>
/// An <see cref="IUserModelCatalogProvider"/> that hands back the SAME catalog for every user — DevHost
/// and tests, where a fixed <see cref="IModelCatalog"/> stands in and there is no per-user overlay store.
/// </summary>
public sealed class SingleUserModelCatalogProvider(IModelCatalog catalog) : IUserModelCatalogProvider
{
    public Task<IModelCatalog> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(catalog);
}
