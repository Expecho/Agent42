# Plan — Long-term memory as a selectable tool

planned-against: main @ 8840fe8

## Overview

**Goal:** make Hindsight long-term memory (ADR 0056) a user-selectable entry in the ADR 0021 tools
picker — `memory` — controllable at all three existing layers (user default, per-conversation
override, per-agent snapshot). Unchecking it attaches no provider, so the turn makes **zero**
Hindsight calls: no recall *and* no retain.

**Why now:** ADR 0056 shipped memory as always-on with **no off switch but the operator's
`Hindsight:ApiKey`**. Its own *Alternatives considered* (`0056:162-164`) names a per-user opt-out "a
plausible and arguably desirable follow-up … the first thing to add if this graduates from
experiment", and its Consequences (`0056:129`) call the durable-injection risk "the strongest
argument **for** the per-user opt-out this iteration does not have". That scope call is now
**deliberately reversed** by the user.

**Shape:** one new static catalog key. No new grain, endpoint, state field, secret, container,
Bicep or frontend change — the key rides the machinery ADR 0021/0026/0048 already built.

## Affected areas

Verified against the tree at `8840fe8`.

| File | Change |
|---|---|
| `src/Agent42.Agents/Tools/ToolCatalog.cs` | new `Memory` const; append to `AllSelectableToolKeys` + `Descriptors`; **rewrite two class-docstring invariants** |
| `src/Agent42.Agents/Memory/AgentGrain.Memory.cs` | `BuildMemoryProvider` takes the effective selection and gates on it |
| `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` | pass `enabled` at the call site; **rewrite the context-providers comment block** |
| `src/Agent42.Backend/Configuration/ConfigHealth.cs` | the "enabled" message (`:127`) **and** the "only off switch" comment (`:124`) are no longer true |
| `src/Agent42.Backend/Memory/HindsightRegistration.cs` | its docstring (`:27-28`) says "no Preferences toggle and no `ToolCatalog` key" — falsified, rewrite |
| `docs/features/tool-selection.md` | the key list (`:9-10`), the "selection can only narrow … intersected with capability" claim (`:16`), and the reproduced `ToolCatalog` block (`:38-49`) all go stale |
| `tests/Agent42.Tests/ToolCatalogTests.cs` | new catalog + legacy-selection tests; **existing order-sensitive tests need updating** |
| `tests/Agent42.Tests/AgentGrainTests.cs` | new gate tests; **two existing order-sensitive tests need updating** |
| `tests/Agent42.Tests/ToolSelectionApiTests.cs` | `memory` in the `/api/tools` catalog assertion |
| `.agentic/decisions/0057-*.md` | new ADR (next free number; 0056 is the highest) |
| `.agentic/gotchas.md` | amend the `null == all` entry and the `AgentSkillsProvider` is a TOOL entry |
| `CLAUDE.md` | amend the ADR 0056 and ADR 0021 bullets |

**Deliberately NOT touched — confirm these stay untouched rather than assuming:**
`UserProfile` / `AgentDefinition` / `AgentBinding` / `AgentGrainState` (the key rides the existing
`EnabledTools` lists — **no new `[Id(n)]`, no migration**); every file under
`src/Agent42.Backend/wwwroot/` **including `sw.js`** (see step 6); `infra/`; any silo configurator;
`AddHindsightMemory`'s *logic* (only its docstring changes).

## Context & constraints

Judgment and findings the executor cannot cheaply re-derive. Governing documents:
`.agentic/decisions/0056-hindsight-long-term-memory.md`, `0021-selectable-tools-three-layer.md`,
`0026-per-mcp-server-selectable-tools.md`, `0048-per-skill-selectable-tools.md`,
`0053-google-calendar-tools.md`; `.agentic/gotchas.md` entries "Selectable tools: `null == all` …",
"`AgentSkillsProvider` is a TOOL …", "The memory provider is built at ACTIVATION …".

### Decisions taken with the user — do NOT re-ask these

1. **`memory` goes IN `AllSelectableToolKeys`** — ON by default for anyone who has never narrowed
   their picker (`null == all`). Chosen for uniformity with every other key, and because the
   operator's `Hindsight:ApiKey` already gates everything underneath. **Opt-in-only was considered
   and rejected.**
2. **The checkbox renders even when no `Hindsight:ApiKey` is configured** (today's production
   state). Precedent: `code_interpreter` renders on models that cannot run it — the catalog
   expresses *intent*, an outer gate decides. Hiding it (the `GET /api/push/config` →
   `enabled:false` shape) was considered and rejected: it would make the per-user catalog endpoint
   depend on operator config for the first time, and a key that appears and disappears interacts
   badly with stored explicit selections. Preferences → Configuration already reports memory's real
   state, so the truth stays discoverable.
3. **Off means both recall and retain** — exactly the incognito semantics (ADR 0056 scope call 4).
   No provider is attached at all. "Recall only, keep retaining" was rejected: it would leave the
   always-on egress and the durable-injection risk fully intact while the UI said memory was off.
4. **Route:** `/plan` then `/execute-plan` in fresh sessions, per the CLAUDE.md working agreement.

