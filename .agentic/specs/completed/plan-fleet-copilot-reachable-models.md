# Plan — Fleet planner: only offer Copilot agents models the Copilot runtime can reach

planned-against: main @ 99ce64e

## Overview

**Goal:** the fleet planner (an LLM that writes each agent's YAML) is told exactly which models a Copilot agent can run
on, and what a plain `copilot` resolves to. Today it is not, so it writes a Copilot agent on a model the runtime cannot
reach (an Anthropic provider has no Copilot mapping, ADR 0103), the plan is rejected at load time
(`Copilot agents are not available, use a normal agent. The provider of 'Claude:claude-opus-5-5' can't be reached by the
Copilot runtime.`), and the planner burns a retry — or loops.

**Why now:** observed in a real fleet task (agent `ToolDeveloper`). The failure is deterministic for any user whose task
model is a Claude model.

## Affected areas

Verified against the tree at `99ce64e`:

- `src/Agent42.Backend/Fleet/FleetTaskService.Run.cs` — `BuildRunAsync` (the `copilotFactory` / `CopilotModelNames` /
  `CopilotEnabled` assembly, ~lines 121-155).
- `src/Agent42.Backend/Fleet/FleetRun.cs` — `CopilotModelNames`, `CopilotEnabled` (~lines 33-46); gets one new property.
- `src/Agent42.Backend/Fleet/FleetTeamRunner.Planning.cs` — `PlannerInstructions`, `CopilotModels()` (~lines 60-82).
- `src/Agent42.Backend/Fleet/FleetCopilotAgentFactory.cs` — gets one static helper. **Its acceptance logic stays as is.**
- `src/Agent42.Backend/Models/ChatClientFactory.cs` — `GetCopilotProvider` (read-only here; it is the reachability oracle).
- Tests: `tests/Agent42.Tests/FleetCopilotAgentFactoryTests.cs`, `CopilotModelDiscoveryTests.cs`, `FleetTeamRunnerTests.cs`.
- Docs: `.agentic/decisions/0108-github-copilot-provider.md` (its "Fleet" bullet) — amend in the same MR.

## Context & constraints

Read first: `.agentic/decisions/0097-agent-fleet-recursive-teams.md`, `0103-anthropic-provider-kind.md` (a Copilot agent on
Claude is refused, AC13), `0108-github-copilot-provider.md` (copilot-only models, decision 5 "Fleet" bullet), and the
gotchas under "Fleet" in `.agentic/gotchas.md`. No gotcha covers the planner's model list.

Findings the executor cannot re-derive cheaply:

1. **The planner's model lists are not Copilot-aware.** `PlannerInstructions` lists `run.Clients` (every non-copilot-only
   chat model) as ordinary models, and `CopilotModels()` appends only the *copilot-only* GitHub models, and only when there
   are any (`FleetTeamRunner.Planning.cs:78`). With no GitHub connection the copilot prompt's "one of the models below"
   points at nothing, so the planner has no list of valid `copilot/<id>` targets. Evidence: `FleetTeamRunner.Planning.cs:66-82`,
   `FleetPrompts.cs` `copilot` prompt (~line 115).
2. **The likely trigger is a plain `copilot`, not `copilot/<id>`.** `ModelFor("copilot")` returns
   `Fleet:DefaultCopilotModel`, else the **task's model** (`record.PlannerModel`), which is `Claude:claude-opus-5-5` in the
   reported failure. `Fleet:DefaultCopilotModel` is unset everywhere (code default `""`, `FleetOptions.cs:79`; not in
   `appsettings.json` or `infra/`). So the prompt must say what a plain `copilot` resolves to *and* when it cannot be used.
   (Inferred from the error text naming the Claude model; the rejected YAML itself was not seen.) **An operator can set
   `Fleet:DefaultCopilotModel`** (`FleetOptionsTests.cs:18` does), so the prompt must describe what `ModelFor("copilot")`
   really returns, never re-derive it from `run.DefaultModel` alone.
