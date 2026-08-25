---
id: 0052
date: 2026-08-25
status: accepted
tags: [frontend, sidebar, ux, css, pwa, touch, a11y]
supersedes: []
extends: []
amends: [0045]
---

# A long press, not a permanent reveal, shows a sidebar row's actions on touch

## Context
ADR 0045 added a pin control to every sidebar row and, in the same MR, fixed a latent gap: the row
actions (`.conv__pin`, `.conv__share`, `.conv__delete`) are `opacity: 0` hover-reveals, and a phone has
no hovering pointer. Its fix was an `@media (hover: none)` block lighting all three **permanently**.

That works, and it is noisy. On the installed PWA every row in the transmissions list carries three lit
glyphs at all times, so the list reads as a wall of controls rather than a list of conversations. The
user reported exactly this, and recalled the *pre*-0045 behaviour — press and hold a row and its actions
appeared — as the one they wanted. That behaviour was never designed: it was Android Chrome applying
`:hover` on a press-and-hold, an emulation side effect ADR 0045 judged (correctly) too unreliable to
depend on.

So the goal is to make that gesture real and deliberate, rather than to restore an accident.

## Decision
On touch, a **~400 ms press-and-hold** on a conversation row reveals that row's three actions. The list
is otherwise quiet. Desktop is unchanged: a hovering pointer still gets the hover-reveal, and keyboard
users still get `:focus-visible`.

Three scoping decisions, taken up front:

1. **Touch only, in the behavioural sense.** A mouse never enters the gesture — it already has hover, and
   giving a hybrid laptop both would be strictly worse than either. This does *not* mean the desktop
   selectors go unedited; see the load-bearing section below.
2. **The gesture is explicit JS, not CSS.** Rejected: deleting the `hover: none` block and letting
   Android's `:hover` emulation do the work. It is the mechanism 0045 already rejected, it sticks until
   the user taps elsewhere, and it varies by browser. Also rejected: revealing only the *open*
   conversation's row, which would force a user to open a chat in order to delete it.
3. **`.conv-row.is-pinned .conv__pin` is untouched.** The lit pin is a *state* indicator, not an action —
   the only thing marking a hoisted row as user-placed rather than merely recent (ADR 0045). It stays
   visible, and hit-testable, whether or not the row is revealed.

### The load-bearing part, and it is not the gesture: `(hover: none)` UAs still match `:hover`
The three reveals were written unconditionally:

```css
.conv-row:hover .conv__delete,
.conv__delete:focus-visible { opacity: 1; }
```

A UA that reports `(hover: none)` still applies `:hover` when it decides an element is hovered, and
Android Chrome does so on tap **and** on press-and-hold, keeping it until the user taps elsewhere.
Removing the always-on rule inside `@media (hover: none)` therefore fixes nothing on its own: a plain tap
would still light all three icons, and the 400 ms timer would be *unobservable* because the row is
already `:hover`ed by the time it fires. The feature would appear to work, for the wrong reason.

The three `:hover` reveals are therefore gated into **`@media (hover: hover)`**, split from their
`:focus-visible` partners, which stay **outside** every media query so keyboard reveal survives on every
device. This was caught in adversarial review of the plan, not in testing — and the measured short-tap
leg below is the one that would have caught it.

### The second half: `opacity: 0` removes the paint, not the hit-test
The three actions are `position: absolute` siblings appended *after* the row button, overlaying the 82 px
strip `.conv`'s `padding-right` reserves. ADR 0045's own manual pass proved the hit-test order live: a
pure `Input.dispatchTouchEvent` tap on the pin control pinned that chat.

Hidden-but-live means a thumb landing on the right edge of any row would fire `deleteConversation`'s
`confirm()` from a control nobody can see. Today that is masked because the controls are visible on touch;
after this change it would not be. So the unrevealed state carries **`pointer-events: none`**, and
`.conv-row.is-pinned .conv__pin` is exempted back to `auto` — it is visible, so it must remain pressable
or it becomes a control that *looks* actionable and is not, which is worse than either alternative.

