# Plan — split `app.js` into ES feature modules

planned-against: main @ 665acde

## Overview

`src/Agent42.Backend/wwwroot/app.js` is 3616 lines / 166 KB in a single IIFE — roughly 25 ADRs'
worth of frontend on one flat file. Split it into ~29 native ES modules under `wwwroot/js/`,
loaded by `<script type="module" src="js/main.js">`.

This is the frontend instalment of ADR 0039 (Agents) and ADR 0040 (Backend): the module names
reuse the same feature vocabulary, so `wwwroot/js/mcp.js` pairs visibly with
`src/Agent42.Backend/Mcp/` and `src/Agent42.Agents/Mcp/`. Native ES modules are chosen precisely
because they need **no build step** — the vanilla-JS/no-SPA property of this frontend is
deliberate and must survive the refactor intact.

Why now: the file already carries 24 hand-drawn section banners, i.e. the author has been
manually simulating modules for a while. The next feature makes it worse, not better.

**Nothing here is a behaviour change.** It is a file split plus per-module state ownership, with
the doc debt paid in the same MR (hard rule 2).

## Affected areas

Verified against the tree at 665acde.

- `src/Agent42.Backend/wwwroot/app.js` — **deleted**; its 3616 lines move into `wwwroot/js/*.js`.
- `src/Agent42.Backend/wwwroot/index.html` — line 800 (the script tag) **and** the comment at
  lines 643–644, whose stated invariant `type="module"` invalidates. Nothing else.
- `src/Agent42.Backend/wwwroot/sw.js` — `SHELL` (line 6) + `CACHE` bump (`agent42-shell-v12` →
  `-v13`), and three prose comments naming `app.js` (lines 27, 56, 119).
- `tests/Agent42.Tests/StaticAssetsServerFixture.cs` — the module-graph concatenation seam, plus
  the shared `Body()` helper (see L2 below).
- 11 test classes, 47 call sites: `AgentEditorAssetsTests` (4), `ConfigHealthAssetsTests` (2),
  `ImageGenerationAssetsTests` (4), `LocationAssetsTests` (4), `McpServersAssetsTests` (2),
  `ModelPickerAssetsTests` (7), `PwaAssetsTests` (11), `ShareLinkAssetsTests` (1),
  `SpeechInputAssetsTests` (5), `UsageAssetsTests` (3), `VoiceConversationAssetsTests` (4).
- `tests/Agent42.Tests/ProjectLayoutTests.cs` — four new frontend-layout assertions.
- `src/Agent42.Backend/Users/AgentApi.Users.cs:25` — comment naming `app.js`.
- `.agentic/gotchas.md` — 9 references to `wwwroot/app.js` (lines 29, 51, 174, 373, 418, 538,
  814, 1006, 1036), including the exemplar-table row; plus three new entries.
- `.agentic/decisions/0041-*.md` — new ADR.
- 7 ADRs referencing `app.js` (0006 L21, 0023 L39/L62, 0028 L118, 0029 L100, 0032 L14/19/35/41/60,
  0036 L14, 0037 L32) and `CLAUDE.md` (L292, L403, L411, L461).

**Untouched, deliberately:** all `index.html` *markup*, `styles.css`, `wwwroot/shared/viewer.js`
(its own self-contained public viewer), `manifest.webmanifest`, all Bicep/CI, every other C# file.

## Context & constraints

### Why ES modules and not a bundler
The frontend is vanilla-JS-by-decision (CLAUDE.md: "no SPA build"). `type="module"` is deferred,
so it executes after DOM parse — later than today's end-of-`<body>` classic script, but after the
same markup exists. `marked`/`purify` stay classic `<script>` tags (`index.html:798-799`) and do
execute before a deferred module, so `marked.setOptions` is safe and `marked`/`DOMPurify` remain
globals. Verified inert for this codebase: no inline `onclick=`/`onchange=` handlers in
`index.html`, no top-level `this`, no `document.currentScript`, no CSP header anywhere in
`Program.cs` or `infra/`, and `"use strict"` is already on. Both hosts use plain `UseStaticFiles()`
(`src/Agent42.Backend/Program.cs:429-430`, `tools/Agent42.DevHost/Program.cs:137-138`) — not
`MapStaticAssets` — so there is no fingerprinting or import-map rewriting to break, and the
Dockerfile copies the project dir wholesale. `container-app.bicep:117` sets `transport: 'auto'`,
so prod is HTTP/2 and ~29 multiplexed subresources cost nothing meaningful.

