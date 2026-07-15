using Agent42.Agents;
using Agent42.Backend;
using Azure.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

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

        var setup = ModelProviderBuilder.Build(options, Configuration(), Credential());

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

        var setup = ModelProviderBuilder.Build(options, Configuration(), Credential());

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
           // branch fails HERE even though the pure-helper seam test stays green (critic MEDIUM-2). The
           // handle is the Step-1 no-I/O ChatClientMetadata.ProviderUri: /openai/v1 on Responses, plain on Chat.
    public void ResponsesProvider_WiresResponsesEndpoint()
    {
        var responsesUri = ResponsesFactory().GetClient("foundry:gpt-4o")
            .GetService<ChatClientMetadata>()?.ProviderUri?.AbsoluteUri;
        var chatUri = Factory().GetClient("foundry:gpt-4o")
            .GetService<ChatClientMetadata>()?.ProviderUri?.AbsoluteUri;

        Assert.NotNull(responsesUri);
        Assert.NotNull(chatUri);
        Assert.EndsWith("/openai/v1", responsesUri);      // the OpenAI-compatible surface (ADR 0014)
        Assert.DoesNotContain("/openai/v1", chatUri);      // the plain Foundry endpoint (Chat Completions)
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

    [Fact] // AC1 (the unit-testable slice) — the bearer bridge maps an Azure token onto a Bearer
           // AuthenticationToken with no network I/O; the LIVE token exchange is the Step-1 manual gate.
    public async Task BearerBridge_MapsAzureTokenToBearer()
    {
        var provider = new MultiProviderChatClientFactory.AzureIdentityTokenProvider(
            new StaticTokenCredential(), ["https://cognitiveservices.azure.com/.default"]);
        var options = provider.CreateTokenOptions(new Dictionary<string, object>())!;

        var sync = provider.GetToken(options, default);
        var async = await provider.GetTokenAsync(options, default);

        // StaticTokenCredential hands back "token"; the bridge must relabel it as a Bearer token, both paths.
        Assert.Equal("token", sync.TokenValue);
        Assert.Equal("Bearer", sync.TokenType);
        Assert.Equal("token", async.TokenValue);
        Assert.Equal("Bearer", async.TokenType);
    }

    private static MultiProviderChatClientFactory Factory()
    {
        var setup = ModelProviderBuilder.Build(Options(), Configuration(), Credential());
        return new MultiProviderChatClientFactory(
            setup.Providers, "foundry:gpt-4o", "test", NullLogger<MultiProviderChatClientFactory>.Instance);
    }

    // A factory whose shipped `foundry` provider runs Responses (ADR 0014). Constructing the client does
    // no network I/O, same as the plain factory — the /openai/v1 OpenAIClient asks for no token at build.
    private static MultiProviderChatClientFactory ResponsesFactory()
    {
        var options = Options();
        options.Providers["foundry"].UseResponsesApi = true;
        var setup = ModelProviderBuilder.Build(options, Configuration(), Credential());
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
            ["AzureOpenAI:SubscriptionId"] = "sub-1",
            ["AzureOpenAI:ResourceGroup"] = "rg-1",
            ["AzureOpenAI:AccountName"] = "account-1",
        })
        .Build();

    // No network: an AzureOpenAIClient never asks for a token at construction time.
    private static TokenCredential Credential() => new StaticTokenCredential();
}
