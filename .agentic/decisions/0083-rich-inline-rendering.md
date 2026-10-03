---
id: 0083
date: 2026-10-03
status: accepted
tags: [frontend, rendering, markdown, share-links, pwa, service-worker, security, vendored-libs, instructions]
supersedes: []
amends: [0032, 0035, 0041]
---

# Render diagrams, highlighted code, math and charts inline with one shared renderer under /shared/lib

## Context

As of 2026-10-03, assistant replies are sanitized markdown (marked + DOMPurify) and nothing more. Mermaid, LaTeX
and data the model would rather chart all show as raw text. This is phase 1 of the "Agent42 canvas" effort
(`.agentic/specs/completed/plan-rich-inline-rendering.md`; phase 2 is `plan-artifact-pane.md`). Four constraints shape it:

- markdown is re-parsed on **every stream chunk**;
- the public share viewer (ADR 0035) is a self-contained classic IIFE that must never load the authenticated `/lib/` or `/js/`;
- SHELL is an all-or-nothing `addAll` (ADR 0032);
- the frontend has no build step and no JS test runner (ADR 0041).

## Decision

1. **One renderer, one copy of each lib, both under the public `/shared/lib/`.** `richtext.js` is a classic
   script exposing `window.Agent42Rich` (`mathExtensions`, `enhance`, `loadScript`, `loadStyle`). The chat app
   loads it from `/shared/lib/` (amends ADR 0041), and the viewer loads it by relative `lib/` (amends ADR 0035).
   That direction is safe: the app is authenticated, and a public asset leaks nothing.
2. **Vendored versions** (resolved with `npm view` on 2026-10-03):
   - eager: highlight.js 11.12.0 (`@highlightjs/cdn-assets`, common build, github + github-dark themes);
   - lazy: mermaid 12.1.0 (`mermaid.min.js`, 5.3 MB), KaTeX 0.19.0 (woff2 fonts only) and Chart.js 4.5.1
     (`chart.umd.min.js`). These are loaded only when a matching element exists.

   An upgrade that changes a lib's API renames the file, because stale-while-revalidate serves the old copy
   for one load.
3. **Enhance on finalize only.** `setAgentMarkdown` calls `globalThis.Agent42Rich?.enhance(prose)` in its non-caret
   branch. Every streaming call passes `caret: true`, so stream end, stop, error, history and recovery are all
   covered with no per-site wiring. The marked math extensions only tokenize, into `.math-inline`/`.math-block`
   placeholders with the TeX in `data-tex`. They never typeset.
4. **SHELL lists only the eager part** (`richtext.js`, highlight.js and its two themes; amends ADR 0032). The lazy
   libs reach the cache at runtime through stale-while-revalidate. Accepted residual: a diagram, formula or chart
   that was never rendered online shows as a code block offline.
5. **Security posture.**
   - Mermaid runs with `securityLevel: "strict"` and `htmlLabels: false`, and its SVG goes through
     `DOMPurify.sanitize(svg, { USE_PROFILES: { svg: true, svgFilters: true } })`. The profile is never widened. That SVG's
     `<style>` is page-global CSS, so diagram text must not reach the network through it:
     `themeCSS`, `themeVariables`, `fontFamily` and `altFontFamily` are added to mermaid's `secure` list. Its
     default list leaves them overridable by a `%%{init}%%` directive. `neutralizeCss` then strips every
     non-fragment `url()` and `@import` from the sanitized SVG.
   - Charts are `JSON.parse` only, so no functions and no callbacks. Size is capped at 100 000 chars and 5 000
     points. Highlighting is capped as well: a block over 50 000 chars is not highlighted, and language
     auto-detection stops at 5 000.
   - KaTeX runs with `trust: false` and `throwOnError: false`.
   - A failing block keeps its code and gets a `.rich-error` note. Each renderer is guarded per block.
   - **No new egress**: every lib is same-origin and nothing calls a CDN. This is not an ADR 0013 extension.
6. **Math delimiters.** `$$…$$` and `\[…\]` form a block extension that may span blank lines. Its `start()`
   reports line-start delimiters only. `$…$` and `\(…\)` form an inline extension. A single `$` needs no
   whitespace inside its delimiters and no digit after the closing `$`, so "$5 and $10" stays text.
7. **The model is told about ` ```chart `.** `WithRenderingHints` *appends* a two-line hint at the single
   `BuildAgent` call site, so it reaches persona and stored-agent chats, which replace the base `Instructions`.
   `/api/agent-run` and MCP-server callers (ADR 0022/0070) see a chart as a readable JSON fence.
8. **The renderer is optional.** `format.js`, `thread.js` and `viewer.js` reach the global through a guard. A 404
   or a skewed cache degrades to plain markdown, never a blank app.

## Alternatives considered

- **An ES module under `js/` plus a copy in the viewer.** Two copies of the renderer logic would drift, and the
  viewer cannot import from `/js/` (an authenticated path).
- **Duplicating the libs into both `/lib/` and `/shared/lib/`** (as marked and purify are). That means a second
  5 MB mermaid and two places to upgrade. marked and purify stay duplicated, because migrating them is out of
  scope.
- **Rendering inside `renderMarkdown`, per chunk.** Half-streamed mermaid or JSON is invalid, and re-rendering
  per token flickers and costs the heavy libs on every delta.
- **Everything in SHELL.** A 5 MB mermaid in the all-or-nothing install slows it and adds a failure point, all
  for offline rendering of content that was never rendered online.
- **Mermaid HTML labels, with DOMPurify widened to allow `<foreignObject>`.** That re-opens an HTML injection
  surface inside the SVG. Plain SVG text labels render fine.
- **The hint in the `Instructions` const.** Persona and stored-agent chats replace that const, so they would
  never see it.

## Consequences

- Both the chat and the share viewer render mermaid, highlighted code with a per-block copy button, KaTeX and
  Chart.js. Phase 2 (the artifact pane) reuses `Agent42Rich` and the vendored libs.
- Every turn pays a few dozen tokens for the rendering hint.
- A live theme toggle re-themes code highlighting, through an observer on `data-theme`. It does not re-render an
  existing diagram or chart, which keeps the theme it was drawn in.
- Guarded by `RichTextAssetsTests`, `ShareLinkAssetsTests.SharedLibs_AreServed`/`Viewer_UsesSharedRenderer`,
  `PwaAssetsTests.ServiceWorkerShell_OmitsLazyLibs` and `AgentGrainTests.RenderingHint_*`. The visual check stays
  manual, because the repo has no JS runner.
