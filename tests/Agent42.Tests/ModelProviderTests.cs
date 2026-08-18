using System.ClientModel.Primitives;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The multi-provider factory and the rules that turn the <c>Models:</c> section into providers
/// (ADR 0013). Nothing here talks to a network: constructing an AzureOpenAIClient/OpenAIClient does
/// no I/O, which is exactly why the eager startup check in Program.cs is cheap.
/// </summary>
public class ModelProviderTests
{
    private const string Endpoint = "https://example.openai.azure.com";

    [Fact] // AC3
    public void UnknownProvider_FallsBackToDefault()
    {
        var factory = Factory();

        // A conversation pinned to a provider that has since left the config must still open.
        var fallback = factory.GetClient("ollama:llama3.1");

        Assert.Same(factory.GetClient(null), fallback);
    }

    [Fact] // AC3 — and its capabilities are the default model's, not a guess.
    public void UnknownProvider_ReportsDefaultModelCapabilities()
    {
        var capabilities = Factory().GetCapabilities("ollama:llama3.1");

        Assert.True(capabilities.SupportsTools);
        Assert.True(capabilities.SupportsVision);
    }

    [Fact] // AC4
    public void BlankApiKeyProvider_IsSkipped_DefaultStillResolves()
    {
        var options = Options();
        options.Providers["github"].ApiKey = "";

        var setup = ModelProviderBuilder.Build(options, Configuration());

        // Skipped, not fatal: an expired third-party token must never take the app down, because
        // Foundry is the availability path.
        Assert.Equal(["github"], setup.SkippedProviders);
        Assert.DoesNotContain("github", setup.Providers.Keys);
        Assert.Empty(setup.DeclaredModels);

        // ...and the app still has a working default model.
        var factory = new MultiProviderChatClientFactory(
            setup.Providers, options.Default, "test", NullLogger<MultiProviderChatClientFactory>.Instance);
        Assert.NotNull(factory.GetClient(null));
    }

    [Fact] // AC5
    public void Factory_CachesOneClientPerQualifiedId()
    {
        var factory = Factory();

        var foundry = factory.GetClient("foundry:gpt-4o");
        var github = factory.GetClient("github:openai/gpt-4o-mini");

        // Two providers yield distinct clients...
        Assert.NotSame(foundry, github);

        // ...and each qualified id is built once and cached, so switching models mid-conversation
        // doesn't rebuild the HTTP pipeline every turn.
        Assert.Same(foundry, factory.GetClient("foundry:gpt-4o"));
        Assert.Same(github, factory.GetClient("github:openai/gpt-4o-mini"));
    }

    [Fact] // AC5 — a legacy unqualified id resolves to the same client as its qualified form.
    public void Factory_LegacyIdAndQualifiedId_ShareAClient()
    {
        var factory = Factory();

        Assert.Same(factory.GetClient("gpt-4o"), factory.GetClient("foundry:gpt-4o"));
    }

    [Fact] // AC6 — capabilities come from the config flags, per model, not per provider.
    public void Capabilities_ComeFromTheModelsConfigEntry()
    {
        var factory = Factory();

        var capable = factory.GetCapabilities("github:openai/gpt-4o-mini");
        Assert.True(capable.SupportsTools);
        Assert.True(capable.SupportsVision);

        var textOnly = factory.GetCapabilities("github:microsoft/phi-4");
        Assert.False(textOnly.SupportsTools);
        Assert.False(textOnly.SupportsVision);

        // A model NOT in the declared list gets nothing. Guessing "true" is the unsafe direction:
        // SupportsVision would hand a third party a SAS URL into our private attachments container.
        var unknown = factory.GetCapabilities("github:openai/never-heard-of-it");
        Assert.False(unknown.SupportsTools);
        Assert.False(unknown.SupportsVision);
    }

