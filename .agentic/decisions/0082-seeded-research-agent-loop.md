---
id: 0082
date: 2026-09-22
status: accepted
tags: [research, stored-agents, agent-framework, loop, todos, compaction, stream-events, frontend, experimental-api, no-migration]
supersedes: []
amends: [0003, 0016, 0046, 0078]
amended_by: [0085]
---

# Seed a built-in Research agent that loops plan → search → reflect, with a live checklist

> **Amended by [ADR 0085](0085-copilot-agent-kind.md) (2026-10-05): `Kind` is no longer seed-only.** A create request
> may claim `copilot` (research and unknown kinds are a 400); every update still preserves the stored kind.

## Context

The web tools (ADR 0011/0027) and RAG already exist, but no agent plans a question into steps, works through
them and checks its own answer. Agent Framework 1.19 ships the pieces in `Microsoft.Agents.AI`: `TodoProvider`
(plan state in the session), `LoopAgent` (re-run until evaluators say stop), `AIJudgeLoopEvaluator`, and the
compaction strategies. A spike on 2026-09-22 (recorded in
`.agentic/specs/completed/plan-research-agent.md`, "Spike results") measured how they behave on this stack.
Two results changed the design: `CompactionProvider` corrupts a multi-turn request, and `LoopAgent` persists
each finished iteration mid-turn.

## Decision

1. **A seeded built-in agent, not a mode.** `AgentDefinition.Kind` / `AgentBinding.Kind` / `AgentGrainState.AgentKind`
   (trailing, nullable: the Cosmos JSON path binds a missing key to `null`, not to `""`). The registry
   seeds one "Research" agent per user on the first `GET /api/agents` that has a **concrete** default model
   (`EnsureResearchAgentAsync`). The seed goes through the normal insert, so the ADR 0022 key hash and directory
   stay consistent, and the flag rides the same write. It is never re-seeded after a delete. `Kind` is set only by
   seeding and preserved on every update. The seeded tools are `web_search` only.
2. **Kind is persona** (amends ADR 0003). It is snapshotted at bind time from all three bind paths, so editing or
   deleting the agent never changes an existing chat.
3. **Per-chat only** (amends ADR 0016). The registry forces `Continuous = false` for a research agent, and the
   editor locks the box. Research turns are the largest turns there are, and a continuous chat is one 2 MB grain.
4. **Composition** (`src/Agent42.Agents/Research/AgentGrain.Research.cs`): when the chat is research-kind and the model takes tools,
   `BuildAgent` adds a `TodoProvider`. It wraps the conversation's client in
   `UseChatReducer(ContextWindowCompactionStrategy(64k, 8k).AsChatReducer())`, placed below the function-invoking
   client, and wraps the agent in `LoopAgent`. The evaluators run in order: an observer
   (records iteration boundaries, always `Stop`), `TodoCompletionLoopEvaluator`, then `AIJudgeLoopEvaluator` on
   the conversation's own client, with `MaxIterations = 6`. A no-tools model gets a plain agent.
5. **One user turn is still ONE assistant turn** (amends ADR 0046). While a research turn runs,
   `GetHistoryAsync` hides everything from that turn's start. When the turn ends, the stored session is
   **folded**: only the last iteration that produced text keeps its text, a message emptied by that is dropped,
   and every tool call stays (ADR 0080 replays them all). Live mirrors it with a new
   `AgentStreamEventType.TextReset`, emitted when a later iteration starts speaking. The browser clears the draft,
   the MCP runner clears its reply, and `reply` (follow-ups, TTS) restarts. The rule everywhere is "the answer is
   the last iteration that spoke".
6. **Stop under a loop** (amends ADR 0078). Iterations that finished are already stored, the user message
   included. So the hand-append adds the user message only when no iteration finished, and adds the reply only
   when its text came from the iteration that was cut short.
7. **Live checklist.** `AgentStreamEventType.Todos` carries the whole list whenever a tool result changed it. It
   is live-only, like `Suggestions`. `research.js` renders it.
8. **Loop feedback is never stored.** The history filter drops `user` messages authored
   `ResearchLimits.LoopAuthorName`, as an extension of `IsInjectedOrReplayed`, never a replacement.

