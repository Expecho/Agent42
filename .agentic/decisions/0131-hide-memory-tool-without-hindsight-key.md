---
id: 0131
date: 2026-11-15
status: accepted
tags: [memory, tools, selection, ui]
supersedes: []
extends: [0057]
amends: [0057]
---

# Hide the memory tool when no Hindsight key is set

## Context
ADR 0057 decision 3 kept the `memory` checkbox in the picker even when the operator configured no `Hindsight:ApiKey`
(today's production state), because a catalog that depended on operator config would be a first for `GET /api/tools`, and a
key that appears and disappears interacts badly with stored explicit selections. The cost is a control that does nothing:
users tick "Long-term memory", nothing is remembered, and only Preferences → Configuration says why.

## Decision
`GET /api/tools` leaves out the `memory` descriptor when `HindsightIntegration.Client` is null (or the integration is not
registered). Only the descriptor is hidden. `memory` stays in `ToolCatalog.AllSelectableToolKeys`, so the `null == all`
sentinel, stored selections and `Sanitize` are unchanged, and the grain still attaches no provider without a client.

## Alternatives considered
- **Keep the row and rely on the Configuration tab (ADR 0057 decision 3).** Rejected: a dead control.
- **Remove `memory` from `AllSelectableToolKeys` when unconfigured.** Rejected: `Sanitize` would drop the key from every
  selection saved meanwhile, and the catalog and the gate would disagree.
- **Carry `memory` forward in `readToolChecks` the way unrendered per-tool MCP keys are.** Not done: it touches
  `wwwroot/js` and the service worker cache for a case the Consequences below accept.

## Consequences
- A user who presses Save in the picker while memory is hidden saves an explicit list without `memory`. When the operator
  later sets the key, that user has memory OFF until they tick it (the "new key lands OFF" rule, CLAUDE.md cross-cutting
  rule 11). Users who never saved still resolve `null == all` and get it. Arguably the safer default for egress.
- The Configuration tab still reports the key's state, so the operator can see why the row is missing.
- Tests: `Tools_Catalog_HidesMemory_WithoutHindsightKey` and `Tools_Catalog_IncludesMemory_WhenHindsightConfigured`.