### State ownership — per module, NOT a shared `state.js`
**This is the load-bearing design decision and the first draft got it wrong.** There are 35
module-level `let`s (`grep -cE "^  let " app.js`), and their write-sites show clean per-feature
ownership already:

| Field | Written from | Owner |
|---|---|---|
| `streaming` (1680, 1789) | send only | `send.js` |
| `profile` (1857, 1949) | profile only | `profile.js` |
| `defaultModel` (347), `modelsData` (346) | models only | `models.js` |
| `toolCatalog` (1979) | tools only | `tools.js` |
| `ephemeral` (889) | agents only | `agents.js` |
| `pendingRegenerate` (803), `lastFix`/`locationRefreshTimer` (1400-1401), `pushConfig` (1591) | section-local | stay module-private |
| `activeId` (761, 1278, 1348) | agents, conversations | `conversations.js` + setter |
| `conversations` (446, 453, 601, 3576) | sidebar, cache | `sidebar.js` + setter |
| `agents` (678, 684, 861, 3580) | agents, cache | `agents.js` + setter |
| `chatTools` (1303, 1352, 2043, 2051, 3608) | conversations, tools, init | `tools.js` + setter |
| `activeAgentId`/`pendingAgentId` (762-763, 1309-1315, 1349-1350, 1775) | agents, conversations, send | `conversations.js` + setters |

So: **each field is declared `export let` in its owning module.** Cross-module *reads* use the
live binding — ES module live bindings mean a read site is **syntactically unchanged**. Only the
handful of cross-module *writes* in the bottom five rows go through a small exported setter.

Two consequences that a single `state` object would have cost us:

- **Six existing assertions survive byte-identical.** `PwaAssetsTests.cs:210-211`
  (`cachePut("conversations", conversations)` — cited by name as evidence in `gotchas.md:1036`),
  `ImageGenerationAssetsTests.cs:36`, `ModelPickerAssetsTests.cs:77/91/102` all embed bare state
  identifiers at *read* sites. A `state.` prefix would have broken every one of them, forcing six
  test edits mid-refactor — exactly the pressure that gets assertions loosened rather than fixed.
- **An evaluation-order hazard never arises.** `app.js:236` is
  `const expandedAgents = loadExpandedAgents();` — a call **at evaluation time** into a function
  declared at line 457, i.e. inside `sidebar.js`. A shared `state.js` holding `expandedAgents`
  would have to import `sidebar.js` and call it during its own evaluation, breaking the
  leaf-imports-nothing rule. Under per-module ownership both sit in `sidebar.js` and hoisting
  keeps working exactly as today.

### Deleting `app.js` breaks a CI gate
`.agentic/scripts/check-doc-paths.mjs` runs in `pr-checks.yml` and **exit-1s on any backticked
repo path in `CLAUDE.md`/`gotchas.md`/`decisions/*.md` that does not exist**. Its `PATH_RE` fires
on any line containing a slashed path, so all 9 full-path `src/Agent42.Backend/wwwroot/app.js`
references in `gotchas.md` fail the moment the file is gone. Bare `` `app.js` `` in prose is not
matched (`ROOT_RE` applies only to `evidence:` lines and table rows), but those mentions still go
stale. `.agentic/specs/` is not scanned.

Rejected alternative: keeping `app.js` as a one-line `import "./js/main.js"` shim so paths
resolve. That satisfies the mechanical check while pointing every doc reference at a stub
containing none of the claimed content — worse than the drift it hides. **Delete it and repoint
the docs at the specific module**, which is a genuine improvement.

### Construct-then-flip, not move-incrementally
An IIFE cannot be half-split: the moment a section leaves `app.js`, the 47 source-text assertions
that grep `/app.js` go red, and there is no way for a test helper to discover `js/*.js` over HTTP
before `main.js` exists (`UseStaticFiles()` has no directory listing). So the modules are
**built alongside an untouched `app.js`** (steps 3–6, suite green throughout because `app.js`
still holds everything), and index.html/sw.js/fixture/deletion flip together in **one atomic
step** (step 7). There is no red window in the repo and no `/app.js` fallback hack in the fixture.

### The 47 source-text assertions, and how they break
1. **Text no longer in one file.** Fixed by the step-7 concatenation seam.
2. **Indentation-dependent brace matching.** `ModelPickerAssetsTests.cs:89` slices a body with
   `app.IndexOf("\n  }", start)` — that two-space indent *is* the IIFE body's indentation.
   De-indenting to module scope makes bodies close with `\n}`, so this scan runs past its end.
   `LocationAssetsTests.cs:122-139` already does the indentation-independent thing (find `{`, count
   depth) — **that is the exemplar**; it is duplicated in five classes already (L2 below).