    [Fact] // AC15
    public void TwoFoundryProviders_ResolveOwnCoordinates_AndInheritWhenAbsent()
    {
        var options = Options();

        // A second Foundry account: declares all four fields, so it inherits none of them.
        options.Providers["foundry-swe"] = new ProviderOptions
        {
            Kind = ProviderKind.AzureOpenAI,
            Discover = true,
            Endpoint = "https://sweden.openai.azure.com",
            SubscriptionId = "sub-2",
            ResourceGroup = "rg-2",
            AccountName = "account-2",
        };

        var setup = ModelProviderBuilder.Build(options, Configuration());

        var inherited = setup.Discovering.Single(d => d.Key == "foundry").Settings;
        var declared = setup.Discovering.Single(d => d.Key == "foundry-swe").Settings;

        // The shipped provider declares nothing and inherits all four from AzureOpenAI:* — one source
        // of truth, shared with Bicep's env vars and the embedding client.
        Assert.Equal(Endpoint, inherited.Endpoint);
        Assert.Equal("sub-1", inherited.SubscriptionId);
        Assert.Equal("rg-1", inherited.ResourceGroup);
        Assert.Equal("account-1", inherited.AccountName);

        // The second account keeps its own, and does NOT silently enumerate the first one's.
        Assert.Equal("https://sweden.openai.azure.com", declared.Endpoint);
        Assert.Equal("sub-2", declared.SubscriptionId);
        Assert.Equal("rg-2", declared.ResourceGroup);
        Assert.Equal("account-2", declared.AccountName);
    }

    [Fact] // AC4 — assert the SEAM, not client identity: a Responses-flagged provider selects Responses.
    public void Seam_SelectsResponsesForFlaggedProvider()
    {
        var flagged = new ProviderOptions { Kind = ProviderKind.AzureOpenAI, UseResponsesApi = true };
        Assert.True(MultiProviderChatClientFactory.Provider.UseResponses(flagged));

        // Responses is Foundry-only: an OpenAI-compatible provider ignores the flag — GitHub Models has
        // no Responses surface, so the ProviderKind gate is part of the decision, not just the caller's.
        var compat = new ProviderOptions { Kind = ProviderKind.OpenAICompatible, UseResponsesApi = true };
        Assert.False(MultiProviderChatClientFactory.Provider.UseResponses(compat));
    }

    [Fact] // AC6 — an unflagged Foundry provider still selects Chat.
    public void Seam_SelectsChatForUnflaggedFoundry()
    {
        var unflagged = new ProviderOptions { Kind = ProviderKind.AzureOpenAI, UseResponsesApi = false };
        Assert.False(MultiProviderChatClientFactory.Provider.UseResponses(unflagged));
    }

    [Fact] // AC3 — a Responses-flagged Foundry provider builds a valid IChatClient with no network I/O.
    public void ResponsesProvider_BuildsClient()
    {
        Assert.NotNull(ResponsesFactory().GetClient("foundry:gpt-4o"));
    }

    [Fact] // AC4 — the built client is WIRED to the Responses path, so a dead or inverted Provider.Foundry
           // branch fails HERE even though the pure-helper seam test stays green (critic MEDIUM-2). Both
           // Foundry paths now share the /openai/v1 endpoint (the Chat path moved there to shed the classic
           // api-version data plane — see FoundryChatProvider_UsesV1Surface), so ProviderUri can no longer
           // discriminate them. The no-I/O handle is instead the underlying SDK client each path exposes via
           // GetService: a ResponsesClient on the Responses path, a ChatClient on the Chat path (verified to
           // survive the OTel wrapper) — so a dead/inverted branch exposes the wrong one and fails here.
    public void ResponsesProvider_WiresResponsesEndpoint()
    {
        var responses = ResponsesFactory().GetClient("foundry:gpt-4o");
        var chat = Factory().GetClient("foundry:gpt-4o");

        // ResponsesClient is [Experimental("OPENAI001")] — the type reference alone trips it, no I/O.
        // Types are fully qualified: `using OpenAI.Chat` would make ChatMessage ambiguous elsewhere here.
#pragma warning disable OPENAI001
        // The Responses path is a ResponsesClient, and is NOT a plain ChatClient...
        Assert.NotNull(responses.GetService(typeof(OpenAI.Responses.ResponsesClient)));
        Assert.Null(responses.GetService(typeof(OpenAI.Chat.ChatClient)));

        // ...while the Chat path is the mirror image: a ChatClient, not a ResponsesClient.
        Assert.NotNull(chat.GetService(typeof(OpenAI.Chat.ChatClient)));
        Assert.Null(chat.GetService(typeof(OpenAI.Responses.ResponsesClient)));
#pragma warning restore OPENAI001
    }

