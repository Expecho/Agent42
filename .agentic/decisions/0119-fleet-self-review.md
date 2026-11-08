---
id: 0119
date: 2026-11-08
status: accepted
tags: [fleet, agents, self-review, loop-agent, judge, budget, frontend, no-migration]
supersedes: []
extends: [0097, 0082]
amends: [0097]
---

# A fleet task can have the agent that writes its result check that result before the team judge sees it

## Context
The team judge (ADR 0097) reviews a result after the team ran, and a rejection re-runs the same team (a fix round) or
designs a new one: expensive. The user asked whether agents can review their own work against the plan, and whether
Agent Framework's loop does that. `LoopAgent` is in the pinned `Microsoft.Agents.AI` 1.25.0 (marked `[Experimental]`,
`MAAI001`) and the research agent already uses it (ADR 0082, `AgentGrain.WithResearchLoop`); only the `HarnessAgent` preset
was rejected there and in ADR 0117. Whether self-review actually saves tokens is **unmeasured**: the prompt-only baseline
(see Alternatives) was not run, so the feature ships OFF.

## Decision
An opt-in **per-task** flag, `FleetTaskRecord.SelfReview` (plain JSON, defaults false, no migration), set by the
composer's "Self-review the result" checkbox (off by default, remembered in `localStorage`) or the `selfReview` field of
`POST /api/fleet/tasks[/with-files]`. A page task only: a task started from a chat (ADR 0115) stores false, like `Clarify`.

- **Who loops.** `FleetSelfReview.Apply`, called in `PlanSubmission` after the agents load and the spawn metric ran, wraps
  only the agents that PRODUCE the team's result in a `LoopAgent`: the last agent of a **Sequential** team and the agents
  of the last stage of a **Staged** team. Concurrent, GroupChat, Handoff and Magentic teams are never wrapped (they rely on
  one turn per agent, a moderator or a manager; Concurrent's result is a join), nor are Copilot agents (own loop; detected
  by a recorded model id that starts with `copilot`), nor teams deeper than `Fleet:JudgeDepth` (those are deliberately
  unjudged and single-attempt for cost).
- **The loop.** `AIJudgeLoopEvaluator` on the task's own budgeted default client (no new egress, ADR 0013 unchanged; the
  judge's and the re-runs' tokens count against the task budget, and a budget cancel reaches them). Runs are capped by
  `Fleet:SelfReviewMaxIterations`, the first run included (default **2**, one fix pass; below 2 turns self-review off).
- **Fails open.** The judge is wrapped in `ReviewEvaluator`: a judge exception (other than a cancellation) keeps the result and
  publishes `selfreview.failed`. A stop or budget cancel is never swallowed. A judge that gives no verdict means "more", so
  the cap is what bounds it.
- **Events.** `selfreview.iteration {teamId, agent, iteration, feedback}` before each re-run (feedback cut to 500
  characters). No attempt id: fix rounds re-run the same wrapped agents, so a captured attempt would go stale. The page
  ignores unknown events, and shows nothing for iterations (out of scope).

**Measured on 1.25.0, and the shape follows from it** (a scripted fake client in a Sequential and a Concurrent workflow, via
`InProcessExecution`):
- `FreshContextPerIteration` stays **off**: on, the re-run sees the task and the feedback but NOT its own draft.
- `ExcludeOnBehalfOfMessages` stays **false**: with `true`, updates that carry no `MessageId` merge the drafts into one
  message ("draft onedraft two") in the workflow output, so the team result was wrong; the user-role feedback message
  between drafts is what keeps them apart (a Concurrent join was clean either way with `false`). `FleetEventMapper` skips
  updates authored `self-review`, or the feedback would be published as the agent's own text.
- `LoopAgent` forwards `Name`, `Id`, `Description` and `GetService<ChatOptions>()`, so `CheckAgent` and executor ids work.
- A judge that throws propagates (hence the guard); a cancel surfaces as `OperationCanceledException`.

## Alternatives considered
- **`HarnessAgent`.** Rejected by ADR 0082 and 0117 (a preset that removes control, no hook for YAML-built agents). `LoopAgent`
  is a separate type in the core package, so it was not what those ADRs rejected.
- **`TodoCompletionLoopEvaluator`.** The todo list is shared per team attempt (ADR 0117), so one agent would loop on a
  teammate's open items; and fleet todos are plain functions, not a `TodoProvider`.
- **Prompt-only: tell the planner to add a "re-check before finishing" line to each agent's instructions.** Costs no code
  and is the baseline the loop must beat. **Not run** (it needs live models); whether it makes this feature unnecessary is open.
- **Looping every agent.** Earlier pipeline agents are refined by later ones, so it costs without a clear gain.
- **Always on, or a global default option.** Each fix pass is a full agent run plus a judge call; a chat-started task has no
  checkbox to opt out. Off by default, per task.
- **Excluding the feedback from the output (`ExcludeOnBehalfOfMessages = true`).** Rejected on the measurement above.

## Consequences
- **Cost multiplier.** Fix rounds and new teams re-run the same wrapped agents, so for a judged team the worst case is
  `MaxAttempts x (1 + MaxFixRounds) x SelfReviewMaxIterations` runs of the writing agent plus a judge call per iteration (with
  the defaults 3 x 2 x 2), all inside the task's token budget. The usage dashboard shows it under "Agent42 fleet".
- **Experimental API.** `LoopAgent` can change or vanish in a later Agent Framework release; the exposure is one region
  under a local `#pragma warning disable MAAI001` in `FleetSelfReview.cs`, like the research agent's.
- **Unproven benefit.** The decision to turn it on, or to remove it, rests on the baseline in the spec
  (`.agentic/specs/completed/plan-fleet-agent-self-review.md`, Step 1) that the user still has to run.
- The two drafts of the looping agent are both streamed as the agent's text (the log coalesces them); `selfreview.iteration`
  marks the boundary for anyone who reads the log.
