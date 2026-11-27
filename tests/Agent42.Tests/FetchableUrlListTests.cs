using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The fetchable-URL port (plan cc-12; ADR 0011/0066/0067): the exfiltration allow-list's only accessor. Pins the
/// whole-URL match on the normalized form, the user-vs-seed separation, own-list trimming, and the live read of
/// state (a replaced state object, as <c>ClearStateAsync</c> does, must not leave the port holding a stale list).
/// </summary>
public sealed class FetchableUrlListTests
{
    private static Uri U(string url) => new(url);

    private static (FetchableUrlList List, AgentGrainState State) Make()
    {
        var state = new AgentGrainState();
        return (new FetchableUrlList(() => state), state);
    }

    [Fact]
    public void Remember_ThenIsFetchable_MatchesOnTheNormalizedWholeUrl()
    {
        var (list, state) = Make();

        list.Remember(U("HTTPS://Example.COM/Path?a=b#frag"));

        Assert.True(list.IsFetchable(U("https://example.com/Path?a=b")));
        Assert.Equal(["https://example.com/Path?a=b"], state.FetchableUrls);
    }

    [Theory]
    [InlineData("https://example.com/other")]
    [InlineData("https://example.com/Path?a=b&secret=1")]
    [InlineData("https://example.com/path?a=b")]
    [InlineData("https://other.example.com/Path?a=b")]
    public void IsFetchable_RefusesAnyDifferentUrl(string candidate)
    {
        var (list, _) = Make();
        list.Remember(U("https://example.com/Path?a=b"));

        Assert.False(list.IsFetchable(U(candidate)));
    }

    [Fact]
    public void Remember_IsIdempotent()
    {
        var (list, state) = Make();

        list.Remember(U("https://example.com/a"));
        list.Remember(U("https://EXAMPLE.com/a#x"));

        Assert.Single(state.FetchableUrls);
    }

    [Fact]
    public void UserAndSeededUrls_StayInSeparateLists_ButBothAreFetchable()
    {
        var (list, state) = Make();

        list.Remember(U("https://user.example/typed"));
        Assert.Equal("https://seed.example/page", list.RememberSeeded(U("https://seed.example/page")));

        Assert.Equal(["https://user.example/typed"], state.FetchableUrls);
        Assert.Equal(["https://seed.example/page"], state.SeededFetchableUrls);
        Assert.True(list.IsFetchable(U("https://user.example/typed")));
        Assert.True(list.IsFetchable(U("https://seed.example/page")));
    }

    [Fact]
    public void RememberSeeded_ReturnsNullWhenAlreadyPresent()
    {
        var (list, state) = Make();
        list.RememberSeeded(U("https://seed.example/page"));

        Assert.Null(list.RememberSeeded(U("https://seed.example/page#again")));
        Assert.Single(state.SeededFetchableUrls);
    }

    [Fact]
    public void Remember_TrimsOldestFirst_AndNeverTouchesTheSeededList()
    {
        var (list, state) = Make();
        list.RememberSeeded(U("https://seed.example/keep"));

        for (var i = 0; i < AgentGrain.MaxFetchableUrls + 5; i++)
        {
            list.Remember(U($"https://host{i}.example/"));
        }

        Assert.Equal(AgentGrain.MaxFetchableUrls, state.FetchableUrls.Count);
        Assert.DoesNotContain("https://host0.example/", state.FetchableUrls);
        Assert.Contains($"https://host{AgentGrain.MaxFetchableUrls + 4}.example/", state.FetchableUrls);
        Assert.Equal(["https://seed.example/keep"], state.SeededFetchableUrls);
    }

    [Fact]
    public void RememberSeeded_TrimsOnlyItsOwnList()
    {
        var (list, state) = Make();
        list.Remember(U("https://user.example/typed"));

        for (var i = 0; i < AgentGrain.MaxSeededFetchableUrls + 5; i++)
        {
            list.RememberSeeded(U($"https://seed{i}.example/"));
        }

        Assert.Equal(AgentGrain.MaxSeededFetchableUrls, state.SeededFetchableUrls.Count);
        Assert.DoesNotContain("https://seed0.example/", state.SeededFetchableUrls);
        Assert.Equal(["https://user.example/typed"], state.FetchableUrls);
    }

    [Fact]
    public void ReadsStateLive_SoAReplacedStateObjectIsNotStale()
    {
        var state = new AgentGrainState();
        var list = new FetchableUrlList(() => state);
        list.Remember(U("https://example.com/a"));

        // ClearStateAsync replaces the state object: the port must follow it, not keep the old list.
        state = new AgentGrainState();

        Assert.False(list.IsFetchable(U("https://example.com/a")));
        list.Remember(U("https://example.com/b"));
        Assert.Equal(["https://example.com/b"], state.FetchableUrls);
    }
}
