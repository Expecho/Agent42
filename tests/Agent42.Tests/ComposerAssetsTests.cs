namespace Agent42.Tests;

/// <summary>
/// Guards the composer's send path after the send button was removed (ADR 0043). Serves the real
/// Backend <c>wwwroot</c> through the shared <see cref="StaticAssetsServerFixture"/> and asserts on the
/// key source strings — the same shape as <see cref="LocationAssetsTests"/>, the repo's canonical
/// removal-of-a-composer-control guard (positives for what survives, negatives across BOTH the markup
/// and <c>/styles.css</c>).
///
/// <para><b>Source-text heuristics.</b> The repo has no JS test runner by design, so these prove the
/// code <i>says</i> the right thing, not that a browser <i>does</i> it. Two limits matter here.
/// First, every negative below is paired with a positive anchor from the same document: a whole-file
/// <c>DoesNotContain</c> passes vacuously if the document it read were ever empty or truncated — and
/// <see cref="StaticAssetsServerFixture.GetAppScriptAsync"/> builds its corpus by walking import
/// specifiers over HTTP, so a degraded walk would silently drop <c>send.js</c>/<c>models.js</c> and turn
/// AC2 green for the wrong reason. Second, <c>enterkeyhint="send"</c> (AC3) is pinned as an attribute
/// only: whether a soft keyboard actually renders a Send key is a device fact no C# string assertion can
/// reach, which is why ADR 0043 records it as an on-device check.</para>
/// </summary>
[Collection("StaticAssets")]
public sealed class ComposerAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public ComposerAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    // AC1 — the button is gone from the markup AND the stylesheet. Anchored by the neighbours that
    // survive, so a truncated document can't make the negatives pass on their own.
    [Fact]
    public async Task SendButton_IsRemovedFromComposerAndStyles()
    {
        var index = await _client.GetStringAsync("/");

        // The composer frame still has its input and its other controls…
        Assert.Contains("id=\"promptInput\"", index);
        Assert.Contains("id=\"micBtn\"", index);

        // …but no send button: Enter is the send affordance now.
        Assert.DoesNotContain("id=\"sendBtn\"", index);

        var styles = await _client.GetStringAsync("/styles.css");
        Assert.Contains("composer__mic", styles);
        Assert.DoesNotContain("composer__send", styles);
    }

    // AC2 — no module still reaches for the removed element. The two positives prove models.js and
    // send.js are genuinely IN the corpus; without them a degraded import walk would pass this test
    // against a corpus that never contained either file (see the class remark).
    [Fact]
    public async Task AppJs_DoesNotReferenceSendButton()
    {
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("applyModelAvailability", app); // models.js is in the corpus
        Assert.Contains("wireSend", app);               // send.js is in the corpus

        Assert.DoesNotContain("sendBtn", app);
    }

    // AC3 — with no button, the soft keyboard's return key IS the send control, so it must be labelled
    // one. Attribute-only: whether a soft keyboard honours it is a device fact this cannot reach, and
    // ADR 0043 records that leg as an unverified residual rather than claiming it works.
    [Fact]
    public async Task PromptInput_HintsSendOnSoftKeyboards()
    {
        var index = await _client.GetStringAsync("/");

        Assert.Contains("id=\"promptInput\"", index);
        Assert.Contains("enterkeyhint=\"send\"", index);
    }

    // AC4 — the no-model gate (ADR 0019) now rests on the input alone. A disabled <textarea> fires no
    // keydown, so the ADR 0023 "share prefills but cannot send" property survives the button's removal;
    // deleting this line would silently reopen the path into a modelless grain.
    [Fact]
    public async Task AppJs_GatesComposerOnInputWhenNoModel()
    {
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("applyModelAvailability", app);
        Assert.Contains("input.disabled = !hasModel", app);
    }

    // AC5 — the load-bearing criterion. After ADR 0043 the keydown handler in composer.js is the ONLY
    // send path in the UI, so both of its branches are pinned: Enter submits the form explicitly
    // (requestSubmit, not a default submitter — there is none any more), and Shift+Enter falls through
    // to the textarea's own newline.
    [Fact]
    public async Task AppJs_EnterSubmitsTheComposerForm()
    {
        var app = await _fixture.GetAppScriptAsync();

        var wire = StaticAssetsServerFixture.Body(app, "export function wireComposer()");
        Assert.Contains("\"Enter\"", wire);
        Assert.Contains("!e.shiftKey", wire);
        Assert.Contains("form.requestSubmit()", wire);
    }

    // AC8 — no user-facing copy may name a control that no longer exists. The share-target picker
    // (ADR 0023) told the user to "press Send"; it now names the key that actually sends.
    [Fact]
    public async Task ShareDialog_DoesNotDirectTheUserToASendButton()
    {
        var index = await _client.GetStringAsync("/");

        Assert.Contains("id=\"shareDialog\"", index);
        Assert.Contains("Pick where the shared content goes", index);

        Assert.DoesNotContain("press Send", index);
    }
}
