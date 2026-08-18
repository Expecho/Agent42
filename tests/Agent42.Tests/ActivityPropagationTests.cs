using System.Diagnostics;
using Agent42.Agents;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using Agent42.Agents.Conversations;

namespace Agent42.Tests;

/// <summary>
/// The regression gate for the "many traces have no parent" fix: proves Orleans propagates the
/// W3C trace context (<see cref="Activity.Current"/>) across a grain call ONLY when the silo is
/// configured with <c>AddActivityPropagation()</c> — and that without it, an in-grain span would
/// begin as a detached root (the orphaned-span symptom).
///
/// Same shape as <see cref="RequestContextPropagationTests"/>: it exercises the real streaming
/// <see cref="IAgentGrain.ChatAsync"/> path and reads back the <c>[trace:&lt;traceId&gt;]</c> marker the
/// injected <see cref="FakeChatClient"/> emits from the <see cref="Activity.Current"/> it sees at the
/// in-grain chat-client call site — the exact context where the production chat-completion span is
/// created. An <see cref="ActivityListener"/> is required for <see cref="ActivitySource.StartActivity(string)"/>
/// to sample (there is no OTel host offline); it is scoped to each test.
/// </summary>
public sealed class ActivityPropagationTests : IAsyncLifetime
{
    private static readonly ActivitySource TestSource = new("Agent42.Tests.ActivityPropagation");

    private TestCluster _withPropagation = null!;
    private TestCluster _withoutPropagation = null!;

    public async Task InitializeAsync()
    {
        // Both sides: the silo registers the INCOMING filter (starts the child span from the propagated
        // parent), the client the OUTGOING one (injects Activity.Current on the grain call). A TestCluster
        // splits client and silo into separate containers, so both must be configured — production co-hosts
        // them, so its single silo.AddActivityPropagation() covers both directions in one container.
        var propagating = new TestClusterBuilder();
        propagating.AddSiloBuilderConfigurator<ActivityPropagationSiloConfigurator>();
        propagating.AddClientBuilderConfigurator<ActivityPropagationClientConfigurator>();
        _withPropagation = propagating.Build();

        var plain = new TestClusterBuilder();
        plain.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _withoutPropagation = plain.Build();

        await _withPropagation.DeployAsync();
        await _withoutPropagation.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        await _withPropagation.DisposeAsync();
        await _withoutPropagation.DisposeAsync();
    }

    // The fix: a root Activity on the caller side reaches the in-grain chat-client site with the SAME
    // trace id — the child span the grain creates will therefore hang under the caller's trace, not float
    // as a new root.
    [Fact]
    public async Task WithActivityPropagation_RootTraceReachesChatClientSite()
    {
        using var listener = SampleEverything();

        using var root = TestSource.StartActivity("caller-root", ActivityKind.Client);
        Assert.NotNull(root);
        var expected = root!.TraceId.ToString();

        var agent = _withPropagation.GrainFactory.GetGrain<IAgentGrain>("trace-propagation-conversation");
        var answer = await DrainAnswerAsync(agent, "hi");

        // The fake echoed the trace id it saw INSIDE the grain's streaming iteration — propagation
        // carried the caller's trace context to the exact context where the chat-completion span is made.
        Assert.Contains($"[trace:{expected}]", answer);
    }

    // The negative that makes the fix load-bearing: WITHOUT AddActivityPropagation, the same ambient root
    // Activity does NOT cross the grain boundary (Orleans does not flow Activity.Current across it), so the
    // in-grain span would start detached — no marker at all.
    [Fact]
    public async Task WithoutActivityPropagation_NoTraceReachesChatClientSite()
    {
        using var listener = SampleEverything();

        using var root = TestSource.StartActivity("caller-root", ActivityKind.Client);
        Assert.NotNull(root);

        var agent = _withoutPropagation.GrainFactory.GetGrain<IAgentGrain>("no-trace-propagation-conversation");
        var answer = await DrainAnswerAsync(agent, "hi");

        Assert.DoesNotContain("[trace:", answer);
    }

    // A listener that samples every source is what makes ActivitySource.StartActivity actually create an
    // Activity (both on the caller for the root, and inside Orleans' incoming filter for the child) — with
    // no listener, StartActivity returns null and there is nothing to propagate. Scoped/disposed per test.
    private static ActivityListener SampleEverything()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
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

/// <summary>
/// The production silo config for the propagation path: everything <see cref="TestSiloConfigurator"/> sets
/// up (in-memory storage/reminders, the fake chat client), PLUS <c>AddActivityPropagation()</c> — the one
/// line under test in <c>Program.cs</c>. Kept separate so every other test's silo stays free of the
/// process-global activity plumbing.
/// </summary>
public sealed class ActivityPropagationSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        new TestSiloConfigurator().Configure(siloBuilder);
        siloBuilder.AddActivityPropagation();
    }
}

/// <summary>
/// The cluster-client half of the propagation path. A <see cref="TestCluster"/> runs its client in a
/// separate container from the silo, so the OUTGOING grain-call filter (which injects the caller's
/// <see cref="Activity.Current"/>) must be registered here — the silo-side <see cref="ActivityPropagationSiloConfigurator"/>
/// only supplies the incoming half. Production co-hosts both in one container, so this split is a
/// test-topology artifact, not a second production knob.
/// </summary>
public sealed class ActivityPropagationClientConfigurator : IClientBuilderConfigurator
{
    public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        => clientBuilder.AddActivityPropagation();
}
