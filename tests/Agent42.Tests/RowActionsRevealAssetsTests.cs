namespace Agent42.Tests;

/// <summary>
/// Guards the long-press reveal of a sidebar row's actions (ADR 0052, amending ADR 0045). Serves the real
/// Backend <c>wwwroot</c> through the shared <see cref="StaticAssetsServerFixture"/> and asserts on the key
/// source strings — the same source-text heuristic as <see cref="PinChatAssetsTests"/> (the repo has no JS
/// runner by design, so these prove the code <i>says</i> the right thing, never that a browser <i>does</i>
/// it). The runtime half — that a hold reveals, a short tap does not, a drag scrolls, and a revealed action
/// actually fires — is the manual pass recorded in ADR 0052.
/// </summary>
[Collection("StaticAssets")]
public sealed class RowActionsRevealAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public RowActionsRevealAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    // AC1: on touch the actions are hidden AND inert. pointer-events is the load-bearing half — opacity: 0
    // removes the paint, not the hit-test, and these three are absolutely positioned over the 82px strip
    // .conv's padding reserves. Without it a thumb on the right edge of any row fires deleteConversation's
    // confirm() from a control nobody can see. The lit pinned pin is exempted: it is visible, so it must
    // stay tappable or it becomes a control that looks pressable and is not.
    [Fact]
    public async Task TouchReveal_IsScopedToTheRevealedRow_AndUnrevealedActionsAreInert()
    {
        var css = await _client.GetStringAsync("/styles.css");

        Assert.Contains("@media (hover: none)", css);
        Assert.Contains(".conv-row.is-revealed .conv__pin", css);
        Assert.Contains(".conv-row.is-revealed .conv__share", css);
        Assert.Contains(".conv-row.is-revealed .conv__delete", css);
        Assert.Contains("pointer-events: none", css);
        Assert.Contains("pointer-events: auto", css);
        // The gesture marks the row from JS. :has() would be the obvious CSS-only alternative and is
        // banned repo-wide for the same reason ADR 0045 marks .is-pinned-last from JS.
        Assert.DoesNotContain(":has(", css);
    }

    // AC2: the gesture is wired into every row buildConvRow produces, at a threshold under Android's ~500ms
    // native long-press so our reveal wins that race.
    [Fact]
    public async Task LongPress_IsWiredIntoConvRow()
    {
        var app = await _fixture.GetAppScriptAsync();

        var row = StaticAssetsServerFixture.Body(app, "export function buildConvRow(conv, hoisted, grouped)");
        Assert.Contains("wireRowReveal(li, btn)", row);

        var wire = StaticAssetsServerFixture.Body(app, "function wireRowReveal(li, btn)");
        Assert.Contains("pointerdown", wire);
        Assert.Contains("is-revealed", wire);
        Assert.Contains("LONG_PRESS_MS", wire);
        Assert.Contains("const LONG_PRESS_MS = 400;", app);
    }

    // AC3 (first half): the desktop reveal still exists and is gated to devices that actually hover. Ungated
    // it fires on touch too — an (hover: none) UA applies :hover on tap and press-and-hold and keeps it
    // until the user taps elsewhere, which would light every row and make the gesture decorative.
    // :focus-visible must stay OUTSIDE the query so keyboard reveal survives on every device.
    [Fact]
    public async Task HoverReveal_SurvivesGatedToPointerDevices()
    {
        var css = await _client.GetStringAsync("/styles.css");

        Assert.Contains("@media (hover: hover)", css);
        Assert.Contains(".conv-row:hover .conv__pin", css);
        Assert.Contains(".conv-row:hover .conv__share", css);
        Assert.Contains(".conv-row:hover .conv__delete", css);
        // Still reachable by keyboard, and not folded into the hover-gated rule.
        Assert.Contains(".conv__pin:focus-visible", css);
        Assert.Contains(".conv__share:focus-visible", css);
        Assert.Contains(".conv__delete:focus-visible", css);
        // The two must not be folded back into one ungated rule. Line endings normalised so this cannot
        // pass vacuously on a CRLF checkout.
        Assert.DoesNotContain(".conv-row:hover .conv__pin,\n.conv__pin:focus-visible", css.Replace("\r\n", "\n"));
    }

    // AC3 (second half): a mouse never enters the gesture. A hovering pointer already has the :hover
    // reveal, so a hybrid laptop keeps the desktop behaviour rather than getting both.
    [Fact]
    public async Task LongPress_IgnoresMousePointers()
    {
        var app = await _fixture.GetAppScriptAsync();

        var wire = StaticAssetsServerFixture.Body(app, "function wireRowReveal(li, btn)");
        Assert.Contains("e.pointerType === \"mouse\"", wire);
    }

    // AC4: the list must still scroll. The gesture cancels on movement past a slop threshold and on
    // pointercancel — but only a PENDING hold: Android fires pointercancel for its own long-press at ~500ms,
    // after our 400ms timer, so tearing down a fired reveal there would make it flash and die on the one
    // platform this exists for. And touch-action: none anywhere near .conv-row would kill scrolling outright.
    [Fact]
    public async Task LongPress_CancelsOnMoveAndCancel_AndNeverDisablesTouchAction()
    {
        var app = await _fixture.GetAppScriptAsync();
        var css = await _client.GetStringAsync("/styles.css");

        var wire = StaticAssetsServerFixture.Body(app, "function wireRowReveal(li, btn)");
        Assert.Contains("pointermove", wire);
        Assert.Contains("pointercancel", wire);
        Assert.Contains("LONG_PRESS_SLOP_PX", wire);
        // cancelPending returns early once the timer has fired, which is what protects a live reveal.
        var cancel = StaticAssetsServerFixture.Body(app, "const cancelPending = () =>");
        Assert.Contains("if (timer === null) return;", cancel);

        // The declaration form, so a comment mentioning the property by name cannot trip this.
        Assert.DoesNotContain("touch-action:", css);
    }

    // AC5: the press that reveals must not also open the chat. The suppression sits on btn, NOT on li:
    // pin/share/delete are li's children, so an li capture listener would intercept their clicks too, and
    // their own stopPropagation runs later in the bubble phase and could not save them.
    [Fact]
    public async Task LongPress_SuppressesTheFollowingClick_OnTheRowButtonOnly()
    {
        var app = await _fixture.GetAppScriptAsync();

        var wire = StaticAssetsServerFixture.Body(app, "function wireRowReveal(li, btn)");
        Assert.Contains("btn.addEventListener(\"click\"", wire);
        Assert.DoesNotContain("li.addEventListener(\"click\"", wire);
        Assert.Contains("swallowNextClick", wire);
        // Cleared on the next pointerdown as well, so a press ending off-row (no click dispatched) can
        // never strand the flag into the following tap.
        Assert.Contains("swallowNextClick = false;", wire);
    }

    // AC6: the reveal is transient. renderSidebar rebuilds every row, so the revealed node is destroyed with
    // them; and a press outside the row dismisses — but a press INSIDE must not, or tapping the revealed
    // row's own pin/share/delete would be eaten by the dismissal.
    [Fact]
    public async Task Reveal_IsClearedOnRerenderAndOnOutsidePointerDown()
    {
        var app = await _fixture.GetAppScriptAsync();

        var render = StaticAssetsServerFixture.Body(app, "export function renderSidebar()");
        Assert.Contains("clearReveal();", render);

        var wire = StaticAssetsServerFixture.Body(app, "function wireRowReveal(li, btn)");
        Assert.Contains("!revealedRow.contains(ev.target)", wire);

        // Registered at most once, guarded on the handle rather than relying on self-removal, so repeated
        // reveals cannot stack document listeners.
        Assert.Contains("if (!onDocPointerDown)", wire);
        var clear = StaticAssetsServerFixture.Body(app, "function clearReveal()");
        Assert.Contains("removeEventListener(\"pointerdown\"", clear);
    }
}
