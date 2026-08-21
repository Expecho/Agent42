# Plan — Remove the composer send button

planned-against: main @ b2f85b4

## Overview

The composer frame carries five controls (attach, tools, incognito, mic, voice) plus a dedicated
send button. `Enter` already sends and `Shift+Enter` already inserts a newline — the arrow button is
redundant chrome in a frame that is already crowded. Remove `#sendBtn` entirely (markup, CSS, JS) and
add `enterkeyhint="send"` to `#promptInput` so touch keyboards render a **Send** key instead of a
return key, keeping the installed Android PWA (ADR 0023 share target, ADR 0028 push) usable.

## Affected areas

| Path | Change |
|------|--------|
| `src/Agent42.Backend/wwwroot/index.html` | Delete the `<button id="sendBtn">` block; add `enterkeyhint="send"` to `#promptInput`; fix the `#shareDialog` note that instructs the user to "press Send" |
| `src/Agent42.Backend/wwwroot/styles.css` | Delete the four `.composer__send*` rules (~987–1003) |
| `src/Agent42.Backend/wwwroot/js/dom.js` | Delete the `sendBtn` export (line 12) |
| `src/Agent42.Backend/wwwroot/js/send.js` | Drop the `sendBtn` import + its two `disabled` writes (lines 7, 34, 143) |
| `src/Agent42.Backend/wwwroot/js/models.js` | Drop `sendBtn` from the import and from `applyModelAvailability`; update **both** comment lines (the function header and the `if (!streaming)` rationale) |
| `src/Agent42.Backend/wwwroot/js/sharetarget.js` | Fix the "reviews and presses Send" comment (~line 60) |
| `src/Agent42.Backend/wwwroot/sw.js` | Bump `CACHE` `agent42-shell-v13` → `-v14` |
| `tests/Agent42.Tests/PwaAssetsTests.cs` | Update the pinned cache name in `ServiceWorker_CacheNameIsCurrent` |
| `tests/Agent42.Tests/ComposerAssetsTests.cs` | **New** — the six tests in the AC table |
| `.agentic/decisions/0043-remove-composer-send-button.md` | **New** ADR (`amends: [0023]`) |
| `.agentic/decisions/0023-web-share-target-android-pwa.md` | Add the "Amended by" blockquote — it covers the stale "presses Send" wording at **both** line 45 (Decision) and line 68 (Consequences) |
| `.agentic/decisions/0019-remove-baseline-chat-providers.md` | Line ~41: "the frontend disables send and shows a hint" → the gate is the input now. A wording fix only; deliberately **not** in ADR 0043's `amends` (the decision itself is untouched) |
| `.agentic/gotchas.md` | Fix the no-model gotcha's "disables send + the input" claim (~line 783) |
| `CLAUDE.md` | Add the ADR 0043 bullet; fix "reviews and presses Send" (~line 295) |

No backend, grain, endpoint, Bicep, or Azure change. No new dependency.

## Context & constraints

