planned-against: main @ a446d80

# Plan: the fleet dream proposes prompt edits, the user approves or dismisses them

## Overview
The nightly fleet dream (ADR 0124) learns team-design lessons and adds them to the planner's user prompt. It never reads or
changes the user's editable fleet prompts (`FleetPrompts.Definitions`, overrides in Blob `{userId}/prompts.json`). The user
wants the dream to also reflect on those prompts and propose targeted edits grounded in what recent tasks showed. Each
proposal shows in the `/fleet/` Lessons view with a before/after diff and **Approve** / **Dismiss** buttons. Approve writes
the edit into the user's stored prompts. Dismiss changes nothing and keeps the proposal as a tombstone so it isn't proposed
again. ADR 0124 listed "prompt rewriting" as out-of-scope Slice 2. The human approval gate is what makes it acceptable to
let derived text reach a system prompt.

## Affected areas (verified against the tree at a446d80)
- `src/Agent42.Agents/Fleet/FleetLesson.cs`: lessons document model. New proposal record, status constants, a trailing
  `PromptProposals` list on `FleetLessonsDocument` and a `Proposed` count on `FleetDreamRun`.
- `src/Agent42.Backend/Fleet/FleetPromptProposals.cs` (NEW): pure rules (allowed keys, validate, apply, merge, expire),
  shaped like `FleetLessons.cs`.
- `src/Agent42.Backend/Fleet/FleetDigest.cs`: an agent-instructions excerpt on each plan line.
- `src/Agent42.Backend/Fleet/FleetDreamer.cs`: a second tool, `propose_prompt_change`, and a second data block in the input.
- `src/Agent42.Backend/Fleet/FleetPrompts.cs`: the `dream` default text gains a short prompt-proposal paragraph.
- `src/Agent42.Backend/Fleet/FleetLessonService.cs`: merges proposals in `FinishRunAsync`, adds `ApproveProposalAsync`,
  `DismissProposalAsync` and `NewCount`.
- `src/Agent42.Backend/Fleet/AgentApi.FleetLessons.cs`: `proposals` in the GET response, plus approve and dismiss routes.
- `src/Agent42.Backend/wwwroot/fleet/fleet.js` (lessons section, lines ~1592-1731) and `wwwroot/fleet/fleet.css`.
- Tests in `tests/Agent42.Tests/`: `FleetPromptProposalsTests.cs` (NEW), `FleetDigestTests.cs`, `FleetDreamTests.cs`,
  `FleetLessonServiceTests.cs`, `FleetLessonApiTests.cs`, `FleetPageAssetsTests.cs`, `FleetStoreTests.cs`.
- Docs: `.agentic/decisions/0143-*.md` (NEW, via `/decide`), `.agentic/gotchas.md`, `docs/features/agent-fleet.md`, and the
  CLAUDE.md feature-map row for the agent fleet (add 0143).

## Context & constraints
**Interview decisions (2026-11-24, by the user):**
1. **Allowed prompts:** `planner`, `delegation`, `clarify`, `judge`, `retry`, `followup`, `moderator`. **Never** `dream` (the
   dream would rewrite its own instructions), `copilot` or `copilot-report` (guidance for the only agent kind with a shell).
   Enforced in code by an allowlist pinned by a test. Excluding only the three is not enough: an allowlist also keeps
   prompts added later out by default.
2. **Form:** targeted edits, not a full rewrite. Two kinds: `replace` (one exact passage `find` → `replacement`) and `append`
   (add `replacement` at the end). The UI shows a before/after diff. On Approve the edit is **re-applied to the CURRENT
   prompt**, so the user's own later edits elsewhere in that prompt survive.
3. **Agent instructions:** the digest gains a capped excerpt of each agent's instructions on its plan line. The `yaml` is
   already in `plan.submitted` (`FleetTeamRunner.Planning.cs:334`) and today `FleetDigest.PlanLine` keeps only `name`.
4. **Opt-in:** this is part of the existing dream toggle. No new switch, because nothing changes until the user approves.
   `Fleet:DreamEnabled` remains the operator kill switch.

