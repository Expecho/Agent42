# Plan — Show a reasoning model's reasoning output in the chat UI

planned-against: main @ 9be5e04

## Overview

A reasoning model's "thinking" is not shown today: `ProjectStreamedContent` switches on the streamed
content parts and everything that is not text / a function call / usage falls into
`default: return null`. This plan surfaces it as a new streamed event rendered in a collapsed
disclosure above the reply, and — a scope call the user made explicitly — also makes a **Foundry**
reasoning model reachable, which today it is not.

Why now: the machinery already exists in the shape of ADR 0036 (a live-only stream event) and
ADR 0038 (a per-model flag threaded through the model representations). Nothing here invents a
mechanism.

**This plan was revised after an adversarial review that falsified its original central claim.** See
the Review section; the correction is load-bearing and is folded into Step 1 and AC2.

## Affected areas

Verified against the tree at 9be5e04.

**Agents**
- `src/Agent42.Agents/Conversations/AgentStreamEvent.cs` — new enum member; trailing `[Id(12)]` only if a field is needed
- `src/Agent42.Agents/Conversations/AgentGrain.cs` — `ProjectStreamedContent` (~line 700); possibly the persist path (~line 745-751, see Step 1c)
- `src/Agent42.Agents/Models/UserProviderConfig.cs` — `StoredModel`, trailing `[Id(7)]`

**Backend — the capability-flag sites.** The authority is
`grep -rn "SupportsSpeech" --include=*.cs src/`, which returns **eight** production sites, not the
five the first draft of this plan claimed. Re-run it before starting; the list below is planning
convenience, the grep is the truth:

1. `src/Agent42.Agents/Models/UserProviderConfig.cs` — `StoredModel` (trailing `[Id(7)]`)
2. `src/Agent42.Backend/Models/ModelProviderOptions.cs` — `ConfiguredModel`
3. `src/Agent42.Backend/Models/ModelCatalog.cs` — `ModelInfo`
4. `src/Agent42.Backend/Models/ModelProviderContracts.cs:31-33` — `ProviderModelRequest` (wire DTO)
5. `src/Agent42.Backend/Models/ModelProviderContracts.cs:41-43` — `ProviderModelResponse` (wire DTO)
6. `src/Agent42.Backend/Models/AgentApi.Models.cs` — `ToStored`
7. `src/Agent42.Backend/Models/AgentApi.Models.cs:189-196` — `Mask` (a **second** mapping in the same file)
8. `src/Agent42.Backend/Models/ModelProviderBuilder.cs:176-188` — `DeclaredModels`, the **only**
   `ConfiguredModel` to `ModelInfo` mapping. Skip it and the field is added but never populated:
   `/api/models` reports `false` for every model and nothing fails.

Miss 4 or 5 and AC6 cannot pass — the checkbox has nothing to bind to and `Mask` cannot echo it.

**Backend — the Foundry path**
- `src/Agent42.Backend/Models/ChatClientFactory.cs` — `Provider.Foundry` (`:238`, `:279-283`)

**Frontend**
- `src/Agent42.Backend/wwwroot/js/thread.js` — new `renderReasoning` / `finishReasoning`
- `src/Agent42.Backend/wwwroot/js/send.js` — one arm in the dispatch if-chain (~line 169-186)
- `src/Agent42.Backend/wwwroot/js/providers.js:243-249` (`capDefs`) **and `:293`** (see the trap below)
- `src/Agent42.Backend/wwwroot/styles.css` — `.msg__thinking`
- `src/Agent42.Backend/wwwroot/sw.js` — `CACHE` bump
- **`index.html` is NOT affected** — the capability checkboxes are built entirely from the JS
  `capDefs` array; there is no per-capability markup.
- **No new JS module.** Rendering goes in `thread.js`, already in `SHELL` and already reachable from
  `main.js` — deliberately avoiding both the SHELL all-or-nothing trap and the
  orphan-module-invisible-to-asset-guards trap.

**Tests and fixtures that must change**
- `tests/Agent42.Tests/FakeChatClient.cs` — the deterministic double every grain/API chat test uses.
  Its streaming path yields only `TextContent` and `FunctionCallContent` (`:89-122`). AC1/AC3/AC4 are
  **unreachable** until it can emit reasoning, via the established prompt-marker idiom
  (`SlowStreamMarker` `:36`, `FaultingStreamMarker` `:53`).
