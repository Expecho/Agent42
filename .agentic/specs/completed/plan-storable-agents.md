# Plan — Storable, selectable agents (side-by-side with chats)

planned-against: main @ 0360448

## Overview

Today the app hosts a single hard-coded persona ("Agent 42": fixed `Instructions`
constant, fixed `Name`, per-conversation model picker). This change adds **stored
agents** — first-class, user-defined personas with a name, description, model, and
system prompt — that live **side by side** with the existing chats:

- The left sidebar keeps the current **chats** list, working exactly as today
  (default persona, model picker).
- A new **agents** section lists the user's stored agents with full create / edit /
  delete.
- Clicking an agent **always starts a new chat** seeded with that agent's model +
  system prompt. In an agent chat the model is **locked** to the agent's model
  (no picker). That chat then lives in the normal chats list and resumes with its
  persona intact.

Why now: the user wants to define reusable personas rather than re-typing a system
prompt and re-picking a model for every conversation.

## Design decisions (from the interview)

1. **Side-by-side, not a replacement.** Chats and agents are two sidebar sections.
   An agent interaction is *always a new chat* (a normal conversation whose persona
   happens to come from an agent). The existing conversation/history/delete/drawer
   machinery is untouched.
2. **Full CRUD** for agents in this change (create / edit / delete via a form).
3. **Model is locked to the agent** inside an agent chat — hide the composer's model
   picker; normal chats keep it.
4. **Persona is snapshotted into the conversation at bind time** (not referenced
   live). Editing or deleting an agent later never changes an existing chat. → ADR
   (Step 6). Alternative rejected: live reference (an edit/delete would retroactively
   mutate or break past conversations).
5. **Agents are per-user**, keyed by `http.GetUserId()`, consistent with every other
   grain (`ConversationIndexGrain`, `AgentGrain`). No sharing mechanism exists in the
   app (Easy Auth, two users, per-user isolation everywhere).
6. **Reuse the `agent-state` storage provider / Cosmos container** for the registry
   grain — Orleans partitions by grain type, so no new container is needed. This
   deliberately avoids the "Cosmos containers must be pre-declared in Bicep" gotcha
   by not introducing one.

## Context & constraints (what the executor can't cheaply re-derive)

- **Registry-grain exemplar:** `src/Agent42.Agents/ConversationIndexGrain.cs` is the
  canonical per-user registry grain — `[PersistentState("conversations",
  AgentGrain.StorageProviderName)]`, a `[GenerateSerializer]` state class holding a
  `List<>`, `[Alias(...)]` on the interface + methods, `Results`-free plain grain
  methods. Copy its shape for the agent registry; do **not** invent structure.
- **Grain/DTO exemplar:** `ConversationInfo` in
  `src/Agent42.Agents/IConversationIndexGrain.cs` shows the serializable record shape
  (`[GenerateSerializer] [Alias(...)] record X([property: Id(0)] ...)`).
- **Minimal-API exemplar:** `src/Agent42.Backend/AgentApi.cs`. Note the gotcha it
  already encodes: a handler returning a bare `string` serves `text/plain`, which the
  JS `response.json()` throws on — wrap bare strings in `Results.Json(...)`. Any new
  endpoint that returns a scalar must do the same. Endpoints resolve `userId` via
  `http.GetUserId()` (`CurrentUser.cs`) and never trust client-supplied identity.
- **Frontend exemplar:** `src/Agent42.Backend/wwwroot/app.js` (vanilla JS IIFE, no
  framework/build) + `index.html` + `styles.css`. There is **no modal/dialog infra
  today** (only `confirm()`); the agent form is net-new UI. Match the existing warm
  amber/mono aesthetic (`styles.css` `:root` tokens).
- **Agent construction:** `AgentGrain.BuildAgent` builds a `ChatClientAgent` with
  `ChatClientAgentOptions { Name = "Agent42", ChatOptions = { Instructions = <const>,
  Tools = [...] } }`. The model comes from `_chatClientFactory.GetClient(deployment)`.
  Persona binding means driving `Name` and `Instructions` from state instead of
  consts (falling back to the consts when unbound).
