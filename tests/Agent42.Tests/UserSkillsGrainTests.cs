using Agent42.Agents;
using Orleans.Core.Internal;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// The per-user uploaded-skill overlay grain (ADR 0049). Two properties carry the feature:
///
/// <para><b>Upsert is by NAME, not by id.</b> A fresh id is minted on every upload (that is what makes the
/// materialized directory immutable and cache-invalidation-free), so identifying a row by id would turn a
/// re-upload into a second, duplicate entry — two identical checkboxes in the picker and an ambiguous gate.</para>
///
/// <para><b>The grain does no blob or disk IO</b>, so <c>SaveAsync</c>/<c>DeleteAsync</c> hand the caller
/// back the row they removed. Without that the superseded package would be orphaned in blob storage and its
/// materialized directory left on disk forever, since only the old id can address either.</para>
/// </summary>
public sealed class UserSkillsGrainTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.DisposeAsync();

    [Fact]
    public async Task NeverSaved_IsEmpty()
    {
        var grain = Grain("skills-empty");

        var config = await grain.GetAsync();

        Assert.Empty(config.Skills);
        Assert.Equal(0, config.Version);
        Assert.Equal(0, await grain.GetVersionAsync());
    }

    [Fact]
    public async Task Save_AddsSkill_AndBumpsVersion()
    {
        var grain = Grain("skills-add");

        Assert.Null(await grain.SaveAsync(Skill("id-1", "mynotes")));

        var config = await grain.GetAsync();
        Assert.Equal(["mynotes"], config.Skills.Select(s => s.Name));
        Assert.Equal(1, config.Version);

        Assert.Null(await grain.SaveAsync(Skill("id-2", "other")));
        Assert.Equal(2, await grain.GetVersionAsync());
    }

    // AC17: re-uploading a skill of the same name REPLACES it rather than duplicating it, and hands back the
    // superseded row so its blob and materialized directory can be cleaned up.
    [Fact]
    public async Task Upload_SameName_Replaces()
    {
        var grain = Grain("skills-replace");
        await grain.SaveAsync(Skill("id-old", "mynotes", "The first version."));

        var superseded = await grain.SaveAsync(Skill("id-new", "mynotes", "The second version."));

        Assert.NotNull(superseded);
        Assert.Equal("id-old", superseded!.Id);

        var skill = Assert.Single((await grain.GetAsync()).Skills);
        Assert.Equal("id-new", skill.Id);
        Assert.Equal("The second version.", skill.Description);
    }

    // A name is also a directory name, and NTFS and ext4 disagree about whether two names differing only in
    // case are one thing. The merge must not depend on which one the code happens to be running on — so the
    // match is OrdinalIgnoreCase and this replaces rather than duplicating.
    //
    // (The framework's own name rule is lowercase-only, so this cannot arise from a well-formed upload
    // today; the guard is here because the cost of being wrong is a silently split selection, and the rule
    // living in the framework is not a rule this grain can see.)
    [Fact]
    public async Task Save_CaseDifferingName_Replaces_RatherThanDuplicating()
    {
        var grain = Grain("skills-case");
        await grain.SaveAsync(Skill("id-lower", "mynotes"));

        var superseded = await grain.SaveAsync(Skill("id-upper", "MyNotes"));

        Assert.NotNull(superseded);
        Assert.Equal("id-lower", superseded!.Id);
        Assert.Single((await grain.GetAsync()).Skills);
    }

    [Fact]
    public async Task Delete_RemovesRow_AndReturnsIt()
    {
        var grain = Grain("skills-delete");
        await grain.SaveAsync(Skill("id-1", "mynotes"));
        await grain.SaveAsync(Skill("id-2", "other"));

        var removed = await grain.DeleteAsync("id-1");

        Assert.NotNull(removed);
        Assert.Equal("mynotes", removed!.Name);
        Assert.Equal(["other"], (await grain.GetAsync()).Skills.Select(s => s.Name));
    }

    // Deleting something that is not there must not bump the version: the version is the provider's cache
    // key, so a no-op write would force every grain to re-materialize for nothing.
    [Fact]
    public async Task Delete_UnknownId_IsANoOp()
    {
        var grain = Grain("skills-delete-unknown");
        await grain.SaveAsync(Skill("id-1", "mynotes"));
        var version = await grain.GetVersionAsync();

        Assert.Null(await grain.DeleteAsync("id-nope"));
        Assert.Equal(version, await grain.GetVersionAsync());
    }

    [Fact]
    public async Task Overlay_SurvivesReactivation()
    {
        var grain = Grain("skills-persist");
        await grain.SaveAsync(Skill("id-1", "mynotes", "Persisted.", ["SKILL.md", "references/notes.md"]));

        await grain.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var skill = Assert.Single((await Grain("skills-persist").GetAsync()).Skills);
        Assert.Equal("id-1", skill.Id);
        Assert.Equal("Persisted.", skill.Description);
        Assert.Equal(["SKILL.md", "references/notes.md"], skill.Files);
    }

    [Fact]
    public async Task Overlays_AreScopedPerUser()
    {
        await Grain("skills-user-a").SaveAsync(Skill("id-a", "mynotes"));

        Assert.Empty((await Grain("skills-user-b").GetAsync()).Skills);
    }

    private IUserSkillsGrain Grain(string userId) => _cluster.GrainFactory.GetGrain<IUserSkillsGrain>(userId);

    private static StoredUserSkill Skill(
        string id, string name, string description = "A skill.", IReadOnlyList<string>? files = null)
        => new(id, name, description, files ?? ["SKILL.md"], DateTimeOffset.UtcNow);
}
