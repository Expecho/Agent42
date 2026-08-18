using System.Text.Json;
using System.Text.Json.Serialization;
using Agent42.Agents;
using Microsoft.AspNetCore.WebUtilities;

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

            // The client sends AgentId only on the first turn of a freshly-started agent chat.
            // Resolve the persona up front (before the index touch) so a new conversation can be
            // recorded under the agent — the sidebar groups agent chats by this identity. A deleted
            // agent resolves to null: no snapshot to record, and the already-pinned persona stands.
            var definition = !string.IsNullOrEmpty(request.AgentId)
                ? await grains.GetGrain<IAgentRegistryGrain>(userId).GetAsync(request.AgentId)
                : null;

            // Title the conversation from the message; an image-only turn has no text, so fall
            // back to the first attachment's filename (or a placeholder) rather than a blank title.
            // An ephemeral (incognito) chat is never touched into the index, so it never appears in the
            // sidebar and — paired with the grain skipping its session write — leaves no trace (ADR 0034).
            if (!request.Ephemeral)
            {
                var title = !string.IsNullOrWhiteSpace(request.Message)
                    ? request.Message
                    : attachmentRefs?.FirstOrDefault()?.FileName ?? "Image";
                await grains.GetGrain<IConversationIndexGrain>(userId)
                    .TouchAsync(conversationId, title, definition?.Id, definition?.Name);
            }

            // Snapshot the persona into the conversation before the turn runs (BindAgentAsync
            // is idempotent — a retried turn no-ops). A deleted agent skips binding and lets the
            // grain's already-pinned persona stand, so a resumed turn never errors (ADR 0003).
            // Binding pins the model, so pass null to keep it for this turn — and do so for ANY
            // agent turn (even a just-deleted one) to preserve the original per-turn model source.
            var model = request.Model;
            if (!string.IsNullOrEmpty(request.AgentId))
            {
                if (definition is not null)
                {
                    await agent.BindAgentAsync(new AgentBinding(
                        definition.Id, definition.Name, definition.SystemPrompt, definition.Model,
                        definition.EnabledTools));
                }

                model = null;
            }

            // A bad fix is dropped, not rejected: the browser is the only source of a position, and a
            // nonsense one (or a hand-rolled request body) must cost the user their location for this
            // turn — not their whole turn. The grain then simply reports the position as unknown.
            var location = request.Location is { IsValid: true } fix ? fix : null;

            http.Response.ContentType = "application/x-ndjson";
            await foreach (var evt in agent.ChatAsync(request.Message, model, attachmentRefs, location, request.Ephemeral))
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
                int chunkCount;
                try
                {
                    chunkCount = await ingestion.IngestAsync(ownerKey, file.FileName, contentType, bytes);
                }
                catch (Exception ex) when (ex is EmbeddingModelNotConfiguredException or EmbeddingDimensionMismatchException)
                {
                    // No embedding model, or one whose vectors don't fit the store (ADR 0007). The composer
                    // gates document attachment client-side (Step 9); this is defense-in-depth for the API.
                    return Results.Text(ex.Message, "text/plain", statusCode: StatusCodes.Status400BadRequest);
                }

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
            // Revoke any read-only share link too, so a deleted conversation can't be resurrected through a
            // still-live public link (ADR 0035). No-op when the conversation was never shared.
            await grains.GetGrain<IUserShareLinksGrain>(userId).RemoveAsync(conversationId);
            return Results.NoContent();
        });

        // ————— read-only share links (ADR 0035) —————

        // The share link for this conversation, or { url: null } when it is not shared. Behind Easy Auth,
        // user-scoped by GetUserId — only the owner ever sees or manages their own links.
        app.MapGet("/api/conversations/{conversationId}/share", async (string conversationId, IGrainFactory grains, HttpContext http) =>
        {
            var link = await grains.GetGrain<IUserShareLinksGrain>(http.GetUserId()).GetAsync(conversationId);
            return Results.Json(new ShareLinkResponse(link is null ? null : ShareUrl(http, link.Token)));
        });

        // Mints (or returns the existing) read-only public link for this conversation and registers the global
        // directory so an unauthenticated recipient can resolve it. Idempotent — one stable, re-copyable link
        // per conversation. Scoped to the caller's own key, so a user can only ever share their own chat.
        app.MapPost("/api/conversations/{conversationId}/share", async (string conversationId, IGrainFactory grains, HttpContext http) =>
        {
            var link = await grains.GetGrain<IUserShareLinksGrain>(http.GetUserId()).CreateOrGetAsync(conversationId);
            return Results.Json(new ShareLinkResponse(ShareUrl(http, link.Token)));
        });

        // Revokes the link (deregisters the global directory), so the public URL stops resolving. No-op if the
        // conversation was never shared.
        app.MapDelete("/api/conversations/{conversationId}/share", async (string conversationId, IGrainFactory grains, HttpContext http) =>
        {
            await grains.GetGrain<IUserShareLinksGrain>(http.GetUserId()).RemoveAsync(conversationId);
            return Results.NoContent();
        });

        // The models available to the SIGNED-IN USER: their effective (baseline ⊕ overlay) provider set
        // (ADR 0018), so a provider they added shows up here without an app restart.
        app.MapGet("/api/models", async (IUserModelCatalogProvider catalogs, HttpContext http, CancellationToken ct) =>
            await (await catalogs.GetForUserAsync(http.GetUserId(), ct)).GetAsync(ct));

        // The selectable-tools catalog (ADR 0021): the curated keys the UI renders as checkbox groups in
        // Preferences, the agent editor and the composer popover. The static keys are the single source of
        // truth in ToolCatalog; per-user MCP servers (ADR 0026) are appended as one `mcp:{id}` descriptor per
        // MANAGEMENT-ENABLED server (a disabled server contributes no tools, so it gets no checkbox). Capability
        // gating still happens server-side in BuildAgent.
        app.MapGet("/api/tools", async (IGrainFactory grains, HttpContext http) =>
        {
            var overlay = await grains.GetGrain<IUserMcpConfigGrain>(http.GetUserId()).GetAsync();
            var mcpDescriptors = overlay.Servers
                .Where(s => s.Enabled)
                .Select(s => new ToolDescriptor(ToolCatalog.McpServerKey(s.Id), s.Name, RequiresHostedTools: false));
            return Results.Json<IReadOnlyList<ToolDescriptor>>([.. ToolCatalog.Descriptors, .. mcpDescriptors]);
        });

        // The EFFECTIVE tools for one conversation (override ?? user-default ?? all), so the composer popover
        // can restore its checkboxes when the conversation is reopened.
        app.MapGet("/api/conversations/{conversationId}/tools", async (string conversationId, IGrainFactory grains, HttpContext http) =>
            Results.Json(await grains.GetGrain<IAgentGrain>(AgentKey(http.GetUserId(), conversationId)).GetEnabledToolsAsync()));

        // Sets the per-conversation tool override (ADR 0021) and returns the resulting effective set. The
        // grain sanitizes unknown keys; the UI represents "all" as the full explicit list (never null), so a
        // PUT is always an explicit selection. Scoped to non-agent chats in the UI — an agent chat's tools
        // come from the bind snapshot, which would clobber a pre-first-turn override.
        app.MapPut("/api/conversations/{conversationId}/tools", async (string conversationId, ToolSelectionRequest request, IGrainFactory grains, HttpContext http) =>
        {
            var agent = grains.GetGrain<IAgentGrain>(AgentKey(http.GetUserId(), conversationId));
            await agent.SetEnabledToolsAsync(request.Tools);
            return Results.Json(await agent.GetEnabledToolsAsync());
        });

        // ————— stored agents (per-user personas) —————

        // The signed-in user's stored agents, most recently updated first.
        app.MapGet("/api/agents", (IGrainFactory grains, HttpContext http) =>
            grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).GetAllAsync());

        // Creates a new stored agent; the registry assigns its id and timestamps, and mints a per-agent
        // API key (ADR 0022) returned ONCE in the response body (only its hash is stored).
        app.MapPost("/api/agents", async (AgentUpsertRequest request, IGrainFactory grains, HttpContext http) =>
        {
            var result = await grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).SaveAsync(
                new AgentDefinition("", request.Name, request.Description, request.Model, request.SystemPrompt,
                    default, default, request.Continuous, request.EnabledTools));
            return Results.Json(new AgentSaveResponse(result.Agent, result.PlaintextApiKey));
        });

        // Updates a stored agent in place, preserving its creation time. A regenerateKey request rotates
        // the agent's API key (ADR 0022): the new raw key rides back ONCE, the old one stops working.
        app.MapPut("/api/agents/{id}", async (string id, AgentUpsertRequest request, IGrainFactory grains, HttpContext http) =>
        {
            var result = await grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).SaveAsync(
                new AgentDefinition(id, request.Name, request.Description, request.Model, request.SystemPrompt,
                    default, default, request.Continuous, request.EnabledTools),
                request.RegenerateKey);
            return Results.Json(new AgentSaveResponse(result.Agent, result.PlaintextApiKey));
        });

        // Deletes a stored agent (and deregisters its key). Existing chats started from it keep their
        // snapshotted persona (ADR 0003), so this never disturbs a conversation.
        app.MapDelete("/api/agents/{id}", async (string id, IGrainFactory grains, HttpContext http) =>
        {
            await grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).DeleteAsync(id);
            return Results.NoContent();
        });

        // External async run (ADR 0022): a per-agent API key (X-Api-Key header) invokes an agent WITHOUT a
        // signed-in session. Multipart (a 'prompt' field + optional 'file' parts) so external automation can
        // hand the agent an image or a document in the same call — the caller has no session and no
        // conversation id for the browser's two-phase upload. Easy Auth EXCLUDES this path
        // (container-app.bicep globalValidation.excludedPaths), so it self-enforces via the key and fails
        // closed (401). Responds 202 and runs the turn in the BACKGROUND (OneWay) into a new listed
        // conversation the owner opens in the UI later. The userId comes from the key resolution, NEVER from
        // GetUserId — on an excluded path there is no Easy Auth principal, so GetUserId would return local-dev.
        app.MapPost("/api/agent-run", async (
            HttpContext http, IGrainFactory grains, IAttachmentStore attachments, DocumentIngestionService ingestion) =>
        {
            var key = http.Request.Headers["X-Api-Key"].ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                return Results.Text("Missing X-Api-Key.", "text/plain", statusCode: StatusCodes.Status401Unauthorized);
            }

            // Resolve the key to its owner via the global directory (an O(1) grain get). Fail closed on null.
            var owner = await grains.GetGrain<IAgentKeyDirectoryGrain>(ApiKey.Hash(key)).ResolveAsync();
            if (owner is null)
            {
                return Results.Text("Unknown API key.", "text/plain", statusCode: StatusCodes.Status401Unauthorized);
            }

            // Re-check the agent still exists (deletion race). 401 not 404 — never disclose agent existence to
            // an unauthenticated caller, keep the auth status uniform, and this backstops the delete ordering
            // (the directory entry clears AFTER the registry write, so a mid-failure orphan still 401s here).
            var definition = await grains.GetGrain<IAgentRegistryGrain>(owner.UserId).GetAsync(owner.AgentId);
            if (definition is null)
            {
                return Results.Text("Unknown API key.", "text/plain", statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!http.Request.HasFormContentType)
            {
                return Results.Text(
                    "Send multipart/form-data with a 'prompt' field and optional 'file' parts.",
                    "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }

            var form = await http.Request.ReadFormAsync();
            var prompt = form["prompt"].ToString();
            var files = form.Files;

            // Validate EVERY file before minting a conversation (constraint 13): a batch with any oversize or
            // unsupported file is rejected WHOLE (400), so automation retries cleanly instead of getting a
            // half-processed run. Media type is resolved by extension (the browser lies about .docx — ADR 0007).
            foreach (var file in files)
            {
                if (file.Length > MaxAttachmentBytes)
                {
                    return Results.Text(
                        $"'{file.FileName}' exceeds the {MaxAttachmentBytes / (1024 * 1024)} MB limit.",
                        "text/plain", statusCode: StatusCodes.Status400BadRequest);
                }

                var mediaType = DocumentMediaTypes.ResolveMediaType(file.FileName, file.ContentType);
                if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    && !DocumentMediaTypes.IsSupported(mediaType))
                {
                    return Results.Text(
                        $"'{file.FileName}' is not a supported image or document type.",
                        "text/plain", statusCode: StatusCodes.Status400BadRequest);
                }
            }

            if (string.IsNullOrWhiteSpace(prompt) && files.Count == 0)
            {
                return Results.Text(
                    "Provide a 'prompt' and/or at least one file.",
                    "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }

            var conversationId = Guid.NewGuid().ToString();
            var ownerKey = AgentKey(owner.UserId, conversationId);

            // Store/ingest each file under the resolved owner+conversation key — the same per-file branch as
            // the browser upload endpoint. Images become attachment refs that ride the run; documents ingest
            // for retrieval (and are registered on the grain below, never sent as attachments — ADR 0007).
            var imageRefs = new List<AttachmentRef>();
            var documentNames = new List<string>();
            foreach (var file in files)
            {
                var mediaType = DocumentMediaTypes.ResolveMediaType(file.FileName, file.ContentType);
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer);
                var bytes = buffer.ToArray();

                if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    imageRefs.Add(await attachments.SaveAsync(ownerKey, file.FileName, mediaType, bytes));
                }
                else
                {
                    int chunkCount;
                    try
                    {
                        chunkCount = await ingestion.IngestAsync(ownerKey, file.FileName, mediaType, bytes);
                    }
                    catch (Exception ex) when (ex is EmbeddingModelNotConfiguredException or EmbeddingDimensionMismatchException)
                    {
                        // A bad document rejects the WHOLE batch (400) — the same all-or-nothing rule as an
                        // unsupported file. The key holder gets a clear message; no partial run is enqueued.
                        return Results.Text(ex.Message, "text/plain", statusCode: StatusCodes.Status400BadRequest);
                    }

                    if (chunkCount > 0)
                    {
                        documentNames.Add(file.FileName);
                    }
                }
            }

            // Title from the prompt, else the first file name, else a placeholder (an attachment-only run
            // has no text). Listing the conversation is what lets the owner find and open the result later.
            var title = !string.IsNullOrWhiteSpace(prompt)
                ? prompt
                : files.FirstOrDefault()?.FileName ?? "Agent run";
            await grains.GetGrain<IConversationIndexGrain>(owner.UserId)
                .TouchAsync(conversationId, title, definition.Id, definition.Name);

            var agentGrain = grains.GetGrain<IAgentGrain>(ownerKey);
            // Snapshot the agent's persona into the conversation (ADR 0003): pins its model + tools for the run.
            await agentGrain.BindAgentAsync(new AgentBinding(
                definition.Id, definition.Name, definition.SystemPrompt, definition.Model, definition.EnabledTools));

            // Enable retrieval for any ingested document BEFORE the run, so the turn can see it (ADR 0008).
            foreach (var name in documentNames)
            {
                await agentGrain.RegisterDocumentAsync(name);
            }

            // Fire the background run (OneWay — returns immediately) and answer 202 with the conversation id.
            await agentGrain.EnqueuePromptAsync(prompt, imageRefs.Count > 0 ? imageRefs : null);

            return Results.Accepted($"/api/conversations/{conversationId}/messages", new { conversationId });
        }).DisableAntiforgery();

        // ————— public read-only share view (ADR 0035) —————
        //
        // Easy Auth EXCLUDES the /api/share/* paths (container-app.bicep globalValidation.excludedPaths), so an
        // unauthenticated recipient can open a shared conversation. Like /api/agent-run these SELF-ENFORCE: the
        // token is the only credential, the owner + conversation come from the DIRECTORY resolution (NEVER
        // GetUserId — there is no Easy Auth principal on an excluded path, so it would be local-dev), and both
        // fail CLOSED. A browser-visitable link fails to 404 ("not found or revoked"), not 401.

        // The shared conversation's title, optional agent name, and full CURRENT history (a live read-only view:
        // future turns appear until the owner revokes). Reuses the ChatTurn/AttachmentRef DTOs the authenticated
        // /messages route returns (AttachmentRef.BlobPath stays [JsonIgnore] — the page only ever gets ids).
        app.MapGet("/api/share/{token}", async (string token, IGrainFactory grains) =>
        {
            var target = await grains.GetGrain<IShareLinkDirectoryGrain>(ShareToken.Hash(token)).ResolveAsync();
            if (target is null)
            {
                return Results.NotFound();
            }

            var turns = await grains.GetGrain<IAgentGrain>(AgentKey(target.UserId, target.ConversationId)).GetHistoryAsync();
            var info = (await grains.GetGrain<IConversationIndexGrain>(target.UserId).GetConversationsAsync())
                .FirstOrDefault(c => c.Id == target.ConversationId);
            return Results.Json(new SharedConversationResponse(info?.Title ?? "Shared conversation", info?.AgentName, turns));
        });

        // Streams a shared attachment's bytes to an unauthenticated viewer. Token-scoped analogue of the
        // user-scoped /api/conversations/{id}/attachments/{id} proxy: the owner + conversation come from the
        // directory (never GetUserId), so the page never holds a blob SAS and can only read blobs under the
        // shared conversation's own key. Fails closed → 404.
        app.MapGet("/api/share/{token}/attachments/{attachmentId}", async (string token, string attachmentId, IAttachmentStore attachments, IGrainFactory grains) =>
        {
            var target = await grains.GetGrain<IShareLinkDirectoryGrain>(ShareToken.Hash(token)).ResolveAsync();
            if (target is null)
            {
                return Results.NotFound();
            }

            var content = await attachments.OpenReadAsync(AgentKey(target.UserId, target.ConversationId), attachmentId);
            return content is null ? Results.NotFound() : Results.File(content.Bytes, content.MediaType);
        });

        // ————— user profile (per-user preferences) —————

        // The signed-in user's profile. Wrapped in Results.Json so the returned object is
        // served as application/json (the browser calls response.json()); the grain never
        // returns null — it yields the empty default when the profile was never saved.
        app.MapGet("/api/profile", async (IGrainFactory grains, HttpContext http) =>
            Results.Json(await grains.GetGrain<IUserProfileGrain>(http.GetUserId()).GetAsync()));

        // Saves the signed-in user's profile and returns the stored value (as JSON). READ-MODIFY-WRITE with
        // absent-means-keep (ADR 0021 + ADR 0005): the profile record holds several independent preferences
        // now (language, Theme, EnabledTools), and each has its own endpoint/dialog. A null field on the
        // request means "leave it as stored", so a language-only save never wipes the theme or the tool
        // default and vice-versa — the UI never sends null to mean "reset" (a stale client with old cached
        // app.js in a second tab that PUTs a language-only save must not reset the saved theme). Tool keys
        // are sanitized server-side.
        app.MapPut("/api/profile", async (ProfileUpdateRequest request, IGrainFactory grains, HttpContext http) =>
        {
            var grain = grains.GetGrain<IUserProfileGrain>(http.GetUserId());
            var current = await grain.GetAsync();
            var saved = await grain.SaveAsync(current with
            {
                PreferredLanguage = request.PreferredLanguage ?? current.PreferredLanguage,
                Theme = request.Theme ?? current.Theme,
                EnabledTools = request.EnabledTools is null ? current.EnabledTools : ToolCatalog.Sanitize(request.EnabledTools),
                SuggestFollowUps = request.SuggestFollowUps ?? current.SuggestFollowUps,
            });
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

        // Synthesizes speech audio for the given text via the user's default speech model (ADR 0038), so the
        // voice conversation (ADR 0037) can speak the reply with a server-side neural voice instead of the
        // browser's speechSynthesis. Resolves the per-user factory (like the grain does) and returns the audio
        // bytes; 409 when no speech model is configured, so the browser falls back. Conversation-independent —
        // it only synthesizes text, so it takes no conversationId. DisableAntiforgery: the app is Easy-Auth
        // gated, not cookie/forms (same as the attachment upload).
        // IServiceProvider (NOT IUserChatClientFactoryProvider) is injected deliberately, mirroring
        // /api/config-health: MapAgentApi infers every endpoint's params up front, and a param a host does not
        // register would be treated as a body and break routing for the WHOLE host — some test hosts don't
        // register the factory provider. Resolving it optionally 409s cleanly where it is absent instead.
        app.MapPost("/api/speech", async (SpeechRequest request, IServiceProvider services, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.Text("No text to synthesize.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }

            var factories = services.GetService<IUserChatClientFactoryProvider>();
            var speech = factories is null ? null : (await factories.GetForUserAsync(http.GetUserId(), ct)).GetSpeechService();
            if (speech is null)
            {
                return Results.Text("No speech model configured.", "text/plain", statusCode: StatusCodes.Status409Conflict);
            }

            var audio = await speech.SynthesizeAsync(request.Text, ct);
            return Results.File(audio.Bytes, audio.MediaType);
        }).DisableAntiforgery();

        // ————— Web Push subscriptions (ADR 0028) —————

        // Whether push is enabled (a VAPID private key is configured on the server) and the non-secret
        // PUBLIC key + subject the browser needs to subscribe. Behind Easy Auth like every other endpoint,
        // but returns nothing sensitive — the private key never leaves the server. When disabled the UI
        // hides the toggle (the optional-secret shape: no key → app runs, feature simply off).
        app.MapGet("/api/push/config", (PushConfig config) =>
            Results.Json(new PushConfigResponse(config.Enabled, config.PublicKey)));

        // ————— configuration health (Preferences → Configuration) —————

        // A read-only diagnostic of which config inputs are present/malformed/(opt-in) unreachable — so a
        // silently degraded feature (geocoding off, push off, context.dev failing) is visible. Behind Easy
        // Auth like every /api/* read; all allowlisted users may view (presence booleans are low-sensitivity,
        // no role system). NEVER leaks a secret — ConfigHealth exposes a status + curated message only.
        //
        // Static by default; ?probe=true also runs the opt-in LIVE probes (a geocoding/context.dev probe
        // costs a billable call, so the static call must run ZERO probes). Injects IServiceProvider (probe
        // deps are resolved OPTIONALLY inside ProbeAsync) plus the both-hosts-registered IConfiguration /
        // IHostEnvironment / PushConfig — deliberately NOT IHttpClientFactory as a parameter: it is not
        // registered in DevHost, and a minimal-API param it can't resolve breaks routing for the whole host.
        app.MapGet("/api/config-health", async (
            bool? probe, IConfiguration configuration, IHostEnvironment environment, PushConfig push,
            IServiceProvider services, CancellationToken cancellationToken) =>
        {
            var report = ConfigHealth.BuildStatic(configuration, environment, push);
            if (probe == true)
            {
                report = await ConfigHealth.ProbeAsync(report, services, configuration, cancellationToken);
            }

            return Results.Json(report);
        });

        // ————— usage dashboard (token telemetry, ADR 0031) —————

        // Global, cross-user token/tool usage aggregated from OpenTelemetry gen_ai spans. Behind Easy
        // Auth like every /api/* read; deliberately GLOBAL (no GetUserId scoping) so any signed-in user
        // sees aggregate spend, matching the reference dashboard — an accepted cross-user exposure of
        // other users' email/usage to the ~2 allowlisted users (no role system). range ∈ 24h|7d|30d|all
        // (unknown → 400; omitted → 30d default; 24h reads hourly, else daily). application/user accept
        // 'all' or omitted = no filter. Injects only IGrainFactory (both-hosts-resolvable — never the
        // Backend-only ingestion channel, which would break DevHost route param inference).
        app.MapGet("/api/usage", async (string? application, string? user, string? range, IGrainFactory grains) =>
        {
            var normalizedRange = string.IsNullOrWhiteSpace(range) ? "30d" : range.ToLowerInvariant();
            if (normalizedRange is not ("24h" or "7d" or "30d" or "all"))
            {
                return Results.Text(
                    $"unknown range '{range}' (expected 24h|7d|30d|all)", "text/plain",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var report = await grains.GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey)
                .QueryAsync(normalizedRange, NormalizeFilter(application), NormalizeFilter(user));
            return Results.Json(report);
        });

        // Distinct application/user values for the filter dropdowns (kept separate so a dropdown doesn't
        // reset when a filter narrows the main payload).
        app.MapGet("/api/usage/applications", async (IGrainFactory grains) =>
            Results.Json(await grains.GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey).ListApplicationsAsync()));

        app.MapGet("/api/usage/users", async (IGrainFactory grains) =>
            Results.Json(await grains.GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey).ListUsersAsync()));

        // Registers (upserts by endpoint) the caller's push subscription for THIS device. The subscription
        // is a device push capability, stored in cleartext in the per-user grain (ADR 0028).
        app.MapPost("/api/push/subscriptions", async (PushSubscriptionRequest request, IGrainFactory grains, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(request.Endpoint)
                || string.IsNullOrWhiteSpace(request.P256dh)
                || string.IsNullOrWhiteSpace(request.Auth))
            {
                return Results.Text(
                    "A push subscription requires endpoint, p256dh and auth.",
                    "text/plain",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await grains.GetGrain<IUserPushGrain>(http.GetUserId())
                .AddAsync(new PushSubscription(request.Endpoint, request.P256dh, request.Auth));
            return Results.NoContent();
        });

        // Removes the caller's subscription for this device (unsubscribe) — endpoint via query. The sender
        // also prunes an endpoint the push service reports gone (404/410), so this is the explicit-opt-out
        // half of the same operation.
        app.MapDelete("/api/push/subscriptions", async (string? endpoint, IGrainFactory grains, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return Results.Text(
                    "endpoint is required.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }

            await grains.GetGrain<IUserPushGrain>(http.GetUserId()).RemoveAsync(endpoint);
            return Results.NoContent();
        });

        // ————— remote MCP servers (per-user overlay, config-driven remote MCP servers) —————

        // The signed-in user's OWN configured MCP servers. Secrets are MASKED to hasHeaderValue flags: the
        // ciphertext never leaves the server, and the plaintext never existed here (the MCP analogue of
        // GET /api/providers, ADR 0018).
        app.MapGet("/api/mcp-servers", async (IGrainFactory grains, HttpContext http) =>
        {
            var overlay = await grains.GetGrain<IUserMcpConfigGrain>(http.GetUserId()).GetAsync();
            return Results.Json(new McpServersResponse([.. overlay.Servers.Select(Mask)]));
        });

        // Creates an MCP server. The id is server-minted (unlike a provider key, a server name is not an
        // identifier — a user may add two servers with the same name), so POST always inserts. The API
        // ENCRYPTS the secret here — the grain only ever stores ciphertext.
        app.MapPost("/api/mcp-servers", async (McpServerUpsertRequest request, IMcpSecretProtector protector, IGrainFactory grains, HttpContext http) =>
        {
            var error = ValidateMcpServer(request);
            if (error is not null)
            {
                return Results.Text(error, "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }

            var id = Guid.NewGuid().ToString("n");
            var stored = await grains.GetGrain<IUserMcpConfigGrain>(http.GetUserId())
                .SaveAsync(ToStored(request, id, protector));
            return Results.Json(Mask(stored));
        });

        // Updates the server named by the route id. A BLANK secret means "keep the stored secret" (the
        // field is write-only in the UI) — ToStored maps blank to a null ciphertext, which the grain reads
        // as "keep".
        app.MapPut("/api/mcp-servers/{id}", async (string id, McpServerUpsertRequest request, IMcpSecretProtector protector, IGrainFactory grains, HttpContext http) =>
        {
            var error = ValidateMcpServer(request);
            if (error is not null)
            {
                return Results.Text(error, "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }

            var stored = await grains.GetGrain<IUserMcpConfigGrain>(http.GetUserId())
                .SaveAsync(ToStored(request, id, protector));
            return Results.Json(Mask(stored));
        });

        // Deletes a server from the user's overlay. A warm conversation grain picks up the change on its
        // next activation (the tool provider is version-cached), never mid-turn.
        app.MapDelete("/api/mcp-servers/{id}", async (string id, IGrainFactory grains, HttpContext http) =>
        {
            await grains.GetGrain<IUserMcpConfigGrain>(http.GetUserId()).DeleteAsync(id);
            return Results.NoContent();
        });

        // ————— interactive OAuth for MCP servers (Phase 3) —————
        //
        // BOTH endpoints stay BEHIND Easy Auth (ADR 0002) — NEVER in excludedPaths — or GetUserId() would
        // return the local-dev principal and the flow would bind tokens to the wrong (or no) user. The app
        // drives auth-code + PKCE itself because the SDK's redirect delegate is BLOCKING and can't bridge a
        // stateless two-request web flow (Step 0). The starting point is the user's OWN configured MCP endpoint
        // (never a model/request); the authorization/token URLs are then DISCOVERED from that server's responses
        // (response-controlled — accepted under the same "user's own server" rationale as ADR 0018; see the
        // McpOAuthClient note), and an upstream token-exchange error is NEVER reflected into the redirect.

        // Begins the flow: discover the server's authorization server, build the PKCE authorization URL, and
        // 302-redirect the browser to it. State + verifier are persisted per-user for the callback.
        app.MapGet("/api/mcp/oauth/start", async (string serverId, McpOAuthClient oauth, IGrainFactory grains, HttpContext http) =>
        {
            var userId = http.GetUserId();
            var overlay = await grains.GetGrain<IUserMcpConfigGrain>(userId).GetAsync();
            var server = overlay.Servers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));
            if (server is null
                || !Enum.TryParse<McpAuthMode>(server.Auth, ignoreCase: true, out var mode)
                || mode != McpAuthMode.InteractiveOAuth)
            {
                return Results.Text("No InteractiveOAuth MCP server with that id.", "text/plain", statusCode: StatusCodes.Status404NotFound);
            }

            var mcpEndpoint = new Uri(server.Endpoint);
            var redirectUri = new Uri($"{http.Request.Scheme}://{http.Request.Host}/api/mcp/oauth/callback");
            try
            {
                var config = await oauth.DiscoverAsync(mcpEndpoint, http.RequestAborted);

                // A pre-registered client id is preferred; otherwise register dynamically if the server allows it.
                var clientId = server.ClientId;
                if (string.IsNullOrWhiteSpace(clientId))
                {
                    if (config.RegistrationEndpoint is null)
                    {
                        return Results.Text(
                            "This server needs a client id and does not support dynamic client registration. Set a client id on the server.",
                            "text/plain", statusCode: StatusCodes.Status400BadRequest);
                    }

                    clientId = await oauth.RegisterClientAsync(config.RegistrationEndpoint, redirectUri, http.RequestAborted);
                    // Persist the registered client id so a later Connect/Reconnect reuses it instead of
                    // minting (and orphaning) a fresh registration each time.
                    await grains.GetGrain<IUserMcpConfigGrain>(userId).SaveAsync(server with { ClientId = clientId });
                }

                var scopes = server.Scopes is { Count: > 0 } ? server.Scopes : config.ScopesSupported;
                var verifier = McpPkce.NewVerifier();
                var state = McpPkce.NewState();

                await grains.GetGrain<IUserMcpOAuthPendingGrain>(userId).PutAsync(new McpOAuthPending(
                    state, server.Id, verifier, clientId, config.TokenEndpoint.AbsoluteUri, redirectUri.AbsoluteUri, [.. scopes], mcpEndpoint.AbsoluteUri));

                var parameters = new Dictionary<string, string?>
                {
                    ["response_type"] = "code",
                    ["client_id"] = clientId,
                    ["redirect_uri"] = redirectUri.AbsoluteUri,
                    ["state"] = state,
                    ["code_challenge"] = McpPkce.Challenge(verifier),
                    ["code_challenge_method"] = "S256",
                    ["resource"] = mcpEndpoint.AbsoluteUri, // RFC 8707 resource indicator
                };
                if (scopes.Count > 0)
                {
                    parameters["scope"] = string.Join(' ', scopes);
                }

                var authorizeUrl = QueryHelpers.AddQueryString(config.AuthorizationEndpoint.AbsoluteUri, parameters);
                return Results.Redirect(authorizeUrl);
            }
            catch (Exception ex)
            {
                return Results.Text($"Could not start authorization: {ex.Message}", "text/plain", statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Completes the flow: validate state, exchange the code for tokens, persist them ENCRYPTED on the
        // server, and redirect back to the app. A replayed callback finds no pending entry (one-time TakeAsync).
        app.MapGet("/api/mcp/oauth/callback", async (McpOAuthClient oauth, IMcpSecretProtector protector, IGrainFactory grains, ILoggerFactory loggerFactory, HttpContext http) =>
        {
            var userId = http.GetUserId();
            var query = http.Request.Query;
            if (query.TryGetValue("error", out var error))
            {
                return Results.Redirect($"/?mcp_oauth=error&reason={Uri.EscapeDataString(error.ToString())}");
            }

            var code = query["code"].ToString();
            var state = query["state"].ToString();
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            {
                return Results.Redirect("/?mcp_oauth=error&reason=missing_code_or_state");
            }

            var pending = await grains.GetGrain<IUserMcpOAuthPendingGrain>(userId).TakeAsync(state);
            if (pending is null)
            {
                return Results.Redirect("/?mcp_oauth=error&reason=unknown_state");
            }

            try
            {
                var tokens = await oauth.ExchangeCodeAsync(
                    new Uri(pending.TokenEndpoint), pending.ClientId, code, pending.CodeVerifier,
                    new Uri(pending.RedirectUri), new Uri(pending.Resource), pending.Scopes, http.RequestAborted);

                var configGrain = grains.GetGrain<IUserMcpConfigGrain>(userId);
                var server = (await configGrain.GetAsync()).Servers
                    .FirstOrDefault(s => string.Equals(s.Id, pending.ServerId, StringComparison.OrdinalIgnoreCase));
                if (server is null)
                {
                    return Results.Redirect("/?mcp_oauth=error&reason=server_deleted");
                }

                await configGrain.SaveAsync(server with { TokenCiphertext = protector.Protect(tokens.Serialize()) });
                return Results.Redirect("/?mcp_oauth=connected");
            }
            catch (Exception ex)
            {
                // The token endpoint is DISCOVERED from the user's own MCP server (PRM), so its response body
                // is response-controlled — never reflect it into the redirect. Log the detail server-side and
                // hand the browser a generic reason.
                loggerFactory.CreateLogger("Agent42.Mcp.OAuth").LogWarning(ex,
                    "MCP OAuth code exchange failed for user {User}.", userId);
                return Results.Redirect("/?mcp_oauth=error&reason=token_exchange_failed");
            }
        });

        return app;
    }

    // An MCP endpoint comes from the authenticated user's OWN persisted config, never a model or a live
    // request — so, exactly like a user provider endpoint (ADR 0018), it is deliberately NOT SSRF
    // address-guarded and may be localhost (a self-hosted MCP server is a valid target). Validation is only structural:
    // absolute http/https, a parseable auth mode, and the fields that mode needs. None/Header (Phase 1) and
    // ClientCredentials (Phase 2) are supported; InteractiveOAuth is staged (Phase 3) and rejected here for
    // now. Returns an error message, or null when valid.
    private static string? ValidateMcpServer(McpServerUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "MCP server name must be non-empty.";
        }

        if (string.IsNullOrWhiteSpace(request.Endpoint)
            || !Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "Endpoint must be an absolute http or https URL.";
        }

        if (!Enum.TryParse<McpAuthMode>(request.Auth, ignoreCase: true, out var auth))
        {
            return $"Unknown auth mode '{request.Auth}'. Use 'None', 'Header', 'ClientCredentials', or 'InteractiveOAuth'.";
        }

        return auth switch
        {
            McpAuthMode.None => null,
            McpAuthMode.Header when string.IsNullOrWhiteSpace(request.HeaderName)
                => "Header auth requires a header name.",
            McpAuthMode.Header => null,
            // InteractiveOAuth: nothing extra is required at save time — the endpoints are discovered (PRM),
            // the client id is optional (dynamic registration otherwise), and tokens are obtained later via
            // the Connect flow (/api/mcp/oauth/start). Validation is only structural (endpoint above).
            McpAuthMode.InteractiveOAuth => null,
            // ClientCredentials (MSAL): the authority is an Entra login endpoint (absolute https); clientId
            // and ≥1 scope are required. The client secret is write-only (blank = keep on edit), so it is not
            // required at validation time — a missing secret degrades to a skipped server (best-effort).
            McpAuthMode.ClientCredentials when string.IsNullOrWhiteSpace(request.Authority)
                || !Uri.TryCreate(request.Authority, UriKind.Absolute, out var authUri)
                || authUri.Scheme != Uri.UriSchemeHttps
                => "ClientCredentials auth requires an absolute https authority (e.g. https://login.microsoftonline.com/{tenant}).",
            McpAuthMode.ClientCredentials when string.IsNullOrWhiteSpace(request.ClientId)
                => "ClientCredentials auth requires a client id.",
            McpAuthMode.ClientCredentials when request.Scopes is not { Count: > 0 }
                => "ClientCredentials auth requires at least one scope.",
            McpAuthMode.ClientCredentials => null,
            _ => $"Auth mode '{auth}' is not yet supported.",
        };
    }

    // Maps the request into the stored shape, encrypting each secret. A blank secret becomes a null
    // ciphertext: on create that is "no secret", on update the grain reads it as "keep the stored secret".
    // The auth mode is normalized to its enum name so the tool provider parses it back.
    private static StoredMcpServer ToStored(McpServerUpsertRequest request, string id, IMcpSecretProtector protector) =>
        new(
            Id: id,
            Name: request.Name.Trim(),
            Endpoint: request.Endpoint.Trim(),
            Auth: Enum.Parse<McpAuthMode>(request.Auth, ignoreCase: true).ToString(),
            Enabled: request.Enabled,
            HeaderName: string.IsNullOrWhiteSpace(request.HeaderName) ? null : request.HeaderName.Trim(),
            HeaderValueCiphertext: string.IsNullOrWhiteSpace(request.HeaderValue) ? null : protector.Protect(request.HeaderValue),
            Authority: string.IsNullOrWhiteSpace(request.Authority) ? null : request.Authority.Trim(),
            ClientId: string.IsNullOrWhiteSpace(request.ClientId) ? null : request.ClientId.Trim(),
            ClientSecretCiphertext: string.IsNullOrWhiteSpace(request.ClientSecret) ? null : protector.Protect(request.ClientSecret),
            Scopes: request.Scopes is { Count: > 0 } ? [.. request.Scopes.Select(s => s.Trim()).Where(s => s.Length > 0)] : null);

    // Never returns a secret — only whether one is stored. Authority/clientId/scopes are non-secret and are
    // returned so the edit form can prefill them; the ciphertexts stay server-side.
    private static McpServerResponse Mask(StoredMcpServer s) =>
        new(
            s.Id, s.Name, s.Endpoint, s.Auth, s.Enabled, s.HeaderName,
            HasHeaderValue: !string.IsNullOrEmpty(s.HeaderValueCiphertext),
            Authority: s.Authority,
            ClientId: s.ClientId,
            Scopes: s.Scopes,
            HasClientSecret: !string.IsNullOrEmpty(s.ClientSecretCiphertext),
            HasToken: !string.IsNullOrEmpty(s.TokenCiphertext));

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
                    m.SupportsImageGeneration, m.SupportsEmbeddings, m.SupportsSpeech))],
            UseResponsesApi: request.UseResponsesApi);

    // Never returns the key — only whether one is stored (hasKey). The ciphertext stays server-side.
    private static ProviderResponse Mask(StoredProvider p) =>
        new(
            p.Key, p.Kind, p.DisplayName, p.Endpoint,
            HasKey: !string.IsNullOrEmpty(p.ApiKeyCiphertext),
            Models: [.. p.Models.Select(m =>
                new ProviderModelResponse(m.Id, m.DisplayName, m.SupportsTools, m.SupportsVision,
                    m.SupportsImageGeneration, m.SupportsEmbeddings, m.SupportsSpeech))],
            p.UseResponsesApi);

    // Agent grains are keyed per user, so a conversation id sent by one user can
    // never resolve to another user's chat.
    private static string AgentKey(string userId, string conversationId)
        => $"{userId}/{conversationId}";

    // The public share URL for a token (ADR 0035): the token rides the URL FRAGMENT so it stays out of the
    // page-navigation access log and the Referer header (the follow-up /api/share/{token} call still carries
    // it — unavoidable). The static /shared/ viewer reads it from location.hash.
    private static string ShareUrl(HttpContext http, string token)
        => $"{http.Request.Scheme}://{http.Request.Host}/shared/#{token}";

    // A usage filter of null, blank, or "all" means "no filter" (ADR 0031) — collapse them so the
    // grain query sees a single sentinel.
    private static string? NormalizeFilter(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
}
