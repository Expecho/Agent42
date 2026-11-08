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
