# Plan: Decision-1 model router ("Auto" model)

planned-against: main @ f7daade

## Overview

Add an **Auto** entry to the model picker. When selected, each turn first asks Microsoft-Decision-1
(a small Foundry classification model that returns a probability per option, no generated text) which
of the user's own chat models should answer, then runs the turn on that model. The options of the
decision are the models available to that user. Why now: users with several providers (Foundry,
Anthropic, GitHub Models, ...) must pick by hand today, and there is no per-turn model choice anywhere
in the code (only a server-side default).

## User decisions (interview, 2026-11-21)

- **Host:** a **new `ProviderKind`** (`DecisionModel`), not a separate setting. It reuses the provider
  editor, key encryption and per-user storage. It holds a Foundry resource endpoint, an API key and ONE
  declared "model" whose id is the Decision-1 deployment name. It is never a chat model.
- **Option text:** the user asked whether a **model card / web source** can describe each model. Answer
  adopted: three layers, first hit wins: (1) optional user-written `Hint` per model, (2) best-effort
  fetched model card, (3) description derived from capability flags + display name. Layer (2) is
  UNVERIFIED as a source (see Step 1 spike); if no source proves good, ship layers 1 and 3 only and drop
  Step 8. The router must work with layer 3 alone (zero setup).
- **Cadence:** every turn, **sticky unless clear win**: keep the conversation's current model unless the
  router picks another with probability above a threshold AND a margin over the current model's
  probability.
- **Scope:** browser chats and chat-app (Telegram/Slack/Discord) plain chats. Agent chats keep their pinned
  model. Fleet, stored agents, email-in, reminders and background turns do NOT route.

## Affected areas (verified against the tree by scouts on 2026-11-21; executor re-verifies paths)

Providers and catalog:
- `src/Agent42.Backend/Models/ModelProviderOptions.cs` (`ProviderKind` enum :7-34, `ConfiguredModel` :41-114)
- `src/Agent42.Agents/Models/UserProviderConfig.cs` (`StoredProvider`/`StoredModel` :52-59, `UserProviderConfig` :69-83)
- `src/Agent42.Backend/Models/ModelProviderBuilder.cs` (kind branch), `ChatClientFactory.cs` (`Resolve` :222-241, `FallbackProviderKey` :218), `UserProviderResolver.cs` (default chain :178-215), `ModelCatalog.cs` (`ModelInfo` :11-42, `CompositeModelCatalog` :160-203), `CopilotOnlyChatClient.cs` (the refusing-client shape to copy)
- `src/Agent42.Backend/Models/AgentApi.Models.cs` (provider CRUD validation :167-183, `PUT /api/default-model`)
- Per-user factory resolution: `src/Agent42.Agents/Models/IUserChatClientFactoryProvider.cs`, `src/Agent42.Backend/Models/UserChatClientFactoryProvider.cs`

Turn path:
- `src/Agent42.Agents/Conversations/AgentGrain.cs` (`ChatCoreAsync` :552, `ChatTurnAsync` :570, model bind :587-609, `SwitchModelAsync` :611/:1094-1119, `GetSelectedModelAsync` :337), `AgentGrainState.cs` (`DeploymentName` :50), `AgentStreamEvent.cs` (events :6-115)
- `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs` (:146-231; agent chats null the model :196-207; `GET .../model` :40)
- `src/Agent42.Backend/ChatGateway/ChatTurnPipeline.Turn.cs` (:82-86), `ChatTurnPipeline.Commands.cs` (:90, `/model`), `src/Agent42.Agents/ChatGateway/ChatRouteGrain.cs` (:62-64)
- Host registration: `AddAgent42Silo()` / `AddAgent42Api()`, Backend, `tools/Agent42.DevHost`, test silos (ADR 0105)

