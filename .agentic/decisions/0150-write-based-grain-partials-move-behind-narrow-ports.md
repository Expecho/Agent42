---
id: 0150
date: 2026-11-27
status: accepted
tags: [refactor, structure, agents, orleans, tools, security, exfiltration, testing, no-migration]
supersedes: []
extends: [0147, 0148]
amends: [0147, 0148, 0011, 0066, 0067]
---

# Write-based grain partials move behind narrow ports (plan cc-12)

## Context
ADR 0147 said a tool that must write `_state`, `_session` or `_agent` stays a partial, and ADR 0148's Consequences left
`WebTools`, `WebDocumentTool`, `ArtifactTools`, `McpApps` and part of `Documents` as partials "until a narrow state-mutation
port". Plan cc-12 builds that port, one partial per MR, riskiest first.

## Decision
A write-based partial may move out of `AgentGrain` when everything it writes goes through a **port**: a small public class
built with `new` in `OnActivateAsync`, taking a `Func` that reads `_state.State` live (a captured list goes stale,
`ClearStateAsync` replaces state; ADR 0148). The port exposes named operations and never the list, and one port serves one
state area. The grain stays the only owner of `_state`, `_session` and `_agent`.

**Step 1: `FetchableUrlList` + `WebTools`.** `FetchableUrlList` is the only accessor of `FetchableUrls` and
`SeededFetchableUrls` (consult, remember, remember-seeded, each trimming only its own list). `UrlNormalizer.Normalize` is
the one normalization, shared with the web-document ingest. `WebTools` holds `web_search` and the three feeders. The
allow-list is consulted exactly as before (whole normalized URL, user and seed lists separate; ADR 0011/0066/0067).

**Step 2: `WebDocumentList` + `WebDocumentTool`.** `WebDocumentList` is the only writer of `WebDocumentNames` and
`WebDocumentUrls` (contains-url, a paired append-and-trim, count, clear), so the two lists stay index-aligned. `WebDocumentTool`
holds `read_web_document`; it gets the chat-client factory as a `Func` (the grain reassigns it) and the grain's own
`WriteStateAsync` as a named `persistNow` (the one write it needs, made before the excerpts are ranked, as before). The
deferred agent rebuild (`RebuildForIngestedWebDocumentAsync`) stays a grain partial because it swaps `_agent` and `_session`.

**Step 3: `ArtifactList` + `ArtifactToolSet`.** `ArtifactList` (count, try-get, add) is the port; a version append or title
change mutates the record it returns. `ArtifactToolSet` holds `create_artifact`, `update_artifact`, `read_artifact`, the
validation and `UnknownArtifact`/`VersionList` (now `internal` statics that `ClientTools` calls). The compaction half
(`CompactToolPayloads`, `ParseArtifactResult`, `ArtifactsFromToolCalls`, ...) stays in `AgentGrain.ArtifactCompaction.cs` with
`GetArtifactsAsync` and the three tool-name consts. The pane, purge and fork still read or replace the dictionary directly.

**Step 4: `McpAppStore`.** The whole replay half of MCP Apps (ADR 0133) moves: store (with the cap), reconcile, the history
projection and the result read. It takes a `Func` for the live record list and a `Func` for the session's call ids (null when
the history is unreadable, so nothing is dropped), so it needs no port class of its own. `GetMcpAppResultAsync` and the
session read (`LiveSessionCallIds`) stay in a slim `src/Agent42.Agents/Mcp/AgentGrain.McpApps.cs`. The call order is unchanged: `StoreAsync`
before the end-of-turn write, `Reconcile` after the retention prune and the edit/regenerate truncation. Purge still clears
the list in the grain.

**The call sites are the control.** A fired reminder never feeds the list because `ReceiveReminder` calls the agent directly
and never reaches `BuildUserTurnAsync`; there is deliberately no `TurnContext` check or flag in the port, because every turn
kind that does reach it has an open context. Agent-run and fleet-result turns feeding the list is existing behaviour, kept.
`ProjectLayoutTests` pins that `WebTools.RememberFromUserText` has exactly one caller, that the seeded feeder is reached
only through `new UrlHarvestingMcpTool(...)`, and that nothing but `FetchableUrlList` writes the lists.

## Alternatives considered
- **Handing the collaborator `Func<AgentGrainState>`** is `_state` by another name.
- **One aggregate state-access object** hides dependencies; ADR 0147 rejected the same shape.
- **A fail-closed check in the port** discriminates nothing (see above) and would imply a guarantee that is not there.

## Consequences
- ADR 0147's "must write state stays a partial" and ADR 0148's Consequences no longer hold for partials with a port.
- Still partials, by decision: `Attachments`, `ModelRouting`, `RegisterDocumentAsync` and the compaction half of
  `ArtifactTools` (they need several ports or touch the live session/agent).
- No tool key, description or parameter changed; persisted types are untouched.
- Later steps of the plan amend this ADR as each partial moves.
