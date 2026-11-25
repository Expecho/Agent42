---
id: 0006
date: 2026-07-08
status: accepted
tags: [frontend, speech-input, dictation, privacy, data-residency]
amended_by: [0038]
---

# Use the browser-native Web Speech API for voice dictation, not server-side Azure transcription

## Context
The preferred-language profile (ADR 0005) was built to "seed speech input later"; this is
that consumer. The composer needs voice dictation: tap a mic button, speak, watch interim
text stream into the prompt box, review and send. The transcript must land in the existing
`#promptInput` textarea for manual review — it never auto-sends. Recognition language comes
from the saved `preferredLanguage` (a BCP-47 tag), falling back to `navigator.language`. The
question is *where transcription runs*, and every non-trivial option carries a data-residency
implication because raw audio has to reach a speech engine.

## Decision
Use the browser-native **Web Speech API** (`SpeechRecognition` / `webkitSpeechRecognition`),
wired entirely in `src/Agent42.Backend/wwwroot/js/speech.js` (ADR 0041 moved it there from `app.js`) — feature-detect and build one recognition instance
(`continuous`, `interimResults`), set `recognition.lang = profile.preferredLanguage ||
navigator.language` at start, render interim+final text live into the textarea, and stop
recognition when a send begins. The feature is **frontend-only**: no endpoint, grain, Bicep,
Azure resource, or JS dependency.

## Alternatives considered
- **Server-side Azure transcription (upload audio → a backend `/api` endpoint → Azure AI
  Speech).** Keeps audio inside the app's Azure tenant, but adds a backend endpoint, audio
  buffering/streaming plumbing, a new Azure resource + Bicep + cost, and IAM in the separate
  AI-Foundry RG. Heavy infrastructure for a personal single-user app; rejected.
- **Azure Speech SDK in the browser (`microsoft-cognitiveservices-speech-sdk`).** Would give
  tenant-controlled transcription client-side, but pulls in a bundled JS dependency —
  violating the deliberate no-build vanilla-JS rule (CLAUDE.md) — and still needs a
  token-issuing backend endpoint + the Speech resource. Rejected on the no-build rule and
  infra cost.

## Consequences
- **Enables** hands-free / mobile prompting with zero backend, infra, or cost, consuming the
  ADR-0005 `preferredLanguage` tag directly as `recognition.lang`, and keeps the no-build
  vanilla-JS rule intact.
- **Accepted downside — data residency.** In Chrome/Edge the captured audio is sent to the
  browser vendor's cloud (Google) for transcription, i.e. *outside* the app's Azure tenant —
  whereas every other data path in this app stays tenant-resident. That tenant-residency
  expectation was previously **de-facto and unwritten**: this ADR is the first document to
  state it. It is explicitly **not** governed by ADR 0002, which concerns Entra Easy Auth
  *gating* only, not where data is processed. Acceptable for a personal, single-user app;
  this ADR is the record to revisit if the app ever gains other users or handles
  third-party data.
- **Graceful degradation.** Firefox exposes no `SpeechRecognition`; the mic button is
  hidden there (not shown-disabled). Requires a secure context — prod is https and
  `localhost` qualifies, so DevHost exercises it offline with a real mic; no infra to test.
- **No automated runtime test.** The Web Speech API is a browser runtime the repo can't
  drive headlessly, so wiring is guarded by C# source-text heuristics
  (`SpeechInputAssetsTests`) and the functional behaviour is verified manually — consistent
  with the existing frontend-test convention, not a coverage gap.
