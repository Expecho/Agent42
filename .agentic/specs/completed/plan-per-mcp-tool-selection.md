# Plan — Per-MCP-tool selectable tools (`mcp:{serverId}:{toolName}`)

planned-against: main @ 3837585

## Overview

An MCP server exposes many tools, and today the ADR 0021 tool picker gates them **per
server** (`mcp:{id}`, ADR 0026) — all of a server's tools on, or all off. Extend the same
selection mechanism one level down so a user can toggle **individual MCP tools**: use a
server's `jira_get_issue` in this chat but not its `jira_delete_issue`.

This is the third instalment of a known pattern (ADR 0026 did servers, ADR 0048 did skills),
but it is **not** a mechanical copy of either: both of those gate over a list that is always
knowable locally (a config row, a directory on disk). MCP **tool names are only knowable by
connecting**, and that single difference is where every hard part of this plan comes from.

Why now — the user asked for it, and per-server granularity is the current friction point.

## Affected areas

Verified against the tree at `3837585`.

**Agents — keys, gate, expansion**
- `src/Agent42.Agents/Tools/ToolCatalog.cs` — new `McpToolKey` / `IsMcpToolEnabled`; doc-comment
  updates on `IsMcpServerKey` and `Sanitize`; `ToolDescriptor` gains `ParentKey` (Context §8).
- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` — the MCP gating block (lines ~186–195)
  and `ExpandMcpWildcard` (lines ~351–365), which gains a second level (Context §4).

**Agents — persisted names**
- `src/Agent42.Agents/Mcp/UserMcpConfig.cs` — `StoredMcpServer` gains trailing
  `[property: Id(12)] IReadOnlyList<string>? KnownToolNames = null`; fix the `UserMcpConfig`
  summary that says every write bumps the version.
- `src/Agent42.Agents/Mcp/IUserMcpToolProvider.cs` — `McpServerTools` gains a trailing
  `KnownToolNames` (Context §3). Not an Orleans-serialized type — no `[Id]` needed; confirm.
- `src/Agent42.Agents/Mcp/IUserMcpConfigGrain.cs` + `UserMcpConfigGrain.cs` — new **non-bumping**
  `RecordToolNamesAsync` (Context §6); add `KnownToolNames` to `SaveAsync`'s `Keep` set
  (Context §5 — **this one is easy to miss and breaks the feature**); fix the two doc comments
  that claim every write bumps.

**Backend**
- `src/Agent42.Backend/Mcp/UserMcpToolProvider.cs` — record discovered names after a **successful**
  connect only; populate each group's `KnownToolNames` from the stored value.
- `src/Agent42.Backend/Mcp/AgentApi.Mcp.cs` — `ToStored` (`:122-134`) builds a fresh record, so the
  edit path must not drop `KnownToolNames` (Context §5).
- `src/Agent42.Backend/Tools/AgentApi.Tools.cs` — `GET /api/tools` appends per-tool descriptors
  from `KnownToolNames`. **Stays a pure config read — no provider, no network** (Context §6).

**Frontend**
- `src/Agent42.Backend/wwwroot/js/tools.js` — `renderToolChecks` (nested render, four-way checked
  OR, master↔child sync, per-container stash), `readToolChecks` (collapse + preservation).
- `src/Agent42.Backend/wwwroot/styles.css` — nesting / disclosure styles (existing surface is only
  three rules, `styles.css:1395-1421`).
- `src/Agent42.Backend/wwwroot/sw.js` — `CACHE` bump `agent42-shell-v23` → `-v24`.

**Offline exercisability**
- `tools/Agent42.DevHost/Program.cs` — seed both the provider **and** the config grain (Context §10).

**Docs**
- `.agentic/decisions/00XX-per-mcp-tool-selectable-tools.md` (new, via `/decide`).
- `.agentic/gotchas.md` — the `null == all` entry (a **fifth** trap) and the MCP live-socket entry.
- `CLAUDE.md` — the ADR 0026 bullet.

**Tests**
- `ToolCatalogTests.cs`, `ToolSelectionApiTests.cs`, `AgentGrainTests.cs`,
  `UserMcpConfigGrainTests.cs` (`Save_BumpsVersion_OnEveryWrite` at `:44` is now misnamed),
  `UserMcpToolProviderTests.cs`, `McpServersApiTests.cs`.
- `SerializationCompatibilityTests.cs:222` + `GoldenStates.cs:92-97` — `StoredMcpServer` is
  **persisted state**; `UserMcpConfigState_RoundTripsFieldForField` compares field-for-field
  against the golden (Context §7).
- `SkillPickerAssetsTests.cs` — **three** assertions touch the rewritten code (Context §9).
- `PwaAssetsTests.cs:215` — pins `agent42-shell-v23`; **must** be edited with the bump.
- New `McpToolPickerAssetsTests.cs`.

**Read-only — verify, do not change.** `profile.js:78` / `agents.js:169` save through
`readToolChecks` under a `toolCatalog.length > 0` guard (that guard is about a *total* catalog
failure and does **not** protect the Context §3 case); `conversations.js:104` / `main.js:102`
seed `chatTools` from `allToolKeys()`, which is only re-rendered, never PUT directly.

## Context & constraints

Read first: `.agentic/decisions/0026-per-mcp-server-selectable-tools.md`,
`0024-config-driven-remote-mcp-servers.md`, `0021-selectable-tools-three-layer.md`,
`0048-per-skill-selectable-tools.md`, and the `.agentic/gotchas.md` entries
"Selectable tools: `null == all` …" and "Remote MCP servers: a live-socket cache …".

### 1. The key hierarchy is three levels, and nothing needs to parse it

`mcp` (all servers) ⊃ `mcp:{serverId}` (all tools of one server) ⊃ `mcp:{serverId}:{toolName}`.
Server ids are `Guid.NewGuid().ToString("n")` (`AgentApi.Mcp.cs:33`) — 32 hex chars, no colons —
but the code never splits a key: gating and expansion **construct** keys from a known
`(serverId, toolName)` pair and test set membership. Do not add a parser.

`ToolCatalog.IsMcpServerKey` is `StartsWith("mcp:")` (`ToolCatalog.cs:207`), so per-tool keys
already pass through `Sanitize` (`:258-265`) with **no code change** — only a doc comment and a
test. Confirm, don't assume.

### 2. Zero migration

`mcp:{id}` keeps its exact current meaning (all of that server's tools), so every stored
selection — legacy bare `mcp`, or an explicit `mcp:{id}` saved under ADR 0026 — keeps working with
no state touch. `KnownToolNames` is a **trailing optional** field (ADR 0045's `[Id(5)] Pinned`
precedent), so it needs none either — but see Context §7: that precedent's actual lesson is about
which *test* proves it.

### 3. ⚠ ONE source of tool names — the invariant the whole design rests on

The picker's checkboxes and the grain's wildcard expansion **must be driven from the same list**.
This is the `MergedSkills()` discipline, stated in this repo's own words at
`AgentApi.Tools.cs:36-37`: *"the picker and the gate must agree about which skills exist, or a key
the user can tick would gate nothing."*

A draft of this plan violated it — the endpoint read persisted `KnownToolNames` while the grain
expanded from live `_mcpServers` — and adversarial review showed the consequence: any
disagreement mints an `mcp:{id}:{tool}` key with **no checkbox**, which the next Save drops. That
is the ADR 0026 hazard reproduced one level down by the rule meant to close it.

**So `KnownToolNames` is the single source, and it reaches the grain through the provider**, not
through a second grain call: `McpServerTools` gains a trailing `KnownToolNames`, which
`UserMcpToolProvider` fills from the stored value (updated with what it just discovered on a
successful connect). `AgentGrain` never touches `IUserMcpConfigGrain` — it holds only
`_mcpServers` (`AgentGrain.cs:114,233`) — and this keeps it that way, adding **no** sixth
`OnActivateAsync` ordering-hazard field.

### 4. ⚠ The failure this plan exists to prevent, and why it becomes unreachable

The danger, found in review of an earlier draft:

1. User expands server S (tools `A`, `B`), unticks `B`, saves. Stored: `[…, mcp:{S}:A]` — **no**
   `mcp:{S}`, **no** bare `mcp` (`readToolChecks` emits only checked boxes, `tools.js:82-83`).
2. S's tool list is later unknown to the picker.
3. The picker renders master `mcp:{S}` with **zero children**, and both master states are wrong:
   unchecked → the Save drops `mcp:{S}:A` and **silently disables S**; checked ("all children
   ticked" is vacuously true over zero children) → the Save emits `mcp:{S}` and **silently
   re-grants `B`**, which the user deliberately deselected.

The `toolCatalog.length > 0` guard in `profile.js`/`agents.js` does **not** catch this — it fires
only on a *total* catalog failure, and here the catalog is fine.

**Step 2 is what makes this unreachable, via two invariants:**

- **Names are never erased.** They are written only after a *successful* connect (never on the
  `Skip`/`catch` empty-group paths, `UserMcpToolProvider.cs:100-105,139-149`) and are preserved
  across server edits (Context §5). So once a server's tools are known, they stay known.
- **Per-tool keys cannot exist before names do.** A user cannot select among tools they have never
  been shown, and the expansion cannot mint per-tool keys without names. So in the only state
  where children are unrendered — a server that has never connected — there is nothing to lose.

**Two rules still ship, because "unreachable" is an argument and arguments rot:**

- **Server-side (unit-testable).** `ExpandMcpWildcard` expands `mcp:{id}` into per-tool keys **only
  when that server's `KnownToolNames` is non-empty**, and leaves `mcp:{id}` **intact** otherwise.
  Never expand into an empty set — that is horn two above.
- **Client-side (backstop, source-text tested).** `readToolChecks` **carries forward** stored
  `mcp:{id}:*` keys for any server whose children were **not** rendered — covering the one residual
  path the invariants do not: a server that legitimately drops to *zero* tools after having had
  some. Be honest in the ADR that this half has no runtime test in this repo (no JS runner, by
  design), and that the server-side rule does **not** defend a fully-explicit partial selection —
  there is nothing there to expand. The invariants are the real defence; this is the belt.

### 5. ⚠ Editing a server must not wipe `KnownToolNames`

`PUT /api/mcp-servers/{id}` calls `SaveAsync(ToStored(...))`, and `ToStored`
(`AgentApi.Mcp.cs:122-134`) constructs a **fresh** `StoredMcpServer` from the request.
`UserMcpConfigGrain.SaveAsync` preserves exactly three fields (`UserMcpConfigGrain.cs:47-53`:
the two secret ciphertexts and the token). A new trailing `KnownToolNames` would therefore be
**destroyed on every edit** — a rename, a header rotation, an enable/disable round-trip — and not
return until that user's next conversation-grain activation reconnects.

That would make Context §4's "children not rendered" state the **routine** state right after any
MCP edit, which is exactly when a user opens the Tools picker. Add `KnownToolNames` to the `Keep`
set. AC12 pins it. This is the single easiest way to ship this feature broken.

### 6. Persisted names, not live reads — and the non-bumping write

**User's decision (revised during review):** `/api/tools` must **not** call `IUserMcpToolProvider`.
Verified reasons:

- `ConnectAllAsync` is a **sequential** `foreach` with a **10 s** `ConnectTimeout` per server
  (`UserMcpToolProvider.cs:32,94,131-132`) behind a per-user `SemaphoreSlim` (`:66`). Three dead
  servers ⇒ ~30 s before the picker is usable. (`loadTools()` is *not* awaited at boot —
  `main.js:84` holds the promise — so the app still starts; what stalls is the composer tool seed
  at `main.js:100-104` and the popover contents.)
- **A GET would become a write.** `ResolveOAuthAccessTokenAsync` refreshes a near-expiry OAuth
  token and calls `SaveAsync` (`:175-178`), bumping the overlay version — invalidating the tool
  cache for every warm grain of that user, on a page load.

So the endpoint reads `KnownToolNames` from the config grain alone, keeping today's shape, cost
and **route-matcher safety** exactly.

`RecordToolNamesAsync` must be **non-bumping** and a **no-op when unchanged**. Get the *reason*
right, because a wrong one invites a "simplification": with the no-op half present, a bumping
write would converge after one extra bump per genuine change — it is not an infinite loop. The
real reason is that **a names-only change must not invalidate the live-socket tool cache**
(`UserMcpToolProvider.cs:26-28`, and the live-socket gotcha) for every warm grain of that user.
Names are picker metadata, not connection config. Do not reuse `SaveAsync`.

Note `SaveAsync` bumps unconditionally today (`UserMcpConfigGrain.cs:62`, `BumpAsync`), and its
doc comment (`:78`), `IUserMcpConfigGrain.cs:22`, `UserMcpConfig.cs:63-65` and the test name
`Save_BumpsVersion_OnEveryWrite` (`UserMcpConfigGrainTests.cs:44`) all assert "every write bumps".
Adding a non-bumping write falsifies all four — fix them in this MR (hard rule 2).

### 7. `StoredMcpServer` is persisted state — prove the field on the right path

Two existing guards cover it: `SerializationCompatibilityTests.UserMcpConfigState_RoundTripsFieldForField`
(`:222`) restores the committed `UserMcpConfigStateBlob` (`:76`) and compares **field for field**
against `GoldenStates.UserMcpConfig()` (`GoldenStates.cs:92-97`). Add `KnownToolNames` to the
golden without regenerating the blob and it fails; omit it and the field is unproven.

**And heed what ADR 0045 actually taught** (§2 cites it, so honour it): its recorded lesson is that
the **Cosmos JSON** path is the real data-at-rest guard — "Newtonsoft binding a *missing* property
onto a positional record is the actual risk" — while the binary assertion is secondary.
`GrainStorageSerializerTests` holds golden *documents* only for `ConversationIndexState` and
`AgentGrainState` (`:135,:144`); `UserMcpConfigState` appears only in the `$type` theory (`:50`).
Add a pre-`KnownToolNames` golden JSON document for `UserMcpConfigState` — that is the assertion
that would actually catch a broken deserialization of existing users' MCP config.

### 8. `ToolDescriptor` gains a trailing optional field

`ToolDescriptor` is a response DTO only (`ToolCatalog.cs:272`) — **not** grain state, so no `[Id]`
and no migration. Add `string? ParentKey = null`, set to `mcp:{id}` on per-tool descriptors, so the
frontend groups children by `parentKey` rather than string-splitting keys. The child's `Label` is
the **tool name verbatim** (ADR 0048's "frontmatter `name` verbatim" precedent); `addCheck` uses
`textContent` (`tools.js:63`) so there is no injection concern.

### 9. Three source-text guards touch the rewritten code, not one

All in `tests/Agent42.Tests/SkillPickerAssetsTests.cs`; no JS runner exists here by design, so
these are the only things catching a dropped term. Widen them, never delete them:
- `:51` `CheckedState_KeepsTheThreeWayWildcardOr` → a **four-way** OR (fourth term: the child's own
  `mcp:{id}` being selected). Rename accordingly.
- `:35-42` `SkillsRenderUnderTheirOwnHeading` — asserts `addGroup("MCP servers", mcpServers)`, which
  the nested render replaces.
- `:22-31` `BuiltinsGroup_ExcludesBothPrefixedGroups` — survives, but `tools.js:51`'s
  `startsWith("mcp:")` filter now sweeps per-tool descriptors into the server group; it **must** be
  partitioned on `parentKey`.

### 10. The picker's stash must be per container

`renderToolChecks`/`readToolChecks` serve **three** containers — `profileToolChecks`
(`profile.js:51,78`), `agentToolChecks` (`agents.js:143,169`), `chatToolChecks` (`tools.js:89,94`)
— plus the round-trip at `tools.js:30`. A module-level single-slot stash for §4's preservation rule
would be correct only under an unstated "every read follows that container's own render" invariant
across three dialogs, and would fail silently the day that ordering changes — while AC18, a
source-text assertion, still passed. Key the stash on the container element (a `WeakMap`), and say
so in the assertion.

### 11. Cold start: a new server shows no tools until the next chat activation

Names are written when a conversation grain activates *and* the provider connects. So a
**newly-added server has no tool checkboxes in any picker until the user's next chat** — opening
the popover triggers nothing, which is the whole point of §6. Its master checkbox works throughout
(it is `mcp:{id}`, gated as today), so the server is fully usable meanwhile; only per-tool
granularity waits. This is the first-run experience for the headline feature: record it in the ADR
and, if cheap, surface it in the picker as a "tools not discovered yet" hint.

### 12. Agent chats have no in-UI remedy — mirror the ADR 0057 treatment

`BindAgentAsync` materialises `Sanitize(binding.EnabledTools ?? AllSelectableToolKeys)`
(`AgentGrain.cs:292-293`) and the composer Tools popover is hidden on agent chats
(`AgentApi.Tools.cs:62-63`). A per-tool selection damaged in the agent editor has no in-chat fix —
the same asymmetry ADR 0057 recorded rather than solved (gotchas `:1292-1295`). Note also that
`profile.js:51` and `agents.js:143` render the **raw stored list**, never `GetEnabledToolsAsync` —
so §4's server-side rule runs on the composer surface only, and the client backstop is what covers
the other two. Record the residual; do not invent a repair path.

### 13. `BuildAgent` complexity should go **down**

The gate becomes `tools.AddRange(server.Tools.Where(t => ToolCatalog.IsMcpToolEnabled(...)))`,
removing the inner `if` rather than nesting a loop. `cognitive_complexity_max` is 15 and S3776 is
an `error` (ADR 0050/0051) — verify with a build. `ExpandMcpWildcard` gaining a second level is
the one place complexity could rise; if it trips, extract the per-server branch (the
`DocumentIndexRegistration` remedy, never a ternary).

### 14. Rejected alternatives (user's calls)

- **Per-tool filtering as a management-level setting on `StoredMcpServer`** (Preferences → MCP
  dialog) — rejected: one setting for the whole user, not a per-conversation / per-agent layer.
  (`KnownToolNames` is *not* this: it is discovered metadata, never a user selection.)
- **Reading names live from the provider** — chosen initially, **reversed in review** (§6).
- **Client-only preservation as the sole guard** — rejected; §4 keeps a server-side rule and, more
  importantly, invariants that make the bad state unreachable.
- **Sending the picker's rendered scope with the save so the grain merges rather than replaces** —
  considered (it would make the partial case server-side-testable) and **rejected as
  disproportionate**: it would have to be threaded through three endpoints (`/api/conversations/
  {id}/tools`, `/api/profile`, `/api/agents`), and §4's invariants close the same hole with no API
  change. Revisit only if the invariants are ever broken.

## Steps

Steps 1–2 sequential. Step 3 depends on 2. Steps 4 and 5–6 are **parallelizable** after 3.

### Step 1 — `ToolCatalog`: the per-tool key and gate
Files: `src/Agent42.Agents/Tools/ToolCatalog.cs`
- `McpToolKey(serverId, toolName) => $"{McpServerKeyPrefix}{serverId}:{toolName}"`.
- `IsMcpToolEnabled(serverId, toolName, enabled)` — `enabled.Contains(Mcp) ||
  enabled.Contains(McpServerKey(serverId)) || enabled.Contains(McpToolKey(serverId, toolName))`.
  It **subsumes** `IsMcpServerEnabled`; keep the latter and say so in the doc comment.
- Add `ParentKey` to `ToolDescriptor` (Context §8).
- Update `Sanitize` / `IsMcpServerKey` doc comments.
- Then: `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` — replace the per-server `if` with a
  per-tool `Where`; rewrite the block comment for the three-level hierarchy.
- Verify: `--filter FullyQualifiedName~ToolCatalogTests` and `~AgentGrainTests`, plus
  `dotnet build Agent42.slnx` (S3776 is an error — Context §13).

### Step 2 — Persisted names, end to end
Files: `src/Agent42.Agents/Mcp/UserMcpConfig.cs`, `IUserMcpToolProvider.cs`,
`IUserMcpConfigGrain.cs`, `UserMcpConfigGrain.cs`, `src/Agent42.Backend/Mcp/UserMcpToolProvider.cs`,
`src/Agent42.Backend/Mcp/AgentApi.Mcp.cs`
- Trailing `[property: Id(12)] IReadOnlyList<string>? KnownToolNames = null` on `StoredMcpServer`;
  trailing `KnownToolNames` on `McpServerTools` (Context §3).
- `RecordToolNamesAsync` — **non-bumping**, **no-op when unchanged** (Context §6).
- `SaveAsync`: add `KnownToolNames` to the `Keep` set (Context §5).
- Provider: call `RecordToolNamesAsync` after a **successful** connect only; populate each group's
  `KnownToolNames` from the stored value so the grain and the picker share one list.
- Fix the four "every write bumps" doc/test-name claims (Context §6).
- Verify: `--filter FullyQualifiedName~UserMcpConfigGrainTests`, `~UserMcpToolProviderTests`,
  `~McpServersApiTests`, `~SerializationCompatibilityTests`

### Step 3 — `ExpandMcpWildcard`: the second level
Files: `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs`
- Expand `mcp:{id}` → per-tool keys **only when that server's `KnownToolNames` is non-empty**;
  leave `mcp:{id}` intact otherwise (Context §4). Read names from the group's `KnownToolNames`
  (step 2) — **not** from live `Tools`, or the picker and the projection diverge (Context §3).
- Preserve the recorded composition order `ExpandSkillsWildcard(ExpandMcpWildcard(…))` — two
  existing `Assert.Equal`s are order-sensitive (gotchas `:1277-1279`).
- Verify: `--filter FullyQualifiedName~AgentGrainTests`

### Step 4 — `GET /api/tools`: per-tool descriptors *(parallelizable with 5–6)*
Files: `src/Agent42.Backend/Tools/AgentApi.Tools.cs`
- Append one child descriptor per entry in each enabled server's `KnownToolNames`, `ParentKey` set
  to `mcp:{id}`, label = tool name verbatim. No provider, no network (Context §6).
- Verify: `--filter FullyQualifiedName~ToolSelectionApiTests`

### Step 5 — `tools.js`: nested render, collapse, preservation *(parallelizable with 6)*
Files: `src/Agent42.Backend/wwwroot/js/tools.js`
- Partition the catalog on `parentKey` (Context §9) — `startsWith("mcp:")` alone is now wrong.
- Render each server as a master row (`value="mcp:{id}"`) + a disclosure control + indented
  children, collapsed by default. The disclosure must **not** be an `<input type=checkbox>` —
  `readToolChecks` selects every checkbox in the container (`tools.js:83`) and would sweep it into
  the saved keys. Use a `<button>`.
- Master↔child sync **both ways**: master toggles all children; ticking the last child ticks the
  master; unticking any child unticks it. Without the child→master half, AC19's collapse never
  fires and Context §4's benefit silently does not apply.
- Four-way checked OR (Context §9).
- `readToolChecks`: the **collapse** rule (master checked ⇒ emit `mcp:{id}` only, drop children)
  **and** the **preservation** rule over a **per-container `WeakMap` stash** (Context §10). Note
  `refreshToolCatalog` calls `readToolChecks` as a *round-trip preserver* (`tools.js:30`), not a
  save — verify both rules behave there too.
- Verify: step 6's asset tests.

### Step 6 — styles + shell bump *(parallelizable with 5)*
Files: `styles.css`, `sw.js`, `tests/Agent42.Tests/PwaAssetsTests.cs`
- Indentation / disclosure styling matching the existing `.tool-check` idiom (`styles.css:1395-1421`).
- Bump `CACHE` → `agent42-shell-v24` (ADR 0041: mandatory for any `wwwroot/js/` change) **and**
  update the pinned literal at `PwaAssetsTests.cs:215`.
- Verify: `--filter FullyQualifiedName~PwaAssetsTests`

### Step 7 — DevHost seed + manual browser leg
Files: `tools/Agent42.DevHost/Program.cs`
- **Both halves are required, and the ids must match**, or the leg proves nothing:
  DevHost registers `new SingleUserMcpToolProvider()` (`Program.cs:61`), which never calls
  `RecordToolNamesAsync` (that lives in the Backend provider), and the MCP UI **cannot** set
  `KnownToolNames` (`McpServerUpsertRequest`/`ToStored` have no such field). So seed the config
  grain **programmatically** with `KnownToolNames`, pinning its id to the fake provider group's —
  `SingleUserMcpToolProvider.Tools` assigns the constant `"offline"`
  (`IUserMcpToolProvider.cs:69`), whereas a UI-minted id is a fresh Guid.
- Build fake tools as the tests do: `AIFunctionFactory.Create(() => "ok", "demo_search", "…")`
  (`AgentGrainTests.cs:2489-2490`); `Microsoft.Extensions.AI` is already referenced. Update the
  comment at `Program.cs:58-60`, which states the offline set is always empty. The seeded tools
  enter `BuildAgent`'s list for every offline conversation under `null == all` — expected.
- Drive http://localhost:61326 with headless Chrome over CDP (ADR 0048/0052/0054 precedent) and
  record measured values: the group expands/collapses; a partial selection round-trips through
  `PUT /api/conversations/{id}/tools`; an all-ticked server saves as bare `mcp:{id}`; **and a
  server with no `KnownToolNames` preserves stored per-tool keys across a Save** (Context §4).
- If no browser driver is available, **say so and record it as an unverified residual** (ADR 0043
  precedent) — do not claim a leg that was not run.

### Step 8 — Tests
Files: as listed in "Affected areas → Tests"
- Written alongside steps 1–6, not deferred (hard rule 3).
- AC5–AC9 reuse `NewClusterWithMcpServersAsync` / `FakeMcpTool` (`AgentGrainTests.cs:2489,2578-2603`).
- Verify: `dotnet test Agent42.slnx` and `dotnet format Agent42.slnx --verify-no-changes`

### Step 9 — Docs
- `/decide` → an ADR amending 0026 (and re-scoping 0021 once more). Record: the three-level
  hierarchy; the §3 one-source invariant and the §4 failure it prevents, **including the honest
  statement that the client backstop has no runtime test and the server rule does not defend a
  fully-explicit partial selection**; the collapse divergence from ADR 0048 and its limits; why
  names are persisted (§6) and the non-bumping write; the §11 cold-start window; the §12 agent-chat
  residual; the §14 rejected merge-scope alternative; egress **unchanged** (ADR 0024/0013).
- Update the `null == all` gotcha with a **fifth** trap and the MCP live-socket gotcha.
- Update the ADR 0026 bullet in `CLAUDE.md`. Move this spec to `.agentic/specs/completed/`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | `McpToolKey` and `IsMcpServerKey` agree | `McpToolKey_IsAlsoAnMcpServerKey` | `ToolCatalogTests.cs` |
| AC2 | `IsMcpToolEnabled` honours all three levels | `IsMcpToolEnabled_HonoursAllThreeLevels` | `ToolCatalogTests.cs` |
| AC3 | A per-tool key does not leak across servers | `IsMcpToolEnabled_DoesNotLeakAcrossServers` | `ToolCatalogTests.cs` |
| AC4 | `Sanitize` preserves per-tool keys, drops garbage | `Sanitize_PreservesPerToolKeys_AndDropsGarbage` | `ToolCatalogTests.cs` |
| AC5 | A selection naming one tool exposes only that tool | `PerToolSelection_ExposesOnlySelectedTool` | `AgentGrainTests.cs` |
| AC6 | A stored `mcp:{id}` still exposes every tool of that server | `LegacyPerServerKey_StillEnablesEveryToolOfThatServer` | `AgentGrainTests.cs` |
| AC7 | `null == all` still exposes every tool of every server | `NullSelection_StillEnablesEveryMcpTool` | `AgentGrainTests.cs` |
| AC8 | `ExpandMcpWildcard` expands `mcp:{id}` from **`KnownToolNames`** | `GetEnabledTools_ExpandsPerServerKeyFromKnownToolNames` | `AgentGrainTests.cs` |
| **AC9** | **Leaves `mcp:{id}` intact when `KnownToolNames` is empty** (§4, server half) | `GetEnabledTools_UnknownToolList_KeepsServerKeyIntact` | `AgentGrainTests.cs` |
| AC10 | `RecordToolNamesAsync` does **not** bump the version | `RecordToolNames_DoesNotBumpVersion` | `UserMcpConfigGrainTests.cs` |
| AC11 | `RecordToolNamesAsync` is a no-op when names are unchanged | `RecordToolNames_UnchangedNames_WritesNothing` | `UserMcpConfigGrainTests.cs` |
| **AC12** | **Editing a server preserves `KnownToolNames`** (§5) | `Edit_PreservesKnownToolNames` | `McpServersApiTests.cs` |
| AC13 | A failed connect does not erase previously known names | `FailedConnect_KeepsPreviouslyKnownToolNames` | `UserMcpToolProviderTests.cs` |
| AC14 | `UserMcpConfigState` round-trips field-for-field with the new field | existing `UserMcpConfigState_RoundTripsFieldForField`, golden updated | `SerializationCompatibilityTests.cs:222` |
| AC15 | A pre-`KnownToolNames` **Cosmos JSON** document still deserializes (§7) | `UserMcpConfigState_LegacyDocument_Deserializes` | `GrainStorageSerializerTests.cs` |
| AC16 | `GET /api/tools` returns one child per known tool, `ParentKey` set | `Tools_Catalog_IncludesPerMcpToolKeys` | `ToolSelectionApiTests.cs` |
| AC17 | A server with no `KnownToolNames` yields its server row and no tool rows | `Tools_Catalog_ServerWithoutKnownTools_KeepsServerRow` | `ToolSelectionApiTests.cs` |
| AC18 | The existing exact-set catalog assertion still passes unchanged | existing `Tools_Catalog_ReturnsSelectableKeys` | `ToolSelectionApiTests.cs` |
| AC19 | `cb.checked` is a **four-way** OR, all four terms present | `CheckedState_KeepsTheFourWayWildcardOr` | `SkillPickerAssetsTests.cs` |
| **AC20** | **`readToolChecks` preserves keys for unrendered servers, via a per-container `WeakMap`** (§4 backstop, §10) | `ReadToolChecks_PreservesKeysForUnrenderedServers` | `McpToolPickerAssetsTests.cs` |
| AC21 | `readToolChecks` collapses an all-ticked server to bare `mcp:{id}` | `ReadToolChecks_CollapsesFullServerToTheServerKey` | `McpToolPickerAssetsTests.cs` |
| AC22 | Ticking the last child ticks the master | `RenderToolChecks_SyncsMasterFromChildren` | `McpToolPickerAssetsTests.cs` |
| AC23 | The catalog is partitioned on `parentKey`, not `startsWith("mcp:")` | `RenderToolChecks_PartitionsOnParentKey` | `McpToolPickerAssetsTests.cs` |
| AC24 | `sw.js` `CACHE` bumped and the pinned literal updated | existing shell assertion, **edited** | `PwaAssetsTests.cs:215` |

AC19–AC23 are **source-text** assertions by necessity — no JS runner exists here by design
(ADR 0026/0048 precedent). AC9, AC12 and AC20 are the three that hold Context §4 and §5 together;
a reviewer should check those first.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time — never from memory.

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet cobertura ∩ `git diff` vs merge base) |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` S3776 at build time — an **error**, so `dotnet build` fails |
| `vulnerable_packages` | **0** | CI package-freshness job |
| `outdated_packages` | report (soft) | CI |
| Lint | clean | `dotnet format Agent42.slnx --verify-no-changes` |
| Tests | green | `dotnet test Agent42.slnx` |

