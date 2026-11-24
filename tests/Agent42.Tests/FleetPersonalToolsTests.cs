using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>The fleet's memory recall, past-chat and push tools (ADR 0144): gating, per-task budgets, user scope, bounds.</summary>
public sealed class FleetPersonalToolsTests
{
    private static readonly ScriptedFleetInteraction Interaction = new();

    private static async Task<string> InvokeAsync(IEnumerable<AIFunction> tools, string name, AIFunctionArguments arguments)
        => (await tools.Single(t => t.Name == name).InvokeAsync(arguments))!.ToString()!;

    private static FleetToolCatalog Catalog(
        IGrainFactory? grains = null, HindsightIntegration? memory = null, IChatSearchIndex? search = null,
        IPushNotificationSender? push = null, ILogger<FleetToolCatalog>? logger = null)
        => new(grains!, logger ?? NullLogger<FleetToolCatalog>.Instance, memory: memory, chatSearch: search, push: push);

    private static Task<FleetToolSet> BuildAsync(FleetToolCatalog catalog, IReadOnlyList<string>? selection, string userId = "alice", string taskId = "t1")
        => catalog.BuildAsync(new FleetToolContext(userId, taskId, "", selection, Interaction), default);

    // ---- memory ----

    [Fact]
    public async Task Memory_NoClient_Absent()
    {
        var set = await BuildAsync(Catalog(FakeUserGrainFactory.Create().Factory, memory: new HindsightIntegration(null)), null);

        Assert.DoesNotContain(set.Tools, t => t.Name == FleetMemoryTools.Name);
    }

    [Fact]
    public async Task Memory_Deselected_Absent()
    {
        var set = await BuildAsync(Catalog(memory: new HindsightIntegration(new RecordingHindsightClient())), [ToolCatalog.WebSearch]);

        Assert.DoesNotContain(set.Tools, t => t.Name == FleetMemoryTools.Name);
    }

    [Fact]
    public async Task Memory_NeverRetains()
    {
        var client = new RecordingHindsightClient();
        client.ReturnsMemories("Prefers C#");
        var set = await BuildAsync(Catalog(memory: new HindsightIntegration(client)), [ToolCatalog.Memory]);

        var text = await InvokeAsync(set.Tools, FleetMemoryTools.Name, new() { ["query"] = "language" });

        Assert.Contains("- Prefers C#", text);
        Assert.Equal(["language"], client.RecallsFor("alice"));
        Assert.Empty(client.Retains);
    }

    [Fact]
    public async Task Memory_ResultCut()
    {
        var client = new RecordingHindsightClient();
        client.ReturnsMemories([.. Enumerable.Range(0, 50).Select(i => new string('m', 500))]);
        var tool = new FleetMemoryTools(client, "alice", NullLogger.Instance).Tool;

        var text = await InvokeAsync([tool], FleetMemoryTools.Name, new() { ["query"] = "x" });

        Assert.True(text.Length < FleetMemoryTools.MaxResultLength + 100, $"{text.Length} characters");
        Assert.Contains("cut off", text);
    }

    // ---- past chats ----

    // Two users with one pancake chat each. The in-memory index is process-wide, so the ids are unique per test.
    private sealed record PastChatWorld(FakeUserGrainFactory Grains, InMemoryChatSearchIndex Index, string Me, string MyChat, string TheirChat);

    private static async Task<PastChatWorld> PastChatsAsync()
    {
        var grains = FakeUserGrainFactory.Create();
        var index = new InMemoryChatSearchIndex();
        var world = new PastChatWorld(grains, index, $"u-{Guid.NewGuid():N}", $"c-{Guid.NewGuid():N}", $"c-{Guid.NewGuid():N}");
        var other = $"u-{Guid.NewGuid():N}";
        grains.Conversations[world.Me] = [new ConversationInfo(world.MyChat, "Pancakes", DateTimeOffset.UtcNow)];
        grains.Conversations[other] = [new ConversationInfo(world.TheirChat, "Secret pancakes", DateTimeOffset.UtcNow)];
        await index.IndexTurnsAsync(world.Me, world.MyChat, [new ChatSearchTurn(0, "user", "my pancake recipe")]);
        await index.IndexTurnsAsync(other, world.TheirChat, [new ChatSearchTurn(0, "user", "their pancake recipe")]);
        return world;
    }

