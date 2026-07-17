# Plan — OpenAI-compatible image-generation model (text-to-image + image-to-image)

`planned-against: main @ 49d0dfb`

## Overview

Add a new **model capability** — image generation — so a user can add an OpenAI-compatible
text-to-image / image-to-image model as a per-user provider (ADR 0018) and pick it from the
model picker. When such a model is selected, sending a prompt produces an image instead of a
chat reply; if the turn has an attached image (or, failing that, the conversation already
contains an image), the model runs **image-to-image** (edit) against it. Generated images are
stored in the existing private `attachments` blob container and shown in the chat — both live
and on reload — exactly like user-uploaded images, but on the **assistant** side.

Why now: the provider/capability catalog (ADR 0013/0017/0018/0019) and the blob attachment
pipeline (ADR 0004) are both in place and stable; image generation slots onto them as one more
per-model capability flag plus an assistant-side mirror of the existing attachment plumbing.

## Interview decisions (from the user)

1. **Invocation = a dedicated image model** picked from the catalog (a new capability flag),
   *not* a tool the text model calls. Presenting as a picked model is the requirement; the
   internal mechanism (adapter vs. grain branch) is Step 0's to settle.
2. **Image-to-image input = attached-image-if-present, else the most recent image** already in
   the conversation (user-uploaded or previously generated).
3. **Retrieval = persist in the conversation** — reuse the existing `attachments` container and
   the `/api/conversations/{id}/attachments/{id}` proxy; images re-appear on reload. **No**
   cross-conversation gallery (explicitly out of scope).

## Affected areas (verified against the tree at 49d0dfb)

- `src/Agent42.Agents/IChatClientFactory.cs` — `ModelCapabilities` record; `IChatClientFactory`
  interface; `SingleChatClientFactory` (offline/test factory).
- `src/Agent42.Backend/ChatClientFactory.cs` — `MultiProviderChatClientFactory.GetCapabilities`;
  `Provider.OpenAICompatible` (SDK client construction).
- `src/Agent42.Backend/ModelProviderOptions.cs` — `ConfiguredModel`.
- `src/Agent42.Agents/UserProviderConfig.cs` — `StoredModel` (per-user overlay persistence).
- `src/Agent42.Backend/ModelCatalog.cs` — `ModelInfo`, `AzureModelCatalog`, `CompositeModelCatalog`.
- `src/Agent42.Backend/AgentApi.cs` — `/api/models` mapping; `ValidateProvider`; providers CRUD mapping.
- `src/Agent42.Agents/AgentGrain.cs` — `ChatCoreAsync`, `BuildAgent`, `GetHistoryAsync`,
  `RehydrateAttachmentUrlsAsync`, activation/`_modelId` handling.
- `src/Agent42.Agents/AgentGrainState.cs` — new assistant-attachment ref list.
- `src/Agent42.Agents/AgentStreamEvent.cs` — new stream-event shape for a generated image.
- `src/Agent42.Agents/ChatTurn.cs` — already carries `Attachments`; reused for assistant turns.
- **New:** `src/Agent42.Agents/IImageGenerator.cs` (interface + offline in-memory impl),
  `src/Agent42.Backend/OpenAIImageGenerator.cs` (prod impl over the OpenAI SDK `ImageClient`).
- Frontend: `src/Agent42.Backend/wwwroot/app.js`, `index.html`, `styles.css` — assistant-bubble
  image rendering, the live image stream event, and the providers-UI capability checkbox.
- DevHost wiring: `tools/Agent42.DevHost/` — register the offline image generator.
- Tests: `tests/Agent42.Tests/AgentGrainTests.cs`, `ModelProviderTests.cs`, `ModelCatalogTests.cs`,
  `ProvidersApiTests.cs`, plus a new asset test for the frontend render path.
- `.agentic/decisions/0020-*.md` (new ADR), `.agentic/gotchas.md`, `CLAUDE.md`.

## Context & constraints (what the executor can't cheaply re-derive)

- **The whole chat path is chat-completions only.** Every turn runs through
  `AgentGrain` wrapping an `IChatClient` and an Agent Framework `AgentSession`
  (`AgentGrain.cs:587` `RunStreamingAsync`). The image APIs (`/v1/images/generations`,
  `/v1/images/edits`) are a *different* surface. **Messages are appended to `AgentSession`
  only through an agent run** — there is no public "add message" — which is the single fact
  that decides Step 0 (adapter vs. branch). Evidence: `GetHistoryAsync` reads history back via
  `_session.TryGetInMemoryChatHistory` (`AgentGrain.cs:634`), and nothing in the grain mutates
  the session except `RunStreamingAsync` / `DeserializeSessionAsync`.