Note what is deliberately absent: no `touch-action` anywhere near `.conv-row`. Disabling touch gestures
there would stop the conversation list scrolling altogether. The press is cancelled from JS instead.

### Three implementation traps
- **`pointercancel` cancels only a *pending* timer.** Android fires `pointercancel` when it recognises
  its own long-press at ~500 ms — a good 100 ms *after* our 400 ms reveal. Treating it as "dismiss" would
  make the reveal flash and die on the one platform this exists for. Our 400 ms threshold is chosen to sit
  below that native threshold so our reveal wins the race.
- **The click-suppression listener sits on the row button, not the `<li>`.** The three actions are the
  `<li>`'s children, so an `<li>` capture listener would intercept their clicks too — and their own
  `stopPropagation` runs later, in the bubble phase, so it could not save them. `btn` is the only node
  whose click means "open this chat".
- **`swallowNextClick` is cleared on the next `pointerdown` as well as by the click it suppresses.** A
  press that ends off-row dispatches no click, and a stranded flag would silently eat the following tap.

### The reveal is transient by design
`renderSidebar` calls `clearReveal()` first, because it rebuilds every row and the revealed node goes with
them. A press *outside* the revealed row dismisses; a press *inside* deliberately does not, or tapping the
revealed row's own pin/share/delete would be eaten by the dismissal. A second tap on the revealed row's
body dismisses rather than navigating.

## Consequences
- **Long-press is undiscoverable.** Nothing on screen hints that the gesture exists. This is the accepted
  cost of a quiet list, and it is the single biggest objection to this change. It is mitigated only by the
  gesture being the platform-conventional one for "act on a list item".
- **Every visible control is still tappable; only hidden ones need the gesture.** ADR 0045 rejected
  leaving the touch gap as a residual because pinning would have been impossible while *un*pinning worked
  — an asymmetry between two states of the same control. That asymmetry does return in gesture form (a
  pinned row's lit pin is one tap; an unpinned row's hidden pin needs a hold), and the rebuttal this rests
  on is that the two cases are no longer *unreachable* versus *reachable* but *immediate* versus *one
  documented gesture away*. Nothing is unreachable on touch that was reachable before.
- **ADR 0045's first-class-surface argument still holds, and is satisfied differently.** Android/Chromium
  remains a first-class surface (ADR 0023 share target, ADR 0028 push). 0045 concluded only always-on
  visibility would serve it because it had no gesture available; with one, the actions remain reachable
  on touch — which was the actual requirement — without every row paying for it visually.
- **`user-select: none` on `.conv-row` costs long-press-to-copy on conversation titles on touch.** Titles
  are server-derived summaries, not user-authored text, so this is judged cheap; it is also unavoidable,
  since the selection callout is precisely what the hold would otherwise raise.
- **A reveal does not survive the action taken from it.** `setPinned` re-renders the sidebar, so
  "hold → pin → share" needs a second hold. Accepted; the alternative is preserving reveal state across a
  re-render, which is more machinery than the case is worth.
- **Screen readers are unaffected.** `opacity: 0` does not remove an element from the accessibility tree
  and `pointer-events: none` does not affect programmatic activation, so the three actions remain
  announced and operable by assistive tech regardless of reveal state.
- **`.agent__action` and `.msg__copy` remain touch-unreachable** — ADR 0045's recorded residual, still
  unfixed here for the same reason it gave: they belong to features whose touch behaviour this MR's manual
  pass did not exercise.

## Alternatives rejected
- **Revert to pure `:hover`** (what the user remembered). Emulated, browser-dependent, sticky. See
  decision 2.
- **Reveal only the open conversation's row.** Forces opening a chat to delete it.
- **A persistent edit-mode toggle** in the sidebar header. Discoverable, but adds durable UI chrome and a
  mode to every user's sidebar to serve a gesture that is conventional on the platform.
- **Swipe-to-reveal.** Conflicts with the drawer's own horizontal gesture at ≤760px.