### Findings from the code — each with its evidence path

- **(A) Memory is not a tool, so its key must SKIP the `selection ∩ capability` intersection that
  `ToolCatalog` documents as universal.** In `AgentGrain.BuildAgent.cs` the static tool list is
  filtered by `IsToolEnabled` and then intersected via `Tools = capabilities.SupportsTools ? tools : []`.
  The memory provider is an `AIContextProvider` added *outside* that path (the `contextProviders`
  list). `NoToolModel_StillGetsMemory` (`AgentGrainTests.cs:2030`) is mutation-verified by ADR 0056
  divergence #6: adding `capabilities.SupportsTools &&` to the memory arm fails that test **and only
  that test**. That property must survive this change — a text-only model with `memory` selected
  still gets memory.
- **(B) It will be the FIRST non-tool key in the catalog, and two `ToolCatalog` class-docstring
  claims become false.** Verbatim today (`ToolCatalog.cs:18-24`): *"the RAG provider is not a tool
  and is gated only by document presence (ADR 0008), so it never appears in this catalog"* — the
  sentence stays true of RAG but its implication (non-tools never appear here) does not; and
  *"Capability is the outer gate … The intersection — selection ∩ capability — keeps the ADR
  0013/0015 gates authoritative"* — memory is the documented exception. **Rewrite both deliberately;
  do not leave them silently falsified.** The same falsification exists in prose at
  `AgentGrain.BuildAgent.cs:225-238`, which currently states of RAG and memory that *"neither has an
  ADR 0021 catalog key"*.
- **(C) `memory` maps to NO tool names, and that is safe.** `ToolNameToKey` is a *name → key* map and
  the only guard over it, `EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate_OrIsAnMcpTool`
  (`AgentGrainTests.cs:2326`), runs in that direction only. There is **no** reverse guard requiring a
  `Descriptors` key to own tool names (checked across `ToolCatalogTests.cs`). So add **no**
  `ToolNameToKey` entry — unlike the `Skills` key, which keeps its three entries precisely because it
  *does* have static tool names.
- **(D) Put the gate inside `BuildMemoryProvider`, not in `BuildAgent`'s `if`.** `cognitive_complexity_max`
  is 15, enforced at **build time** by S3776 at `error` severity (ADR 0050/0051) — and ADR 0056
  divergence #3 records this exact feature already failing that gate once by adding one branch to a
  composition root. **Hand-counted, `BuildAgent` (`AgentGrain.BuildAgent.cs:16-285`) scores ≈14 of 15**
  — hosted if/else 2, `if(SupportsTools){foreach{if}}` 6, three ternaries 3, three `if`s in the
  `contextProviders` block 3. That is at the edge, so a fourth branch there is a real risk, and
  `BuildMemoryProvider` (1→2) is safe. Treat 14 as an estimate to re-measure by building, not a fact.
  Change the signature to
  `BuildMemoryProvider(ISet<string> enabled)` and add the check beside the existing
  `_memory.Client is null` guard in the small `Memory/` partial; the `BuildAgent` call site then
  changes only by passing an argument, adding **zero** branches there. Keep the `Client is null` check
  **first** — it is what makes `UnconfiguredMemory_MakesNoCalls` (zero calls, not failing ones) true.
  The `enabled` set already exists in `BuildAgent` scope (built at `:169`, well before the
  `contextProviders` block at `:239`).
- **(E) EXACTLY ONE existing test breaks, and the others must NOT be "fixed".** An earlier draft of
  this plan got this wrong in a way that would have destroyed a guard, so it is stated precisely.
  `Tools_Catalog_ReturnsSelectableKeys` (`ToolSelectionApiTests.cs:55-61`) **hardcodes** the key array
  and **will fail** — update it. Nothing else should.
  **`AllKeysExpanded()` (`AgentGrainTests.cs:1435-1440`) is DERIVED** — it is built from
  `ToolCatalog.AllSelectableToolKeys.Where(k => k != Mcp && k != Skills)` — so its two consumers,
  `BindAgent_SnapshotsEnabledTools` (`:2651`) and `LegacyState_NullEnabledTools_MeansAll` (`:2668`),
  move expectation and actual together and **pass unchanged**. The two
  `Sanitize_OrdersStaticFirst_*` tests (`ToolCatalogTests.cs:93`, `:137`) pass explicit input lists
  that do not contain `memory`, and `Sanitize` emits `AllSelectableToolKeys.Where(tools.Contains)`, so
  they **pass unchanged** too.
  **If any of those four DOES fail, that is a real bug — do not hardcode a list into
  `AllKeysExpanded()` to silence it.** The gotcha (`.agentic/gotchas.md:1273-1275`) calls that
  ordering guard load-bearing; "assert an order-sensitive sequence" means the sequence matters, not
  that it is written out by hand.
  Still **append `Memory` at the END of both `AllSelectableToolKeys` and `Descriptors`** — it keeps
  the one real expectation change to an append, and keeps catalog order stable for every existing key.
