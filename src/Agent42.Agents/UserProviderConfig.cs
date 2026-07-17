namespace Agent42.Agents;

/// <summary>
/// One model a user-configured provider declares. Mirrors the Backend's <c>ConfiguredModel</c>
/// shape but lives in the Agents layer (Backend references Agents, not the reverse — see ADR 0018),
/// so the overlay grain carries no upward dependency on the Backend options types.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.StoredModel")]
public sealed record StoredModel(
    [property: Id(0)] string Id,
    [property: Id(1)] string? DisplayName,
    [property: Id(2)] bool SupportsTools,
    [property: Id(3)] bool SupportsVision);

/// <summary>
/// One provider in a user's overlay (ADR 0018). Mirrors the Backend's <c>ProviderOptions</c>, but:
/// <list type="bullet">
/// <item><see cref="Kind"/> is a <b>string</b> the Backend resolver parses into its
/// <c>ProviderKind</c> — the enum lives in the Backend and cannot be referenced from this layer.</item>
/// <item><see cref="ApiKeyCiphertext"/> is the API key <b>already encrypted</b> (the API layer protects
/// it via <c>IProviderSecretProtector</c> before the grain ever sees it): the grain stores ciphertext,
/// never plaintext, and it rides a distinct <c>"providers"</c> state — never the 2 MB session (ADR 0004).
/// A blank ciphertext on an update means "keep the stored key" (see <see cref="IUserProviderConfigGrain"/>).</item>
/// </list>
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.StoredProvider")]
public sealed record StoredProvider(
    [property: Id(0)] string Key,
    [property: Id(1)] string Kind,
    [property: Id(2)] string? DisplayName,
    [property: Id(3)] string? Endpoint,
    [property: Id(4)] string? ApiKeyCiphertext,
    [property: Id(5)] IReadOnlyList<StoredModel> Models,
    [property: Id(6)] bool UseResponsesApi);

/// <summary>
/// A user's provider overlay (ADR 0018): the providers they added or overrode, an optional default
/// model id, and a monotonically-increasing <see cref="Version"/> the grain bumps on every write.
/// The version is the cache key the per-user factory/catalog layer stamps on, so a save is reflected
/// without an app restart. An empty overlay (the default) means "baseline only" — no behaviour change.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.UserProviderConfig")]
public sealed record UserProviderConfig(
    [property: Id(0)] IReadOnlyList<StoredProvider> Providers,
    [property: Id(1)] string? Default,
    [property: Id(2)] int Version)
{
    /// <summary>
    /// A factory (fresh instance per access), never a cached singleton — a shared static default is
    /// mutated by Orleans' copier and leaks across grains (the same discipline as <c>UserProfile.Empty</c>).
    /// </summary>
    public static UserProviderConfig Empty => new([], null, 0);
}
