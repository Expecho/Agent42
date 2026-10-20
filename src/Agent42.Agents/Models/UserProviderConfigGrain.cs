using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.UserProviderConfigState")]
public sealed class UserProviderConfigState
{
    // UserProviderConfig.Empty is a factory (fresh instance), so this is a per-state default, never a
    // shared mutable static — see the note on UserProviderConfig.Empty.
    [Id(0)]
    public UserProviderConfig Config { get; set; } = UserProviderConfig.Empty;
}

/// <summary>
/// Per-user model-provider overlay grain (ADR 0018). Rides the shared "agent-state" storage provider
/// on a distinct "providers" state name (like <see cref="UserProfileGrain"/>'s "profile"), so it is a
/// separate Cosmos item from the 2 MB session (ADR 0004) — a user secret never lands in the session.
/// </summary>
public sealed class UserProviderConfigGrain : Grain, IUserProviderConfigGrain
{
    private readonly IPersistentState<UserProviderConfigState> _state;

    public UserProviderConfigGrain(
        [PersistentState("providers", AgentGrain.StorageProviderName)] IPersistentState<UserProviderConfigState> state)
    {
        _state = state;
    }

    public Task<UserProviderConfig> GetAsync() => Task.FromResult(_state.State.Config);

    public Task<int> GetVersionAsync() => Task.FromResult(_state.State.Config.Version);

    public Task<StoredProvider> SaveAsync(StoredProvider provider)
    {
        // The API refuses these first; this is the same rule where the data lives (ADR 0108).
        if (GitHubCopilotProvider.IsReserved(provider.Key, provider.Kind))
        {
            throw new ArgumentException("The GitHub Copilot provider is connected with GitHub sign-in, not saved directly.");
        }

        return UpsertAsync(provider);
    }

    public Task<StoredProvider> ConnectGitHubCopilotAsync(string tokenCiphertext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenCiphertext);
        return UpsertAsync(new StoredProvider(
            GitHubCopilotProvider.Key, GitHubCopilotProvider.Kind, GitHubCopilotProvider.DisplayName,
            // A real List, never a collection expression: this is grain state (gotcha "A collection expression into
            // IReadOnlyList<T> written from INSIDE a grain is unreadable from Cosmos").
            Endpoint: null, tokenCiphertext, Models: new List<StoredModel>(), UseResponsesApi: false));
    }

    private async Task<StoredProvider> UpsertAsync(StoredProvider provider)
    {
        var providers = _state.State.Config.Providers.ToList();
        var existing = providers.FindIndex(
            p => string.Equals(p.Key, provider.Key, StringComparison.OrdinalIgnoreCase));

        StoredProvider stored;
        if (existing >= 0)
        {
            // A blank incoming ciphertext means "keep the stored key" (the API sends no ciphertext when
            // the user left the write-only key field empty) — so the user needn't re-enter a secret to
            // edit other fields. A non-blank ciphertext replaces it.
            var key = string.IsNullOrEmpty(provider.ApiKeyCiphertext)
                ? providers[existing].ApiKeyCiphertext
                : provider.ApiKeyCiphertext;
            stored = provider with { ApiKeyCiphertext = key };
            providers[existing] = stored;
        }
        else
        {
            stored = provider;
            providers.Add(stored);
        }

        await BumpAsync(_state.State.Config with { Providers = providers });
        return stored;
    }

    public Task SetDefaultAsync(string? defaultModelId)
    {
        var value = string.IsNullOrWhiteSpace(defaultModelId) ? null : defaultModelId;
        return BumpAsync(_state.State.Config with { Default = value });
    }

    public Task SetEmbeddingDefaultAsync(string? embeddingModelId)
    {
        var value = string.IsNullOrWhiteSpace(embeddingModelId) ? null : embeddingModelId;
        return BumpAsync(_state.State.Config with { EmbeddingDefault = value });
    }

    public Task SetSpeechDefaultAsync(string? speechModelId)
    {
        var value = string.IsNullOrWhiteSpace(speechModelId) ? null : speechModelId;
        return BumpAsync(_state.State.Config with { SpeechDefault = value });
    }

    public async Task DeleteAsync(string key)
    {
        var providers = _state.State.Config.Providers.ToList();
        if (providers.RemoveAll(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            await BumpAsync(_state.State.Config with { Providers = providers });
        }
    }

    // Every write bumps the monotonic version, which the per-user factory/catalog layer keys its cache
    // on (ADR 0018) — so a save is picked up without an app restart.
    private async Task BumpAsync(UserProviderConfig next)
    {
        _state.State.Config = next with { Version = next.Version + 1 };
        await _state.WriteStateAsync();
    }
}
