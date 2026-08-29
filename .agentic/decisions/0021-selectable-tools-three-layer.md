---
id: 0021
date: 2026-07-17
status: accepted
tags: [agents, tools, capabilities, user-preferences, agent-definition]
---

# Selectable tools per chat, per user, and per agent

> **Amended by [ADR 0026](0026-per-mcp-server-selectable-tools.md) (2026-07-19) —
> static-catalog scope only.** `ToolCatalog` remains the authoritative source for the **static**
> keys, but `GET /api/tools` is no longer fully static: it appends a per-user, per-server
> `mcp:{id}` descriptor for each of the user's enabled MCP servers. The `null == all` sentinel and
> three-layer model are unchanged.

> **Amended by ADR 0057** — `memory` (long-term memory, ADR 0056) joins the catalog as a selectable key
> and is the **one documented exception** to the "then `BuildAgent` intersects it with model capability"
> rule below. It gates an `AIContextProvider` rather than a tool, so it never passes through the `Tools`
> filter: a text-only model with `memory` selected still gets memory. Everything else here stands.

## Context
Every tool is unconditionally registered in `AgentGrain.BuildAgent`, gated only by model
capability (ADR 0013/0015) — there is no user control over *which* tools an assistant may use. The
user asked to choose the tool set and have it remembered at three layers: a **user-level default**
(every new regular chat), a **per-conversation override** (change tools inside an open chat), and a
**per-agent** selection (stored on the persona). This is a *selection* layer, not a new security
boundary — the capability gates stay authoritative.

## Decision
Add a nullable **`EnabledTools`** (a list of curated **catalog keys** — `ToolCatalog`) to
`UserProfile` `[Id(1)]`, `AgentDefinition` `[Id(8)]`, `AgentBinding` `[Id(4)]`, and
`AgentGrainState` `[Id(10)]`. **`null` means "all selectable tools"** (today's behaviour → no
migration); a non-null list is an explicit set. `AgentGrain` resolves the effective set as
**override ?? user-default ?? all**, then `BuildAgent` **intersects** it with model capability
exactly as before. The user default is read once on activation from `IUserProfileGrain` (before the
first `BuildAgent`). An agent chat **snapshots** the agent's tools into conversation state at bind
time, materialized to a concrete set (ADR 0003 — editing an agent affects only future chats).
Amends the tool-registration description of ADR 0013/0015; the security boundary is unchanged.

## Alternatives considered
- **Per-function granularity** (toggle each tool individually) — rejected: too fine for the user
  intent ("let it search the web") and it would couple the UI to the tool inventory. One curated
  key maps to one or more registered tools instead.
- **Represent "all" as an empty list, default new fields to `[]`** — rejected: an empty list is
  "none", and defaulting existing state to `[]` would silently strip every tool from every existing
  user / agent / conversation. `null == all` is the only sentinel that needs no migration.
- **Keep the selection off the binding and read it live, like ADR 0016's `Continuous`** — rejected:
  history mode is *routing*, but a tool set is *persona*. Snapshotting it (ADR 0003) makes an agent
  self-contained and stops it inheriting the user's per-chat default.

## Consequences
- **No migration.** Every new field is a trailing `[Id]` with a `null` default (same pattern as
  ADR 0016 `Continuous`, ADR 0020 `GeneratedImages`); old state deserializes to `null == all`.
- **Capability stays the outer gate.** A selected tool is registered only if the model supports it —
  a text-only model still gets none; `code_interpreter` still needs a hosted-tools model. Selection
  can only ever *narrow*.
- **Disabling `skills` must drop the `AgentSkillsProvider`, not just a label** — it is a tool
  (`load_skill`/`read_skill_resource`/`run_skill_script`), so leaving it on hands the model a schema
  it can't use. The document RAG `TextSearchProvider` is **not** a tool and stays on whenever the
  conversation has documents, regardless of selection (ADR 0008).
- **User-default staleness (accepted).** The default is read on activation (mirrors default-model
  resolution), so a Preferences change reflects in a warm regular chat only on its next activation.
- **Headline reach is unchanged.** Selection cannot grant a capability; it only turns registered
  tools off. Nothing here relaxes the ADR 0011 exfiltration surface or the ADR 0013 egress framing.
- **Adding a new catalog key later is semi-breaking for existing explicit savers.** Because the UI
  represents "all selected" as the **full explicit list** (never `null`), a user or agent who saved
  "all" today stores the current keys explicitly. A never-saved (`null`) user/agent auto-gets a newly
  added key via `null == all`; an explicit saver does **not** (their stored list predates it). So a
  future contributor adding a `ToolCatalog` key must decide whether existing explicit selections should
  be migrated to include it — the new key is off-by-default for them, which may or may not be intended.