- **(F) The Calendar legacy test canNOT be copied verbatim.**
  `LegacyStoredSelection_DoesNotEnableCalendar_ButNullDoes` (`ToolCatalogTests.cs:64`) asserts through
  `IsToolEnabled("list_calendar_events", …)` — a **tool name**. Memory has none, so the memory
  analogue must assert on the **key** directly (`legacy.Contains(ToolCatalog.Memory)` is false; a set
  built from `AllSelectableToolKeys` contains it). A naive copy will not compile against anything
  meaningful.
- **(G) The selection gate may be read at BUILD time; the incognito gate may not.** This asymmetry is
  the subtle part and is worth a code comment. `_ephemeral` is **turn-scoped** and must stay a
  delegate (`IsEnabled = () => !_ephemeral`) — the whole point of the "built at ACTIVATION but the
  incognito gate is per-TURN" gotcha. The *selection* is different: every path that can change it
  (`SetEnabledToolsAsync`, `BindAgentAsync`, and activation itself) **rebuilds the agent** —
  `SetEnabledToolsAsync` at `AgentGrain.BuildAgent.cs:296` does the snapshot→rebuild→restore→write
  dance explicitly. So a build-time read is correct here and no second delegate is needed. Do **not**
  "fix" `IsEnabled` into carrying the selection too; the two gates have different lifetimes on
  purpose. **Caveat to verify, not assume:** the user-default layer (`_userDefaultEnabledTools`) is
  read **once on activation** — the documented accepted staleness — so changing the Preferences
  default reflects in a warm regular chat only on its next activation, exactly as it already does for
  every other key. That is inherited behaviour, not a new defect; do not try to fix it here.
- **(H) Zero frontend change, therefore NO `sw.js` `CACHE` bump.** Labels are server-driven
  (`renderToolChecks` uses `tool.label`, `tools.js:63`), `allToolKeys()` is catalog-derived
  (`tools.js:21`), and `memory` carries no `mcp:`/`skill:` prefix so it renders as a builtin
  automatically. The "every MR touching `wwwroot/js/` must bump `CACHE`" rule is conditioned on
  touching those files — this MR does not. Current value is `agent42-shell-v23`; **leave it.**
- **(I) The key lands OFF for anyone who has ever pressed Save — AND on every already-bound agent
  chat, where there is no remedy.** The ADR 0048/0053 divergence: the picker materialises "all" as the
  full **explicit** list, so a stored selection cannot contain a key that did not exist when it was
  saved. **Deliberately no migration** — the ADR 0053 `Calendar` docstring argues this at length and
  ADR 0049's auto-enable is not a precedent here (an upload is an event to hang a rewrite on; a
  shipped key is not).
  **The agent layer is worse than the Calendar precedent and the ADR must say so.**
  `BindAgentAsync` (`AgentGrain.cs:292-293`) **always** materialises a concrete list
  (`binding.EnabledTools ?? ToolCatalog.AllSelectableToolKeys`), and a re-bind **returns early**
  (`:279-282`) because a bound persona is immutable (ADR 0003). So **every conversation already bound
  to an agent** holds a pre-`memory` explicit snapshot, and `agents.js:227` sets `toolsBtn.hidden = true`
  on agent chats — the per-conversation popover cannot re-enable it. "One press of Save" is **false**
  for that layer. New chats are fine (a never-narrowed agent re-materialises the current key list at
  bind time). The stuck case is a **continuous** agent (ADR 0016): one permanent grain, so memory is
  off there until `DELETE /api/conversations/agent-{agentId}`.
  **User decision (2026-08-29): accept and document.** It fails safe — the widest egress in the app
  stays off on pre-existing agent chats until deliberately re-enabled. Un-hiding the popover on agent
  chats and back-filling the key into existing snapshots were both considered and rejected: the first
  contradicts ADR 0003/0021's immutable bind snapshot and would need its own ADR; the second silently
  re-enables that egress on conversations nobody opted in for.
- **(J) Baseline is green.** `dotnet build Agent42.slnx` on `8840fe8` exits 0 (run 2026-08-28), so any
  S3776 violation the executor sees is theirs.

## Steps

Sequential — step 2 needs the const from step 1, and the tests need both. Nothing here parallelises
usefully.

### Step 1 — Add the `memory` key to the catalog, and repair the two falsified invariants
Files: `src/Agent42.Agents/Tools/ToolCatalog.cs`

- `public const string Memory = "memory";` with a docstring covering: it is the **first non-tool
  key**; it maps to no `ToolNameToKey` entries and why that is safe (finding C); it **skips the
  capability intersection** (finding A); and the "lands OFF for anyone who ever pressed Save, no
  migration, and that fails safe here" note (finding I) — the `Calendar` docstring is the shape to
  copy.
- Append `Memory` to the **end** of `AllSelectableToolKeys` and a
  `new(Memory, "Long-term memory", RequiresHostedTools: false)` to the **end** of `Descriptors`
  (finding E). The label is **"Long-term memory"**, not "Memory" — user decision 2026-08-29: plain
  "Memory" reads as the conversation's own history (ADR 0004 session), which this is not, and that
  misreading would make the checkbox look like it wipes the current chat.
