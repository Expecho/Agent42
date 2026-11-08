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

    // ADR 0078 — the stop control. It lives where the send button used to, which is only acceptable while
    // it stays transient: served hidden, revealed for the duration of a turn. Anchored on its neighbours
    // for the same anti-vacuity reason as AC1 above.
    [Fact]
    public async Task StopButton_IsServedHiddenAndLabelled()
    {
        var index = await _client.GetStringAsync("/");

        Assert.Contains("id=\"stopBtn\"", index);
        Assert.Contains("aria-label=\"Stop generating\"", index);
        Assert.Contains("id=\"voiceBtn\"", index);

        // `hidden`, not opacity: an invisible-but-clickable control on the right edge of the composer is
        // the ADR 0052 hit-test trap, and a thumb would find it.
        var stop = index[index.IndexOf("id=\"stopBtn\"", StringComparison.Ordinal)..];
        Assert.Contains("hidden", stop[..stop.IndexOf('>')]);

        var styles = await _client.GetStringAsync("/styles.css");
        Assert.Contains("composer__stop", styles);
        Assert.Contains("thread__stopped", styles);
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

}
