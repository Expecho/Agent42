using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// What a fleet share link's viewer gets of a task (ADR 0120): an ALLOWLIST, never <see cref="FleetTaskRecord"/>. The
/// record carries the user id, the workspace id, notes, the tool selection, the chat the task came from, the uploaded file
/// names and more, none of which belongs on a public page. <c>Reason</c> is a fixed text per status: the stored reason
/// is a raw exception message.
/// </summary>
public sealed record SharedFleetTaskResponse(
    string Request, string Status, string? Result, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? Reason);

/// <summary>
/// Read-only share links for fleet tasks (ADR 0120), the twin of ADR 0035's. The owner routes sit behind Easy Auth and are
/// scoped by <c>GetUserId()</c>. The public routes live under <c>/api/share/*</c>, which Easy Auth already excludes
/// (ADR 0042, the wildcard form), so they SELF-ENFORCE: the token is the only credential, the owner and task come from the
/// directory (NEVER <c>GetUserId()</c>, which is <c>local-dev</c> on an excluded path), and every miss is a 404.
/// </summary>
public static partial class AgentApi
{
    private static void MapFleetSharingEndpoints(IEndpointRouteBuilder app)
    {
        // The link for this task, or { url: null } when it is not shared. The caller's own links grain, so it can only
        // ever show the caller's own link.
        app.MapGet("/api/fleet/tasks/{id}/share", async (string id, IGrainFactory grains, HttpContext http) =>
        {
            var link = await grains.GetGrain<IUserFleetShareLinksGrain>(http.GetUserId()).GetAsync(id);
            return Results.Json(new ShareLinkResponse(link is null ? null : FleetShareUrl(http, link.Token)));
        });

        // Mints (or returns) the link. Only for a task of the caller's own: another user's task is a 404 and mints nothing.
        app.MapPost("/api/fleet/tasks/{id}/share", async (string id, IServiceProvider services, IGrainFactory grains, HttpContext http, CancellationToken ct) =>
        {
            if (services.GetService<FleetTaskService>() is not { } fleet)
            {
                return FleetUnavailable();
            }

            var userId = http.GetUserId();
            if (await fleet.GetAsync(userId, id, ct) is null)
            {
                return FleetNotFound();
            }

            var link = await grains.GetGrain<IUserFleetShareLinksGrain>(userId).CreateOrGetAsync(id);
            return Results.Json(new ShareLinkResponse(FleetShareUrl(http, link.Token)));
        });

        app.MapDelete("/api/fleet/tasks/{id}/share", async (string id, IGrainFactory grains, HttpContext http) =>
        {
            await grains.GetGrain<IUserFleetShareLinksGrain>(http.GetUserId()).RemoveAsync(id);
            return Results.NoContent();
        });

        // ————— public read-only views (ADR 0120). PUBLIC by construction: see the class comment. —————

        app.MapGet("/api/share/fleet/{token}", async (string token, IServiceProvider services, IGrainFactory grains, CancellationToken ct) =>
            await ResolveSharedFleetTaskAsync(token, services, grains, ct) is { } shared
                ? Results.Json(SharedFleetView(shared.Task))
                : Results.NotFound());

        app.MapGet("/api/share/fleet/{token}/files", async (string token, IServiceProvider services, IGrainFactory grains, CancellationToken ct) =>
            await ResolveSharedFleetTaskAsync(token, services, grains, ct) is { } shared
                ? await FleetFileListAsync(shared.Fleet, shared.Task, ct)
                : Results.NotFound());

        app.MapGet("/api/share/fleet/{token}/files/{**path}", async (string token, string path, IServiceProvider services, IGrainFactory grains, HttpContext http, CancellationToken ct) =>
            await ResolveSharedFleetTaskAsync(token, services, grains, ct) is { } shared
                ? await FleetFileDownloadAsync(shared.Fleet, shared.Task, path, http, ct)
                : Results.NotFound());

        app.MapGet("/api/share/fleet/{token}/canvas", async (string token, IServiceProvider services, IGrainFactory grains, CancellationToken ct) =>
            await ResolveSharedFleetTaskAsync(token, services, grains, ct) is { } shared
                ? await FleetCanvasListAsync(shared.Fleet, shared.Task, ct)
                : Results.NotFound());

        app.MapGet("/api/share/fleet/{token}/canvas/{name}/versions/{version:int}/content",
            (string token, string name, int version, IServiceProvider services, IGrainFactory grains, HttpContext http, CancellationToken ct)
                => ServeSharedFleetCanvasAsync(token, name, version, frame: false, services, grains, http, ct));

        app.MapGet("/api/share/fleet/{token}/canvas/{name}/versions/{version:int}/frame",
            (string token, string name, int version, IServiceProvider services, IGrainFactory grains, HttpContext http, CancellationToken ct)
                => ServeSharedFleetCanvasAsync(token, name, version, frame: true, services, grains, http, ct));
    }

    private sealed record SharedFleetTask(FleetTaskService Fleet, FleetTaskRecord Task);

    /// <summary>
    /// The task a token names, or null for everything else: an unknown or revoked token, a host without the fleet, a deleted
    /// task. The task is re-read through the service, so a deleted task is gone even if a deregister was missed.
    /// </summary>
    private static async Task<SharedFleetTask?> ResolveSharedFleetTaskAsync(string token, IServiceProvider services, IGrainFactory grains, CancellationToken ct)
    {
        if (services.GetService<FleetTaskService>() is not { } fleet)
        {
            return null;
        }

        var target = await grains.GetGrain<IFleetShareLinkDirectoryGrain>(ShareToken.Hash(token)).ResolveAsync();
        return target is not null && await fleet.GetAsync(target.UserId, target.TaskId, ct) is { } task
            ? new SharedFleetTask(fleet, task)
            : null;
    }

    // The sandbox headers go on first, so a miss is sandboxed too (ADR 0084).
    private static async Task<IResult> ServeSharedFleetCanvasAsync(
        string token, string name, int version, bool frame,
        IServiceProvider services, IGrainFactory grains, HttpContext http, CancellationToken ct)
    {
        AddArtifactHeaders(http.Response);
        return await ResolveSharedFleetTaskAsync(token, services, grains, ct) is { } shared
            ? await FleetCanvasVersionAsync(shared.Fleet, shared.Task, name, version, frame, http, ct)
            : Results.NotFound();
    }

    private static SharedFleetTaskResponse SharedFleetView(FleetTaskRecord task)
        => new(task.Request, task.Status, task.Result, task.StartedAt, task.FinishedAt, SharedFleetReason(task.Status));

    // Fixed texts: the stored reason is a raw exception message and may name providers, endpoints or paths.
    private static string? SharedFleetReason(string status) => status switch
    {
        FleetTaskStatus.Stopped => "The task was stopped before it finished.",
        FleetTaskStatus.Interrupted => "The task was interrupted by a restart.",
        FleetTaskStatus.Failed => "The task failed.",
        _ => null,
    };

    // The token rides the URL fragment, as in ShareUrl (ADR 0035).
    private static string FleetShareUrl(HttpContext http, string token)
        => $"{http.Request.Scheme}://{http.Request.Host}/shared/fleet.html#{token}";
}
