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

    public static IEndpointRouteBuilder MapAgentApi(this IEndpointRouteBuilder app)
    {
        // Streaming chat: sends a prompt to the conversation's agent grain and streams
        // the turn back as NDJSON events (text deltas, tool calls, token usage).
        app.MapPost("/api/chat/{conversationId}", async (string conversationId, ChatRequest request, IGrainFactory grains, HttpContext http) =>
        {
            var userId = http.GetUserId();
            await grains.GetGrain<IConversationIndexGrain>(userId).TouchAsync(conversationId, request.Message);
            var agent = grains.GetGrain<IAgentGrain>(AgentKey(userId, conversationId));

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
            await foreach (var evt in agent.ChatAsync(request.Message, model))
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

        return app;
    }

    // Agent grains are keyed per user, so a conversation id sent by one user can
    // never resolve to another user's chat.
    private static string AgentKey(string userId, string conversationId)
        => $"{userId}/{conversationId}";
}

// AgentId is the stored agent to bind on the first turn of an agent chat; the default
// keeps existing positional callers (new ChatRequest("hi", null)) compiling.
public record ChatRequest(string Message, string? Model, string? AgentId = null);

// Client payload for creating (POST) or updating (PUT) a stored agent. The id and
// timestamps are owned by the registry grain, so they aren't part of the request.
public record AgentUpsertRequest(string Name, string Description, string Model, string SystemPrompt);
