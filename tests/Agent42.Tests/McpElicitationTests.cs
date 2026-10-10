using System.Text.Json;
using Agent42.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// MCP elicitation through a real grain (ADR 0095). The fake MCP tool calls its group's real
/// <see cref="McpElicitationRouter"/> from a context the call's async local does not flow into, which is what the
/// SDK does on a stateful HTTP server (measured), so these tests run the single-in-flight fallback. The grain-side
/// <see cref="ElicitingMcpTool"/> opens the scope, the run's broker queues the elicitation, the stream emits it while
/// the tool is still running, and the answer comes back through <see cref="IAgentGrain.SubmitClientToolResultAsync"/>.
/// </summary>
public sealed class McpElicitationTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private const string ToolName = "elicit_tool";

    private const string ServerName = "Elicit Server";

    private static readonly string[] Advertised = [ClientToolLimits.McpElicitation];

    private static readonly McpElicitationRequest NameForm = new(
        McpElicitationLimits.FormMode,
        "Your name?",
        """{"type":"object","properties":{"name":{"type":"string","maxLength":5000}},"required":["name"]}""");

    private IAgentGrain Agent(string key) => Cluster.GrainFactory.GetGrain<IAgentGrain>(key);

    private static string Prompt() => FakeChatClient.CallToolPrompt(ToolName, new { q = "x" });

    // A server whose one tool elicits NameForm and returns "<action>|<content>" plus records what the router answered.
    private (McpElicitationRouter Router, TaskCompletionSource<McpElicitationAnswer> Answered) GiveElicitingServer(
        Func<AIFunction>? tool = null)
    {
        var router = new McpElicitationRouter();
        var answered = new TaskCompletionSource<McpElicitationAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var function = tool?.Invoke() ?? AIFunctionFactory.Create(
            async (string q, CancellationToken cancellationToken) =>
            {
                Task<McpElicitationAnswer> routed;
                using (ExecutionContext.SuppressFlow())
                {
                    routed = Task.Run(() => router.RouteAsync(NameForm, CancellationToken.None));
                }

                _ = routed.ContinueWith(t => answered.TrySetResult(t.Result), TaskScheduler.Default);
                var answer = await routed.WaitAsync(cancellationToken);
                return $"{answer.Action}|{answer.ContentJson}";
            },
            ToolName,
            "asks the user for their name");
        GiveServers(new McpServerTools(
            "elicit-srv", [function], [function.Name], ServerName: ServerName, Elicitation: router));
        return (router, answered);
    }

    private void GiveServers(params McpServerTools[] servers)
    {
        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<SingleUserMcpToolProvider>().Servers = servers;
        }
    }

    private static async Task<AgentStreamEvent> ReadToElicitationAsync(
        IAsyncEnumerator<AgentStreamEvent> stream, List<AgentStreamEvent> events)
    {
        while (await stream.MoveNextAsync())
        {
            events.Add(stream.Current);
            if (stream.Current.Type == AgentStreamEventType.McpElicitation)
            {
                return stream.Current;
            }
        }

        Assert.Fail("The turn ended without an elicitation.");
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

    private static string Result(IEnumerable<AgentStreamEvent> events)
    {
        var answer = string.Concat(events.Where(e => e.Type == AgentStreamEventType.TextDelta).Select(e => e.Text));
        var start = answer.IndexOf(FakeChatClient.ScriptedResultOpen, StringComparison.Ordinal);
        var end = answer.LastIndexOf(FakeChatClient.ScriptedResultClose, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"no scripted result in: {answer}");
        return answer[(start + FakeChatClient.ScriptedResultOpen.Length)..end];
    }

    private static string IdOf(AgentStreamEvent elicitation)
        => JsonDocument.Parse(elicitation.Elicitation!).RootElement.GetProperty("id").GetString()!;

    // AC2: the elicitation streams while the MCP tool is still running, anchored to its call, and the submitted
    // answer reaches the server.
    [Fact]
    public async Task Elicitation_MidTool_StreamsAnEvent_AndTheSubmittedAnswerReachesTheServer()
    {
        GiveElicitingServer();
        var agent = Agent("me-flow-user/conv");
        var events = new List<AgentStreamEvent>();
        await using var stream = agent.ChatAsync(Prompt(), null, clientTools: Advertised).GetAsyncEnumerator();

        var elicitation = await ReadToElicitationAsync(stream, events);
        var start = Assert.Single(events, e => e.Type == AgentStreamEventType.ToolCallStart && e.ToolName == ToolName);
        Assert.Equal(start.ToolCallId, elicitation.ToolCallId);
        Assert.Equal(ServerName, elicitation.Text);
        using (var input = JsonDocument.Parse(elicitation.Elicitation!))
        {
            var root = input.RootElement;
            Assert.StartsWith("elicit_", root.GetProperty("id").GetString(), StringComparison.Ordinal);
            Assert.Equal(McpElicitationLimits.FormMode, root.GetProperty("mode").GetString());
            Assert.Equal(JsonValueKind.Object, root.GetProperty("schema").ValueKind);
        }

        Assert.True(await agent.SubmitClientToolResultAsync(
            IdOf(elicitation), """{"action":"accept","content":{"name":"Bob"}}"""));
        await DrainRestAsync(stream, events);

        Assert.Contains("""accept|{"name":"Bob"}""", Result(events), StringComparison.Ordinal);
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
    }

    // AC3: a background run answers cancel without asking anyone.
    [Fact]
    public async Task Elicitation_BackgroundRun_AnswersCancelWithoutAnEvent()
    {
        var (_, answered) = GiveElicitingServer();
        var agent = Agent("me-background-user/conv");

        await agent.EnqueuePromptAsync(Prompt(), null);

        Assert.Equal(McpElicitationLimits.Cancel, (await answered.Task.WaitAsync(TimeSpan.FromSeconds(10))).Action);
    }

    // AC3: the agent-MCP surface drains ChatAsync with interactive: false (AgentMcpRunner); even a client-tool list
    // gets no broker there.
    [Fact]
    public async Task Elicitation_AgentMcpRun_AnswersCancelWithoutAnEvent()
    {
        GiveElicitingServer();
        var agent = Agent("me-agentmcp-user/conv");

        var events = await CollectAsync(agent.ChatAsync(Prompt(), null, interactive: false, clientTools: Advertised));

        Assert.StartsWith("cancel|", Result(events), StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.McpElicitation);
    }

    // AC3: a browser that did not advertise mcp_elicitation is never asked, even though it runs client tools.
    [Fact]
    public async Task Elicitation_BrowserDidNotAdvertise_AnswersCancel()
    {
        GiveElicitingServer();
        var agent = Agent("me-noadvert-user/conv");
        var started = DateTimeOffset.UtcNow;

        var events = await CollectAsync(agent.ChatAsync(Prompt(), null, clientTools: [ClientToolLimits.ConfirmAction]));

        Assert.StartsWith("cancel|", Result(events), StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.McpElicitation);
        Assert.True(DateTimeOffset.UtcNow - started < TestSiloConfigurator.ClientToolWait, "it waited for nobody");
    }

    // AC5: nobody answers. The wait ends with cancel and the turn completes.
    [Fact]
    public async Task Elicitation_Timeout_AnswersCancel_AndTheTurnCompletes()
    {
        GiveElicitingServer();
        var agent = Agent("me-timeout-user/conv");

        var events = await CollectAsync(agent.ChatAsync(Prompt(), null, clientTools: Advertised));

        Assert.Single(events, e => e.Type == AgentStreamEventType.McpElicitation);
        Assert.StartsWith("cancel|", Result(events), StringComparison.Ordinal);
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
        Assert.Empty(await agent.GetPendingClientToolCallsAsync());
    }

    // AC5: Stop while the elicitation waits ends the turn as stopped, the server gets cancel, and a late answer 404s.
    [Fact]
    public async Task Elicitation_Stop_CancelsTheWait()
    {
        var (_, answered) = GiveElicitingServer();
        var agent = Agent("me-stop-user/conv");
        var events = new List<AgentStreamEvent>();
        await using var stream = agent.ChatAsync(Prompt(), null, clientTools: Advertised).GetAsyncEnumerator();
        var elicitation = await ReadToElicitationAsync(stream, events);

        var drain = DrainRestAsync(stream, events);
        await agent.StopAsync();
        await drain;

        Assert.True(events[^1].Stopped);
        Assert.Equal(McpElicitationLimits.Cancel, (await answered.Task.WaitAsync(TimeSpan.FromSeconds(10))).Action);
        Assert.False(await agent.SubmitClientToolResultAsync(IdOf(elicitation), """{"action":"accept"}"""));
    }

    // AC6: recovery lists a waiting elicitation under its own id, with the same JSON the event carried.
    [Fact]
    public async Task Elicitation_PendingCalls_ListIt_AsMcpElicitation()
    {
        GiveElicitingServer();
        var agent = Agent("me-pending-user/conv");
        var events = new List<AgentStreamEvent>();
        await using var stream = agent.ChatAsync(Prompt(), null, clientTools: Advertised).GetAsyncEnumerator();
        var elicitation = await ReadToElicitationAsync(stream, events);

        var pending = Assert.Single(await agent.GetPendingClientToolCallsAsync());
        Assert.Equal(
            (IdOf(elicitation), ClientToolLimits.McpElicitation, elicitation.Elicitation),
            (pending.ToolCallId, pending.ToolName, pending.ToolInput));

        await agent.SubmitClientToolResultAsync(pending.ToolCallId, """{"action":"decline"}""");
        await DrainRestAsync(stream, events);
        Assert.StartsWith("decline|", Result(events), StringComparison.Ordinal);
    }

    // AC7: content that does not match the schema is never forwarded; the server gets cancel.
    [Fact]
    public async Task Elicitation_InvalidContent_AnswersCancel()
    {
        GiveElicitingServer();
        var agent = Agent("me-invalid-user/conv");
        var events = new List<AgentStreamEvent>();
        await using var stream = agent.ChatAsync(Prompt(), null, clientTools: Advertised).GetAsyncEnumerator();
        var elicitation = await ReadToElicitationAsync(stream, events);

        Assert.True(await agent.SubmitClientToolResultAsync(
            IdOf(elicitation), """{"action":"accept","content":{"name":5,"extra":"x"}}"""));
        await DrainRestAsync(stream, events);

        Assert.StartsWith("cancel|", Result(events), StringComparison.Ordinal);
    }

    // AC7: an answer over the cap becomes cancel rather than being cut (cut JSON is corrupt).
    [Fact]
    public async Task Elicitation_OversizedAnswer_AnswersCancel_NotTruncated()
    {
        GiveElicitingServer();
        var agent = Agent("me-oversize-user/conv");
        var events = new List<AgentStreamEvent>();
        await using var stream = agent.ChatAsync(Prompt(), null, clientTools: Advertised).GetAsyncEnumerator();
        var elicitation = await ReadToElicitationAsync(stream, events);

        var huge = JsonSerializer.Serialize(new
        {
            action = "accept",
            content = new { name = new string('x', ClientToolLimits.MaxResultChars) },
        });
        Assert.True(await agent.SubmitClientToolResultAsync(IdOf(elicitation), huge));
        await DrainRestAsync(stream, events);

        Assert.StartsWith("cancel|", Result(events), StringComparison.Ordinal);
    }

    // AC9: a -32042 shows the user the full URL through the run's sink, and gives the model the host only.
    [Fact]
    public async Task UrlElicitationRequired_PresentsTheUrlToTheSink_AndTheResultCarriesHostNotUrl()
    {
        const string url = "https://auth.example.com/secret-path?token=abc";
        GiveElicitingServer(() => AIFunctionFactory.Create(
            string (string q) => throw new McpUrlElicitationRequiredException(
                "sign in", [new McpElicitationRequest(McpElicitationLimits.UrlMode, "Sign in", Url: url)]),
            ToolName,
            "needs a sign-in"));
        var agent = Agent("me-32042-user/conv");
        var events = new List<AgentStreamEvent>();
        await using var stream = agent.ChatAsync(Prompt(), null, clientTools: Advertised).GetAsyncEnumerator();

        var elicitation = await ReadToElicitationAsync(stream, events);
        using (var input = JsonDocument.Parse(elicitation.Elicitation!))
        {
            Assert.Equal(McpElicitationLimits.UrlMode, input.RootElement.GetProperty("mode").GetString());
            Assert.Equal(url, input.RootElement.GetProperty("url").GetString());
            Assert.Equal("auth.example.com", input.RootElement.GetProperty("host").GetString());
        }

        Assert.True(await agent.SubmitClientToolResultAsync(IdOf(elicitation), """{"action":"accept"}"""));
        await DrainRestAsync(stream, events);

        var result = Result(events);
        Assert.Contains("auth.example.com: the user opened it", result, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-path", result, StringComparison.Ordinal);
        Assert.DoesNotContain("token=abc", result, StringComparison.Ordinal);
    }

    // AC5: once the run is over its queue is closed, and a late elicitation is answered cancel at once, never left
    // waiting on the SDK's thread.
    [Fact]
    public async Task Elicitation_AfterTurnEnd_AnswersCancelImmediately()
    {
        var broker = new ClientToolBroker(Advertised);
        broker.Close();
        var sink = new McpElicitationSink(broker, TimeSpan.FromMinutes(5), NullLogger.Instance);

        var answer = await sink.AskAsync(ServerName, "call_1", NameForm, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(McpElicitationLimits.Cancel, answer.Action);
        Assert.Empty(broker.Elicitations.Pending);
    }

    // A server's request text is unbounded by the protocol and would ride the stream and the pending list; an
    // oversized one is answered cancel without being queued.
    [Fact]
    public async Task Elicitation_OversizedRequest_AnswersCancelWithoutAsking()
    {
        var broker = new ClientToolBroker(Advertised);
        var sink = new McpElicitationSink(broker, TimeSpan.FromMinutes(5), NullLogger.Instance);

        var answer = await sink.AskAsync(ServerName, "call_1",
            NameForm with { Message = new string('x', McpElicitationLimits.MaxRequestChars) }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(McpElicitationLimits.Cancel, answer.Action);
        Assert.Empty(broker.Elicitations.TakeNew());
    }

    // The browser goes away while an elicitation waits: the stream is disposed at the elicitation's yield, with the
    // pull still running. The wait must end with cancel, the turn must not fault, and the conversation must stay usable.
    [Fact]
    public async Task Elicitation_ConsumerDisposesTheStream_AnswersCancel_AndTheNextTurnRuns()
    {
        var (_, answered) = GiveElicitingServer();
        var agent = Agent("me-dispose-user/conv");
        var events = new List<AgentStreamEvent>();
        var stream = agent.ChatAsync(Prompt(), null, clientTools: Advertised).GetAsyncEnumerator();
        await ReadToElicitationAsync(stream, events);

        await stream.DisposeAsync();

        Assert.Equal(McpElicitationLimits.Cancel, (await answered.Task.WaitAsync(TimeSpan.FromSeconds(10))).Action);
        var next = await CollectAsync(agent.ChatAsync("hi", null));
        Assert.Equal(AgentStreamEventType.Done, next[^1].Type);
    }

    // AC10: elicitation innermost, URL seeding around it, the reauth notice outermost (ADR 0081).
    [Fact]
    public void ElicitingWrapper_IsInnermost_ReauthStaysOutermost()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        var source = File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Agent42.Agents", "Tools", "AgentGrain.BuildAgent.cs"));

        Assert.Contains(
            "WithReauthNotice(server, WithUrlSeeding(server, WithElicitation(server, t)))", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"name":"Bob"}""", true)]
    [InlineData("""{}""", false)]
    [InlineData("""{"name":5}""", false)]
    [InlineData("""{"name":"Bob","other":1}""", false)]
    [InlineData("""{"name":"Bob","age":200}""", false)]
    [InlineData("""{"name":"Bob","age":42.5}""", false)]
    [InlineData("""{"name":"Bob","age":42,"color":"red","tags":["a"],"ok":true}""", true)]
    [InlineData("""{"name":"Bob","color":"green"}""", false)]
    [InlineData("""{"name":"Bob","tags":["a","z"]}""", false)]
    [InlineData("""[1]""", false)]
    public void Content_IsCheckedAgainstTheSchema(string content, bool valid)
    {
        const string schema = """
            {"type":"object","required":["name"],"properties":{
              "name":{"type":"string","minLength":1,"maxLength":10},
              "age":{"type":"integer","minimum":0,"maximum":150},
              "color":{"type":"string","oneOf":[{"const":"red","title":"Red"},{"const":"blue","title":"Blue"}]},
              "tags":{"type":"array","items":{"type":"string","enum":["a","b"]}},
              "ok":{"type":"boolean"}}}
            """;

        Assert.Equal(valid, McpElicitationContent.IsValid(schema, content));
    }
}
