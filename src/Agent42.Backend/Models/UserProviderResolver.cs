using Agent42.Agents;
using Microsoft.Extensions.Configuration;

namespace Agent42.Backend;

/// <summary>A user's effective provider set: baseline ⊕ overlay, already built (ADR 0018).</summary>
public sealed record ResolvedProviderSet(
    IReadOnlyDictionary<string, MultiProviderChatClientFactory.Provider> Providers,
    IReadOnlyList<(string Key, string? DisplayName, FoundrySettings Settings)> Discovering,
    IReadOnlyList<ModelInfo> DeclaredModels,
    string DefaultModelId,
    int Version,
    // The user's chosen default embedding model id (document RAG resolves its generator from this), or ""
    // when none is configured or the pinned id no longer names a declared embedding-capable model. There is
    // NO baseline fallback — the operator embedding floor was removed with the per-user embedding model.
    string EmbeddingDefaultModelId = "",
    // The user's chosen default speech model id (ADR 0038; the voice reply is synthesized from this), or ""
    // when none is configured or the pinned id no longer names a declared speech-capable model. No baseline
    // fallback — an opt-in pick, exactly like the embedding default.
    string SpeechDefaultModelId = "");

/// <summary>
/// Resolves a user's effective (baseline ⊕ overlay) provider set (ADR 0018). An interface so the
/// per-user factory and catalog layers can be unit-tested against a fake resolver without a grain.
/// </summary>
public interface IUserProviderResolver
{
    Task<ResolvedProviderSet> ResolveAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The single source of truth for "this user's effective providers" (ADR 0018): merges the operator
/// baseline (the bound <c>Models:</c> set, the availability floor) with the user's persisted overlay,
/// and hands the result to both the per-user chat-client factory and the per-user model catalog.
///
/// <para>The merge is <b>options-level, then build via <see cref="ModelProviderBuilder.TryBuildProvider"/></b>
/// — the same per-provider rules the baseline uses, from one place (no duplication of the Foundry
/// inheritance / skip / fatal logic). The only difference from startup: an <c>Invalid</c> overlay build
/// is <b>not fatal</b> — it falls back to the baseline entry, so a user override can never brick the app.</para>
/// </summary>
public sealed class UserProviderResolver : IUserProviderResolver
{
    // Overlay providers are built with NO inheritance of the operator's AzureOpenAI:* secrets: a user's
    // Foundry provider must declare its own endpoint + key, so the operator key is never sent to a
    // user-chosen endpoint, and a blanked Foundry key genuinely resolves Invalid (→ baseline fallback,
    // AC2) rather than silently re-inheriting the operator key. Inheritance stays a baseline-only concept.
    private static readonly IConfiguration NoInheritance = new ConfigurationBuilder().Build();

    private readonly IGrainFactory _grains;
    private readonly IProviderSecretProtector _protector;
    private readonly ModelProviderSetup _baseline;
    private readonly string _baselineDefault;

    public UserProviderResolver(
        IGrainFactory grains,
        IProviderSecretProtector protector,
        ModelProviderSetup baseline,
        string baselineDefault)
    {
        _grains = grains;
        _protector = protector;
        _baseline = baseline;
        _baselineDefault = baselineDefault;
    }

    /// <summary>Fetches the user's overlay and merges it onto the baseline.</summary>
    public async Task<ResolvedProviderSet> ResolveAsync(string userId, CancellationToken cancellationToken = default)
    {
        var overlay = await _grains.GetGrain<IUserProviderConfigGrain>(userId).GetAsync();
        return Merge(overlay);
    }

    /// <summary>
    /// The pure merge — baseline ⊕ overlay — with no grain I/O, so it is unit-testable directly. Each
    /// overlay provider upserts its key onto the baseline: <c>Built</c> replaces that key's provider and
    /// catalog contributions; <c>Skipped</c>/<c>Invalid</c> leave whatever the baseline had (keep the
    /// baseline entry if one exists, else nothing — never throw).
    /// </summary>
    public ResolvedProviderSet Merge(UserProviderConfig overlay)
    {
        var providers = new Dictionary<string, MultiProviderChatClientFactory.Provider>(
            _baseline.Providers, StringComparer.OrdinalIgnoreCase);
        var discovering = _baseline.Discovering.ToList();
        var declared = _baseline.DeclaredModels.ToList();

        foreach (var stored in overlay.Providers)
        {
            if (!Enum.TryParse<ProviderKind>(stored.Kind, ignoreCase: true, out var kind))
            {
                // An unrecognised kind (the API validates it, so this is defense) contributes nothing —
                // it leaves the baseline entry for this key untouched.
                continue;
            }

            var result = ModelProviderBuilder.TryBuildProvider(stored.Key, ToOptions(stored, kind), NoInheritance);
            if (result.Status != ProviderBuildStatus.Built)
            {
                // Skipped (OpenAI-compatible with no key) or Invalid (e.g. a Foundry override with a
                // blank key) → keep the baseline-built entry if one exists, else skip. Availability floor.
                continue;
            }

            providers[stored.Key] = result.Provider!;

            // Replace this key's catalog contributions with the overlay's (the provider was overridden).
            declared.RemoveAll(m => string.Equals(m.Provider, stored.Key, StringComparison.OrdinalIgnoreCase));
            discovering.RemoveAll(d => string.Equals(d.Key, stored.Key, StringComparison.OrdinalIgnoreCase));
            if (result.Discovering is { } d)
            {
                discovering.Add(d);
            }

            if (result.DeclaredModels is { Count: > 0 } dm)
            {
                declared.AddRange(dm);
            }
        }

        return new ResolvedProviderSet(
            providers, discovering, declared, EffectiveDefault(overlay, providers, declared), overlay.Version,
            EffectiveEmbeddingDefault(overlay, declared),
            EffectiveSpeechDefault(overlay, declared));
    }

