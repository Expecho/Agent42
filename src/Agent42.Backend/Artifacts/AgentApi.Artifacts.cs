using System.Text;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The artifact routes: a conversation's artifact list, one version's raw source (<c>/content</c>), and an html or
/// svg version as a page for a sandboxed iframe (<c>/frame</c>). The public share twins live in
/// <c>AgentApi.Sharing.cs</c> and go through the same <see cref="ServeArtifactVersionAsync"/>.
///
/// <para><b>Model-written HTML must never run on the app origin, and the protection is the RESPONSE HEADER.</b>
/// Anyone can open <c>/frame</c> as a top-level page, where the iframe's <c>sandbox</c> attribute does not exist.
/// The CSP <c>sandbox</c> directive (without <c>allow-same-origin</c>) gives the document an opaque origin even
/// then: no cookies, no storage, no reading <c>/api</c>. <c>connect-src 'none'</c> stops fetch, XHR and
/// WebSockets, and <c>form-action 'none'</c> stops a cookie-bearing form post to a same-origin endpoint, which
/// the sandbox alone does NOT stop (gotcha "Proxying a third-party app onto our own origin"). <c>'none'</c>, never
/// <c>'self'</c>: <c>'self'</c> matches every same-origin URL. What is NOT closed, and cannot be in a browser, is
/// self-navigation (<c>location = …</c>), an accepted egress residual recorded in the artifact ADR.</para>
/// </summary>
public static partial class AgentApi
{
    /// <summary>
    /// The policy on every artifact response. The cdnjs allowance lets an artifact load a library (and, accepted,
    /// any script gadget hosted there, still inside the sandbox).
    /// <c>frame-src https:</c> lets it embed any https page (a video or map embed); the nested frame inherits the
    /// sandbox, and the embed URL is an accepted egress channel (ADR 0084, user decision 2026-10-08).
    /// The two Google Fonts hosts serve the app's own fonts to <see cref="ArtifactTheme"/>.
    /// </summary>
    public const string ArtifactCsp =
        "sandbox allow-scripts; default-src 'none'; "
        + "script-src 'unsafe-inline' https://cdnjs.cloudflare.com; "
        + "style-src 'unsafe-inline' https://cdnjs.cloudflare.com https://fonts.googleapis.com; "
        + "img-src data: blob:; font-src data: https://cdnjs.cloudflare.com https://fonts.gstatic.com; media-src data: blob:; "
        + "frame-src https:; connect-src 'none'; form-action 'none'; base-uri 'none'; frame-ancestors 'self'";

    private static void MapArtifactEndpoints(IEndpointRouteBuilder app)
    {
        // Metadata only: kinds, titles and version numbers. The attachment ids stay [JsonIgnore]'d, so the page
        // only ever reads a version through the two routes below, which send the sandbox headers.
        app.MapGet("/api/conversations/{conversationId}/artifacts", async (string conversationId, IGrainFactory grains, HttpContext http) =>
            Results.Json(await grains.GetGrain<IAgentGrain>(AgentKey(http.GetUserId(), conversationId)).GetArtifactsAsync()));

        app.MapGet("/api/conversations/{conversationId}/artifacts/{artifactId}/versions/{version:int}/content",
            (string conversationId, string artifactId, int version, IGrainFactory grains, IAttachmentStore attachments, HttpContext http) =>
                ServeArtifactVersionAsync(AgentKey(http.GetUserId(), conversationId), artifactId, version, frame: false, grains, attachments, http));

        app.MapGet("/api/conversations/{conversationId}/artifacts/{artifactId}/versions/{version:int}/frame",
            (string conversationId, string artifactId, int version, IGrainFactory grains, IAttachmentStore attachments, HttpContext http) =>
                ServeArtifactVersionAsync(AgentKey(http.GetUserId(), conversationId), artifactId, version, frame: true, grains, attachments, http));
    }

    // Serves one version, owner-scoped by the grain key. The artifact id is resolved against THIS conversation's
    // state before any blob is read, so a client can only reach versions the conversation holds. The headers go on
    // every response from these routes, failures included, so no path through here renders unsandboxed.
    private static async Task<IResult> ServeArtifactVersionAsync(
        string ownerKey, string artifactId, int version, bool frame,
        IGrainFactory grains, IAttachmentStore attachments, HttpContext http)
    {
        AddArtifactHeaders(http.Response);

        var artifact = (await grains.GetGrain<IAgentGrain>(ownerKey).GetArtifactsAsync())
            .FirstOrDefault(a => a.Id == artifactId);
        var entry = artifact?.Versions.Find(v => v.Version == version);
        if (artifact is null || entry is null || (frame && !ArtifactKind.IsFramed(artifact.Kind)))
        {
            return Results.NotFound();
        }

        var stored = await attachments.OpenReadAsync(ownerKey, entry.AttachmentId, http.RequestAborted);
        if (stored is null)
        {
            return Results.NotFound();
        }

        var source = Encoding.UTF8.GetString(stored.Bytes);
        if (!frame)
        {
            // Always text/plain, whatever the kind, so even an html source opened directly reads as text.
            return Results.Text(source, "text/plain; charset=utf-8");
        }

        // The app's look goes in first, so the artifact's own CSS wins (ArtifactTheme). The theme is the pane's.
        var page = artifact.Kind == ArtifactKind.Svg ? WrapSvg(source) : source;
        return Results.Text(ArtifactTheme.Apply(page, http.Request.Query["theme"], http.Request.Query["palette"]), "text/html; charset=utf-8");
    }

    private static void AddArtifactHeaders(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy = ArtifactCsp;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.CacheControl = "private, no-store";
    }

    // An svg is framed inside a minimal page, under the same policy, so a scripted svg is contained like html. The
    // page is centred on the theme background, and the svg's currentColor is the theme's ink.
    private static string WrapSvg(string svg)
        => "<!doctype html><html><head><meta charset=\"utf-8\"><style>html,body{margin:0;height:100%}"
            + "body{display:flex;align-items:center;justify-content:center}svg{max-width:100%;max-height:100%}"
            + "</style></head><body>" + svg + "</body></html>";
}