    [Fact]
    public async Task PastChats_OtherUser_NotReturned()
    {
        var world = await PastChatsAsync();
        var set = await BuildAsync(Catalog(world.Grains.Factory, search: world.Index), [ToolCatalog.PastChats], world.Me);

        var found = await InvokeAsync(set.Tools, PastChatLimits.SearchToolName, new() { ["query"] = "pancake recipe" });
        var read = await InvokeAsync(set.Tools, PastChatLimits.ReadToolName, new() { ["conversationId"] = world.TheirChat, ["ordinal"] = 0 });

        Assert.Contains(world.MyChat, found);
        Assert.DoesNotContain(world.TheirChat, found);
        Assert.StartsWith("No conversation with id", read);
    }

    [Fact]
    public async Task PastChats_BudgetPerTask()
    {
        var world = await PastChatsAsync();
        var set = await BuildAsync(Catalog(world.Grains.Factory, search: world.Index), [ToolCatalog.PastChats], world.Me);

        for (var i = 0; i < FleetPastChatTools.MaxCallsPerTask; i++)
        {
            await InvokeAsync(set.Tools, PastChatLimits.SearchToolName, new() { ["query"] = "pancake" });
        }

        var refused = await InvokeAsync(set.Tools, PastChatLimits.SearchToolName, new() { ["query"] = "pancake" });
        var nextTask = await BuildAsync(Catalog(world.Grains.Factory, search: world.Index), [ToolCatalog.PastChats], world.Me, "t2");

        Assert.Contains("the limit", refused);
        Assert.DoesNotContain("the limit", await InvokeAsync(nextTask.Tools, PastChatLimits.SearchToolName, new() { ["query"] = "pancake" }));
    }

    [Fact]
    public async Task PastChats_Deselected_Absent()
    {
        var set = await BuildAsync(Catalog(FakeUserGrainFactory.Create().Factory, search: new InMemoryChatSearchIndex()), [ToolCatalog.WebSearch]);

        Assert.DoesNotContain(set.Tools, t => t.Name.Contains("past_chat", StringComparison.Ordinal));
    }

    // ---- push ----

    private static FakeUserGrainFactory WithDevice(string userId)
    {
        var grains = FakeUserGrainFactory.Create();
        grains.Devices[userId] = [new PushSubscription("https://push.example/1", "p", "a")];
        return grains;
    }

    [Fact]
    public async Task Push_NoDevices_ToldModel()
    {
        var sender = new RecordingPushNotificationSender();
        var set = await BuildAsync(Catalog(FakeUserGrainFactory.Create().Factory, push: sender), [ToolCatalog.PushNotification]);

        var text = await InvokeAsync(set.Tools, FleetPushTools.Name, new() { ["title"] = "Done", ["body"] = "Report ready." });

        Assert.Contains("no devices", text);
        Assert.Empty(sender.Sends);
    }

    [Fact]
    public async Task Push_LinksToTask()
    {
        var sender = new RecordingPushNotificationSender();
        var set = await BuildAsync(Catalog(WithDevice("alice").Factory, push: sender), [ToolCatalog.PushNotification], taskId: "task-9");

        await InvokeAsync(set.Tools, FleetPushTools.Name, new() { ["title"] = "Done", ["body"] = "Report ready." });

        var (userId, message) = Assert.Single(sender.Sends);
        Assert.Equal(("alice", "/fleet/?task=task-9", "Done"), (userId, message.Url, message.Title));
    }

    [Fact]
    public async Task Push_BudgetPerTask()
    {
        var sender = new RecordingPushNotificationSender();
        var set = await BuildAsync(Catalog(WithDevice("alice").Factory, push: sender), [ToolCatalog.PushNotification]);

        var results = new List<string>();
        for (var i = 0; i <= FleetPushTools.MaxPushesPerTask; i++)
        {
            results.Add(await InvokeAsync(set.Tools, FleetPushTools.Name, new() { ["title"] = $"N{i}", ["body"] = "Body." }));
        }

        Assert.Equal(FleetPushTools.MaxPushesPerTask, sender.Sends.Count);
        Assert.Contains("the limit", results[^1]);
    }

    [Fact]
    public async Task Push_DoesNotLogText()
    {
        var logger = new CapturingLogger<FleetToolCatalog>();
        var set = await BuildAsync(Catalog(WithDevice("alice").Factory, push: new RecordingPushNotificationSender(), logger: logger),
            [ToolCatalog.PushNotification]);

        await InvokeAsync(set.Tools, FleetPushTools.Name, new() { ["title"] = "SecretTitle", ["body"] = "SecretBody" });

        Assert.NotEmpty(logger.Messages);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("Secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Push_Deselected_Absent()
    {
        var set = await BuildAsync(Catalog(WithDevice("alice").Factory, push: new RecordingPushNotificationSender()), [ToolCatalog.WebSearch]);

        Assert.DoesNotContain(set.Tools, t => t.Name == FleetPushTools.Name);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => [.. _messages];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _messages.Enqueue(formatter(state, exception));
    }
}
