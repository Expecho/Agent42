---
id: 0041
date: 2026-08-19
status: accepted
tags: [refactor, structure, frontend, pwa, tooling]
supersedes: []
amends: []
extends: [0039, 0040]
---

# `app.js` split into native ES feature modules

## Context
ADR 0039 reorganised `Agent42.Agents` into feature folders; ADR 0040 did the same for
`Agent42.Backend` and split `MapAgentApi` into per-feature partials. Both explicitly left the
frontend alone. This is that instalment.

The single frontend file (`app.js`, now deleted, under the Backend wwwroot) was **3616 lines / 166 KB in one IIFE** — roughly 25 ADRs'
worth of frontend on one flat file, carrying 35 module-level `let`s and **24 hand-drawn section
banners**. The banners are the tell: the author had been manually simulating modules for a while, and
the next feature made it worse, not better. Finding the MCP OAuth return, or the incognito document
gate, meant knowing a line range.

## Decision

### 1. Twenty-eight native ES modules under `wwwroot/js/`, no build step
`app.js` is **deleted**. Its contents live in `js/` beside it, loaded by a single
`<script type="module" src="js/main.js">`. The module names reuse the ADR 0039/0040 feature
vocabulary, so `src/Agent42.Backend/wwwroot/js/mcp.js` pairs visibly with `src/Agent42.Backend/Mcp/` and
`src/Agent42.Agents/Mcp/`.

**Native ES modules precisely because they need no build step.** The vanilla-JS/no-SPA property of
this frontend is deliberate (CLAUDE.md) and had to survive the refactor intact. A bundler was
considered and rejected: it would buy tree-shaking and one request, and cost the property that makes
this frontend editable without a toolchain. Both hosts serve `wwwroot` with plain `UseStaticFiles()`
— not `MapStaticAssets()` — so there is no fingerprinting or import-map rewriting to break, and
`container-app.bicep` sets `transport: 'auto'`, so prod is HTTP/2 and ~28 multiplexed subresources
cost nothing meaningful.

`marked`/`purify` stay **classic** `<script>` tags. Classic scripts execute before a deferred module,
so `marked.setOptions` in `src/Agent42.Backend/wwwroot/js/format.js` is safe and both remain window globals.

### 2. There is deliberately NO flat-namespace analogue here
A reader arriving from ADR 0039 will look for one, so: **the Cosmos `$type` hazard that forces flat
namespaces in `Agent42.Agents` has no frontend counterpart.** ES module specifiers are *file paths
resolved by the browser at load time* and are **never persisted anywhere**. Renaming or moving a
module changes nothing at rest. Folders and namespaces cannot disagree here because there are no
namespaces. The two refactors look alike and rest on completely different constraints.

### 3. State ownership is per-module `export let`, NOT a shared `state.js`
Each of the 35 module-level `let`s moves into the module that **writes** it — the write-sites already
showed clean per-feature ownership (`streaming` written only by `send`, `profile` only by `profile`,
`toolCatalog` only by `tools`, and so on; four are section-local and simply stay private).

Cross-module *reads* use the ES **live binding**, so a read site is **syntactically unchanged**. Only
the handful of cross-module *writes* go through a small exported setter (`setActiveId`,
`setChatTools`, `setConversations`, `setAgents`, `setPendingAgentId`, `setStopSpeech`, `setVoice`, …)
— an `import`ed binding is read-only, so a write must go through its owner.

A first draft used a shared `state.js`. Rejecting it bought two things:

- **Six existing assertions survive byte-identical.** `PwaAssetsTests` (`cachePut("conversations",
  conversations)`), `ImageGenerationAssetsTests` and `ModelPickerAssetsTests` embed **bare state
  identifiers** at read sites. A `state.` prefix would have broken all six, forcing test edits
  mid-refactor — exactly the pressure that gets assertions loosened rather than fixed.
- **An evaluation-order hazard never arises.** `const expandedAgents = loadExpandedAgents();` runs at
  *evaluation* time and calls a function declared ~220 lines later. A shared state module holding
  `expandedAgents` would have to import `sidebar.js` and call into it during its own evaluation,
  breaking the leaf-imports-nothing rule. Under per-module ownership both sit in `sidebar.js` and
  hoisting keeps working exactly as today.

