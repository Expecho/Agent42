---
id: 0037
date: 2026-08-05
status: accepted
tags: [frontend, speech, tts, voice, barge-in, privacy, data-residency]
supersedes: []
extends: [0006]
---

# Hands-free voice conversation with barge-in (browser-native TTS + auto-listen loop)

## Context
Dictation (ADR 0006) gave the composer a *listen* half: tap `#micBtn`, speak, watch interim
text stream into `#promptInput`, review, and send by hand. The user now wants a genuine
hands-free **conversation** — speak → the assistant **speaks back** → **talk over it to
interrupt and it reacts immediately** → no buttons or typing between turns. That needs three
things ADR 0006 doesn't have: speech **output** (there was no `speechSynthesis` anywhere), an
**auto-send** loop (dictation never auto-sends), and **barge-in** (stop talking *and* stop
generating the instant the user speaks). The question is the same one ADR 0006 answered —
*where does the speech run* — because it carries a data-residency implication.

## Decision
Add a **frontend-only** hands-free mode, an extension of ADR 0006, wired entirely in
`src/Agent42.Backend/wwwroot/` — no endpoint, grain, Bicep, Azure resource, or JS dependency.
- **Engine: browser-native.** `SpeechRecognition` to listen (as ADR 0006) + `speechSynthesis`
  to speak. A **dedicated second `SpeechRecognition` instance** drives voice mode so the
  dictation instance's delicate accumulation logic (the Android zero-confidence workaround) is
  left untouched; the two mic uses are mutually exclusive (`startVoice()` calls `stopSpeech()`).
- **Entry point: a dedicated `#voiceBtn`** beside `#micBtn` (which stays plain dictation). One
  tap starts the loop — unavoidable, since browsers gate mic access behind a user gesture;
  everything after is voice. Tap again (or switch chats) to exit.
- **The loop** is a small controller in `app.js` with a state machine
  `idle → listening → thinking → speaking → listening`. Recognition runs continuously (kept
  alive by restarting on Chrome's silence auto-`end`); a **~1.2 s silence debounce** marks
  end-of-utterance and auto-sends. The reply is spoken **sentence-by-sentence as it streams**
  (fed from `send()`'s `textDelta` chunks, markdown-stripped) so speech starts before the reply
  finishes; the last utterance's `end` re-arms listening.
- **Barge-in** works because the mic stays **live while speaking**. A genuine result while
  `speaking`/`thinking` cancels TTS (`speechSynthesis.cancel()`) and **aborts generation
  mid-stream** via a new `AbortController` on `send()`'s `fetch` (`sendAbort`); an `AbortError`
  is treated as a clean interruption, not a transmission failure, and the interrupting utterance
  is captured as the next turn.

## Alternatives considered
- **Server-side realtime (OpenAI Realtime / Azure Speech).** Best voice quality, native echo
  cancellation and instant barge-in — but a large new WebSocket backend, reverses ADR 0006,
  adds per-minute cost, needs a new secret/model config, and sends audio outside the Entra
  tenant (ADR 0013 egress). Rejected for a personal single-user app; recorded here as the path
  to revisit if voice becomes central or the echo heuristic proves inadequate.
- **Reuse the one dictation `SpeechRecognition` instance.** Rejected: voice mode needs
  opposite result handling (auto-send + barge-in, no textarea writes) and would entangle the
  Android-specific dictation fixes; a second instance is cheaper than refactoring proven code.

## Consequences
- **Enables** a fully hands-free conversation with zero backend, infra, or cost, reusing the
  ADR-0005 `preferredLanguage` tag for both `recognition.lang` and the TTS voice, and keeps the
  no-build vanilla-JS rule intact.
- **Egress (ADR 0013) is unchanged.** TTS output is a *new but egress-free* path — the reply
  text is synthesized in the browser and never leaves it. Recognition still uses the browser
  vendor's cloud, exactly the residual ADR 0006 already accepted; this ADR adds no new one.
- **Accepted residual — the assistant can hear itself.** With the mic live during TTS, the
  recognizer may transcribe the spoken reply (self-echo) or the trailing final of the just-sent
  utterance, and misfire a barge-in. Mitigated by a best-effort heuristic — ignore a result
  whose words are mostly contained in what is being spoken or was just submitted
  (`isSelfOrEcho`, ≥ 0.6 overlap) — but **headphones remain the clean guarantee**. The heuristic
  is honest, not airtight; it is the record to revisit if it proves noisy.
- **Graceful degradation.** The button is hidden unless **both** `SpeechRecognition` and
  `speechSynthesis` exist (Firefox has neither usefully; iOS is limited), mirroring ADR 0006's
  hide-the-button path. Secure-context only — prod https and `localhost` qualify, so DevHost
  exercises it offline with a real mic.
- **No automated runtime test.** The speech APIs can't be driven headlessly, so wiring is
  guarded by C# source-text heuristics (`VoiceConversationAssetsTests`) and the functional
  behaviour (auto-listen, spoken reply, talk-over-to-interrupt, re-arm) is verified manually —
  consistent with ADR 0006 and the existing frontend-test convention, not a coverage gap.
