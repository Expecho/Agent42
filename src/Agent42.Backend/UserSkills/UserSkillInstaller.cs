using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>The outcome of <see cref="UserSkillInstaller.InstallAsync"/>.</summary>
/// <param name="Stored">The rows that installed. Empty when <paramref name="Error"/> is set.</param>
/// <param name="Skipped">One line per dropped zip entry, shown to the user verbatim.</param>
/// <param name="Error">Why nothing installed (a 400 for the upload endpoint), or null.</param>
/// <param name="Unavailable">True when this deployment has no skill package store (a 409 for the upload endpoint).</param>
public sealed record SkillInstallResult(
    IReadOnlyList<StoredUserSkill> Stored, IReadOnlyList<string> Skipped, string? Error, bool Unavailable = false);

/// <summary>
/// The ONE way a skill gets installed for a user (ADR 0049, 0146): the upload endpoint and the approval of a dream's skill
/// proposal both go through <see cref="InstallAsync"/>, so the normalize → cap → store → grain row → supersede cleanup →
/// auto-enable order, and every limit in it, exist once.
///
/// <para>Resolves <see cref="ISkillPackageStore"/> OPTIONALLY through <see cref="IServiceProvider"/> (the <c>/api/speech</c>
/// pattern): a host without one reports <see cref="Available"/> false instead of failing a registration or breaking route
/// matching for the whole host.</para>
/// </summary>
public sealed class UserSkillInstaller(IGrainFactory grains, IServiceProvider services)
{
    /// <summary>Whether this deployment can store skill packages at all.</summary>
    public bool Available => services.GetService<ISkillPackageStore>() is not null;

