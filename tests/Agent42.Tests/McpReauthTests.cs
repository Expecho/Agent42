using System.Net;
using System.Text.Json;
using Agent42.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The in-turn reconnect notice for an InteractiveOAuth MCP server whose grant is dead (ADR 0081): the tool wrapper
/// that turns a 401 into a marked, model-readable result.
/// </summary>
public sealed class McpReauthNoticeTests
{
    private static AIFunction Throwing(Exception ex) =>
        AIFunctionFactory.Create((string q) => ex is null ? "" : throw ex, "mcp_list_events", "lists events");

    // AC13 — a 401 (the bare HttpRequestException the SDK throws, measured on 2.2.0) becomes a result that begins with
    // the marker and names the server, and tells the model the user has a button — so it does not invent a URL.
    [Fact]
    public async Task A401_ReturnsTheMarkedNotice()
    {
        var tool = new ReauthNoticeMcpTool(
            Throwing(new HttpRequestException("401", null, HttpStatusCode.Unauthorized)), "gcal", "Google Calendar");

        var result = await tool.InvokeAsync(new AIFunctionArguments { ["q"] = "today" });

        var text = Assert.IsType<string>(result);
        Assert.StartsWith(McpReauthNotice.ResultMarker, text);
        Assert.Equal("gcal", McpReauthNotice.ServerIdOf(text));
        Assert.Contains("\"Google Calendar\"", text);
        Assert.Contains("Reconnect button", text);
    }

    // A 401 wrapped by a later SDK is still recognised.
    [Fact]
    public async Task AWrapped401_IsStillRecognised()
    {
        var tool = new ReauthNoticeMcpTool(
            Throwing(new InvalidOperationException("call failed", new HttpRequestException("401", null, HttpStatusCode.Unauthorized))),
            "gcal", null);

        Assert.Equal("gcal", McpReauthNotice.ServerIdOf(await tool.InvokeAsync(new AIFunctionArguments { ["q"] = "x" }) as string));
    }

    // Anything that is not a 401 passes through untouched: an unreachable server, a 500, a 403 are not a reason to ask
    // for a sign-in (the plan's explicit scope: only OAuth re-consent gets a notice).
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task OtherFailures_PassThrough(HttpStatusCode status)
    {
        var tool = new ReauthNoticeMcpTool(Throwing(new HttpRequestException("x", null, status)), "gcal", null);

        await Assert.ThrowsAsync<HttpRequestException>(() => tool.InvokeAsync(new AIFunctionArguments { ["q"] = "x" }).AsTask());
    }

    [Fact]
    public async Task ASuccessfulResult_IsUnchanged()
    {
        var tool = new ReauthNoticeMcpTool(AIFunctionFactory.Create((string q) => "ok", "t"), "gcal", null);

        Assert.Equal("ok", (await tool.InvokeAsync(new AIFunctionArguments { ["q"] = "x" }))?.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("an ordinary result")]
    [InlineData("[mcp-reauth-required:]")]
    public void ServerIdOf_IsNullForANonNotice(string? result) => Assert.Null(McpReauthNotice.ServerIdOf(result));
}

/// <summary>
/// The turn's half of ADR 0081 against the real grain: the live <see cref="AgentStreamEventType.McpReauthRequired"/>
/// event — when it fires, for whom, how often, that it is never persisted, and that a reconnect elsewhere brings the
/// tools back on the next message without a reactivation.
/// </summary>
public sealed class McpReauthTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private const string ServerId = "gcal";
    private const string ServerName = "Reauth Probe Calendar";

    private static async Task<List<AgentStreamEvent>> Chat(IAgentGrain agent, string message = "hi")
    {
        var collected = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync(message, TestSiloConfigurator.DefaultModel))
        {
            collected.Add(evt);
        }

