namespace Agent42.Agents.Speech;

/// <summary>
/// Synthesizes speech audio from text (ADR 0038): the server-side text-to-speech path the hands-free
/// voice conversation (ADR 0037) uses when the user has picked a <b>default speech model</b>, instead of
/// the browser's <c>speechSynthesis</c>. This is a different API from chat (OpenAI-compatible
/// <c>/v1/audio/speech</c>, not <c>/v1/chat/completions</c>) — the same shape as
/// <see cref="IImageGenerationService"/>, which wraps a distinct SDK sub-client.
///
/// <para>Named <c>ISpeechService</c>, NOT a <c>*SpeechToText*</c>/<c>ITextToSpeech*</c> name, on purpose:
/// Microsoft.Extensions.AI 1.13 ships its own <c>Microsoft.Extensions.AI.ISpeechToTextClient</c>, and a
/// same-named type here would be an ambiguous reference (CS0104) in Backend files that import both
/// namespaces — the exact trap <see cref="IImageGenerationService"/> documents. We keep a minimal in-house
/// abstraction rather than adopt the heavier M.E.AI shape.</para>
///
/// <para>Production is <c>OpenAISpeechService</c> (Backend, over the OpenAI SDK <c>AudioClient</c>); local
/// dev and tests use <see cref="InMemorySpeechService"/>, which returns a tiny canned WAV — the real
/// <c>/v1/audio/speech</c> contract is spike-verified (OpenAI 2.12.0), not reachable offline (same
/// limitation ADR 0004 documents for images).</para>
///
/// <para><b>Egress (ADR 0013):</b> a production implementation sends the reply text to a user-configured
/// endpoint OUTSIDE the Entra tenant. That is the headline consequence recorded in ADR 0038 — and why the
/// browser voice (ADR 0037), which never leaves the browser, stays the default until a speech model is picked.</para>
/// </summary>
public interface ISpeechService
{
    /// <summary>
    /// Synthesizes <paramref name="text"/> to audio, returning the raw bytes + media type (e.g. MP3 →
    /// <c>audio/mpeg</c>). The caller (the <c>/api/speech</c> endpoint) streams the bytes straight to the
    /// browser; they are never persisted into the session.
    /// </summary>
    Task<SynthesizedSpeech> SynthesizeAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Synthesized speech: its raw audio bytes and media type. Streamed to the browser, never stored.</summary>
public sealed record SynthesizedSpeech(byte[] Bytes, string MediaType);

/// <summary>
/// In-memory <see cref="ISpeechService"/> for local dev and tests, where no real <c>/v1/audio/speech</c>
/// endpoint is reachable (the same offline limitation as <see cref="InMemoryImageGenerator"/>). Returns a
/// tiny canned silent WAV for every call — enough to exercise the endpoint/playback flow end to end; it
/// never inspects the text. Stateless, so a plain instance suffices.
/// </summary>
public sealed class InMemorySpeechService : ISpeechService
{
    // A minimal 44-byte silent WAV header (PCM, mono, 8 kHz, 8-bit, zero samples) — the smallest valid WAV,
    // so the offline flow has real, well-formed bytes without shipping an asset. The returned array is copied
    // per call so a caller can never mutate the source.
    private static readonly byte[] CannedWav = Convert.FromBase64String(
        "UklGRiQAAABXQVZFZm10IBAAAAABAAEAQB8AAEAfAAABAAgAZGF0YQAAAAA=");

    public Task<SynthesizedSpeech> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(new SynthesizedSpeech([.. CannedWav], "audio/wav"));
}
