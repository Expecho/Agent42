using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Uploaded agent skills (ADR 0049): a signed-in user installs their own skills — a standalone
/// <c>SKILL.md</c> or a zip of skill directories — and they merge into the ADR 0048 per-skill picker as
/// ordinary <c>skill:{name}</c> keys.
///
/// <para>Behind Easy Auth like every other <c>/api/*</c> write, and deliberately <b>not</b> on
/// <c>excludedPaths</c> (ADR 0042 — that list stays exactly <c>/api/agent-run</c>, <c>/api/share/*</c>,
/// <c>/shared</c>, <c>/shared/*</c>).</para>
/// </summary>
public static partial class AgentApi
{
    private static void MapUserSkillEndpoints(IEndpointRouteBuilder app)
    {
        // Handler bodies live in named methods below rather than inline lambdas (ADR 0050): cognitive
        // complexity sums a method's nested lambdas, so a registration method accumulates the score of
        // every endpoint it declares even though the endpoints are independent of one another.
        app.MapGet("/api/skills", ListSkillsAsync);
        app.MapPost("/api/skills", UploadSkillAsync).DisableAntiforgery();
        app.MapDelete("/api/skills/{id}", DeleteSkillAsync);
    }

    // The user's installed skills, read from the GRAIN alone (never a blob, never disk) so the list is
    // correct even on a container that has not materialized anything yet.
    private static async Task<IResult> ListSkillsAsync(IGrainFactory grains, HttpContext http)
    {
        var config = await grains.GetGrain<IUserSkillsGrain>(http.GetUserId()).GetAsync();
        return Results.Json(new UserSkillsResponse([.. config.Skills.Select(ToResponse)]));
    }

    // Installs one upload through UserSkillInstaller (ADR 0146: the one install path, shared with the approval of a dream's
    // skill proposal). The endpoint keeps the size and HTTP checks; the installer owns normalize -> cap -> store -> row ->
    // supersede cleanup -> auto-enable.
    //
    // Errors are text/plain on 400, matching the attachment upload, so the browser can render the body as
    // a clean unquoted message. DisableAntiforgery is applied at the registration above: the app is
    // Easy-Auth gated, not cookie/forms.
    //
    // The installer is resolved OPTIONALLY via IServiceProvider rather than taken as a handler parameter, mirroring
    // /api/speech and /api/config-health: MapAgentApi infers every endpoint's parameters up front, and one a host does not
    // register would be treated as a body and break routing for the WHOLE host.
    private static async Task<IResult> UploadSkillAsync(
        IFormFile file, IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return Results.Text("No file was uploaded.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > MaxAttachmentBytes)
        {
            return Results.Text(
                $"Upload exceeds the {MaxAttachmentBytes / (1024 * 1024)} MB limit.",
                "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        if (services.GetService<UserSkillInstaller>() is not { Available: true } installer)
        {
            return Results.Text(
                "Skill uploads are not available on this deployment.",
                "text/plain", statusCode: StatusCodes.Status409Conflict);
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);

        var result = await installer.InstallAsync(http.GetUserId(), file.FileName, buffer.ToArray(), autoEnable: true, ct);
        if (result.Error is { } error)
        {
            return Results.Text(
                error, "text/plain",
                statusCode: result.Unavailable ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
        }

        return Results.Json(new UserSkillUploadResponse(
            [.. result.Stored.Select(ToResponse)],
            result.Skipped,
            ShadowingWarnings(result.Stored)));
    }

    // Removes a skill: the grain row, its package, and — best effort — its materialized directory. A
    // failure to delete the directory must not fail the request; nothing points at it once the row is
    // gone, and the next container restart clears it.
    private static async Task<IResult> DeleteSkillAsync(
        string id, IGrainFactory grains, IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        var userId = http.GetUserId();
        var removed = await grains.GetGrain<IUserSkillsGrain>(userId).DeleteAsync(id);
        if (removed is null)
        {
            return Results.NotFound();
        }

        var packages = services.GetService<ISkillPackageStore>();
        if (packages is not null)
        {
            await packages.DeleteAsync(userId, removed.Id, ct);
        }

        services.GetService<IUserSkillProvider>()?.RemoveMaterialized(removed.Id);
        return Results.NoContent();
    }

    // One warning per shipped skill an upload has replaced. The user chose "uploaded skill wins", so this
    // does not block — but shadowing can REMOVE a control, not just add one: the shipped `websearch`
    // SKILL.md is the only place the ADR 0011 prompt-injection mitigation text lives.
    private static IReadOnlyList<string> ShadowingWarnings(IReadOnlyList<StoredUserSkill> installed)
    {
        var shipped = SkillCatalog.Skills.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return
        [
            .. installed
                .Where(s => shipped.Contains(s.Name))
                .Select(s => $"'{s.Name}' replaces the built-in skill of the same name. The built-in version is no longer used in your chats."),
        ];
    }

    private static UserSkillResponse ToResponse(StoredUserSkill skill)
        => new(skill.Id, skill.Name, skill.Description, skill.Files, skill.UploadedAt);
}
