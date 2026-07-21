# Plan — group agent-produced conversations in the sidebar

planned-against: main @ ab599b7

## Overview

When a user chats with a stored agent and continuous mode is **off** (ADR 0016), each
start mints a fresh random conversation id, so every agent chat lands as its own flat row
in the sidebar's "// transmissions" list — they pile up undifferentiated among regular
chats. This plan groups conversations produced by an agent under a **collapsible group
labelled with the agent's name**, while regular (non-agent) chats stay as flat rows.

Goal: the sidebar shows each agent's chats collected under one collapsible header (agent
name + count), leaving non-agent chats ungrouped. Collapse state persists across renders
and reloads.

## Affected areas (verified against the tree at ab599b7)

- `src/Agent42.Agents/IConversationIndexGrain.cs` — `ConversationInfo` record + `TouchAsync` signature.
- `src/Agent42.Agents/ConversationIndexGrain.cs` — persist/preserve agent identity on touch.
- `src/Agent42.Backend/AgentApi.cs` — pass agent identity into `TouchAsync` on the two
  conversation-creating paths (interactive chat ~line 56; async agent-run ~line 370).
- `src/Agent42.Backend/wwwroot/app.js` — `renderSidebar` (lines 366–406): build groups; collapse state.
- `src/Agent42.Backend/wwwroot/styles.css` — group header / body / chevron styles (after `.conversations__empty`, ~line 280).
- `tests/Agent42.Tests/AgentGrainTests.cs` — grain-level data test.
- `tests/Agent42.Tests/AgentApiTests.cs` — API wiring test.
- `tests/Agent42.Tests/PwaAssetsTests.cs` — source-heuristic smoke test for the shipped UI code.

## Context & constraints (what the executor can't cheaply re-derive)

- **The index grain is the only place a conversation's list metadata lives.**
  `ConversationInfo(Id, Title, UpdatedAt)` (`IConversationIndexGrain.cs:8`) has no agent
  field today; `GET /api/conversations` returns it verbatim (`AgentApi.cs:91`). The agent
  binding lives on the *AgentGrain* (`AgentBinding`, `AgentGrain.cs:503`), one grain call
  per conversation — too expensive to enrich the list at read time. So the grouping key
  must be **stored on `ConversationInfo` at touch time**, not fetched per-row.

- **Both creating paths already know the agent at touch time.** Interactive chat: the
  client sends `request.AgentId` only on the first turn of a freshly-started agent chat
  (`AgentApi.cs:58` comment; client `pendingAgentId`, `app.js:511`). The definition
  (carrying the display name) is fetched at `AgentApi.cs:66` — currently *after* `TouchAsync`
  at line 56, so the executor must **reorder**: fetch the definition first, pass its id+name
  into `TouchAsync`, then bind. Async agent-run: `definition` is already in scope before the
  `TouchAsync` at line 370 — pass `definition.Id, definition.Name` directly.

- **Store both id and a name snapshot.** Group *key* = `AgentId` (stable). Group *label* =
  `AgentName`, snapshotted onto `ConversationInfo` (consistent with ADR 0003's persona
  snapshotting: a later rename/delete of the source agent must not blank an existing chat's
  group label). The frontend labels each group with the name from its most-recent (first
  encountered) member.

- **`TouchAsync` sets agent identity only when creating the entry.** On an existing
  conversation the method just bumps `UpdatedAt` (`ConversationIndexGrain.cs:30-33`) — it
  must **preserve** the stored agent identity and ignore null agent args from later turns
  (resumed turns send no `AgentId`). This is why the new params are optional and only the
  new-entry branch writes them.

- **No state migration.** New fields are trailing optional `[Id(3)]`/`[Id(4)]` with `null`
  defaults; `TouchAsync` gains trailing optional params. Existing serialized state and
  every existing 2-arg `TouchAsync` call keep working (personal project, both ends deploy
  together — CLAUDE.md repo notes).

