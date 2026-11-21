using Agent42.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>The pure routing logic (ADR 0137): capability filter, option text layers, sticky rule, failure policy.</summary>
public class ModelRouterServiceTests
{
    private static readonly RouterCandidate Cheap = new("p:cheap", "Cheap", SupportsTools: true, SupportsVision: false);
    private static readonly RouterCandidate Strong = new("p:strong", "Strong", SupportsTools: true, SupportsVision: true);
    private static readonly RouterCandidate Plain = new("p:plain", "Plain", SupportsTools: false, SupportsVision: false);

    private sealed class FakeClient(IReadOnlyDictionary<string, double>? answer, Exception? throws = null) : IDecisionClient
    {
        public int Calls;
        public string? State;
        public IReadOnlyDictionary<string, string>? Options;

        public Task<IReadOnlyDictionary<string, double>?> ChooseAsync(
            string state, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken = default)
        {
            Calls++;
            State = state;
            Options = options;
            return throws is null ? Task.FromResult(answer) : Task.FromException<IReadOnlyDictionary<string, double>?>(throws);
        }
    }

    private static ModelRouterService Router(FakeClient client, params RouterCandidate[] candidates)
        => new(candidates, client, new ModelRouterOptions(), NullLogger.Instance);

    private static RouterTurn Turn(string? current, bool images = false, bool tools = false, string message = "hello")
        => new(message, images, tools, current);

    private static Dictionary<string, double> P(double m1, double m2, double none = 0) =>
        new() { ["m1"] = m1, ["m2"] = m2, ["none"] = none };

    [Fact]
    public async Task Sticky_SwitchesOnClearWin()
    {
        var client = new FakeClient(P(0.1, 0.85));
        var decision = await Router(client, Cheap, Strong).RouteAsync(Turn("p:cheap"));
        Assert.Equal("p:strong", decision!.ModelId);
        Assert.True(decision.Changed);
    }

    [Fact]
    public async Task Sticky_KeepsBelowThreshold()
    {
        var client = new FakeClient(P(0.1, 0.55));
        var decision = await Router(client, Cheap, Strong).RouteAsync(Turn("p:cheap"));
        Assert.Equal("p:cheap", decision!.ModelId);
        Assert.False(decision.Changed);
    }

    [Fact]
    public async Task Sticky_KeepsWithinMargin()
    {
        var client = new FakeClient(P(0.5, 0.7));
        var decision = await Router(client, Cheap, Strong).RouteAsync(Turn("p:cheap"));
        Assert.False(decision!.Changed);
    }

    [Fact]
    public async Task Sticky_ThresholdAndMarginEdgesAreInclusive()
    {
        var client = new FakeClient(P(0.35, 0.6));
        var decision = await Router(client, Cheap, Strong).RouteAsync(Turn("p:cheap"));
        Assert.Equal("p:strong", decision!.ModelId);
    }

    [Fact]
    public async Task Sticky_CannotTellKeepsCurrent()
    {
        var client = new FakeClient(P(0.02, 0.2, none: 0.78));
        var decision = await Router(client, Cheap, Strong).RouteAsync(Turn("p:cheap"));
        Assert.Equal("p:cheap", decision!.ModelId);
        Assert.False(decision.Changed);
    }

    [Fact]
    public async Task Sticky_CurrentIsBestKeepsItWithoutChange()
    {
        var client = new FakeClient(P(0.9, 0.05));
        var decision = await Router(client, Cheap, Strong).RouteAsync(Turn("p:cheap"));
        Assert.False(decision!.Changed);
    }

