# Plan — Per-MCP-server selectable tools

planned-against: main @ 883a183

## Overview

Today the ADR 0021 tool-selection layer (user-default tools, per-conversation "Tools"
popover, per-agent editor) exposes a **single** "MCP servers" checkbox (`ToolCatalog.Mcp`)
that turns **all** of a user's remote MCP servers on/off as one group. The user wants to
pick **individual** MCP servers in that picker instead of all-or-none.

The management-level per-server `Enabled` flag (Preferences → MCP servers edit dialog)
stays as-is — it is the **master on/off**. This plan adds per-server granularity to the
**selection** layer only: each management-enabled MCP server becomes its own selectable
key `mcp:{serverId}` in the picker, so a chat/agent can use a subset.

Why now: the user asked for it; the whole-group gate is a known limitation of ADR 0024.

## Design (the load-bearing decisions)

**Key scheme.** Per-server key is `mcp:{serverId}`. The existing `ToolCatalog.Mcp` key
(`"mcp"`) is **repurposed from a UI group toggle to a wildcard "all servers" meta-key**:

- It **stays in `AllSelectableToolKeys`** — so the `null == all` default keeps enabling
  **all** servers with no migration, and it is the wildcard the grain honours.
- It is **removed from `Descriptors`** — it is no longer a single UI checkbox.
- Gating rule: a server's tools are enabled iff
  `SupportsTools && (enabled.Contains(Mcp) || enabled.Contains("mcp:{serverId}"))`.

This wildcard is what preserves backward-compat with **zero migration**: (1) a `null`
`EnabledTools` resolves to `AllSelectableToolKeys` which contains `Mcp` → all servers on;
(2) any legacy stored explicit selection that contains the literal `"mcp"` string keeps
meaning "all servers". The **UI never emits the bare `mcp` key** — an explicit selection
sends per-server `mcp:{id}` keys (the picker renders the full explicit list for "all
selected", per the null==all gotcha). The wildcard is only ever reached via null-default
or legacy state.

**Server attribution.** The grain currently stores `_mcpTools` as a **flat**
`IReadOnlyList<AITool>` with no server identity, so it cannot gate per server. `McpToolSet`
must carry per-server grouping (a new `McpServerTools(ServerId, Tools)` record).
`UserMcpToolProvider.ConnectAllAsync` already iterates per server — it just flattens today;
keep the grouping instead.

**The catalog becomes per-user for MCP.** `/api/tools` is currently a static endpoint. It
must append one descriptor per **management-enabled** MCP server
(`ToolDescriptor("mcp:{id}", server.Name, RequiresHostedTools:false)`), read from the
caller's `IUserMcpConfigGrain`. A management-**disabled** server produces no tools (the
provider filters `s.Enabled`), so it is excluded from the picker — a checkbox that does
nothing would be a lie.

**Sanitize.** `ToolCatalog.Sanitize` must preserve `mcp:{id}` keys (it drops them today —
they are not in `AllSelectableToolKeys`). It stays **static** (no per-user coupling): keep
the static keys in catalog order, then pass through any `mcp:`-prefixed key (deduped). A
stale `mcp:{deletedId}` is inert (gates no server) and self-heals when the user re-saves
from the UI.

**The wildcard must be EXPANDED before it reaches the picker (critic Finding 1 — the
load-bearing fix).** `GetEnabledToolsAsync` returns `EffectiveEnabledTools()`, which for any
null/all conversation materialises `AllSelectableToolKeys` — a list that contains the bare
`mcp` wildcard and **no** `mcp:{id}` keys. The picker renders only keys present in the
catalog (`renderToolChecks`), and after step 1 removes `mcp` from `Descriptors` **no
checkbox binds to `mcp`**. So a user who merely opens Preferences / the composer Tools
popover on an existing (null) chat and presses Save — without touching any MCP box — would
emit a list with the wildcard dropped and no per-server key added, silently flipping "all
servers on" → "all servers off." That is silent data loss, not an edge case (the null==all
gotcha says "all selected" is *always* stored as the full explicit list). Two coordinated
fixes close it:

