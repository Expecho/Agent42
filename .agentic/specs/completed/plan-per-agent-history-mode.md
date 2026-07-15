# Plan — Per-agent history mode (continuous vs per-chat)

planned-against: main @ 5d3d9f1

## Overview

Give each stored agent a **history mode**: either "one history per chat" (today's
behaviour — every time you start a chat from the agent you get a fresh conversation with
its own history) or "continuous chat" (all chats started from the agent share **one**
ongoing conversation/history). The mode is a per-agent setting the user picks in the agent
editor. Default is per-chat, so nothing changes for existing agents.

Why now: the user asked for it. It's a small, self-contained addition that rides the
existing agent-definition + conversation-id machinery without new grains.

## Affected areas (verified against the tree @ 5d3d9f1)

- `src/Agent42.Agents/AgentDefinition.cs` — add a **trailing defaulted** `Continuous`
  field (`[property: Id(7)] bool Continuous = false`). **Id(7) is contended** — see the
  collision note under Context; the executor takes the next free id at execution time.
- `src/Agent42.Backend/AgentApi.cs` — add a trailing defaulted `bool Continuous = false`
  to `AgentUpsertRequest`; the POST/PUT `/api/agents` handlers pass `request.Continuous`
  into the `AgentDefinition` ctor.
- `src/Agent42.Backend/wwwroot/index.html` — a checkbox field in the agent form.
- `src/Agent42.Backend/wwwroot/app.js` — `openAgentDialog`, `saveAgent`, `startAgentChat`,
  and the element-ref block (`~app.js:30-33`).