Other model-taking entry points that must refuse `auto` (see constraints): `AgentApi.FleetAnalyzer.cs`, `AgentApi.Fleet.cs`, `AgentApi.FleetLessons.cs`, `AgentApi.StoredAgents.cs`, `AgentApi.Models.cs` (`PUT /api/default-model`), `AgentGrain.cs` (`BindAgentAsync` :351, `RebindModelAsync`). Provider API shapes: `ModelProviderContracts.cs`.
Existing tests touched: `tests/Agent42.Tests/AttachmentUrlRewriteTests.cs:155`, `ProvidersApiTests.cs:181`, `ModelProviderTests.cs`, `ModelCatalogTests.cs`, `UserModelCatalogTests.cs`, `AndroidStreamContractTests.cs`.
Frontend: `src/Agent42.Backend/wwwroot/js/models.js` (picker; `setModel` must not add a raw `auto`), `providers.js` (editor: `capDefs`, `syncProviderKindUi`, new kind + hint field), `send.js` (:238-240), `thread.js` (render of the routed label), `conversations.js` (:49,:75 model restore), `sw.js` (CACHE).
Android contract: `tools/Agent42.Android` NDJSON golden file (ADR 0114), touched only if a new stream event is added (Step 7).

## Context and constraints the executor cannot cheaply re-derive

- **No prior art.** No ADR, gotcha or code mentions a router, Auto model or Decision-1 (grep, 2026-11-21).
  Two Microsoft APIs exist; this plan uses ONLY the Foundry one:
  `POST <foundry-resource-endpoint>/providers/microsoft/v1/systemone`, body
  `{model: <deployment name>, state, questions: {key: {type: "choice", instructions, criteria: {optionKey: description}}}}`,
  auth `api-key: <key>` or Entra bearer (`https://cognitiveservices.azure.com/.default`). Choice answer = selected
  option + probability per option + `confidence`; 1-255 options; context window 32,768 tokens; no output tokens.
  Source: learn.microsoft.com/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision.
  The Azure OpenAI "Decisions API" (`gpt-6-luna`, `predicate`, `input`) is a different shape: out of scope.
