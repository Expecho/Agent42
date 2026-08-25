using System.Text;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// Normalization of an uploaded agent skill (ADR 0049). Three contracts matter here, and each one is a
/// property the rest of the feature leans on:
///
/// <para><b>1. The framework decides what a skill is.</b> Everything staged is handed to
/// <c>SkillCatalog.Discover</c>, so the name/description and the "frontmatter name must equal the directory
/// name" rule come from the same parser that loads the skill later.</para>
///
/// <para><b>2. A name is validated before it becomes a path.</b> The bare-<c>.md</c> path is the only one
/// that picks a directory name itself, and it must reject a hostile name before any write.</para>
///
/// <para><b>3. The archive guards are platform-independent.</b> Names that collide on NTFS but not ext4,
/// and characters that are illegal on one and legal on the other, must resolve the same way on both — the
/// bug class that made an earlier skills change green on Windows and red on Linux CI.</para>
/// </summary>
public class SkillPackageTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }

        GC.SuppressFinalize(this);
    }

    // AC1: a standalone SKILL.md installs, wrapped into the one storage shape — a zip whose single
    // top-level directory is the frontmatter name.
    [Fact]
    public void Upload_BareMarkdown_Installs()
    {
        var result = SkillPackage.Normalize("SKILL.md", Utf8(Frontmatter("mynotes", "Takes notes.")), NewRoot());

        Assert.Null(result.Error);
        var skill = Assert.Single(result.Skills);
        Assert.Equal("mynotes", skill.Name);
        Assert.Equal("Takes notes.", skill.Description);
        Assert.Equal(["SKILL.md"], skill.Files);
        Assert.Equal(["mynotes/SKILL.md"], EntryNames(skill.Zip));
    }

    // AC6: the frontmatter `name` is validated BEFORE it becomes a path. Writing {staging}/{name}/SKILL.md
    // first and validating afterwards would make each of these a traversal (or, on Windows, an
    // alternate-data-stream) write that had already happened by the time the validator ran — so the test
    // asserts on the staging directory as much as on the result.
    [Theory]
    [InlineData("../../../../foo")]
    [InlineData("..")]
    [InlineData("foo:bar")]
    [InlineData("/abs")]
    [InlineData("My-Skill")]      // uppercase: the framework's own rule rejects it
    [InlineData("my_skill")]
    [InlineData("con")]           // passes the framework's name rule; a reserved device name on Windows
    [InlineData("aux")]
    [InlineData("com1")]
    public void BareMarkdown_TraversalName_RejectedBeforeWrite(string name)
    {
        var staging = NewRoot();

        var result = SkillPackage.Normalize("SKILL.md", Utf8(Frontmatter(name, "Hostile.")), staging);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Skills);
        // Nothing was written anywhere under staging — not even a directory.
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void BareMarkdown_WithoutFrontmatter_IsRejected()
    {
        var result = SkillPackage.Normalize("SKILL.md", Utf8("just some prose, no frontmatter"), NewRoot());

        Assert.NotNull(result.Error);
        Assert.Empty(result.Skills);
    }

    // AC2: a zip with several skill directories installs all of them, and the wrapper depth does not
    // matter. Two wrappers is the interesting row: the framework hard-caps its OWN skill search at depth 2
    // (MaxSkillDirectorySearchDepth, and SearchDepth does not raise it — measured), so a single
    // Discover(stagingRoot) call would find NOTHING here. A zipped parent folder produces exactly this.
    [Theory]
    [InlineData("")]
    [InlineData("wrapper/")]
    [InlineData("outer/inner/")]
    [InlineData("a/b/c/d/")]
    public void Upload_MultiSkillZip_InstallsEach(string prefix)
    {
        var zip = Zip(
            ($"{prefix}alpha/SKILL.md", Frontmatter("alpha", "First.")),
            ($"{prefix}beta/SKILL.md", Frontmatter("beta", "Second.")));

        var result = SkillPackage.Normalize("skills.zip", zip, NewRoot());

        Assert.Null(result.Error);
        Assert.Equal(["alpha", "beta"], result.Skills.Select(s => s.Name).Order());
    }

    // AC3: bundled resources survive into the normalized zip. Note what is and is not kept, all of it
    // measured rather than assumed: classification is by EXTENSION at any folder, so `docs/guide.md` is a
    // resource just like `references/notes.md`, and `references/tool.py` is a SCRIPT despite its folder.
    [Fact]
    public void Normalize_KeepsResources_ByExtensionNotFolder()
    {
        var zip = Zip(
            ("kit/SKILL.md", Frontmatter("kit", "Has resources.")),
            ("kit/references/notes.md", "NOTES"),
            ("kit/references/data.csv", "a,b"),
            ("kit/assets/logo.txt", "LOGO"),
            ("kit/docs/guide.md", "GUIDE"),
            ("kit/top.json", "{}"));

        var result = SkillPackage.Normalize("kit.zip", zip, NewRoot());

        var skill = Assert.Single(result.Skills);
        Assert.Equal(
            ["SKILL.md", "assets/logo.txt", "docs/guide.md", "references/data.csv", "references/notes.md", "top.json"],
            skill.Files.Order(StringComparer.Ordinal));
        Assert.Equal("kit/references/notes.md", EntryNames(skill.Zip).Single(e => e.EndsWith("notes.md", StringComparison.Ordinal)));
    }

    // AC4: script and binary entries are dropped AND reported. Dropping scripts is a security control (no
    // AgentFileSkillScriptRunner is ever registered), and a folder-keyed filter would miss `references/x.py`
    // entirely — the framework classifies it as a script wherever it sits.
    [Fact]
    public void Normalize_DropsScripts_AndReportsThem()
    {
        var zip = Zip(
            ("kit/SKILL.md", Frontmatter("kit", "Has junk.")),
            ("kit/scripts/run.sh", "echo hi"),
            ("kit/references/tool.py", "print(1)"),
            ("kit/helper.ps1", "Write-Host"),
            ("kit/lib.cs", "class X {}"),
            ("kit/logo.png", "PNG"),
            ("kit/archive.zip", "ZIP"));

        var result = SkillPackage.Normalize("kit.zip", zip, NewRoot());

        var skill = Assert.Single(result.Skills);
        Assert.Equal(["SKILL.md"], skill.Files);
        Assert.All(
            new[] { "scripts/run.sh", "references/tool.py", "helper.ps1", "lib.cs", "logo.png", "archive.zip" },
            name => Assert.Contains(result.Skipped, s => s.Contains(name, StringComparison.Ordinal)));
        Assert.Contains(result.Skipped, s => s.Contains("run.sh", StringComparison.Ordinal) && s.Contains("scripts are not supported", StringComparison.Ordinal));
    }

    // AC5 (traversal + rooted names): zip-slip in both spellings. Nothing may land outside the staging root.
    [Fact]
    public void Normalize_RejectsTraversal_AndCapsSize()
    {
        var staging = NewRoot();
        var zip = Zip(
            ("kit/SKILL.md", Frontmatter("kit", "Fine.")),
            ("../escape.md", "ESCAPE"),
            ("kit/../../escape2.md", "ESCAPE"),
            ("/etc/passwd.md", "ROOT"),
            (@"C:\windows\evil.md", "DRIVE"));

        var result = SkillPackage.Normalize("kit.zip", zip, staging);

        var skill = Assert.Single(result.Skills);
        Assert.Equal(["SKILL.md"], skill.Files);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.GetDirectoryName(staging.TrimEnd(Path.DirectorySeparatorChar))!, "escape*.md"),
            _ => true);
    }

    // AC5 (per-entry cap): the cap is enforced against the bytes ACTUALLY COPIED, not ZipArchiveEntry.Length —
    // that field is attacker-controlled metadata a zip bomb lies about.
    [Fact]
    public void Normalize_DropsOversizedEntry()
    {
        var zip = Zip(
            ("kit/SKILL.md", Frontmatter("kit", "Fine.")),
            ("kit/huge.md", new string('x', (int)SkillToolLimits.MaxEntryBytes + 1)));

        var result = SkillPackage.Normalize("kit.zip", zip, NewRoot());

        var skill = Assert.Single(result.Skills);
        Assert.Equal(["SKILL.md"], skill.Files);
        Assert.Contains(result.Skipped, s => s.Contains("huge.md", StringComparison.Ordinal) && s.Contains("per-file limit", StringComparison.Ordinal));
    }

    // AC5 (entry count): a zip of a million empty files is tiny on the wire and ruinous on disk, so the
    // count is capped independently of the byte caps.
    [Fact]
    public void Normalize_RejectsTooManyEntries()
    {
        var entries = Enumerable.Range(0, SkillToolLimits.MaxZipEntries + 1)
            .Select(i => ($"kit/f{i}.md", "x"))
            .ToArray();

        var result = SkillPackage.Normalize("kit.zip", Zip(entries), NewRoot());

        Assert.NotNull(result.Error);
        Assert.Contains("limit is", result.Error, StringComparison.Ordinal);
    }

    // AC5 (zip bomb): highly compressible entries, each individually under the per-entry cap, whose TOTAL
    // expansion blows the budget. This is the case a per-entry cap alone does not catch.
    [Fact]
    public void Normalize_RejectsTotalExpansionBomb()
    {
        var oneUnder = new string('\0', (int)SkillToolLimits.MaxEntryBytes - 1);
        var count = (int)(SkillToolLimits.MaxTotalUncompressedBytes / SkillToolLimits.MaxEntryBytes) + 2;
        var entries = Enumerable.Range(0, count).Select(i => ($"kit/f{i}.md", oneUnder)).ToArray();

        var result = SkillPackage.Normalize("kit.zip", Zip(entries), NewRoot());

        Assert.NotNull(result.Error);
        Assert.Contains("expands to more than", result.Error, StringComparison.Ordinal);
    }

    // AC5 (hostile entry names): reserved device names and trailing dots/spaces are illegal on Windows and
    // legal on Linux, so the same archive must resolve identically on both. `con.md` IS the CON device —
    // the extension does not save it.
    [Theory]
    [InlineData("kit/con.md")]
    [InlineData("kit/CON/notes.md")]
    [InlineData("kit/lpt1.md")]
    [InlineData("kit/trailing./notes.md")]
    [InlineData("kit/notes .md ")]
    [InlineData("kit/no*tes.md")]
    [InlineData("kit/what?.md")]
    public void Normalize_HandlesHostileEntryNames(string entryName)
    {
        var zip = Zip(("kit/SKILL.md", Frontmatter("kit", "Fine.")), (entryName, "x"));

        var result = SkillPackage.Normalize("kit.zip", zip, NewRoot());

        var skill = Assert.Single(result.Skills);
        Assert.Equal(["SKILL.md"], skill.Files);
        Assert.Contains(result.Skipped, s => s.Contains("not allowed", StringComparison.Ordinal));
    }

    // AC5 (duplicates): duplicate entry names are legal in a zip. First wins by archive order so the winner
    // is the same everywhere; ExtractToFile would otherwise throw or silently last-win.
    //
    // The comparison is case-INSENSITIVE, and that is the platform-independence fix rather than pedantry:
    // on NTFS `kit/SKILL.md` and `KIT/skill.md` are ONE file (the second silently overwrites the first),
    // while on ext4 they are two. Treating them as a duplicate makes both filesystems agree.
    [Fact]
    public void Normalize_DuplicateEntryNames_FirstWins()
    {
        var zip = Zip(
            ("kit/SKILL.md", Frontmatter("kit", "The first one.")),
            ("kit/SKILL.md", Frontmatter("kit", "The second one.")),
            ("KIT/SKILL.md", Frontmatter("kit", "The case-differing one.")));

        var result = SkillPackage.Normalize("kit.zip", zip, NewRoot());

        var skill = Assert.Single(result.Skills);
        Assert.Equal("The first one.", skill.Description);
        Assert.Equal(2, result.Skipped.Count(s => s.Contains("duplicate", StringComparison.Ordinal)));
    }

    // AC5 (case-only skill collision): two DIFFERENT directories declaring the same skill name. Both are
    // valid on their own, so the tie-break must be deterministic — lowest path ordinal, mirroring
    // SkillCatalog's own rule so the winner is identical on NTFS and ext4.
    [Fact]
    public void Normalize_TwoDirectoriesDeclaringOneName_FirstByPathWins()
    {
        var zip = Zip(
            ("zeta/dup/SKILL.md", Frontmatter("dup", "The loser.")),
            ("alpha/dup/SKILL.md", Frontmatter("dup", "The winner.")));

        var result = SkillPackage.Normalize("kit.zip", zip, NewRoot());

        var skill = Assert.Single(result.Skills);
        Assert.Equal("The winner.", skill.Description);
        Assert.Contains(result.Skipped, s => s.Contains("more than once", StringComparison.Ordinal));
    }

    // Resources deeper than the framework will ever read are dropped and reported. Measured: with the
    // default SearchDepth the parser surfaces `top.md` and `L0/one.md` but never `L0/L1/two.md`, so keeping
    // the deeper file would store bytes the model can provably never see.
    [Fact]
    public void Normalize_DropsResourcesTooDeepForTheFrameworkToRead()
    {
        var zip = Zip(
            ("kit/SKILL.md", Frontmatter("kit", "Fine.")),
            ("kit/references/notes.md", "OK"),
            ("kit/references/deep/buried.md", "UNREADABLE"));

        var result = SkillPackage.Normalize("kit.zip", zip, NewRoot());

        var skill = Assert.Single(result.Skills);
        Assert.Equal(["SKILL.md", "references/notes.md"], skill.Files.Order(StringComparer.Ordinal));
        Assert.Contains(result.Skipped, s => s.Contains("buried.md", StringComparison.Ordinal) && s.Contains("nested too deeply", StringComparison.Ordinal));
    }

    // Every kept path is persisted into the user-skills GRAIN ITEM, which shares the 2 MB Cosmos cap —
    // so the file COUNT is capped separately from the byte caps that bound the upload. Without this,
    // MaxZipEntries x MaxInstalledSkillsPerUser is ~12,500 persisted paths of pure path text.
    [Fact]
    public void Normalize_CapsTheNumberOfFilesKeptPerSkill()
    {
        var entries = new List<(string, string)> { ("kit/SKILL.md", Frontmatter("kit", "Fine.")) };
        for (var i = 0; i < SkillToolLimits.MaxFilesPerSkill + 10; i++)
        {
            entries.Add(($"kit/r{i:D3}.md", "x"));
        }

        var result = SkillPackage.Normalize("kit.zip", Zip([.. entries]), NewRoot());

        var skill = Assert.Single(result.Skills);
        Assert.Equal(SkillToolLimits.MaxFilesPerSkill, skill.Files.Count);
        Assert.Contains("SKILL.md", skill.Files);   // the skill file itself is never the one dropped
        Assert.Contains(result.Skipped, s => s.Contains("at most", StringComparison.Ordinal));
    }

    // A SKILL.md whose frontmatter `name` disagrees with its directory name is not installed — the
    // framework simply omits it, so this delegates rather than re-implementing the rule.
    [Fact]
    public void Normalize_NameDirectoryMismatch_IsNotInstalled()
    {
        var result = SkillPackage.Normalize(
            "kit.zip", Zip(("dirname/SKILL.md", Frontmatter("othername", "Mismatched."))), NewRoot());

        Assert.NotNull(result.Error);
        Assert.Empty(result.Skills);
        Assert.Contains(result.Skipped, s => s.Contains("not a valid skill", StringComparison.Ordinal));
    }

    [Fact]
    public void Normalize_ZipWithNoSkillFile_IsAnError()
    {
        var result = SkillPackage.Normalize("kit.zip", Zip(("readme.md", "nothing here")), NewRoot());

        Assert.NotNull(result.Error);
        Assert.Empty(result.Skills);
    }

    [Fact]
    public void Normalize_GarbageBytes_IsAnErrorNotAThrow()
    {
        var result = SkillPackage.Normalize("kit.zip", [0x00, 0x01, 0x02, 0x03], NewRoot());

        Assert.NotNull(result.Error);
        Assert.Empty(result.Skills);
    }

    [Fact]
    public void Normalize_UnknownFileType_IsAnError()
    {
        var result = SkillPackage.Normalize("notes.pdf", Utf8("whatever"), NewRoot());

        Assert.NotNull(result.Error);
        Assert.Contains("SKILL.md", result.Error, StringComparison.Ordinal);
    }

    // The normalized zip is the single storage shape: exactly one top-level directory, named for the skill,
    // whatever the upload looked like. This is what lets materialization be one code path.
    [Fact]
    public void NormalizedZip_AlwaysHasOneTopLevelDirectoryNamedForTheSkill()
    {
        var zip = Zip(
            ("outer/inner/kit/SKILL.md", Frontmatter("kit", "Buried.")),
            ("outer/inner/kit/references/notes.md", "NOTES"));

        var skill = Assert.Single(SkillPackage.Normalize("kit.zip", zip, NewRoot()).Skills);

        Assert.All(EntryNames(skill.Zip), e => Assert.StartsWith("kit/", e, StringComparison.Ordinal));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static string Frontmatter(string name, string description) => ZipTestData.Frontmatter(name, description);

    private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

    // Entry names are written VERBATIM (see ZipTestData) - a fixture built from real files could not
    // contain "../escape.md" or a duplicate name at all, so it could not exercise these guards.
    private static byte[] Zip(params (string Name, string Content)[] entries) => ZipTestData.Zip(entries);

    private static IReadOnlyList<string> EntryNames(byte[] zip) => ZipTestData.EntryNames(zip);

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "skillpkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }
}
