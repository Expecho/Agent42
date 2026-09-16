using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.DevHost;

/// <summary>
/// Streams a canned markdown reply so the frontend can be exercised offline.
/// Every turn ends with canned token usage, and the last user message is mapped to a
/// tool call — "remind" schedules, "cancel all" clears every reminder, "cancel" clears
/// one — so the tool-call and usage UI can be exercised without a real model.
/// </summary>
public sealed partial class CannedChatClient : IChatClient
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
        var call = SelectToolCall(messages, options);
        if (call is not null)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [call]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [BuildUsage(120, 25)]);
            yield break;
        }

        var prompt = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

        // Reasoning output (ADR 0071), so the collapsed "thinking" disclosure is exercisable offline.
        // Gated on a prompt marker rather than emitted on every turn, so the ordinary offline reply keeps
        // its existing shape and a test or a screenshot of it does not suddenly grow a disclosure.
        //
        // Streamed in pieces and AHEAD of the reply, which is what a real reasoning model does — one whole
        // blob would not exercise the append-into-an-existing-element path renderReasoning takes on every
        // delta after the first.
        if (prompt.Contains(ReasoningMarker, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var piece in ReasoningPieces)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    new List<AIContent> { new TextReasoningContent(piece) });
                await Task.Delay(140, cancellationToken);
            }
        }

        var reply = BuildReply(prompt);
        foreach (var chunk in reply.Split(' ').Chunk(4).Select(words => string.Join(' ', words) + " "))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Delay(30, cancellationToken);
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, [BuildUsage(260, 180)]);
    }

    /// <summary>
    /// Prompt marker that makes the canned reply stream reasoning ahead of its text. A PHRASE, matching the
    /// idiom of the test fake's markers ("fault the stream", "stream slowly"): a bare "think" would fire on
    /// an ordinary "what do you think…", which is precisely what gating on a marker is meant to avoid.
    /// </summary>
    private const string ReasoningMarker = "think out loud";

    /// <summary>
    /// The canned "thinking", in deltas. Deliberately long enough to wrap, so the disclosure's scrolling
    /// and wrapping are visible in the offline browser pass rather than just its summary line.
    /// </summary>
    private static readonly string[] ReasoningPieces =
    [
        "The user is asking a question, so first I should work out what they actually want. ",
        "This looks like the canonical question rather than a specific technical one, ",
        "which means the answer is the famous one and I should say so plainly. ",
        "I will give the number, then add a little context about how this app stores conversations, ",
        "since that is what the person running it offline is most likely checking.",
    ];

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

    private static FunctionCallContent BuildWebSearchCall(string query) =>
        new("canned-search-1", "web_search", new Dictionary<string, object?>
        {
            ["query"] = query,
        });

    /// <summary>Prompt trigger for the offline URL-seeding demo (ADR 0067).</summary>
    public const string SeedingChainTrigger = "find and read";

    /// <summary>
    /// Drives ONE <c>send_push_notification</c> call (ADR 0075), so the offline leg can see the tool chip,
    /// the queued-to-N-devices return string and the incognito refusal.
    /// </summary>
    public const string PushTrigger = "send me a notification";

    /// <summary>
    /// Drives FOUR <c>send_push_notification</c> calls in one turn, so the per-turn cap is reachable
    /// offline. Like <see cref="SeedingChainTrigger"/> this spans more than one call, so it is the second
    /// trigger the "a tool already ran" guard must let through.
    /// </summary>
    public const string PushCapTrigger = "spam notifications";

    // The demo link tool registered on the offline MCP group in Program.cs. Its RESULT carries an absolute
    // URL that nobody typed, so it is readable only if the harvest allowlisted it.
    private const string LinkToolName = "demo_find_links";

    // Step 1 calls the link tool; step 2 reads the first absolute URL out of ITS RESULT. Reading the URL
    // from the result rather than hardcoding it is the point: a hardcoded URL would be allowlisted by the
    // user's own prompt text and would prove nothing about seeding.
    private static FunctionCallContent? BuildSeedingChainCall(
        IReadOnlyList<ChatMessage> list, int lastUserIndex, ChatOptions? options)
    {
        // A model cannot call a tool it was not given. Without this, deselecting the MCP tools in the
        // picker (ADR 0021/0026 — the first thing anyone tries by hand) makes this emit a call for an
        // unregistered function instead of answering in text. FakeChatClient carries the same guard.
        if (options?.Tools?.Any(t => t.Name == LinkToolName) is not true)
        {
            return null;
        }

        var calls = list.Skip(lastUserIndex + 1).SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToList();
        if (!calls.Any(c => c.Name == LinkToolName))
        {
            return new("canned-links-1", LinkToolName, new Dictionary<string, object?> { ["filter"] = "pdf" });
        }

        if (calls.Any(c => c.Name == "read_web_document") || FirstUrlInToolResults(list, lastUserIndex) is not { } url)
        {
            return null;
        }

        return new("canned-read-1", "read_web_document", new Dictionary<string, object?>
        {
            ["url"] = url,
            ["question"] = "what does this document say",
        });
    }

    private static string? FirstUrlInToolResults(IReadOnlyList<ChatMessage> list, int lastUserIndex)
    {
        for (var i = lastUserIndex + 1; i < list.Count; i++)
        {
            foreach (var result in list[i].Contents.OfType<FunctionResultContent>())
            {
                if (UrlInResult().Match(result.Result?.ToString() ?? "") is { Success: true } match)
                {
                    return match.Value;
                }
            }
        }

        return null;
    }

    [GeneratedRegex("https?://[^\\s\"',\\\\\\]}<>]+")]
    private static partial Regex UrlInResult();

    private static UsageContent BuildUsage(long input, long output) =>
        new(new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            CachedInputTokenCount = input / 2,
        });

    // Picks the tool call (if any) the model should make for the current user turn.
    private static FunctionCallContent? SelectToolCall(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        var lastUserIndex = LastUserIndex(list);
        if (lastUserIndex < 0)
        {
            return null;
        }

        var text = list[lastUserIndex].Text ?? "";

        // Once a tool has run for THIS turn, its result is in the messages that follow the
        // triggering user message — answer in text instead of calling the tool again (which
        // would loop forever). Scoping to messages after the last user turn is what lets a
        // schedule-then-cancel conversation call a second tool on the later turn.
        //
        // The seeding chain and the push CAP are the two deliberate exceptions — both deliberately span
        // more than one call in a turn — so their triggers are let through here.
        if (!text.Contains(SeedingChainTrigger, StringComparison.OrdinalIgnoreCase)
            && !text.Contains(PushCapTrigger, StringComparison.OrdinalIgnoreCase)
            && list.Skip(lastUserIndex + 1).Any(m => m.Contents.Any(c => c is FunctionResultContent)))
        {
            return null;
        }

        // The URL-seeding chain (ADR 0067), and the ONLY canned case that spans two calls — which is why
        // the guard above lets its trigger through. Without it the feature is unexercisable offline: there
        // would be no way to get from the demo link tool's result to read_web_document.
        if (text.Contains(SeedingChainTrigger, StringComparison.OrdinalIgnoreCase))
        {
            return BuildSeedingChainCall(list, lastUserIndex, options);
        }

        // send_push_notification (ADR 0075). The cap branch COUNTS calls already made this turn rather
        // than testing whether the tool ran, and gives every call a DISTINCT CallId — a single shared id
        // cannot be matched to four results. Four attempts against a cap of three is what makes the
        // refusal visible offline.
        if (text.Contains(PushCapTrigger, StringComparison.OrdinalIgnoreCase))
        {
            var pushCalls = CountPushCalls(list, lastUserIndex);
            return pushCalls >= PushToolLimits.MaxPushesPerTurn + 1 ? null : BuildPushCall(pushCalls + 1);
        }

        if (text.Contains(PushTrigger, StringComparison.OrdinalIgnoreCase))
        {
            return BuildPushCall(null);
        }

        // Ordering is load-bearing: "reminder" contains the substring "remind", so the cancel
        // checks MUST precede the schedule check — do not reorder these branches. The two push triggers
        // above sit outside that constraint rather than ahead of it: neither contains "cancel" or
        // "remind", so they collide with nothing here and their position is free. (FakeChatClient appends
        // its equivalents last for the same reason, stated the other way round.)
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

        // "look up …" drives the web_search tool against the in-memory search (no SearXNG offline),
        // so the tool chip renders with no Docker and no network. Chosen because it cannot collide
        // with the cancel/remind branches above.
        if (text.Contains("look up", StringComparison.OrdinalIgnoreCase))
        {
            return BuildWebSearchCall(text);
        }

        return null;
    }

    // A push call. `ordinal` suffixes the title and varies the CallId for the multi-call cap case; null
    // means the single-call case, where one hardcoded id is fine (every other builder here does that).
    private static FunctionCallContent BuildPushCall(int? ordinal) =>
        new(
            ordinal is null ? "push-call-1" : $"push-call-{ordinal}",
            "send_push_notification",
            new Dictionary<string, object?>
            {
                ["title"] = ordinal is null ? "Build finished" : $"Build finished {ordinal}",
                ["body"] = "The nightly build completed successfully.",
            });

    private static int CountPushCalls(IReadOnlyList<ChatMessage> list, int lastUserIndex)
    {
        var count = 0;
        for (var i = lastUserIndex + 1; i < list.Count; i++)
        {
            count += list[i].Contents
                .OfType<FunctionCallContent>()
                .Count(c => c.Name == "send_push_notification");
        }

        return count;
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
