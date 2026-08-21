# Plan — move incognito + share into a persistent top-right action bar

planned-against: main @ b0d32e3

## Overview

The incognito toggle (ADR 0034) lives inside `.composer__frame` and the read-only share
action (ADR 0035) exists only as a hover-revealed glyph on each sidebar conversation row.
Both are hard to find, and neither is reachable at a glance while reading a long chat.

This plan promotes both to a **persistent top-right action cluster** in the existing
`.topbar`, which today is rendered but CSS-hidden above 760px. Revealing it on desktop
gives both buttons a home that never scrolls away — `.topbar` is a flex sibling *above*
`.chat__scroll`, the only element in the main column with `overflow-y: auto`, so
"stays visible while scrolling" needs no `position: sticky`, no z-index and no new
stacking context.

## Affected areas

Verified present at `b0d32e3`:

| File | What changes |
|---|---|
| `src/Agent42.Backend/wwwroot/index.html` | `.topbar` (l.97–103) gains an action cluster; `#incognitoBtn` (l.135–145, comment l.135–136) moves out of `.composer__frame` |
| `src/Agent42.Backend/wwwroot/styles.css` | `.topbar` desktop reveal (l.1827) + its stale section comment (l.1824–1826); `.composer__incognito` block (l.1261–1298) → topbar variants; `.conv__share` (l.275–305) glyph→SVG sizing |
| `src/Agent42.Backend/wwwroot/js/dom.js` | `incognitoBtn` (l.148) stays; add `topbarShareBtn` beside the share-link exports (l.159–168) |
| `src/Agent42.Backend/wwwroot/js/sharelinks.js` | topbar-share state + setter + wire + a null guard (currently 69 lines) |
| `src/Agent42.Backend/wwwroot/js/sidebar.js` | `buildConvRow` (l.45–86) — swap `↗` (l.68) for the shared SVG |
| `src/Agent42.Backend/wwwroot/js/conversations.js` | `selectConversation` (l.22), `startNewConversation` (l.92) — drive share availability |
| `src/Agent42.Backend/wwwroot/js/agents.js` | **`startAgentChat` (l.92)** — the fourth transition; see the gating section |
| `src/Agent42.Backend/wwwroot/js/send.js` | success path of `send()` — enable share unless ephemeral |
| `src/Agent42.Backend/wwwroot/js/main.js` | boot-chat seed (l.83–85) — import `setTopbarShare`, seed disabled |
| `src/Agent42.Backend/wwwroot/sw.js` | `CACHE` bump `agent42-shell-v14` → `v15` |
| `tests/Agent42.Tests/PwaAssetsTests.cs` | l.216 cache-name assertion, bumped **in the same step** as `sw.js` |
| `tests/Agent42.Tests/ShareLinkAssetsTests.cs` | extend for the topbar entry point + shared icon |
| `tests/Agent42.Tests/TopbarActionsAssetsTests.cs` | **new** — placement, gating, tint, removals |

`agents.js`'s `lockPicker`/`unlockPicker`/`setIncognito` (l.214–243) need **no** change —
see "Verified blast radius".

## Context & constraints

### Decisions taken in the interview (do not relitigate)

1. **Reveal the existing `.topbar` on desktop** rather than adding a floating overlay.
   Rejected: a `position: sticky` cluster over `.chat__scroll` — it would need a z-index, a
   translucent backing so text doesn't run illegibly beneath it, and would introduce the
   first sticky element in the main column.
2. **Share is additive, not a move.** The sidebar row action **stays**. The topbar button is
   a second entry point into the same `openShareLink()`. Consequence:
   `ShareLinkAssetsTests.ShareAction_WiredIntoConvRow_AndOwnerEndpoint` (l.42–44) keeps
   passing unchanged — it must not be weakened.
3. **One SVG share icon in both places.** The row's `↗` text glyph (`share.textContent = "↗"`,
   sidebar.js:68) is replaced by the same inline SVG used in the topbar: the three-node share
   mark, `16×16`, `stroke-width="1.4"`, `stroke="currentColor"`, `fill="none"`,
   `aria-hidden="true"` — matching every other icon in this app. The row's sibling `×` delete
   stays a text glyph; that asymmetry is accepted.
4. **Incognito keeps every existing state rule** — hidden on agent chats, disabled once a chat
   has history, off by default. Only its DOM position changes.
