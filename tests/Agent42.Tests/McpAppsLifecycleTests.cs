using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Storage;

namespace Agent42.Tests;

/// <summary>
/// The replay lifecycle of MCP Apps (ADR 0133): a stored app call replays after a reload, and every way a call can leave
/// the conversation (incognito, the cap, purge, a prune, an edit or regenerate, a fork) leaves no dangling or leaked
/// record. The tool is the real <see cref="TruncatingMcpTool"/>; the fake model calls it with call id
/// <see cref="CallId"/> on every turn.
/// </summary>
public sealed class McpAppsLifecycleTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private const string CallId = "seeding-link-call-1";
    private const string Marker = "STORED-RESULT-MARKER";

    private static readonly McpAppRef AppRef = new("srv-app", FakeChatClient.SeedingLinkToolName, "ui://chart/view.html");

    protected override Task OnInitializeAsync()
    {
        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress)
                .GetRequiredService<SingleUserMcpToolProvider>().Servers =
                [
                    new McpServerTools("srv-app", [AppTool()], [FakeChatClient.SeedingLinkToolName]),
                ];
        }

        return Task.CompletedTask;
    }

    private static AIFunction AppTool() =>
        new TruncatingMcpTool(
            AIFunctionFactory.Create(
                JsonElement (string filter) => JsonDocument.Parse(
                    $$$"""{"content":[{"type":"text","text":"ok"}],"structuredContent":{"m":"{{{Marker}}}"}}""").RootElement,
                FakeChatClient.SeedingLinkToolName,
                "shows a chart"),
            AppRef);

    private IAgentGrain Agent(string key) => Cluster.GrainFactory.GetGrain<IAgentGrain>(key);

    private static async Task ChatAsync(IAgentGrain agent, string message, bool ephemeral = false)
    {
        await foreach (var _ in agent.ChatAsync(
            message, TestSiloConfigurator.DefaultModel, null, ephemeral, true, [ClientToolLimits.McpApp]))
        {
        }
    }

    private static Task ChatWithAppAsync(IAgentGrain agent, bool ephemeral = false)
        => ChatAsync(agent, $"{FakeChatClient.SeedingChainTrigger} a chart", ephemeral);

    private static async Task<(int Count, string Hash)> TokenOf(IAgentGrain agent)
    {
        var users = (await agent.GetHistoryAsync()).Where(t => t.Role == "user").Select(t => t.Text).ToList();
        return (users.Count, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(users.Count == 0 ? "" : users[^1]))));
    }

    private static McpAppRef? AppOf(IEnumerable<ChatTurn> history)
        => history.SelectMany(t => t.ToolCalls ?? []).Select(c => c.McpApp).FirstOrDefault(a => a is not null);

    private async Task<AgentGrainState> StoredAsync(IAgentGrain agent)
    {
        var storage = Cluster.GetSiloServiceProvider(Cluster.Primary.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);
        var state = new GrainState<AgentGrainState>(new AgentGrainState());
        await storage.ReadStateAsync("agent", agent.GetGrainId(), state);
        return state.State!;
    }

    // AC8: after the turn, history replays the view reference on the tool call and the result route's source returns the
    // stored raw result: this is what a reload draws the card from.
    [Fact]
    public async Task StoredTurn_ReplaysTheCard_AndItsRawResult()
    {
        var agent = Agent("apps-life-replay/conv");

        await ChatWithAppAsync(agent);

        Assert.Equal(AppRef, AppOf(await agent.GetHistoryAsync()));
        var stored = await agent.GetMcpAppResultAsync(CallId);
        Assert.Contains(Marker, stored, StringComparison.Ordinal);
        Assert.Contains("structuredContent", stored, StringComparison.Ordinal);
        Assert.Null(await agent.GetMcpAppResultAsync("some-other-call"));
    }

    // AC8: an incognito turn persists no record and no blob; the card was live only.
    [Fact]
    public async Task IncognitoTurn_StoresNothing()
    {
        var agent = Agent("apps-life-incognito/conv");

        await ChatWithAppAsync(agent, ephemeral: true);

        Assert.Null(AppOf(await agent.GetHistoryAsync()));
        Assert.Null(await agent.GetMcpAppResultAsync(CallId));
        Assert.Empty((await StoredAsync(agent)).McpApps);
    }

    // AC17: past the cap the OLDEST record is dropped and that card degrades; the new call is stored.
    [Fact]
    public async Task TheCap_DropsTheOldestRecord()
    {
        var agent = Agent("apps-life-cap/conv");
        await SeedRecordsAsync(agent, McpToolLimits.MaxAppsPerConversation);

        await ChatWithAppAsync(agent);

        var records = (await StoredAsync(agent)).McpApps;
        Assert.Equal(McpToolLimits.MaxAppsPerConversation, records.Count);
        Assert.DoesNotContain(records, r => r.CallId == "old-0");
        Assert.Contains(records, r => r.CallId == "old-1");
        Assert.Equal(CallId, records[^1].CallId);
    }

    // AC17: purge clears the list next to the artifacts.
    [Fact]
    public async Task Purge_ClearsTheRecords()
    {
        var agent = Agent("apps-life-purge/conv");
        await ChatWithAppAsync(agent);
        Assert.Single((await StoredAsync(agent)).McpApps);

        await agent.PurgeAsync();

        Assert.Empty((await StoredAsync(agent)).McpApps);
        Assert.Null(await agent.GetMcpAppResultAsync(CallId));
    }

    // AC17: an edit or regenerate removes the last turn, and the record of its call goes with it. No dangling record
    // and no card on the regenerated turn until it makes its own call.
    [Fact]
    public async Task RemovingTheLastTurn_DropsItsRecord()
    {
        var agent = Agent("apps-life-edit/conv");
        await ChatAsync(agent, "a plain first question");
        await ChatWithAppAsync(agent);
        Assert.Single((await StoredAsync(agent)).McpApps);
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));

        Assert.Empty((await StoredAsync(agent)).McpApps);
        Assert.Null(AppOf(await agent.GetHistoryAsync()));
        Assert.Null(await agent.GetMcpAppResultAsync(CallId));
    }

    // AC17: a record whose call is still in the session survives an unrelated edit of a LATER turn.
    [Fact]
    public async Task RemovingALaterTurn_KeepsAnEarlierRecord()
    {
        var agent = Agent("apps-life-keep/conv");
        await ChatWithAppAsync(agent);
        await ChatAsync(agent, "a plain later question");
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));

        Assert.Single((await StoredAsync(agent)).McpApps);
        Assert.Equal(AppRef, AppOf(await agent.GetHistoryAsync()));
    }

    // AC17: a fork copies no apps: its session still holds the call, but the card degrades to the plain chip and there is
    // no stored result to read. (ConversationFork has no apps field, so nothing can be copied.)
    [Fact]
    public async Task AFork_CarriesNoApps()
    {
        var source = Agent("apps-life-fork/src");
        await ChatWithAppAsync(source);
        var (count, hash) = await TokenOf(source);
        var result = await source.CreateForkAsync(0, count, hash);
        Assert.Equal(HistoryEditOutcome.Ok, result.Outcome);

        var fork = Agent("apps-life-fork/fork");
        Assert.True(await fork.InitializeFromForkAsync(result.Fork!));

        Assert.Contains(await fork.GetHistoryAsync(), t => t.ToolCalls is { Count: > 0 });
        Assert.Null(AppOf(await fork.GetHistoryAsync()));
        Assert.Empty((await StoredAsync(fork)).McpApps);
        Assert.Null(await fork.GetMcpAppResultAsync(CallId));
        Assert.NotNull(AppOf(await source.GetHistoryAsync()));
    }

    // The conversation's session must hold the model's text only: no raw result, so the stored session stays small.
    [Fact]
    public async Task TheStoredSession_HoldsNoRawResult_ButTheBlobDoes()
    {
        var agent = Agent("apps-life-session/conv");

        await ChatWithAppAsync(agent);

        var record = Assert.Single((await StoredAsync(agent)).McpApps);
        Assert.True(record.SizeBytes > 0);
        Assert.Contains(Marker, await agent.GetMcpAppResultAsync(CallId), StringComparison.Ordinal);
    }

    private async Task SeedRecordsAsync(IAgentGrain agent, int count)
    {
        var storage = Cluster.GetSiloServiceProvider(Cluster.Primary.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);
        var state = new GrainState<AgentGrainState>(new AgentGrainState());
        for (var i = 0; i < count; i++)
        {
            state.State.McpApps.Add(new McpAppRecord($"old-{i}", "srv-app", "t", "ui://x/v", $"blob-{i}", 10));
        }

        await storage.WriteStateAsync("agent", agent.GetGrainId(), state);
    }
}