- `src/Agent42.Backend/wwwroot/styles.css` — checkbox-field styling (none exists yet).
- Constructor call sites that must keep compiling (all nine — the trailing **default**
  is what keeps the seven that don't pass the flag intact, mirroring `ChatRequest`):
  `new AgentDefinition(...)` at `AgentApi.cs:204,213`, `AgentRegistryGrainTests.cs:29,72,74`;
  `new AgentUpsertRequest(...)` at `AgentApiTests.cs:163,175,195,243`.
- Tests: `tests/Agent42.Tests/AgentRegistryGrainTests.cs`,
  `tests/Agent42.Tests/AgentApiTests.cs`, and a new frontend source-heuristic asset test
  (`tests/Agent42.Tests/AgentEditorAssetsTests.cs`).
- Docs: new ADR `.agentic/decisions/0016-*.md`; `CLAUDE.md` repo-notes; a gotcha
  (unconditional — see the 2 MB consequence under Context).

## Context & constraints (what the executor can't cheaply re-derive)

- **Grain key is `{userId}/{conversationId}`** (`AgentApi.cs:247` `AgentKey`), so an
  `AgentGrain` **is** one conversation and its history is 1:1 with the conversation
  (`AgentGrainState.SerializedSession`, `AgentGrain.cs:96-99,553-555`). There is no shared
  history primitive — "continuous" must therefore be achieved by **routing multiple chat
  starts onto the same conversation id**, not by a new grain.

- **The client owns conversation ids today.** `startAgentChat` sets
  `activeId = crypto.randomUUID()` (`app.js:337`) and the backend simply keys a grain by
  whatever id arrives (`AgentApi.cs:26-29`). So the continuous case is: derive a
  **deterministic** conversation id from the agent — `` `agent-${agent.id}` `` — so every
  start collapses onto `{userId}/agent-{agentId}`. `crypto.randomUUID()` never emits the
  `agent-` prefix, so no collision with per-chat conversations.

- **Decision — the flag lives on `AgentDefinition` only, NOT on `AgentBinding`, and is
  read live at chat-start.** `AgentBinding` is the per-conversation persona snapshot that
  ADR 0003 makes immutable. History mode is a *routing* decision, not a persona trait that
  changes model behaviour, so snapshotting it would be wrong: keeping it off the binding
  lets a user flip an agent's mode for **future** chats while every existing conversation
  is untouched (consistent with ADR 0003's "editing an agent never disturbs an existing
  chat"). This is the load-bearing design call — record it in the ADR (Step 6).
  Evidence for immutability model: `.agentic/decisions/0003-*.md`, `AgentGrain.cs:310-348`.

- **No migration needed.** `Continuous` is a trailing field on `AgentDefinition`, so
  agents persisted before this change deserialize it as `false` = per-chat. Same
  no-migration pattern the repo relies on elsewhere (trailing Orleans ids). The property
  name/id caution in the `DeploymentName` gotcha does **not** apply here — we are *adding*
  a trailing field, not renaming one (a trailing addition deserializes to `false` under
  both the binary field-id and JSON property-name serializers).

- **Both new fields are trailing DEFAULTED params (`= false`)**, mirroring `ChatRequest`
  (`AgentApi.cs:254-258`: "All are trailing defaults so existing positional callers keep
  compiling"). This keeps all nine constructor call sites listed above compiling; only the
  sites that actually want the flag (the POST/PUT handlers and the new-behaviour tests)
  reference it. A non-defaulted param would break four otherwise-untouched call sites.

- **Id(7) is contended by a sibling active plan.** `.agentic/specs/plan-agent-run-endpoint.md`
  (Step 1, its line 34) also adds `[Id(7)] string ApiKeyHash` to `AgentDefinition`, and
  neither plan has landed (`AgentDefinition.cs` still ends at `[Id(6)]`). Whichever executes
  **second** must take the next free id. At execution time, re-check the record and use
  `[Id(7)]` if free, else `[Id(8)]`. Do not assume 7.

- **Continuous mode makes the 2 MB session cap a designed-in failure mode.** A continuous
  agent funnels every chat into ONE grain forever, and that grain re-serializes its whole
  session into a single `agent-state` Cosmos item every turn, hard-capped at 2 MB (ADR 0004;
  see the "an agent tool's RETURN value is persisted" gotcha, `gotchas.md:106-122`). Per-chat
  mode bounds each conversation; continuous mode grows without bound (text + tool results +
  image refs + RAG excerpts, all replayed every turn) and will eventually fail
  `WriteStateAsync` *after* the model has answered — bricking the conversation, silently, in
  prod. **The escape hatch is delete-to-reset:** `DELETE /api/conversations/agent-{agentId}`
  purges the grain, and the next start recreates it fresh at the same id. This consequence
  MUST be recorded in ADR 0016 (Step 7) and as a gotcha (Step 8); no code mitigation is in
  scope beyond documenting the escape hatch.

- **Continuous mode disables ADR 0003's only escape hatch for persona edits.** Binding is
  immutable once set (`AgentGrain.cs:314`), and ADR 0003's remedy for a wanted persona edit
  is "start a new chat from the updated agent" (`0003-*.md:39-40`). For a continuous agent
  that is impossible — every start collapses onto the one already-bound grain — so editing a
  continuous agent's system prompt or model has **zero effect** on its ongoing conversation
  until it is deleted. Reading the flag live (not snapshotting it) does NOT itself conflict
  with ADR 0003, but this consequence must be spelled out in ADR 0016.

- **Binding is idempotent** (`AgentGrain.BindAgentAsync` no-ops once `AgentId` is set,
  `AgentGrain.cs:314`). So re-entering a continuous chat and re-sending `agentId` on a
  first turn is harmless. `pendingAgentId` is only sent on the first turn of a freshly
  started agent chat (`app.js:339,996`).

- **Resume semantics for continuous.** Clicking a continuous agent must **load the single
  ongoing conversation**, not show a blank thread over a grain that already has history.
  So `startAgentChat` must, when the deterministic id already appears in the loaded
  `conversations` list, delegate to `selectConversation(convId)` (which fetches
  `/messages` + `/model` + `/agent` and locks the picker — `app.js:693-734`). Only the
  first-ever start sets up the empty-thread state.

- **Frontend tests are source-text heuristics** (no JS runner — see
  `ModelPickerAssetsTests`/`LocationAssetsTests` headers). They prove the code *says* the
  right thing. Exercise the real toggle by hand in DevHost.

## Steps

1. **`AgentDefinition.Continuous` field.** Add `[property: Id(7)] bool Continuous = false`
   as the trailing parameter of the `AgentDefinition` record (`AgentDefinition.cs`) — but
   **re-check the next free id first** (Id(7) is contended by the sibling plan; use Id(8) if
   `ApiKeyHash` already landed). Trailing **default** so the seven call sites that don't pass
   it keep compiling. Update the xml-doc to mention the history-mode flag.
   *Verify:* `dotnet build Agent42.slnx`.

2. **API round-trip.** In `AgentApi.cs`: add `bool Continuous = false` (trailing default) to
   `AgentUpsertRequest`; pass `request.Continuous` into the `AgentDefinition` ctor in both
   the POST and PUT handlers.
   *Verify:* `dotnet build Agent42.slnx`, then the AC2 test below.
   *(Steps 1–2 are one backend unit; do them together. The defaults mean no other call site
   needs touching — the build confirms it.)*

3. **Registry round-trip test (AC1).** In `AgentRegistryGrainTests.cs`, extend
   `AgentRegistry_CrudRoundTrips_AndSurvivesReactivation` (or add a focused test) to set
   `Continuous: true` (named arg) on create and assert it survives get/update/reactivation.
   *Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~AgentRegistryGrainTests`.

4. **API test (AC2 + AC3).** In `AgentApiTests.cs`, extend `AgentsEndpoints_CrudRoundTrip_AsJson`
   (or add a test) to POST an agent with `continuous: true`, assert the created/listed JSON
   carries it, and PUT it back to `false` and assert the change. **AC3:** also POST a payload
   that OMITS `continuous` (an old-shape client) and assert the stored value is `false` — this
   is the real "old data defaults to per-chat" property, and it belongs at the API/JSON layer,
   not the grain ctor.
   *Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~AgentApiTests`.
   *(Steps 3 and 4 are independent of each other — parallelizable.)*

5. **Frontend.** In `index.html` add a checkbox field to the agent form (follow the
   `agent-form__field` / `agent-form__label` shape, id `agentContinuous`, plus a one-line
   `agent-form__note` explaining "all chats from this agent share one history"). In
   `app.js`:
   - `openAgentDialog`: `agentContinuousInput.checked = agent?.continuous ?? false;`
   - `saveAgent`: add `continuous: agentContinuousInput.checked` to the payload.
   - `startAgentChat`: compute
     `` const convId = agent.continuous ? `agent-${agent.id}` : crypto.randomUUID(); ``
     If `agent.continuous` **and** `conversations.some(c => c.id === convId)` →
     `selectConversation(convId); return;` (resume the ongoing history). Otherwise proceed
     as today but with `activeId = convId`.
   - Wire the new element ref alongside the existing agent-form refs.
   Add matching checkbox CSS to `styles.css` (none exists — keep it consistent with the
   form's monospace aesthetic).
   *Verify:* `dotnet build Agent42.slnx` (asset tests serve real wwwroot), then AC4/AC5
   test below; hand-check in DevHost (`dotnet run --project tools/Agent42.DevHost`).

6. **Frontend asset test (AC4/AC5).** Add `tests/Agent42.Tests/AgentEditorAssetsTests.cs`
   (collection `"StaticAssets"`, shape per `ModelPickerAssetsTests`): assert `index.html`
   contains the `agentContinuous` checkbox; assert `app.js` reads `agent.continuous` in
   `openAgentDialog`, includes `continuous:` in the `saveAgent` payload, that `startAgentChat`
   contains the `` `agent-${ `` deterministic-id derivation, **and that its continuous branch
   references `selectConversation`** (the resume-existing-history delegation — the anti-blank-
   thread logic, otherwise proven only by the DevHost hand-check). A future edit dropping the
   resume delegation must fail here.
   *Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~AgentEditorAssetsTests`.

7. **ADR (`/decide`).** Record ADR 0016 "Per-agent history mode via deterministic
   conversation id": the decision, the two alternatives rejected —
   (a) snapshot the flag into `AgentBinding` (rejected: it's routing, not persona; would
   collide with ADR 0003 immutability and freeze the mode per-conversation), and
   (b) a backend endpoint that owns continuous routing (rejected: the client already owns
   conversation ids; a deterministic id is the minimal change and per-user grain scoping
   means it's not a security boundary either way) — **and the two consequences**: (i)
   continuous mode makes the 2 MB session cap a designed-in failure mode with delete-to-reset
   as the only escape hatch, and (ii) it disables ADR 0003's persona-edit escape hatch (edits
   to a continuous agent never reach its ongoing conversation). Cross-reference ADR 0003 and
   ADR 0004.

8. **Docs (`/sync-docs`).** Add a repo-note bullet to `CLAUDE.md` (agent history mode, the
   deterministic-id mechanism, flag-not-snapshotted, the 2 MB consequence). Add a `gotcha`
   entry (unconditional) capturing the non-obvious trap: **a continuous agent funnels every
   chat into one unbounded grain, so it will eventually hit the 2 MB session cap and brick —
   delete-to-reset is the escape hatch — and its flag is read client-side, never snapshotted
   into the binding, so persona edits to it are inert.** Run `dotnet test` + `dotnet format
   Agent42.slnx --verify-no-changes` before reporting done.

## Acceptance criteria → test mapping

| AC | Criterion | Test | File |
|----|-----------|------|------|
| AC1 | `AgentDefinition.Continuous` persists through create/update/get and survives grain reactivation; defaults to `false` | `AgentRegistry_CrudRoundTrips_AndSurvivesReactivation` (extended) | `tests/Agent42.Tests/AgentRegistryGrainTests.cs` |
| AC2 | POST/PUT `/api/agents` accept and echo `continuous`; GET returns it | `AgentsEndpoints_CrudRoundTrip_AsJson` (extended) | `tests/Agent42.Tests/AgentApiTests.cs` |
| AC3 | A POST that OMITS `continuous` (old-shape client) stores `false` — old data defaults to per-chat | added to `AgentsEndpoints_CrudRoundTrip_AsJson` (API/JSON layer) | `tests/Agent42.Tests/AgentApiTests.cs` |
| AC4 | Agent editor exposes the continuous checkbox; `saveAgent` sends it; `openAgentDialog` reads it | `AgentEditor_HasContinuousToggle_AndRoundTripsIt` | `tests/Agent42.Tests/AgentEditorAssetsTests.cs` (new) |
| AC5 | `startAgentChat` derives a deterministic `agent-`-prefixed id when continuous (random UUID otherwise) AND resumes existing history via `selectConversation` | `AgentEditor_ContinuousRoutesToDeterministicConversationId_AndResumes` | `tests/Agent42.Tests/AgentEditorAssetsTests.cs` (new) |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` @ 5d3d9f1)

- `new_issues: 0`
- `new_security_issues: 0`
- `duplication_pct_max: 3`
- `coverage_new_code_pct_min: 80`
- `cognitive_complexity_max: 15`
- `vulnerable_packages: 0`
- Branch: work on a `feature/*` branch + MR (never commit to `main`). Tests written
  alongside each step, not deferred. `dotnet format --verify-no-changes` clean.

## Out of scope

- **Backend enforcement of continuity.** Routing stays client-side (deterministic id),
  consistent with the client already owning conversation ids. It is not a security
  boundary: the grain key is per-user, so a hand-crafted client can only affect its own
  conversations.
- **Merging existing per-chat histories** into a continuous one when the flag is flipped.
  Flipping only changes where *future* starts route; existing conversations are untouched.
- **Any "continuous" badge** on the conversation list or agent list (optional polish).
- **Normal (non-agent) chats** — unchanged; they have no agent definition to carry a flag.
- **Renaming/re-typing `AgentBinding` or `AgentGrainState`** — no changes there at all.

## Review

A `critic` subagent reviewed this plan against the repo's ADRs, gotchas, and code.
Verdict: mechanism sound; six findings accepted, one nit rejected.

**Accepted & folded in:**
- **2 MB session cap is a designed-in failure mode for continuous mode** — continuous funnels
  every chat into one unbounded grain. Added to Context, ADR step 7, and an unconditional
  gotcha (step 8); delete-to-reset documented as the escape hatch.
- **`[Id(7)]` collides with the active sibling plan `plan-agent-run-endpoint.md`** (also adds
  `[Id(7)] ApiKeyHash`; neither has landed). Step 1 now re-checks the next free id at
  execution time (use Id(8) if `ApiKeyHash` landed first).
- **Both new fields must be trailing DEFAULTED params** (`= false`), mirroring `ChatRequest`,
  or four otherwise-untouched positional call sites break. All nine call sites now enumerated
  in Affected areas; steps 1–2 updated.
- **AC3 was vacuous as mapped** (asserting `false==false` in a grain ctor test) — restated as
  an API-layer test (POST omitting `continuous` → `false`) and remapped to `AgentApiTests`.
- **The continuous resume path was untested** — AC5's test now also asserts `startAgentChat`
  references `selectConversation` in its continuous branch.
- **Continuous mode disables ADR 0003's persona-edit escape hatch** (edits never reach the
  ongoing conversation) — added as an explicit ADR 0016 consequence.

**Rejected:**
- *"`planned-against: 5d3d9f1` is stale / HEAD is ahead."* Verified false: `git rev-parse
  --short HEAD` is `5d3d9f1` (ADR 0015 landed **in** that commit via the branch's pull), and
  `AgentDefinition.cs` still ends at `[Id(6)]`. The base is current; the nit's premise is wrong.

## Implementation notes (divergence from plan)

Executed on `feature/per-agent-history-mode` from `main @ 5d3d9f1`. The plan was followed as
written; no structural divergences. Points worth recording:

- **`[Id(7)]` was free at execution time.** The contended sibling plan `plan-agent-run-endpoint.md`
  had not landed (`AgentDefinition.cs` still ended at `[Id(6)]`), so `Continuous` took `[Id(7)]` as
  the primary path — no fallback to `[Id(8)]` was needed. **Whichever of the two plans lands second
  must renumber**; this MR claims `[Id(7)]`.
- **AC1 layer note (from the critic, accepted as-is).** AC1's "defaults to `false`" sub-clause is
  proven at the **API/JSON layer** by AC3 (`AgentApiTests` — an old-shape POST omitting `continuous`
  stores `false`), not in the grain-ctor test, which exercises only the `true` round-trip. This
  matches the plan's own AC3 remap; no coverage hole in aggregate, so no extra grain-layer assertion
  was added.
- **Extra verification beyond the source-heuristic asset tests.** Because the frontend tests only
  prove the code *says* the right thing (no JS runner), the flag was additionally exercised
  **end-to-end against a live DevHost**: POST `continuous:true` persists and echoes true, GET carries
  it, an old-shape POST omitting the field stores `false`, and the served `app.js` contains the
  `agent-${agent.id}` derivation + `selectConversation(convId)` resume + save-payload wiring.
- **Quality gate:** 260/260 tests pass; `dotnet format --verify-no-changes` exit 0; coverlet line
  coverage on changed backend files `AgentApi.cs` 98.9%, `AgentDefinition.cs` 100%. Duplication and
  cognitive-complexity gates SKIPPED (no measuring tool in the repo — self-reported only per
  `quality-gates.yaml`).
