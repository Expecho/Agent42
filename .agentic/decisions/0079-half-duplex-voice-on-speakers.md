---
id: 0079
date: 2026-09-20
status: accepted
tags: [frontend, voice, speech, barge-in, half-duplex, audio-devices, heuristic, no-migration]
supersedes: []
extends: [0037]
amends: [0037, 0078]
---

# Close the microphone while the assistant speaks, unless the reply is going to a headset

> **Amended by [ADR 0114](0114-android-companion-app.md) (2026-10-21), for the native Android app only.** On Android
> the reply plays on the call audio path (`MODE_IN_COMMUNICATION`, `USAGE_VOICE_COMMUNICATION`), where the phone's
> echo canceller removes it. Measured on the owner's phone: 0 self-echo triggers and talk-over working on the
> speaker, against 1 self-echo for the browser-like setup. The app therefore keeps the mic live on the speaker. This
> ADR's rule still governs the browser.

## Context

ADR 0037 built the hands-free loop on a deliberate choice: **the mic stays live while the assistant
speaks**, because that is the only way talking over the reply can interrupt it. It recorded the cost as an
accepted residual — "the assistant can hear itself" — mitigated by `isSelfOrEcho`, a ≥ 0.6 word-overlap
heuristic, and closed with "headphones remain the clean guarantee… the record to revisit if it proves
noisy."

It proved noisy. On speakers the loop talks to itself: the recognizer transcribes the reply coming out of
the speakers, `isSelfOrEcho` lets through whatever the model phrased differently than expected, and the
assistant interrupts itself and answers its own words. The user asked for the mic to be muted while the
agent is responding, and then for that muting **not** to apply "when speak is over bluetooth" — on a
headset there is nothing to hear, so nothing needs fixing.

Two facts shape what is possible:

- **There is no "current audio output device" API.** `enumerateDevices()` lists the devices that *exist*;
  `HTMLMediaElement.sinkId` is `""` until someone calls `setSinkId`. The only signal that points at where
  sound is actually going is the `audiooutput` entry whose `deviceId` is `"default"`, and the only thing
  it carries is an **OS- and locale-worded label**, blank until the origin holds a media permission.
- **The property that matters is not Bluetooth.** A Bluetooth *speaker* echoes exactly like a laptop
  speaker; wired earbuds echo like neither. What decides the question is whether the microphone can hear
  the output — headset-ness — which happens to cover every Bluetooth headset and correctly excludes a
  Bluetooth soundbar.

## Decision

Voice mode becomes **half-duplex on speakers and stays full-duplex on a headset**, decided per turn.

- **The mic closes at auto-send**, not when speech starts (`if (!outputIsHeadset) muteMic()` in
  `trySubmit`). Both halves of the self-hearing live in that window: the **trailing final of the utterance
  just sent** arrives during `thinking`, and the **reply** arrives during `speaking`. Closing it when TTS
  begins would fix only the second.
- **`abort()`, not `stop()`.** `stop()` finalizes and can still deliver that trailing result — one of the
  two things being silenced. `abort()` discards it.
- **The keep-alive must not undo the mute.** Chrome ends continuous recognition on its own and ADR 0037's
  `end` handler restarts it; it is now gated on `!muted`, or the mic re-opens a second after closing and
  the whole feature is inert. The same handler is the **safety net** for `unmuteMic`'s `start()` throwing
  when the abort's `end` has not landed yet: that `end` still arrives, now sees `muted === false`, and
  starts the mic itself.
- **Three gestures replace talking over the reply**, all narrow — they do nothing unless a reply is
  actually running: **Esc**, the composer's **stop button** (ADR 0078), and **the voice button**, which
  while a reply runs means "never mind" and only exits voice mode on a second tap. The tooltip says so,
  and `setUi` swaps it for "Stop the reply and listen again (Esc)" while busy.
- **The stop button cuts the speaking, not the generation.** `send.js` already stops the server; voice
  adds `silenceAndRearm` (cancel speech + re-arm) and deliberately **not** `stopGeneration`, because two
  handlers calling it means two POSTs. `interrupt()` — the Esc and voice-button path — does both.
- **`outputIsHeadset` is a cached best-effort label test** (`/bluetooth|headset|headphone|earphone|earbud|
  \bbuds\b|airpod|hands.?free/i`) against **strictly the `"default"` device**, refreshed at voice start,
  on every `devicechange`, and after each auto-send for the next turn. Matching *any* listed output would
  keep the mic live whenever a headset is merely **connected** while sound still comes out of the
  speakers. When Chrome blanks every label, one silent `getUserMedia({audio:true})` — voice mode already
  holds the mic permission, so it does not prompt — unlocks them and the stream is stopped immediately.
- **Unknown means speakers.** No API, no labels, a throw, a device the word list does not know: the mic
  mutes. Muting a headset user costs one gesture per reply; not muting a speaker user costs the feature.
- **ADR 0037's spoken barge-in is kept whole** for the headset path — `bargeIn`, `isSelfOrEcho`,
  `lastSubmitted` and `spokenText` all stay, and the result handler still routes to them. This is a
  conditional mute, not a removal.
- **The re-arm now retries** (`maybeArmListening` chains one `rearmTimer`, cancelled by the next call).
  With the mic live, losing the "reply finished, nothing left speaking" race only mislabelled the state
  and the user could still talk; with the mic shut it leaves the loop **deaf**, so an empty reply, an
  error, or a stream that settles late must still recover on its own.

## Alternatives considered

- **Mute with no way to interrupt at all.** Rejected (user choice): the reply would always play to the
  end, and a wrong or long answer could not be cut short without leaving voice mode.