- **Bytes must never enter the serialized session (2 MB Cosmos cap, ADR 0004).** The session is
  re-serialized to one `agent-state` item every turn (`AgentGrain.cs:616-618`). Generated image
  bytes go to blob storage; the session may hold only a short `UriContent` SAS **reference**,
  refreshed per turn by `RehydrateAttachmentUrlsAsync` (`AgentGrain.cs:1184`). This is the
  symmetric twin of how user images already work — copy that shape, do not inline
  `DataContent`. See the "image attachment bytes must stay out of the session" gotcha.
- **User images are: `UriContent` in the session + a durable `AttachmentRef` in
  `AgentGrainState.TurnAttachments`, joined to turns positionally by index**
  (`AgentGrain.cs:566-567`, `647-661`; `RehydrateAttachmentUrlsAsync` at `1199-1202`). This join
  works **only because there is exactly one `ChatRole.User` message per turn**.
- **The assistant side is NOT 1:1 with turns — do NOT mirror the positional join.** A
  tool-calling turn emits a *standalone* assistant message carrying `FunctionCallContent`
  (`AgentGrain.cs:597`) and then a *separate* assistant text message — confirmed even offline in
  `tests/Agent42.Tests/FakeChatClient.cs:49`. Any list keyed by assistant-**message index** drifts
  by one for every prior tool turn (web search / reminders / location are all wired in), misaligning
  the generated-image ref against the wrong bubble **and** misaligning the vision-gate strip/restore
  (leaking or losing a private-container SAS). **Assistant generated images are therefore stored
  ID-KEYED, never positionally** — a durable map/list of `AttachmentRef` looked up by a stable
  attachment id, with strip/restore/history done by id. See the positional-join gotcha
  (`gotchas.md`, "Withholding images from a no-vision model must cover HISTORY") for why the fragile
  join must not be extended to a side where it is provably not 1:1.
- **The vision gate strips image `UriContent` from history for a no-vision chat model and is
  reversible via the durable ref record** (`RehydrateAttachmentUrlsAsync(includeImages)`,
  `StripImageParts`, `AgentGrain.cs:1204-1242`). An assistant-generated image is *also* a SAS
  URL into the private container, so it MUST ride the same gate: withheld from a no-vision
  third-party chat model, restored when switching back. Reuse `AssistantTurnAttachments` as its
  durable record. Gotcha: "Withholding images from a no-vision model must cover HISTORY."
- **Capabilities are declared in config and enforced in the GRAIN, not the UI** (ADR 0013;
  `GetCapabilities` at `ChatClientFactory.cs:62`). An unknown/undeclared model returns
  *no* capabilities — guessing `true` is the unsafe direction. Image generation is a new bit on
  the same record; default `false`; discovered Foundry deployments are chat, so `false`.
- **OpenAI-compatible providers deliberately use the SDK's own transport, not an
  `IHttpClientFactory` client** (`Provider.OpenACompatible`, `ChatClientFactory.cs:198-203`),
  to dodge `AddServiceDefaults`' 3-retry pipeline on a non-reentrant grain. The image client
  MUST be built the same way (`OpenAIClient(...).GetImageClient(model)`). See the "named
  HttpClient inherits the retry pipeline" gotcha.
- **Per-user providers, no inheritance, best-effort overlay** (ADR 0018/0019): an image model is
  a user-added `OpenAICompatible` provider. It does *not* inherit `AzureOpenAI:*`. The endpoint is
  user config, so it is deliberately **not** SSRF-guarded and may be `localhost` — do not add a
  guard. See the "user provider overlay is best-effort … endpoint NOT SSRF-guarded" gotcha.