- **`renderSidebar` is called on every refresh** (`app.js:362,421,517,963,1027`) — collapse
  state must live **outside** the render (a module-level `Set` of collapsed agent ids,
  persisted to `localStorage`, mirroring the existing `THEME_KEY`/`LOCATION_GRANT_KEY`
  pattern, `app.js:1463,1094`). Wrap `localStorage` access in try/catch (private mode).

- **Grouping rule:** iterate the already-recency-sorted list; a conversation with no
  `agentId` renders as today's flat `.conv-row`; a conversation with an `agentId` is
  appended into its group, and the group is positioned at its **first-encountered** (=
  most-recent) member so groups interleave with regular chats by recency. Continuous agents
  (ADR 0016) also carry an `agentId` and naturally form a single-item group — acceptable and
  consistent; no special-casing of the `agent-` id prefix.

- **No JS test runner by design** (`PwaAssetsTests.cs:59` comment). UI logic is verified
  manually; the repo's convention is a **source-text heuristic** asset test (see
  `ServiceWorker_BypassesApiBeforeCaching`) to prove the code shipped.

- Governing docs: ADR 0016 (per-agent history mode / continuous), ADR 0003 (persona
  snapshotting). No ADR conflict — this is a sidebar presentation change over existing
  index state; no new decision needed (confirm with `/decide` only if the critic disagrees).

## Steps

1. **`ConversationInfo` gains agent identity.**
   `IConversationIndexGrain.cs`: add `[property: Id(3)] string? AgentId = null` and
   `[property: Id(4)] string? AgentName = null` to the record. Add trailing optional
   `string? agentId = null, string? agentName = null` to the `TouchAsync` signature +
   XML doc note.
   *Verify:* `dotnet build Agent42.slnx` compiles.

2. **`ConversationIndexGrain.TouchAsync` persists/preserves identity.**
   In the new-entry branch (`ConversationIndexGrain.cs:35-43`) construct
   `new ConversationInfo(conversationId, title, UtcNow, agentId, agentName)`. The existing
   branch keeps the stored record's agent fields untouched (only `UpdatedAt` changes).
   *Verify:* Step 6 grain test.

3. **Interactive chat path passes agent identity.**
   `AgentApi.cs` (~40–75): move the `request.AgentId` → `IAgentRegistryGrain.GetAsync`
   definition fetch **above** the `TouchAsync` call, then call
   `TouchAsync(conversationId, title, definition?.Id, definition?.Name)`. A deleted agent
   (definition null) passes nulls → an already-listed conversation keeps its stored
   identity. Keep the existing bind + `model = null` logic.
   *Verify:* Step 7 API test.

4. **Async agent-run path passes agent identity.**
   `AgentApi.cs:370`: `TouchAsync(conversationId, title, definition.Id, definition.Name)`
   (`definition` already in scope).
   *Verify:* `dotnet build`; covered indirectly by existing agent-run tests still passing.

5. **Sidebar grouping + collapse (frontend).**
   `app.js`: rewrite `renderSidebar` (366–406) to (a) extract the current flat-row build
   into a helper `appendConvRow(container, conv)`; (b) walk conversations in order, routing
   non-agent convs to the list and agent convs into a per-`agentId` group element created on
   first encounter; (c) render each group as a `<li class="conv-group">` with a header
   button (chevron + agent name + count) toggling a `<ul class="conv-group__body">`. Add a
   module-level `collapsedAgents` `Set` loaded from / saved to `localStorage`
   (`COLLAPSED_AGENTS_KEY`), toggled on header click, applied as a `.is-collapsed` class.
   Active-conversation highlight and delete still work inside groups.
   *Verify:* Step 8 asset smoke test + manual (`dotnet run --project tools/Agent42.DevHost`,
   create an agent, start 2 non-continuous chats, confirm they collapse under the name).