3. `export function foo()` still contains `function foo()`, so declaration-anchored `IndexOf`
   assertions survive the prefix untouched.

### The composition-root failure mode inherited from ADR 0040
ADR 0040 found that an endpoint group which exists but is never called from `MapAgentApi` compiles
cleanly and silently deletes every endpoint in it. The frontend split has the **exact analogue**: a
module whose `wire*()` is never called from `main.js` loads fine, throws nothing, and silently
attaches no listeners — that feature's buttons just do nothing. No build, lint or type check sees
it. Hence per-feature `wire*()` with `main.js` as composition root, and a dedicated guard
(step 9) mirroring `AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot`.

### The sw.js SHELL traps — both directions
`sw.js:11` is `c.addAll(SHELL)`, which is **all-or-nothing**: one non-2xx entry rejects the whole
`install` `waitUntil` and **the service worker never activates**. SHELL goes from 10 entries to
~38, so both directions are now hazards:

- **Missing entry** → the module isn't cached → the installed PWA breaks **offline only**,
  invisible in every test, in DevHost and in prod-online. ADR 0032's IndexedDB mirror makes it
  worse: it will happily paint cached conversations into a shell whose JS failed to load.
- **Stale entry** (a renamed/deleted module still listed) → `addAll` rejects → **no SW at all**,
  which takes down the offline read cache (ADR 0032), the Web Share Target (ADR 0023 — `POST
  /share` reaches a server with no route) and Web Push (ADR 0028 — no registration to subscribe
  against). Also invisible online.

The step-9 guard must therefore be **bidirectional**: the `/js/*` entries of SHELL must equal the
directory contents exactly, not merely contain them.

### Version skew across a module graph is a new failure mode
`sw.js:92` serves subresources cache-first. With one JS file a stale serve is self-consistent.
Across ~29 modules the window between a network-first `index.html` (`sw.js:81`) and the new SW
activating can serve a **version-skewed graph** — and a cross-module export rename then throws
`SyntaxError: does not provide an export named X`, i.e. a blank app rather than a degraded one.
Bumping `CACHE` is necessary but does not close the window. Consequence: **every future MR that
touches `wwwroot/js/` must bump `CACHE`**, a rule that used to be forgiving. Records as an ADR
consequence and a gotcha.

### The cycles, and the seam that already exists to break them
`send.js` ↔ `voice.js` is a genuine cycle: `send()` feeds stream deltas to the voice controller,
and the controller auto-sends the captured utterance. **This is already solved in the current
code** — `voice`, `stopSpeech`, `sendAbort` (lines 259–269) are forward-declared mutable slots
that exist for exactly this reason. Preserve it: they are `export let` on `send.js`, assigned via
setter by `voice.js`/`speech.js`, and `send.js` only ever *reads* them at call time. Do not "clean
this up" into direct imports.

The dependency rule, asserted rather than hoped for:
- **Leaves** (`format.js`, `tabs.js`, `cache.js`) import nothing from the app.
- `dom.js` imports **only** `tabs.js` — never the reverse. `wireTabs` is currently *called* at
  line 88 but *declared* at 1884; hoisting makes that work today and it becomes a real
  evaluation-order edge across modules.
- Feature modules import leaves + `dom.js` freely, and each other only for **runtime calls and
  live read-bindings**, never for values consumed during module evaluation. Residual cycles are
  then harmless.
- `main.js` is imported by nobody and is the only caller of `wire*()`/`init*()`.

### Governing docs to read before executing
`.agentic/decisions/0039-*.md`, `0040-*.md` (the shape and its verification techniques);
`.agentic/gotchas.md` — "Service worker must stay network-first" (L1009), "Offline data … lives in
app.js IndexedDB" (L1023), "The boot chat is not created via `startNewConversation()`" (L523),
"The Web Share Target `respondWith` must sit after the `/api` guard" (L1359).

## Steps

Sequential — every step edits the same shrinking file, so two agents in one checkout would corrupt
each other. Do not parallelize.

### Step 1 — Branch and baseline
Branch `feature/frontend-es-modules`. Confirm `dotnet test Agent42.slnx` green at HEAD. Capture the
statement multiset of `app.js` (non-blank, non-brace-only, non-comment, trimmed) — step 8
reconciles against it, and the result is recorded in the ADR, not left in a scratch file.

