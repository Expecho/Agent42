---
id: 0047
date: 2026-08-22
status: accepted
tags: [frontend, pwa, mobile, sharing, egress, ux]
supersedes: []
extends: [0013, 0035, 0044]
amends: [0035]
---

# Hand a read-only share link to the OS share sheet

## Context
ADR 0035 mints a public, no-login link to a conversation; ADR 0044 gave that dialog a second entry
point in the persistent topbar. Getting the link *out* of the app, though, has only ever been
**Copy** plus a manual paste into whatever app the recipient uses.

On the installed Android PWA that is the wrong shape. The platform affordance for "send this to
someone" is the system **share sheet**, reached through `navigator.share` — and until now there was
**no `navigator.share` call anywhere in the frontend**. The only "share" in the manifest is ADR
0023's *inbound* share **target**; this is the outbound half, and the two are unrelated mechanisms
that happen to share a word.

## Decision
**Pressing Create link mints, renders, and then hands the fresh URL straight to the OS share sheet;
and a Share button replaces Copy on any browser that implements the API.**

Frontend-only — no endpoint, grain, token shape, `excludedPaths` entry, Bicep, secret or package
change. Both buttons ship in `src/Agent42.Backend/wwwroot/index.html` with Share `hidden`, and
`wireShareLinks` flips the pair.

Rejected alternatives:

1. **Touch-only gating via `(hover: none)`** — would keep Copy on desktop and Share on phones. It
   makes the gate a *guess about the input device* rather than a statement about the capability, and
   a touchscreen laptop lands on the wrong side of it. Rejected deliberately, with the desktop
   consequences below stated up front.
2. **UA sniffing for Android.** Same objection, worse mechanism.
3. **Keeping Share and Copy side by side.** Two buttons that do the same job, one of which the OS
   sheet already offers as a target. The action row is the only place either lives, and a third
   control there is noise.
4. **Web Share Level 2 (`files:`)** — sharing a rendered image or PDF of the conversation rather
   than a link. A different feature with its own rendering problem; out of scope.

### The gate is a pure feature-detect, and that is the whole decision
`const canNativeShare = typeof navigator.share === "function";` — evaluated once at module scope, the
`src/Agent42.Backend/wwwroot/js/speech.js` `SpeechRecognition` idiom that
`src/Agent42.Backend/wwwroot/js/voice.js` and `src/Agent42.Backend/wwwroot/js/push.js` both branch on
to hide their control. No pointer test, no media query, no UA string.

**This is not an Android test.** Desktop Chrome and Edge on Windows, Safari on macOS/iOS and Firefox
on Android all satisfy it, so **Copy disappears there too**. Accepted, with the mitigation named
precisely: `#shareLinkUrl` remains a selectable readonly input, so a desktop user who wants the raw
text still selects it out of the field. `NativeShare_IsGatedOnAPureFeatureDetect` in
`tests/Agent42.Tests/ShareLinkAssetsTests.cs` pins the exact expression with paired negatives —
without it, every other guard here passes against a hardcoded `true`, and the decision becomes
unfalsifiable.

### Render before you share — the ordering is load-bearing
Transient user activation is a **Window-scoped time window** (~5 s in Chromium), not a token the
`fetch` consumes; `share()` itself consumes it. The risk the auto-open introduces is therefore the
window **expiring** across the `await fetch(POST)` — and the engine where an intervening `await`
breaks the gesture *deterministically* is **WebKit**, with its same-task requirement, not Chrome.

So `createShareLink` calls `renderShareLinkState(url)` **first** and only then `nativeShareLink()`.
A sheet that never opens costs the user nothing: the link is already on screen.
`NativeShare_RendersTheLinkBeforeOpeningTheSheet` asserts the *ordering*, not the presence.

The `await` sits inside the existing `try`, so the `finally` re-enables Create only once the sheet
closes — harmless, because `renderShareLinkState` has already hidden that button.

### Two distinct rejections, both silent
- **`AbortError`** — the user dismissed the sheet.
- **`NotAllowedError`** — no transient activation at call time (expired window, or WebKit's
  same-task rule).

Neither is surfaced: no toast, no label flash, no error state. The link is in the dialog either way.

