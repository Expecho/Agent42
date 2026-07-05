using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Agent42.DevHost;

/// <summary>
/// Streams a canned markdown reply so the frontend can be exercised offline.
/// Every turn ends with canned token usage, and a prompt containing "remind"
/// triggers a <c>schedule_prompt</c> tool call, so the tool-call and usage UI
/// can be exercised without a real model.
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
        if (ShouldCallReminderTool(messages))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [BuildReminderCall()]);
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

    private static UsageContent BuildUsage(long input, long output) =>
        new(new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            CachedInputTokenCount = input / 2,
        });

    private static bool ShouldCallReminderTool(IEnumerable<ChatMessage> messages)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        // Once the tool has run this turn its result is in the message list — answer in
        // text instead of calling the tool again (which would loop forever).
        if (list.Any(m => m.Contents.Any(c => c is FunctionResultContent)))
        {
            return false;
        }

        var lastUser = list.LastOrDefault(m => m.Role == ChatRole.User);
        return lastUser?.Text?.Contains("remind", StringComparison.OrdinalIgnoreCase) == true;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
