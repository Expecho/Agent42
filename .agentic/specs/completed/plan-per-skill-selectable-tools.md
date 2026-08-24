# Plan — Per-skill selectable tools (`skill:{name}` keys with a `skills` wildcard)

planned-against: main @ 9ba20b7

## Overview

Today the ADR 0021 tool picker shows **one** "Skills" checkbox that turns the whole
`AgentSkillsProvider` on or off — every shipped skill or none. The user wants to pick
**individual** skills: use `websearch` in this chat, not `scheduledprompt`.

This is the exact analogue of **ADR 0026** (per-MCP-server selectable tools), applied to the
file-based skills in `src/Agent42.Agents/Skills/`. The `skills` key is repurposed into an
all-skills **wildcard** (kept in `AllSelectableToolKeys`, removed from `Descriptors`), and
`GET /api/tools` appends one `skill:{name}` descriptor per shipped skill. Zero state migration.

## Affected areas

Verified against the tree at 9ba20b7:

| File | Change |
|---|---|
| `src/Agent42.Agents/Tools/SkillCatalog.cs` | **new** — discovers shipped skills once, exposes `(Name, Description, Path)` |
| `src/Agent42.Agents/Tools/ToolCatalog.cs` | `Skills` becomes a wildcard; `SkillKey`/`IsSkillKey`/`IsSkillEnabled`; drop `Skills` from `Descriptors`; `Sanitize` passthrough |
| `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` | build the provider from the *selected* skill dirs; drop it when none; expand the `skills` wildcard in `GetEnabledToolsAsync` |
| `src/Agent42.Backend/Tools/AgentApi.Tools.cs` | append per-skill descriptors to `GET /api/tools` |
| `src/Agent42.Backend/wwwroot/js/tools.js` | render a "Skills" heading + one box per skill; treat a stored bare `skills` as all-checked |
| `src/Agent42.Backend/wwwroot/sw.js` | `CACHE` bump `agent42-shell-v19` → `-v20` (ADR 0041 rule) |
| `tests/Agent42.Tests/ToolCatalogTests.cs` | wildcard + sanitize + key helpers |
| `tests/Agent42.Tests/AgentGrainTests.cs` | per-skill gating, wildcard expansion, legacy selection — **and two existing assertions at lines 2128 and 2144** that compare against `AllSelectableToolKeys.Where(k => k != ToolCatalog.Mcp)`; they must also drop `ToolCatalog.Skills` and expect the expanded `skill:{name}` keys |
| `tests/Agent42.Tests/ToolSelectionApiTests.cs` | catalog shape (`Tools_Catalog_ReturnsSelectableKeys` **must change** — it asserts the exact static key list including `"skills"`, at line 102) |
| `tests/Agent42.Tests/SkillCatalogTests.cs` | **new** |
| `tests/Agent42.Tests/SkillPickerAssetsTests.cs` | **new** — source-text guard for the JS (no JS runner in this repo) |
| `tests/Agent42.Tests/PwaAssetsTests.cs` | cache-version assertion at line 215 |

**Verified, no change — but read before you assume otherwise** (these are the completeness contract):

| File | Why it is safe |
|---|---|
| `src/Agent42.Agents/Conversations/AgentGrain.cs:260` | the third reader of `AllSelectableToolKeys` — `Sanitize(binding.EnabledTools ?? AllSelectableToolKeys)`. It is the line that decides the bare `skills` wildcard reaches persisted `AgentGrainState`, which is correct and wanted (see finding 6). |
| `tests/Agent42.Tests/GrainStorageSerializerTests.cs` + `GoldenStates.cs` | goldens hold `["web_search","mcp:server-1"]` / `["web_search"]` — no `skills` key, so unaffected. Do not regenerate them. |
| `tests/Agent42.Tests/ProjectLayoutTests.cs` | `SkillCatalog.cs` adds no folder and stays in the flat namespace, so the folder-set and namespace assertions are untouched. |
| `sw.js`'s `SHELL` list | no new JS module, so only `CACHE` moves. |
| `Agent42.Agents.csproj` `Skills\**\*` Content glob | already lands in the output of `Agent42.Backend`, `Agent42.DevHost` **and** `Agent42.Tests`. |

No Bicep, no secret, no new grain, no new Cosmos surface, no endpoint added or removed, and no new
mandatory host registration (so the six-host route-matcher trap does not apply).

## Context & constraints

### Governing prior art — read these first
- `.agentic/decisions/0026-per-mcp-server-selectable-tools.md` — **the template**. Same wildcard
  trick, same "expand before it reaches the picker" hazard, same `Descriptors` removal.
- `.agentic/decisions/0021-selectable-tools-three-layer.md` — the three layers this inherits
  (user default / per-conversation / per-agent) for free: all three render from `GET /api/tools`.
- `.agentic/gotchas.md` line 500 — *"`AgentSkillsProvider` is a TOOL"*. The provider must be
  **dropped**, never merely relabelled; the RAG `TextSearchProvider` must **stay**.
- `.agentic/gotchas.md` line 1176 — *"Selectable tools: `null == all`"*, including the ADR 0026
  wildcard-expansion trap. This plan adds a **second** wildcard with the identical hazard.
- `.agentic/decisions/0041-frontend-es-modules.md` — every MR touching `wwwroot/js/` bumps `CACHE`.

### Investigation findings the executor cannot cheaply re-derive

These were established by a spike (`dotnet run app.cs` pinned to `Microsoft.Agents.AI@1.13.0`,
outside the repo tree) plus reading the 1.13.0 package XML docs. **They are measured, not recalled.**

