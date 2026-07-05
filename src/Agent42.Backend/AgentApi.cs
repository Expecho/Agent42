using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

public static class AgentApi
{
    // Single-user for now; the conversation index grain is keyed by user id.
    private const string UserId = "default";

    public static IEndpointRouteBuilder MapAgentApi(this IEndpointRouteBuilder app)
    {
        // Streaming chat: sends a prompt to the conversation's agent grain and streams
        // the reply back as NDJSON chunks.
        app.MapPost("/api/chat/{conversationId}", async (string conversationId, ChatRequest request, IGrainFactory grains, HttpContext http) =>
        {
            await grains.GetGrain<IConversationIndexGrain>(UserId).TouchAsync(conversationId, request.Message);
            var agent = grains.GetGrain<IAgentGrain>(conversationId);

            http.Response.ContentType = "application/x-ndjson";
            await foreach (var chunk in agent.ChatAsync(request.Message))
            {
                await http.Response.WriteAsync(
                    JsonSerializer.Serialize(new ChatChunk("text", chunk), JsonSerializerOptions.Web) + "\n");
                await http.Response.Body.FlushAsync();
            }
        });

        // The user's conversations, most recently used first.
        app.MapGet("/api/conversations", (IGrainFactory grains) =>
            grains.GetGrain<IConversationIndexGrain>(UserId).GetConversationsAsync());

        // Full visible history of one conversation, so the UI can resume it.
        app.MapGet("/api/conversations/{conversationId}/messages", (string conversationId, IGrainFactory grains) =>
            grains.GetGrain<IAgentGrain>(conversationId).GetHistoryAsync());

        return app;
    }
}

public record ChatRequest(string Message);
public record ChatChunk(string Type, string Content);
