# Plan — Copy-to-clipboard button for agent responses

planned-against: main @ 0c5015c

## Overview

Add a small "copy to clipboard" icon button to each **finalized agent response** in the
chat thread. Clicking it puts the response's **raw markdown** on the clipboard and shows a
brief visual confirmation. The button is **hidden until the message is hovered or focused**,
matching the existing hover-reveal action pattern. Frontend-only; no backend, grain, or API
change.

Why now: user request. It's a low-risk quality-of-life feature that fits entirely inside the
existing vanilla-JS frontend and its established conventions.

## Affected areas

Verified against the tree at `main @ 0c5015c`:

- `src/Agent42.Backend/wwwroot/app.js` — message rendering (`addMessage`, `setAgentMarkdown`),
  the streaming finalize path, and the history-reload render path. New copy-button helper +
  two wiring calls.
- `src/Agent42.Backend/wwwroot/styles.css` — new `.msg__copy` button rule + hover/focus reveal.
- `src/Agent42.Backend/wwwroot/sw.js` — bump the shell cache version (see constraints).
- `tests/Agent42.Tests/PwaAssetsTests.cs` — extend with source-text assertions; update the
  existing cache-version test.

## Context & constraints

Judgment/rationale the executor cannot cheaply re-derive. Code facts are pointers, not restated.

1. **Raw response text is NOT in the DOM.** Assistant messages render markdown → sanitized HTML
   into a `.prose` div (`renderMarkdown` / `setAgentMarkdown` in `app.js`; evidence:
   `app.js:179-180`, `~778-786`). The `.prose` div holds only rendered HTML. The raw markdown is
   available in exactly two places: the `answer` accumulator during streaming
   (`app.js:1195-1222`) and `turn.text` on the history-reload path (`app.js:859-908`). **The copy
   button's handler must capture the raw text via closure at the moment the message is finalized**
   — do not try to read it back out of the DOM, and do not stash it in a `data-` attribute (the
   text is arbitrary length / contains markup). This is the central design constraint.

2. **Two finalize sites, both must wire the button** — or copy works live but disappears after a
   reload (or vice-versa):
   - Streaming: after the final `setAgentMarkdown(agentMsg, answer)` (caret removed) at
     `app.js:~1216-1222`.
   - Reload: after rendering `turn.text` in the messages-restore path at `app.js:859-908`.

3. **Reuse the existing clipboard pattern, don't invent one.** `navigator.clipboard.writeText(...)`
   with a transient "done" affordance and a silently-swallowed catch is already the house style
   for the agent-key / run-URL copy buttons (`app.js:1814-1830`, `agentKeyCopyBtn.textContent =
   "Copied"` at `app.js:1817`). Mirror it: swap the button's **glyph** to a check on success and
   revert after ~1.5s.

4. **Follow the hover-reveal action convention.** `.conv__delete` (`styles.css:244-273`) and
   `.agent__action` (`styles.css:372-398`) are the exemplars: `opacity: 0` at rest, revealed by
   `<row>:hover .<action>` and `.<action>:focus-visible`, amber on hover. The new rule is
   `.msg--agent:hover .msg__copy` + `.msg__copy:focus-visible`. Known limitation inherited from
   this convention: **no hover on touch devices** — `:focus-visible` (tab/tap-focus) is the only
   reveal there. Accepted for consistency; see Out of scope.

5. **Button shape — match the JS-CREATED exemplars, not the static-SVG composer buttons.** The
   hover-reveal buttons that are built in JS (the relevant precedent, since this button is created
   at finalize time) use a **text glyph** via `textContent`, not an inline SVG: `del.textContent =
   "×"` (`.conv__delete`, `app.js:325`) and `edit.textContent = "✎"` (`.agent__action`,
   `app.js:399`), styled `display: inline-flex`. `app.js` has **zero** `createElementNS`/SVG-
   creation precedent — the composer's SVGs are static in `index.html`, a different family. So
   build `.msg__copy` as a **text-glyph `<button>`** (a monochrome copy glyph such as `⧉` or `⎘`,
   swapped to `✓` on copy — mirroring the `✎`/`×` convention **and** the clipboard textContent
   swap), `display: inline-flex`, sized in the small-action range (~22-26px). This keeps M1
   (critic) closed: one coherent family, no invented pattern.

