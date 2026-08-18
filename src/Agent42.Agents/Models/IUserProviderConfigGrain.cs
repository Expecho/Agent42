namespace Agent42.Agents.Models;

/// <summary>
/// Stores a signed-in user's model-provider overlay (ADR 0018). The grain key is the user id, like
/// <see cref="IUserProfileGrain"/> and <see cref="IAgentRegistryGrain"/>. A singleton per user — one
/// overlay, not a per-conversation thing — so it exposes upsert/delete-by-key plus a version read the
/// per-user factory/catalog layer polls to invalidate its cache.
///
/// <para>The grain stores API keys as <b>ciphertext</b> only: the API layer encrypts a key (via
/// <c>IProviderSecretProtector</c>) before calling <see cref="SaveAsync"/>, and the grain never sees or
/// returns plaintext. On update, a blank <see cref="StoredProvider.ApiKeyCiphertext"/> means "keep the
/// stored key", so the user need not re-enter a secret to edit other fields.</para>
/// </summary>
[Alias("Agent42.Agents.IUserProviderConfigGrain")]
public interface IUserProviderConfigGrain : IGrainWithStringKey
{
    /// <summary>Returns the user's overlay, or <see cref="UserProviderConfig.Empty"/> when never saved.</summary>
    [Alias("GetAsync")]
    Task<UserProviderConfig> GetAsync();

    /// <summary>The overlay's current version — bumped on every write. Cheap poll for cache invalidation.</summary>
    [Alias("GetVersionAsync")]
    Task<int> GetVersionAsync();

    /// <summary>
    /// Upserts one provider by <see cref="StoredProvider.Key"/> and bumps the version. A blank
    /// <see cref="StoredProvider.ApiKeyCiphertext"/> on an existing key keeps its stored ciphertext.
    /// Returns the stored provider (with the retained ciphertext).
    /// </summary>
    [Alias("SaveAsync")]
    Task<StoredProvider> SaveAsync(StoredProvider provider);

    /// <summary>Sets (or clears, with null/blank) the overlay's default model id and bumps the version.</summary>
    [Alias("SetDefaultAsync")]
    Task SetDefaultAsync(string? defaultModelId);

    /// <summary>
    /// Sets (or clears, with null/blank) the overlay's default <b>embedding</b> model id and bumps the
    /// version. Independent of <see cref="SetDefaultAsync"/> (chat) — document RAG resolves its generator
    /// from this id, null = no embedding model configured.
    /// </summary>
    [Alias("SetEmbeddingDefaultAsync")]
    Task SetEmbeddingDefaultAsync(string? embeddingModelId);

    /// <summary>
    /// Sets (or clears, with null/blank) the overlay's default <b>speech</b> (text-to-speech) model id and
    /// bumps the version (ADR 0038). Independent of the chat/embedding defaults — the voice reply is
    /// synthesized from this id server-side, null = fall back to the browser voice (ADR 0037).
    /// </summary>
    [Alias("SetSpeechDefaultAsync")]
    Task SetSpeechDefaultAsync(string? speechModelId);

    /// <summary>Removes the provider with <paramref name="key"/>, bumping the version only if one was removed.</summary>
    [Alias("DeleteAsync")]
    Task DeleteAsync(string key);
}