Findings the executor should not have to re-derive (verify, don't trust — hard rule 2):

1. **`#sendBtn` has exactly three JS references**, all frontend:
   `wwwroot/js/dom.js:12` (the export), `js/send.js:7,34,143`, `js/models.js:3,40`.
   `grep -rn "sendBtn" src tests` confirms nothing else — no C# test asserts on it today, and no test
   asserts on `.composer__send` in CSS. Ignore hits under `.claude/worktrees/` (a gitignored stale
   worktree checkout, not the tree being changed).

2. **The button is not the load-bearing part of any gate.**
   - *Streaming*: the real guard is the early-return in `wireSend()`'s submit handler in `js/send.js`,
     plus the `!streaming` check on the suggestion chips in `js/thread.js`. The `sendBtn.disabled`
     writes in `send()` are purely visual.
   - *No model configured* (ADR 0019): `applyModelAvailability()` sets **both** `sendBtn.disabled` and
     `input.disabled`. The `input.disabled` half survives and is sufficient — a disabled `<textarea>`
     fires no `keydown`, so the ADR 0023 "prefills but cannot send" property still holds after removal
     (`sharetarget.js`'s `applyShare` only `focus()`es the disabled textarea). **This is why the
     `input.disabled` line must not be touched.**
   - *User decision*: no replacement busy-state affordance. The streaming caret in the agent bubble is
     the signal; Enter during streaming stays silently ignored, exactly as today. (Rejected: dimming the
     composer frame; a stop-generation button — the latter is a new feature, not a removal.)

3. **`form.requestSubmit()` still works with zero submit buttons** — `composer.js`'s keydown handler calls
   it explicitly, so the form never relied on the button being the default submitter. `send()` is also
   called directly (bypassing the form) by `js/thread.js`'s chips and `js/voice.js`; both are unaffected.
   **After this diff `composer.js`'s keydown handler is the only send path in the UI** — which is why
   AC5 exists and why step 5's manual smoke is not optional.

4. **User decision — `enterkeyhint="send"` on `#promptInput` is in scope.** Android/iOS soft keyboards
   render a return key for a `<textarea>` by default; `enterkeyhint` relabels it, and the existing
   keydown handler already turns that press into a submit. It is the reason "remove everywhere" was
   chosen over "hide on desktop only". **This is an attribute-only mitigation with no automated proof:**
   AC3 pins the attribute string, nothing more. Desktop DevHost cannot exercise it (`enterkeyhint` is
   inert there), so it needs the on-device step 5b and an accepted-residual entry in ADR 0043 — the
   same treatment ADR 0037 gives its untestable half.

5. **Mandatory `CACHE` bump** — `.agentic/gotchas.md` "Every MR that touches `wwwroot/js/` must bump
   `CACHE` in sw.js". The sharp reason here is not merely that three modules change: this diff **removes
   a named export from `dom.js`**, which is precisely the *"cross-module export rename → `SyntaxError`
   → blank app"* skew the gotcha was written for. Bumping makes the skew transient, not absent — it does
   **not** close the window (so AC6 is worded as "the pin moved", not "no user sees skew").
   `SHELL` is **unchanged** — no module is added or removed, so
   `ProjectLayoutTests.ServiceWorkerShell_ListsExactlyTheModuleDirectory` keeps passing untouched.

6. **Exemplar for the new test file: `tests/Agent42.Tests/LocationAssetsTests.cs`**, specifically
   `LocationControl_IsInPreferences_AndComposerButtonRemoved`. It is the repo's canonical shape for
   *removing a composer control* — positives for what survives, plus negatives across **both** the
   markup and `/styles.css`. (Do **not** model this on `SpeechInputAssetsTests.cs`; that is a
   feature-addition test with no removal shape.) Mechanics are shared: `[Collection("StaticAssets")]`,
   ctor takes `StaticAssetsServerFixture`, `_client.GetStringAsync("/")` / `("/styles.css")` for served
   documents, `_fixture.GetAppScriptAsync()` for the JS corpus.

7. **Every negative assertion needs a positive anchor in the same test.** The house rule is stated at
   `tests/Agent42.Tests/PwaAssetsTests.cs` (~line 401): assertions must be mutation-observable, and a
   whole-file `Contains` check can pass vacuously. The concrete failure mode is in `.agentic/gotchas.md`
   ("A frontend module unreachable from `main.js` is invisible to all 47 source-text guards"):
   `GetAppScriptAsync()` builds its corpus by walking import specifiers over HTTP, so if that walk
   degrades, `Assert.DoesNotContain("sendBtn", corpus)` goes green against a corpus that never contained
   `send.js`/`models.js` at all.

8. **Governing docs.** ADR 0023 states in **both** its Decision (~line 45) and its Consequences
   (~line 68) that the user "presses Send"; ADR 0019 (~line 41) says the no-model gate "disables send".
   The same wording leaks into `index.html`'s share dialog, `js/sharetarget.js`, `CLAUDE.md` and
   `.agentic/gotchas.md` — the full list is in the affected-areas table. Amend ADR 0023 with the repo's
   standard `> **Amended by [ADR 0043](…)**` blockquote (see ADR 0013/0017/0035 for the shape;
   `.agentic/decisions/README.md` for the rules) rather than editing its decision text.
   ADR 0006/0037 (mic/voice) and ADR 0041 (module split) are read-only context — nothing in them is
   contradicted. `.agentic/specs/completed/plan-*.md` also mention `#sendBtn`; those are **historical
   records — leave them**.

## Steps

Steps 1–3 are one coherent frontend edit; do them in order. Step 4 depends on 1–3. Steps 5–7 follow.

**Step 0 — branch.** `git switch -c feature/remove-composer-send-button` from `main` @ `b2f85b4`.
Never commit to `main` (hard rule 1).

**Step 1 — markup.** `src/Agent42.Backend/wwwroot/index.html`:
- delete the whole `<button class="composer__send" id="sendBtn" type="submit" …>…</button>` block
  (~line 164) including its inline SVG;
- add `enterkeyhint="send"` to the `#promptInput` textarea;
- fix the `#shareDialog` note (~line 455) so it no longer names a control that does not exist — e.g.
  *"…prefilled for you to review — then press Enter to send."* Keep the surrounding comment's intent
  (never auto-sent) but update its wording too (~448–450).

Leave `.composer__hint` (`<kbd>Enter</kbd> to send · <kbd>Shift</kbd>+<kbd>Enter</kbd> for a new line`)
in place — it is now the only discoverability affordance. Its `<kbd>` framing names keys a soft keyboard
does not have and it renders unchanged at ≤760px; that gap is **out of scope here** but must be recorded
in ADR 0043's consequences.
*Verify:* covered by AC1/AC3/AC8 in step 4.

**Step 2 — styles.** `src/Agent42.Backend/wwwroot/styles.css`: delete `.composer__send` and its
`:hover:not(:disabled)`, `:active:not(:disabled)` and `:disabled` rules (~987–1003). Touch nothing else —
`.composer__frame` is `flex` with `gap`, so the neighbours reflow with no further change.
*Verify:* AC1's `/styles.css` half.

**Step 3 — JS.** Four files:
- `js/dom.js`: delete `export const sendBtn = document.getElementById("sendBtn");`
- `js/send.js`: drop `sendBtn` from the `./dom.js` import; delete `sendBtn.disabled = true;` in `send()`
  and `sendBtn.disabled = false;` in its `finally`. **Keep** `modelSelect.disabled` /
  `incognitoBtn.disabled` and the `applyModelAvailability()` call.
- `js/models.js`: drop `sendBtn` from the `./dom.js` import and delete `sendBtn.disabled = !hasModel;`
  from `applyModelAvailability`. **Keep `input.disabled = !hasModel;` and the `if (!streaming)` guard.**
  Update **both** stale comments: the function header ("disable send + the input" → the input alone) and
  the line explaining the streaming guard — `sendBtn.disabled` *was* the thing that guard protected
  inside this function (`send()` never touches `input.disabled`), so state the surviving reason or the
  guard reads as unmotivated.
- `js/sharetarget.js`: update the "reviews and presses Send" comment (~line 60).
*Verify:* `grep -rn "sendBtn" src/` → no hits (outside `.claude/worktrees/`); and step 4's
`ProjectLayoutTests.EveryNamedImport_IsActuallyExportedByItsTargetModule`, which fails loudly if the
export is deleted while an importer is missed — the most likely way this step goes wrong (AC9).

**Step 4 — SW bump + tests.** Bump `CACHE` to `"agent42-shell-v14"` in `wwwroot/sw.js`; update the pinned
literal in `tests/Agent42.Tests/PwaAssetsTests.cs` (`ServiceWorker_CacheNameIsCurrent`). Add
`tests/Agent42.Tests/ComposerAssetsTests.cs` with the six tests in the AC table, modelled on
`LocationAssetsTests.LocationControl_IsInPreferences_AndComposerButtonRemoved` (context item 6). Every
negative assertion pairs with a positive anchor from the same document (context item 7).
*Verify:* `dotnet test Agent42.slnx` — whole suite green, including `ProjectLayoutTests` and the other
`*AssetsTests`. Then `dotnet format Agent42.slnx --verify-no-changes`.

**Step 5a — desktop smoke.** `dotnet run --project tools/Agent42.DevHost` → http://localhost:61326.
Confirm: the composer renders with no arrow button and no layout gap; `Enter` sends; `Shift+Enter`
inserts a newline; a mid-stream `Enter` is a no-op and does not double-send; mic and voice still work.

**Step 5b — on-device check (the one AC3 cannot prove).** On the installed Android PWA (or Chrome for
Android against a reachable DevHost), confirm the soft keyboard shows a **Send** key on `#promptInput`
and that pressing it sends rather than inserting a newline; then run one Web Share Target flow (ADR 0023)
end to end, since its picker copy changed in step 1. **If this cannot be run, do not silently skip it** —
say so and record it as an unverified residual in ADR 0043 rather than claiming the platform works.
*Verify:* manual; the browser keyboard cannot be driven headlessly (same rationale as ADR 0037).

**Step 6 — ADR.** `/decide` → `.agentic/decisions/0043-remove-composer-send-button.md`, frontmatter
`id: 0043`, `status: accepted`, `tags: [frontend, composer, ux, pwa, accessibility]`, `amends: [0023]`.
Record: the decision; the rejected alternatives from the interview (keep-on-touch via a `pointer: coarse`
media query; remove with no `enterkeyhint`; and the three busy-state options); and the consequences —
(a) Enter is now the **only** send path, so a keyboard or IME that swallows Enter has no fallback
(accepted residual, `enterkeyhint` is a mitigation and not a guarantee); (b) the no-model gate now rests
on `input.disabled` alone; (c) the surviving `.composer__hint` names `<kbd>` keys a soft keyboard does
not have and is not responsive — known gap, deliberately not fixed here; (d) whatever step 5b actually
verified. Add the `> **Amended by [ADR 0043](0043-remove-composer-send-button.md) (<today>)**`
blockquote near the top of ADR 0023, noting it covers the "presses Send" wording in both its Decision
and its Consequences.

**Step 7 — doc debt (`/sync-docs`).** Fix `.agentic/gotchas.md` (~line 783, "the frontend disables send
+ the input"), `.agentic/decisions/0019-…` (~line 41, "the frontend disables send and shows a hint"),
and `CLAUDE.md` (~line 295, "reviews and presses Send"); add the `CLAUDE.md` repo-note bullet for ADR
0043 in the existing style.
*Verify:* `dotnet test Agent42.slnx` (CI also runs a doc-path integrity check — every path cited in docs
must exist).

## Acceptance criteria → test mapping

All new tests live in `tests/Agent42.Tests/ComposerAssetsTests.cs` unless stated otherwise.

| # | Acceptance criterion | Test |
|---|----------------------|------|
| AC1 | The send button is gone from **both** the markup and the stylesheet | `SendButton_IsRemovedFromComposerAndStyles` — `GET /` lacks `id="sendBtn"`; `GET /styles.css` lacks `composer__send`; anchored by positives that `id="promptInput"`, `id="micBtn"` and `composer__mic` all survive |
| AC2 | No JS module still reaches for it | `AppJs_DoesNotReferenceSendButton` — `GetAppScriptAsync()` lacks `sendBtn`, anchored by positives that the corpus contains `applyModelAvailability` and `wireSend` (proves `models.js` + `send.js` are really in it — the vacuous-pass trap, context item 7) |
| AC3 | Touch keyboards offer a Send key | `PromptInput_HintsSendOnSoftKeyboards` — `GET /` contains `enterkeyhint="send"`. Pins the attribute only; the platform behaviour is step 5b |
| AC4 | The no-model gate survives on the input alone (ADR 0019/0023) | `AppJs_GatesComposerOnInputWhenNoModel` — corpus contains `input.disabled = !hasModel` |
| AC5 | Enter still sends; Shift+Enter still newlines | `AppJs_EnterSubmitsTheComposerForm` — corpus contains `form.requestSubmit()` and the `shiftKey` branch of the keydown handler; **plus** manual step 5a. This is the load-bearing criterion: after this diff it is the only send path |
| AC8 | No user-facing copy names a control that no longer exists | `ShareDialog_DoesNotDirectTheUserToASendButton` — `GET /` lacks `press Send`, anchored by a positive that `id="shareDialog"` and its note element are still present |
| AC6 | Returning PWA users get a **new** cache generation, so module skew is transient rather than sticky | `ServiceWorker_CacheNameIsCurrent` pins `agent42-shell-v14` — `tests/Agent42.Tests/PwaAssetsTests.cs` |
| AC9 | Deleting the `dom.js` export leaves no dangling importer | `EveryNamedImport_IsActuallyExportedByItsTargetModule` (existing, unchanged) — `tests/Agent42.Tests/ProjectLayoutTests.cs` |
| AC7 | The module graph and SW shell stay consistent | `ServiceWorkerShell_ListsExactlyTheModuleDirectory` (existing, unchanged) — `tests/Agent42.Tests/ProjectLayoutTests.cs` |

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (2026-08-21):

| Gate | Threshold |
|------|-----------|
| `new_issues` | 0 |
| `new_security_issues` | 0 |
| `duplication_pct_max` | 3 |
| `coverage_new_code_pct_min` | 80 |
| `cognitive_complexity_max` | 15 |
| `dependencies.vulnerable_packages` | 0 (hard block) |
| enforcement | local `soft_warning`; CI `hard_block` on build, tests, `dotnet format`, vulnerable packages, doc-path integrity |
| coverage tooling | `coverlet` — `dotnet test Agent42.slnx --collect:"XPlat Code Coverage"` |

Protected branches: `main` — always a `feature/*` branch + MR.

## Out of scope

- A stop-generation button (would use the existing `sendAbort`; a new feature, its own ADR).
- Any other composer control — attach, tools, incognito, mic, voice all stay exactly as they are.
- Rewording or restyling `.composer__hint`, and making it responsive at ≤760px — recorded as a known
  gap in ADR 0043 instead.
- The model picker, the composer footer, and any reflow beyond what deleting one flex child does.
- Backend, grain, endpoint, Bicep or Azure changes — there are none.
- Editing `.agentic/specs/completed/*` to match; those are historical records.

## Review

A `critic` pass ran against this spec (verdict: changes requested — 2 high, 4 medium, 5 low). Every
finding was verified against the tree and **all were accepted and folded in**: the stale "press Send"
copy in `index.html`/`sharetarget.js`/`CLAUDE.md`/ADR 0023 (now the affected-areas table + step 1/3/6/7);
the missing `styles.css` acceptance criterion (AC1 now asserts both documents); the wrong exemplar
(`LocationAssetsTests`, not `SpeechInputAssetsTests` — context item 6); vacuous negative-only assertions
(positive anchors, context item 7); AC5 having no named test (`AppJs_EnterSubmitsTheComposerForm`); the
untested `enterkeyhint` platform claim (step 5b + an ADR residual); the stale `if (!streaming)` rationale
(step 3); AC6 overclaiming what a cache-name pin proves (reworded); the uncited
`EveryNamedImport_IsActuallyExportedByItsTargetModule` guard (AC9); ADR 0019's parallel stale line
(affected areas, with the reason it is a wording fix rather than an `amends` entry); and a verbatim
source line quoted in context item 2 (removed — a spec that paraphrases code is a drift source, hard
rule 2).

No findings were rejected.

## Execution — where the implementation diverged from the plan

Executed 2026-08-21 on `feature/remove-composer-send-button` from `main` @ `b2f85b4` (no drift: the
`planned-against:` ref was HEAD). Steps 1–4, 6 and 7 landed as written. Four divergences:

1. **Step 5a and 5b were BOTH skipped, and are recorded as unverified residuals** rather than done.
   No browser driver was available in the session (the Chrome extension was not connected), so neither
   the desktop smoke nor the on-device check ran. The plan required this to be stated rather than
   silently skipped; it is, in ADR 0043's consequences and in the `CLAUDE.md` bullet. The user was
   asked and chose to ship on that basis. What *was* verified in place of 5a: the served assets over
   HTTP against a running DevHost — `/` carries no `sendBtn` and does carry `enterkeyhint="send"`,
   `/styles.css` carries no `composer__send`, every module in the import graph returns 200, and
   `/sw.js` serves `agent42-shell-v14`. That is strictly weaker than the smoke: it proves what is
   served, not what a browser does with it.

2. **`docs/features/android-share-target.md` was added to the affected areas** (three spots: prose at
   line 11, the mermaid diagram at line 35, and a code snippet at line 99 quoting the very
   `sharetarget.js` comment step 3 rewrote). The plan's affected-areas table missed `docs/` entirely —
   it swept `index.html`, `sharetarget.js`, `CLAUDE.md`, `gotchas.md` and the two ADRs, but not the
   feature docs. Found by the step-5 `critic` pass.

3. **The share-dialog copy is neutral, not `"press Enter to send"`.** Step 1 offered that wording as an
   example. The `critic` pass observed that the share picker is reachable **only** from the Android
   PWA — i.e. only ever from a soft keyboard — which is exactly the surface where ADR 0043's own
   consequence (c) says naming `Enter` is wrong. Shipping it would have planted a second instance of the
   gap the ADR records as known. The note now reads *"…prefilled for you to review — then send it."*
   AC8 is unaffected (it asserts the absence of `press Send`, anchored on the note's opening clause).

4. **`applyModelAvailability`'s two-line `if (!streaming) { … }` block collapsed to one line**, since
   removing `sendBtn.disabled` left it with a single statement. The plan said to keep the guard and
   restate its rationale, which is done — but AC4's pinned string (`input.disabled = !hasModel`) is
   unaffected by the reformat, so the mapping still holds.

Also worth recording for the next reader: the plan's context item 1 said `#sendBtn` had **three JS
references**, which was a count of *files*, not call sites — there were five (two imports plus three
`disabled` writes). ADR 0043 states the correct figure.

The `critic` pass returned 2 medium + 4 low findings; **all six were accepted and fixed** before the
commit (the two above, plus: the ADR overstating that the stale-copy sweep was complete; the edited
`gotchas.md` entry needing its `verified:` bumped and its Evidence list extended; "the only send path
in the UI" being self-contradicted by the suggestion chips and voice mode, which send without Enter;
and the `accessibility` tag not being earned by any consequence — `#sendBtn` was the composer's only
actionable control with an accessible name, now stated and accepted, untested with a screen reader).
None were rejected.
