---
id: 0114
date: 2026-10-21
status: accepted
tags: [android, client, voice, speech, barge-in, full-duplex, auth, easy-auth, client-tools, egress, release]
supersedes: []
extends: [0002, 0006, 0037, 0089, 0090]
amends: [0079]
---

# A native Android companion app: chat plus a full-duplex voice call, on the existing API and Easy Auth session

## Context
The PWA covers chat on a phone, but not voice the way a phone should do it. ADR 0079 had to close the microphone while
the assistant speaks on a speaker, because the browser loop heard its own TTS and answered itself, so talking over the
reply only works on a headset. A browser also stops listening when the screen locks, and can't route through car
Bluetooth like a call. Android can do all three. Two unknowns decided the shape, and both were measured on the owner's
phone (Galaxy A54, Android 16, nl-NL) on 2026-10-21. The evidence is in
`.agentic/specs/completed/plan-android-companion-app.md`, "Spike results".

## Decision
- **App:** Kotlin + Jetpack Compose in `tools/Agent42.Android`, next to the other non-server clients (the Chrome
  extension, the local bridge). It's a client of the existing HTTP API with **no new server endpoint**:
  - `POST /api/chat` NDJSON is read by `Agent42Api`.
  - The wire format is pinned by a golden file, `tools/Agent42.Android/contract/stream-events.ndjson`. Both
    `AndroidStreamContractTests` (against the production `ChatStreamJson.Options`) and the app's
    `StreamEventParserTest` read it.
- **Auth is the Easy Auth session cookie from a WebView sign-in** (ADR 0002/0090), with **no Entra or Bicep change**.
  - The app loads `/.auth/login/aad` in a WebView, reads `AppServiceAuthSession` from `CookieManager`, and sends it as a
    `Cookie` header from OkHttp.
  - Measured: Entra sign-in completes inside the WebView; the cookie works from outside it; a POST with no `Origin` is
    accepted.
  - An unauthenticated **non-browser** request gets a **401**, not the login redirect. The app treats a 401, a redirect
    or an HTML page as "signed out" and signs in again. That happens at most every 14 days (ADR 0090's FixedTime
    session).
- **Client tools ride the existing `ChatRequest.clientTools`** (ADR 0089). The server answers every tool the client
  didn't list with `{"status":"unavailable"}` at once, so the app can't stall a turn.
  - The chat screen, while in the foreground, lists `get_current_location` and `confirm_action`.
  - Voice mode lists **nothing**.
  - The app never lists `control_artifact_pane`, `get_browser_context` or `mcp_elicitation`.
- **Voice is full duplex on the phone's own speaker.** This **amends ADR 0079 for the native client only**. The setup:
  - `AudioManager.MODE_IN_COMMUNICATION`;
  - the reply routed to the loudspeaker, or to a connected headset or Bluetooth device. How: see "The call is a
    Telecom call" below. `setCommunicationDevice` is only the fallback;
  - every reply played with `USAGE_VOICE_COMMUNICATION` attributes;
  - the default `SpeechRecognizer` listening throughout.

  That puts playback on the call path, where the phone's echo canceller has the reference.

  | Setup (measured) | Self-echo while silent | Heard the user over the reply |
  |---|---|---|
  | Browser-like: media playback, normal mode | 1 (its own sentence, verbatim) | 1 |
  | Call path (the decision) | 0 | 2 |

  Barge-in = stop playback + `POST /api/chat/{id}/stop` (ADR 0078). Reasoning is never spoken (ADR 0071).

  **The call path reduces self-echo; it does not remove it.** In the first real call (the voice build's trace,
  2026-10-21), the recognizer twice transcribed a sentence of the reply verbatim while it played on the speaker. So the
  web client's word-overlap check (`isSelfOrEcho`, ported as `SpeechText.isSelfOrEcho`) is **load-bearing**, together
  with two guards the first build lacked:
  - a partial result needs **two words** to interrupt (four one-word scraps were held back before the real
    interruption, *"Wanneer is"*);
  - for **2.5 s after the reply ends**, a result that echoes it is dropped instead of being sent as the next turn.

  The build without these guards answered its own reply.
