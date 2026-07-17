using Agent42.Agents;
using Microsoft.Extensions.Configuration;

namespace Agent42.Backend;

/// <summary>A user's effective provider set: baseline ⊕ overlay, already built (ADR 0018).</summary>
public sealed record ResolvedProviderSet(
    IReadOnlyDictionary<string, MultiProviderChatClientFactory.Provider> Providers,
    IReadOnlyList<(string Key, FoundrySettings Settings)> Discovering,
    IReadOnlyList<ModelInfo> DeclaredModels,
    string DefaultModelId,
    int Version);

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
            providers, discovering, declared, EffectiveDefault(overlay, providers, declared), overlay.Version);
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

        var (providerKey, _) = ModelId.Parse(modelId, "");
        return providers.ContainsKey(providerKey);
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
        })],
    };
}
