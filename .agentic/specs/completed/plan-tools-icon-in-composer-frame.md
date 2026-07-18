# Plan — Tools control as an icon button next to the attach icon

planned-against: main @ 66228c7

## Overview

Today the per-conversation tools control (ADR 0021) is a text button `// tools`
(`#toolsBtn`, class `.model__tools`) sitting in the composer **footer** next to the
model picker. Move it to be an **icon-only** button inside the composer **frame**,
directly next to the "attach" icon button (`#attachBtn`), matching the visual
language of the attach/mic/send icon buttons. Purely a UI relocation + restyle;
no change to tools behaviour, the popover, or the ADR 0021 resolution logic.

## Affected areas (verified against the tree @ 66228c7)

- `src/Agent42.Backend/wwwroot/index.html` — `#toolsBtn` markup (currently lines
  132–135, in `.composer__footer`); target insertion point is `.composer__frame`
  after `#attachBtn` (lines 105–110).
- `src/Agent42.Backend/wwwroot/styles.css` — `.model__tools` rules (lines 1060–1080);
  icon-button exemplars `.composer__attach` (844–859) and `.composer__mic` (863–885).
- `src/Agent42.Backend/wwwroot/sw.js` — shell cache name `agent42-shell-v5` (line 5).
- `src/Agent42.Backend/wwwroot/app.js` — **reference only**; `toolsBtn` is looked up by
  id (line 81) and shown/hidden (lines 536, 542, 1835) and wired to `openToolsDialog`
  (line 1445). Id is unchanged, so **no JS logic change is required**. Confirm during
  execution that nothing keys off the `.model__tools` class or `// tools` text.
- `tests/Agent42.Tests/PwaAssetsTests.cs` — `ServiceWorker_CacheNameIsCurrent`
  (line 170, asserts `agent42-shell-v5`); add a new placement heuristic test.

## Context & constraints

- **No ADR touches this.** ADR 0021 defines the tools *model* (catalog, three-layer
  resolution, `null == all`); it says nothing about the composer button's DOM position
  or styling. Verified: grep of `.agentic/decisions/0021*.md` for composer/popover/
  button/icon returned nothing. So this is a pure presentation change — no `/decide`.
