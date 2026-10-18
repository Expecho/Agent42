using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// <see cref="SharedApiFixture"/> for <see cref="AgentApiTests"/>, which is the one class that reads the
/// route-matcher registrations back out of the API instead of merely needing them to resolve: two providers
/// so <c>/api/models</c> can prove the qualified-id contract (ADR 0013), and Web Push ENABLED with a known
/// key so <c>/api/push/config</c> and <c>/api/push/test</c> are assertable (ADR 0028). Both replace a default from the base fixture.
/// </summary>
public sealed class AgentApiFixture : SharedApiFixture
{
    public const string TestVapidPublicKey = "BTestVapidPublicKeyForConfigAssertion";

    // An identity alias (ADR 0074), so AgentApiTests can prove end to end that two principals reach one
    // dataset. Both are GUIDs no other test uses, which matters because this fixture is shared with
    // ChatGroupApiTests and SharedClusterSeamTests: an alias whose key nothing sends is inert for them.
    public const string AliasPrincipalOid = "aaaaaaaa-0000-0000-0000-00000000aaaa";
    public const string CanonicalPrincipalOid = "bbbbbbbb-0000-0000-0000-00000000bbbb";

    // The push transport behind POST /api/push/test, so AgentApiTests can assert what was sent without a
    // real push service. Only the test notifier uses it; nothing else in this host sends pushes.
    internal FakeWebPushClient PushClient { get; } = new();

    protected override void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog("foundry:gpt-4o",
        [
            new ModelInfo("foundry:gpt-4o", "foundry", "GPT-4o", "gpt-4o", "2024-08-06", true, true),
            new ModelInfo("github:openai/gpt-4o-mini", "github", "GPT-4o mini", "openai/gpt-4o-mini",
                null, SupportsTools: true, SupportsVision: false),
        ]));
        // GET /api/agents seeds the research agent on the user's default model (ADR 0082). The endpoint resolves the
        // per-user factory optionally, so only a host that registers one seeds; this one borrows the silo's.
        builder.Services.AddSingleton(_ => Cluster.GetSiloServiceProvider(Cluster.Primary!.SiloAddress)
            .GetRequiredService<IUserChatClientFactoryProvider>());
        builder.Services.AddSingleton(
            new PushConfig(Enabled: true, PublicKey: TestVapidPublicKey, Subject: "mailto:test@agent42"));
        builder.Services.AddSingleton(sp => new PushTestNotifier(
            sp.GetRequiredService<IGrainFactory>(), PushClient, NullLogger<PushTestNotifier>.Instance));
        builder.Services.AddSingleton(UserIdAliases.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection([
                    new KeyValuePair<string, string?>(
                        $"{UserIdAliases.SectionName}:{AliasPrincipalOid}", CanonicalPrincipalOid),
                ])
                .Build()));
    }
}
