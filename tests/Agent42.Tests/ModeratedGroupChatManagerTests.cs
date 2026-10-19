using System.Reflection;
using Agent42.Backend;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>
/// The fleet GroupChat moderator over Claude (ADR 0103). It asks for <c>ToolMode = RequireAny</c>, which current Claude
/// models reject, and the Anthropic shim downgrades it to <c>Auto</c>. Under <c>Auto</c> the model may answer in prose,
/// which silently degrades selection to round-robin; both paths are pinned. The manager's hooks are protected
/// overrides of a sealed class, so they are invoked by reflection.
/// </summary>
public class ModeratedGroupChatManagerTests
{
    [Fact]
    public async Task Moderator_OverAnthropicShim_UsesAutoAndSelectsSpeaker()
    {
        var inner = new RecordingChatClient
        {
            Reply = new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("toolu_1", "next_speaker", new Dictionary<string, object?> { ["name"] = "critic" })]),
        };
        var (manager, participants) = Manager(inner);

        Assert.False(await ShouldTerminate(manager, History()));
        var next = await SelectNext(manager, History());

        Assert.Same(ChatToolMode.Auto, inner.Options!.ToolMode);
        Assert.Same(participants[1], next);
    }

    [Fact]
    public async Task Moderator_ProseReply_FallsBackToRoundRobin()
    {
        var inner = new RecordingChatClient { Reply = new ChatMessage(ChatRole.Assistant, "The critic should go next.") };
        var (manager, participants) = Manager(inner);

        Assert.False(await ShouldTerminate(manager, History()));
        var next = await SelectNext(manager, History());

        // No tool call: the chat does not finish, and the turn goes round-robin (turn 0 → the first participant).
        Assert.Same(participants[0], next);
    }

    [Fact]
    public void ModeratorPrompt_AsksForAToolCall()
    {
        Assert.Contains("Always answer by calling next_speaker or finish", FleetPromptSet.Defaults.Get("moderator"),
            StringComparison.Ordinal);
    }

    private static (ModeratedGroupChatManager Manager, AIAgent[] Participants) Manager(RecordingChatClient inner)
    {
        AIAgent[] participants =
        [
            new ChatClientAgent(inner, name: "writer", description: "Writes."),
            new ChatClientAgent(inner, name: "critic", description: "Critiques."),
        ];
        var shim = new AnthropicCompatibilityChatClient(inner, "claude-test");
        return (new ModeratedGroupChatManager(participants, shim, FleetPromptSet.Defaults, "Write a poem.", ""), participants);
    }

    private static List<ChatMessage> History() =>
    [
        new ChatMessage(ChatRole.User, "Write a poem."),
        new ChatMessage(ChatRole.Assistant, "Roses are red.") { AuthorName = "writer" },
    ];

    private static async Task<bool> ShouldTerminate(ModeratedGroupChatManager manager, IReadOnlyList<ChatMessage> history) =>
        await (ValueTask<bool>)Invoke(manager, "ShouldTerminateAsync", history);

    private static async Task<AIAgent> SelectNext(ModeratedGroupChatManager manager, IReadOnlyList<ChatMessage> history) =>
        await (ValueTask<AIAgent>)Invoke(manager, "SelectNextAgentAsync", history);

    private static object Invoke(ModeratedGroupChatManager manager, string name, IReadOnlyList<ChatMessage> history) =>
        typeof(ModeratedGroupChatManager)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, [history, CancellationToken.None])!;
}
