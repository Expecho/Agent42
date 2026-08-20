namespace Agent42.Tests;

/// <summary>
/// Guards the hands-free voice-conversation wiring in the composer (ADR 0037): browser-native
/// <c>SpeechRecognition</c> to listen + <c>speechSynthesis</c> to speak, with barge-in. Serves the
/// real Backend <c>wwwroot</c> through the shared <see cref="StaticAssetsServerFixture"/> and asserts
/// on the presence of the key source strings. Like <see cref="SpeechInputAssetsTests"/>, these are
/// source-text heuristics — the repo has no JS test runner by design — so they prove the wiring
/// exists, not that the speech engines behave at runtime. The runtime half (auto-listen, spoken
/// reply, talk-over-to-interrupt, re-arm) is verified manually in DevHost with a real mic; the
/// browser speech APIs can't be driven headlessly.
/// </summary>
[Collection("StaticAssets")]
public sealed class VoiceConversationAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public VoiceConversationAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task VoiceButton_IsPresent()
    {
        var index = await _client.GetStringAsync("/");
        Assert.Contains("id=\"voiceBtn\"", index);
    }

    [Fact]
    public async Task AppJs_FeatureDetectsSpeechSynthesis()
    {
        var app = await _fixture.GetAppScriptAsync();
        // The output half is new here (dictation, ADR 0006, is input-only). Its presence proves the
        // feature-detect guard exists — and thus the hide-the-button path where TTS is unavailable.
        Assert.Contains("speechSynthesis", app);
        Assert.Contains("SpeechSynthesisUtterance", app);
    }

    [Fact]
    public async Task AppJs_WiresAbortControllerForBargeIn()
    {
        var app = await _fixture.GetAppScriptAsync();
        // Barge-in stops generation mid-stream: send()'s fetch carries an AbortController signal that
        // the voice controller aborts when the user talks over the reply. Source-text heuristic; the
        // runtime interruption is the manual half.
        Assert.Contains("AbortController", app);
        Assert.Contains("sendAbort", app);
    }

    [Fact]
    public async Task AppJs_TreatsAbortAsCleanInterruption()
    {
        var app = await _fixture.GetAppScriptAsync();
        // An aborted turn is a deliberate barge-in, not a transmission failure — the catch must
        // special-case AbortError so it doesn't paint the error state over the partial reply.
        Assert.Contains("AbortError", app);
    }

    [Fact]
    public async Task Index_ShipsSpeechModelSelector()
    {
        var index = await _client.GetStringAsync("/");
        // ADR 0038: Preferences → model providers gains a "default speech model" select, populated by JS
        // from the models a provider marks as speech models.
        Assert.Contains("id=\"speechModelSelect\"", index);
    }

    [Fact]
    public async Task AppJs_SynthesizesReplyServerSideWhenSpeechModelSet()
    {
        var app = await _fixture.GetAppScriptAsync();
        // ADR 0038: when a default speech model is configured the voice reply is spoken by POSTing each
        // sentence to /api/speech and playing the returned audio, else it falls back to speechSynthesis.
        Assert.Contains("/api/speech", app);
        Assert.Contains("userDefaultSpeechModel", app);
    }
}
