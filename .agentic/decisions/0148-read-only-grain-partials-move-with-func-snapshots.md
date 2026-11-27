---
id: 0148
date: 2026-11-27
status: accepted
tags: [refactor, structure, agents, orleans, tools, testing, no-migration]
supersedes: []
extends: [0147]
amends: [0111, 0098]
amended_by: [0150]
---

# Read-only grain partials move into per-activation classes, reading state through `Func`s

## Context
ADR 0147 moved the leaf tool sets that never touch `_state`, and left the `_state`-reading partials to plan cc-10. A survey
of the tier-B partials (2026-11-27) found two that only **read** state: the chat delivery hand-off
(`AgentGrain.ChatDelivery.cs`) and the `read_document` tool (`AgentGrain.ReadDocumentTool.cs`). The rest write `_state`
(`FetchableUrls`, `WebDocumentNames`, `Artifacts`, `McpApps`) or touch `_session`/`_agent`.

## Decision
`ChatDeliveryRelay` and `ReadDocumentTool` are classes built with `new` in `OnActivateAsync`, as ADR 0147 does. They never
hold `_state`: what they need of it comes through a `Func` that reads it live on every call. A captured list would go stale,
because `ClearStateAsync` replaces state and `ClearDocuments` clears in place, and `SetChatOriginAsync` can set the origin
after activation. `ReadDocumentTool` takes **two** name `Func`s, not one union: the web list alone decides the untrusted
marking (ADR 0066), so merging them would mark a web document as trusted.

Left in the grain: `SetChatOriginAsync` (a public write-once grain method), `AwayReplyText`, and the static compaction
helpers `CompactReadDocumentResult` and `PartArgument` (they share `ToolText` with the other compaction branches, ADR 0147).
The relay must be built before `PushTools`, which receives `relay.DeliverNotificationAsync`.

## Alternatives considered
- **A narrow state-mutation port now** — only the write-based partials need it, and none of them moves in this change, so a
  port would have no caller. It stays the plan for any later move of `WebTools` and onwards; the `FetchableUrls` allow-list
  (ADR 0011/0066) must keep being consulted exactly as it is.
- **Passing the lists captured at activation** — goes stale, see above.

## Consequences
- No tool key, description or parameter changed (`ToolCatalogSnapshotTests` and its golden file are untouched).
- The write-based partials (`WebTools`, `WebDocumentTool`, `Documents`, `Attachments`, `ArtifactTools`, `McpApps`,
  `ModelRouting`) stay partials. Moving them is a separate plan with the port above.
