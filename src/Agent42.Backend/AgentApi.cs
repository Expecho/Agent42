using System.Text.Json;
using System.Text.Json.Serialization;
using Agent42.Agents;

namespace Agent42.Backend;

public static class AgentApi
{
    // NDJSON wire format for stream events: camelCase fields, camelCase enum values
    // (e.g. "textDelta", "toolCallStart"), and null fields omitted.
    private static readonly JsonSerializerOptions StreamEventJson = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // Uploads are capped so a single request can't be abused. Images become chat attachments
    // whose bytes live in blob storage, never in the serialized session (ADR 0004); documents are
    // ingested for retrieval and never stored as attachments (ADR 0007).
    private const long MaxAttachmentBytes = 10 * 1024 * 1024;

    public static IEndpointRouteBuilder MapAgentApi(this IEndpointRouteBuilder app)
    {
        // Streaming chat: sends a prompt to the conversation's agent grain and streams
        // the turn back as NDJSON events (text deltas, tool calls, token usage).
        app.MapPost("/api/chat/{conversationId}", async (string conversationId, ChatRequest request, IGrainFactory grains, IAttachmentStore attachments, HttpContext http) =>
        {
            var userId = http.GetUserId();
            var agent = grains.GetGrain<IAgentGrain>(AgentKey(userId, conversationId));

            // Resolve the ids the client sends back into refs, server-side, so a turn can only
            // attach images that actually exist under this user's conversation key (never a
            // client-supplied blob path). Unknown ids are skipped rather than failing the turn.
            IReadOnlyList<AttachmentRef>? attachmentRefs = null;
            if (request.AttachmentIds is { Count: > 0 })
            {
                var resolved = new List<AttachmentRef>();
                foreach (var id in request.AttachmentIds)
                {
                    var reference = await attachments.GetAsync(AgentKey(userId, conversationId), id);
                    if (reference is not null)
                    {
                        resolved.Add(reference);
                    }
                }

                attachmentRefs = resolved;
            }

            // Title the conversation from the message; an image-only turn has no text, so fall
            // back to the first attachment's filename (or a placeholder) rather than a blank title.
            var title = !string.IsNullOrWhiteSpace(request.Message)
                ? request.Message
                : attachmentRefs?.FirstOrDefault()?.FileName ?? "Image";
            await grains.GetGrain<IConversationIndexGrain>(userId).TouchAsync(conversationId, title);

            // The client sends AgentId only on the first turn of a freshly-started agent chat.
            // Snapshot that persona into the conversation before the turn runs (BindAgentAsync
            // is idempotent — a retried turn no-ops). A deleted agent resolves to null: skip
            // binding and let the grain's already-pinned persona stand, so a resumed turn never
            // errors (ADR 0003). Binding pins the model, so pass null to keep it for this turn.
            var model = request.Model;
            if (!string.IsNullOrEmpty(request.AgentId))
            {
                var definition = await grains.GetGrain<IAgentRegistryGrain>(userId).GetAsync(request.AgentId);
                if (definition is not null)
                {
                    await agent.BindAgentAsync(new AgentBinding(
                        definition.Id, definition.Name, definition.SystemPrompt, definition.Model));
                }

                model = null;
            }

            // A bad fix is dropped, not rejected: the browser is the only source of a position, and a
            // nonsense one (or a hand-rolled request body) must cost the user their location for this
            // turn — not their whole turn. The grain then simply reports the position as unknown.
            var location = request.Location is { IsValid: true } fix ? fix : null;

            http.Response.ContentType = "application/x-ndjson";
            await foreach (var evt in agent.ChatAsync(request.Message, model, attachmentRefs, location))
            {
                await http.Response.WriteAsync(JsonSerializer.Serialize(evt, StreamEventJson) + "\n");
                await http.Response.Body.FlushAsync();
            }
        });

        // The signed-in user's conversations, most recently used first.
        app.MapGet("/api/conversations", (IGrainFactory grains, HttpContext http) =>
            grains.GetGrain<IConversationIndexGrain>(http.GetUserId()).GetConversationsAsync());

        // Full visible history of one conversation, so the UI can resume it.
        app.MapGet("/api/conversations/{conversationId}/messages", (string conversationId, IGrainFactory grains, HttpContext http) =>
            grains.GetGrain<IAgentGrain>(AgentKey(http.GetUserId(), conversationId)).GetHistoryAsync());

        // Uploads a file for a conversation. Branches on media type: an image becomes a chat
        // attachment (bytes in blob storage, sent to the model as a UriContent SAS — ADR 0004) and
        // returns an id the client sends back on the next turn; a supported document type is
        // ingested for retrieval (extract → chunk → embed → index, ADR 0007) and is NOT registered
        // as an attachment — it returns a document-kind marker with no id, so the client never sends
        // it back as an attachmentId (a document must never enter the image UriContent path).
        // Rejects unsupported and oversize files. Antiforgery is disabled: the app is gated by Easy
        // Auth (ADR 0002), not cookies+forms.
        app.MapPost("/api/conversations/{conversationId}/attachments", async (string conversationId, IFormFile file, IAttachmentStore attachments, DocumentIngestionService ingestion, IGrainFactory grains, HttpContext http) =>
        {
            if (file is null || file.Length == 0)
            {
                // text/plain (not Results.BadRequest, which JSON-encodes the string) so the client
                // can read res.text() as a clean, unquoted message for its error chip.
                return Results.Text("No file was uploaded.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }

            if (file.Length > MaxAttachmentBytes)
            {
                return Results.Text($"Upload exceeds the {MaxAttachmentBytes / (1024 * 1024)} MB limit.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }

            // The browser's content type is unreliable for documents (Windows sends octet-stream or
            // nothing for .docx/.pptx/.xlsx/.md), so resolve it against the file extension before
            // branching — otherwise a real document is rejected as "unsupported" (ADR 0007).
            var contentType = DocumentMediaTypes.ResolveMediaType(file.FileName, file.ContentType);
            var ownerKey = AgentKey(http.GetUserId(), conversationId);

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            var bytes = buffer.ToArray();

            if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                var reference = await attachments.SaveAsync(ownerKey, file.FileName, contentType, bytes);
                // Return only what the browser needs (id + display metadata) — the internal blob
                // path stays server-side, addressed by id under the authenticated user's key.
                return Results.Json(new AttachmentMetadata(reference.Id, reference.MediaType, reference.FileName));
            }

            if (DocumentMediaTypes.IsSupported(contentType))
            {
                var chunkCount = await ingestion.IngestAsync(ownerKey, file.FileName, contentType, bytes);
                // Tell the grain the document exists (only when it yielded searchable text), so the
                // model is made aware of it and automatic retrieval is enabled for the conversation
                // (ADR 0008) — a document is otherwise invisible to the model (ADR 0007).
                if (chunkCount > 0)
                {
                    await grains.GetGrain<IAgentGrain>(ownerKey).RegisterDocumentAsync(file.FileName);
                }

                // No id: a document is retrieval-only, never sent back as an attachment (ADR 0007).
                return Results.Json(new DocumentUploadResult("document", file.FileName, contentType, chunkCount));
            }

            return Results.Text("Only image and document (PDF, Office, text) attachments are supported.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }).DisableAntiforgery();

        // Streams a stored attachment's bytes to the browser. First-party proxy (authenticated by
        // Easy Auth, user-scoped by grain key), so the page never holds a blob SAS (constraint 6).
        app.MapGet("/api/conversations/{conversationId}/attachments/{attachmentId}", async (string conversationId, string attachmentId, IAttachmentStore attachments, HttpContext http) =>
        {
            var content = await attachments.OpenReadAsync(AgentKey(http.GetUserId(), conversationId), attachmentId);
            return content is null
                ? Results.NotFound()
                : Results.File(content.Bytes, content.MediaType);
        });

        // The model this conversation is currently using, so the picker can be restored.
        // Wrapped in Results.Json so the bare string is sent as JSON ("gpt-4o"), not as a
        // text/plain body — the UI parses this response as JSON like every other endpoint.
        app.MapGet("/api/conversations/{conversationId}/model", async (string conversationId, IGrainFactory grains, HttpContext http) =>
            Results.Json(await grains.GetGrain<IAgentGrain>(AgentKey(http.GetUserId(), conversationId)).GetSelectedModelAsync()));

        // The persona bound to this conversation, or JSON null for a normal chat, so the UI
        // can lock the model picker and label an agent chat when it is reopened. Serialized
        // explicitly because Results.Json writes no body or content-type for a null value —
        // that would make the browser's response.json() throw (the text/plain gotcha). An
        // unbound conversation must return a proper JSON `null` with application/json.
        app.MapGet("/api/conversations/{conversationId}/agent", async (string conversationId, IGrainFactory grains, HttpContext http) =>
        {
            var binding = await grains.GetGrain<IAgentGrain>(AgentKey(http.GetUserId(), conversationId)).GetAgentBindingAsync();
            return Results.Text(JsonSerializer.Serialize(binding, JsonSerializerOptions.Web), "application/json");
        });

        // Permanently deletes a conversation: purges the agent grain (session + durable
        // reminders), then removes it from the user's conversation index.
        app.MapDelete("/api/conversations/{conversationId}", async (string conversationId, IGrainFactory grains, HttpContext http) =>
        {
            var userId = http.GetUserId();
            await grains.GetGrain<IAgentGrain>(AgentKey(userId, conversationId)).PurgeAsync();
            await grains.GetGrain<IConversationIndexGrain>(userId).RemoveAsync(conversationId);
            return Results.NoContent();
        });

        // The models available to the SIGNED-IN USER: their effective (baseline ⊕ overlay) provider set
        // (ADR 0018), so a provider they added shows up here without an app restart.
        app.MapGet("/api/models", async (IUserModelCatalogProvider catalogs, HttpContext http, CancellationToken ct) =>
            await (await catalogs.GetForUserAsync(http.GetUserId(), ct)).GetAsync(ct));

        // ————— stored agents (per-user personas) —————

        // The signed-in user's stored agents, most recently updated first.
        app.MapGet("/api/agents", (IGrainFactory grains, HttpContext http) =>
            grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).GetAllAsync());

        // Creates a new stored agent; the registry assigns its id and timestamps.
        app.MapPost("/api/agents", async (AgentUpsertRequest request, IGrainFactory grains, HttpContext http) =>
        {
            var created = await grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).SaveAsync(
                new AgentDefinition("", request.Name, request.Description, request.Model, request.SystemPrompt,
                    default, default, request.Continuous));
            return Results.Json(created);
        });

        // Updates a stored agent in place, preserving its creation time.
        app.MapPut("/api/agents/{id}", async (string id, AgentUpsertRequest request, IGrainFactory grains, HttpContext http) =>
        {
            var updated = await grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).SaveAsync(
                new AgentDefinition(id, request.Name, request.Description, request.Model, request.SystemPrompt,
                    default, default, request.Continuous));
            return Results.Json(updated);
        });

        // Deletes a stored agent. Existing chats started from it keep their snapshotted
        // persona (ADR 0003), so this never disturbs a conversation.
        app.MapDelete("/api/agents/{id}", async (string id, IGrainFactory grains, HttpContext http) =>
        {
            await grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).DeleteAsync(id);
            return Results.NoContent();
        });

        // ————— user profile (per-user preferences) —————

        // The signed-in user's profile. Wrapped in Results.Json so the returned object is
        // served as application/json (the browser calls response.json()); the grain never
        // returns null — it yields the empty default when the profile was never saved.
        app.MapGet("/api/profile", async (IGrainFactory grains, HttpContext http) =>
            Results.Json(await grains.GetGrain<IUserProfileGrain>(http.GetUserId()).GetAsync()));

        // Saves the signed-in user's profile and returns the stored value (as JSON).
        app.MapPut("/api/profile", async (ProfileUpdateRequest request, IGrainFactory grains, HttpContext http) =>
        {
            var saved = await grains.GetGrain<IUserProfileGrain>(http.GetUserId())
                .SaveAsync(new UserProfile(request.PreferredLanguage));
            return Results.Json(saved);
        });

        // ————— model providers (per-user overlay, ADR 0018) —————

        // The signed-in user's OWN configured providers (their overlay — not the operator baseline).
        // Keys are MASKED to a hasKey flag: the ciphertext never leaves the server, and the plaintext
        // never existed here.
        app.MapGet("/api/providers", async (IGrainFactory grains, HttpContext http) =>
        {
            var overlay = await grains.GetGrain<IUserProviderConfigGrain>(http.GetUserId()).GetAsync();
            return Results.Json(new ProvidersResponse(
                [.. overlay.Providers.Select(Mask)], overlay.Default));
        });

        // Creates or updates a provider by key. The key is user-chosen and identifies the provider, so
        // POST upserts (like the grain's SaveAsync). The API ENCRYPTS the key here — the grain only ever
        // stores ciphertext (ADR 0018). A blank ApiKey on a new provider means "no key" (a supported,
        // skipped state for an OpenAI-compatible provider).
        app.MapPost("/api/providers", async (ProviderUpsertRequest request, IProviderSecretProtector protector, IGrainFactory grains, HttpContext http) =>
        {
            var error = ValidateProvider(request.Key, request.Kind, request.Endpoint);
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
            var error = ValidateProvider(key, request.Kind, request.Endpoint);
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

        return app;
    }

    // A user provider must be self-contained (it does NOT inherit the operator AzureOpenAI:* config —
    // ADR 0018), so both kinds require an absolute http/https endpoint. localhost/private hosts are
    // ACCEPTED on purpose — an Ollama endpoint is a supported target, and this stays off the SSRF path
    // because the endpoint comes from the authenticated user's own config, not a model or a request
    // (ADR 0013). Deliberately NO PrivateAddressGuard. Returns an error message, or null when valid.
    private static string? ValidateProvider(string key, string kind, string? endpoint)
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
                new StoredModel(m.Id, m.DisplayName, m.SupportsTools, m.SupportsVision))],
            UseResponsesApi: request.UseResponsesApi);

    // Never returns the key — only whether one is stored (hasKey). The ciphertext stays server-side.
    private static ProviderResponse Mask(StoredProvider p) =>
        new(
            p.Key, p.Kind, p.DisplayName, p.Endpoint,
            HasKey: !string.IsNullOrEmpty(p.ApiKeyCiphertext),
            Models: [.. p.Models.Select(m =>
                new ProviderModelResponse(m.Id, m.DisplayName, m.SupportsTools, m.SupportsVision))],
            p.UseResponsesApi);

    // Agent grains are keyed per user, so a conversation id sent by one user can
    // never resolve to another user's chat.
    private static string AgentKey(string userId, string conversationId)
        => $"{userId}/{conversationId}";
}

// AgentId is the stored agent to bind on the first turn of an agent chat; AttachmentIds are
// the ids returned by the upload endpoint for images to send with this turn; Location is the
// browser's current position, when the user has authorized sharing it (ADR 0012) — it rides the
// chat request because a server-side tool cannot ask the browser for a fix mid-turn. All are
// trailing defaults so existing positional callers (new ChatRequest("hi", null)) keep compiling.
public record ChatRequest(
    string Message, string? Model, string? AgentId = null, IReadOnlyList<string>? AttachmentIds = null,
    UserLocation? Location = null);

// What the upload endpoint returns for an image: the id to send back on the next turn plus the
// metadata needed to render a preview. The internal blob path is deliberately omitted.
public record AttachmentMetadata(string Id, string MediaType, string FileName);

// What the upload endpoint returns for a document: a kind marker (so the client renders a chip and
// never sends it back as an attachmentId — documents are retrieval-only, ADR 0007), the filename,
// and how many chunks were indexed (0 = no extractable text, e.g. a scanned PDF).
public record DocumentUploadResult(string Kind, string FileName, string MediaType, int ChunkCount);

// Client payload for creating (POST) or updating (PUT) a stored agent. The id and
// timestamps are owned by the registry grain, so they aren't part of the request.
// Continuous is the history-mode flag (ADR 0016); a trailing default so an old-shape
// client that omits it stores false = per-chat.
public record AgentUpsertRequest(string Name, string Description, string Model, string SystemPrompt,
    bool Continuous = false);

// Client payload for PUT /api/profile: the preferred-language BCP-47 tag the user picked
// ("" = use the device default). More preference fields will be added here as the profile grows.
public record ProfileUpdateRequest(string PreferredLanguage);

// Client payload for creating/updating a user provider (ADR 0018). ApiKey is WRITE-ONLY — it is
// encrypted server-side and never echoed back; blank on an update means "keep the stored key". Kind is
// "AzureOpenAI" or "OpenAICompatible". Models/UseResponsesApi are trailing defaults so a minimal body works.
public record ProviderUpsertRequest(
    string Key, string Kind, string? DisplayName, string? Endpoint, string? ApiKey,
    IReadOnlyList<ProviderModelRequest>? Models = null, bool UseResponsesApi = false);

public record ProviderModelRequest(string Id, string? DisplayName, bool SupportsTools, bool SupportsVision);

// What GET/POST/PUT return for a provider: everything EXCEPT the key, which is masked to HasKey so the
// ciphertext (and the plaintext) never reach the client (ADR 0018).
public record ProviderResponse(
    string Key, string Kind, string? DisplayName, string? Endpoint, bool HasKey,
    IReadOnlyList<ProviderModelResponse> Models, bool UseResponsesApi);

public record ProviderModelResponse(string Id, string? DisplayName, bool SupportsTools, bool SupportsVision);

// The GET /api/providers payload: the user's overlay providers (masked) plus their optional default.
public record ProvidersResponse(IReadOnlyList<ProviderResponse> Providers, string? Default);