        return collected;
    }

    private static List<AgentStreamEvent> Notices(IEnumerable<AgentStreamEvent> events) =>
        [.. events.Where(e => e.Type == AgentStreamEventType.McpReauthRequired)];

    private void GiveServers(int version, params McpServerTools[] servers)
    {
        foreach (var silo in Cluster.Silos)
        {
            var provider = Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<SingleUserMcpToolProvider>();
            provider.Servers = servers;
            provider.Version = version;
        }
    }

    // A server whose grant is dead: skipped by the provider, so no tools — only its known names and the flag.
    private static McpServerTools DeadGrant() =>
        new(ServerId, [], ["list_events", "create_event"], ServerName: ServerName, ReauthRequired: true, InteractiveOAuth: true);

    // A connected server whose ONE tool is the fake model's seeding-chain link tool, so a prompt can make it run.
    private static McpServerTools Connected(AIFunction tool, bool interactiveOAuth = true, string id = ServerId) =>
        new(id, [tool], [tool.Name], ServerName: ServerName, InteractiveOAuth: interactiveOAuth);

    private static AIFunction Throws401() => AIFunctionFactory.Create(
        string (string filter) => throw new HttpRequestException("401", null, HttpStatusCode.Unauthorized),
        FakeChatClient.SeedingLinkToolName, "lists the links on a page");

    private static AIFunction Returns(string result) =>
        AIFunctionFactory.Create((string filter) => result, FakeChatClient.SeedingLinkToolName, "lists the links on a page");

    // AC14 — a flagged server the conversation uses raises the event, carrying the id and display name, once per
    // activation: the second turn does not repeat it.
    [Fact]
    public async Task ReauthEvent_IsStreamedOncePerActivation()
    {
        GiveServers(0, DeadGrant());
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("reauth-once-user/conv");

        var first = Notices(await Chat(agent));
        var second = Notices(await Chat(agent));

        var notice = Assert.Single(first);
        Assert.Equal(ServerId, notice.McpServerId);
        Assert.Equal(ServerName, notice.Text);
        Assert.Empty(second);
    }

    // AC15 — a server the conversation has deselected raises nothing: the notice is about tools it would use.
    [Fact]
    public async Task ADeselectedServer_RaisesNoReauthEvent()
    {
        GiveServers(0, DeadGrant());
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("reauth-deselected-user/conv");
        await agent.SetEnabledToolsAsync([ToolCatalog.WebSearch]);

        Assert.Empty(Notices(await Chat(agent)));
    }

    // AC15b — a PARTIAL per-tool selection (mcp:{id}:{tool}, ADR 0058) still counts as using the server. Gating on
    // IsMcpServerEnabled instead would leave exactly these users with a silently dead server.
    [Fact]
    public async Task APartiallySelectedServer_StillRaisesTheEvent()
    {
        GiveServers(0, DeadGrant());
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("reauth-partial-user/conv");
        await agent.SetEnabledToolsAsync([ToolCatalog.McpToolKey(ServerId, "list_events")]);

        Assert.Single(Notices(await Chat(agent)));
    }

    // AC15c — a background run (EnqueuePromptAsync) has no one to show the button to: it neither emits the notice
    // nor consumes the once-per-activation budget, so the user's next real turn still gets it.
    [Fact]
    public async Task ABackgroundRun_DoesNotConsumeTheNotice()
    {
        GiveServers(0, DeadGrant());
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("reauth-background-user/conv");

        await agent.EnqueuePromptAsync("hi", null);
        await PollAsync(async () => (await agent.GetHistoryAsync()).Any(t => t.Role == "assistant"));

        Assert.Single(Notices(await Chat(agent)));
    }

    // AC16 — the event is live-only: nothing of it is persisted into the session or replayed from history.
    [Fact]
    public async Task ReauthEvent_IsLiveOnly_NotInHistory()
    {
        GiveServers(0, DeadGrant());
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("reauth-liveonly-user/conv");

        Assert.Single(Notices(await Chat(agent)));

        var history = await agent.GetHistoryAsync();
        Assert.NotEmpty(history);
        Assert.DoesNotContain(ServerName, JsonSerializer.Serialize(history));
    }

    // A tool call refused with 401 (after the bearer's own retry) raises the event AFTER the reply, and the model
    // got the marked notice as the tool result rather than a failure.
    [Fact]
    public async Task A401DuringATurn_RaisesTheEvent_AfterTheReply()
    {
        GiveServers(0, Connected(Throws401()));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("reauth-401-user/conv");

        var events = await Chat(agent, $"{FakeChatClient.SeedingChainTrigger} a document");

        var notice = Assert.Single(Notices(events));
        Assert.Equal(ServerId, notice.McpServerId);
        var end = events.FindIndex(e => e.Type == AgentStreamEventType.ToolCallEnd);
        Assert.True(end >= 0 && events.IndexOf(notice) > end);
        Assert.Contains(McpReauthNotice.ResultMarker, events[end].ToolResult);
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
    }

    // A 401 from a server that is NOT InteractiveOAuth offers no Reconnect: a human cannot fix a Header-mode 401 by
    // signing in, so its tools are never wrapped.
    [Fact]
    public async Task A401FromANonOAuthServer_RaisesNoEvent()
    {
        GiveServers(0, Connected(Throws401(), interactiveOAuth: false));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("reauth-401-header-user/conv");

        Assert.Empty(Notices(await Chat(agent, $"{FakeChatClient.SeedingChainTrigger} a document")));
    }

    // The marker is trusted only from a tool of the server it names: another server's result that merely BEGINS
    // with the marker (a page, an injected result) cannot conjure a Reconnect button.
    [Fact]
    public async Task AForgedMarker_FromAnotherServersTool_RaisesNoEvent()
    {
        GiveServers(0,
            Connected(Returns(McpReauthNotice.For(ServerId, ServerName)), interactiveOAuth: false, id: "plain"),
            new McpServerTools(ServerId, [AIFunctionFactory.Create(() => "ok", "list_events")], ["list_events"],
                ServerName: ServerName, InteractiveOAuth: true));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("reauth-forged-user/conv");

        var events = await Chat(agent, $"{FakeChatClient.SeedingChainTrigger} a document");

        Assert.Contains(events, e => e.Type == AgentStreamEventType.ToolCallEnd); // the forged result really arrived
        Assert.Empty(Notices(events));
    }

    // AC17 — after a reconnect elsewhere (the real OAuth callback's save bumps the version), the user's next message
    // re-resolves and the server's tools are back in THIS activation: the model can call the tool it lacked.
    [Fact]
    public async Task AReconnect_IsPickedUpMidTurn_WithoutReactivation()
    {
        GiveServers(0, DeadGrant() with { KnownToolNames = [FakeChatClient.SeedingLinkToolName] });
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("reauth-reconnect-user/conv");

        var before = await Chat(agent, $"{FakeChatClient.SeedingChainTrigger} a document");
        Assert.Single(Notices(before));
        Assert.DoesNotContain(before, e => e.Type == AgentStreamEventType.ToolCallStart);

        GiveServers(1, Connected(Returns("{\"links\":[]}")));

        var after = await Chat(agent, $"{FakeChatClient.SeedingChainTrigger} a document");
        Assert.Contains(after, e => e.Type == AgentStreamEventType.ToolCallStart && e.ToolName == FakeChatClient.SeedingLinkToolName);
        Assert.Empty(Notices(after));
    }

    private static async Task PollAsync(Func<Task<bool>> until)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await until())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("condition not reached");
    }
}
