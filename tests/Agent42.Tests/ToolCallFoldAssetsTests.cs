namespace Agent42.Tests;

/// <summary>
/// Guards the tool-call fold: a turn that makes more than <c>TOOL_GROUP_THRESHOLD</c> tool calls renders
/// its chips inside ONE counted disclosure that folds itself shut when the turn ends, instead of a flat
/// list that pushes the reply off the screen. Serves the real Backend <c>wwwroot</c> through the shared
/// <see cref="StaticAssetsServerFixture"/> and asserts on key source strings — the repo has no JS test
/// runner by design, so these prove the wiring exists rather than that it paints. The rendered result was
/// verified manually in DevHost.
/// </summary>
[Collection("StaticAssets")]
public sealed class ToolCallFoldAssetsTests(StaticAssetsServerFixture fixture)
{
    /// <summary>
    /// The group, its counter and its closer all exist and are reachable from <c>main.js</c>. A module
    /// unreachable from there is invisible to this fixture, so this also proves the import graph carries
    /// them.
    /// </summary>
    [Fact]
    public async Task Frontend_ShipsTheToolCallGroup()
    {
        var app = await fixture.GetAppScriptAsync();

        Assert.Contains("msg__toolgroup", app);
        Assert.Contains("finishToolCalls", app);
        // The count in the summary comes from the chips actually present, not an incremented counter.
        Assert.Contains("group.querySelectorAll(\".tool-call\").length", app);
    }

    /// <summary>
    /// The fold has a threshold, and it is a count comparison rather than an unconditional wrapper.
    ///
    /// <para>Pinned because the threshold is the whole reason a one-call turn still looks like it always
    /// did: drop the comparison and every single-tool turn grows a "1 tool call" header, which is the
    /// change this feature was explicitly scoped not to make.</para>
    /// </summary>
    [Fact]
    public async Task TheFold_IsThresholded()
    {
        var app = await fixture.GetAppScriptAsync();

        Assert.Contains("const TOOL_GROUP_THRESHOLD = 3;", app);
        Assert.Contains("count > TOOL_GROUP_THRESHOLD", CodeOnly(StaticAssetsServerFixture.Body(app, "function syncToolGroup(group)")));
    }

    /// <summary>
    /// <b>The rule that matters:</b> the group is collapsed only when the turn is over, never while it is
    /// streaming.
    ///
    /// <para>Mid-turn the running chips are the progress indicator — folding them away as soon as the 4th
    /// call arrives hides exactly the thing the user is waiting on. So <c>toolGroup</c> opens the
    /// disclosure and neither it nor the per-call <c>syncToolGroup</c> may ever close it; only
    /// <c>finishToolCalls</c> does, and only for a group that actually crossed the threshold.</para>
    /// </summary>
    [Fact]
    public async Task TheGroup_IsCollapsedOnlyWhenTheTurnEnds()
    {
        var app = await fixture.GetAppScriptAsync();

        var create = CodeOnly(StaticAssetsServerFixture.Body(app, "function toolGroup(bubble)"));
        Assert.Contains("group.open = true", create);
        Assert.DoesNotContain("open = false", create);
        Assert.DoesNotContain("open = false", CodeOnly(StaticAssetsServerFixture.Body(app, "function syncToolGroup(group)")));

        var finish = CodeOnly(StaticAssetsServerFixture.Body(app, "export function finishToolCalls(msg)"));
        Assert.Contains("group.open = false", finish);
        // Folded only when it grew past the threshold — a short turn stays flat, so closing it would
        // hide chips behind a header that is not even drawn.
        Assert.Contains("is-grouped", finish);
    }

    /// <summary>
    /// Every tail settles the group, not just the happy one.
    ///
    /// <para>The <c>done</c> arm covers a normal turn, but three others never see a <c>done</c>: a voice
    /// barge-in (<c>AbortError</c>, ADR 0037), a stream that died and was recovered (ADR 0046), and an
    /// outright failure. Each would otherwise leave a pulsing lamp over an expanded chain of chips —
    /// until a reload — which, since ADR 0080, does replay the chips and does heal the group. This is what
    /// settles it without one.</para>
    ///
    /// <para>The <c>finally</c> is the only construct that covers all four, so that is what this pins —
    /// scoped to the block, because <c>finishToolCalls</c> is legitimately called from the done arm too
    /// and an unscoped assertion would pass on that one alone.</para>
    ///
    /// <para><b>The scoping is narrowed to <c>send</c>'s body first, and that is load-bearing.</b> Three
    /// other modules contain a <c>finally</c>, and the concatenated corpus is ordered by the import graph,
    /// so a bare <c>"} finally {"</c> marker can anchor in one of those and run the slice across module
    /// boundaries — far enough to swallow <c>send.js</c>'s own <c>import</c> line, which names
    /// <c>finishToolCalls</c> and makes the assertion vacuous. Mutation-checked: deleting the call from
    /// the <c>finally</c> fails this test.</para>
    /// </summary>
    [Fact]
    public async Task EveryStreamTail_SettlesTheToolGroup()
    {
        var app = await fixture.GetAppScriptAsync();

        var send = StaticAssetsServerFixture.Body(app, "export async function send(prompt)");
        var tail = CodeOnly(Section(send, "} finally {", "sendAbort = null"));
        Assert.Contains("finishToolCalls", tail);
    }

