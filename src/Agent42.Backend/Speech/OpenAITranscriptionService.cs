using System.ClientModel;
using Agent42.Agents;
using OpenAI.Audio;

namespace Agent42.Backend;

/// <summary>
/// Production <see cref="ISpeechTranscriptionService"/> over the OpenAI SDK <see cref="AudioClient"/>: speech-to-text
/// via <c>TranscribeAudioAsync(Stream, fileName, options, ct)</c> (OpenAI 2.14.0, checked in <c>OpenAI.xml</c>). The
/// chat gateway turns a voice note into a turn with it (ADR 0111).
///
/// <para>The <see cref="AudioClient"/> comes from the provider's own SDK client, exactly like
/// <see cref="OpenAISpeechService"/>, NOT an <c>IHttpClientFactory</c> client (a named <c>HttpClient</c> inherits
/// <c>AddServiceDefaults</c>' retry pipeline). The endpoint comes only from the user's provider config.</para>
///
/// <para>A 4xx from the provider (an unsupported format, a file over the cap) becomes a
/// <see cref="SpeechTranscriptionException"/> carrying OUR text, never the provider's, which can echo the request.
/// Anything else (a 5xx, a network failure) propagates for the caller to log.</para>
/// </summary>
public sealed class OpenAITranscriptionService(AudioClient client) : ISpeechTranscriptionService
{
    public async Task<string> TranscribeAsync(byte[] audio, string fileName, string mediaType, CancellationToken cancellationToken = default)
    {
        try
        {
            using var stream = new MemoryStream(audio, writable: false);
            var result = await client.TranscribeAudioAsync(
                stream,
                fileName,
                new AudioTranscriptionOptions { ResponseFormat = AudioTranscriptionFormat.Simple },
                cancellationToken);
            return result.Value.Text ?? "";
        }
        catch (ClientResultException ex) when (ex.Status is >= 400 and < 500)
        {
            throw new SpeechTranscriptionException("The transcription model could not process this audio.", ex);
        }
    }
}