- Add **no** `ToolNameToKey` entry.
- Rewrite the two class-docstring claims per finding B: the always-on paragraph must now distinguish
  "not a tool" from "not selectable" (RAG is both; memory is the former only), and the capability
  paragraph must name memory as the documented exception to `selection ∩ capability`.

Verify: `dotnet build Agent42.slnx` then
`dotnet test Agent42.slnx --filter "FullyQualifiedName~ToolCatalogTests"`. **Expect these to stay
green** — per finding E the `Sanitize_Orders*` tests do not exercise the new key. If one goes red,
investigate it as a bug; do not edit its expectation.

### Step 2 — Gate the provider on the selection
Files: `src/Agent42.Agents/Memory/AgentGrain.Memory.cs`, `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs`

- `BuildMemoryProvider(ISet<string> enabled)`: return `null` when `_memory.Client is null`
  (**keep first**) or when `!enabled.Contains(ToolCatalog.Memory)`.
- Call site becomes `BuildMemoryProvider(enabled)` — no new branch in `BuildAgent` (finding D).
- Comment the lifetime asymmetry of finding G at the new check: selection is safe to read at build
  time *because every mutation path rebuilds*; `_ephemeral` is not and stays a delegate.
- Rewrite the `contextProviders` comment block (`:225-238`) — it currently asserts memory has no
  catalog key. RAG remains ungated by selection; memory no longer is. That difference is now the
  interesting one and should be what the comment explains.
- **Three more in-code claims are falsified and are outside that range — fix them in this step:**
  `AgentGrain.BuildAgent.cs:270-272` calls memory "ADR 0056, **ungated**" in the
  `AIContextProviders` initializer comment; `AgentGrain.Memory.cs:12` calls it the "**always-on**
  memory provider"; and `ConfigHealth.cs:124` says "it is the only off switch there is" (a comment,
  distinct from the `:127` message covered by step 4).

Verify: `dotnet build Agent42.slnx` (this is the S3776 gate — findings D/J) then
`dotnet test Agent42.slnx --filter "FullyQualifiedName~AgentGrainTests"`. **Expect
`LegacyState_NullEnabledTools_MeansAll` and `BindAgent_SnapshotsEnabledTools` to stay green** —
`AllKeysExpanded()` is derived (finding E). A failure there is a real bug, not an expectation to
update.

### Step 3 — Tests for the new gate
Files: `tests/Agent42.Tests/AgentGrainTests.cs`, `tests/Agent42.Tests/ToolCatalogTests.cs`,
`tests/Agent42.Tests/ToolSelectionApiTests.cs`

New tests (names indicative; `RecordingHindsightClient` plus the existing `RecallsFor`/`RetainsFor`
helpers are the harness, and `TestSiloConfigurator.NoToolsNoVisionModel` is the text-only model):

**Read this first — the trivial-pass trap this feature has already fallen into once.** ADR 0056
divergence #5 and the gotcha at `.agentic/gotchas.md:2170-2176` record that
`EphemeralTurn_NeitherRecallsNorRetains` passes trivially against a provider that never records
anything, so a gate stuck permanently OFF is invisible to it. **Every "assert nothing was recorded"
test below has that shape.** So each test states which direction it catches, and at least one test
must fail when the gate is stuck **ON** and at least one when it is stuck **OFF**. Mutation-test both
directions before claiming the ACs are met, exactly as 0056 did.

- `MemoryDeselected_NeitherRecallsNorRetains` — an explicit selection omitting `memory` produces
  **zero** of both. The headline AC. **Catches stuck-ON only**; passes trivially when stuck OFF.
- `MemorySelected_RecallsAndRetains` — an explicit selection containing `memory` behaves as today.
  **Catches stuck-OFF.**
- `MemorySelection_IsNotIntersectedWithCapability` — `memory` explicitly selected on
  `NoToolsNoVisionModel` still recalls. Guards finding A from the *explicit*-selection side, which
  `NoToolModel_StillGetsMemory` (null selection) does not reach. **Catches stuck-OFF** and the
  capability-folding mutation.
- `TogglingMemoryMidChat_DetachesOnTheSameActivation` — **the load-bearing one, and it must be the
  two-turn shape**: turn 1 with memory on → assert a recall was recorded → `SetEnabledToolsAsync`
  without `memory` → turn 2 → assert **no new** recall, same activation. Written as "deselect, then
  one turn, assert nothing" it is `MemoryDeselected_…` with a different setup and proves nothing about
  the rebuild (finding G). **Catches both directions** — this is the `MemoryResumes_…` of this
  feature.
- `LegacyAgentSnapshot_DoesNotEnableMemory` — a conversation whose `EnabledTools` holds a pre-`memory`
  explicit list (the finding I agent-bind case) records **zero** recalls on a turn. Grain-level, so it
  executes the feature.
- `Tools_Catalog_IncludesMemory` — the descriptor reaches `GET /api/tools` with label
  "Long-term memory".
- `Memory_IsSelectable_AndAppearsInDescriptors` (`ToolCatalogTests.cs`) — the AC1 catalog assertion.

