# Plan — long-press to reveal a sidebar row's actions on touch

planned-against: main @ 7d04bf7

## Overview

On the installed PWA the pin / share / delete glyphs sit permanently lit on **every** sidebar row
(ADR 0045's `@media (hover: none)` block), which makes the transmissions list noisy. Replace that
unconditional reveal with an explicit **long-press gesture**: press and hold a row for ~400 ms and that
one row reveals its three actions.

Why now: the user reports the always-on icons as the visible defect, and recalls the pre-0045 behaviour
(where Android Chrome's press-and-hold happened to apply `:hover`) as the wanted one. This makes that
behaviour deliberate and browser-independent instead of an emulation side effect.

**Scope is touch only — meaning no change to what a desktop user sees or can do.** It does *not* mean
the desktop selectors go unedited: the three `:hover` reveals must be gated into `@media (hover: hover)`
or the gesture is decorative (see Context, finding 1). Gating them changes which UAs match, not what a
hovering pointer gets.

## Affected areas

Verified against the tree at `7d04bf7`:

- `src/Agent42.Backend/wwwroot/styles.css` — the `@media (hover: none)` block at **line 353** (the only
  `hover: none` block in the file); the three unconditional hover-reveal pairs at **265-266**
  (`.conv__delete`), **299-300** (`.conv__share`), **332-333** (`.conv__pin`); `.conv` at **206** with
  `padding: 10px 82px 9px 14px` at **210**.
- `src/Agent42.Backend/wwwroot/js/sidebar.js` — `buildConvRow(conv, hoisted)` (li assembled at line 121,
  `li.append(btn, pin, share, del); return li;`), the three action handlers at 89 / 107 / 117,
  `renderSidebar` (line 167), `setPinned`'s re-render (line 237), `deleteConversation`'s `confirm` (243).
- `src/Agent42.Backend/wwwroot/sw.js` — `const CACHE = "agent42-shell-v21";` at line 5.
- `tests/Agent42.Tests/PinChatAssetsTests.cs` —
  `PinnedState_IsVisibleWithoutHover_AndReachableOnTouch` at line 96.
- `tests/Agent42.Tests/PwaAssetsTests.cs` line 215 — the `agent42-shell-v21` literal, inside
  **`ServiceWorker_CacheNameIsCurrent`** (line 205).
- New: `tests/Agent42.Tests/RowActionsRevealAssetsTests.cs`.
- New: `.agentic/decisions/0052-long-press-reveals-row-actions.md` (amends 0045).
- `.agentic/gotchas.md` — the pinned-conversations entry, hover paragraph at ~1719-1723 and its
  `**Evidence:**` block at ~1735; plus a **new** entry for the gesture (see step 6).
- `CLAUDE.md` — the ADR 0045 bullet's "permanently" claim, and a new ADR 0052 bullet.

## Context & constraints

**Decisions taken in the interview — do not relitigate:**

1. **Touch only**, in the behavioural sense defined in the Overview. (Rejected: revealing on hover *and*
   on selection at all pointer types.)
2. **Explicit long-press in JS**, not a revert to pure `:hover`. (Rejected: deleting the `hover: none`
   block and relying on Android Chrome applying `:hover` on press-and-hold — emulated behaviour ADR 0045
   judged unreliable, and it sticks until the user taps elsewhere.) Also rejected: revealing only the
   *open* conversation's row, which would force opening a chat to delete it.
3. **`.conv-row.is-pinned .conv__pin { opacity: 1 }` stays.** The lit pin is a *state* indicator — the
   only thing distinguishing a hoisted row from a merely recent one (ADR 0045). On touch a pinned row
   therefore still shows one icon when unrevealed, and it stays tappable (see finding 3).

**Investigation findings the executor cannot cheaply re-derive:**

1. **The `:hover` reveals are ungated, and `(hover: none)` UAs still match `:hover`.**
   `styles.css:265-266, 299-300, 332-333` sit outside every media query. Android Chrome applies `:hover`
   on tap and on press-and-hold and *keeps it* until the user taps elsewhere. Removing the unconditional
   reveal inside `@media (hover: none)` therefore fixes nothing on its own: a plain tap would still light
   all three icons, and the 400 ms timer would be invisible because the row is already `:hover`ed.
   **The three `:hover` reveals must move into `@media (hover: hover)`.** Split them from their
   `:focus-visible` partners, which stay **ungated** — keyboard reveal must survive on every UA.
2. **`opacity: 0` removes the paint, not the hit-test.** The three actions are `position: absolute`
   siblings appended *after* `btn` (`sidebar.js:121`), overlaying the 82 px right strip `.conv`'s padding
   reserves (`styles.css:210`). ADR 0045's manual pass proves the hit-test order live: a pure
   `Input.dispatchTouchEvent` tap on the pin control pinned that chat
   (`0045-pinned-conversations.md:181-183`). After this change they would be invisible **and still
   tappable** — a thumb on the right edge of any row would fire `deleteConversation`'s `confirm`
   (`sidebar.js:243`) from a control nobody can see. **The unrevealed state needs
   `pointer-events: none`, not just `opacity: 0`.**
3. **…and the lit pin must be exempted from that.** `.conv-row.is-pinned .conv__pin` is visible, so it
   must stay `pointer-events: auto` or it becomes a control that looks tappable and is not — strictly
   worse than either alternative.
4. **Android fires `pointercancel` for its *own* long-press** (context menu / selection) at ~500 ms —
   roughly 100 ms *after* our 400 ms timer. So `pointercancel` must cancel only a **pending** timer and
   must **never** tear down a reveal that has already fired, or the reveal flashes and dies on the
   primary target platform. `contextmenu.preventDefault()` is reactive and does not retract a
   `pointercancel`; the `user-select`/`-webkit-touch-callout` suppression is what discourages the native
   gesture in the first place.
5. **There are no pointer/touch handlers anywhere in `wwwroot/js/`** — verified across all 28 modules.
   This is the first, so there is no in-repo exemplar for the gesture. The nearest idiom is the lazily
   registered document listener in `providers.js:271-277` — note it removes itself **only when the click
   lands outside**, so an inside click leaves it attached and a re-open would stack another. Register the
   reveal's document listener at most once (guard on `revealedRow === null`) rather than copying that
   shape verbatim.
6. **`styles.css` uses no `touch-action` anywhere, and must not gain one here.**
   `touch-action: none` on `.conv-row` would kill scrolling of the conversation list.
7. **Every `activeId` transition re-renders the sidebar** (`conversations.js:35`, `conversations.js:117`,
   `agents.js:121`), as does `setPinned` (`sidebar.js:237`) and the post-turn `refreshConversations()`
   (`send.js:231`). A re-render destroys the revealed node, so the reveal state must be reset in
   `renderSidebar`.
8. **`:has()` is forbidden by an existing assertion** —
   `PinChatAssetsTests.cs:109`, `Assert.DoesNotContain(":has(", css)`. Mark the revealed row with a class
   from JS, as ADR 0045 marks `.is-pinned-last`.
9. **The CDP lever for the manual pass** (ADR 0045 recorded this after a false negative cost a pass):
   `Emulation.setEmulatedMedia` with `hover`/`pointer` features does **not** flip
   `matchMedia("(hover: none)")` in Chrome. Use `Emulation.setTouchEmulationEnabled` **plus mobile device
   metrics**. Drive real touch with `Input.dispatchTouchEvent` (touch events only, no mouse).
10. **`.conv-row` is the containing block** for the three absolutely-positioned actions, whose `top: 50%`
    resolves against its **padding** box — never add `padding` to `.conv-row` (ADR 0045's divider trap).
11. The repo has **no JS runner by design**; every frontend test is a source-text assertion over
    `StaticAssetsServerFixture.GetAppScriptAsync()` (which walks the import graph from `main.js` over
    HTTP) or `GetStringAsync("/styles.css")`. `sidebar.js` is reachable from `main.js`.
12. **The coverage gate sees nothing here.** `check-new-code-coverage.mjs:49-50` gates
    `src/**/*.cs` only and excludes test projects by design. This change adds **zero** production C#, so
    the script has no gated lines. Report what it prints; do not reason about it.

**Governing docs to read first:** `.agentic/decisions/0045-pinned-conversations.md` (especially line 96,
"The hover-only row actions were unreachable on touch", the rejected-alternatives list at 222, and the
manual-pass section at 163); `.agentic/gotchas.md` ~1700-1740; ADR 0041 — **every MR touching
`wwwroot/js/` must bump `CACHE` in `sw.js`**, or a cache-first stale module boots a blank app.

**Design shape** (settled here so the executor does not re-invent it):

*CSS.* Gate the three `:hover` reveals in `@media (hover: hover)`, leaving `:focus-visible` ungated.
Rewrite the `@media (hover: none)` block to:
- `.conv__pin, .conv__share, .conv__delete { pointer-events: none; }` — unrevealed actions are inert.
- `.conv-row.is-revealed .conv__pin/.conv__share/.conv__delete { opacity: 1; pointer-events: auto; }`
- `.conv-row.is-pinned .conv__pin { pointer-events: auto; }` — the lit state indicator stays tappable.
- `.conv-row { -webkit-touch-callout: none; user-select: none; }` — the hold must not raise a selection
  UI. Keep `hover: none` as the media condition. Add **no** `touch-action`, add **no** `padding`.

*JS, in `sidebar.js`.* Module-level `let revealedRow = null; let swallowNextClick = false;` plus
`clearReveal()` and `wireRowReveal(li, btn)` called from `buildConvRow` before `return li`.
- `pointerdown` on the `li`: first, `swallowNextClick = false` (so a stranded flag can never outlive one
  press). Ignore unless `e.pointerType !== "mouse"` — a mouse has hover, so desktop never enters the
  gesture even on a hybrid device. Start a 400 ms timer, kept **below** Android's ~500 ms native
  context-menu threshold so our reveal wins the race.
- `pointermove` past a small threshold, `pointerup`, and `pointercancel` clear the **pending timer only**
  — each returns early if the timer has already fired (finding 4).
- Timer fires → `clearReveal()`, add `is-revealed` to this `li`, `revealedRow = li`,
  `swallowNextClick = true`, and register the document listener if one is not already registered.
- **Capture-phase `click` on `btn`, not on `li`** — `btn` is the only node whose click means "open the
  chat"; a capture listener on `li` would also eat the revealed pin/share/delete, whose own
  `stopPropagation` runs later in the bubble phase (`sidebar.js:89, 107, 117`). Behaviour: if
  `swallowNextClick`, clear the flag and swallow (this is the click that *ended* the long press — the
  reveal must survive it); else if `revealedRow === li`, `clearReveal()` and swallow (a second tap on the
  row body dismisses); else let it through.
- `contextmenu` on the `li` is `preventDefault()`ed while that row is revealed.
- The document `pointerdown` listener clears only when the target is **outside** `revealedRow`, so a tap
  on that row's own actions still lands; it removes itself when it clears.
- `renderSidebar` calls `clearReveal()` first.

## Steps

Sequential — steps 1-3 touch adjacent regions of the same two files. Each step lands **with its tests**
(hard rule 3); no step defers verification to a later one.

**Step 1 — CSS.**
Files: `src/Agent42.Backend/wwwroot/styles.css`; new
`tests/Agent42.Tests/RowActionsRevealAssetsTests.cs` (the CSS assertions: AC1, AC3, AC4).
Gate the three `:hover` reveals into `@media (hover: hover)`, split from their ungated `:focus-visible`
partners. Rewrite the `hover: none` block per the design shape, including `pointer-events`. Rewrite the
block's leading comment — it currently explains the permanent reveal and would become a lie.
Verify: `dotnet test Agent42.slnx --filter "RowActionsRevealAssetsTests|PinChatAssetsTests"`.

**Step 2 — the gesture.**
Files: `src/Agent42.Backend/wwwroot/js/sidebar.js`; the JS assertions in
`RowActionsRevealAssetsTests.cs` (AC2, AC5, AC6, AC7).
Implement `revealedRow` / `swallowNextClick` / `clearReveal()` / `wireRowReveal(li, btn)`; call it from
`buildConvRow` before `return li`; call `clearReveal()` at the top of `renderSidebar`.
Verify: `dotnet test Agent42.slnx --filter RowActionsRevealAssetsTests` — a real gate, unlike a bare
`dotnet build`, which cannot fail on a JS-only edit.

**Step 3 — shell cache bump.**
Files: `src/Agent42.Backend/wwwroot/sw.js` line 5 (`v21` → `v22`);
`tests/Agent42.Tests/PwaAssetsTests.cs` line 215 (the matching literal in
`ServiceWorker_CacheNameIsCurrent`).
Verify: `dotnet test Agent42.slnx --filter PwaAssetsTests`.

**Step 4 — repoint the ADR 0045 guard.**
Files: `tests/Agent42.Tests/PinChatAssetsTests.cs`.
Update `PinnedState_IsVisibleWithoutHover_AndReachableOnTouch`: its comment now describes the long-press.
**Keep** `.conv-row.is-pinned .conv__pin` and `DoesNotContain(":has(")`. **Replace**
`Assert.Contains("hover: none", css)` — after this change that string proves nothing about what the block
does; assert the `.is-revealed`-scoped rule instead. Add a `DoesNotContain` pinned to the **exact**
old literal (`".conv__pin,\n  .conv__share,\n  .conv__delete {"`, matching the file's real whitespace) —
a loose substring would collide with the new rule's own `.conv__pin,` and pass vacuously, the trap
`PinChatAssetsTests.cs:110` already guards against with an exact literal.
Verify: `dotnet test Agent42.slnx`.

**Step 5 — the manual pass (required; there is no JS runner).**
DevHost (`dotnet run --project tools/Agent42.DevHost`, http://localhost:61326), headless Chrome over CDP,
using the lever in finding 9. Seed ≥3 conversations, one pinned. Record measured values, and read
`li.classList` **alongside** computed style on every leg so a reveal is attributable to `is-revealed`
rather than to sticky `:hover`:
(a) unrevealed touch row → three actions `opacity: 0`, `pointer-events: none`; a pinned row's pin
`opacity: 1`, `pointer-events: auto`;
(b) **short tap → still `opacity: 0`** — the leg that catches a regression of finding 1, and the one
sticky `:hover` would fail;
(c) a short tap opens the chat;
(d) 400 ms+ hold → that row's three actions `opacity: 1` and `is-revealed` present;
(e) the hold does **not** open the chat;
(f) the reveal survives ≥1 s after the hold (finding 4's `pointercancel` race);
(g) tapping the revealed row's delete/pin actually fires it (finding 2's `pointer-events` flip);
(h) holding a second row clears the first; tapping outside clears;
(i) a touch-drag over a row scrolls the list and does not reveal;
(j) desktop with no touch emulation — hover-reveal and `:focus-visible` unchanged.
Do not commit the driver. Values go into ADR 0052 verbatim; a leg that cannot be run is reported as such,
never estimated.

**Step 6 — docs, same MR.**
New `.agentic/decisions/0052-long-press-reveals-row-actions.md`, front-matter `amends: [0045]`. It must
**argue, not inherit**, the two things ADR 0045 decided and this reverses:
- 0045:222 rejected "leaving the touch gap as a residual" because pinning would be impossible while
  unpinning worked. Under locked decision 3 an asymmetry returns in gesture form (lit pin = one tap to
  unpin; hidden pin = a hold to pin). The ADR must state the rebuttal it rests on — every *visible*
  control is tappable and every *hidden* one has one documented gesture — rather than leave it implicit.
- 0045:101-102 justified the permanent block by Android/Chromium being a first-class surface (ADR 0023
  share target, ADR 0028 push). That argument is unchanged and still true; the ADR must say why a
  gesture satisfies it where 0045 concluded only always-on visibility would.
It must also record: long-press is **undiscoverable** (no affordance hints at it) — the accepted cost of
a quiet list; `user-select: none` costs long-press-to-copy on conversation titles on touch; a
`renderSidebar` after `setPinned` drops the reveal, so "hold → pin → share" needs a second hold; and
`.agent__action` / `.msg__copy` remain touch-unreachable (0045's residual, still unfixed).
Also: `.agentic/gotchas.md` — amend the 0045 hover paragraph and its `**Evidence:**` list, **and add a
new entry** for the gesture's own traps (findings 1, 2 and 6 — an archived spec cannot carry them);
`CLAUDE.md` — fix the "permanently" claim in the ADR 0045 bullet and add an ADR 0052 bullet.
Verify: the doc-path integrity CI job.

**Step 7 — gates, then MR.**
`dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`,
`dotnet build Agent42.slnx`, `node .agentic/scripts/check-new-code-coverage.mjs`.
Branch `fix/long-press-row-actions` off `main`; open the PR.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | On touch an unrevealed row's actions are hidden **and inert** — the reveal is `.is-revealed`-scoped, and `pointer-events: none` applies unrevealed | `TouchReveal_IsScopedToTheRevealedRow_AndUnrevealedActionsAreInert` | `RowActionsRevealAssetsTests.cs` |
| AC2 | A ~400 ms press-and-hold on a row reveals its actions | `LongPress_IsWiredIntoConvRow` | `RowActionsRevealAssetsTests.cs` |
| AC3 | Desktop is untouched: the `:hover` reveals still exist, gated to `(hover: hover)`, and `:focus-visible` stays ungated; the gesture ignores mouse pointers | `HoverReveal_SurvivesGatedToPointerDevices` + `LongPress_IgnoresMousePointers` | `RowActionsRevealAssetsTests.cs` |
| AC4 | The list still scrolls: the gesture cancels on move/cancel and the sheet has no `touch-action` | `LongPress_CancelsOnMoveAndCancel_AndNeverDisablesTouchAction` | `RowActionsRevealAssetsTests.cs` |
| AC5 | A completed hold does not also open the chat, and the suppression is scoped to the row button | `LongPress_SuppressesTheFollowingClick_OnTheRowButtonOnly` | `RowActionsRevealAssetsTests.cs` |
| AC6 | The reveal is dropped on re-render and on a pointerdown outside the row | `Reveal_IsClearedOnRerenderAndOnOutsidePointerDown` | `RowActionsRevealAssetsTests.cs` |
| AC7 | A pinned row still shows its lit pin when unrevealed **and it remains tappable** | `PinnedState_IsVisibleWithoutHover_AndReachableOnTouch` (updated) | `PinChatAssetsTests.cs` |
| AC8 | The old unconditional touch reveal is gone (exact-literal negative) | `PinnedState_IsVisibleWithoutHover_AndReachableOnTouch` (updated) | `PinChatAssetsTests.cs` |
| AC9 | The shell cache is bumped so clients pick up the new module | `ServiceWorker_CacheNameIsCurrent` (existing, literal updated) | `PwaAssetsTests.cs` |
| AC10 | Runtime behaviour: a hold reveals and survives; a **short tap does not reveal**; a tap opens; a drag scrolls; a revealed action fires | **Not automated** — no JS runner. Manual pass, step 5, measured values in ADR 0052 | — |

Every automated row above is a **source-text** assertion: it proves the code *says* the right thing,
never that a browser *does* it. AC10 is where the behaviour is actually established.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (`7d04bf7`):

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | 15 | `SonarAnalyzer.CSharp` S3776 at build time — an **error** since ADR 0051, so a breach fails `dotnet build` |
| `vulnerable_packages` | 0 | CI package-freshness job |
| `outdated_packages` | report (soft) | CI |

This change adds no production C#, and the coverage script gates `src/**/*.cs` only
(`check-new-code-coverage.mjs:49-50`), so it will see zero gated lines. Report its actual output.

## Out of scope

- `.agent__action` and `.msg__copy` remain touch-unreachable (ADR 0045's recorded residual).
- No discoverability affordance (no hint text, no edit-mode toggle, no swipe actions).
- No change to the pinned two-pass render, the pin endpoint, or any server code.
- No change to what a hovering pointer sees or can do.
- No JS test harness is introduced.

## Review

A `critic` subagent reviewed this plan at `7d04bf7`. It verified every path, line number and gate
threshold as correct, and raised eleven findings. All eleven were accepted; none was rejected.

| # | Finding | Handling |
|---|---|---|
| S1 | **Blocking.** The `:hover` reveals are ungated, so touch still triggers them and the gesture is decorative | Finding 1; Overview reframed ("touch only" = behaviour, not selectors); step 1 gates them in `(hover: hover)`; AC3 + manual leg (b) |
| S2 | **Blocking.** `opacity: 0` actions stay hit-testable — an invisible `confirm()`-firing delete on every row | Finding 2; `pointer-events: none` in the design shape; AC1 + manual leg (g) |
| S3 | Android's own long-press fires `pointercancel` ~100 ms after our timer | Finding 4; cancellation restricted to a pending timer; manual leg (f) |
| S4 | `swallowNextClick` had no lifetime; the capture listener was on the wrong node | Design shape: listener on `btn`, flag cleared on the next `pointerdown` and on the swallowed click; AC5 |
| S5 | ADR 0052 must rebut what 0045 actually decided, not just note new residuals | Step 6 now names both 0045 passages and requires an argument |
| S6 | `ServiceWorker_CachesTheShell` does not exist; AC1/AC3 unprovable as mapped; the negative assertion would collide | Corrected to `ServiceWorker_CacheNameIsCurrent`; AC3 split and given a CSS assertion; step 4 pins an exact literal |
| S7 | Tests deferred to a late step, against hard rule 3 | Steps 1-4 each land their own assertions; step 2's gate is a test, not `dotnet build` |
| S8 | The manual pass could not distinguish the gesture from sticky `:hover` | Step 5 adds the short-tap leg (b), the survival leg (f), the action-fires leg (g), and `classList` readings throughout |
| S9 | `providers.js` exemplar mischaracterised — it leaks listeners on inside clicks | Finding 5 corrected; the reveal registers at most once |
| S10 | Doc debt: no ADR 0052 bullet in CLAUDE.md, no new gotcha | Step 6 requires both |
| S11 | Unrecorded costs: `user-select`, reveal lost after pin, coverage reasoning wrong | Costs moved into step 6's ADR obligations; finding 12 corrects the coverage note |
