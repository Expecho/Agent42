using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// <see cref="UserSkillInstaller"/> (ADR 0146): the one install path shared by the upload endpoint and the approval of a dream's
/// skill proposal, on a real cluster with an in-memory package store.
/// </summary>
public sealed class UserSkillInstallerTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private readonly InMemorySkillPackageStore _packages = new();

    private UserSkillInstaller Installer(bool withStore = true)
    {
        var services = new ServiceCollection();
        if (withStore)
        {
            services.AddSingleton<ISkillPackageStore>(_packages);
        }

        return new UserSkillInstaller(Cluster.GrainFactory, services.BuildServiceProvider());
    }

    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task Installer_NoStore_IsUnavailable()
    {
        var installer = Installer(withStore: false);

        var result = await installer.InstallAsync("inst-nostore", "SKILL.md", Bytes(ZipTestData.Frontmatter("alpha", "A.")), true);

        Assert.False(installer.Available);
        Assert.True(result.Unavailable);
        Assert.Empty(result.Stored);
    }

    [Fact]
    public async Task Installer_AutoEnable_AppendsKeyToMaterialisedSelectionOnly()
    {
        var on = "inst-on";
        await Cluster.GrainFactory.GetGrain<IUserProfileGrain>(on).SaveAsync(UserProfile.Empty with { EnabledTools = [ToolCatalog.WebSearch] });
        var off = "inst-off";
        await Cluster.GrainFactory.GetGrain<IUserProfileGrain>(off).SaveAsync(UserProfile.Empty with { EnabledTools = [ToolCatalog.WebSearch] });
        var installer = Installer();

        await installer.InstallAsync(on, "SKILL.md", Bytes(ZipTestData.Frontmatter("alpha", "A.")), autoEnable: true);
        await installer.InstallAsync(off, "SKILL.md", Bytes(ZipTestData.Frontmatter("alpha", "A.")), autoEnable: false);

        Assert.Contains(ToolCatalog.SkillKey("alpha"), (await Cluster.GrainFactory.GetGrain<IUserProfileGrain>(on).GetAsync()).EnabledTools!);
        Assert.DoesNotContain(ToolCatalog.SkillKey("alpha"), (await Cluster.GrainFactory.GetGrain<IUserProfileGrain>(off).GetAsync()).EnabledTools!);
    }

    [Fact]
    public async Task Installer_Cap_RefusesNewButAllowsReplace()
    {
        var user = "inst-cap";
        var installer = Installer();
        for (var i = 0; i < SkillToolLimits.MaxInstalledSkillsPerUser; i++)
        {
            Assert.Null((await installer.InstallAsync(user, "SKILL.md", Bytes(ZipTestData.Frontmatter($"skill-{i}", "S.")), false)).Error);
        }

        var extra = await installer.InstallAsync(user, "SKILL.md", Bytes(ZipTestData.Frontmatter("one-too-many", "S.")), false);
        var replace = await installer.InstallAsync(user, "SKILL.md", Bytes(ZipTestData.Frontmatter("skill-0", "Changed.")), false);

        Assert.NotNull(extra.Error);
        Assert.Null(replace.Error);
    }

    [Fact]
    public async Task Installer_Replace_SupersedesOldPackage_AndReadsBackMarkdown()
    {
        var user = "inst-replace";
        var installer = Installer();
        var first = (await installer.InstallAsync(user, "SKILL.md", Bytes(ZipTestData.Frontmatter("alpha", "One.")), false)).Stored.Single();
        var second = (await installer.InstallAsync(user, "SKILL.md", Bytes(ZipTestData.Frontmatter("alpha", "Two.")), false)).Stored.Single();

        Assert.Null(await _packages.ReadAsync(user, first.Id));
        Assert.NotNull(await _packages.ReadAsync(user, second.Id));
        Assert.Contains("Two.", await installer.ReadSkillMarkdownAsync(user, "alpha"));
        Assert.Null(await installer.ReadSkillMarkdownAsync(user, "missing"));
    }

    [Fact]
    public void SkillPackage_LogsWithoutContent()
    {
        var logger = new TestLogger();

        Assert.Null(SkillPackage.ReadSkillMarkdown([1, 2, 3], "alpha", logger));
        Assert.Null(SkillPackage.ReplaceSkillMarkdown([1, 2, 3], "alpha", "PRIVATE BODY", logger));

        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, e =>
        {
            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, e.Level);
            Assert.Contains("alpha", e.Message);
            Assert.DoesNotContain("PRIVATE", e.Message);
        });
    }

    [Fact]
    public void ReplaceSkillMarkdown_KeepsOtherEntries_AndReadsBack()
    {
        var zip = ZipTestData.Zip(
            ("alpha/SKILL.md", ZipTestData.Frontmatter("alpha", "A.")), ("alpha/references/notes.md", "NOTES"));

        var rebuilt = SkillPackage.ReplaceSkillMarkdown(zip, "alpha", "NEW TEXT")!;

        Assert.Equal("NEW TEXT", SkillPackage.ReadSkillMarkdown(rebuilt, "alpha"));
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(rebuilt));
        Assert.Equal("NOTES", new StreamReader(archive.GetEntry("alpha/references/notes.md")!.Open()).ReadToEnd());
        Assert.Null(SkillPackage.ReplaceSkillMarkdown(zip, "other", "x"));
        Assert.Null(SkillPackage.ReadSkillMarkdown([1, 2, 3], "alpha"));
    }
}
