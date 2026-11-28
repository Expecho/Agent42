using Agent42.Agents;
using Agent42.Backend;
using Orleans.Runtime;

namespace Agent42.Tests;

/// <summary>
/// ADR 0153, AC17: the pump runs after the request handler returns, so the identity the middleware put in RequestContext must
/// be re-published inside it. Copy of <see cref="RequestContextPropagationTests"/>'s real path, driven through the pump on a
/// task that starts with an EMPTY RequestContext, like a detached run does.
/// </summary>
public sealed class ChatTurnPumpIdentityTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    [Fact]
    public async Task Pump_PropagatesUserIdentity_ToGrainSpans()
    {
        var telemetry = new UserTelemetryContext("oid-pumped", "user@contoso.com", null, null, null, "aad");
        var harness = new PumpHarness(ChatTurnTestOptions.Fast(), telemetry: telemetry);
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("pump-identity-conversation");

        await Task.Run(async () =>
        {
            RequestContext.Clear();
            await harness.Pump.RunAsync(agent.ChatAsync("hi", null), CancellationToken.None);
        });

        var text = string.Concat(harness.Feed.Read(0).Events
            .Where(e => e.Event.Type == AgentStreamEventType.TextDelta)
            .Select(e => e.Event.Text));
        Assert.Contains("[uctx:oid-pumped]", text);
    }

    [Fact]
    public async Task Pump_WithoutCapturedIdentity_LeaksNone()
    {
        var harness = new PumpHarness(ChatTurnTestOptions.Fast());
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("pump-no-identity-conversation");

        await Task.Run(async () =>
        {
            RequestContext.Clear();
            await harness.Pump.RunAsync(agent.ChatAsync("hi", null), CancellationToken.None);
        });

        var text = string.Concat(harness.Feed.Read(0).Events
            .Where(e => e.Event.Type == AgentStreamEventType.TextDelta)
            .Select(e => e.Event.Text));
        Assert.DoesNotContain("[uctx:", text);
    }
}
