using Agent42.Agents;
using OpenAI.Audio;
using Agent42.Agents.Speech;

namespace Agent42.Backend;

/// <summary>
/// Production <see cref="ISpeechService"/> (ADR 0038) over the OpenAI SDK <see cref="AudioClient"/>:
/// text-to-speech via <c>GenerateSpeechAsync</c>. The <c>/api/speech</c> endpoint streams the returned
/// bytes straight to the browser; they are never persisted into the session (ADR 0004).
///
/// <para>The <see cref="AudioClient"/> is built the SAME transport way as the chat/image/embedding
/// clients (<see cref="MultiProviderChatClientFactory.Provider"/>) — off the SDK's own
/// <c>OpenAIClient</c>/<c>AzureOpenAIClient</c>, NOT an <c>IHttpClientFactory</c> client — because a named
/// <c>HttpClient</c> silently inherits <c>AddServiceDefaults</c>' 3-retry resilience pipeline (see the
/// "named HttpClient inherits the retry pipeline" gotcha). The endpoint still comes only from the
/// provider's config — SSRF invariant intact.</para>
///
/// <para>The voice is fixed to <c>Alloy</c> and the format to MP3 — a single server-side transport choice,
/// not user-selectable (the model itself is the user's pick). A spike (pinned OpenAI 2.12.0) confirmed the
/// surface: <c>GenerateSpeechAsync(text, GeneratedSpeechVoice, SpeechGenerationOptions, ct)</c> returns
/// <c>ClientResult&lt;BinaryData&gt;</c>. The LIVE <c>/v1/audio/speech</c> exchange is not reachable offline
/// (<see cref="InMemorySpeechService"/> stands in), so this class is exercised by prod smoke, not a unit test.</para>
/// </summary>
public sealed class OpenAISpeechService(AudioClient client) : ISpeechService
{
    public async Task<SynthesizedSpeech> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await client.GenerateSpeechAsync(
            text,
            GeneratedSpeechVoice.Alloy,
            new SpeechGenerationOptions { ResponseFormat = GeneratedSpeechFormat.Mp3 },
            cancellationToken);

        return new SynthesizedSpeech(result.Value.ToArray(), "audio/mpeg");
    }
}
