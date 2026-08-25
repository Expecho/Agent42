using Microsoft.Agents.AI;

namespace Agent42.Agents;

/// <summary>
/// One shipped agent skill, discovered on disk under <c>Skills/</c>: its frontmatter <c>name</c> (the
/// selection key's suffix and the picker's label), its frontmatter <c>description</c>, and the directory
/// that declares it — what <see cref="AgentSkillsProvider"/> is pointed at to load exactly that one skill.
/// </summary>
public sealed record SkillInfo(string Name, string Description, string Path);

/// <summary>
/// The catalog of skills shipped with this deployment, discovered once per process from the
/// <c>Skills/</c> folder next to the built assembly (the <c>Agent42.Agents.csproj</c> Content glob puts
/// it there). This is what makes a skill INDIVIDUALLY selectable: the tool picker renders one
/// <c>skill:{name}</c> checkbox per entry, and <c>BuildAgent</c> points the provider at only the
/// selected entries' <see cref="SkillInfo.Path"/>s.
///
/// <para><b>The SHIPPED layer, not the whole picture (ADR 0049).</b> This set is per-deployment — skills
/// ship with the image, so every user sees the same ones — but it is now the <i>base</i> of a per-user
/// overlay: <c>AgentGrain.MergedSkills()</c> combines it with the skills that user has uploaded
/// (<c>IUserSkillProvider</c>), an upload of the same name <b>shadowing</b> the shipped one, and it is the
/// merged list that reaches both the picker and the <c>BuildAgent</c> gate. Keep this class shipped-only and
/// read-only: its <see cref="Lazy{T}"/> is process-wide, so putting a user's uploads in here would serve
/// them to everyone.</para>
///
/// <para><b>Discovery must never fail the host.</b> A missing or unreadable <c>Skills/</c> folder, or one
/// malformed <c>SKILL.md</c>, must yield a shorter list rather than an exception — a broken skill file must
/// not take down every conversation on the silo. That guarantee is load-bearing here because
/// <see cref="Lazy{T}"/> <b>caches a thrown exception</b>: one throw out of <see cref="Discover"/> would
/// rethrow on every subsequent touch for the life of the process, so <c>BuildAgent</c> would fail for every
/// conversation, not just lose a skill. Hence the whole body is guarded.</para>
/// </summary>
public static class SkillCatalog
{
    // Lazy, not a static field initializer, so the disk read happens on first USE (and once), not at
    // type load. Task.Run — see the comment in Discover: the blocking wait must not run on a grain's
    // activation TaskScheduler.
    private static readonly Lazy<IReadOnlyList<SkillInfo>> Lazy =
        new(() => Discover(System.IO.Path.Combine(AppContext.BaseDirectory, "Skills")));

    /// <summary>The shipped skills, ordered by name (ordinal) so the picker is stable across processes.</summary>
    public static IReadOnlyList<SkillInfo> Skills => Lazy.Value;

