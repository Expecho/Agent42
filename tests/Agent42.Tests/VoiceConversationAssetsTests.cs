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

        // Since ADR 0078 barge-in does BOTH, and the local abort is the half that cannot be dropped: it is
        // what makes the interruption immediate, because onDelta speaks whatever it is handed and is not
        // gated by `interrupted`. Waiting for the server's answer would read a round trip of buffered text
        // over the user. Pinned as a pair, so replacing the abort with the stop call turns this red.
        var bargeIn = StaticAssetsServerFixture.Body(app, "function bargeIn(heard)");
        Assert.Contains("stopGeneration({ abortLocally: true })", bargeIn);
    }

    // The other half of that pair (ADR 0078): the stop reaches the server, so the turn the user talked over
    // is stored as what was actually spoken — not as the full reply the model went on to write unheard.
    [Fact]
    public async Task AppJs_BargeInAlsoStopsTheServer()
    {
        var app = await _fixture.GetAppScriptAsync();

        // Sliced by hand rather than through Body(): stopGeneration's parameter list is a destructured
        // object, so brace matching from the declaration would return the parameter, not the body.
        var stopGeneration = Slice(app, "export async function stopGeneration", "export async function send(prompt, replay = null)");
        Assert.Contains("/stop`, { method: \"POST\" }", stopGeneration);
        // And it still falls back to the local abort when the stop call cannot be made, so the control is
        // never inert offline.
        Assert.Contains("sendAbort?.abort()", stopGeneration);

        // ORDER, not just presence, and a Contains assertion cannot see it — this is the one that failed
        // review once already. The local abort has to run BEFORE the awaited stop request: onDelta speaks
        // whatever it is handed and is not gated by `interrupted`, so aborting after the round trip leaves
        // the reply being read aloud over the user for its whole duration, which is stop-only barge-in
        // wearing the right source text.
        var abortAt = stopGeneration.IndexOf("if (abortLocally) sendAbort?.abort()", StringComparison.Ordinal);
        var fetchAt = stopGeneration.IndexOf("await fetch(", StringComparison.Ordinal);
        Assert.True(abortAt >= 0, "the barge-in abort disappeared from stopGeneration");
        Assert.True(
            abortAt < fetchAt,
            "barge-in's local abort must run before the stop request is awaited, or the reply keeps "
                + "speaking for a full round trip (ADR 0037/0078).");
    }

    // ── ADR 0079: half-duplex on speakers ────────────────────────────────────────────────────────────
    // The mic closes at AUTO-SEND, not when speech starts. Both halves of the loop hearing itself live in
    // that gap: the trailing final of the utterance just sent arrives while "thinking", and the reply
    // itself arrives while "speaking". Closing it later would fix only the second.
    [Fact]
    public async Task AppJs_ClosesTheMicAtAutoSendWhenOnSpeakers()
    {
        var app = await _fixture.GetAppScriptAsync();
        var trySubmit = StaticAssetsServerFixture.Body(app, "function trySubmit()");

        Assert.Contains("if (!outputIsHeadset) muteMic()", trySubmit);

        // abort(), never stop(). stop() finalizes and can still deliver the trailing result of the
        // utterance just sent — one of the two things being silenced — so the distinction is the feature.
        var muteMic = StaticAssetsServerFixture.Body(app, "function muteMic()");
        Assert.Contains("vr.abort()", muteMic);
        Assert.DoesNotContain("vr.stop()", muteMic);
    }

    // Chrome ends continuous recognition on its own and the 'end' handler restarts it. Without the muted
    // guard that restart silently re-opens the mic a second after it closed, and the whole thing is inert.
    [Fact]
    public async Task AppJs_DoesNotReopenTheMicWhileMuted()
    {
        var app = await _fixture.GetAppScriptAsync();
        Assert.Contains("if (active && !muted) { try { vr.start(); }", app);
    }

    // On a headset there is no acoustic path back into the mic, so ADR 0037's spoken barge-in is kept
    // whole — this is what makes the mute conditional rather than a removal of the feature.
    [Fact]
    public async Task AppJs_KeepsSpokenBargeInForHeadsets()
    {
        var app = await _fixture.GetAppScriptAsync();
        var refresh = StaticAssetsServerFixture.Body(app, "async function refreshOutputKind()");

        Assert.Contains("enumerateDevices", refresh);
        Assert.Contains("audiooutput", refresh);
        // Strictly the DEFAULT device: matching any listed output would keep the mic live whenever a
        // headset is merely connected while the sound still comes out of the speakers.
        Assert.Contains("d.deviceId === \"default\"", refresh);
        // And the unknown answer is "speakers" — the catch must not leave a stale true standing.
        Assert.Contains("outputIsHeadset = false", refresh);
        Assert.Contains("function bargeIn(heard)", app);
    }

    // What replaces talking over the reply: three gestures, and the stop button deliberately not calling
    // stopGeneration (send.js's own handler does that — two handlers would mean two POSTs).
    [Fact]
    public async Task AppJs_OffersGesturesToCutAReplyShort()
    {
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("if (e.key === \"Escape\") interrupt()", app);
        Assert.Contains("stopBtn.addEventListener(\"click\", () => { silenceAndRearm(); })", app);

        var interrupt = StaticAssetsServerFixture.Body(app, "function interrupt()");
        Assert.Contains("silenceAndRearm()", interrupt);
        Assert.Contains("stopGeneration({ abortLocally: true })", interrupt);

        // Re-arming is what hands the mic back; an interrupt that only cancelled the audio would leave a
        // muted turn deaf, which is worse than the echo it replaced.
        var silence = StaticAssetsServerFixture.Body(app, "function silenceAndRearm()");
        Assert.Contains("cancelSpeech()", silence);
        Assert.Contains("armListening()", silence);
        Assert.DoesNotContain("stopGeneration", silence);
    }

    // The re-arm retries. It did not have to before: with the mic live, losing this race only mislabelled
    // the state. Now it leaves the loop deaf, so a turn that ends with nothing to speak must still recover.
    [Fact]
    public async Task AppJs_RetriesTheRearmSoAMutedMicIsNeverStuck()
    {
        var app = await _fixture.GetAppScriptAsync();
        var maybeArm = StaticAssetsServerFixture.Body(app, "function maybeArmListening()");

        Assert.Contains("rearmTimer = setTimeout(maybeArmListening", maybeArm);
        Assert.Contains("clearTimeout(rearmTimer)", maybeArm);
    }

    // The tooltip promised "talk over it to interrupt", which is now true only on a headset. User-facing
    // text that contradicts the code is the doc debt this repo fixes in the same MR.
    [Fact]
    public async Task Index_VoiceTooltipNamesTheNewGesture()
    {
        var index = await _client.GetStringAsync("/");
        var title = index[index.IndexOf("id=\"voiceBtn\"", StringComparison.Ordinal)..];
        title = title[..title.IndexOf('>')];

        Assert.Contains("Esc", title, StringComparison.Ordinal);
        Assert.DoesNotContain("talk over it to interrupt", title, StringComparison.Ordinal);
    }

    // The text between one declaration and the next, for a function Body()'s brace matching cannot handle.
    private static string Slice(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' not found in the front-end script — did it get renamed?");
        var end = source.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"'{to}' no longer follows '{from}' — the slice is wrong.");
        return source[start..end];
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
