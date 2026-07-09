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

            http.Response.ContentType = "application/x-ndjson";
            await foreach (var evt in agent.ChatAsync(request.Message, model, attachmentRefs))
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
        app.MapPost("/api/conversations/{conversationId}/attachments", async (string conversationId, IFormFile file, IAttachmentStore attachments, DocumentIngestionService ingestion, HttpContext http) =>
        {
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest("No file was uploaded.");
            }

            if (file.Length > MaxAttachmentBytes)
            {
                return Results.BadRequest($"Upload exceeds the {MaxAttachmentBytes / (1024 * 1024)} MB limit.");
            }

            var contentType = file.ContentType ?? "";
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
                // No id: a document is retrieval-only, never sent back as an attachment (ADR 0007).
                return Results.Json(new DocumentUploadResult("document", file.FileName, contentType, chunkCount));
            }

            return Results.BadRequest("Only image and document (PDF, Office, text) attachments are supported.");
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

        // Live list of the model deployments available in the configured AI Foundry account.
        app.MapGet("/api/models", (IModelCatalog catalog, CancellationToken ct) => catalog.GetAsync(ct));

        // ————— stored agents (per-user personas) —————

        // The signed-in user's stored agents, most recently updated first.
        app.MapGet("/api/agents", (IGrainFactory grains, HttpContext http) =>
            grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).GetAllAsync());

        // Creates a new stored agent; the registry assigns its id and timestamps.
        app.MapPost("/api/agents", async (AgentUpsertRequest request, IGrainFactory grains, HttpContext http) =>
        {
            var created = await grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).SaveAsync(
                new AgentDefinition("", request.Name, request.Description, request.Model, request.SystemPrompt,
                    default, default));
            return Results.Json(created);
        });

        // Updates a stored agent in place, preserving its creation time.
        app.MapPut("/api/agents/{id}", async (string id, AgentUpsertRequest request, IGrainFactory grains, HttpContext http) =>
        {
            var updated = await grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).SaveAsync(
                new AgentDefinition(id, request.Name, request.Description, request.Model, request.SystemPrompt,
                    default, default));
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

        return app;
    }

    // Agent grains are keyed per user, so a conversation id sent by one user can
    // never resolve to another user's chat.
    private static string AgentKey(string userId, string conversationId)
        => $"{userId}/{conversationId}";
}

// AgentId is the stored agent to bind on the first turn of an agent chat; AttachmentIds are
// the ids returned by the upload endpoint for images to send with this turn. Both are trailing
// defaults so existing positional callers (new ChatRequest("hi", null)) keep compiling.
public record ChatRequest(
    string Message, string? Model, string? AgentId = null, IReadOnlyList<string>? AttachmentIds = null);

// What the upload endpoint returns for an image: the id to send back on the next turn plus the
// metadata needed to render a preview. The internal blob path is deliberately omitted.
public record AttachmentMetadata(string Id, string MediaType, string FileName);

// What the upload endpoint returns for a document: a kind marker (so the client renders a chip and
// never sends it back as an attachmentId — documents are retrieval-only, ADR 0007), the filename,
// and how many chunks were indexed (0 = no extractable text, e.g. a scanned PDF).
public record DocumentUploadResult(string Kind, string FileName, string MediaType, int ChunkCount);

// Client payload for creating (POST) or updating (PUT) a stored agent. The id and
// timestamps are owned by the registry grain, so they aren't part of the request.
public record AgentUpsertRequest(string Name, string Description, string Model, string SystemPrompt);

// Client payload for PUT /api/profile: the preferred-language BCP-47 tag the user picked
// ("" = use the device default). More preference fields will be added here as the profile grows.
public record ProfileUpdateRequest(string PreferredLanguage);
