
using Agent42.Agents;


namespace Agent42.Backend;

/// <summary>
/// Read-only share links (ADR 0035). The owner routes sit behind Easy Auth; the two public routes are on excludedPaths, self-enforce the token, take userId from the directory and fail closed to 404.
/// </summary>
public static partial class AgentApi
{
    private static void MapSharingEndpoints(IEndpointRouteBuilder app)
    {
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

        // ————— public read-only share view (ADR 0035) —————
        //
        // Easy Auth EXCLUDES the /api/share/* paths (container-app.bicep globalValidation.excludedPaths — the
        // WILDCARD form is load-bearing, ADR 0042: a bare /api/share entry matches exactly and covers neither
        // route below), so an unauthenticated recipient can open a shared conversation. Like /api/agent-run
        // these SELF-ENFORCE: the token is the only credential, the owner + conversation come from the
        // DIRECTORY resolution (NEVER GetUserId — there is no Easy Auth principal on an excluded path, so it
        // would be local-dev), and both fail CLOSED. A browser-visitable link fails to 404 ("not found or
        // revoked"), not 401. Anything added under /api/share/ from now on is PUBLIC by construction and
        // inherits that duty.

        // The shared conversation's title, optional agent name, and full CURRENT history (a live read-only view:
        // future turns appear until the owner revokes). Reuses the ChatTurn/AttachmentRef DTOs the authenticated
        // /messages route returns (AttachmentRef.BlobPath stays [JsonIgnore] — the page only ever gets ids),
        // MINUS the tool calls — see the strip below.
        app.MapGet("/api/share/{token}", async (string token, IGrainFactory grains) =>
        {
            var target = await grains.GetGrain<IShareLinkDirectoryGrain>(ShareToken.Hash(token)).ResolveAsync();
            if (target is null)
            {
                return Results.NotFound();
            }

            var turns = await grains.GetGrain<IAgentGrain>(AgentKey(target.UserId, target.ConversationId)).GetHistoryAsync();

            // Tool calls are withheld from a public link (ADR 0080). Their arguments and results carry search
            // queries, fetched URLs and MCP payloads; widening a public link to include them is an ADR 0013
            // egress decision that was declined. Stripped SERVER-side and not by [JsonIgnore]: the viewer
            // ignoring the field is not the same as not sending it (it would still be one DevTools tab away),
            // and [JsonIgnore] would hide it from the OWNER's /messages response too, which is where the
            // replay this strip exists to limit actually happens.
            turns = [.. turns.Select(turn => turn with { ToolCalls = null })];

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
    }

    // The public share URL for a token (ADR 0035): the token rides the URL FRAGMENT so it stays out of the
    // page-navigation access log and the Referer header (the follow-up /api/share/{token} call still carries
    // it — unavoidable). The static /shared/ viewer reads it from location.hash.
    private static string ShareUrl(HttpContext http, string token)
        => $"{http.Request.Scheme}://{http.Request.Host}/shared/#{token}";
}
