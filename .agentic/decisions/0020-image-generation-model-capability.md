---
id: 0020
date: 2026-07-17
status: accepted
tags: [models, capabilities, image-generation, attachments, per-user, security, data-egress]
amends: 0004 (assistant-generated attachments), 0013 (egress extends to image models + image bytes), 0018 (per-user providers gain image models)
---

# OpenAI-compatible image-generation model capability (text-to-image + image-to-image)

> **Amends [ADR 0004](0004-attachments-in-blob.md), [ADR 0013](0013-multi-provider-model-catalog.md),
> and [ADR 0018](0018-user-configurable-model-providers.md)** without superseding them: attachments
> gain an **assistant** side (0004 was user-uploads only), the capability catalog gains an
> **image-generation** bit (0013), and a per-user provider (0018) may now declare an image model.
> All three keep `status: accepted`.

## Context
The provider/capability catalog (ADR 0013/0017/0018/0019) and the blob attachment pipeline (ADR 0004)
are both in place and stable. The operator wants to generate images from the chat UI. The requirement
(from the interview) is that image generation is presented as a **picked model** from the catalog — a
new capability flag, **not** a tool the text model calls — and that when such a model is selected,
sending a prompt produces an image; if the turn has an attached image (or, failing that, the most
recent image already in the conversation), the model runs **image-to-image** (edit) against it.
Generated images persist in the conversation (reusing the `attachments` container) and re-appear on
reload, on the **assistant** side. A cross-conversation gallery, `n>1` results, size/quality pickers,
inpainting masks, and image generation on the Foundry/Responses hosted-tools path are out of scope.

## Step-0 spike (pinned OpenAI 2.12.0 + Microsoft.Agents.AI 1.13 — what it established)
A throwaway file-based app (`dotnet run`, repo-pinned versions) settled four facts that decided the
mechanism, mirroring how ADR 0013/0014 recorded their spikes:
1. **Text-to-image returns bytes.** `OpenAIClient.GetImageClient(model).GenerateImageAsync(prompt,
   new ImageGenerationOptions { ResponseFormat = GeneratedImageFormat.Bytes }, ct)` → a
   `GeneratedImage` with `BinaryData ImageBytes`.
2. **Edit surface.** `GenerateImageEditAsync(Stream image, string imageFilename, string prompt,
   ImageEditOptions options, ct)` — input is a **stream + filename** (media type inferred), the
   image-to-image path.
3. **No append-without-inference.** `AgentSession` (AF 1.13) exposes **no** public way to append a
   user+assistant pair without running inference — `TryGetInMemoryChatHistory` yields no mutable list
   on a fresh session. **→ the adapter mechanism is required** (not a manual append).
4. **`AdditionalProperties` round-trips.** An assistant `UriContent`'s **content-level** and a
   `ChatMessage`'s **message-level** `AdditionalProperties` both survive
   `SerializeSessionAsync`/`DeserializeSessionAsync`, **and** the message-level id survives an
   image-strip + round-trip. **→ the assistant image is linked to its durable ref by an id, not a
   position.**

## Decision
**Image generation is a new per-model capability bit (`ModelCapabilities.SupportsImageGeneration`),
declared per OpenAI-compatible model, enforced in the grain — an assistant-side mirror of the existing
user-image attachment plumbing.**

- **Capability + factory.** The bit rides the same path as tools/vision: `ConfiguredModel` /
  `StoredModel` / `ModelInfo` / `ProviderModel*` carry it, `GetCapabilities` reports it (Foundry and
  the no-provider path → `false`; `ModelCapabilities.Full` stays chat-only). A new
  `IChatClientFactory.GetImageGenerationService(modelId)` returns a generator **only** for such a
  model, built the **same transport way** as `Provider.OpenAICompatible` (the SDK's own `OpenAIClient`,
  no `IHttpClientFactory` — dodging the `AddServiceDefaults` retry pipeline on a non-reentrant grain).
- **Generation abstraction.** A minimal in-house `IImageGenerationService` (prod
  `OpenAIImageGenerator` over the SDK `ImageClient`; offline `InMemoryImageGenerator` returns a canned
  PNG) — mirroring `IAttachmentStore`'s real+in-memory split. It is **named `IImageGenerationService`,
  not `IImageGenerator`**, because M.E.AI 1.13 ships its own `Microsoft.Extensions.AI.IImageGenerator`
  and a same-named type would be an ambiguous reference in every Backend file importing both namespaces
  (the spike hit exactly that on `ImageGenerationOptions`).