- **The call is a self-managed Telecom call** (androidx core-telecom `CallsManager.addCall`, outgoing, audio,
  `SUPPORTS_SET_INACTIVE`). That puts it in the system call UI and on the lock screen; car and headset buttons hang it up,
  and a real incoming call puts it on hold, which closes the mic and stops the speech.
  - Telecom owns the route: the app uses its endpoints (`requestEndpointChange`) and **never** `setCommunicationDevice`
    during the call. The library's docs say doing so breaks call audio.
  - The call starts on a headset if one is connected, else the loudspeaker. The platform sets `MODE_IN_COMMUNICATION`
    itself (measured).
  - The plain `AudioRoute`, with `setCommunicationDevice` and transient focus, is used only when `addCall` fails.
  - The foreground service is `phoneCall|microphone` (`MANAGE_OWN_CALLS`), with a CallStyle notification.
  - Alternative rejected: a hand-rolled `ConnectionService`. It is the same platform with more code to own.
- **Entry points:** a Quick Settings tile, a home-screen widget (plain RemoteViews, no Glance), app shortcuts, and
  `ACTION_ASSIST` / `VOICE_COMMAND` filters.
  - The `ACTION_ASSIST` filter alone makes the app selectable as the digital assistant (measured: it is listed with
    Google, Claude and ChatGPT), so no `VoiceInteractionService` is needed.
  - Android 14 only lets a foreground app start a microphone call, so every entry point opens the activity, which
    returns to a running call or starts a new voice chat.
- **The voice trace** (`adb logcat -s A42VOICE`) logs every loop decision. Conversation text (heard, spoken) is
  included **only in a debug build**; a release build logs its length instead (`VoiceSession.redact`).
- **STT is the platform recognizer.**
  - Dutch has **no on-device model** on the A54 (`supported=false`). The default recognizer is Google's network
    service, the same egress as Chrome's Web Speech today (ADR 0006), so this is **not** a new ADR 0013 extension.
  - On-device is used where the language has a model, so that seam stays.
  - Server STT is the later path: an endpoint over ADR 0113's `ISpeechTranscriptionService`, on the user's own
    transcription model.
- **TTS:** the reply is spoken through `POST /api/speech` (ADR 0038). On 409 (no speech model) it falls back to the
  platform `TextToSpeech`, preferring an offline voice. A network voice sends the reply text to Google, the same exposure
  as the browser's `speechSynthesis` fallback.
- **Distribution:** sideloaded. `.github/workflows/android.yml`:
  - checks PRs that touch the app (unit tests, lint, debug build);
  - on main or an `android-v*` tag, publishes a signed APK as a GitHub Release (draft, upload, then publish).

  `versionCode` = major·10000 + minor·100 + patch, so updates install. The signing key is the four `ANDROID_*` secrets
  and must never change.

## Alternatives considered
- **A TWA (the PWA in a Play Store wrapper).** It's the browser again, so it gets none of the voice gains.
- **.NET MAUI**, to stay in C#. It would let us reuse no server types (they live in Backend/Agents), and audio-mode,
  Telecom and recognizer APIs are thinner there. Compose is the platform's own toolkit.
- **MSAL + Easy Auth's client-directed flow (`X-ZUMO-AUTH`), or the MSAL token as `Bearer`.** Not run: the cookie
  path met every criterion with no new app registration, no `allowedAudiences` and no infra.
- **Our own `AudioRecord(VOICE_COMMUNICATION)` with `AcousticEchoCanceler`, piped into the recognizer
  (`EXTRA_AUDIO_SOURCE`).** Measured: the default recognizer never transcribed the piped audio. It's more code and
  worked worse.
- **Keep ADR 0079's half duplex in the app as well.** Rejected: the measurement removed the reason for it.

## Consequences
- **No server change apart from `ChatStreamJson`** (the NDJSON options, made public for the contract test). Adding a
  stream event type now also means a fixture line, or `AndroidStreamContractTests` fails.
- **The evidence is narrow:** one phone, a few runs, a quiet room, and so far only the platform voice (the first call
  spoke all 14 sentences through `TextToSpeech`, with no server MP3). Server MP3s on the call path still need the same
  check. Another phone may need ADR 0079's fallback (mic closed on the speaker), which the app keeps as
  `DuplexPolicy(fullDuplexOnSpeaker = false)`.
- `adb logcat -s A42VOICE` traces every decision of a call (heard, dropped as echo, too short, barge-in, route, voice
  path). That is how the guards above were found and verified.
- **Kotlin code is not under the coverage gate.** No repo tool measures it (ADR 0050). The Gradle unit tests run in
  `android.yml`, but that check is path-filtered, so it isn't a required check.
- **Deferred:**
  - FCM push (a new sender and possible content egress, ADR 0013/0028);
  - background location in voice mode;
  - server STT;
  - realtime speech-to-speech;
  - rich rendering (mermaid, math, artifacts open in the web app instead).
