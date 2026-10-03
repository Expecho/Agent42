# Plan — Rich inline rendering (Mermaid, code highlighting + copy, KaTeX math, JSON charts)

planned-against: main @ 8b91841

## Overview

**Goal:** assistant replies render richer than plain sanitized markdown, in the chat app **and**
in the public share viewer:

1. ` ```mermaid ` fenced blocks → an SVG diagram.
2. Every fenced code block → syntax highlighting plus a per-block **copy** button.
3. `$…$`, `$$…$$`, `\(…\)`, `\[…\]` → typeset math (KaTeX).
4. ` ```chart ` fenced blocks holding a JSON Chart.js config → a chart.

**Why now:** this is phase 1 of an "Agent42 canvas" effort. The user wants the Claude
Artifacts / ChatGPT Canvas experience. Phase 1 handles everything that can render inline from the
reply text alone. Phase 2 (`plan-artifact-pane.md`) adds a model-driven side pane and **depends on
this plan**: it reuses the shared renderer and the vendored highlight/markdown pipeline from here.

**Shape:**

```
reply text ──marked (+ math extension)──► DOMPurify ──► .prose innerHTML      (every stream chunk, unchanged)
                                                           │
                                       on FINALIZE only ───┘
                                                           ▼
                         Agent42Rich.enhance(proseEl)   (/shared/lib/richtext.js, classic script)
                           ├─ pre>code            → highlight.js + copy button       (eager lib)
                           ├─ code.language-mermaid → lazy-load mermaid → sanitized SVG
                           ├─ code.language-chart   → lazy-load Chart.js → <canvas>
                           └─ .math-inline/.math-block → lazy-load KaTeX → typeset
```

## Affected areas

Verified against the tree at the base commit.

- `src/Agent42.Backend/wwwroot/shared/lib/`: **new** vendored libs plus the new shared renderer
  `richtext.js`. The renderer and the heavy libs live **only** here (see Context, decision D1).
- `src/Agent42.Backend/wwwroot/js/format.js`: registers the math marked-extension.
- `src/Agent42.Backend/wwwroot/js/thread.js` (`setAgentMarkdown`): calls `enhance` **inside itself
  when `caret` is false**. Every non-streaming caller is then covered without per-site wiring:
  - `conversations.js:86` `renderTurns` (history and offline cache);
  - `send.js:75` `recoverPersistedReply` (ADR 0073);
  - `send.js:251` (stream end);
  - `send.js:277` (abort/barge-in partial);
  - `send.js:287` (error text, which is harmless to enhance).

  Every per-chunk call passes `caret: true` (`send.js:154/207/234`). That 1:1 is what the design
  relies on, and the asset test pins it.
- `src/Agent42.Backend/wwwroot/index.html`: classic `<script>` tags for highlight.js + richtext.js,
  plus the highlight theme and KaTeX CSS links. They sit next to the marked/purify tags at ~L1016–1018.
- `src/Agent42.Backend/wwwroot/shared/index.html`, `src/Agent42.Backend/wwwroot/shared/viewer.js`: the same wiring
  for the public viewer (`renderMarkdown` at ~L19–20, turn render at ~L120).
- `src/Agent42.Backend/wwwroot/styles.css` + `src/Agent42.Backend/wwwroot/shared/viewer.css`: code-block copy button,
  diagram/chart/math containers, error note. viewer.css is a deliberate visual copy (its L4 comment).
- `src/Agent42.Backend/wwwroot/sw.js`: `CACHE` bump (rule 3). SHELL gets **only** the eager
  entries (see D3).
- `src/Agent42.Agents/Conversations/AgentGrain.cs` (or the partial that holds `WithDocumentAwareness`):
  appends a short **rendering hint** to the instructions so models know the `chart` format (see D5).
- `tools/Agent42.DevHost/CannedChatClient.cs`: a trigger that emits a canned reply containing every
  rich block, so the feature can be exercised offline.