**Do NOT write a `ToolCatalogTests` analogue of
`LegacyStoredSelection_DoesNotEnableCalendar_ButNullDoes`.** The Calendar original is meaningful
because it runs through `IsToolEnabled`, real code. Memory owns no tool name (finding F), so the
same shape degenerates to `legacy.Contains(ToolCatalog.Memory)` on a hardcoded literal set and
`new HashSet<string>(AllSelectableToolKeys).Contains(Memory)` — one is a tautology, the other
restates AC1. The no-migration property is proved at grain level by `LegacyAgentSnapshot_…` above,
and the null-selection half already has a home in `AgentChat_InjectsRecalledMemories`
(`AgentGrainTests.cs:1922`).

Must still pass untouched: `NoToolModel_StillGetsMemory`,
`MemoryResumes_OnTheNormalTurnAfterAnEphemeralOne` (the load-bearing incognito guard),
`EphemeralTurn_NeitherRecallsNorRetains`, `UnconfiguredMemory_MakesNoCalls`, `BankId_IsTheUserId`,
`MemoryFailure_DoesNotBreakTheTurn`, `AgentChat_InjectsRecalledMemories`.

Verify: `dotnet test Agent42.slnx`.

### Step 4 — Config-health message accuracy
Files: `src/Agent42.Backend/Configuration/ConfigHealth.cs`, `tests/Agent42.Tests/ConfigHealthTests.cs`

The present-message reads *"long-term memory enabled — every non-incognito turn is recalled and
retained"* (`:127`). That is now false for a user who has deselected `memory`. Reword to say the key
is configured **and that memory is additionally per-user selectable in Preferences → Tools**. Hard
rule 2: the doc follows the code in the same MR.

**There is nothing to "update" — AC12 needs a NEW assertion.** `ConfigHealth_ReportsHindsight`
(`ConfigHealthTests.cs:229-247`) asserts only `Status == Ok` on the present branch and
`Assert.Contains("long-term memory is off", …)` on the absent one; **nothing pins the present-branch
string**, so an executor reading "update the asserting test" will correctly find no work and AC12
would ship unproven. Add an assertion on the new present-branch message.
Also fix the prose at `ConfigHealthTests.cs:225-227`, which states *"not setting the key is its ONLY
off switch (ADR 0056) — there is no Preferences toggle"*.

Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~ConfigHealth"`.

### Step 5 — ADR 0057, gotchas, CLAUDE.md
Files: `.agentic/decisions/0057-memory-as-a-selectable-tool.md`, `.agentic/gotchas.md`, `CLAUDE.md`

- **ADR 0057** (`amends: [0021, 0056]`). It must state plainly what it reverses. **Cite these two
  places, by line — ADR 0056 has NO "Out of scope" section and quoting one will send a cold-handoff
  session hunting or fabricating:** `0056:24` scope call 1, *"Port it and wire it in — not a parked
  library, and not a Preferences UI"*; and `0056:162-164` Alternatives, *"**A per-user opt-out, or a
  `ToolCatalog` key.** Scoped out by the user…"*. That Alternatives entry is promoted to the
  decision. (The "no Preferences tab, no per-user enable/disable, no `ToolCatalog` key" wording lives
  in the **plan** that produced 0056 —
  `.agentic/specs/completed/plan-hindsight-long-term-memory.md:571-575` — not in the ADR.) Record: the rejected
  `SuggestFollowUps`-shaped alternative (a null-sentinel `bool?` on `UserProfile`, ADR 0036 — simpler
  and honest about memory not being a tool, but only **one user-wide switch**; the picker was chosen
  because it also buys per-conversation and per-agent control, and "this agent doesn't remember" is
  worth having); the rejected opt-in-only default; the rejected hide-when-unconfigured; the rejected
  recall-only semantics; and finding I's no-migration argument including why it fails **safe** here.
  **Finding I's agent-chat consequence gets its own Consequences paragraph** — every already-bound
  agent conversation has memory off with no in-UI remedy, permanently for a continuous agent, and
  both alternatives (un-hide the popover; back-fill the snapshots) were rejected with reasons.
  Note the residual honestly: **the operator key remains the only gate that stops egress for the
  whole deployment**, and a per-user selection does not undo memories already retained — ADR 0056's
  no-delete-path residual is untouched by this ADR.
- **`src/Agent42.Backend/Memory/HindsightRegistration.cs`** — its docstring (`:27-28`) asserts
  *"Not setting the key is also the only off switch. There is no Preferences toggle and no
  `ToolCatalog` key"*. Rewrite it; hard rule 2 makes this mandatory in this MR, not optional.
- **`docs/features/tool-selection.md`** — add `memory` to the key list (`:9-10`); amend
  *"Selection can only narrow, never grant: it is intersected with model capability"* (`:16`) to name
  memory as the exception; and refresh the reproduced `ToolCatalog` const block and
  `AllSelectableToolKeys` (`:38-49`). **`check-doc-paths.mjs` validates paths only — nothing catches
  this drift automatically.**
