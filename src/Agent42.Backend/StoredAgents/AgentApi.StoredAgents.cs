
using Agent42.Agents;


namespace Agent42.Backend;

/// <summary>
/// Stored agent CRUD (ADR 0003) and the fire-and-forget POST /api/agent-run (ADR 0022) — the one endpoint on excludedPaths, so it self-enforces its API key and takes userId from the key directory, never from GetUserId.
/// </summary>
public static partial class AgentApi
{
    private static void MapStoredAgentEndpoints(IEndpointRouteBuilder app)
    {
        // The signed-in user's stored agents, most recently updated first.
        app.MapGet("/api/agents", (IGrainFactory grains, HttpContext http) =>
            grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).GetAllAsync());

        // Creates a new stored agent; the registry assigns its id and timestamps, and mints a per-agent
        // API key (ADR 0022) returned ONCE in the response body (only its hash is stored).
        app.MapPost("/api/agents", async (AgentUpsertRequest request, IGrainFactory grains, HttpContext http) =>
        {
            var result = await grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).SaveAsync(
                new AgentDefinition("", request.Name, request.Description, request.Model, request.SystemPrompt,
                    default, default, request.Continuous, request.EnabledTools,
                    // By NAME: McpExposed is the eleventh positional parameter and these call sites pass
                    // nine, so a positional append would silently land in ApiKeyHash (ADR 0065's scar).
                    McpExposed: request.McpExposed));
            return Results.Json(new AgentSaveResponse(result.Agent, result.PlaintextApiKey));
        });

        // Updates a stored agent in place, preserving its creation time. A regenerateKey request rotates
        // the agent's API key (ADR 0022): the new raw key rides back ONCE, the old one stops working.
        app.MapPut("/api/agents/{id}", async (string id, AgentUpsertRequest request, IGrainFactory grains, HttpContext http) =>
        {
            var result = await grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).SaveAsync(
                new AgentDefinition(id, request.Name, request.Description, request.Model, request.SystemPrompt,
                    default, default, request.Continuous, request.EnabledTools,
                    McpExposed: request.McpExposed),
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

        app.MapPost("/api/agent-run", RunAgentAsync).DisableAntiforgery();
    }

    // External async run (ADR 0022): a per-agent API key (X-Api-Key header) invokes an agent WITHOUT a
    // signed-in session. Multipart (a 'prompt' field + optional 'file' parts) so external automation can
    // hand the agent an image or a document in the same call — the caller has no session and no
    // conversation id for the browser's two-phase upload. Easy Auth EXCLUDES this path
    // (container-app.bicep globalValidation.excludedPaths), so it self-enforces via the key and fails
    // closed (401). Responds 202 and runs the turn in the BACKGROUND (OneWay) into a new listed
    // conversation the owner opens in the UI later. The userId comes from the key resolution, NEVER from
    // GetUserId — on an excluded path there is no Easy Auth principal, so GetUserId would return local-dev.
    private static async Task<IResult> RunAgentAsync(
        HttpContext http, IGrainFactory grains, IAttachmentStore attachments, DocumentIngestionService ingestion)
    {
        var (owner, definition, authError) = await ResolveAgentRunCallerAsync(http, grains);
        if (authError is not null)
        {
            return authError;
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

        if (ValidateAgentRunFiles(files) is { } fileError)
        {
            return fileError;
        }

        if (string.IsNullOrWhiteSpace(prompt) && files.Count == 0)
        {
            return Results.Text(
                "Provide a 'prompt' and/or at least one file.",
                "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        var conversationId = Guid.NewGuid().ToString();
        var ownerKey = AgentKey(owner.UserId, conversationId);

        var (imageRefs, documentNames, storeError) =
            await StoreAgentRunFilesAsync(files, attachments, ingestion, ownerKey);
        if (storeError is not null)
        {
            return storeError;
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

    }

    // Authenticates an agent-run caller: the API key resolves to an owner through the global directory,
    // and the agent must still exist. Every failure is a uniform 401 — never 404 — so an unauthenticated
    // caller cannot probe which agents exist, and so the delete ordering (the directory entry clears AFTER
    // the registry write) still fails closed on a mid-failure orphan.
    private static async Task<(AgentKeyOwner? Owner, AgentDefinition? Definition, IResult? Error)>
        ResolveAgentRunCallerAsync(HttpContext http, IGrainFactory grains)
    {
        static (AgentKeyOwner?, AgentDefinition?, IResult?) Unauthorized(string message) =>
            (null, null, Results.Text(message, "text/plain", statusCode: StatusCodes.Status401Unauthorized));

        var key = http.Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            return Unauthorized("Missing X-Api-Key.");
        }

        // Resolve the key to its owner via the global directory (an O(1) grain get). Fail closed on null.
        var owner = await grains.GetGrain<IAgentKeyDirectoryGrain>(ApiKey.Hash(key)).ResolveAsync();
        if (owner is null)
        {
            return Unauthorized("Unknown API key.");
        }

        var definition = await grains.GetGrain<IAgentRegistryGrain>(owner.UserId).GetAsync(owner.AgentId);
        return definition is null
            ? Unauthorized("Unknown API key.")
            : (owner, definition, null);
    }

    // Validates EVERY file before a conversation is minted (constraint 13): a batch with any oversize or
    // unsupported file is rejected WHOLE (400), so automation retries cleanly instead of getting a
    // half-processed run. Media type is resolved by extension (the browser lies about .docx - ADR 0007).
    // Returns null when the batch is acceptable.
    private static IResult? ValidateAgentRunFiles(IFormFileCollection files)
    {
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

        return null;
    }

    // Stores/ingests each file under the resolved owner+conversation key - the same per-file branch as
    // the browser upload endpoint. Images become attachment refs that ride the run; documents ingest for
    // retrieval (and are registered on the grain by the caller, never sent as attachments - ADR 0007).
    private static async Task<(List<AttachmentRef> Images, List<string> Documents, IResult? Error)>
        StoreAgentRunFilesAsync(
            IFormFileCollection files, IAttachmentStore attachments,
            DocumentIngestionService ingestion, string ownerKey)
    {
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
                    chunkCount = (await ingestion.IngestAsync(ownerKey, file.FileName, mediaType, bytes)).ChunkCount;
                }
                catch (Exception ex) when (ex is EmbeddingModelNotConfiguredException or EmbeddingDimensionMismatchException)
                {
                    // A bad document rejects the WHOLE batch (400) — the same all-or-nothing rule as an
                    // unsupported file. The key holder gets a clear message; no partial run is enqueued.
                    return (imageRefs, documentNames, Results.Text(ex.Message, "text/plain", statusCode: StatusCodes.Status400BadRequest));
                }

                if (chunkCount > 0)
                {
                    documentNames.Add(file.FileName);
                }
            }
        }

        return (imageRefs, documentNames, null);
    }


}
