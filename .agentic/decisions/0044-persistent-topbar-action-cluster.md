---
id: 0044
date: 2026-08-21
status: accepted
tags: [frontend, ux, topbar, incognito, sharing, css, pwa]
supersedes: []
extends: []
amends: [0034, 0035]
amended_by: [0086]
---

# Promote incognito and share into a persistent top-right action cluster

## Context
Two conversation-level controls were effectively hidden.

The incognito toggle (ADR 0034) sat inside `.composer__frame` as the third of five icon-only buttons —
attach, tools, incognito, mic, voice — where nothing distinguishes a control that changes *how the whole
chat is persisted* from one that attaches a file. The read-only share action (ADR 0035) was worse: it
existed **only** as a hover-revealed `↗` glyph on a sidebar conversation row, so it was unreachable
without first finding the row for the chat you were already reading, and invisible to anyone who never
hovers.

Both are properties of the conversation, not of the message being composed, and neither should scroll
away while reading a long chat.

Meanwhile `.topbar` — a real `<header>` in the markup with a hamburger and a brand — was
`display: none` above 760px, existing only as off-canvas chrome for the mobile drawer. There was already
a container in exactly the right place, rendered and unused on every desktop load.

## Decision
Reveal `.topbar` at every width and give it a right-aligned `.topbar__actions` cluster holding the
**incognito toggle** (moved out of the composer frame, same id, same state rules) and a **second share
entry point** (`#topbarShareBtn`).

**The structural point, and the reason this needed no new layout machinery:** `.chat` is
`display: flex; flex-direction: column`, `.chat__scroll` is `flex: 1; min-height: 0` and is the only
element in the main column with `overflow-y: auto`. `.topbar` is its *sibling*, above it. So "the buttons
stay visible while a long chat scrolls" is a consequence of the existing box model — **no
`position: sticky`, no z-index, no new stacking context, no translucent backing**. That absence is
load-bearing in one specific way: the usage dashboard (`#usageView`, `position: fixed; z-index: 100`)
still covers the topbar precisely *because* the topbar is unpositioned. Giving `.topbar` a z-index is how
this breaks.

Six decisions shape the rest:

1. **Share is additive, not a move.** The sidebar row action stays; the topbar button is a second door
   into the same `openShareLink()`. ADR 0035's owner UI is extended, not replaced.
2. **One icon in both places.** The row's `↗` text glyph is replaced by the same inline three-node share
   SVG the topbar button draws (16×16, `stroke-width="1.4"`, `currentColor`), matching every other icon
   in the app. Its sibling `×` delete stays a text glyph — an accepted asymmetry.
3. **Incognito keeps every existing state rule** — hidden under the agent-chat picker lock, disabled once
   a chat has history, off by default. Only its DOM position changed, so `setIncognito` is untouched.
4. **The engaged tint MOVED, it was not duplicated.** `body.is-incognito .composer__frame` is deleted and
   replaced by `body.is-incognito .topbar`. Same body class, so the JS writer needed no change; the tint
   now sits where the toggle sits.
5. **Topbar share is *disabled* when there is nothing to share** — a fresh empty chat, or an incognito
   one. Disabled rather than hidden, so the control does not move around; and disabled rather than
   always-on, because an always-on button would mint a live public link to an empty view.
6. **Brand and hamburger become mobile-only.** They are revealed only inside `@media (max-width: 760px)`.
   The desktop sidebar header already renders the identical mark and wordmark, so a naively revealed
   topbar shows "42 AGENT 42" twice, side by side.

### Share availability is driven from four transitions, not three
The button must be enabled iff the active conversation is persisted **and** has history. Four sites
change the active conversation:

| Site | Action |
|---|---|
| `startNewConversation()` | `setTopbarShare(null)` — nothing in it yet |
| `startAgentChat(agent)` | `setTopbarShare(null)` on the fresh-chat path |
| `selectConversation(id)` | `setTopbarShare(id, title)` — persisted, with history |
| `send()` success path | `setTopbarShare(convId, …)` unless `ephemeral` |

**`startAgentChat` is the one that matters and the one that is easy to miss.** It does not delegate to
`startNewConversation`; it calls `setActiveId(convId)` itself. Without an explicit clear there, opening
conversation A and then clicking an agent leaves the button *enabled and still pointing at A* — pressing
Create would mint a public, no-login link to a conversation the user is not looking at. That is a direct
violation of ADR 0035's consent property ("nothing is minted until the user presses Create — that press
is their explicit consent to the exposure"). The analogy to `setIncognito` does **not** carry, because
`lockPicker` *hides* incognito whereas decision 5 chose *disabled, not hidden* for share, so a stale
target stays visible and clickable. Its continuous-agent early return (ADR 0016) delegates to
`selectConversation` and already enables correctly, so the clear belongs after that branch.

In `send()` the enable sits at the **end of the `try`**, on the success path. Not beside the
`incognitoBtn.disabled = true` lock at the top — that runs *before* the fetch, so an offline send, a
failed turn or a voice barge-in `AbortError` (ADR 0037) would light the button up on a chat that
persisted nothing. Not in the `finally` either, for the same reason. Failure therefore leaves it
disabled, which is the conservative direction and self-heals: the chat appears in the sidebar and opening
it enables share through `selectConversation`. The title there is a neutral constant
(`"This conversation"`) because a fresh chat's title is server-derived and not known client-side at that
moment; `selectConversation` resolves the real one from the sidebar list.

