# Plan: the dream proposes edits to, and creation of, the user's skills

planned-against: main @ e78705e   (suggested branch: `feature/dream-skill-proposals`; a feature, so not `fix/*`)

## Overview
The nightly fleet dream (ADR 0124) learns team-design lessons (auto-applied) and, since ADR 0143, proposes targeted edits to the
user's fleet prompts that only the user's **Approve** applies. Extend that second mechanism to the user's **uploaded skills**
(ADR 0049): (1) targeted `replace`/`append` edits to an installed skill's body, (2) creating a new skill. Both stay **pending**
in the `/fleet/` Lessons view until Approve; Dismiss keeps a tombstone. Why: a procedural, reusable lesson ("when doing X in
this codebase, do Y") belongs in a skill, not as prose in the planner prompt. User decisions (this conversation):
- Do BOTH slices (adapt and create).
- The dream MAY write skills whose guidance covers shell-capable tools (the Copilot shell). **No shell-specific content rule,
  and no gated-term rule for skills** (0143's `GatedTerms` stays prompt-only). The Approve diff is the control.
- Guard against one-off skills: evidence from at least **3** different tasks that do not share a workspace.

## Affected areas (verified against the tree)
- `src/Agent42.Agents/Fleet/FleetLesson.cs` — `FleetLessonsDocument` (add `SkillProposals`), `FleetDreamRun` (add `SkillsProposed`), new `FleetSkillProposal` + kind/status constants. Flat namespace `Agent42.Agents` (ADR 0039).
- `src/Agent42.Backend/Fleet/FleetPromptProposals.cs` — the EXEMPLAR for the new pure `FleetSkillProposals.cs` (Validate / Apply / Merge / Expire, no I/O). Reuse `Normalize`, `InvisibleProblem`/`IsInvisible`/`LookalikeProblem` (make them `internal` or move to a shared static; do not duplicate).
- `src/Agent42.Backend/Fleet/FleetLessons.cs` — `EvidenceProblem` uses `options.DreamMinSupport`; add an overload taking an explicit minimum.
- `src/Agent42.Backend/Fleet/FleetDreamer.cs` — third tool `propose_skill_change` + a third framed data block (the user's installed skills). `FleetDreamOutcome` gains skill drafts + refused count.
- `src/Agent42.Backend/Fleet/FleetLessonService.cs` (591 lines) — approve/dismiss and the run merge. Put the new code in a **partial** `FleetLessonService.Skills.cs` (keeps S3776 and file size in check); `WithUserLockAsync` is private there and must be reachable.
- `src/Agent42.Backend/Fleet/AgentApi.FleetLessons.cs` — view + 2 endpoints; badge count in `FleetLessonService` (~line 94).
- `src/Agent42.Backend/UserSkills/AgentApi.UserSkills.cs` — `UploadSkillAsync` orchestrates normalize → store → grain row → supersede cleanup → auto-enable **inline**. Extract to a shared `UserSkillInstaller` (new, same folder) so Approve installs through the identical path. Pure refactor first.
- `src/Agent42.Agents/UserSkills/SkillPackage.cs` — add read of a stored zip's `SKILL.md` and rebuild-with-new-`SKILL.md`; `ISkillPackageStore.ReadAsync(userId, skillId)` already returns the zip.
- `src/Agent42.Backend/wwwroot/fleet/fleet.js` (+ `fleet.css`) — Lessons view (`proposalsBlock`, `proposalCard`, ~line 1659–1730). `/fleet/` is outside `SHELL`: **no `sw.js` bump** (ADR 0143 consequence; confirm no `wwwroot/js/` file is touched).
- `src/Agent42.Backend/Fleet/FleetPrompts.cs` — the shipped `dream` prompt default: one encouraging paragraph only.
- Docs: new ADR 0146, `.agentic/gotchas.md`, `CLAUDE.md` feature map (Fleet row and Selectable-tools row), the fleet feature doc under `docs/features/` if it describes the dream.

## Context & constraints
- **The approval is the control** (ADR 0143). Checks keep the review honest, they do not judge meaning. Skill text reaches the model through `load_skill` (a tool result), and **natively** in a Copilot chat/fleet agent (ADR 0085), where the agent has a shell. A skill's text does not GRANT any tool (picker keys do, ADR 0021/0048); a skill can only advise using one. The user accepted this; ADR 0146 must record it as a residual, as must the card ("Approving installs it and turns it on").
- **Targets**: only the user's **uploaded** skills (`IUserSkillsGrain` rows). Never a shipped skill, and never a companion name (`websearch`, `scheduledprompt`, ADR 0126). **A created skill may not take ANY shipped skill's name** (not just companions): an upload that shadows a shipped skill silently replaces it, and `websearch`'s SKILL.md holds the ADR 0011 mitigation text (see `ShadowingWarnings` in `AgentApi.UserSkills.cs`). Also not an existing uploaded name (that is an edit).
- **Frontmatter is code-built / frozen.** Create: the model supplies `name`, `description`, `body`; code assembles `SKILL.md` and runs it through `SkillPackage.Normalize` (the framework is the authority on validity; `name` must equal the directory name and pass `SkillPackage.IsSafeSkillName`). Edit: `find`/`replacement` apply to the **body only** (text after the closing `---`); name and description are never changed (the picker key is `skill:{name}`).
- **The `description` is the injection point (critic #1).** `BuildMarkdown` writes it into frontmatter, which `SkillPackage` scans minimally. A create's `description` must be ONE line (no `\n`/`\r`), ≤ 200 chars (`FleetSkillTools.MaxListedDescriptionLength`), pass `FleetLessons.TextProblem`-style character rules, and contain no `:` followed by space, no quote and no `---`. Test that the built SKILL.md round-trips through `SkillPackage.Normalize` with the SAME `Name` and `Description`. `name` is checked by `SkillPackage.IsSafeSkillName`.
- **Lookalike and invisible baselines (critic #2).** For an edit, `LookalikeProblem` compares against the skill's current SKILL.md, as for prompts. For a create there is no current text, so the baseline is the user's OTHER installed skills' text plus nothing else: a non-Latin letter or mark that occurs nowhere there is refused. `IsInvisible` also refuses emoji (surrogates) and every non-plain space in a skill; accepted and stated in the tool description so the model writes plain text.
- **Edit targets (critic #4).** Editable = a row in the user's `IUserSkillsGrain` whose name is NOT a shipped or companion name. An upload that shadows a shipped name (for example an uploaded `websearch`) is therefore **refused as a target** (the name rule wins over "uploaded only"). At Approve, an edit whose skill was deleted or re-uploaded since is `outdated` (409), not an error.
- **Size is measured on the whole SKILL.md** (frontmatter included) against `SkillToolLimits.MaxSkillBodyLength`, because that is what `load_skill` returns (`TruncatingSkillsProvider`). Test the boundary on both sides.
- **SKILL.md only.** A created skill has no resource files; scripts (`.py/.sh/.ps1/...`) are dropped by `SkillPackage` anyway (`ScriptExtensions`). An edit rebuilds the stored zip with the other entries untouched.
- **Size**: created body ≤ 6,000 chars; edit `find` ≤ 1,500, `replacement` ≤ 2,000 (the 0143 caps); the RESULT must stay ≤ `SkillToolLimits.MaxSkillBodyLength` (16 KiB) or `load_skill` truncates it. Install cap `SkillToolLimits.MaxInstalledSkillsPerUser` (25) applies to a create; a full user is refused at propose time and again at Approve.
- **Re-apply on the CURRENT text** (ADR 0143 gotcha "RE-APPLIED"): read the skill's `SKILL.md` as it is at Approve; `find` not exactly once → `outdated` (409); append already at the end → already applied (idempotent). Compare with `\n` endings.
- **Create approve is idempotent too**: if the name now exists with an identical body it counts as applied; if it exists with different text → `outdated`.
- **Auto-enable only on create** (`AutoEnableUploadedSkillsAsync`): a re-install for an edit must NOT re-tick a skill the user switched off. Consequence to state in the card: an approved create is on for the default selection immediately (existing agents/conversations with a materialised override still need a manual tick, ADR 0049).
- **Evidence**: ≥ 3 different non-sharing workspaces (new constant `FleetSkillProposals.MinSupport = 3`, applied as `Math.Max(options.DreamMinSupport, 3)`). Not a new `Fleet:*` option (0143: caps are constants; a user-settable one needs ADR 0135 descriptors AND `FleetUserSettings`).
- **Caps** (constants, like 0143): ≤ 2 skill proposals per run, one pending per skill name, ≤ 5 pending skill proposals total, decided ones capped at 50, pending expire after `LessonTtlDays`.
- **Rules live in the tool description and the code-built block**, not the `dream` prompt (gotcha "A rule the dream's model must follow goes in the tool description"): a user who edited `dream` keeps their old text.
- **Untrusted input**: the dream reads task digests; the skills block is the user's own text and is framed as data with `FleetToolLimits.FrameAsData`, like `PromptsBlock`. Each skill excerpt is capped (3,000 chars, total 15,000); a truncated body says so, and an edit's `find` is still validated against the FULL body.
- **No new switch**: part of the existing dream toggle; `Fleet:DreamEnabled` stays the operator kill switch. No `Access:Owners` relevance.
- **Egress (ADR 0013 extension, same destination as 0124/0143)**: the user's own skill bodies now also go to their chosen dream model. No new destination.
- **Persistence**: `lessons.json` is our own Blob JSON, not grain state; the new fields are trailing + defaulted, an older document reads, no migration (prove it with a test, ADR 0143 did the same).
- **`MaxIterations = 4`** (model round trips per run) was sized for two tools. A test (`Dreamer_ThreeToolRun_FitsIterations`) scripts a realistic run (lessons, one prompt change, one skill change, one refused-then-fixed retry). If it needs more than 4, raise the constant to 6 in this change and say so in the ADR; do not leave it as a watch item. The skill threshold of 3 must appear in the tool description (the `dream` prompt's `minSupport` placeholder carries the lesson threshold, 2, and a user-edited `dream` prompt never sees newer text).
- **Locking**: Approve holds the per-user lessons lock across reading and writing `lessons.json` AND the skill install (like 0143's prompts). The skill upload endpoint has no such lock; a concurrent upload of the same skill can still race. Accepted, same as `prompts.json` in 0143.
- Resolve `UserSkillInstaller` / `ISkillPackageStore` in the Backend as a **registration both hosts make** (`AddAgent42Api()` or the existing skills registration); check `DevHost` has an in-memory `ISkillPackageStore` (`InMemorySkillPackageStore` exists) so the DevHost UI works offline. `AgentApiReachabilityTests` must stay green.
- **How `FleetLessonService` gets the installer (critic #3).** It is a singleton built positionally in tests (`FleetTaskLessonsTests`). Add ONE optional trailing constructor parameter `UserSkillInstaller? skills = null` (resolved by DI when registered; no mandatory both-hosts registration, ADR 0105 / the `/api/speech` pattern). When it is null: the dream skips the skills block and the third tool is not offered, Approve answers 409 "Skills are not available on this deployment" and nothing else changes. Existing constructions keep compiling.
- **Zip layout (critic #5).** A stored skill zip is `{name}/SKILL.md` plus optional resources (`SkillPackage.Normalize` output). The edit path rebuilds exactly that layout with the new SKILL.md and installs it as `"{name}.zip"` through the installer; the create path installs the bare `SKILL.md` text as `"SKILL.md"`.
- Governing ADRs: 0124, 0143 (extends/amends), 0049, 0048, 0126, 0085, 0013, 0144. Gotchas to read first: the two 0143 entries (~line 4949 onward), the fleet-lesson and skills entries (`grep -n "skill" .agentic/gotchas.md`).

## Steps
1. **Extract `UserSkillInstaller`** (pure refactor, no behaviour change). Move the normalize → cap check → store → grain row → supersede cleanup → (optional) auto-enable block of `UploadSkillAsync` into a service with `InstallAsync(userId, fileName, bytes, autoEnable, ct)` returning stored rows + skipped lines + error, plus `ReadSkillMarkdownAsync(userId, name, ct)`. The endpoint keeps its size/HTTP checks and calls the installer. Add `SkillPackage` helpers for reading and replacing `SKILL.md` in a zip. *Verify:* `dotnet test --filter "FullyQualifiedName~UserSkills"` unchanged and green; add installer tests (autoEnable on/off, cap, replace keeps other zip entries).
2. **Model.** `FleetSkillProposal` (Id, SkillName, Kind, Find, Replacement, Description, Rationale, Evidence, Status, CreatedAt, DecidedAt), `FleetSkillProposalKind` (replace/append/create), reuse the status set (+ outdated), `FleetLessonsDocument.SkillProposals`, `FleetDreamRun.SkillsProposed`. *Verify:* a JSON round-trip test, including an older document without the fields.
3. **Pure rules `FleetSkillProposals`** (parallelizable with 1): `Validate(draft, installedSkills, shippedNames, existing, taskWorkspaces, options)`, `Apply(currentSkillMarkdown, proposal)` (body-only; idempotent append), `BuildMarkdown(name, description, body)`, `Merge`, `Expire`, `ExistingProblem`. Reuse the invisible-character, lookalike, text-rule (`FleetLessons.TextProblem` for the rationale) and evidence checks; **no gated terms**. *Verify:* `FleetSkillProposalsTests` (new), modelled on `FleetPromptProposalsTests`.
4. **Dreamer.** `propose_skill_change` tool (description carries every rule), `SkillsBlock()` data block, outcome fields, per-run refusal rules. `FleetLessonService.DreamOverAsync` reads the user's installed skills (name, description, body via the installer) and the stored skill proposals and passes them in; `FinishRunAsync` merges them under the lock and sets `SkillsProposed`. *Verify:* `FleetDreamTests` additions.
5. **Approve / dismiss** in `FleetLessonService.Skills.cs`: Approve re-reads the current `SKILL.md`, `Apply`s, installs through the installer (create → `autoEnable: true`, edit → false), marks approved; conflicts mark `outdated`. *Verify:* `FleetLessonServiceTests` additions (double Approve → one install; outdated; create when name now exists; cap reached at Approve).
6. **API.** `GET /api/fleet/lessons` returns `skillProposals` (with `applies`); `POST /api/fleet/lessons/skill-proposals/{id}/approve|dismiss`; the badge count includes pending skill proposals. *Verify:* `FleetLessonApiTests` additions; `AgentApiReachabilityTests`; no Easy Auth `excludedPaths` change (`EasyAuthExclusionTests`).
7. **UI** (depends on 6). A "Skill changes" block in the Lessons view: edit = before/after diff (same `proposalDiff`), create = name, description and full body preview; the note "Approving installs this skill and turns it on" (create) / "Approving changes your installed skill" (edit). *Verify:* the existing static asset tests for `fleet.js` (find them with `grep -rln "fleet.js" tests`); a manual check in DevHost (`dotnet run --project tools/Agent42.DevHost`, `/fleet/`, Lessons) is **hand-off to the user if it needs a browser session**, report SKIPPED rather than claim it.
8. **Docs** (last): `/decide` → ADR 0146 (amends 0143 and 0049, extends 0124; records the shell/Copilot trade-off, the no-gated-terms choice, create-may-not-shadow-shipped, auto-enable on create); `/gotcha` entries (skill proposals re-apply on the current SKILL.md and never touch the frontmatter; the installer is the one install path); `/sync-docs` for `CLAUDE.md` map rows and the fleet doc; update the `dream` default prompt paragraph. Run `dotnet test` and `dotnet format --verify-no-changes`.

Parallelizable: step 1 ‖ steps 2–3 (separate files). Steps 4–7 are sequential. Use worktree isolation if dispatching two writers.

## Acceptance criteria → test mapping
| AC | Test | File |
|----|------|------|
| Skill proposals and the new run field round-trip; an older `lessons.json` reads | `SkillProposals_RoundTrip_OlderDocumentReads` | `tests/Agent42.Tests/FleetSkillProposalsTests.cs` (new) |
| An edit of a shipped or companion skill is refused | `Validate_RefusesShippedAndCompanionTargets` | same |
| A create named like ANY shipped skill, or an installed one, is refused | `Validate_Create_RefusesShippedAndExistingNames` | same |
| Evidence from fewer than 3 non-sharing workspaces is refused | `Validate_NeedsThreeWorkspaces` | same |
| Invisible/lookalike characters, over-size and over-16 KiB result (whole SKILL.md, both sides of the boundary) are refused | `Validate_ScreensText_AndSize` | same |
| A create's description with a newline, `---`, `: ` or a quote is refused; a valid one round-trips through `SkillPackage.Normalize` with the same name and description | `Validate_Description_*`, `BuildMarkdown_RoundTripsThroughNormalize` | same |
| Create's lookalike baseline is the user's other skills | `Validate_Create_LookalikeBaseline` | same |
| An uploaded skill that shadows a shipped name is not an edit target | `Validate_RefusesShadowingUploadAsTarget` | same |
| An edit whose skill was deleted or re-uploaded since is `outdated` (409) | `ApproveSkillEdit_SkillGone_IsOutdated` | `tests/Agent42.Tests/FleetLessonServiceTests.cs` |
| No installer registered: no third tool, no skills block, Approve is 409 | `NoInstaller_DegradesGracefully` | `tests/Agent42.Tests/FleetLessonServiceTests.cs` |
| A realistic three-tool run with one refusal fits `MaxIterations` | `Dreamer_ThreeToolRun_FitsIterations` | `tests/Agent42.Tests/FleetDreamTests.cs` |
| No gated-term refusal: shell/tool wording is accepted | `Validate_AllowsShellAndToolGuidance` | same |
| An edit never touches the frontmatter | `Apply_OnlyEditsTheBody` | same |
| Apply re-applies on the current text; outdated when `find` is gone; append idempotent | `Apply_ReappliesOnCurrentText_Outdated_AppendIdempotent` | same |
| One pending per skill, ≤ 2 per run, ≤ 5 pending, dismissed not re-proposed, pending expire | `Merge_Caps_Tombstones_Expiry` | same |
| The dream tool accepts a valid proposal and returns the refusal text otherwise; rules are in the tool description | `Dreamer_ProposeSkillChange_*`, `ProposeSkillDescription_HoldsTheRules` | `tests/Agent42.Tests/FleetDreamTests.cs` |
| The skills block frames the user's skills as data and caps them | `SkillsBlock_FramesAndCaps` | same |
| A run stores skill proposals pending and counts `SkillsProposed`; a failed run keeps what was accepted | `Run_StoresSkillProposals` | `tests/Agent42.Tests/FleetLessonServiceTests.cs` |
| Approve edit installs the new SKILL.md, keeps other zip entries, does not re-enable a disabled skill | `ApproveSkillEdit_*` | same |
| Approve create installs and auto-enables; double Approve installs once; cap and name clash at Approve → conflict | `ApproveSkillCreate_*` | same |
| Dismiss keeps a tombstone | `DismissSkillProposal_*` | same |
| API lists, approves, dismisses; badge counts them; 404/409 shapes | `FleetLessonApi_SkillProposals_*` | `tests/Agent42.Tests/FleetLessonApiTests.cs` |
| The upload endpoint behaves exactly as before the extraction | existing suite + `Installer_*` | `tests/Agent42.Tests/UserSkillsApiTests.cs`, `UserSkillInstallerTests.cs` (new) |
| The new endpoints are mapped (reachability) and not Easy-Auth-excluded | existing | `AgentApiReachabilityTests`, `EasyAuthExclusionTests` |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` at plan time)
- `coverage_new_code_pct_min: 80` — measure with `node .agentic/scripts/check-new-code-coverage.mjs`. Report the number; never estimate.
- `cognitive_complexity_max: 15` — SonarAnalyzer S3776 at build (`dotnet build Agent42.slnx`); split like `HindsightRegistration`, don't game it.
- `dotnet format Agent42.slnx --verify-no-changes`, `dotnet test Agent42.slnx`.
- Hard rules: branch + MR only (never `main`); no package added (no version to resolve); no Azure RBAC.

## Out of scope
- Deleting a skill, renaming one, or changing a skill's description/frontmatter.
- Resource files or scripts in a created skill (SKILL.md only).
- Proposing edits to **shipped** skills or companion skills.
- Auto-applying anything; a separate toggle; a user-settable cap or support threshold.
- A one-click revert (store the before-text) and measuring whether an approved skill helped — same follow-ups as ADR 0143.
- Skill proposals from **chat** history (the dream's digest is fleet tasks only).
- Extending the other agent kinds' prompts (`dream`, `copilot`, `copilot-report` stay off the prompt allowlist).

## Review
`critic` verdict: no blocking contradiction with the ADRs or gotchas; paths, line references and "ADR 0146 unused" verified. Six findings, all accepted and folded in above:
1. Frontmatter injection through a created skill's `description` → rule + round-trip test added (Context, AC table).
2. Undefined lookalike baseline for a create, and emoji/space refusals → baseline defined as the user's other skills; refusals stated.
3. `FleetLessonService` had no way to receive the installer → one optional trailing parameter, graceful degradation, test added.
4. Edit target deleted or shadowing a shipped name → `outdated` and refused-as-target, tests added.
5. Size measured on body vs file, and zip layout → whole SKILL.md; layout `{name}/SKILL.md` stated.
6. `MaxIterations` was a watch item → now a scheduled test with a decision rule.
Rejected: none. (The "~line 4949" gotcha pointer is fragile; executor finds them by the entry titles: "A rule the dream's model must follow goes in the tool description, never only in the `dream` prompt" and "An approved prompt proposal is RE-APPLIED to the current prompt, never written back from the stored text".)

## Divergences from the plan (as implemented, 2026-11-26)
- **`UserSkillInstaller` takes `IServiceProvider`** (not `ISkillPackageStore` directly) and resolves the store optionally, with an `Available`
  flag. It is a `TryAddSingleton` in `AddAgent42Api()`, so it is always resolvable and adds no mandatory both-hosts port. `FleetLessonService`
  still takes it as an optional trailing parameter; "no installer" means "no store". The installer also exposes `ListAsync`,
  `ReadPackageAsync` and `ReadSkillMarkdownAsync`.
- **`FleetLessons.EvidenceProblem` got an optional `minSupport` parameter**, not a separate overload.
- **The create description is a whitelist** (letters, digits, spaces and `. , ; ( ) - / % +`, starting with a letter or digit), stricter than
  the plan's "no `: `, quote or `---`". It rules out every YAML-significant character at once.
- **`MaxIterations` went from 4 to 6.** `Dreamer_ThreeToolRun_FitsIterations` scripts a model that calls the three tools one by one, retries
  one refusal and answers: five round trips. Recorded in ADR 0146.
- **A full user at Approve leaves a create pending** (409, "Delete one first") instead of marking it `outdated`: it is not stale, the user can free a slot.
- **The dream's idle reason text** is now "The model proposed no lessons, prompt changes or skill changes." (an existing test was updated).
- **`FleetProposalDecision` gained a trailing `SkillName`** so the approve route can return it.
- **Quality gates:** the full suite has 6 failures, none in this change: 5 live-model tests (HTTP 401, no credentials here) and
  `OtelConfigurationTests.WithAppInsights_GrpcEnvEndpoint_ReachesTheCollectorWithHeaders`, which passes alone (11/11) and fails under full-suite load.
