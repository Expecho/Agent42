---
id: 0147
date: 2026-11-27
status: accepted
tags: [refactor, structure, agents, orleans, tools, testing, no-migration]
supersedes: []
extends: [0106]
amends: [0039, 0075, 0106, 0112, 0115]
---

# Leaf tool sets move out of `AgentGrain` into classes built with `new` per activation

## Context
`AgentGrain` was still a god class after ADR 0106. Its leaf tool partials (`get_current_location`, the past-chat tools,
the fleet tools, the fleet-analyzer tools, `send_push_notification`) are cohesive sets that each reach one service through
a grain field. Plan cc-09 (2026-11-27) moves them so each has a name, a constructor that states its real dependencies, and
a unit-testable surface.

## Decision
`LocationTools`, `PastChatTools`, `FleetTools`, `FleetAnalyzerTools` and `PushTools` are classes built with `new` in
`OnActivateAsync`, beside ADR 0106's services. They are not DI-registered, so the grain constructor does not change. What
a tool needs of the grain is passed at construction: ids, the logger, a `Func<string>` for the model id (it can change
after activation), or a method group for grain behaviour (`AwaitClientResultAsync`, `DeliverNotificationToChatAsync`).
Tool keys, descriptions and parameter names move verbatim, pinned by `ToolCatalogSnapshotTests` against
`ToolSchemas.golden.txt`. The classes are `public` because the repo has no `InternalsVisibleTo`. Every moved `AIFunction`
still reaches Copilot through `SchedulerBoundAIFunction`.

Left in the grain on purpose: `AgentGrain.Push.cs` (reads `_state`), `Webhooks` (reads `_state`), the memory provider
builder (a context provider, not a tool set, and its rebuild-time reasoning is tied to `BuildAgent`), `FollowUps` (a model
call helper), and past-chat result compaction (it shares `ToolText`/`PartArgument` with the other compaction branches).

## Alternatives considered
- **One DI-registered `AgentToolSets` aggregate** — it hides four dependencies from `ThrowIfPortsMissing` (ADR 0105), the
  Backend cannot be booted in a test to prove the transitive resolve, it reverses ADR 0106 §4, and it is a parameter object
  that games a constructor-count metric.
- **Moving the tools into `IServiceCollection`-built singletons** — the tools read per-activation values and per-run state,
  so a singleton would need those passed on every call.
- **Moving the `_state`-reading partials too** — that is the tier-B work of plan cc-10, decided after this one.

## Consequences
- The grain constructor keeps its parameters: it still receives the services and passes them in. The gain is cohesion, not a
  smaller constructor.
- The privacy guard `PushTool_DoesNotLogNotificationText` now reads `src/Agent42.Agents/Push/PushTools.cs`; the assertion is unchanged.
- A new tool set follows this shape; a tool that must write `_state`, `_session` or `_agent` stays a partial.