    [Fact] // Regression: a plain (Chat Completions) Foundry provider reaches the GA /openai/v1 surface,
           // which carries NO api-version — NOT the classic AzureOpenAIClient data plane whose stable-SDK
           // api-version (capped at 2024-10-21) newer Foundry deployments reject with "api version not
           // supported". The no-I/O handle is ChatClientMetadata.ProviderUri ending in /openai/v1.
    public void FoundryChatProvider_UsesV1Surface_NoApiVersion()
    {
        var chatUri = Factory().GetClient("foundry:gpt-4o")
            .GetService<ChatClientMetadata>()?.ProviderUri?.AbsoluteUri;

        Assert.NotNull(chatUri);
        Assert.EndsWith("/openai/v1", chatUri);
    }

    [Fact] // AC5 — tools/vision stay Full on the Responses flag (orthogonal to the wire API); only the
           // hosted-tools bit tracks it: ON for a Responses Foundry provider, OFF for a plain one.
    public void ResponsesProvider_ReportsHostedTools_ToolsAndVisionUnchanged()
    {
        var responses = ResponsesFactory().GetCapabilities("foundry:gpt-4o");
        Assert.True(responses.SupportsTools);
        Assert.True(responses.SupportsVision);
        Assert.True(responses.SupportsHostedTools);

        // A plain (Chat Completions) Foundry provider does NOT advertise hosted tools — they 400 there.
        var chat = Factory().GetCapabilities("foundry:gpt-4o");
        Assert.True(chat.SupportsTools);
        Assert.False(chat.SupportsHostedTools);

        // And an OpenAI-compatible model never gets them either (GitHub Models has no Responses surface).
        Assert.False(Factory().GetCapabilities("github:openai/gpt-4o-mini").SupportsHostedTools);
    }

    [Fact] // AC1 — a Foundry provider builds a working Chat client from the inherited API key (no I/O).
    public void FoundryProvider_BuildsChatClientFromApiKey()
    {
        // Factory()'s `foundry` provider declares no key; it inherits AzureOpenAI:ApiKey via ResolveFoundry
        // (ADR 0017). Constructing the AzureOpenAIClient with an AzureKeyCredential does no network I/O.
        Assert.NotNull(Factory().GetClient("foundry:gpt-4o"));
    }

    [Theory] // AC1 — the Responses auth policy sets Azure's `api-key` header (not the SDK's default
             // Authorization: Bearer), with no I/O, on BOTH the sync and async pipeline paths (production
             // drives the async one). The LIVE /openai/v1 exchange is the Step-1 manual gate.
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApiKeyHeaderPolicy_SetsApiKeyHeader_NotBearer(bool async)
    {
        var policy = new MultiProviderChatClientFactory.ApiKeyHeaderPolicy("secret-key");
        var message = ClientPipeline.Create().CreateMessage();
        message.Request.Method = "POST";
        message.Request.Uri = new Uri("https://example.openai.azure.com/openai/v1/responses");

        // Index 0 is the policy; a terminal no-op at index 1 stops ProcessNext before any transport.
        IReadOnlyList<PipelinePolicy> pipeline = [policy, new TerminalPolicy()];
        if (async)
        {
            await policy.ProcessAsync(message, pipeline, 0);
        }
        else
        {
            policy.Process(message, pipeline, 0);
        }

        Assert.True(message.Request.Headers.TryGetValue("api-key", out var apiKey));
        Assert.Equal("secret-key", apiKey);
        // The point of the custom policy (ADR 0017): Azure reads the key from `api-key`, not Bearer.
        Assert.False(message.Request.Headers.TryGetValue("Authorization", out _));
    }

