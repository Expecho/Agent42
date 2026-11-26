using System.IO.Compression;
using System.Text;
using Microsoft.Agents.AI;

namespace Agent42.Agents;

/// <summary>One skill successfully normalized out of an upload, ready to be stored and materialized.</summary>
/// <param name="Name">The frontmatter <c>name</c> — the picker key's suffix and the normalized zip's single
/// top-level directory. Guaranteed to satisfy <see cref="SkillPackage.IsSafeSkillName"/>.</param>
/// <param name="Description">The frontmatter <c>description</c>, as the framework parsed it.</param>
/// <param name="Zip">The normalized zip: exactly one top-level directory, named <paramref name="Name"/>.</param>
/// <param name="Files">The paths inside the zip, relative to the skill directory (e.g. <c>references/notes.md</c>).</param>
public sealed record NormalizedSkill(string Name, string Description, byte[] Zip, IReadOnlyList<string> Files);

/// <summary>
/// The outcome of normalizing one upload: the skills that installed, a human-readable report of everything
/// that was deliberately dropped, and — when nothing installed — why.
/// </summary>
/// <param name="Skills">The skills to install. Empty when <paramref name="Error"/> is set.</param>
/// <param name="Skipped">One line per dropped entry or shadowed duplicate, surfaced to the user verbatim.</param>
/// <param name="Error">Non-null when the upload yielded no installable skill; the 400 body.</param>
public sealed record SkillPackageResult(
    IReadOnlyList<NormalizedSkill> Skills,
    IReadOnlyList<string> Skipped,
    string? Error);

/// <summary>
/// Normalizes an uploaded agent skill — a standalone <c>SKILL.md</c>, or a zip containing one or more skill
/// directories with bundled resources — into <b>one zip per skill whose single top-level directory is the
/// skill's frontmatter name</b>. One storage format, one materialization path, one test surface.
///
/// <para><b>The framework is the authority on what a valid skill is.</b> Everything reaching disk is handed
/// to <see cref="SkillCatalog.Discover"/>, which runs <c>AgentFileSkillsSource</c> — the same parser that
/// will later load the skill. So the name/description come from the framework, and its rules (frontmatter
/// <c>name</c> must equal the directory name; the name must match
/// <c>^[a-z0-9]([a-z0-9]*-[a-z0-9])*[a-z0-9]*$</c>, ≤ 64 chars) are enforced by the code that enforces them
/// at load time, not by a second parser that can drift.</para>
///
/// <para><b>The one exception is a security boundary.</b> A bare <c>.md</c> upload has no directory yet, so
/// that path — and only that path — scans the <c>name:</c> frontmatter line itself to choose one. That value
/// is validated by <see cref="IsSafeSkillName"/> <b>before it touches the filesystem</b>: writing
/// <c>{staging}/{name}/SKILL.md</c> first would make <c>name: ../../../../foo</c> a path-traversal write and,
/// on Windows, <c>name: foo:bar</c> an alternate-data-stream write, before any validator ran.</para>
///
/// <para><b>Why the staging tree is searched rather than simply discovered.</b> Measured against
/// Microsoft.Agents.AI 1.13.0: <c>AgentFileSkillsSource</c> hard-caps its skill-directory search at
/// <c>MaxSkillDirectorySearchDepth = 2</c> and <c>AgentFileSkillsSourceOptions.SearchDepth</c> does
/// <b>not</b> raise it (3 and 9 measured identical to the default). So a skill under two wrapper directories
/// — a zipped parent folder, or a macOS-produced archive — yields <b>nothing</b> at all. This class therefore
/// walks the staging tree for <c>SKILL.md</c> itself and points <c>Discover</c> <b>at each skill's own
/// directory</b>, which is exactly the per-skill filtering mechanism ADR 0048 already relies on.</para>
/// </summary>
public static class SkillPackage
{
    /// <summary>The file that declares a skill. Matched ordinally so behaviour is identical on NTFS and ext4.</summary>
    public const string SkillFileName = "SKILL.md";