5. **The engaged tint MOVES to the topbar.** `body.is-incognito .composer__frame`
   (styles.css:1295–1298) is replaced by a topbar-side tint. The composer frame no longer
   tints. Rejected: tinting both.
6. **Topbar share is disabled when there is nothing to share** — a fresh empty chat, or an
   incognito chat. Rejected: always-enabled (mints a live public link to an empty view) and
   hidden-in-those-states (the control would move around).

### Share gating: FOUR transitions, not three

The button must be enabled iff the active conversation is persisted **and** has history —
`NOT(fresh empty chat) AND NOT(ephemeral)`. Four sites change the active conversation:

| Site | Action |
|---|---|
| `startNewConversation()` — conversations.js:92 | disable |
| `startAgentChat(agent)` — **agents.js:92** | disable (fresh agent chat) |
| `selectConversation(id)` — conversations.js:22 | enable (an opened conversation is persisted with history; an ephemeral chat never gets a sidebar row) |
| `send()` success path — send.js | enable **unless `ephemeral`** |

**`startAgentChat` is the one that bites, and it is easy to miss.** It does not delegate to
`startNewConversation`; it calls `setActiveId(convId)` directly (agents.js:105) and then
`lockPicker`/`clearThread`. So without a call there:

> open conversation A (share enabled, target = A) → click an agent → a fresh agent chat is on
> screen, but the topbar button is **still enabled and still pointing at A**. Pressing Create
> mints a public, no-login link to a conversation the user is not looking at.

That directly violates ADR 0035's consent property ("nothing is minted until the user presses
Create — that press is their explicit consent to the exposure", sharelinks.js:16–18). The
analogy to `setIncognito` does **not** carry here: incognito is *hidden* by `lockPicker`
(agents.js:221), whereas decision 6 chose *disabled, not hidden* for share — so a stale target
stays visible and clickable.

Note `startAgentChat`'s early branch: a **continuous** agent (ADR 0016) with an existing
conversation delegates to `selectConversation` and returns (agents.js:100–103), which already
enables correctly. Place the disable on the fresh-chat path **after** that branch.

The incognito toggle click itself needs **no** share update — the toggle is only interactive on
a fresh empty chat, which already has share disabled.

### Where exactly in `send()`, and with what title

**Do not** put this beside `incognitoBtn.disabled = true` (send.js:37). That line is the third
statement of `send()`, **before** the `fetch` (l.66) — enabling there would light the button up
pre-flight, so an offline send, a "Transmission failed" turn, or a voice barge-in `AbortError`
(l.132) would leave share enabled on a chat that persisted nothing. The `finally` (l.139–146)
has the same problem: it runs on failure too.

Put it at the **end of the `try`, on the success path**, gated on `!ephemeral`. Failure leaves
the button disabled — the conservative direction, and self-healing: the chat appears in the
sidebar via `refreshConversations()` (l.146) and opening it enables share through
`selectConversation`.

**Title:** a fresh chat's title is server-derived and not known client-side at that moment, and
`openShareLink` renders it as `` `“${title}”` `` (sharelinks.js:22). Pass a neutral constant
(e.g. `"This conversation"`) from the send path rather than inventing one; `selectConversation`
resolves the real title from the `conversations` list.

### Keeping `sharelinks.js` leaf-ward (NOT a cycle hazard)

`openShareLink(convId, title)` needs a title, so `sharelinks.js` must know the current target.
Hold it in module-local `topbarConvId` / `topbarConvTitle`, set through an exported setter:

```js
// sharelinks.js — null convId disables the button
export function setTopbarShare(convId, title) { … }
```

**Rationale — read this, because the obvious rationale is wrong.** This is *not* because an
import would create a cycle. Cycles here are fine and several already ship: `sharelinks.js:4`
→ `send.js:10` → `sidebar.js:8` → `sharelinks.js` is a live cycle today, and ADR 0041 §5
(`.agentic/decisions/0041-frontend-es-modules.md:89–91`) states the rule outright — feature
modules import each other "for **runtime calls and live read-bindings**, never for values
consumed during module evaluation. Residual cycles are then harmless, and there are several."

The setter is right because it matches this repo's canonical seam for exactly this shape — the
`voice` / `stopSpeech` / `sendAbort` `export let` slots on `send.js`, assigned through setters
(ADR 0041 §5, which also says **do not** "clean this up" into direct imports). Callers pass
what they already hold; `sharelinks.js` gains only a `dom.js` import.