## Alternatives considered

- **`HarnessAgent` (`Microsoft.Agents.AI.Harness`).** It is a preset over the same types (measured), so it adds a
  package and removes control over provider order, the storage filter and the evaluators.
- **A generic "research mode" flag on every agent.** Rejected by the user: a seeded agent is discoverable and needs
  no per-agent settings UI. `Kind` leaves that door open.
- **`CompactionProvider` (the plan's choice).** Measured on 1.19: once compaction has run, it re-emits the request
  from its cached message groups. From turn 2 on, the model was sent **the first question again**, and the real one
  was never stored (`users=[:question 1]` after four turns). It also persisted its own `messagegroups` state,
  which duplicates tool results (72k–181k chars vs 21k).
- **Compaction as a stateful builder middleware, or `AsChatReducer()` on the history provider.** The first had the
  largest state. The second shrinks **stored** history, which deletes what ADR 0080 replays and does not bound the
  in-run view. The chosen reducer on the chat client is stateless and changes only what one call sees.
- **Folding at projection time (the plan's choice).** `GetHistoryAsync` already turns every text-bearing
  assistant message into a turn. With the feedback messages filtered out, storage no longer holds the iteration
  boundaries, so a projection could not tell a draft from the answer. Folding at persist time also keeps the
  model's later context small.
- **Carrying the per-run tracker in `AgentRunOptions.AdditionalProperties`.** Measured: that bag is copied into
  `ChatOptions.AdditionalProperties` of every model call, which is the provider request body. The tracker is keyed
  by the turn's own user `ChatMessage` in a `ConditionalWeakTable` instead. `LoopContext.InitialMessages` holds
  that same reference (measured).
- **`Model ""` for the seed.** `""` is the ADR 0019 no-model sentinel. The editor cannot save it and silently
  rewrites it, so the seed waits for a concrete model instead.
- **Continuous research agents.** Rejected (ADR 0016 size).
- **Seeding `read_web_document`.** A mid-run ingest cannot reach retrieval until the turn ends, and a whole loop is
  one run. Seeding it would also bypass that key's consent stance. The user can still add it.

## Consequences

- **Experimental API (MAAI001):** `LoopAgent`, `LoopAgentOptions`, the evaluators and `ContextWindowCompactionStrategy`
  are `[Experimental]` in 1.19. The scoped `#pragma` in `AgentGrain.Research.cs` is the only use. A framework
  upgrade must re-run `ResearchAgentTests` and `StopGenerationTests`.
- **Egress (rule 13):** no new third party. The judge calls the conversation's own provider, but it is **one extra
  call per finished iteration** once the todos are done. If the user adds `memory`, Hindsight recall and retain run
  **once per iteration**, not once per turn (`BuildAgent`'s memory provider is inside the loop).
- **ADR 0070/0022:** a research turn will often exceed the MCP `RunTimeout` (120 s). The existing "still running"
  contract applies, and the MCP reply is the final answer only.
- **Reminder path:** a scheduled prompt in a research chat runs the full loop through `RunAsync`
  (`NonStreamingReturnsLastResponseOnly`). It has its own reset, hide and fold.
- **A failed research turn is still folded.** Iterations stored before the failure are folded in a `finally`
  (`ReleaseResearchRun(foldFirst:)`), at both run sites.
- **Accepted — overlapping runs.** Two runs on one activation (a reminder firing while a research chat streams)
  share `_session`, as they already did before this ADR. Under research they also share the todo list and the
  history indices, so the later run's plan reset and fold can disturb the earlier one. Not guarded: research
  chats rarely schedule prompts, and the same overlap already interleaves an ordinary chat's messages.
- **Accepted — stream recovery (ADR 0046).** The whole research turn is hidden until it ends, which is usually
  longer than `send.js`'s ~30 s recovery poll. So a backgrounded research turn shows "Transmission failed"
  although it completes, and a reload shows the answer.
- **Accepted:** the context budget is a constant (no per-model window metadata), so a provider with a smaller
  window still fails like any long chat. An abandoned stream is unhidden but not folded. The checklist is not
  replayed on reload. Voice mode may have spoken a draft before its `TextReset`.
