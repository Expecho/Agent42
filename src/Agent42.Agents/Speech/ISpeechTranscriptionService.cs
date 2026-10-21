namespace Agent42.Agents;

/// <summary>
/// Transcribes recorded speech to text (the server-side speech-to-text ADR, amending ADR 0038, which was TTS only).
/// The chat gateway (ADR 0111) uses it to turn a voice note into a turn. It runs over the user's own provider with
/// the transcription model they picked; <b>there is no operator model</b> (ADR 0019/0030), so a user without one gets
/// <c>null</c> from <see cref="IChatClientFactory.GetTranscriptionService"/> and a curated reply in the chat.
///
/// <para>Named for what it does, not <c>ISpeechToTextClient</c>: Microsoft.Extensions.AI ships a type of that name,
/// and a clash is an ambiguous reference (CS0104) in Backend files importing both (the trap
/// <see cref="ISpeechService"/> records).</para>
///
/// <para><b>Egress (ADR 0013):</b> the audio goes to the user's configured endpoint, the same trust path as their
/// chat model.</para>
/// </summary>
public interface ISpeechTranscriptionService
{
    /// <summary>
    /// Returns the transcript of <paramref name="audio"/>. <paramref name="fileName"/>'s extension is what the
    /// endpoint keys the format on. Throws <see cref="SpeechTranscriptionException"/> when the provider refuses the
    /// audio (an unsupported format, a file too large), so the caller can answer with curated text.
    /// </summary>
    Task<string> TranscribeAsync(byte[] audio, string fileName, string mediaType, CancellationToken cancellationToken = default);
}

/// <summary>The provider refused to transcribe the audio. The message is ours, never the provider's.</summary>
public sealed class SpeechTranscriptionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Which recordings are sent for transcription (spike 1g, 2026-10-21). OpenAI and Azure OpenAI both document
/// <c>mp3 mp4 mpeg mpga m4a wav webm</c>, at most 25 MB. <b>Ogg is not on either list</b>, yet Telegram, Discord and
/// WhatsApp voice notes are Ogg/Opus, and ffmpeg is out of scope (image size, CVE surface). So Ogg is still SENT, as
/// itself, and a refusal becomes the curated "can't transcribe this format" reply: one failed call is cheaper than a
/// voice feature that never works where it could. Whether Ogg is accepted is checked live in Step 19.
/// </summary>
public static class SpeechTranscriptionFormats
{
    /// <summary>The documented upload cap for both providers.</summary>
    public const long MaxBytes = 25 * 1024 * 1024;

    /// <summary>The documented formats (by extension, which is what the endpoint keys on).</summary>
    public static readonly IReadOnlySet<string> Documented = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".mp4", ".mpeg", ".mpga", ".m4a", ".wav", ".webm",
    };

    /// <summary>Undocumented but attempted: the voice-note container of three of the platforms.</summary>
    public static readonly IReadOnlySet<string> Attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".ogg", ".oga", ".opus",
    };

    /// <summary>
    /// The file name to send <paramref name="fileName"/> under, or <c>null</c> when the format is neither documented
    /// nor attempted. A missing or unknown extension is inferred from <paramref name="mediaType"/> (Slack and
    /// Telegram label voice clips loosely).
    /// </summary>
    public static string? UploadName(string? fileName, string? mediaType)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "voice" : Path.GetFileName(fileName);
        var extension = Path.GetExtension(name);
        if (Documented.Contains(extension) || Attempted.Contains(extension))
        {
            return name;
        }

        var inferred = (mediaType ?? "").Split(';', 2)[0].Trim().ToLowerInvariant() switch
        {
            "audio/ogg" or "audio/opus" => ".ogg",
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" or "audio/m4a" or "audio/x-m4a" or "audio/aac" => ".m4a",
            "video/mp4" => ".mp4",
            "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
            "audio/webm" or "video/webm" => ".webm",
            _ => null,
        };
        return inferred is null ? null : Path.GetFileNameWithoutExtension(name) + inferred;
    }
}

/// <summary>
/// Offline <see cref="ISpeechTranscriptionService"/> for DevHost and tests: no real transcription endpoint is reachable
/// there, so it returns <see cref="Transcript"/> for any audio.
/// </summary>
public sealed class InMemorySpeechTranscriptionService(string transcript = InMemorySpeechTranscriptionService.DefaultTranscript)
    : ISpeechTranscriptionService
{
    public const string DefaultTranscript = "This is a transcribed voice note.";

    public string Transcript { get; } = transcript;

    public Task<string> TranscribeAsync(byte[] audio, string fileName, string mediaType, CancellationToken cancellationToken = default)
        => Task.FromResult(Transcript);
}