`conversations.js` may resolve the title from `sidebar.js`'s `conversations` — it already
imports `renderSidebar` from there (l.11), so that adds no new edge. Fall back to a constant
when the lookup misses.

### Gotchas that apply (read before editing)

- **`.agentic/gotchas.md:1580` — "Every MR that touches `wwwroot/js/` must bump `CACHE`".**
  Non-negotiable: this MR *adds* an export (`setTopbarShare`), and a version-skewed graph then
  throws `does not provide an export named 'setTopbarShare'` — a **blank app**, not a degraded
  one. Bump `sw.js` `CACHE` **and** `PwaAssetsTests.cs:216` in the same step.
- **`.agentic/gotchas.md` — "The service worker's SHELL is all-or-nothing".** This plan adds
  **no new module file**, so `SHELL` (sw.js:6) stays byte-identical.
- **`.agentic/gotchas.md` — "A frontend module unreachable from `main.js` is invisible to all
  47 source-text guards".** `StaticAssetsServerFixture.GetAppScriptAsync()` walks the import
  graph over HTTP from `main.js`; assertions must target code reachable that way.
  `StaticAssetsServerFixture.Body(app, sig)` counts brace depth — use it for **JS function
  bodies only**; it cannot slice HTML.
- **`.agentic/gotchas.md:524` — the boot chat is not created via `startNewConversation()`**,
  which is why the initial seed belongs in `main.js`'s block at l.83–85.
- **ADR 0034 §UI** fixes ephemerality at chat creation. The button's *rules* are the feature;
  its *position* is not.
- **ADR 0035 §Owner UI** records the row action as the share entry point.

### Verified blast radius (do not re-derive)

Grepped across `wwwroot/` at `b0d32e3`:

- `body.is-incognito` has exactly **two** occurrences — `agents.js:239` (writer) and
  `styles.css:1295` (only reader). Retargeting the tint is a pure CSS edit; `setIncognito`
  needs no change **provided the new topbar class keeps an `.is-active` rule**, since
  `setIncognito` toggles `is-active` on `#incognitoBtn` (agents.js:236–243).
- `.composer__incognito` appears only in `index.html:137` and its own `styles.css` block.
- The public share viewer (`wwwroot/shared/`) has its own `viewer__topbar` in a **separate,
  deliberately self-contained** stylesheet (ADR 0035; machine-checked by ADR 0042). Unaffected
  — do not touch it, and do not point it at `styles.css`.
- **No existing test references `topbar`, `menuToggle`, `incognitoBtn`, `.composer__incognito`
  or `is-incognito`** — the topbar reveal and the old composer position are currently
  unguarded, which is why ACs 1/2/3/3b are new tests.
- The usage dashboard (`#usageView`, index.html:642, outside `.app`) is `position: fixed;
  inset: 0; z-index: 100` (styles.css:1952–1955) over an **unpositioned** topbar, so it still
  covers it. Do not add a z-index to `.topbar` — that is how this breaks.
- `.chat` is `display: flex; flex-direction: column` (styles.css:555) with `.chat__scroll` at
  `flex: 1; min-height: 0` (562) — an always-present static `.topbar` sibling takes its natural
  height and the scroller absorbs the rest. That is the entire mechanism behind AC 10.
- `voice.js`, `usage.js` and `sharetarget.js` need no change **beyond** the `startAgentChat`
  fix above (`sharetarget.js:80` reaches `startAgentChat`, so it inherits the fix). ADR 0023's
  "a share prefills but cannot send" property rests on `input.disabled`
  (`ComposerAssetsTests:80–87`) and is untouched.

### Not a security change

Neither button changes what is exposed. `openShareLink` still mints nothing until the user
presses **Create** — ADR 0035's explicit-consent property (ADR 0013 egress) is untouched, and
the topbar button must **not** create a link directly. The `startAgentChat` gap above is the
one place this plan could have violated that; it is fixed in step 5.

## Steps

Tests are written **inside** the step that creates the behaviour (hard rule 3 — never
deferred). Steps 1–2 are parallelizable (disjoint files).

### Step 1 — Markup: topbar action cluster, incognito relocated
**Files:** `index.html` · **Tests:** `TopbarActionsAssetsTests.cs` (new) — AC 1

