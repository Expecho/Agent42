using System.Net;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>Layer two of the Auto router's option text (ADR 0137): a cached public model card, exact-name matched, never on the hot path.</summary>
public class ModelCardSourceTests
{
    // The shape captured from the public listing on 2026-11-21 (trimmed to the fields read).
    private const string Listing = """
        {"data":[
          {"id":"anthropic/claude-sonnet-5.5","description":"A balanced   model\nfor coding and agents.","context_length":1000000},
          {"id":"anthropic/claude-sonnet-5.5:batch","description":"Batch variant.","context_length":1000000},
          {"id":"openai/gpt-4o-mini-2024-07-18","description":"Small and fast.","context_length":128000},
          {"id":"vendor/no-description","description":""},
          {"id":"vendor/no-context","description":"Plain."}
        ]}
        """;

    private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Assert.Equal(OpenRouterModelCardSource.ListingUrl, request.RequestUri!.ToString());
            return Task.FromResult(respond());
        }
    }

    private static OpenRouterModelCardSource Source(Handler handler, TimeProvider? time = null)
        => new(new HttpClient(handler), time ?? TimeProvider.System, NullLogger<OpenRouterModelCardSource>.Instance);

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent(Listing) };

    [Theory]
    [InlineData("claude-sonnet-5-5", "claude-sonnet-5-5")]
    [InlineData("anthropic/claude-sonnet-5.5", "claude-sonnet-5-5")]
    [InlineData("GPT-4o-mini-2024-07-18", "gpt-4o-mini")]
    [InlineData("gpt-4o-mini-20240718", "gpt-4o-mini")]
    [InlineData(" openai/gpt-4o-mini ", "gpt-4o-mini")]
    public void Normalize_MakesDeploymentAndListingNamesComparable(string name, string expected)
        => Assert.Equal(expected, OpenRouterModelCardSource.Normalize(name));

    [Fact]
    public void Parse_ReadsDescriptionsWithContext_SkipsVariantsAndEmpties()
    {
        var cards = OpenRouterModelCardSource.Parse(Listing);

        Assert.Equal("A balanced model for coding and agents. Context window 1000k tokens.", cards["claude-sonnet-5-5"]);
        Assert.Equal("Small and fast. Context window 128k tokens.", cards["gpt-4o-mini"]);
        Assert.Equal("Plain.", cards["no-context"]);
        Assert.DoesNotContain("no-description", cards.Keys);
        Assert.Equal(3, cards.Count);
    }

    [Fact]
    public void Parse_LongDescriptionIsCut()
    {
        var json = $$"""{"data":[{"id":"v/long","description":"{{new string('x', 600)}}"}]}""";

        Assert.True(OpenRouterModelCardSource.Parse(json)["long"].Length <= 281);
    }

    [Theory]
    [InlineData("""{"data":"nope"}""")]
    [InlineData("""{}""")]
    public void Parse_UnexpectedShape_IsEmpty(string json)
        => Assert.Empty(OpenRouterModelCardSource.Parse(json));

    [Fact]
    public async Task TryGet_IsCacheOnly_ThenAnswersOnceLoaded()
    {
        var handler = new Handler(Ok);
        var source = Source(handler);

        // A cold lookup never waits for the network.
        Assert.Null(source.TryGetDescription("claude-sonnet-5-5"));

        await source.LoadAsync();

        Assert.StartsWith("A balanced model", source.TryGetDescription("claude-sonnet-5-5"));
        Assert.Null(source.TryGetDescription("some-unknown-deployment"));
    }

    [Fact]
    public async Task Load_Failure_FallsThroughToNoCard_AndIsNotRetriedPerTurn()
    {
        var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var source = Source(handler);

        await source.LoadAsync();

        Assert.Null(source.TryGetDescription("claude-sonnet-5-5"));
        var calls = handler.Calls;
        // Within the failure back-off, further lookups start no second fetch.
        Assert.Null(source.TryGetDescription("claude-sonnet-5-5"));
        await Task.Delay(50);
        Assert.Equal(calls, handler.Calls);
    }

    [Fact]
    public async Task Load_TransportError_IsSwallowed()
    {
        var source = new OpenRouterModelCardSource(
            new HttpClient(new ThrowingHandler()), TimeProvider.System, NullLogger<OpenRouterModelCardSource>.Instance);

        await source.LoadAsync();

        Assert.Null(source.TryGetDescription("anything"));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline");
    }

    // ---- layer order: Hint > card > flags ----------------------------------------------------------------------

    private sealed class FixedCards(string? text) : IModelCardSource
    {
        public string? TryGetDescription(string modelName) => text;
    }

    [Fact]
    public void LayerOrder_HintBeatsCardBeatsFlags()
    {
        var plain = new ModelInfo("p:m", "p", "Model M", "m", null, true, false);
        var hinted = plain with { Hint = "my hint" };
        var cards = new FixedCards("the card");

        Assert.Equal("my hint", ModelRouterService.Describe(CatalogModelRouter.ToCandidate(hinted, cards)));
        Assert.Equal("the card", ModelRouterService.Describe(CatalogModelRouter.ToCandidate(plain, cards)));
        Assert.Equal("Model M: chat model, calls tools", ModelRouterService.Describe(CatalogModelRouter.ToCandidate(plain, new FixedCards(null))));
        Assert.Equal("Model M: chat model, calls tools", ModelRouterService.Describe(CatalogModelRouter.ToCandidate(plain)));
    }

    [Fact]
    public void Card_IsLookedUpByTheProvidersModelName_NotTheQualifiedId()
    {
        string? asked = null;
        var cards = new CapturingCards(n => asked = n);

        CatalogModelRouter.ToCandidate(new ModelInfo("p:my-deployment", "p", null, "my-deployment", null, true, false), cards);

        Assert.Equal("my-deployment", asked);
    }

    private sealed class CapturingCards(Action<string> capture) : IModelCardSource
    {
        public string? TryGetDescription(string modelName)
        {
            capture(modelName);
            return null;
        }
    }
}