## What the automated tests can and cannot reach
Every frontend assertion in `RowActionsRevealAssetsTests` is a source-text heuristic — the repo has no JS
runner by design. They prove the shipped code *says* the right thing: the `.is-revealed` scoping, the
`pointer-events` flip, the `(hover: hover)` gating, the 400 ms constant, the pointer-type gate, the
cancel-only-if-pending guard, the listener node, the `clearReveal()` in `renderSidebar`, and the absence
of a `touch-action` declaration. They prove **none** of the behaviour. That is the manual pass.

## The manual pass was run, and these are the measured values
Driven as in ADR 0044/0045 — DevHost on `localhost:61326`, headless Chrome over the DevTools protocol
(throwaway driver, deliberately not committed). Nine seeded conversations, several pinned.

**The lever, again** (0045 recorded a false negative from getting this wrong):
`Emulation.setEmulatedMedia` with `hover`/`pointer` features does not flip `matchMedia`. Touch emulation
plus mobile device metrics does — measured here as `matchMedia("(hover: none)") === true` and
`("(hover: hover)") === false`. **One addition to 0045's note:** the viewport must be **wider than 760 px**
(900×800 was used). At 390 px the sidebar is an off-canvas drawer, every synthetic touch lands on nothing,
and the first run produced a clean false negative — no reveal on any leg — that looked exactly like a
broken feature.

- **(a) At rest on touch.** Unpinned rows: pin / share / delete all `opacity: 0`, `pointer-events: none`.
  Pinned rows: pin `opacity: 1`, `pointer-events: auto`; share and delete `opacity: 0`,
  `pointer-events: none`. Exactly the intended split.
- **(b) A short tap does not reveal.** After a 120 ms touch, all three stayed `opacity: 0` and no row
  carried `is-revealed`. **This is the leg that proves sticky `:hover` is gone** — before the
  `(hover: hover)` gating it would have lit the row.
- **(c) A short tap opens the chat.** Active row index went `-1 → 5`; revealed row `-1`. Instrumenting the
  button showed `pointerdown / pointerup / click`, all with `defaultPrevented === false`.
- **(d) A 600 ms hold reveals that row, and only that row.** The held row gained `is-revealed` and all
  three actions went `opacity: 1`, `pointer-events: auto`; every other row was unchanged.
- **(e) The hold does not open the chat.** Active row stayed `-1`; revealed row `5`.
- **(f) The reveal survives.** Re-probed 1.2 s after the hold — still `is-revealed`, still `opacity: 1`.
  The `pointercancel` race does not fire in practice under emulation, and the guard holds regardless.
- **(g) A revealed action actually fires.** Touching the revealed row's pin moved it into the pinned
  block: pinned rows went 3 → 4 and the `is-revealed` row became `is-pinned` (the re-render clearing the
  reveal, as designed). This is the `pointer-events: auto` flip working end to end.
- **(h) One row at a time; outside dismisses.** Holding row 0 then row 2 left revealed flags
  `[false, true, false, …]`. A touch outside the sidebar took the revealed count to `0`.
- **(i) A drag scrolls, it does not reveal.** A 60 px touch-drag over a row left the revealed count at `0`.
- **(j) A second tap on a revealed row dismisses without navigating.** Revealed `5 → -1`, active row
  stayed `-1`.
- **(k) Desktop is unchanged.** With touch emulation off at 1400×900, `matchMedia("(hover: hover)")` is
  `true`; at rest all three actions are `opacity: 0` with `pointer-events: auto` (as before this change —
  the `pointer-events: none` rule lives only in the `hover: none` block), and moving the mouse over a row
  takes that row's three to `opacity: 1`.

**Not verified:** the gesture on a real Android device against the installed PWA — in particular whether
Chrome's native long-press loses the race to our 400 ms timer in practice, and whether the
`-webkit-touch-callout` / `user-select` suppression is sufficient there. Emulated touch does not reproduce
the native context-menu gesture. That is an honest residual, as ADR 0037 treated its untestable half.