- Add a `.topbar__actions` wrapper inside `<header class="topbar">`, after `.topbar__brand`.
- **Move** `#incognitoBtn` (l.135–145) out of `.composer__frame` into that wrapper, verbatim
  including `hidden`, `aria-pressed`, `aria-label`, `title` and its SVG. Change the class from
  `composer__incognito` to the new topbar class. **Keep the id** — every JS reference keys off it.
- Add `#topbarShareBtn` beside it: `type="button"`, `aria-label="Share this conversation"`, a
  `title` naming the read-only public link, the SVG mark from decision 3, and **`disabled` in
  the markup** — between HTML parse and the deferred module executing it would otherwise be
  live with no target. It is disabled, not `hidden` (decision 6).
- Carry the ADR-0034 comment with the moved button; add one citing ADR 0035 for the new one.

New test class shaped on `ShareLinkAssetsTests` (`[Collection("StaticAssets")]`, injected
`StaticAssetsServerFixture`, class doc naming the ADRs). For AC 1's "in the topbar, not the
composer" containment claim use the **index-ordering idiom** (`IndexOf` comparisons) as in
`ConfigHealthAssetsTests.cs:41–48` / `PwaAssetsTests.cs:85–92` — `Body()` cannot slice HTML,
and a bare `Contains("topbar__actions")` proves nothing about placement.

**Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~AssetsTests`

### Step 2 — Shared share icon in the sidebar row
**Files:** `js/sidebar.js`, `styles.css` · **Tests:** `ShareLinkAssetsTests.cs` — AC 8

- In `buildConvRow` (l.63–72) replace `share.textContent = "↗"` (l.68) with the SVG from
  decision 3 via `innerHTML` (the surrounding code builds elements imperatively — match that
  idiom; this string is author-controlled, not user data, so it is not a sanitiser bypass).
- Adjust `.conv__share` (styles.css:275–305) so a 16×16 SVG centres in the 22×22 box — the
  existing `font: 400 13px/1 var(--mono)` was sized for a text glyph. Keep the hover-reveal
  (`opacity: 0` → `1` on `.conv-row:hover`) exactly as-is.
- Add `ShareIcon_IsTheSameMarkInBothPlaces`.

**Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~ShareLinkAssetsTests` —
`ShareAction_WiredIntoConvRow_AndOwnerEndpoint` (l.42–44) asserts `conv__share` and
`openShareLink(conv.id` inside `buildConvRow`'s body; both must survive.

### Step 3 — CSS: reveal the topbar, style the cluster, move the tint
**Files:** `styles.css` · **Tests:** `TopbarActionsAssetsTests.cs` — ACs 2, 3, 3b, 6, 6b

- Replace `.topbar { display: none; }` (l.1827) with the always-on flex layout the ≤760px block
  currently sets (l.1906–1912); give `.topbar__actions` `margin-left: auto`. Remove the now-
  redundant `.topbar` rule from the media query, leaving the other mobile rules intact.
- **On desktop the topbar must carry the action cluster and nothing else.** Both current
  children invert their visibility — hidden by default, revealed inside
  `@media (max-width: 760px)`:
  - `.topbar__menu` (☰) — above 760px the sidebar is not a drawer, so it has nothing to toggle.
  - `.topbar__brand` — **the trap.** The desktop sidebar header already renders the identical
    mark and wordmark (`index.html:41–48`, `.brand__mark` / `.brand__name`), so a naively
    revealed topbar shows "42 AGENT 42" **twice, side by side**. The topbar brand exists only
    because the sidebar is off-canvas on mobile.

  Net: at ≤760px the topbar is unchanged plus the cluster; above 760px a slim right-aligned
  action strip. Check the bar's height when its only children are two 34px buttons — the
  current padding was tuned around the 30px `.topbar__mark`.
- **Fix the now-false section comment** at l.1824–1826 (`/* ————— mobile top bar + drawer ————— */`
  and "Off-canvas chrome — desktop hidden; the @media block below reveals it at ≤760px") — hard
  rule 2, same MR.
- **Rename, don't copy:** port `.composer__incognito`'s rest/hover/active/focus-visible/
  `.is-active`/`:disabled` rules (l.1261–1292) to the topbar class and **delete the originals**
  (`duplication_pct_max: 3`). The `.is-active` rule must survive — `setIncognito` depends on it.
- The buttons now sit on the topbar background, not inside the composer frame — re-check
  contrast in **both** themes; `--amber` differs between them by design.