6. **Placement — bottom of the bubble (user decision, confirmed at approval).** Append the button
   to the assistant message's `.msg__bubble` (`styles.css:569-572`), **after** the `.prose` div,
   so it sits at the **end of the reply** — the conventional "copy this response" position. The
   button is the last child of the bubble; append it in `addCopyButton` after `.prose` (or as a
   trailing `.msg__actions` wrapper if a footer row reads cleaner). Reveal on `.msg--agent:hover
   .msg__copy` / `:focus-visible` as usual. Note ordering in `addMessage`: `msg.append(meta,
   bubble)` and `.prose` is appended into the bubble at creation (`app.js:569-590`), so the copy
   button must be appended **after** the final `setAgentMarkdown` render (which sets `.prose`
   innerHTML) — appending to the bubble, not the prose, keeps it clear of the re-rendered HTML.
   No change to `.msg__meta` is needed (the earlier meta-row/flex approach is dropped).

7. **Service-worker cache MUST be bumped.** `app.js` + `styles.css` are in the SW precache shell
   (`sw.js:6-8`), keyed by `const CACHE = "agent42-shell-v7"` (`sw.js:5`). Changing cached assets
   without bumping the version serves **stale** JS/CSS to returning clients. Bump to
   `agent42-shell-v8`. This is enforced socially by `PwaAssetsTests.ServiceWorker_CacheNameIsCurrent`
   (`PwaAssetsTests.cs:~166`), which pins the exact current string — that test must be updated in
   the same step, or it fails. See the SW gotcha (`.agentic/gotchas.md:816-828`).

8. **No CSP; clipboard is available.** No Content-Security-Policy is defined (no meta tag, no
   backend header — scout-confirmed), and `navigator.clipboard` already works in this app over its
   secure context. No new security surface: the button reads text already on the page and writes it
   to the user's own clipboard.

9. **Test reality: no JS runner.** The frontend is tested by C# **source-text** assertions over the
   served static assets — `Assert.Contains` / `DoesNotContain` / `Matches` in
   `tests/Agent42.Tests/PwaAssetsTests.cs` ("the repo has no JS test runner by design"). ACs below
   are therefore proven by asserting the feature's wired-in markers are present in the served
   `app.js` / `styles.css` / `sw.js`. Behavioral correctness (does clicking actually copy?) is
   verified manually — these tests guard against regression of the wiring, which is what this repo's
   test layer can express. This is a stated limitation, not a gap to paper over.

No ADR governs this feature; grep of `.agentic/decisions/` finds none touching chat-message
client rendering. No new ADR required (a UI affordance with no architectural decision or egress
change). No new gotcha anticipated.

## Steps

