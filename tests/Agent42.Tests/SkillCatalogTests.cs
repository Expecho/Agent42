using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// Discovery of the shipped agent skills (per-skill selectable tools). Two contracts matter: the
/// (directory ↔ frontmatter name) pairing that makes a single skill addressable, and the tolerance
/// guarantee — a missing/unreadable folder or one malformed <c>SKILL.md</c> costs skills, never the host
/// (and, because <c>Lazy</c> caches a thrown exception, never permanently).
/// </summary>
public class SkillCatalogTests : IDisposable
{
    private readonly List<string> _tempRoots = [];

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }

        GC.SuppressFinalize(this);
    }

    // AC10: both skills that ship in the image are discovered, with a name, a description and a path
    // that really exists — the path is what BuildAgent points AgentSkillsProvider at.
    [Fact]
    public void DiscoversShippedSkills()
    {
        var skills = SkillCatalog.Skills;

        Assert.Equal(["scheduledprompt", "websearch"], skills.Select(s => s.Name)); // ordinal order, stable
        Assert.All(skills, s => Assert.False(string.IsNullOrWhiteSpace(s.Description)));
        Assert.All(skills, s => Assert.True(Directory.Exists(s.Path), $"skill path '{s.Path}' must exist"));
    }

    // AC10/AC14: the pairing is the load-bearing part — each skill's Path must be the directory that
    // declares THAT skill, not a shared ancestor. Pointing the discovery at one skill's own path must
    // therefore yield exactly that skill; if Path were the Skills/ root, every entry would return both and
    // the BuildAgent gate could not separate them.
    [Fact]
    public void EachSkillsPathResolvesToThatSkillAlone()
    {
        foreach (var skill in SkillCatalog.Skills)
        {
            Assert.Equal([skill.Name], SkillCatalog.Discover(skill.Path).Select(s => s.Name));
        }
    }

    // AC11: discovery is defensive on every axis at once — a missing root, an empty subdirectory, a
    // malformed SKILL.md, and a SKILL.md whose frontmatter `name` disagrees with its directory name. A
    // throw here would take out every conversation on the silo (Lazy caches the exception), not one skill.
    //
    // MEASURED (spike, Microsoft.Agents.AI 1.13.0), and it corrects a plan premise: AgentFileSkillsSource
    // returns the good skills and simply OMITS a directory whose frontmatter `name` differs from its
    // directory name — it does not throw. So a mismatched skill is SILENTLY absent from the picker.
    [Fact]
    public void Discover_ToleratesBadInput()
    {
        // A root that does not exist at all.
        Assert.Empty(SkillCatalog.Discover(Path.Combine(NewRoot(), "nope")));

        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "empty"));         // no SKILL.md
        WriteSkill(root, "broken", "this is not frontmatter at all");   // malformed
        WriteSkill(root, "mismatch", Frontmatter("other-name", "name disagrees with the directory"));
        WriteSkill(root, "good", Frontmatter("good", "a well-formed skill"));

        var skills = SkillCatalog.Discover(root);

        Assert.Equal(["good"], skills.Select(s => s.Name));
        Assert.Equal("a well-formed skill", skills.Single().Description);
        Assert.Equal(Path.Combine(root, "good"), skills.Single().Path);
    }

    // AC11 (de-dupe): the source recurses, so two skills CAN share a frontmatter name once directories
    // nest — and two identical checkboxes addressing an ambiguous gate is worse than losing one. First
    // wins, ordinal. This is also the regression pin for the nesting bug a per-directory scan would have:
    // each nested skill's Path must be its OWN directory, never the shared category directory.
    [Fact]
    public void Discover_DedupesByName_AndPairsNestedSkillsWithTheirOwnDirectory()
    {
        var root = NewRoot();
        WriteSkill(root, Path.Combine("alpha", "dup"), Frontmatter("dup", "the first one wins"));
        WriteSkill(root, Path.Combine("zeta", "dup"), Frontmatter("dup", "the second one loses"));
        WriteSkill(root, Path.Combine("alpha", "solo"), Frontmatter("solo", "a nested unique skill"));

        var skills = SkillCatalog.Discover(root);

        Assert.Equal(["dup", "solo"], skills.Select(s => s.Name));
        Assert.Equal("the first one wins", skills.Single(s => s.Name == "dup").Description);

        // Each path is the skill's own directory, not the `alpha` category above it.
        Assert.Equal(Path.Combine(root, "alpha", "dup"), skills.Single(s => s.Name == "dup").Path);
        Assert.Equal(Path.Combine(root, "alpha", "solo"), skills.Single(s => s.Name == "solo").Path);
    }

    private static string Frontmatter(string name, string description) =>
        $"---\nname: {name}\ndescription: {description}\n---\n\nSome guidance.\n";

    private static void WriteSkill(string root, string dir, string content)
    {
        var path = Path.Combine(root, dir);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "SKILL.md"), content);
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "skillcat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }
}
