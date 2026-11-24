---
id: 0058
date: 2026-08-29
status: accepted
tags: [mcp, tools, per-user, selection, ux, no-migration, persistence]
supersedes: []
extends: [0024, 0048]
amends: [0021, 0026]
amended_by: [0064]
---

# Per-MCP-tool selectable tools — `mcp:{serverId}:{toolName}` keys

> **Amended by [ADR 0064](0064-mcp-connection-test-endpoint.md) (2026-09-04) — the cold start below is
> CLOSED.** The rule that `GET /api/tools` never calls `IUserMcpToolProvider` is **unchanged and still
> load-bearing**; 0064 adds a separate user-pressed POST that connects to one named server, and a successful
> test records the names. So "no per-tool checkboxes until the next chat activation" now reads "…until the
> next chat activation **or the user presses Test**". 0064 also adds the second writer of `KnownToolNames`,
> which must agree with this one — and the rule that an **empty** list is never recorded, because recording
> `[]` would erase the names and make this ADR's unreachable state reachable.

> **Amends [ADR 0026](0026-per-mcp-server-selectable-tools.md) and re-scopes
> [ADR 0021](0021-selectable-tools-three-layer.md) once more.** Nothing is overturned: `mcp:{id}` keeps its
> exact ADR 0026 meaning ("all of this server's tools") and the bare `mcp` wildcard is untouched. What
> changes is that the selectable set gains a third level beneath them. ADR 0026's mandated shape — a
> **server-side** expansion rule, not client-only — is preserved and extended, not replaced.

## Context

ADR 0026 made each MCP server individually selectable as an `mcp:{id}` key. A server, though, exposes many
tools, and the useful line is often *inside* one: use a Jira server's `jira_get_issue` but not its
`jira_delete_issue`. Per-server granularity was the friction point the user asked to remove.

This is the third instalment of a shape the repo already has — ADR 0026 did servers, ADR 0048 did skills —
but it is **not** a mechanical copy of either, and the difference is the whole design. Both of those gate
over a list that is always knowable locally: a config row, a directory on disk. **MCP tool names are only
knowable by connecting.** Every hard part below follows from that one fact.

## Decision

A three-level key hierarchy, each level a superset of the next:

```
mcp                      all servers        (the ADR 0026 wildcard, unchanged)
 └─ mcp:{serverId}       all tools of one server   (the ADR 0026 key, meaning unchanged)
     └─ mcp:{serverId}:{toolName}   one tool       (new)
```

`ToolCatalog.IsMcpToolEnabled(serverId, toolName, enabled)` grants a tool if **any** level is selected. It
subsumes `IsMcpServerEnabled`, which is kept for callers that have no tool name in hand. `BuildAgent`'s
per-server `if` becomes a per-tool `Where`, so cognitive complexity goes **down**, not up.

**Nothing parses a key.** Gating and expansion *construct* a key from a known `(serverId, toolName)` pair
and test set membership. `IsMcpServerKey` stays a `StartsWith("mcp:")` prefix test, which is why per-tool
keys pass through `Sanitize` with no code change — the per-tool key was deliberately shaped as a per-server
key plus a segment so that would be true.

**Zero migration.** `mcp:{id}` keeps its exact ADR 0026 meaning, so every stored selection — legacy bare
`mcp`, or an explicit `mcp:{id}` — keeps working untouched. `KnownToolNames` is a trailing optional
`[Id(12)]` on `StoredMcpServer` (the ADR 0045 shape).

### One source of tool names — the invariant everything rests on

The picker's checkboxes and the grain's wildcard expansion **must be driven from the same list**, or the
expansion mints an `mcp:{id}:{tool}` key the picker has no checkbox for, and the very next Save drops it.
That is the ADR 0026 hazard reproduced one level down by the rule meant to close it. (A draft of this design
had exactly that defect: the endpoint read persisted names while the grain expanded from live tools.)

So `StoredMcpServer.KnownToolNames` is the single source, and it reaches the grain **through the provider**,
not through a second grain call: `McpServerTools` gains a trailing `KnownToolNames` that `UserMcpToolProvider`
fills. `AgentGrain` never touches `IUserMcpConfigGrain` and gains **no sixth `OnActivateAsync` ordering-hazard
field**.

### Names are persisted, discovered, and never erased

`GET /api/tools` reads `KnownToolNames` from the config grain and **never calls `IUserMcpToolProvider`.**
Two verified reasons, both fatal to the alternative:

- `ConnectAllAsync` is a **sequential** `foreach` with a **10 s** timeout per server behind a per-user
  semaphore. Three dead servers ⇒ ~30 s before the picker is usable.
- **A GET would become a WRITE.** Resolving an InteractiveOAuth token refreshes it and calls `SaveAsync`,
  bumping the overlay version — invalidating the live-socket tool cache for every warm grain of that user,
  on a page load.

