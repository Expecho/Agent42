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

            http.Response.ContentType = "application/x-ndjson";
            await foreach (var evt in agent.ChatAsync(request.Message, request.Model))
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
        app.MapGet("/api/conversations/{conversationId}/model", (string conversationId, IGrainFactory grains, HttpContext http) =>
            grains.GetGrain<IAgentGrain>(AgentKey(http.GetUserId(), conversationId)).GetSelectedModelAsync());

        // Live list of the model deployments available in the configured AI Foundry account.
        app.MapGet("/api/models", (IModelCatalog catalog, CancellationToken ct) => catalog.GetAsync(ct));

        return app;
    }

    // Agent grains are keyed per user, so a conversation id sent by one user can
    // never resolve to another user's chat.
    private static string AgentKey(string userId, string conversationId)
        => $"{userId}/{conversationId}";
}

public record ChatRequest(string Message, string? Model);