1. **`AgentFileSkillsSource` treats each supplied path as a search root, and a path pointing at a
   single skill directory yields exactly that one skill.** Measured: `new AgentFileSkillsSource(root)`
   → `[alpha, beta]`; `new AgentFileSkillsSource(root/alpha)` → `[alpha]`; a list of two skill dirs
   → `[alpha, beta]`; a **missing** dir → `[]` (no throw); an **empty** list → `[]` (no throw).
   *This is the filtering mechanism.* The existing `AgentSkillsProvider(IEnumerable<string> paths, …)`
   overload takes the list directly, so filtering is a change of **argument**, not of approach —
   the current call at `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs:91` keeps its shape.
2. **Do NOT use `AgentSkillsProviderBuilder.UseFilter`.** It exists in 1.13.0 with signature
   `Func<AgentSkill, AgentSkillsSourceContext, bool>`, but `Build()` throws
   `InvalidOperationException: "File-based skill sources require a script runner"` for any file
   source — measured. Supplying a runner to satisfy the builder would introduce script-execution
   plumbing this app deliberately does not have. Rejected on that basis.
3. **Enumerating skills for the catalog works with a null context**: `GetSkillsAsync(default!)`
   returns the parsed skills with `Frontmatter.Name` / `Frontmatter.Description`.
   `new AgentSkillsSourceContext(null!, null!)` **throws** `ArgumentNullException(agent)` — so the
   context must be passed as a plain `default!`/`null!`, never constructed. `AgentFileSkillsSource`
   ignores it today; this is undocumented tolerance, so `SkillCatalog` must **not** fail the host
   if it ever changes (see step 1's per-directory try/catch) and step 1's test pins it.
4. **Key = the skill's frontmatter `name`, discovered per directory.** Enumerating one directory at
   a time is what produces a reliable `(directoryPath ↔ frontmatterName)` pair — enumerating the
   root returns skills with no path attached. Both shipped skills happen to have
   `frontmatter.name == directory name` (`websearch`, `scheduledprompt`), but the code must not
   rely on that.
5. **The existing gate is a `HashSet.Contains` at `AgentGrain.BuildAgent.cs:150**
   (`var skillsEnabled = capabilities.SupportsTools && enabled.Contains(ToolCatalog.Skills);`) and the
   provider is constructed **unconditionally** at line 91, before `enabled` exists. The construction
   must move down, after `var enabled = …` (line 123), so it can be built from the selection.
6. **The agent-bind path materialises the wildcard into state.** `AgentGrain.cs:260` stores
   `ToolCatalog.Sanitize(binding.EnabledTools ?? ToolCatalog.AllSelectableToolKeys)`, so an agent bound
   with a null selection persists the **bare `skills` wildcard** into `AgentGrainState.EnabledTools`.
   That is correct and needs no change — the gate honours the raw wildcard — but it means
   `BindAgent_SnapshotsEnabledTools` (`AgentGrainTests.cs:~2107`) and
   `LegacyState_NullEnabledTools_MeansAll` (`~2135`) compare against a `Where(k => k != ToolCatalog.Mcp)`
   projection that will now also need `Skills` excluded and the `skill:{name}` keys added.
7. **The `GrainStorageSerializerTests` golden document is unaffected** — its persisted
   `EnabledTools` is `["web_search","mcp:server-1"]` (line 97/154), which carries no `skills` key.
   Do not touch it.
8. **The key-coverage guard test survives untouched.** `AgentGrainTests.cs:1825` asserts every
   registered tool name is `get_date`, has a `ToolCatalog.KeyFor(...)`, or is an MCP tool.
   `load_skill`/`read_skill_resource`/`run_skill_script` keep mapping to the `skills` constant,
   which stays in `AllSelectableToolKeys`. Do not remove those three map entries.

### Decisions made in the interview (do not relitigate)
- **Built-in skills only.** User-authored / uploaded skills are explicitly deferred — see Out of scope.
- **The "Skills" master checkbox is replaced, not kept as a parent.** ADR 0026's shape exactly:
  `skills` stays in `AllSelectableToolKeys` as an all-skills wildcard (so `null == all` and every
  legacy stored selection carrying literal `"skills"` keep enabling every skill — zero migration)
  but is **removed from `Descriptors`**, so the picker renders one box per skill under a "Skills"
  heading. Unchecking every skill drops the provider entirely.
- **The picker label is the skill's frontmatter `name`, verbatim** (`websearch`, `scheduledprompt`) —
  lowercase identifiers sitting next to "Web search" and "Code interpreter". Deliberate: it is exactly
  the string the model is shown and the string the `skill:{key}` addresses, so a user reading the
  picker and a user reading the ADR see the same token. Rejected: title-casing (a display-only
  transform that would drift from the key) and adding a `title:` frontmatter field (a second name to
  keep in sync, for two built-in skills). AC14 pins the label so the choice is falsifiable.
- **No coupling between a skill and the tools it documents.** A skill is guidance text; leaving the
  `websearch` skill on while `web_search` is off is mildly wasteful and never an error. Rejected:
  auto-hiding a skill whose tools are all off — it would need a skill→tool-keys mapping the
  `SKILL.md` files do not carry, i.e. a second source of truth to keep in sync.

## Steps

### Step 1 — `SkillCatalog`: discover the shipped skills once
**Files:** `src/Agent42.Agents/Tools/SkillCatalog.cs` (new),
`tests/Agent42.Tests/SkillCatalogTests.cs` (new).

A static class in the flat `Agent42.Agents` namespace (ADR 0039 — folders and namespaces
deliberately disagree; do **not** give it a `Agent42.Agents.Tools` namespace).

- `public sealed record SkillInfo(string Name, string Description, string Path)`.
- **`internal static IReadOnlyList<SkillInfo> Discover(string root)`** — the testable seam. Enumerate
  `Directory.GetDirectories(root)`, and for **each** directory construct `new AgentFileSkillsSource(dir)`
  and read `Frontmatter.Name`/`.Description` (finding 4). Order by `Name` (ordinal) so the picker is
  stable. Wrap **each directory** in its own `try/catch` that skips it, and guard the root with
  `Directory.Exists` — a missing `Skills/` folder or one malformed `SKILL.md` must yield a shorter
  list, never a startup failure. **De-duplicate by `Name`, first wins** (ordinal): two directories
  can declare the same frontmatter `name`, which would otherwise render two identical checkboxes
  addressing an ambiguous gate. This mirrors the framework's own `DeduplicatingAgentSkillsSource`.
  Make `Agent42.Agents`'s internals visible to `Agent42.Tests` if they are not already.
- `public static IReadOnlyList<SkillInfo> Skills { get; }` — a `Lazy<>` over
  `Discover(Path.Combine(AppContext.BaseDirectory, "Skills"))`, evaluated once per process.
- **The sync-over-async bridge must be `Task.Run(...).GetAwaiter().GetResult()`, not a bare
  `.GetAwaiter().GetResult()`.** `GetSkillsAsync` is async and `Skills`'s first touch is
  nondeterministic — it can easily be `BuildAgent` on a grain thread rather than the `/api/tools`
  handler. Orleans grains have no `SynchronizationContext` but *do* run on the activation
  `TaskScheduler`, which `await` captures, so a bare blocking wait on a continuation scheduled back
  onto that same activation deadlocks it — a bricked conversation, not a slow one. It happens to be
  safe today only because 1.13.0's `GetSkillsAsync` completes synchronously; that is an
  implementation detail of a pinned package, **recalled rather than measured**, and not something to
  rest an activation on. `Task.Run` moves the continuation to the thread pool and removes the
  question. Do not "simplify" it back.

**Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~SkillCatalogTests`. The `Discover`
seam is what makes AC11 assertable: point it at a temp directory containing a missing subdirectory,
an empty subdirectory, a malformed `SKILL.md` and two directories declaring the same `name`, and
assert the result is the tolerated subset rather than a throw. The shipped-skills assertions (AC10)
go through the public `Skills` property.

### Step 2 — `ToolCatalog`: the `skills` wildcard and `skill:{name}` keys
**Files:** `src/Agent42.Agents/Tools/ToolCatalog.cs`, `tests/Agent42.Tests/ToolCatalogTests.cs`.

Mirror the `Mcp` members that already exist in this file, one for one:

- Rewrite the `Skills` const's doc comment into the wildcard explanation (copy the shape of the
  `Mcp` doc comment at lines 26–38, which spells out *why* it stays in `AllSelectableToolKeys` and
  *why* it leaves `Descriptors`).