### 4. `main.js` is the composition root, and that is a guarded contract
ADR 0040 found that an endpoint group which exists but is never called from `MapAgentApi` compiles
cleanly and **silently deletes every endpoint in it**. The frontend has the exact analogue: a module
whose `wire*()` is never called from `main.js` loads fine, throws nothing, and silently attaches no
listeners — that feature's buttons just do nothing. No build, lint or type check sees it.

So each feature module exports a `wire<Feature>()` (or `init<Feature>()`) holding its listener
registrations, `main.js` is imported by nobody and is their only caller, and
`ProjectLayoutTests.FrontEndModule_ExportsItsEntryPoint_AndIsCalledFromTheCompositionRoot` asserts
both halves from an **explicit name table** — not a directory scan, which would pass trivially if a
module dropped its `wire*()` and registered listeners at top level again. Same reasoning as that
file's existing folder-set assertion.

### 5. The dependency rule, asserted rather than hoped for
- **Leaves** (`format.js`, `tabs.js`, `cache.js`) import nothing from the app.
- `dom.js` imports **only** `tabs.js`, never the reverse — `profileTabs`/`agentTabs` are built by
  calling `wireTabs` at evaluation time, so `tabs.js` must be fully evaluated first.
- Feature modules import leaves + `dom.js` freely, and each other only for **runtime calls and live
  read-bindings**, never for values consumed during module evaluation. Residual cycles are then
  harmless, and there are several (`send` ↔ `thread`, `models` ↔ `providers`, `profile` ↔ `theme`).
- `send.js` ↔ `voice.js` is a genuine cycle, and **the seam that breaks it already existed**:
  `voice`, `stopSpeech` and `sendAbort` were forward-declared mutable slots for exactly this reason.
  They are now `export let` on `send.js`, assigned by `voice.js`/`speech.js` through setters, and
  `send.js` only ever *reads* them at call time. Do not "clean this up" into direct imports.

### 6. Construct-then-flip, not move-incrementally
An IIFE cannot be half-split: the moment a section leaves `app.js`, the 47 source-text assertions
that grep it go red, and no test helper can discover `js/*.js` over HTTP before `main.js` exists
(`UseStaticFiles()` has no directory listing). So the modules were **built alongside an untouched
`app.js`** — suite green throughout, because `app.js` still held everything — and `index.html`,
`sw.js`, the fixture, the 47 call sites and the deletion flipped **together**. There was no red
window and no `/app.js` fallback hack in the fixture.

### 7. The test seam: walk the import graph over HTTP
`StaticAssetsServerFixture.GetAppScriptAsync()` starts at the served `main.js`, regex-extracts relative
specifiers, fetches transitively with a visited-set, and concatenates. Fetching **over HTTP** (rather
than reading `wwwroot` off disk) preserves the original meaning of these assertions — that the code is
actually *served*. Walking the **import graph** rather than the directory means an unreferenced file
is correctly excluded from the corpus rather than silently propping an assertion up; the orphan
itself is caught separately.

The specifier regex handles **all four** forms (`import … from`, bare `import`, `export … from`,
`export * from`) — miss the side-effect form and a module drops out of the corpus *and* is reported
as an orphan.

A fifth guard was added beyond the plan's four, because building this split produced the bug it
catches: `EveryNamedImport_IsActuallyExportedByItsTargetModule`. A named import with no matching
export is the one static error that yields a **blank** app rather than a degraded one — the browser
throws `SyntaxError: does not provide an export named X`, module evaluation aborts, and nothing wires.
No build step exists to catch it, and a per-file linter validates each module in isolation, never the
edges between them. It caught two real cases (`renderAgents`, `loadTools`) during this refactor.

The 5×-duplicated `Body()` depth-counting helper moved onto the fixture in the same pass, and
`ModelPickerAssetsTests` now uses it instead of `IndexOf("\n  }")`: that two-space indent *was* the
IIFE body's indentation, and at module scope bodies close at column 0, so the old scan would run past
its end. Net duplication goes **down**.