- **Id must stay `toolsBtn`.** app.js resolves the element by `getElementById("toolsBtn")`
  and toggles `.hidden` in `lockPicker`/`unlockPicker`/init (agent chats hide it — ADR
  0021: an agent chat's tools come from its bind snapshot). Keep the id; only the
  parent element, class, and inner content change.
- **Icon-only ⇒ needs an accessible name.** The current button gets its name from its
  `// tools` text. Once it's icon-only, it MUST carry an `aria-label` (mirror
  `#attachBtn`'s `aria-label="Attach an image or document"` + `title`). Reuse the
  existing `title` copy ("Choose which tools the assistant may use in this conversation").
- **Cache bump is mandatory.** `sw.js` serves the shell (`index.html`/`styles.css`/
  `app.js`) cache-first; the `ServiceWorker_CacheNameIsCurrent` test comment
  (PwaAssetsTests.cs:165–168) states the name is bumped **whenever the cached shell
  changes**. index.html + styles.css change here, so bump `v5`→`v6` in sw.js AND update
  the test's asserted string. (Precedent: the tabbed-dialog commit bumped to v5.)
- **Style by mirroring, not inventing** (gotchas: copy the exemplar's shape). The new
  `.composer__tools` rule should mirror `.composer__attach` (34×34 grid-centered icon
  button, same border/radius/hover/active). Delete the now-unused `.model__tools` rules.
- **Update the two inline comments** that describe the old placement (hard rule 2):
  index.html:132–133 ("sits next to the model picker" / footer rationale) and
  styles.css:1060 ("sits next to the model picker in the composer footer"). Keep the
  ADR 0021 reference and the agent-chat-hides-it rationale; just correct the location.
- No JS test runner in this repo by design — frontend invariants are guarded by
  source-text heuristics in `PwaAssetsTests` and by driving DevHost.

## Steps

1. **index.html — relocate + iconify the button.** Remove `#toolsBtn` from
   `.composer__footer`. Insert it into `.composer__frame` immediately after `#attachBtn`
   (before `#promptInput`), as an icon-only button: `class="composer__tools"`, keep
   `id="toolsBtn" type="button" hidden`, add `aria-label`, keep the `title`, and give it
   an inline SVG (16×16, `stroke="currentColor"`, `aria-hidden="true"` — same shape as
   its siblings; a wrench/sliders glyph). Update the relocated HTML comment to describe
   the frame placement while keeping the ADR 0021 + agent-hidden rationale.
   *Verify:* `dotnet build Agent42.slnx` (index served as a static asset), and visual
   check in step 5.
2. **styles.css — add `.composer__tools`, drop `.model__tools`.** Add a
   `.composer__tools` rule set mirroring `.composer__attach` (rest + `:hover` +
   `:active`, and a `:focus-visible` like `.model__tools` had). Delete the
   `.model__tools` / `:hover` / `:focus-visible` rules and fix the comment.
   *Verify:* `dotnet build`; grep confirms no remaining `.model__tools` reference in
   any wwwroot file.
3. **sw.js — bump shell cache** `agent42-shell-v5` → `agent42-shell-v6`.
   *Verify:* step 4's updated test.
4. **PwaAssetsTests.cs — update cache assertion + add placement guard.** Change the
   asserted cache name in `ServiceWorker_CacheNameIsCurrent` to `agent42-shell-v6`. Add
   a `Frontend_ToolsControlIsIconButtonInComposerFrame` test (source-text heuristic).
   Because whole-file `Contains` checks pass vacuously (index.html already has many
   `aria-label`s and a `// tools` label at line 210), the assertions must be
   **mutation-observable** — follow the ordering style of
   `ServiceWorker_BypassesApiBeforeCaching` (PwaAssetsTests.cs:62–70, which uses
   `IndexOf` to prove position):
   - **Accessible name on the target button:** regex-match that the `composer__tools`
     button carries an `aria-label`, e.g. `Assert.Matches(@"class=""composer__tools""[^>]*aria-label", index)`
     (co-occurrence on the same tag, not a bare `Contains("aria-label")`).
   - **Adjacency/nesting in the frame:** `IndexOf("composer__tools") > IndexOf("attachBtn")`
     **and** `< IndexOf("id=\"promptInput\"")` — i.e. it sits between the attach icon and
     the textarea, inside `.composer__frame`.
   - **Removal of the old text button:** `Assert.DoesNotContain("model__tools", index)`
     and `Assert.DoesNotContain(">// tools<", index)` (guards the icon-only conversion;
     the surviving `// tools` at index.html:210 is the agent-form label, which is
     `>// tools</span>` style — ensure the negative assertion targets the button form,
     not the label; assert on the removed `class="model__tools"` string to be safe).
   - **Style exists:** `Assert.Contains(".composer__tools", styles)`.
   *Verify:* `dotnet test Agent42.slnx --filter PwaAssetsTests`.
5. **Drive DevHost to confirm placement + behaviour.** `dotnet run --project
   tools/Agent42.DevHost`; confirm the tools icon renders next to the attach icon in
   the frame, opens the tools dialog on click, is hidden in an agent chat, and the
   footer no longer shows `// tools`.
   *Verify:* manual UI check (per the repo's "behaviour verified by driving DevHost"
   convention).

Steps 1–3 touch disjoint files but are logically sequential (test in step 4 depends on
all three); execute in order. Not worth parallelizing.

## Acceptance criteria → test mapping

| AC | Test | File |
|----|------|------|
| Tools control renders as an icon-only button inside the composer frame next to attach | `Frontend_ToolsControlIsIconButtonInComposerFrame` (new) — `IndexOf` adjacency between `attachBtn` and `promptInput` | `tests/Agent42.Tests/PwaAssetsTests.cs` |
| Icon button has an accessible name (aria-label) | same test — regex co-occurrence of `composer__tools` + `aria-label` on the tag | same |
| `.composer__tools` style exists; `.model__tools` removed | same test — `Contains(".composer__tools")` + `DoesNotContain("model__tools")` | same |
| Shell cache bumped so returning users get the new shell | `ServiceWorker_CacheNameIsCurrent` (updated to v6) | same |
| Behaviour unchanged: opens dialog, hidden in agent chats | driving DevHost (step 5) — no JS runner by design | — |

## Quality gate checklist (from `.agentic/quality-gates.yaml`)

<!-- Executor: read `.agentic/quality-gates.yaml` at execution time and copy the live
     thresholds here; do not carry numbers from memory (hard rule 3). -->
- `dotnet build Agent42.slnx` — clean.
- `dotnet test Agent42.slnx` — all green (incl. the new + updated PwaAssetsTests).
- `dotnet format Agent42.slnx --verify-no-changes` — clean.
- Branch: work on `feature/*` (never main — hard rule 1); open an MR.

## Out of scope

- Any change to tools resolution, the catalog, the popover contents, or `/api/tools`.
- Icons/behaviour of the attach, mic, or send buttons.
- The agent-editor Tools tab and Preferences default-tools UI (unaffected).
- app.js logic (id and wiring unchanged).

## Review

Critic verdict: **sound — one moderate AC-to-test gap, otherwise clean.** All structural
claims (line refs, `planned-against` ref, no missed `.model__tools`/`toolsBtn`/`// tools`
references, cache-bump necessity, no JS breakage from reparenting, no ADR/gotcha
contradiction) verified against the tree.

- **Accepted (moderate):** the originally-described `Contains`-only test could not
  discriminate placement, accessible name, or removal — `aria-label` and `// tools`
  already appear elsewhere in index.html, so the checks passed vacuously. **Fixed** in
  step 4 and the AC table: now mutation-observable (`IndexOf` adjacency between
  `attachBtn` and `promptInput`; regex co-occurrence of `composer__tools` + `aria-label`;
  `DoesNotContain("model__tools")`), mirroring `ServiceWorker_BypassesApiBeforeCaching`.
- **Accepted (trivial):** `.composer__mic` line range corrected `863–892` → `863–885`.

## Execution notes (divergence from plan)

Executed on `feature/tools-icon-in-composer-frame` from `main @ 66228c7` (no drift).
Implemented as planned, in order; all five steps landed with no material deviation.

- **Icon glyph:** used a "sliders/lines-with-dots" SVG (three horizontal lines with two
  offset circles) for the tools button, in the same 16×16 / `stroke="currentColor"` /
  `aria-hidden="true"` idiom as its siblings. The plan left the exact glyph open
  ("a wrench/sliders glyph"); sliders was chosen to read as "adjust which tools".
- **`aria-label` copy:** used `aria-label="Choose tools"` (short accessible name) with the
  full existing `title` copy retained ("Choose which tools the assistant may use in this
  conversation") — mirrors `#attachBtn`'s short-label + longer-title split.
- **`:focus-visible`:** kept (carried from `.model__tools`) as the plan directed, even
  though neither icon exemplar (`.composer__attach`/`.composer__mic`) defines one. Critic
  flagged this as an a11y-improving divergence, no action needed.
- **Verification:** DevHost served the built shell correctly — tools button renders
  between `#attachBtn` and `#promptInput` inside `.composer__frame`, `model__tools` gone,
  `sw.js` cache `v6`, `.composer__tools` CSS present. Behaviour preserved because app.js
  wiring is id-based (`toolsBtn`, unchanged).
- **Gates:** `dotnet build` 0 errors; `dotnet test` 361 passed / 0 failed (incl. new
  `Frontend_ToolsControlIsIconButtonInComposerFrame` + updated `ServiceWorker_CacheNameIsCurrent`);
  `dotnet format --verify-no-changes` clean. Critic: clean pass.
- No `/decide` or `/gotcha`: no ADR governs composer button placement/styling (pure
  presentation), and no new trap surfaced.