- `SkillKeyPrefix = "skill:"`, `SkillKey(string name)`, `IsSkillKey(string key)`,
  `IsSkillEnabled(string name, ISet<string> enabled) => enabled.Contains(Skills) || enabled.Contains(SkillKey(name))`.
- **Remove** `new(Skills, "Skills", RequiresHostedTools: false)` from `Descriptors` (line 83), leaving
  a comment in its place exactly like the `Mcp` one below it.
- Leave `Skills` in `AllSelectableToolKeys` (line 52) — load-bearing for `null == all` and legacy state.
- Leave the three `load_skill`/`read_skill_resource`/`run_skill_script` entries in `ToolNameToKey`
  (finding 8) — but **fix the comment above them at `ToolCatalog.cs:57-59`**, which says "the provider
  itself is gated by the Skills arm in BuildAgent". After step 3 the gate is "at least one selected
  skill path". A comment that survives the code it describes is doc debt inside the source file.
- `Sanitize`: pass `skill:{name}` keys through, deduped, after the static keys and before the
  MCP keys — `[.. AllSelectableToolKeys.Where(tools.Contains), .. tools.Where(IsSkillKey).Distinct(), .. tools.Where(IsMcpServerKey).Distinct()]`.
  Note `Sanitize` stays **static** and does not validate a skill name against `SkillCatalog`
  (same reasoning as the MCP case: a stale key is inert and self-heals on the next save).

**Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~ToolCatalogTests`.

### Step 3 — `AgentGrain.BuildAgent`: build the provider from the selected skills
**Files:** `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs`, `tests/Agent42.Tests/AgentGrainTests.cs`.

- **Move** the `new AgentSkillsProvider(...)` construction from line 91 down to below
  `var enabled = EffectiveEnabledTools().ToHashSet(...)` (line 123).
- Replace `var skillsEnabled = … enabled.Contains(ToolCatalog.Skills);` (line 150) with a resolved
  path list:
  ```
  var selectedSkillPaths = capabilities.SupportsTools
      ? SkillCatalog.Skills.Where(s => ToolCatalog.IsSkillEnabled(s.Name, enabled)).Select(s => s.Path).ToList()
      : [];
  ```
  and construct the provider **only when the list is non-empty** — an empty list would build a
  provider that advertises no skills while still handing the model a `load_skill` schema, which is
  precisely the trap the line-500 gotcha describes. The `AIContextProviders` switch keys off
  `selectedSkillPaths.Count > 0`; the `BuildDocumentSearchProvider()` arms are **unchanged**.
- Keep the existing `AgentSkillsProviderOptions { DisableLoadSkillApproval = true }`.
- **Rewrite the block comment at `BuildAgent.cs:146-149`** ("a user disabling `skills` must DROP the
  provider") so it describes the new gate. Its *point* survives verbatim and must be preserved: the
  provider goes when the selection empties, the RAG `TextSearchProvider` does not.
- Generalise `ExpandMcpWildcard` (lines ~236–253) — either a second `ExpandSkillsWildcard` composed
  with it, or one `ExpandWildcards`. It must replace a bare `skills` with `skill:{name}` for every
  entry in `SkillCatalog.Skills`, exactly as the MCP one does, and for the same reason: the picker
  renders only catalog keys, `skills` has no checkbox, so an unexpanded wildcard reaching the client
  would be dropped on the next Save and **silently disable every skill**. Non-wildcard selections
  pass through unchanged. Unlike the MCP list this needs **no grain field** — `SkillCatalog` is static
  and process-wide, so there is no `OnActivateAsync`-ordering hazard here.

**Two existing tests provably break, and that breakage is the feature working.**
`BindAgent_SnapshotsEnabledTools` (`AgentGrainTests.cs:2127-2129`) and
`LegacyState_NullEnabledTools_MeansAll` (`:2143-2145`) both `Assert.Equal` against
`AllSelectableToolKeys.Where(k => k != ToolCatalog.Mcp)`, which still contains `"skills"`. They must
now exclude `ToolCatalog.Skills` too and expect the expanded keys. **`Assert.Equal` on a sequence is
order-sensitive, so fix the order at the source:** implement the expansion as
`ExpandSkillsWildcard(ExpandMcpWildcard(effective))` — MCP first, skills outermost — giving
`[web_search, location, scheduling, code_interpreter, skill:scheduledprompt, skill:websearch]` for a
null selection in the test host (static keys in catalog order, then `SkillCatalog.Skills` in its
`Name`-ordinal order). If you compose them the other way the tests still pass but the order differs
from what this spec records; do not silently diverge.

**Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~AgentGrainTests`. The
**pre-existing** `SkillsDisabled_DropsLoadSkill_KeepsRetrieval`,
`NoToolModel_RegistersNoTools_AndNoSkillsProvider` and `WebSearchSkill_IsAdvertisedToTheModel` must
stay green **without edits** — they are the regression guard that this refactor changed only the
granularity, not the gating.

**Assertion vocabulary for AC1/AC2/AC4** (copy `WebSearchSkill_IsAdvertisedToTheModel` at
`AgentGrainTests.cs:1431` and `PerServerSelection_EnablesOnlySelectedServer` at `:1938`): the fake
client echoes the instructions as `[sys:…]` and the tool schemas as `[tools:…]`. Assert skill
*presence/absence* on the raw `Answer(...)` (skill names are `websearch` / `scheduledprompt`) and
tool presence on `ToolNames(...)`, which scopes to the `[tools:…]` marker. The negative assertion
`DoesNotContain("scheduledprompt", …)` is only safe because the tool names use underscores
(`schedule_prompt`) — see the gotcha at `.agentic/gotchas.md:956`, which records exactly this
collision for `web_search` vs the `websearch` skill description. Do not weaken it to a substring
that could match a tool name.

### Step 4 — `GET /api/tools`: one descriptor per shipped skill
**Files:** `src/Agent42.Backend/Tools/AgentApi.Tools.cs`, `tests/Agent42.Tests/ToolSelectionApiTests.cs`.
*(Parallelizable with step 5 once step 3 lands.)*

Append `SkillCatalog.Skills.Select(s => new ToolDescriptor(ToolCatalog.SkillKey(s.Name), s.Name, RequiresHostedTools: false))`
between the static descriptors and the MCP ones:
`[.. ToolCatalog.Descriptors, .. skillDescriptors, .. mcpDescriptors]`.
Unlike the MCP portion this is **not** per-user — it is per-deployment, read from disk. Extend the
endpoint's comment to say so.

`Tools_Catalog_ReturnsSelectableKeys` (`ToolSelectionApiTests.cs:93`, asserting the exact list at
line 102) **must be updated**: `"skills"` leaves, `skill:websearch` and `skill:scheduledprompt` arrive.

**Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~ToolSelectionApiTests`.

### Step 5 — Frontend picker
**Files:** `src/Agent42.Backend/wwwroot/js/tools.js`, `src/Agent42.Backend/wwwroot/sw.js`,
`tests/Agent42.Tests/SkillPickerAssetsTests.cs` (new), `tests/Agent42.Tests/PwaAssetsTests.cs`.
*(Parallelizable with step 4.)*

In `renderToolChecks`:
- `const skillsWildcard = selected.has("skills");` alongside the existing `mcpWildcard`.
- `builtins` must now exclude **both** prefixes: `!t.key.startsWith("mcp:") && !t.key.startsWith("skill:")`.
  Missing this is the likely bug — skills would render twice.
- A `skills` group rendered under a `.tool-check__heading` reading "Skills" (the class already
  exists and is styled — reuse it, do **not** add CSS), placed between the built-ins and the MCP group.
- The `cb.checked` line becomes a **three-way** OR — **add** the skills term, never replace the MCP one:
  ```js
  cb.checked = selected.has(tool.key)
    || (mcpWildcard && tool.key.startsWith("mcp:"))
    || (skillsWildcard && tool.key.startsWith("skill:"));
  ```
  Dropping the `mcpWildcard` term re-opens the exact ADR 0026 trap the `null == all` gotcha records:
  a profile or agent storing the bare `"mcp"` would render every server unchecked and the next Save
  would silently disable them all. **No existing test asserts on this line** — nothing in the repo
  catches it — which is why AC12 must pin the full three-way expression by source text.

No change is needed in `profile.js`, `agents.js`, `conversations.js` or `main.js`: they all pass
`… ?? allToolKeys()`, which is derived from the catalog and so picks up the new keys for free.

Bump `sw.js` `CACHE` to `agent42-shell-v20` and update `PwaAssetsTests.cs:215`.

**Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~SkillPickerAssetsTests` and
`~PwaAssetsTests`. Then a manual DevHost pass (`dotnet run --project tools/Agent42.DevHost`,
http://localhost:61326): open Preferences → Tools and the composer Tools popover, confirm two skill
checkboxes under a "Skills" heading, uncheck one, reopen and confirm it stayed unchecked.
**Record the observed result in the ADR** — do not claim the manual leg if it was not run
(ADR 0043 shipped an unverified manual leg and the ADR had to say so).

### Step 6 — Documentation debt, in the same MR
**Files:** `.agentic/decisions/00NN-per-skill-selectable-tools.md`, `.agentic/gotchas.md`, `CLAUDE.md`.

- `/decide` → a new ADR that **amends ADR 0021 and ADR 0026's precedent** (it does not supersede
  either; both stay `status: accepted`). Copy ADR 0026's explicit **Amends** block and re-scope
  0021's "`ToolCatalog` is the **static** single source of truth" a *second* time, in the same
  sentence shape 0026 used — the skills portion of `GET /api/tools` is now disk-derived and
  per-deployment (not per-user, which is the interesting difference from the MCP portion). Record
  the two spike findings that constrain the mechanism (the builder's script-runner requirement, the
  per-directory path filter), the four interview decisions with their rejected alternatives, and
  the consequence below.
- **The ADR must record this consequence, which is not obvious from the diff:** because the picker
  materialises "all" as the **full explicit list**, a Save after this ships persists
  `skill:websearch, skill:scheduledprompt`. A third skill added in a later release is therefore
  **silently off** for every user, agent and conversation that has an explicit selection — where
  today's stored `"skills"` meant "all skills, including future ones". `BindAgentAsync`
  (`AgentGrain.cs:260`) still snapshots the bare wildcard, so it *does* pick up future skills: the
  two paths diverge. ADR 0026 has the same shape for MCP servers, but there a new server is a user
  action; here it is a deploy-time change nobody consented to. Not a blocker (adding a skill is
  ours to sequence, and the fix is a one-line note in that release), but it must be written down.
- `/gotcha` → extend the existing `null == all` gotcha (line 1176) with a **fourth** wildcard trap
  rather than writing a new entry, and add to the "`AgentSkillsProvider` is a TOOL" entry (line 500)
  a sentence saying the gate is now *"at least one selected skill"*, not `enabled.Contains(skills)`.
- `CLAUDE.md` → **two** edits, not one. Append the new bullet in the house style **and rewrite the
  existing ADR 0021 bullet**, which becomes false on two counts the moment `skills` leaves
  `Descriptors`: it lists `skills` among the curated catalog keys, and it says "disabling `skills`
  must **drop the `AgentSkillsProvider`**". Hard rule 2 — fix the invalidated claim in the same MR.
  ADR 0026's bullet was rewritten rather than appended to; follow that.

**Verify:** `dotnet test Agent42.slnx` (full) + `dotnet format Agent42.slnx --verify-no-changes`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | With no stored selection (`null == all`), every shipped skill is advertised to the model | `AllSkills_AdvertisedByDefault` (new) | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC2 | Selecting only `skill:websearch` advertises `websearch` and **not** `scheduledprompt`, and `load_skill` is still registered | `PerSkillSelection_AdvertisesOnlySelectedSkill` (new) | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC3 | Selecting **zero** skills drops the provider entirely (no `load_skill`), RAG retained | `SkillsDisabled_DropsLoadSkill_KeepsRetrieval` (existing, unedited) | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC4 | A legacy stored selection containing bare `"skills"` still enables every skill (zero migration) | `LegacySkillsWildcard_EnablesEverySkill` (new) | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC5 | `GetEnabledToolsAsync` expands a bare `skills` into `skill:{name}` for every shipped skill | `GetEnabledTools_NullSelection_ExpandsSkillsWildcard` (new) | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC6 | `GET /api/tools` returns one descriptor per shipped skill and **no** bare `skills` descriptor | `Tools_Catalog_ReturnsSelectableKeys` (updated) + `Tools_Catalog_IncludesPerSkillKeys` (new) | `tests/Agent42.Tests/ToolSelectionApiTests.cs` |
| AC7 | `Sanitize` preserves `skill:{name}` keys, dedupes them, drops garbage, and `null` stays `null` | `Sanitize_PreservesPerSkillKeys_AndDropsGarbage` (new) | `tests/Agent42.Tests/ToolCatalogTests.cs` |
| AC8 | `skills` is in `AllSelectableToolKeys` but not in `Descriptors` | `Skills_Wildcard_IsInAllSelectableKeys_ButNotInDescriptors` (new) | `tests/Agent42.Tests/ToolCatalogTests.cs` |
| AC9 | A no-tools model still gets no skills provider, whatever is selected | `NoToolModel_RegistersNoTools_AndNoSkillsProvider` (existing, unedited) | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC10 | Both shipped skills are discovered with name + description + an existing path | `DiscoversShippedSkills` (new) | `tests/Agent42.Tests/SkillCatalogTests.cs` |
| AC11 | `Discover` tolerates a missing root, an empty/unreadable subdirectory and a malformed `SKILL.md`, and de-dupes two skills sharing a `name` (first wins) | `Discover_ToleratesBadInput_AndDedupesByName` (new, over a temp directory via the `internal` seam) | `tests/Agent42.Tests/SkillCatalogTests.cs` |
| AC12 | The picker excludes **both** `mcp:` and `skill:` from the built-ins group, renders a "Skills" heading, and `cb.checked` keeps the **three-way** OR (the MCP wildcard term survives) | `SkillPicker_*` source-text guards (new), incl. one pinning the full three-way expression | `tests/Agent42.Tests/SkillPickerAssetsTests.cs` |
| AC13 | The service-worker shell cache was bumped | existing cache assertion, updated to `-v20` | `tests/Agent42.Tests/PwaAssetsTests.cs:215` |
| AC14 | A skill's picker label is its frontmatter `name` verbatim, and its key is `skill:{name}` | `Tools_Catalog_IncludesPerSkillKeys` (new — assert `Key`/`Label` together) | `tests/Agent42.Tests/ToolSelectionApiTests.cs` |
| AC15 | The two `null == all` guards still pass, with `skills` expanded | `BindAgent_SnapshotsEnabledTools`, `LegacyState_NullEnabledTools_MeansAll` (both **updated**) | `tests/Agent42.Tests/AgentGrainTests.cs:2107, :2135` |