3. **Reachability has one oracle: `ICopilotProviderSource.GetCopilotProvider(id) is not null`.** It is non-null for Foundry
   and OpenAI-compatible BYOK providers and for the user's GitHub provider while it is connected; null for Anthropic
   (`ChatClientFactory.cs:293-298`, `:197-212`). It is the same call `FleetCopilotAgentFactory.TryCreateAsync` makes
   (`FleetCopilotAgentFactory.cs:101`), so filtering with it cannot disagree with the factory. **Only probe ids that come
   from the catalog**: for an id whose provider is gone, `Resolve` falls back to the default provider and would report that
   provider's settings.
4. **Do not touch the factory's acceptance or its error text.** `CopilotAgent_OnAnthropicModel_IsRejectedToPlanner`
   (`FleetCopilotAgentFactoryTests.cs:95`, ADR 0103 AC13) pins `The provider of '<id>' can't be reached`. Filtering is
   upstream of it: the planner stops asking, the factory remains the backstop.
5. **The new prompt text goes in code, not in `FleetPrompts.cs`.** The `copilot` prompt is user-editable and persisted per
   user (`store.GetPromptsAsync`), so a changed default never reaches a user who saved their own copy. `CopilotModels()` is
   the established place for this (the comment at `FleetTeamRunner.Planning.cs:76-77`, ADR 0108).
6. **Existing assertions to keep green:** `Planner_ListsCopilotModels_OnlyAsCopilot` (`CopilotModelDiscoveryTests.cs:107`)
   needs the substring `run only as a Copilot agent` to stay, the GitHub list after it, and no `github-copilot` before it.
   `Planner_UsesUserCatalogModels` (`FleetTeamRunnerTests.cs:98`) asserts no `claude` in the prompt, but that run has no
   Copilot factory, so the new text must be emitted **only** inside the `CopilotEnabled && CopilotFactory is not null` branch.

Decisions made in the planning conversation (user's call): do **1** (filter what a Copilot agent is offered) and **3** (tell
the planner in the prompt). **Rejected for now: 2** (make a bare `copilot` fall back to a reachable model, or fail with the
valid choices) and any change to `Fleet:DefaultCopilotModel`. Consequence the user should know: without 2, a planner that
ignores the prompt and writes a plain `copilot` on an unreachable default still gets the load error. This plan makes that
unlikely, not impossible.

## Steps

Branch: `fix/fleet-copilot-reachable-models` off `main` @ `99ce64e`.

1. **Reachability helper.** Add to `FleetCopilotAgentFactory`
   `public static IReadOnlyList<ModelInfo> ReachableModels(IChatClientFactory factory, IEnumerable<ModelInfo> models)`,
   returning those for which `factory is ICopilotProviderSource s && s.GetCopilotProvider(m.Id) is not null`. A factory
   that is not an `ICopilotProviderSource` yields none. Pure function (no I/O) so it tests without a runtime.
   *Verify:* AC1 tests.

2. **Carry the BYOK set and the effective default on the run.** Add to `FleetRun`:
   `CopilotByokModelIds` (`IReadOnlyDictionary<string, string?>` id to display name, default empty; the reachable models
   that are **not** `CopilotOnly`), and `CopilotDefaultModel` (`string?`, default null; what a plain `copilot` resolves
   to). `CopilotModelNames` (copilot-only GitHub models) is unchanged.
   *Verify:* compiles; existing fleet tests untouched.

3. **Wire it in `BuildRunAsync`.** `models` already holds both chat models and copilot-only ones (`FleetTaskService.cs:130`;
   `Run.cs:133,149` split it by `CopilotOnly`). Call `ReachableModels(factory, models)` once, then:
   `CopilotByokModelIds` = reachable and not `CopilotOnly`; `CopilotDefaultModel` = `copilotFactory.ModelFor("copilot")`
   (the one resolver, so it honours `Fleet:DefaultCopilotModel`); `CopilotEnabled` = an extracted
   `internal static bool CopilotOffered(FleetCopilotHost? host, IReadOnlyCollection<ModelInfo> reachable)` that is
   `host?.Available == true && reachable.Count > 0`, so a user with no Copilot-capable model is never told Copilot agents
   exist. Leave `[.. models.Select(m => m.Id)]` as the factory's `catalogModels` (finding 4).
   *Verify:* AC5.

