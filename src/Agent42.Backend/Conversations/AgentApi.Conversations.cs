using System.Text.Json;
using System.Text.Json.Serialization;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Chat and conversation endpoints: the NDJSON streaming turn (ADR 0004) plus listing, history, model, bound agent and delete.
/// </summary>
public static partial class AgentApi
{
    // NDJSON wire format for stream events: camelCase fields, camelCase enum values
    // (e.g. "textDelta", "toolCallStart"), and null fields omitted.
    private static readonly JsonSerializerOptions StreamEventJson = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static void MapConversationEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat/{conversationId}", ChatAsync);

        // Stops the turn this conversation is currently streaming (ADR 0078). A separate request rather
        // than the chat connection dying, and that separation is the whole point: an aborted connection is
        // what a backgrounded PWA produces on every turn, and the server must keep going there (ADR 0046).
        // Only an explicit press means stop.
        //
        // 204 either way, and deliberately: by the time this arrives the turn may have finished on its own,
        // and telling the client it "failed" would only invite a retry of a race it cannot win.
        app.MapPost("/api/chat/{conversationId}/stop", async (
            string conversationId, IGrainFactory grains, HttpContext http) =>
        {
            await grains.GetGrain<IAgentGrain>(AgentKey(http.GetUserId(), conversationId)).StopAsync();
            return Results.NoContent();
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

        // Pins or unpins a conversation so it leads the sidebar listing regardless of recency (ADR 0045).
        // Idempotent, and a silent no-op for an id that isn't listed — an unlisted id is indistinguishable
        // from an unchanged one, so pinning discloses nothing about what exists. The grain key scopes the
        // write to the signed-in user, so pinning someone else's conversation is structurally impossible.
        app.MapPut("/api/conversations/{conversationId}/pin", async (string conversationId, PinConversationRequest request, IGrainFactory grains, HttpContext http) =>
        {
            await grains.GetGrain<IConversationIndexGrain>(http.GetUserId()).SetPinnedAsync(conversationId, request.Pinned);
            return Results.NoContent();
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
    }

    // Streaming chat: sends a prompt to the conversation's agent grain and streams
    // the turn back as NDJSON events (text deltas, tool calls, token usage).
    private static async Task ChatAsync(
        string conversationId, ChatRequest request, IGrainFactory grains, IAttachmentStore attachments, HttpContext http)
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
                    definition.EnabledTools, definition.Kind));
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
    }
}