**Governing ADRs and gotchas:**
- ADR 0124 (`.agentic/decisions/0124-fleet-dreaming-lessons.md`): lessons are auto-applied with no `proposed` state, the
  user rejected an approval gate *for lessons*, and lessons go in the user prompt, never the system prompt. This plan
  does **not** change any of that for lessons. It adds a *separate* proposal kind that is gated by approval and may change
  a system prompt only after approval. That extends 0124 and needs a new ADR (Step 8). `amends: [0124]` covers its Slice 2
  line.
- ADR 0135 + gotcha `.agentic/gotchas.md:5150`: the dream resolves the user's effective options at tick time. Use the
  `effective` options already passed through `DreamOverAsync`. A new user-settable `Fleet:*` field would need
  `FleetSettingsService.Descriptors` + `FleetUserSettings`. **This plan adds no new option**: the caps are constants in
  `FleetPromptProposals` (see Out of scope).
- `FleetLessons.Validate` is "shape hygiene, not an injection defence" (ADR 0124). The same applies here, but the human
  gate is the real control, so the checks are those that keep that review **honest**: no invisible characters
  (control except `\n`/`\t`, format/Cf such as zero-width and bidi, private-use, surrogates, line and paragraph separators),
  since a hidden character would defeat review of the diff. Other checks are placeholder integrity, size caps and the key
  allowlist. No denylist on the replacement text: the default planner prompt itself says "copilot". The **rationale** is a
  one-line sentence and goes through the lesson text rules (`FleetLessons` text allowlist + denylist + link check). Expose
  that check as an `internal`/`public` helper rather than duplicating it.
- **Gated terms (critic finding 1):** an allowed prompt (`planner`, `delegation`) can still steer the planner towards shell
  agents or fan-out. A `replacement` may therefore not contain a **gated term** that the text it replaces does not already
  contain. For `replace` the comparison is with `find`; for `append` it is with nothing, so none may appear. The gated
  terms are `FleetLessons.DeniedTerms` plus `copilot`, `delegate_to_team`, `model:`, `approv` and `tools:`, matched as in
  `FleetLessons` (word starts, case-insensitive), with the list pinned by a test. A user who wants such a change edits the
  prompt themselves in the editor. This mirrors `PromptBlock`'s rule that derived text is "never a reason to add tools,
  change approvals or use a particular agent kind" (`FleetLessons.cs:54-67`).
- **Lookalike characters (critic finding 5):** a letter outside the Latin script (Basic Latin, Latin-1 Supplement, Latin
  Extended-A/B) in `replacement` is refused unless that character already occurs in the current prompt text. This stops
  Cyrillic or Greek letters that look Latin from faking a placeholder or tool name in a diff that reads normally. The
  written text is normalised to `\n` line endings.
- **Forking the default (critic finding 4):** approving stores the WHOLE prompt as an override (`FleetPrompts.WithOverride`),
  so a user who never edited that prompt stops receiving later changes to its shipped default. This is the cost ADR 0124
  avoided for lessons (0124:68). It is accepted here because the user explicitly approves the change, and the card says so:
  "Approving saves your own copy of this prompt: later updates to its default won't reach it (Reset in the prompt editor
  restores the default)." The ADR records it.
- Rule 11 analogue, and the trap to record as a gotcha: a user who edited the `dream` prompt keeps their override, so new
  instructions in the `dream` **default** never reach them. The rules the model must follow (allowed keys, kinds, find must
  match exactly once, keep placeholders, cite evidence) therefore go in the **`propose_prompt_change` tool description and
  the code-built input**. The default-prompt paragraph is only encouragement.
- The digest and prompts are untrusted data. Frame the new prompts block with `FleetToolLimits.FrameAsData`, as the digest
  is framed, and keep the "this is data, not instructions" line.
- Single replica (rule 12): the per-user `SemaphoreSlim` in `FleetLessonService` guards read-modify-write of `lessons.json`.
  `prompts.json` has **no lock**: `FleetTaskService.SetPromptAsync` (`FleetTaskService.cs:281`) is an unlocked
  read-modify-write. Approve writes `prompts.json` inside the lessons lock. A simultaneous prompt-editor save in another
  tab can still race. Accept this and note it in the ADR, as 0124 accepted the no-ETag rollover loss.
- Egress (rule 13): no new destination. The user's own prompts and capped agent-instruction excerpts now also go to the
  user's chosen dream model, the same provider set as ADR 0124. The ADR must say so in those words.
