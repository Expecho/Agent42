

namespace Agent42.Backend;

// Client payload for creating/updating a user provider (ADR 0018). ApiKey is WRITE-ONLY — it is
// encrypted server-side and never echoed back; blank on an update means "keep the stored key". Kind is
// a ProviderKind name ("AzureOpenAI", "OpenAICompatible" or "Anthropic"). Models/UseResponsesApi are trailing defaults so a minimal body works.
public record ProviderUpsertRequest(
    string Key, string Kind, string? DisplayName, string? Endpoint, string? ApiKey,
    IReadOnlyList<ProviderModelRequest>? Models = null, bool UseResponsesApi = false);

// Client payload for PUT /api/default-model: the qualified `provider:model` id new chats start on. Null or
// blank clears it (the grain normalises blank to null), and the resolver then falls back to the first
// declared model. The id is opaque here — never split past the first colon, never case-normalised.
public record DefaultModelRequest(string? Default);

// The default EMBEDDING model id (document RAG, ADR 0007/0018). Blank clears it (the grain normalises blank
// to null), and RAG is then unavailable until the user picks one. Not validated against the catalog here —
// the resolver's EffectiveEmbeddingDefault is the single validation point (see /api/default-embedding-model).
public record DefaultEmbeddingModelRequest(string? EmbeddingDefault);

// The default SPEECH model id (voice reply TTS, ADR 0037/0038). Blank clears it (the grain normalises blank
// to null), and the voice reply then falls back to the browser's speechSynthesis. Not validated against the
// catalog here — the resolver's EffectiveSpeechDefault is the single validation point.
public record DefaultSpeechModelRequest(string? SpeechDefault);

// SupportsImageGeneration (ADR 0020), SupportsEmbeddings and SupportsSpeech (ADR 0038) and SupportsReasoning
// (ADR 0071) are trailing defaults so an old-shape client body still binds — an image model produces images
// instead of chat replies; an embedding model is picked as the "default embedding model" and feeds document
// RAG; a speech model is picked as the "default speech model" and synthesizes the voice reply; a reasoning
// model is an ordinary chat model that also streams its own thinking.
public record ProviderModelRequest(
    string Id, string? DisplayName, bool SupportsTools, bool SupportsVision,
    bool SupportsImageGeneration = false, bool SupportsEmbeddings = false, bool SupportsSpeech = false,
    bool SupportsReasoning = false);

// What GET/POST/PUT return for a provider: everything EXCEPT the key, which is masked to HasKey so the
// ciphertext (and the plaintext) never reach the client (ADR 0018).
public record ProviderResponse(
    string Key, string Kind, string? DisplayName, string? Endpoint, bool HasKey,
    IReadOnlyList<ProviderModelResponse> Models, bool UseResponsesApi);

public record ProviderModelResponse(
    string Id, string? DisplayName, bool SupportsTools, bool SupportsVision,
    bool SupportsImageGeneration = false, bool SupportsEmbeddings = false, bool SupportsSpeech = false,
    bool SupportsReasoning = false);

// The GET /api/providers payload: the user's overlay providers (masked), their optional chat default, their
// optional embedding default (the "default embedding model" id), and their optional speech default (the
// "default speech model" id, ADR 0038, null = none configured → the browser voice is used).
public record ProvidersResponse(
    IReadOnlyList<ProviderResponse> Providers, string? Default, string? EmbeddingDefault = null,
    string? SpeechDefault = null);
