using System.Net;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The DecisionModel provider kind and its Foundry client (ADR 0137): invisible as a chat model everywhere, refuses as a
/// chat client, and parses the response captured from a real Decision-1.1 deployment on 2026-11-21.
/// </summary>
public class DecisionModelProviderTests
{
    // Captured from a real deployment (the plan's Step 1 spike); only the question key is ours.
    private const string CapturedSample = """
        {"model":"microsoft-decision-1.1","answers":{"route":{"type":"choice","choice":"m2","confidence":0.9748798363694561,
        "probabilities":{"m1":0.0036172766198779268,"m2":0.9811598772770921,"m3":0.0005635533042922819,"none":0.014659292798737584}}},
        "usage":{"input_tokens":97,"output_tokens":1}}
        """;

    private readonly IProviderSecretProtector _protector =
        new ProviderSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<ProviderSecretProtector>.Instance);

    private static IConfiguration Configuration() => new ConfigurationBuilder().Build();

    private static ProviderOptions Decision(string? endpoint = "https://res.services.ai.azure.com", string? key = "k", params string[] models) => new()
    {
        Kind = ProviderKind.DecisionModel,
        Endpoint = endpoint,
        ApiKey = key,
        Models = [.. (models.Length == 0 ? ["Decision-1.1"] : models).Select(m => new ConfiguredModel { Id = m })],
    };

    private StoredProvider StoredDecision(params string[] models) => new(
        "router", "DecisionModel", "Router", "https://res.services.ai.azure.com", _protector.Protect("k"),
        [.. (models.Length == 0 ? ["Decision-1.1"] : models).Select(m => new StoredModel(m, null, false, false))],
        UseResponsesApi: false);

    private UserProviderResolver Resolver() => new(null!, _protector,
        ModelProviderBuilder.Build(new ModelProvidersOptions { Default = "" }, Configuration()), "");

    private static StoredProvider Ollama(string? cipher) => new(
        "ollama", "OpenAICompatible", null, "http://localhost:11434/v1", cipher,
        [new StoredModel("llama3.1", "Llama", SupportsTools: true, SupportsVision: false)], UseResponsesApi: false);

    // ---- builder -----------------------------------------------------------------------------------------------

    [Fact]
    public void Builds_WithNoCatalogModels_AndRouterSettings()
    {
        var built = ModelProviderBuilder.TryBuildProvider("router", Decision(), Configuration());

        Assert.Equal(ProviderBuildStatus.Built, built.Status);
        Assert.Empty(built.DeclaredModels!);
        Assert.Null(built.Discovering);
        Assert.Equal(new DecisionProviderSettings("https://res.services.ai.azure.com", "k", "Decision-1.1"), built.Provider!.Decision);
    }

    [Theory]
    [InlineData(null, "k")]
    [InlineData("", "k")]
    public void NoEndpoint_IsInvalid(string? endpoint, string key)
        => Assert.Equal(ProviderBuildStatus.Invalid, ModelProviderBuilder.TryBuildProvider("router", Decision(endpoint, key), Configuration()).Status);

    [Fact]
    public void BlankKey_IsSkipped()
        => Assert.Equal(ProviderBuildStatus.Skipped, ModelProviderBuilder.TryBuildProvider("router", Decision(key: ""), Configuration()).Status);

    [Fact]
    public void NotExactlyOneModel_IsInvalid()
    {
        Assert.Equal(ProviderBuildStatus.Invalid,
            ModelProviderBuilder.TryBuildProvider("router", Decision(models: ["a", "b"]), Configuration()).Status);
        Assert.Equal(ProviderBuildStatus.Invalid,
            ModelProviderBuilder.TryBuildProvider("router", new ProviderOptions { Kind = ProviderKind.DecisionModel, Endpoint = "https://x", ApiKey = "k" }, Configuration()).Status);
    }

    // ---- invisible as a chat model -----------------------------------------------------------------------------

    [Fact]
    public void DecisionModelKind_IsNotInTheCatalogOrADefault()
    {
        var resolved = Resolver().Merge(new UserProviderConfig([Ollama(_protector.Protect("o")), StoredDecision()], null, 1));

        Assert.Contains("router", resolved.Providers.Keys);
        Assert.DoesNotContain(resolved.DeclaredModels, m => m.Provider == "router");
        Assert.Equal("ollama:llama3.1", resolved.DefaultModelId);
        Assert.NotNull(resolved.Decision);
    }

    [Fact]
    public void DecisionModelKind_AloneGivesNoDefault_EvenWhenPinned()
    {
        var overlay = new UserProviderConfig([StoredDecision()], "router:Decision-1.1", 1);

        var resolved = Resolver().Merge(overlay);

        Assert.Equal("", resolved.DefaultModelId);
        Assert.Empty(resolved.DeclaredModels);
        Assert.Equal("Decision-1.1", resolved.Decision!.Deployment);
    }

    // A router is per-user only (ADR 0019): an operator-baseline DecisionModel provider must not give every user one.
    [Fact]
    public void BaselineDecisionProvider_IsIgnored()
    {
        var baseline = ModelProviderBuilder.Build(new ModelProvidersOptions
        {
            Default = "",
            Providers = { ["router"] = Decision() },
        }, Configuration());
        var resolver = new UserProviderResolver(null!, _protector, baseline, "");

        var resolved = resolver.Merge(UserProviderConfig.Empty);

        Assert.Null(resolved.Decision);
        Assert.DoesNotContain("router", resolved.Providers.Keys);
    }

    [Fact]
    public void NoDecisionProvider_MeansNoRouter()
        => Assert.Null(Resolver().Merge(new UserProviderConfig([Ollama(_protector.Protect("o"))], null, 1)).Decision);

    [Fact]
    public async Task DecisionModel_ChatClientRefuses_NoSilentFallbackToTheDefault()
    {
        var resolved = Resolver().Merge(new UserProviderConfig([Ollama(_protector.Protect("o")), StoredDecision()], null, 1));
        var factory = new MultiProviderChatClientFactory(
            resolved.Providers, resolved.DefaultModelId, "test", NullLogger<MultiProviderChatClientFactory>.Instance);

        var client = factory.GetClient("router:Decision-1.1");

        Assert.NotNull(client.GetService<DecisionModelOnlyChatClient>());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync("hi"));
        Assert.Equal(DecisionModelOnlyChatClient.Message, ex.Message);
        // A legacy unqualified id still lands on the chat provider, never the router.
        Assert.Null(factory.GetClient("llama3.1").GetService<DecisionModelOnlyChatClient>());
    }

    // ---- Foundry client ----------------------------------------------------------------------------------------

    [Fact]
    public void Parse_ReadsTheCapturedSample()
    {
        var p = FoundryDecisionClient.ParseProbabilities(CapturedSample)!;

        Assert.Equal(4, p.Count);
        Assert.Equal(0.9811598772770921, p["m2"], 12);
        Assert.Equal(0.014659292798737584, p["none"], 12);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"answers":{"route":{"choice":"m1"}}}""")]
    [InlineData("""{"answers":{"other":{"probabilities":{"m1":1}}}}""")]
    [InlineData("""{"answers":{"route":{"probabilities":{}}}}""")]
    public void Parse_UnexpectedShape_IsNull(string json)
        => Assert.Null(FoundryDecisionClient.ParseProbabilities(json));

    [Theory]
    [InlineData("https://agent-42.services.ai.azure.com", "https://agent-42.services.ai.azure.com/providers/microsoft/v1/systemone")]
    [InlineData("https://agent-42.services.ai.azure.com/api/projects/agent42", "https://agent-42.services.ai.azure.com/providers/microsoft/v1/systemone")]
    [InlineData("http://localhost:8080/", "http://localhost:8080/providers/microsoft/v1/systemone")]
    public void Uri_IsTheResourceRoot(string endpoint, string expected)
    {
        Assert.True(FoundryDecisionClient.TryBuildUri(endpoint, out var uri));
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://x/y")]
    public void Uri_RejectsNonHttp(string endpoint)
        => Assert.False(FoundryDecisionClient.TryBuildUri(endpoint, out _));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static FoundryDecisionClient Client(StubHandler handler) => new(
        new HttpClient(handler), new DecisionProviderSettings("https://res.services.ai.azure.com/api/projects/p", "secret-key", "Decision-1.1"),
        NullLogger<FoundryDecisionClient>.Instance);

    private static readonly Dictionary<string, string> Options = new() { ["m1"] = "cheap", ["none"] = "unsure" };

    [Fact]
    public async Task Choose_SendsTheCapturedRequestShape_AndReturnsProbabilities()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CapturedSample) });

        var result = await Client(handler).ChooseAsync("hello", Options);

        Assert.Equal(0.9811598772770921, result!["m2"], 12);
        Assert.Equal("https://res.services.ai.azure.com/providers/microsoft/v1/systemone", handler.Seen!.RequestUri!.ToString());
        Assert.Equal("secret-key", handler.Seen.Headers.GetValues("api-key").Single());
        using var body = System.Text.Json.JsonDocument.Parse(handler.Body!);
        Assert.Equal("Decision-1.1", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello", body.RootElement.GetProperty("state").GetString());
        var question = body.RootElement.GetProperty("questions").GetProperty("route");
        Assert.Equal("choice", question.GetProperty("type").GetString());
        Assert.Equal("cheap", question.GetProperty("criteria").GetProperty("m1").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Choose_NonSuccess_IsNull(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("""{"error":{"code":"x"}}""") });
        Assert.Null(await Client(handler).ChooseAsync("hello", Options));
    }

    [Fact]
    public async Task Choose_TransportFailureOrBadJson_IsNull()
    {
        Assert.Null(await Client(new StubHandler(_ => throw new HttpRequestException("down"))).ChooseAsync("hello", Options));
        Assert.Null(await Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") })).ChooseAsync("hello", Options));
    }

    [Fact]
    public async Task Choose_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new StubHandler(_ => throw new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).ChooseAsync("hello", Options, cts.Token));
    }

    [Fact]
    public async Task Choose_InvalidEndpoint_IsNullWithoutACall()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new FoundryDecisionClient(
            new HttpClient(handler), new DecisionProviderSettings("nope", "k", "d"), NullLogger<FoundryDecisionClient>.Instance);

        Assert.Null(await client.ChooseAsync("hello", Options));
        Assert.Null(handler.Seen);
    }

    // ---- catalog -> candidates ---------------------------------------------------------------------------------

    [Fact]
    public void ToCandidate_MapsFlagsAndMarksNonChatModels()
    {
        var chat = CatalogModelRouter.ToCandidate(new ModelInfo("p:a", "p", "A", "a", null, true, true, Hint: "hard maths"));
        Assert.Equal(("p:a", "A", true, true, true, "hard maths"), (chat.Id, chat.DisplayName, chat.SupportsTools, chat.SupportsVision, chat.IsChat, chat.Hint));

        Assert.False(CatalogModelRouter.ToCandidate(new ModelInfo("p:i", "p", null, "i", null, false, false, SupportsImageGeneration: true)).IsChat);
        Assert.False(CatalogModelRouter.ToCandidate(new ModelInfo("p:e", "p", null, "e", null, false, false, SupportsEmbeddings: true)).IsChat);
        Assert.True(CatalogModelRouter.ToCandidate(new ModelInfo("github-copilot:c", "github-copilot", null, "c", null, true, false, CopilotOnly: true)).CopilotOnly);
        Assert.Equal("m", CatalogModelRouter.ToCandidate(new ModelInfo("p:m", "p", null, "m", null, false, false)).DisplayName);
    }
}