- Tests: `tests/Agent42.Tests/ShareLinkAssetsTests.cs`, `tests/Agent42.Tests/PwaAssetsTests.cs`,
  a new `tests/Agent42.Tests/RichTextAssetsTests.cs`, and the instruction test next to the existing
  `WithDocumentAwareness` tests in `tests/Agent42.Tests/AgentGrainTests.cs`.

## Context & constraints

**Investigation findings**

- The markdown is re-parsed on **every stream chunk** (`send.js` textDelta → `setAgentMarkdown` →
  `prose.innerHTML = renderMarkdown(text)`, `thread.js:90–97`). Anything heavy that runs inside
  `renderMarkdown` runs per token and flickers, and a half-streamed mermaid/JSON block is invalid
  anyway. **So all enhancement runs on finalize only.** While streaming, users see the plain code
  block, which is acceptable.
- The share viewer (`shared/viewer.js`) is a **self-contained classic IIFE**. It must never load the
  authenticated `/lib/` (`ShareLinkAssetsTests.Viewer_IsSelfContained_NoAuthenticatedAssets`,
  `EasyAuthExclusionTests` ~L235–266). `/shared` and `/shared/*` are Easy Auth `excludedPaths`
  (`infra/modules/container-app.bicep` ~L365–372), so anything under `/shared/lib/` is public and
  loadable by **both** pages.
- The SW (`sw.js` ~L75–110) stale-while-revalidates **every** same-origin non-`/api` GET into the
  shell cache. A lazy-loaded lib is therefore cached at runtime on first use without being in SHELL.
- No CSP exists on the app or the viewer, so lazy `<script>` injection from the same origin works.
- Vendored libs carry their version only in the minified header (`marked v15.0.12`,
  `DOMPurify 3.2.6`). There is no package.json and no JS test runner (by design). The frontend is
  guarded by **source-text asset tests** fetched over HTTP from `StaticAssetsServerFixture`.
- A per-message copy button already exists (`thread.js` `addCopyButton`, ~L100–124). Copy its glyph
  and feedback idiom (`⧉` → `✓`, revert after 1.5 s, swallow clipboard errors) for the per-block
  button. Don't invent a second style.
- Persona chats replace the base `Instructions` const entirely (`AgentGrain.BuildAgent.cs` ~L284:
  `WithDocumentAwareness(_state.State.SystemPrompt ?? Instructions)`). A hint placed in the
  `Instructions` const would never reach persona or agent chats. It must be **appended** the way
  `WithDocumentAwareness` appends.

**Decisions (user + plan)**

- **User scope call:** all four renderers (mermaid, highlight + copy, KaTeX, JSON charts).
- **D1: one renderer, one copy of each heavy lib, both under `/shared/lib/`.** `richtext.js` is a
  **classic script** exposing `window.Agent42Rich`. It is loaded by the chat app (`index.html`) *and*
  the viewer from `/shared/lib/`, the same way marked/purify are classic globals today (index.html's
  L1016 comment). Rejected: an ES module under `js/` plus a copy in the viewer, because two copies of
  ~400 lines of renderer logic would drift. Also rejected: duplicating a ~3 MB mermaid bundle into
  both `lib/` and `shared/lib/`. marked/purify stay duplicated as they are today, since migrating
  them is out of scope. The chat app loading a public `/shared/lib/` path is new, so ADR it (step 8).
  That direction is safe: the app is authenticated and a public asset leaks nothing. The reverse
  direction is the one the tests forbid.
- **D2: highlight.js is eager; mermaid, KaTeX and Chart.js are lazy.** highlight.js (common-languages
  build, ~120 KB) runs on almost every code reply. The other three are loaded by
  `Agent42Rich.loadScript`/`loadStyle` (memoized promises) only when a matching element exists.
