# Plan — Selectable tools per chat and per agent (remembered)

`planned-against: main @ 7df3d9d`

## Overview

Let a user choose **which tools** an assistant may use, and remember that choice at three
layers: a **user-level default** (applies to every new regular chat), a **per-conversation
override** (change tools inside an open chat; persists for that conversation), and a
**per-agent** selection (stored on the agent persona, snapshotted into its chats — ADR 0003).
Today every tool is unconditionally registered in `AgentGrain.BuildAgent`, gated only by
model capability; there is no user control. This adds a selection layer that **intersects**
with the existing capability gates (a text-only model still gets none; enabling a hosted-only
tool on a non-hosted model is a no-op).

Scope was fixed with the user:
- **Curated tool set** (not every function individually).
- **User default + per-chat override** for regular chats.
- Per-agent tools live on `AgentDefinition` and are **snapshotted** into the conversation
  (consistent with ADR 0003 — editing an agent's tools affects only *future* chats).

## Curated tool catalog (the selectable set)

One **logical key** maps to one or more registered tools. This is the single source of truth
(define once in C#, expose to the UI via `GET /api/tools`).

| Key | Controls | Capability needed |
|-----|----------|-------------------|
| `web_search` | `web_search` + `fetch_web_page` shims (Chat Completions) **or** `HostedWebSearchTool` (Responses) | `SupportsTools` |
| `location` | `get_current_location` | `SupportsTools` |
| `scheduling` | `schedule_prompt`, `cancel_scheduled_prompt`, `cancel_all_scheduled_prompts` | `SupportsTools` |
| `skills` | `AgentSkillsProvider` (`load_skill`) — an `AIContextProvider`, not in the `Tools` list | `SupportsTools` |
| `code_interpreter` | `HostedCodeInterpreterTool` | `SupportsHostedTools` |

**Always-on, never shown / never selectable:** `get_date`; the document RAG `TextSearchProvider`
(gated by document presence only — ADR 0008, per scope decision it stays automatic).

## Representation & resolution (the core design)

`EnabledTools` is a **nullable list of keys** everywhere it is stored:
- `null` (or absent) = **all selectable tools enabled** — this is today's behavior, so every
  existing user / agent / conversation keeps all tools with **no migration**.
- non-null = an explicit set (empty list = none selected).

Effective set computed in `AgentGrain` before/inside `BuildAgent`:

```
effective = _state.State.EnabledTools          // per-conversation override, or agent snapshot
         ?? _userDefaultEnabledTools            // fetched from IUserProfileGrain on activation
         ?? AllSelectableToolKeys;              // ultimate fallback == current behavior
```

Then `BuildAgent` **intersects** `effective` with model capability exactly as today: a tool is
registered only if `effective` contains its key **and** the capability gate passes. This keeps
the ADR 0013/0015 gates authoritative (security boundary is server-side, not the UI).

- **Agent chat:** `BindAgentAsync` snapshots the agent's tools into
  `_state.State.EnabledTools` as a **concrete** set (`binding.EnabledTools ?? AllSelectableToolKeys`)
  so the persona is self-contained — an agent never inherits the *user's* per-chat default
  (ADR 0003). An old agent (null) materializes to all-selectable.
- **Regular chat:** `_state.State.EnabledTools` stays `null` until the user changes tools in
  that chat, at which point it becomes an explicit override.

## Affected areas (verified against the tree @ 7df3d9d)

**Domain / state (`src/Agent42.Agents/`)**
- `AgentGrainState.cs` — add `[Id(10)] List<string>? EnabledTools = null` (highest Id today is 9).
- `AgentDefinition.cs` — add `[Id(8)] IReadOnlyList<string>? EnabledTools = null` (highest is 7 `Continuous`).
- `AgentBinding.cs` — add `[Id(4)] IReadOnlyList<string>? EnabledTools = null` (highest is 3 `Model`).
- `UserProfile.cs` — add `[Id(1)] IReadOnlyList<string>? EnabledTools = null`; update `Empty` factory.
- `AgentGrain.cs` — `BuildAgent` tool/skills filtering; fetch user default on activation
  (`OnActivateAsync`) **before the `BuildAgent(_modelId)` call at line 119** (see constraint
  below); snapshot in `BindAgentAsync`; new `SetEnabledToolsAsync` / `GetEnabledToolsAsync`;
  `GetAgentBindingAsync` construction at `AgentGrain.cs:387` (add `EnabledTools` for
  completeness — no current consumer needs it); a `ToolCatalog` helper (keys, tool→key map,
  `AllSelectableToolKeys`).
- `IAgentGrain.cs` — declare `SetEnabledToolsAsync` + `GetEnabledToolsAsync` (with `[Alias]`).
- `UserProfileGrain.cs` — round-trip `EnabledTools` through `SaveAsync`.

**API (`src/Agent42.Backend/`)**
- `AgentApi.cs` — `GET /api/tools` (catalog); `GET`/`PUT /api/conversations/{id}/tools`;
  extend `ProfileUpdateRequest` + `PUT /api/profile`; extend `AgentUpsertRequest` +
  `POST`/`PUT /api/agents` (pass `EnabledTools` into `AgentDefinition`); pass `EnabledTools`
  into `AgentBinding` at the bind site (line ~68).

**Frontend (`src/Agent42.Backend/wwwroot/`)** — `app.js` + the dialogs' HTML (index/host page)
- Fetch `/api/tools`; render checkbox groups in: **Preferences** dialog (user default,
  save via `/api/profile`), **agent create/edit** dialog (`openAgentDialog`/`saveAgent`),
  and a **composer "Tools"** popover (per-chat). Restore per-chat selection on conversation
  open via `GET /api/conversations/{id}/tools`; persist changes via `PUT`.

**Offline / tests**
- `tools/Agent42.DevHost/Program.cs` + `tests/.../TestSiloConfigurator.cs` — the single-user
  adapters already back `IUserProfileGrain`/`IAgentRegistryGrain`, so the CRUD is exercisable
  offline; confirm no new registration is needed for the new grain methods (no new grain type
  is introduced — reuse existing grains).

**Docs** — new ADR; `CLAUDE.md` bullet; gotcha(s); complete this spec.

## Context & constraints (what the executor can't cheaply re-derive)

- **`BuildAgent` tool map** (`AgentGrain.cs:144–283`): tools are assembled into a `List<AITool>`
  then `Tools = capabilities.SupportsTools ? tools : []` (line 251); skills/RAG are
  `AIContextProviders` via a `(SupportsTools, HasDocuments)` switch (lines 265–271). The filter
  must be applied to **both** the `tools` list (by tool `Name`→key) and the skills-provider arm
  of the switch. **RAG must remain** in that switch regardless of selection.
- **GOTCHA — "AgentSkillsProvider is a TOOL"** (`.agentic/gotchas.md` ~359–375): dropping the
  function tools while leaving the skills provider on hands the model a `load_skill` schema it
  can't use. Same rule applies to *user-disabled* skills: disabling `skills` must drop the
  provider, not just the label. `TextSearchProvider` is **not** a tool and must stay.
- **GOTCHA — `HostedWebSearchTool.Name` is `web_search`** (~739–757): on a hosted model the
  `web_search` key controls `HostedWebSearchTool`; on Chat Completions it controls the two
  SearXNG shims. One key, two wire realizations — the swap (ADR 0015) still happens first, then
  the filter applies. `code_interpreter` exists **only** on the hosted arm.
- **Capability intersection is load-bearing:** never register a tool the model can't handle even
  if selected — the existing `SupportsTools`/`SupportsHostedTools` gates stay the outer gate.
- **Grain rebuild shape:** `SetEnabledToolsAsync` must snapshot→rebuild→restore the session
  exactly like `RegisterDocumentAsync` (`AgentGrain.cs:390–409`) and `BindAgentAsync`, then
  `WriteStateAsync`. Do **not** invent a new rebuild path.
- **User-default fetch MUST precede the first `BuildAgent`** (critic blocker-risk): `BuildAgent`
  is called at `OnActivateAsync:119` and `ReceiveReminder` reuses that same `_agent`. If the
  `IUserProfileGrain` fetch is placed *after* line 119, the first chat turn **and every reminder
  turn on that activation** register all tools and silently ignore the user's default. Fetch the
  default into its grain field **before** line 119 — same ordering hazard the `_chatClientFactory`
  comment at `AgentGrain.cs:104–107` warns about for a different field.
- **User-default staleness (accepted):** the user default is read **once on activation** (mirrors
  how the default model resolves on activation, `OnActivateAsync:116`). A Preferences change
  reflects in a warm regular chat on its next activation. Acceptable; document it, don't fight it.
- **Profile save must not wipe `EnabledTools`** (critic should-fix): the profile endpoint
  reconstructs the whole record (`AgentApi.cs:240` → `UserProfileGrain.cs:44` `with { ... }`), so a
  language-only save under `null==all` would reset the tool default to "all." **Convention:
  absent (`null`) means keep.** The `PUT /api/profile` handler does read-modify-write — fetch the
  current profile, then `current with { PreferredLanguage = req.PreferredLanguage ?? current...,
  EnabledTools = req.EnabledTools ?? current.EnabledTools }` — so neither field's endpoint wipes
  the other. The UI represents "all selected" as the **full explicit list**, never `null`, so the
  only thing `null` can't do here (reset-to-all) is never needed. Must have an **endpoint-level**
  round-trip test (AC14), not just the grain-level one.
- **Composer tools popover is non-agent chats only** (critic nit): an agent chat's tools come from
  the agent snapshot, and `BindAgentAsync` overwrites `_state.State.EnabledTools` on the first turn
  — so a per-chat override PUT before the first agent turn would be clobbered. Gate the popover the
  same way the model picker is already locked for agent chats (`app.js:394`). Mid-conversation the
  bind won't re-run, but simplest and clearest is: popover hidden on agent chats.
- **Test harness:** the fake terminal chat client echoes `[tools:<names>]` built from what the
  **terminal** client actually received (`AgentGrainTests.cs:1162–1230`, helpers `Answer`,
  `ToolNames`, `TestSiloConfigurator.DefaultModel` / `NoToolsNoVisionModel` / `HostedToolsModel`).
  Assert on those echoes — never on grain intent. New tests mirror `ToolModel_StillRegistersAllTools`
  and `NoToolModel_StillGetsDocumentRetrieval`.
- **Backward-compat sentinel:** `null == all`. Do **not** represent "all" as an empty list, and do
  **not** default a new list to `[]` (that would silently strip every tool from existing state).
- **Serialization:** new fields are trailing `[Id]` with `null` defaults → no migration (same
  pattern as ADR 0016 `Continuous`, ADR 0020 `GeneratedImages`).
- **New-chat wiring:** a per-chat `PUT /api/conversations/{id}/tools` activates the grain and
  persists an override even before the first message. If a regular chat's conversation id is
  minted lazily at first send, the client must have an id before it PUTs (mint upfront, or apply
  a pending selection right after the first send). Executor picks the minimal wiring; keep it
  consistent with how `startAgentChat` mints `convId`.

## Governing ADRs / gotchas to read first
- ADR 0013 (multi-provider capabilities), ADR 0015 (hosted-tools swap), ADR 0016 (client-vs-
  snapshot field pattern — precedent for a routing flag; here tools are **persona**, so we
  snapshot like ADR 0003), ADR 0005 (`UserProfile` extension pattern), ADR 0018 (per-user overlay
  / version-bumped grain pattern), ADR 0008 (RAG always-on).
- `.agentic/gotchas.md`: "AgentSkillsProvider is a TOOL", "`HostedWebSearchTool.Name` is
  `web_search`", "a shared static default for grain state is unsafe" (mind `UserProfile.Empty`).

## Steps

1. **ADR** — `/decide`: record the selectable-tools model (three-layer resolution, `null==all`,
   curated catalog, snapshot-into-persona). Note it amends ADR 0013/0015's tool-registration
   description. *Verify:* ADR file exists in `.agentic/decisions/`, no unresolved conflict.

2. **Tool catalog helper** (`AgentGrain.cs` or a new `ToolCatalog.cs` in `Agent42.Agents`) —
   define keys, `AllSelectableToolKeys`, a `tool-name → key` map, and the per-key required
   capability. *Verify:* unit test that every registered tool name maps to a key or is the
   always-on `get_date`. **Parallelizable** with step 3.

3. **State/DTO fields** — add trailing `EnabledTools` to `AgentGrainState` (`[Id(10)]`),
   `AgentDefinition` (`[Id(8)]`), `AgentBinding` (`[Id(4)]`), `UserProfile` (`[Id(1)]`, + `Empty`),
   and round-trip in `UserProfileGrain.SaveAsync`. **Coordinate `[Id]` with the active
   `.agentic/specs/plan-agent-run-endpoint.md`, which also extends `AgentDefinition` — whichever
   lands second must take the next free `[Id]` (re-check before writing).** *Verify:* `dotnet build`;
   a serialize/deserialize test that old-shape state → `null` (all on). **Parallelizable** with step 2.

4. **BuildAgent filtering** — compute `effective` (override ?? default ?? all); intersect the
   `tools` list by key and the skills-provider switch-arm by `skills`; keep RAG always. Fetch the
   user default on activation into a grain field. *Verify (new tests in `AgentGrainTests.cs`):*
   - default (null) → all tools (regression: `ToolModel_StillRegistersAllTools` still passes);
   - override `["web_search"]` → only `web_search`/`fetch_web_page` (+ `get_date`), no
     `load_skill`/`get_current_location`/`schedule_prompt`;
   - `skills` disabled → no `load_skill`, RAG excerpt still retrieved (mirror
     `NoToolModel_StillGetsDocumentRetrieval`);
   - `code_interpreter` selected on Chat model → absent; on `HostedToolsModel` → present;
   - selecting a tool on `NoToolsNoVisionModel` → still `[tools:]` (capability wins).

5. **Grain surface** — `SetEnabledToolsAsync(list?)` (snapshot→rebuild→restore→write, like
   `RegisterDocumentAsync`) and `GetEnabledToolsAsync()` (returns the **effective** concrete set);
   `BindAgentAsync` snapshots `binding.EnabledTools ?? AllSelectableToolKeys`. Depends on 4.
   *Verify:* test that `SetEnabledToolsAsync` changes the next turn's registered tools and
   survives a grain deactivation; test that an agent bind pins the agent's set (and a null-tools
   agent pins all).

6. **API endpoints** — `GET /api/tools`; `GET`/`PUT /api/conversations/{id}/tools`; extend
   profile + agent request records and their handlers (`PUT /api/profile` does **read-modify-write**
   with absent-means-keep — see constraint); pass `EnabledTools` into `AgentBinding` at the bind
   site. Depends on 3, 5. *Verify:* endpoint tests mirroring `ProvidersApiTests` /
   `ModelCatalogTests` — profile and agent round-trip `EnabledTools`; **a language-only profile
   save does NOT wipe a previously-saved tool default** (AC14); `/api/tools` returns the five keys;
   per-conversation PUT then GET returns the set (AC13).

7. **Frontend** — `/api/tools`-driven checkbox groups in Preferences, agent dialog, and a
   composer "Tools" popover; restore per-chat on open; persist on change. Optionally grey out
   `code_interpreter` unless the selected model supports hosted tools and all tools when the model
   has no tool support (polish, not AC-critical). Depends on 6. *Verify:* `/run` (DevHost) — pick
   tools in each surface, confirm they persist across reload and that a disabled tool stops being
   offered to the model (watch the tool-call chip / `[tools:]`).

8. **Docs** — `/gotcha` for the three-layer resolution + `null==all` + "disabling `skills` drops
   the provider"; update the `CLAUDE.md` repo-notes bullets (tools are now selectable at three
   layers); `/sync-docs`; complete this spec into `specs/completed/`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | No selection (null) registers all tools — no regression | `ToolModel_StillRegistersAllTools` (existing, must stay green) | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC2 | Per-conversation override limits the registered tools | `Override_LimitsRegisteredTools` (new) | `AgentGrainTests.cs` |