- **`.agentic/gotchas.md`** — amend the `null == all` entry (memory is a static key, not a wildcard,
  but it is the first key in `Descriptors` with **no** `ToolNameToKey` entry and it is **exempt from
  the capability intersection**) and the "`AgentSkillsProvider` is a TOOL" entry (its RAG-vs-skills
  asymmetry is now a **three**-way split: skills = tool, gated by capability *and* selection; RAG =
  not a tool, gated by neither; memory = not a tool, gated by selection but **not** capability).
  Record finding G's lifetime asymmetry in the existing memory-provider gotcha.
- **`CLAUDE.md`** — the ADR 0056 bullet currently says *"**not setting `Hindsight:ApiKey` is the ONLY
  off switch** — no Preferences toggle, no `ToolCatalog` key"*. That becomes false; fix it. Add
  `memory` to the ADR 0021 bullet's catalog-key list, and note the press-Save-once consequence
  alongside the existing `calendar` one.

Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~QualityGateTests|FullyQualifiedName~ProjectLayoutTests"`
then `node .agentic/scripts/check-doc-paths.mjs` (the doc-path-integrity job, `pr-checks.yml:88`).

### Step 6 — Confirm the non-changes, then the gates
- Assert by inspection that `git diff --stat` touches **no** file under `src/Agent42.Backend/wwwroot/`.
  If it does, finding H is wrong and `sw.js`'s `CACHE` **must** be bumped from `agent42-shell-v23`.
- Assert no new `[Id(n)]` on any of the four `EnabledTools` carriers.

Verify: `dotnet build Agent42.slnx` · `dotnet test Agent42.slnx` ·
`dotnet format Agent42.slnx --verify-no-changes` ·
`node .agentic/scripts/check-new-code-coverage.mjs`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | `memory` is a selectable key in both `AllSelectableToolKeys` and `Descriptors` | `Memory_IsSelectable_AndAppearsInDescriptors` | `ToolCatalogTests.cs` |
| AC2 | A null selection (never narrowed) enables memory | `AgentChat_InjectsRecalledMemories` (existing, null selection — must still pass) | `AgentGrainTests.cs` |
| AC3 | Deselecting memory produces **zero** recall and **zero** retain calls | `MemoryDeselected_NeitherRecallsNorRetains` (stuck-ON only) | `AgentGrainTests.cs` |
| AC4 | Selecting memory recalls and retains as before | `MemorySelected_RecallsAndRetains` (stuck-OFF) | `AgentGrainTests.cs` |
| AC5 | Memory skips the capability intersection (explicit selection) | `MemorySelection_IsNotIntersectedWithCapability` | `AgentGrainTests.cs` |
| AC5b | Memory skips the capability intersection (null selection) — unchanged | `NoToolModel_StillGetsMemory` (existing, must still pass) | `AgentGrainTests.cs` |
| AC6 | A pre-existing explicit selection does not gain memory; no migration | `LegacyAgentSnapshot_DoesNotEnableMemory` (grain-level) | `AgentGrainTests.cs` |
| AC7 | Deselecting mid-chat detaches the provider on the **same activation** | `TogglingMemoryMidChat_DetachesOnTheSameActivation` (**load-bearing — fails in both directions**) | `AgentGrainTests.cs` |
| AC8 | The per-agent snapshot carries the memory selection | `BindAgent_SnapshotsEnabledTools` (existing, **passes unchanged** — `AllKeysExpanded()` is derived) | `AgentGrainTests.cs` |
| AC9 | With no API key, selecting memory still makes zero calls | `UnconfiguredMemory_MakesNoCalls` (existing, must still pass) | `AgentGrainTests.cs` |
| AC10 | The incognito gate still works **and still resumes** | `EphemeralTurn_NeitherRecallsNorRetains` + `MemoryResumes_OnTheNormalTurnAfterAnEphemeralOne` (existing, must still pass) | `AgentGrainTests.cs` |
| AC11 | `GET /api/tools` serves the memory descriptor, labelled "Long-term memory" | `Tools_Catalog_IncludesMemory` | `ToolSelectionApiTests.cs` |
| AC12 | Config health no longer claims every turn is remembered | **new** assertion in `ConfigHealth_ReportsHindsight` | `ConfigHealthTests.cs` |
| AC13 | The picker renders memory with no frontend change | step 6's diff check (no test — asserting a *non*-change) | — |

