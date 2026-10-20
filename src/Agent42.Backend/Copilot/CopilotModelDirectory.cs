using Agent42.Agents;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Agent42.Backend;

/// <summary>One model of a user's GitHub Copilot plan, as the runtime lists it.</summary>
public sealed record CopilotListedModel(string Id, string? Name);

/// <summary>Lists the Copilot models a GitHub token's plan allows (ADR 0108). Implemented by the chat's runtime host.</summary>
public interface ICopilotModelLister
{
    Task<IReadOnlyList<CopilotListedModel>> ListAsync(string gitHubToken, CancellationToken cancellationToken);
}

/// <summary>
/// The models of each connected user's GitHub Copilot plan (ADR 0108), cached in process (single replica, CLAUDE.md
/// rule 12) so <c>/api/models</c> does not ask the runtime on every call. Keyed by a hash of the token, never the token
/// itself, so a reconnect (a new token) is a new entry. A listing is bounded by <see cref="DefaultTimeout"/>, and any failure
/// (runtime missing, GitHub down, a revoked token) gives an empty list and a log line, never an exception: the picker
/// degrades, it does not fail. The capabilities of these models do NOT come from here
/// (<c>MultiProviderChatClientFactory.GetCapabilities</c> fixes them), so an expired entry can't change a chat.
/// </summary>
public sealed class CopilotModelDirectory(
    ICopilotModelLister lister, TimeProvider time, ILogger<CopilotModelDirectory> logger, TimeSpan? timeout = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a FAILED listing is remembered (as "no models"), so a hung runtime or a slow GitHub can't stall every
    /// <c>/api/models</c> call (and every fleet submit) for <see cref="DefaultTimeout"/>.
    /// </summary>
    public static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(1);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, IReadOnlyList<CopilotListedModel> Models)> _cache = new();

    public async Task<IReadOnlyList<CopilotListedModel>> GetAsync(string gitHubToken, CancellationToken cancellationToken)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gitHubToken)));
        if (_cache.TryGetValue(key, out var hit) && time.GetUtcNow() < hit.Until)
        {
            return hit.Models;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            var models = await lister.ListAsync(gitHubToken, timeout.Token).WaitAsync(timeout.Token);
            _cache[key] = (time.GetUtcNow() + Ttl, models);
            return models;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Remembered briefly, then the next picker load tries again.
            logger.LogWarning(ex, "Could not list the GitHub Copilot models of a connected account; offering none.");
            _cache[key] = (time.GetUtcNow() + FailureTtl, []);
            return [];
        }
    }
}

/// <summary>
/// The catalog contribution of a connected GitHub Copilot provider (ADR 0108): its plan's models, flagged
/// <see cref="ModelInfo.CopilotOnly"/> so only Copilot pickers offer them.
/// </summary>
public sealed class CopilotModelCatalog(CopilotModelDirectory directory, string gitHubToken) : IModelCatalog
{
    public async Task<ModelCatalogResult> GetAsync(CancellationToken cancellationToken = default)
    {
        var models = await directory.GetAsync(gitHubToken, cancellationToken);
        return new ModelCatalogResult("", [.. models.Select(m => new ModelInfo(
            Id: ModelId.Qualify(GitHubCopilotProvider.Key, m.Id),
            Provider: GitHubCopilotProvider.Key,
            DisplayName: m.Name ?? m.Id,
            Model: m.Id,
            Version: null,
            SupportsTools: true,
            SupportsVision: false,
            ProviderDisplayName: GitHubCopilotProvider.DisplayName,
            CopilotOnly: true))]);
    }
}
