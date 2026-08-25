using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The real <see cref="IUserSkillProvider"/> (ADR 0049): reads a user's overlay, materializes each installed
/// skill's zip onto the local filesystem, and hands back <see cref="SkillInfo"/> entries the grain merges
/// with <see cref="SkillCatalog"/>. Cached by the overlay <b>version</b> the grain bumps on every save, the
/// <see cref="IUserMcpToolProvider"/> shape — an activation on an unchanged overlay reuses the cached list;
/// an upload bumps the version, so the next grain activation re-resolves and the change takes effect with no
/// app restart (in-flight grains pick it up on their next activation — accepted staleness, and the reason
/// the UI says "takes effect on your next new chat").
///
/// <para><b>Why <see cref="Path.GetTempPath"/> and never next to the shipped skills.</b>
/// <c>src/Agent42.Backend/Dockerfile</c> ends with <c>USER $APP_UID</c>, so the app runs non-root and
/// <c>/app</c> — hence <c>AppContext.BaseDirectory/Skills</c>, where <see cref="SkillCatalog"/> reads — is
/// not writable. Writing uploads there would fail in production while working on a dev box, and would also
/// make <see cref="SkillCatalog"/>'s process-wide cache serve one user's uploads to everyone.</para>
///
/// <para><b>The directory is immutable, so there is no cache invalidation.</b> A fresh skill id is minted on
/// every upload, including a replace, so <c>{temp}/agent42-skills/{skillId}/{name}/</c> either exists with
/// exactly the right content or does not exist at all. Cleanup is the endpoint's job (a delete or a replace
/// calls <see cref="RemoveMaterialized"/>); beyond that the tree is bounded only by container restarts,
/// which is a deliberately accepted property rather than an eviction scheme.</para>
///
/// <para><b>Best-effort throughout.</b> A missing or corrupt package costs that one skill and never throws:
/// this runs inside <c>AgentGrain.OnActivateAsync</c>, so a throw would fail the activation and take out the
/// conversation, not just the skill.</para>
///
/// <para>Unlike <c>UserMcpToolProvider</c> this lives in the Agents project rather than the Backend: it
/// needs no Backend-only type (just <see cref="IGrainFactory"/> and <see cref="ISkillPackageStore"/>), and
/// keeping it here lets DevHost and the tests run the real materialize→discover path over
/// <see cref="InMemorySkillPackageStore"/> instead of a stub. There is deliberately <b>no</b>
/// <c>SingleUser…</c> offline analogue (the shape <see cref="IUserMcpToolProvider"/> needs, because an MCP
/// connection cannot be faked with a file): every host registers this one, so uploading a skill in DevHost
/// exercises the same normalize → store → materialize → discover path production runs.</para>
/// </summary>
public sealed class UserSkillProvider : IUserSkillProvider
{
    private readonly IGrainFactory _grains;
    private readonly ISkillPackageStore _packages;
    private readonly ILogger<UserSkillProvider> _logger;

    private readonly ConcurrentDictionary<string, UserCache> _caches = new(StringComparer.Ordinal);

    public UserSkillProvider(IGrainFactory grains, ISkillPackageStore packages, ILogger<UserSkillProvider> logger)
    {
        _grains = grains;
        _packages = packages;
        _logger = logger;
    }

    /// <summary>
    /// The root every uploaded skill is materialized under. Public so the endpoint and the tests can reason
    /// about it; the per-skill directory below it is <c>{skillId}/{name}/</c>.
    /// </summary>
    public static string MaterializationRoot { get; } = Path.Combine(Path.GetTempPath(), "agent42-skills");

    public async Task<UserSkillSet> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var version = await _grains.GetGrain<IUserSkillsGrain>(userId).GetVersionAsync();
        var cache = _caches.GetOrAdd(userId, _ => new UserCache());

        // Serialize per user so two concurrent activations don't both unpack the same zips over each other;
        // a cache hit on the current version returns without touching storage or disk.
        await cache.Gate.WaitAsync(cancellationToken);
        try
        {
            if (cache.Version == version && cache.Set is { } hit)
            {
                return hit;
            }

            var config = await _grains.GetGrain<IUserSkillsGrain>(userId).GetAsync();
            var skills = new List<SkillInfo>();
            foreach (var stored in config.Skills)
            {
                if (await MaterializeAsync(userId, stored, cancellationToken) is { } info)
                {
                    skills.Add(info);
                }
            }

            var set = new UserSkillSet(skills, version);
            cache.Version = version;
            cache.Set = set;
            return set;
        }
        finally
        {
            cache.Gate.Release();
        }
    }

    public void RemoveMaterialized(string skillId)
    {
        try
        {
            var directory = Path.Combine(MaterializationRoot, skillId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale directory is harmless — nothing points at it once the grain row is gone, and the next
            // container restart clears it. Failing the user's delete over it would not be.
            _logger.LogWarning(ex, "Could not remove the materialized directory for skill {SkillId}.", skillId);
        }
    }

    // Unpacks one skill's package if it is not already on disk, then asks the framework about the result.
    // Returns null — never throws — when anything goes wrong, so the skill is simply absent.
    private async Task<SkillInfo?> MaterializeAsync(
        string userId, StoredUserSkill stored, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(MaterializationRoot, stored.Id);
        var skillDirectory = Path.Combine(directory, stored.Name);

        try
        {
            if (!Directory.Exists(skillDirectory))
            {
                var zip = await _packages.ReadAsync(userId, stored.Id, cancellationToken);
                if (zip is null)
                {
                    _logger.LogWarning(
                        "Skill package {SkillId} for user {User} is missing from storage; skipping it.",
                        stored.Id, userId);
                    return null;
                }

                Extract(zip, directory);
            }

            // Discover pointed AT the skill's own directory returns exactly that one skill — the same
            // per-skill mechanism the shipped catalog uses — and returns nothing if the tree is not a valid
            // skill after all, which is the tolerance this method needs anyway.
            return SkillCatalog.Discover(skillDirectory).FirstOrDefault();
        }
        catch (Exception ex)
        {
            // Best-effort: a corrupt package, a full disk or a permission problem costs this one skill.
            // Throwing here would fail AgentGrain.OnActivateAsync and brick the conversation.
            _logger.LogWarning(ex, "Could not materialize skill {SkillId} for user {User}; skipping it.", stored.Id, userId);
            TryDeletePartial(directory);
            return null;
        }
    }

    private static void Extract(byte[] zip, string directory)
    {
        using var stream = new MemoryStream(zip, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        // ExtractToDirectory refuses to write outside the destination, so it is a second guard behind
        // SkillPackage's — cheap, and this reads bytes that were written a deployment ago.
        archive.ExtractToDirectory(directory, overwriteFiles: true);
    }

    // A half-extracted directory would be treated as materialized on the next pass (the existence check) and
    // silently serve a truncated skill, so a failed extraction leaves nothing behind.
    private void TryDeletePartial(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not clean up the partially materialized directory {Directory}.", directory);
        }
    }

    private sealed class UserCache
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int Version { get; set; } = -1;

        public UserSkillSet? Set { get; set; }
    }
}
