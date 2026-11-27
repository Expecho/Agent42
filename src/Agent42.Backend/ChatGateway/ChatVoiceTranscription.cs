using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Voice notes (ADR 0111 phase 1c, over the server-side speech-to-text of ADR 0113, which amends ADR 0038). A voice
/// note is transcribed BEFORE the route is touched, so a recording that can't be read mints no conversation. The
/// transcript becomes the turn's text, marked as a voice note for the model, and is echoed back as one quoted line so
/// the owner sees what was heard (Hermes' <c>stt_echo_transcripts</c>). Any failure gets exactly ONE curated reply,
/// never the provider's text and never two notices (Hermes <c>run_inbound.py</c>).
///
/// <para>The audio goes to the owner's own transcription model (their provider, the same trust path as their chat
/// model). There is no operator model: without one the owner is told to add it. A host that registers no
/// <see cref="IUserChatClientFactoryProvider"/> (<paramref name="chatClients"/> null) answers the same way.</para>
///
/// <para>It never sends anything itself: the one reply, if any, comes back in <see cref="VoiceNote.Reply"/> for the
/// pipeline to send.</para>
///
/// <para>Public only because the pipeline's public constructor takes it and this repo has no
/// <c>InternalsVisibleTo</c>.</para>
/// </summary>
public sealed class ChatVoiceTranscription(
    ChatSessionRegistry sessions,
    TimeProvider timeProvider,
    ILogger<ChatVoiceTranscription> logger,
    IUserChatClientFactoryProvider? chatClients = null)
{
    private const string VoiceOffText = "Voice notes are turned off for this chat. Turn them on for this connection in Agent42, or type your message.";
    private const string NoTranscriptionModelText = "To use voice notes, add a transcription model to one of your model providers in Agent42 and pick it as your default.";
    private const string UnsupportedVoiceText = "I can't transcribe that kind of recording. Try again, or type your message.";
    private const string TranscriptionFailedText = "I couldn't transcribe that voice note. Your transcription model may not accept this platform's recordings; try typing instead.";
    private const int MaxEchoChars = 300;
    private static readonly TimeSpan TranscriptionTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The outcome of reading a message's voice note: whether the turn goes on, its transcript, and the one reply to send first.</summary>
    internal sealed record VoiceNote(bool Proceed, string? Transcript, string? Reply = null)
    {
        public static VoiceNote None { get; } = new(true, null);
    }

    // The first voice note, transcribed. Proceed is false when the turn must not run (Reply says why).
    internal async Task<VoiceNote> TranscribeVoiceNoteAsync(ChatTurnWork work)
    {
        if (work.Message.Files.FirstOrDefault(f => f.IsVoiceNote) is not { } voice)
        {
            return VoiceNote.None;
        }

        var transcriber = await TranscriberAsync(work.UserId);
        if (VoiceRefusal(work, voice, transcriber) is { } refusal)
        {
            return new VoiceNote(false, null, refusal);
        }

        var transcript = await TranscribeAsync(work, voice, transcriber!);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return new VoiceNote(false, null, transcript is null ? TranscriptionFailedText : "I couldn't hear any words in that voice note.");
        }

        transcript = transcript.Trim();
        var echo = transcript.Length <= MaxEchoChars ? transcript : transcript[..MaxEchoChars] + "…";
        return new VoiceNote(true, transcript, "> " + echo.ReplaceLineEndings(" "));
    }

    // The turn's words: a voice note's transcript, marked so the model knows it was spoken, then any caption.
    internal static string WithTranscript(string text, string? transcript)
        => transcript is null ? text : ("[Voice note] " + transcript + (text.Length > 0 ? "\n" + text : "")).Trim();

    // Why a voice note can't be read at all, judged before anything is downloaded; null when it can be tried.
    private static string? VoiceRefusal(ChatTurnWork work, ChatInboundFile voice, ISpeechTranscriptionService? transcriber)
    {
        if (!work.Connection.VoiceNotesEnabled)
        {
            return VoiceOffText;
        }

        if (SpeechTranscriptionFormats.UploadName(voice.FileName, voice.DeclaredMediaType) is null
            || voice.Size > SpeechTranscriptionFormats.MaxBytes)
        {
            return UnsupportedVoiceText;
        }

        return transcriber is null ? NoTranscriptionModelText : null;
    }

    private async Task<ISpeechTranscriptionService?> TranscriberAsync(string userId)
    {
        if (chatClients is null)
        {
            return null;
        }

        try
        {
            return (await chatClients.GetForUserAsync(userId)).GetTranscriptionService();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Resolving a transcription model for a chat voice note failed.");
            return null;
        }
    }

    // Downloads and transcribes; null on any failure (the caller sends the one curated reply).
    private async Task<string?> TranscribeAsync(ChatTurnWork work, ChatInboundFile voice, ISpeechTranscriptionService transcriber)
    {
        if (sessions.Get(work.Connection.Id) is not { } session)
        {
            return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TranscriptionTimeout, timeProvider);
            if (await session.DownloadAsync(voice, SpeechTranscriptionFormats.MaxBytes, timeout.Token) is not { } audio)
            {
                return null;
            }

            // Non-null: VoiceRefusal already accepted this name and type.
            var name = SpeechTranscriptionFormats.UploadName(voice.FileName, voice.DeclaredMediaType)!;
            return await transcriber.TranscribeAsync(audio.Bytes, name, voice.DeclaredMediaType ?? audio.MediaType ?? "application/octet-stream", timeout.Token);
        }
        catch (Exception ex)
        {
            // SpeechTranscriptionException is the provider refusing the audio; anything else is a transport failure. The
            // owner gets the same curated line either way.
            logger.LogWarning(ex, "A chat voice note on connection {ConnectionId} could not be transcribed.", work.Connection.Id);
            return null;
        }
    }
}
