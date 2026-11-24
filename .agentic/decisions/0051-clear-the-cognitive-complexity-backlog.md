---
id: 0051
date: 2026-08-25
status: accepted
tags: [quality-gates, refactoring, complexity, no-behaviour-change]
amends: [0050]   # 0050 (the gate now blocks; its recorded backlog is gone)
---

# Title: Clear the cognitive-complexity backlog and make the gate block

> **Amends [ADR 0050](0050-measurable-quality-gates.md).** 0050 made `cognitive_complexity_max`
> measurable but left it non-blocking, because eleven methods already exceeded it and refactoring
> them inside a tooling MR was the wrong risk. This pays that debt: the eleven are gone, S3776 is an
> **`error`**, and the `--exclude-diagnostics S3776` carve-out 0050 needed on the format gate is
> removed. ADR 0050 stays `status: accepted`.

## Context

ADR 0050 recorded its own debt honestly and left it visible: S3776 at `warning`, eleven violations
listed with their numbers, and a note that the gate "becomes blocking when that backlog is cleared".
A gate that cannot fail is the exact thing 0050 was written to abolish, so leaving it there
indefinitely would have hollowed out the ADR from the inside.

## Decision

**Refactor all eleven; change no behaviour.** Every extraction below moves code verbatim — same
statements, same order, same comments — and is covered by the existing suite. No test was changed to
accommodate a refactor.

Then flip the gate to enforcing: `dotnet_diagnostic.S3776.severity = error`, so a method over 15
fails `dotnet build`. **`warning` would not have been enough** — the build is not `-warnaserror`, so
a warning is visible but nothing fails, which is a gate in name only.

### The five endpoint partials were one problem, not five

`MapAttachmentEndpoints` (18), `MapUserSkillEndpoints` (22), `MapConversationEndpoints` (25),
`MapMcpEndpoints` (29) and `MapStoredAgentEndpoints` (41) all scored high for the same reason:
**cognitive complexity sums a method's nested lambdas**, so an ADR 0040 registration partial
accumulates the score of every endpoint it declares, even though those endpoints are entirely
independent of one another.

The fix is to register method groups instead of inline lambdas — `app.MapPost(route, HandlerAsync)`
— which gives each handler a name and a doc comment, and leaves the registration method reading as
the table of contents it always wanted to be. This is **not** metric-gaming: the metric was pointing
at something real (a 126-line lambda nested inside a registration call), just not at "this method is
hard to follow".

ADR 0040's structure is untouched: the same partial still owns its feature's endpoints, and
`ProjectLayoutTests.AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot` still passes.

### The other six

| Method | Was | Change |
|---|---|---|
| `RunAgentAsync` (after lifting) | 26 | Split into its three real phases: `ResolveAgentRunCallerAsync` (auth), `ValidateAgentRunFiles` (the all-or-nothing batch check), `StoreAgentRunFilesAsync` (store/ingest). |
| `ChatCoreAsync` | 39 | Four yield-free blocks extracted — see below. |
| `RehydrateAttachmentUrlsAsync` | 27 | `RestoreUserTurnImagesAsync` extracted; the positional join stayed put. |
| `GetHistoryAsync` | 23 | `ProjectUserTurn` / `ProjectAssistantTurn` — the two roles resolve images differently, so they were always two things. |
| `src/Agent42.Backend/Program.cs` (top-level) | 16 | The document-index registration moved to `src/Agent42.Backend/Documents/DocumentIndexRegistration.cs`. |
| `FakeChatClient.SelectToolCall` | 21 | A dozen `if (text.Contains(...))` branches became an ordered trigger table. |
| `AddOpenTelemetryExporters` | 22 | Six copies of the same pair of `if`s collapsed to one local function. |

**`ChatCoreAsync` is an `IAsyncEnumerable`, which decides what can move.** Anything containing
`yield` has to stay: the no-model notice, the image-generation branch, the withheld-image notice, the
streaming loop's `yield return`, the suggestions event and `Done`. Four blocks contain no `yield` and
came out cleanly — `SwitchModelAsync`, `BuildUserTurnAsync`, `ProjectStreamedContent` /
`ProjectStreamedUpdate`, and `PersistTurnUnlessEphemeralAsync`. Three further reductions were
structural rather than extractive: the token totals became a small class (an iterator cannot hand out
`ref` locals), the doubly-nested stream loop became one loop over a synchronous iterator, and the
suggestion gate moved behind `SuggestFollowUpsAsync` — **still short-circuiting, so the extra model
call is skipped rather than made and discarded** on the agent-run/reminder drain.

