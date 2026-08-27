using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// <see cref="SharedApiFixture"/> for <see cref="AgentApiTests"/>, which is the one class that reads the
/// route-matcher registrations back out of the API instead of merely needing them to resolve: two providers
/// so <c>/api/models</c> can prove the qualified-id contract (ADR 0013), and Web Push ENABLED with a known
/// key so <c>/api/push/config</c> is assertable (ADR 0028). Both replace a default from the base fixture.
/// </summary>
public sealed class AgentApiFixture : SharedApiFixture
{
    public const string TestVapidPublicKey = "BTestVapidPublicKeyForConfigAssertion";

    protected override void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog("foundry:gpt-4o",
        [
            new ModelInfo("foundry:gpt-4o", "foundry", "GPT-4o", "gpt-4o", "2024-08-06", true, true),
            new ModelInfo("github:openai/gpt-4o-mini", "github", "GPT-4o mini", "openai/gpt-4o-mini",
                null, SupportsTools: true, SupportsVision: false),
        ]));
        builder.Services.AddSingleton(
            new PushConfig(Enabled: true, PublicKey: TestVapidPublicKey, Subject: "mailto:test@agent42"));
    }
}