- `tests/Agent42.Tests/GoldenStates.cs:124-127` and
  `tests/Agent42.Tests/SerializationCompatibilityTests.cs:283-309` — see Step 4's trap.

**Offline**
- `tools/Agent42.DevHost/CannedChatClient.cs` — must emit reasoning or the feature is not
  exercisable offline at all.

## Context and constraints

### The decisions the user made (do not relitigate)

1. **Live-only.** Reasoning is streamed and then discarded — never written to `SerializedSession`,
   never replayed by `renderTurns`. This is ADR 0036's suggestion-chip shape.
2. **Also enable Foundry reasoning** — not just "surface it if a model happens to emit it".
3. **Always shown, collapsed.** No Preferences toggle, no `UserProfile` field, no new grain state.

### The correction that matters most: `reply` is NOT the persistence path

The first draft of this plan asserted that `ProjectStreamedContent`'s `reply` StringBuilder becomes
the persisted assistant turn. **That is false**, and building on it would have produced a mutation
check that proves nothing:

- `AgentGrain.cs:591-593` — *"Accumulate the reply text (only) so we can feed the last exchange to
  the follow-up suggestion call after the turn"*. Its only consumers are `:618` and `:670-671`,
  both `SuggestFollowUpsAsync`.
- Persistence is `AgentGrain.cs:749-751` — `SerializeSessionAsync(_session)`, the **framework
  session**, never `reply`.

Two separate concerns fall out, and they need separate ACs:

- **The real live-only risk is invisible to `ProjectStreamedContent` entirely.** Agent Framework
  appends the assistant `ChatMessage` with *all* its `AIContent` parts into `_session`, and
  `_session` is what gets serialised. If the session retains a `TextReasoningContent` part, then
  **reasoning is already persisted at HEAD**, before this feature ships a line, and decision 1 is
  violated by the status quo. Step 1c settles this; AC2 targets `SerializedSession`, not the
  projection function.
- **`reply` still must not receive reasoning** — but the consequence is that reasoning would be fed
  into the ADR 0036 follow-up model call: a real bug, extra third-party egress (ADR 0013), and not
  a persistence bug. That is AC2b.

### The Foundry flag may itself persist reasoning — Step 1b gates Step 4

`.agentic/gotchas.md` ("The Responses API is stateful by default") says `includeReasoningEncryptedContent:
true` is wanted for a reasoning Foundry model *"to carry reasoning items across turns without server
storage"* — and `ChatClientFactory.cs:270-272` says stateless is mandatory because *"the grain holds
all history client-side (ADR 0004)"*. Read together: on the Responses path "without server storage"
means **our session** carries the encrypted reasoning blobs, i.e. setting the flag `true` writes
reasoning into `SerializedSession` and against both decision 1 and ADR 0004's 2 MB item.

So Step 4 must not be written before Step 1b answers. Two branches, decide with measurements:

- **(a) Visible reasoning requires the flag.** Then this ADR **amends 0004**, needs a measured
  per-turn `SerializedSession` growth figure, and needs an AC bounding it. Raise it with the user
  before proceeding — it partially reverses decision 1.
- **(b) Visible reasoning works without it** (e.g. via `ReasoningOptions`, which is present in the
  pinned `Microsoft.Extensions.AI.Abstractions` 10.9.0). Then leave the flag `false`, and AC5 drops
  its `true` half.

### The Foundry seam is a THIRD pattern (verified, holds)

`Provider.Foundry` receives `ProviderOptions options` (`ChatClientFactory.cs:238`) and its client
factory is a **per-model lambda** (`:279-283`), so the flag can be read at construction time by
model id. It must **not** go on `ModelCapabilities`: `GetCapabilities` returns
`ModelCapabilities.Full with { ... }` for Foundry (`:92-98`) and never consults declared models —
the shortcut ADR 0030 and ADR 0038 sidestepped. Nor is it picker-filter-only like
`SupportsEmbeddings`/`SupportsSpeech`, which never reach client construction. Record the third
pattern in the ADR.

### Two frontend traps

- **`providers.js:293` destructures positionally:**
  `const [tools, vision, image, embed, speech] = row.querySelectorAll(".agent-form__checkbox");`
  A sixth flag must be appended to `capDefs` **and** to this destructuring, in the same order, or
  every flag after the insertion point silently shifts.