### What was deliberately left alone

The load-bearing invariants the gotchas name were preserved rather than tidied:

- **The positional `TurnAttachments` ↔ user-turn join** stays in its calling loop in both
  `RehydrateAttachmentUrlsAsync` and `GetHistoryAsync`. `ProjectUserTurn` advances the index by `ref`
  even for a turn it then skips, because skipping the increment misaligns every image after it.
- **The turn-scoped `_ephemeral` / `_turnLocation` / `_interactiveTurn` fields** are still cleared
  exactly where they were; no extraction crossed one of those lifetimes.
- **`FakeChatClient`'s trigger precedence** ("reminder" contains "remind", so both cancel rows must
  precede the schedule row) is now the table's row order, with the rules that were scattered across
  branch comments collected above it.
- **The `IngestDocumentAsync` bytes.** The first draft of the attachments extraction re-read the
  `IFormFile` inside the extracted method instead of passing the array already read. Caught before it
  built, but it is the precise failure mode a "pure relocation" must not have, and the reason every
  step here was verified against the suite rather than by eye.

## Alternatives considered

- **Suppress the eleven** with `#pragma` or an `.editorconfig` exclusion list. Rejected: eleven
  suppressions on day one is a gate that measures nothing, dressed as one that does.
- **Raise the threshold** until the code passes. The maximum was 41 (`MapStoredAgentEndpoints`), so
  the number that would have passed is one that could never fail anything real.
- **Scope S3776 away from `AgentApi.*.cs`**, on the argument that it measures the ADR 0040 idiom
  rather than complexity. Tempting, and half true — but the extraction turned out to *improve* those
  files rather than merely appease the metric, which settled it. Excluding a whole file pattern would
  also have hidden genuine complexity growth inside handlers later.
- **Split `ChatCoreAsync` further**, into a non-iterator pipeline. Rejected as a much larger change
  to the app's hottest path for no further gate benefit; it is at the threshold, not over it.

## Consequences

- **`cognitive_complexity_max: 15` now blocks.** A method over the threshold fails `dotnet build`,
  which CI runs — so it fails a PR, not just a log.
- **The format gate's carve-out is gone.** `dotnet format --verify-no-changes` runs unqualified again
  in CI and in CLAUDE.md's Commands table; `QualityGateTests.FormatGate_CarriesNoComplexityCarveOut`
  keeps it that way, because re-adding it would blind the one job that catches a regression earliest.
- **New endpoints should be registered as method groups.** An inline lambda is not forbidden, but a
  partial that accumulates several will trip the gate — which is now a build error, discovered
  immediately rather than in review.
- **One new file**: `src/Agent42.Backend/Documents/DocumentIndexRegistration.cs`. It is inside an
  existing feature folder, so `ProjectLayoutTests.ExpectedFolders` is unchanged.
- **No behaviour change and no new tests.** That is the claim, and it is worth stating plainly rather
  than implying: the existing 825 tests are the evidence, and none of them were adjusted to pass.

## Verification

Full suite green with every S3776 violation gone (`dotnet build` reports none across the solution),
`dotnet format --verify-no-changes` clean **without** `--exclude-diagnostics`, and
`check-doc-paths` clean.

Each step was verified against its own tests before the next began, rather than batching the
validation to the end — the attachment/skills/MCP/agent-run endpoint suites after each partial, the
109 `AgentGrainTests` after the `FakeChatClient` trigger table (it drives every tool test, so a
precedence slip would surface there), and the attachment-rehydrate and image-generation suites after
the grain extractions.

The gate was also checked for vacuity in the direction that now matters, by building a deliberately
convoluted throwaway method: with S3776 at `error` the solution build **failed** with
`error S3776: … Cognitive Complexity from 34 to the 15 allowed`, and removing the probe restored a
clean build. That is the difference between this ADR and the state ADR 0050 left behind — the gate
can fail, and was made to.
