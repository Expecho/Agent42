using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Chat and conversation endpoints: the NDJSON streaming turn (ADR 0004) plus listing, history, model, bound agent and delete.
/// </summary>
public static partial class AgentApi
{
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
            // The turn replay logs hold conversation text too (ADR 0153): purged first, and a running turn's pump then finds
            // its log missing and stops persisting, so nothing is resurrected (D11).
            await PurgeChatTurnsAsync(http, userId, conversationId);
            await grains.GetGrain<IAgentGrain>(AgentKey(userId, conversationId)).PurgeAsync();
            await grains.GetGrain<IConversationIndexGrain>(userId).RemoveAsync(conversationId);
            // Revoke any read-only share link too, so a deleted conversation can't be resurrected through a
            // still-live public link (ADR 0035). No-op when the conversation was never shared.
            await grains.GetGrain<IUserShareLinksGrain>(userId).RemoveAsync(conversationId);
            return Results.NoContent();
        });

        // Fork from here (ADR 0092): a new listed conversation holding this one through a chosen exchange.
        app.MapPost("/api/conversations/{conversationId}/fork", ForkConversationAsync);

        // ————— read-only share links (ADR 0035) —————
    }

    // The fork route. The order is the safety argument: the source must be LISTED (one check that refuses an
    // incognito, unknown or other user's id), the new id is generated here and never taken from the caller, and the
    // new conversation is listed only AFTER its grain holds the session, so a failure leaves an unlisted grain and
    // never a listed empty conversation (the ADR 0070 shape).
    private static async Task<IResult> ForkConversationAsync(
        string conversationId, ForkRequest request, IGrainFactory grains, HttpContext http)
    {
        var userId = http.GetUserId();
        var index = grains.GetGrain<IConversationIndexGrain>(userId);
        var source = (await index.GetConversationsAsync()).FirstOrDefault(c => c.Id == conversationId);
        if (source is null)
        {
            return Results.NotFound();
        }

        var result = await grains.GetGrain<IAgentGrain>(AgentKey(userId, conversationId)).CreateForkAsync(
            request.ThroughUserTurn, request.ExpectedUserTurnCount, request.ExpectedLastUserTextHash ?? "");
        if (result is not { Outcome: HistoryEditOutcome.Ok, Fork: { } fork })
        {
            return HistoryEditConflict(result.Outcome);
        }

        var forkId = Guid.NewGuid().ToString();
        if (!await grains.GetGrain<IAgentGrain>(AgentKey(userId, forkId)).InitializeFromForkAsync(fork))
        {
            // A fresh random id names an empty grain, so this is unreachable short of a collision.
            return Results.Conflict(new { error = "The fork could not be created." });
        }

        await index.TouchAsync(forkId, ForkTitle(source.Title), source.AgentId, source.AgentName);
        if (source.GroupId is { } groupId)
        {
            await index.SetGroupAsync(forkId, groupId);
        }

        return Results.Created($"/api/conversations/{forkId}", new ForkResponse(forkId));
    }

    private const string ForkTitleSuffix = " (fork)";

    // The source title with " (fork)" appended, capped FIRST so the index's 60-character cap
    // (ConversationIndexGrain.MaxTitleLength) never cuts the suffix off. A source title the index already capped
    // ends in "…"; that is dropped before measuring and put back when this cut is needed too.
    private static string ForkTitle(string sourceTitle)
    {
        const int maxTitleLength = 60;
        var title = sourceTitle.Trim().TrimEnd('…').TrimEnd();
        var room = maxTitleLength - ForkTitleSuffix.Length;
        if (title.Length > room)
        {
            title = title[..(room - 1)].TrimEnd() + "…";
        }

        return title + ForkTitleSuffix;
    }

    // An incognito turn on a continuous agent's shared grain would stay in its in-memory session and the next normal
    // turn would persist it (ADR 0134, gotchas: an incognito turn stays in the in-memory session).
    private static bool RefusesIncognito(string conversationId, ChatRequest request)
        => request.Ephemeral && conversationId.StartsWith(RetentionLimits.ContinuousConversationIdPrefix, StringComparison.Ordinal);

    // Streaming chat: sends a prompt to the conversation's agent grain and streams
    // the turn back as NDJSON events (text deltas, tool calls, token usage).
    private static async Task ChatAsync(
        string conversationId, ChatRequest request, IGrainFactory grains, IAttachmentStore attachments, HttpContext http)
    {
        if (RefusesIncognito(conversationId, request))
        {
            await Results.BadRequest(new { error = "A continuous agent's conversation is shared and persisted, so it cannot be incognito" })
                .ExecuteAsync(http);
            return;
        }

        var userId = http.GetUserId();
        var agent = grains.GetGrain<IAgentGrain>(AgentKey(userId, conversationId));

        // Edit and regenerate need the whole concurrency token (ADR 0092); a half token is a malformed request.
        if (request.ReplaceLastTurn
            && (request.ExpectedUserTurnCount is null || string.IsNullOrEmpty(request.ExpectedLastUserTextHash)))
        {
            await Results.BadRequest(new { error = "replaceLastTurn needs expectedUserTurnCount and expectedLastUserTextHash" })
                .ExecuteAsync(http);
            return;
        }

        var attachmentRefs = await ResolveAttachmentsAsync(attachments, AgentKey(userId, conversationId), request.AttachmentIds);

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
                    definition.EnabledTools, definition.Kind, definition.RetentionMinutes), request.Ephemeral);
            }

            model = null;
        }

        // Edit and regenerate (ADR 0092): the last turn goes as LATE as possible — after the persona lookup, the
        // index touch and the bind, any of which may throw — so a failure there never loses the exchange. A refusal
        // answers 409 with no stream. Once removed it is written at once; if the run below then fails, the client
        // puts the text back in the composer.
        if (await ReplaceLastTurnAsync(agent, request) is { } outcome && outcome != HistoryEditOutcome.Ok)
        {
            await HistoryEditConflict(outcome).ExecuteAsync(http);
            return;
        }

        http.Response.ContentType = "application/x-ndjson";
        var stream = agent.ChatAsync(
            request.Message, model, attachmentRefs, request.Ephemeral,
            clientTools: KnownClientTools(request.ClientTools),
            // An edit or regenerate re-runs the turn just removed: its webhook event says so (ADR 0094).
            replacesLastTurn: request.ReplaceLastTurn,
            // What open MCP App views told the model (ADR 0133): per turn, untrusted, never stored.
            appContext: request.AppContext);

        // The resumable turn (ADR 0153): the grain stream is drained by a detached pump, and this response is only a reader of it,
        // so a dead socket loses the live view and never the turn. Resolved optionally: a host without it, or ChatTurns:Enabled=false,
        // streams the grain straight into the response as before.
        if (http.RequestServices.GetService<ChatTurnRunner>() is { Enabled: true } runner)
        {
            await StreamThroughPumpAsync(runner, stream, userId, conversationId, request.Ephemeral, http);
            return;
        }

        await foreach (var evt in stream)
        {
            await http.Response.WriteAsync(JsonSerializer.Serialize(evt, ChatStreamJson.Options) + "\n");
            await http.Response.Body.FlushAsync();
        }
    }

    // Resolves the ids the client sends back into refs, server-side, so a turn can only attach images that actually
    // exist under this user's conversation key (never a client-supplied blob path). Unknown ids are skipped rather
    // than failing the turn.
    private static async Task<IReadOnlyList<AttachmentRef>?> ResolveAttachmentsAsync(
        IAttachmentStore attachments, string ownerKey, IReadOnlyList<string>? ids)
    {
        if (ids is not { Count: > 0 })
        {
            return null;
        }

        var resolved = new List<AttachmentRef>();
        foreach (var id in ids)
        {
            if (await attachments.GetAsync(ownerKey, id) is { } reference)
            {
                resolved.Add(reference);
            }
        }

        return resolved;
    }

    // Removes the last turn for an edit or regenerate (ADR 0092), or null when this is an ordinary turn. The caller
    // has already refused a request without both token parts.
    private static async Task<HistoryEditOutcome?> ReplaceLastTurnAsync(IAgentGrain agent, ChatRequest request)
        => request.ReplaceLastTurn
            ? await agent.TruncateLastTurnAsync(
                request.ExpectedUserTurnCount!.Value, request.ExpectedLastUserTextHash!, request.Ephemeral)
            : null;

    // The 409 for a refused edit, regenerate or fork (ADR 0092), with a message the UI shows as it is.
    private static IResult HistoryEditConflict(HistoryEditOutcome outcome)
        => Results.Conflict(new
        {
            error = outcome switch
            {
                HistoryEditOutcome.Stale => "This conversation changed in another tab or window. Reload it and try again.",
                HistoryEditOutcome.Busy => "A reply is still running in this conversation. Wait for it to finish.",
                HistoryEditOutcome.Unsupported => "Copilot chats cannot be edited, regenerated or forked.",
                HistoryEditOutcome.NoModel => "No model provider is configured. Add one in Settings.",
                _ => "There is no message to replace here.",
            },
            outcome = outcome.ToString(),
        });

    // The client tools this browser says it can run (ADR 0089), plus mcp_elicitation (ADR 0095), narrowed to the
    // ones the server defines. A client can only narrow the set, never name a tool of its own, and the known set is
    // tiny, so the result is capped by construction. A list rather than a lazy query: it crosses the grain boundary.
    internal static List<string> KnownClientTools(IReadOnlyList<string>? advertised)
        => advertised is null
            ? []
            : [.. advertised.Where(ClientToolLimits.AdvertisableCapabilities.Contains).Distinct(StringComparer.Ordinal)];
}