- **Model already lives in state:** `AgentGrainState.DeploymentName` is already the
  per-conversation model and is hot-swapped in `ChatAsync`. Binding an agent sets it
  the same way; because the picker is hidden in agent chats, it just never changes.
- **State is additive-compatible:** adding new `[Id(n)]` properties to
  `AgentGrainState` deserializes old Cosmos state with nulls → existing conversations
  fall back to the default persona. Safe.
- **Do NOT change `ChatAsync`'s signature.** Add an idempotent `BindAgentAsync` +
  `GetAgentBindingAsync` instead — this keeps every existing caller (AgentApi + both
  test files) untouched and the diff small.
- **Fake clients ignore the system prompt** (`tests/Agent42.Tests/FakeChatClient.cs`,
  `tools/Agent42.DevHost/CannedChatClient.cs`) — they return canned text. To *test*
  that a bound system prompt reaches the model, the fake must echo the instructions
  it receives. **Unverified library behavior:** whether Agent Framework 1.13 surfaces
  `ChatOptions.Instructions` to the terminal `IChatClient` as `options.Instructions`
  or as a prepended system-role `ChatMessage`. **Resolve this FIRST** (Step 2a) with a
  `spike-dotnet` that logs what the inner `IChatClient` receives — do not write AC3's
  test blind. Then have the fake echo whichever source actually carries it (check both
  for robustness) and assert delivery. Only if the spike proves the instructions are
  genuinely unobservable at the terminal client does AC3 get reclassified as
  spike/manual-verified — it must **not** silently degrade into a second copy of AC2
  (`GetAgentBindingAsync` only proves storage, not delivery; critic MEDIUM).
- **Governing docs:** ADR 0001 (grain-per-conversation) — this change is *additive*
  (a new registry grain + persona fields), not a supersession, so no `/decide`
  against 0001. `.agentic/gotchas.md`: the `text/plain` and "pre-declare Cosmos
  containers" entries both apply and are honored above.

## Affected areas (verified against the tree)

New files:
- `src/Agent42.Agents/AgentDefinition.cs` — stored-agent record.
- `src/Agent42.Agents/IAgentRegistryGrain.cs` — per-user registry interface.
- `src/Agent42.Agents/AgentRegistryGrain.cs` — registry grain + state.
- `src/Agent42.Agents/AgentBinding.cs` — persona snapshot DTO (bind input + get output).
- `tests/Agent42.Tests/AgentRegistryGrainTests.cs`.

Modified files:
- `src/Agent42.Agents/AgentGrainState.cs` — add `AgentId`, `AgentName`, `SystemPrompt`.
- `src/Agent42.Agents/IAgentGrain.cs` — add `BindAgentAsync`, `GetAgentBindingAsync`.
- `src/Agent42.Agents/AgentGrain.cs` — persona-aware `BuildAgent`; implement the two
  new methods.
- `src/Agent42.Backend/AgentApi.cs` — agents CRUD endpoints; bind on chat when
  `agentId` present; `GET /api/conversations/{id}/agent`; extend `ChatRequest`.
- `src/Agent42.Backend/wwwroot/index.html` — agents sidebar section + agent form dialog.
- `src/Agent42.Backend/wwwroot/app.js` — agents load/render/CRUD, start-agent-chat,
  picker lock, resume-time binding read.
- `src/Agent42.Backend/wwwroot/styles.css` — agents list + dialog styling.
- `tests/Agent42.Tests/FakeChatClient.cs` — echo received instructions.
- `tests/Agent42.Tests/AgentGrainTests.cs` — binding tests.
- `tests/Agent42.Tests/AgentApiTests.cs` — CRUD + bind endpoint tests.
- `.agentic/decisions/0003-*.md` — snapshot-persona ADR (Step 6).

Out of scope (see below) — no changes to: Bicep/infra, `Program.cs` wiring (registry
grain needs none — it rides the existing provider), `ConversationInfo`/sidebar chat
badges, `CannedChatClient` echo (DevHost is manual-verify only, optional).

## Steps

