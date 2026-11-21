using Agent42.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>The pure routing logic (ADR 0137): capability filter, option text layers, sticky rule, failure policy.</summary>
public class ModelRouterServiceTests
{
    private static readonly RouterCandidate Cheap = new("p:cheap", "Cheap", SupportsTools: true, SupportsVision: false);
    private static readonly RouterCandidate Strong = new("p:strong", "Strong", SupportsTools: true, SupportsVision: true);
    private static readonly RouterCandidate Plain = new("p:plain", "Plain", SupportsTools: false, SupportsVision: false);

    private static readonly RouterCandidate Img = new(
        "p:img", "Img", SupportsTools: false, SupportsVision: false, IsChat: false, IsImageGeneration: true);

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

    [Fact]
    public void Candidates_Image_ExemptFromToolsAndVisionButOtherNonChatStaysOut()
    {
        var embedding = Cheap with { Id = "p:embed", IsChat = false };
        var copilotImage = Img with { Id = "p:copilot-img", CopilotOnly = true };
        var eligible = ModelRouterService.FilterCandidates(
            [Strong, Img, embedding, copilotImage], Turn(null, images: true, tools: true));
        Assert.Equal(["p:strong", "p:img"], eligible.Select(c => c.Id));
    }

    [Fact]
    public async Task Candidates_OnlyImageSurvives_NoImageTurn()
    {
        // Tools on and no tools chat model; an attached image and no vision chat model: only the image option survives.
        var toolsClient = new FakeClient(P(0.1, 0.95));
        Assert.Null(await Router(toolsClient, Plain, Img).RouteAsync(Turn(null, tools: true)));
        var visionClient = new FakeClient(P(0.1, 0.95));
        Assert.Null(await Router(visionClient, Cheap, Img).RouteAsync(Turn("p:cheap", images: true)));
        Assert.Equal(0, toolsClient.Calls + visionClient.Calls);

        // Two image models and still no chat model: still no call (not only a LONE survivor).
        var twoClient = new FakeClient(P(0.95, 0.05));
        var other = Img with { Id = "p:img2", DisplayName = "Img2" };
        Assert.Null(await Router(twoClient, Plain, Img, other).RouteAsync(Turn(null, tools: true)));
        Assert.Equal(0, twoClient.Calls);
    }

    [Fact]
    public async Task ImageBar_ChosenAtTheBar_AndIsAOneOff()
    {
        var decision = await Router(new FakeClient(P(0.1, 0.8)), Cheap, Img).RouteAsync(Turn("p:cheap"));
        Assert.Equal("p:img", decision!.ModelId);
        Assert.True(decision.ImageGeneration);
        Assert.True(decision.Changed);
    }

    [Fact]
    public async Task ImageBar_BelowTheBarFallsBackToTheChatRule()
    {
        // 0.79 clears the chat bar (0.6) but not the image bar; the current chat model is kept.
        var keep = await Router(new FakeClient(P(0.1, 0.79)), Cheap, Img).RouteAsync(Turn("p:cheap"));
        Assert.Equal("p:cheap", keep!.ModelId);
        Assert.False(keep.ImageGeneration);

        // With no current model the best CHAT candidate is taken, never the image model.
        var none = await Router(new FakeClient(P(0.1, 0.79)), Cheap, Img).RouteAsync(Turn(null));
        Assert.Equal("p:cheap", none!.ModelId);
        Assert.False(none.ImageGeneration);
    }

    [Fact]
    public async Task ImageBar_MustAlsoBeTheLargestAnswer_EvenWhenTheBarIsLowered()
    {
        var options = new ModelRouterOptions { ImageMinProbability = 0.3 };
        var router = new ModelRouterService([Cheap, Img], new FakeClient(P(0.2, 0.4, none: 0.4)), options, NullLogger.Instance);
        // "Cannot tell" ties the image option, so the image is not the largest strictly above it... and a chat win is not clear either.
        var tied = await router.RouteAsync(Turn("p:cheap"));
        Assert.True(tied!.ImageGeneration);

        var beaten = new ModelRouterService([Cheap, Img], new FakeClient(P(0.2, 0.4, none: 0.45)), options, NullLogger.Instance);
        var decision = await beaten.RouteAsync(Turn("p:cheap"));
        Assert.False(decision!.ImageGeneration);
        Assert.Equal("p:cheap", decision.ModelId);

        var chatBeats = new ModelRouterService([Cheap, Img], new FakeClient(P(0.5, 0.4, none: 0.1)), options, NullLogger.Instance);
        Assert.False((await chatBeats.RouteAsync(Turn("p:cheap")))!.ImageGeneration);
    }

    [Fact]
    public async Task ImageBar_CannotTellFallsToChat()
    {
        var decision = await Router(new FakeClient(P(0.1, 0.75, none: 0.15)), Cheap, Img).RouteAsync(Turn("p:cheap"));
        Assert.Equal("p:cheap", decision!.ModelId);
        Assert.False(decision.ImageGeneration);
    }

    [Fact]
    public async Task Decide_NoChatCandidates_Null()
    {
        var other = Img with { Id = "p:img2", DisplayName = "Img2" };
        // Only image options survive: no call, and nothing is routed even when the classifier would be sure.
        var client = new FakeClient(P(0.9, 0.05));
        Assert.Null(await Router(client, Img, other).RouteAsync(Turn(null)));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Sticky_CurrentImageModelIsIgnored()
    {
        static Dictionary<string, double> P3(double a, double b, double c) => new() { ["m1"] = a, ["m2"] = b, ["m3"] = c, ["none"] = 0 };

        // The conversation sits on an image model: a chat winner needs no margin and no "cannot tell" check, and Changed is true.
        var chat = await Router(new FakeClient(P3(0.3, 0.5, 0.2)), Cheap, Strong, Img).RouteAsync(Turn("p:img"));
        Assert.Equal("p:strong", chat!.ModelId);
        Assert.True(chat.Changed);
        Assert.False(chat.ImageGeneration);

        // An image winner is a one-off picture turn, flagged as such.
        var image = await Router(new FakeClient(P3(0.05, 0.05, 0.9)), Cheap, Strong, Img).RouteAsync(Turn("p:img"));
        Assert.True(image!.ImageGeneration);
    }

    [Fact]
    public async Task State_PreviousReplyWasImage()
    {
        var withFlag = new FakeClient(P(0.5, 0.5));
        await Router(withFlag, Cheap, Strong).RouteAsync(Turn("p:cheap", message: "make it bluer") with { PreviousReplyWasImage = true });
        Assert.Equal(ModelRouterService.PreviousReplyWasImageLine + "\nmake it bluer", withFlag.State);

        var without = new FakeClient(P(0.5, 0.5));
        await Router(without, Cheap, Strong).RouteAsync(Turn("p:cheap", message: "make it bluer"));
        Assert.Equal("make it bluer", without.State);
    }

    [Fact]
    public void Describe_ImageModel_DerivedDescription_HintAndCardStillWin()
    {
        Assert.Equal("Img: image generator, creates or edits a picture from a prompt", ModelRouterService.Describe(Img));
        Assert.Equal("card", ModelRouterService.Describe(Img with { CardText = "card" }));
        Assert.Equal("hint", ModelRouterService.Describe(Img with { Hint = "hint", CardText = "card" }));
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData(" Auto ", true)]
    [InlineData("p:auto", false)]
    [InlineData(null, false)]
    public void IsAuto_MatchesOnlyTheReservedValue(string? value, bool expected)
        => Assert.Equal(expected, AutoModelId.IsAuto(value));
}