| AC3 | Disabling `skills` drops `load_skill` but keeps RAG | `SkillsDisabled_DropsLoadSkill_KeepsRetrieval` (new) | `AgentGrainTests.cs` |
| AC4 | `code_interpreter` only when hosted + selected | `CodeInterpreter_OnlyOnHostedModelWhenSelected` (new) | `AgentGrainTests.cs` |
| AC5 | Capability still wins over selection (no-tools model) | `SelectionOnNoToolModel_StillRegistersNoTools` (new) | `AgentGrainTests.cs` |
| AC6 | User default applies to a chat with no override | `UserDefault_AppliesWhenNoOverride` (new) | `AgentGrainTests.cs` |
| AC7 | Override beats user default | `Override_BeatsUserDefault` (new) | `AgentGrainTests.cs` |
| AC8 | `SetEnabledToolsAsync` persists across deactivation | `SetEnabledTools_PersistsAcrossDeactivation` (new) | `AgentGrainTests.cs` |
| AC9 | Agent bind snapshots the agent's tools (null → all) | `BindAgent_SnapshotsEnabledTools` (new) | `AgentGrainTests.cs` |
| AC10 | Old-shape state deserializes to null == all | `LegacyState_NullEnabledTools_MeansAll` (new) | `AgentGrainTests.cs` (or serialization test) |
| AC11 | Profile grain round-trips `EnabledTools` | extend `UserProfileGrainTests` | `tests/Agent42.Tests/UserProfileGrainTests.cs` |
| AC12 | Agent create/edit round-trips `EnabledTools`; `/api/tools` catalog | new `ToolSelectionApiTests` (mirror `ProvidersApiTests`) | `tests/Agent42.Tests/ToolSelectionApiTests.cs` |
| AC13 | Per-conversation `PUT` then `GET /api/conversations/{id}/tools` returns the set | `ToolSelectionApiTests` | `tests/Agent42.Tests/ToolSelectionApiTests.cs` |
| AC14 | A language-only `PUT /api/profile` does NOT wipe a saved tool default | `ToolSelectionApiTests` (endpoint-level, not grain) | `tests/Agent42.Tests/ToolSelectionApiTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` @ plan time)
- `new_issues: 0`, `new_security_issues: 0`
- `duplication_pct_max: 3`
- `coverage_new_code_pct_min: 80`
- `cognitive_complexity_max: 15`
- `vulnerable_packages: 0`; new deps `latest-stable` (none expected — no new package)
- Branch: work on `feature/selectable-tools` (never commit to `main`); tests alongside impl;
  `dotnet test Agent42.slnx` + `dotnet format Agent42.slnx --verify-no-changes` before MR.

