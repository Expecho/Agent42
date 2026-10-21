planned-against: main @ f027feb

# Plan: Android companion app (voice-first)

## Overview

A native Android app (Kotlin + Jetpack Compose) for Agent42 that does the basic chat and adds a
**voice-call mode** a browser can't do. The ADR 0037 hands-free loop had to turn off barge-in on the
phone speaker because the browser can't cancel its own TTS echo (ADR 0079 amendment, headset only).
On Android, the `VOICE_COMMUNICATION` audio path with hardware AEC, a microphone foreground service
and Telecom integration can deliver: screen-off conversation, barge-in on the speaker, and car
Bluetooth "call Agent 42". Phase 1 = chat + voice mode. Phase 2 = system call UI, assistant role,
tile, widget. Both are in this plan (user decision).

## Affected areas

Verified against the tree at f027feb:

- **New** `tools/Agent42.Android/`. Gradle project, Kotlin + Compose, a single `app` module. It
  follows the precedent of `tools/Agent42.ChromeExtension/` and `tools/Agent42.LocalBridge/`:
  non-server clients live under `tools/`, and there's no `clients/` dir.
- **New** `.github/workflows/android.yml`. Build, test and lint on PR; signed APK release on main and
  on tags. Mirrors `.github/workflows/release-chrome-extension.yml`.
- **New** contract fixture `tools/Agent42.Android/contract/stream-events.ndjson`, plus a .NET test
  in `tests/Agent42.Tests/` that pins it against `src/Agent42.Agents/Conversations/AgentStreamEvent.cs`.
- **Possibly** `infra/modules/container-app.bicep` and `infra/main.bicep` (Easy Auth
  `validation.allowedAudiences`, `jwtClaimChecks`, `tokenStore`). Only needed if Step 1 picks the
  MSAL client-directed flow.
- `.gitignore`: Android/Gradle entries.
- **New** ADR in `.agentic/decisions/`. Also `CLAUDE.md` (stack line, commands table, manual
  prerequisites, feature-map row), `.agentic/gotchas.md` (traps found on device).
- **One small backend C# change:** `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs:14`.
  `StreamEventJson` is `private static`, and its only use is `:223`. Extract it to a public static
  holder in the same folder (flat namespace, ADR 0039), e.g. `ChatStreamJson.Options`, so the
  contract test serializes with the production options. `Agent42.Backend` has no
  `InternalsVisibleTo`, so it has to be public, not internal. No other server change is expected:
  the client-tool capability signal already exists (see Context).
- **New** `tests/Agent42.Tests/AndroidReleaseTests.cs`. It pins `android.yml`'s triggers, version
  stamping and draft-then-publish, in the shape of `ChromeExtensionReleaseTests.cs` and
  `LocalBridgeReleaseTests.cs`.

## Context & constraints

Executors: read this. None of it is in the code in one place.

### The client-tool stall concern is already solved server-side
`ChatRequest.clientTools` (`src/Agent42.Backend/Conversations/ChatContracts.cs:103-106`) is the
capability declaration.
- The server intersects it with `ClientToolLimits.AdvertisableCapabilities`
  (`AgentApi.Conversations.cs:277-281`).
- A tool the client did not advertise answers `{"status":"unavailable"}` immediately
  (`src/Agent42.Agents/ClientTools/AgentGrain.ClientTools.cs:117-124`).
- An advertised tool that gets no answer returns `{"status":"timeout"}` after 15 s, or 120 s for
  `confirm_action` and `mcp_elicitation` (`src/Agent42.Agents/ClientTools/ClientToolLimits.cs:50,53`). The turn continues either
  way.

So the Android client advertises only what it implements, and only while the chat screen is in the
foreground:
- `get_current_location`.
- `confirm_action`.

**Voice mode advertises nothing.** Location with the screen off would need
`ACCESS_BACKGROUND_LOCATION` plus a location foreground-service type. That's deferred (critic
finding 5).
- Never `control_artifact_pane`, `get_browser_context` or `mcp_elicitation`.

Gotcha `.agentic/gotchas.md:4231` covers the broker design. The result body is
`POST /api/chat/{id}/tool-result {toolCallId, result}`, capped at 8 KB.

**The location result shape must match what `wwwroot/js` posts.** Read the web client's location
tool before implementing.

