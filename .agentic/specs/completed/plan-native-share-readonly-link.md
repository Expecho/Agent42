# Plan — hand a read-only share link to the OS share sheet

planned-against: main @ 39bac64

## Overview

Today the only way to get a minted read-only share link (ADR 0035) out of Agent 42 is the
**Copy** button plus a manual paste. On the installed Android PWA that is the wrong shape: the
platform affordance for "send this to someone" is the system **share sheet**, reached through
`navigator.share`. There is currently **no `navigator.share` call anywhere in the frontend** — the
only "share" in the manifest is ADR 0023's *inbound* share **target**. This plan adds the outbound
half: pressing **Create link** hands the fresh URL straight to the OS sheet, and a **Share** button
replaces **Copy** on any browser that implements the API.

Frontend-only. No endpoint, grain, Bicep, secret or package change.

## Affected areas

| File | Change |
|---|---|
| `src/Agent42.Backend/wwwroot/index.html` | new `#shareLinkShareBtn` inside `.share-link__actions`, `hidden` in markup; the stale "copy/revoke" comment above it |
| `src/Agent42.Backend/wwwroot/js/dom.js` | export `shareLinkShareBtn` |
| `src/Agent42.Backend/wwwroot/js/sharelinks.js` | `canNativeShare` const, `nativeShareLink()`, raw-title capture, the Copy/Share swap, the auto-open in `createShareLink` |
| `src/Agent42.Backend/wwwroot/sw.js` | `CACHE` `agent42-shell-v18` → `-v19` |
| `tests/Agent42.Tests/PwaAssetsTests.cs` | line 215 assertion follows the bump |
| `tests/Agent42.Tests/ShareLinkAssetsTests.cs` | five new source-text guards; the stale "create/copy/revoke" class comment |
| `.agentic/decisions/0047-native-share-for-read-only-links.md` | new ADR |
| `.agentic/decisions/0035-read-only-share-links.md` | **amendment banner** pointing at 0047 (the repo convention — see F4 below) |
| `.agentic/gotchas.md` | new entry (the three traps below) |
| `CLAUDE.md` | new ADR 0047 bullet **and** an amendment to the existing ADR 0035 bullet's "(create/copy/revoke)" phrase |

