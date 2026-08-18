using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agent42.Agents.Models;

namespace Agent42.Tests;

/// <summary>
/// The baseline ⊕ overlay merge (ADR 0018). Tests <c>UserProviderResolver.Merge</c> directly — no grain
/// I/O — with a real <see cref="ProviderSecretProtector"/> so the decrypt path is exercised end to end.
/// </summary>
public class UserProviderResolverTests
{
    private const string Endpoint = "https://example.openai.azure.com";
    private const string BaselineDefault = "foundry:gpt-4o";

    private readonly IProviderSecretProtector _protector =
        new ProviderSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<ProviderSecretProtector>.Instance);

    // AC1: a user-added OpenAI-compatible provider appears in the effective set and catalog.
    [Fact]
    public void AddsOpenAICompatibleProvider()
    {
        var resolver = Resolver();
        var overlay = OverlayWith(Ollama(_protector.Protect("ollama-key")));

        var resolved = resolver.Merge(overlay);

        Assert.Contains("ollama", resolved.Providers.Keys);
        var model = Assert.Single(resolved.DeclaredModels, m => m.Provider == "ollama");
        Assert.Equal("ollama:llama3.1", model.Id);
        Assert.True(model.SupportsTools);

        // The baseline is untouched — its providers still resolve.
        Assert.Contains("github", resolved.Providers.Keys);
        Assert.Contains("foundry", resolved.Providers.Keys);
    }

    // AC2: a Foundry override with a blank key resolves Invalid and falls back to the baseline entry —
    // the availability floor. The app never bricks on a bad user override.
    [Fact]
    public void FoundryOverrideWithBlankKey_FallsBackToBaseline()
    {
        var baseline = Baseline();
        var resolver = new UserProviderResolver(null!, _protector, baseline, BaselineDefault);

        // Override "foundry" with the user's own endpoint but NO key (blank ciphertext → decrypts null →
        // no inheritance of the operator key for overlay providers) → Invalid → baseline entry kept.
        var overlay = OverlayWith(new StoredProvider(
            Key: "foundry",
            Kind: "AzureOpenAI",
            DisplayName: "My Foundry",
            Endpoint: "https://user.openai.azure.com",
            ApiKeyCiphertext: null,
            Models: [],
            UseResponsesApi: false));

        var resolved = resolver.Merge(overlay);

        // Same object as the baseline-built provider: the override was discarded, not applied.
        Assert.Same(baseline.Providers["foundry"], resolved.Providers["foundry"]);
    }

    // AC3: a conversation pinned to a provider the user removed degrades to the default, not an error.
    [Fact]
    public void RemovedProvider_DegradesToDefault()
    {
        var resolved = Resolver().Merge(UserProviderConfig.Empty);

        var factory = new MultiProviderChatClientFactory(
            resolved.Providers, resolved.DefaultModelId, "test",
            NullLogger<MultiProviderChatClientFactory>.Instance);

        // "ollama" was never configured (or was removed) — the pinned id falls back to the default client.
        Assert.Same(factory.GetClient(null), factory.GetClient("ollama:llama3.1"));
    }

    [Fact]
    public void OverlayDefault_IsHonoured_WhenItNamesABuiltProvider()
    {
        var overlay = new UserProviderConfig(
            [Ollama(_protector.Protect("k"))], Default: "ollama:llama3.1", Version: 3);

        var resolved = Resolver().Merge(overlay);

        Assert.Equal("ollama:llama3.1", resolved.DefaultModelId);
        Assert.Equal(3, resolved.Version);
    }

    [Fact]
    public void OverlayDefault_NamingAMissingProvider_FallsBackToBaselineDefault()
    {
        // Default names "ghost", but no such provider is configured → the baseline default stands, so the
        // factory never throws resolving an absent default provider.
        var overlay = new UserProviderConfig([], Default: "ghost:model", Version: 1);

        Assert.Equal(BaselineDefault, Resolver().Merge(overlay).DefaultModelId);
    }

    [Fact]
    public void BlankKeyOpenAICompatibleOverlay_IsSkipped_NotAdded()
    {
        // A user provider with no key is skipped (ADR 0013's degrade rule), not added — never fatal.
        var resolved = Resolver().Merge(OverlayWith(Ollama(apiKeyCiphertext: null)));

        Assert.DoesNotContain("ollama", resolved.Providers.Keys);
        Assert.DoesNotContain(resolved.DeclaredModels, m => m.Provider == "ollama");
    }

    // AC3 (ADR 0019): with the baseline chat set removed, a user who adds a single provider but pins no
    // explicit default gets that provider's FIRST declared model — the first-declared-model fallback, since
    // both the overlay default and the (now empty) baseline default are absent.
    [Fact]
    public void NoOverlayDefault_UsesFirstResolvedModel()
    {
        var overlay = OverlayWith(Ollama(_protector.Protect("k")));

        var resolved = EmptyResolver().Merge(overlay);

        Assert.Equal("ollama:llama3.1", resolved.DefaultModelId);
    }

    // AC4 (ADR 0019): genuinely zero providers (empty baseline + empty overlay) ⇒ an EMPTY default, so the
    // picker is empty and the composer gates on it — not a bogus "" model.
    [Fact]
    public void NoProviders_EmptyDefault()
    {
        var resolved = EmptyResolver().Merge(UserProviderConfig.Empty);

        Assert.Empty(resolved.Providers);
        Assert.Empty(resolved.DeclaredModels);
        Assert.Equal("", resolved.DefaultModelId);
    }

    // AC3 (embeddings): the embedding default resolves only when it names a declared EMBEDDING-capable model.
    [Fact]
    public void EmbeddingDefault_ResolvesToEmbeddingCapableModel()
    {
        var overlay = new UserProviderConfig(
            [OllamaWithEmbedding(_protector.Protect("k"))],
            Default: null, Version: 1, EmbeddingDefault: "ollama:nomic-embed");

        Assert.Equal("ollama:nomic-embed", Resolver().Merge(overlay).EmbeddingDefaultModelId);
    }

    [Fact]
    public void EmbeddingDefault_NamingANonEmbeddingModel_ResolvesEmpty()
    {
        // "ollama:llama3.1" is declared but NOT embedding-capable — it must not silently become the embedding
        // model.
        var overlay = new UserProviderConfig(
            [OllamaWithEmbedding(_protector.Protect("k"))],
            Default: null, Version: 1, EmbeddingDefault: "ollama:llama3.1");

        Assert.Equal("", Resolver().Merge(overlay).EmbeddingDefaultModelId);
    }

    [Fact]
    public void NoEmbeddingDefault_ResolvesEmpty()
    {
        var overlay = OverlayWith(OllamaWithEmbedding(_protector.Protect("k")));

        Assert.Equal("", Resolver().Merge(overlay).EmbeddingDefaultModelId);
    }

    // ADR 0038 (speech): the speech default resolves only when it names a declared SPEECH-capable model.
    [Fact]
    public void SpeechDefault_ResolvesToSpeechCapableModel()
    {
        var overlay = new UserProviderConfig(
            [OllamaWithSpeech(_protector.Protect("k"))],
            Default: null, Version: 1, EmbeddingDefault: null, SpeechDefault: "ollama:tts-1");

        Assert.Equal("ollama:tts-1", Resolver().Merge(overlay).SpeechDefaultModelId);
    }

    [Fact]
    public void SpeechDefault_NamingANonSpeechModel_ResolvesEmpty()
    {
        // "ollama:llama3.1" is declared but NOT speech-capable — it must not silently become the speech model.
        var overlay = new UserProviderConfig(
            [OllamaWithSpeech(_protector.Protect("k"))],
            Default: null, Version: 1, EmbeddingDefault: null, SpeechDefault: "ollama:llama3.1");

        Assert.Equal("", Resolver().Merge(overlay).SpeechDefaultModelId);
    }

    [Fact]
    public void NoSpeechDefault_ResolvesEmpty()
    {
        var overlay = OverlayWith(OllamaWithSpeech(_protector.Protect("k")));

        Assert.Equal("", Resolver().Merge(overlay).SpeechDefaultModelId);
    }

    // An Ollama provider declaring a chat model AND a speech-capable model.
    private static StoredProvider OllamaWithSpeech(string? apiKeyCiphertext) => new(
        Key: "ollama",
        Kind: "OpenAICompatible",
        DisplayName: "Local Ollama",
        Endpoint: "http://localhost:11434/v1",
        ApiKeyCiphertext: apiKeyCiphertext,
        Models:
        [
            new StoredModel("llama3.1", "Llama 3.1", SupportsTools: true, SupportsVision: false),
            new StoredModel("tts-1", "TTS 1", SupportsTools: false, SupportsVision: false,
                SupportsImageGeneration: false, SupportsEmbeddings: false, SupportsSpeech: true),
        ],
        UseResponsesApi: false);

    // An Ollama provider declaring a chat model AND an embedding-capable model.
    private static StoredProvider OllamaWithEmbedding(string? apiKeyCiphertext) => new(
        Key: "ollama",
        Kind: "OpenAICompatible",
        DisplayName: "Local Ollama",
        Endpoint: "http://localhost:11434/v1",
        ApiKeyCiphertext: apiKeyCiphertext,
        Models:
        [
            new StoredModel("llama3.1", "Llama 3.1", SupportsTools: true, SupportsVision: false),
            new StoredModel("nomic-embed", "Nomic Embed", SupportsTools: false, SupportsVision: false,
                SupportsImageGeneration: false, SupportsEmbeddings: true),
        ],
        UseResponsesApi: false);

    private UserProviderResolver Resolver() =>
        new(null!, _protector, Baseline(), BaselineDefault);

    // A resolver whose baseline ships NO chat provider (ADR 0019) — the production shape.
    private UserProviderResolver EmptyResolver() =>
        new(null!, _protector, EmptyBaseline(), "");

    private static ModelProviderSetup Baseline() => ModelProviderBuilder.Build(Options(), Configuration());

    private static ModelProviderSetup EmptyBaseline() =>
        ModelProviderBuilder.Build(new ModelProvidersOptions { Default = "" }, Configuration());

    private static UserProviderConfig OverlayWith(StoredProvider provider) => new([provider], null, 1);

    private static StoredProvider Ollama(string? apiKeyCiphertext) => new(
        Key: "ollama",
        Kind: "OpenAICompatible",
        DisplayName: "Local Ollama",
        Endpoint: "http://localhost:11434/v1",
        ApiKeyCiphertext: apiKeyCiphertext,
        Models: [new StoredModel("llama3.1", "Llama 3.1", SupportsTools: true, SupportsVision: false)],
        UseResponsesApi: false);

    private static ModelProvidersOptions Options() => new()
    {
        Default = BaselineDefault,
        Providers =
        {
            ["foundry"] = new ProviderOptions { Kind = ProviderKind.AzureOpenAI, Discover = true },
            ["github"] = new ProviderOptions
            {
                Kind = ProviderKind.OpenAICompatible,
                Endpoint = "https://models.github.ai/inference",
                ApiKey = "pat",
                Models = [new ConfiguredModel { Id = "openai/gpt-4o-mini", SupportsTools = true, SupportsVision = true }],
            },
        },
    };

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureOpenAI:Endpoint"] = Endpoint,
            ["AzureOpenAI:ApiKey"] = "azure-key",
            ["AzureOpenAI:SubscriptionId"] = "sub-1",
            ["AzureOpenAI:ResourceGroup"] = "rg-1",
            ["AzureOpenAI:AccountName"] = "account-1",
        })
        .Build();
}