### Step 2 — Leaves
Create `js/format.js` (`marked.setOptions`, `renderMarkdown`, `escapeHtml`, `linkifyPlainText`,
`timeLabel`, `LANGUAGES` — lines 291–334), `js/tabs.js` (`wireTabs`, 1884–1911), `js/cache.js`
(3514–3584). `app.js` is **not** edited yet.
**Verify:** `dotnet test Agent42.slnx` — still green, still reading the intact `app.js`.

### Step 3 — `js/dom.js`
Every `document.getElementById` lookup (5–48, 52–173) as named exports, importing `wireTabs` from
`js/tabs.js` for `profileTabs`/`agentTabs`. Comment blocks intact.
**Verify:** `dotnet test Agent42.slnx`.

### Step 4 — Feature modules
One module per banner section, each exporting its functions, its own `export let` state per the
ownership table above, and a `wire<Feature>()` attaching that feature's listeners.

| Module | Source lines | Notes |
|---|---|---|
| `models.js` | 335–439 | owns `modelsData`, `defaultModel` |
| `sidebar.js` | 440–607 | owns `conversations` + `expandedAgents` (and `loadExpandedAgents`, see H5) |
| `sharelinks.js` | 608–671 | |
| `agents.js` | 672–895 | owns `agents`, `ephemeral`, `pendingRegenerate`; incl. `lockPicker`/`unlockPicker`/`setIncognito` |
| `thread.js` | 896–980 + 1203–1271 | message DOM + tool-call/usage rendering + `scrollToBottom` (1203) |
| `attachments.js` | 981–1202 | |
| `conversations.js` | 1272–1365 | owns `activeId`, `activeAgentId`, `pendingAgentId` + setters |
| `drawer.js` | 1366–1385 | |
| `location.js` | 1386–1586 | owns `lastFix`, `locationRefreshTimer` |
| `push.js` | 1587–1675 | owns `pushConfig` |
| `send.js` | 1676–1798 | owns `streaming` + the `voice`/`stopSpeech`/`sendAbort` slots |
| `composer.js` | 1799–1805 | `autosize` — imported by send/speech/attachments |
| `theme.js` | 1810–1852 | split out of the profile section |
| `profile.js` | 1853–1970 | owns `profile` |
| `tools.js` | 1971–2062 | owns `toolCatalog`, `chatTools` + setter |
| `providers.js` | 2063–2255 + 2323–2501 | |
| `confighealth.js` | 2256–2322 | |
| `mcp.js` | **2502–2692** | see re-cut below |
| `usage.js` | 2713–2916 | owns `usageRefreshTimer` |
| `speech.js` | 2918–3020 | sets the `stopSpeech` slot |
| `voice.js` | **3021–3365** | sets the `voice` slot |
| `sharetarget.js` | 3421–3513 | |
| `pwa.js` | 3413–3420 | |

**Four sections are deliberately re-cut, not moved whole** (the first draft named only two):
- **providers** (439 lines) splits its config-health half into `confighealth.js`.
- **`mcp.js` ends at 2692, not 2712.** Lines 2693–2711 are composer/send/attachment wiring —
  `input` autosize, Enter-to-submit, `form submit → send(prompt)`, `attachBtn`, `fileInput →
  handleFiles` — sitting under the MCP banner. They belong in `wireComposer()`/`wireSend()`/
  `wireAttachments()`. Leaving them would put the chat submit handler behind `wireMcp()`.
- **`voice.js` ends at 3365, not ~3380.** Lines 3367–3376 are
  `newChatBtn → startNewConversation` (→ `conversations.js`) and agent add/cancel/submit wiring
  (→ `agents.js`).
- The remaining tail 3378–3411 (agent-key copy, agent-run URL copy, drawer keydown) splits between
  `agents.js` and `drawer.js`.

**Verify:** `dotnet test Agent42.slnx` after the step — still green against the intact `app.js`,
which proves only that nothing else regressed. The real proof is step 8.

### Step 5 — `js/main.js`
The composition root: import every `wire*()` and call them, then the init sequence from 3585–3616
verbatim — including the `Promise.all([profileReady, toolsReady])` boot-chat seed, whose guard
(`activeId === bootChatId && activeAgentId === null`) is load-bearing per the boot-chat gotcha
(`gotchas.md:523`).
**Verify:** `dotnet test Agent42.slnx`.