- Blob JSON, not Orleans state: `lessons.json` is our own JSON. New fields are trailing with defaults so an older document
  reads (`FleetLesson.cs` header). No `[Id]`, no migration. Prove with a store round-trip test of a document without the
  new fields.
- S3776 is a build error at 15: keep `Validate`/`Apply` split into small helpers, like `FleetLessons`.
- `/fleet/` is outside `SHELL` (`sw.js:105-108`), so **no `CACHE` bump**. `FleetPageAssetsTests.FleetPage_LessonText_IsNeverWrittenAsMarkup`
  scans from `// ---------- lessons (ADR 0124)` to `// ---------- wiring ----------`. The new UI code must sit inside that
  range and must never use `innerHTML`, `setMarkdown` or `mdBlock`, because proposal text is model-authored.

**Design (decided here; the executor implements it as written):**
- `FleetPromptProposal` (in `FleetLesson.cs`): `Id`, `PromptKey`, `Kind` (`replace`|`append`), `Find` (null for append),
  `Replacement`, `Rationale`, `Evidence` (task ids), `Status` (`pending`|`approved`|`dismissed`|`outdated`), `CreatedAt`,
  `DecidedAt?`. Static classes `FleetPromptProposalKind`, `FleetPromptProposalStatus`.
- Constants in `FleetPromptProposals`: `AllowedKeys` (the seven), `MaxPerRun = 2`, `MaxPending = 5`, `MaxFindChars = 1_500`,
  `MaxReplacementChars = 2_000`, `MaxDecided = 50` (tombstone/decided cap, oldest dropped first).