- **`send.js:172` calls `voice.onDelta(chunk.text)` inside the `textDelta` arm.** Copied literally
  into the reasoning arm, hands-free voice (ADR 0037) speaks the model's thinking aloud and, with a
  speech model configured (ADR 0038), pays a third party to do it. The reasoning arm must not touch
  `voice`.

### Governing ADRs and gotchas

- `.agentic/decisions/0036-*.md` — the live-only stream-event exemplar
- `.agentic/decisions/0014-responses-api-stateless-mode.md` — stateless is mandatory; its spec LOW-5
  is what "reasoning Foundry models are deferred" refers to
- `.agentic/decisions/0038-*.md`, `0030-*.md` — the capability-flag threading
- gotchas: "`OpenAI` is pinned to 2.12.0..." — **do not lift the pin**; a restore error on 2.13.0 is
  the guard working, not a broken lockfile
- gotchas: "The Responses API is stateful by default", "An agent tool's RETURN value is persisted..."
  (2 MB cap), "Every MR that touches `wwwroot/js/` must bump `CACHE`", "The service worker's SHELL is
  all-or-nothing", "A frontend module unreachable from `main.js` is invisible to every source-text guard"

### Deliberate difference from ADR 0036

Suggestions are gated on `_interactiveTurn` because they cost an **extra model call**. Reasoning
costs nothing extra — it is already in the stream — so it is **not** gated, and no new turn-scoped
field is introduced. That keeps this clear of the
`_turnLocation`/`_ephemeral`/`_interactiveTurn`/`_webDocumentIngested` clear-it-in-three-places trap.
Say so in the ADR so the next reader does not "harmonise" it.

### Exemplars to copy

- Stream event end to end: `Suggestions` — `AgentStreamEvent.cs` to `AgentGrain.cs` to
  `AgentApi.Conversations.cs:149` (record serialised directly, camelCase enum, so the wire type is
  `"reasoning"` and **no endpoint edit is needed** — verified) to `send.js:169-186` to `thread.js:129`
- DOM shape: **`addToolCall` / `completeToolCall` (`thread.js:157`, `:183`)** — a `<details>`
  inserted into `.msg__bubble` before `.prose`, mutated in place as the stream advances. Closer than
  `renderSuggestions`, which renders once from a complete list.
- Untestable-wiring fallback: `NativeShare_IsGatedOnAPureFeatureDetect` (ADR 0047) — a source-text pin
- Spike harness: `tests/Agent42.Tests/StreamingEmptyChoicesTests.cs` — stub SSE transport driving a
  real `AsIChatClient()`. No network, deterministic.

## Steps

**Step 1 — Spike three unknowns (blocking; before any feature code).**
Model on `StreamingEmptyChoicesTests`; `/spike-dotnet` for anything needing a scratch project.
- **(a)** Feed a chat-completions SSE chunk carrying `delta.reasoning_content` through
  `AsIChatClient()` on the pinned stack; record what content type comes out. Note: the `found=False`
  in the pin gotcha was measured against an **empty-choices** chunk (correctly absent) and says
  nothing about a chunk that really carries reasoning.
- **(b)** Determine whether visible reasoning on the Responses path needs
  `includeReasoningEncryptedContent: true`, or whether `ReasoningOptions` suffices. **This gates Step 4** —
  see the two branches above.
- **(c)** Does `SerializeSessionAsync(_session)` retain a `TextReasoningContent` part? This decides
  whether decision 1 needs a strip step, and whether reasoning is *already* persisted at HEAD.

*Verify:* the spike prints observed content types and a serialized-session excerpt. **Record every
measured value in the ADR.** If (a) shows reasoning never surfaces on the pinned stack, stop and
re-plan — the pin is not liftable. If (b) lands on branch (a) or (c) says yes, **check in with the
user before continuing**: both partially reverse decision 1.

**Step 2 — The event type.** Append `Reasoning` to `AgentStreamEventType` (last member); reuse the
existing `Text` property. Trailing `[Id(12)]` only if the spike shows a dedicated field is needed.
*Verify:* `dotnet build Agent42.slnx`.

**Step 3 — Emit it, and keep it out of the two places it must not reach.** One new arm in
`ProjectStreamedContent` above `default:`, not appending to `reply`. Add the strip-before-persist
step only if Step 1c requires it.
*Verify:* `dotnet test Agent42.slnx --filter AgentGrainTests`.