## Out of scope
- Making `get_date` or document RAG selectable (RAG stays automatic — ADR 0008).
- Per-tool configuration/arguments (allowlist tuning, SearXNG engine choice, etc.).
- Live propagation of a changed user default into already-warm conversations (activation-time
  read is accepted; documented).
- A new Cosmos container or storage provider (reuse existing grains, per ADR 0001/0005).
- Version-bumped overlay polling for tool changes (unlike ADR 0018 providers — tool selection
  needs no cross-request cache-invalidation signal).

## Review

A `critic` subagent reviewed this plan against the code at `7df3d9d`. It verified clean on:
no compile breakage (all added record fields are trailing `null` defaults; every
`new AgentBinding`/`AgentDefinition`/`UserProfile` site keeps compiling), gotcha compliance
(skills-arm dropped on disable, RAG always-on, name→key filter applied *after* the ADR 0015
hosted swap), ADR framing (tools-as-persona snapshot does not contradict ADR 0016's routing-only
`Continuous`), and the `null==all` sentinel (no existing readers; `UserProfile.Empty` stays valid).

Findings addressed in-plan:
- **[should-fix] User-default fetch ordering** — pinned to run *before* `BuildAgent` at
  `OnActivateAsync:119` (Affected areas + Context & constraints), or reminder/first turns silently
  ignore the default.