Branch: `feature/per-mcp-tool-selection` — never commit to `main` (hard rule 1).

## Out of scope

- Per-tool gating of anything but MCP; skills stay at `skill:{name}` (ADR 0048).
- A management-level tool filter on `StoredMcpServer` (rejected, Context §14).
- Threading the picker's rendered scope through the save endpoints (rejected, Context §14).
- Reworking ADR 0048's skills precedent to also collapse to its wildcard — a separate call.
- Any change to `excludedPaths`, Bicep, secrets, or Cosmos containers. This feature adds none.
- Egress or capability changes: selection still only narrows; the capability intersection stays the
  authoritative gate (ADR 0013/0015).
- Capping the size of a stored selection. Per-tool keys are server-supplied and uncapped — noted as
  a residual; no cap exists for `skill:{name}` either.
- Eagerly discovering tools when a server is added (the Context §11 cold-start window).

## Review

Two `critic` passes. The first found a defect that reshaped the design; the second found that the
reshape had introduced a subtler version of the same problem. Dispositions:

### Pass 1

| # | Finding | Disposition |
|---|---|---|
| R1 | A **partial** per-tool selection is silently destroyed (or widened) when a server's children aren't rendered | **Accepted**; drove the redesign. Now Context §4. The original claim "no path silently disables a server" was false. |
| R2 | AC untestable: endpoint resolves from the API host container, not the silo | **Accepted, then dissolved** by the §6 redesign — the endpoint no longer touches the provider. |
| R3 | Contradicts ADR 0026's rejection of client-only expansion | **Accepted**; the server-side rule (§4) restores ADR 0026's mandated shape, so no supersession is needed. |
| R4 | `/api/tools` would become a write path and a latency risk | **Accepted, design changed** (§6). Verified `UserMcpToolProvider.cs:32,131-132,175-178`. |
| R5 | `PwaAssetsTests.cs:215` pins `agent42-shell-v23` | **Accepted** — step 6, AC24. |
| R6 | Three `SkillPickerAssetsTests` assertions touch the rewritten code | **Accepted** — Context §9. |
| R7 | `Tools_Catalog_ReturnsSelectableKeys` is exact set equality | **Accepted as a risk to verify** — AC18. |
| R8 | Missed `readToolChecks` caller: `tools.js:30` | **Accepted** — step 5. |
| R9 | Collapse trigger inconsistent; child→master sync missing | **Accepted** — step 5, AC22. |
| R10 | Agent chats never mentioned; the ADR 0048 claim overreached | **Accepted** — Context §12; §4 now states the limits. |
| R11 | Checkbox disclosure would be swept into saved keys; keys are uncapped | **Partly accepted** — `<button>` mandated (step 5); the size cap is an out-of-scope residual. |
| R12 | DevHost seed exercised the wrong arm | **Accepted** — superseded by step 7's rewrite. |