### HTTP contract
See the scout summary below. Re-read the cited files rather than trusting it.
- **Chat:** `POST /api/chat/{id}` (body `ChatContracts.cs:14-17`; the client generates the
  conversation id as a UUID) streams NDJSON, one `AgentStreamEvent` per line
  (`AgentStreamEvent.cs:6-160`, camelCase, nulls omitted). There's no error event.
- **No resumable stream (ADR 0046).** A dropped stream is recovered by re-fetching
  `GET /api/conversations/{id}/messages` and `GET /api/chat/{id}/pending-tool-calls`, as in
  `wwwroot/js/send.js:71-96`. The server keeps running the turn after a disconnect (gotcha :2612).
- **Model:** set per conversation via the `model` field on `POST /api/chat`. There's no PUT. Read it
  with `GET /api/conversations/{id}/model`; the list is `GET /api/models` (`{default, models[]}`).
  Hide `copilotOnly` models (ADR 0108).
- **Attachments:** multipart `POST /api/conversations/{id}/attachments`, max 10 MB. Images return
  `{id}` for `attachmentIds`. Documents return `{kind:"document"}` with no id.
- **TTS only:** `POST /api/speech {text}` returns one whole MP3. It returns 409 when the user has no
  speech model, and the web client then falls back to `speechSynthesis` (`voice.js:9-21`). Android
  falls back to `TextToSpeech` the same way. There's no STT endpoint, and server STT was rejected in
  ADR 0006/0037/0038.
- **Reasoning events are never spoken** (ADR 0071). `textReset` must reset both the spoken queue and
  the shown text.
- **Stop:** `POST /api/chat/{id}/stop` always returns 204 (ADR 0078). Barge-in = stop playback +
  POST stop, the same as the web client after ADR 0078.

### Auth is the main unknown (Step 1 spike)
- The backend reads only the `X-MS-CLIENT-PRINCIPAL*` headers (`src/Agent42.Backend/Api/CurrentUser.cs:24-27`).
- A request without them silently becomes `local-dev` (`:160-161`). That's harmless in prod, where
  ingress injects them, but it means **the app can't tell the two auth paths apart**: whichever path
  is used, Easy Auth must inject the headers.
- Easy Auth config: `infra/modules/container-app.bicep:383-476`.
  - `unauthenticatedClientAction: RedirectToLoginPage`, so the native client sees a 302 to
    login.microsoftonline.com rather than a 401. The client must treat a redirect or HTML response
    as "signed out".
  - No `allowedAudiences`, no `tokenStore`, no CORS.
  - `allowedPrincipals` limits access to two object ids.
- The Entra app registration (`agent42-easyauth`, client id in `infra/main.bicep:117`) is **manual,
  not Bicep**.

Candidates:
- **A. MSAL + client-directed flow.** MSAL (public client) gets an access token for the easyauth
  app, then `POST /.auth/login/aad {"access_token": …}` returns `authenticationToken`, sent as
  `X-ZUMO-AUTH`. This is documented for App Service and **unverified on Container Apps**. It needs:
  - a new public-client app registration with an msauth redirect URI;
  - an exposed scope on `agent42-easyauth`;
  - probably `allowedAudiences`.
- **B. WebView sign-in + session cookie.** Load `/.auth/login/aad` in a WebView and read the
  `AppServiceAuthSession` cookie from `CookieManager`. OkHttp then sends it. There's no infra change.
  - Cost: the cookie is FixedTime 14 days (ADR 0090), so there's an interactive WebView re-login at
    least every 14 days.
  - Risk: Entra or Conditional Access may block embedded-browser sign-in.
- **C. MSAL access token as `Authorization: Bearer`.** Send the token directly, with no
  `/.auth/login` exchange. This needs `allowedAudiences`. Its interaction with `RedirectToLoginPage`
  and `allowedPrincipals` is unmeasured. If it works, it's the simplest steady state, because MSAL
  refreshes silently.

**Rule 5 extended:** every Entra app-registration or role change is prepared as exact `az` commands
and **handed to the user to run**. The agent never runs them.

### Voice is the second unknown (Step 2 spike)
The value of the app depends on full-duplex barge-in on the speaker, measured on the user's
**Samsung, Android 14+** (user decision; `minSdk 34`).