    [Fact] // AC3 — the Foundry key is the availability path: blank at BOTH the provider level and
           // AzureOpenAI:ApiKey is FATAL at build, not a silent skip like a third-party key.
    public void BlankFoundryKey_IsFatal()
    {
        var options = Options();
        // The `foundry` provider declares no key, so with AzureOpenAI:ApiKey also absent it resolves blank.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = Endpoint,
                // No AzureOpenAI:ApiKey — the Foundry provider has nothing to inherit.
                ["AzureOpenAI:SubscriptionId"] = "sub-1",
                ["AzureOpenAI:ResourceGroup"] = "rg-1",
                ["AzureOpenAI:AccountName"] = "account-1",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => ModelProviderBuilder.Build(options, configuration));
        Assert.Contains("no API key", ex.Message);
    }

    [Fact] // AC4 — a non-discovering Foundry provider (ADR 0017: no ARM discovery under a key) contributes
           // the deployments it DECLARES, so they reach the picker instead of only the default.
    public void NonDiscoveringFoundry_ContributesDeclaredModels()
    {
        var options = Options();
        var foundry = options.Providers["foundry"];
        foundry.Discover = false;
        foundry.Models =
        [
            new ConfiguredModel { Id = "gpt-5.4", DisplayName = "GPT-5.4", SupportsTools = true, SupportsVision = true },
        ];

        var setup = ModelProviderBuilder.Build(options, Configuration());

        var declared = Assert.Single(setup.DeclaredModels, m => m.Provider == "foundry");
        Assert.Equal("foundry:gpt-5.4", declared.Id);
        Assert.True(declared.SupportsTools);
        Assert.True(declared.SupportsVision);

        // A non-discovering provider does NOT appear in the ARM discovery list.
        Assert.DoesNotContain(setup.Discovering, d => d.Key == "foundry");
    }

    [Fact] // The provider's DisplayName rides its declared models (and its discovery entry) so /api/models
           // can LABEL each picker optgroup with it; a provider with none leaves ProviderDisplayName null and
           // the frontend falls back to the key.
    public void ProviderDisplayName_RidesDeclaredModelsAndDiscovery()
    {
        var options = Options();
        options.Providers["github"].DisplayName = "GitHub Models";
        // A second, discovering Foundry account with a display name — proves the discovery tuple carries it too.
        options.Providers["foundry-swe"] = new ProviderOptions
        {
            Kind = ProviderKind.AzureOpenAI,
            Discover = true,
            DisplayName = "Foundry (Sweden)",
            Endpoint = "https://sweden.openai.azure.com",
            SubscriptionId = "sub-2",
            ResourceGroup = "rg-2",
            AccountName = "account-2",
        };

        var setup = ModelProviderBuilder.Build(options, Configuration());

        // Every declared GitHub model carries the provider display name.
        var githubModels = setup.DeclaredModels.Where(m => m.Provider == "github").ToList();
        Assert.NotEmpty(githubModels);
        Assert.All(githubModels, m => Assert.Equal("GitHub Models", m.ProviderDisplayName));

        // The discovery entry carries it, so AzureModelCatalog can stamp it on each discovered deployment.
        Assert.Equal("Foundry (Sweden)", setup.Discovering.Single(d => d.Key == "foundry-swe").DisplayName);

        // The shipped `foundry` provider declares no display name → null → frontend falls back to the key.
        Assert.Null(setup.Discovering.Single(d => d.Key == "foundry").DisplayName);
    }

    [Fact] // AC1 (ADR 0020) — a declared image-generation model surfaces SupportsImageGeneration in
           // capabilities; a chat model and a discovered Foundry deployment do not.
    public void GetCapabilities_ImageModel_ReportsImageGeneration()
    {
        var options = Options();
        options.Providers["images"] = new ProviderOptions
        {
            Kind = ProviderKind.OpenAICompatible,
            Endpoint = "https://images.example/v1",
            ApiKey = "k",
            Models = [new ConfiguredModel { Id = "gpt-image-1", SupportsImageGeneration = true }],
        };
        var setup = ModelProviderBuilder.Build(options, Configuration());
        var factory = new MultiProviderChatClientFactory(
            setup.Providers, "foundry:gpt-4o", "test", NullLogger<MultiProviderChatClientFactory>.Instance);

        var image = factory.GetCapabilities("images:gpt-image-1");
        Assert.True(image.SupportsImageGeneration);
        // A dedicated image model is not also a tools/vision chat model — the flag is orthogonal, opt-in.
        Assert.False(image.SupportsTools);
        Assert.False(image.SupportsVision);

        // A chat model, and a discovered Foundry deployment (ModelCapabilities.Full), never report it.
        Assert.False(factory.GetCapabilities("github:openai/gpt-4o-mini").SupportsImageGeneration);
        Assert.False(factory.GetCapabilities("foundry:gpt-4o").SupportsImageGeneration);
    }

    [Fact] // AC3 (ADR 0020) — the factory yields an IImageGenerationService for an image model, and null for
           // a chat model / a Foundry model / an unknown model. The generator is what turns a turn into an image.
    public void Factory_ImageModel_YieldsGenerator()
    {
        var options = Options();
        options.Providers["images"] = new ProviderOptions
        {
            Kind = ProviderKind.OpenAICompatible,
            Endpoint = "https://images.example/v1",
            ApiKey = "k",
            Models = [new ConfiguredModel { Id = "gpt-image-1", SupportsImageGeneration = true }],
        };
        var setup = ModelProviderBuilder.Build(options, Configuration());
        var factory = new MultiProviderChatClientFactory(
            setup.Providers, "foundry:gpt-4o", "test", NullLogger<MultiProviderChatClientFactory>.Instance);

        // An image model yields a generator (built with no network I/O, like the chat client).
        Assert.NotNull(factory.GetImageGenerationService("images:gpt-image-1"));

        // A chat model, a Foundry deployment, and an unknown model all yield null — only an image model runs
        // the image path.
        Assert.Null(factory.GetImageGenerationService("github:openai/gpt-4o-mini"));
        Assert.Null(factory.GetImageGenerationService("foundry:gpt-4o"));
        Assert.Null(factory.GetImageGenerationService("images:not-declared"));
    }

    [Fact] // AC3 — the empty (no-provider) factory yields no generator, without hitting Resolve.
    public void EmptyProviders_YieldsNoImageGenerator()
    {
        var factory = new MultiProviderChatClientFactory(
            new Dictionary<string, MultiProviderChatClientFactory.Provider>(),
            "", "test", NullLogger<MultiProviderChatClientFactory>.Instance);

        Assert.Null(factory.GetImageGenerationService(null));
        Assert.Null(factory.GetImageGenerationService("images:gpt-image-1"));
    }

    [Fact] // AC1 — an empty provider set constructs a factory (no throw) with an empty default (ADR 0019):
           // a user who configured no chat provider gets an empty picker, not a startup crash.
    public void EmptyProviders_ConstructsWithNoDefault()
    {
        var factory = new MultiProviderChatClientFactory(
            new Dictionary<string, MultiProviderChatClientFactory.Provider>(),
            "", "test", NullLogger<MultiProviderChatClientFactory>.Instance);

        Assert.Equal("", factory.DefaultModelId);

        // Nothing is capable of anything — and this returns without hitting Resolve, which assumes a
        // provider exists.
        var capabilities = factory.GetCapabilities(null);
        Assert.False(capabilities.SupportsTools);
        Assert.False(capabilities.SupportsVision);
    }

    [Fact] // AC2 — the empty factory's GetClient hands out the no-op NoModelChatClient: never null (the
           // grain builds a valid agent from it), and running it throws a CLEAR message, not a cryptic
           // KeyNotFoundException from resolving a "" id against an empty provider dict.
    public async Task EmptyProviders_GetClient_ReturnsNoModelClient_ThrowsOnRun()
    {
        var factory = new MultiProviderChatClientFactory(
            new Dictionary<string, MultiProviderChatClientFactory.Provider>(),
            "", "test", NullLogger<MultiProviderChatClientFactory>.Instance);

        var client = factory.GetClient(null);
        Assert.NotNull(client);

        var streaming = Assert.Throws<InvalidOperationException>(
            () => client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        Assert.Contains("No model provider is configured", streaming.Message);

        var response = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        Assert.Contains("No model provider is configured", response.Message);

        // The rest of the IChatClient contract the grain relies on while building an agent: GetService
        // returns null and Dispose is a harmless no-op (the shared instance must survive disposal).
        Assert.Null(client.GetService(typeof(ChatClientMetadata)));
        client.Dispose();
    }

    // AC4 — GetEmbeddingGenerator resolves the configured embedding model's provider generator; with no
    // embedding model configured it hands back the no-op that throws a CLEAR, actionable error on use.
    [Fact]
    public async Task GetEmbeddingGenerator_ResolvesConfigured_AndNoOpWhenUnset()
    {
        var options = new ModelProvidersOptions
        {
            Default = "github:openai/gpt-4o-mini",
            Providers =
            {
                ["github"] = new ProviderOptions
                {
                    Kind = ProviderKind.OpenAICompatible,
                    Endpoint = "https://models.github.ai/inference",
                    ApiKey = "pat",
                    Models = [new ConfiguredModel { Id = "text-embedding-3-large", SupportsEmbeddings = true }],
                },
            },
        };
        var setup = ModelProviderBuilder.Build(options, Configuration());

        // Configured: a real generator, not the no-op (building the OpenAI embedding client does no I/O).
        var configured = new MultiProviderChatClientFactory(
            setup.Providers, "github:openai/gpt-4o-mini", "test",
            NullLogger<MultiProviderChatClientFactory>.Instance,
            defaultEmbeddingModelId: "github:text-embedding-3-large");
        Assert.Equal("github:text-embedding-3-large", configured.DefaultEmbeddingModelId);
        Assert.IsNotType<NoEmbeddingGenerator>(configured.GetEmbeddingGenerator());

        // Unset: DefaultEmbeddingModelId is "" and the generator throws a clear message on use.
        var unset = new MultiProviderChatClientFactory(
            setup.Providers, "github:openai/gpt-4o-mini", "test",
            NullLogger<MultiProviderChatClientFactory>.Instance);
        Assert.Equal("", unset.DefaultEmbeddingModelId);
        var ex = await Assert.ThrowsAsync<EmbeddingModelNotConfiguredException>(
            () => unset.GetEmbeddingGenerator().GenerateAsync(["hi"]));
        Assert.Contains("embedding model", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static MultiProviderChatClientFactory Factory()
    {
        var setup = ModelProviderBuilder.Build(Options(), Configuration());
        return new MultiProviderChatClientFactory(
            setup.Providers, "foundry:gpt-4o", "test", NullLogger<MultiProviderChatClientFactory>.Instance);
    }

    // A factory whose shipped `foundry` provider runs Responses (ADR 0014). Constructing the client does
    // no network I/O, same as the plain factory — the /openai/v1 OpenAIClient asks for no token at build.
    private static MultiProviderChatClientFactory ResponsesFactory()
    {
        var options = Options();
        options.Providers["foundry"].UseResponsesApi = true;
        var setup = ModelProviderBuilder.Build(options, Configuration());
        return new MultiProviderChatClientFactory(
            setup.Providers, "foundry:gpt-4o", "test", NullLogger<MultiProviderChatClientFactory>.Instance);
    }

    private static ModelProvidersOptions Options() => new()
    {
        Default = "foundry:gpt-4o",
        Providers =
        {
            // Declares no endpoint or coordinates: it inherits them (C12/C14).
            ["foundry"] = new ProviderOptions { Kind = ProviderKind.AzureOpenAI, Discover = true },
            ["github"] = new ProviderOptions
            {
                Kind = ProviderKind.OpenAICompatible,
                Endpoint = "https://models.github.ai/inference",
                ApiKey = "pat",
                Models =
                [
                    new ConfiguredModel
                    {
                        Id = "openai/gpt-4o-mini", SupportsTools = true, SupportsVision = true,
                    },
                    // Measured, not assumed: phi-4 rejects a tools request outright (ADR 0013 spike).
                    new ConfiguredModel
                    {
                        Id = "microsoft/phi-4", SupportsTools = false, SupportsVision = false,
                    },
                ],
            },
        },
    };

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureOpenAI:Endpoint"] = Endpoint,
            // The Foundry availability-path key (ADR 0017). Both keyless Foundry providers in these
            // fixtures — `foundry` in Options() and `foundry-swe` — inherit it, so Build does not throw
            // the fatal-blank-key error. BlankFoundryKey_IsFatal deliberately omits it.
            ["AzureOpenAI:ApiKey"] = "azure-key",
            ["AzureOpenAI:SubscriptionId"] = "sub-1",
            ["AzureOpenAI:ResourceGroup"] = "rg-1",
            ["AzureOpenAI:AccountName"] = "account-1",
        })
        .Build();

    // A terminal pipeline stage: does nothing, so ProcessNext stops here instead of attempting transport.
    private sealed class TerminalPolicy : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index) { }

        public override ValueTask ProcessAsync(
            PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index) => ValueTask.CompletedTask;
    }
}