`RecordToolNamesAsync` is therefore a **non-bumping** write, and a **no-op when unchanged**. Get the reason
right: the no-op half alone would already stop a bumping write from recurring forever, so "it would bump on
every activation" is *not* it. The reason is that a names-only change must not tear down live MCP sockets.
Names are picker metadata, not connection config.

Two invariants make names durable:

- **Written only on a successful connect** — never on the skip/catch empty-group paths, which carry the
  *previous* names forward. This is also why `KnownToolNames` is not derived from `Tools`: on a failed
  connect `Tools` is empty and the names are not.
- **Preserved across a server edit.** `PUT /api/mcp-servers/{id}` calls `SaveAsync(ToStored(...))`, and
  `ToStored` builds a **fresh** record from the request, which carries no names. Without a `Keep` clause in
  the grain, every rename, header rotation or enable/disable round-trip would erase them. **This is the
  single easiest way to ship this feature broken**, and `Edit_PreservesKnownToolNames` pins it.

### The failure this exists to prevent, and why it is unreachable

The danger, found in adversarial review of an earlier draft:

1. A user expands server S (tools `A`, `B`), unticks `B`, saves. Stored: `[…, mcp:{S}:A]` — no `mcp:{S}`,
   no bare `mcp` (the picker emits only checked boxes).
2. S's tool list later becomes unknown to the picker.
3. The picker renders master `mcp:{S}` with **zero children**, and both master states are wrong: unchecked
   → the Save drops `mcp:{S}:A` and **silently disables S**; checked (vacuously "all children ticked" over
   zero children) → the Save emits `mcp:{S}` and **silently re-grants `B`**, which the user deselected.

The `toolCatalog.length > 0` guard in `profile.js`/`agents.js` does **not** catch this: it fires only on a
*total* catalog failure, and here the catalog is fine.

Two invariants make the state unreachable: **names are never erased** (above), and **per-tool keys cannot
exist before names do** — a user cannot select among tools they were never shown. So in the only state where
children go unrendered (a server that has never connected) there is nothing to lose.

Two rules ship anyway, because "unreachable" is an argument and arguments rot:

- **Server-side.** `ExpandMcpWildcard` expands `mcp:{id}` into per-tool keys **only when that server's
  `KnownToolNames` is non-empty**, and leaves `mcp:{id}` **intact** otherwise. Never expand into an empty
  set — that is horn two.
- **Client-side.** `readToolChecks` **carries forward** stored `mcp:{id}:*` keys for any server whose
  children were not rendered, over a **per-container `WeakMap`** (three containers share these functions; a
  module-level slot would depend on an unstated cross-dialog ordering invariant and fail silently).
  Deliberately scoped to keys whose **server row is present in the catalog**: a key for a deleted or
  management-disabled server still drops on the next Save, exactly as ADR 0026 always dropped a stale
  `mcp:{deletedId}`. Preserving every unknown `mcp:` key instead would make stored selections grow
  monotonically and never shed dead ones — and would quietly falsify `ToolCatalog.Sanitize`'s "a stale key
  self-heals" doc comment, which is now stated with the one exception this rule creates.

**Be honest about the limits.** The server-side rule does **not** defend a fully-explicit partial selection —
there is nothing there to expand. And `profile.js`/`agents.js` render the raw stored list, never
`GetEnabledToolsAsync`, so the server rule runs on the composer surface only; the client backstop is what
covers the other two. The invariants are the real defence; the rules are the belt.

### The picker

Each server renders as a master checkbox + a disclosure **button** + indented children, collapsed by
default. A `<button>`, never a checkbox: `readToolChecks` collects every checkbox in the container and would
sweep a checkbox disclosure into the saved keys.

- The catalog is partitioned on **`ToolDescriptor.ParentKey`** (a new trailing optional field; the DTO is
  not grain state, so no `[Id]`), never on the key prefix — a server row and its tool rows both start
  `mcp:`.
- `cb.checked` becomes a **four-way** OR; the new fourth term is "this row's parent key is selected", which
  is what makes a stored `mcp:{id}` render as *all of its tools ticked* rather than as an empty group.
- Master ↔ child sync runs **both ways**, and the master is **also derived from its children at render
  time** — not only on a change event. The render-time half is the one that bites: `GetEnabledToolsAsync`
  expands `mcp:{id}` *away* into per-tool keys, and that projection is what the composer renders from on
  every reopen, so without it the master draws unchecked above a full set of ticked children and the
  collapse rule below silently stops firing. This shipped wrong for one review cycle and was caught by the
  `critic` pass, not by testing — the browser leg only ever exercised a *fresh* conversation, which seeds
  from `allToolKeys()` and therefore does contain `mcp:{id}`. See Verification.
- `readToolChecks` **collapses** an all-ticked server back to the bare `mcp:{id}`, preserving ADR 0026's
  "all of this server's tools, including future ones" meaning.
- Child labels are the tool name **verbatim** (the ADR 0048 precedent); `addCheck` uses `textContent`.

## Consequences