    // The effective embedding default: the pinned id only when it names a declared EMBEDDING-capable model,
    // else "". Unlike the chat default there is no baseline fallback (the operator embedding floor is gone)
    // and no first-model fallback — an embedding model is an explicit, opt-in pick, and a chat-only model set
    // must NOT silently become the embedding model. "" means "no embedding model" → RAG upload is blocked and
    // retrieval degrades to empty (never throws).
    private static string EffectiveEmbeddingDefault(UserProviderConfig overlay, IReadOnlyList<ModelInfo> declared)
    {
        if (string.IsNullOrWhiteSpace(overlay.EmbeddingDefault))
        {
            return "";
        }

        return declared.Any(m =>
            m.SupportsEmbeddings && string.Equals(m.Id, overlay.EmbeddingDefault, StringComparison.OrdinalIgnoreCase))
            ? overlay.EmbeddingDefault!
            : "";
    }

    // The effective speech default (ADR 0038): the pinned id only when it names a declared SPEECH-capable
    // model, else "". Same discipline as the embedding default — no baseline/first-model fallback (an opt-in
    // pick), "" means "no speech model" → /api/speech 409s and the browser voice (ADR 0037) stays in use.
    private static string EffectiveSpeechDefault(UserProviderConfig overlay, IReadOnlyList<ModelInfo> declared)
    {
        if (string.IsNullOrWhiteSpace(overlay.SpeechDefault))
        {
            return "";
        }

        return declared.Any(m =>
            m.SupportsSpeech && string.Equals(m.Id, overlay.SpeechDefault, StringComparison.OrdinalIgnoreCase))
            ? overlay.SpeechDefault!
            : "";
    }

    // Picks the effective default model, honouring a pinned default only when it names a provider that
    // actually built — otherwise the factory's Resolve would throw resolving it against a missing provider.
    // Order (ADR 0019): a valid overlay default → a valid baseline default → the first resolved declared
    // model → "". The baseline now ships empty (its default is ""), so the first-declared-model fallback is
    // what gives a user with a single self-configured provider a sensible default without pinning one; a
    // non-empty provider set always declares ≥1 model (the API's ≥1-model rule), so this yields "" only for
    // genuinely zero providers — an empty picker, not a bogus "" model.
    private string EffectiveDefault(
        UserProviderConfig overlay,
        IReadOnlyDictionary<string, MultiProviderChatClientFactory.Provider> providers,
        IReadOnlyList<ModelInfo> declared)
    {
        if (NamesBuiltProvider(overlay.Default, providers))
        {
            return overlay.Default!;
        }

        if (NamesBuiltProvider(_baselineDefault, providers))
        {
            return _baselineDefault;
        }

        return declared.Count > 0 ? declared[0].Id : "";
    }

    private static bool NamesBuiltProvider(
        string? modelId, IReadOnlyDictionary<string, MultiProviderChatClientFactory.Provider> providers)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        // A GitHub Copilot model is never a default (ADR 0108): it runs only in Copilot agents, so a plain chat that
        // started on it would hit the refusing client. Being built is not enough.
        var (providerKey, _) = ModelId.Parse(modelId, "");
        return providers.TryGetValue(providerKey, out var provider) && provider.Options.Kind != ProviderKind.GitHubCopilot;
    }

    // A stored (overlay) provider → the same ProviderOptions shape the baseline uses, with the API key
    // decrypted. Discover is always false: a user provider never ARM-discovers (ADR 0017/0018) — a
    // Foundry provider declares its deployments.
    private ProviderOptions ToOptions(StoredProvider stored, ProviderKind kind) => new()
    {
        Kind = kind,
        DisplayName = stored.DisplayName,
        Endpoint = stored.Endpoint,
        ApiKey = _protector.Unprotect(stored.ApiKeyCiphertext),
        Discover = false,
        UseResponsesApi = stored.UseResponsesApi,
        Models = [.. stored.Models.Select(m => new ConfiguredModel
        {
            Id = m.Id,
            DisplayName = m.DisplayName,
            SupportsTools = m.SupportsTools,
            SupportsVision = m.SupportsVision,
            SupportsImageGeneration = m.SupportsImageGeneration,
            SupportsEmbeddings = m.SupportsEmbeddings,
            SupportsSpeech = m.SupportsSpeech,
            SupportsReasoning = m.SupportsReasoning,
        })],
    };
}