- **[should-fix] Profile save wipes `EnabledTools`** — adopted absent-means-keep read-modify-write
  on `PUT /api/profile`; added endpoint-level no-wipe test **AC14**.
- **[nit] Agent-chat bind clobbers a pre-send per-chat override** — composer popover is now
  scoped to non-agent chats (agent tools come from the snapshot).
- **[nit] `GetAgentBindingAsync` construction site** — added to Affected areas.
- **[nit] Endpoint round-trips missing from AC table** — added **AC13** (per-conversation
  PUT/GET) and **AC14** (profile endpoint no-wipe).
- **[nit] Cross-plan `[Id]` coordination** — added a coordination note to step 3 re:
  `plan-agent-run-endpoint.md` also extending `AgentDefinition`.

No findings were rejected.

## Implementation notes — divergence from the plan

Recorded at execution time (branch `feature/selectable-tools`). The design landed as planned; the
deltas below are refinements discovered while implementing, none of them design changes.

- **The `skills` key controls THREE provider tools, not one.** The plan's catalog table listed only
  `load_skill` for the `skills` key. `AgentSkillsProvider` actually surfaces `load_skill`,
  `read_skill_resource`, and `run_skill_script` — the step-2 key-coverage guard test
  (`EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate`) caught the two extras, and all three are mapped
  to the `skills` key in `ToolCatalog`. They are only in the name→key map for guard-test coverage; the
  provider is still gated by dropping it from the `AIContextProviders` switch-arm, never by filtering the
  `Tools` list (they never enter it).
