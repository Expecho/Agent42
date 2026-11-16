using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Microsoft.AspNetCore.Http.Features;
using Orleans.Runtime;

namespace Agent42.Backend;

/// <summary>A task the page starts: the request, optionally the done task it follows up, the model, the token cap,
/// a tool selection overriding the user's default (null follows the default, ADR 0099), and the task whose attached
/// files a Run again copies (ADR 0101). <c>Clarify</c> asks the user questions about the task first (ADR 0118).
/// <c>SelfReview</c> lets the agent that writes a team's result check it against the task and fix gaps (ADR 0119).</summary>
public sealed record FleetSubmitRequest(
    string? Task, string? FollowUpOf, string? Model, long? MaxTokens, IReadOnlyList<string>? EnabledTools = null,
    string? CopyInputsOf = null, bool? Clarify = null, bool? SelfReview = null);

/// <summary>The user's answer to a task's open question, optionally naming the question (the <c>prompt</c> event's id).</summary>
public sealed record FleetAnswerRequest(string? Answer, int? QuestionId = null);

/// <summary>An edited system prompt.</summary>
public sealed record FleetPromptRequest(string? Text);

/// <summary>
/// The agent fleet's endpoints (ADR 0097), all behind Easy Auth (none is in <c>excludedPaths</c>) and all scoped to
/// <c>GetUserId()</c>: another user's task is a 404. <see cref="FleetTaskService"/> is resolved optionally (the
/// <c>/api/speech</c> pattern), so a host without the fleet answers 503. Errors are JSON objects, never bare strings.
/// </summary>
public static partial class AgentApi
{
    private static void MapFleetEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/fleet/config", FleetConfig);
        app.MapGet("/api/fleet/tasks", ListFleetTasks);
        app.MapPost("/api/fleet/tasks", SubmitFleetTask);
        app.MapPost("/api/fleet/tasks/with-files", SubmitFleetTaskWithFiles).DisableAntiforgery();
        app.MapGet("/api/fleet/tasks/{id}", GetFleetTask);
        app.MapDelete("/api/fleet/tasks/{id}", DeleteFleetTask);
        app.MapPost("/api/fleet/tasks/{id}/stop", StopFleetTask);
        app.MapPost("/api/fleet/tasks/{id}/answer", AnswerFleetTask);
        app.MapGet("/api/fleet/tasks/{id}/events", StreamFleetEvents);
        app.MapGet("/api/fleet/tasks/{id}/files", ListFleetFiles);
        app.MapGet("/api/fleet/tasks/{id}/files/{**path}", DownloadFleetFile);
        app.MapGet("/api/fleet/tasks/{id}/canvas", ListFleetCanvas);
        app.MapGet("/api/fleet/tasks/{id}/canvas/{name}/versions/{version:int}/content",
            (string id, string name, int version, IServiceProvider services, HttpContext http, CancellationToken ct)
                => ServeFleetCanvasAsync(id, name, version, frame: false, services, http, ct));
        app.MapGet("/api/fleet/tasks/{id}/canvas/{name}/versions/{version:int}/frame",
            (string id, string name, int version, IServiceProvider services, HttpContext http, CancellationToken ct)
                => ServeFleetCanvasAsync(id, name, version, frame: true, services, http, ct));
        app.MapGet("/api/fleet/prompts", ListFleetPrompts);
        app.MapPut("/api/fleet/prompts/{key}", PutFleetPrompt);
        app.MapDelete("/api/fleet/prompts/{key}", ResetFleetPrompt);
    }

    private static IResult FleetUnavailable()
        => Results.Json(new { error = "The agent fleet is not available on this server." }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult FleetNotFound() => Results.Json(new { error = "No such task." }, statusCode: StatusCodes.Status404NotFound);

    private static IResult FleetOutcomeResult(FleetOutcome outcome, string conflict) => outcome switch
    {
        FleetOutcome.Ok => Results.Json(new { }),
        FleetOutcome.NotFound => FleetNotFound(),
        _ => Results.Json(new { error = conflict }, statusCode: StatusCodes.Status409Conflict),
    };

    private static async Task<IResult> FleetConfig(IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetTaskService>() is not { } fleet)
        {
            return FleetUnavailable();
        }

        // The composer shows what THIS user's task will run on (ADR 0135); the token caps stay the operator's.
        var options = await fleet.ResolveOptionsAsync(http.GetUserId(), ct);
        return Results.Json(new
        {
            defaultMaxTokens = options.DefaultMaxTokensPerTask,
            maxTokensLimit = options.MaxTokensPerTaskLimit,
            maxAgents = options.MaxAgentsPerTask,
            maxTeamDepth = options.MaxTeamDepth,
            approvalDepth = options.ApprovalDepth,
            judgeDepth = options.JudgeDepth,
            copilotAgentsEnabled = fleet.CopilotAvailable,
        });
    }

    private static async Task<IResult> ListFleetTasks(IServiceProvider services, HttpContext http, CancellationToken ct)
        => services.GetService<FleetTaskService>() is { } fleet
            ? Results.Json(await fleet.ListAsync(http.GetUserId(), ct))
            : FleetUnavailable();

    private static async Task<IResult> SubmitFleetTask(FleetSubmitRequest request, IServiceProvider services, HttpContext http, CancellationToken ct)
        => services.GetService<FleetTaskService>() is { } fleet
            ? await SubmitFleetAsync(fleet, request, [], http, ct)
            : FleetUnavailable();

    /// <summary>The form overhead allowed on top of the files: the boundaries, part headers and the request JSON.</summary>
    private const long FleetFormOverheadBytes = 1024 * 1024;

    /// <summary>
    /// A task with attached files (ADR 0101), as <c>multipart/form-data</c>: a <c>request</c> field with the
    /// <see cref="FleetSubmitRequest"/> JSON and one <c>files</c> part per file. A route of its own, so the raised body
    /// limit, set here before the body is read, never applies to the JSON route.
    /// </summary>
    private static async Task<IResult> SubmitFleetTaskWithFiles(IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetTaskService>() is not { } fleet)
        {
            return FleetUnavailable();
        }

        var options = fleet.Options;
        var limit = Math.Min(options.MaxInputFiles * options.MaxInputFileBytes, options.MaxWorkspaceBytes) + FleetFormOverheadBytes;
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
        {
            bodySize.MaxRequestBodySize = limit;
        }

        if (!http.Request.HasFormContentType)
        {
            return FleetBadRequest("Send the task as multipart/form-data.");
        }

        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = limit }, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            return Results.Json(new { error = "The files are too large for one task." }, statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        FleetSubmitRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<FleetSubmitRequest>(form["request"].ToString(), JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null)
        {
            return FleetBadRequest("The form needs a 'request' field with the task as JSON.");
        }

        var files = new List<FleetInputFile>();
        foreach (var file in form.Files)
        {
            using var content = new MemoryStream();
            await file.CopyToAsync(content, ct);
            files.Add(new FleetInputFile(file.FileName, content.ToArray()));
        }

        return await SubmitFleetAsync(fleet, request, files, http, ct);
    }

    private static async Task<IResult> SubmitFleetAsync(
        FleetTaskService fleet, FleetSubmitRequest request, IReadOnlyList<FleetInputFile> files, HttpContext http, CancellationToken ct)
    {
        // The request's identity, as the middleware put it in RequestContext: the background run re-publishes it.
        var telemetry = RequestContext.Get(UserTelemetryContext.RequestContextKey) as UserTelemetryContext;
        var (taskId, error) = await fleet.SubmitAsync(
            http.GetUserId(),
            new FleetSubmit(request.Task ?? "", request.FollowUpOf, request.Model, request.MaxTokens, request.EnabledTools, files, request.CopyInputsOf, Clarify: request.Clarify == true, SelfReview: request.SelfReview == true),
            telemetry, ct);
        return taskId is null
            ? Results.Json(new { error }, statusCode: StatusCodes.Status409Conflict)
            : Results.Json(new { taskId }, statusCode: StatusCodes.Status202Accepted);
    }

    private static IResult FleetBadRequest(string error)
        => Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

    private static async Task<IResult> GetFleetTask(string id, IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetTaskService>() is not { } fleet)
        {
            return FleetUnavailable();
        }

        return await fleet.GetAsync(http.GetUserId(), id, ct) is { } task ? Results.Json(task) : FleetNotFound();
    }

    private static async Task<IResult> DeleteFleetTask(string id, IServiceProvider services, IGrainFactory grains, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetTaskService>() is not { } fleet)
        {
            return FleetUnavailable();
        }

        var userId = http.GetUserId();
        var outcome = await fleet.DeleteAsync(userId, id, ct);
        if (outcome == FleetOutcome.Ok)
        {
            // Deleting a task also revokes its share link (ADR 0120), belt and braces: the public routes re-read the task.
            await grains.GetGrain<IUserFleetShareLinksGrain>(userId).RemoveAsync(id);
            // And its analyzer conversation (ADR 0123): nothing else could purge it once the task is gone.
            var analyzer = FleetAnalyzerGrain(grains, userId, id);
            await analyzer.StopAsync();
            await analyzer.PurgeAsync();
        }

        return FleetOutcomeResult(outcome, "The task is running, stop it first.");
    }

    private static async Task<IResult> StopFleetTask(string id, IServiceProvider services, HttpContext http, CancellationToken ct)
        => services.GetService<FleetTaskService>() is { } fleet
            ? FleetOutcomeResult(await fleet.StopAsync(http.GetUserId(), id, ct), "The task is not running.")
            : FleetUnavailable();

    private static async Task<IResult> AnswerFleetTask(string id, FleetAnswerRequest request, IServiceProvider services, HttpContext http, CancellationToken ct)
        => services.GetService<FleetTaskService>() is { } fleet
            ? FleetOutcomeResult(await fleet.AnswerAsync(http.GetUserId(), id, request.Answer ?? "", request.QuestionId, ct), "No question is open.")
            : FleetUnavailable();

    /// <summary>
    /// Server-sent events: the task's persisted log, then live while it runs, one <c>data:</c> line per event. An idle
    /// stream gets a comment line every <c>Fleet:SseKeepAlive</c>, so the ACA ingress does not drop it.
    /// </summary>
    private static async Task StreamFleetEvents(string id, IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetTaskService>() is not { } fleet)
        {
            await FleetUnavailable().ExecuteAsync(http);
            return;
        }

        if (await fleet.SubscribeAsync(http.GetUserId(), id, ct) is not { } lines)
        {
            await FleetNotFound().ExecuteAsync(http);
            return;
        }

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.Body.FlushAsync(ct);
        await WriteEventStreamAsync(http.Response, lines, fleet.Options.SseKeepAlive, ct);
    }

    private static async Task WriteEventStreamAsync(HttpResponse response, IAsyncEnumerable<string> lines, TimeSpan keepAlive, CancellationToken ct)
    {
        var enumerator = lines.GetAsyncEnumerator(ct);
        var next = enumerator.MoveNextAsync().AsTask();
        // One timer at a time, not one per event: streamed text arrives in bursts of hundreds of events.
        var tick = Task.Delay(keepAlive, ct);
        try
        {
            while (true)
            {
                if (await Task.WhenAny(next, tick) == tick)
                {
                    await tick;
                    await response.WriteAsync(": keep-alive\n\n", ct);
                    await response.Body.FlushAsync(ct);
                    tick = Task.Delay(keepAlive, ct);
                    continue;
                }

                if (!await next)
                {
                    return;
                }

                await response.WriteAsync($"data: {enumerator.Current}\n\n", Encoding.UTF8, ct);
                await response.Body.FlushAsync(ct);
                next = enumerator.MoveNextAsync().AsTask();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The browser went away.
        }
        finally
        {
            // A pending MoveNext ends with the request's cancellation; it has to finish before the dispose.
            await next.ContinueWith(_ => { }, TaskScheduler.Default);
            await enumerator.DisposeAsync();
        }
    }

    private static async Task<IResult> ListFleetFiles(string id, IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetTaskService>() is not { } fleet)
        {
            return FleetUnavailable();
        }

        var userId = http.GetUserId();
        return await fleet.GetAsync(userId, id, ct) is { } task
            ? await FleetFileListAsync(fleet, task, ct)
            : FleetNotFound();
    }

    /// <summary>The workspace's files. Shared by the owner route and the public share route (ADR 0120).</summary>
    private static async Task<IResult> FleetFileListAsync(FleetTaskService fleet, FleetTaskRecord task, CancellationToken ct)
        => Results.Json(await fleet.Store.ListWorkspaceAsync(task.UserId, task.WorkspaceId, ct));

    /// <summary>
    /// A workspace file. The content is model-written, so this is an XSS sink: it is always an attachment, with
    /// <c>nosniff</c> and a type that never renders (<c>text/plain</c> or <c>application/octet-stream</c>), never
    /// <c>text/html</c> or <c>image/svg+xml</c> from the app origin (the ADR 0084 precedent).
    /// </summary>
    private static async Task<IResult> DownloadFleetFile(string id, string path, IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetTaskService>() is not { } fleet)
        {
            return FleetUnavailable();
        }

        var userId = http.GetUserId();
        return await fleet.GetAsync(userId, id, ct) is { } task
            ? await FleetFileDownloadAsync(fleet, task, path, http, ct)
            : FleetNotFound();
    }

    /// <summary>
    /// One workspace file, for the task's owner or for a share link's viewer (ADR 0120): both go through here, so the
    /// attachment, <c>nosniff</c> and never-renders rules cannot drift between them.
    /// </summary>
    private static async Task<IResult> FleetFileDownloadAsync(FleetTaskService fleet, FleetTaskRecord task, string path, HttpContext http, CancellationToken ct)
    {
        byte[]? content;
        try
        {
            content = await fleet.Store.GetWorkspaceFileAsync(task.UserId, task.WorkspaceId, path, ct);
        }
        catch (ArgumentException)
        {
            return Results.Json(new { error = "That is not a path inside the workspace." }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (content is null)
        {
            return FleetNotFound();
        }

        http.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(content, IsUtf8Text(content) ? "text/plain; charset=utf-8" : "application/octet-stream", Path.GetFileName(path));
    }

    /// <summary>The task's canvas pages (ADR 0102), in the shape the artifact pane lists: the page name is the id.</summary>
    private static async Task<IResult> ListFleetCanvas(string id, IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetTaskService>() is not { } fleet)
        {
            return FleetUnavailable();
        }

        var userId = http.GetUserId();
        return await fleet.GetAsync(userId, id, ct) is { } task
            ? await FleetCanvasListAsync(fleet, task, ct)
            : FleetNotFound();
    }

    /// <summary>The canvas pages in the shape the artifact pane lists. Shared by the owner and the share routes (ADR 0120).</summary>
    private static async Task<IResult> FleetCanvasListAsync(FleetTaskService fleet, FleetTaskRecord task, CancellationToken ct)
    {
        var manifest = await fleet.Store.GetCanvasManifestAsync(task.UserId, task.WorkspaceId, ct);
        return Results.Json(manifest.Pages.Select(p => new
        {
            id = p.Name,
            kind = p.Kind,
            title = p.Title,
            language = p.Language,
            versions = p.Versions.Select(v => new { version = v }),
        }));
    }

    /// <summary>
    /// One canvas page version, read-only, under the chat artifacts' sandbox headers (ADR 0084's serving rules, reused
    /// by ADR 0102): the headers go on first, so a miss carries them too. The task is resolved owner-scoped, and the
    /// name and version against the MANIFEST before any blob is read, so a pruned or unknown version is a 404.
    /// </summary>
    private static async Task<IResult> ServeFleetCanvasAsync(
        string id, string name, int version, bool frame, IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        AddArtifactHeaders(http.Response);
        if (services.GetService<FleetTaskService>() is not { } fleet)
        {
            return FleetUnavailable();
        }

        var userId = http.GetUserId();
        return await fleet.GetAsync(userId, id, ct) is { } task
            ? await FleetCanvasVersionAsync(fleet, task, name, version, frame, http, ct)
            : Results.NotFound();
    }

    /// <summary>
    /// One canvas page version for the owner or a share link's viewer (ADR 0120). The caller has already put the
    /// sandbox headers on the response and resolved the task, so a miss here carries them too.
    /// </summary>
    private static async Task<IResult> FleetCanvasVersionAsync(
        FleetTaskService fleet, FleetTaskRecord task, string name, int version, bool frame, HttpContext http, CancellationToken ct)
    {
        var page = (await fleet.Store.GetCanvasManifestAsync(task.UserId, task.WorkspaceId, ct)).Pages.FirstOrDefault(p => p.Name == name);
        if (page is null || !page.Versions.Contains(version) || (frame && !ArtifactKind.IsFramed(page.Kind))
            || await fleet.Store.GetCanvasVersionAsync(task.UserId, task.WorkspaceId, page.Name, version, ct) is not { } stored)
        {
            return Results.NotFound();
        }

        var source = Encoding.UTF8.GetString(stored);
        if (!frame)
        {
            return Results.Text(source, "text/plain; charset=utf-8");
        }

        var html = page.Kind == ArtifactKind.Svg ? WrapSvg(source) : source;
        return Results.Text(ArtifactTheme.Apply(html, http.Request.Query["theme"], http.Request.Query["palette"]), "text/html; charset=utf-8");
    }

    /// <summary>True for valid UTF-8 without NUL bytes: such a file is shown as text, anything else is binary.</summary>
    public static bool IsUtf8Text(byte[] content)
    {
        if (Array.IndexOf(content, (byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static async Task<IResult> ListFleetPrompts(IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetTaskService>() is not { } fleet)
        {
            return FleetUnavailable();
        }

        var prompts = await fleet.GetPromptsAsync(http.GetUserId(), ct);
        return Results.Json(FleetPrompts.Definitions.Select(d => new
        {
            key = d.Key,
            title = d.Title,
            description = d.Description,
            placeholders = d.Placeholders,
            @default = d.Default,
            text = prompts.Get(d.Key),
            edited = prompts.IsEdited(d.Key),
        }));
    }

    private static Task<IResult> PutFleetPrompt(string key, FleetPromptRequest request, IServiceProvider services, HttpContext http, CancellationToken ct)
        => SetFleetPrompt(key, request.Text ?? "", services, http, ct);

    private static Task<IResult> ResetFleetPrompt(string key, IServiceProvider services, HttpContext http, CancellationToken ct)
        => SetFleetPrompt(key, null, services, http, ct);

    private static async Task<IResult> SetFleetPrompt(string key, string? text, IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetTaskService>() is not { } fleet)
        {
            return FleetUnavailable();
        }

        var userId = http.GetUserId();
        if (!await fleet.SetPromptAsync(userId, key, text, ct))
        {
            return Results.Json(new { error = $"No prompt '{key}'." }, statusCode: StatusCodes.Status404NotFound);
        }

        var prompts = await fleet.GetPromptsAsync(userId, ct);
        return Results.Json(new { text = prompts.Get(key), edited = prompts.IsEdited(key) });
    }
}
