namespace Agent42.Tests;

/// <summary>
/// Guards the persistent top-right action cluster that hosts the incognito toggle (ADR 0034) and the
/// read-only share entry point (ADR 0035). Serves the real Backend <c>wwwroot</c> through the shared
/// <see cref="StaticAssetsServerFixture"/> and asserts on the key source strings — the same source-text
/// heuristic as <see cref="ComposerAssetsTests"/>, whose two disciplines are carried over verbatim:
/// every negative is paired with a positive anchor from the same document (a whole-file
/// <c>DoesNotContain</c> passes vacuously against a truncated read), and placement claims use the
/// index-ordering idiom rather than a bare <c>Contains</c>, because
/// <see cref="StaticAssetsServerFixture.Body"/> counts braces and cannot slice HTML.
///
/// <para><b>What these tests cannot reach.</b> The visibility criteria (the topbar showing on desktop,
/// the cluster right-aligning, brand + hamburger staying mobile-only) are <i>cascade</i> claims: a
/// source-text test can prove a declaration exists, never which rule or media query wins at a given
/// viewport. They are pinned here as "the declaration shipped" and verified for real by the manual leg
/// recorded in the ADR — the same untestable-half treatment as ADR 0037 and ADR 0043. Likewise "the
/// buttons stay visible while a long chat scrolls" has no automated proof; only its structural
/// precondition (the buttons live outside <c>.chat__scroll</c>) is machine-checked, by AC 1.</para>
/// </summary>
[Collection("StaticAssets")]
public sealed class TopbarActionsAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public TopbarActionsAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    // AC1 — both buttons sit inside <header class="topbar">, which is a static flex sibling ABOVE
    // .chat__scroll (the only scrolling element in the main column). That containment IS the mechanism
    // behind "stays visible while scrolling": no position:sticky, no z-index, no stacking context. Proved
    // by index ordering, since Body() cannot slice HTML and a bare Contains proves nothing about place.
    [Fact]
    public async Task TopbarActions_LiveInTheTopbar_NotTheComposer()
    {
        var index = await _client.GetStringAsync("/");

        var topbarAt = index.IndexOf("<header class=\"topbar\"", StringComparison.Ordinal);
        Assert.True(topbarAt >= 0, "the topbar header is missing");
        var topbarEnd = index.IndexOf("</header>", topbarAt, StringComparison.Ordinal);
        Assert.True(topbarEnd > topbarAt, "the topbar header is not closed");
        var scrollAt = index.IndexOf("class=\"chat__scroll\"", StringComparison.Ordinal);
        var composerAt = index.IndexOf("class=\"composer__frame\"", StringComparison.Ordinal);
        Assert.True(topbarEnd < scrollAt && scrollAt < composerAt, "topbar / scroller / composer are out of order");

        var actionsAt = index.IndexOf("class=\"topbar__actions\"", StringComparison.Ordinal);
        var incognitoAt = index.IndexOf("id=\"incognitoBtn\"", StringComparison.Ordinal);
        var shareAt = index.IndexOf("id=\"topbarShareBtn\"", StringComparison.Ordinal);

        Assert.True(actionsAt > topbarAt && actionsAt < topbarEnd, "the action cluster is not inside the topbar");
        Assert.True(incognitoAt > actionsAt && incognitoAt < topbarEnd, "incognito is not in the topbar cluster");
        Assert.True(shareAt > actionsAt && shareAt < topbarEnd, "topbar share is not in the topbar cluster");

        // The composer frame keeps its own controls — the anchor that stops the two claims above from
        // passing against a document that lost the composer entirely.
        Assert.Contains("id=\"attachBtn\"", index);
        Assert.Contains("id=\"toolsBtn\"", index);
    }

    // AC1 (second half) — the topbar share button opens the dialog, it never mints. It ships `disabled`
    // in the markup too: between HTML parse and the deferred module executing there is no target yet.
    [Fact]
    public async Task TopbarShare_ShipsDisabledInMarkup()
    {
        var index = await _client.GetStringAsync("/");

        var shareAt = index.IndexOf("id=\"topbarShareBtn\"", StringComparison.Ordinal);
        Assert.True(shareAt >= 0, "the topbar share button is missing");
        var buttonEnd = index.IndexOf('>', shareAt);
        var openTag = index[shareAt..buttonEnd];
        Assert.Contains("disabled", openTag);
        Assert.DoesNotContain("hidden", openTag); // disabled, not hidden — the control must not move around
    }

    // AC4 — the topbar button is a second entry point into the SAME dialog, and it must not create a
    // link directly: ADR 0035's consent property is that nothing is minted until the user presses Create.
    // So the click handler only calls openShareLink, and openShareLink now refuses a null target (the
    // guard create/revoke already carried) rather than rendering “undefined” and fetching an empty id.
    [Fact]
    public async Task TopbarShare_OpensTheDialog_AndCreatesNothing()
    {
        var app = await _fixture.GetAppScriptAsync();

        var wire = StaticAssetsServerFixture.Body(app, "export function wireShareLinks()");
        Assert.Contains("topbarShareBtn.addEventListener(\"click\"", wire);
        Assert.Contains("openShareLink(topbarConvId, topbarConvTitle)", wire);

        // A POST is what mints the link; it lives in createShareLink alone, never on the topbar path.
        var open = StaticAssetsServerFixture.Body(app, "async function openShareLink(convId, title)");
        Assert.Contains("if (!convId) return", open);
        Assert.DoesNotContain("method: \"POST\"", open);

        // The setter is the only thing that flips the button's availability, and null disables it.
        var setter = StaticAssetsServerFixture.Body(app, "export function setTopbarShare(convId, title)");
        Assert.Contains("topbarShareBtn.disabled = !topbarConvId", setter);
    }

    // AC5 — the button is enabled iff the active conversation is persisted AND has history. Three of the
    // four transitions are pinned here: a fresh chat clears it, an opened one points at it, and a sent
    // turn enables it only when the chat is NOT incognito (an ephemeral chat is never written, so a link
    // to it would resolve to nothing — ADR 0034).
    [Fact]
    public async Task TopbarShare_IsGatedOnEmptyAndEphemeralChats()
    {
        var app = await _fixture.GetAppScriptAsync();

        var fresh = StaticAssetsServerFixture.Body(app, "export function startNewConversation()");
        Assert.Contains("setTopbarShare(null)", fresh);

        var opened = StaticAssetsServerFixture.Body(app, "export async function selectConversation(id)");
        Assert.Contains("setTopbarShare(id,", opened);

        // In send() it must sit on the success path, gated on !ephemeral — NOT beside the incognitoBtn
        // lock, which runs before the fetch and would light the button up on a turn that never landed.
        var send = StaticAssetsServerFixture.Body(app, "export async function send(prompt)");
        Assert.Contains("if (!ephemeral) setTopbarShare(", send);
        var lockAt = send.IndexOf("incognitoBtn.disabled = true", StringComparison.Ordinal);
        var fetchAt = send.IndexOf("await fetch(", StringComparison.Ordinal);
        var enableAt = send.IndexOf("if (!ephemeral) setTopbarShare(", StringComparison.Ordinal);
        Assert.True(lockAt < fetchAt, "the incognito lock is expected before the fetch");
        Assert.True(enableAt > fetchAt, "share must be enabled after the fetch, on the success path");
    }

    // AC5b — the fourth transition, and the one this test exists for. startAgentChat does NOT delegate to
    // startNewConversation: it sets the conversation id itself. Without an explicit clear the button stays
    // enabled pointing at the PREVIOUS conversation, so pressing Create on a fresh agent chat would mint a
    // public, no-login link to a chat the user is not even looking at — a direct violation of ADR 0035's
    // consent property. The clear must sit after the continuous early-return, which resumes through
    // selectConversation and enables share correctly on its own.
    [Fact]
    public async Task TopbarShare_IsClearedWhenAnAgentChatStarts()
    {
        var app = await _fixture.GetAppScriptAsync();

        var start = StaticAssetsServerFixture.Body(app, "export function startAgentChat(agent)");
        Assert.Contains("setTopbarShare(null)", start);

        var resumeAt = start.IndexOf("selectConversation(convId)", StringComparison.Ordinal);
        var clearAt = start.IndexOf("setTopbarShare(null)", StringComparison.Ordinal);
        Assert.True(resumeAt >= 0, "the continuous-agent resume branch is missing");
        Assert.True(clearAt > resumeAt, "the clear must be on the fresh-chat path, after the continuous early-return");
    }

    // AC2 (cascade-limited) — the bar is no longer desktop-hidden chrome. Source-text can prove the
    // `display: none` declaration is gone and a flex layout took its place; which rule wins at a given
    // viewport is the manual leg.
    [Fact]
    public async Task Topbar_IsVisibleOnDesktop()
    {
        var styles = await _client.GetStringAsync("/styles.css");

        Assert.Contains(".topbar {", styles);              // anchor: the rule exists at all
        Assert.DoesNotContain(".topbar { display: none; }", styles);

        var topbarAt = styles.IndexOf(".topbar {", StringComparison.Ordinal);
        var ruleEnd = styles.IndexOf('}', topbarAt);
        Assert.Contains("display: flex", styles[topbarAt..ruleEnd]);
    }

    // AC3 (cascade-limited) — the cluster is pushed to the far right by an auto margin.
    [Fact]
    public async Task TopbarActions_AreRightAligned()
    {
        var styles = await _client.GetStringAsync("/styles.css");

        var actionsAt = styles.IndexOf(".topbar__actions {", StringComparison.Ordinal);
        Assert.True(actionsAt >= 0, "the .topbar__actions rule is missing");
        var ruleEnd = styles.IndexOf('}', actionsAt);
        Assert.Contains("margin-left: auto", styles[actionsAt..ruleEnd]);
    }

    // AC3b (cascade-limited) — the trap this criterion exists for: the desktop sidebar header already
    // renders the identical mark + wordmark, so a naively revealed topbar brand shows "42 AGENT 42"
    // twice, side by side. Both original children are therefore hidden by default and revealed only
    // inside the ≤760px block, which is pinned here by index ordering.
    [Fact]
    public async Task TopbarBrandAndMenu_AreMobileOnly()
    {
        var styles = await _client.GetStringAsync("/styles.css");

        var mediaAt = styles.IndexOf("@media (max-width: 760px)", StringComparison.Ordinal);
        Assert.True(mediaAt >= 0, "the ≤760px block is missing");

        // Their base rules sit BEFORE the media block and hide them...
        var brandAt = styles.IndexOf(".topbar__brand {", StringComparison.Ordinal);
        var menuAt = styles.IndexOf(".topbar__menu {", StringComparison.Ordinal);
        Assert.True(brandAt >= 0 && brandAt < mediaAt, "the base .topbar__brand rule is missing or misplaced");
        Assert.True(menuAt >= 0 && menuAt < mediaAt, "the base .topbar__menu rule is missing or misplaced");
        Assert.Contains("display: none", styles[brandAt..styles.IndexOf('}', brandAt)]);
        Assert.Contains("display: none", styles[menuAt..styles.IndexOf('}', menuAt)]);

        // ...and only the ≤760px block brings them back.
        var mobile = styles[mediaAt..];
        Assert.Contains(".topbar__brand { display: inline-flex; }", mobile);
        Assert.Contains(".topbar__menu { display: grid; }", mobile);
    }

    // AC6 — the engaged tint MOVED (it was not duplicated): it now keys off the same body class on the
    // topbar, which is why setIncognito needed no JS change. The composer frame no longer tints.
    [Fact]
    public async Task IncognitoTint_MovedToTheTopbar()
    {
        var styles = await _client.GetStringAsync("/styles.css");

        Assert.Contains("body.is-incognito .topbar", styles);
        Assert.Contains("composer__frame", styles);  // anchor: the composer rules are still in this file
        Assert.DoesNotContain("body.is-incognito .composer__frame", styles);

        // The writer is untouched: it still toggles the body class and .is-active on the button, and the
        // new topbar rule must keep an .is-active selector for the latter to mean anything.
        var app = await _fixture.GetAppScriptAsync();
        var set = StaticAssetsServerFixture.Body(app, "export function setIncognito(on, interactive)");
        Assert.Contains("classList.toggle(\"is-incognito\", on)", set);
        Assert.Contains("incognitoBtn.classList.toggle(\"is-active\", on)", set);
        Assert.Contains(".topbar__action.is-active", styles);
    }

    // AC6b — the old composer-side class is gone from BOTH documents, not merely unused in one. Renamed,
    // not copied: a surviving duplicate would be dead CSS and would trip the duplication gate.
    [Fact]
    public async Task ComposerIncognitoStyles_AreGone()
    {
        var index = await _client.GetStringAsync("/");
        Assert.Contains("id=\"incognitoBtn\"", index);      // anchor: the button itself survived the move
        Assert.DoesNotContain("composer__incognito", index);

        var styles = await _client.GetStringAsync("/styles.css");
        Assert.Contains(".topbar__action {", styles);        // anchor: its replacement shipped
        Assert.DoesNotContain("composer__incognito", styles);
    }

    // AC7 — moving the toggle changed its position, not its rules: an agent chat's tools and ephemerality
    // both come from the bind snapshot, so incognito still hides under the same picker lock as the tools
    // button, and unlockPicker still brings it back.
    [Fact]
    public async Task Incognito_StillHidesUnderThePickerLock()
    {
        var app = await _fixture.GetAppScriptAsync();

        var locked = StaticAssetsServerFixture.Body(app, "export function lockPicker(name, model)");
        Assert.Contains("incognitoBtn.hidden = true", locked);

        var unlocked = StaticAssetsServerFixture.Body(app, "export function unlockPicker()");
        Assert.Contains("incognitoBtn.hidden = false", unlocked);
    }
}
