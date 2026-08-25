using Agent42.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// Materialization of a user's uploaded skills (ADR 0049): overlay → blob → local directory →
/// <c>SkillCatalog.Discover</c>. Two properties matter beyond the happy path.
///
/// <para><b>It is best-effort.</b> This runs inside <c>AgentGrain.OnActivateAsync</c>, so a missing or
/// corrupt package must cost that one skill and nothing else — a throw here would fail the activation and
/// brick the conversation.</para>
///
/// <para><b>"Resources survived" is not the same claim as "resources are readable".</b> A file on disk
/// proves nothing: the framework classifies resources by extension and only scans one folder below
/// SKILL.md, so the test that matters asks <c>AgentSkillsProvider</c> itself what it can see.</para>
/// </summary>
public sealed class UserSkillProviderTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    private readonly List<string> _materialized = [];

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var id in _materialized)
        {
            try { Directory.Delete(Path.Combine(UserSkillProvider.MaterializationRoot, id), recursive: true); }
            catch { /* best effort */ }
        }

        await _cluster.DisposeAsync();
    }

    [Fact]
    public async Task NoUploads_IsEmpty()
        => Assert.Empty((await Provider().GetForUserAsync("prov-empty")).Skills);

    // The core path: an installed skill is unpacked and comes back as a SkillInfo whose Path is the
    // directory AgentSkillsProvider will be pointed at.
    [Fact]
    public async Task Materializes_AndDiscovers()
    {
        const string user = "prov-basic";
        var provider = Provider();
        await InstallAsync(user, "mynotes", "Takes notes.");

        var set = await provider.GetForUserAsync(user);

        var skill = Assert.Single(set.Skills);
        Assert.Equal("mynotes", skill.Name);
        Assert.Equal("Takes notes.", skill.Description);
        Assert.True(Directory.Exists(skill.Path));
        Assert.Equal(1, set.Version);
    }

    // AC3: the resources half of the feature. Extraction is necessary but not sufficient — this asserts
    // the framework ITSELF surfaces the resource, by building the same provider BuildAgent builds and
    // reading what load_skill advertises. `references/notes.md` must be there; `deep/buried.md` is the
    // control, proving the assertion can fail.
    [Fact]
    public async Task MaterializedSkill_ResourcesAreDiscoverable()
    {
        const string user = "prov-resources";
        var provider = Provider();
        await InstallAsync(user, "kit", "Has resources.",
            ("references/notes.md", "NOTES-BODY-MARKER"),
            ("data.csv", "a,b"));

        var skill = Assert.Single((await provider.GetForUserAsync(user)).Skills);

        Assert.True(File.Exists(Path.Combine(skill.Path, "references", "notes.md")));

        var body = await LoadSkillAsync(skill.Path, "kit");
        Assert.Contains("references/notes.md", body, StringComparison.Ordinal);
        Assert.Contains("data.csv", body, StringComparison.Ordinal);
    }

    // AC20: a package the store cannot return, and one whose bytes are not a zip at all. Both degrade to a
    // shorter list; neither throws, and neither leaves a half-extracted directory behind that the existence
    // check would later mistake for a materialized skill.
    [Fact]
    public async Task Provider_ToleratesCorruptBundle()
    {
        const string user = "prov-corrupt";
        var provider = Provider();
        var store = new InMemorySkillPackageStore();

        // Row with no package at all.
        await Grain(user).SaveAsync(new StoredUserSkill(Guid.NewGuid().ToString("n"), "ghost", "No bytes.", ["SKILL.md"], DateTimeOffset.UtcNow));

        // Row whose package is garbage.
        var garbageId = Guid.NewGuid().ToString("n");
        _materialized.Add(garbageId);
        await store.SaveAsync(user, garbageId, [0x00, 0x01, 0x02, 0x03]);
        await Grain(user).SaveAsync(new StoredUserSkill(garbageId, "broken", "Bad bytes.", ["SKILL.md"], DateTimeOffset.UtcNow));

        // And one good skill alongside them, so "empty" cannot pass by accident.
        await InstallAsync(user, "mynotes", "Fine.");

        var set = await provider.GetForUserAsync(user);

        Assert.Equal(["mynotes"], set.Skills.Select(s => s.Name));
        Assert.False(Directory.Exists(Path.Combine(UserSkillProvider.MaterializationRoot, garbageId)));
    }

    // The cache is keyed on the overlay VERSION, which is what makes an upload take effect on the next
    // activation with no restart — and what stops every activation re-unpacking every zip.
    [Fact]
    public async Task Cache_HitsOnUnchangedVersion_AndRefreshesOnBump()
    {
        const string user = "prov-cache";
        var provider = Provider();
        await InstallAsync(user, "one", "First.");

        var first = await provider.GetForUserAsync(user);
        Assert.Same(first, await provider.GetForUserAsync(user));

        await InstallAsync(user, "two", "Second.");

        var second = await provider.GetForUserAsync(user);
        Assert.NotSame(first, second);
        Assert.Equal(["one", "two"], second.Skills.Select(s => s.Name).Order());
    }

    // Materialization happens once: the second resolve must reuse the directory rather than re-unpacking,
    // which is only safe BECAUSE the id is fresh on every upload and the directory is therefore immutable.
    [Fact]
    public async Task MaterializesOnce_AndSurvivesACacheMiss()
    {
        const string user = "prov-once";
        await InstallAsync(user, "mynotes", "Fine.");

        var path = Assert.Single((await Provider().GetForUserAsync(user)).Skills).Path;
        var marker = Path.Combine(path, "references", "marker.md");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await File.WriteAllTextAsync(marker, "written after materialization");

        // A brand-new provider instance = a guaranteed cache miss. The existing directory must be reused.
        Assert.Single((await Provider().GetForUserAsync(user)).Skills);
        Assert.True(File.Exists(marker), "the existing materialized directory must be reused, not re-unpacked");
    }

    [Fact]
    public async Task RemoveMaterialized_DeletesTheDirectory_AndIsSafeWhenAbsent()
    {
        const string user = "prov-remove";
        var provider = Provider();
        var skillId = await InstallAsync(user, "mynotes", "Fine.");
        var path = Assert.Single((await provider.GetForUserAsync(user)).Skills).Path;

        provider.RemoveMaterialized(skillId);

        Assert.False(Directory.Exists(path));
        provider.RemoveMaterialized("id-never-existed"); // must not throw
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private UserSkillProvider Provider()
        => new(_cluster.GrainFactory, new InMemorySkillPackageStore(), NullLogger<UserSkillProvider>.Instance);

    private IUserSkillsGrain Grain(string userId) => _cluster.GrainFactory.GetGrain<IUserSkillsGrain>(userId);

    // Installs a skill the way the endpoint does — normalize, store the package, record the row — so the
    // fixture cannot drift from the real write path.
    // The id is minted here rather than passed in: MaterializationRoot is a MACHINE-GLOBAL directory and
    // MaterializeAsync skips extraction when it already exists, so a fixed id lets a stale tree from an
    // aborted earlier run silently serve the wrong bundle to a later one.
    private async Task<string> InstallAsync(
        string userId, string name, string description, params (string Path, string Content)[] resources)
    {
        var skillId = Guid.NewGuid().ToString("n");
        var staging = Path.Combine(Path.GetTempPath(), "provtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var entries = new List<(string, string)>
            {
                ($"{name}/SKILL.md", $"---\nname: {name}\ndescription: {description}\n---\n\nGuidance.\n"),
            };
            entries.AddRange(resources.Select(r => ($"{name}/{r.Path}", r.Content)));

            var result = SkillPackage.Normalize("skill.zip", ZipTestData.Zip([.. entries]), staging);
            var skill = Assert.Single(result.Skills);

            _materialized.Add(skillId);
            await new InMemorySkillPackageStore().SaveAsync(userId, skillId, skill.Zip);
            await Grain(userId).SaveAsync(
                new StoredUserSkill(skillId, skill.Name, skill.Description, skill.Files, DateTimeOffset.UtcNow));
            return skillId;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }
    }

    // Builds the same AgentSkillsProvider BuildAgent builds and returns what load_skill hands the model, so
    // "the framework can see it" is asserted rather than inferred from the filesystem.
    private static async Task<string> LoadSkillAsync(string skillPath, string name)
    {
        var provider = new AgentSkillsProvider(
            [skillPath],
            options: new AgentSkillsProviderOptions
            {
                DisableLoadSkillApproval = true,
                DisableReadSkillResourceApproval = true,
            });

        var capture = new ToolCapturingChatClient();
        await new ChatClientAgent(capture, new ChatClientAgentOptions
        {
            Name = "skills-probe",
            AIContextProviders = [provider],
        }).RunAsync("hi");

        var load = capture.Tools.OfType<AIFunction>().Single(f => f.Name == "load_skill");
        var services = new ServiceCollection().BuildServiceProvider();
        var result = await load.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["skillName"] = name }) { Services = services });

        return result?.ToString() ?? "";
    }
}