**AC13 has no automated proof and that is recorded, not hidden** — it is an assertion about the
absence of a diff, in the ADR 0043 `enterkeyhint` tradition. A DevHost browser pass (headless Chrome
over CDP, the ADR 0052/0054 method) would confirm the checkbox renders and round-trips; run it if a
driver is available and record the observed values in the ADR, or record it as an unverified
residual if not.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (2026-08-28) — do not write these from memory.

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet cobertura ∩ `git diff -U0` vs merge base) |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` S3776 at **build time**, `error` severity — `dotnet build` fails outright (ADR 0050/0051) |
| `vulnerable_packages` | **0** | CI package-freshness job |
| lint | clean | `dotnet format Agent42.slnx --verify-no-changes` |

Coverage note: this branch adds few production lines (a const, a descriptor entry, one predicate) and
many test lines, so the 80% new-code floor should be comfortable — the usual squeeze is DI/config
wiring, and there is none here.

## Out of scope

- **A delete path for memories, or any UI to inspect a bank.** ADR 0056's unmitigable residual is
  untouched: deselecting memory stops *future* recall and retain, and does **not** remove what was
  already retained. Say so in the ADR; do not imply otherwise in the picker label.
- **Per-user Hindsight API keys** (the ADR 0018/0024 DP-encrypted overlay shape).
- **Per-conversation or per-agent memory *banks*.** The bank stays one per user (ADR 0056 scope
  call 3); this plan gates the provider, not the bank.
- **Migrating existing explicit selections** to include the new key — finding I, deliberate.
- **Hiding the key when unconfigured** — decided against; see Context decision 2.
- **Any change to `IsEnabled`/`_ephemeral`,** the incognito semantics, or the reminder/agent-run
  inclusion. Memory stays live on turns with no user present when selected.
- **Any frontend change**, and therefore no `sw.js` `CACHE` bump — finding H, verified in step 6.
- **Retiring `Hindsight:ApiKey` as the operator gate.** It remains the outer switch and the only one
  that stops egress deployment-wide.
- **Any remedy for the agent-chat lockout** (finding I) — un-hiding the composer popover on agent
  chats, or a re-sync path from an edited agent definition into an already-bound conversation. Both
  contradict ADR 0003/0021's immutable bind snapshot and would need their own ADR.

## Review

A `critic` subagent reviewed the first draft against the ADRs, gotchas, quality gates and the code.
Verdict: **do not approve as-is** — 4 blocking, 5 major, 4 minor. **All 13 were accepted**; four were
independently re-verified against the tree before folding in, because they contradicted the draft.

**Blocking — all fixed:**

| # | Finding | Fix |
|---|---|---|
| B1 | `HindsightRegistration.cs:27-28` says "no Preferences toggle and no `ToolCatalog` key" — the draft listed that file as deliberately untouched, which hard rule 2 forbids | Moved into Affected areas + step 5 |
| B2 | `docs/features/tool-selection.md` was missing entirely — it carries the key list, the "only narrows / intersected with capability" claim, and a verbatim copy of the `ToolCatalog` block | Added to Affected areas + step 5, with the note that `check-doc-paths.mjs` validates paths only |
| B3 | **Finding E was wrong.** `AllKeysExpanded()` (`AgentGrainTests.cs:1435-1440`) is *derived* from `AllSelectableToolKeys`, so its two consumers pass unchanged; the two `Sanitize_Orders*` tests take explicit inputs and also pass. Only `Tools_Catalog_ReturnsSelectableKeys` breaks. The draft told the executor to "fix the expectations" of four passing tests, which invited hardcoding a list into a guard the gotcha calls load-bearing | Finding E rewritten to name the one real failure and to say explicitly that a failure in the other four is a bug, not an expectation to update. Both steps' verify text corrected. Re-verified by reading `AgentGrainTests.cs:1432-1440` directly |
| B4 | ADR 0056 has **no "Out of scope" section**; the quoted string lives in the *plan* that produced it | Step 5 now cites `0056:24` (scope call 1) and `0056:162-164` (Alternatives) by line, and says where the other wording actually lives |

**Major — all fixed:**

| # | Finding | Fix |
|---|---|---|
| M1 | AC2/AC6 mapped to tautologies — memory owns no tool name, so the Calendar test's shape degenerates to asserting on a hardcoded literal | Both moved to grain level (`LegacyAgentSnapshot_DoesNotEnableMemory`, and `AgentChat_InjectsRecalledMemories` for the null half); step 3 now says explicitly not to write the `ToolCatalogTests` analogue and why |
| M2 | AC7's test as described was AC3 with a different setup, and shared its trivial-pass shape | Rewritten to the two-turn same-activation shape and named the load-bearing test; step 3 gained a preamble requiring both mutation directions, per ADR 0056 divergence #5 |
| M3 | AC12 mapped to an assertion that does not exist — nothing pins the present-branch message | Step 4 now says AC12 needs a **new** assertion, and adds the falsified prose at `ConfigHealthTests.cs:225-227` |
| M4 | Finding I understated the blast radius: `BindAgentAsync` always materialises, re-binds return early, and the popover is hidden on agent chats — so **every already-bound agent conversation** has memory off with no remedy, permanently for a continuous agent | Finding I rewritten with the mechanism and line numbers; **put to the user, who chose accept-and-document**; ADR 0057 gets a Consequences paragraph; both alternatives recorded as rejected in Out of scope. Re-verified against `AgentGrain.cs:279-293` and `agents.js:227` |
| M5 | Three more falsified in-code claims outside the ranges the draft named | Added to step 2 (`BuildAgent.cs:270-272`, `AgentGrain.Memory.cs:12`, `ConfigHealth.cs:124`) |

**Minor — all fixed:** m1 the ADR 0056 paraphrase was inverted ("strongest argument *for* the opt-out",
not against the risk) — corrected in Overview; m2 finding D's "largest method in the area" replaced
with the hand-counted ≈14/15 and flagged as an estimate to re-measure; m3 step 5 now names
`node .agentic/scripts/check-doc-paths.mjs`; m4 AC8's "expectation updated" corrected to "passes
unchanged".

**Confirmed clean by the critic, no action:** findings A, C, G and H. **G is the plan's most
load-bearing original claim** — that the selection may be read at build time because every mutation
path rebuilds — and it was verified exhaustively: `SetEnabledToolsAsync`, `BindAgentAsync` and
`OnActivateAsync` are the only paths, plus `AutoEnableUploadedSkillsAsync`
(`AgentApi.UserSkills.cs:193`) writing the user-default layer under the same documented activation
staleness. H was confirmed across every file under `wwwroot/` — no `CACHE` bump is due. The plan also
avoids the "following ADR 0008" mistake ADR 0056 explicitly warns about.

**No findings were rejected.** A second critic pass has **not** been run.

## Divergences from the plan (recorded at execution, 2026-08-29)

Executed on `feature/memory-as-selectable-tool` from `main @ 8840fe8` — the exact
`planned-against:` ref, so there was **zero drift** and no re-plan.

The plan held up unusually well: **all six steps executed as written, and every one of findings A–J
was confirmed rather than corrected.** In particular finding E — the one the critic had already
rewritten once — was exactly right: `Tools_Catalog_ReturnsSelectableKeys` was the **only** existing
test that failed, and the four tests the draft would have wrongly "fixed" (`AllKeysExpanded()`'s two
consumers and the two `Sanitize_OrdersStaticFirst_*`) passed untouched. `AllKeysExpanded()` was not
edited.

Five things worth recording, none of which changed the design:

1. **Finding D's ≈14/15 estimate never had to be tested.** The plan told the executor to re-measure by
   building rather than trust the hand count. Putting the gate inside `BuildMemoryProvider` meant
   `BuildAgent` gained **zero** branches, so `dotnet build` stayed at 0 errors throughout and S3776
   never fired. The estimate is therefore still unverified — and deliberately so, since the whole point
   of finding D was not to spend it.

2. **The mutation testing step 3 demanded was run, and it confirmed the predicted table exactly.**
   Mutation 1 (selection term deleted → gate stuck ON): 3 failures — `MemoryDeselected_…`,
   `TogglingMemoryMidChat_…`, `LegacyAgentSnapshot_…`. Mutation 2 (`|| true` → gate stuck OFF): 8
   failures — `MemorySelected_…`, `MemorySelection_IsNotIntersectedWithCapability`,
   `TogglingMemoryMidChat_…` plus the five pre-existing ADR 0056 memory tests, with `MemoryDeselected_…`
   and `LegacyAgentSnapshot_…` passing **trivially**, exactly as the plan warned they would.
   `TogglingMemoryMidChat_DetachesOnTheSameActivation` failed in **both** directions, confirming it as
   the load-bearing test. The table is reproduced as a comment above the tests and in ADR 0057.

3. **AC12 was a new assertion, as the plan said — and the message needed rewriting, not tweaking.**
   Nothing pinned the present-branch string, so there genuinely was no test to "update". The new
   message reports the key as *configured* and points at the per-user control, rather than promising
   that every non-incognito turn is remembered.

4. **Finding H confirmed by inspection, not assumption.** `git diff --name-only | grep wwwroot`
   returned nothing, so `sw.js` stayed at `agent42-shell-v23`. Likewise no new `[Id(n)]` on any
   `EnabledTools` carrier.

5. **One unrelated test flakes, and it was PROVEN pre-existing rather than argued away.**
   `OtelConfigurationTests.AddLiveTelemetryCapture_ComposesWithTheDualExporterPipeline` (ADR 0054)
   failed on 2 of 11 full-suite runs on this branch. The tempting move was to point at the diff
   touching zero telemetry files and call it unrelated — but this branch adds seven span-producing
   grain tests, which is exactly the kind of thing that could worsen a span-counting flake, so it was
   measured instead: clean `main` was checked out into a throwaway worktree and run five times, and
   **failed 2 of 5** with the same test and the same shape. Branch 2/11, main 2/5 — pre-existing, and
   not worsened here. Both failing runs on each side were the slow ones (38–55 s against a ~27 s
   median), consistent with the likely mechanism: `Assert.Empty(buffer…)` reads a process-wide OTel
   listener that can catch a span from a concurrently-running test. **Not fixed here** — it belongs to
   ADR 0054's suite and wants its own change. Flagged for a follow-up.

### AC13 — the honest residual, unchanged from the plan
"The picker renders memory with no frontend change" remains **unproven by any automated test**, by
construction: it is an assertion about the *absence* of a diff (the ADR 0043 `enterkeyhint` tradition).
The DevHost browser pass the plan offered as optional confirmation was **not run**. The supporting
evidence is indirect but real — labels are server-driven (`renderToolChecks` uses `tool.label`),
`allToolKeys()` is catalog-derived, `memory` carries no `mcp:`/`skill:` prefix so it renders as a
builtin, and `Tools_Catalog_IncludesMemory` proves the descriptor reaches the wire with the right
label. What is untested is only the last hop: the checkbox actually painting and round-tripping.
