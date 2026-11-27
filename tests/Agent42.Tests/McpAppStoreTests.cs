using Agent42.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The MCP App store (plan cc-12; ADR 0133): store, cap, reconcile and the history projection, over a live record list
/// and a stand-in for the session's call ids.
/// </summary>
public sealed class McpAppStoreTests
{
    private sealed class Rig
    {
        public List<McpAppRecord> Records { get; } = [];

        public IReadOnlySet<string>? Live { get; set; } = new HashSet<string>();

        public McpAppStore Store { get; }

        public Rig() => Store = new McpAppStore(() => Records, () => Live, new InMemoryAttachmentStore(), NullLogger.Instance, "u/c");
    }

    private static McpAppResult Result(string json = "{}") => new(new McpAppRef("srv", "tool", "ui://view"), json);

    private static TurnContext TurnWith(bool ephemeral, params string[] callIds)
    {
        var turn = TurnContext.ForChat(ephemeral, interactive: true, broker: null);
        turn.SetAppsSupported(true);
        foreach (var id in callIds)
        {
            turn.RecordAppResult(id, Result($"{{\"id\":\"{id}\"}}"));
        }

        return turn;
    }

    [Fact]
    public async Task Store_RecordsEachResult_AndTheBlobReadsBack()
    {
        var rig = new Rig();

        await rig.Store.StoreAsync(TurnWith(false, "call-1"));

        var record = Assert.Single(rig.Records);
        Assert.Equal("call-1", record.CallId);
        Assert.Equal("{\"id\":\"call-1\"}", await rig.Store.GetResultAsync("call-1"));
        Assert.Null(await rig.Store.GetResultAsync("missing"));
    }

    [Fact]
    public async Task Store_SkipsAnIncognitoTurn()
    {
        var rig = new Rig();

        await rig.Store.StoreAsync(TurnWith(true, "call-1"));

        Assert.Empty(rig.Records);
    }

    [Fact]
    public async Task Store_ReplacesTheSameCall_AndDropsTheOldestPastTheCap()
    {
        var rig = new Rig();
        await rig.Store.StoreAsync(TurnWith(false, "call-0"));
        await rig.Store.StoreAsync(TurnWith(false, "call-0"));
        Assert.Single(rig.Records);

        for (var i = 1; i <= McpToolLimits.MaxAppsPerConversation; i++)
        {
            await rig.Store.StoreAsync(TurnWith(false, $"call-{i}"));
        }

        Assert.Equal(McpToolLimits.MaxAppsPerConversation, rig.Records.Count);
        Assert.DoesNotContain(rig.Records, r => r.CallId == "call-0");
        Assert.Equal($"call-{McpToolLimits.MaxAppsPerConversation}", rig.Records[^1].CallId);
    }

    [Fact]
    public async Task Reconcile_DropsRecordsWhoseCallLeftTheSession()
    {
        var rig = new Rig();
        await rig.Store.StoreAsync(TurnWith(false, "keep", "gone"));
        rig.Live = new HashSet<string> { "keep" };

        rig.Store.Reconcile();

        Assert.Equal(["keep"], rig.Records.Select(r => r.CallId));
    }

    [Fact]
    public async Task Reconcile_DropsNothing_WhenTheHistoryIsUnreadable()
    {
        var rig = new Rig();
        await rig.Store.StoreAsync(TurnWith(false, "a", "b"));
        rig.Live = null;

        rig.Store.Reconcile();

        Assert.Equal(2, rig.Records.Count);
    }

    [Fact]
    public async Task WithApps_AttachesTheViewReferenceToTheMatchingCallOnly()
    {
        var rig = new Rig();
        await rig.Store.StoreAsync(TurnWith(false, "app-call"));
        var turn = new ChatTurn("assistant", "hi", ToolCalls:
        [
            new ChatToolCall("app-call", "tool", null, null),
            new ChatToolCall("other", "tool", null, null),
        ]);

        var projected = rig.Store.WithApps(turn);

        Assert.Equal(new McpAppRef("srv", "tool", "ui://view"), projected.ToolCalls![0].McpApp);
        Assert.Null(projected.ToolCalls[1].McpApp);
    }

    [Fact]
    public void WithApps_ReturnsTheSameTurn_WhenThereAreNoRecords()
    {
        var rig = new Rig();
        var turn = new ChatTurn("assistant", "hi", ToolCalls: [new ChatToolCall("c", "tool", null, null)]);

        Assert.Same(turn, rig.Store.WithApps(turn));
    }
}