- **Keep the mic live and tighten `isSelfOrEcho`** (require a scored final, raise the overlap bar, ignore
  the first N ms). Rejected: it is the same best-effort shape that already failed, and it cannot reach
  zero — the recognizer and the model are both free to phrase things the heuristic does not line up.
- **A profile preference for muting.** Rejected (user choice): it would add a persisted `UserProfile`
  field, grain and Cosmos-JSON tests and profile UI, and keep both code paths alive forever, to express a
  choice the device can answer.
- **Test the label literally for "bluetooth".** Rejected on the physics: it misses AirPods and most
  Windows headset labels, which read "Headphones (…)" or "Headset (…)", and it wrongly keeps the mic live
  for a Bluetooth speaker, which echoes.
- **Correlate the default output's `groupId` with an `audioinput`** ("input and output are one physical
  device, therefore a headset"). Rejected: built-in laptop speakers and the built-in mic also share a
  group, so it answers "headset" for the single worst case.
- **`setSinkId` / `selectAudioOutput()` to know the device for certain.** Rejected: `selectAudioOutput()`
  needs a user gesture and shows a picker every time, and routing our own audio through a chosen sink
  still says nothing about where the *system* sends it.
- **Server-side realtime speech with native echo cancellation** (ADR 0037's own "path to revisit").
  Still rejected, and for ADR 0037's reasons — a new WebSocket backend, per-minute cost, and conversation
  audio leaving the tenant (ADR 0013). Recorded again: that is what buys real full-duplex on speakers.

## Consequences

- **On speakers the conversation is half-duplex.** You speak, it speaks, and you wait or press something.
  This is a real reduction of ADR 0037's headline feature, taken deliberately because a loop that
  interrupts itself is worse than one that takes turns.
- **ADR 0037's accepted residual is retired on the speaker path and unchanged on the headset path.** The
  self-echo it describes can no longer happen when the mic is shut; when a headset is detected, the
  heuristic and its honest limits are exactly as before.
- **A new failure mode replaces the old one: a stuck turn is now a deaf mic.** The retrying re-arm is the
  answer, and `interrupt()` is the manual floor under it — the buttons always hand the mic back.
- **`Escape` now has a fourth meaning.** It is guarded to `speaking`/`thinking` in an active voice
  session, so the drawer (ADR 0043's rail), search and usage handlers keep theirs; an Esc that both closes
  the drawer and shuts the assistant up is possible and accepted.
- **The voice button is overloaded** — interrupt while busy, toggle otherwise — so leaving voice mode
  mid-reply costs two taps.
- **Egress (ADR 0013) improves slightly.** While muted, the assistant's own spoken words are no longer
  streamed to the browser vendor's recognition cloud, which they were under ADR 0037 for the whole
  duration of every reply.
- **A detection heuristic is now load-bearing for the interaction model**, and it is worded by the user's
  operating system. `console.debug("[voice] audio output:", …)` is left in on purpose so the one fact a
  reader cannot guess — what their OS calls their device — is one keystroke away.
- **This amends ADR 0078**: the composer's stop button gains a second effect in voice mode (it silences
  playback and re-arms the mic), and ADR 0078's stop-then-abort ordering inside `stopGeneration` is
  untouched and still what makes `interrupt()` immediate.
- **No backend, endpoint, grain, package, secret or migration.** One module (`voice.js`), one tooltip in
  `index.html`, `sw.js` `CACHE` → `v40`.

## Verification

`dotnet test Agent42.slnx` → **1553 passed, 0 failed**, 1 skipped (the opt-in local-bridge live test);
`dotnet format --verify-no-changes` clean.

Six new source-text guards in `VoiceConversationAssetsTests`, each pinning a decision that a plausible
"simplification" would undo: the mute happens in `trySubmit` and not at speech start; `muteMic` uses
`abort()` and **not** `stop()`; the `end` keep-alive carries `!muted`; `refreshOutputKind` reads
`enumerateDevices`, filters `audiooutput`, takes **only** `deviceId === "default"`, and falls back to
`outputIsHeadset = false`; `bargeIn` still exists (the headset path is not deleted); the three gestures
are wired and `silenceAndRearm` does **not** call `stopGeneration`; `maybeArmListening` retries; and the
tooltip no longer promises "talk over it to interrupt".

## Unverified

**Everything at runtime.** This is the largest residual in this ADR and it is not small.

- **No browser was available in the session that wrote this** — the Claude-in-Chrome extension was not
  connected and the `browseros-neo` MCP server refused the connection — so nothing here was exercised
  against a real mic, a real speaker, a real headset, or a real `enumerateDevices()`. The guards above are
  source-text heuristics (ADR 0037's own convention, no JS runner by design): they prove the wiring
  shipped, not that it behaves.
- **The label word list has never seen a real label.** It was written from how Windows, macOS and Android
  are understood to word `audiooutput` labels, not from output measured on this machine. If voice mode
  mutes while a Bluetooth headset is connected, the `console.debug` line names the string that has to be
  matched. **First thing to check** before trusting any of this.
- **Whether `getUserMedia` actually unlocks the labels here** without a second permission prompt, and
  whether Chrome's SpeechRecognition permission is enough on its own.
- **Whether `vr.abort()` reliably fires `end`** on the engines in use, which is what the `unmuteMic`
  self-heal leans on. If it does not, a `start()` that throws leaves the mic shut until a gesture.
- **`devicechange` while a reply is playing** — plugging a headset in mid-turn changes the answer only for
  the *next* turn, by design, but the sequence was never run.
- **Android**, where `audiooutput` enumeration is poor to absent. The expected behaviour there is
  "always mute", which is the safe answer but removes barge-in on a phone with earbuds in.
