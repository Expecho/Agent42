---
id: 0043
date: 2026-08-21
status: accepted
tags: [frontend, composer, ux, pwa, accessibility]
supersedes: []
extends: []
amends: [0023]
---

# Remove the composer send button; Enter becomes the send control

## Context
The composer frame carries five controls — attach, tools, incognito, mic (ADR 0006), voice
(ADR 0037) — plus a dedicated arrow send button, and it is visibly crowded at narrow widths.
`Enter` has always sent and `Shift+Enter` has always inserted a newline (`wireComposer()`'s
keydown handler calls `form.requestSubmit()` explicitly, so the form never relied on the button
being its default submitter), and `.composer__hint` already advertises both. The button was
redundant chrome. The one place it was not redundant is **touch**: a soft keyboard renders a
return key for a `<textarea>`, so removing the button on a phone would remove the only obvious
way to send — and the installed Android PWA is a first-class surface here (ADR 0023 share
target, ADR 0028 push).

## Decision
Delete `#sendBtn` entirely — markup, the four `.composer__send*` CSS rules, the `dom.js` export and
its five references across `send.js` and `models.js` (two imports, three `disabled` writes) — and add
`enterkeyhint="send"` to `#promptInput` so touch keyboards label their return key **Send**.
`wireComposer()`'s keydown handler becomes **the only path that sends arbitrary composed text**;
`send()` is still called directly by the suggestion chips (ADR 0036) and by voice mode (ADR 0037),
neither of which needs Enter. The no-model gate (ADR 0019) keeps only its `input.disabled` half.

## Alternatives considered
- **Keep the button on touch only, via a `pointer: coarse` media query.** Rejected: two composer
  layouts to reason about and to test, for a control the platform already offers once
  `enterkeyhint` labels it. The interview explicitly chose "remove everywhere".
- **Remove it with no `enterkeyhint`.** Rejected: on a phone the return key would still read as a
  newline key, leaving the primary action unlabelled. The attribute is the whole reason
  remove-everywhere was viable.
- **Replace it with a stop-generation button.** Rejected here as a *new feature*, not part of a
  removal — it would reuse `sendAbort` (ADR 0037) and deserves its own decision.
- **Dim the composer frame while streaming**, or otherwise add a busy-state affordance to replace
  the button's disabled look. Rejected: the streaming caret in the agent bubble is already the
  signal, and Enter during a turn stays silently ignored exactly as it did before.

## Consequences
- **Enter is now the only way to send text the user typed.** A keyboard layout or IME that swallows
  Enter (a composition commit, some hardware keyboards) leaves no fallback *control* for a typed
  prompt. `enterkeyhint` is a *mitigation, not a guarantee* — it relabels a key, it does not add one.
  Accepted residual. Two send paths survive that do not involve Enter, but neither substitutes: a
  suggestion chip (ADR 0036) sends **its own** text, and voice mode (ADR 0037) auto-sends what it
  heard — so a user who cannot press Enter can still send *something*, just never what they typed.
- **The no-model gate rests on `input.disabled` alone.** That is sufficient: a disabled `<textarea>`
  fires no `keydown`, so ADR 0023's "a share prefills the composer but cannot send" property still
  holds. It also makes that one line load-bearing in a way it was not before — deleting it would
  reopen the path into a modelless grain, which is why `AppJs_GatesComposerOnInputWhenNoModel` pins it.
- **`.composer__hint` is now the only discoverability affordance**, and it names `<kbd>Enter</kbd>`
  and `<kbd>Shift</kbd>` — keys a soft keyboard does not have — and does not adapt at ≤760px. A known
  gap, deliberately left as-is: rewording/restyling it was out of scope for this removal. For the
  same reason the share-picker note (ADR 0023) was reworded to a neutral "then send it" rather than
  "press Enter": that dialog is reachable **only** from the Android PWA, i.e. only from a soft
  keyboard, so naming Enter there would have planted a second instance of exactly this gap.
- **Assistive technology.** `#sendBtn` was the composer's only actionable control carrying an
  accessible name (`aria-label="Send prompt"`), so removing it does narrow what a screen reader or
  switch-access user can land on. Accepted: the textarea keeps its own `aria-label`, and Enter is
  reachable from every AT that can reach a textarea at all — but this was **not** tested with a
  screen reader, and it belongs on the same unverified list as the two interactive legs below.
- **`enterkeyhint` has no automated proof.** `PromptInput_HintsSendOnSoftKeyboards` pins the attribute
  string; whether a soft keyboard renders a Send key is a device fact no C# source-text assertion can
  reach, and DevHost on desktop cannot exercise it (the attribute is inert there). Same treatment as
  ADR 0037's untestable half.
  **What was actually verified in this change: the automated suite and the served assets only.** The
  full suite is green (675 tests, including the six new `ComposerAssetsTests`), `dotnet format` is
  clean, and DevHost was queried over HTTP — `/` serves no `sendBtn` and does serve
  `enterkeyhint="send"`, `/styles.css` serves no `composer__send`, every module in the import graph
  returns 200, and `/sw.js` serves `agent42-shell-v14`. **Neither interactive leg was run.** No browser
  driver was available in the session, so the desktop smoke (Enter sends, Shift+Enter newlines,
  mid-stream Enter is a no-op, no layout gap, mic and voice unaffected) and the on-device leg (the
  Android soft-keyboard Send key, plus one end-to-end Web Share Target flow against the changed picker
  copy) are both **unverified residuals**. Recorded rather than skipped: the source-text guards prove
  the code *says* the right thing, and after this change `wireComposer()`'s keydown handler is the only
  send path, so the first real browser to load it is also the first proof it works.
- **Stale copy fixed in the same change.** ADR 0023's "the user presses Send" wording had leaked into
  `index.html`'s share-picker note, `src/Agent42.Backend/wwwroot/js/sharetarget.js`, `CLAUDE.md`,
  `.agentic/gotchas.md` and `docs/features/android-share-target.md` (prose, the mermaid diagram, and a
  code snippet quoting the very comment this change rewrote); ADR 0019 said the gate "disables send".
  None of them names the button any more. `.agentic/specs/completed/*` still does, deliberately — those
  are historical records of what was true when they were written.
- Returning PWA users need a new cache generation (`agent42-shell-v13` → `-v14`): this diff **removes a
  named export from `dom.js`**, which is exactly the cross-module skew that turns into a `SyntaxError`
  and a blank app. The bump makes that window transient — it does not close it.
