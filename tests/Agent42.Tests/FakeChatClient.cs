using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>
/// Deterministic in-memory <see cref="IChatClient"/> so agent tests run without a real model.
/// When the user asks to be reminded about something it emits a call to the
/// <c>create_reminder</c> tool, letting tests exercise the tool + Orleans reminder path
/// end-to-end; otherwise it returns a canned greeting.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private static readonly string[] ReplyChunks = ["Hello! ", "How can I ", "help you today?"];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var contents = ShouldCallReminderTool(messages)
            ? new List<AIContent> { BuildReminderCall() }
            : new List<AIContent> { new TextContent(string.Concat(ReplyChunks)) };

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (ShouldCallReminderTool(messages))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { BuildReminderCall() });
            yield break;
        }

        foreach (var chunk in ReplyChunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Yield();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static FunctionCallContent BuildReminderCall() =>
        new("reminder-call-1", "create_reminder", new Dictionary<string, object?>
        {
            ["name"] = "drink-water",
            ["prompt"] = "Tell the user to drink some water.",
            ["dueInMinutes"] = 1.0,
            ["repeatEveryMinutes"] = 60.0,
        });

    private static bool ShouldCallReminderTool(IEnumerable<ChatMessage> messages)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        // Once the tool has run this turn, its result is in the message list — answer in
        // text instead of calling the tool again (which would loop forever).
        if (list.Any(m => m.Contents.Any(c => c is FunctionResultContent)))
        {
            return false;
        }

        var lastUser = list.LastOrDefault(m => m.Role == ChatRole.User);
        return lastUser?.Text?.Contains("remind", StringComparison.OrdinalIgnoreCase) == true;
    }
}