    /// <summary>
    /// Discovers the skills under <paramref name="root"/> in a single enumeration.
    ///
    /// <para><b>The (directory ↔ name) pairing comes from the framework, not from us.</b>
    /// <see cref="AgentFileSkillsSource"/> yields <see cref="AgentFileSkill"/>, whose public
    /// <see cref="AgentFileSkill.Path"/> is the directory the skill was discovered in — so one enumeration
    /// of the root gives both halves of the pair (measured against Microsoft.Agents.AI 1.13.0), and a
    /// per-directory loop would be redundant rather than merely different.</para>
    ///
    /// <para><b>Two measured limits, one of which corrects what this comment used to say (ADR 0049).</b>
    /// A skill is recorded under its OWN directory from any root that reaches it — an earlier version of
    /// this comment warned that a nested <c>Skills/category/foo/SKILL.md</c> "would be recorded under
    /// <c>Skills/category</c>" if each subdirectory were scanned as a root, and that is simply not what the
    /// source does. The real constraint is depth: <c>DefaultSearchDepth = 2</c> and
    /// <c>MaxSkillDirectorySearchDepth = 2</c>, and <c>AgentFileSkillsSourceOptions.SearchDepth</c> does
    /// <b>not</b> raise it (3 and 9 measure identical to the default; 0 throws) — it governs the
    /// resource/script scan only. So <c>Skills/category/foo/SKILL.md</c> IS found from the root, while
    /// <c>Skills/a/b/foo/SKILL.md</c> is invisible, silently. Pointing this method AT a single skill's own
    /// directory returns exactly that one skill, which is what makes per-skill selection (ADR 0048) and
    /// uploaded-skill materialization (ADR 0049) work.</para>
    ///
    /// <para><b>Why the <see cref="Task.Run"/> bridge:</b> <c>GetSkillsAsync</c> is async and the first touch
    /// of <see cref="Skills"/> is nondeterministic — it can easily be <c>BuildAgent</c> on a grain thread
    /// rather than the <c>/api/tools</c> handler. Orleans grains have no <c>SynchronizationContext</c> but DO
    /// run on the activation <c>TaskScheduler</c>, which <c>await</c> captures, so a bare
    /// <c>.GetAwaiter().GetResult()</c> on a continuation scheduled back onto that same activation deadlocks
    /// it — a bricked conversation, not a slow one. It happens to be safe today only because 1.13.0's
    /// <c>GetSkillsAsync</c> completes synchronously, which is an implementation detail of a pinned package.
    /// Do not "simplify" this away.</para>
    ///
    /// <para>Public rather than internal because this repo has no <c>InternalsVisibleTo</c> (the same reason
    /// <c>ChatClientFactory.ApiKeyHeaderPolicy</c> is public): the tolerance guarantees above are only
    /// assertable if a test can point this at a directory it controls.</para>
    /// </summary>
    public static IReadOnlyList<SkillInfo> Discover(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            // The context is passed as a plain null: AgentFileSkillsSource ignores it today, and
            // `new AgentSkillsSourceContext(null!, null!)` THROWS ArgumentNullException(agent), so one cannot
            // be constructed here. That tolerance is undocumented, which is one more reason the whole call
            // sits inside this try — if a later version starts dereferencing it, the picker loses its skills
            // instead of the host failing to start.
            var skills = Task.Run(() => new AgentFileSkillsSource(root).GetSkillsAsync(default!))
                .GetAwaiter().GetResult();

            return
            [
                .. skills
                    .OfType<AgentFileSkill>()
                    .Where(s => !string.IsNullOrWhiteSpace(s.Frontmatter.Name))
                    .Select(s => new SkillInfo(s.Frontmatter.Name, s.Frontmatter.Description ?? "", s.Path))
                    .OrderBy(s => s.Name, StringComparer.Ordinal)
                    // ThenBy(Path) is NOT cosmetic — it is what makes the de-dupe below deterministic.
                    // Two skills CAN share a frontmatter name once directories nest (a `foo` skill under
                    // two different category directories), and two identical checkboxes addressing an
                    // ambiguous gate is worse than losing one. But "first wins" is only meaningful if
                    // "first" is well defined, and the input order here is the FILESYSTEM's: NTFS and ext4
                    // enumerate differently, so ordering on Name alone picks a different winner per
                    // platform. CI caught exactly that — green on Windows, red on Linux. Sorting on Path
                    // pins the winner to the lowest path, ordinal, everywhere.
                    .ThenBy(s => s.Path, StringComparer.Ordinal)
                    .DistinctBy(s => s.Name, StringComparer.Ordinal),
            ];
        }
        catch
        {
            // A malformed SKILL.md, an unreadable directory, or an unreadable root must cost the skills,
            // never the host — and never permanently, via Lazy's cached-exception behaviour (class doc).
            return [];
        }
    }
}