**Step 4 — Foundry enablement** (only after Step 1b). Thread `SupportsReasoning` through all eight
sites; `Provider.Foundry`'s lambda consults it per model via a small pure helper.
**Golden-fixture trap:** `GoldenStates.cs:124-127` deliberately alternates the capability bits, and
`SerializationCompatibilityTests.cs:283-309` compares the live fixture against a **frozen, never
regenerated** base64 blob. So leave `SupportsReasoning` **false** in the fixture (the blob has no
`[Id(7)]` and deserialises `false`), update the "five capability bits" comment to six, and add a
sixth `Assert.Equal` at `:309`.
*Verify:* `dotnet test Agent42.slnx --filter "ModelProvider|Providers|Serialization|GoldenStates"` —
note `--filter Provider` alone matches neither serialization test.
*Parallelizable with Step 5.*

**Step 5 — Frontend.** `renderReasoning`/`finishReasoning` in `thread.js` (copy `addToolCall`'s
`<details>` shape, insert before `.prose`); one dispatch arm in `send.js` that **does not call
`voice.onDelta`**; `.msg__thinking` CSS; `capDefs` **and** the `:293` destructuring; bump `CACHE`
from `agent42-shell-v34` to `-v35`.
*Verify:* `dotnet test Agent42.slnx --filter Assets`.
*Parallelizable with Step 4.*

**Step 6 — Offline leg.** Teach `CannedChatClient` to emit reasoning; run the browser pass over CDP
and record measured values in the ADR. **This covers rendering only** — DevHost wires a
`StaticModelCatalog` + `CannedChatClient` and has no Foundry Responses endpoint, so the Foundry half
is prod-only and unverifiable in CI. Record that as a named residual, as ADR 0038 did for TTS.

**Step 7 — Docs.** `/decide` for ADR **0071**. Front matter, following ADR 0038's precedent:
`extends: [0013, 0018, 0030, 0036, 0038]`, `amends: [0014]` — plus **`0004`** if Step 1b lands on
branch (a). Add a gotcha for the `reply`-is-not-persistence correction (it misled this plan once).
Update the CLAUDE.md repo-notes bullet.

## Acceptance criteria to test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC0 | `FakeChatClient` can emit reasoning on a prompt marker | prerequisite for AC1/AC3/AC4 | `tests/Agent42.Tests/FakeChatClient.cs` |
| AC1 | A streamed `reasoning_content` delta surfaces as a `Reasoning` event | `Reasoning_SurfacesFromAChatCompletionsStream` | `tests/Agent42.Tests/ReasoningStreamTests.cs` (new) |
| AC2 | **Reasoning is absent from `SerializedSession` after a reasoning turn** | `ReasoningText_IsNotPersisted` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC2b | Reasoning never reaches the follow-up suggestion call | `ReasoningText_DoesNotReachTheFollowUpCall` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC3 | The event is emitted before `Done` | `ChatAsync_EmitsReasoningBeforeDone` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC4 | Wire shape is `{"type":"reasoning","text":...}` | `ChatEndpoint_StreamsReasoningAsNdjson` | `tests/Agent42.Tests/AgentApiTests.cs` |
| AC5 | The helper returns true only for a declared reasoning model, **and** the call site passes it | helper test + a source-text pin on the call site | `tests/Agent42.Tests/ModelProviderTests.cs` |
| AC6 | `SupportsReasoning` survives provider CRUD and masking | `Providers_AcceptReasoningModel_MaskKey` (copy `:139`) | `tests/Agent42.Tests/ProvidersApiTests.cs` |
| AC7 | Frontend renders a collapsed disclosure, clears it per turn, and does not speak it | `Frontend_ShipsReasoningDisclosure` | `tests/Agent42.Tests/ReasoningAssetsTests.cs` (new) |
| AC8 | Shell cache bumped | `ServiceWorker_CacheNameIsCurrent` (update literal to `v35`) | `tests/Agent42.Tests/PwaAssetsTests.cs` |
| AC9 | The 2.12.0 pin regression stays green | existing, unchanged | `tests/Agent42.Tests/StreamingEmptyChoicesTests.cs` |
| AC10 | Golden fixture + frozen blob stay in agreement with a sixth bit | extend existing | `tests/Agent42.Tests/SerializationCompatibilityTests.cs` |