### The target is held in a setter slot, and that is not a cycle workaround
`sharelinks.js` keeps module-local `topbarConvId` / `topbarConvTitle` behind an exported
`setTopbarShare(convId, title)`. **The obvious rationale for this would be wrong.** It is not because an
import would create a cycle: cycles between feature modules are explicitly permitted here — ADR 0041 §5
says they are for "runtime calls and live read-bindings, never for values consumed during module
evaluation", and `sharelinks.js → send.js → sidebar.js → sharelinks.js` already shipped. The setter is
right because it matches this repo's canonical seam for exactly this shape: the `voice` / `stopSpeech` /
`sendAbort` slots on `send.js`, assigned through setters. Callers pass what they already hold.

`openShareLink` also gained the null guard `createShareLink` and `revokeShareLink` already carried —
without it a null slip renders a quoted `undefined` and fetches `/api/conversations//share`.

## Consequences
- **Not a security change.** Neither button changes what a share link exposes or who can reach it.
  `openShareLink` still only *opens* the dialog; nothing is minted until the user presses **Create**. The
  `startAgentChat` gap above is the single place this change could have weakened ADR 0035's consent
  property, and it is closed and pinned by a test.
- **ADR 0034 is amended**: the toggle no longer "tints the composer frame while engaged" — it tints the
  topbar. Its behavioural rules are unchanged.
- **ADR 0035 is amended**: the row action is no longer the only owner entry point.
- The composer frame drops to four controls, which is the visual relief ADR 0043 was after.
- Returning PWA users need a new cache generation (`agent42-shell-v14` → `-v15`): this diff **adds** a
  named export (`setTopbarShare`) to `sharelinks.js`, and a version-skewed module graph then throws
  "does not provide an export named 'setTopbarShare'" — a blank app, not a degraded one. `SHELL` is
  unchanged; no new module file was created.
- **What the automated tests can and cannot reach.** The behavioural criteria are source-text-pinned in
  `TopbarActionsAssetsTests` (placement by index ordering, since `Body()` counts braces and cannot slice
  HTML; the four transitions; the tint's move; the removal of the dead `.composer__incognito` rules, each
  negative anchored by a positive from the same document). The *visual* criteria — the bar showing on
  desktop, the cluster right-aligning, the wordmark not doubling, contrast in both themes, the buttons
  surviving a scroll — are cascade and rendering facts that no C# string assertion can settle. They are
  pinned only as "the declaration shipped".
- **Unlike ADR 0043, the manual leg was actually run.** No browser extension was available, but headless
  Chrome driven over the DevTools protocol was, and DevHost served the change at every step. **How to
  re-drive it** (the driver was a throwaway script, deliberately not committed — the repo has no browser
  test harness and this MR does not add one): start `dotnet run --project tools/Agent42.DevHost`, launch
  `chrome --headless=new --remote-debugging-port=9222 --user-data-dir=<tmp> http://localhost:61326/`,
  then over the page's CDP WebSocket use `Emulation.setDeviceMetricsOverride` for each viewport,
  `Runtime.evaluate` for `getComputedStyle` / `getBoundingClientRect` / `elementsFromPoint` and for
  clicking, and `Page.captureScreenshot` for the theme captures. Measured, not asserted: at 1400×900 the topbar computes `display: flex` / `position: static`, exactly one of
  `.brand__name` / `.topbar__brand` is visible, the cluster's right gap is 16px, and `.topbar__menu` is
  `display: none`; at 500×800 the hamburger and brand return. With incognito engaged and focus off the
  input, the topbar's border becomes `--amber-deep` and `.composer__frame`'s `box-shadow` is `none` — the
  only rules naming `.composer__frame` are itself and `:focus-within`. On a chat scrolled 3155px the
  share button sits at `top: 8` and `elementsFromPoint` at its centre returns it, so it is genuinely
  hit-testable, not merely painted. A fresh chat has share disabled; its first turn enables it with the
  neutral title (there is no sidebar row to read a title from yet) and its **second** turn picks up the
  server-derived one; opening the conversation retargets it to the real title; and clicking an agent from
  there disables it *and* makes the click inert (the dialog does not open) — the stale-target scenario,
  confirmed fixed against a running app. Both themes were captured. What remains unverified is the same
  class ADR 0043 records: real touch input on a physical Android device.

### One pre-existing bug this surfaced but deliberately did not fix
`startAgentChat` never calls `setIncognito`. Toggle incognito on a fresh chat and then click an agent and
`ephemeral` stays `true`: the agent chat posts `ephemeral: true`, keeps the composer's document gate
engaged, and keeps `body.is-incognito` set — while `lockPicker` *hides* `#incognitoBtn`, so the signal has
no visible control explaining it. That predates this change (the same orphaned tint sat on
`.composer__frame` before), and this ADR only relocates where the tint lands, so it is neither caused nor
worsened here. It is left alone on purpose: making an agent chat reset ephemerality is a change to ADR
0034's *behaviour*, and which way it should go (reset silently? refuse the transition? allow ephemeral
agent chats and stop hiding the toggle?) is a decision, not a mechanical fix. Recorded here so the next
person to touch either feature finds it.

## Alternatives considered
- **A floating `position: sticky` cluster over `.chat__scroll`.** Rejected: it needs a z-index, a
  translucent backing so prose does not run illegibly beneath it, and it would introduce the first
  positioned element in the main column — competing with `#usageView`'s overlay. Revealing the header
  that already exists costs one `display` change and no stacking at all.
- **Move the share action instead of adding one.** Rejected: the row action is how you share a chat you
  are *not* currently reading, which the topbar button cannot do.
- **Tint both the topbar and the composer frame while incognito.** Rejected as noise; one signal, where
  the control is.
- **Leave topbar share always enabled.** Rejected: Create on an empty chat mints a live public link to a
  view with nothing in it.
- **Hide topbar share instead of disabling it.** Rejected: the cluster would reflow as you move between
  chats, and the affordance would be undiscoverable exactly when a user is looking for it.