Open questions to measure:
1. Is `SpeechRecognizer.isOnDeviceRecognitionAvailable()` true on that phone?
2. Can the recognizer be fed our own echo-cancelled PCM via `RecognizerIntent.EXTRA_AUDIO_SOURCE`
   (API 33)? This would come from an `AudioRecord(VOICE_COMMUNICATION)` with `AcousticEchoCanceler`.
3. Failing (2): with `MODE_IN_COMMUNICATION` set and TTS played as `USAGE_VOICE_COMMUNICATION`, does
   the recognizer's own capture stay echo-free enough to avoid self-triggering?

**Fallback if no full-duplex path works:** ADR 0079 behaviour, i.e. mic closed while speaking on the
speaker and barge-in only on a headset or Bluetooth. That's still worth shipping, because the screen
can be off, it survives app switching and car audio routes correctly.

**TTS fallback egress:** Samsung/Google `TextToSpeech` engines may use network voices. The ADR must
say what that fallback can send off-device, the same way ADR 0079 treats `speechSynthesis`. Prefer
an offline voice where the engine reports one.

**STT egress:** the user chose on-device first, with a seam for server STT later.
- On-device recognition has no egress.
- If only the cloud recognizer exists, that is the same egress Chrome's Web Speech already has
  (`wwwroot/js/speech.js:6-7`; ADR 0006). Record it in the ADR as such, not as a new ADR 0013
  extension.
- Server STT stays out of scope. It would be a new endpoint and an ADR 0013 extension.

### Other decisions made with the user
- **Distribution:** sideload only. CI builds a signed release APK published as a GitHub Release,
  like the Chrome extension. No Play Store. The signing key must stay stable or updates won't
  install, so the user generates the keystore and adds it as secrets.
- **Rich content is out of scope.** Mermaid, math, charts and artifacts render as code blocks, plus
  an "Open in web" action that opens the conversation in Chrome, which already holds the Easy Auth
  cookie. Plain markdown is rendered natively.
- **FCM push is deferred.** It's a new sender and possible content egress (ADR 0013, 0028).
- **No configuration screens** (providers, MCP, skills, agents, fleet). Those stay in the web app.

### Toolchain
- This machine has **no JDK, Gradle or Android SDK** (checked 2026-10-21).
- Hard rule 4 applies to Android too. AGP, Kotlin, the Compose BOM, Gradle, OkHttp, MSAL and
  androidx.core:core-telecom are **resolved by tooling, never written from memory**: the Android
  Studio new-project wizard, `gradle wrapper` against services.gradle.org's current version, or the
  Google Maven / Maven Central metadata. They're pinned in `gradle/libs.versions.toml`, which is this
  project's central pinning point, like `Directory.Packages.props`.

### Governing ADRs and gotchas
- **ADRs:** 0002, 0042 and 0090 (Easy Auth); 0006, 0037 (with its 0078 and 0079 amendments) and
  0038 (voice); 0046 (recovery); 0071 (reasoning); 0089 and 0093 (client tools); 0013 (egress); 0050
  (gates need a tool); 0109 (closest non-.NET client precedent).
- **Gotchas:** :2612 (turn outlives client), :4231 (client-tool wait), :4531 (no-referrer → Origin
  null → Easy Auth 403; relevant to WebView POSTs), :2016 (excluded paths). We add **no** excluded
  path.

## Steps

### Step 0 — Prerequisites (user, manual)
- Install Android Studio. It provides the JDK, SDK and `adb`.
- Enable developer mode and USB debugging on the Samsung phone.
- Generate a release keystore (`keytool`) and keep it outside the repo.

**Verify:** `adb devices` lists the phone. `java -version` works.

### Step 1 — Spike: native auth against prod Easy Auth (throwaway, outside the repo tree)
1. Try candidate B first. It's cheapest and needs no infra change. Using a minimal WebView app (or a
   desktop browser) plus curl, check that `AppServiceAuthSession` replayed from a non-browser client
   gets a 200 with JSON from `GET /api/conversations`.
