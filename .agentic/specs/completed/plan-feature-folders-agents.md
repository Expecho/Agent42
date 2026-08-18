# Plan — feature folders in Agent42.Agents

planned-against: main @ 584c53d

## Overview

`src/Agent42.Agents` is 73 flat `.cs` files in one `namespace Agent42.Agents;`, plus a
2070-line `AgentGrain.cs` that inlines every feature's tool logic. Reorganise the project
into top-level feature folders with folder-matching namespaces, and split `AgentGrain`
into per-feature `partial` files so each ADR's grain surface is an openable file rather
than a line range.

Why now: the project has absorbed ~20 ADRs' worth of features onto a flat layout, and the
next feature makes it worse, not better. Nothing about the change is behavioural — it is a
pure move + namespace + partial-split, with the doc debt paid in the same MR.

## Affected areas

- `src/Agent42.Agents/**` — all 73 files move; `AgentGrain.cs` splits into 10 parts.
- `.editorconfig` — **new file** at repo root (none exists today).
- 79 files get `using`-line changes only, no logic: `src/Agent42.Backend` (24),
  `tools/Agent42.DevHost/Program.cs` (1), `tests/Agent42.Tests` (54).
- `.agentic/gotchas.md` (56 references on 47 lines), `CLAUDE.md`, `docs/features/*.md`
  (44 references across 16 files).
- `.agentic/decisions/0039-*.md` — new ADR; a new `.agentic/gotchas.md` entry.
- `tests/Agent42.Tests/` — three new test files.

## Context & constraints

**The alias strings are frozen — do not "fix" them.**
Every serializable and grain-interface type carries an explicit literal
`[Alias("Agent42.Agents.X")]` (53 of them; e.g. `AgentGrainState.cs:8`, `IAgentGrain.cs:9`),
and the alias is a free-form identity token, *not* a mirror of the namespace. After the move
these strings will no longer match the real namespace. **That is correct and required.**
Rewriting them to the new namespaces is the one edit in this plan that could orphan
existing conversations in Cosmos. This trap spans 53 attributes in ~35 files, so it is
booked as a **gotcha entry** (step 7), not just an ADR line and a code comment.

**What actually keys a Cosmos document is the grain type, and no grain declares one.**
The persisted item is keyed on `GrainId` = `{GrainType}/{key}`. None of the 12 grain classes
carries `[GrainType]` (`AgentGrain.cs:19`, `UserProfileGrain.cs:21`,
`UserProviderConfigGrain.cs:20`, `UserPushGrain.cs:32`, `UserShareLinksGrain.cs:19`,
`ShareLinkDirectoryGrain.cs:23`, `AgentKeyDirectoryGrain.cs:23`, `AgentRegistryGrain.cs:18`,
`ConversationIndexGrain.cs:13`, `UsageRollupGrain.cs:14`, `UserMcpConfigGrain.cs:21`,
`UserMcpOAuthPendingGrain.cs:54`) — all fall through to Orleans' naming convention. The
convention appears to be class-name-based, and no class name changes here, so the move is
*expected* to be inert. **The plan does not rely on that expectation: step 2 pins all 12
grain ids in a test before anything moves.** This is the highest-consequence mechanism in
the change and it is not the one `[Alias]` governs.

**Be honest about what the serialization test can and cannot prove.** Orleans elides the
type token when a value's runtime type matches its statically-declared type, and all 12
state roots plus their members are concretely typed. So golden blobs would very likely still
deserialize even if aliases changed — the blobs mainly guard `[Id(n)]`/member drift, and a
golden test with no negative control is unfalsifiable. Step 3 therefore **requires a
mutation check**. Note also that a hand-built `Serializer<T>` does **not** exercise the
Cosmos `IGrainStorageSerializer` that `AddCosmosGrainStorage`
(`src/Agent42.Backend/Program.cs:372-379`) installs, so this plan **cannot** settle ADR
0013's "which serializer does Cosmos actually use" question — do not claim it does.

**A repo-wide IDE0130 rule breaks the build — scope it.**
`src/Agent42.ServiceDefaults/Extensions.cs:13` declares `namespace
Microsoft.Extensions.Hosting;` on purpose (the Aspire shared-project convention):
`src/Agent42.Backend/Program.cs:32` calls `builder.AddServiceDefaults()` with **no**
`using Agent42.ServiceDefaults;` in its using block (lines 1-13), and AppHost and DevHost do
the same. A repo-wide `[*.cs]` IDE0130-as-error both fails its own verification and, if
auto-fixed, breaks three projects. The rule must be scoped to `[src/Agent42.Agents/**.cs]`.