1. **CSS — `.msg__copy` button + hover/focus reveal.** In `styles.css`, add a `.msg__copy` rule in
   the messages section: small text-glyph button (~22-26px, `display: inline-flex; align-items:
   center; justify-content: center;` — matching `.conv__delete`/`.agent__action`, NOT the
   composer's `grid` SVG buttons), transparent at rest, amber on hover, `opacity: 0` by default,
   revealed by `.msg--agent:hover .msg__copy` and `.msg__copy:focus-visible`. The button is
   appended at the **bottom of `.msg__bubble`, after `.prose`** (constraint 6) — give it a little
   top margin to separate it from the reply text; no `.msg__meta` change. Mirror `.conv__delete` /
   `.agent__action`. *Verify:* `dotnet build
   Agent42.slnx`; visual check deferred to /verify. *(Coupled with step 2 — the class name must
   match.)*

2. **JS — copy helper + wire both finalize sites.** In `app.js`: add `addCopyButton(msgEl, rawText)`
   that (a) returns early if `rawText` is empty/whitespace (skips image-only / empty-tool replies),
   (b) builds a `<button class="msg__copy" type="button" aria-label="Copy response">` whose
   `textContent` is a monochrome copy glyph (e.g. `⧉`), (c) appends it to the message's
   `.msg__bubble`, after the `.prose` div (constraint 6), (d) on click calls `navigator.clipboard.writeText(rawText)`, swaps `textContent` to `✓` for
   ~1.5s then reverts, and swallows the catch — mirroring the JS-created glyph buttons (`app.js:325`,
   `399`) and the clipboard textContent-swap (`app.js:1814-1830`). No inline SVG / `createElementNS`
   (constraint 5). Call it at the streaming finalize site (`~1222`, passing `answer`) and in the
   reload render (`859-908`, passing `turn.text`). *Verify:* `dotnet build`; manual smoke via
   /verify or DevHost.

3. **SW — bump cache version.** In `sw.js:5`, change `agent42-shell-v7` → `agent42-shell-v8`.
   *Independent of steps 1-2; parallelizable.* *Verify:* covered by step 4's test.

4. **Tests — extend `PwaAssetsTests.cs`.** Add assertions that the served assets contain the wiring:
   `styles.css` contains `.msg--agent:hover .msg__copy` (reveal rule) and a `.msg__copy` base rule;
   `app.js` contains `msg__copy` and `navigator.clipboard.writeText`. For AC3, assert the helper is
   wired on **both** paths specifically — anchor one `addCopyButton(` call near the streaming
   `answer` accumulator and another near the reload `turn.text` render (e.g. two `Assert.Matches`
   with distinct surrounding-context patterns), not merely "referenced ≥2×" (a count could sit twice
   on one path — critic M3). **Update** `ServiceWorker_CacheNameIsCurrent` to expect
   `agent42-shell-v8`. *Verify:* `dotnet test Agent42.slnx`. *(Depends on steps 1-3.)*

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test (source-text assertion) | Test file |
|---|----------------------|------------------------------|-----------|
| AC1 | A copy button exists on agent replies, hidden until hover/focus | Assert `styles.css` contains `.msg--agent:hover .msg__copy` and `.msg__copy` with `opacity: 0` at rest | `tests/Agent42.Tests/PwaAssetsTests.cs` |
| AC2 | Clicking copies raw markdown via the clipboard API with confirmation | Assert `app.js` contains `msg__copy` + `navigator.clipboard.writeText` (helper wiring); check-glyph swap present | `tests/Agent42.Tests/PwaAssetsTests.cs` |
| AC3 | Button appears on both live-streamed and history-restored replies | Two anchored assertions: an `addCopyButton(` call near the streaming `answer` accumulator, and another near the reload `turn.text` render (distinct-context `Assert.Matches`, not a bare ≥2× count — M3) | `tests/Agent42.Tests/PwaAssetsTests.cs` |
| AC4 | Returning clients receive the updated assets | `ServiceWorker_CacheNameIsCurrent` asserts `sw.js` contains `agent42-shell-v8` | `tests/Agent42.Tests/PwaAssetsTests.cs` |

Behavioral verification (actual copy, icon swap, hover reveal) is done via `/verify` / manual
DevHost run — the C# layer proves the wiring is present, per constraint 8.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time:

- `new_issues: 0`
- `new_security_issues: 0`
- `duplication_pct_max: 3`
- `coverage_new_code_pct_min: 80`
- `cognitive_complexity_max: 15`

Note: local runs treat coverage/duplication/complexity as self-reported soft warnings; CI hard-blocks
on build/tests/format/vulnerable-packages/doc-integrity. `dotnet format Agent42.slnx --verify-no-changes`
must pass. The added JS helper must stay under cognitive complexity 15 (it's a small handler — trivially met).

## Out of scope

- No copy of **rendered plain text** or of code blocks individually — raw markdown of the whole
  response only (explicit user choice this session).
- No always-visible button; hover/focus-reveal only (explicit user choice). The touch-device
  no-hover limitation is accepted, not solved here.
- No copy button on **user** messages, error bubbles, or empty/image-only replies.
- No "copied!" toast / global notification — the in-button icon swap is the only feedback.
- No backend, grain, `/api`, or persistence change; nothing leaves the tenant (no ADR 0013 egress).

## Review

Critic subagent (fresh context) reviewed this spec against the ADRs, gotchas, exemplars, and
actual code at `main @ 0c5015c`. **Verdict: sound — all cited `file:line` anchors verified
accurate, no ADR contradiction, SW gotcha respected, correctness traps (raw-text-not-in-DOM,
image-gen empty text, error bubbles, ADR 0022 agent-run background path) all covered. Minor
findings only.** Dispositions:

- **M1 (accepted)** — the plan originally said "inline SVG button" citing the static-SVG composer
  buttons, but the JS-*created* hover-reveal exemplars use text glyphs (`textContent = "×"`/`"✎"`,
  `inline-flex`) and `app.js` has no SVG-creation precedent. Constraints 5, and steps 1-2, rewritten
  to a **text-glyph button** — one coherent family, no invented pattern.
- **M3 (accepted)** — AC3's "referenced ≥2×" test was a weak proxy (both refs could sit on one
  path). Tightened to two context-anchored assertions (near `answer`, near `turn.text`).
- **M4 (accepted + resolved at approval)** — placement was flagged for the user; the user chose
  **bottom-of-bubble** (after `.prose`). Constraint 6 and steps 1-2 now specify appending to
  `.msg__bubble` after the final render; no `.msg__meta` layout change needed.
- **M2 (acknowledged, no change)** — AC2's substantive guarantee (raw markdown is actually copied)
  can't be proven by source-text assertions; it's manual-verified. This is inherent to the repo's
  no-JS-runner test design (constraint 9), disclosed, not a gap this plan can close.

## Implementation notes (divergence from plan)

Executed on `feature/plan-copy-response-button` (from `main @ 0c5015c`; no drift — HEAD equalled
`planned-against`). The implementation followed the plan as written. Concrete choices where the plan
left latitude:

- **Copy glyph:** `⧉` at rest, swapped to `✓` on copy (both offered in the plan; chose `⧉`).
- **CSS placement/shape:** `.msg__copy` added after the `.msg--agent.is-error .msg__bubble` block in
  `styles.css`, before the tool-calls section. In-flow (no `position: absolute`, unlike the
  absolutely-positioned `.conv__delete`/`.agent__action` overlays) — 24px, `margin-top: 6px` to
  separate it from the reply. The `.msg__actions` footer-wrapper alternative from constraint 6 was
  not needed; the button appends directly to `.msg__bubble` as its last child.
- **Reload wiring position:** `addCopyButton(msg, turn.text)` is called immediately after
  `setAgentMarkdown(msg, turn.text)` and **before** the `addAgentImage` loop, so the copy button
  stays the bubble's last child while generated images (ADR 0020) insert above the prose.
- **Tests:** AC3's two anchored assertions use `Assert.Contains` on the full call strings
  (`addCopyButton(agentMsg, answer)` / `addCopyButton(msg, turn.text)`) — the distinct argument
  proves the path (M3), a stronger form than the `Assert.Matches` context-regex the plan suggested.
- **Verification:** `dotnet test Agent42.slnx` (full suite) + `dotnet format --verify-no-changes`
  both green; `critic` subagent returned a clean pass. Behavioral copy/hover/glyph-swap left to
  `/verify` per constraint 9.
- **No ADR / no gotcha** created — as anticipated (frontend affordance, no egress/architectural
  decision, no new trap).
