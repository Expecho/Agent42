using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Model catalogue and the per-user provider overlay (ADR 0013/0018/0030/0038), including the three separate defaults. Note PUT /api/default-model is a distinct path rather than /api/providers/default, because a user-chosen provider key would out-rank the literal segment.
/// </summary>
public static partial class AgentApi
{
    private static void MapModelEndpoints(IEndpointRouteBuilder app)
    {
        // The models available to the SIGNED-IN USER: their effective (baseline ⊕ overlay) provider set
        // (ADR 0018), so a provider they added shows up here without an app restart.
        app.MapGet("/api/models", async (IUserModelCatalogProvider catalogs, HttpContext http, CancellationToken ct) =>
            await (await catalogs.GetForUserAsync(http.GetUserId(), ct)).GetAsync(ct));

        // The signed-in user's OWN configured providers (their overlay — not the operator baseline).
        // Keys are MASKED to a hasKey flag: the ciphertext never leaves the server, and the plaintext
        // never existed here.
        app.MapGet("/api/providers", async (IGrainFactory grains, HttpContext http) =>
        {
            var overlay = await grains.GetGrain<IUserProviderConfigGrain>(http.GetUserId()).GetAsync();
            return Results.Json(new ProvidersResponse(
                [.. overlay.Providers.Select(Mask)], overlay.Default, overlay.EmbeddingDefault, overlay.SpeechDefault));
        });

        // Creates or updates a provider by key. The key is user-chosen and identifies the provider, so
        // POST upserts (like the grain's SaveAsync). The API ENCRYPTS the key here — the grain only ever
        // stores ciphertext (ADR 0018). A blank ApiKey on a new provider means "no key" (a supported,
        // skipped state for an OpenAI-compatible provider).
        app.MapPost("/api/providers", async (ProviderUpsertRequest request, IProviderSecretProtector protector, IGrainFactory grains, HttpContext http) =>
        {
            var error = ValidateProvider(request.Key, request.Kind, request.Endpoint, request.Models);
            if (error is not null)
            {
                return Results.Text(error, "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }

            var stored = await grains.GetGrain<IUserProviderConfigGrain>(http.GetUserId())
                .SaveAsync(ToStored(request, protector));
            return Results.Json(Mask(stored));
        });

        // Updates the provider named by the route key. A BLANK ApiKey means "keep the stored key" (the
        // field is write-only in the UI), so the user needn't re-enter a secret to edit other fields —
        // ToStored maps blank to a null ciphertext, which the grain reads as "keep".
        app.MapPut("/api/providers/{key}", async (string key, ProviderUpsertRequest request, IProviderSecretProtector protector, IGrainFactory grains, HttpContext http) =>
        {
            var error = ValidateProvider(key, request.Kind, request.Endpoint, request.Models);
            if (error is not null)
            {
                return Results.Text(error, "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }

            var stored = await grains.GetGrain<IUserProviderConfigGrain>(http.GetUserId())
                .SaveAsync(ToStored(request with { Key = key }, protector));
            return Results.Json(Mask(stored));
        });

        // Deletes a provider from the user's overlay. A conversation pinned to it degrades to the default
        // (ADR 0018/0013), so this never disturbs a conversation.
        app.MapDelete("/api/providers/{key}", async (string key, IGrainFactory grains, HttpContext http) =>
        {
            await grains.GetGrain<IUserProviderConfigGrain>(http.GetUserId()).DeleteAsync(key);
            return Results.NoContent();
        });

        // Sets (or clears, with a blank body) the model new chats start on — the overlay default the
        // resolver already prefers (ADR 0018). The path is NOT /api/providers/default: a provider key is
        // USER-chosen, so a user may legitimately key a provider `default`, and ASP.NET Core routing gives
        // the literal segment precedence over {key} regardless of mapping order — which would silently make
        // that provider un-editable. GET stays on /api/providers (ProvidersResponse already carries Default).
        //
        // The id is NOT validated against the catalog here: a default naming a currently-unresolvable
        // provider is legal and the resolver's read-time NamesBuiltProvider guard already falls through to
        // the next candidate. Validating at write time would reject a temporarily-unresolvable id and put
        // that rule in a second place. The 200-char cap is not validation either — it exists solely to keep
        // unbounded text out of a persisted grain state; a qualified id is `provider:model` and both halves
        // are bounded by their sources, so 200 is far above any real id.
        app.MapPut("/api/default-model", async (DefaultModelRequest request, IGrainFactory grains, HttpContext http) =>
        {
            if (request.Default is { Length: > 200 })
            {
                return Results.Text(
                    "Default model id must be at most 200 characters.",
                    "text/plain",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await grains.GetGrain<IUserProviderConfigGrain>(http.GetUserId()).SetDefaultAsync(request.Default);
            return Results.NoContent();
        });

        // Sets (or clears, with a blank body) the user's default EMBEDDING model — the one document RAG uses
        // (ADR 0007/0018). Mirrors /api/default-model exactly: a distinct path (a provider key is user-chosen,
        // so a literal segment under /api/providers would out-rank {key}); the id is NOT validated against the
        // catalog here (the resolver's read-time EffectiveEmbeddingDefault guard is the single validation
        // point — it only honours an id that names a declared embedding-capable model); the 200-char cap only
        // keeps unbounded text out of the persisted grain state. GET stays on /api/providers (EmbeddingDefault).
        app.MapPut("/api/default-embedding-model", async (DefaultEmbeddingModelRequest request, IGrainFactory grains, HttpContext http) =>
        {
            if (request.EmbeddingDefault is { Length: > 200 })
            {
                return Results.Text(
                    "Default embedding model id must be at most 200 characters.",
                    "text/plain",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await grains.GetGrain<IUserProviderConfigGrain>(http.GetUserId())
                .SetEmbeddingDefaultAsync(request.EmbeddingDefault);
            return Results.NoContent();
        });

        // Sets (or clears, with a blank body) the user's default SPEECH (text-to-speech) model — the one the
        // hands-free voice conversation synthesizes its reply from (ADR 0037/0038). Mirrors
        // /api/default-embedding-model exactly: a distinct path (a provider key is user-chosen, so a literal
        // segment under /api/providers would out-rank {key}); the id is NOT validated against the catalog here
        // (the resolver's read-time EffectiveSpeechDefault guard is the single validation point — it only
        // honours an id that names a declared speech-capable model); the 200-char cap only keeps unbounded text
        // out of the persisted grain state. GET stays on /api/providers (SpeechDefault).
        app.MapPut("/api/default-speech-model", async (DefaultSpeechModelRequest request, IGrainFactory grains, HttpContext http) =>
        {
            if (request.SpeechDefault is { Length: > 200 })
            {
                return Results.Text(
                    "Default speech model id must be at most 200 characters.",
                    "text/plain",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await grains.GetGrain<IUserProviderConfigGrain>(http.GetUserId())
                .SetSpeechDefaultAsync(request.SpeechDefault);
            return Results.NoContent();
        });
    }

    // A user provider must be self-contained (it does NOT inherit the operator AzureOpenAI:* config —
    // ADR 0018), so both kinds require an absolute http/https endpoint. localhost/private hosts are
    // ACCEPTED on purpose — an Ollama endpoint is a supported target, and this stays off the SSRF path
    // because the endpoint comes from the authenticated user's own config, not a model or a request
    // (ADR 0013). Deliberately no SSRF address-guard. Returns an error message, or null when valid.
    private static string? ValidateProvider(
        string key, string kind, string? endpoint, IReadOnlyList<ProviderModelRequest>? models)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains(':'))
        {
            return "Provider key must be non-empty and contain no ':' (it qualifies model ids as key:model).";
        }

        if (!Enum.TryParse<ProviderKind>(kind, ignoreCase: true, out _))
        {
            return $"Unknown provider kind '{kind}'. Use 'AzureOpenAI' or 'OpenAICompatible'.";
        }

        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "Endpoint must be an absolute http or https URL.";
        }

        // A provider must declare ≥1 model (ADR 0019). A user provider never ARM-discovers (it always
        // contributes only its declared Models), so a zero-model provider is keyed-but-modelless: it would
        // count toward providers.Count yet contribute no default, breaking "providers ⟺ a usable default".
        if (models is not { Count: > 0 })
        {
            return "A provider must declare at least one model.";
        }

        return null;
    }

    // Maps the request into the stored shape, encrypting the key. A blank ApiKey becomes a null
    // ciphertext: on create that is "no key", on update the grain reads it as "keep the stored key".
    private static StoredProvider ToStored(ProviderUpsertRequest request, IProviderSecretProtector protector) =>
        new(
            Key: request.Key.Trim(),
            Kind: request.Kind,
            DisplayName: request.DisplayName,
            Endpoint: request.Endpoint,
            ApiKeyCiphertext: string.IsNullOrWhiteSpace(request.ApiKey) ? null : protector.Protect(request.ApiKey),
            Models: [.. (request.Models ?? []).Select(m =>
                new StoredModel(m.Id, m.DisplayName, m.SupportsTools, m.SupportsVision,
                    m.SupportsImageGeneration, m.SupportsEmbeddings, m.SupportsSpeech,
                    m.SupportsReasoning))],
            UseResponsesApi: request.UseResponsesApi);

    // Never returns the key — only whether one is stored (hasKey). The ciphertext stays server-side.
    private static ProviderResponse Mask(StoredProvider p) =>
        new(
            p.Key, p.Kind, p.DisplayName, p.Endpoint,
            HasKey: !string.IsNullOrEmpty(p.ApiKeyCiphertext),
            Models: [.. p.Models.Select(m =>
                new ProviderModelResponse(m.Id, m.DisplayName, m.SupportsTools, m.SupportsVision,
                    m.SupportsImageGeneration, m.SupportsEmbeddings, m.SupportsSpeech,
                    m.SupportsReasoning))],
            p.UseResponsesApi);
}
