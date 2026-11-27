
using Agent42.Agents;


namespace Agent42.Backend;

/// <summary>
/// Read-only share links (ADR 0035). The owner routes sit behind Easy Auth; the two public routes are on excludedPaths, self-enforce the token, take userId from the directory and fail closed to 404.
/// </summary>
public static partial class AgentApi
{
    private static void MapSharingEndpoints(IEndpointRouteBuilder app)
    {
        MapShareOwnerGroup(app);
        MapSharedConversationGroup(app);
        MapSharedArtifactsGroup(app);
    }

    // The owner's routes (behind Easy Auth): read, mint and revoke the link.
    private static void MapShareOwnerGroup(IEndpointRouteBuilder app)
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

    }

    // The public read-only conversation view and its attachments (excluded from Easy Auth, so self-enforcing).
    private static void MapSharedConversationGroup(IEndpointRouteBuilder app)
    {
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
        app.MapGet("/api/share/{token}/attachments/{attachmentId}", async (string token, string attachmentId, IAttachmentStore attachments, IGrainFactory grains, HttpContext http) =>
        {
            var target = await grains.GetGrain<IShareLinkDirectoryGrain>(ShareToken.Hash(token)).ResolveAsync();
            if (target is null)
            {
                return Results.NotFound();
            }

            var content = await attachments.OpenReadAsync(AgentKey(target.UserId, target.ConversationId), attachmentId);

            // nosniff: an artifact blob is stored as octet-stream so it can never render here, and this keeps a
            // browser from sniffing any blob under the shared key into HTML on the app origin either.
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return content is null ? Results.NotFound() : Results.File(content.Bytes, content.MediaType);
        });

    }

    // The public artifact routes of a shared conversation (same rules as the group above).
    private static void MapSharedArtifactsGroup(IEndpointRouteBuilder app)
    {
        // The shared conversation's artifacts (every artifact and version in its state, consistent with a LIVE
        // view: later turns appear too). Token-scoped analogues of the owner routes in AgentApi.Artifacts.cs, and
        // the same rules as the two routes above: owner from the directory, never GetUserId, fail closed to 404.
        // The version routes go through ServeArtifactVersionAsync, so they send the same sandbox headers and serve
        // only ids that exist in the shared conversation's state.
        app.MapGet("/api/share/{token}/artifacts", async (string token, IGrainFactory grains) =>
        {
            var target = await grains.GetGrain<IShareLinkDirectoryGrain>(ShareToken.Hash(token)).ResolveAsync();
            return target is null
                ? Results.NotFound()
                : Results.Json(await grains.GetGrain<IAgentGrain>(AgentKey(target.UserId, target.ConversationId)).GetArtifactsAsync());
        });

        app.MapGet("/api/share/{token}/artifacts/{artifactId}/versions/{version:int}/content",
            (string token, string artifactId, int version, IGrainFactory grains, IAttachmentStore attachments, HttpContext http) =>
                ServeSharedArtifactVersionAsync(token, artifactId, version, frame: false, grains, attachments, http));

        app.MapGet("/api/share/{token}/artifacts/{artifactId}/versions/{version:int}/frame",
            (string token, string artifactId, int version, IGrainFactory grains, IAttachmentStore attachments, HttpContext http) =>
                ServeSharedArtifactVersionAsync(token, artifactId, version, frame: true, grains, attachments, http));

        // ————— user profile (per-user preferences) —————
    }

    // A shared artifact version: the headers first (so a 404 is sandboxed too), then the token, then the owner route's
    // logic under the owner + conversation the directory names.
    private static async Task<IResult> ServeSharedArtifactVersionAsync(
        string token, string artifactId, int version, bool frame,
        IGrainFactory grains, IAttachmentStore attachments, HttpContext http)
    {
        AddArtifactHeaders(http.Response);
        var target = await grains.GetGrain<IShareLinkDirectoryGrain>(ShareToken.Hash(token)).ResolveAsync();
        return target is null
            ? Results.NotFound()
            : await ServeArtifactVersionAsync(
                AgentKey(target.UserId, target.ConversationId), artifactId, version, frame, grains, attachments, http);
    }

    // The public share URL for a token (ADR 0035): the token rides the URL FRAGMENT so it stays out of the
    // page-navigation access log and the Referer header (the follow-up /api/share/{token} call still carries
    // it — unavoidable). The static /shared/ viewer reads it from location.hash.
    private static string ShareUrl(HttpContext http, string token)
        => $"{http.Request.Scheme}://{http.Request.Host}/shared/#{token}";
}
