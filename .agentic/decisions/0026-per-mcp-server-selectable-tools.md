---
id: 0026
date: 2026-07-19
status: accepted
tags: [mcp, tools, per-user, ux, no-migration]
amends: 0021, 0024 (MCP gate + static-catalog scope only — see "Amends")
---

# Title: Per-MCP-server selectable tools — `mcp:{id}` keys with an `mcp` wildcard

> **Amends [ADR 0024](0024-config-driven-remote-mcp-servers.md) and
> [ADR 0021](0021-selectable-tools-three-layer.md).** Two things are re-scoped, not overturned:
> 0024's "MCP tools are gated as **one whole `mcp` group**" becomes per-server (`mcp:{id}`) with
> the `mcp` key repurposed as an all-servers **wildcard**; and 0021's "`ToolCatalog` is the
> **static** single source of truth" is re-scoped — the MCP portion of `GET /api/tools` is now
> per-user/dynamic, while the static keys stay authoritative and centralised in `ToolCatalog`.
> Both ADRs stay `status: accepted`. No state migration (the wildcard preserves every existing
> selection).

## Context
ADR 0024 gated **all** of a user's remote MCP servers behind a single `ToolCatalog.Mcp`
group key: the ADR 0021 tool picker (user-default / per-conversation / per-agent) showed one
"MCP servers" checkbox that turned every server on or off together. Users want to pick
**individual** servers there — use server A in this chat, not server B. The management-level
`Enabled` flag (Preferences → MCP servers dialog) stays the master on/off; this is about the
**selection** layer only.

Two facts shape the design. (1) `EnabledTools` uses the `null == all` sentinel (ADR 0021) —
any migration that reinterprets stored selections is a regression risk. (2) `ToolCatalog`
was a **static** catalog, but MCP servers are **per-user and dynamic**, so the selectable set
can no longer be fully static.

## Decision
Each management-enabled MCP server is individually selectable as a per-server key
**`mcp:{serverId}`**. The former group key `Mcp` (`"mcp"`) is **repurposed into a wildcard
"all servers" meta-key**:

- It stays in `AllSelectableToolKeys` (so `null == all` and any legacy selection carrying
  literal `"mcp"` keep enabling **every** server — **zero migration**) but is **removed from
  `Descriptors`** (no longer a rendered checkbox).
- `BuildAgent` gates **per server**: a server's tools are kept when
  `SupportsTools && (enabled.Contains(Mcp) || enabled.Contains("mcp:{id}"))`
  (`ToolCatalog.IsMcpServerEnabled`), inserted at the same position as the old group gate
  (after the `IsToolEnabled` filter, before the capability intersection). MCP tools remain
  the documented group-gated exception to the "every registered tool maps to a catalog key"
  guard — dynamic names, absent from `ToolNameToKey`.
- `GET /api/tools` becomes **per-user** for the MCP portion: it appends one
  `ToolDescriptor("mcp:{id}", server.Name, false)` per management-enabled server. A disabled
  server contributes no tools, so it gets no checkbox.
- `McpToolSet` carries **per-server grouping** (`McpServerTools(ServerId, Tools)`); the
  provider emits a group for **every** management-enabled server, an **empty** one for a
  skipped/unreachable server, so the grain has the complete server-id list.

**The wildcard must be expanded before it reaches the picker.** `GetEnabledToolsAsync`
returns the effective set (which for a null selection materialises `AllSelectableToolKeys`,
carrying the bare `mcp` with no `mcp:{id}`). The picker renders only catalog keys, and `mcp`
has no checkbox — so a no-op Save would drop the wildcard and disable **every** server. The
grain therefore **expands** a present `mcp` into `mcp:{serverId}` for each of the user's
servers before returning (a read-only, client-facing projection; the gate still honours the
raw wildcard). A client-side rule in `renderToolChecks` treats a stored `mcp` as "all
servers checked" too, covering the profile/agent editors that render the raw stored list.

## Alternatives considered
- **Keep the single `mcp` group key** — rejected: it is exactly the all-or-none the user
  wants gone.
- **Migrate stored selections** (rewrite `mcp` → per-server keys at read/write) — rejected:
  the wildcard-key approach preserves every existing selection with no migration and no state
  touch, the same discipline as the ADR 0013 legacy-model-id fallback.
- **Drop `mcp` entirely** — rejected: `null == all` resolves to `AllSelectableToolKeys`, which
  needs a token meaning "all servers" for users who never configured a selection; and legacy
  explicit selections carrying `"mcp"` would silently lose all servers. Keeping `mcp` as the
  wildcard is what makes both cases correct.
- **Validate `mcp:{id}` against the live server list in `Sanitize`** — rejected: `Sanitize`
  is static (no per-user coupling); a stale `mcp:{deletedId}` is inert and self-heals on the
  next save. Making Sanitize per-user would spread the server list into the persist path.
- **Client-only wildcard expansion** — rejected as the sole fix: it is not unit-testable
  here (no JS runner), and the hazard is silent data loss. Server-side expansion in
  `GetEnabledToolsAsync` is the testable primary; the client rule is defence for the editor
  surfaces that read raw stored lists.

## Consequences
- **Enables** per-server selection in all three ADR 0021 layers with no new state field and
  no migration. A newly added server is on for `null`-default (wildcard) chats and off for
  chats with an explicit selection until re-selected — consistent with ADR 0021's explicit-
  selection semantics.
- **Amends ADR 0024** — its "gated as a WHOLE group by one `mcp` key" decision is now
  per-server with an `mcp` wildcard fallback.
- **Amends ADR 0021** — `ToolCatalog` is no longer the *complete* static single source of
  truth; the MCP portion of `GET /api/tools` is per-user/dynamic. The static keys are still
  authoritative and centralised in `ToolCatalog`.
- **New reverse-breaking-change note:** removing a rendered catalog key (`mcp`) while stored
  state still carries it is worse than ADR 0021's "adding a key is semi-breaking" — without
  the expand-before-picker rule it is silent data loss. Any future removal of a rendered key
  must ship the same expansion/migration.
- **Egress unchanged** (ADR 0024/0013): per-server selection only narrows what a conversation
  can reach; it never grants beyond model capability.