- **Data egress (ADR 0013 headline).** Generating an image sends the **prompt** — and for edits,
  the **input image bytes** — to a user-configured endpoint outside the Entra tenant. For edits
  this ships an actual uploaded/earlier photo to a third party. The new ADR must record this
  (it extends ADR 0013's egress position to image models and image bytes).
- **The `attachments` container and its RBAC already exist** (`infra/modules/storage.bicep`,
  `storage-role-assignment.bicep`; `IAttachmentStore.SaveAsync` → `AttachmentRef`). Generated
  images reuse them under the same owner key `{userId}/{conversationId}` — **no Bicep change,
  no new container** (confirm during Step 8). The write already works: the app MI holds Storage
  Blob Data Contributor.
- **Offline can't reach a real image endpoint** (same limitation ADR 0004 documents for images).
  The in-memory `IImageGenerator` returns a canned placeholder PNG so the DevHost + grain tests
  exercise the store/display/history flow; the real endpoint contract is spike-verified (Step 0),
  not unit-tested — honestly out of unit scope.

## Governing ADRs / gotchas to read before touching each area

- ADR 0004 (attachments in blob) · ADR 0013 (capability catalog + egress) · ADR 0017 (key auth)
  · ADR 0018 (per-user providers) · ADR 0019 (empty baseline / no-op client).
- Gotchas: "image attachment bytes must stay out of the session"; "Withholding images from a
  no-vision model must cover HISTORY"; "a named HttpClient inherits the retry pipeline"; "a user
  provider overlay is best-effort … endpoint NOT SSRF-guarded"; "`AgentSkillsProvider` is a TOOL";
  "an agent tool's RETURN value is persisted" (byte-budget discipline).

## Steps

### Step 0 — Spike the external + framework contract (BLOCKING; do first)
Use `/spike-dotnet` (throwaway file outside the tree, pinned to `OpenAI 2.12.0`). Settle:
1. `OpenAIClient(ApiKeyCredential, OpenAIClientOptions{Endpoint}).GetImageClient(model)` exists and
   the **text-to-image** call returns image **bytes** (`ResponseFormat = Bytes` / `b64_json`), plus
   the option names for `size` and `n`.
2. The **image-to-image / edit** call surface (`GenerateImageEdit…` — input image stream + prompt),
   and what it wants for the input (stream + filename + media type).
3. Whether the Agent Framework `AgentSession` (Microsoft.Agents.AI 1.13) exposes any way to append
   a user+assistant message pair **without** invoking inference. **This decides Step 4's mechanism:**
   - If yes → a dedicated grain branch calls `IImageGenerator` directly and records the turn.
   - If no → an `ImageGenerationChatClient : IChatClient` adapter, run through `RunStreamingAsync`,
     is required so the session records the turn naturally. **Assume the adapter** unless the spike
     proves manual append works. Note the adapter is not a one-liner: `_agent`/`GetClient` wraps the
     model's *chat* client (`ChatClientFactory.cs:200-202`), so recording into the existing history
     means the same snapshot → rebuild-agent-on-adapter → restore-session dance as the model-switch
     at `AgentGrain.cs:474-478`. Step 4 must carry that.
4. **How to carry a stable attachment id from the assistant image back to its durable ref** (the
   C1 fix — no positional join). Determine whether `AIContent.AdditionalProperties` (e.g. an
   `attachmentId`) **survives `SerializeSessionAsync`/`DeserializeSessionAsync`**. If yes → the
   assistant `UriContent` carries its id and rehydrate/history/strip look the ref up by id. If no →
   fallback: store generated refs **only** in grain state (id-keyed) and have `GetHistoryAsync` merge
   them, with the session assistant message carrying no image part. Either way the record is **id-keyed,
   never positional**.
- **Verify:** the spike file runs (`dotnet run image-spike.cs`) and prints the confirmed surface
  (image bytes + edit shape + append capability + AdditionalProperties round-trip).
  Record the findings in the ADR (Step 11), mirroring ADR 0013/0014's "what the spike established".

### Step 1 — Capability flag through the model/catalog types  *(parallelizable with Step 2)*
- Add `bool SupportsImageGeneration = false` to `ModelCapabilities`
  (`IChatClientFactory.cs`); keep `ModelCapabilities.Full` chat-only (image gen stays opt-in).
- Add `SupportsImageGeneration` to `ConfiguredModel` (`ModelProviderOptions.cs`), `StoredModel`
  (`UserProviderConfig.cs` — new trailing `[Id(n)]`, default `false`, so no overlay migration),
  and `ModelInfo` (`ModelCatalog.cs`). Map it through `CompositeModelCatalog`/declared-model
  catalog; discovered Foundry deployments → `false` (`AzureModelCatalog`).
- `MultiProviderChatClientFactory.GetCapabilities` (`ChatClientFactory.cs:87-92`): carry
  `configured.SupportsImageGeneration` for OpenAI-compatible declared models; Foundry branch and
  the no-provider branch → `false`.
- **Verify:** `dotnet test --filter ModelProviderTests|ModelCatalogTests` — a provider declaring an
  image model surfaces the flag in capabilities and in the `/api/models` catalog.

### Step 2 — `IImageGenerator` abstraction + offline impl  *(parallelizable with Step 1)*
- New `src/Agent42.Agents/IImageGenerator.cs`: `Task<GeneratedImage> GenerateAsync(string prompt,
  ImageInput? input, CancellationToken ct)` where `GeneratedImage(byte[] Bytes, string MediaType)`
  and `ImageInput(byte[] Bytes, string MediaType)` (null input ⇒ text-to-image, non-null ⇒ edit).
- New in-memory impl in the same file (or `InMemoryImageGenerator.cs`) returning a small canned PNG
  — the offline/test stand-in (mirrors `InMemoryAttachmentStore`).
- **Verify:** compiles; a direct unit test of the in-memory generator returns non-empty bytes.

### Step 3 — Prod image generator + factory access
- New `src/Agent42.Backend/OpenAIImageGenerator.cs` wrapping the SDK `ImageClient` from Step 0,
  built the **same transport way** as `Provider.OpenAICompatible` (no `IHttpClientFactory`).
- Expose it from the per-user factory: add `IImageGenerator? GetImageGenerator(string? modelId)` to
  `IChatClientFactory` — returns non-null **only** when the resolved model's
  `SupportsImageGeneration` is true; else `null`. Implement in `MultiProviderChatClientFactory`
  (build from the provider's `OpenAIClient`/endpoint+key), in `SingleChatClientFactory` (optional
  injected generator, `null` by default), and in the empty/no-provider path (`null`).
- **Verify:** `dotnet build`; a factory test — an image model yields a generator, a chat model
  yields `null`.

### Step 4 — Grain integration (mechanism per Step 0)
- `AgentGrainState.cs`: add an **id-keyed** durable record of generated images —
  `Dictionary<string, AttachmentRef> GeneratedImages = []` (new trailing `[Id(9)]`; `AgentGrainState`
  currently ends at `[Id(8)]`). **Not** a positional per-assistant-turn list (see C1 in Context) — the
  key is the `AttachmentRef.Id`, and there is no alignment/back-fill to maintain.
- In `ChatCoreAsync` (`AgentGrain.cs:434`), **after** model resolve/rebind and **before** the chat
  vision-gate block, branch when `_chatClientFactory.GetCapabilities(_modelId).SupportsImageGeneration`:
  1. Resolve the input image: if this turn's `attachments` has an image → its bytes
     (`_attachments.OpenReadAsync` on the owner key); else `FindMostRecentImageRef()` (walk the
     session in order, mapping user turns via `TurnAttachments` and assistant `UriContent` via
     `GeneratedImages`, take the last image ref) → bytes; else null (text-to-image).
  2. Call `_chatClientFactory.GetImageGenerator(_modelId)!.GenerateAsync(message, input, ct)`.
  3. `_attachments.SaveAsync(ownerKey, fileName, mediaType, bytes, ct)` → assistant `AttachmentRef`
     (note the interface order: `IAttachmentStore.SaveAsync(ownerKey, fileName, mediaType, content, …)`,
     `IAttachmentStore.cs:22-24` — `fileName` before `content`). Store `GeneratedImages[ref.Id] = ref`.
  4. Record the turn so history renders it and the SAS stays fresh — per Step 0: the **adapter** path
     returns an assistant message whose `UriContent` carries `ref.Id` (via `AdditionalProperties` if
     the spike confirmed it survives), run via the snapshot→rebuild-on-adapter→restore dance
     (`AgentGrain.cs:474-478`); the **fallback** path records the assistant image in `GeneratedImages`
     only and lets `GetHistoryAsync` merge it. Either way the link is **by id**, never by position.
  5. Persist state; emit the new image stream event (Step 5) then `Done`.
- Guard the byte budget: bytes go to blob, **never** into the session (assert in Step 9's session-size
  test). Do not run the no-op `NoModelChatClient` on this path.
- **Verify:** `dotnet test --filter AgentGrainTests` for the three cases (text-to-image;
  image-to-image with attachment; image-to-image with most-recent) using the in-memory generator.

### Step 5 — Stream event + history for assistant images
- `AgentStreamEvent.cs`: add `AgentStreamEventType.Image` and an `AttachmentId` (+ media type) field,
  so the live UI can render the generated image immediately (image gen is not token-streamed — one
  event, then `Done`).
- `GetHistoryAsync` (`AgentGrain.cs:632`): emit assistant turns with their generated-image refs,
  looked up in `GeneratedImages` by the id carried on the assistant `UriContent` (adapter path) or
  merged from state (fallback path). **Relax the assistant filter at `AgentGrain.cs:663`**, which
  today drops any blank-text assistant message and passes **no** attachments (`new ChatTurn(role,
  message.Text)` at `:665`) — a generated-image turn is typically blank-text, so without this it
  vanishes on reload (breaks AC7). Mirror the user-branch fix at `:653-658` ("retain an image-only
  turn") and pass the refs into `ChatTurn.Attachments` (which already exists).
- `RehydrateAttachmentUrlsAsync` (`AgentGrain.cs:1184`): extend to refresh **assistant** `UriContent`
  by looking its ref up in `GeneratedImages` by id, and apply the same vision-gate strip/restore to
  them by id (a generated image is a private-container SAS too — it must be withheld from a no-vision
  chat model and restored on switch-back). If Step 0 chose the fallback (no assistant `UriContent` in
  the session), this reduces to keeping `GeneratedImages` fresh for display only.
- **Verify:** `dotnet test --filter AgentGrainTests` — history round-trips an assistant image ref;
  a no-vision chat model switch strips it from the model's view but a switch back restores it.

### Step 6 — Providers CRUD + validation
- `AgentApi.cs` providers mapping + `ValidateProvider`: accept and persist `SupportsImageGeneration`
  per declared model (map to/from `StoredModel`). The existing "≥1 model" rule (ADR 0019) already
  covers an image-only provider — confirm an image model counts as a model.
- **Verify:** `dotnet test --filter ProvidersApiTests` — a provider with an image-capable model is
  accepted and round-trips the flag; the key stays masked (`hasKey`).

### Step 7 — Frontend  *(parallelizable with Step 6)*
- `app.js`: render assistant-bubble images (mirror the user gallery at `app.js:498-525`, via
  `attachmentUrl(convId, att.id)` — the existing authenticated proxy); handle the new `Image`
  stream event live; add the per-model **"image generation"** capability checkbox in the
  Preferences → model providers form (alongside tools/vision).
- Optional: a small composer hint when an image model is selected. Keep the existing upload composer
  as the image-to-image input path (no new upload UI).
- `index.html` / `styles.css`: any markup/class needed for the assistant image gallery.
- **Verify:** manual `/run` in DevHost (offline generator) — select the image model, send a prompt,
  see the placeholder image live and on reload; attach an image and see the edit path invoked.
  Add a C# **asset test** (source-text heuristic, like `LocationAssetsTests`) pinning that the
  assistant-render path references the attachment proxy and the `Image` event is handled.

### Step 8 — Storage confirmation (no infra change expected)
- Confirm generated images reuse the `attachments` container + owner key with **no** Bicep edit
  (`IAttachmentStore.SaveAsync` already writes there; the MI already has the grant). If a size or
  content-type concern surfaces, note it — do not add a container.
- **Verify:** DevHost stores + serves a generated image via the proxy; review confirms no Bicep delta.

### Step 9 — Session-size + egress guard tests
- `AgentGrainTests`: an image-gen turn keeps the serialized session small (bytes not inlined) —
  mirror `ToolResults_DoNotBlowTheSessionSizeBudget`.
- Assert the input-image edit path reads bytes from the store (owner-scoped), and that a
  text-to-image turn sends no input image.
- **Verify:** `dotnet test --filter AgentGrainTests`.

### Step 10 — DevHost wiring
- Register `InMemoryImageGenerator` (and its factory access) in `tools/Agent42.DevHost` so the offline
  single-user path can select an image model and generate.
- **Verify:** `dotnet run --project tools/Agent42.DevHost` boots; the image model appears in the picker.

### Step 11 — ADR + docs (`/decide`, `/sync-docs`)
- New ADR 0020 "OpenAI-compatible image-generation model capability": records the capability bit, the
  dedicated-model UX, the assistant-attachment mirror, the reuse of the `attachments` container, the
  Step-0 spike findings, and the **data-egress** consequence (prompt + edit input bytes leave the
  tenant — extends ADR 0013). Note it amends ADR 0004 (assistant-generated attachments) and ADR 0018
  (per-user providers gain image models).
- Add a gotcha if Step 4/5 surface a non-obvious trap (e.g. the assistant positional join, or the
  adapter's byte-out-of-session rule). Update `CLAUDE.md`'s "Where knowledge lives" model bullet.
- **Verify:** `/audit-docs` clean on the touched claims; `dotnet format --verify-no-changes`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | A provider declaring an image model surfaces `SupportsImageGeneration` in capabilities | `GetCapabilities_ImageModel_ReportsImageGeneration` | `ModelProviderTests.cs` |
| AC2 | `/api/models` reports the image-generation flag for an image model | `Catalog_IncludesImageGenerationFlag` | `ModelCatalogTests.cs` |
| AC3 | The factory yields an `IImageGenerator` for an image model and `null` for a chat model | `Factory_ImageModel_YieldsGenerator` | `ModelProviderTests.cs` |
| AC4 | Selecting an image model + a prompt generates & stores an image (text-to-image) | `ImageModel_TextPrompt_GeneratesAndStoresImage` | `AgentGrainTests.cs` |
| AC5 | An attached image on an image-model turn drives image-to-image | `ImageModel_WithAttachment_RunsEdit` | `AgentGrainTests.cs` |
| AC6 | No attachment ⇒ image-to-image uses the most recent conversation image | `ImageModel_NoAttachment_UsesMostRecentImage` | `AgentGrainTests.cs` |
| AC7 | An assistant image round-trips through history on reload | `History_IncludesAssistantGeneratedImage` | `AgentGrainTests.cs` |
| AC8 | An image-gen turn does not bloat the serialized session (bytes out of session) | `ImageTurn_DoesNotBlowTheSessionSizeBudget` | `AgentGrainTests.cs` |
| AC9 | Switching to a no-vision chat model strips assistant images from the model's view, restored on switch back — **with a tool-calling turn interleaved before the image turn** (proves the id-keyed link survives non-1:1 assistant messages; a clean single-turn test would pass vacuously — see C1) | `NoVisionModel_WithholdsAssistantImage_AndRestores_AcrossToolTurn` | `AgentGrainTests.cs` |
| AC10 | Providers CRUD accepts + round-trips an image-capable model, key stays masked | `Providers_AcceptImageModel_MaskKey` | `ProvidersApiTests.cs` |
| AC11 | The frontend render path references the proxy + handles the `Image` event | asset-test heuristic | new `*AssetsTests.cs` |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` @ 49d0dfb)

- `new_issues: 0` · `new_security_issues: 0`
- `duplication_pct_max: 3`
- `coverage_new_code_pct_min: 80`
- `cognitive_complexity_max: 15`
- `dependencies.vulnerable_packages: 0` (hard block) · `add_policy: latest-stable`
- CI hard-blocks: build, tests, `dotnet format --verify-no-changes`, vulnerable packages,
  doc-path integrity. Branch: a `feature/*` branch + MR (never commit to `main`).

## Out of scope

- A cross-conversation **gallery** of generated images (decision 3 — persist-in-conversation only).
- Image generation via a **tool** the text model calls (decision 1 chose a dedicated model).
- Image generation on **Foundry/Responses** hosted tools (that is ADR 0015's `code_interpreter`
  path; this is a user-added OpenAI-compatible image endpoint).
- Multi-image (`n>1`) results, size/quality pickers, streaming partial images, inpainting masks —
  a single image per turn, default size. (Revisit later.)
- Using a generated image as input to a **vision chat** model automatically (the user can attach it).
- Any new blob container or Bicep/RBAC change (Step 8 confirms reuse).

## Review

Adversarial `critic` pass run against the draft (fresh context, at 49d0dfb). Findings and handling:

- **C1 (Critical) — assistant positional join is unsound (assistant messages not 1:1 with turns).**
  ACCEPTED. Redesigned: generated images are stored **id-keyed** in `GeneratedImages`
  (`AgentGrainState`), never a positional per-assistant-turn list. Strip/restore/history all key by
  `AttachmentRef.Id`. Context section, Step 0.4, Step 4, Step 5 rewritten. This also dissolves M2.
- **M1 (Major) — `GetHistoryAsync` drops text-less assistant turns; an image-only turn vanishes
  (breaks AC7).** ACCEPTED. Step 5 now names the `AgentGrain.cs:663/665` filter relaxation and
  passing refs into `ChatTurn.Attachments`, mirroring the user-branch fix at `:653-658`.
- **M2 (Major) — no alignment discipline for a positional assistant list.** DISSOLVED by the C1
  redesign (id-keyed map needs no alignment/back-fill).
- **M3 (Major) — AC9 unprovable without an interleaved tool turn.** ACCEPTED. AC9 now mandates a
  tool-calling turn before the image turn (expressible offline via the fakes).
- **m1 (Minor) — wrong `SaveAsync` arg order.** ACCEPTED. Step 4.3 corrected to
  `SaveAsync(ownerKey, fileName, mediaType, content, ct)` per `IAttachmentStore.cs:22-24`.
- **m2 (Minor) — adapter reuse of `_session` is not a one-liner.** ACCEPTED. Step 0.3 and Step 4.4
  now carry the snapshot→rebuild-on-adapter→restore dance (`AgentGrain.cs:474-478`).
- **m3 (Minor) — `StoredModel`/`AgentGrainState` Id ordering.** Confirmed correct (next ids
  `StoredModel [Id(4)]`, `AgentGrainState [Id(9)]`); noted in Step 4.

Critic's "no finding" areas (ADR supersession, empty-baseline/no-op interaction, SSRF/retry/byte
gotchas, quality-gate thresholds) were confirmed sound and left as drafted.

## Implementation notes — where the build diverged from the plan

Executed on `feature/image-generation-model` from `main @ 49d0dfb`. All 11 ACs pass; full suite 313/313;
`dotnet format --verify-no-changes` clean. Divergences, all driven by Step-0 spike findings:

1. **Interface named `IImageGenerationService`, not `IImageGenerator`** (and the factory accessor is
   `GetImageGenerationService`). The spike found Microsoft.Extensions.AI 1.13 ships its own
   `Microsoft.Extensions.AI.IImageGenerator`, which would be an ambiguous reference (CS0104) in every
   Backend file importing both namespaces. The records `GeneratedImage`/`ImageInput` kept their planned
   names (they don't collide in the Agents layer; `OpenAIImageGenerator` fully-qualifies the SDK's
   `OpenAI.Images.GeneratedImage`). File is `IImageGenerationService.cs` (plan said `IImageGenerator.cs`).
2. **Mechanism = adapter (Step 0.3 resolved to the adapter, not manual append).** The spike proved AF 1.13
   exposes no append-without-inference (`TryGetInMemoryChatHistory` yields no mutable list on a fresh
   session), so the turn is recorded by running a one-shot `ImageGenerationChatClient : IChatClient`
   (`RecordImageTurnAsync`, the snapshot→rebuild-on-adapter→restore dance).
3. **Assistant→ref link is a MESSAGE-LEVEL `assistantImageId`, not the content-level `UriContent` prop
   (Step 0.4 refined).** The spike confirmed the content-level `AdditionalProperties` round-trips (as
   planned), but ALSO that a **message-level** id survives both serialization AND the image-strip — which
   the content-level one does not (the `UriContent` is removed when withholding from a no-vision model). So
   the vision-gate strip/restore for assistant images stays reversible by the message-level id
   (`RehydrateAssistantImageAsync`). Still id-keyed, never positional (C1 honoured).
4. **Step 9 tests folded into AC4/AC5/AC8** — the session-size guard is AC8, "edit reads bytes from the
   store" is AC5, and "text-to-image sends no input" is AC4 (the probe echoes edit input but returns a
   distinct marker for text-to-image), so no separate Step-9 tests were needed.
5. **AC9's test image model is declared tools-capable** (`TestSiloConfigurator.ImageModel`), so switching
   to it does not strip the interleaved tool turn — keeping a genuinely non-1:1 assistant structure at
   strip time. A real image provider declares no tools; the capability is orthogonal to the image path.
6. **No `index.html`/`styles.css` changes** — the assistant image gallery reuses the existing generic
   `.msg__images`/`.msg__image` classes and the provider-row flex row wraps the extra checkbox for free.
7. **Step 7 verified end-to-end via DevHost HTTP** (image NDJSON event → proxy serves a valid PNG →
   history round-trips the assistant image → most-recent-edit second turn), which is stronger than the
   planned browser-only `/run`.
