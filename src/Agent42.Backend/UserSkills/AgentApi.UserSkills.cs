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
        // The user's installed skills, read from the GRAIN alone (never a blob, never disk) so the list is
        // correct even on a container that has not materialized anything yet.
        app.MapGet("/api/skills", async (IGrainFactory grains, HttpContext http) =>
        {
            var config = await grains.GetGrain<IUserSkillsGrain>(http.GetUserId()).GetAsync();
            return Results.Json(new UserSkillsResponse([.. config.Skills.Select(ToResponse)]));
        });

        // Installs one upload. Orchestration order matters: normalize into a scratch directory FIRST (nothing
        // durable is written until the framework has agreed the upload contains a real skill), then store the
        // package, then record the row, then clean up whatever the row superseded, then auto-enable.
        //
        // Errors are text/plain on 400, matching the attachment upload, so the browser can render the body as
        // a clean unquoted message. DisableAntiforgery: the app is Easy-Auth gated, not cookie/forms.
        //
        // ISkillPackageStore is resolved OPTIONALLY via IServiceProvider rather than taken as a handler
        // parameter, mirroring /api/speech and /api/config-health: MapAgentApi infers every endpoint's
        // parameters up front, and one a host does not register would be treated as a body and break routing
        // for the WHOLE host. This adds no new mandatory both-hosts registration.
        app.MapPost("/api/skills", async (
            IFormFile file, IGrainFactory grains, IServiceProvider services, HttpContext http, CancellationToken ct) =>
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

            var packages = services.GetService<ISkillPackageStore>();
            if (packages is null)
            {
                return Results.Text(
                    "Skill uploads are not available on this deployment.",
                    "text/plain", statusCode: StatusCodes.Status409Conflict);
            }

            var userId = http.GetUserId();
            var grain = grains.GetGrain<IUserSkillsGrain>(userId);

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);

            var staging = Path.Combine(Path.GetTempPath(), "agent42-skill-upload-" + Guid.NewGuid().ToString("n"));
            SkillPackageResult result;
            try
            {
                result = SkillPackage.Normalize(file.FileName, buffer.ToArray(), staging);
                if (result.Error is { } error)
                {
                    return Results.Text(error, "text/plain", statusCode: StatusCodes.Status400BadRequest);
                }

                // Per-user install cap (SkillToolLimits): the 10 MB per-upload limit bounds a single request,
                // not what a user can accumulate on the container filesystem over many of them. Counted
                // against the names that would REMAIN after this upload, so replacing an existing skill is
                // always allowed even at the cap.
                var installed = (await grain.GetAsync()).Skills.Select(s => s.Name);
                var resulting = installed
                    .Concat(result.Skills.Select(s => s.Name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                if (resulting > SkillToolLimits.MaxInstalledSkillsPerUser)
                {
                    return Results.Text(
                        $"You can install at most {SkillToolLimits.MaxInstalledSkillsPerUser} skills. Delete one first.",
                        "text/plain", statusCode: StatusCodes.Status400BadRequest);
                }

                var stored = new List<StoredUserSkill>();
                foreach (var skill in result.Skills)
                {
                    // A FRESH id on every upload, including a replace — that is what makes the materialized
                    // directory immutable and so needing no cache invalidation (see StoredUserSkill).
                    var id = Guid.NewGuid().ToString("n");
                    await packages.SaveAsync(userId, id, skill.Zip, ct);

                    var row = new StoredUserSkill(id, skill.Name, skill.Description, skill.Files, DateTimeOffset.UtcNow);
                    var superseded = await grain.SaveAsync(row);
                    stored.Add(row);

                    // The grain does no blob or disk IO, so cleaning up what it replaced is this handler's
                    // job — and only the OLD id can address either the blob or the directory.
                    if (superseded is not null)
                    {
                        await packages.DeleteAsync(userId, superseded.Id, ct);
                        services.GetService<IUserSkillProvider>()?.RemoveMaterialized(superseded.Id);
                    }
                }

                await AutoEnableUploadedSkillsAsync(grains, userId, stored);

                return Results.Json(new UserSkillUploadResponse(
                    [.. stored.Select(ToResponse)],
                    result.Skipped,
                    ShadowingWarnings(stored)));
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
            }
        }).DisableAntiforgery();

        // Removes a skill: the grain row, its package, and — best effort — its materialized directory. A
        // failure to delete the directory must not fail the request; nothing points at it once the row is
        // gone, and the next container restart clears it.
        app.MapDelete("/api/skills/{id}", async (
            string id, IGrainFactory grains, IServiceProvider services, HttpContext http, CancellationToken ct) =>
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
        });
    }

    /// <summary>
    /// Appends the newly installed skills' <c>skill:{name}</c> keys to the user's DEFAULT tool selection —
    /// but only when that selection is non-null.
    ///
    /// <para><b>Why this is needed at all.</b> ADR 0048: the picker materialises "all" as the full explicit
    /// list, so a skill added later is silently OFF for anyone who has ever pressed Save. An uploaded skill
    /// <i>is</i> a later-added skill, which would make the headline user story — upload a skill, use it — do
    /// nothing at all for exactly the users who have expressed a preference. Uploading is an explicit act of
    /// wanting the skill, so it opts itself in.</para>
    ///
    /// <para>A <c>null</c> selection is left null: it already means "all", and materialising it here would
    /// freeze today's tool set as the user's explicit choice.</para>
    ///
    /// <para><b>Bounded, deliberately.</b> Per-conversation overrides and per-agent snapshots (ADR
    /// 0003/0021) are NOT rewritten — an existing conversation with a materialised override, and an existing
    /// agent, still need a manual tick. The Skills tab says so.</para>
    /// </summary>
    private static async Task AutoEnableUploadedSkillsAsync(
        IGrainFactory grains, string userId, IReadOnlyList<StoredUserSkill> installed)
    {
        var profileGrain = grains.GetGrain<IUserProfileGrain>(userId);
        var profile = await profileGrain.GetAsync();
        if (profile.EnabledTools is not { } selection)
        {
            return;
        }

        var missing = installed
            .Select(s => ToolCatalog.SkillKey(s.Name))
            .Where(key => !selection.Contains(key, StringComparer.Ordinal))
            .ToList();

        if (missing.Count > 0)
        {
            await profileGrain.SaveAsync(profile with { EnabledTools = [.. selection, .. missing] });
        }
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
