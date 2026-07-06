using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Agent42.DevHost;

/// <summary>
/// Streams a canned markdown reply so the frontend can be exercised offline.
/// Every turn ends with canned token usage, and the last user message is mapped to a
/// tool call — "remind" schedules, "cancel all" clears every reminder, "cancel" clears
/// one — so the tool-call and usage UI can be exercised without a real model.
/// </summary>
public sealed class CannedChatClient : IChatClient
{
    private static string BuildReply(string prompt) => $$"""
        You asked: *{{prompt}}*

        ## The Answer

        The answer to the ultimate question of life, the universe, and everything is **42**.

        Some things worth knowing:

        - Conversations are stored in an **Orleans grain** per conversation
        - The session is persisted, so you can *resume* it later
        - Streaming happens chunk by chunk over NDJSON

        ```csharp
        var agent = grains.GetGrain<IAgentGrain>(conversationId);
        await foreach (var chunk in agent.ChatAsync("hi")) { }
        ```

        | Component | Technology |
        |-----------|------------|
        | Actor model | Orleans 10 |
        | Agent | Microsoft Agent Framework 1.13 |
        | State | Cosmos DB |

        > Don't panic — everything is persisted.
        """;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            BuildReply(messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? ""))));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var call = SelectToolCall(messages);
        if (call is not null)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [call]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [BuildUsage(120, 25)]);
            yield break;
        }

        var reply = BuildReply(messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "");
        foreach (var chunk in reply.Split(' ').Chunk(4).Select(words => string.Join(' ', words) + " "))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Delay(30, cancellationToken);
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, [BuildUsage(260, 180)]);
    }

    private static FunctionCallContent BuildReminderCall() =>
        new("canned-call-1", "schedule_prompt", new Dictionary<string, object?>
        {
            ["name"] = "drink-water",
            ["prompt"] = "Tell the user to drink some water.",
            ["dueInMinutes"] = 1.0,
            ["repeatEveryMinutes"] = 60.0,
        });

    private static FunctionCallContent BuildCancelCall() =>
        new("canned-cancel-1", "cancel_scheduled_prompt", new Dictionary<string, object?>
        {
            ["name"] = "drink-water",
        });

    private static FunctionCallContent BuildCancelAllCall() =>
        new("canned-cancel-all-1", "cancel_all_scheduled_prompts", new Dictionary<string, object?>());

    private static UsageContent BuildUsage(long input, long output) =>
        new(new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            CachedInputTokenCount = input / 2,
        });

    // Picks the tool call (if any) the model should make for the current user turn.
    private static FunctionCallContent? SelectToolCall(IEnumerable<ChatMessage> messages)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        var lastUserIndex = LastUserIndex(list);
        if (lastUserIndex < 0)
        {
            return null;
        }

        // Once a tool has run for THIS turn, its result is in the messages that follow the
        // triggering user message — answer in text instead of calling the tool again (which
        // would loop forever). Scoping to messages after the last user turn is what lets a
        // schedule-then-cancel conversation call a second tool on the later turn.
        for (var i = lastUserIndex + 1; i < list.Count; i++)
        {
            if (list[i].Contents.Any(c => c is FunctionResultContent))
            {
                return null;
            }
        }

        var text = list[lastUserIndex].Text ?? "";

        // Ordering is load-bearing: "reminder" contains the substring "remind", so the cancel
        // checks MUST precede the schedule check — do not reorder these branches.
        if (text.Contains("cancel all", StringComparison.OrdinalIgnoreCase))
        {
            return BuildCancelAllCall();
        }

        if (text.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        {
            return BuildCancelCall();
        }

        if (text.Contains("remind", StringComparison.OrdinalIgnoreCase))
        {
            return BuildReminderCall();
        }

        return null;
    }

    private static int LastUserIndex(IReadOnlyList<ChatMessage> list)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Role == ChatRole.User)
            {
                return i;
            }
        }

        return -1;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