- **Egress is unchanged** (ADR 0024/0013). Selection can only narrow, never grant, and the capability
  intersection stays the authoritative gate.
- **Cold start** *(narrowed by [ADR 0064](0064-mcp-connection-test-endpoint.md))*: a newly added server has
  **no tool checkboxes until the user's next chat activation — or until they press Test**, because names are
  learned when something connects and *opening the picker* still triggers nothing, which remains the whole
  point of not connecting from the endpoint. ADR 0064 adds the deliberate second way to make it happen: a
  user-pressed `POST /api/mcp-servers/{id}/test`, which is also run automatically after a successful save and
  on the OAuth return. Its master checkbox works throughout, so the server is fully usable meanwhile; only
  per-tool granularity waits. The picker says so ("tools not discovered yet") rather than showing an empty
  disclosure.
- **Agent chats have no in-UI remedy** for a per-tool selection damaged in the agent editor: `BindAgentAsync`
  materialises the list and the composer popover is hidden on agent chats. The same asymmetry ADR 0057
  recorded rather than solved. Recorded, not repaired.
- **Per-tool keys are server-supplied and uncapped.** A server advertising very many tools grows the stored
  selection. No cap exists for `skill:{name}` either; noted as a residual, not addressed.
- **Stale keys self-heal, with one deliberate exception.** A key for a server that is gone from the catalog
  (deleted or management-disabled) is inert and clears on the next Save. A `mcp:{id}:{tool}` key for a server
  that still exists but whose tools are not currently known is **kept** — that is the backstop above, and it
  persists until the server's tools are discovered again.

## Alternatives considered

- **A management-level per-tool filter on `StoredMcpServer`** (Preferences → MCP dialog) — **rejected by the
  user**: one setting for the whole user, not a per-conversation / per-agent layer. (`KnownToolNames` is not
  this — it is discovered metadata, never a user selection.)
- **Reading tool names live from the provider in `GET /api/tools`** — chosen initially, **reversed in
  review** for the latency and hidden-write reasons above.
- **Client-only preservation as the sole guard** — rejected; ADR 0026 explicitly rejected client-only
  expansion, so a server-side rule ships too.
- **Sending the picker's rendered scope with the save so the grain merges rather than replaces** —
  considered (it would make the partial case server-side-testable) and **rejected as disproportionate**: it
  would thread through three endpoints (`/api/conversations/{id}/tools`, `/api/profile`, `/api/agents`) and
  the invariants close the same hole with no API change. Revisit only if the invariants are ever broken.
- **Collapsing `skill:{name}` to its wildcard the same way** — out of scope, a separate call.

## Verification

Full suite green (1023 tests), `dotnet format` clean, `dotnet build` clean with S3776 at `error`.

**The offline browser leg was run** — headless Chrome over CDP against DevHost, which seeds both a connected
demo server and a never-connected one. Measured:

| What | Measured |
|---|---|
| Server rows rendered | `2` |
| Disclosure element | `BUTTON`, label `▸ 2`, `aria-expanded="false"` |
| Children hidden by default | `true` |
| Expand → collapse | `hidden:false, aria:"true", label:"▾ 2"` → `hidden:true, aria:"false", label:"▸ 2"` |
| Children checked under `null == all` | `[true, true]` |
| Never-connected server | no toggle; hint `"tools not discovered yet"` |
| Untick one child | master → `false`; saved `["mcp:offline:demo_search"]` (partial, no server key) |
| Re-tick last child | master → `true`; saved `["mcp:offline"]` (**collapsed**) |
| Stored key for an unrendered server, after an unrelated toggle | **survives**: `["mcp:offline-cold:secret_tool"]` |

That last row is the ADR's most important claim and the one with no automated test, so it was **mutation
checked**: with the preservation rule removed, the same script reports `persistedAfterSave: []` — the
selection silently destroyed. With it, the key survives.

**A second round was run after the `critic` pass**, covering the two cases the first round did not reach.
Both were genuine defects in the first implementation, not merely unmeasured:

| What | Before the fix | After |
|---|---|---|
| Master on a **reopened** conversation (rendered from the `GetEnabledToolsAsync` projection) | `master:false` above `children:[true,true]` — contradictory, and the next save emitted `["mcp:offline:demo_search","mcp:offline:demo_delete"]` instead of collapsing | `master:true`, next save `["mcp:offline"]` |
| Stale keys for a server absent from the catalog (`mcp:ghost`, `mcp:ghost:zombie_tool`) | preserved forever | **dropped**, while `mcp:offline-cold:secret_tool` (live server, unrendered children) still **survives** |

The first of those is why the render-time master derivation exists. It was invisible to the original leg
because a fresh conversation seeds its selection from `allToolKeys()`, which contains `mcp:{id}`; only a
reopen goes through the projection that expands it away.

`Edit_PreservesKnownToolNames` and the grain's `Keep` clause were mutation checked the same way (both tests
fail when the clause is removed), as was the `.ToList()` in `RecordToolNamesAsync` (see the gotcha).