2. Then try candidates C and A:
   - Prepare the `az ad app` commands (new public client, exposed scope, redirect URI) and **hand
     them to the user**.
   - **C:** measure `Authorization: Bearer <token>`.
   - **A:** measure `POST /.auth/login/aad` with an MSAL access token, and whether `X-ZUMO-AUTH`
     gets principal headers injected.
   - Check whether Bicep needs `allowedAudiences`, `allowedApplications` or `tokenStore`.
   - Measure token lifetime and refresh.
   - Testing A or C against prod needs the `allowedAudiences` change live. Prepare that as a
     **user-run `az containerapp auth update`** for the spike only; it gets pinned in Bicep later
     (Step 6).
3. Pick one using these criteria, in order: works on Container Apps; no embedded-browser block; no
   interactive re-login more often than every 14 days; least infra.

**Verify:** a recorded HTTP transcript for each candidate (status + body type) in the ADR.
**Redact** every token, cookie and session value before it touches a file.

### Step 2 — Spike: voice on the Samsung (throwaway app)
1. Measure questions 1–3 from Context.
2. Run a self-echo test: play 60 s of TTS on the speaker while recognizing, and count false
   transcripts.
3. Run a barge-in test: speak over playback, and measure time-to-detect.

**Verify:** the numbers are written into the ADR. Decide go/no-go for full duplex on the speaker.

### Step 3 — Record the decision (`/decide`)
One ADR, "Android companion app". Take the next free number at execution time: 0111 is absent from
`main` and may be held by an open branch, so check open PRs and `AdrNumberingTests`. It records:
- the repo location;
- Kotlin/Compose over MAUI;
- the auth path with the Step 1 evidence;
- the advertised client tools;
- the voice architecture with the Step 2 evidence. If full duplex works, it **amends ADR 0037/0079
  for the native client only**; the browser rule stands. Add a pointer in 0037.
- STT egress parity with ADR 0006, and the TTS-fallback egress;
- sideload distribution and versioning;
- deferred FCM, server STT and background location.

Add a CLAUDE.md feature-map row. This step owns it; Step 14 doesn't add it again. Docs scanned by
`check-doc-paths.mjs` get real paths only, never `.../`.

**Verify:** `dotnet test Agent42.slnx --filter AdrNumbering` passes.

### Step 4 — Scaffold project + CI
- Create `tools/Agent42.Android/` using the wizard: Empty Compose Activity, package `nl.agent42.android`,
  minSdk 34, version catalog. Add the Gradle wrapper.
- Add `.gitignore` entries: `.gradle/`, `build/`, `local.properties`, `*.jks`, `*.keystore`.
- Create `.github/workflows/android.yml`, mirroring `.github/workflows/release-chrome-extension.yml`
  **in full**:
  - **On PR (to main)**, path filter `tools/Agent42.Android/**` and the workflow file:
    `setup-java`, then `./gradlew test lint assembleDebug`. The path filter means this is not a
    required check, so a PR that touches the app shows it red or green, and that's the merge gate in
    practice. Don't add it to `pr-checks.yml`: that workflow has no path filter, and every .NET PR
    would pay the Gradle cost.
  - **Release, on push to main (same path filter), a tag `android-v*` or a manual run:**
    - Derive the version the way the exemplar does (`release-chrome-extension.yml:53-76`): from the
      tag, or the latest `android-v*` tag with the patch bumped on a main push.
    - Stamp `versionName` with it, and `versionCode` with a strictly increasing integer derived from
      it (e.g. major*10000+minor*100+patch). Android refuses an update whose `versionCode` doesn't
      increase.
    - Pass both as Gradle properties.
    - Run `assembleRelease`, signed from the secrets `ANDROID_KEYSTORE_BASE64`,
      `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` and `ANDROID_KEY_PASSWORD`. Fail clearly if
      they're absent.
    - Create a draft release, upload the APK, then publish (immutable-releases lesson).
- Add `tests/Agent42.Tests/AndroidReleaseTests.cs` pinning the triggers, path filter, version
  stamping and draft-then-publish, in the shape of `ChromeExtensionReleaseTests.cs`.
- Update CLAUDE.md: the Commands table gets an Android build/test row, and Manual prerequisites gets
  the signing secrets ("missing means no release APK; PR build still runs").

**Verify:**
- `./gradlew test assembleDebug` locally once Step 0 is done; until then CI only.
- The PR job passes on the branch.
- `dotnet test --filter AndroidRelease` passes.
- The signed release path can't run on a PR. It's verified by the first real `android-v0.1.0`
  release (Step 14), and recorded as such.