    [Fact]
    public async Task Sticky_CurrentFilteredOutTakesBestEligible()
    {
        // Image turn: Cheap cannot see, so only Strong survives (single candidate, no call).
        var client = new FakeClient(P(0.9, 0.05));
        var decision = await Router(client, Cheap, Strong).RouteAsync(Turn("p:cheap", images: true));
        Assert.Equal("p:strong", decision!.ModelId);
        Assert.True(decision.Changed);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Sticky_NoCurrentModelPicksBestWithoutMargin()
    {
        var client = new FakeClient(P(0.3, 0.6));
        var decision = await Router(client, Cheap, Strong).RouteAsync(Turn(null));
        Assert.Equal("p:strong", decision!.ModelId);
    }

    [Fact]
    public void Candidates_ExcludeCopilotOnlyAndNonChat()
    {
        var all = new[]
        {
            Cheap,
            Strong with { Id = "p:copilot", CopilotOnly = true },
            Strong with { Id = "p:image", IsChat = false },
        };
        var eligible = ModelRouterService.FilterCandidates(all, Turn(null));
        Assert.Equal(["p:cheap"], eligible.Select(c => c.Id));
    }

    [Fact]
    public void Candidates_ToolsOnDropsNoToolModels()
    {
        var eligible = ModelRouterService.FilterCandidates([Cheap, Plain], Turn(null, tools: true));
        Assert.Equal(["p:cheap"], eligible.Select(c => c.Id));
        Assert.Equal(2, ModelRouterService.FilterCandidates([Cheap, Plain], Turn(null, tools: false)).Count);
    }

    [Fact]
    public void Candidates_ImagesDropNoVisionModels()
    {
        var eligible = ModelRouterService.FilterCandidates([Cheap, Strong], Turn(null, images: true));
        Assert.Equal(["p:strong"], eligible.Select(c => c.Id));
    }

    [Fact]
    public async Task NoCandidates_NoCallAndNull()
    {
        var client = new FakeClient(P(1, 0));
        Assert.Null(await Router(client, Plain).RouteAsync(Turn("p:plain", tools: true)));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task OneCandidate_SkipsTheCall()
    {
        var client = new FakeClient(P(1, 0));
        var decision = await Router(client, Cheap).RouteAsync(Turn(null));
        Assert.Equal("p:cheap", decision!.ModelId);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Failure_ReturnsNull()
    {
        Assert.Null(await Router(new FakeClient(null), Cheap, Strong).RouteAsync(Turn("p:cheap")));
        Assert.Null(await Router(new FakeClient(null, new HttpRequestException("boom")), Cheap, Strong).RouteAsync(Turn("p:cheap")));
    }

    [Fact]
    public async Task UnknownKeysOnly_ReturnsNull()
    {
        var client = new FakeClient(new Dictionary<string, double> { ["zzz"] = 1 });
        Assert.Null(await Router(client, Cheap, Strong).RouteAsync(Turn("p:cheap")));
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = new FakeClient(null, new OperationCanceledException(cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Router(client, Cheap, Strong).RouteAsync(Turn("p:cheap"), cts.Token));
    }

    [Fact]
    public async Task Options_UsePositionalKeysAndAnUnknownOption_NeverModelIds()
    {
        var client = new FakeClient(P(0.5, 0.5));
        await Router(client, Cheap, Strong).RouteAsync(Turn("p:cheap"));
        Assert.Equal(["m1", "m2", "none"], client.Options!.Keys.Order());
        Assert.DoesNotContain(client.Options.Values, v => v.Contains("p:cheap"));
    }

    [Fact]
    public void Describe_HintBeatsCardBeatsFlags()
    {
        var c = Strong with { Hint = " my hint ", CardText = "card" };
        Assert.Equal("my hint", ModelRouterService.Describe(c));
        Assert.Equal("card", ModelRouterService.Describe(c with { Hint = null }));
        Assert.Equal("Strong: chat model, calls tools, reads images", ModelRouterService.Describe(Strong));
        Assert.Equal("Plain: chat model", ModelRouterService.Describe(Plain));
    }

    [Fact]
    public void Trim_KeepsTheTailWithinTheBudget()
    {
        Assert.Equal("abc", ModelRouterService.Trim("  abc ", 10));
        Assert.Equal("cde", ModelRouterService.Trim("abcde", 3));
    }

    [Fact]
    public async Task LongMessage_IsTrimmedBeforeTheCall()
    {
        var client = new FakeClient(P(0.5, 0.5));
        await Router(client, Cheap, Strong).RouteAsync(Turn("p:cheap", message: new string('x', 20000)));
        Assert.Equal(8000, client.State!.Length);
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData(" Auto ", true)]
    [InlineData("p:auto", false)]
    [InlineData(null, false)]
    public void IsAuto_MatchesOnlyTheReservedValue(string? value, bool expected)
        => Assert.Equal(expected, AutoModelId.IsAuto(value));
}
