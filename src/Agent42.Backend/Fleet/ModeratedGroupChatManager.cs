using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// Lets a model moderate a fleet GroupChat (ported from the engine): after every turn it picks who speaks next, or
/// ends the chat once the termination criteria are met. The first participant opens the chat, and the turn goes
/// round-robin whenever the model makes no usable choice.
/// </summary>
public sealed class ModeratedGroupChatManager : GroupChatManager
{
    private static readonly AITool[] Tools =
    [
        AIFunctionFactory.Create(
            ([Description("Name of the participant who speaks next.")] string name) => name,
            "next_speaker",
            "Let a participant speak next."),
        AIFunctionFactory.Create(
            ([Description("Why the chat is done.")] string reason) => reason,
            "finish",
            "End the chat, the last message is the final result."),
    ];

    private readonly IReadOnlyList<AIAgent> _participants;
    private readonly IChatClient _chatClient;
    private readonly string _instructions;
    private AIAgent? _nextSpeaker;
    private int _turns;

    public ModeratedGroupChatManager(IReadOnlyList<AIAgent> participants, IChatClient chatClient, FleetPromptSet prompts, string task, string terminationCriteria)
    {
        _participants = participants;
        _chatClient = chatClient;
        var criteria = string.IsNullOrWhiteSpace(terminationCriteria) ? "the result fully accomplishes the task" : terminationCriteria;
        _instructions = prompts.Render("moderator",
            ("criteria", criteria),
            ("task", task),
            ("participants", string.Join("\n", participants.Select(p => $"- {p.Name}: {p.Description}"))));
    }

    protected override async ValueTask<bool> ShouldTerminateAsync(IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken = default)
    {
        if (await base.ShouldTerminateAsync(history, cancellationToken))
        {
            return true;
        }

        // Nobody spoke yet, the first participant opens the chat.
        if (!history.Any(m => m.Role == ChatRole.Assistant))
        {
            return false;
        }

        var (finish, speaker) = await ModerateAsync(history, cancellationToken);
        _nextSpeaker = speaker;

        // Every participant speaks at least once, so an agent that writes the others' lines can't end the chat alone.
        return finish && _turns >= _participants.Count;
    }

    protected override ValueTask<AIAgent> SelectNextAgentAsync(IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken = default)
    {
        var speaker = _nextSpeaker ?? _participants[_turns % _participants.Count];
        _nextSpeaker = null;
        _turns++;
        return ValueTask.FromResult(speaker);
    }

    protected override void Reset()
    {
        base.Reset();
        _nextSpeaker = null;
        _turns = 0;
    }

    private async Task<(bool Finish, AIAgent? Speaker)> ModerateAsync(IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken)
    {
        static string Shorten(string text) => text.Length <= 3000 ? text : $"{text[..3000]}...";
        var transcript = string.Join("\n\n", history.TakeLast(20).Select(m => $"[{m.AuthorName ?? m.Role.ToString()}]: {Shorten(m.Text)}"));

        // The tools are only declared: this client doesn't invoke them, the choice is read from the tool call.
        var options = new ChatOptions { Instructions = _instructions, Tools = Tools, ToolMode = ChatToolMode.RequireAny };
        var response = await _chatClient.GetResponseAsync($"The conversation so far:\n\n{transcript}\n\nWhat happens next?", options, cancellationToken);

        var call = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().FirstOrDefault();
        if (call?.Name == "finish")
        {
            return (true, null);
        }

        var name = call?.Arguments is { } arguments && arguments.TryGetValue("name", out var value) ? value?.ToString() : null;
        return (false, _participants.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)));
    }
}
