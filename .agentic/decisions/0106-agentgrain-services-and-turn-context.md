---
id: 0106
date: 2026-10-19
status: accepted
tags: [refactor, structure, agents, orleans, turn-state, tools, testing, no-migration]
supersedes: []
extends: [0039]
amends: [0034, 0039, 0066, 0075, 0089, 0098]
---

# AgentGrain delegates to per-activation services; per-run state is an explicit `TurnContext`, never a field

## Context
On 2026-10-19 `AgentGrain` was one class in 24 partial files (~7,200 lines). The ADR 0039 split moved files and
nothing else, and its "Alternatives considered" left the real decoupling open. Five values that belong to one run
were activation-lifetime grain fields: `_ephemeral`, `_interactiveTurn`, `_pushesThisTurn`, `_documentPartsRead`
and `_webDocumentIngested`. Each had to be hand-reset at two or three sites (CLAUDE.md rule 6, four gotchas entries).
They were also shared by every run in flight on the activation. `ChatAsync` suspends between `yield return`s, and
a reminder or an `EnqueuePromptAsync` run executes in that gap (the `_turnCts` remarks in `AgentGrain.cs`), so a
reminder's reset block wrote over the streaming turn's values. Outside the turn state, search projection,
retention, edit/fork and tool selection were grain methods that only a silo could test.

## Decision
1. **Per-run state is a `TurnContext`** (`Conversations/TurnContext.cs`, a sealed reference type). One is created at
   each run entry (`ChatAsync`, `EnqueuePromptAsync`, `ReceiveReminder`). On the grain side it is passed
   explicitly as a parameter. Tools and context providers, which cannot take a parameter, read
   `TurnContext.CurrentOrClosed`. That accessor sits over one private `AsyncLocal`, which is set around every
   enumerator interaction of the run (`PullAsync`, `DisposeRunAsync`) and around the reminder's `RunAsync`. It
   generalises the ADR 0089 broker async-local, which it absorbs.
2. **A missing context fails closed, through that one accessor.** Outside a run, `CurrentOrClosed` is the shared
   `TurnContext.Closed`: ephemeral, budgets exhausted, no broker, and mutations ignored. A source-text guard
   (`ProjectLayoutTests`) pins that only `TurnContext.cs` touches the raw async-local.
3. **Copilot runs are resolved through a by-identity registry.** The Copilot SDK invokes custom tools on its RPC
   thread, outside the run's `ExecutionContext`. `SchedulerBoundAIFunction` resolves the context only when
   **exactly one** Copilot run is in flight on the activation. With zero or more than one, it uses `Closed` and
   the tool refuses.
4. **Four services are extracted, each with an explicit context argument:** `ChatSearchProjection`,
   `HistoryRetention`, `ConversationForking` and a stateless `ToolSelection`. `BuildAgent` returns an
   `AgentComposition` that one helper assigns. The grain constructs the services per activation. They are not
   DI-registered, and the grain ctor does not change. The grain remains the only owner of `_state`, `_session` and
   `_agent`, and of every persist site and its order.

## Alternatives considered
- **Keep the fields, centralise the reset in one helper.** This is cheaper, but the fields are still shared by
  interleaved runs, so it fixes the ritual and not the bug.
- **Thread the context as a parameter all the way into the tools.** `AIFunction`s and `AIContextProvider`s are
  invoked by Agent Framework, which has no per-run argument slot. The bindings are built once per `BuildAgent`, not
  per run, so a closure would capture the wrong run.
- **DI-registered service factories (`IXxxFactory` ports).** Three of the services hold activation-scoped state, so
  a singleton is wrong. A factory port adds a registration to three hosts and about eight test silos (ADR 0105), and
  nothing about it varies per host. The interfaces exist for unit tests and grain-test fakes only.
- **A cached `ToolSelection`.** `EnabledTools` changes at activation, bind, MCP re-resolve, `SetEnabledToolsAsync`
  and fork. A cache needs a rebuild at each of those, and a missed one leaves a tool (or memory egress) ON after the
  user turned it off (ADR 0021, rule 11). Computing the selection per call is cheap.
- **Move the tool functions into services too.** They write `_state` (reminders, `FetchableUrls`, artifacts), so
  this would move state ownership. Left for a separate plan.
- **A first-wins Copilot slot (`_copilotRun ??= ctx`).** This leaks, and when two runs are in flight it hands the
  second run's tool the first run's budget. The registry refuses in that case instead.

## Consequences
- Rule 6's clear ritual is gone, because there are no turn-valued fields left to clear. `_turnCts`,
  `_researchInFlight`, `_clientToolBrokers`, `_mcpReauthNotified` and `_runsInFlight` stay as they are. They name
  running turns or are activation-scoped by design.
- **Measured on AF 1.23 (spike, 2026-10-19), with a fake `IChatClient` that emits a tool call:**
  - An async-local set around each `MoveNextAsync` reaches the FICC-invoked tool, both `AIContextProvider` hooks
    and the history provider's storage filter. Tool mutations are visible to the caller afterwards.
  - Set once in an async frame before `RunAsync`, it reaches the same code and does not leak to the caller.
  - Two interleaved runs (pull A once, run B to the end, finish A) each see only their own context.
  - Through `LoopAgent`, the inner agent's tools see the context.
  - Code on a thread started under `ExecutionContext.SuppressFlow()` sees null.
  - `StoreAIContextAsync`, where Hindsight retain runs, executes inside the **final `MoveNextAsync`**, not in
    `DisposeAsync`. An early dispose runs no provider hook. `DisposeRunAsync` is therefore defensive on 1.23, and it
    is kept so that a framework change cannot make memory read `Closed` at retain time.
  - Whether the real Copilot SDK flows the run's `ExecutionContext` into a custom tool was **not** measured. The
    registry stays until that is shown.
- **Residual:** if two Copilot runs are in flight on one activation (a reminder firing in a gap of a streamed Copilot
  turn), `schedule_prompt`, the artifact tools and `send_push_notification` refuse for both runs. Today both runs share
  one counter, so the refusal is strictly safer than the current behaviour.
- Search, retention, edit/fork and selection logic now have plain xUnit tests with no silo. The silo tests still pin
  the end-to-end behaviour, and they pass unchanged.
- No persisted state, grain interface, endpoint or frontend change, so no migration is needed.
