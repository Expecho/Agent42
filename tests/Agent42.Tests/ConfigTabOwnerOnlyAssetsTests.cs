namespace Agent42.Tests;

/// <summary>
/// Guards the frontend half of the owner-only Configuration tab (ADR 0121): a non-owner must not see an empty tab
/// for an endpoint that answers them 404. The repo has no JS test runner, so these assert on source strings of the
/// real <c>wwwroot</c>; the rendering is checked manually in DevHost (where nobody is an owner).
/// </summary>
[Collection("StaticAssets")]
public sealed class ConfigTabOwnerOnlyAssetsTests(StaticAssetsServerFixture fixture)
{
    // The tab ships hidden, so a non-owner never sees it flash, and a failed /api/access leaves it hidden.
    [Fact]
    public async Task ConfigurationTab_StartsHidden()
    {
        var html = await fixture.Client.GetStringAsync("/");

        Assert.Matches(@"id=""tabConfig""\s+hidden", html);
    }

    // Only an explicit isOwner === true reveals it; a failure of any kind hides it, and a non-owner never
    // calls the owner-only endpoint.
    [Fact]
    public async Task ConfigHealthModule_RevealsTheTabForOwnersOnly()
    {
        var js = await fixture.Client.GetStringAsync("/js/confighealth.js");

        Assert.Contains("/api/access", js);
        Assert.Contains("isOwner === true", js);
        Assert.Contains("tabConfig.hidden = !isOwner", js);
        Assert.Contains("if (!(await applyAccess())) return;", js);
    }

    // Arrow/Home/End must skip the hidden tab, or select() would un-hide its panel for a non-owner.
    [Fact]
    public async Task Tablist_KeyboardNavSkipsHiddenTabs()
    {
        var js = await fixture.Client.GetStringAsync("/js/tabs.js");

        Assert.Contains(".filter((i) => !tabPairs[i][0].hidden)", js);
    }
}