- **D3: SHELL lists only the eager additions** (`/shared/lib/richtext.js`,
  `/shared/lib/highlight.min.js` and its theme CSS). Lazy libs reach the cache at runtime through
  stale-while-revalidate. Putting a 3 MB mermaid into the all-or-nothing `addAll`
  (gotcha "SHELL is all-or-nothing") would slow install and add a failure point. Accepted residual:
  a diagram that was never rendered online shows as a code block offline.
- **D4: security posture.** Everything stays sanitized:
  - mermaid uses `securityLevel: "strict"`, and its SVG output goes through
    `DOMPurify.sanitize(svg, { USE_PROFILES: { svg: true, svgFilters: true } })` before insertion. If
    that strips node labels (mermaid's `foreignObject` HTML labels), set `htmlLabels: false` in the
    mermaid config. Do **not** widen the DOMPurify profile.
  - Charts take `JSON.parse` only. A JSON config cannot carry functions, so no callbacks run. Cap the
    datapoints (e.g. ≤ 5 000 total) and the source size.
  - KaTeX uses `trust: false` and `throwOnError: false`. The marked math extension emits a placeholder
    (`<span class="math-inline" data-tex="…">` / `<div class="math-block" data-tex="…">`) with the
    escaped TeX as text fallback, which DOMPurify keeps (data-* attributes are allowed by the html profile).
  - Every renderer failure **leaves the original code block** and adds a small
    `.rich-error` note. Never throw out of `enhance`: one bad block must not stop the rest.
  - There is **no new egress**: every lib is vendored and same-origin, and nothing calls a CDN.
- **D5: the model is told about ` ```chart `** through an appended, always-on rendering hint (a few
  lines: mermaid and LaTeX are supported, and ` ```chart ` takes a Chart.js JSON config with no
  functions). Mermaid/LaTeX need no teaching, but `chart` is our own convention. The hint also reaches
  `/api/agent-run` and MCP-server consumers (ADR 0022/0070). They then see raw JSON fences, which are
  readable and acceptable. Keep the hint short, since it costs tokens every turn.
- **D6: math delimiters, chosen against false positives.** Inline `$x$` matches only when the char
  after the opening `$` and before the closing `$` is non-whitespace, and the closing `$` is not
  followed by a digit (so "$5 and $10" stays text). `$$…$$`, `\(…\)`, `\[…\]` always count.
  Implementation is a **pair** of marked extensions:
  - a **block** extension (`level: "block"`) for `$$…$$` and `\[…\]`, which may span lines and blank
    lines (an inline-only extension would split them at paragraph boundaries);
  - an **inline** extension for `$…$` and `\(…\)`.

  Both need a `start()` function. Without one, marked's text tokenizer runs past the delimiter and its
  `escape` tokenizer eats `\(`/`\[`. Math inside code spans or fences is never touched, because those
  tokenizers win first.
- **D8: the app must survive a missing renderer.** `format.js` and `viewer.js` guard
  `typeof Agent42Rich !== "undefined"` before `marked.use(...)`, and `setAgentMarkdown` calls
  `Agent42Rich?.enhance`. A 404 or a skewed cache on `richtext.js` must degrade to today's plain
  rendering, never to the blank app that the PwaAssetsTests comment (~L249–254) warns about.
- **D3 caveat:** stale-while-revalidate is cache-first, so an updated lazy lib is served stale for one
  load after a lib upgrade. That is acceptable. A lib upgrade that changes the renderer's API must
  rename the file (e.g. a version suffix) rather than rely on revalidation.
- **D7: theme.** Mermaid renders with `theme: "dark"` or `"default"` from the resolved app theme
  (`js/theme.js`) at render time. The viewer follows `prefers-color-scheme`. Re-rendering on a live
  theme toggle is out of scope.