6. **Group styles.**
   `styles.css` (after `.conversations__empty`, ~280): `.conv-group`, `.conv-group__header`
   (button, faint mono label matching `.conversations__label` register), `.conv-group__count`,
   a chevron that rotates on `.is-collapsed`, and `.conv-group.is-collapsed .conv-group__body { display:none }`.
   Reuse existing `--ink-*`/`--line-*` tokens; indent group items slightly.
   *Verify:* manual visual check in DevHost.

7. **Tests** — see the AC→test table below. Written alongside steps 2–5.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | A conversation touched with an agent id+name carries them in `GetConversationsAsync`; a later touch without agent args preserves them and bumps `UpdatedAt` | `ConversationIndex_TouchWithAgent_StoresAndPreservesIdentity` (new) | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC2 | A non-agent touch yields `AgentId == null` | asserted within the AC1 test (regular `conv` from existing `ConversationIndex_Lists…` remains null) | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC3 | After `POST /api/chat/{id}` with an `AgentId`, `GET /api/conversations` returns that conversation with matching `AgentId`/`AgentName` | `ChatWithAgentId_ListsConversationUnderAgent` (new) | `tests/Agent42.Tests/AgentApiTests.cs` |
| AC4 | A normal chat lists with `AgentId == null` | asserted within the AC3 test (second, agent-less conversation) | `tests/Agent42.Tests/AgentApiTests.cs` |
| AC5 | The shipped UI contains the grouping logic + styles (code-present smoke) | `Sidebar_GroupsAgentConversations` source-heuristic (new) | `tests/Agent42.Tests/PwaAssetsTests.cs` |
| AC6 | Collapsible group renders, toggles, and persists; non-agent chats stay flat | Manual verification in DevHost (no JS runner — repo convention) | — |

## Quality gate checklist (from `.agentic/quality-gates.yaml` @ ab599b7)

- `new_issues: 0`, `new_security_issues: 0`
- `duplication_pct_max: 3`
- `coverage_new_code_pct_min: 80` — new C# (grain field + touch branch + API wiring) covered by AC1–AC4; UI is heuristic + manual per repo convention.
- `cognitive_complexity_max: 15` — keep `renderSidebar` within budget by extracting `appendConvRow`.
- `dependencies.vulnerable_packages: 0` — no new packages.
- Gates: `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`.

## Out of scope

- Grouping/collapsing the **// agents** launcher list (this is only the transmissions list).
- Any change to continuous-agent routing (ADR 0016) — continuous chats still map to one
  `agent-<id>` conversation and simply appear as a single-item group.
- Reordering or re-sorting conversations beyond the group-at-most-recent-member behavior;
  the backend recency sort is unchanged.
- Server-side collapse-state persistence (kept client-side in `localStorage`).
- Migrating or backfilling agent identity onto conversations created before this change —
  they list ungrouped (null `AgentId`), which is correct.

## Review

Critic verdict: **PASS** (no plan defects). Central claims verified: trailing
`[Id(3)]/[Id(4)]` + optional `TouchAsync` params avoid migration (repo convention, ADR
0016/0022); single co-hosted silo replica → no aliased-method version skew; reorder hits
three distinct grains with no ordering dependency; caller inventory complete; JSON camelCase
(`agentId`/`agentName`) matches the frontend.

Implementation cautions carried from the critic (not plan changes):
- **Step 3:** keep `IAgentRegistryGrain.GetAsync` guarded on non-empty `request.AgentId`
  (don't fetch on every regular turn), and keep `model = null` firing for **any** non-empty
  `AgentId` — including a deleted agent (`definition is null`) — i.e. do **not** nest
  `model = null` under `definition is not null`. Otherwise a first turn of a just-deleted
  agent chat silently changes model source.
- The grain param stays named `firstPrompt` (title derived internally); call sites pass a
  local `title`.
- AC5 proves code *presence* only; collapse/persist behavior (AC6) has no automated guard
  by repo convention (no JS runner).