4. **Rewrite `CopilotModels()`.** Keep the existing copilot-only block word for word (finding 6). Add, after it:
   - the reachable BYOK models, as `copilot/<model id>` targets (id and display name, ordered like the other lists);
   - the plain-`copilot` rule, from `run.CopilotDefaultModel ?? run.DefaultModel` (never `run.DefaultModel` alone) and whether
     that id is in the BYOK or copilot-only lists: if it is, say a plain `copilot` runs on it; if it is not, say a plain
     `copilot` **cannot** run on it and the agent must name `copilot/<model id>` from the lists above;
   - one sentence that any model not listed here (for example an Anthropic/Claude model) cannot run as a Copilot agent and
     must be used as an ordinary agent.
   Split into small private methods; S3776 is a build error at 15 (CLAUDE.md rule 9).
   *Verify:* AC2-AC4, plus the two existing planner tests above.

5. **Docs.** Amend ADR 0108's "Fleet" bullet (decision 5): the planner prompt also lists the reachable BYOK models and says
   what a plain `copilot` resolves to. Run `/sync-docs`. No new ADR: this narrows an existing prompt, it does not reverse a
   decision. Do not edit `FleetPrompts.cs` (finding 5).
   *Verify:* `QualityGateTests` / doc-path integrity still pass.

Steps 1-4 are sequential (each compiles against the previous). Step 5 is parallelizable with step 4.

## Acceptance criteria → test mapping

| AC | Test | File |
|----|------|------|
| AC1 The reachable set contains Foundry/OpenAI-compatible BYOK ids, contains a GitHub id only while GitHub is connected, excludes an Anthropic id, and is empty for a factory that is not an `ICopilotProviderSource` | `ReachableModels_KeepsByokAndGitHub_ExcludesAnthropic` | `tests/Agent42.Tests/FleetCopilotAgentFactoryTests.cs` |
| AC2 The planner lists each reachable BYOK model as a `copilot/<id>` target and does not list an unreachable (Claude) one in the Copilot section | `Planner_ListsReachableByokModels_NotUnreachableOnes` | `tests/Agent42.Tests/FleetCopilotAgentFactoryTests.cs` |
| AC3 When the default model is unreachable the prompt says a plain `copilot` cannot run on it and to name `copilot/<id>` | `Planner_UnreachableDefault_SaysPlainCopilotCannotRun` | `tests/Agent42.Tests/FleetCopilotAgentFactoryTests.cs` |
| AC4 When the default model is reachable the prompt says a plain `copilot` runs on it | `Planner_ReachableDefault_SaysPlainCopilotRunsOnIt` | `tests/Agent42.Tests/FleetCopilotAgentFactoryTests.cs` |
| AC5 No reachable model means `CopilotOffered` is false even with the kill switch on and the runtime installed, and a `FleetRun` with `CopilotEnabled = false` has no Copilot section | `NoReachableModel_CopilotIsNotOffered` | `tests/Agent42.Tests/FleetCopilotAgentFactoryTests.cs` (tests the extracted static `CopilotOffered`; no existing `FleetTaskService` test builds a `FleetCopilotHost`, and `BuildRunAsync` is private, so do not look for a harness) |
| AC8 With `Fleet:DefaultCopilotModel` set to a reachable model, the prompt says a plain `copilot` runs on that model, not on the task's model; set to an unreachable one it says it cannot run | `Planner_ConfiguredDefaultCopilotModel_IsWhatThePromptSays` | `tests/Agent42.Tests/FleetCopilotAgentFactoryTests.cs` |
| AC6 The factory still rejects a Copilot agent on an Anthropic model with the same message (regression pin) | `CopilotAgent_OnAnthropicModel_IsRejectedToPlanner` (existing, must stay green) | `tests/Agent42.Tests/FleetCopilotAgentFactoryTests.cs` |
| AC7 The GitHub copilot-only list keeps its wording and order (regression pin) | `Planner_ListsCopilotModels_OnlyAsCopilot` (existing, must stay green) | `tests/Agent42.Tests/CopilotModelDiscoveryTests.cs` |

