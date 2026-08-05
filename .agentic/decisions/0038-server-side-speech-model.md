---
id: 0038
date: 2026-08-05
status: accepted
tags: [frontend, backend, speech, tts, voice, providers, privacy, data-residency]
supersedes: []
amends: [0006, 0037]
extends: [0013, 0018, 0020, 0030]
---

# Server-side (Azure/OpenAI) text-to-speech for the voice reply, opt-in via a per-user speech model

## Context
The hands-free voice conversation (ADR 0037) speaks replies with the browser's `speechSynthesis`
— free, tenant-resident, but robotic. The user wants a natural voice: *when a speech model is set
in Preferences, use the Azure server side*. In Azure/OpenAI terms text-to-speech and speech-to-text
are separate models; the user scoped this to the **assistant voice (TTS) only** — they keep speaking
through the browser recognizer (ADR 0006/0037), unchanged. This re-introduces the server-side speech
path ADR 0006 deliberately rejected, but now as an **explicit, per-model opt-in** — the same shape
the per-user embedding model (ADR 0030) and image-generation model (ADR 0020) already established.

## Decision
Add a **speech (text-to-speech) model kind**, mirroring the embedding model (ADR 0030) almost exactly:
- **Capability flag `SupportsSpeech`** on a declared model (`StoredModel`/`ConfiguredModel`/`ModelInfo`
  + the provider-editor checkbox), surfaced on `/api/models`. It is used **only to filter the picker**
  — deliberately NOT added to `ModelCapabilities` (like embeddings), so it never touches per-turn chat
  resolution and sidesteps the Foundry `GetCapabilities → Full` shortcut.
- **A default speech model** on the overlay (`UserProviderConfig.SpeechDefault`, `SetSpeechDefaultAsync`,
  `PUT /api/default-speech-model`, read via `GET /api/providers`), validated once in the resolver
  (`EffectiveSpeechDefault` → `ResolvedProviderSet.SpeechDefaultModelId`) — an id resolves only when it
  names a declared speech-capable model; **no baseline/first-model fallback** (an opt-in pick).
- **A service seam** `ISpeechService.SynthesizeAsync(text) → (bytes, mediaType)`, resolved from the
  per-user factory (`IChatClientFactory.GetSpeechService()` / `DefaultSpeechModelId`, the
  `GetEmbeddingGenerator()` shape). Production `OpenAISpeechService` wraps the OpenAI SDK `AudioClient`
  (`GenerateSpeechAsync`, voice `Alloy`, MP3), built off the SAME provider client as chat — Foundry over
  its GA `/openai/v1` surface with the `api-key` header (NOT the classic `api-version` data plane the
  embedding client uses, which the stable Azure SDK caps at 2024-10-21, rejected by newer TTS
  deployments), OpenAI-compatible off its one `OpenAIClient`. Never a named `HttpClient` (the retry-pipeline
  trap); endpoint from config only (SSRF invariant).
- **Endpoint `POST /api/speech`** (text → audio) resolves the factory optionally via `IServiceProvider`
  (the `/api/config-health` pattern — avoids a new mandatory both-hosts registration that would break
  MapAgentApi routing) and **409s when no speech model is configured**, so the browser voice takes over.
- **Frontend:** the voice controller (ADR 0037) speaks each sentence via `/api/speech` and plays the
  returned audio through an ordered queue (fetches pipeline, playback stays sequential) when
  `userDefaultSpeechModel` is set; barge-in aborts in-flight speech fetches + stops playback alongside
  the existing generation-abort; **any `/api/speech` failure falls back to `speechSynthesis`** for that
  sentence. Offline (DevHost) can't reach a real TTS endpoint, so a default speech model isn't
  configurable there and the browser voice stays in use — same offline limitation as image gen (ADR 0020).

## Alternatives considered
- **Server-side STT too (full realtime pipeline).** Rejected for now (user scoped to TTS): browser
  recording + upload, a custom VAD for barge-in, and a much larger egress surface. The seam added here
  is TTS-shaped; STT would be a separate ADR.
- **Add `SupportsSpeech` to `ModelCapabilities` and route per-turn (image-gen shape).** Rejected: TTS
  is a single user default, not a per-turn chat mode — the embedding-model (default-id) shape fits, and
  it avoids the Foundry `Full` capability shortcut.
- **A named `HttpClient` / `IHttpClientFactory` transport for the audio client.** Rejected — it inherits
  `AddServiceDefaults`' retry pipeline (gotcha); the SDK's own transport is used, as for chat/image/embed.

## Consequences
- **Enables** a natural server-side voice for the hands-free conversation, per-user and opt-in, with no
  new Cosmos container, secret, or mandatory host registration — it rides the existing provider overlay
  (ADR 0018) and factory seam.
- **Headline consequence — egress (ADR 0013).** Picking a speech model sends the **reply text** to the
  model's endpoint, OUTSIDE the Entra tenant (and for a third-party provider, off Azure entirely). That
  is why it is opt-in and why the browser voice — which never leaves the browser (ADR 0037) — stays the
  default. This reverses ADR 0006's tenant-residency choice *only when the user asks for it*.
- **Foundry audio wire path is spike-informed, not live-verified.** The OpenAI 2.12.0 `AudioClient`
  surface is spike-confirmed; the Foundry `/openai/v1` audio exchange (and whether a given TTS deployment
  is reachable there vs the api-version data plane) is a prod-smoke gate, like the image path (ADR 0020).
  If a deployment rejects `/openai/v1` audio, the fallback is the `api-version` client — a one-line change.
- **Graceful degradation everywhere:** no speech model → 409 → browser voice; a failed synth call →
  browser voice for that sentence; offline → browser voice. The voice feature never breaks.
- **Test coverage:** the resolver validation (`EffectiveSpeechDefault`) and the endpoint (409 / bytes /
  400) are unit-tested; the live `AudioClient` exchange and the browser audio playback are out of unit
  scope (no reachable endpoint offline; speech APIs can't run headless — the ADR 0037 constraint), guarded
  by source-text asset tests and verified manually in prod.
