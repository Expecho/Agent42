using System.Runtime.CompilerServices;
using System.Text.Json;
using Agent42.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>
/// The Agents-layer seam of the copilot agent kind, without a grain: the history mirror
/// (<see cref="CopilotChatAgent"/>), the scheduler marshalling (<see cref="SchedulerBoundAIFunction"/>), the runtime's
/// tool-name rule, <c>read_skill_resource</c>, and the session-preserving stand-in.
/// </summary>
public sealed class CopilotSeamTests
{
    // A run that narrates, calls a tool, then answers: the shape a Copilot turn arrives in.
    private static (CopilotChatAgent Agent, ScriptedChatClient Client) NarratingAgent(string toolResult = "KIWI-9")
    {
        var client = new ScriptedChatClient();
        var tool = AIFunctionFactory.Create(() => toolResult, "get_word", "Returns the word.");
        var inner = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions { Tools = [tool] },
            ChatHistoryProvider = new InMemoryChatHistoryProvider(
                new InMemoryChatHistoryProviderOptions { StateKey = ChatClientCopilotAgentFactory.TranscriptStateKey }),
        });
        return (new CopilotChatAgent(inner, "Pilot"), client);
    }

    private static List<ChatMessage> Mirror(AgentSession session)
        => session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) && messages is not null ? messages : [];

    // AC5 (unit half): a streamed run is mirrored as the user message, the tool call and result, and ONE assistant
    // message holding exactly the text that streamed.
    [Fact]
    public async Task Mirror_Streaming_StoresOneAssistantTurnWithItsToolCalls()
    {
        var (agent, _) = NarratingAgent();
        var session = await agent.CreateSessionAsync();

        var streamed = string.Concat(
            await agent.RunStreamingAsync("what is the word?", session).Select(u => u.Text).ToListAsync());

        var mirror = Mirror(session);
        Assert.Equal(ChatRole.User, mirror[0].Role);
        Assert.Equal("what is the word?", mirror[0].Text);
        Assert.Single(mirror, m => m.Contents.Any(c => c is FunctionCallContent { Name: "get_word" }));
        Assert.Single(mirror, m => m.Contents.Any(c => c is FunctionResultContent));
        var answer = Assert.Single(mirror, m => m.Role == ChatRole.Assistant && !string.IsNullOrEmpty(m.Text));
        Assert.Same(answer, mirror[^1]);
        Assert.Equal(streamed, answer.Text);
        Assert.Equal("Calling get_word. The word is KIWI-9.", answer.Text);
        Assert.Equal("Pilot", answer.AuthorName);
    }

    // The reminder path runs RunAsync, not the stream: same mirror shape.
    [Fact]
    public async Task Mirror_NonStreaming_StoresTheSameShape()
    {
        var (agent, _) = NarratingAgent();
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("what is the word?", session);

        var mirror = Mirror(session);
        Assert.Equal(4, mirror.Count);
        Assert.Equal("Calling get_word. The word is KIWI-9.", mirror[^1].Text);
    }

    // A cancelled run mirrors nothing, so the grain's stopped-turn hand-append (ADR 0078) stays the one writer.
    [Fact]
    public async Task Mirror_CancelledRun_StoresNothing()
    {
        var (agent, client) = NarratingAgent();
        var session = await agent.CreateSessionAsync();
        using var cts = new CancellationTokenSource();
        client.OnFirstChunk = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in agent.RunStreamingAsync("what is the word?", session, cancellationToken: cts.Token))
            {
            }
        });

        Assert.Empty(Mirror(session));
    }

    // The mirror is part of the 2 MB grain session (ADR 0004), so a huge tool result is cut there even though
    // Copilot's own transcript keeps it whole.
    [Fact]
    public async Task Mirror_BoundsALargeToolResult()
    {
        var (agent, _) = NarratingAgent(new string('x', CopilotLimits.MaxMirroredToolResultLength * 2));
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("what is the word?", session);

        var result = Mirror(session).SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        var text = Assert.IsType<string>(result.Result);
        Assert.EndsWith(CopilotLimits.TruncationMarker, text, StringComparison.Ordinal);
        Assert.Equal(CopilotLimits.MaxMirroredToolResultLength + CopilotLimits.TruncationMarker.Length, text.Length);
    }

    // The mirror survives the session's own serialize/deserialize, the path the grain persists it on.
    [Fact]
    public async Task Mirror_RoundTripsThroughTheSerializedSession()
    {
        var (agent, _) = NarratingAgent();
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("what is the word?", session);

        var json = JsonSerializer.Serialize(await agent.SerializeSessionAsync(session));
        var restored = await agent.DeserializeSessionAsync(JsonSerializer.Deserialize<JsonElement>(json));

        Assert.Equal(Mirror(session).Select(m => m.Text), Mirror(restored).Select(m => m.Text));
    }

    // AC8 (unit half): a tool invoked from a foreign thread, the way the Copilot runtime calls custom tools, runs on
    // the captured scheduler. Without the wrapper it would run on the thread pool.
    [Fact]
    public async Task SchedulerBound_InvokedFromForeignThread_RunsOnTheCapturedScheduler()
    {
        var exclusive = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        TaskScheduler? ranOn = null;
        var tool = new SchedulerBoundAIFunction(
            AIFunctionFactory.Create(() => { ranOn = TaskScheduler.Current; return "ok"; }, "probe"), exclusive,
            () => null);

        var result = await Task.Run(async () => await tool.InvokeAsync());

        Assert.Equal("ok", result?.ToString());
        Assert.Same(exclusive, ranOn);
    }

    // AC5 (ADR 0106): the Copilot runtime calls a tool on its RPC thread, where the run's context does not flow. The
    // call is made under SuppressFlow, the honest stand-in for that thread: a plain Task.Run flows the caller's
    // context and would pass without the re-establish. With exactly one Copilot run in flight, the tool sees it.
    [Fact]
    public async Task CopilotTool_SeesItsRunsTurnContext()
    {
        var run = TurnContext.ForBackground();
        var tool = ContextProbe(out var seen, soleRun: () => run);

        await TurnContext.RunInAsync(run, async () => await InvokeWithoutFlowAsync(tool));

        Assert.Same(run, seen());
    }

    // AC5: with two Copilot runs in flight, nothing says which one a call belongs to, so the tool gets the closed
    // context (ephemeral, so every trace-leaving tool refuses) rather than the wrong run's budget.
    [Fact]
    public async Task CopilotTool_TwoRunsInFlight_Refuses()
    {
        var tool = ContextProbe(out var seen, soleRun: () => null);

        await TurnContext.RunInAsync(TurnContext.ForBackground(), async () => await InvokeWithoutFlowAsync(tool));

        Assert.Same(TurnContext.Closed, seen());
    }

    // When the context DID flow (an SDK that carries the ExecutionContext), it is kept over the registry.
    [Fact]
    public async Task CopilotTool_KeepsAFlowedContext()
    {
        var run = TurnContext.ForBackground();
        var tool = ContextProbe(out var seen, soleRun: () => null);

        await TurnContext.RunInAsync(run, async () => await Task.Run(async () => await tool.InvokeAsync()));

        Assert.Same(run, seen());
    }

    private static SchedulerBoundAIFunction ContextProbe(out Func<TurnContext?> seen, Func<TurnContext?> soleRun)
    {
        TurnContext? observed = null;
        seen = () => observed;
        return new SchedulerBoundAIFunction(
            AIFunctionFactory.Create(() => { observed = TurnContext.CurrentOrClosed; return "ok"; }, "probe"),
            new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler,
            soleRun);
    }

    private static async Task<object?> InvokeWithoutFlowAsync(AIFunction tool)
    {
        Task<object?> call;
        using (ExecutionContext.SuppressFlow())
        {
            call = Task.Run(async () =>
            {
                Assert.Same(TurnContext.Closed, TurnContext.CurrentOrClosed);
                return await tool.InvokeAsync();
            });
        }

        return await call;
    }

    // The runtime rejects a custom tool whose name has a '.' or ':' at session.create, which fails every turn, and
    // the provider rejects one longer than 128 (measured).
    [Theory]
    [InlineData("get_word", true)]
    [InlineData("get-word", true)]
    [InlineData("get.word", false)]
    [InlineData("srv:get_word", false)]
    [InlineData("", false)]
    public void ToolName_FollowsTheRuntimeRule(string name, bool valid)
        => Assert.Equal(valid, CopilotLimits.IsValidToolName(name));

    [Fact]
    public void ToolName_LengthLimitIs128()
    {
        Assert.True(CopilotLimits.IsValidToolName(new string('w', 128)));
        Assert.False(CopilotLimits.IsValidToolName(new string('w', 129)));
    }

    // read_skill_resource reads inside a selected skill's folder only, and bounds what it returns.
    [Fact]
    public void ReadSkillResource_ReadsInsideTheSkillOnly()
    {
        var root = Directory.CreateTempSubdirectory("copilot-skill-").FullName;
        try
        {
            var skillDir = Directory.CreateDirectory(Path.Combine(root, "fruitfacts", "references")).Parent!.FullName;
            File.WriteAllText(Path.Combine(skillDir, "references", "fruit.md"), "DRAGONFRUIT-7");
            File.WriteAllText(Path.Combine(skillDir, "references", "big.md"), new string('y', SkillToolLimits.MaxResourceTextLength + 10));
            File.WriteAllText(Path.Combine(root, "secret.txt"), "outside");
            IReadOnlyList<SkillInfo> skills = [new("fruitfacts", "", skillDir)];

            Assert.Equal("DRAGONFRUIT-7", ReadSkillResourceTool.Read(skills, "FruitFacts", "references/fruit.md"));
            Assert.EndsWith(SkillToolLimits.TruncationMarker,
                ReadSkillResourceTool.Read(skills, "fruitfacts", "references/big.md"), StringComparison.Ordinal);
            Assert.DoesNotContain("outside", ReadSkillResourceTool.Read(skills, "fruitfacts", "../secret.txt"), StringComparison.Ordinal);
            Assert.DoesNotContain("outside", ReadSkillResourceTool.Read(skills, "fruitfacts", Path.Combine(root, "secret.txt")), StringComparison.Ordinal);
            Assert.StartsWith("No selected skill", ReadSkillResourceTool.Read(skills, "other", "references/fruit.md"), StringComparison.Ordinal);
            Assert.Contains("has no file", ReadSkillResourceTool.Read(skills, "fruitfacts", "references/none.md"), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The stand-in used while a copilot chat cannot get its agent keeps the session's transcript pointer and mirror
    // exactly, so a transient outage cannot orphan Copilot's transcript or empty the chat.
    [Fact]
    public async Task UnavailableAgent_PreservesSessionIdAndMirror_AndRefusesToRun()
    {
        var bag = new AgentSessionStateBag();
        var stored = JsonSerializer.SerializeToElement(new { sessionId = "abc-123", stateBag = bag.Serialize() });
        var agent = new CopilotUnavailableAgent("no runtime here");

        var session = await agent.DeserializeSessionAsync(stored);
        session.SetInMemoryChatHistory([new ChatMessage(ChatRole.User, "hi")]);
        var json = await agent.SerializeSessionAsync(session);

        Assert.Equal("abc-123", json.GetProperty("sessionId").GetString());
        var restored = await agent.DeserializeSessionAsync(json);
        Assert.Equal("hi", Assert.Single(Mirror(restored)).Text);
        var ex = await Assert.ThrowsAsync<CopilotUnavailableException>(() => agent.RunAsync("x", restored));
        Assert.Equal("no runtime here", ex.Message);
    }

    /// <summary>
    /// Answers the first call with narration plus a <c>get_word</c> call, and the second (after the tool result) with
    /// the answer, streamed in two chunks so a test can cancel in between.
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        public Action? OnFirstChunk { get; set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var answered = messages.Any(m => m.Contents.Any(c => c is FunctionResultContent));
            if (!answered)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "Calling get_word. ") { MessageId = "m1" };
                OnFirstChunk?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("c1", "get_word")]) { MessageId = "m1" };
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "The word is ") { MessageId = "m2" };
            yield return new ChatResponseUpdate(ChatRole.Assistant, "KIWI-9.") { MessageId = "m2" };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