- **Delete** `body.is-incognito .composer__frame` (l.1294–1298), replaced by a topbar-side tint
  keyed off the same body class, so `setIncognito` needs no JS change (decision 5).

Test discipline (follow `ComposerAssetsTests.cs:8–18` verbatim): **every negative is paired
with a positive anchor from the same document** — a whole-file `DoesNotContain` passes
vacuously against a truncated read. AC 6 (`body.is-incognito .composer__frame` gone) and AC 6b
(`composer__incognito` gone from **both** `/` and `/styles.css`) are pure negatives and need
anchors. Record in the class doc that ACs 2/3/3b are **cascade claims a source-text test cannot
fully reach** — it can prove a string is present, not which rule or media query owns it.

**Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~AssetsTests`

### Step 4 — Share availability state + boot seed
**Files:** `js/dom.js`, `js/sharelinks.js`, `js/main.js` · **Tests:** `TopbarActionsAssetsTests.cs` — AC 4

- Export `topbarShareBtn` from `dom.js`, beside the existing share-link exports (l.159–168).
- In `sharelinks.js`: module-local `topbarConvId` / `topbarConvTitle`; exported
  `setTopbarShare(convId, title)` (null ⇒ `disabled = true`); wire the click in
  `wireShareLinks()` to `openShareLink(topbarConvId, topbarConvTitle)`.
- **Add the missing null guard to `openShareLink`** (l.19–25): `createShareLink` and
  `revokeShareLink` both guard `if (!shareLinkConvId) return`, but `openShareLink` does not — a
  null slip renders `“undefined”` and fetches `/api/conversations//share`.
- In `main.js`: import `setTopbarShare` and seed it disabled in the boot-chat block (l.83–85),
  beside `setIncognito(false, true)`. The boot chat is not created via `startNewConversation()`
  (gotchas.md:524), so this seed is load-bearing.

**Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~ShareLinkAssetsTests`

### Step 5 — Drive availability from all four transitions
**Files:** `js/conversations.js`, `js/agents.js`, `js/send.js` · **Tests:** `TopbarActionsAssetsTests.cs` — AC 5, 5b

Per the four-transition table above:

- `startNewConversation()` — `setTopbarShare(null)` beside `setIncognito(false, true)` (l.107).
- `startAgentChat()` — `setTopbarShare(null)` on the fresh-chat path, **after** the continuous
  early-return (agents.js:100–103), near `setActiveId(convId)` (l.105).
- `selectConversation(id)` — `setTopbarShare(id, title)`, title from the `conversations` list
  already reachable via the existing `sidebar.js` import, with a constant fallback.
- `send()` — end of the `try`, success path only, gated on `!ephemeral`, passing the neutral
  constant title. `send.js` already imports `ephemeral` (l.3) and `activeId` (l.6).

Comment each site with *why* (a fresh, agent-fresh or ephemeral chat would share an empty view;
a stale target would share the wrong one), not *what*.

AC 5b specifically pins the `startAgentChat` call — it is the finding this step exists for.

**Verify:** `dotnet test Agent42.slnx`

### Step 6 — Service worker cache bump
**Files:** `sw.js`, `tests/Agent42.Tests/PwaAssetsTests.cs` · **AC 9**

Bump `CACHE` `agent42-shell-v14` → `agent42-shell-v15` **and** the pinned assertion at
`PwaAssetsTests.cs:216` **in this same step** — bumping the assertion before `sw.js` (or after)
leaves a red gate in between. `SHELL` is unchanged (no new module).

> **No new module file is created by this plan.** All new JS lands in existing modules, so
> `SHELL`, `ProjectLayoutTests`' `{ "sharelinks.js", "wireShareLinks" }` map (l.244) and
> `main.js`'s `wireShareLinks()` call (l.55) all stay as they are. If a step tempts you to add
> a module, three guards fire at once.

**Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~PwaAssetsTests`

### Step 7 — Manual verification + docs
**Files:** `CLAUDE.md`, `.agentic/decisions/`, `.agentic/gotchas.md` (as needed)

