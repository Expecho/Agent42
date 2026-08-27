using Agent42.Agents;
using Orleans.Runtime;

namespace Agent42.Tests;

/// <summary>
/// The load-bearing gate (ADR 0010): proves Orleans propagates <see cref="RequestContext"/>
/// key/values set before a grain call into the grain — specifically into the <b>streaming</b>
/// <see cref="IAgentGrain.ChatAsync"/> path, where the production chat-completion span is created
/// inside an <c>IAsyncEnumerable</c> iterator that Orleans pumps via repeated MoveNext RPCs (a
/// scalar request/response probe would not prove propagation at the real span's context).
///
/// It exercises the real path: set a <see cref="UserTelemetryContext"/> in RequestContext, drain a
/// chat, and read back the <c>[uctx:&lt;user.id&gt;]</c> marker the injected <see cref="FakeChatClient"/>
/// prepends from the RequestContext it sees at the in-grain chat-client call site. If this fails,
/// the whole downstream-attribution design is off and propagation must be enabled (silo config or a
/// grain-call-filter pair) before anything else. No new grain is defined here (the test assembly has
/// no Orleans SDK codegen) — reusing <see cref="AgentGrain"/> + <see cref="FakeChatClient"/> avoids that.
/// </summary>
public sealed class RequestContextPropagationTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    // AC3: identity set before the streaming grain call is visible at the in-grain chat-client site.
    [Fact]
    public async Task RequestContext_SetBeforeStreamingChat_IsVisibleAtChatClientSite()
    {
        RequestContext.Set(
            UserTelemetryContext.RequestContextKey,
            new UserTelemetryContext("oid-propagated", "user@contoso.com", null, null, null, "aad"));
        try
        {
            var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("uctx-propagation-conversation");
            var answer = await DrainAnswerAsync(agent, "hi");

            // The fake echoed the RequestContext it saw INSIDE the grain's streaming iteration —
            // propagation reached the exact context where the chat-completion span is created.
            Assert.Contains("[uctx:oid-propagated]", answer);
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    // AC3 (negative): nothing set → no identity leaks into the grain, so no marker.
    [Fact]
    public async Task RequestContext_NotSet_IsAbsentAtChatClientSite()
    {
        RequestContext.Clear();

        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("no-uctx-propagation-conversation");
        var answer = await DrainAnswerAsync(agent, "hi");

        Assert.DoesNotContain("[uctx:", answer);
    }

    private static async Task<string> DrainAnswerAsync(IAgentGrain agent, string message)
    {
        var text = new List<string>();
        await foreach (var evt in agent.ChatAsync(message, null))
        {
            if (evt.Type == AgentStreamEventType.TextDelta)
            {
                text.Add(evt.Text ?? "");
            }
        }

        return string.Concat(text);
    }
}