### Step 5 — Stream contract fixture (parallelizable with Step 6 after Step 4)
1. Extract `StreamEventJson` (`AgentApi.Conversations.cs:14`) to a public `ChatStreamJson.Options`
   and use it at `:223`. There's no behaviour change.
2. Add a .NET test `AndroidStreamContractTests` in `tests/Agent42.Tests/`.
   - It builds one fully populated sample `AgentStreamEvent` per `AgentStreamEventType`, with every
     field that type carries set.
   - It serializes them with `ChatStreamJson.Options`.
   - It asserts the result **equals** `tools/Agent42.Android/contract/stream-events.ndjson` line for
     line: a golden file.
   - On mismatch the failure message prints the expected file content, so updating it is a copy.
   - The test also asserts every enum value appears.

   A new event type, a renamed field or an added field then fails CI.
3. A Kotlin unit test parses the same fixture into the app's sealed event types. Unknown types are
   ignored, not fatal.

**Verify:** `dotnet test --filter AndroidStreamContract`; `./gradlew test`.

### Step 6 — API client + auth + settings
- Use OkHttp (resolved version) with no read timeout on the chat call. An advertised `confirm_action`
  waits up to 120 s, and turns run for minutes.
- Add an NDJSON line reader producing a `Flow<StreamEvent>`.
- Typed calls for:
  - conversations, messages and search;
  - models, the conversation's model and `copilotOnly` filtering;
  - attachments, speech, stop, tool-result and pending-tool-calls.
- Implement the auth path from Step 3:
  - A redirect or HTML response means signed out, which leads to re-auth.
  - Store credentials encrypted with an Android Keystore key. `EncryptedSharedPreferences`
    (androidx.security-crypto) is deprecated, so check the current recommendation rather than
    defaulting to it. With MSAL, MSAL's own token cache is enough.
- Settings: base URL, defaulting to `https://chat.agent42.nl`. For dev it can point at DevHost via
  `adb reverse tcp:61326 tcp:61326`; DevHost has no Easy Auth, so requests run as `local-dev`.
- If the auth path is A or C: apply the Bicep `authConfigs` changes in this step, plus a
  Bicep-content test in the same style as `EasyAuthExclusionTests`.
  - **Order:** the user runs the Entra app-registration commands **before** this merges, because
    `deploy.yml` triggers on `infra/**` and deploys on merge.
  - The Bicep must reproduce exactly what the Step 1 `az containerapp auth update` set, so the
    deploy is a no-op.

**Verify:**
- MockWebServer unit tests cover NDJSON chunk boundaries (a line split across reads), redirect →
  signed-out, 409 on speech, and 404 on tool-result.
- Manual: the app lists real conversations against DevHost.

### Step 7 — Chat UI
- **Conversation list:** pinned first, server order. Search uses `/api/search` and shows snippets.
- **Chat view:** history, plus live streaming of `textDelta`, `reasoning` (collapsed), tool chips
  (`toolCallStart`/`End`), `image`, `suggestions` and `todos`. Handle `textReset`.
- **Controls:** stop; model picker; "New chat" (client UUID); "Open in web" (find the web app's
  conversation deep-link route in `wwwroot/js`).
- **Native markdown:** a resolved-version Compose markdown library. Mermaid and math render as code
  blocks.
- **Recovery:** on resume or reconnect, re-fetch messages and pending tool calls (ADR 0046 parity).

**Verify:**
- Unit tests for the stream → UI-state reducer: delta append, textReset, done with `stopped`, and an
  unknown event.
- Manual against DevHost: send, stream, stop, switch model, and background mid-turn then return.

### Step 8 — Attachments + share intent
- Use the photo picker and the camera. Upload before send and pass `attachmentIds`.
- Show document uploads as "added to documents" (they have no id).
- Add an `ACTION_SEND` / `SEND_MULTIPLE` intent filter for text, images and files. It opens a new
  chat with the content prefilled, like the PWA share target.
- Enforce the 10 MB limit client-side.

**Verify:** unit tests for the upload-result mapping (image vs document) and the size guard. Manual:
share from Gallery and from Chrome.