    /// <summary>
    /// Installs one upload (a bare <c>SKILL.md</c> or a zip). Normalizes into a scratch directory FIRST (nothing durable is
    /// written until the framework has agreed the upload holds a real skill), then stores the package, records the row, cleans
    /// up whatever the row superseded and, when <paramref name="autoEnable"/>, opts the new skills into the default selection.
    /// </summary>
    public async Task<SkillInstallResult> InstallAsync(
        string userId, string fileName, byte[] bytes, bool autoEnable, CancellationToken cancellationToken = default)
    {
        if (services.GetService<ISkillPackageStore>() is not { } packages)
        {
            return new SkillInstallResult([], [], "Skill uploads are not available on this deployment.", Unavailable: true);
        }

        var staging = Path.Combine(Path.GetTempPath(), "agent42-skill-upload-" + Guid.NewGuid().ToString("n"));
        try
        {
            var result = SkillPackage.Normalize(fileName, bytes, staging);
            if (result.Error is { } error)
            {
                return new SkillInstallResult([], result.Skipped, error);
            }

            var grain = grains.GetGrain<IUserSkillsGrain>(userId);

            // Per-user install cap (SkillToolLimits): the 10 MB per-upload limit bounds a single request, not what a user can
            // accumulate on the container filesystem over many of them. Counted against the names that would REMAIN after
            // this upload, so replacing an existing skill is always allowed even at the cap.
            var installed = (await grain.GetAsync()).Skills.Select(s => s.Name);
            var resulting = installed
                .Concat(result.Skills.Select(s => s.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (resulting > SkillToolLimits.MaxInstalledSkillsPerUser)
            {
                return new SkillInstallResult(
                    [], result.Skipped, $"You can install at most {SkillToolLimits.MaxInstalledSkillsPerUser} skills. Delete one first.");
            }

            var stored = new List<StoredUserSkill>();
            foreach (var skill in result.Skills)
            {
                stored.Add(await StoreAsync(userId, grain, packages, skill, cancellationToken));
            }

            if (autoEnable)
            {
                await AutoEnableAsync(userId, stored);
            }

            return new SkillInstallResult(stored, result.Skipped, null);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The current <c>SKILL.md</c> of one of the user's installed skills, or null when it is not installed (or its
    /// package is gone or unreadable).</summary>
    public async Task<string?> ReadSkillMarkdownAsync(string userId, string name, CancellationToken cancellationToken = default)
    {
        if (services.GetService<ISkillPackageStore>() is not { } packages)
        {
            return null;
        }

        var row = (await grains.GetGrain<IUserSkillsGrain>(userId).GetAsync()).Skills
            .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        return row is null || await packages.ReadAsync(userId, row.Id, cancellationToken) is not { } zip
            ? null
            : SkillPackage.ReadSkillMarkdown(zip, row.Name, services.GetService<ILoggerFactory>()?.CreateLogger<UserSkillInstaller>());
    }

    /// <summary>The user's installed skill rows (name, description, id), from the grain alone.</summary>
    public async Task<IReadOnlyList<StoredUserSkill>> ListAsync(string userId)
        => (await grains.GetGrain<IUserSkillsGrain>(userId).GetAsync()).Skills;

    /// <summary>The stored package bytes of an installed skill, or null.</summary>
    public async Task<byte[]?> ReadPackageAsync(string userId, string skillId, CancellationToken cancellationToken = default)
        => services.GetService<ISkillPackageStore>() is { } packages ? await packages.ReadAsync(userId, skillId, cancellationToken) : null;

    private async Task<StoredUserSkill> StoreAsync(
        string userId, IUserSkillsGrain grain, ISkillPackageStore packages, NormalizedSkill skill, CancellationToken cancellationToken)
    {
        // A FRESH id on every upload, including a replace — that is what makes the materialized directory immutable and so
        // needing no cache invalidation (see StoredUserSkill).
        var id = Guid.NewGuid().ToString("n");
        await packages.SaveAsync(userId, id, skill.Zip, cancellationToken);

        var row = new StoredUserSkill(id, skill.Name, skill.Description, skill.Files, DateTimeOffset.UtcNow);
        var superseded = await grain.SaveAsync(row);

        // The grain does no blob or disk IO, so cleaning up what it replaced is this service's job — and only the OLD id can
        // address either the blob or the directory.
        if (superseded is not null)
        {
            await packages.DeleteAsync(userId, superseded.Id, cancellationToken);
            services.GetService<IUserSkillProvider>()?.RemoveMaterialized(superseded.Id);
        }

        return row;
    }

    /// <summary>
    /// Appends the newly installed skills' <c>skill:{name}</c> keys to the user's DEFAULT tool selection — but only when that
    /// selection is non-null.
    ///
    /// <para><b>Why this is needed at all.</b> ADR 0048: the picker materialises "all" as the full explicit list, so a skill
    /// added later is silently OFF for anyone who has ever pressed Save. An uploaded skill <i>is</i> a later-added skill, which
    /// would make the headline user story — upload a skill, use it — do nothing at all for exactly the users who have expressed
    /// a preference. Uploading is an explicit act of wanting the skill, so it opts itself in.</para>
    ///
    /// <para>A <c>null</c> selection is left null: it already means "all", and materialising it here would freeze today's tool
    /// set as the user's explicit choice.</para>
    ///
    /// <para><b>Bounded, deliberately.</b> Per-conversation overrides and per-agent snapshots (ADR 0003/0021) are NOT rewritten
    /// — an existing conversation with a materialised override, and an existing agent, still need a manual tick. The Skills tab
    /// says so.</para>
    /// </summary>
    private async Task AutoEnableAsync(string userId, IReadOnlyList<StoredUserSkill> installed)
    {
        var profileGrain = grains.GetGrain<IUserProfileGrain>(userId);
        var profile = await profileGrain.GetAsync();
        if (profile.EnabledTools is not { } selection)
        {
            return;
        }

        var missing = installed
            .Where(s => !ToolCatalog.IsCompanionSkill(s.Name)) // follows its tool's key; a skill: key would be inert
            .Select(s => ToolCatalog.SkillKey(s.Name))
            .Where(key => !selection.Contains(key, StringComparer.Ordinal))
            .ToList();

        if (missing.Count > 0)
        {
            await profileGrain.SaveAsync(profile with { EnabledTools = [.. selection, .. missing] });
        }
    }
}
