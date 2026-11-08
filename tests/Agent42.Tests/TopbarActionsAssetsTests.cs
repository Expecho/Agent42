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
        var send = StaticAssetsServerFixture.Body(app, "export async function send(prompt, replay = null)");
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

    // AC3b (cascade-limited) — the trap this criterion exists for: the desktop sidebar header already
    // renders the identical mark + wordmark, so a naively revealed topbar brand shows "42 AGENT 42"
    // twice, side by side. Both original children are therefore hidden by default and revealed only
    // inside the ≤760px block, which is pinned here by index ordering. The one desktop exception is a
    // collapsed sidebar (ADR 0086), where the sidebar header is gone; SidebarCollapseAssetsTests pins it.
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

}