**CI hard-blocks on doc paths, and main is already red.**
`.github/workflows/pr-checks.yml` runs `.agentic/scripts/check-doc-paths.mjs`, which
`existsSync`-checks every backtick-quoted path in `CLAUDE.md`, `.agentic/gotchas.md`,
`.agentic/gotchas/*.md` and `.agentic/decisions/*.md`. It **currently exits 1 on main**:
`CLAUDE.md:461` names `wwwroot/app.js` (ADR 0037's bullet) where the file is
`src/Agent42.Backend/wwwroot/app.js`. Pre-existing, not caused by this work — fix it in
step 0 so the gate is meaningful. The checker does **not** scan `docs/` or `.agentic/specs/`,
so those references are doc debt (hard rule 2) rather than CI failures. The ADRs reference
only `Skills/websearch/SKILL.md`, which does not move.

**Decisions taken in the interview (all four confirmed by the user):**

1. Folder-matching namespaces (`Agent42.Agents.Web`), not folders-only — enforced by a new
   scoped `.editorconfig` rule. Cost accepted: 79 files get a `using` change.
2. `AgentGrain` splits into partials that live **in the feature folders**. Consequence
   accepted: a partial class is one namespace, so parts under `Web/`, `Location/` etc.
   declare `namespace Agent42.Agents.Conversations;` and need a **scoped IDE0130 exception**
   for the `AgentGrain.*.cs` glob.
3. Top-level feature folders, **not** the `Feature/` prefix.
4. Scope is the Agents project + docs. `Agent42.Backend` (34 flat files) and
   `Agent42.Tests` (81 flat files) keep their layout; only their `using` lines change.

**No ADR or gotcha contradicts this.** ADR 0005 ("never renumber `[Id(n)]`") and ADR 0013
(never rename `AgentGrainState.DeploymentName`) constrain *members*, which this plan does not
touch. The gotchas exemplar table has 4 rows pointing at files that move (lines 21, 22, 23,
28); the row at line 30 points at `Skills/scheduledprompt/SKILL.md`, which does not.

**Prior art to preserve:** the canonical shape is contract + in-memory impl in
`Agent42.Agents`, production impl in `Agent42.Backend` (gotchas exemplar row, line 28). The
feature folders should make that shape more visible, never break it.

**Accepted design consequence:** `AgentGrain.StorageProviderName` (`AgentGrain.cs:21`) is
consumed by 11 `[PersistentState(..., AgentGrain.StorageProviderName)]` attributes spread
across 7 of the new folders, so most feature namespaces will carry
`using Agent42.Agents.Conversations;` for that one const. Deliberately not relocated — one
storage provider genuinely is shared, and moving the const buys a cleaner using-block at the
cost of churn. Record it in the ADR rather than designing around it.

## Target layout

15 feature folders; `Skills/` unchanged (its csproj `Content Include="Skills\**\*"` glob must
keep working — do not move it). The stored-agent feature folder is `StoredAgents/`, not
`Agents/`, which would produce the absurd `Agent42.Agents.Agents`.

| Folder | Files |
|---|---|
| `StoredAgents/` (7) | `AgentDefinition`, `AgentBinding`, `AgentRegistryGrain`, `IAgentRegistryGrain`, `AgentKeyDirectoryGrain`, `IAgentKeyDirectoryGrain`, `ApiKey` |
| `Attachments/` (3) | `AttachmentRef`, `IAttachmentStore`, `InMemoryAttachmentStore` |
| `Conversations/` (7) | `AgentGrain`, `AgentGrainState`, `IAgentGrain`, `AgentStreamEvent`, `ChatTurn`, `ConversationIndexGrain`, `IConversationIndexGrain` |
| `Documents/` (10) | `DocumentChunk`, `DocumentChunker`, `DocumentSegment`, `DocumentMediaTypes`, `DocumentTextExtractor`, `IDocumentTextExtractor`, `IDocumentIndex`, `InMemoryDocumentIndex`, `NoEmbeddingGenerator`, `DeterministicEmbeddingGenerator` |
| `ImageGeneration/` (2) | `IImageGenerationService`, `ImageGenerationChatClient` |
| `Location/` (4) | `IGeocoder`, `CoordinatesOnlyGeocoder`, `UserLocation`, `GeoToolLimits` |
| `Mcp/` (6) | `IUserMcpConfigGrain`, `UserMcpConfig`, `UserMcpConfigGrain`, `UserMcpOAuthPendingGrain`, `IUserMcpToolProvider`, `McpToolLimits` |
| `Models/` (5) | `IChatClientFactory`, `IUserChatClientFactoryProvider`, `UserProviderConfig`, `UserProviderConfigGrain`, `IUserProviderConfigGrain` |
| `Push/` (4) | `IPushNotificationSender`, `NullPushNotificationSender`, `IUserPushGrain`, `UserPushGrain` |
| `Scheduling/` (1) | `ReminderInfo` |
| `Sharing/` (5) | `ShareToken`, `IShareLinkDirectoryGrain`, `ShareLinkDirectoryGrain`, `IUserShareLinksGrain`, `UserShareLinksGrain` |
| `Speech/` (1) | `ISpeechService` |
| `Telemetry/` (7) | `UserTelemetryContext`, `UsageContracts`, `UsageEvent`, `UsageRollupGrain`, `IUsageRollupGrain`, `UsageRollupState`, `UsageSpanProjector` |
| `Tools/` (1) | `ToolCatalog` |
| `Users/` (3) | `UserProfile`, `UserProfileGrain`, `IUserProfileGrain` |
| `Web/` (7) | `IWebSearch`, `InMemoryWebSearch`, `WebSearchResult`, `IWebPageFetcher`, `InMemoryWebPageFetcher`, `WebPageContent`, `WebToolLimits` |

### AgentGrain partial split

All parts declare `namespace Agent42.Agents.Conversations;` regardless of folder.

| New file | Members (current line refs in `AgentGrain.cs`) |
|---|---|
| `Conversations/AgentGrain.cs` | fields + ctor (21-148), `OnActivateAsync` (150), `UserIdFromGrainKey` (202), `ConversationIdFromGrainKey` (211), `GetSelectedModelAsync` (483), `BindAgentAsync` (485), `GetAgentBindingAsync` (531), `ChatAsync` (606), `EnqueuePromptAsync` (636), `PublishUsageApplication` (685), `ChatCoreAsync` (693), `GetHistoryAsync` (1237), `PurgeAsync` (1292), `GetDate` (1318), **`ReceiveReminder` (1775)**, `RebindModelAsync` (2032), `StripToolHistory` (2050) |
| `Conversations/AgentGrain.FollowUps.cs` | `GenerateFollowUpsAsync` (942) through `FollowUpSentenceSplit` (1027) — ADR 0036 |
| `Tools/AgentGrain.BuildAgent.cs` | `BuildAgent` (233), `EffectiveEnabledTools` (420), `SetEnabledToolsAsync` (538), `GetEnabledToolsAsync` (557), `ExpandMcpWildcard` (569) |
| `Documents/AgentGrain.Documents.cs` | `BuildDocumentSearchProvider` (430), `ExcludeInjectedContextMessages` (446), `IsInjectedOrReplayed` (449), `WithDocumentAwareness` (469), `RegisterDocumentAsync` (585), `SearchDocumentContextAsync` (1327) |
| `Web/AgentGrain.WebTools.cs` | `WebSearchAsync` (1487), `FetchWebPageAsync` (1553), `BuildFetchPayload` (1607), `IsFetchableUrl` (1699), `RememberFetchableUrl` x2 (1702, 1738), `RememberFetchableUrlsInUserText` (1720), `Normalize` (1757), `UrlPattern` (1767) |
| `Location/AgentGrain.LocationTool.cs` | `GetCurrentLocationAsync` (1651) |
| `Scheduling/AgentGrain.Scheduling.cs` | `GetScheduledPromptsAsync` (1288), `SchedulePromptAsync` (1367), `CancelScheduledPromptAsync` (1419), `CancelAllScheduledPromptsAsync` (1454) |
| `ImageGeneration/AgentGrain.ImageTurn.cs` | `AssistantImageIdKey` (1037), `GenerateImageTurnAsync` (1043), `RecordImageTurnAsync` (1156), `FindMostRecentImageRef` (1193), `AssistantImageRef` (1230) |
| `Attachments/AgentGrain.Attachments.cs` | `AlignTurnAttachmentsToHistory` (1851), `RehydrateAttachmentUrlsAsync` (1883), `WithheldImagePlaceholder` (1965), `StripImageParts` (1967), `RehydrateAssistantImageAsync` (1993) |
| `Push/AgentGrain.Push.cs` | `SendAwayReplyPushAsync` (223) |

Rules for the split:

- **`ReceiveReminder` stays in the core part, with `ChatAsync`.** It is not filed under
  `Scheduling/` even though it is the reminder callback, because `.agentic/gotchas.md:335`
  and `:358` pin the safety of `_turnLocation` and `_ephemeral` on *two* clears that must be
  read together: the `finally` in `ChatAsync` **and** `ReceiveReminder` nulling on entry.
  Splitting those across files is exactly the reading failure the gotchas exist to prevent.
  Only the schedule/cancel tool methods move to `Scheduling/`.
- **All instance fields stay in `Conversations/AgentGrain.cs`** (`AgentGrain.cs:38-125`) —
  every dependency and every turn-scoped flag. Three *static/const* members move with their
  feature by design: `ExcludeInjectedContextMessages` (446), `AssistantImageIdKey` (1037),
  `WithheldImagePlaceholder` (1965).
- One residual split of a documented invariant is unavoidable: `_ephemeral`'s write-skip in
  `GenerateImageTurnAsync` lands in `ImageGeneration/` while the flag and its clear stay in
  core. Step 6 must add an explicit cross-file pointer to the `:358` gotcha. The behaviour is
  already covered by `EphemeralChat_RunsTheTurn_ButPersistsNothing`,
  `EphemeralFlag_IsClearedAfterTheTurn_SoTheNextNormalTurnPersists` and
  `EphemeralChat_SchedulingIsRefused_NoReminderRegistered`
  (`tests/Agent42.Tests/AgentGrainTests.cs:1401, 1433, 1453`), plus
  `ReceiveReminder_HasNoUserLocation` (`:1266`) for `_turnLocation` — all must stay green.
- A shared private helper stays in the core part unless exactly one slice calls it
  (`SerializeOrNull` at 1029 — place by call site).
- Every part repeats `public sealed partial class AgentGrain` and carries only the `using`
  lines it needs. `[GeneratedRegex]` partial methods sit in the same part as their callers.
- **No logic edits in the split commit.**

## Steps

**Step 0 — Fix the pre-existing doc-path break.**
`CLAUDE.md:461` names `wwwroot/app.js`; correct it to `src/Agent42.Backend/wwwroot/app.js`.
*Verify:* `node .agentic/scripts/check-doc-paths.mjs`; expect exit 0.

**Step 1 — Pin grain identity before anything moves.**
New `tests/Agent42.Tests/GrainIdentityTests.cs`: for each of the 12 grain classes, resolve a
grain reference from the TestCluster and assert `GetGrainId().ToString()` equals a hard-coded
literal captured from today's behaviour (e.g. `agent/user%2Fconv`). Hard-code the expected
strings in the test source — do not compute them from the type, which would make the test
symmetric and blind. This is the guard for the one mechanism that keys Cosmos documents.
*Verify:* `dotnet test Agent42.slnx --filter GrainIdentity` green **before** any move, and
unchanged after steps 4, 5 and 6.

**Step 2 — Add the root `.editorconfig`.** (parallelizable with steps 1 and 3)
Minimal and deliberately whitespace-free so it cannot trigger a repo-wide reformat:
`root = true`; a `[src/Agent42.Agents/**.cs]` section — **scoped, never `[*.cs]`** — with
`dotnet_style_namespace_match_folder = true` and `dotnet_diagnostic.IDE0130.severity = error`;
then `[src/Agent42.Agents/**/AgentGrain.*.cs]` setting `IDE0130` to `none`, commented with
why one partial class cannot span namespaces. Add a comment recording that the rule is scoped
because `src/Agent42.ServiceDefaults/Extensions.cs:13` declares `Microsoft.Extensions.Hosting`
by Aspire convention and must never be "fixed".
*Verify, both directions:* (a) `dotnet format Agent42.slnx --verify-no-changes` is clean on
the untouched tree — proves no collateral rules; (b) **negative control** — temporarily put a
file under `src/Agent42.Agents/Tmp/` with the wrong namespace, confirm the same command exits
non-zero, then revert. If (b) passes silently, IDE0130 has no fixer in `dotnet format` and
AC4's enforcement claim is false — fall back to the step-8 layout test as the sole guard and
say so in the ADR. Note `dotnet build` will *not* catch this: no project sets
`EnforceCodeStyleInBuild`.

**Step 3 — Serialization golden blobs, authored on the CURRENT layout.**
New `tests/Agent42.Tests/SerializationCompatibilityTests.cs` plus a committed fixture of
base64 blobs, one per persisted state root: `AgentGrainState`, `AgentRegistryState`,
`AgentKeyDirectoryState`, `ConversationIndexState`, `ShareLinkDirectoryState`,
`UsageRollupState`, `UserMcpConfigState`, `UserMcpOAuthPendingState`, `UserProfileState`,
`UserProviderConfigState`, `UserPushState`, `UserShareLinksState`. Populate every state with
non-default values (a session string, a scheduled prompt, an attachment, a model id) so a
dropped field fails the assert.
- **Generate the fixture explicitly**, via a throwaway `dotnet run app.cs` (the
  `spike-dotnet` skill) or a one-shot `[Fact(Skip = "generator")]`, and commit the output.
  **A test that writes the fixture when absent is forbidden** — it would be self-fulfilling.
- **Mutation check, mandatory:** temporarily drop one `[Alias]` and separately change one
  namespace, confirm the suite goes red or record honestly that it does not. Expect it may
  *not* — see the constraint above — in which case document that these blobs guard `[Id(n)]`
  and member drift, and that grain identity (step 1) is the real safety net.
- Also add `[Alias("Agent42.Agents.UsageEventKind")]` to `UsageEvent.cs:9`. It is the only
  `[GenerateSerializer]` type without one; it is a concretely-typed member of an aliased
  record so no live exposure is claimed — this is cheap future-proofing, nothing more.
*Verify:* `dotnet test Agent42.slnx --filter SerializationCompatibility` green before any
move; green again after steps 4, 5 and 6.

**Step 4 — Move the 72 non-`AgentGrain` files.**
Create the 15 folders, `git mv` each file per the table, change its `namespace` line, fix
intra-project `using`s. Delete `src/Agent42.Agents/Feature/Telemetry` and
`.../Feature/Web` **if present** — they are untracked empty directories from an abandoned
attempt and will be absent in a clean clone or worktree. **Do not touch any `[Alias(...)]`
string.**
*Verify:* `dotnet build src/Agent42.Agents/Agent42.Agents.csproj` compiles (the rest of the
solution is still broken here — expected).

**Step 5 — Sweep `using`s across the other three projects (79 files).**
24 in `src/Agent42.Backend`, `tools/Agent42.DevHost/Program.cs`, 54 in
`tests/Agent42.Tests`. Four references are qualified rather than `using`-based and need
re-qualifying by hand: `src/Agent42.Backend/OpenAIImageGenerator.cs` lines 38 and 61
(`Agent42.Agents.GeneratedImage`, qualified to dodge `OpenAI.Images.GeneratedImage`),
`src/Agent42.Backend/WebPushClientAdapter.cs:2` and its mirror at
`tests/Agent42.Tests/WebPushClientAdapterTests.cs:6` (a `using PushSubscription = ...` alias).
*Verify:* `dotnet build Agent42.slnx`; `dotnet test Agent42.slnx` (full suite, including
steps 1 and 3); `dotnet format Agent42.slnx --verify-no-changes` — all green.

**Step 6 — Split `AgentGrain` into the 10 partial files.** Separate commit, mechanical.
*Verify:* `dotnet build Agent42.slnx` and `dotnet test Agent42.slnx` green;
`git diff -M -C --numstat` on the commit shows every part file recognised as a rename or copy
of `AgentGrain.cs` (any part reported as a wholly new file means content changed — re-check
it); named regression tests `ReceiveReminder_HasNoUserLocation`,
`EphemeralFlag_IsClearedAfterTheTurn_SoTheNextNormalTurnPersists` and
`EphemeralChat_SchedulingIsRefused_NoReminderRegistered` still pass.

**Step 7 — Docs, ADR and gotcha.** (the three sub-items are parallelizable)
- `.agentic/gotchas.md`: 56 references on 47 lines, plus the 4 exemplar rows at lines 21, 22,
  23, 28 (line 30 points at `Skills/`, unaffected). Where a gotcha names a member now in a
  partial (`FetchWebPageAsync`, `GenerateImageTurnAsync`, `_turnLocation`, `_ephemeral`,
  `RehydrateAttachmentUrlsAsync`, `BuildAgent`), repoint it at the specific part file — that
  is the point of the split — and add the `:358` cross-file pointer named in the split rules.
- **New gotcha entry:** `[Alias("Agent42.Agents.*")]` strings are frozen and deliberately no
  longer mirror the namespace; "correcting" one orphans persisted state. Evidence:
  `Conversations/AgentGrainState.cs`, ADR 0039. Also put a comment at the
  `AgentGrainState` alias itself.
- `CLAUDE.md`, then `docs/features/*.md`: 44 references across 16 files (`web-search.md`,
  `custom-agents.md`, `document-qa.md`, `image-generation.md`, `push-notifications.md`,
  `current-location.md`, `code-interpreter.md`, `conversation-history.md`,
  `streaming-replies.md`, `tool-selection.md`, `skills.md`, `model-picker.md`,
  `bring-your-own-models.md`, `image-attachments.md`, `scheduled-reminders.md`,
  `remote-mcp-servers.md`).
- `/decide` writes `.agentic/decisions/0039-feature-folders-in-agents-project.md`: the four
  interview decisions and rejected alternatives; frozen aliases; the scoped IDE0130 rule and
  its two exceptions (`AgentGrain.*.cs`, ServiceDefaults); why `ReceiveReminder` stays in
  core; the `StorageProviderName` using-coupling; and that Backend/Tests stay flat on
  purpose. **Do not claim it settles ADR 0013's serializer question** — nothing here
  exercises the Cosmos `IGrainStorageSerializer`.
*Verify:* `node .agentic/scripts/check-doc-paths.mjs` exits 0; open the paths named by three
rewritten gotchas and confirm the member is in the file named.

**Step 8 — Layout guard test and final gates.**
New `tests/Agent42.Tests/ProjectLayoutTests.cs`, following the source-tree-walk precedent at
`tests/Agent42.Tests/UserTelemetryProcessorTests.cs:181-195` (`RepoRoot()` +
`Directory.EnumerateFiles(..., SearchOption.AllDirectories)`) — **not** the HTTP-based
`VoiceConversationAssetsTests`. Assert: (a) no `.cs` sits directly in the project root;
(b) every `.cs` under `src/Agent42.Agents` declares a namespace matching its folder, except
files matching `AgentGrain.*.cs`; (c) the 10 `AgentGrain` part files exist at their planned
paths; (d) `Conversations/AgentGrain.cs` still declares the turn-scoped fields `_turnLocation`,
`_ephemeral`, `_interactiveTurn`. Keep (d) to those three names — a general "only the core
declares fields" assertion is not implementable (three static/const members legitimately move).
*Verify:* `dotnet test Agent42.slnx` full suite; `dotnet format Agent42.slnx
--verify-no-changes`; `dotnet list Agent42.slnx package --vulnerable --include-transitive`
reports none; `node .agentic/scripts/check-doc-paths.mjs` exits 0; then render the gate table
below and report each line with its evidence.

## Acceptance criteria to test mapping

| # | Acceptance criterion | Test / command | Lives in |
|---|---|---|---|
| AC1 | Grain ids — what keys every Cosmos document — are byte-identical after the move | `GrainIdentityTests` over all 12 grains, expectations hard-coded pre-move | `tests/Agent42.Tests/GrainIdentityTests.cs` (new) |
| AC2 | Persisted state written before the move still deserializes field-for-field | `SerializationCompatibility_*` golden blobs, 12 state roots, plus the recorded mutation-check result | `tests/Agent42.Tests/SerializationCompatibilityTests.cs` (new) |
| AC3 | No behaviour change anywhere | `dotnet test Agent42.slnx` — full suite green; the 54 touched test files differ only in `using` lines | all 81 test files |
| AC4 | The turn-scoped-field invariants survive the split | `ReceiveReminder_HasNoUserLocation`, `EphemeralFlag_IsClearedAfterTheTurn_SoTheNextNormalTurnPersists`, `EphemeralChat_SchedulingIsRefused_NoReminderRegistered` pass unchanged | `tests/Agent42.Tests/AgentGrainTests.cs:1266, 1433, 1453` |
| AC5 | Folder/namespace invariant is enforced, not merely applied | step 2's negative control, plus `ProjectLayoutTests` assertions (a) and (b) | `.editorconfig`, `tests/Agent42.Tests/ProjectLayoutTests.cs` (new) |
| AC6 | `AgentGrain` is split as planned and keeps its turn-scoped state in the core part | `ProjectLayoutTests` assertions (c) and (d) | `tests/Agent42.Tests/ProjectLayoutTests.cs` (new) |
| AC7 | Every documented path resolves | `node .agentic/scripts/check-doc-paths.mjs` exits 0 | CI job `agent-docs-check` |
| AC8 | The frozen-alias trap is recorded where an agent will read it | gotcha entry exists and names an evidence path; ADR 0039 exists | `.agentic/gotchas.md`, `.agentic/decisions/0039-*.md` |

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (2026-08-18):

| Gate | Threshold |
|---|---|
| `new_issues` | 0 |
| `new_security_issues` | 0 |
| `duplication_pct_max` | 3 |
| `coverage_new_code_pct_min` | 80 |
| `cognitive_complexity_max` | 15 |
| `vulnerable_packages` | 0 (hard block) |
| `outdated_packages` | report only |
| enforcement | local: soft_warning; CI: hard_block on build, tests, format, vulnerable packages, doc paths |

Note on coverage: this MR adds almost no executable code, so `coverage_new_code_pct_min: 80`
applies to the three new test files' own helpers only. Do not report a coverage delta on
moved code as if it were new code.

Branch: `refactor/feature-folders-agents` (never `main` — hard rule 1).

## Out of scope

- Reorganising `src/Agent42.Backend` (34 flat files) or `tests/Agent42.Tests` (81 flat
  files). Only their `using` lines change here. Each is its own follow-up MR.
- Settling ADR 0013's open question about which serializer Cosmos grain storage uses. It
  needs a test against the configured `IGrainStorageSerializer`, which nothing here builds.
- Extracting the `AgentGrain` feature slices into separate collaborator *classes*. That is
  the real decoupling, but it moves turn-scoped state across object boundaries and is a
  behavioural change — a partial split is deliberately the non-behavioural step.
- Rewriting the 128 path references in `.agentic/specs/completed/*.md`. They are a historical
  record, the doc-path checker does not scan them, and editing them would falsify the record.
- Any change to `[Id(n)]` numbering, `[Alias]` strings, state names, grain keys, storage
  provider names, `src/Agent42.ServiceDefaults/Extensions.cs`'s namespace, or the `Skills/`
  folder and its csproj `Content` glob.

## Review

Adversarial review by the `critic` subagent, 2026-08-18. Verdict: **not a clean pass** —
14 findings, 2 blockers. All were accepted; the spec above is the corrected version.

| # | Finding | Disposition |
|---|---|---|
| S1 | BLOCKER — a repo-wide `[*.cs]` IDE0130 rule fires on `ServiceDefaults/Extensions.cs:13` (deliberate Aspire `Microsoft.Extensions.Hosting` namespace); auto-fixing it breaks Backend, AppHost and DevHost | Accepted, verified independently. Rule scoped to `[src/Agent42.Agents/**.cs]`; the reason is recorded in the `.editorconfig` and the ADR |
| S2 | BLOCKER — the golden test could not prove what it claimed (Orleans elides type tokens for concretely-typed roots), had no negative control, could not settle ADR 0013, and never said how the fixture is produced | Accepted. Step 3 now mandates a mutation check, forbids a self-writing fixture, names the generation route, and the ADR-0013 claim is deleted and moved to Out of scope |
| S3 | MAJOR — grain type resolution, which actually keys Cosmos documents, was never addressed; no grain declares `[GrainType]` | Accepted. New step 1 pins all 12 grain ids pre-move with hard-coded expectations (now AC1) |
| S4 | MAJOR — AC6 ("only the core declares fields") contradicted the split table, which moves three static/const members | Accepted. AC6 narrowed to the three named turn-scoped fields |
| S5 | MAJOR — steps 5, 7, 8 had no runnable verification | Accepted. All three now have commands |
| S6 | MODERATE — `dotnet format --verify-no-changes` may exit 0 for a diagnostic with no fixer; enforcement was asserted, not proven | Accepted. Step 2 gains a negative control and a documented fallback |
| S7 | MODERATE — AC2 claimed test suites pass "unmodified" while step 5 edits 54 of them | Accepted, reworded |
| S8 | MODERATE — the split separated the two halves of the `_turnLocation` / `_ephemeral` invariants documented at `gotchas.md:335` and `:358` | Accepted — the best finding. `ReceiveReminder` now stays in the core part; the one residual split is named, pointed at, and covered by four named regression tests (AC4) |
| S9 | MODERATE — the frozen-alias trap spans 53 attributes but was booked only into an ADR and one comment | Accepted. Step 7 adds a gotcha entry; AC8 checks it |
| S10 | LOW — `Agents/` yields `Agent42.Agents.Agents`; `StorageProviderName` forces most features to `using ...Conversations` | Accepted in part: folder renamed to `StoredAgents/`. The const is deliberately **not** relocated — one storage provider genuinely is shared; recorded as an accepted consequence instead |
| S11 | LOW — count drift (Backend 24 not 23, sweep 79 not 78, gotchas 56 refs on 47 lines, specs 128, Backend 34 flat files, 4 moving exemplar rows not 5) | Accepted, all re-counted and corrected |
| S12 | LOW — the empty `Feature/` dirs are untracked and the `.vs` evidence is gitignored, so neither exists in a clean clone | Accepted. Step 4 says "if present"; the `.vs` citation is gone |
| S13 | LOW — the `UsageEventKind` mixed-revision rationale was not evidenced | Accepted, downgraded to explicit future-proofing |
| S14 | LOW — `ProjectLayoutTests` cited `VoiceConversationAssetsTests`, which reads over HTTP, not the source tree | Accepted, now cites `UserTelemetryProcessorTests.cs:181-195` |

Nothing was rejected. The critic separately confirmed and could not break: the 73-file target
table (exact, no omissions or phantoms), the four hand-qualified references, the 12 persisted
state roots, the alias inventory (53 aliases; `UsageEventKind` the sole gap), the gate-table
values, step 0's reproduction, and the absence of any other namespace-encoding mechanism
(Dockerfile, `.slnx`, deploy workflow, OTel source names, log filters, DI scanning, JSON
discriminators).

## Execution notes — where the implementation diverged from the plan

Executed 2026-08-18 on `refactor/feature-folders-agents`, five commits. All eight steps done.
The plan held up well; the divergences below are the honest record.

**1. Step 4 removed the empty `Feature/` dirs, as written — they were present.**
`src/Agent42.Agents/Feature/Telemetry` and `.../Feature/Web` existed, untracked and empty, and were
deleted. The plan's "if present" hedge was the right call.

**2. Step 2's negative control passed, so the fallback was never needed.**
The plan allowed for `dotnet format` having no IDE0130 fixer, in which case AC4's enforcement claim
would be false and the layout test would be the sole guard. It does fire: a wrong-namespace file under
`src/Agent42.Agents/Tmp/` makes `dotnet format --verify-no-changes` exit 2 with `error IDE0130`. The
`AgentGrain.*.cs` exception glob was verified in the same way (exit 0 with a deliberately
wrong-namespace `AgentGrain.NegControl.cs`). Both temporary files were reverted.

**3. Step 3's mutation check came back mostly NEGATIVE, and the plan's own hedge was correct.**
Three mutations, not the two the plan named:

| Mutation | Result |
|---|---|
| Renumber one `[Id(n)]` (`StoredModel.SupportsSpeech` 6 → 7) | **caught** |
| Change an `[Alias]` string (`StoredMcpServer`) | **not caught** |
| Move a serialized type + its grain to another namespace | **not caught** |

The plan predicted the alias mutation might not fail; it did not. The namespace mutation was added
because it is the one this refactor actually performs — and its negative result is the reassuring
one. So the blobs guard `[Id(n)]`/member drift only, `GrainIdentityTests` is the real safety net, and
both the class doc and ADR 0039 say exactly that rather than the stronger claim.

One trap surfaced that the plan did not anticipate: the blobs visibly contain
``ReadOnlyListInterfaceCodec`1[[Agent42.Agents.StoredMcpServer]]`` for interface-typed members, which
*looks* alias-derived. The alias mutation proves it is not load-bearing on the read path — do not
infer alias safety from a green suite. This is recorded in the gotcha entry.

**4. A fixture improvement the plan did not call for.** `StoredModel`'s five capability bits were
initially all `true`, which would have made a renumbering that swaps two adjacent bools undetectable.
They now alternate. That change is what made mutation 1 detectable at all — "populate with non-default
values" is not sufficient for adjacent same-typed fields; they must be *distinguishable*.

**5. Step 6's verification criterion was not achievable and was replaced with a stronger one.**
The plan asked that `git diff -M -C --numstat` show every part as a rename or copy of `AgentGrain.cs`.
It does not, and cannot: splitting one 2084-line file into ten leaves no part above git's 50%
similarity threshold (the largest, the core part, is ~40%). Git reports them as additions. Instead the
"no logic edits" property was verified by **reconstruction**: the 1805 non-blank member lines of the
pre-split file and the 1805 of the ten parts are identical as a multiset (`diff` exit 0). That is
direct evidence of content identity rather than a similarity heuristic, so it is strictly stronger.

**6. Step 6 also moved `AgentGrain.cs` itself.** The plan left it at the project root through steps 4
and 5 (a valid intermediate state — root namespace matches root folder) and it moved as part of the
split. Worth noting because it means the `AgentGrain.StorageProviderName` using-coupling the plan
predicted only appeared at step 6, not step 4.

**7. `docs/features/scheduled-reminders.md` gained a row rather than having one repointed.**
Its single "Tools + `ReceiveReminder`" row named one file that is now two — the schedule/cancel tools
in `Scheduling/` and the callback in the core part. Splitting the row is the point of the exercise, so
it became two rows with the reason recorded inline.

**8. The `_ephemeral` cross-file pointer names THREE files, not two.** The plan anticipated the
`GenerateImageTurnAsync` write-skip landing in `ImageGeneration/`. The `SchedulePromptAsync` refusal
also moved, to `Scheduling/`. The gotcha entry names all three.

**9. Test-suite flakiness observed under load (not a regression).** Two of the full-suite runs during
execution aborted with Orleans TestCluster `TimeoutException`s and a silo-membership
"I have been told I am dead" host crash, while runs on an otherwise idle machine were fully green.
The failing runs took 21-23 minutes against ~6 minutes idle, and the failures were in different tests
each time — machine load, not this diff. Recorded here so a future reader does not chase it as a
regression of this MR.

**Not diverged:** the 15-folder target table was exact; the four hand-qualified references were
exactly the four named; the 12 state roots and 12 grains were exactly right; `SerializeOrNull` did
belong in core by call site; `ReceiveReminder` staying in core was the right call; and no `[Alias]`
string was touched.

**10. One out-of-plan change: a transitive security pin.** The `vulnerable_packages: 0` hard gate
failed on `System.Security.Cryptography.Xml` 10.0.7 (four high-severity advisories), reached via
`Azure.Extensions.AspNetCore.DataProtection.Blobs` → `Microsoft.AspNetCore.DataProtection` — so it is
a **production** dependency (the ADR 0018 DP key ring), not a test-only one. It was **pre-existing**:
`git diff main...HEAD` over `Directory.Packages.props`, every `.csproj` and `Agent42.slnx` was empty,
so the restore graph was byte-identical to `main` and `main` fails the same gate today. On the user's
instruction it was fixed here rather than deferred, by pinning 10.0.11 (resolved from the feed, hard
rule 4) as a direct `PackageReference` in `Agent42.Backend` — Central Package Management does not
override a transitive version from a `PackageVersion` entry alone. `dotnet list package --vulnerable
--include-transitive` now reports all six projects clean. The pin carries a comment saying it is a
security pin, not a feature dependency, and when to drop it.

**11. Two gates cannot be measured in this repo and were reported as SKIPPED, not estimated.**
`duplication_pct_max` has no tool at all; `cognitive_complexity_max` has none that measures *cognitive*
complexity (the Roslyn `Metrics` target is not wired up, and `lizard` needs Python, which is not
installed). Cyclomatic complexity was measured instead, from the coverlet cobertura report: 22 methods
exceed 15, all pre-existing and — by the reconstruction check — unchanged by this MR. Per the working
agreement these two gates should be deleted from `quality-gates.yaml`; the user chose to leave them
and have the situation flagged rather than edit the gate file inside a refactor MR.

## Post-execution correction — the plan's central premise was wrong

Added 2026-08-18, after the branch had been fully built, reviewed and opened as PR #77.

**The plan's biggest divergence is not in the list above: it is that the plan was wrong about the
thing it cared most about.**

The plan asked "is this move inert for persisted state?", answered it with `GrainIdentityTests` and
golden serialization blobs, and explicitly listed *"Settling ADR 0013's open question about which
serializer Cosmos grain storage uses"* under **Out of scope**. Divergence note 3 above then recorded
the namespace mutation as "not caught" and called that "the reassuring result".

It was not reassuring. It was the test looking in the wrong place.

`AddCosmosGrainStorage` configures no serializer, so grain state is persisted by Orleans' default
`IGrainStorageSerializer` — `JsonGrainStorageSerializer`, Newtonsoft JSON with `$type` discriminators
carrying the **full CLR type name**:

```
{"$id":"1","$type":"Agent42.Agents.AgentGrainState, Agent42.Agents", ...}
```

Feeding a pre-move document to the post-move code throws
`JsonSerializationException: Error resolving type specified in JSON`, **on the root type**, so the read
fails whole. Shipping the folder-matching-namespace version would have made every conversation, stored
agent, user profile, provider config, MCP server, push subscription, share link and usage rollup
unreadable in one deploy.

**Why nothing caught it.** `SerializationCompatibilityTests` builds a `Serializer<T>` — Orleans'
*binary* serializer, used for grain-to-grain calls. Cosmos does not use it. The suite (596 green), the
lint gate, the `critic` review and CI were all measuring a path that was genuinely unaffected. The
critic even confirmed "the move is purely mechanical", which was true and beside the point.

**The generalisable lesson:** a passing test proves a property of *the path it exercises*. "We verified
serialization" was a meaningless sentence until it said **which serializer**. The plan's own "Out of
scope" line named the exact gap and it was still carried forward, because the gap was framed as an
unrelated open question rather than as a precondition of the plan's central claim. When a plan defers a
question, check whether its main conclusion depends on the answer.

**What changed as a result** (user chose "keep folders, drop the namespace change"):
- All 82 files reverted to `namespace Agent42.Agents;`. The 16 feature folders and the 10-file
  `AgentGrain` split are **kept** — they deliver the reviewability benefit; the namespace never did.
- The 79-file `using` sweep is gone entirely, so `Agent42.Backend`, `Agent42.DevHost` and
  `Agent42.Tests` are untouched except for the split's own effects. The diff shrank substantially.
- `.editorconfig` is **inverted**: it now sets `dotnet_style_namespace_match_folder = false` and
  IDE0130 to `none`, so the IDE stops offering the change that breaks production. What was an
  enforcement rule is now a guardrail against the trap.
- `ProjectLayoutTests.EveryFileDeclaresTheNamespaceOfItsFolder` became
  `EveryFileStaysInTheFlatNamespace` — the inverse assertion.
- New `GrainStorageSerializerTests` covers the real `IGrainStorageSerializer`: it pins the JSON+`$type`
  format, pins all 12 state-root type names as literals, and deserializes a committed real pre-move
  payload. Negative control reproduces the production exception exactly.
- Two gotcha entries replace the single frozen-alias one: the `$type` mechanism (the important one),
  and a corrected alias entry saying plainly that `[Alias]` governs the binary path and is **not** what
  protects data at rest — conflating the two is what caused this.
- ADR 0013's open question is now **settled** and recorded in ADR 0039: property names and type names
  are both load-bearing, so `AgentGrainState.DeploymentName` must not be renamed either.

**Still unverified:** no production document was read. The analysis is from the configured serializer
in this repo at this commit. Anyone planning a type rename should inspect a real document first.