**AC2 must be mutation-checked** — it is the only thing holding decision 1 in place, and its first
draft was aimed at the wrong object. AC5's helper half cannot catch a correct helper wired next to a
hardcoded `false`; the source-text pin is what covers that, and the plan says so rather than
implying the seam is fully testable.

A search-index AC was **dropped deliberately**: `GetHistoryAsync` builds every `ChatTurn` from
`message.Text`, and a `TextReasoningContent` part is not a `TextContent`, so such a test would be
green with or without the feature. AC2 covers the same property at the layer where it can fail.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time:

- `coverage_new_code_pct_min`: **80** — `node .agentic/scripts/check-new-code-coverage.mjs`
- `cognitive_complexity_max`: **15** — SonarAnalyzer S3776 at build time, `error` severity (blocks)
- `vulnerable_packages`: **0** — hard block
- Enforcement: local `soft_warning`, CI `hard_block`
- Branch policy: `main` is protected — branch `feature/*` plus an MR

Watch S3776 on `ProjectStreamedContent` (one more switch arm) and on `Provider.Foundry`.

## Out of scope

- Persisting or replaying reasoning; any change to `renderTurns`. **Caveat:** if Step 1b lands on
  branch (a), the Foundry flag persists encrypted reasoning as a side effect — that is a scope
  change to raise with the user, not something to absorb silently.
- A Preferences toggle for reasoning visibility
- Lifting the `OpenAI` 2.12.0 pin
- Reasoning-effort selection (low/medium/high) as a user-facing control
- Metering reasoning tokens in the ADR 0031 usage dashboard — the OTel emitter emits no reasoning
  token count, so that field stays 0; separate work
- Redacting reasoning from ADR 0054's `/logs`, which captures span content regardless

## Review

