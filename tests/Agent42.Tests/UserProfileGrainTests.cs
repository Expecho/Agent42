using Agent42.Agents;
using Orleans.Core.Internal;
using Orleans.TestingHost;

namespace Agent42.Tests;

public sealed class UserProfileGrainTests : IAsyncLifetime
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

    // AC1: the preferred language persists and survives grain deactivation (Cosmos in prod).
    [Fact]
    public async Task UserProfile_RoundTrips_AndSurvivesReactivation()
    {
        const string user = "profile-roundtrip-user";
        var profile = _cluster.GrainFactory.GetGrain<IUserProfileGrain>(user);

        // Never saved → the empty default.
        Assert.Equal("", (await profile.GetAsync()).PreferredLanguage);

        var saved = await profile.SaveAsync(new UserProfile("nl-NL"));
        Assert.Equal("nl-NL", saved.PreferredLanguage);
        Assert.Equal("nl-NL", (await profile.GetAsync()).PreferredLanguage);

        // Force deactivation so the next call re-reads persisted state.
        await profile.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var reactivated = _cluster.GrainFactory.GetGrain<IUserProfileGrain>(user);
        Assert.Equal("nl-NL", (await reactivated.GetAsync()).PreferredLanguage);
    }

    // AC11 (ADR 0021): the user's default selectable-tools set persists and survives reactivation, and
    // language + EnabledTools are independent (saving one preserves the other via the record `with`).
    [Fact]
    public async Task UserProfile_RoundTripsEnabledTools_AndSurvivesReactivation()
    {
        const string user = "profile-tools-user";
        var profile = _cluster.GrainFactory.GetGrain<IUserProfileGrain>(user);

        var saved = await profile.SaveAsync(new UserProfile("de-DE", [ToolCatalog.WebSearch, ToolCatalog.Scheduling]));
        Assert.Equal("de-DE", saved.PreferredLanguage);
        Assert.Equal(new[] { ToolCatalog.WebSearch, ToolCatalog.Scheduling }, saved.EnabledTools);

        await profile.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var reactivated = _cluster.GrainFactory.GetGrain<IUserProfileGrain>(user);
        var reread = await reactivated.GetAsync();
        Assert.Equal("de-DE", reread.PreferredLanguage);
        Assert.Equal(new[] { ToolCatalog.WebSearch, ToolCatalog.Scheduling }, reread.EnabledTools);
    }

    [Fact]
    public async Task UserProfile_TrimsAndCapsLanguageTag()
    {
        var profile = _cluster.GrainFactory.GetGrain<IUserProfileGrain>("profile-cap-user");

        var saved = await profile.SaveAsync(new UserProfile("  en-US  "));
        Assert.Equal("en-US", saved.PreferredLanguage);

        var overlong = await profile.SaveAsync(new UserProfile(new string('x', 200)));
        Assert.True(overlong.PreferredLanguage.Length <= 35);
    }

    // Deterministic guard for the "shared static instance must never be a grain-state default"
    // gotcha: UserProfile.Empty must be a factory (fresh instance per access), never a cached
    // singleton — a shared static default is mutated by Orleans' copier and leaks across grains.
    [Fact]
    public void UserProfile_Empty_IsAFreshInstance_NotASharedSingleton()
    {
        Assert.NotSame(UserProfile.Empty, UserProfile.Empty);
    }

    [Fact]
    public async Task UserProfile_IsIsolatedPerUser()
    {
        var alice = _cluster.GrainFactory.GetGrain<IUserProfileGrain>("profile-alice");
        var bob = _cluster.GrainFactory.GetGrain<IUserProfileGrain>("profile-bob");

        await alice.SaveAsync(new UserProfile("fr-FR"));

        Assert.Equal("fr-FR", (await alice.GetAsync()).PreferredLanguage);
        Assert.Equal("", (await bob.GetAsync()).PreferredLanguage);
    }
}
