using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>
/// Deterministic in-memory <see cref="IChatClient"/> so agent tests run without a real model.
/// It maps the last user message to a tool call — "remind" schedules, "cancel all" clears
/// every reminder, "cancel" clears one — so tests exercise the schedule/cancel tools and the
/// Orleans reminder path end-to-end; otherwise it returns a canned greeting.
///
/// To let a test prove a bound persona actually reaches the model, it prepends a stable
/// <c>[sys:…]</c> marker built from the instructions it receives. Agent Framework 1.13
/// delivers <c>ChatOptions.Instructions</c> as <c>options.Instructions</c> (verified by a
/// spike), but the marker also inspects system-role messages so the echo is robust if that
/// delivery path ever changes.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private static readonly string[] ReplyChunks = ["Hello! ", "How can I ", "help you today?"];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var call = SelectToolCall(messages);
        var contents = call is not null
            ? new List<AIContent> { call }
            : new List<AIContent>
            {
                new TextContent(InstructionsEcho(messages, options) + ImageEcho(messages) + string.Concat(ReplyChunks)),
            };

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var call = SelectToolCall(messages);
        if (call is not null)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { call });
            yield break;
        }

        var echo = InstructionsEcho(messages, options) + ImageEcho(messages);
        if (echo.Length > 0)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, echo);
            await Task.Yield();
        }

        foreach (var chunk in ReplyChunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Yield();
        }
    }

    // Surfaces the instructions the terminal client received so a test can assert the bound
    // system prompt was delivered. Prefers options.Instructions (how AF 1.13 carries it) and
    // falls back to any system-role message. Empty when nothing was supplied.
    private static string InstructionsEcho(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var instructions = options?.Instructions;
        if (string.IsNullOrWhiteSpace(instructions))
        {
            instructions = messages
                .Where(m => m.Role == ChatRole.System)
                .Select(m => m.Text)
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        }

        return string.IsNullOrWhiteSpace(instructions) ? "" : $"[sys:{instructions}] ";
    }

    // Surfaces the number of image content parts on the last user message as an [img:N] marker,
    // so a test can assert an attachment actually reached the model — a storage round-trip proves
    // persistence, not delivery. Counts UriContent/DataContent (how images ride on a turn).
    private static string ImageEcho(IEnumerable<ChatMessage> messages)
    {
        var lastUser = (messages as IReadOnlyList<ChatMessage> ?? messages.ToList())
            .LastOrDefault(m => m.Role == ChatRole.User);
        var count = lastUser?.Contents.Count(c => c is UriContent or DataContent) ?? 0;
        return count > 0 ? $"[img:{count}] " : "";
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static FunctionCallContent BuildReminderCall() =>
        new("reminder-call-1", "schedule_prompt", new Dictionary<string, object?>
        {
            ["name"] = "drink-water",
            ["prompt"] = "Tell the user to drink some water.",
            ["dueInMinutes"] = 1.0,
            ["repeatEveryMinutes"] = 60.0,
        });

    private static FunctionCallContent BuildCancelCall() =>
        new("cancel-call-1", "cancel_scheduled_prompt", new Dictionary<string, object?>
        {
            ["name"] = "drink-water",
        });

    private static FunctionCallContent BuildCancelAllCall() =>
        new("cancel-all-call-1", "cancel_all_scheduled_prompts", new Dictionary<string, object?>());

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
}
