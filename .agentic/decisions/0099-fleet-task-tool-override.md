---
id: 0099
date: 2026-10-17
status: accepted
tags: [fleet, tools, selectable-tools, mcp, blob, no-migration, no-egress]
supersedes: []
amends: [0097]
---

# A fleet task can override the user's default tool selection

## Context
ADR 0097 decision 6 gates a fleet task's `web_search`/`fetch_web_page` and MCP tools by the user's DEFAULT chat tool
selection (`UserProfile.EnabledTools`, ADR 0021). On 2026-10-17 the user wanted to narrow or widen that for one task
without changing the chat default, and to keep the chat default as the fleet's default.

## Decision
A task's selectable tools resolve as **task override ?? `profile.EnabledTools` ?? all**. `FleetTaskRecord.EnabledTools`
(Blob JSON, null by default, so older records need no migration) holds the override, sanitized by
`ToolCatalog.Sanitize` in `FleetTaskService.SubmitAsync`. `FleetToolCatalog.BuildAsync` uses it and skips the profile
read when the task has one. The fleet composer has a tools picker that offers only `web_search` (which gates
`fetch_web_page` too) and the MCP servers with their tools.

- **It sends `enabledTools` only when the user changed the picker** and both `/api/tools` and `/api/profile` loaded.
  An untouched picker sends nothing, so the task follows the default when it runs, and ADR 0021 `null == all` still
  grants a newly added MCP server. A failed catalog never turns into "no tools", and `[]` always means none.
- **A sent override drops every key the picker doesn't show** (skills, memory, …). This is on purpose: the fleet
  ignores them, and the override belongs to one task and is never written back to the profile. That is unlike the
  main picker's carry-forward of unrendered keys, which protects a persisted default.
- A server whose tools are all ticked, or that lists no tools yet (the cold-start window), is sent as `mcp:{id}`, so it
  also grants tools added later (ADR 0026). Otherwise only its ticked `mcp:{id}:{tool}` keys are sent.
- "Run again" re-sends the record's override as is (null stays null). A follow-up pre-selects the override of the
  task it follows up. The task header shows "Tools: your defaults" or "Tools: N selected".
- Copilot fleet agents are unaffected (built-in tools only, ADR 0097 decision 7). No new egress: this only narrows or
  widens tools that were already gated.

## Alternatives considered
- **A separate fleet default in Settings**: the user rejected it. A second default to keep in sync with the chat one
  adds UI for little gain.
- **Always send the picker's full state**: every task would freeze an explicit list. That silently drops `null == all`,
  so a newly added MCP server would never reach a task the user didn't touch.
- **Reuse `src/Agent42.Backend/wwwroot/js/tools.js`**: `fleet.js` is a classic script outside the ES-module graph (ADR 0041, `sw.js`
  skips `/fleet`), so it can't import the module. Turning the page into a module for one picker wasn't worth it, so
  `fleet.js` has a small local picker with the same parent/child semantics.

## Consequences
- The user can run one task with fewer or more of their tools without touching the chat default.
- Two pickers implement the same `IsMcpToolEnabled` semantics, one in `tools.js` and one in `fleet.js`. A change to the
  key scheme has to update both.
- The planner still picks each agent's tools from the task's pool. Per-agent choice by the user stays out of scope.
