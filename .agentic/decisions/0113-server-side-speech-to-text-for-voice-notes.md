---
id: 0113
date: 2026-10-21
status: accepted
tags: [speech, stt, transcription, voice, providers, chat-apps, egress, privacy, no-migration]
supersedes: []
amends: [0038]
extends: [0013, 0018, 0030, 0111]
---

# Server-side speech-to-text for chat voice notes, over a per-user transcription model

## Context
ADR 0038 added server-side **text-to-speech** only and said "STT would be a separate ADR". In the browser, the user
still speaks through `SpeechRecognition` (ADR 0006/0037), which never leaves the browser. The chat gateway (ADR 0111)
needs STT where no browser exists: a voice note sent to the owner's Telegram, Slack or Discord bot arrives as an audio
file, and the plan's G4 puts voice notes in phase 1c. There is no operator model of any kind (ADR 0019/0030), so the
model has to be the user's own.

## Decision
Add a **transcription (speech-to-text) model kind**, mirroring ADR 0038's speech model field for field:
- **Capability flag `SupportsTranscription`** on a declared model (`StoredModel` `[Id(8)]`, `ConfiguredModel`,
  `ModelInfo`, and the provider editor's 7th capability checkbox). It filters pickers only, never per-turn chat
  resolution, so a transcription model is not a chat or fleet model.
- **A default transcription model** on the overlay (`UserProviderConfig.TranscriptionDefault` `[Id(5)]`,
  `SetTranscriptionDefaultAsync`, `PUT /api/default-transcription-model`). The resolver validates it once
  (`EffectiveTranscriptionDefault`): it resolves only when it names a declared transcription model, with no fallback.
- **A service seam** `ISpeechTranscriptionService.TranscribeAsync(bytes, fileName, mediaType) → text`, from
  `IChatClientFactory.GetTranscriptionService()` (a default interface member returning `null`).
  - Production is `OpenAITranscriptionService`, over the SDK `AudioClient.TranscribeAudioAsync`, built off the same
    provider client as chat (never a named `HttpClient`).
  - A 4xx from the provider becomes `SpeechTranscriptionException` with **our** text. The provider's message never
    reaches a chat.
  - The offline hosts use `InMemorySpeechTranscriptionService`.
- **The gateway's use (ADR 0111 phase 1c):** a voice note is transcribed **before** the route is touched. Any failure
  gets **one** curated reply and mints no conversation:
  - voice notes are off for the connection;
  - the user has no transcription model;
  - the format or size is unsupported;
  - the provider refused the audio.

  The transcript becomes the turn's text, prefixed `[Voice note]` for the model. It is echoed back as one quoted line,
  so the owner sees what was heard. The conversation is titled by the transcript.

### Formats (spike 1g, 2026-10-21)
OpenAI's and Azure OpenAI's current docs both list **mp3, mp4, mpeg, mpga, m4a, wav and webm**, at most **25 MB**.
**Ogg is on neither list**, yet Telegram, Discord and WhatsApp voice notes are Ogg/Opus. Slack clips are m4a/mp4.
ffmpeg stays out (image size, CVE surface), so:
- documented formats are sent as they are;
- **Ogg (`.ogg`, `.oga`, `.opus`) is still sent, as itself**. A refusal becomes the curated "couldn't transcribe" reply.
  Whether either provider accepts Ogg is **unverified until the Step 19 live check**. One failed call is cheaper than
  a voice feature that never works where it might;
- anything else gets the curated "can't transcribe that kind of recording" reply, and is never downloaded.

OpenAI's docs also mark `whisper-1`, `gpt-4o-transcribe` and `gpt-4o-mini-transcribe` deprecated, shutting down on
2027-02-26, and recommend `gpt-transcribe`. The model is the user's pick, so nothing in code names one.

## Alternatives considered
- **ffmpeg in the image to transcode Ogg to a documented format.** Rejected for this plan: a much larger image and a
  standing CVE surface for one feature. Revisit if Step 19 shows that Ogg is refused.
- **A hand-written Ogg → WebM remux.** Both containers carry Opus, so this needs no transcoding, but a container
  writer is real code to own. Deferred behind the same live check.
- **Reuse the speech (TTS) model for STT.** Rejected: TTS and STT are separate models and separate deployments on
  both providers. A flag on one model would lie about the other.
- **Transcribe in the browser and send text.** That doesn't apply: a chat app has no Agent42 browser.

## Consequences
- **Egress (ADR 0013):** the owner's voice notes go to the owner's configured endpoint, on the same trust path as
  their chat model. Nothing is sent without both the connection's voice toggle and a chosen transcription model.
- New persisted fields are trailing with defaults (rule 2). Old documents read back `false` and `null`, which the
  Cosmos JSON path proves (`GrainStorageSerializerTests`). No migration is needed.
- The browser voice (ADR 0006/0037) is unchanged: browser recognition is still the composer's path. This ADR adds no
  browser STT.

## Verification
- `OpenAITranscriptionServiceTests` use a stub SDK transport. They check the POST to `/audio/transcriptions`, the
  multipart file name and model, the transcript, and that a 400 becomes `SpeechTranscriptionException` with none of
  the provider's text.
- `UserProviderResolverTests` cover the default against declared, non-transcription and missing models.
  `ProvidersApiTests` cover the flag and the default endpoint.
- In `ChatTurnPipelineTests`, `Pipeline_Voice_BecomesATurn_WithTheTranscriptEchoed` and
  `Pipeline_Voice_Failure_RepliesOnce_AndRunsNoTurn` (off, no model, format, refused) each check that there is
  exactly one reply, no conversation, and no download before a refusal.
- Not yet verified live: a real transcription deployment, and whether Ogg/Opus is accepted by OpenAI and Azure
  (plan Step 19).