AC12 has **no runtime proof** — this repo has no JS runner, so the frontend assertions are
source-text guards over `GetAppScriptAsync()` plus the manual DevHost pass in step 5. Treat that
the way ADR 0037/0043 treat their untestable halves: state honestly in the ADR what was and was
not run.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (2026-08-24) — do not recall these numbers:

| Gate | Threshold |
|---|---|
| `new_issues` | 0 |
| `new_security_issues` | 0 |
| `duplication_pct_max` | 3 |
| `coverage_new_code_pct_min` | 80 |
| `cognitive_complexity_max` | 15 |
| `dependencies.vulnerable_packages` | 0 (hard block) |
| Local enforcement | `soft_warning` — render the gate table, ask proceed/abort on FAIL |
| CI enforcement | `hard_block` on build, tests, `dotnet format`, vulnerable packages, doc-path integrity |
| Branch policy | `branches.protected: [main]` — work on `feature/per-skill-selectable-tools`, always an MR |

Coverage tooling: `dotnet test Agent42.slnx --collect:"XPlat Code Coverage"` (coverlet).

## Out of scope

- **User-authored / uploaded skills.** Deferred by explicit interview decision. It needs its own
  storage, ingestion, management UI and a security position on `run_skill_script` — a separate ADR
  and plan.
- **Coupling a skill to the tools it documents** (auto-hiding `websearch` when `web_search` is off).
  Rejected in the interview; recorded in the ADR as a considered alternative.
- **Surfacing a skill's description in the picker**, and **title-casing the label**. `ToolDescriptor`
  has no description field and adding one changes the contract for every descriptor; the label stays
  the frontmatter `name` verbatim (see the interview decisions).
- **Per-skill *management* (enable/disable at the account level)**, the analogue of the MCP
  `Enabled` flag. Skills ship with the image; every one of them is always in the catalog.
- **Any change to the shipped `SKILL.md` files**, the `.csproj` Content glob, `styles.css`, or
  `index.html` markup.
- **A `skills`-off master switch.** Replaced by the wildcard, per the interview decision.

## Review

A `critic` pass ran against this spec before approval. Verdict: **not a clean pass — 4 high, 4
medium, 3 low**. It independently re-verified the spike findings against the pinned 1.13.0 package
and confirmed every line reference, the Content-glob claim, the gate thresholds and the
`.tool-check__heading` reuse. **All 11 findings were accepted and folded into the steps above —
none rejected.** Where they landed:

| # | Finding | Where it landed |
|---|---|---|
| H1 | Step 5's `cb.checked` line silently deleted the ADR 0026 MCP wildcard term, re-opening the "Save disables every server" trap — and no test would catch it | Step 5 now spells out the **three-way** OR; AC12 pins the full expression by source text |
| H2 | Two existing `null == all` guards provably break and were unnamed; no expected **ordering** given for the order-sensitive `Assert.Equal` | Step 3 names both with line numbers, fixes the composition order (`ExpandSkillsWildcard ∘ ExpandMcpWildcard`) and records the exact expected sequence; new AC15 |
| H3 | AC11 was unassertable — `SkillCatalog` as specified had no seam through which a test could present a bad directory | Step 1 adds an `internal static Discover(string root)` seam; AC11 rewritten around it |
| H4 | The sync-over-async rationale was wrong for Orleans (grains capture the activation `TaskScheduler`), and the safety rested on an unmeasured library detail | Step 1 mandates `Task.Run(...).GetAwaiter().GetResult()` and says plainly that the "completes synchronously" claim was recalled, not measured |
| M1 | Step 6 added a CLAUDE.md bullet but left the ADR 0021 bullet false on two counts | Step 6 now requires **two** CLAUDE.md edits, citing hard rule 2 and the ADR 0026 precedent |
| M2 | Two source comments become false and were unmentioned | Step 2 (`ToolCatalog.cs:57-59`) and Step 3 (`BuildAgent.cs:146-149`) each name their comment |
| M3 | A future shipped skill is silently **off** for anyone who ever pressed Save, and the bind path diverges | Step 6 makes recording it in the ADR an explicit deliverable |
| M4 | The picker label was decided implicitly and pinned by nothing | Promoted to a stated interview decision with its rejected alternatives; new AC14 pins it |
| L1 | `AgentGrain.cs:260` — the third `AllSelectableToolKeys` reader — was missing from Affected areas | Added, in a new "verified, no change" table that also covers the goldens, `ProjectLayoutTests` and `sw.js`'s `SHELL` |
| L2 | No dedupe rule for two skills sharing a frontmatter `name` | Step 1 specifies first-wins ordinal dedupe, mirroring `DeduplicatingAgentSkillsSource`; covered by AC11 |
| L3 | The ADR should re-scope 0021's "static catalog" claim explicitly, as 0026 did | Step 6 requires the explicit Amends block and the re-scoping sentence |