### Step 1 — Agent registry domain (grain, definition, state) — *parallelizable with Step 2*
Files: `AgentDefinition.cs`, `IAgentRegistryGrain.cs`, `AgentRegistryGrain.cs` (new).
- `AgentDefinition(Id, Name, Description, Model, SystemPrompt, CreatedAt, UpdatedAt)`
  as a `[GenerateSerializer] [Alias]` record (copy `ConversationInfo`'s shape).
- `IAgentRegistryGrain : IGrainWithStringKey` (keyed by userId): `GetAllAsync()`,
  `GetAsync(string id)`, `SaveAsync(AgentDefinition)` (upsert — insert if `Id` empty/
  unknown, else replace), `DeleteAsync(string id)`. `[Alias]` interface + methods.
- `AgentRegistryGrain` + `AgentRegistryState { List<AgentDefinition> Agents }`,
  `[PersistentState("agents", AgentGrain.StorageProviderName)]`. Assign `Id`
  (`Guid.NewGuid().ToString()`) and timestamps in `SaveAsync` when creating.
- Copy `ConversationIndexGrain` structure exactly.
Verify: `AgentRegistryGrainTests` (Step written alongside) — create→list→get→update→
delete round-trip + survives `DeactivateOnIdle`. `dotnet test Agent42.slnx --filter
FullyQualifiedName~AgentRegistryGrainTests`.

### Step 2 — Conversation-grain persona binding — *parallelizable with Step 1*
Files: `AgentBinding.cs` (new), `AgentGrainState.cs`, `IAgentGrain.cs`, `AgentGrain.cs`,
`tests/.../FakeChatClient.cs`, `tests/.../AgentGrainTests.cs`.
- **Step 2a (do first):** `spike-dotnet` — build a tiny `ChatClientAgent` around a
  probe `IChatClient` with `ChatOptions.Instructions = "MARKER"` and log whether the
  probe sees the marker in `options.Instructions` or as a system-role `ChatMessage`.
  This decides how `FakeChatClient` echoes and whether AC3 is a real delivery test or
  spike/manual-verified. Pin the spike to the repo's Agent Framework version.
- `AgentBinding(AgentId, Name, SystemPrompt, Model)` — `[GenerateSerializer] [Alias]`
  record. Used as both `BindAgentAsync` input and `GetAgentBindingAsync` output.
- `AgentGrainState`: add `[Id(3)] AgentId`, `[Id(4)] AgentName`, `[Id(5)] SystemPrompt`
  (all `string?`).
- `IAgentGrain`: add `[Alias] Task BindAgentAsync(AgentBinding binding)` and
  `[Alias] Task<AgentBinding?> GetAgentBindingAsync()`. **Leave `ChatAsync` unchanged.**
- `AgentGrain`:
  - `BuildAgent`: use `_state.State.AgentName ?? "Agent42"` for `Name` and
    `_state.State.SystemPrompt ?? Instructions` for `ChatOptions.Instructions`.
  - `BindAgentAsync`: **idempotent** — if `_state.State.AgentId` is already set, no-op.
    Else pin `AgentId/AgentName/SystemPrompt/DeploymentName`, set `_deployment`, then
    **mirror `ChatAsync`'s model-switch shape** (`AgentGrain.cs:126–130`): snapshot →
    `_agent = BuildAgent(...)` → restore the session, then `await
    _state.WriteStateAsync()`. Bind always runs on a brand-new empty session, but
    copying the exemplar's snapshot/restore keeps the rebuild path uniform and avoids
    relying on an empty session being portable across agent instances (critic LOW).
  - `GetAgentBindingAsync`: return an `AgentBinding` from state when `AgentId` is set,
    else `null`.
- `FakeChatClient`: when the received `options?.Instructions` **or** a system-role
  message in `messages` is non-empty, prepend a stable marker (e.g.
  `"[sys:<instructions>] "`) to the streamed reply, so a test can assert the persona
  reached the model. Keep replies non-empty (existing tests only assert non-empty /
  substring "hi"/tool names — do not break them).
Verify: `AgentGrainTests` new cases — (a) after `BindAgentAsync`, `GetAgentBindingAsync`
returns the pinned name/model/prompt and `GetSelectedModelAsync` returns the agent's
model; (b) binding survives `DeactivateOnIdle`; (c) an agent chat's reply reflects the
bound system prompt (via the fake echo); (d) `BindAgentAsync` called twice keeps the
first persona. Existing AgentGrainTests still green. `dotnet test Agent42.slnx --filter
FullyQualifiedName~AgentGrainTests`.

### Step 3 — Backend API (agents CRUD + chat bind + conversation-agent endpoint)
Depends on Steps 1 & 2. Files: `AgentApi.cs`.
- `GET /api/agents` → `IAgentRegistryGrain(userId).GetAllAsync()`.
- `POST /api/agents` (body `AgentUpsertRequest(Name, Description, Model, SystemPrompt)`)
  → `SaveAsync` a new definition → return created (as JSON).
- `PUT /api/agents/{id}` → `SaveAsync` with that `Id` → return updated.
- `DELETE /api/agents/{id}` → `DeleteAsync` → `Results.NoContent()`.
- Extend `ChatRequest` to `record ChatRequest(string Message, string? Model, string?
  AgentId = null)` — the **defaulted** third arg keeps existing positional callers
  (`new ChatRequest("hi", null)` in `AgentApiTests.cs:69,90`) compiling.
- In the chat handler: if `AgentId` is non-empty, resolve the definition via the
  registry. **Null-guard it** — if `GetAsync(agentId)` returns `null` (the agent was
  deleted since the chat started), **skip binding** and proceed to `ChatAsync`; the
  grain's persona is already snapshotted in its own state, so a deleted definition must
  never 500 a resumed turn (design decision #4). When the definition is found, `await
  agent.BindAgentAsync(new AgentBinding(def.Id, def.Name, def.SystemPrompt, def.Model))`
  **before** `ChatAsync` (idempotent — no-ops if already bound). Then call
  `ChatAsync(message, null)` (model null → keep the pinned agent model). Non-agent chats
  behave exactly as today.
- **The frontend only sends `agentId` on the first message of a freshly-started agent
  chat, never on resumes** (see Step 4). This null-guard is defense-in-depth for the
  race where an agent is deleted between start and first send.
- `GET /api/conversations/{id}/agent` → `Results.Json(await grain.GetAgentBindingAsync())`
  (JSON `null` when unbound — honors the text/plain gotcha).
Verify: `AgentApiTests` new cases — CRUD round-trip over HTTP; a chat POST with
`agentId` makes `GET .../agent` return the binding and `GET .../model` return the
agent's model; a normal chat's `GET .../agent` is `null`; responses are
`application/json`; **and the AC7 regression** — start an agent chat, `DELETE` the
agent, then POST another turn with that `agentId` → the turn still succeeds and the
binding is unchanged (no 500). `dotnet test Agent42.slnx --filter
FullyQualifiedName~AgentApiTests`.

### Step 4 — Frontend (agents section, CRUD form, start-agent-chat, locked picker)
Depends on Step 3. Files: `index.html`, `app.js`, `styles.css`.
- `index.html`: add an **AGENTS** `<nav>` section in the sidebar (mirror the
  `.conversations` block) with a `+` add button and an empty `<ul id="agentList">`; add
  a `<dialog id="agentDialog">` form (name, description, model `<select
  id="agentModelSelect">`, system-prompt `<textarea>`, save/cancel).
- `app.js`:
  - `loadAgents()` / `renderAgents()` — `GET /api/agents`; each row is a click-to-chat
    button plus ✎ (edit) and 🗑 (delete) controls (reuse the `.conv__delete` pattern +
    `confirm()`).
  - Agent form: open blank (create) or pre-filled (edit); populate the form's model
    `<select>` from the same `/api/models` data as the composer; `POST`/`PUT` on save;
    refresh the list.
  - Track **two** pieces of agent state, deliberately distinct: `activeAgentId` (drives
    the picker lock + label; set whenever the current chat is an agent chat, including
    resumes) and `pendingAgentId` (the agent to *bind* — set only when a fresh agent
    chat is started, cleared after the first successful send). Only `pendingAgentId`
    goes on the wire. This is what prevents a resumed chat from re-resolving a possibly-
    deleted agent (critic HIGH).
  - `startAgentChat(agent)`: new `activeId = crypto.randomUUID()`; set `activeAgentId =
    pendingAgentId = agent.id`; snapshot the agent's model/name locally for the label;
    clear thread, show empty state, **hide the composer model picker** and show a static
    "agent · model" label.
  - `send`: include `agentId: pendingAgentId` (null for normal chats and for resumed
    agent chats). Clear `pendingAgentId` after the send resolves.
  - `selectConversation(id)`: fetch `GET /api/conversations/{id}/agent` **alongside**
    the existing messages+model fetches, but — honoring the text/plain gotcha's ordering
    lesson — parse it and lock the picker **after** the history render and `setModel`
    (mirror the position of the `setModel` call at `app.js:285`), so a failure here can
    never hide the conversation. If non-null: set `activeAgentId`, leave `pendingAgentId`
    null (already bound — do not rebind), lock the picker + show the label. If null:
    clear `activeAgentId`, show the normal picker.
  - `startNewConversation()` / new-chat button: clear `activeAgentId` and
    `pendingAgentId`, restore picker.
- `styles.css`: style the agents list, row controls, and the dialog to match the warm
  amber/mono theme.
Verify (this is UI — verify by running the app, per the repo's `/run` + `/verify`
skills, not just a build): `dotnet run --project tools/Agent42.DevHost`, then in the
browser — create an agent, edit it, delete one; click an agent → new chat with the
picker hidden/locked; send a message and confirm the canned reply streams; reopen the
agent chat from the chats list and confirm it's still locked/labelled; confirm a normal
"New conversation" still shows the picker and default persona. Screenshot the agents
section + a locked agent chat. `dotnet build Agent42.slnx` must also be clean.

### Step 5 — Full gate pass
Run `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format
Agent42.slnx --verify-no-changes`. Fix anything red.

### Step 6 — Docs (ADR + doc-debt sweep)
- `/decide`: record ADR 0003 — "Snapshot agent config into the conversation at bind
  time" (context, decision, alternative = live reference, consequences: edits/deletes
  don't disturb existing chats; a chat is self-contained). 
- `/sync-docs` over the diff: refresh any touched exemplar `verified:` dates in
  `.agentic/gotchas.md`; add a gotcha only if a genuine trap surfaced (candidate: the
  fake-client-must-echo-instructions detail, if non-obvious). No gotcha if nothing
  qualifies. `execute-plan` moves this spec to `specs/completed/`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | A user can create, list, update, and delete stored agents (name, description, model, system prompt) that persist across grain deactivation | `AgentRegistry_CrudRoundTrips_AndSurvivesReactivation` | `tests/Agent42.Tests/AgentRegistryGrainTests.cs` |
| AC2 | Binding an agent pins its persona + model into the conversation and survives reactivation | `Binding_PinsPersonaAndModel_SurvivesReactivation` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC3 | The bound system prompt actually reaches the model in an agent chat | `AgentChat_AppliesBoundSystemPrompt` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC4 | Binding is immutable once set (persona can't be flipped mid-conversation) | `BindAgent_IsIdempotent_KeepsFirstPersona` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC5 | Agents CRUD is reachable over HTTP and returns JSON | `AgentsEndpoints_CrudRoundTrip_AsJson` | `tests/Agent42.Tests/AgentApiTests.cs` |
| AC6 | Starting a chat with `agentId` binds it; the conversation reports its agent + the agent's model; a normal chat reports no agent | `ChatWithAgentId_BindsAgent_AndReportsIt` + `NormalChat_HasNoAgentBinding` | `tests/Agent42.Tests/AgentApiTests.cs` |
| AC7 | Resuming an agent chat after its agent is **deleted** still replies with the pinned persona and does not error (snapshot decision #4; critic HIGH) | `ChatAfterAgentDeleted_StillUsesPinnedPersona_NoError` | `tests/Agent42.Tests/AgentApiTests.cs` |
| AC8 | Agents appear in the sidebar; clicking one starts a locked-model chat; the picker returns for normal / resumed-normal chats | Manual run-app verification (Step 4) — no JS test harness in repo | `wwwroot/*` |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` @ plan time)

- new_issues: 0
- new_security_issues: 0
- duplication_pct_max: 3
- coverage_new_code_pct_min: 80
- cognitive_complexity_max: 15
- vulnerable_packages: 0 (hard block)
- Branch policy: never commit to `main` (protected) — work on `feature/storable-agents`
  + MR. Gates: build, `dotnet test`, `dotnet format --verify-no-changes`, no vulnerable
  packages, doc-path integrity.
- New dependencies: none expected. If any are needed, resolve latest-stable via
  `dotnet add package` and pin in `Directory.Packages.props` (never a version from memory).

## Review (critic pass — fresh context)

A `critic` subagent reviewed this spec against the real tree. Verdict: well-grounded;
Cosmos-container reuse, the text/plain handling, additive serialization, ADR-0001
additivity, and server-enforced model lock all confirmed clean. Findings and handling:

- **HIGH — resumed agent chat re-resolves a deleted agent → NRE.** Accepted & fixed.
  Frontend now sends `agentId` only on the first message of a *freshly started* agent
  chat (`pendingAgentId`), never on resumes; the backend null-guards a missing
  definition and skips binding. New regression **AC7** covers it.
- **MEDIUM — AC3's fallback silently degrades into AC2 (storage, not delivery).**
  Accepted & fixed. Added **Step 2a** (spike first to pin how Instructions reach the
  terminal client); AC3 stays a real delivery assertion or is explicitly reclassified —
  it may not collapse into `GetAgentBindingAsync`.
- **LOW — `ChatRequest` positional change breaks existing callers.** Accepted & fixed:
  `string? AgentId = null` default.
- **LOW — `BindAgentAsync` rebuilds `_agent` without the session snapshot/restore
  `ChatAsync` uses.** Accepted & fixed: Step 2 now mirrors the exemplar's
  snapshot/rebuild/restore shape.
- **LOW — new `/agent` fetch must not repeat the text/plain "hidden history" trap.**
  Accepted & fixed: Step 4 pins the parse/lock *after* history render + `setModel`.

No findings were rejected.

## Implementation divergences (filled in at execution time)

Where the build departed from the plan, and why:

1. **AC3 assertion loosened (still a real delivery test).** Step 2a's spike confirmed AF
   1.13 delivers `Instructions` as `options.Instructions`. But the `AgentSkillsProvider`
   *appends* its skill catalog to those instructions, so the fake's `[sys:{persona}]` echo
   isn't an exact bracketed match. `AgentChat_AppliesBoundSystemPrompt` asserts the persona
   is *contained* in the `[sys:…]` echo — still a genuine model-delivery assertion, not AC2's
   storage check. New gotcha captured.
2. **`/agent` endpoint serializes explicitly, not via `Results.Json`.** `Results.Json(null)`
   sends an empty 200 with no `Content-Type`, re-tripping the text/plain `response.json()`
   trap for a normal (unbound) chat. Used
   `Results.Text(JsonSerializer.Serialize(binding, JsonSerializerOptions.Web), "application/json")`
   so an unbound conversation returns a real JSON `null`. Added as a corollary to the
   text/plain gotcha; regression covered by `NormalChat_HasNoAgentBinding`.
3. **New CSS rule `[hidden] { display: none !important; }`.** Found during run-app
   verification: `.model { display: inline-flex }` (a class selector) beat the UA `[hidden]`
   rule on a specificity tie, so `modelField.hidden = true` silently failed to hide the model
   picker in an agent chat. Not anticipated in the plan.
4. **`send()` sends `model: null` for *any* agent chat, not only fresh ones.** In addition to
   gating `agentId` on `pendingAgentId`, the wire model is nulled whenever `activeAgentId` is
   set, so a *resumed* agent chat can't send a stale (hidden) picker value that would switch
   the pinned model. Strengthens the plan's pending/active split.
5. **Extra tests beyond the AC→test table:** `AgentRegistry_ListsMostRecentlyUpdatedFirst`
   (registry ordering) and `NormalChat_HasNoBinding` (grain-level unbound check). Added a
   "Per-user registry grain" exemplar row to `gotchas.md`.

## Out of scope

- Sharing agents between users (agents are per-user).
- A visual "which agent" badge on chat rows in the sidebar (`ConversationInfo`
  unchanged) — agent chats are still fully functional and labelled inside the chat.
- Seeding a built-in default agent — normal chats already provide the default persona.
- `CannedChatClient` echoing instructions (DevHost is manual-verify; optional nicety).
- Any Bicep/infra change — the registry grain reuses the existing `agent-state`
  container.
- Automated frontend tests — the repo has no JS test harness; Step 4 is run-app verified.
```