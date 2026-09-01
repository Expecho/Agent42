using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Chat history search (ADR 0061): an owner-scoped query over the message projection, and the
/// user-invocable backfill that fills it.
///
/// <para>Both routes are ordinary Easy-Auth-gated <c>/api/*</c> endpoints — deliberately NOT on
/// <c>excludedPaths</c>. The userId comes from <see cref="CurrentUser.GetUserId"/> and never from a
/// query parameter, so no token path (ADR 0022's agent-run key, ADR 0035's share tokens) can reach
/// the index at all.</para>
///
/// <para><see cref="IChatSearchIndex"/> is resolved optionally via <see cref="IServiceProvider"/> —
/// the <c>/api/speech</c> and <c>/api/config-health</c> pattern — so none of the hosts that call
/// <c>MapAgentApi</c> gain a mandatory dependency. A minimal-API parameter a host does not register
/// would be treated as a body and break routing for that WHOLE host; resolving it optionally 409s
/// cleanly instead.</para>
/// </summary>
public static partial class AgentApi
{
    private static void MapSearchEndpoints(IEndpointRouteBuilder app)
    {
        // Searches the caller's own indexed messages. Owner scoping is structural: userId is the
        // container's partition key, so this cannot read another user's corpus even if the query
        // tried to. A blank query returns no hits rather than the whole corpus.
        app.MapGet("/api/search", async (string? q, IServiceProvider services, HttpContext http, CancellationToken ct) =>
        {
            var index = services.GetService<IChatSearchIndex>();
            if (index is null)
            {
                return Results.Text("Chat search is not configured.", "text/plain", statusCode: StatusCodes.Status409Conflict);
            }

            if (string.IsNullOrWhiteSpace(q))
            {
                return Results.Json(new SearchResponse([]));
            }

            var hits = await index.SearchAsync(
                http.GetUserId(), q, ChatSearchLimits.MaxSearchResults, ct);

            return Results.Json(new SearchResponse(
                [.. hits.Select(h => new SearchHitResponse(h.ConversationId, h.Role, h.Text, h.Ordinal))]));
        });

        // Backfill (ADR 0061). Per-user by construction: it walks the CALLER's conversation index and
        // reindexes each of their conversations, never anyone else's.
        //
        // Bounded rather than merely polite: each conversation costs a grain ACTIVATION, and
        // OnActivateAsync connects to that user's MCP servers sequentially with a 10 s timeout each
        // (ADR 0024), so an unbounded sweep over a user with several dead servers is a multi-minute
        // request. `skip` plus the batch cap let the client resume; `More` says whether it should.
        app.MapPost("/api/search/reindex", async (int? skip, IGrainFactory grains, HttpContext http, CancellationToken ct) =>
        {
            var userId = http.GetUserId();
            var conversations = await grains.GetGrain<IConversationIndexGrain>(userId).GetConversationsAsync();

            var from = Math.Max(skip ?? 0, 0);
            var batch = conversations.Skip(from).Take(ChatSearchLimits.ReindexBatchSize).ToArray();

            var turns = 0;
            foreach (var conversation in batch)
            {
                ct.ThrowIfCancellationRequested();
                turns += await grains
                    .GetGrain<IAgentGrain>(AgentKey(userId, conversation.Id))
                    .ReindexAsync();
            }

            var more = from + batch.Length < conversations.Count;
            return Results.Json(new ReindexResponse(batch.Length, turns, more));
        });
    }
}