- **Versions are resolved, never recalled (hard rule 4).** Get each lib's latest stable with
  `npm view <pkg> version` (packages: `@highlightjs/cdn-assets`, `mermaid`, `katex`, `chart.js`),
  fetch with `npm pack <pkg>@<ver>` into the scratchpad, and copy the dist files
  (`highlight.min.js` + one light and one dark theme CSS, `mermaid.min.js`, `katex.min.js` +
  `katex.min.css` + `fonts/`, `chart.umd.js`). Leave the license headers intact. Record the resolved
  versions in the ADR.

**Governing ADRs / gotchas:** ADR 0035 (share links), ADR 0041 (ES modules, classic marked/purify),
ADR 0042 (excludedPaths), ADR 0032 (offline cache). Gotchas: "SHELL is all-or-nothing", "Every MR
that touches `wwwroot/js/` must bump `CACHE`", "A frontend module unreachable from `main.js` is
invisible" (its `wwwroot/shared/` paragraph applies: richtext.js needs its **own** asset test that
fetches from the fixture client and never uses `GetAppScriptAsync()`), "Service worker must stay
network-first".

## Steps

1. **Vendor the libs** into `wwwroot/shared/lib/` (`highlight/`, `mermaid/`, `katex/` incl. fonts,
   `chart/`), with versions resolved as above. *Verify:* `ShareLinkAssetsTests` gets a test that GETs
   each file through the fixture client (200, non-empty). Run `dotnet test --filter ShareLinkAssetsTests`.
2. **Write `shared/lib/richtext.js`** (classic IIFE → `window.Agent42Rich`). Exports:
   `mathExtensions` (block + inline marked extensions per D6), `enhance(rootEl, { theme })` (idempotent: marks the
   processed blocks with `data-rich="1"`), `loadScript`/`loadStyle` (memoized, absolute
   `/shared/lib/...` paths), and the per-block copy button (copies `code.textContent`, the raw
   source). Keep each function's cognitive complexity small. There is no analyzer for JS, but the
   same discipline applies. *Verify:* `RichTextAssetsTests` (new) asserts from source text that:
   mermaid uses `securityLevel: "strict"`; mermaid output passes `DOMPurify.sanitize` with the svg
   profile; KaTeX uses `trust: false`; chart uses `JSON.parse` and never `eval`/`new Function`; no
   `http://`/`https://` URL appears (no CDN); and `enhance` has a per-block try/catch.
3. **Wire the chat app.**
   - `format.js` runs `marked.use({ extensions: Agent42Rich.mathExtensions })`, guarded per D8.
   - `index.html` adds the classic scripts after purify and before the module.
   - `setAgentMarkdown` (`thread.js`) calls `Agent42Rich?.enhance(prose, …)` only when `caret` is
     false. That covers all five final call sites listed in Affected areas.
   - Add CSS. Bump `CACHE`, and add the D3 SHELL entries.

   *Verify:*
   - `PwaAssetsTests.ServiceWorker_CacheNameIsCurrent` is updated to the new value.
   - `PwaAssetsTests.ServiceWorkerShell_OmitsLazyLibs`: SHELL lists `richtext.js` and highlight, and
     does **not** list mermaid, katex or chart.
   - New `RichTextAssetsTests` assertions over `GetAppScriptAsync()`:
     - slicing `setAgentMarkdown` with `StaticAssetsServerFixture.Body` shows `enhance` gated on
       `!caret`;
     - every `setAgentMarkdown(` call that passes `caret: true` is inside the stream loop;
     - `format.js` guards the global;
     - `index.html` loads `/shared/lib/richtext.js` after purify.
   - `ProjectLayoutTests.ServiceWorkerShell_ListsExactlyTheModuleDirectory` still passes (no `/js/`
     change in SHELL).
4. **Wire the share viewer.** `shared/index.html` adds the same classic scripts and CSS with
   **relative** paths (`lib/richtext.js`). An absolute `/shared/lib/…` in the page contains `/lib/`
   and fails `Viewer_IsSelfContained_NoAuthenticatedAssets`. `viewer.js` registers the math
   extensions (guarded per D8) and calls `enhance` after each turn render. Mirror the CSS into
   `viewer.css`. *Verify:* `ShareLinkAssetsTests.Viewer_IsSelfContained_NoAuthenticatedAssets` still
   passes, plus a new assertion that viewer.js calls `Agent42Rich.enhance` and the page loads
   `lib/richtext.js` relative to `/shared/`.