1. **Server-side (primary, testable):** `GetEnabledToolsAsync` **expands** a present `mcp`
   wildcard into `mcp:{serverId}` for each of the user's management-enabled servers, so the
   client always receives explicit per-server keys that round-trip losslessly. The grain
   gets the complete enabled-server id list from `_mcpServers` — so the provider must
   include an entry (empty-tools group) for **every** management-enabled server, even one
   that is unreachable / not-yet-OAuth-connected, otherwise the wildcard would expand to
   fewer keys than the catalog shows. `EffectiveEnabledTools()` (the gate's source) is left
   returning the raw wildcard — `IsMcpServerEnabled` honours it — so the gate is unchanged;
   only the client-facing read expands.
2. **Client-side (defence for the editor surfaces):** `renderToolChecks` treats a present
   `mcp` wildcard as "all `mcp:{id}` checkboxes checked." This covers the profile-default
   and agent-editor pickers, which render the **raw stored** `EnabledTools` (a legacy list
   could still carry a bare `mcp`), not the grain-expanded effective set.

**`/api/tools` must be re-fetched after MCP CRUD (critic Finding 3).** The client fetches
`/api/tools` once at init; the MCP add/edit/delete/enable paths update `mcpServers` but never
re-fetch the catalog, so a newly added server has no picker checkbox until a full reload.
Re-fetch `toolCatalog` after MCP CRUD.

## Affected areas (verified against the tree at 883a183)

- `src/Agent42.Agents/ToolCatalog.cs` — remove `Mcp` from `Descriptors`, keep it in
  `AllSelectableToolKeys` as the wildcard; add `McpServerKey(id)` / `IsMcpServerKey(k)` /
  `IsMcpServerEnabled(id, enabled)` helpers; update `Sanitize` to pass through `mcp:` keys.
- `src/Agent42.Agents/IUserMcpToolProvider.cs` — `McpToolSet` gains per-server grouping
  (`McpServerTools` record + `AllTools` convenience); `SingleUserMcpToolProvider` gains a
  way to expose multiple server groups (keep the `Tools` setter mapping to one default-id
  group so existing tests compile).
- `src/Agent42.Backend/UserMcpToolProvider.cs` — `ConnectAllAsync` returns per-server
  groups instead of a flat list, including an **empty-tools group for every
  management-enabled server that is skipped** (unsupported auth / unreachable /
  not-yet-connected) so the grain's enabled-server id list is complete (needed for wildcard
  expansion); `GetForUserAsync` builds an `McpToolSet` of groups. Cache shape unchanged.
- `src/Agent42.Agents/AgentGrain.cs` — `_mcpTools` (flat) becomes `_mcpServers`
  (`IReadOnlyList<McpServerTools>`), resolved in the **same place** in `OnActivateAsync`
  (before the first `BuildAgent` — the ordering hazard is unchanged); `BuildAgent` replaces
  the single `enabled.Contains(Mcp)` gate with a per-server loop using
  `ToolCatalog.IsMcpServerEnabled`; `GetEnabledToolsAsync` expands the `mcp` wildcard into
  `mcp:{serverId}` keys (from `_mcpServers`) before returning to the client.
- `src/Agent42.Backend/AgentApi.cs` — `/api/tools` becomes an async handler that reads the
  user's MCP overlay and appends per-server descriptors.
- `src/Agent42.Backend/wwwroot/app.js` — `renderToolChecks` (a) groups MCP server checkboxes
  under a subheading so they read as servers, not built-in tools, and (b) treats a present
  `mcp` wildcard as "all `mcp:{id}` checked" (editor-surface defence); MCP CRUD paths
  re-fetch `/api/tools` so a new server gets a checkbox without a reload. (`allToolKeys()`
  already picks up the extra catalog keys for the null==all default.)