### Step 9 — Client tools
- Advertise `get_current_location`:
  - Platform `LocationManager`, so no Play Services dependency.
  - The result shape matches the web client.
  - Runtime permission; denial → the same refusal result the web client sends.
- Advertise `confirm_action` only while the chat screen is foreground. It shows a native dialog and
  posts the result.
- Read `pending-tool-calls` on reconnect.

**Verify:** unit tests for the per-mode advertisement set (chat foreground vs voice) and the result
JSON shape against a fixture copied from the web client's output. Manual: "where am I?" on DevHost.

### Step 10 — Voice core (pure Kotlin state machine + Android adapters)
- **`VoiceLoop` state machine:** Idle → Listening → Sending → Speaking → (barge-in → Listening). No
  Android types, so it's unit-testable.
- **Interfaces:**
  - `SpeechInput` (on-device recognizer; the Step 2 audio-source variant; a seam for server STT);
  - `SpeechOutput` (server MP3 per sentence via `/api/speech`, falling back to `TextToSpeech` on 409);
  - `ChatTurnRunner`.
- **Behaviour:**
  - Split sentences like `voice.js:312` (the flush; buffer `speakBuf` at `:53`). Fetch and play them
  in order like `voice.js:244-296`.
  - Never speak reasoning.
  - Barge-in = cancel playback + `POST /stop`.
  - Full duplex on the speaker only if Step 2 said go. Otherwise mic closed while speaking unless
    `AudioManager.getCommunicationDevice()` is a headset or Bluetooth device. That's the native
    equivalent of the `voice.js` label regex, but from device type, not labels.
- **`VoiceCallService`:** a foreground service of type microphone. Audio focus;
  `MODE_IN_COMMUNICATION`; `setCommunicationDevice` for speaker or Bluetooth; playback with
  `USAGE_VOICE_COMMUNICATION` attributes so AEC has the reference.
- Voice turns advertise no client tools (see Context).

**Verify:** state-machine unit tests (barge-in during Speaking calls stop; textReset clears the
queue; 409 → fallback output; headset vs speaker policy). Manual on the Samsung: a screen-off 3-turn
conversation; barge-in on the speaker (if go); BT headset.

### Step 11 — Voice UI
- A full-screen call surface: big state indicator, transcript, mute, speaker/BT switch, hang up.
- An ongoing notification with hang-up.
- Pick or continue a conversation for voice.

**Verify:** manual checklist.

### Step 12 — Phase 2: Telecom call integration
- Use `androidx.core:core-telecom` `CallsManager` (resolved version) to register the voice session as
  a self-managed outgoing call to "Agent 42". This gives the system call UI and lock screen, car
  Bluetooth hang-up and answer, and correct pausing when a real call arrives.
- Move the foreground-service type to whatever core-telecom requires. Verify this against its docs,
  not memory.

**Verify:** unit test of the call-state → `VoiceLoop` mapping. Manual: start from the lock screen,
hang up from the car or BT button, take a real incoming call mid-turn.

### Step 13 — Phase 2: entry points
- A Quick Settings `TileService` and a home-screen widget (Glance) that start a voice call.
- Static app shortcuts: new chat, voice call.
- Assistant role: a minimal `VoiceInteractionService` + `VoiceInteractionSessionService` that hands
  off to the voice call, so the app is selectable as the default digital assistant (power-key long
  press).
  - Android 14 limits starting a microphone foreground service from the background. Every entry
    point must go through an activity or an allowed exemption. Measure this on the device.
  - If the assistant role proves impractical on Samsung, document the "side key → open app"
    alternative in the ADR and drop it.

**Verify:** manual on the Samsung: each entry point reaches a live voice call with the screen locked
and unlocked.

### Step 14 — Docs + gotchas + release
- `/sync-docs`: verify the CLAUDE.md rows from Steps 3 and 4; add gotchas for every device trap
  found in Steps 2, 10, 12 and 13, each with evidence.
- Bump nothing in `sw.js`; `wwwroot/js` isn't touched.
- Cut `android-v0.1.0` after merge, and install from the GitHub Release.

**Verify:** `node .agentic/scripts/check-doc-paths.mjs`; `dotnet test Agent42.slnx`;
`dotnet format Agent42.slnx --verify-no-changes`.