5. **Rendering hint (D5).** Add `WithRenderingHints` in a partial next to `WithDocumentAwareness`
   (`src/Agent42.Agents/Documents/AgentGrain.Documents.cs:69`). Apply it at the single call site
   `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs:284`, wrapping the
   `SystemPrompt ?? Instructions` result, so both paths get it. *Verify:* two grain tests, one with a
   persona system prompt and one without, each asserting that the bound instructions contain the
   chart hint. Copy the "bound persona reaches the model" testing approach (gotcha "Testing that a
   bound persona reaches the model needs the fake to echo its instructions"). The existing `[sys:`
   tests use `Contains`, so they keep passing. Run `dotnet test --filter <names>`.
6. **DevHost trigger.** This is a **text** reply, not a tool call, so it does **not** go in
   `SelectToolCall`. Add a `RichMarker = "show rich"` branch to the reply selection in both
   `GetStreamingResponseAsync` (~L83–87) and `GetResponseAsync` (~L43–47), shaped like the
   `ReasoningMarker` gating (~L69–79). Make the canned reply a **separate plain `const string`**:
   `BuildReply` is a `$$"""` interpolated raw string, and chart JSON braces would collide with its
   interpolation. The reply contains:
   - a mermaid flowchart;
   - a C# and a JS code block, one of them containing a ```` ``` ```` inside a string;
   - inline and multi-line block math, with blank lines inside `$$…$$`;
   - `\(…\)`;
   - a currency sentence ("$5 and $10");
   - a bar chart;
   - one deliberately invalid mermaid block.

   The stream re-chunks on `Split(' ')`. That round-trips faithfully, but confirm that newlines
   survive in the browser. *Verify:* build, then the browser check in step 7.
7. **Browser verification (manual, required; there is no JS runner).** Run DevHost, send "show
   rich", and confirm in light and dark theme: the diagram renders; the invalid diagram shows code +
   error note while everything else renders; highlighting and per-block copy work (clipboard
   contents equal the raw source); math renders; the currency sentence stays text; the chart renders;
   streaming shows plain blocks and then enhances once on completion; reloading the chat (history
   path) renders identically. Stopping a reply mid-stream renders the partial reply enhanced. Mermaid
   diagrams keep their styling after the svg-profile sanitize (mermaid's `<style>` element must
   survive). With `/shared/lib/richtext.js` blocked in DevTools, the app still loads and renders plain
   markdown (D8). Then create a share link and confirm the same in the viewer (logged
   out / private window). Check the console for errors. Capture a GIF.
8. **ADR + docs.** `/decide` a new ADR covering D1–D7 and the resolved lib versions. Add a gotcha
   for "enhance on finalize only, never per chunk" and "heavy libs are lazy and NOT in SHELL".
   Update the PWA/UI row of CLAUDE.md's feature map with the new ADR number. Run `/sync-docs`.

Steps 1→2→(3 ∥ 4)→7. Step 5 and step 6 are independent of 1–4 and parallelizable.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | Vendored libs are served from `/shared/lib/` (public) | `SharedLibs_AreServed` | `tests/Agent42.Tests/ShareLinkAssetsTests.cs` |
| 2 | Mermaid is strict and its SVG is DOMPurify-sanitized | `Mermaid_IsStrictAndSanitized` | `tests/Agent42.Tests/RichTextAssetsTests.cs` |
| 3 | KaTeX `trust:false`; charts `JSON.parse`, no eval/Function | `Math_And_Chart_AreSafe` | `RichTextAssetsTests.cs` |
| 4 | No external URL in the renderer (no new egress) | `Renderer_HasNoExternalUrls` | `RichTextAssetsTests.cs` |
| 5 | A failing block doesn't break the others (per-block try/catch) | `Enhance_IsolatesBlockFailures` (source text) + manual step 7 | `RichTextAssetsTests.cs` |
| 6 | Enhancement only on finalize (all non-caret callers), never per stream chunk | `Enhance_RunsOnFinalizeOnly` | `RichTextAssetsTests.cs` |
| 6b | App survives a missing renderer (global guarded) | `Renderer_IsOptional` (source text) + manual step 7 | `RichTextAssetsTests.cs` |
| 6c | Math extensions: block + inline, both with `start()`; currency regex rejects digit after the closing `$` | `MathExtensions_AreBlockAndInlineWithStart` (source text) + manual step 7 | `RichTextAssetsTests.cs` |
| 7 | Viewer renders rich content and stays self-contained | `Viewer_UsesSharedRenderer` + existing `Viewer_IsSelfContained_NoAuthenticatedAssets` | `ShareLinkAssetsTests.cs` |
| 8 | SW cache bumped; SHELL has eager entries only, not mermaid/katex/chart | `ServiceWorker_CacheNameIsCurrent` (updated) + `ServiceWorkerShell_OmitsLazyLibs` | `tests/Agent42.Tests/PwaAssetsTests.cs` |
| 9 | Chart hint reaches persona and base chats | `RenderingHint_ReachesPersonaChat`, `RenderingHint_ReachesBaseChat` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| 10 | Visual/behavioural correctness (diagram, copy, math, currency, chart, viewer) | manual browser check, step 7 | — |

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time:

| Gate | Threshold | Tool |
|---|---|---|
| New-code coverage | ≥ 80 % | `node .agentic/scripts/check-new-code-coverage.mjs` (C# only; the JS is guarded by asset tests) |
| Cognitive complexity | ≤ 15 (build error) | SonarAnalyzer S3776 via `dotnet build` |
| Vulnerable packages | 0 | CI package-freshness job (no NuGet change expected) |
| Format | clean | `dotnet format Agent42.slnx --verify-no-changes` |
| Branch | not `main` | `feature/rich-inline-rendering` |

## Out of scope

- Phase 2's artifact pane (`plan-artifact-pane.md`) and HTML/SVG execution of any kind.
- Re-rendering on a live theme toggle, and rendering while streaming.
- Migrating marked/purify into `/shared/lib/` only (they stay duplicated).
- Other mermaid-like formats (PlantUML, Graphviz), and interactive charts beyond Chart.js defaults.
- Offline rendering of a diagram/chart/math that was never rendered online.

## Review

Critic pass on 2026-10-03 (fresh context). All findings were accepted. None were rejected.

- **HIGH, finalize call sites wrong and incomplete.** Two problems: `send.js:75` is stream
  recovery, not history, and `conversations.js:86` and `send.js:277` were missed. Fixed: `enhance`
  now lives inside `setAgentMarkdown` when `!caret`, so all five non-streaming callers are covered,
  and the asset test pins the caret↔stream relationship.
- **M2, a top-level dependency on a global could blank the app.** Fixed: D8 guard plus a manual
  "blocked richtext.js" check.
- **M3, DevHost trigger pointed at the tool-call branch set.** Fixed: text-reply selection in both
  response methods, and a separate constant because of `$$"""` brace interpolation.
- **M4, AC gaps.** Fixed: base-chat hint test, `ServiceWorkerShell_OmitsLazyLibs` added to step 3,
  math source-text AC.
- **M5, the math extension was underspecified.** Fixed: a block + inline pair, both with `start()`.
- **LOW items.** Fixed: hint file paths made exact; the SWR stale-for-one-load caveat added to D3;
  relative paths in the viewer page; a mermaid `<style>` survival check added to step 7.

## Implementation divergences (2026-10-03)

- **Lib files.** `chart.umd.min.js` (not the unminified `chart.umd.js`), and KaTeX ships **woff2 fonts only**.
  Every browser we target picks woff2 first from `katex.min.css`'s `src` list. Versions resolved with `npm view`:
  highlight.js 11.12.0, mermaid 12.1.0, KaTeX 0.19.0, Chart.js 4.5.1.
- **`globalThis.Agent42Rich?.enhance`**, not the plan's `Agent42Rich?.enhance`. A bare reference to an undeclared
  global is a ReferenceError even with `?.`, which would defeat D8.
- **Highlight theme follows a live toggle.** Both themes ship as `<link data-hljs-theme>`. `richtext.js` enables the
  one matching `data-theme`, and a MutationObserver keeps them in sync. Diagrams and charts still keep the theme
  they were drawn in (D7 is unchanged for them).
- **Code wrapper.** Each `<pre>` is wrapped in `.rich-code`, which also holds the copy button. A rendered
  diagram or chart is inserted before the wrapper and **hides** it, so the source stays in the DOM. A failed
  one leaves it visible with the note.
- **Inline `$$…$$`.** The inline extension also takes a mid-sentence `$$…$$` (a display-mode span). The block
  `start()` reports only line-start delimiters, because marked passes it `src.slice(1)`.
- **`WithRenderingHints`** lives in its own partial, `Conversations/AgentGrain.RenderingHints.cs`, not in the
  Documents partial, because it isn't a documents concern. It is applied at the same single call site as planned.
- **Chart theming.** The browser check found that setting `Chart.defaults.borderColor` silently disables Chart.js's
  automatic palette, which turned the bars grey. The grid is themed through `Chart.defaults.scale.grid/border`
  instead (recorded as a gotcha).
- **`ToolResults_DoNotBlowTheSessionSizeBudget`** now excludes the bytes of the echoed hint (5,220 measured).
  `FakeChatClient` echoes its instructions into every stored reply, so the hint inflated that test's session
  without changing anything in production. The 256 KB ceiling is unchanged. The cache-version pins in
  `SearchAssetsTests`/`ResearchAssetsTests` moved to v44 together with `PwaAssetsTests`.
- **Step 7 ran headless**, not in the user's browser. Neither the Chrome extension nor BrowserOS was
  connected, so there is no GIF. The checks ran through Playwright on the system Edge against DevHost:
  - light and dark themes;
  - mid-stream state (caret present, nothing enhanced);
  - final render;
  - per-block copy (the clipboard holds the raw source, including the inner fence);
  - history reload;
  - a real Stop mid-stream (the partial reply was enhanced);
  - the share viewer in a fresh context (renders everything, and requests nothing outside `/shared` and
    `/api/share` besides the pre-existing Google Fonts);
  - `richtext.js` blocked (plain code blocks, no page errors, composer usable).

  Mermaid's `<style>` and its text labels survive the sanitize. A visual pass in a real browser is still
  recommended before merge.
- **Docs beyond the plan:** `docs/features/rich-rendering.md`, plus README bullets. `amended_by: 0083` was added
  to ADRs 0032/0035/0041, and the exemplar table gained a row for the shared classic script.
- **Critic findings, fixed (2026-10-03):**
  - **Mermaid `themeCSS` injection.** A `%%{init}%%` directive could put a page-global `url()`/`@import` into the
    SVG's `<style>`. The CSS keys are now in `secure`, and `neutralizeCss` strips non-fragment urls. Verified
    headless: the payload renders with no external URL and makes no request, and `url(#…)` markers are kept.
    `style`/`classDef` with `url(` already fail mermaid's own grammar. Pinned by `Mermaid_CannotInjectExternalCss`.
  - **Block math swallowing prose.** Block math could stretch past a mid-line `$$` and swallow later prose. Its
    content can no longer contain its own closing delimiter (verified headless).
  - **Highlighting** is size-capped.
  - **Rendered diagrams and charts** get their own copy button for the source.
