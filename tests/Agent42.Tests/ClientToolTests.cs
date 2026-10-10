using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// Client tools (ADR 0089) through a real grain: the model's call streams as a <c>toolCallStart</c> flagged
/// <c>clientTool</c>, the tool waits, an interleaved <see cref="IAgentGrain.SubmitClientToolResultAsync"/> answers
/// it, and the turn resumes. Each test drives the tool through <see cref="FakeChatClient.CallToolPrompt"/>, and the
/// fake echoes the scripted call's result into the reply, which is how a test reads what the tool returned.
///
/// <para>The test silo waits <see cref="TestSiloConfigurator.ClientToolWait"/> rather than the production two
/// minutes, so the timeout path runs in seconds.</para>
/// </summary>
public sealed class ClientToolTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private static readonly string[] AllClientTools =
    [
        ClientToolLimits.ConfirmAction, ClientToolLimits.ControlArtifactPane, ClientToolLimits.GetBrowserContext,
        ClientToolLimits.GetCurrentLocation,
    ];

    private IAgentGrain Agent(string key) => Cluster.GrainFactory.GetGrain<IAgentGrain>(key);

    private static string ConfirmPrompt(string action = "Delete the draft")
        => FakeChatClient.CallToolPrompt(ClientToolLimits.ConfirmAction, new { action });

    private static IAsyncEnumerable<AgentStreamEvent> Chat(
        IAgentGrain agent, string prompt, IReadOnlyList<string>? clientTools = null, bool interactive = true)
        => agent.ChatAsync(prompt, null, interactive: interactive, clientTools: clientTools ?? AllClientTools);

    // Reads the stream up to and including the first client-tool call, and returns it with the enumerator parked
    // there, so the test decides what happens while the tool waits.
    private static async Task<AgentStreamEvent> ReadToClientCallAsync(
        IAsyncEnumerator<AgentStreamEvent> stream, List<AgentStreamEvent> events)
    {
        while (await stream.MoveNextAsync())
        {
            events.Add(stream.Current);
            if (stream.Current.ClientTool == true)
            {
                return stream.Current;
            }
        }

        Assert.Fail("The turn ended without a client tool call.");
        return null!;
    }

    private static async Task DrainRestAsync(IAsyncEnumerator<AgentStreamEvent> stream, List<AgentStreamEvent> events)
    {
        while (await stream.MoveNextAsync())
        {
            events.Add(stream.Current);
        }
    }

    private static async Task<List<AgentStreamEvent>> CollectAsync(IAsyncEnumerable<AgentStreamEvent> stream)
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        return events;
    }

    private static string Answer(IEnumerable<AgentStreamEvent> events) =>
        string.Concat(events.Where(e => e.Type == AgentStreamEventType.TextDelta).Select(e => e.Text));

    // The result of this turn's scripted call, as the fake echoes it.
    private static string Result(IEnumerable<AgentStreamEvent> events)
    {
        var answer = Answer(events);
        var start = answer.IndexOf(FakeChatClient.ScriptedResultOpen, StringComparison.Ordinal);
        var end = answer.LastIndexOf(FakeChatClient.ScriptedResultClose, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"no scripted result in: {answer}");
        return answer[(start + FakeChatClient.ScriptedResultOpen.Length)..end];
    }

    // AC1: the call pauses the turn, a result posted while the tool waits resumes it, and the model reads it.
    [Fact]
    public async Task ConfirmAction_ResultPostedMidTurn_ResumesTheTurn()
    {
        var agent = Agent("ct-resume-user/conv");
        var events = new List<AgentStreamEvent>();
        await using var stream = Chat(agent, ConfirmPrompt()).GetAsyncEnumerator();

        var call = await ReadToClientCallAsync(stream, events);
        Assert.Equal(ClientToolLimits.ConfirmAction, call.ToolName);
        Assert.Contains("Delete the draft", call.ToolInput, StringComparison.Ordinal);

        // Answered while the next pull is already waiting inside the tool, which is the browser's real timing.
        var drain = DrainRestAsync(stream, events);
        await WaitForPendingAsync(agent, call.ToolCallId!);
        Assert.True(await agent.SubmitClientToolResultAsync(call.ToolCallId!, """{"approved":true}"""));
        await drain;

        Assert.Contains("\"approved\":true", Result(events), StringComparison.Ordinal);
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
        Assert.Null(events[^1].Stopped);
    }

    // An answer that arrives BEFORE the next pull invokes the tool is kept: the call was announced when its
    // toolCallStart was projected, so the tool reads the early answer instead of waiting for a second one.
    [Fact]
    public async Task ConfirmAction_ResultPostedBeforeTheToolRuns_IsKept()
    {
        var agent = Agent("ct-early-user/conv");
        var events = new List<AgentStreamEvent>();
        await using var stream = Chat(agent, ConfirmPrompt()).GetAsyncEnumerator();

        var call = await ReadToClientCallAsync(stream, events);
        Assert.True(await agent.SubmitClientToolResultAsync(call.ToolCallId!, """{"approved":false}"""));
        await DrainRestAsync(stream, events);

        Assert.Contains("\"approved\":false", Result(events), StringComparison.Ordinal);
    }

    // AC2: no browser — a background-style run (interactive: false) or a browser that advertised nothing — answers
    // "unavailable" at once, and the call is not flagged for any browser.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ConfirmAction_NoBrowser_ReturnsUnavailableImmediately(bool interactive, bool advertised)
    {
        var agent = Agent($"ct-nobrowser-{interactive}-{advertised}-user/conv");
        var started = DateTimeOffset.UtcNow;

        var events = await CollectAsync(Chat(agent, ConfirmPrompt(), advertised ? AllClientTools : [], interactive));

        Assert.Contains("unavailable", Result(events), StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.ClientTool == true);
        Assert.Contains(events, e => e.Type == AgentStreamEventType.ToolCallStart && e.ToolName == ClientToolLimits.ConfirmAction);
        Assert.True(DateTimeOffset.UtcNow - started < TestSiloConfigurator.ClientToolWait, "it waited for nobody");
    }

    // AC3: nobody answers — the wait ends with a timeout RESULT, and the turn still completes and persists.
    [Fact]
    public async Task ConfirmAction_Timeout_ReturnsTimeoutAndTurnCompletes()
    {
        var agent = Agent("ct-timeout-user/conv");

        var events = await CollectAsync(Chat(agent, ConfirmPrompt()));

        Assert.Contains("timeout", Result(events), StringComparison.Ordinal);
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
        var history = await agent.GetHistoryAsync();
        Assert.Equal(["user", "assistant"], history.Select(t => t.Role));
        Assert.Contains("timeout", history[1].ToolCalls![0].ToolResult, StringComparison.Ordinal);
        Assert.Empty(await agent.GetPendingClientToolCallsAsync());
    }

    // AC6 + AC16c: Stop while the tool waits ends the turn as stopped; the session holds no call without its result,
    // the late answer is refused, and the next turn runs normally.
    [Fact]
    public async Task Stop_WhileWaitingForAClientTool_StopsTheTurn()
    {
        var agent = Agent("ct-stop-user/conv");
        var events = new List<AgentStreamEvent>();
        await using var stream = Chat(agent, ConfirmPrompt()).GetAsyncEnumerator();

        var call = await ReadToClientCallAsync(stream, events);
        var drain = DrainRestAsync(stream, events);
        await WaitForPendingAsync(agent, call.ToolCallId!);
        await agent.StopAsync();
        await drain;

        Assert.True(events[^1].Stopped);
        Assert.False(await agent.SubmitClientToolResultAsync(call.ToolCallId!, """{"approved":true}"""));
        var history = await agent.GetHistoryAsync();
        Assert.DoesNotContain(history, t => t.ToolCalls?.Any(c => c.ToolResult is null) == true);

        var next = await CollectAsync(agent.ChatAsync("hi", null));
        Assert.Null(next[^1].Stopped);
        Assert.Contains("assistant", (await agent.GetHistoryAsync()).Select(t => t.Role));
    }

    // AC7: a background run started in a gap of the interactive turn never reaches the user's browser. Its call
    // answers "unavailable", and the interactive turn's pending list never shows it.
    [Fact]
    public async Task BackgroundRun_CannotRouteToTheInteractiveBroker()
    {
        var agent = Agent("ct-background-user/conv");
        var events = new List<AgentStreamEvent>();
        await using var stream = Chat(agent, ConfirmPrompt()).GetAsyncEnumerator();
        var call = await ReadToClientCallAsync(stream, events);

        // The interactive turn is parked between pulls, so the background run executes now, in the gap, with the
        // interactive broker open. It must complete BEFORE the interactive call is answered, or this proves nothing.
        await agent.EnqueuePromptAsync(ConfirmPrompt("Background action"), null);
        var history = await WaitForTurnsAsync(agent, 2);
        var background = history.Last(t => t.Role == "assistant");
        Assert.Contains("unavailable", background.ToolCalls![0].ToolResult, StringComparison.Ordinal);

        var pending = await agent.GetPendingClientToolCallsAsync();
        Assert.Equal([call.ToolCallId], pending.Select(p => p.ToolCallId));

        Assert.True(await agent.SubmitClientToolResultAsync(call.ToolCallId!, """{"approved":true}"""));
        await DrainRestAsync(stream, events);
        Assert.Contains("\"approved\":true", Result(events), StringComparison.Ordinal);
    }

    // AC5 (grain half): the pending list carries what the browser needs to answer after its stream died.
    [Fact]
    public async Task PendingToolCalls_ListTheWaitingCall_WithItsInput()
    {
        var agent = Agent("ct-pending-user/conv");
        var events = new List<AgentStreamEvent>();
        await using var stream = Chat(agent, ConfirmPrompt("Book the flight")).GetAsyncEnumerator();
        var call = await ReadToClientCallAsync(stream, events);
        var drain = DrainRestAsync(stream, events);

        var pending = Assert.Single(await WaitForPendingAsync(agent, call.ToolCallId!));
        Assert.Equal((ClientToolLimits.ConfirmAction, call.ToolInput), (pending.ToolName, pending.ToolInput));

        await agent.SubmitClientToolResultAsync(call.ToolCallId!, """{"approved":true}""");
        await drain;
        Assert.Empty(await agent.GetPendingClientToolCallsAsync());
    }

    [Fact]
    public async Task SubmitResult_UnknownCallId_ReturnsFalse()
    {
        Assert.False(await Agent("ct-unknown-user/conv").SubmitClientToolResultAsync("no-such-call", "{}"));
    }

    // AC16c: after the wait timed out, a late answer is refused rather than silently accepted.
    [Fact]
    public async Task SubmitResult_AfterTimeout_ReturnsFalse()
    {
        var agent = Agent("ct-late-user/conv");
        var events = await CollectAsync(Chat(agent, ConfirmPrompt()));
        var callId = events.First(e => e.ClientTool == true).ToolCallId!;

        Assert.False(await agent.SubmitClientToolResultAsync(callId, """{"approved":true}"""));
    }

    // AC8: an oversized answer is cut to the cap before the model (and the 2 MB session) sees it.
    [Fact]
    public async Task SubmitResult_IsTruncated()
    {
        var agent = Agent("ct-truncate-user/conv");
        var events = new List<AgentStreamEvent>();
        await using var stream = Chat(agent, FakeChatClient.CallToolPrompt(ClientToolLimits.GetBrowserContext, new { }))
            .GetAsyncEnumerator();
        var call = await ReadToClientCallAsync(stream, events);

        var huge = JsonSerializer.Serialize(new { selection = new string('x', ClientToolLimits.MaxResultChars * 2) });
        Assert.True(await agent.SubmitClientToolResultAsync(call.ToolCallId!, huge));
        await DrainRestAsync(stream, events);

        var stored = (await agent.GetHistoryAsync())[^1].ToolCalls![0].ToolResult!;
        var result = JsonDocument.Parse(stored).RootElement.GetString()!;
        Assert.Equal(ClientToolLimits.MaxResultChars, result.Length);
    }

    // AC10: an artifact the conversation does not have is refused by the server, at once, and the browser is never
    // asked (the call is not flagged).
    [Fact]
    public async Task ControlArtifactPane_UnknownArtifact_FailsWithoutWaiting()
    {
        var agent = Agent("ct-pane-user/conv");
        var started = DateTimeOffset.UtcNow;

        var events = await CollectAsync(Chat(agent, FakeChatClient.CallToolPrompt(
            ClientToolLimits.ControlArtifactPane, new { action = "open", artifactId = "art_nope" })));

        Assert.Contains("There is no artifact", Result(events), StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.ClientTool == true);
        Assert.True(DateTimeOffset.UtcNow - started < TestSiloConfigurator.ClientToolWait, "it waited for the browser");
    }

    // An action that needs no artifact goes to the browser, and its answer comes back.
    [Fact]
    public async Task ControlArtifactPane_Close_IsAskedOfTheBrowser()
    {
        var agent = Agent("ct-pane-close-user/conv");
        var events = new List<AgentStreamEvent>();
        await using var stream = Chat(agent, FakeChatClient.CallToolPrompt(
            ClientToolLimits.ControlArtifactPane, new { action = "close" })).GetAsyncEnumerator();

        var call = await ReadToClientCallAsync(stream, events);
        Assert.True(await agent.SubmitClientToolResultAsync(call.ToolCallId!, """{"ok":true}"""));
        await DrainRestAsync(stream, events);

        Assert.Contains("\"ok\":true", Result(events), StringComparison.Ordinal);
    }

    // The interleaved pending query is the test's way to know the tool is really waiting.
    private static async Task<IReadOnlyList<PendingClientToolCall>> WaitForPendingAsync(IAgentGrain agent, string callId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var pending = await agent.GetPendingClientToolCallsAsync();
            if (pending.Any(p => p.ToolCallId == callId))
            {
                return pending;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Call {callId} never became pending.");
        return [];
    }

    // EnqueuePromptAsync is [OneWay], so the background turn finishes on its own schedule.
    private static async Task<IReadOnlyList<ChatTurn>> WaitForTurnsAsync(IAgentGrain agent, int count)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var history = await agent.GetHistoryAsync();
            if (history.Count >= count)
            {
                return history;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"The background turn never reached {count} history turns.");
        return [];
    }
}