A `critic` pass reviewed the first draft against the ADRs, gotchas and code at 9be5e04. Two blockers
and five majors; **all findings accepted**, and the three I spot-checked personally
(`reply`'s consumers, the `providers.js:293` destructuring, the `GoldenStates` fixture comment)
confirmed verbatim. Nothing was rejected.

| Finding | Disposition |
|---|---|
| BLOCKER 1 — `reply` is not the persisted turn; AC2 guarded nothing | Accepted. Verified at `AgentGrain.cs:591/618/670` vs `:749`. Rewrote the trap section, split AC2 / AC2b, added spike 1c. |
| BLOCKER 2 — the Foundry flag is the mechanism for *persisting* reasoning, contradicting Out of scope | Accepted. Step 1b now gates Step 4, with both branches and a user check-in. |
| MAJOR 3 — "five model representations" is wrong; 8 sites, 2 files missing | Accepted. Enumerated, with the grep named as the authority. |
| MAJOR 4 — `FakeChatClient` missing; AC1/3/4 unreachable | Accepted. Added as AC0. |
| MAJOR 5 — AC5 not assertable; `ChatClientFactoryTests.cs` invented | Accepted. Reworded to helper + source-text pin, moved to `ModelProviderTests.cs`. |
| MAJOR 6 — `[Id(7)]` walks into the frozen golden blob | Accepted. Folded into Step 4 with the fixture rule and a corrected verify filter. |
| MAJOR 7 — AC6 filed in the wrong test file | Accepted. Moved to `ProvidersApiTests.cs`, copying `:139`. |
| MINOR 8 — `index.html` not affected; the trap is the positional destructuring | Accepted. Corrected both. |
| MINOR 9 — front-matter extends/amends off precedent | Accepted. Rewritten per ADR 0038. |
| MINOR 10 — AC10 vacuous | Accepted. Dropped, with the reasoning recorded. |
| MINOR 11 — `voice.onDelta`; Foundry half unverifiable offline | Accepted. Both now explicit. |

## Divergence from the plan (recorded at execution)

Executed on `feature/show-reasoning-output` from `main @ 9be5e04` — the exact ref in `planned-against:`,
so there was **zero drift** to reconcile. Shipped as ADR 0071.

### Step 1's spikes decided two things the plan left open

- **1(a) — reasoning survives the pinned stack.** `delta.reasoning_content` → `TextReasoningContent`. No
  re-plan needed; the pin is not in the way.
- **1(b) — landed on branch (b), as hoped.** But the plan framed the choice as "the flag, or
  `ReasoningOptions`". Measured, it is not a choice at all: `includeReasoningEncryptedContent` is
  **irrelevant to visibility** (reasoning surfaces on both values) and its only effect is the `include`
  array. The knob is `ChatOptions.Reasoning.Output = Summary`. So ADR 0004 is **not** amended and AC5 lost
  its `true` half, as the plan anticipated — but for a different reason than the plan supposed.
- **1(c) — said YES, and the user was consulted as the plan required.** Reasoning was already being
  persisted at HEAD, on **both** run paths. The user chose "strip it, per decision 1". This is the one
  material scope addition, and the plan explicitly allowed for it ("add the strip-before-persist step only
  if Step 1c requires it").

### Changes of substance

1. **The strip needed a seam the plan had not identified.** `StorageInputResponseMessageFilter` — the
   response-side twin of the `StorageInputRequestMessageFilter` already in `BuildAgent`'s options block.
   Found by probing `InMemoryChatHistoryProviderOptions`; it is a sequence transform, not a predicate, so
   it can rebuild a message without its reasoning parts rather than dropping the whole message.

2. **The Foundry flag is wired in `ChatClientFactory.Build`, not in `Provider.Foundry`'s lambda.** `Build`
   is the single place holding both the resolved provider and the bare model id, and is where the OTel
   wrapper — the other cross-cutting decoration — already lives. One wiring covers Foundry and
   OpenAI-compatible alike. Justified by a measurement the plan did not have: `ChatOptions.Reasoning` is
   **inert on Chat Completions** (byte-identical request body), so applying it uniformly is safe and keeps
   the checkbox meaning one thing on every provider.

3. **AC5's call site is asserted behaviourally, not by source text.** The plan settled for a source-text
   pin, reasoning that "a correct helper wired next to a hardcoded `false`" was otherwise uncatchable. It
   is catchable: `ConfigureOptions` wraps the client in a `ConfigureOptionsChatClient` that `GetService`
   finds through the delegating chain. Mutation-checked in **both** directions — hardcoded `false` and
   hardcoded `true` — with a non-reasoning control.

4. **Nine capability-flag sites, not eight.** The plan named the grep as the authority and it earned its
   keep: `UserProviderResolver.cs:215` (`StoredModel` → `ConfiguredModel`) is a ninth site, and it is the
   one that carries the flag to client construction, so missing it would have left the feature dead for
   every user-added provider while every other test stayed green.

5. **Two extra tests beyond the AC table.** `ReasoningText_IsNotPersisted_OnTheBackgroundPath` (the
   `RunAsync` path persists too, and the spike showed the framework stores reasoning there as well) and
   `NoReasoningField_IsSent_WhenReasoningIsNotRequested` (without it, a body that always carried the field
   would satisfy the positive assertion).

6. **`FakeChatClient` emits reasoning AHEAD of its echo, not just ahead of the reply chunks.** The first
   placement put it after the `[sys:…]`/`[tools:…]` echo, which is itself a `TextDelta`, so the ordering
   assertion in AC3 failed. A reasoning model thinks before it emits any text, so the fix was to move the
   emission, not to weaken the assertion.

7. **A test-authoring trap worth carrying forward.** The negative assertions in `ReasoningAssetsTests` are
   tripped by the *comments* in the code under test, which name `voice.onDelta` and `innerHTML` in order to
   forbid them. They go through a `CodeOnly` helper that strips `//` comments first — otherwise the only
   way to make the guard pass is to delete the comment documenting the rule (the ADR 0065 shape).

8. **The browser pass caught a defect no assertion would have.** `▸`/`▾` are not in the mono font stack and
   fell back to a dot, so the expand affordance read as a bullet. Replaced with a CSS-border triangle
   rotated on `[open]`. Also re-learned in passing: the service worker caches `styles.css`, so a CSS change
   needs the SW unregistered (or a cache clear) before it is visible in an already-primed profile — the
   `CACHE` bump only helps a *new* client.

### Unchanged from the plan

Live-only, always-shown-collapsed, no Preferences toggle, no new turn-scoped field, no new `[Id]` on
`AgentStreamEvent` (reasoning reuses `Text`), no endpoint edit (the camelCase enum gives `"reasoning"` on
the wire, as predicted), no new JS module, no infra. The golden-fixture trap played out exactly as written:
`SupportsReasoning` stays `false` in `GoldenStates`, the comment moved from five bits to six, and a sixth
`Assert.Equal` was added against the frozen blob.