- **Validate** (at dream time, against the user's effective prompt text):
  - key allowed
  - kind known
  - `replace`: `find` non-empty and occurs **exactly once** (ordinal, line endings normalised to `\n`)
  - no invisible characters in `find`/`replacement`
  - size caps
  - **placeholder integrity of the result**: every `{{x}}` in the current text is still present at least once (a
    duplicate of a known placeholder is harmless and allowed), and no `{{x}}` outside the definition's `Placeholders` is
    added
  - no gated term the replaced text lacks, and no non-Latin letter that isn't already in the prompt (see above)
  - result differs from current
  - rationale passes the lesson text rules
  - evidence passes the same min-support rule as lessons (`DreamMinSupport` distinct workspaces, ids in the digest)
  - no other **pending** proposal for that key
  - not a repeat of a **dismissed** one (same key + kind + normalised find + normalised replacement)
  - pending count under `MaxPending`

  Refusal texts go back to the model, as `submit_lessons` does.
- **Apply** (pure): `(currentText, proposal) → (newText | error)`. `replace` needs `find` exactly once in the CURRENT text,
  otherwise "outdated". `append` adds `\n\n` + replacement; if the current text already ends with the replacement it
  returns "already applied". This makes approve idempotent when the prompts write succeeded but the lessons write failed.
  It re-runs the placeholder-integrity check.
- **Dreamer:** a second `AIFunction` `propose_prompt_change(promptKey, kind, find, replacement, rationale, evidence[])`.
  `FleetDreamer` takes the user's `FleetPromptSet` (already passed) plus the pending and dismissed proposals, and validates
  inside the tool, as `Take` does. The input message gets a second framed block: the allowed prompts' **current effective
  text**, each under its key and title, and the keys that already have a pending proposal. `FleetDreamOutcome` gains
  `Proposals` + `ProposalsRefused`.
- **Service:** `FinishRunAsync` merges accepted proposals under the lock after a RE-READ, re-checking the pending-per-key,
  dismissed and `MaxPending` rules against the fresh document. The proposal merge is **not** gated on lessons: today
  `FinishRunAsync` skips `Merge` when `Accepted.Count` is 0, so restructure it so that proposals merge with zero
  lessons, and on a failed run too (the fail-open rule: what was accepted still counts). `FleetDreamer`'s
  `"The model proposed no lessons."` reason becomes "...no lessons or prompt changes" and applies only when both are
  zero.
- **Locking shape (critic finding 2):** `MutateAsync` takes a synchronous lambda over `lessons.json` only, so approve cannot
  use it. Extract the gate into `private async Task<T> WithUserLockAsync<T>(string userId, Func<Task<T>> body, CancellationToken)`
  and have `MutateAsync` call it. Approve runs its whole body inside `WithUserLockAsync`. It is the one caller allowed to
  hold the lock across two Blob reads and two writes; update the class doc's "short read-modify-writes ONLY" sentence to
  name that exception. A double-click on Approve must give the second call a 409, which a test pins.
- `ApproveProposalAsync(userId, id)` runs under that lock:
  1. Find the user's pending proposal (404 if missing, 409 if not pending).
  2. Read prompts and call Apply on the effective text.
  3. If outdated, mark it `outdated` and return 409 with the reason.
  4. Otherwise write prompts with `FleetPrompts.WithOverride`, then mark it `approved` + `DecidedAt`.

  `DismissProposalAsync` marks it `dismissed`. Expiry: a pending proposal older than `LessonTtlDays` is dropped by the
  next run's expire step, and decided ones are capped at `MaxDecided`. `NewCount` adds pending proposals **with
  `CreatedAt > Settings.SeenAt`**, the same rule as lessons, so the badge clears after `/seen` even while proposals stay
  pending. The pending count itself is shown in the proposals block's heading, not in the badge.
- **API:** the GET `/api/fleet/lessons` response adds `proposals`: pending first, then the decided ones. Each carries `key`,
  `title` (from `FleetPrompts.Find`), the proposal fields, and a computed `applies` bool for pending ones, which is whether
  Apply would succeed now, so the UI can grey out a stale one. `POST /api/fleet/lessons/proposals/{id}/approve` returns
  200 `{ key, text, edited }` like `SetFleetPrompt`, 404 for unknown or another user's id, and 409 `{ error }` for outdated
  or already decided. `POST /api/fleet/lessons/proposals/{id}/dismiss` returns 200 or 404. Both use `GetUserId()`, are
  never excluded from Easy Auth, and answer 503 without the service (the existing pattern).
- **UI:** a "Proposed prompt changes" block at the top of the Lessons view. Each card shows:
  - the prompt title and the rationale (plain text)
  - "Based on" task links (reuse the evidence links)
  - the diff: for `replace`, a removed `<pre>` (find) and an added `<pre>` (replacement); for `append`, "Added at the end:"
    + `<pre>`; always `textContent`
  - **Approve** and **Dismiss** buttons

  The card also carries the fork notice from "Forking the default" above.
  Approve calls approve, then refreshes the lessons. If the prompt editor's list `S.prompts` (`fleet.js:1511`) is
  loaded, it updates that key's entry `text` (through `lf`) and `edited` from the response. It leaves any unsaved
  `S.drafts[key]` alone, so the editor shows it as unsaved rather than losing it. A 409 shows its error on the card and refreshes. When a proposal no longer applies, the card shows "The prompt
  changed since this was proposed" with Approve disabled and Dismiss available. Decided proposals sit in a collapsed
  `<details>` ("Decided"). Use the existing card/badge/button classes in `fleet.css`. The dream log row (`dreamRunRow`,
  `fleet.js:1668`) adds "N prompt change(s) proposed" when `run.proposed > 0`.

## Steps
Each step: run the narrowest test named, then `dotnet build Agent42.slnx` (S3776).

1. **Model.** Add `FleetPromptProposal`, `FleetPromptProposalKind`, `FleetPromptProposalStatus`, the trailing
   `FleetLessonsDocument.PromptProposals` (default `[]`) and `FleetDreamRun.Proposed` (default 0) in `FleetLesson.cs`.
   Verify: new test in `FleetStoreTests.cs`. A lessons document with proposals round-trips, and an older JSON without
   the field reads as an empty list. `dotnet test --filter FullyQualifiedName~FleetStoreTests`.
2. **Pure rules.** New `FleetPromptProposals.cs` with `AllowedKeys`, `Validate`, `Apply`, `Merge`, `Expire`, plus the
   shared rationale text check exposed from `FleetLessons`. Verify: new `FleetPromptProposalsTests.cs` (see the AC table).
   `--filter FullyQualifiedName~FleetPromptProposalsTests` and `~FleetLessonsTests` (no regressions in the shared text
   rules).
3. **Digest excerpt** *(parallelizable with 2)*. In `FleetDigest.PlanLine`, for each agent append
   `name: <instructions excerpt>`. Take the text after `instructions:` in the yaml (or the whole yaml if absent), flatten it
   with `Flat`, and cap it at a new const `AgentInstructionChars = 150`. **Leave `MaxTaskChars` (3_000) and
   `DreamMaxDigestChars` unchanged**, so ADR 0124's digest bounds hold. A block that grows past the cap is cut by the
   existing `[cut]` rule. Verify: `FleetDigestTests`. The plan line carries a capped, one-line excerpt; a multi-line or
   tag-shaped instruction cannot break out of its line or the frame; the existing budget and cap tests pass unchanged.
4. **Dreamer** *(after 2)*. Add the `propose_prompt_change` tool and the framed prompts block, extend
   `FleetDreamOutcome`, and add a short paragraph to the `dream` default in `FleetPrompts.cs`. No new placeholder: the
   block is built in code. Verify: `FleetDreamTests`. Use `ScriptedDreamClient` (`FleetLessonTestSupport.cs:46`) and add a
   `ProposePrompt(...)` helper next to `Submit(...)`.
5. **Service** *(after 2, 4)*. Merge in `FinishRunAsync`, `ApproveProposalAsync`, `DismissProposalAsync`, `NewCount`, and
   the `Proposed` count on the run. Verify: `FleetLessonServiceTests`, copying the `ADisableOrDeleteMadeWhileTheDreamRuns_Survives`
   shape (:381) for the "dismiss during a run" race.
6. **API** *(after 5)*. GET shape plus the approve and dismiss routes in `MapFleetLessonEndpoints`. Verify:
   `FleetLessonApiTests` (fixture `FleetLessonApiFixture`), and that `AgentApiReachabilityTests` and `EasyAuthExclusionTests`
   still pass.
7. **UI** *(after 6)*. `fleet.js` lessons section plus `fleet.css`. Verify: `FleetPageAssetsTests`. A new test asserts the
   approve and dismiss endpoint strings and the proposals block id are wired. The existing markup-scan test must still
   cover the new code (place it before the wiring marker). Then a manual DevHost check (`dotnet run --project tools/Agent42.DevHost`):
   the canned LLM won't propose, so seed the store or the fake (as the existing DevHost lesson seeding does, if any) and
   click Approve and Dismiss. If DevHost can't build locally (see the memory note on SDK 11), report the check as SKIPPED.
8. **Docs.** `/decide` → ADR 0143 "The fleet dream proposes prompt edits that the user approves" (`extends: [0124, 0097]`,
   `amends: [0124]` for the Slice 2 line). It records:
   - the allowed keys
   - the approval gate as the control, and its residual: a human can approve a subtle steer
   - the invisible-character, lookalike and gated-term checks
   - forking the shipped default on approve
   - egress
   - the unlocked `prompts.json` race
   - the lock held across Blob I/O on approve
   - the rejected alternatives (see Review) Add a gotcha for
   the edited-dream-prompt trap (rules in the tool description) and the "re-apply on approve" rule. Update
   `docs/features/agent-fleet.md` (Lessons section) and add 0143 to the fleet row of the CLAUDE.md feature map. Then
   `/sync-docs`.
9. **Gates.** `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`, and the new-code coverage
   script.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| 1 | An older `lessons.json` without proposals reads; proposals round-trip | `LessonsDocument_WithoutProposals_ReadsEmpty_AndProposalsRoundTrip` | FleetStoreTests.cs |
| 2 | Only the seven allowed keys can be proposed; `dream`, `copilot` and `copilot-report` are refused; the list is pinned | `AllowedKeys_ArePinned`, `Validate_RefusesDreamAndCopilotPrompts` | FleetPromptProposalsTests.cs |
| 3 | `replace` needs `find` exactly once; zero or two matches are refused | `Validate_ReplaceNeedsExactlyOneMatch` | FleetPromptProposalsTests.cs |
| 4 | Invisible characters (zero-width, bidi, control except newline and tab, private-use, line and paragraph separators) are refused | `Validate_RefusesInvisibleCharacters` | FleetPromptProposalsTests.cs |
| 5 | A result that drops a placeholder or adds an unknown one is refused | `Validate_KeepsEveryPlaceholder_AndAddsNoUnknownOne` | FleetPromptProposalsTests.cs |
| 6 | The rationale follows the lesson text rules; evidence needs min support | `Validate_RationaleAndEvidenceFollowTheLessonRules` | FleetPromptProposalsTests.cs |
| 7 | One pending per key, a dismissed repeat is refused, and the per-run, pending and size caps hold | `Merge_OnePendingPerKey_DismissedIsNotReproposed_CapsHold` | FleetPromptProposalsTests.cs |
| 8 | Apply on the current text: a replace is outdated if `find` is gone; append is idempotent; another edit elsewhere survives | `Apply_ReappliesOnTheCurrentText_OutdatedWhenFindIsGone_AppendIdempotent` | FleetPromptProposalsTests.cs |
| 9 | Pending proposals expire after the TTL; decided ones are capped | `Expire_DropsOldPending_CapsDecided` | FleetPromptProposalsTests.cs |
| 9a | A replacement can't introduce a gated term its `find` lacks (an append can't contain one at all); the gated list is pinned; the default planner text's own "copilot" can still be kept by a replace whose `find` contains it | `Validate_RefusesNewGatedTerms_GatedTermsArePinned` | FleetPromptProposalsTests.cs |
| 9b | A non-Latin letter not already in the prompt is refused; one already present is accepted; written text uses `\n` | `Validate_RefusesNewLookalikeLetters_NormalisesLineEndings` | FleetPromptProposalsTests.cs |
| 10 | Plan lines carry a capped one-line instructions excerpt that can't escape its line | `PlanLine_CarriesACappedInstructionsExcerpt_OnOneLine` | FleetDigestTests.cs |
| 11 | The dream sees the allowed prompts' current text framed as data, and no excluded prompt | `TheModelSeesTheAllowedPromptsAsData_NotDreamOrCopilot` | FleetDreamTests.cs |
| 12 | A valid proposal is accepted; a refused one returns its reason to the model; the per-run cap holds | `ProposePromptChange_AcceptsValid_RefusesWithReason_CapsPerRun` | FleetDreamTests.cs |
| 13 | A run stores pending proposals and the `Proposed` count; **no prompt changes** without approval | `ARun_StoresPendingProposals_AndChangesNoPrompt` | FleetLessonServiceTests.cs |
| 14 | A dismiss made while a dream runs survives, and that proposal is not re-added | `ADismissMadeWhileTheDreamRuns_Survives` | FleetLessonServiceTests.cs |
| 15 | Approve writes the edit into `prompts.json` and marks it approved; outdated returns a conflict and marks it outdated | `Approve_WritesThePrompt_OutdatedIsAConflict` | FleetLessonServiceTests.cs |
| 13a | A run with zero lessons but an accepted proposal stores it; a run that failed after accepting one still stores it; the reason text accounts for proposals | `AProposalWithoutLessons_AndOnAFailedRun_IsStored` | FleetLessonServiceTests.cs |
| 15a | A second Approve of the same proposal (double-click) gets 409 and the prompt changes once | `Approve_Twice_SecondIsAConflict_PromptChangedOnce` | FleetLessonServiceTests.cs |
| 16 | The badge counts pending proposals created after `SeenAt`, and clears after `MarkSeen` while they stay pending | `NewCount_IncludesNewPendingProposals_AndClearsAfterSeen` | FleetLessonServiceTests.cs |
| 17 | GET lists proposals with `title` and `applies`; approve returns 200 and GET `/api/fleet/prompts` shows the new text; dismiss returns 200; another user's id returns 404 | `Proposals_ListApproveDismiss_ScopedToTheCaller` | FleetLessonApiTests.cs |
| 17a | After the user edits the prompt so `find` is gone, GET shows `applies=false` and approve returns 409 and marks it outdated; approving an already-decided proposal returns 409 | `Proposals_OutdatedAndDecided_AreConflicts` | FleetLessonApiTests.cs |
| 18 | Approve and dismiss answer 503 without the service | extend the existing unavailable test | FleetApiTests.cs (`FleetApiUnavailableTests`) |
| 19 | The UI wires the proposals block, both endpoints, the outdated state, the fork notice and the run count, and never writes proposal text as markup | `FleetPage_PromptProposals_AreWired` (asserts endpoint strings, the block id, the `applies` check and the `proposed` field), existing `FleetPage_LessonText_IsNeverWrittenAsMarkup` | FleetPageAssetsTests.cs |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at plan time)