    /// <summary>
    /// The group is scoped to its own turn, both on the way in and on the way out.
    ///
    /// <para><c>toolGroup</c> looks up inside the bubble it was handed: a <c>document.querySelector</c>
    /// would find the FIRST group in the whole thread and append every later turn's chips into the
    /// earliest reply. <c>completeToolCall</c> re-syncs through the chip's own <c>closest</c> ancestor for
    /// the same reason — the count and the lamp belong to the turn the chip is in.</para>
    /// </summary>
    [Fact]
    public async Task TheGroup_IsScopedToItsOwnTurn()
    {
        var app = await fixture.GetAppScriptAsync();

        var create = CodeOnly(StaticAssetsServerFixture.Body(app, "function toolGroup(bubble)"));
        Assert.Contains("bubble.querySelector", create);
        Assert.DoesNotContain("document.querySelector", create);

        // Section, not Body: this declaration destructures its second parameter, so the first `{` after
        // it is the destructuring pattern and the brace-depth extractor would stop at its close.
        var complete = CodeOnly(Section(app, "export function completeToolCall", "export function finishToolCalls"));
        Assert.Contains("item.closest(\".msg__toolgroup\")", complete);
    }

    /// <summary>
    /// ADR 0080 AC7 — <b>both</b> painters of a stored <c>ChatTurn</c> replay the turn's tool calls,
    /// through the one shared helper.
    ///
    /// <para><c>renderTurns</c> is the obvious one (a reopened chat, and the offline-cache replay).
    /// <c>recoverPersistedReply</c> is the one that gets missed: it re-reads the same
    /// <c>/messages</c> payload after a stream died (ADR 0046) and repaints into an already-rendered
    /// bubble. ADR 0046 justified its missing chips by equivalence with a reload — once a reload replays
    /// them, that equivalence only holds if recovery replays them too.</para>
    ///
    /// <para>The <c>renderTurns</c> marker is the full signature deliberately: <c>shared/viewer.js</c>
    /// has a <c>renderTurns</c> of its own. It is outside <c>/js/</c> and so is not in this corpus today,
    /// but a bare name would be a trap the day that changes.</para>
    /// </summary>
    [Fact]
    public async Task BothHistoryPainters_ReplayToolCalls()
    {
        var app = await fixture.GetAppScriptAsync();

        Assert.Contains("replayToolCalls",
            CodeOnly(StaticAssetsServerFixture.Body(app, "function renderTurns(id, turns)")));
        Assert.Contains("replayToolCalls",
            CodeOnly(StaticAssetsServerFixture.Body(app, "async function recoverPersistedReply")));
    }

    /// <summary>
    /// ADR 0080 AC8 — every replayed call is completed, so none can pulse forever.
    ///
    /// <para>A tool that returned null replays with a null result, and so does any call the history holds
    /// no result for. Gate <c>completeToolCall</c> on that result and the chip stays <c>is-running</c> in a
    /// transcript that is finished — <c>finishToolCalls</c> clears the GROUP's lamp, not the chip's. So
    /// the call must be unconditional, which is what the negative assertion pins: the helper never
    /// inspects the result at all.</para>
    ///
    /// <para><c>CodeOnly</c> is required, not decorative — the comment above the loop explains this exact
    /// rule and names <c>toolResult</c> while doing so.</para>
    /// </summary>
    [Fact]
    public async Task EveryReplayedCall_IsCompleted()
    {
        var app = await fixture.GetAppScriptAsync();

        var replay = CodeOnly(StaticAssetsServerFixture.Body(app, "export function replayToolCalls"));
        Assert.Contains("completeToolCall", replay);
        Assert.DoesNotContain("toolResult", replay);
    }

    /// <summary>
    /// ADR 0080 AC9 — a recovered turn does not double-render chips it already painted live.
    ///
    /// <para>Neither primitive is idempotent: <c>addToolCall</c> appends a chip unconditionally and
    /// <c>completeToolCall</c> appends a result body unconditionally, so a naive loop over a bubble that
    /// already holds pre-disconnect chips duplicates the chips AND gives the finished ones two results.
    /// The helper therefore keys on the call id and skips only what is already <c>is-done</c> — which is
    /// also what lets it heal a chip the disconnect left running.</para>
    /// </summary>
    [Fact]
    public async Task ReplayDeduplicates_ByCallId()
    {
        var app = await fixture.GetAppScriptAsync();

        var replay = CodeOnly(StaticAssetsServerFixture.Body(app, "export function replayToolCalls"));
        Assert.Contains("data-call-id", replay);
        Assert.Contains("is-done", replay);
    }

    /// <summary>
    /// The styles ship, including the one rule that keeps a below-threshold turn looking untouched: the
    /// summary is not drawn unless the group is marked <c>is-grouped</c>.
    /// </summary>
    [Fact]
    public async Task Styles_ShipTheToolGroup()
    {
        var css = await fixture.Client.GetStringAsync("/styles.css");

        Assert.Contains(".msg__toolgroup", css);
        Assert.Contains(".msg__toolgroup:not(.is-grouped) > .msg__toolgroup-summary { display: none; }", css);
    }

    /// <summary>
    /// <paramref name="source"/> with its <c>//</c> line comments removed. Every negative assertion here
    /// MUST go through this: the comments in the code under test name the thing it must not do ("never
    /// closed here"), so a raw <c>DoesNotContain</c> would be tripped by the explanation rather than by
    /// the defect. Same shape as <see cref="ReasoningAssetsTests"/>.
    /// </summary>
    private static string CodeOnly(string source) =>
        string.Join('\n', source
            .Split('\n')
            .Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment >= 0 ? line[..comment] : line;
            }));

    /// <summary>
    /// The slice of <paramref name="source"/> between two markers — used only for the <c>finally</c>
    /// block, which is a sub-region of a function body and so has nothing for the canonical brace-depth
    /// extractor to anchor on. Asserts both markers are found, so a rename fails loudly instead of
    /// silently searching "".
    /// </summary>
    private static string Section(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Marker not found in the bundled source: {from}");
        var end = source.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Marker not found after '{from}': {to}");
        return source[start..end];
    }
}