### 8. Deleting `app.js` breaks a CI gate, deliberately
`.agentic/scripts/check-doc-paths.mjs` exit-1s on any backticked repo path in
`CLAUDE.md`/`gotchas.md`/`decisions/*.md` that does not exist, so all nine full-path
`app.js` references failed the moment the file was gone. Keeping `app.js`
as a one-line `import "./js/main.js"` shim was considered and **rejected**: it satisfies the
mechanical check while pointing every doc reference at a stub containing none of the claimed content
— worse than the drift it hides. The references were repointed at the specific module instead, which
is a genuine improvement (`.agentic/gotchas.md` line 51 now names `src/Agent42.Backend/wwwroot/js/speech.js`, not "somewhere in 3616 lines").

## Consequences

- **Every MR that touches `wwwroot/js/` must bump `CACHE` in `sw.js`.** This used to be a forgiving
  convention. Subresources are served cache-first, so the window between a network-first
  `index.html` and the new SW activating can serve a **version-skewed module graph**, and a
  cross-module export rename then throws `SyntaxError: does not provide an export named X` — a blank
  app, not a degraded one. Bumping is necessary but does not close the window. Recorded as a gotcha.
- **`SHELL` is now a bidirectional hazard.** It went from 10 entries to 37, and `cache.addAll` is
  all-or-nothing: a *missing* entry breaks the PWA offline-only (invisible in tests, DevHost and
  prod-online), while a *stale* entry makes the SW never activate at all — taking down ADR 0032's
  offline cache, ADR 0023's share target and ADR 0028's push. The guard asserts SHELL **equals** the
  directory. Recorded as a gotcha.
- **A module must be reachable from `main.js` to be covered by the 47 assertions at all.** Recorded
  as a gotcha.
- **No behaviour change, and the evidence is below.** No endpoint, grain, contract, Bicep, CI or
  dependency changed. `styles.css` and `index.html`'s markup are untouched (only the script tag and
  the one comment whose stated invariant `type="module"` invalidates).
- Load ordering changed in one respect: a deferred module executes after DOM parse rather than at the
  end of `<body>`. Verified inert here — no inline `onclick=`/`onchange=` handlers in `index.html`,
  no top-level `this`, no `document.currentScript`, no CSP header in `Program.cs` or `infra/`, and
  `"use strict"` is implicit in a module.

### Inertness evidence (AC3)
The statement multiset of `app.js` at `665acde` (2351 statements — non-blank, non-brace-only,
comments and string/regex literals stripped) was reconciled against the concatenated module graph
(2510). **Zero unexplained differences in either direction.** Every difference falls into an
enumerated category:

| Count | Difference |
|------:|------------|
| 113 | `import` statements linking the graph |
| 2 | the IIFE wrapper and `"use strict"`, dropped |
| 215 | `export ` prefixed onto an otherwise-unchanged declaration |
| 24 | `wire*()` / setter function headers |
| 16 | `wire*()` calls from the composition root |
| 8 | setter bodies (`activeId = value;` &c.) |
| 11 | cross-module writes that became setter calls |

No expression was edited: 2351 of 2351 original statements are present verbatim.

## Alternatives considered

- **A bundler (esbuild/rollup/vite).** Rejected: buys tree-shaking and one request, costs the
  no-build-step property that is the deliberate design of this frontend.
- **In-place reorganisation** (keep one file, tidy the banners). Rejected by the maintainer: it is
  what the 24 banners already are, and it does not stop the file growing.
- **Classic scripts + a namespace object** (`window.Agent42.send = …`). Rejected: reintroduces load-order
  fragility that `type="module"` solves for free, and pollutes the global scope the IIFE existed to avoid.
- **A shared `state.js`.** Rejected — see decision 3.
- **Keeping `app.js` as a re-export shim** so doc paths resolve. Rejected — see decision 8.

## Out of scope
`styles.css` (2234 lines) and `index.html`'s markup (802 lines) have the same accumulated-features
problem and are explicitly deferred to separate MRs. Reorganising `tests/Agent42.Tests` (81 flat
files) remains the follow-up ADR 0040 already named. `src/Agent42.Backend/wwwroot/shared/viewer.js` is a separate
self-contained public viewer (ADR 0035) and is not part of the app shell.
