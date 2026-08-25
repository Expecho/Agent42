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

    // AC8: every sidebar row (regular + agent chats share buildConvRow) carries a pin toggle that reflects
    // its state to assistive tech and flips it through setPinned.
    [Fact]
    public async Task PinAction_WiredIntoConvRow()
    {
        var app = await _fixture.GetAppScriptAsync();

        var row = StaticAssetsServerFixture.Body(app, "function buildConvRow(conv, hoisted)");
        Assert.Contains("conv__pin", row);
        Assert.Contains("setPinned(conv.id", row);
        Assert.Contains("is-pinned", row);
        Assert.Contains("aria-pressed", row);
    }

    // AC14: a hoisted row sits outside its agent group, which is the only thing that names the agent —
    // so the hoisted path must render a badge, or a pinned agent chat reads as a regular one. Pinned on
    // agentId + the "Agent" fallback, NOT on agentName: grouping keys on agentId and buildAgentGroup
    // falls back, so keying the badge on the name would hoist a blank-named agent chat with no label at
    // all — the exact bug the badge exists to prevent.
    [Fact]
    public async Task HoistedRow_CarriesTheAgentBadge()
    {
        var app = await _fixture.GetAppScriptAsync();

        var row = StaticAssetsServerFixture.Body(app, "function buildConvRow(conv, hoisted)");
        Assert.Contains("hoisted && conv.agentId", row);
        Assert.Contains("conv.agentName || \"Agent\"", row);
        Assert.Contains("conv__agent", row);
    }

    // AC8: the toggle reaches the owner pin endpoint, and follows deleteConversation's mutate-then-reconcile
    // shape — a mid-stream click is ignored rather than racing the turn's own index touch.
    [Fact]
    public async Task SetPinned_TargetsThePinEndpoint()
    {
        var app = await _fixture.GetAppScriptAsync();

        var setPinned = StaticAssetsServerFixture.Body(app, "async function setPinned(id, pinned)");
        Assert.Contains("/pin", setPinned);
        Assert.Contains("method: \"PUT\"", setPinned);
        Assert.Contains("if (streaming) return;", setPinned);
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

        var hoistAt = render.IndexOf("buildConvRow(conv, true)", StringComparison.Ordinal);
        var groupsAt = render.IndexOf("const groups = new Map()", StringComparison.Ordinal);
        Assert.True(hoistAt >= 0, "the pinned block does not render hoisted rows");
        Assert.True(groupsAt > hoistAt, "the pinned block must be emitted before the grouping walk");

        // The walk's own source text carries the guard that keeps a hoisted row out of its group.
        Assert.Contains("if (conv.pinned) continue;", render[groupsAt..]);

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
