using Agent42.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.DevHost;

/// <summary>
/// The research agent offline (ADR 0082). Without this the canned client would answer every loop iteration with
/// the same reply and never touch the todos, so the loop would run to the cap and the checklist would never tick.
/// Instead a research chat (recognised by the todo tools it was handed, whatever the prompt) plans three steps,
/// searches, ticks two, drafts an answer, and — once the loop's feedback says one step is left — ticks the last and
/// answers for real. The judge is answered "done", so the turn ends after two iterations.
/// </summary>
public sealed partial class CannedChatClient
{
    // The loop's AI judge opens its system prompt with this (Agent Framework 1.19, measured).
    private const string JudgeSystemMarker = "You are an evaluator";

    private static bool IsJudgeCall(IEnumerable<ChatMessage> messages, ChatOptions? options)
        => ((options?.Instructions ?? "") + string.Concat(messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text)))
            .Contains(JudgeSystemMarker, StringComparison.Ordinal);

    private static ChatResponse JudgeVerdict()
        => new(new ChatMessage(ChatRole.Assistant, """{"answered":true,"gapAnalysis":""}"""));

    // True when this request belongs to a research chat. `call` is the next scripted call, or null to answer in text.
    private static bool TryResearchCall(IReadOnlyList<ChatMessage> list, ChatOptions? options, out FunctionCallContent? call)
    {
        call = null;
        if (options?.Tools?.Any(t => t.Name == "todos_add") is not true)
        {
            return false;
        }

        var start = ResearchPromptIndex(list);
        var calls = list.Skip(start + 1).SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToList();
        var completes = calls.Count(c => c.Name == "todos_complete");
        var laterIteration = list.Any(IsLoopFeedback);

        call = (calls.Any(c => c.Name == "todos_add"), calls.Any(c => c.Name == "web_search"), completes, laterIteration) switch
        {
            (false, _, _, _) => ResearchCall("todos_add", new()
            {
                ["todos"] = new[] { "Find recent sources", "Compare what they say", "Check the numbers" }
                    .Select(t => new Dictionary<string, object?> { ["title"] = t }).ToList(),
            }),
            (_, false, _, _) => BuildWebSearchCall(list[start].Text ?? ""),
            (_, _, < 2, false) => ResearchCall("todos_complete", CompleteArgs(completes + 1)),
            (_, _, 2, true) => ResearchCall("todos_complete", CompleteArgs(3)),
            _ => null,
        };
        return true;
    }

    // The user's own question: the last user message that is neither injected context (the todo list, RAG
    // excerpts) nor the loop's feedback.
    private static int ResearchPromptIndex(IReadOnlyList<ChatMessage> list)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Role == ChatRole.User && !IsLoopFeedback(list[i]) && !IsInjected(list[i]))
            {
                return i;
            }
        }

        return 0;
    }

    // The text a research reply should answer: the user's question, never the injected todo list.
    private static string ResearchPrompt(IEnumerable<ChatMessage> messages)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        return list.Count == 0 ? "" : list[ResearchPromptIndex(list)].Text ?? "";
    }

    private static bool IsLoopFeedback(ChatMessage message)
        => message.Role == ChatRole.User && message.AuthorName == ResearchLimits.LoopAuthorName;

    private static bool IsInjected(ChatMessage message)
        => message.AdditionalProperties?.TryGetValue(AgentRequestMessageSourceAttribution.AdditionalPropertiesKey, out var value) == true
            && value is AgentRequestMessageSourceAttribution { SourceType: var source }
            && source == AgentRequestMessageSourceType.AIContextProvider;

    private static Dictionary<string, object?> CompleteArgs(int id) => new()
    {
        ["items"] = new List<Dictionary<string, object?>> { new() { ["id"] = id, ["reason"] = "done" } },
    };

    private static FunctionCallContent ResearchCall(string name, Dictionary<string, object?> arguments)
        => new($"{name}-{Guid.NewGuid():N}", name, arguments);
}