### Pass 2 (on the revision)

| # | Finding | Disposition |
|---|---|---|
| F1 | Expansion source (live `_mcpServers`) and catalog source (`KnownToolNames`) diverge, minting keys with no checkbox — the same hazard one level down | **Accepted; this is the most important finding in either pass.** Context §3 makes `KnownToolNames` the single source, carried to the grain on `McpServerTools` so no sixth ordering-hazard field appears. |
| F2 | The server-side rule is a no-op for a fully-explicit partial selection, and never runs on the profile/agent surfaces; AC17 (client, source-text) carried the whole feature | **Accepted.** Context §4 now rests on two **invariants** (names never erased; per-tool keys cannot precede names) that make the state unreachable, with the two rules as belt. The limitation is stated plainly here and in the ADR rather than papered over. The suggested merge-scope alternative was considered and **rejected as disproportionate** (§14). |
| F3 | `ToStored` rebuilds the record, so every server edit wipes `KnownToolNames` | **Accepted** — verified `AgentApi.Mcp.cs:122-134`, `UserMcpConfigGrain.cs:47-53`. Context §5, AC12. |
| F4 | A module-level stash is wrong across three containers | **Accepted** — Context §10 mandates a per-container `WeakMap`; AC20 asserts it. |
| F5 | Persisted-shape guards missing (`SerializationCompatibilityTests`, and ADR 0045's real lesson about the Cosmos JSON path) | **Accepted** — Context §7, AC14 **and** AC15. |
| F6 | Step 7's offline leg cannot work: DevHost's provider never records names, the UI cannot set them, and the ids won't match | **Accepted** — step 7 rewritten to seed the config grain programmatically with a pinned id. |
| F7 | The cold-start window is never stated | **Accepted** — Context §11, and an out-of-scope entry. |
| F8 | §5's "bumps forever" rationale is falsifiable given its own no-op half | **Accepted** — Context §6 now gives the real reason (not invalidating the live-socket cache). |
| F9 | Doc debt inside the changed files, not in the docs step | **Accepted** — step 2 fixes all four claims (hard rule 2). |
| F10 | Child descriptor `Label` unspecified | **Accepted** — Context §8: the tool name verbatim. |

---

## Where the implementation diverged from the plan

Executed 2026-08-29 on `feature/per-mcp-tool-selection`. Shipped as ADR 0058. Four divergences, one of
them substantial.

### 1. AC14 — the golden blob was NOT regenerated (deliberate, follows ADR 0045)

The plan's Context §7 stated the dilemma ("add `KnownToolNames` to the golden without regenerating the blob
and it fails; omit it and the field is unproven") and AC14 said "golden updated". Both were set aside in
favour of the precedent §7 itself cites approvingly.

`SerializationCompatibilityTests` blobs are committed **pre-feature artifacts**; that is their entire value.
Regenerating `UserMcpConfigStateBlob` to carry the new field would forfeit the legacy-guard property for
every *other* member it covers, to prove one field on the path §7 correctly calls secondary. ADR 0045 did
the opposite for `Pinned` — kept the blob, asserted the new member reads back as its default — and that is
what shipped here (`Assert.Null(actual.KnownToolNames)`).

The field is not left unproven: **AC15** (`UserMcpConfigDocument_WrittenBeforeKnownToolNames_StillDeserializes`,
a committed pre-feature **Cosmos JSON** document) is the data-at-rest guard, and
`KnownToolNames_WrittenWithToList_SurviveTheCosmosJsonRoundTrip` proves a written value round-trips.

### 2. A pre-existing hazard was found, escalated wrongly, then narrowed on evidence

Implementing `RecordToolNamesAsync` with `KnownToolNames = [.. toolNames]` made
`RecordToolNames_UnchangedNames_WritesNothing` fail with
`Unable to find a constructor to use for type <>z__ReadOnlyArray`1`. A collection expression targeting
`IReadOnlyList<T>` compiles to a compiler-internal type that Newtonsoft writes into `$type` and cannot
construct on read, stranding the whole document.

This was initially reported to the user as a **live production bug across four handlers**
(`AgentApi.Users.cs`, `AgentApi.UserSkills.cs`, `AgentApi.Mcp.cs`, `AgentApi.Models.cs`), and fixes were
applied there on the user's instruction. That report was **wrong**, and the fixes were reverted: a
mutation check showed the handlers are safe because their values cross a **grain-call boundary**, where
Orleans' binary serializer rebuilds them as a plain `List<T>` before any write. Measured — with the fix
removed, the stored runtime type still read back as `List<string>`.

The genuinely exposed case is state a grain builds **inside itself** and writes directly, which is exactly
what this feature added (and why `AgentGrain.BuildAgent.cs` already carried a lone `?.ToList()`). Net
result: one real fix (`toolNames.ToList()`, mutation checked), two guards
(`StateBuiltInsideAGrain_WithACollectionExpression_CannotBeReadBack`, and an assertion in
`ProfileToolSave_IsReadableBackFromStorage` pinning the laundering rather than assuming it), and a new
gotcha. **No unrelated handler was changed.**

### 3. Step 7's DevHost seed needed `StartAsync`, and gained a second server

The seed is a grain call, so running it before the silo starts NREs inside Orleans'
`InsideRuntimeClient.SendRequest`. `app.Run()` became `await app.StartAsync(); …seed…; await
app.WaitForShutdownAsync();`.

A **second** seeded server (`offline-cold`, no `KnownToolNames`, absent from the tool provider) was added
beyond the plan, so the cold-start UI *and* Context §4's preservation case are both exercisable offline.
Without it the plan's fourth measured outcome had nothing to measure.

### 4. `ExpandOneServerKey` was extracted (Context §13's remedy, as predicted)

`ExpandMcpWildcard` gaining a second level pushed toward the S3776 ceiling. The per-server branch was
extracted into a private helper — the remedy §13 named — rather than nesting or a ternary. Build is clean
with S3776 at `error`.

### Not divergences, recorded to close them out

- **AC18 held.** `Tools_Catalog_ReturnsSelectableKeys` (exact set equality, review finding R7) passes
  unchanged — it runs for a user with no MCP servers.
- **Context §1 held.** `IsMcpServerKey`/`Sanitize` needed only doc comments, no code change; confirmed by
  `McpToolKey_IsAlsoAnMcpServerKey` and `Sanitize_PreservesPerToolKeys_AndDropsGarbage`.
- **The browser leg was run**, not skipped — headless Chrome over CDP against DevHost. All four outcomes
  step 7 asked for were measured, plus a mutation check on the preservation rule (the one rule with no
  automated test). Values are in ADR 0058.
- Three tests were added beyond the AC table where a mutation check showed a gap:
  `RecordToolNames_UnknownServer_IsANoOp`, `Save_WithNullKnownToolNames_KeepsStoredNames`,
  `SkippedServer_KeepsPreviouslyKnownToolNames`, plus `SuccessfulConnect_RecordsDiscoveredToolNames` and
  `GetEnabledTools_ExplicitPerToolKey_RoundTripsUnchanged`.

### 5. The `critic` pass found a shipped bug; two client rules changed after it

The step-5 `critic` review found seven items. Two were real defects in the implementation, four were doc
debt, one was an evidence gap. All were verified before acting — two of the seven were rejected or narrowed
on measurement rather than accepted as written.

**F1 (accepted, real bug).** `ExpandMcpWildcard` removes `mcp:{id}` when it expands, and the composer renders
from exactly that projection — so on any **reopened** conversation the server master drew unchecked above a
full set of ticked children, and `readToolChecks` then declined to collapse. Reproduced over CDP
(`master:false, children:[true,true]`, saving the explicit per-tool list). Fixed by deriving the master from
its children at render time, not only on a change event. The original browser leg missed it because it only
ever used a *fresh* conversation, which seeds from `allToolKeys()` and does contain `mcp:{id}` — a real gap in
the step-7 script, not just in the code.

**F3/F4 (accepted, real).** The preservation rule stashed *every* selected `mcp:`-prefixed key absent from the
catalog, so a deleted or management-disabled server's keys were carried forward forever — falsifying
`ToolCatalog.Sanitize`'s "a stale key self-heals" comment (which this diff had itself extended to per-tool
keys) and silently changing ADR 0026 behaviour for disabled servers. Fixed by scoping the stash to keys whose
**server row is present**, rather than by editing the doc to match the code. Verified over CDP: the live
server's partial key survives while `mcp:ghost` and `mcp:ghost:zombie_tool` both drop.

**F2, F5 (accepted, doc debt).** Four stale "three-way OR" claims and two references to the renamed
`CheckedState_KeepsTheThreeWayWildcardOr` were fixed in `gotchas.md` and `CLAUDE.md` (hard rule 2); the two
remaining hits are inside *completed specs*, which are historical records and were left alone. A guard comment
overstated what the fourth OR term protects — the immediate next Save is actually lossless; the loss is one
interaction away — and was corrected to say so.

**F6 (accepted, precision).** The new gotcha's type mapping was ambiguous and quoted a failure message from a
different probe than the one it described. Replaced with the measured table (four expression shapes → three
compiler-internal types) and both verbatim failure strings.

**F7 (accepted as an evidence gap; the claim survived).** The laundering measurement used `SharedApiFixture`,
which injects the TestCluster's **external** client, so handler→grain calls fully serialize — while production
co-hosts the silo (`builder.UseOrleans`), where those calls are local and Orleans **deep-copies** instead.
Different mechanism, and only one had been measured. Orleans' `DeepCopier<T>` was then measured directly:
it converts `<>z__ReadOnlyList` → `List<string>` too, so the rule holds on both topologies. A permanent test
(`OrleansDeepCopy_LaundersACollectionExpressionList_LikeSerializationDoes`) now covers the production path,
and the gotcha cites both mechanisms instead of generalising from one.

Net: 1024 tests (up from 1023), two client-side behaviour fixes, no change to the server-side design.