- **Exact response JSON names for the per-option probabilities are NOT in the docs I read** (docs say "a
  probability for every option" and a `confidence`; the Foundry sample reads only `answers.<q>.choice`).
  Step 1 spike must capture a real response. Never code the parser from the docs alone.
- **Egress (CLAUDE.md rule 13, ADR 0013):** the last user message (trimmed) goes to the user's own Foundry
  resource. This is an ADR 0013 extension and must be recorded as such in the new ADR. It is opt-in: with no
  `DecisionModel` provider nothing is sent and no Auto entry appears. No operator model (ADR 0019/0030): the
  router is per-user only. The endpoint comes from the stored provider, never from a request or model (ADR
  0018: user endpoints are deliberately not SSRF-guarded; keep that rule, do not widen it).
- **`ProviderKind` is stored by name; append the new member LAST** (ADR 0103, `ModelProviderOptions.cs:7-34`).
  The editor's capability checkboxes are positional: append any new flag LAST (`gotchas.md:4769`).
- **The new kind must be invisible as a chat model everywhere:** `GET /api/models`, the picker,
  `PUT /api/default-model`, the default chain and `FallbackProviderKey` (ADR 0019/0108 pattern:
  `UserProviderResolver.cs:203-215`, `ChatClientFactory.cs:218-219`), embedding/speech/transcription default
  selectors. A client built for it must refuse, like `CopilotOnlyChatClient`. `Resolve` maps a missing provider
  onto the default (`gotchas.md:4628`): make sure a persisted id naming the DecisionModel provider refuses
  instead of falling back.
- **Reserved model value `auto`** exists only on the wire (`ChatRequest.Model`) and in the picker. It must
  NEVER be persisted into `AgentGrainState.DeploymentName`, passed to `ModelId.Parse`, or reach fork/fleet
  (fork reads `DeploymentName`, `AgentGrain.EditFork.cs:208-245`). Persist the chosen real model there, plus a new
  trailing bool `AutoModel` (default false, so the Cosmos CLR-default trap `gotchas.md:4695` cannot flip it;
  not named `ModelRouting`, which already exists in Fleet: `ModelRoutingPromptAgentFactory`)
  that makes `GetSelectedModelAsync`/`GET .../model` answer `auto` so the picker restores to Auto.
  Any explicit model choice clears the flag.
- **`auto` must be refused at EVERY server entry that takes a client model, not only in `ChatTurnAsync`**
  (critic finding, verified by reading code). With no router the old path would call `SwitchModelAsync("auto")`
  and persist it (`AgentGrain.cs:611`, `:1103`). One guard lives in the grain in `SwitchModelAsync`,
  `RebindModelAsync` and `BindAgentAsync` (`AgentGrain.cs:351` persists `binding.Model` without passing
  `ChatTurnAsync`). Endpoint-level validation (400, or ignore for the chat path when no router) on:
  `AgentApi.FleetAnalyzer.cs:56-63`, `AgentApi.Fleet.cs:168`, `AgentApi.FleetLessons.cs:111,139`,
  `AgentApi.StoredAgents.cs:45,58`, `PUT /api/default-model` (`AgentApi.Models.cs:82-94`). One test per path.
- **Stale Auto (provider deleted or disabled after Auto was chosen):** `GetSelectedModelAsync` answers `auto`
  only while the user still has a router, else the real `DeploymentName`; the gateway ignores a stored
  `ModelOverride = "auto"` for a user with no router (`ChatRouteGrain.cs:62-64`); `models.js` `setModel` must not
  add a raw `auto` option to the picker when Auto is unavailable.
- **Candidate source for the grain:** the Agents project cannot see `ModelInfo`/`IUserModelCatalogProvider`
  (Backend) and `IChatClientFactory` has no enumeration. Decision: `IUserModelRouterProvider.GetForUserAsync`
  returns an `IModelRouter` that owns BOTH the candidates (Agents-side record: id, display name, flags,
  hint, copilot-only/chat-capable) built in the Backend from the per-user catalog, and the decision call. The
  grain passes only per-turn facts (message, has images, tools on, current model). `IChatClientFactory` stays
  unchanged (so its test factory at `IChatClientFactory.cs:~139` is not rippled).
- **Egress scope (ADR 0013), wider than the Foundry call:** Auto changes which of the user's providers receives
  the whole conversation, including third-party endpoints (GitHub Models, Ollama-style) the user did not pick for
  that chat. The ADR must say so and list a per-model opt-out from Auto as a rejected-for-v1 alternative (the user
  can still simply pick a model by hand; a provider they don't want used must not be connected).
- **Incognito/ephemeral turns (ADR 0134): no routing.** An ephemeral turn keeps the current/default model and
  makes no router call, so it writes no `AutoModel`/`DeploymentName` change and sends nothing extra anywhere.
- **Concurrent runs (CLAUDE.md rule 6):** `SwitchModelAsync` mutates the shared `_modelId`/`_agent`/`_session`.
  Routing is skipped while another run is in flight on the activation (use the existing in-flight handle,
  `_turnCts`); that turn keeps the current model. State it in the ADR.
- **Hard capability filter before the router sees options** (the router is advice, never a capability check):
  drop `CopilotOnly`, non-chat models (image-generation-only, embeddings, speech, transcription kinds), models
  without tools when tools are enabled for the turn (a no-tools downgrade strips tool history,
  `AgentGrain.cs:1107-1115`), and models without vision when the turn has image attachments or history images
  (`CanSeeImages` :655, `gotchas.md:829`). 0 candidates: no router call, keep current/default. 1: skip the call.
- **Sticky rule needs the current model's probability**, so the current model must be among the options when it
  survives the filter. Thresholds are constants in an options class with defaults (no per-user knob in v1).
  Add an explicit "cannot tell" option (Microsoft's guidance): choosing it keeps the current model.
- **Failure never fails the turn:** timeout (short, a few seconds), non-2xx, 429, bad JSON, unknown key, a
  refusal: keep the current model (or the default if none bound), log a warning, continue. Honor the turn's
  cancellation token. The named `HttpClient` must opt out of `AddServiceDefaults`' retry pipeline and be its own
  client (`gotchas.md:407, :3260`; CLAUDE.md rule 8).
- **Router state is per turn, never a grain field (ADR 0106).** The decision is a local in the turn; the
  existing `_modelId` switch via `SwitchModelAsync` is the only state change. The router service is resolved
  per user like the chat-client factory (`gotchas.md:1088`: resolved on activation, not injected); register the
  port per host (ADR 0105). A direct grain constructor dependency is validated at startup, a transitive one is
  not.
- **Prompt cache (ADR 0104):** the cache key is per conversation, not per model (`PromptCacheKey.cs:18-26`), but
  a model switch changes the prefix. Stickiness is the mitigation; do not claim a measured hit rate.
- **Usage dashboard counts chat spans only** (`gotchas.md:675`), and the router call is not an `IChatClient`.
  Router tokens are therefore NOT in the dashboard or the cost estimate. State that in the ADR; log token usage on
  an `Activity`. Do not add a price row you cannot source (CLAUDE.md rule 3). Decision-1 list price seen:
  $0.042 per 1M input tokens, no output charge: informational only.
- **Visibility:** silent routing is confusing. Chosen design: a new `Routed` NDJSON stream event (model id +
  display name), shown as a small non-persisted label on the assistant bubble. Rejected: a streamed `TextDelta`
  notice (it lands in the bubble text, copy, export and regenerate). Cost of the choice: the Android golden file
  and its parser must tolerate/know the new event (ADR 0114): Step 7 verifies, and the critic should challenge it.
- **Manual prerequisite, never executed by the agent (CLAUDE.md rule 5):** deploying Microsoft-Decision-1.1 to
  a Foundry resource (`az cognitiveservices account deployment create ... --model-name Microsoft-Decision-1.1
  --model-format Microsoft --model-version 1 --sku-name GlobalStandard`). The plan needs no role assignment (API
  key per user). Hand the user the exact command; the live checks in Step 9 need their deployment.
- Governing ADRs: 0013, 0018, 0019, 0030, 0033, 0103, 0104, 0105, 0106, 0108, 0110, 0114, 0021 (null==all if a
  toggle is added later). Gotchas: see inline paths above. Next ADR number: **0137**.

## Steps

1. **Spike (user-assisted, no repo change).** With `spike-dotnet`: against a real Decision-1(.1) Foundry
   deployment (user supplies endpoint, deployment, key; do not store them), send a `choice` question with 3-4
   options + a "cannot tell" option and record the exact response JSON (per-option probability field names,
   `confidence`, refusal shape, usage). Also measure latency (p50/p95 over ~20 calls) and whether api-key auth
   works. Second spike: evaluate candidate model-card sources for layer 2 (Azure/Foundry catalog metadata the
   existing `AzureModelCatalog` can already reach, OpenRouter's public models listing, Anthropic/others'
   models APIs): which give a usable description + context length for a user's model by name, how names map
   from Azure deployment names (ARM exposes the underlying model name/version), and whether a request needs auth.
   NOTE: a quick fetch of OpenRouter's listing during planning returned an unexpected shape (10 Anthropic-style
   entries, no description/pricing), so do not assume it. Output: findings pasted in the spec's Review section
   plus a go/no-go for Step 8. Verify: a captured JSON sample exists and the parser in Step 3 is written from it.
2. **ADR 0137 via `/decide`** (egress extension, per-user DecisionModel kind, `auto` reserved value and
   `ModelRouting` flag, sticky thresholds, failure policy, router tokens outside the usage dashboard, scope
   limits, rejected alternatives: separate setting, reuse of an existing provider, per-turn without stickiness,
   TextDelta notice). Cross-link 0013/0018/0019/0104/0108. Verify: ADR README index updated, `QualityGateTests`
   and any ADR-index tests green.
3. **Router core (Agents project).** `IModelRouter` + `IUserModelRouterProvider` (mirror
   `IUserChatClientFactoryProvider`), request/option/decision records, and `ModelRouterService` holding the pure
   logic: candidate filtering, option-text layering, key mapping (positional keys like `m1..mN`, never raw model
   ids), sticky/threshold/margin/"cannot tell" decision. Keep each method under the complexity limit. Files:
   new `src/Agent42.Agents/Models/Routing/*`. Verify: unit tests for filtering (vision, tools, copilot-only,
   non-chat), stickiness, threshold edges, 0/1/N candidates, message trimming to a safe char budget.
4. **Decision-1 HTTP client + provider kind (Backend).** Append `DecisionModel` to `ProviderKind`; builder branch
   builds a refusing chat client and exposes endpoint + deployment + decrypted key to the router provider;
   `FoundryDecisionClient` (typed named `HttpClient`, no retry pipeline, short timeout, `api-key` header, parser
   written from the Step 1 sample). Provider CRUD validation: DecisionModel requires an endpoint and exactly one
   model, coded into `ValidateProvider` (`AgentApi.Models.cs:167-183`, which today only requires at least one model).
   It must pass `UserProviderResolver.Merge` without adding to `declared`: copy Copilot's `[]` at
   `ModelProviderBuilder.cs:165-169`. Existing tests that iterate every `ProviderKind` will see the new member and
   must be updated: `tests/Agent42.Tests/AttachmentUrlRewriteTests.cs:155`, `ProvidersApiTests.cs:181`. Exclude the kind from catalog, picker data, default chain, `FallbackProviderKey`, `PUT /api/default-model`
   and the embedding/speech/transcription selectors. Files: `ModelProviderOptions.cs`, `ModelProviderBuilder.cs`,
   `UserProviderResolver.cs`, `ModelCatalog.cs`, `ChatClientFactory.cs`, `AgentApi.Models.cs`, new
   `src/Agent42.Backend/Models/Routing/*`. Verify: provider API tests, catalog tests, a test proving a persisted
   id naming the DecisionModel provider refuses (no silent default fallback), parser tests on the Step 1 sample.
5. **Persisted fields.** `StoredModel.Hint` (trailing `[Id(n)]`, nullable, capped length) and
   `AgentGrainState.AutoModel` (trailing `[Id(n)]`, default false). Files: `UserProviderConfig.cs`,
   `AgentGrainState.cs`, plus the API shapes `ModelProviderContracts.cs` (`ProviderModelRequest`/`ProviderModelResponse`)
   and the mapping at `AgentApi.Models.cs:212-224` for `Hint`. Verify: `GrainStorageSerializerTests` round-trips both on the Cosmos JSON path and the
   binary path; a document without the fields loads with defaults. Parallelizable with Step 3.
6. **Grain integration.** In `ChatTurnAsync` (before the bind/switch at :587-611): when the request model is `auto`
   and a router exists, compute candidates, call the router, apply `SwitchModelAsync(chosen)`, set
   `AutoModel=true`; any other explicit model sets it false; `GetSelectedModelAsync` returns `auto` when set and a
   router exists. `auto` must never reach `ModelId.Parse`/`DeploymentName` (guard in `SwitchModelAsync`,
   `RebindModelAsync`, `BindAgentAsync` + the endpoint validations listed in the constraints, one test per path).
   No router, no candidates, an ephemeral turn, or another run in flight: no router call, behave as today with the
   current/default model and never persist `auto`. Agent-pinned turns unchanged (the endpoint already nulls the model).
   Register the router port in Backend, DevHost (canned deterministic router) and test silos; confirm
   `AddAgent42Silo()` startup validation still passes. Files: `AgentGrain.cs` (+ a new partial to keep
   complexity down), host registration files, `tools/Agent42.DevHost`. Verify: grain tests with a fake router
   (auto switches model and persists the real id; sticky keeps; failure keeps and the turn completes; explicit
   model clears the flag; fork after an auto turn uses the real model; image turn only offers vision models).
7. **Visibility event + chat gateway.** Add `Routed` to `AgentStreamEvent` (emitted only when the model changed
   or was chosen on an auto turn), update the Android NDJSON golden file and verify the Android parser ignores or
   handles it (`./gradlew test` in `tools/Agent42.Android`, or document that it could not run here). Chat apps:
   `/model auto` accepted only when the user has a router (`ChatTurnPipeline.Commands.cs`), stored as the
   override, passed through `ChatTurnPipeline.Turn.cs:82-86`; a stored `auto` for a user who no longer has a router is ignored.
   Append `Routed` LAST in `AgentStreamEvent`'s type enum. `AndroidStreamContractTests.cs:31,39-59` has an exhaustive
   switch over every event type, so the golden file and that switch must be edited; `StreamEvent.kt:37` already maps an
   unknown type to `UNKNOWN` with `ignoreUnknownKeys`, so risk is low, but decide explicitly what `ChatReducer` does with it.
   Verify: stream/serializer tests, golden-file test, gateway command tests (auto accepted with router, rejected without,
   stale auto ignored).
8. **(Conditional on Step 1 go) Model-card source.** `IModelCardSource` behind a fixed-host client (never a
   user-supplied URL), in-memory cache with TTL, per-model failure tolerated (falls through to layer 3), fetches
   model names only (no conversation content; record this metadata egress in ADR 0137). Skip entirely on no-go.
   Verify: tests for cache, failure fallthrough, name mapping, and that layer order is Hint > card > flags.
9. **Frontend + live checks.** `models.js`: inject an **Auto** option at the top only when the user has an
   enabled DecisionModel provider (data from the catalog/providers response, not a hardcoded flag); selecting it
   sends `auto`; restore via `GET .../model`; hide/ignore for agent chats. Provider editor: DecisionModel kind
   with endpoint, key, deployment name, and a per-model "when to use" hint field (append-last rule for any flag).
   Render the `Routed` label in `thread.js`. **Bump `CACHE` in `sw.js`** (CLAUDE.md rule 3; 4 tests pin it). Verify:
   source-text guards, DevHost run showing Auto + routed label offline; then the user's live Foundry deployment
   (a real Auto turn, a vision turn, a router outage) as a post-merge checklist item.
10. **Docs.** `/sync-docs`: CLAUDE.md feature map (Model providers row gains 0137), gotchas entries for the three
    non-obvious traps (reserved `auto` never persisted; DecisionModel kind must be invisible as a chat model and
    refuse on `Resolve` fallback; Decision-1 is not an `IChatClient`, so its tokens are outside the usage
    dashboard), and the user-facing docs `docs/features/bring-your-own-models.md` and `docs/features/model-picker.md`.

Parallelizable: Step 3 and Step 5; Step 8 after Step 3; Steps 6-7 after 3-5; Step 9 after 6-7.

## Acceptance criteria to test mapping

| AC | Test | File |
|----|------|------|
| Auto turn asks the router and runs on the chosen model; the real id is persisted, never `auto` | `AutoTurn_PersistsRealModel` | `tests/Agent42.Tests/ModelRouterGrainTests.cs` (new) |
| Router failure, timeout or refusal keeps the current model and the turn completes | `RouterFailure_KeepsModel` | same |
| Sticky: switches only above threshold and margin, "cannot tell" keeps | `Sticky_*` | `ModelRouterServiceTests.cs` (new) |
| Candidates exclude copilot-only, non-chat, no-tools (when tools on) and no-vision (images) models | `Candidates_*` | `ModelRouterServiceTests.cs` |
| 0 candidates or no router: no call, behaviour unchanged | `NoCandidates_*` | `ModelRouterGrainTests.cs` |
| DecisionModel provider never appears in `/api/models`, defaults or fallback, and refuses as a chat client | `DecisionModelKind_*` | `ModelProviderTests.cs` (existing area, verify name) |
| Provider validation: endpoint required, exactly one model | `ProviderValidation_DecisionModel` | provider API tests (existing) |
| Foundry request/response mapping matches the captured sample | `FoundryDecisionClientTests` | new, fixture from Step 1 |
| New fields round-trip on the Cosmos JSON and binary paths, old documents load with defaults | `GrainStorageSerializerTests` additions | existing file |
| Picker restores to Auto; explicit choice clears it | grain test + a source-text guard for `conversations.js`/`models.js` | `ModelRouterGrainTests.cs`, the existing frontend source-text guard test file (executor locates it) |
| `Routed` event serializes and the Android contract accepts it | golden-file test | existing NDJSON contract tests |
| `/model auto` accepted with a router, rejected without | gateway command tests | existing `ChatTurnPipeline` tests |
| `sw.js` CACHE bumped and module reachable from `main.js` | existing pins | existing tests |
| `auto` is refused or ignored (never persisted) on each non-chat path: fleet submit, fleet analyzer bind, fleet lessons, stored-agent create/update, `PUT /api/default-model`, `BindAgentAsync` | `AutoOutsideChat_*` (one per path) | `ModelRouterGrainTests.cs` + the existing API test file of each endpoint |
| Provider removed after Auto was chosen: `GET .../model` returns the real model, a stale gateway `auto` is ignored, picker shows no raw `auto` | `StaleAuto_*` | `ModelRouterGrainTests.cs`, gateway tests, frontend guard |
| Ephemeral (incognito) turn and a turn with another run in flight make no router call and write no state | `Auto_SkipsEphemeralAndConcurrent` | `ModelRouterGrainTests.cs` |
| Every `ProviderKind` loops still pass with the new kind; DecisionModel needs endpoint + exactly one model | updated loops + `ProviderValidation_DecisionModel` | `AttachmentUrlRewriteTests.cs`, `ProvidersApiTests.cs` |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` by the constraints scout, 2026-11-21)

- `branches.protected`: `[main]`: work on `feature/decision-model-router`, MR only
- `coverage_new_code_pct_min`: 80 (tool: coverlet)
- `cognitive_complexity_max`: 15 (S3776 is a build error; split, as `HindsightRegistration` does)
- `vulnerable_packages`: 0
- `outdated_packages`: report
- `add_policy`: latest-stable (no new package expected; if one is needed, resolve with `dotnet add package`, never from memory)
- Also: `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`, `dotnet build Agent42.slnx`

## Out of scope

- Routing for stored agents, `/api/agent-run`, email-in, fleet tasks/defaults, reminders and other background turns.
- The Azure OpenAI "Decisions API" (`gpt-6-luna`) and Entra bearer auth for the router (api-key only in v1).
- Per-user threshold settings, learned/trained routing, cost-aware routing (no price in `ModelInfo`), context-window-aware routing (no per-model context field).
- Counting router tokens in the usage dashboard or cost estimate.
- Using the router as a cascade judge or evaluation judge.
- Deploying the Decision-1 model or any Azure RBAC change (handed to the user).

## Review

Critic verdict on the first draft: not ready (2 high, 5 medium, 5 low). The critic read the code for the cited paths
and line numbers; the gate thresholds matched `quality-gates.yaml`. It did not read every cited ADR/gotcha in full.
All findings were accepted and folded in above:

1. HIGH `auto` leaking via other entry points: accepted. Guard in `SwitchModelAsync`/`RebindModelAsync`/`BindAgentAsync`
   plus endpoint validation on five paths, one test each.
2. HIGH stale Auto after provider removal: accepted (see "Stale Auto" constraint and `StaleAuto_*` test).
3. MED no candidate source for the grain: accepted; the router port owns the candidates, `IChatClientFactory` unchanged.
4. MED missing contract/editor files: accepted (affected areas, Step 5, Step 9).
5. MED tests that iterate every `ProviderKind`; exactly-one-model validation; `Merge` `declared`: accepted (Step 4).
6. MED egress understated and incognito: accepted. ADR records the wider egress and the rejected per-model opt-out;
   ephemeral turns do not route.
7. MED Android check: accepted, but the critic found the risk low (`StreamEvent.kt` already maps unknown types to
   `UNKNOWN`); the plan keeps the new event and appends it last.
8. LOW wrong `ChatRouteGrain.cs` path: fixed (`src/Agent42.Agents/ChatGateway/`).
9. LOW `ModelRouting` name collision with Fleet: renamed the flag `AutoModel`. Type names `IModelRouter` etc. kept,
   they do not collide.
10. LOW vague test locations: named the existing files; the frontend guard file is for the executor to locate.
11. LOW doc debt: both feature docs named in Step 10.
12. LOW ADR 0106 framing and concurrent runs: accepted; routing is skipped while another run is in flight.

Rejected: none.

## Step 1 spike findings (2026-11-21, real Foundry deployment `Decision-1.1`)

Endpoint shape that works: the **resource** root, `POST https://<resource>.services.ai.azure.com/providers/microsoft/v1/systemone`,
`api-key` header, no `api-version`. The project-scoped URL (`/api/projects/<p>/providers/...`) answers 400
"Missing required query parameter: api-version", so the stored endpoint must be the resource root (strip a `/api/projects/...`
suffix or validate it).

Captured response (200):
```json
{"model":"microsoft-decision-1.1","answers":{"q1":{"type":"choice","choice":"m2","confidence":0.9748798363694561,
 "probabilities":{"m1":0.0036,"m2":0.9812,"m3":0.0006,"none":0.0147}}},"usage":{"input_tokens":97,"output_tokens":1}}
```
- Per-option probabilities: `answers.<q>.probabilities.<optionKey>` (sums to 1). `choice` is the argmax key; `confidence` is a separate number.
- Empty `state` returns 200 with `choice: "none"` (the "cannot tell" key worked); a bare "hi" picked the cheap model.
- Errors: bad key 401 `{"error":{"code":"401","message":...}}`; unknown deployment 404 `{"error":{"code":"DeploymentNotFound",...}}`.
- Latency over 20 sequential calls (4 options, short state): p50 0.24 s, p95 0.28 s, max 0.28 s. A 3 s timeout is generous.
- api-key auth works. Entra bearer not tested (out of scope).

Model-card source: OpenRouter's public `GET https://openrouter.ai/api/v1/models` needs no auth and returned 476 entries with
`id`, `name`, `description`, `context_length`, `architecture.input_modalities`, `pricing`, `supported_parameters`, `benchmarks`
(the planning-time odd shape did not reproduce). The listing is ~650 KB, so cache it. Weak point is name mapping:
user deployment names are arbitrary, so match only on exact/normalised id and fall through to layer 3 on a miss.
**Step 8 verdict: GO, best-effort, conditional on a miss falling through to layer 3.** Azure catalog/ARM mapping not evaluated.

## Divergences from the plan (as implemented, 2026-11-21)

- **Step 1:** done with the user's deployment. The endpoint must be the Foundry resource ROOT (a project URL needs an api-version), so the client cuts a stored endpoint back to its origin. OpenRouter's listing worked unauthenticated; Azure/ARM mapping was not evaluated.
- **Reserved value class** is `AutoModelId` (not `AutoModel`), because `AgentGrainState.AutoModel` is the persisted flag.
- **Router port on the grain** is an OPTIONAL constructor parameter (`IUserModelRouterProvider? = null`), like `TimeProvider`, so no test silo ripples; Backend registers the real one, DevHost a canned `DemoModelRouter`, and the test silo a switchable `FakeModelRouterProvider`.
- **Concurrency skip uses `_runsInFlight > 1`, not `_turnCts`** (background runs never publish `_turnCts`), and is re-checked AFTER the router await (critic finding), so a decision is dropped if another run started meanwhile.
- **Gateway:** `ChatRouteGrain` is unchanged. A stored `auto` for a user with no router is ignored by the grain (`ResolveTurnModelAsync`), which also covers every other caller. `/model auto` and `/model list` live in `ChatTurnPipeline.Commands.cs`.
- **`/api/models` gains `auto: true`** (set by the endpoint from the router port, never by a catalog) so the picker offers Auto from server data. `ModelCatalogResult` got a trailing `Auto = false`.
- **Picker refresh:** `loadModels({pin:false})` now restores the previous selection after the refill, because the refill would otherwise land the open chat on Auto.
- **Step 8 (model cards):** shipped. Cache-only on the hot path with a background load, exact normalised-name match. The request carries nothing about the user, so the ADR's "model names only" became "an unparameterised GET".
- **Router cancellation:** the call takes no turn token (the stop handle is not published yet at that point); the 4 s client timeout bounds it.
- **Not run here:** `tools/Agent42.DevHost` (SDK 11 static-assets build error, pre-existing), the Android Gradle build (no JDK), and the live Auto turn, vision turn and router outage against the user's Foundry deployment (post-merge checklist). The Android golden file, Kotlin enum and reducer were updated; the Kotlin enum still lacks `error` from ADR 0136, which is out of scope.
- **Not tested at the grain:** a history or attachment image making `HasImages` true, and fork after an Auto turn (fork reads the real `DeploymentName` by construction).
