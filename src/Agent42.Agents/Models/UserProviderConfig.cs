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
    [property: Id(3)] bool SupportsVision,
    // Trailing [Id(4)] with a default so an existing overlay (persisted before ADR 0020) deserializes it
    // false with no migration — an image-generation model (ADR 0020), false = a chat model.
    [property: Id(4)] bool SupportsImageGeneration = false,
    // Trailing [Id(5)] with a default so an existing overlay deserializes it false with no migration —
    // true only for a declared embedding model (the user picks it as the "default embedding model" in
    // Preferences; document RAG resolves the generator from it). An embedding model is a distinct model
    // KIND sharing the provider's endpoint + key, reached via GetEmbeddingClient not the chat client.
    [property: Id(5)] bool SupportsEmbeddings = false,
    // Trailing [Id(6)] with a default so an existing overlay deserializes it false with no migration —
    // true only for a declared speech (text-to-speech) model (ADR 0038; the user picks it as the "default
    // speech model" in Preferences; the voice reply is synthesized from it). Like embeddings it is a
    // distinct model KIND sharing the provider's endpoint + key, reached via GetAudioClient not the chat client.
    [property: Id(6)] bool SupportsSpeech = false,
    // Trailing [Id(7)] with a default so an existing overlay deserializes it false with no migration —
    // true only for a declared REASONING model (ADR 0071), one that emits its own "thinking" alongside the
    // reply. Unlike embeddings and speech above this is NOT a distinct model kind: it is the same chat
    // model on the same chat client, and the flag only says to ask the provider for a reasoning summary
    // and to expect the extra content part back.
    [property: Id(7)] bool SupportsReasoning = false,
    // Trailing [Id(8)] with a default so an existing overlay deserializes it false with no migration —
    // true only for a declared TRANSCRIPTION (speech-to-text) model (the chat gateway's voice notes, ADR 0113). A
    // distinct model KIND like speech above, reached via GetAudioClient not the chat client.
    [property: Id(8)] bool SupportsTranscription = false,
    // Trailing [Id(9)] with a default so an existing overlay deserializes it null with no migration — the user's own
    // "when to use this model" text, the first layer of the Auto router's option description (ADR 0137).
    [property: Id(9)] string? Hint = null);

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
    [property: Id(2)] int Version,
    // Trailing [Id(3)] with a default so an existing overlay deserializes it null with no migration —
    // the user's chosen "default embedding model" id (qualified provider:model), null = none configured.
    // Independent of Default (the chat default): embeddings and chat are separate model kinds.
    [property: Id(3)] string? EmbeddingDefault = null,
    // Trailing [Id(4)] with a default so an existing overlay deserializes it null with no migration —
    // the user's chosen "default speech model" id (ADR 0038), null = none configured (voice falls back to
    // the browser's speechSynthesis, ADR 0037). Independent of Default/EmbeddingDefault — a distinct kind.
    [property: Id(4)] string? SpeechDefault = null,
    // Trailing [Id(5)] with a default so an existing overlay deserializes it null with no migration — the user's
    // chosen "default transcription model" id, null = none configured (a voice note then gets a curated reply).
    [property: Id(5)] string? TranscriptionDefault = null)
{
    /// <summary>
    /// A factory (fresh instance per access), never a cached singleton — a shared static default is
    /// mutated by Orleans' copier and leaks across grains (the same discipline as <c>UserProfile.Empty</c>).
    /// </summary>
    public static UserProviderConfig Empty => new([], null, 0, null, null, null);
}