For AC1, build a multi-provider factory the way `ModelProviderTests` does and `AnthropicProviderTests.Factory` for the
Claude case (`grep -n "Factory(" tests/Agent42.Tests/ModelProviderTests.cs tests/Agent42.Tests/AnthropicProviderTests.cs`).

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (tool in brackets):

- [ ] `coverage_new_code_pct_min: 80` [`node .agentic/scripts/check-new-code-coverage.mjs`, coverlet x git diff]
- [ ] `cognitive_complexity_max: 15` [SonarAnalyzer S3776, a build error]
- [ ] `vulnerable_packages: 0` [CI package-freshness job] — no package is added by this plan
- [ ] `dotnet format Agent42.slnx --verify-no-changes`, `dotnet test Agent42.slnx`, `dotnet build Agent42.slnx`

Report a value as SKIPPED if its tool cannot run; never estimate one (CLAUDE.md rule 3).

## Out of scope

- Making a plain `copilot` fall back to a reachable model, or listing valid choices in the factory's error (option 2).
- Setting or changing `Fleet:DefaultCopilotModel`.
- Changing the factory's acceptance rules, error text or `catalogModels` argument.
- Editing the default `copilot` prompt in `FleetPrompts.cs` (user-saved copies would not pick it up).
- Chat-side (non-fleet) Copilot agent model pickers.
- Adding a way for the planner to *query* the runtime live (`Rpc.Models.ListAsync`): that API lists a GitHub account's
  models only, and `CopilotModelDirectory` already feeds it into the catalog (ADR 0108).

## Review

`critic` pass on the first draft (read-only, nothing built or run). All three findings were legitimate and are fixed above:

1. **Medium — the plain-`copilot` rule ignored `Fleet:DefaultCopilotModel`.** Fixed: `FleetRun.CopilotDefaultModel` is set from
   `copilotFactory.ModelFor("copilot")` (one resolver), step 4 reads it, and AC8 pins the configured-default case.
2. **Medium — AC5 named a harness that does not exist** (no `FleetTaskService` test builds a `FleetCopilotHost`; `BuildRunAsync`
   is private). Fixed: the extracted `internal static CopilotOffered` is now the primary path, AC5 tests it directly.
3. **Low — step 3 named lists that do not exist in `BuildRunAsync`.** Fixed: reachability runs over `models` and splits by `CopilotOnly`.

Verified by the critic, no change needed: the reachability oracle matches the factory's own check on the same factory object;
a plain `copilot` resolves to the task model when the key is unset; `CopilotEnabled` has one reader (`FleetTeamRunner.Planning.cs:71`),
so turning it off has no other side effect; the existing planner tests stay green if the new text is emitted only inside the
Copilot branch and after the GitHub block. Not re-checked by the critic: the gate checklist (copied by me from
`.agentic/quality-gates.yaml` at plan time).

Rejected findings: none.

## Implementation divergences

- `CopilotOffered` is `public static`, not `internal`: the repo has no `InternalsVisibleTo` (the plan's step 3 and AC5 said internal).
- `PlainCopilotRule` compares the default model against the BYOK/copilot-only lists with `StringComparer.OrdinalIgnoreCase`
  (critic finding): the factory matches catalog ids case-insensitively, and `Fleet:DefaultCopilotModel` is used verbatim.
  `Planner_ConfiguredDefaultCopilotModel_IsWhatThePromptSays` pins a case-drifted id.
- The "not listed, cannot run" sentence is appended whenever the Copilot section is emitted (only when `CopilotOffered`).
- AC1's "GitHub id included while connected" is not tested (no connected-GitHub factory in the test harness); only the
  disconnected exclusion is. `BuildRunAsync` wiring has no direct test (private, as the plan accepted).
- The first full run had one failure, `LiveCopilotTests.LiveCopilot_BuiltIns_ShellSeesFileToolWrite_AndResumes` (a live Copilot
  shell test); it passed when re-run alone.
