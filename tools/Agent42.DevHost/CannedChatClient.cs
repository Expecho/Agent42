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
        => Task.FromResult(IsJudgeCall(messages, options)
            ? JudgeVerdict()
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, SelectReply(ResearchPrompt(messages)))));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var call = SelectToolCall(messages, options);
        if (call is not null)
        {
            // Paced so a research checklist visibly ticks (ADR 0082) instead of arriving complete.
            await Task.Delay(400, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [call]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [BuildUsage(120, 25)]);
            yield break;
        }

        // The user's own question — not the todo list or RAG excerpts injected as user-role messages, and not a
        // research loop's feedback (ADR 0082).
        var prompt = ResearchPrompt(messages);

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

        // A research loop's first iteration drafts; the answer comes once the loop's feedback says a step is left.
        var reply = options?.Tools?.Any(t => t.Name == "todos_add") == true && !messages.Any(IsLoopFeedback)
            ? $"Drafting an answer to *{prompt}* from the first sources — one step of the plan is still open."
            : SelectReply(prompt);
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

    // The plain canned reply, or the rich one (ADR 0083) when the prompt carries the marker. A text reply,
    // not a tool call, so it is chosen here and not in SelectToolCall.
    private static string SelectReply(string prompt) =>
        prompt.Contains(RichMarker, StringComparison.OrdinalIgnoreCase) ? RichReply : BuildReply(prompt);

    /// <summary>Prompt marker that makes the canned reply exercise every rich renderer (ADR 0083).</summary>
    private const string RichMarker = "show rich";

    /// <summary>
    /// One of each rich block: a mermaid flowchart, highlighted code (one block with a fence inside a string),
    /// inline/display/bracket math, a currency sentence that must stay text, a bar chart, and a deliberately
    /// broken diagram whose failure must not stop the rest. A plain constant, not part of the interpolated
    /// <see cref="BuildReply"/>: the chart's JSON braces would collide with its interpolation holes.
    /// </summary>
    private const string RichReply = """
        Here is everything the renderer can do.

        ```mermaid
        flowchart LR
          User[Browser] -->|NDJSON stream| Api[ASP.NET Core]
          Api --> Grain[(Agent grain)]
          Grain --> Model{{Model provider}}
        ```

        ```csharp
        var fence = "```";
        await foreach (var chunk in agent.ChatAsync("hi")) { Console.Write(chunk.Text); }
        ```

        ```js
        const answer = [6, 7].reduce((a, b) => a * b, 1);
        console.log(`the answer is ${answer}`);
        ```

        Euler's identity, inline: $e^{i\pi} + 1 = 0$, and with brackets: \(a^2 + b^2 = c^2\).

        $$
        \int_0^\infty e^{-x^2}\,dx = \frac{\sqrt{\pi}}{2}

        \sum_{n=1}^{\infty} \frac{1}{n^2} = \frac{\pi^2}{6}
        $$

        \[
        \mathbf{A}\mathbf{x} = \mathbf{b}
        \]

        Prices stay text: it costs $5 and $10 for the pair.

        ```chart
        {"type": "bar", "data": {"labels": ["Mon", "Tue", "Wed", "Thu", "Fri"], "datasets": [{"label": "Questions answered", "data": [12, 19, 7, 42, 23]}]}}
        ```

        ```mermaid
        flowchart LR
          this is -->> not valid ((
        ```

        That last diagram is broken on purpose; it should show its code and an error note.
        """;

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

        if (TryResearchCall(list, options, out var research))
        {
            return research;
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

        if (ArtifactCall(list, lastUserIndex, text, options) is { } artifact)
        {
            return artifact;
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

    /// <summary>Creates a small HTML artifact that demonstrates the sandbox (a canvas, and a fetch that fails).</summary>
    public const string ArtifactTrigger = "make an artifact";

    /// <summary>Updates the most recent artifact in this chat to a second version.</summary>
    public const string ArtifactUpdateTrigger = "update the artifact";

    /// <summary>Creates a markdown artifact.</summary>
    public const string MarkdownArtifactTrigger = "make a markdown artifact";

    /// <summary>Creates a code artifact whose source contains a triple-backtick run.</summary>
    public const string CodeArtifactTrigger = "make a code artifact";

    // The artifact tools, offline. Worded to collide with nothing else here (no "cancel", "remind" or "look up"), and
    // extracted so SelectToolCall stays under S3776. Like FakeChatClient: no call for a tool the model was not given.
    private static FunctionCallContent? ArtifactCall(
        IReadOnlyList<ChatMessage> list, int lastUserIndex, string text, ChatOptions? options)
    {
        if (options?.Tools?.Any(t => t.Name == "create_artifact") is not true)
        {
            return null;
        }

        if (text.Contains(ArtifactUpdateTrigger, StringComparison.OrdinalIgnoreCase))
        {
            return LastArtifactId(list, lastUserIndex) is { } id
                ? new("canned-artifact-update", "update_artifact", new Dictionary<string, object?>
                {
                    ["artifactId"] = id,
                    ["content"] = DemoPage("#4fa3ff", "v2"),
                    ["title"] = "Sandbox demo (v2)",
                })
                : null;
        }

        (string Kind, string Title, string Content, string? Language)? artifact =
            text.Contains(MarkdownArtifactTrigger, StringComparison.OrdinalIgnoreCase)
                ? ("markdown", "Release notes", DemoMarkdown, null)
                : text.Contains(CodeArtifactTrigger, StringComparison.OrdinalIgnoreCase)
                    ? ("code", "fence.py", DemoCode, "python")
                    : text.Contains(ArtifactTrigger, StringComparison.OrdinalIgnoreCase)
                        ? ("html", "Sandbox demo", DemoPage("#ffb13d", "v1"), null)
                        : null;

        return artifact is { } a
            ? new("canned-artifact-create", "create_artifact", new Dictionary<string, object?>
            {
                ["kind"] = a.Kind,
                ["title"] = a.Title,
                ["content"] = a.Content,
                ["language"] = a.Language,
            })
            : null;
    }

    // The newest artifact id in an earlier tool result (create_artifact returns {"artifactId":"art_…"}).
    private static string? LastArtifactId(IReadOnlyList<ChatMessage> list, int lastUserIndex)
        => list.Take(lastUserIndex)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => ArtifactIdInResult().Match(r.Result?.ToString() ?? ""))
            .LastOrDefault(m => m.Success)?.Value;

    [GeneratedRegex("art_[0-9a-f]{12}")]
    private static partial Regex ArtifactIdInResult();

    private const string DemoMarkdown = """
        # Release notes

        - **Artifacts** open in a side pane
        - Versions are kept

        $e^{i\pi}+1=0$
        """;

    // The source contains a markdown fence on purpose: the pane must render it as code, not break out of a fence.
    private const string DemoCode = """
        # a source that contains a markdown fence
        FENCE = "```"


        def wrap(code):
            return f"{FENCE}\n{code}\n{FENCE}"
        """;

    // A page that draws on a canvas, then tries the three things the sandbox must stop and prints what happened.
    private static string DemoPage(string color, string label) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Sandbox demo</title>
        <style>body{font:14px system-ui;margin:16px;background:#111;color:#eee}canvas{border:1px solid #444}li{margin:4px 0}</style>
        </head><body>
        <h1>Sandbox demo {{label}}</h1>
        <canvas id="c" width="320" height="160"></canvas>
        <ul id="out"></ul>
        <script>
          const ctx = document.getElementById("c").getContext("2d");
          for (let i = 0; i < 12; i++) { ctx.fillStyle = "{{color}}"; ctx.globalAlpha = (i + 1) / 12; ctx.fillRect(i * 26, 160 - i * 12, 22, i * 12); }
          const out = document.getElementById("out");
          const say = (t) => { const li = document.createElement("li"); li.textContent = t; out.appendChild(li); };
          say("origin: " + location.origin);
          try { say("cookie: " + document.cookie); } catch (e) { say("cookie blocked: " + e.name); }
          try { localStorage.setItem("x", "1"); say("localStorage worked"); } catch (e) { say("localStorage blocked: " + e.name); }
          fetch("/api/conversations").then(() => say("fetch /api WORKED (bad)"), (e) => say("fetch /api blocked: " + e.message));
        </script>
        </body></html>
        """;

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