Run `dotnet run --project tools/Agent42.DevHost` (UI at http://localhost:61326) and confirm the
*manual* ACs at desktop width **and** ≤760px, in **both** themes. Explicitly exercise:
open a chat → click an agent → confirm the share button greys out (finding 1's scenario).

Then record this with `/decide` as a new ADR — it amends ADR 0034 (the toggle "tints the
composer frame while engaged", 0034:49–52) and ADR 0035 (row action as owner UI, 0035:66) — and
add `Amended by` pointers to both, matching the 0042→0035 precedent. Update the CLAUDE.md
bullets for 0034 and 0035 in the same MR (hard rule 2).

**Verify:** `dotnet test Agent42.slnx` · `dotnet format Agent42.slnx --verify-no-changes`

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | Both buttons are in the topbar, not the composer frame | `TopbarActions_LiveInTheTopbar_NotTheComposer` | `TopbarActionsAssetsTests.cs` |
| 2 | The topbar is visible on desktop, not only ≤760px *(cascade-limited)* | `Topbar_IsVisibleOnDesktop` | `TopbarActionsAssetsTests.cs` |
| 3 | The action cluster right-aligns *(cascade-limited)* | `TopbarActions_AreRightAligned` | `TopbarActionsAssetsTests.cs` |
| 3b | Brand + hamburger are mobile-only, so the wordmark isn't doubled *(cascade-limited)* | `TopbarBrandAndMenu_AreMobileOnly` | `TopbarActionsAssetsTests.cs` |
| 4 | Topbar share opens the dialog and creates nothing | `TopbarShare_OpensTheDialog_AndCreatesNothing` | `TopbarActionsAssetsTests.cs` |
| 5 | Share is gated on fresh + ephemeral chats | `TopbarShare_IsGatedOnEmptyAndEphemeralChats` | `TopbarActionsAssetsTests.cs` |
| 5b | Starting an agent chat clears the share target | `TopbarShare_IsClearedWhenAnAgentChatStarts` | `TopbarActionsAssetsTests.cs` |
| 6 | The tint targets the topbar; the composer-frame tint is gone | `IncognitoTint_MovedToTheTopbar` | `TopbarActionsAssetsTests.cs` |
| 6b | The dead `.composer__incognito` rules and class are removed | `ComposerIncognitoStyles_AreGone` | `TopbarActionsAssetsTests.cs` |
| 7 | Incognito still hides under the agent-chat picker lock | `Incognito_StillHidesUnderThePickerLock` | `TopbarActionsAssetsTests.cs` |
| 8 | Row action still works, now with the SVG icon | `ShareAction_WiredIntoConvRow_AndOwnerEndpoint` (existing, unchanged) + `ShareIcon_IsTheSameMarkInBothPlaces` | `ShareLinkAssetsTests.cs` |
| 9 | The shell cache is bumped | `ServiceWorker_CacheNameIsCurrent` (assertion updated) | `PwaAssetsTests.cs` |
| 10 | Buttons stay visible while a long chat scrolls | **manual** (step 7); structural precondition covered by AC 1 | — |
| 11 | Contrast holds in light and dark themes | **manual** (step 7) | — |

**Untestable halves, stated plainly.** ACs 10 and 11 have no automated proof — the repo has no
JS runner or browser driver by design, the treatment ADR 0037 and ADR 0043 both record. ACs 2,
3 and 3b are *partially* provable: a source-text test sees that a declaration exists, not which
rule or media query owns it, so the rendered result is manual too. AC 10's structural
precondition **is** machine-checked (the button is in `.topbar`, outside the scroller). Record
all of this as residuals in the step-7 ADR — and, per ADR 0043's precedent, state in the MR
whether the manual leg was **actually run** rather than implying it.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (never from memory):

| Gate | Threshold |
|---|---|
| `new_issues` | 0 |
| `new_security_issues` | 0 |
| `duplication_pct_max` | 3 |
| `coverage_new_code_pct_min` | 80 |
| `cognitive_complexity_max` | 15 |
| `vulnerable_packages` | 0 (hard block) |
| `outdated_packages` | report (soft) |

Enforcement: `local: soft_warning` (render the table, ask proceed/abort on FAIL);
`ci: hard_block` on build, tests, `dotnet format`, vulnerable packages, doc-path integrity.

Commands: `dotnet test Agent42.slnx` · `dotnet format Agent42.slnx --verify-no-changes` ·
`dotnet build Agent42.slnx`.

Coverage note: this MR is almost entirely HTML/CSS/JS, which coverlet does not measure. The 80%
figure applies to the changed C# (the test files themselves). Do not report a coverage number
that implies the frontend was measured.

Branch: `feature/topbar-actions` (hard rule 1 — never commit to `main`).

## Out of scope

- Any change to what a share link exposes, or to create/copy/revoke inside `#shareLinkDialog`.
  ADR 0035's egress and consent properties are untouched.
- Any change to ephemeral chat *behaviour* — the grain, `_ephemeral`, the `attachments.js`
  document gate, the scheduling refusal. Position only.
- Moving any other composer control (attach, tools, mic, voice) to the topbar.
- Making `.composer__hint` responsive at ≤760px — a known, deliberately unfixed gap (ADR 0043).
- A stop-generation button (ADR 0043 defers this to its own ADR).
- The `.conv__delete` glyph; the mobile drawer's *behaviour* (this plan changes only the
  visibility breakpoint of `.topbar__menu`, not `toggleDrawer`/`closeDrawer` in `drawer.js`);
  and the sidebar beyond `buildConvRow`'s share button.

## Review

A `critic` pass ran against `b0d32e3` before approval. Findings and disposition:

| # | Finding | Disposition |
|---|---|---|
| 1 | **HIGH** — gating missed `startAgentChat`; a stale target could mint a public link to the wrong conversation | **Accepted.** Verified at agents.js:92/105. Four-transition table added, `agents.js` added to affected areas, step 5 + AC 5b + a manual check in step 7. |
| 2 | Step 6/7 inverted — the cache assertion bump preceded the `sw.js` bump, so its own gate was red; tests deferred to a late step | **Accepted.** Merged into one step 6; every test folded into the step that creates the behaviour (hard rule 3). |
| 3 | `send.js` call site self-contradictory, and l.37 is pre-flight so share would enable on failed turns; title source unspecified | **Accepted.** Verified l.37 precedes the l.66 fetch. Moved to the success path with an explicit failure rule and a neutral constant title. |
| 4 | The "import-cycle hazard" rationale is false and contradicts ADR 0041 §5 | **Accepted.** Verified the `sharelinks → send → sidebar → sharelinks` cycle already ships and that ADR 0041:89–91 permits it. Rationale rewritten around the `send.js` setter-slot idiom; the design itself was already right. |
| 5 | `.topbar__brand` would double the wordmark on desktop; stale CSS section comment | **Accepted** — found independently during planning and already fixed; the stale comment at styles.css:1824–1826 added to step 3. |
| 6 | `main.js` missing from affected areas | **Accepted.** Added to the table and to step 4. |
| 7 | Cascade ACs oversold; removal-negative anchoring discipline not carried over; no AC for deleting the dead rules | **Accepted.** ACs 2/3/3b marked cascade-limited, anchoring rule quoted into step 3, AC 6b added, "rename, don't copy" made explicit. |
| 8 | AC 1 needs the `IndexOf` ordering idiom — `Body()` cannot slice HTML | **Accepted.** Named in step 1 with its exemplars. |
| 9 | Two stale line refs (`startNewConversation` 91→92; `setIncognito` ends 243 not 240) | **Accepted.** Corrected. |
| 10 | "Out of scope" contradicted steps that touch drawer chrome and the sidebar | **Accepted.** Narrowed to behaviour-vs-visibility. |
| 11 | `#topbarShareBtn` not `disabled` in markup; `openShareLink` lacks a null guard | **Accepted.** Both in steps 1 and 4. |

No findings were rejected. The critic also confirmed clean: the non-scrolling-sibling
structural claim, the usage overlay still covering the topbar, the CACHE/SHELL reasoning,
`ShareAction_WiredIntoConvRow_AndOwnerEndpoint` surviving step 2, and that ADR 0034/0035 are
the correct two amendments.

## Divergences from the plan (recorded at execution)

Executed on `feature/topbar-actions` from `main @ b0d32e3` — no drift; the spec's
`planned-against` ref matched HEAD exactly. All seven steps were implemented in order,
sequentially rather than dispatching steps 1–2 to parallel subagents (both are small and
touch adjacent files; a worktree round-trip would have cost more than it saved).

**Naming and shape choices the plan left open**

1. **One class for both buttons, not one per button.** The plan said to rename
   `.composer__incognito` to "the new topbar class" without naming it. Both buttons share a
   single `.topbar__action`, so the ported rest/hover/active/focus-visible/`.is-active`/
   `:disabled` block is written once — which is what keeps `duplication_pct_max: 3` honest.
2. **`:hover`/`:active` gained a `:not(:disabled)` guard** that `.composer__incognito` never
   had. The incognito toggle is *hidden* when inert, but topbar share is *disabled and
   visible* (decision 6), so without the guard an inert button would still light up amber
   under the cursor and claim to be pressable.
3. **`.conv__share svg` is 14×14, not 16×16.** The plan said to centre "a 16×16 SVG in the
   22×22 box"; at 16px the mark crowds the hover background, so the shared markup stays
   16×16 (identical string in both places, which is what AC 8 pins) and the row scales it
   down in CSS. The now-meaningless `font: 400 13px/1 var(--mono)` was dropped.
4. **Desktop topbar padding is `8px 16px`**, not the `12px 16px` ported from the ≤760px
   block. The plan flagged this as something to check: the mobile bar is sized around a 30px
   `.topbar__mark`, and with only two 34px buttons the taller padding made the bar heavier
   than the sidebar header it sits beside. Consequence for step 3's instruction to "remove
   the now-redundant `.topbar` rule from the media query": it is **not** removed, but reduced
   to a padding-only override (`.topbar { padding: 12px 16px; }`), because the two widths now
   genuinely differ. Mobile therefore renders exactly as before.

**Tests**

5. **AC 7 was implemented in step 1**, not a step of its own — the plan's AC table lists it
   but no step claimed it. It is verifiable the moment the button moves, so it lives beside
   AC 1.
6. **One test beyond the AC table: `TopbarShare_ShipsDisabledInMarkup`.** Review finding 11
   required `disabled` in the markup but the AC table gave it no criterion; it is pinned
   rather than left to a code reading, and it also asserts *not* `hidden` (decision 6).

**Execution incidents**

7. **The CRLF gotcha (`gotchas.md:1342`) was tripped and repaired.** The scripted edits read
   in text mode and wrote with `newline=''` — `newline` on the write end only, which is
   exactly the half-fix the gotcha warns about — silently converting every touched file to
   LF. Caught from git's `LF will be replaced by CRLF` warnings on `git diff --stat`; all 18
   touched files were converted back to CRLF in binary mode, and `dotnet format` plus the
   asset/layout suite were re-run afterwards. The committed diff is unaffected (git
   normalises), but the working tree would have diverged from a fresh clone.

**Step 7 went further than the plan asked**

8. **The manual leg was actually run, and measured rather than eyeballed.** The plan called
   for DevHost plus a human browser. No browser extension was connected, but headless Chrome
   driven over the DevTools protocol was available, so every "cascade-limited" and "manual"
   AC (2, 3, 3b, 6, 10, 11) was verified against computed styles and hit-testing on the
   running app, at 1400×900 and 500×800, in both themes — including finding 1's scenario
   end to end (open a chat, click an agent, observe the button both disabled *and* inert).
   The measured values are recorded in ADR 0044's consequences. This is the one place this
   MR is stronger than ADR 0043's precedent, which had to record both interactive legs as
   unverified.
10. **A `critic` pass ran on the finished diff** and returned no HIGH findings. Four were
    fixed in-branch: `send()` was passing the neutral title unconditionally and would blank an
    already-named chat's title on its second turn (it now does the same `conversations` lookup
    `selectConversation` does); a vacuous `DoesNotContain` in the AC-4 test was deleted (the
    real guard is the anchored no-`POST` assertion beside it); the icon-parity assertion was
    bounded at both ends rather than only below; and the "all 47 source-text guards" count in
    `gotchas.md` + `CLAUDE.md` — which this MR invalidated by adding ten assertions — was
    replaced with a phrasing no MR can falsify. Two doc gaps were closed (how to re-drive the
    headless leg; this padding deviation). One finding was **accepted and deliberately not
    fixed**: `startAgentChat` never resets incognito, so an agent chat started from an
    incognito fresh chat inherits `ephemeral: true` with its toggle hidden. It predates this
    MR, is neither caused nor worsened by relocating the tint, and choosing the right
    behaviour is a decision about ADR 0034 rather than a mechanical fix — recorded in ADR
    0044 for whoever touches either feature next.

9. **A gotcha entry was added** (the plan said "as needed"): the four-transition rule with
   `startAgentChat` as the one that bites, and the "never give `.topbar` a z-index" trap
   together with the inverted mobile-only visibility of `.topbar__brand`/`.topbar__menu`.