Deliberately **not** touched: `styles.css` (`[hidden] { display: none !important; }` at line 56 makes
the markup `hidden` attribute sufficient, `.agent-form__btn` declares no `display`, and
`.share-link__actions`' flex row absorbs a third child — the critic verified all three), the
manifest, the service worker's `SHELL` list (no new file), and every server-side file.

## Context & constraints

### Decisions taken in the interview (do not re-litigate)

1. **Share *replaces* Copy when supported**, gated on a **pure feature-detect** —
   `typeof navigator.share === "function"` — with **no touch/pointer test and no UA sniff**. The
   user was explicitly shown that desktop Chrome and Edge on Windows also implement
   `navigator.share`, so Copy disappears there too, and chose this anyway. Do not "fix" it by
   adding a `(hover: none)` media query. Record it as an accepted consequence in the ADR.
2. **Create auto-opens the sheet.** `createShareLink` mints, renders, then calls
   `navigator.share` in the same press — one tap from unshared to the Android sheet.
3. **Payload is `{ title, url }`** — `title` is `Agent 42 — <conversation title>`, no `text`
   field (a `text` that repeats the URL renders twice in some targets).
4. **A blocked or dismissed sheet is silent.** No toast, no label flash, no error state.

### How `navigator.share` actually behaves (get this right — it lands in a permanent gotcha)

Transient user activation is a **Window-scoped time window** (~5 s in Chromium), not a token the
`fetch` consumes. `share()` itself consumes it. So the risk introduced by decision 2 is the window
**expiring** across the `await fetch(POST)`, not the fetch "spending" it — and the engine where an
intervening `await` breaks the gesture *deterministically* is **WebKit** (its same-task
requirement), not Chrome. The mitigation is the same either way: render first, treat the rejection
as a non-event. Write it up that way; do not write "the fetch spends the activation".

The two silent rejections are therefore:
- `AbortError` — the user dismissed the sheet.
- `NotAllowedError` — no transient activation at call time (expired window, or WebKit's same-task rule).

`navigator.share` also requires a **secure context**; `http://localhost:61326` qualifies, so DevHost
is a valid venue for the manual leg.

### Investigation findings the executor should not re-derive

- **`createShareLink` must render before it shares**, per the mechanism above.
  Evidence: `src/Agent42.Backend/wwwroot/js/sharelinks.js` (`createShareLink`).
- **`shareLinkConvTitle.textContent` is not reusable as the share title** — `openShareLink` writes
  the title wrapped in curly quotes into it. Capture the raw `title` argument into a module-level
  slot beside the existing `shareLinkConvId`, the same shape that file already uses.
  Evidence: `src/Agent42.Backend/wwwroot/js/sharelinks.js` (`openShareLink`).
- **The feature-detect belongs at module scope, not inside `wireShareLinks`** — `createShareLink`
  needs to read it too. A module-level `const` evaluated at import time is the established idiom
  here: `src/Agent42.Backend/wwwroot/js/speech.js` exports `SpeechRecognition` exactly that way, and
  `voice.js` / `push.js` both branch on an import-time capability value to hide their control
  (`voiceBtn.hidden = true`, `pushSection.hidden = true`). Copy that shape.
- **Both buttons ship in the markup; the swap is a `hidden` flip at wire time.** `#shareLinkShareBtn`
  is `hidden` in `index.html` so an unsupported browser (and the pre-JS paint) shows Copy, and
  `wireShareLinks` flips the pair. Do not create the button from JS.
- **`createShareLink` has no `url` local today** — line 55 is the one-liner
  `if (res.ok) renderShareLinkState((await res.json()).url);`. Step 3 introduces one; that is a
  deliberate restructure, not incidental.
- **Nothing in `tests/` asserts on Copy.** No file references `shareLinkCopyBtn`, `copyShareLink`,
  `share-link__actions`, `renderShareLinkState` or `createShareLink` in an assertion, so hiding Copy
  breaks nothing. The two `IndexOf`-bounded topbar tests anchor on the literal `id="topbarShareBtn"`,
  which `id="shareLinkShareBtn"` does not substring-match. Verified by the critic against the tree.
- **`StaticAssetsServerFixture.Body(app, signature)` can extract all three bodies** the new tests
  need (`createShareLink`, `nativeShareLink`, `wireShareLinks` — each declared exactly once in the
  corpus). But it depth-counts braces **without skipping comments or strings**
  (`tests/Agent42.Tests/StaticAssetsServerFixture.cs:100-117`), so a lone `{` or `}` inside a comment
  silently truncates or over-runs the slice — see the Step 3 constraint.
- **DevHost can mint a real link** — `tools/Agent42.DevHost/Program.cs:140` calls `app.MapAgentApi()`,
  which includes the owner `POST /api/conversations/{id}/share` route
  (`src/Agent42.Backend/Sharing/AgentApi.Sharing.cs`, whose `CreateOrGetAsync` has no
  conversation-existence guard). The manual leg does not need a fake endpoint.
- **Doc-path integrity is a Node script, not a test** — `.agentic/scripts/check-doc-paths.mjs`,
  invoked from `.github/workflows/pr-checks.yml:66`. It scans `CLAUDE.md`, `.agentic/gotchas.md` and
  `.agentic/decisions/*.md` for backticked paths — exactly the artifacts Step 6 produces.

### Governing ADRs / gotchas — read before editing

| Path | Why |
|---|---|
| `.agentic/decisions/0035-read-only-share-links.md` | the feature this extends; its consent property (nothing minted until **Create**) must survive, and it takes an amendment banner |
| `.agentic/decisions/0044-persistent-topbar-action-cluster.md` | the second entry point into the same dialog, and the precedent for how a UI amendment to 0035 is recorded; `setTopbarShare` and the four transitions are untouched here |
| `.agentic/decisions/0023-web-share-target-android-pwa.md` | the *inbound* half — `#shareDialog` vs `#shareLinkDialog`; keep the id families distinct |
| `.agentic/decisions/0041-frontend-es-modules.md` | module conventions, `main.js` reachability |
| `.agentic/gotchas.md` "Every MR that touches `wwwroot/js/` must bump `CACHE` in sw.js" (~line 1580) | **mandatory** — a new export against a cache-first stale module is a blank app |
| `.agentic/gotchas.md` "The service worker's SHELL is all-or-nothing" (~line 1560) | no new file is added, so `SHELL` must **not** grow |
| `.agentic/gotchas.md` "A frontend module unreachable from `main.js` is invisible to every source-text guard" (~line 1597) | `sharelinks.js` is already reachable; do not create a new module |
| `.agentic/gotchas.md` "A scripted whole-file rewrite silently converts the checkout from CRLF to LF" (~line 1342) | the repo has **no `.gitattributes`** and every touched file is **CRLF** — edit in place, never rewrite whole files through an LF-emitting script, or `dotnet format` fails on every line |
| `.agentic/gotchas.md` "A read-only share link stores the RAW token..." (~line 1393) | server-side invariants — nothing here changes them; confirm that stays true |
| `tests/Agent42.Tests/TopbarActionsAssetsTests.cs:8-9` | the repo's own assertion discipline: **every negative is paired with a positive anchor from the same document** — a whole-file `DoesNotContain` passes vacuously |

An ADR is required (`/decide`) because this makes a user-visible behavioural choice with rejected
alternatives; it **extends** ADR 0035 and 0013's egress statement and supersedes nothing.

## Steps

Steps 1–4 are one sequential frontend edit (same files, same commit-worthy unit). Step 5 is
test-only and Step 6 is docs; **Step 7 (manual verification) must run after 1–4**.

### Step 1 — markup: the Share button

Files: `src/Agent42.Backend/wwwroot/index.html`

Add, as the **first** child of `.share-link__actions` (before `#shareLinkCopyBtn`):

```html
<button class="agent-form__btn agent-form__btn--primary" type="button" id="shareLinkShareBtn" hidden>Share</button>
```

Amend the comment three lines above it (line ~492), which currently reads
`<!-- Shared state: the URL + copy/revoke, revealed once a link exists. -->` — it is false as soon
as Copy is conditional (hard rule 2, and it is inside the edit's own blast radius).

Verify: `dotnet test Agent42.slnx --filter FullyQualifiedName~ShareLinkAssetsTests` still green
(the new assertions land in Step 5; this run proves nothing regressed).

### Step 2 — `dom.js` export

Files: `src/Agent42.Backend/wwwroot/js/dom.js`

Add `export const shareLinkShareBtn = document.getElementById("shareLinkShareBtn");` beside the
other `shareLink*` exports (lines ~161–168), keeping the file's existing grouping order.

Verify: `dotnet test Agent42.slnx --filter FullyQualifiedName~PwaAssetsTests`.

### Step 3 — `sharelinks.js`: detect, swap, share

Files: `src/Agent42.Backend/wwwroot/js/sharelinks.js`

1. Import `shareLinkShareBtn` from `./dom.js`.
2. Module-level `const canNativeShare = typeof navigator.share === "function";` — that **exact
   expression**, because Step 5 pins it. Comment it: names the `speech.js` idiom, and states plainly
   that this is **not** an Android test — desktop Chrome/Edge, Safari and Firefox-on-Android all
   match, and that is the accepted trade (interview decision 1).
3. A module-level slot for the raw title, set in `openShareLink` next to `shareLinkConvId`
   (`shareLinkConvTitleText`), because `shareLinkConvTitle.textContent` is quote-wrapped.
4. `async function nativeShareLink()` — guards on an empty `shareLinkUrl.value`, calls
   `navigator.share({ title: <template literal>, url })`, and swallows every rejection.
5. In `createShareLink`, introduce a `url` local (the one-liner at line 55 has none), then:
   `renderShareLinkState(url)` **first**, then `if (url && canNativeShare) await nativeShareLink();`
   — the ordering is load-bearing, comment it. Keep the `await` **inside the existing `try`**; the
   `finally { shareLinkCreateBtn.disabled = false; }` then runs only after the sheet closes, which
   is harmless because `renderShareLinkState` has already hidden that button. State that in the
   comment so it is not rediscovered as a bug.
6. In `wireShareLinks`: `shareLinkShareBtn.addEventListener("click", nativeShareLink);` and the
   swap `shareLinkShareBtn.hidden = !canNativeShare; shareLinkCopyBtn.hidden = canNativeShare;`.

**Two hard constraints on the comments in this file** (both are test mechanics, not style):
- **No `{` or `}` in any comment inside `nativeShareLink`, `createShareLink` or `wireShareLinks`.**
  `Body()` depth-counts braces blind to comments, so one stray brace silently mis-slices the body
  and can make Step 5's positive assertions pass vacuously.
- **Do not write the literal `text:` anywhere in `nativeShareLink`** — not even as "no `text:`
  field" in prose. Step 5's negative assertion is scoped to the `navigator.share({…})` argument, but
  keep the whole function clean so a later reword cannot trip it. Say "no text field" instead.

Do **not** touch `openShareLink`'s existing null guard, `setTopbarShare`, `revokeShareLink` or
`copyShareLink` beyond the title capture.

Verify: `dotnet build Agent42.slnx`, then the manual leg in Step 7.

### Step 4 — bump the shell cache

Files: `src/Agent42.Backend/wwwroot/sw.js` (line 5), `tests/Agent42.Tests/PwaAssetsTests.cs` (line 215)

`agent42-shell-v18` → `agent42-shell-v19` in both. `SHELL` gains **no** entry.

Verify: `dotnet test Agent42.slnx --filter FullyQualifiedName~PwaAssetsTests`.

### Step 5 — tests

Files: `tests/Agent42.Tests/ShareLinkAssetsTests.cs`

Amend the stale class-level comment at line ~22 ("The owner-side dialog and its **create/copy/revoke**
controls are present"), then add five `[Fact]`s to the existing class (it already holds the
`StaticAssetsServerFixture` and the `Body(app, signature)` helper — do **not** add a new test class):

- `NativeShare_ButtonIsInTheDialog_AndHiddenByDefault` — `GET /` contains
  `id="shareLinkShareBtn"`, and the `hidden` attribute is inside that button's own tag (bound the
  slice from `IndexOf("id=\"shareLinkShareBtn\"")` to the next `</button>`, the same bounded-slice
  technique `ShareIcon_IsTheSameMarkInBothPlaces` already uses).
- `NativeShare_ReplacesCopyWhenTheApiExists` — the `wireShareLinks` body contains
  `shareLinkShareBtn.hidden = !canNativeShare` **and** `shareLinkCopyBtn.hidden = canNativeShare`,
  proving the swap is a pair and not an add.
- **`NativeShare_IsGatedOnAPureFeatureDetect`** — this is the test that makes interview decision 1
  falsifiable, and without it the three above all pass against `const canNativeShare = true` or a UA
  sniff. Slice `sharelinks.js` out of the corpus (or fetch `/js/sharelinks.js` directly), then:
  `Assert.Contains("typeof navigator.share === \"function\"", slice)` as the positive anchor, and
  `Assert.DoesNotContain("userAgent", slice)` + `Assert.DoesNotContain("hover: none", slice)` as the
  negatives — paired with that anchor per `TopbarActionsAssetsTests.cs:8-9`.
- `NativeShare_RendersTheLinkBeforeOpeningTheSheet` — inside the `createShareLink` body, the index
  of `renderShareLinkState` is **less than** the index of `nativeShareLink`. This is the
  activation-window guard; assert the ordering, not just presence.
- `NativeShare_SendsTitleAndUrl_AndSwallowsAFailedSheet` — the `nativeShareLink` body contains
  `navigator.share`, `title:` and a `catch`; and the **`navigator.share({…})` argument slice
  specifically** (bounded from `navigator.share(` to its closing `)`, not the whole function) does
  **not** contain `text:`.

Verify: `dotnet test Agent42.slnx --filter FullyQualifiedName~ShareLinkAssetsTests` — all green,
then the full `dotnet test Agent42.slnx` and `dotnet format Agent42.slnx --verify-no-changes`.

**Sanity-check the new tests can actually fail**: temporarily flip `canNativeShare` to `true`, and
temporarily swap the render/share order, and confirm the two corresponding tests go red before
reverting. A source-text guard that has never been seen red is not evidence.

### Step 6 — docs (parallelizable with Step 7)

- `/decide` → `.agentic/decisions/0047-native-share-for-read-only-links.md`. Follow ADR 0046's
  shape (`# Title` / `## Context` / `## Decision` / a named sub-heading for the load-bearing
  details / `## Consequences`). It must record:
  - **Accepted:** Copy disappears wherever `navigator.share` exists — **not just Android**: desktop
    Chrome/Edge on Windows, Safari on macOS/iOS, Firefox on Android. The pure feature-detect was
    chosen with that stated. The mitigation that keeps the desktop path usable is **`#shareLinkUrl`
    itself**, which stays a selectable readonly input (`index.html:496`) — name that, not "the
    Windows share sheet has a copy target".
  - **Accepted, second consequence:** decision 2's auto-open is not gated to touch either, so on
    desktop pressing **Create link** now force-opens an OS share flyout. Record it explicitly; it is
    the more intrusive of the two desktop effects.
  - **New egress detail:** the conversation **title** now leaves the app alongside the URL — Copy
    carried the URL alone. The receiving app and the OS share history both see it. Extends ADR 0013 /
    0035; the exposure the *link itself* grants is unchanged.
  - **ADR 0035's consent property is intact:** nothing is minted without the Create press; the
    auto-open rides that same press.
  - **Rejected alternatives:** touch-only gating via `(hover: none)`; UA sniffing for Android;
    keeping Share *and* Copy side by side; Web Share Level 2 (`files:`) to share a rendered
    screenshot of the conversation.
  - **Untestable half:** the real Android sheet is a device fact, in ADR 0037/0043's tradition —
    but state what Step 7 *did* verify, with measured values, and do not leave it an unverified
    residual.
- **Amend `.agentic/decisions/0035-read-only-share-links.md`** with an "Amended by ADR 0047" banner,
  matching the existing 0042 (line 9) and 0044 (line 17) banners in that same file. This is the repo
  convention for an extending ADR and ADR 0044 is the direct precedent.
- `/gotcha` → one entry in `.agentic/gotchas.md` carrying the three traps: (1) render before you
  share, because the activation **window can expire** across the POST await (state the mechanism as
  written in "How `navigator.share` actually behaves" above — **not** "the fetch spends the
  activation"); (2) `navigator.share` is **not** an Android test — desktop Chrome/Edge, Safari and
  Firefox-Android all match, and the missing Copy button there is deliberate; (3) the rejection
  swallow covers **two** distinct causes and must stay silent. Evidence paths + verified date,
  matching the file's entry format.
- `CLAUDE.md` — a new ADR 0047 bullet in the repo-specific notes, in the same voice as the ADR
  0044/0045/0046 bullets, adjacent to the ADR 0035 bullet — **and** amend that existing bullet's
  phrase "both open the same `#shareLinkDialog` (create/copy/revoke)" (line ~433), which goes false
  here.

Verify: `node .agentic/scripts/check-doc-paths.mjs` (the actual doc-path integrity gate — it is a
Node script run from `.github/workflows/pr-checks.yml:66`, **not** a test in `ProjectLayoutTests`),
plus the full `dotnet test Agent42.slnx`.

### Step 7 — manual verification (the leg ADR 0043 skipped)

Run DevHost (`dotnet run --project tools/Agent42.DevHost`, UI at `http://localhost:61326`) and drive
headless Chrome over CDP, the same way ADR 0044's manual leg was run.

**The host is Windows, so headless Chrome ships `navigator.share` — pass 1 must remove it
explicitly, and `delete navigator.share` is a no-op** because `share` lives on `Navigator.prototype`,
not the instance. Both passes therefore inject through `Page.addScriptToEvaluateOnNewDocument`,
which lands ahead of module evaluation so the module-level `canNativeShare` sees the change.

1. **Unsupported pass** — remove the API first:
   ```js
   delete Navigator.prototype.share;
   delete Navigator.prototype.canShare;
   ```
   (or `Object.defineProperty(Navigator.prototype, "share", { value: undefined, configurable: true })`).
   Confirm `typeof navigator.share` is `"undefined"` before proceeding. Open a conversation with at
   least one turn, press the topbar share button, press **Create link**. Expect: the URL renders,
   **Copy** is visible, **Share** is hidden, nothing throws.
2. **Supported pass** — stub it:
   ```js
   navigator.share = (d) => { window.__shared = d; return Promise.resolve(); };
   ```
   Repeat the flow. Expect: **Share** visible, **Copy** hidden, and `window.__shared` equal to
   `{ title: "Agent 42 — <conversation title>", url: "<the minted /shared/#shr_… url>" }` after the
   single **Create link** press — proving the swap, the payload and the auto-open together.
3. **Rejecting pass** — `navigator.share = () => Promise.reject(new DOMException("x", "NotAllowedError"));`
   Confirm the dialog still shows the link, the Share button is pressable, and nothing is surfaced.

Record the measured values in the ADR. What stays genuinely unverified — and must be *said*, not
implied away — is the real Android system sheet on a physical device.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Proven by | File |
|---|---|---|---|
| AC1 | A Share button exists in the share dialog and is hidden until JS enables it | `NativeShare_ButtonIsInTheDialog_AndHiddenByDefault` | `tests/Agent42.Tests/ShareLinkAssetsTests.cs` |
| AC2 | Share **replaces** Copy when `navigator.share` exists, and Copy survives when it does not | `NativeShare_ReplacesCopyWhenTheApiExists` (the swap is a pair) + Step 7 pass 1 (API deleted → Copy visible) vs pass 2 | `tests/Agent42.Tests/ShareLinkAssetsTests.cs` |
| AC3 | The gate is a pure feature-detect — no UA sniff, no pointer/media test | `NativeShare_IsGatedOnAPureFeatureDetect` | `tests/Agent42.Tests/ShareLinkAssetsTests.cs` |
| AC4 | Pressing Create mints, renders, **then** opens the sheet — and a blocked sheet never loses the link | `NativeShare_RendersTheLinkBeforeOpeningTheSheet` + Step 7 pass 3 | `tests/Agent42.Tests/ShareLinkAssetsTests.cs` |
| AC5 | The sheet receives title + URL and nothing else | `NativeShare_SendsTitleAndUrl_AndSwallowsAFailedSheet` + Step 7's `window.__shared` value | `tests/Agent42.Tests/ShareLinkAssetsTests.cs` |
| AC6 | A dismissed or blocked sheet surfaces no error | `NativeShare_SendsTitleAndUrl_AndSwallowsAFailedSheet` (the `catch`) + Step 7 pass 3 | `tests/Agent42.Tests/ShareLinkAssetsTests.cs` |
| AC7 | The PWA shell cache is bumped so the changed module reaches installed clients | `PwaAssetsTests` (line ~215 assertion on `agent42-shell-v19`) | `tests/Agent42.Tests/PwaAssetsTests.cs` |
| AC8 | Existing share-link behaviour (create / revoke / topbar targeting / public viewer) is unchanged | existing `ShareLinkAssetsTests`, `ShareLinkGrainTests`, `TopbarActionsAssetsTests`, `EasyAuthExclusionTests` — all green, unmodified apart from one stale comment | `tests/Agent42.Tests/` |
| AC9 | No doc claim about the share dialog is left false | `node .agentic/scripts/check-doc-paths.mjs` + the three amendments named in Steps 1/5/6 | `CLAUDE.md`, `index.html`, `ShareLinkAssetsTests.cs` |

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (2026-08-22):

| Gate | Threshold |
|---|---|
| `new_issues` | 0 |
| `new_security_issues` | 0 |
| `duplication_pct_max` | 3 |
| `coverage_new_code_pct_min` | 80 |
| `cognitive_complexity_max` | 15 |
| `dependencies.vulnerable_packages` | 0 (hard block) |
| `dependencies.outdated_packages` | report (soft) |
| enforcement | local `soft_warning`; CI `hard_block` on build, tests, `dotnet format`, vulnerable packages, doc-path integrity |

Branch policy: `branches.protected: [main]` — work on a `feature/*` branch + MR. Never commit to `main`.

Note on `coverage_new_code_pct_min`: the change is JS, which `coverlet` does not instrument (the repo
has no JS runner by design — ADR 0041). Coverage is therefore N/A for the new lines; the source-text
guards in Step 5 (each one seen red at least once, per that step) plus Step 7's three browser passes
are the substitute evidence. Say that explicitly in the MR rather than reporting a hollow percentage.

## Out of scope

- **Web Share Level 2 (`files:`)** — sharing a rendered image/PDF of the conversation. Own feature.
- **Sharing from the sidebar row or the topbar button directly**, without opening the dialog.
  Both entry points keep opening `#shareLinkDialog`; the mint-consent step stays.
- **Any change to ADR 0023's inbound share target**, the manifest, or the service worker's `SHELL`.
- **Server-side anything** — no new route, grain, token shape, `excludedPaths` entry or Bicep edit.
- **A long-press / one-gesture "mint and share" from a sidebar row.**
- **A `styles.css` change.** If the button row visually breaks at narrow widths in Step 7, stop and
  report it rather than silently widening the plan.
- **Touch-gating the auto-open**, even though the ADR records it as intrusive on desktop. The user
  chose the ungated behaviour with that consequence stated; changing it is a new decision.

## Review

A `critic` subagent reviewed this plan against the repo's knowledge files and the tree. Verdict:
**not a clean pass — nine findings.** All nine were accepted and fixed; none was rejected.

| # | Sev | Finding | Disposition |
|---|---|---|---|
| F1 | High | Step 7's "plain headless Chrome (no `navigator.share`)" contradicts decision 1's own premise — the Windows host **does** ship the API — and `delete navigator.share` is a no-op because `share` is on `Navigator.prototype`. AC2's negative half had no evidence. | Fixed. Step 7 pass 1 now deletes `Navigator.prototype.share` explicitly and asserts `typeof navigator.share === "undefined"` first; a third rejecting pass was added. |
| F2 | High | All four proposed tests passed against `const canNativeShare = true` or a UA sniff — nothing pinned *how* the gate is computed, so interview decision 1 was unfalsifiable. | Fixed. New `NativeShare_IsGatedOnAPureFeatureDetect` (AC3) pins the exact expression with paired negatives, per `TopbarActionsAssetsTests.cs:8-9`. Step 5 also now requires each new guard be seen red once. |
| F3 | Med | Step 6 named `ProjectLayoutTests` as the doc-path integrity check; it contains no such check. The real gate is `.agentic/scripts/check-doc-paths.mjs` (`pr-checks.yml:66`). | Fixed — command corrected. |
| F4 | Med | The repo's convention is that an extending ADR adds an amendment banner to the ADR it extends (0035 already carries 0042's and 0044's); the plan listed only the new file. | Fixed — `0035-read-only-share-links.md` added to Affected areas and Step 6. |
| F5 | Med | Three live doc claims go false when Copy becomes conditional, all inside files the plan already opens: `CLAUDE.md:433`, `index.html:492`, `ShareLinkAssetsTests.cs:22`. | Fixed — each named in Steps 1/5/6 and covered by AC9. |
| F6 | Med-low | The transient-activation mechanism was described wrongly ("the fetch spends it"); activation is a Window-scoped ~5 s window that `share()` consumes, the risk is **expiry**, and the deterministic same-task breakage is **WebKit**, not Chrome — and the wrong wording was routed into a permanent gotcha. | Fixed. New "How `navigator.share` actually behaves" section is now the single source for that wording, and Step 6 points the gotcha at it. |
| F7 | Low | The accepted-consequence list named only desktop Chrome/Edge (Safari and Firefox-Android also match), missed that the auto-open is likewise ungated on desktop, and cited the wrong mitigation. | Fixed — all three corrected in Step 6, plus a new Out-of-scope entry. |
| F8 | Low | AC's `text:` negative was fragile against the `catch` comment the same step mandates, and `Body()` depth-counts braces blind to comments, so a brace in a comment can mis-slice a body and make positives pass vacuously. | Fixed — the negative is now scoped to the `navigator.share({…})` argument slice, and Step 3 forbids braces and the literal `text:` in those comments. |
| F9 | Low | Step 3.5 assumed a `url` local that does not exist (line 55 is a one-liner), and did not say where the `await` sits relative to the existing `try`/`finally`. | Fixed — both stated explicitly in Step 3.5. |

The critic separately **verified clean**: the `styles.css`-untouched claim (`[hidden]` at line 56,
`.agent-form__btn` declares no `display`, the flex row absorbs a third child); that no existing test
asserts on Copy; that the two `IndexOf`-bounded topbar tests cannot be substring-matched by the new
id; that `Body()` can extract all three target functions; the sw.js/`SHELL`/`CACHE` handling; all
five gotcha line references; `planned-against: main @ 39bac64`; DevHost's ability to mint; that no
`navigator.share` call exists in `wwwroot/`; the `speech.js`/`voice.js`/`push.js` idiom citations;
and that the gate thresholds match `quality-gates.yaml` exactly. No active ADR is contradicted and
no supersession is needed.

## Divergence from the plan (filled in at execution)

Executed on `feature/native-share-readonly-link` from `main @ 39bac64` — the exact
`planned-against:` ref, so no drift check was needed. Steps 1–7 all ran. Five deltas:

1. **`createShareLink` uses an early return, not a nullable `url`.** Step 3.5 called for a `url` local
   followed by `renderShareLinkState(url)`. Passing `null` on a non-ok response would have *changed*
   behaviour (the old one-liner rendered nothing at all on failure), so the body reads
   `if (!res.ok) return;` then `const url = (await res.json()).url;`. The mandated ordering, the `await`
   inside the `try`, and the `if (url && canNativeShare)` guard are all as planned; only the failure
   path is unchanged rather than newly reset.

2. **A pre-existing gate failure was fixed in-branch.** `node .agentic/scripts/check-doc-paths.mjs`
   exited 1 on `main` — three backticked paths in
   `.agentic/decisions/0045-pinned-conversations.md` (`js/send.js`,
   `Users/AgentApi.Users.cs`, `Sharing/AgentApi.Sharing.cs`) were written project-relative rather than
   repo-relative. Confirmed failing on `main` before touching them, then corrected to full repo paths.
   Not in the plan; repo policy is to fix a failing gate in-branch, never to defer or edit the gate.

3. **Two extra assertions and three extra comment fixes, from the execution-time critic.** The critic
   returned four low findings, all accepted: `NativeShare_ButtonIsInTheDialog_AndHiddenByDefault` now
   bounds its slice to `#shareLinkDialog` first (the planned version proved the `hidden` attribute but
   not the *containment* its name claims — it passed with the button moved to the topbar);
   `NativeShare_SendsTitleAndUrl_AndSwallowsAFailedSheet` now also asserts `url` on the argument slice
   (`navigator.share({ title })` with the url dropped passed all five guards); and three sibling
   "mint/copy/revoke" comments the plan had missed — `index.html:478`, `dom.js:159` and
   `sharelinks.js:34`, each inside a file the diff already opened — were corrected under AC9.

4. **ADR 0047 records one consequence the plan did not anticipate.** The critic found that the most
   likely share path sends a *placeholder*: `send.js` falls back to `"This conversation"` for a chat
   whose title the server has not yet derived, which was display-only text before this change and is
   user-visible egress after it. Recorded as accepted (re-timing the fallback would change ADR 0044's
   transitions, not this feature) and the pass-2 measurement is labelled accordingly. The egress
   wording was also sharpened: what changed is the token URL's *distribution surface*, not what the
   token grants.

5. **Step 7 gained a fourth pass and a geometry check.** Beyond the three planned passes: a fourth on a
   conversation with a real title returned `Agent 42 — hello` while the dialog showed `“hello”`,
   proving the raw-title capture; and the action row was measured at a 360 × 740 viewport (285 px
   container, 285 px scroll width) to discharge the plan's "stop and report if the button row breaks at
   narrow widths" clause. It does not break, and no `styles.css` change was needed. The page-level
   overflow visible at that width was confirmed pre-existing by reproducing it on the untouched agent
   dialog.

**Unchanged from the plan:** every acceptance criterion is met, each of the five new guards (and both
strengthened assertions) was seen red against a deliberate mutation before being accepted, `SHELL` did
not grow, no server-side file was touched, and nothing in the Out-of-scope list was built.

**Flagged, not acted on** (outside this plan's scope): `.agentic/gotchas.md` now holds 73 entries
against its own ~40 guardrail — pre-existing at 72, and the header asks for an audit rather than a
split. And `.claude/skills/execute-plan/SKILL.md:86` still carries an unfilled onboarding placeholder
for the MR command; this repo is GitHub, so `gh pr create` was used.
