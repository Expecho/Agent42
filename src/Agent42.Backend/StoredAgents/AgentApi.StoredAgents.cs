
using Agent42.Agents;


namespace Agent42.Backend;

/// <summary>
/// Stored agent CRUD (ADR 0003) and the fire-and-forget POST /api/agent-run (ADR 0022) — the one endpoint on excludedPaths, so it self-enforces its API key and takes userId from the key directory, never from GetUserId.
/// </summary>
public static partial class AgentApi
{
    private static void MapStoredAgentEndpoints(IEndpointRouteBuilder app)
    {
        // The signed-in user's stored agents, most recently updated first. Seeds the built-in research agent
        // first (ADR 0082): once per user, idempotent, and only once the user has a concrete chat model, so a
        // user with no provider yet gets it on the first list after they add one.
        //
        // IServiceProvider rather than IUserChatClientFactoryProvider, for the /api/speech reason: a test host
        // that registers no factory provider must still route this endpoint, and simply skips the seed.
        app.MapGet("/api/agents", async (IGrainFactory grains, IServiceProvider services, HttpContext http, CancellationToken ct) =>
        {
            var registry = grains.GetGrain<IAgentRegistryGrain>(http.GetUserId());
            if (services.GetService<IUserChatClientFactoryProvider>() is { } factories)
            {
                var model = (await factories.GetForUserAsync(http.GetUserId(), ct)).DefaultModelId;
                await registry.EnsureResearchAgentAsync(model);
            }

            return await registry.GetAllAsync();
        });

        // Creates a new stored agent; the registry assigns its id and timestamps, and mints a per-agent
        // API key (ADR 0022) returned ONCE in the response body (only its hash is stored).
        // Kind: null/"" is an ordinary agent and "copilot" is the one kind a user may choose. Research stays
        // seed-only (ADR 0082), so it is refused here like any unknown kind instead of being silently dropped.
        app.MapPost("/api/agents", async (AgentUpsertRequest request, IGrainFactory grains, HttpContext http) =>
        {
            if (!AgentKinds.IsUserSelectable(request.Kind))
            {
                return Results.Text("Unknown agent kind. Use \"copilot\" or omit it.", "text/plain",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).SaveAsync(
                new AgentDefinition("", request.Name, request.Description, request.Model, request.SystemPrompt,
                    default, default, request.Continuous, request.EnabledTools,
                    // By NAME: McpExposed is the eleventh positional parameter and these call sites pass
                    // nine, so a positional append would silently land in ApiKeyHash (ADR 0065's scar).
                    McpExposed: request.McpExposed, Kind: request.Kind));
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
                request.RegenerateKey, request.RegenerateEmail);
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

        if (ValidateAgentRunFiles(files, AgentKinds.IsCopilot(definition?.Kind)) is { } fileError)
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
            // A bad document rejects the WHOLE batch (400) — the same all-or-nothing rule as an unsupported file.
            return Results.Text(storeError, "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        // Title from the prompt, else the first file name, else a placeholder (an attachment-only run
        // has no text). Listing the conversation is what lets the owner find and open the result later.
        var title = !string.IsNullOrWhiteSpace(prompt)
            ? prompt
            : files.FirstOrDefault()?.FileName ?? "Agent run";
        await StartAgentRunAsync(grains, owner.UserId, definition, conversationId, title, prompt, imageRefs, documentNames);

        // The run is in the background (OneWay); answer 202 with the conversation id.
        return Results.Accepted($"/api/conversations/{conversationId}/messages", new { conversationId });

    }

    // Lists the conversation, binds the agent and fires its background turn — shared by the agent-run and inbound
    // email doors (ADR 0022/0096), which differ only in how they authenticate and what they parse.
    private static async Task StartAgentRunAsync(
        IGrainFactory grains, string userId, AgentDefinition definition, string conversationId, string title,
        string prompt, List<AttachmentRef> imageRefs, List<string> documentNames)
    {
        await grains.GetGrain<IConversationIndexGrain>(userId)
            .TouchAsync(conversationId, title, definition.Id, definition.Name);

        var agentGrain = grains.GetGrain<IAgentGrain>(AgentKey(userId, conversationId));
        // Snapshot the agent's persona into the conversation (ADR 0003): pins its model + tools for the run.
        await agentGrain.BindAgentAsync(new AgentBinding(
            definition.Id, definition.Name, definition.SystemPrompt, definition.Model, definition.EnabledTools,
            definition.Kind));

        // Enable retrieval for any ingested document BEFORE the run, so the turn can see it (ADR 0008).
        foreach (var name in documentNames)
        {
            await agentGrain.RegisterDocumentAsync(name);
        }

        // Fire the background run (OneWay — returns immediately).
        await agentGrain.EnqueuePromptAsync(prompt, imageRefs.Count > 0 ? imageRefs : null);
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
    // Returns null when the batch is acceptable. A copilot agent takes no documents (it has no retrieval), so for one
    // a document is refused here too, before anything is ingested.
    private static IResult? ValidateAgentRunFiles(IFormFileCollection files, bool copilot)
    {
        foreach (var file in files)
        {
            if (AgentRunFileError(file, copilot) is { } error)
            {
                return Results.Text(error, "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }
        }

        return null;
    }

    // Why one file cannot ride an agent run, or null when it can. The agent-run door rejects the whole batch on the
    // first error; the email door skips the file and tells the agent (ADR 0096), so the rule lives in one place.
    private static string? AgentRunFileError(IFormFile file, bool copilot)
        => AgentRunFileError(file.FileName, file.ContentType, file.Length, copilot);

    // The same rule from a file's METADATA alone, so a caller that must download the bytes (the chat gateway, ADR 0111)
    // validates before fetching anything. An unknown length is checked later, by the capped download.
    internal static string? AgentRunFileError(string fileName, string? declaredType, long? length, bool copilot)
    {
        if (length > MaxAttachmentBytes)
        {
            return $"'{fileName}' exceeds the {MaxAttachmentBytes / (1024 * 1024)} MB limit.";
        }

        var mediaType = DocumentMediaTypes.ResolveMediaType(fileName, declaredType);
        if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !DocumentMediaTypes.IsSupported(mediaType))
        {
            return $"'{fileName}' is not a supported image or document type.";
        }

        return copilot && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? CopilotDocumentsRefused
            : null;
    }

    // Stores/ingests each file under the resolved owner+conversation key - the same per-file branch as
    // the browser upload endpoint. Images become attachment refs that ride the run; documents ingest for
    // retrieval (and are registered on the grain by the caller, never sent as attachments - ADR 0007).
    private static async Task<(List<AttachmentRef> Images, List<string> Documents, string? Error)>
        StoreAgentRunFilesAsync(
            IReadOnlyList<IFormFile> files, IAttachmentStore attachments,
            DocumentIngestionService ingestion, string ownerKey)
    {
        var contents = new List<AgentRunFileContent>();
        foreach (var file in files)
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            contents.Add(new AgentRunFileContent(file.FileName, file.ContentType, buffer.ToArray()));
        }

        return await StoreAgentRunFilesAsync(contents, attachments, ingestion, ownerKey);
    }

    // The byte-level core the form overload and the chat gateway share.
    internal static async Task<(List<AttachmentRef> Images, List<string> Documents, string? Error)>
        StoreAgentRunFilesAsync(
            IReadOnlyList<AgentRunFileContent> files, IAttachmentStore attachments,
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
            var bytes = file.Bytes;

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
                catch (Exception ex) when (ex is EmbeddingModelNotConfiguredException or EmbeddingDimensionMismatchException
                    or EmbeddingProviderException)
                {
                    // A bad document rejects the WHOLE batch — the same all-or-nothing rule as an unsupported
                    // file. The caller picks the status; no partial run is enqueued.
                    return (imageRefs, documentNames, ex.Message);
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

/// <summary>One file's bytes for <see cref="AgentApi.StoreAgentRunFilesAsync(IReadOnlyList{AgentRunFileContent}, IAttachmentStore, DocumentIngestionService, string)"/>.</summary>
/// <param name="ContentType">The declared type; the extension wins (a browser's or platform's label is unreliable).</param>
internal sealed record AgentRunFileContent(string FileName, string? ContentType, byte[] Bytes);