The critic also confirmed sound, with no action needed: the `Sanitize` ordering leaves
`Sanitize_OrdersStaticFirst_ThenPerServer_Deduped` green (`"skills".StartsWith("skill:")` is false,
so the prefixes cannot collide); `SkillCatalog.cs` satisfies all three `ProjectLayoutTests`
assertions; no new `MapAgentApi` registration, so the six-host route-matcher trap does not apply;
AC2's negative assertion is safe; and the goldens need no regeneration.

## Execution notes — where the implementation diverged from this plan

Executed 2026-08-24 on `feature/per-skill-selectable-tools`. All 15 acceptance criteria are met and
all 726 tests pass. Four deliberate divergences, each with its reason:

### 1. TWO plan premises were wrong, and the critic caught the second one after I had shipped it into an ADR

**(a) The name/directory rule (found during step 1).** Finding 4 said "both shipped skills happen to have
`frontmatter.name == directory name` … the code must not rely on that". A spike measured that
`AgentFileSkillsSource` **silently omits** a directory whose frontmatter `name` differs from its directory
name — it does not throw. So the framework enforces that pairing itself, and a mismatched skill is
invisible in the picker with no error anywhere. Recorded as a gotcha and an ADR consequence.

**(b) The pairing mechanism itself (found by the step-5 critic, after a first ADR draft had already
asserted the opposite as "measured").** Finding 4 also said enumerating the root "returns skills with no
path attached", which is what motivated a per-directory scan. **That is false.** `AgentFileSkillsSource`
yields `AgentFileSkill`, whose **public `Path`** is the skill's own directory — one root enumeration gives
both halves of the pair. Verified directly:

```
--- enumerate the ROOT once ---
type=AgentFileSkill name=alpha
    AgentFileSkill.Path = ...\skillprobe2-...\alpha
type=AgentFileSkill name=beta
    AgentFileSkill.Path = ...\skillprobe2-...\beta
```

This was not merely a documentation error — the per-directory scan it motivated carried a **latent bug**.
The source *recurses*, so a nested `Skills/category/foo/SKILL.md` would have been recorded with
`Path = Skills/category`, and two skills under one category would have become **mutually inseparable in
the `BuildAgent` gate**. Measured:

```
--- nested layout: Skills/category/foo/SKILL.md ---
    name=foo path=...\skillprobe3-...\category\foo
```

`Discover` was rewritten around one root enumeration + `AgentFileSkill.Path`. That change also:

- **fixes the nesting bug** (pinned by `Discover_DedupesByName_AndPairsNestedSkillsWithTheirOwnDirectory`);
- **makes AC11's de-dupe half constructible again** — two skills *can* share a name once directories nest
  (`Skills/alpha/dup`, `Skills/zeta/dup`), so the first-wins ordinal de-dupe the plan asked for is now
  genuinely tested rather than unreachable dead code;
- **closes a `Lazy` hazard the critic flagged separately**: `Directory.GetDirectories(root)` sat *outside*
  the per-directory `try`, and `Lazy<T>` **caches a thrown exception** — an unreadable root would have
  rethrown on every subsequent touch, failing `BuildAgent` for every conversation on the silo for the life
  of the process. The whole body is now guarded.

**(c) A third rejection was too broad.** The ADR draft rejected filtering on the measured
`AgentSkillsProviderBuilder` script-runner throw. That throw is real, but **`FilteringAgentSkillsSource`
is public and works without a script runner**, composing with the source-taking `AgentSkillsProvider` ctor:

```
--- is FilteringAgentSkillsSource usable without a script runner? ---
    filtered -> [alpha]
    AgentSkillsProvider(source, options) constructed OK: AgentSkillsProvider
```

The path-list approach was **kept** (a `SkillCatalog` must exist regardless — `GET /api/tools` needs each
skill's name and description, which a predicate cannot supply — and the drop-the-provider rule needs an
explicit count either way), but the ADR now records the alternative honestly as viable-and-rejected-on-
balance rather than impossible.

**The lesson worth keeping:** a plan finding labelled "measured, not recalled" was neither, and it survived
into an accepted ADR. It was caught only because the critic re-derived it from the package metadata.

### 2. `SkillCatalog.Discover` is `public`, not `internal` + `InternalsVisibleTo`