    /// <summary>
    /// The file extensions the framework will actually surface as a readable resource (measured against
    /// Microsoft.Agents.AI 1.13.0: <c>AgentFileSkillsSource.s_defaultResourceExtensions</c>). Anything else is
    /// dropped: storing bytes the model can never read is pure cost — blob storage, container disk and the
    /// user's 10 MB upload budget — with no upside.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedResourceExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md", ".json", ".yaml", ".yml", ".csv", ".xml", ".txt" };

    /// <summary>
    /// The extensions the framework classifies as a <b>script</b> (measured:
    /// <c>AgentFileSkillsSource.s_defaultScriptExtensions</c>). Dropping these is a security control, not
    /// hygiene — see the class docs on <c>run_skill_script</c>.
    ///
    /// <para><b>Classification is by extension, at any depth — folder names are irrelevant.</b> Measured:
    /// a <c>references/code.py</c> is a SCRIPT and a <c>docs/other.md</c> is a RESOURCE; the
    /// <c>scripts/</c> and <c>references/</c> folder names carry no meaning to the parser. A filter keyed on
    /// the <c>scripts/</c> folder would let a <c>.py</c> under <c>references/</c> straight through.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> ScriptExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py", ".js", ".sh", ".ps1", ".cs", ".csx" };

    /// <summary>
    /// How many directory levels below a skill's own directory the framework still scans for resources
    /// (measured: <c>AgentFileSkillsSource.DefaultSearchDepth = 2</c> yields <c>top.md</c> and
    /// <c>L0/one.md</c> but not <c>L0/L1/two.md</c>). A file deeper than this is stored-but-unreadable, so it
    /// is dropped for the same reason a disallowed extension is.
    /// </summary>
    public const int MaxResourceDepth = 1;

    // Windows reserved device names. The framework's own name rule does NOT exclude them — measured:
    // ValidateName("con"), ("aux") and ("com1") all return true — yet creating a directory so named fails or
    // misbehaves on Windows (dev and CI) while succeeding on Linux (prod). Same platform-dependence class as
    // the de-dupe tie-break that ADR 0048's follow-up commit had to fix.
    private static readonly IReadOnlySet<string> ReservedDeviceNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

    /// <summary>
    /// Normalizes one upload into installable skills under <paramref name="stagingRoot"/> (a caller-owned
    /// scratch directory it is expected to delete afterwards). Never throws for bad input: a malformed
    /// upload comes back as <see cref="SkillPackageResult.Error"/>.
    /// </summary>
    public static SkillPackageResult Normalize(string fileName, byte[] bytes, string stagingRoot)
    {
        var skipped = new List<string>();
        var extension = Path.GetExtension(fileName);

        try
        {
            Directory.CreateDirectory(stagingRoot);

            if (string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase))
            {
                if (StageBareMarkdown(bytes, stagingRoot) is { } error)
                {
                    return new SkillPackageResult([], skipped, error);
                }
            }
            else if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (ExtractZip(bytes, stagingRoot, skipped) is { } error)
                {
                    return new SkillPackageResult([], skipped, error);
                }
            }
            else
            {
                return new SkillPackageResult([], skipped, "Upload a SKILL.md file or a .zip containing one or more skill directories.");
            }