### Step 6 — The test seam
In `StaticAssetsServerFixture.cs`, add `GetAppScriptAsync()`: start at `/js/main.js`,
regex-extract specifiers, fetch transitively (visited-set), concatenate. Fetching over HTTP keeps
the "is it actually served" meaning; walking the *import graph* rather than the directory means an
unreferenced file is correctly excluded. **The specifier regex must handle all four forms** —
`import … from "…"`, `import "…"` (side-effect only, which `pwa.js`/`sharetarget.js` naturally
are), `export … from "…"`, and `export * from "…"` — or those modules are silently dropped from
the corpus *and* flagged as orphans by step 9's guard, which uses the same walker.

Also move the `Body()` depth-counting helper — currently duplicated in five classes
(`ConfigHealthAssetsTests.cs:71`, `ImageGenerationAssetsTests.cs:74`, `LocationAssetsTests.cs:124`,
`ShareLinkAssetsTests.cs:80`, `UsageAssetsTests.cs:84`) — onto the fixture, and use it for the
`ModelPickerAssetsTests.cs:89` fix rather than copying it a sixth time. Net duplication goes down,
which matters against the 3% gate.
**Verify:** compiles; tests still read `/app.js` at this point.

### Step 7 — The flip (atomic)
In one commit: `index.html:800` → `<script type="module" src="js/main.js"></script>`; fix the
now-false comment at `index.html:643-644` (a deferred module runs after parsing, so "a script tag
runs synchronously at the point it's parsed" no longer explains the ordering); `sw.js` SHELL gets
all `js/*.js` and `CACHE` → `agent42-shell-v13`, with the three prose comments (27, 56, 119)
repointed; the 47 call sites switch to `GetAppScriptAsync()`; **`app.js` is deleted**.
**Verify:** `dotnet test Agent42.slnx` green; `dotnet run --project tools/Agent42.DevHost` loads
with no console errors and every module 200s in the Network tab.

### Step 8 — Prove inertness
Reconcile the step-1 multiset against the concatenated modules. Allowed differences, exhaustively:
(a) `import`/`export` statement lines; (b) the removed IIFE wrapper and `"use strict"`; (c) the
`export ` prefix on declarations; (d) **the new `wire*()` function headers and the ~44 listener
registrations relocated into them** — enumerate these; (e) the handful of cross-module writes that
became setter calls. Anything else is an unintended edit: revert or justify in writing.
**Verify:** the diff is empty or fully accounted for, and the account goes into ADR 0041.

### Step 9 — The four new guards
In `ProjectLayoutTests.cs`. Following that file's own convention (`:109-111` — "asserted by NAME
rather than by count, because prose saying 'sixteen folders' beside a list of seventeen is how the
docs drifted"), the composition-root guard uses an **explicit `TheoryData` name table**, not a
directory scan: a scan passes trivially if a module drops its `wire*()` and inlines listeners at
top level.
1. **Composition root** — each named module exports its `wire*()`/`init*()` *and* `main.js` calls
   it. Negative control: delete one call, test must fail.
2. **Shell completeness, bidirectional** — the `/js/*` entries in `sw.js`'s `SHELL` equal the
   directory contents exactly (neither missing nor stale).
3. **No orphans** — every file under `wwwroot/js/` is reachable from `main.js`'s import graph.
4. **Entry point** — `index.html` contains `<script type="module" src="js/main.js">`, and
   `GET /js/main.js` serves as JavaScript (mirroring `ServiceWorker_IsServed_AsJavaScript`).
   Without this, nothing automated proves the app is reachable at all: the suite now enters at
   `/js/main.js` directly, so a broken/missing/`type`-less script tag leaves **every test green
   while the app is dead in a browser**. Today that is guarded only implicitly, by every asset
   test doing `GET /app.js`.
**Verify:** `dotnet test Agent42.slnx`; run each negative control by hand and confirm it fails.

### Step 10 — Manual smoke
In DevHost, exercise what cannot run headlessly: hands-free voice (ADR 0037 — listen, speak,
barge-in), dictation (ADR 0006), Preferences tab order incl. the MCP-OAuth deep link to
`profileTabs.select(3)`, attachment upload, incognito toggle, drawer on a narrow viewport, and an
**offline reload of the installed PWA** (the only way to catch a SHELL mistake).
**Verify:** each behaves as before; anything that doesn't goes in the MR description.

### Step 11 — Documentation debt, same MR
`.agentic/decisions/0041-frontend-es-modules.md`, following the 0039/0040 shape: `extends: [0039,
0040]`; the module vocabulary; **why there is no flat-namespace analogue** (ES specifiers are file
paths resolved by the browser at load time and are never persisted — the Cosmos `$type` hazard
forcing flat namespaces in Agents has no frontend counterpart, and a reader arriving from 0039
will look for one); per-module state ownership and why a shared `state.js` was rejected; the
composition-root failure mode; the step-8 inertness evidence; alternatives considered (bundler,
in-place reorganisation, classic scripts + namespace object — the latter two were put to the
maintainer and rejected).

Three `gotchas.md` entries: the **bidirectional** SHELL trap (`addAll` is all-or-nothing — a stale
entry kills the whole SW, not just one file); **module version skew** (cache-first subresources +
a renamed export = `SyntaxError` and a blank app; every MR touching `wwwroot/js/` must bump
`CACHE`); and the `GetAppScriptAsync()` seam (a new module must be reachable from `main.js` to be
covered by the 47 assertions at all).

Repoint the 9 `gotchas.md` references — including the exemplar-table row, which becomes a feature
module plus a composition-root row mirroring the two ADR 0040 rows — the 7 ADRs, the 4 CLAUDE.md
lines, and `AgentApi.Users.cs:25`. Update CLAUDE.md's "Frontend is deliberately vanilla JS" bullet
to describe the module layout.
**Verify:** `node .agentic/scripts/check-doc-paths.mjs` exits 0.

### Step 12 — Gates and MR
`dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`, `dotnet build
Agent42.slnx`, `node .agentic/scripts/check-doc-paths.mjs`. Render the gate table, then open the MR.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test / proof | Lives in |
|---|---|---|---|
| AC1 | `app.js` is gone and the app loads from `js/main.js` | new entry-point test (guard 4) + manual DevHost load | `tests/Agent42.Tests/ProjectLayoutTests.cs`, step 10 |
| AC2 | All 47 existing source-text guards pass, unchanged in meaning | `dotnet test Agent42.slnx` — per-module `export let` keeps every read site byte-identical; only `ModelPickerAssetsTests.cs:89`'s indentation-dependent slice is edited, to the existing depth-counting shape | the 11 `*AssetsTests` classes |
| AC3 | No *expression* changed — only declarations gained `export`, listeners moved into `wire*()`, and cross-module writes became setter calls | step-8 multiset reconciliation, recorded in ADR 0041 | ADR 0041 |
| AC4 | A `wire*()` not called from `main.js` fails the build | composition-root test (name table) + negative control | `tests/Agent42.Tests/ProjectLayoutTests.cs` |
| AC5 | SHELL matches `wwwroot/js/` exactly, both directions | bidirectional shell test | `tests/Agent42.Tests/ProjectLayoutTests.cs` |
| AC6 | No orphan module | import-graph test (all four specifier forms) | `tests/Agent42.Tests/ProjectLayoutTests.cs` |
| AC7 | No doc references a path that no longer exists | `check-doc-paths.mjs` exits 0 | CI `pr-checks.yml` |
| AC8 | Voice, dictation, tabs, attachments, incognito, drawer, offline reload behave as before | manual DevHost smoke | step 10, reported in the MR |

AC3 and AC8 have no automated guard — AC3's evidence lands in the ADR rather than a scratch file,
and AC8 is manual because the speech APIs cannot run headlessly (the same limitation ADR 0037
already accepted). Both rest on the executing agent's reported evidence; that is stated here
rather than papered over.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (2026-08-19):

| Gate | Threshold | Expectation for this MR |
|---|---|---|
| `new_issues` | 0 | — |
| `new_security_issues` | 0 | no new surface; no code changes hands |
| `duplication_pct_max` | 3 | **watch this one.** A split invites copy-pasted helpers — `format.js`/`composer.js` exist so `escapeHtml`/`autosize` are imported, not duplicated. Step 6 nets duplication *down* by lifting the 5×-duplicated `Body()` helper onto the fixture |
| `coverage_new_code_pct_min` | 80 | not measurable: coverlet covers C# only; the JS is guarded by source-text asset tests. Report honestly rather than inventing a number |
| `cognitive_complexity_max` | 15 | unchanged — no function body is edited |
| `vulnerable_packages` | 0 | no dependency change |

CI hard-blocks on build, tests, `dotnet format`, vulnerable packages, doc-path integrity.

## Out of scope

- `styles.css` (2234 lines) and `index.html`'s **markup** (802 lines) — the same accumulated-
  features problem, explicitly deferred by the maintainer to separate MRs. Only the script tag and
  the one comment its semantics invalidate change here.
- Any behaviour change, bug fix, or "while I'm in here" cleanup. If a bug is spotted during the
  move, note it in the MR and leave the code as-is — an inert split is the whole value.
- A bundler, minifier, TypeScript, or any build step.
- `wwwroot/shared/viewer.js` — a separate self-contained public viewer, not part of the app shell.
- Reorganising `tests/Agent42.Tests` (81 flat files) — the obvious next follow-up, as ADR 0040 said.

## Review

A `critic` subagent reviewed the first draft and returned five HIGH, six MEDIUM and five LOW
findings. All were verified against the tree before acting; none were taken on trust.

**Accepted and fixed:**

- **H1** (steps 3–5 unverifiable; the `/app.js` fallback in the fixture is self-contradictory,
  since those steps remove the very text the assertions grep). Restructured to **construct-then-
  flip**: modules are built alongside an untouched `app.js` (green throughout), and the flip is one
  atomic step. The fallback hack is gone entirely.
- **H2** (six assertions embed bare state identifiers, so a `state.` prefix breaks them; AC2 was
  false). Fixed at the design level rather than by the critic's proposed remedy of editing six
  tests: **state moves to per-module `export let` ownership**, whose live read-bindings leave every
  read site byte-identical. Verified against the write-site distribution — most fields have a
  single writer already. AC2 is now true.
- **H3** (`cache.addAll` is all-or-nothing, so a *stale* SHELL entry kills the whole SW — the
  one-directional guard misses it). Guard is now bidirectional; consequences for ADR 0023/0028/0032
  recorded.
- **H4** (nothing proves `index.html` points at the entry; the implicit `GET /app.js` guard
  disappears with the split). Added as guard 4 and as AC1's actual proof.
- **H5** (`app.js:236` `const expandedAgents = loadExpandedAgents()` is a second evaluation-order
  hazard). Dissolved by H2's redesign — both now live in `sidebar.js`. Noted explicitly so a future
  reader does not reintroduce a shared state module and resurrect it.
- **M1** (two more mis-cut ranges). Verified: `mcp.js` must end at **2692** — 2693–2711 is composer/
  send/attachment wiring — and `voice.js` at **3365**, with 3367–3376 being newChat/agent wiring.
- **M2** (AC3's inertness proof unachievable, since `wire*()` wrappers are new statements).
  AC3 restated as "no *expression* changed", with the allowed differences enumerated exhaustively.
- **M3** (`index.html:643-644` states an invariant `type="module"` invalidates, colliding with
  "index.html out of scope"). Now an explicit in-scope edit; the Out-of-scope section says markup.
- **M4** (35 `let`s not 33; four are section-local; reference counts eyeballed). Corrected, and the
  ownership table now says which stay module-private.
- **M5** (specifier regex misses side-effect imports). All four forms specified, with the reason.
- **M6** (module version skew → `SyntaxError` and a blank app). Adopted as an ADR consequence, a
  gotcha, and a standing rule that every MR touching `wwwroot/js/` bumps `CACHE`.
- **L1** (guards should use a name table, per that file's own convention). Adopted.
- **L2** (`Body()` copied a sixth time, against the 3% gate). Lifted onto the fixture instead.
- **L3/L4** (`AgentApi.Users.cs:25`; "four prose comments" is three). Corrected.
- **L5** (AC3/AC8 have no automated guard). Not fixable — stated plainly in the AC table instead.

**Nothing was rejected.** The critic also independently verified, and found correct, the 47 call
sites and per-class counts, the doc-path analysis and the rejected-shim reasoning, the absence of
any static-web-assets fingerprinting risk, the `type="module"` scope analysis, the
indentation-dependency diagnosis, the gate thresholds, and that no active ADR is contradicted —
in particular that `sw.js`'s fetch-handler ordering is untouched, so ADR 0002/0023 and
`ServiceWorker_BypassesApiBeforeCaching` are unaffected.

---

## Implementation notes — where the build diverged from the plan

Executed 2026-08-19 on `feature/frontend-es-modules` from `main @ 665acde` (the `planned-against`
ref, no drift). Baseline before any edit: `Failed: 0, Passed: 625`.

**Mechanics.** The move was done by a generator script (kept in the session scratchpad, not the repo)
that sliced line ranges out of `app.js` verbatim and de-indented by two, so the moved text is
byte-identical rather than retyped. `export` was added only where another module actually imports the
name, computed from a cross-module identifier scan over comment/string/regex-stripped source. Three
verification passes ran outside the .NET suite: ESLint (`no-undef`, `no-unused-vars`,
`no-import-assign`) over the module graph; a named-import-vs-export resolver; and the step-8 multiset
reconciliation.

### Deliberate deviations

1. **`hydrateFromCache` lives in `main.js`, not `cache.js`.** The plan gave `cache.js` lines
   3514–3584, which include it — but it reads `conversations`/`agents` and calls
   `renderSidebar`/`renderAgents`, so keeping it there would have made a *leaf* import `sidebar.js`
   and `agents.js`, breaking the plan's own asserted "leaves import nothing from the app" rule. It is
   startup/composition work, so it sits in the composition root. `cache.js` stays a true leaf.

2. **`initMcpOAuthReturn` lives in `mcp.js`, not `sharetarget.js`.** The plan's `sharetarget.js`
   range (3421–3513) swept it up by proximity. It is the MCP OAuth return, and matching the feature
   vocabulary is the point of the split.

3. **A fifth re-cut, in the spirit of the four the plan named.** Lines 1141–1201 —
   `setAgentMarkdown`, `addCopyButton`, `renderSuggestions`, `clearSuggestions` — sit under the
   *attachments* banner but are thread rendering, so they went to `thread.js`. The plan's
   `attachments.js` range (981–1202) would have swallowed them.

4. **28 modules, not "~29"** — 27 feature/leaf modules plus `main.js`. The plan's table listed 23
   feature modules; the difference is the four leaves/roots it described separately.

5. **Guard 4 is split across two files.** The plan put all four guards in `ProjectLayoutTests`, but
   its "`GET /js/main.js` serves as JavaScript" half needs the HTTP fixture. Both halves (served as
   JS **and** `index.html` carries `<script type="module" src="js/main.js">`) live in one test,
   `PwaAssetsTests.EntryPointModule_IsServed_AsJavaScript`, next to the
   `ServiceWorker_IsServed_AsJavaScript` the plan named as its model. The three filesystem guards are
   in `ProjectLayoutTests`, plus a fourth there — `TheMonolithicAppScript_IsGone`.

6. **A fifth guard was added, because building the split produced the bug it catches.**
   `EveryNamedImport_IsActuallyExportedByItsTargetModule`. A named import with no matching export is
   the one static error that yields a **blank** app rather than a degraded one, and nothing else sees
   it: there is no JS build step, and a per-file linter validates modules in isolation, never the
   edges between them. It caught two real cases — `main.js` imported `renderAgents` and `loadTools`,
   which the generator had not exported because no *generated* module needed them.

### Evidence

- **AC3 (inertness).** Zero unexplained differences in either direction: 2351 statements in `app.js`
  at `665acde` vs 2510 in the module graph, every difference in an enumerated category (113 imports,
  2 IIFE-wrapper lines, 215 `export` prefixes, 24 `wire*()`/setter headers, 16 `wire*()` calls, 8
  setter bodies, 11 write→setter substitutions). Table reproduced in ADR 0041.
- **AC4/5/6 negative controls, all run by hand and all failing as intended:** dropping `wireVoice();`
  from `main.js`; adding a stale `/js/gone.js` to `SHELL`; removing a live entry from `SHELL`;
  dropping an orphan `orphan.js` into `wwwroot/js/`; downgrading the entry `<script>` to a classic tag.

### AC8 is NOT fully discharged — manual browser smoke still outstanding

Step 10 could not be completed in this session: the Chrome extension was not connected, so no browser
drove the app. What *was* verified headlessly, against a running DevHost: all 28 modules return 200
with a JavaScript content type, and every named import resolves to a real export (the failure that
would blank the app). What remains for a human, in DevHost or the installed PWA:

- hands-free voice (ADR 0037 — listen, speak, barge-in) and dictation (ADR 0006);
- Preferences tab order, including the MCP-OAuth deep link to `profileTabs.select(3)`;
- attachment upload, the incognito toggle, the drawer on a narrow viewport;
- **an offline reload of the installed PWA** — the only way to catch a `SHELL` mistake, though the
  bidirectional guard now makes one much harder to introduce.

The speech items could never have been automated (ADR 0037 accepted that); the rest could have been,
and were not. This is flagged in the MR rather than papered over.