**Parallelizable:** 5 ∥ 6 after 4; 8 ∥ 9 after 7; 13 ∥ 11 after 10 (both are UI over the service).
The rest is sequential.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Proof | Location |
|---|---|---|---|
| 1 | The Android contract fixture equals the server's serialization of every event type, all fields populated | `AndroidStreamContractTests` (golden file) | `tests/Agent42.Tests/AndroidStreamContractTests.cs` |
| 1b | The release workflow stamps an increasing `versionCode` and does draft-then-publish | `AndroidReleaseTests` | `tests/Agent42.Tests/AndroidReleaseTests.cs` |
| 2 | The app parses every fixture line and ignores unknown types | `StreamEventParserTest` | `tools/Agent42.Android/app/src/test/.../StreamEventParserTest.kt` |
| 3 | The NDJSON reader handles lines split across reads | `NdjsonReaderTest` | `.../NdjsonReaderTest.kt` |
| 4 | An Easy Auth redirect or HTML response maps to signed-out | `AuthInterceptorTest` (MockWebServer) | `.../AuthInterceptorTest.kt` |
| 5 | The stream reducer: delta, textReset, stopped done | `ChatReducerTest` | `.../ChatReducerTest.kt` |
| 6 | Image upload → id in `attachmentIds`; document → no id; >10 MB refused | `AttachmentUploadTest` | `.../AttachmentUploadTest.kt` |
| 7 | Advertised client tools: chat foreground = {location, confirm}; voice = {} | `ClientToolAdvertisementTest` | `.../ClientToolAdvertisementTest.kt` |
| 8 | The location result JSON matches the web client's shape | `LocationResultTest` (fixture) | `.../LocationResultTest.kt` |
| 9 | Barge-in during Speaking cancels playback and POSTs stop | `VoiceLoopTest` | `.../VoiceLoopTest.kt` |
| 10 | Reasoning is never queued for speech; textReset clears the queue | `VoiceLoopTest` | `.../VoiceLoopTest.kt` |
| 11 | `/api/speech` 409 → fallback to the platform TTS | `SpeechOutputTest` | `.../SpeechOutputTest.kt` |
| 12 | Speaker vs headset duplex policy follows the Step 3 ADR | `DuplexPolicyTest` | `.../DuplexPolicyTest.kt` |
| 13 | Telecom call states drive the VoiceLoop (hold → pause, disconnect → stop) | `CallStateMappingTest` | `.../CallStateMappingTest.kt` |
| 14 | (If auth path A) Easy Auth audiences are pinned in Bicep | Bicep-content test like `EasyAuthExclusionTests` | `tests/Agent42.Tests/` |
| 15 | Screen-off voice, speaker barge-in (if go), BT, lock-screen call, every entry point | **Manual device checklist** on the Samsung, recorded in the PR | PR description |

## Quality gate checklist

From `.agentic/quality-gates.yaml` at f027feb:

- [ ] `branches.protected: [main]`. Work on `feature/android-companion-app`.
- [ ] `coverage_new_code_pct_min: 80`. Measured by coverlet plus
  `.agentic/scripts/check-new-code-coverage.mjs` on **.NET code only** (the contract test and any
  Bicep test). Kotlin code is **not measured by any repo tool**, so no Kotlin coverage claim is made
  (ADR 0050). Report the .NET number, and Kotlin as "not gated".