| Gate | Threshold | Tool |
|------|-----------|------|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | 15 (S3776, build error) | `dotnet build Agent42.slnx` |
| `vulnerable_packages` | 0 | CI package-freshness job |
| Lint | no changes | `dotnet format Agent42.slnx --verify-no-changes` |

No new package. No `sw.js` bump (fleet is outside `SHELL`).

## Out of scope
- Full-prompt rewrites, and proposals for `dream`, `copilot` or `copilot-report`.
- Auto-applying any prompt change. Every change needs an explicit Approve.
- An undo of an approved change beyond the prompt editor's existing Reset-to-default. Possible follow-up: store the
  before-text for a one-click revert.
- New `Fleet:*` options for the caps (constants for now). If a user wants to tune them, add them through ADR 0135's
  descriptors later.
- Measuring whether an approved prompt change improved scores (0124's Slice 2 "retire a lesson by outcome" stays open).
- Changing how lessons are applied (still the planner's user prompt, still auto-applied).
- A lock or ETag on `prompts.json`.

## Review
Critic pass on 2026-11-24 found no blockers. Every finding was accepted and folded in above:
1. Gated terms on the replacement, so `planner`/`delegation` proposals can't add shell or fan-out steering. Accepted, see
   "Gated terms".
2. Approve can't use the synchronous `MutateAsync`. Accepted: `WithUserLockAsync`, plus a double-click test.
3. `NewCount` would never clear. Accepted: count only pending proposals newer than `SeenAt`.
4. Approve forks the shipped default. Accepted: the card shows a notice and the ADR records it.
5. Lookalike characters and line endings. Accepted: non-Latin letters must already be in the prompt, and text is
   normalised to `\n`.
6. AC gaps (zero-lesson merge, failed run, 409 paths, digest budget). Accepted: ACs 13a, 15a, 17a. `MaxTaskChars` is
   left unchanged instead of raised, so 0124's bounds hold.
7. Run-log display, duplicate placeholders, naming `S.prompts`, and this section. Accepted.

**Alternatives rejected during planning, for the ADR:**
- A full-prompt rewrite: hard to review, and it overwrites the user's manual edits (the user chose targeted edits).
- Auto-applying prompt edits like lessons: a system prompt is a higher-authority surface than the planner's user prompt
  (ADR 0124:68). Approval is the control.
- Including `dream`, `copilot` and `copilot-report`: self-modification, and steering of the shell agent kind.
- A separate toggle: nothing changes without approval, so the dream toggle is consent enough (the user's choice).
- A content denylist on the whole replacement: the shipped planner prompt itself names `copilot`. The relative gated-term
  rule is used instead.

## Implementation divergences (2026-11-24)
- **Extra screening rules beyond the plan:** an `append` whose text the prompt already contains is refused at validate
  time ("already contains"), and every space other than U+0020 (no-break, wide) is refused as invisible. Combining marks
  fall under the lookalike rule like non-Latin letters. Both came from the critic pass on the diff.
- **`FleetDreamRun.Refused` counts lessons only.** Refused proposals are counted on `FleetDreamOutcome.ProposalsRefused` and
  aren't added to the run's `Refused`; the run shows `Proposed`.
- **`FleetPromptApply`** reports `AlreadyApplied` for an append at the end of the prompt; approve then marks the proposal
  approved without writing `prompts.json` again.
- **`FleetLessonService.PromptsAsync`** was added so the API can compute `applies` and the approve response without
  depending on `FleetTaskService`.
- **The `FleetLessons` helpers** (`TextProblem`, `EvidenceProblem`, the new `TermRegex`) became `public` and take a label for
  the refusal text. The lesson messages are unchanged.
- **The proposals block** sits right under the view's intro, above the dream settings card. An approve/dismiss error is
  kept per proposal (`S.proposalErrors`) so it survives the refresh that follows a 409.
- **The manual DevHost check (Step 7) was SKIPPED:** DevHost keeps the fleet in memory with no lesson seeding, and the
  Chrome extension wasn't connected to drive a stubbed page. The UI wiring is covered by `FleetPage_PromptProposals_AreWired`.
