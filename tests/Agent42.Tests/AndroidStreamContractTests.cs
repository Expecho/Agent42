using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The Android client parses <c>POST /api/chat/{id}</c> NDJSON from a checked-in golden file
/// (<c>tools/Agent42.Android/contract/stream-events.ndjson</c>), and its own unit test reads the same file. This pins
/// that file to what the endpoint actually writes: one fully populated event per <see cref="AgentStreamEventType"/>,
/// serialized with the production <see cref="ChatStreamJson.Options"/>. A new event type, or a renamed or added field
/// on an existing one, fails here until the fixture (and so the Android parser test) is updated with it.
/// </summary>
public sealed class AndroidStreamContractTests
{
    [Fact]
    public void Fixture_EqualsTheServerSerializationOfEveryEventType()
    {
        var expected = string.Join("\n", Samples().Select(e => JsonSerializer.Serialize(e, ChatStreamJson.Options))) + "\n";
        var actual = File.ReadAllText(FixturePath()).ReplaceLineEndings("\n");

        Assert.True(expected == actual,
            "tools/Agent42.Android/contract/stream-events.ndjson is out of date. Replace its content with:\n" + expected);
    }

    [Fact]
    public void Samples_CoverEveryEventType()
    {
        var covered = Samples().Select(e => e.Type).ToHashSet();

        Assert.Equal(Enum.GetValues<AgentStreamEventType>().ToHashSet(), covered);
    }

    // One sample per type, with every field that type carries set. A new enum value hits the throwing default arm, so
    // both tests fail until it gets a sample here and a line in the fixture. Sample strings carry no
    // double quotes: the web encoder writes them as ", which editors and tools tend to "helpfully" decode, and a
    // golden file nobody can save byte-exact is worse than one with plainer values.
    private static IEnumerable<AgentStreamEvent> Samples() =>
        Enum.GetValues<AgentStreamEventType>().Select(type => type switch
        {
            AgentStreamEventType.TextDelta => new AgentStreamEvent(type, Text: "Hello"),
            AgentStreamEventType.ToolCallStart => new AgentStreamEvent(
                type, ToolName: "get_current_location", ToolCallId: "call_1", ToolInput: "{}", ClientTool: true),
            AgentStreamEventType.ToolCallEnd => new AgentStreamEvent(
                type, ToolName: "web_search", ToolCallId: "call_2", ToolResult: "3 results",
                McpApp: new McpAppResult(new McpAppRef("srv_1", "show_chart", "ui://chart/view.html"), "{}")),
            AgentStreamEventType.Image => new AgentStreamEvent(type, AttachmentId: "att_1", MediaType: "image/png"),
            AgentStreamEventType.Done => new AgentStreamEvent(
                type, InputTokens: 120, OutputTokens: 45, CachedTokens: 100, Stopped: true,
                CacheWriteTokens: 20, CostUsd: 0.0012m, CostStatus: "priced"),
            AgentStreamEventType.Suggestions => new AgentStreamEvent(type, Suggestions: ["Tell me more", "Thanks"]),
            AgentStreamEventType.Reasoning => new AgentStreamEvent(type, Text: "Thinking"),
            AgentStreamEventType.McpReauthRequired => new AgentStreamEvent(type, Text: "Calendar", McpServerId: "mcp_1"),
            AgentStreamEventType.Todos => new AgentStreamEvent(type, Todos: [new ResearchTodo(1, "Search", true)]),
            AgentStreamEventType.TextReset => new AgentStreamEvent(type),
            AgentStreamEventType.StateSnapshot => new AgentStreamEvent(type, State: "{}", StateVersion: 3),
            AgentStreamEventType.McpElicitation => new AgentStreamEvent(
                type, Text: "Calendar", ToolCallId: "call_3", Elicitation: "{}"),
            AgentStreamEventType.Error => new AgentStreamEvent(type, Text: "The model returned an empty reply."),
            _ => throw new InvalidOperationException(
                $"No Android contract sample for {type}: add one here and its line to stream-events.ndjson."),
        });

    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Agent42.slnx) from " + AppContext.BaseDirectory);
        return Path.Combine(dir!.FullName, "tools", "Agent42.Android", "contract", "stream-events.ndjson");
    }
}