- [ ] `cognitive_complexity_max: 15` (S3776, C# build error). Applies to the C# tests only.
- [ ] `dependencies.vulnerable_packages: 0` (`dotnet list package --vulnerable`). .NET only.
- [ ] `dependencies.add_policy: latest-stable`. Android dependencies are also resolved by tooling and
  pinned in `libs.versions.toml`.
- [ ] `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`, the
  `check-doc-paths.mjs` script and the new `android.yml` (`./gradlew test lint assembleDebug`) are
  all green.

## Out of scope

- Server-side STT, and a speech-to-speech realtime model (phase 3, a separate ADR; ADR 0013 egress).
- FCM push and "Agent 42 calls you" (incoming calls).
- Rich rendering (mermaid, math, charts), the artifact pane, shared state and MCP elicitation UI.
- Edit/regenerate the last turn, fork, share links, export, groups management, incognito.
- All configuration UIs: providers, models defaults, MCP, skills, agents, fleet, usage, logs.
- Play Store distribution, iOS, Wear OS, Android Auto app templates.
- Wake word.
- Background location during voice mode.

## Spike results

### Step 2 — voice on the phone (2026-10-21)

**Setup:** Galaxy A54 (SM-A546B), Android 16 / SDK 36, One UI 8.5, locale nl-NL, phone speaker, no headset.
Throwaway app in the session scratchpad (`VoiceSpike.kt`), run once per mode by the user. Each run: 25 s silent while
the phone speaks a Dutch paragraph (any transcript is a self-echo trigger), then 20 s of the user talking over it.

**Capabilities:**
- The default recognizer is `com.google.android.tts/…GoogleTTSRecognitionService`. AEC and NS are available.
- On-device recognition exists, but **Dutch is not supported on-device**: nl-NL `supported=false`. Only en-US is
  supported, and it isn't downloaded. With the on-device recognizer every attempt failed with error 12 (language not
  supported).
- TTS: Google engine, 16 Dutch voices, 9 of them offline.

**Results** (default recognizer, nl-NL):

| Mode | Self-echo triggers while silent | Heard the user over playback |
|---|---|---|
| A: like the browser (USAGE_MEDIA, MODE_NORMAL) | **1**: transcribed its own first sentence verbatim | 1 |
| B: comm (MODE_IN_COMMUNICATION, speaker via `setCommunicationDevice`, TTS on USAGE_VOICE_COMMUNICATION) | **0** | **2** |
| C: AEC pipe (own `AudioRecord(VOICE_COMMUNICATION)` + AEC → `EXTRA_AUDIO_SOURCE`) | 0 | 0: the default recognizer never transcribed piped audio |

**Decision input for the ADR:**
- **Go for full duplex on the speaker with mode B.** Mode A reproduces ADR 0079's self-echo problem exactly; mode B
  removes it, and talking over still works.
- Drop the `EXTRA_AUDIO_SOURCE` path.
- STT runs on Google's network recognizer, because Dutch has no on-device model on this phone. That is the same egress
  as Chrome's Web Speech today (ADR 0006), so it is not a new ADR 0013 extension. Keep the on-device seam for languages
  that have a model.
- Limits:
  - One run per mode, one phone, one quiet room.
  - Server MP3 playback (`/api/speech`) was not tested. Step 10 must play it with USAGE_VOICE_COMMUNICATION attributes
    and re-check self-echo with it.
  - Error 11 (server disconnected) follows every result. It's harmless and the loop restarts, but step 10 must not
    surface it.

## Review

Critic pass on 2026-10-21. There were no ADR contradictions; 1 high, 4 medium and 3 low findings, all
accepted and folded in:
1. **(High)** Production NDJSON options are private, and the type-only fixture missed field drift.
   Fixed: extract `ChatStreamJson.Options`; the fixture is now an equality golden file.
2. **(Medium)** Wrong line cites (`ClientToolLimits`, `voice.js`). Corrected.
3. **(Medium)** No `versionCode` stamping, and the release trigger was ambiguous. Fixed: the
   exemplar's version derivation, a strictly increasing `versionCode`, and draft-then-publish.
4. **(Medium)** No workflow pinning test, and an unclear merge gate. Fixed: `AndroidReleaseTests`;
   the path-filtered PR job is the practical gate, deliberately not in `pr-checks.yml`.
5. **(Medium)** Location in voice mode with the screen off. Fixed: voice advertises no client tools;
   background location is out of scope.
6. **(Medium)** Missing auth candidate "Bearer token directly". Added as candidate C; the 14-day
   cookie re-login is now a stated cost of B.
7. **(Low)** Entra-vs-Bicep deploy order, token redaction in transcripts, deprecated
   `EncryptedSharedPreferences`. All three addressed in Steps 1 and 6.
8. **(Low)** The TTS-fallback egress was unrecorded. Now required in the ADR.
9. **(Low)** The feature-map row had two owners, and `.../` paths would fail in scanned docs. Step 3
   owns the row; real paths only.

The AC table keeps `app/src/test/.../X.kt` shorthand. Specs aren't scanned by `check-doc-paths.mjs`;
the executor writes real paths.