- **Grain path (adapter mechanism, per spike #3).** When the resolved model reports
  `SupportsImageGeneration`, `ChatCoreAsync` branches **before** the chat vision-gate (an image model
  *consumes* images, so the withhold-from-a-no-vision-model gate must not fire): resolve the input
  image (this turn's attachment → most recent conversation image → none), `GenerateAsync`, store the
  **bytes** in the `attachments` blob container (never the session — ADR 0004), and record a
  user+assistant turn by running a one-shot `ImageGenerationChatClient : IChatClient` through
  `RunAsync` (the snapshot→rebuild-agent-on-adapter→restore dance, as a model switch).
- **Id-keyed assistant images (per spike #4).** Generated images live **id-keyed** in
  `AgentGrainState.GeneratedImages` (`Dictionary<string, AttachmentRef>`), and the assistant message
  carries `assistantImageId` in its **message-level `AdditionalProperties`**. History, the
  most-recent-input walk, and the vision-gate strip/restore all look the ref up **by id** — never by an
  assistant-message position (see the C1 rejected alternative). The vision gate applies to assistant
  images too: a generated image is a private-container SAS, withheld from a no-vision chat model and
  restored on switch-back, reversibly (the message-level id survives the strip).
- **Streaming + UI.** Image gen is not token-streamed: one `AgentStreamEvent.Image` (attachment id +
  media type), then `Done`. The frontend renders the assistant image through the existing
  attachment proxy (by id) — live and on reload — and the providers UI gains an "image gen" per-model
  checkbox alongside tools/vision.

## Alternatives considered
- **C1 — a positional per-assistant-turn list of generated images.** **Rejected** (critic Critical).
  Assistant messages are **not** 1:1 with turns: a tool-calling turn emits a standalone
  `FunctionCallContent` message plus a separate text message, so any list keyed on assistant-message
  index drifts by one for every prior tool turn, misaligning the generated image against the wrong
  bubble **and** the vision-gate strip/restore. Replaced by the id-keyed `GeneratedImages` +
  message-level `assistantImageId` (spike #4 proved it survives serialization and the strip). AC9
  interleaves a tool turn **before** the image turn precisely so a positional bug would surface.
- **Image generation as a tool the text model calls.** Rejected by the interview: the requirement is
  a picked model, which also keeps the egress decision explicit (below).
- **Manual append into the session (no adapter).** Rejected — spike #3 proved AF 1.13 has no
  append-without-inference; the adapter is the only way to record the turn so it serializes naturally.
- **Adopt `Microsoft.Extensions.AI.IImageGenerator`.** Rejected: heavier request/response surface than
  needed; the in-house abstraction mirrors the codebase's `IAttachmentStore` real+in-memory pattern.
  (Its existence is why ours is named `IImageGenerationService`.)
- **Feed a generated image to a later vision chat model automatically.** Out of scope — the user
  re-attaches it if wanted; so the vision gate only needs to *withhold* the SAS from a no-vision model,
  not guarantee delivery to a vision one.

## Consequences
- **Data egress (extends ADR 0013 to image models and image bytes).** Generating an image sends the
  **prompt** — and for an edit, the **input image bytes** (an actual uploaded/earlier photo) — to a
  user-configured endpoint **outside the Entra tenant**. This is the headline consequence, and the
  reason it is a deliberate per-model, per-user opt-in (ADR 0018), never a baseline.
- **Assistant attachments (extends ADR 0004).** The `attachments` container now holds assistant-side
  images too, under the same owner key `{userId}/{conversationId}` — **no new container, no Bicep/RBAC
  change** (the app MI already holds Storage Blob Data Contributor). Bytes stay out of the 2 MB session
  (AC8 pins it).
- **Offline is display-only.** The real `/v1/images` contract is spike-verified, not unit-tested —
  `InMemoryImageGenerator` (a canned PNG) exercises the store/display/history flow in DevHost and the
  grain tests; the prod `OpenAIImageGenerator` is prod smoke, honestly out of unit scope (same
  limitation ADR 0004 documents for images).
- **A tools-capable declaration is not required.** A real image model declares no tools; the
  capability is orthogonal to the image path (which never registers tools). AC9's test image model is
  declared tools-capable only to keep an interleaved tool turn from being stripped, so the id-keyed
  strip/restore is tested against a genuinely non-1:1 assistant structure.
