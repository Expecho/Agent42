using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// <see cref="TestSiloConfigurator"/> with long-term memory UNCONFIGURED —
/// <c>HindsightIntegration(null)</c>, the state of any deployment with no <c>Hindsight:ApiKey</c>
/// (ADR 0056).
///
/// <para>It delegates to <see cref="TestSiloConfigurator"/> and overrides one registration, which is the
/// <see cref="ActivityPropagationSiloConfigurator"/> shape and matters for more than brevity: a copy
/// would run BC4 against a differently-capable grain than every other memory AC (no image generator, no
/// capability predicate, no embedding generator), and would silently stop tracking
/// <see cref="TestSiloConfigurator"/> as that evolves. The last registration wins for
/// <c>GetRequiredService</c>, so the <c>AddSingleton</c> below replaces the recorder-wrapped integration
/// the base configurator registered.</para>
///
/// <para>The <see cref="RecordingHindsightClient"/> therefore stays registered and resolvable, but is no
/// longer reachable by the grain — which is exactly what BC4 needs, because the property under test is
/// that the provider is never <em>attached</em>: no recall, no retain, no call of any kind. That
/// distinction is invisible in <c>FakeChatClient.InstructionsEcho</c>, where "no provider attached" and
/// "attached, recalled nothing" both produce instructions with no <c>## Memories</c> block.</para>
/// </summary>
public sealed class UnconfiguredMemorySiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        new TestSiloConfigurator().Configure(siloBuilder);
        siloBuilder.ConfigureServices(services => services.AddSingleton(new HindsightIntegration(null)));
    }
}
