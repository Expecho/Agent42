using System.Diagnostics;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

public sealed class FallbackWebSearchTests
{
    private static readonly IReadOnlyList<WebSearchResult> One = [new("t", "https://example.com/1", "s")];

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeProvider(
        string name, Func<CancellationToken, Task<IReadOnlyList<WebSearchResult>>> behaviour, bool canBeBenched = true)
        : IWebSearchProvider
    {
        public int Calls { get; private set; }
        public string Name { get; } = name;
        public bool CanBeBenched { get; } = canBeBenched;

        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(
            string query, int maxResults, CancellationToken cancellationToken = default)
        {
            Calls++;
            return behaviour(cancellationToken);
        }
    }

    private static FakeProvider Ok(string name, IReadOnlyList<WebSearchResult>? results = null, bool benchable = true) =>
        new(name, _ => Task.FromResult(results ?? One), benchable);

    private static FakeProvider Fails(string name, WebSearchFailureKind kind, TimeSpan? retryAfter = null) =>
        new(name, _ => throw new WebSearchProviderException(kind, retryAfter));

    private static FakeProvider Hangs(string name) =>
        new(name, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return One;
        });

    private static (FallbackWebSearch Chain, WebSearchCooldowns Cooldowns, ManualTime Time) Build(
        WebSearchTimeouts? timeouts = null, params IWebSearchProvider[] providers)
    {
        var time = new ManualTime();
        var cooldowns = new WebSearchCooldowns(time);
        return (new FallbackWebSearch(providers, cooldowns, NullLogger<FallbackWebSearch>.Instance, timeouts),
            cooldowns, time);
    }

    private static readonly WebSearchTimeouts Quick = new(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(400));

    [Fact]
    public async Task FirstSuccess_ShortCircuits()
    {
        var first = Ok("tavily");
        var second = Ok("firecrawl");
        var (chain, _, _) = Build(null, first, second);

        var results = await chain.SearchAsync("q", 5);

        Assert.Same(One, results);
        Assert.Equal(1, first.Calls);
        Assert.Equal(0, second.Calls);
    }

    [Theory]
    [InlineData(WebSearchFailureKind.Transient)]
    [InlineData(WebSearchFailureKind.RateLimited)]
    [InlineData(WebSearchFailureKind.Exhausted)]
    [InlineData(WebSearchFailureKind.Unauthorized)]
    public async Task EachFailureKind_FallsThrough(WebSearchFailureKind kind)
    {
        var failing = Fails("tavily", kind);
        var next = Ok("searxng", benchable: false);
        var (chain, cooldowns, _) = Build(null, failing, next);

        var results = await chain.SearchAsync("q", 5);

        Assert.Same(One, results);
        Assert.True(cooldowns.TryGetBenched("tavily", out var benched));
        Assert.Equal(kind, benched.Kind);
    }

    [Fact]
    public async Task UnclassifiedException_IsTransient()
    {
        var broken = new FakeProvider("tavily", _ => throw new HttpRequestException("secret-url-with-key"));
        var (chain, cooldowns, _) = Build(null, broken, Ok("searxng", benchable: false));

        await chain.SearchAsync("q", 5);

        Assert.True(cooldowns.TryGetBenched("tavily", out var benched));
        Assert.Equal(WebSearchFailureKind.Transient, benched.Kind);
    }

    [Fact]
    public async Task EmptyResults_AreNotAFailure()
    {
        var empty = Ok("tavily", []);
        var next = Ok("searxng", benchable: false);
        var (chain, cooldowns, _) = Build(null, empty, next);

        var results = await chain.SearchAsync("q", 5);

        Assert.Empty(results);
        Assert.Equal(0, next.Calls);
        Assert.False(cooldowns.TryGetBenched("tavily", out _));
    }

    [Fact]
    public async Task Cooldown_SkipsThenRecovers()
    {
        var failing = new FakeProvider("tavily", _ => throw new WebSearchProviderException(WebSearchFailureKind.Exhausted));
        var (chain, _, time) = Build(null, failing, Ok("searxng", benchable: false));

        await chain.SearchAsync("q", 5);
        await chain.SearchAsync("q", 5);
        Assert.Equal(1, failing.Calls); // benched: not tried again

        time.Advance(WebSearchOptions.ExhaustedCooldown - TimeSpan.FromSeconds(1));
        await chain.SearchAsync("q", 5);
        Assert.Equal(1, failing.Calls);

        time.Advance(TimeSpan.FromSeconds(2));
        await chain.SearchAsync("q", 5);
        Assert.Equal(2, failing.Calls); // cooldown over: tried again
    }

    [Fact]
    public void CooldownDurations_DifferPerKind()
    {
        Assert.Equal(WebSearchOptions.TransientCooldown, WebSearchOptions.CooldownFor(WebSearchFailureKind.Transient, null));
        Assert.Equal(WebSearchOptions.ExhaustedCooldown, WebSearchOptions.CooldownFor(WebSearchFailureKind.Exhausted, null));
        Assert.Equal(WebSearchOptions.UnauthorizedCooldown, WebSearchOptions.CooldownFor(WebSearchFailureKind.Unauthorized, null));
        Assert.True(WebSearchOptions.TransientCooldown < WebSearchOptions.ExhaustedCooldown);
        Assert.True(WebSearchOptions.ExhaustedCooldown < WebSearchOptions.UnauthorizedCooldown);
    }

    [Fact]
    public void RetryAfter_IsHonouredAndCapped()
    {
        Assert.Equal(TimeSpan.FromSeconds(20),
            WebSearchOptions.CooldownFor(WebSearchFailureKind.RateLimited, TimeSpan.FromSeconds(20)));
        Assert.Equal(WebSearchOptions.RetryAfterCap,
            WebSearchOptions.CooldownFor(WebSearchFailureKind.RateLimited, TimeSpan.FromHours(3)));
        Assert.Equal(WebSearchOptions.RateLimitedCooldown,
            WebSearchOptions.CooldownFor(WebSearchFailureKind.RateLimited, null));
    }

    [Fact]
    public async Task Searxng_IsNeverBenched()
    {
        var keyed = Fails("tavily", WebSearchFailureKind.Exhausted);
        var searxng = new FakeProvider("searxng", _ => throw new InvalidOperationException("down"), canBeBenched: false);
        var (chain, cooldowns, _) = Build(null, keyed, searxng);

        await Assert.ThrowsAsync<InvalidOperationException>(() => chain.SearchAsync("q", 5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chain.SearchAsync("q", 5));

        Assert.Equal(2, searxng.Calls); // reached again even though everything else is benched
        Assert.False(cooldowns.TryGetBenched("searxng", out _));
        Assert.Equal(1, keyed.Calls);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var hanging = Hangs("tavily");
        var (chain, cooldowns, _) = Build(null, hanging, Ok("searxng", benchable: false));

        var task = chain.SearchAsync("q", 5, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(cooldowns.TryGetBenched("tavily", out _));
    }

    [Fact]
    public async Task HostedBudget_FallsThroughToSearxng()
    {
        var slowOne = Hangs("tavily");
        var slowTwo = Hangs("firecrawl");
        var slowThree = Hangs("serper");
        var searxng = Ok("searxng", benchable: false);
        var budget = new WebSearchTimeouts(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(200));
        var (chain, _, _) = Build(budget, slowOne, slowTwo, slowThree, searxng);

        var results = await chain.SearchAsync("q", 5);

        Assert.Same(One, results);
        Assert.Equal(1, searxng.Calls);
        Assert.Equal(0, slowThree.Calls); // never reached: the budget ended the hosted phase
    }

    [Fact]
    public async Task PerAttemptTimeout_BenchesAsTransient()
    {
        var (chain, cooldowns, _) = Build(Quick, Hangs("tavily"), Ok("searxng", benchable: false));

        await chain.SearchAsync("q", 5);

        Assert.True(cooldowns.TryGetBenched("tavily", out var benched));
        Assert.Equal(WebSearchFailureKind.Transient, benched.Kind);
    }

    [Fact]
    public async Task AllFailed_MessageIsCurated()
    {
        var leaky = new FakeProvider("tavily", _ => throw new InvalidOperationException(
            "https://api.example.com/search?key=SECRET-KEY body: {\"detail\":\"oops\"}"));
        var searxng = new FakeProvider("searxng",
            _ => throw new InvalidOperationException("http://localhost:8888/search?q=private"), canBeBenched: false);
        var (chain, _, _) = Build(null, leaky, searxng);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => chain.SearchAsync("private", 5));

        Assert.Contains("tavily", ex.Message);
        Assert.Contains("searxng", ex.Message);
        Assert.DoesNotContain("SECRET-KEY", ex.Message);
        Assert.DoesNotContain("http", ex.Message);
        Assert.DoesNotContain("private", ex.Message);
    }

    [Fact]
    public async Task Observability_TagsTheAnsweringProviderAndSkips()
    {
        var tags = new Dictionary<string, string?>();
        using var source = new ActivitySource("test.websearch");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.websearch",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        var (chain, _, _) = Build(null, Fails("tavily", WebSearchFailureKind.Exhausted), Ok("searxng", benchable: false));

        using (var activity = source.StartActivity("search"))
        {
            await chain.SearchAsync("secret query", 5);
            foreach (var tag in activity!.Tags)
            {
                tags[tag.Key] = tag.Value;
            }
        }

        Assert.Equal("searxng", tags[FallbackWebSearch.ProviderTag]);
        Assert.Equal("tavily:credits-exhausted", tags[FallbackWebSearch.SkippedTag]);
        Assert.DoesNotContain(tags.Values, v => v!.Contains("secret query"));
    }
}