### The payload is title plus url, and nothing else
The title is `Agent 42 — <conversation title>`. There is no `text` field — one that repeats the URL
renders twice in some targets. The title is captured **raw** into a module-level slot in
`openShareLink`, because `shareLinkConvTitle.textContent` is quote-wrapped for display and would
otherwise carry curly quotes into the payload.

## Consequences
- **ADR 0035's consent property is intact.** Nothing is minted without the Create press; the
  auto-open rides that same press. Both entry points still only *open* the dialog.
- **New egress detail (extends ADR 0013 / 0035): the conversation title now leaves the app**
  alongside the URL. Copy carried the URL alone. The receiving app sees the title, and so does the
  OS share history. What the token *grants* is unchanged — but its **distribution surface** is not:
  Copy put a bearer-token URL on the clipboard, transient and one paste away from a destination the
  user picked by hand, whereas the sheet hands it to an arbitrary target app and to the OS share
  history. Same exposure, more places it can come to rest.
- **A first-turn chat shares the placeholder title, not a title.** `send.js` points the topbar at
  `conversations.find(…)?.title ?? "This conversation"`, and a brand-new conversation's title is
  derived server-side *after* the turn — so the common path (good answer → topbar share → Create)
  sends `Agent 42 — This conversation` to the sheet and on to the recipient. Harmless before this ADR,
  when that string was display-only text in a dialog header; it is user-visible egress now. Accepted
  as-is rather than re-resolved after the post-turn list refresh, because the fallback belongs to
  `setTopbarShare` and re-timing it changes ADR 0044's transitions, not this feature. Sharing the same
  chat later from the sidebar carries the real title (measured — the fourth pass below).
- **Copy is gone wherever the API exists** — desktop Chrome/Edge, Safari, Firefox-Android included,
  not just Android. `#shareLinkUrl` is the fallback.
- **The auto-open is not gated to touch either**, so on desktop pressing **Create link** now
  force-opens an OS share flyout. The more intrusive of the two desktop effects, recorded rather
  than hidden. Touch-gating it would be a new decision, not a fix.
- **No new module, no new `SHELL` entry.** `src/Agent42.Backend/wwwroot/js/sharelinks.js` was already
  reachable from `src/Agent42.Backend/wwwroot/js/main.js`. Shell cache bumped `agent42-shell-v18` →
  `agent42-shell-v19` — mandatory, since the module gained a new export and cache-first subresources
  would otherwise serve a stale one.

### What was measured, and what was not
Headless Chrome 151 over CDP against DevHost (`http://localhost:61326` is a secure context, so
`navigator.share` is legal there), three passes. All inject through
`Page.addScriptToEvaluateOnNewDocument`, which lands ahead of module evaluation so the module-level
`canNativeShare` sees the change — and pass 1 must remove the API from **`Navigator.prototype`**,
because the Windows host ships it and `delete navigator.share` is a no-op on an instance.

| Pass | `typeof navigator.share` | Share / Copy `hidden` | Result |
|---|---|---|---|
| 1 — `delete Navigator.prototype.share` | `"undefined"` | `true` / `false` | link rendered, **Copy** visible, nothing thrown |
| 2 — stub resolving | `"function"` | `false` / `true` | one Create press produced `{ title: "Agent 42 — This conversation", url: "http://localhost:61326/shared/#shr_ve2XUC…" }`, the url equal to the rendered link. That title is the **placeholder** — a first-turn chat, the case in the Consequences above |
| 3 — stub rejecting `NotAllowedError` | `"function"` | `false` / `true` | `share()` was called, rejected, link still on screen, dialog open, nothing surfaced |

A fourth pass on a conversation with a real title returned `{ title: "Agent 42 — hello", … }` while
the dialog showed `“hello”` — the raw-title capture doing its job. At a 360 × 740 mobile viewport the
action row measured 285 px wide with 285 px of scroll width (Share 78 px + Revoke 86 px): no
overflow, and no `src/Agent42.Backend/wwwroot/styles.css` change was needed. The page-level overflow
visible at that width is pre-existing — the untouched agent dialog reproduces it.

**What stays genuinely unverified: the real Android system share sheet on a physical device.** Every
pass above drove a stub. In the tradition of ADR 0037 and 0043 that half is a device fact; unlike
ADR 0043, the browser-side legs were actually run.