- **No change was needed in `UserProfileGrain.SaveAsync`.** The plan (step 3) called for round-tripping
  `EnabledTools` there, but `SaveAsync` already reconstructs the record with `profile with { … }`, which
  preserves every unmentioned field — so `EnabledTools` round-trips with no edit. AC11 verifies this.
- **An existing test needed a fix the plan didn't anticipate:**
  `Binding_PinsPersonaAndModel_SurvivesReactivation` compared two `AgentBinding` records with
  `Assert.Equal`. Now that `AgentBinding` carries `EnabledTools` (a list), record structural equality
  compares that reference, so two equal-content lists across the reactivation boundary differ. Changed to
  field-wise assertions with a sequence compare on `EnabledTools`.
- **`ToolDescriptor` and `GET /api/tools`.** The catalog is exposed as a plain record
  (`ToolDescriptor(Key, Label, RequiresHostedTools)`) served directly by minimal API — no Orleans
  serialization needed (it never crosses a grain boundary). The UI greys nothing out yet; the
  `RequiresHostedTools` flag is surfaced (rendered as a "(Responses)" label) but the optional grey-out
  polish from step 7 was left out as non-AC-critical.
- **`ProfileUpdateRequest.PreferredLanguage` is now nullable** (`string? = null`), as the plan's
  read-modify-write constraint required — absent means keep. Existing positional callers still compile.
- **`[Id]` assignment held as planned:** `AgentDefinition.EnabledTools` took `[Id(8)]`; the stale sibling
  `plan-agent-run-endpoint.md` (planned against an older ref where its `ApiKeyHash` claimed `[Id(7)]`,
  now taken by `Continuous`) will take the next free `[Id]` when it lands.
- **Verification:** all 328 tests pass; `dotnet format --verify-no-changes` clean; the API contract was
  additionally exercised end-to-end against the DevHost (offline) — `/api/tools`, per-conversation
  PUT/GET tools (with server-side sanitization of a garbage key), the profile no-wipe round-trip, and
  agent `enabledTools` create round-trip.
