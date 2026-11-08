namespace Agent42.Tests;

/// <summary>
/// Guards the pinned-conversation wiring (ADR 0045). Serves the real Backend <c>wwwroot</c> through the
/// shared <see cref="StaticAssetsServerFixture"/> and asserts on the key source strings — the same
/// source-text heuristic as <see cref="ShareLinkAssetsTests"/> (the repo has no JS runner by design, so
/// these prove the code <i>says</i> the right thing, never that a browser <i>does</i> it). The runtime
/// half — that a pinned chat renders exactly once — is covered by the manual pass recorded in ADR 0045.
/// </summary>
[Collection("StaticAssets")]
public sealed class PinChatAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public PinChatAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    // AC14: a row rendered outside its agent group must carry a badge, or an agent chat reads as a regular
    // one. TWO placements put it there, which is why the condition is a disjunction and not the single
    // `hoisted` flag it was before ADR 0073: the pinned block (ADR 0045), and a custom group, which wins
    // over the agent group. Still pinned on agentId + the "Agent" fallback, NOT on agentName: grouping keys
    // on agentId and buildAgentGroup falls back, so keying the badge on the name would render a
    // blank-named agent chat with no label at all — the exact bug the badge exists to prevent.
    [Fact]
    public async Task HoistedRow_CarriesTheAgentBadge()
    {
        var app = await _fixture.GetAppScriptAsync();

        var row = StaticAssetsServerFixture.Body(app, "export function buildConvRow(conv, hoisted, grouped)");
        Assert.Contains("(hoisted || grouped) && conv.agentId", row);
        Assert.Contains("conv.agentName || \"Agent\"", row);
        Assert.Contains("conv__agent", row);
    }

    // AC9: the two-pass render. The pinned block must be emitted BEFORE the grouping walk, and the walk must
    // skip pinned entries — otherwise a pinned agent chat drags its whole group to the top (the group's slot
    // follows its first-encountered member), or renders twice.
    //
    // This is a source-text heuristic: it proves the pass split and the unpinned guard shipped, NOT that no
    // row renders twice at runtime. That half is the manual pass recorded in ADR 0045.
    [Fact]
    public async Task RenderSidebar_EmitsThePinnedBlockBeforeTheGroupingWalk()
    {
        var app = await _fixture.GetAppScriptAsync();

        var render = StaticAssetsServerFixture.Body(app, "export function renderSidebar()");

        // ADR 0073 split the passes into named functions, so the ORDER is now proved by the call order
        // here and each pass's own guard by its own body below.
        var hoistAt = render.IndexOf("renderPinnedBlock()", StringComparison.Ordinal);
        var walkAt = render.IndexOf("renderAgentWalk()", StringComparison.Ordinal);
        Assert.True(hoistAt >= 0, "the pinned block does not render hoisted rows");
        Assert.True(walkAt > hoistAt, "the pinned block must be emitted before the grouping walk");

        var pinnedBlock = StaticAssetsServerFixture.Body(app, "function renderPinnedBlock()");
        Assert.Contains("buildConvRow(conv, true, false)", pinnedBlock);

        // The walk's own source text carries the guard that keeps a hoisted row out of its group.
        var walk = StaticAssetsServerFixture.Body(app, "function renderAgentWalk()");
        Assert.Contains("const agentGroups = new Map()", walk);
        Assert.Contains("if (conv.pinned ||", walk);

        // The divider between the two blocks is marked in JS, not selected with :has() — and the mark is
        // conditional on something FOLLOWING the block, so an all-pinned list draws no trailing rule.
        Assert.Contains("conversationList.lastElementChild !== lastPinned", render);
        Assert.Contains("is-pinned-last", render);
    }

    // AC10: a pinned row reads as pinned WITHOUT hovering — a hover-only reveal suits an action but not a
    // state indicator. ADR 0052 narrowed how the OTHER two are reached on touch (a press-and-hold reveals
    // one row, rather than every row carrying three lit glyphs), but this rule is deliberately untouched:
    // the lit pin is the only thing marking a hoisted row as user-placed rather than merely recent, so it
    // stays visible — and, in the (hover: none) block, hit-testable — whether or not the row is revealed.
    [Fact]
    public async Task PinnedState_IsVisibleWithoutHover_AndReachableOnTouch()
    {
        var css = await _client.GetStringAsync("/styles.css");

        Assert.Contains(".conv-row.is-pinned .conv__pin", css);
        // ADR 0052 replaced the permanent touch reveal with an .is-revealed-scoped one. Asserting the bare
        // "hover: none" string would now prove nothing about what that block DOES, so pin the rule itself.
        Assert.Contains("@media (hover: none)", css);
        Assert.Contains(".conv-row.is-revealed .conv__pin", css);
        // The exact old rule, whitespace included: a looser negative would collide with the .is-revealed
        // rule's own ".conv__pin," and pass vacuously — the same trap the padding assertion below guards.
        // Line endings are normalised so the needle cannot pass vacuously on a CRLF checkout.
        var oldTouchRule = string.Join("\n", "  .conv__pin,", "  .conv__share,", "  .conv__delete {", "    opacity: 1;");
        Assert.DoesNotContain(oldTouchRule, css.Replace("\r\n", "\n"));
        // The pinned-block divider must not use padding: .conv-row is the containing block for the three
        // absolutely-positioned actions, whose `top: 50%` resolves against its PADDING box, so a
        // padding-bottom would push pin/share/delete off-centre on that one row.
        Assert.Contains(".conv-row.is-pinned-last", css);
        Assert.DoesNotContain(":has(", css);
        // Three hover-revealed actions now, not two — the old two-action right padding would let the pin
        // glyph overlap a long title.
        Assert.DoesNotContain("10px 58px 9px 14px", css);
    }
}