- Docs (same MR): new ADR (0026) amending ADR 0024's whole-group gate; update the two
  gotchas ("Selectable tools: null==all…" and "Remote MCP servers: …group-gated catalog
  exception…"); update the ADR 0024 bullet in `CLAUDE.md`.

## Context & constraints the executor cannot cheaply re-derive

- **Two distinct layers** exist and only one changes. Management `Enabled`
  (`StoredMcpServer.Enabled`, edited via the MCP dialog, filtered in
  `UserMcpToolProvider.ConnectAllAsync` `.Where(s => s.Enabled)`) is the master switch and
  is **out of scope**. The ADR 0021 selection layer is what gains per-server granularity.
- **Do NOT represent "all" as an empty list, and do NOT default any field to `[]`.** The
  `null == all` sentinel is the whole reason there is no migration (gotcha "Selectable
  tools: null==all…"). The wildcard `mcp` key living in `AllSelectableToolKeys` is what
  carries "all servers" through the null path — keep it there.
- **`_mcpServers` must be resolved in `OnActivateAsync` BEFORE the first `BuildAgent`**, for
  the identical reason `_mcpTools`, `_userDefaultEnabledTools` and `_chatClientFactory` are
  (same gotcha) — `ReceiveReminder` reuses that `_agent`. Keep it at the current resolution
  site (`AgentGrain.cs:150`), just change the stored shape.
- **MCP tool names are dynamic and absent from `ToolNameToKey`** — they must NOT go through
  `IsToolEnabled` (which keeps unmapped names unconditionally, bypassing the picker). The
  per-server loop in `BuildAgent` is inserted at the **same position** as today's group
  gate: **after** the `IsToolEnabled` filter, **before** the `Tools = SupportsTools ? …`
  capability intersection. Capability (`SupportsTools`) stays the outer gate — a text-only
  model gets zero MCP tools regardless of selection.
- **The guard test** `EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate_OrIsAnMcpTool`
  carves MCP tools out by the provider's set (by name), never by catalog key — that stays
  true; MCP tools still have no catalog-map entry.
- **The MCP endpoint carries no SSRF guard** (config, not a request) — unchanged, do not
  add one.
- Governing docs to read first: `.agentic/decisions/0021-selectable-tools-three-layer.md`,
  `.agentic/decisions/0024-config-driven-remote-mcp-servers.md`, and the two gotchas named
  above (`.agentic/gotchas.md`).

## Steps (ordered)

1. **`ToolCatalog`: wildcard + per-server helpers + Sanitize.** In `ToolCatalog.cs`: remove
   the `Mcp` entry from `Descriptors` (keep it in `AllSelectableToolKeys`); add
   `McpServerKey(string id)`, `IsMcpServerKey(string key)`,
   `IsMcpServerEnabled(string serverId, ISet<string> enabled)`; change `Sanitize` to append
   pass-through `mcp:`-prefixed keys (deduped) after the static keys. **Verify:** new unit
   test `ToolCatalogTests` (create if absent) — `Sanitize` preserves `mcp:{id}` and drops
   garbage; `Descriptors` no longer contains `mcp` but `AllSelectableToolKeys` does;
   `IsMcpServerEnabled` honours both the wildcard and the per-server key.
   `dotnet test Agent42.slnx --filter FullyQualifiedName~ToolCatalog`.

2. **`McpToolSet` per-server grouping.** In `IUserMcpToolProvider.cs`: add
   `McpServerTools(string ServerId, IReadOnlyList<AITool> Tools)`; change `McpToolSet` to
   hold `IReadOnlyList<McpServerTools> Servers` with an `AllTools` convenience and `Empty`;
   update `SingleUserMcpToolProvider` to expose server groups (keep a `Tools` setter that
   maps to a single default-id group for existing tests, add a `Servers` setter for
   multi-server tests). **Verify:** compiles; `dotnet build Agent42.slnx`. (Behavioural
   coverage lands in steps 3–4.)

3. **`UserMcpToolProvider` returns groups (incl. skipped servers).** `ConnectAllAsync`
   builds a `List<McpServerTools>` — one per management-enabled server: successfully-
   connected servers carry their truncation-wrapped tools, **skipped** servers (unsupported
   auth / not-yet-connected / unreachable) carry an **empty** tool list but keep their id.
   `GetForUserAsync` stores/returns an `McpToolSet` of groups. Cache eviction/disposal
   unchanged (empty groups hold no connection). **Verify:** update `UserMcpToolProviderTests`
   for the new shape and add a case asserting a skipped server still appears as an empty
   group; `dotnet test Agent42.slnx --filter FullyQualifiedName~UserMcpToolProvider`.

4. **`AgentGrain` per-server gate + wildcard expansion.** Replace `_mcpTools` with
   `_mcpServers` (`IReadOnlyList<McpServerTools>`), resolved at the same activation site
   from `GetForUserAsync(...).Servers`; in `BuildAgent`, replace the single `mcpEnabled`
   block with a per-server loop gated by `ToolCatalog.IsMcpServerEnabled(server.ServerId,
   enabled)` under `capabilities.SupportsTools`, at the same position (after `IsToolEnabled`,
   before the capability intersection). In `GetEnabledToolsAsync`, expand a present `mcp`
   wildcard into `mcp:{serverId}` for each `_mcpServers` group before returning (leave
   `EffectiveEnabledTools` returning the raw wildcard for the gate). **Verify:** existing MCP
   grain tests stay green (they use the wildcard `ToolCatalog.Mcp`); new
   `PerServerSelection_EnablesOnlySelectedServer` (two servers, select only one `mcp:{id}` →
   only that server's tools reach the model); new
   `GetEnabledTools_NullSelection_ExpandsWildcardToPerServerKeys` (null selection + two
   servers → returns `mcp:{id1},mcp:{id2}`, no bare `mcp`).
   `dotnet test Agent42.slnx --filter FullyQualifiedName~AgentGrain`.

5. **`/api/tools` per-user MCP descriptors.** Change the `/api/tools` handler to async,
   read `IUserMcpConfigGrain(userId)`, and append one `ToolDescriptor("mcp:{id}",
   server.Name, false)` per management-enabled server after `ToolCatalog.Descriptors`.
   **Verify:** update `ToolSelectionApiTests.Tools_Catalog_ReturnsSelectableKeys` (no bare
   `mcp` now — expect the 5 static keys with no servers configured) and add a case: with an
   enabled MCP server configured, the catalog includes `mcp:{id}` labelled with the server
   name; a disabled server is excluded.
   `dotnet test Agent42.slnx --filter FullyQualifiedName~ToolSelectionApi`.

6. **Frontend grouping + wildcard rendering + catalog refresh.** In `app.js`: (a) adjust
   `renderToolChecks` so MCP server keys (`key.startsWith("mcp:")`) render under a small
   "MCP servers" subheading beneath the built-in tools; (b) in the same function, treat a
   present `mcp` wildcard in the selected set as "all `mcp:{id}` checkboxes checked" (editor-
   surface defence); (c) re-fetch `/api/tools` (call `loadTools()` then re-render open
   pickers) after MCP add/edit/delete so a new server gets a checkbox without a reload.
   `allToolKeys()` and the null==all default already pick up the extra catalog keys.
   **Verify (mandatory manual, JS is not unit-testable here):** run DevHost
   (`dotnet run --project tools/Agent42.DevHost`); add two MCP servers; confirm both render
   as checkboxes in Preferences → Tools and the composer popover **without a page reload**;
   confirm opening a picker on a fresh (all) chat shows every server checked and pressing
   **Save without touching a box keeps all servers enabled** (the Finding-1 regression);
   confirm unchecking one server persists and that server's tools drop. Also
   `dotnet test Agent42.slnx --filter FullyQualifiedName~McpServersAssets` if it asserts markup.

7. **Docs (same MR).** `/decide` → new ADR `0026-per-mcp-server-selectable-tools.md`
   amending ADR 0024's whole-group gate **and** ADR 0021's "static single source of truth"
   framing of `ToolCatalog` (`/api/tools` is now per-user for the MCP portion). Record the
   wildcard-key alternative and why it was chosen (zero migration), the expand-before-picker
   requirement, and the reverse-breaking-change note (removing a rendered key while stored
   state still carries it — the Finding-1 hazard). Update the two gotchas (null==all;
   MCP group-gated exception) to describe per-server keys + the wildcard + the expand-on-read
   rule, and update the ADR 0024 bullet in `CLAUDE.md`. **Verify:** manual re-read /
   `/audit-docs` scope; `dotnet format Agent42.slnx --verify-no-changes`.

Steps 1–2 are foundational; 3 and 5 are independent once 1–2 land; 4 depends on 1–3;
6 depends on 5; 7 is last.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | A per-server selection reaches only the selected server's tools | `PerServerSelection_EnablesOnlySelectedServer` (new) | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC2 | The wildcard `mcp` / null-default still enables all servers at the gate (no migration) | existing `McpTools_ReachModel_WhenEnabledAndToolsCapable` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC2b | An explicit selection WITHOUT any mcp key drops all MCP tools | existing `ExplicitToolsWithoutMcp_DropsMcpTools`, `McpDisabled_DropsMcpTools` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC3 | A no-tools model gets zero MCP tools regardless of selection | existing `NoToolModel_GetsNoMcpTools` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC4 | `_mcpServers` resolved before the first `BuildAgent` (ordering) | existing `McpTools_ResolvedBeforeFirstBuildAgent` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC5 | `/api/tools` lists per-server `mcp:{id}` descriptors for enabled servers, excludes disabled; no bare `mcp` | updated + new case in `Tools_Catalog_*` | `tests/Agent42.Tests/ToolSelectionApiTests.cs` |
| AC6 | `Sanitize` preserves `mcp:{id}`, drops garbage; `Descriptors` has no bare `mcp` | new `ToolCatalogTests` | `tests/Agent42.Tests/ToolCatalogTests.cs` (new) |
| AC7 | Provider returns per-server groups, incl. an empty group for a skipped server | updated `UserMcpToolProviderTests` | `tests/Agent42.Tests/UserMcpToolProviderTests.cs` |
| AC8 | Guard: every registered tool maps to a key, is `get_date`, or is an MCP tool | existing `EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate_OrIsAnMcpTool` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC9 | **Round-trip safety:** a null-selection read expands the wildcard to per-server keys (so a no-op picker Save cannot disable all servers) | new `GetEnabledTools_NullSelection_ExpandsWildcardToPerServerKeys` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC10 | Picker UI: per-server checkboxes render (grouped), a wildcard renders as all-checked, new servers appear without reload | manual (DevHost) — JS not unit-testable here | step 6 verification |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` @ 883a183)

- new_issues: 0
- new_security_issues: 0
- duplication_pct_max: 3
- coverage_new_code_pct_min: 80
- cognitive_complexity_max: 15
- vulnerable_packages: 0 (hard block)
- Gates: build, tests, `dotnet format --verify-no-changes`, doc-path integrity (CI hard block).

## Out of scope

- The management-level per-server `Enabled` flag and its dialog — unchanged (master on/off).
- Surfacing a quick enable/disable toggle in the server list (the user chose the picker
  layer only).
- Any migration of stored `EnabledTools` — the wildcard key makes it unnecessary.
- SSRF guarding of MCP endpoints — deliberately absent, unchanged.
- Per-server labels beyond the server name; grouping/iconography beyond a subheading.

## Divergence from plan (filled at execution)

- **Step order:** the type change to `McpToolSet` (step 2) breaks its consumers, so steps 3–4
  were implemented **before** the first successful full build rather than building after step 2.
  No scope change; the plan's "build after step 2" verify was folded into the step-3/4 test runs.
- **New public surface on `ToolCatalog`:** added `McpServerKeyPrefix` const alongside
  `McpServerKey`/`IsMcpServerKey`/`IsMcpServerEnabled` (plan named the three methods; the prefix
  const keeps the `"mcp:"` literal in one place).
- **`SingleUserMcpToolProvider`:** kept the `Tools` setter (maps to a single group under
  `DefaultServerId = "offline"`) and added a `Servers` setter, as planned. The default id is
  `"offline"`, surfaced as a public const for tests.
- **`ExpandMcpWildcard`** dedups (`Distinct`) in case a selection ever carries both the wildcard
  and an explicit `mcp:{id}` — not called out in the plan but harmless and safer.
- **Frontend refresh:** `refreshToolCatalog()` re-fetches `/api/tools` and re-renders the
  Preferences Tools tab in place (preserving current checks) so an added/removed server appears
  without a reload; the composer popover and agent editor re-render on their next open. A new
  `.tool-check__heading` CSS rule styles the "MCP servers" subheading.
- **DevHost caveat:** offline the `SingleUserMcpToolProvider` returns no server groups, so a
  config-added server appears in `/api/tools` (which reads the config grain) but not in the
  grain's `_mcpServers` (the fake provider). Wildcard-expansion of a *connected* server is
  therefore proven by unit test `GetEnabledTools_NullSelection_ExpandsWildcardToPerServerKeys`,
  not by the DevHost drive; the DevHost drive proved the per-user catalog + no-bare-`mcp` leak.

## Review

Critic pass (fresh-context) — **not a clean pass**; four findings, all accepted and folded
into the plan:

- **Finding 1 (High) — the `mcp` wildcard has no checkbox, so a no-op picker Save silently
  disables all MCP servers.** `GetEnabledToolsAsync` hands `AllSelectableToolKeys` (which
  contains bare `mcp`, no `mcp:{id}`) to the picker for every null conversation; with `mcp`
  removed from `Descriptors` no checkbox binds it, so the next Save drops it. **Accepted —
  fixed:** server-side wildcard expansion in `GetEnabledToolsAsync` (step 4, AC9) + client
  wildcard-rendering defence in `renderToolChecks` (step 6), and the provider now emits an
  empty group per skipped server so the id list for expansion is complete (step 3).
- **Finding 2 (Medium) — AC/test mapping missed the regression and miscited AC2.** Accepted:
  AC2 split into AC2 (gate honours wildcard) + AC2b (explicit-without-mcp drops), added
  `McpDisabled_DropsMcpTools`, and added AC9 (round-trip expansion) as the regression pin.
- **Finding 3 (Low) — `/api/tools` becomes per-user but is fetched once; new servers won't
  appear without a reload.** Accepted — fixed: step 6 re-fetches `/api/tools` after MCP CRUD.
- **Finding 4 (Low) — supersession scope understated.** Accepted: step 7 now also amends
  ADR 0021's "static single source of truth" framing and records the reverse-breaking-change
  (removing a rendered key while stored state carries it).

The critic confirmed the grain-gate half is sound: keeping `Mcp` in `AllSelectableToolKeys`
preserves the wildcard through `Sanitize` and the gate; the ordering hazard, guard-test
carve-out, and SSRF non-guard are all preserved. No findings rejected.