Step 1 said "make `Agent42.Agents`'s internals visible to `Agent42.Tests` if they are not already".
They are not — and the repo deliberately has **no `InternalsVisibleTo` anywhere**. The existing
precedent for exactly this situation is documented in `ChatClientFactory.ApiKeyHeaderPolicy`
("Public for the same reason `Provider` is — the repo has no `InternalsVisibleTo`, so … unit-testable
only if reachable from the test project"). Copying the exemplar's shape beat introducing a
project-wide affordance for one seam. `Discover`'s doc comment records the reason.

### 3. `renderToolChecks` grew a small `addGroup` helper

Step 5 implied a second copy of the six-line heading block beside the MCP one. Extracting
`addGroup(heading, group)` and calling it twice is behaviourally identical and removes the duplication.
No existing test asserted on the old block (checked before changing it). AC12's guards were written
against the new shape, and one of them (`addGroup("MCP servers", mcpServers)`) pins that the MCP group
still renders.

### 4. `GetSkillsAsync` returns `Task<IList<AgentSkill>>`, not a `ValueTask`

A one-line correction found at compile time — no `.AsTask()` is needed inside the `Task.Run` bridge.
The bridge itself is exactly as step 1 mandated and must not be "simplified" to a bare
`.GetAwaiter().GetResult()`.

### 5. Critic findings folded in before the MR

A `critic` pass ran on the full diff (step 5). Verdict: 1 medium-high, 1 medium, 4 low — **all six
accepted and fixed**, none rejected:

| # | Finding | Fix |
|---|---|---|
| 1 | The ADR's "measured" pairing premise was refuted by the package's own metadata (`AgentFileSkill.Path` is public), and the mechanism it motivated had a nested-layout bug | `Discover` rewritten around one root enumeration; ADR premises 1 + 2 rewritten to record what was wrong and why; nesting pinned by a new test |
| 2 | The recorded expansion order in the `GetEnabledToolsAsync` comment was **backwards** — the real projection is `[static…, mcp:…, skill:…]`, invisible to tests because both order-sensitive guards run with zero MCP servers | Comment corrected in code and ADR, and now says explicitly that it disagrees with `Sanitize`'s order harmlessly, and that the mcp:/skill: relative order has no test |
| 3 | The tolerance guarantee was stated wider than the code delivered: root enumeration sat outside the `try`, and `Lazy` caches the exception permanently | Whole body guarded; the `Lazy`-caches-exceptions reason documented in the class doc and ADR consequences |
| 4 | `DiscoversShippedSkills`' pairing loop did not test what its comment claimed (`GetDirectoryName(Path)` is the `Skills/` root) | Split into a real `EachSkillsPathResolvesToThatSkillAlone` that points `Discover` at each skill's own path |
| 5 | `ToolCatalog`'s class doc still claimed to be the unqualified "single source of truth" — false for both prefixed groups since ADR 0026 | Re-scoped in the class doc, naming both appended groups and both wildcard sentinels |
| 6 | The new CLAUDE.md bullet was separated by a blank line, turning the list loose | Blank line removed |

The critic also confirmed, with no action needed: all 15 ACs map to an existing test; AC3/AC9 are the
pre-existing guards and are genuinely unedited; the three `load_skill`/`read_skill_resource`/`run_skill_script`
map entries were correctly retained; AC2's negative assertion is safe; both edited gotchas keep the required
evidence/verified format; and `check-doc-paths.mjs` passes.

### Manual verification — the leg the plan asked for, and it WAS run

Step 5 required a DevHost pass and warned not to claim it if unrun (the ADR 0043 precedent). It ran,
in headless Chrome over CDP against DevHost. Observed:

- `GET /api/tools` returned the four static descriptors plus
  `skill:scheduledprompt` / `skill:websearch`, and **no** bare `skills` descriptor.
- Preferences → Tools rendered the built-ins, a `Skills` heading, then both skill boxes — labelled
  `scheduledprompt` / `websearch` (frontmatter name verbatim, AC14) — all checked from the expanded
  `null == all`.
- Unchecking `skill:websearch` and pressing Save persisted
  `["web_search","location","scheduling","code_interpreter","skill:scheduledprompt"]`; reopening showed
  it still unchecked. That persisted value is also the direct evidence for the "a future shipped skill
  is silently off" consequence ADR 0048 records.
- The composer Tools popover rendered the same grouping from the same function.

Separately, the AC12 source-text guard was checked for **vacuity**: deleting the `mcpWildcard` term
from `cb.checked` makes `CheckedState_KeepsTheThreeWayWildcardOr` fail, and restoring it makes it pass.

### Quality gate (measured this session, never estimated)

| Gate | Threshold | Actual | Result |
|---|---|---|---|
| `new_issues` | 0 | 0 (`dotnet build` succeeded) | PASS |
| `new_security_issues` | 0 | 0 — selection can only narrow; no new egress surface | PASS |
| Tests | green | 726/726 | PASS |
| Lint | clean | `dotnet format --verify-no-changes` exit 0 | PASS |
| `coverage_new_code_pct_min` | 80 | **98.8%** (512/518 lines, the 4 changed production files) | PASS |
| `duplication_pct_max` | 3 | no tool in this repo | **SKIPPED — flag for removal** |
| `cognitive_complexity_max` | 15 | no cognitive tool; cyclomatic max **CCN 9** (`BuildAgent`, was 8) via lizard 1.23.0 | **SKIPPED — flag for removal** |
| `dependencies.vulnerable_packages` | 0 | 0 across all 6 projects | PASS |

Two gates name a metric nothing in this repo measures. Reported SKIPPED rather than estimated, per
`/execute-plan` step 4 — they should either be deleted or paired with a real tool.