            var skills = CollectSkills(stagingRoot, skipped);
            return skills.Count == 0
                ? new SkillPackageResult([], skipped, "No valid skill was found. A skill needs a SKILL.md whose frontmatter 'name' matches its directory name.")
                : new SkillPackageResult(skills, skipped, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return new SkillPackageResult([], skipped, "The upload could not be read. Expected a SKILL.md file or a valid .zip archive.");
        }
    }

    /// <summary>
    /// True when a skill name is safe to use as a directory name on <b>both</b> Windows and Linux, on top of
    /// the framework's own rule. Public because the endpoint validates the bare-<c>.md</c> frontmatter name
    /// with it before anything is written.
    /// </summary>
    public static bool IsSafeSkillName(string? name)
        => !string.IsNullOrWhiteSpace(name)
            && AgentSkillFrontmatter.ValidateName(name, out _)
            && !ReservedDeviceNames.Contains(name)
            // Belt and braces: the framework's regex already excludes all of these, but this predicate is what
            // stands between an upload and a filesystem write, so it does not delegate its whole answer.
            && name.IndexOfAny(['/', '\\', ':', '.', ' ']) < 0
            && name.Length <= 64;

    /// <summary>
    /// The text of <c>{name}/SKILL.md</c> in a stored (normalized) skill zip, or null when the zip has no such entry or
    /// cannot be read. Read-only: the zip is one this process wrote, but a corrupt blob must not throw into a caller.
    /// </summary>
    public static string? ReadSkillMarkdown(byte[] zip, string name)
    {
        try
        {
            using var stream = new MemoryStream(zip, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = archive.GetEntry($"{name}/{SkillFileName}");
            if (entry is null)
            {
                return null;
            }

            using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false));
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rebuilds a stored skill zip with <c>{name}/SKILL.md</c> replaced by <paramref name="markdown"/> and every other
    /// entry copied untouched, in the same <c>{name}/...</c> layout <see cref="Normalize"/> produces. Returns null when the
    /// zip cannot be read or has no <c>SKILL.md</c> to replace.
    /// </summary>
    public static byte[]? ReplaceSkillMarkdown(byte[] zip, string name, string markdown)
    {
        var target = $"{name}/{SkillFileName}";
        try
        {
            using var source = new MemoryStream(zip, writable: false);
            using var input = new ZipArchive(source, ZipArchiveMode.Read);
            if (input.GetEntry(target) is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            using (var output = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var entry in input.Entries)
                {
                    var copy = output.CreateEntry(entry.FullName);
                    using var to = copy.Open();
                    if (entry.FullName == target)
                    {
                        to.Write(new UTF8Encoding(false).GetBytes(markdown));
                    }
                    else
                    {
                        using var from = entry.Open();
                        from.CopyTo(to);
                    }
                }
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    // ---- bare .md ------------------------------------------------------------------------------------

    // Scans the frontmatter for `name:`, validates it, and only THEN creates {staging}/{name}/SKILL.md.
    // The ordering is the security boundary described in the class docs.
    private static string? StageBareMarkdown(byte[] bytes, string stagingRoot)
    {
        var text = new UTF8Encoding(false).GetString(bytes);
        var name = ScanFrontmatterName(text);

        if (!IsSafeSkillName(name))
        {
            return "The SKILL.md frontmatter must declare a 'name' of lowercase letters, digits and hyphens (max 64 characters).";
        }

        var dir = Path.Combine(stagingRoot, name!);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, SkillFileName), bytes);
        return null;
    }

    // A deliberately minimal frontmatter scan: enough to pick a directory name, never enough to be a second
    // parser. Whatever it picks is re-parsed by the framework once the file is on disk, and a disagreement
    // simply means the skill is not discovered.
    private static string? ScanFrontmatterName(string text)
    {
        using var reader = new StringReader(text);
        var line = reader.ReadLine();
        if (line is null || !line.TrimStart('\uFEFF').Trim().Equals("---", StringComparison.Ordinal))
        {
            return null;
        }

        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Trim().Equals("---", StringComparison.Ordinal))
            {
                return null;
            }

            if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                return line["name:".Length..].Trim().Trim('"', '\'');
            }
        }

        return null;
    }

    // ---- zip -----------------------------------------------------------------------------------------

    private static string? ExtractZip(byte[] bytes, string stagingRoot, List<string> skipped)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        if (archive.Entries.Count > SkillToolLimits.MaxZipEntries)
        {
            return $"The archive has {archive.Entries.Count} entries; the limit is {SkillToolLimits.MaxZipEntries}.";
        }

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        foreach (var entry in archive.Entries)
        {
            if (ClassifyEntry(entry.FullName) is not { } relative)
            {
                // ClassifyEntry reports nothing for directory entries; everything else it rejects is worth
                // telling the user about, since "my file vanished" is otherwise inexplicable.
                if (!IsDirectoryEntry(entry.FullName))
                {
                    skipped.Add($"{entry.FullName} — {RejectionReason(entry.FullName)}");
                }

                continue;
            }

            // Duplicate entry names are legal in a zip. First wins, by archive order, so the winner is the
            // same on every platform; ExtractToFile would otherwise throw or silently last-win.
            if (!written.Add(relative))
            {
                skipped.Add($"{entry.FullName} — a duplicate entry name; the first one was kept");
                continue;
            }

            var destination = Path.Combine(stagingRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            var copied = CopyBounded(entry, destination, SkillToolLimits.MaxEntryBytes);
            if (copied < 0)
            {
                File.Delete(destination);
                written.Remove(relative);
                skipped.Add($"{entry.FullName} — larger than the {SkillToolLimits.MaxEntryBytes / (1024 * 1024)} MB per-file limit");
                continue;
            }

            total += copied;
            if (total > SkillToolLimits.MaxTotalUncompressedBytes)
            {
                return $"The archive expands to more than {SkillToolLimits.MaxTotalUncompressedBytes / (1024 * 1024)} MB.";
            }
        }

        return null;
    }

    // Copies at most `limit` bytes and returns the count, or -1 if the entry was longer. Bounds the bytes
    // ACTUALLY COPIED rather than trusting ZipArchiveEntry.Length, which is attacker-controlled metadata a
    // zip bomb lies about.
    private static long CopyBounded(ZipArchiveEntry entry, string destination, long limit)
    {
        using var source = entry.Open();
        using var target = File.Create(destination);

        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += read;
            if (copied > limit)
            {
                return -1;
            }

            target.Write(buffer, 0, read);
        }

        return copied;
    }

    /// <summary>
    /// The single entry-classification predicate: the sanitized relative path to extract to, or null to drop
    /// the entry. Split out of <see cref="ExtractZip"/> so both stay readable and testable.
    /// </summary>
    private static string? ClassifyEntry(string entryName)
    {
        if (IsDirectoryEntry(entryName))
        {
            return null;
        }

        // Zip entry names are '/'-separated by spec, but real archivers emit '\' too.
        var segments = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(IsHostileSegment))
        {
            return null;
        }

        // Rooted or drive-qualified names (zip-slip's other half): "/etc/passwd", "C:\x".
        if (entryName.StartsWith('/') || entryName.StartsWith('\\') || Path.IsPathRooted(entryName) || entryName.Contains(':'))
        {
            return null;
        }

        var file = segments[^1];
        if (!string.Equals(file, SkillFileName, StringComparison.Ordinal)
            && !AllowedResourceExtensions.Contains(Path.GetExtension(file)))
        {
            return null;
        }

        return Path.Combine(segments);
    }

    // Why an entry was dropped, phrased for the user. Kept beside ClassifyEntry so the two cannot drift.
    private static string RejectionReason(string entryName)
    {
        var segments = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var file = segments.Length > 0 ? segments[^1] : entryName;
        var extension = Path.GetExtension(file);

        if (segments.Any(IsHostileSegment) || Path.IsPathRooted(entryName) || entryName.Contains(':'))
        {
            return "its path is not allowed";
        }

        return ScriptExtensions.Contains(extension)
            ? "scripts are not supported and are never stored"
            : $"'{(extension.Length == 0 ? file : extension)}' is not a supported resource type";
    }

    private static bool IsDirectoryEntry(string entryName)
        => entryName.EndsWith('/') || entryName.EndsWith('\\');

    private static bool IsHostileSegment(string segment)
        => segment is "." or ".."
            // `con.md` IS the CON device on Windows — the extension does not save it.
            || ReservedDeviceNames.Contains(Path.GetFileNameWithoutExtension(segment))
            || segment.EndsWith('.')
            || segment.EndsWith(' ')
            || segment.IndexOfAny(InvalidNameChars) >= 0;

    // Deliberately NOT Path.GetInvalidFileNameChars(): that returns the Windows set on Windows and only
    // { '\0', '/' } on Linux, so the same archive would be accepted in production and rejected in CI. This
    // fixed superset makes the answer identical on both — the same platform-independence rule the skill
    // de-dupe tie-break already follows.
    private static readonly char[] InvalidNameChars =
        ['\0', '/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    // ---- discovery -----------------------------------------------------------------------------------

    // Walks the staged tree for SKILL.md files and asks the framework about each one's own directory. See the
    // class docs for why this is not one Discover(stagingRoot) call.
    private static List<NormalizedSkill> CollectSkills(string stagingRoot, List<string> skipped)
    {
        var candidates = Directory
            .EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories)
            // Ordinal on purpose: the framework matches SKILL.md ordinally, so a `skill.md` that NTFS would
            // happily serve must not be discovered here either — or the upload would behave differently in
            // dev and in production.
            .Where(p => string.Equals(Path.GetFileName(p), SkillFileName, StringComparison.Ordinal))
            .Select(p => Path.GetDirectoryName(p)!)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var skills = new List<NormalizedSkill>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in candidates)
        {
            // Discover pointed AT the skill directory returns exactly that one skill — and returns nothing
            // when the frontmatter name disagrees with the directory name, or fails the framework's name rule.
            if (SkillCatalog.Discover(directory).FirstOrDefault() is not { } info || !IsSafeSkillName(info.Name))
            {
                skipped.Add($"{Path.GetFileName(directory)}/{SkillFileName} — not a valid skill (its frontmatter 'name' must match its directory name and use lowercase letters, digits and hyphens)");
                continue;
            }

            // OrdinalIgnoreCase because a case-only difference is one directory on NTFS and two on ext4.
            // Candidates are already ordered by path, so the winner is the same on both.
            if (!seen.Add(info.Name))
            {
                skipped.Add($"{info.Name} — the archive declares this skill more than once; the first was kept");
                continue;
            }

            var files = CollectFiles(directory, info.Name, skipped);
            skills.Add(new NormalizedSkill(info.Name, info.Description, BuildZip(info.Name, directory, files), files));
        }

        return skills;
    }

    // The files to carry into the normalized zip: SKILL.md plus every allowed resource the framework can
    // actually reach. The extension gate already ran during extraction; what is left to apply here is the
    // DEPTH gate, which is relative to the skill's own directory and so is unknowable until it is found.
    private static IReadOnlyList<string> CollectFiles(string skillDirectory, string name, List<string> skipped)
    {
        var kept = new List<string>();

        foreach (var path in Directory.EnumerateFiles(skillDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(skillDirectory, path).Replace('\\', '/');
            if (string.Equals(relative, SkillFileName, StringComparison.Ordinal))
            {
                kept.Add(relative);
            }
            else if (relative.Count(c => c == '/') > MaxResourceDepth)
            {
                skipped.Add($"{name}/{relative} — nested too deeply; the framework only reads resources one folder below SKILL.md");
            }
            else if (kept.Count >= SkillToolLimits.MaxFilesPerSkill)
            {
                // Every kept path is persisted verbatim into the user-skills grain item, which shares the
                // 2 MB Cosmos cap (ADR 0004) — so the file COUNT needs a bound of its own, separate from
                // the byte caps that bound the upload. See SkillToolLimits.MaxFilesPerSkill.
                skipped.Add($"{name}/{relative} — a skill may keep at most {SkillToolLimits.MaxFilesPerSkill} files");
            }
            else
            {
                kept.Add(relative);
            }
        }

        return kept;
    }

    private static byte[] BuildZip(string name, string skillDirectory, IReadOnlyList<string> files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var relative in files)
            {
                archive.CreateEntryFromFile(
                    Path.Combine(skillDirectory, relative.Replace('/', Path.DirectorySeparatorChar)),
                    $"{name}/{relative}");
            }
        }

        return buffer.ToArray();
    }
}
