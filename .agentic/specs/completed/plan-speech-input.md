# Plan — Speech input (voice dictation) in the chat composer

planned-against: main @ 9e6fc8f  (prerequisite merged — see below)

## Prerequisite (SATISFIED)

This work depends on the **preferred-language profile** feature. As of 2026-07-08 it is
**merged to main** (`9e6fc8f`, fast-forward from `a5ce1de`) — no longer a blocker. Branch
`feature/speech-input` off current main. The spec was originally authored against pre-merge
main; the line-number-drift note below is now historical but the anchor-string discipline
still stands (it survives any future shift).

What that prerequisite delivers (relied on by this plan):
- `profile` render-cache object with `profile.preferredLanguage` (BCP-47 tag, `""` = device
  default), loaded at startup by `loadProfile()` — `src/Agent42.Backend/wwwroot/app.js`.
- The `// preferred language` picker whose comment already declares the tag "seeds future
  speech input" — this feature is that consumer.

If the executor finds `profile`/`profile.preferredLanguage` absent in `app.js`, STOP — the
prerequisite has not merged; do not reintroduce the profile plumbing here.

## Overview

Add a microphone button to the chat composer that dictates a prompt via the browser-native
**Web Speech API** (`SpeechRecognition` / `webkitSpeechRecognition`). Tap to start, tap to
stop; partial ("interim") text appears live in the message box as the user speaks and is
finalized on stop. The transcript lands in the existing `#promptInput` textarea for the user
to review/edit and send manually — it never auto-sends. Recognition language is driven by the
user's saved `preferredLanguage`, falling back to `navigator.language`.

This is a **frontend-only** feature: no backend endpoint, no Orleans grain, no Bicep, no new
Azure resource, no new NuGet/JS dependency. Audio is transcribed by the browser vendor's
service (Google for Chrome/Edge) — see the privacy decision in Context.

**Why now:** the language picker was built explicitly to seed this; it is the natural next
step and unblocks hands-free/mobile prompting.

## Affected areas (verified against the tree)

| File | Change |
|------|--------|
| `src/Agent42.Backend/wwwroot/index.html` | Add `#micBtn` (`.composer__mic`) inside the `.composer__frame` block, between `#promptInput` and `#sendBtn`. |
| `src/Agent42.Backend/wwwroot/app.js` | New "speech input" section: feature-detect, build recognition, toggle handler, interim/final rendering, lang from `profile`, stop-on-send, errors, hide-when-unsupported. Wire alongside the existing composer block (`function autosize()` … `attachBtn.addEventListener`); grab `micBtn` in the element-refs block (near `const attachBtn = …`). |
| `src/Agent42.Backend/wwwroot/styles.css` | Add `.composer__mic` (mirror `.composer__attach`) + a recording-state style reusing `@keyframes pulse`. |
| `tests/Agent42.Tests/SpeechInputAssetsTests.cs` (new) | Source-text guardrail tests over the served `wwwroot`, mirroring `PwaAssetsTests` (TestServer + `UseStaticFiles`). |
| `.agentic/decisions/0006-*.md` (new, via `/decide`) | Record the Web-Speech-API-vs-Azure choice + the third-party-audio privacy trade-off. (0005 is taken by the prerequisite branch.) |
| `.agentic/gotchas.md` | Add an entry only if a real trap surfaces during implementation (secure-context requirement; Chrome continuous+interim quirks) — via `/gotcha`, not pre-emptively. |

## Context & constraints (what the executor can't cheaply re-derive)

- **Engine decision — Web Speech API (browser-native).** Chosen over server-side Azure
  transcription and the Azure Speech SDK. Rationale: zero backend/infra/cost, consumes the
  BCP-47 `preferredLanguage` tag directly as `recognition.lang`, and keeps the repo's
  no-build vanilla-JS rule (CLAUDE.md) intact (no JS dependency). Trade-off the user
  **explicitly accepted**: in Chrome/Edge the audio is sent to the browser vendor's cloud
  (Google), i.e. outside the app's Azure tenant — whereas every other data path stays inside
  the tenant. Note this residency expectation is **de-facto/unwritten**: no ADR states it.
  (ADR 0002 is about Entra Easy Auth *gating* only — do not cite it as a data-residency
  authority.) This is acceptable for a personal, single-user app but is a genuine
  architectural decision → record it as an ADR (`/decide`). Firefox has
  no `SpeechRecognition`; the button is **hidden** there (graceful degradation), not shown-disabled.
- **Secure-context requirement.** `SpeechRecognition` requires a secure context. Prod is
  https (`chat.agent42.nl`); dev via `dotnet run --project tools/Agent42.DevHost` serves at
  `http://localhost:61326`, and `localhost` **is** a secure context — so the feature is fully
  exercisable offline in DevHost with a real mic. No infra needed to test it.
- **Reuse the cached profile, don't re-fetch.** `app.js` already holds `profile` (populated by
  `loadProfile()` at startup and after save). Read `profile.preferredLanguage` at
  recognition-start; do **not** add a fetch. `rec.lang = profile.preferredLanguage || navigator.language`.
- **Programmatic `input.value` writes don't fire the `input` event.** The autosize handler is
  bound via `input.addEventListener("input", autosize)`. When recognition writes the textarea,
  call `autosize()` **directly** after each update, exactly as `send()` does right after it
  sets `input.value = ""`.
- **Stop recognition when a send starts.** `send()` clears the box with `input.value = ""`. If
  recognition is still live, a late `onresult` would repopulate it. Add a `stopSpeech()`
  helper (idempotent) and call it at the top of the submit path / `send()` so a dictated
  prompt is captured cleanly and the mic resets.

> **Line numbers are deliberately omitted below and above.** This plan is anchored on
> pre-merge main, but executes on main *after* the prerequisite merges, which shifts every
> line in `app.js`/`styles.css`/`index.html` below the profile insertions. Locate code by the
> quoted anchor strings (`input.addEventListener("input", autosize)`, `input.value = ""`,
> `.composer__attach`, `@keyframes pulse`, the `.composer__frame` block) — they are stable
> across the shift; raw line numbers are not (hard rule 2).
- **Interim rendering is plain text, not greyed.** The mockup showed greyed interim text;
  true greyed partials are not achievable inside a plain `<textarea>` without a
  contenteditable/overlay, which would fight the vanilla-JS simplicity. Interim text is
  rendered live in the textarea as normal text and finalized in place; the "listening" cue is
  the pulsing mic button (and optionally a transient placeholder/status). A greyed-overlay is
  **out of scope**.
- **Frontend test convention.** The repo has **no JS test runner by design** — see the
  comment in `PwaAssetsTests.ServiceWorker_BypassesApiBeforeCaching` (`tests/Agent42.Tests/PwaAssetsTests.cs`).
  Frontend guardrails are C# source-text heuristics that serve the real `wwwroot` through a
  `TestServer` and assert on presence/position of key strings. These prove wiring exists, not
  runtime behavior; the functional check is manual (DevHost + mic). New tests follow that
  exemplar exactly.
- **No ADR conflict.** ADR 0002 (Easy Auth gate) is unaffected — the mic button is a static
  asset behind the same auth; no `/api` path is added. No active ADR governs the frontend
  speech surface.

## Steps

1. **Composer markup — mic button** (`index.html`). Add a `<button class="composer__mic"
   id="micBtn" type="button" aria-label="Dictate prompt" aria-pressed="false">` with an inline
   mic SVG (match the `#attachBtn`/`#sendBtn` 16×16 svg idiom), placed between `#promptInput`
   and `#sendBtn` in `.composer__frame`. *Verify:* new test `MicButton_IsPresent` (served `/`
   contains `id="micBtn"`); manual — button renders in the composer.
   *(Parallelizable with Step 2 — different files.)*

2. **Styles** (`styles.css`). Add `.composer__mic` mirroring the `.composer__attach` rule for
   the resting state, plus a recording state (e.g. `.composer__mic[aria-pressed="true"]`)
   using amber accent + `animation: pulse …` (reuse the existing `@keyframes pulse`). *Verify:*
   manual visual in DevHost (resting + pulsing while recording). *(Parallelizable with Step 1.)*

3. **Speech wiring** (`app.js`). Depends on Steps 1–2 existing in the DOM. Add a
   `/* ————— speech input ————— */` section near the composer wiring:
   - `const micBtn = document.getElementById("micBtn");` in the refs block.
   - `const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;`
     If falsy → `micBtn.hidden = true;` and return early (no wiring). Firefox path.
   - Build one `recognition` instance: `continuous = true`, `interimResults = true`,
     `lang` set at **start** from `profile.preferredLanguage || navigator.language`.
   - Toggle handler on `micBtn` click: if not recognizing → snapshot `baseText = input.value`
     (append point), set `recognition.lang`, `recognition.start()`; if recognizing →
     `recognition.stop()`.
   - `onresult`: rebuild `final`+`interim` from the results list, set
     `input.value = baseText + final + interim`, call `autosize()`, keep caret at end.
   - `onstart`/`onend`: toggle `aria-pressed` + recording class; on end, `input.focus()` and
     clear the recognizing flag (handles Chrome's silence/timeout auto-stop).
   - `onerror`: reset UI; for `not-allowed`/`service-not-allowed` (mic denied) leave a subtle
     one-shot hint (no intrusive alert).
   - `stopSpeech()` helper (idempotent), called at the top of the submit/`send()` path so a
     dictated prompt is captured and the mic resets before `input.value` is cleared.
   *Verify:* new tests assert app.js source contains `webkitSpeechRecognition`,
   `interimResults`, and `navigator.language`; manual — dictate in DevHost, watch interim
   text stream in, stop, edit, send.

4. **Guardrail tests** (`tests/Agent42.Tests/SpeechInputAssetsTests.cs`, new). Mirror
   `PwaAssetsTests`' `IAsyncLifetime` + `TestServer` bootstrap (`UseDefaultFiles` /
   `UseStaticFiles`, `ResolveBackendDir` walk-up). Facts (source-text heuristics — comment the
   "no JS runner" rationale, as the exemplar does):
   - `MicButton_IsPresent` — served `/` contains `id="micBtn"`.
   - `AppJs_FeatureDetectsSpeechRecognition` — `/app.js` contains `webkitSpeechRecognition`.
   - `AppJs_EnablesInterimResults` — `/app.js` contains `interimResults`.
   - `AppJs_DrivesRecognitionLanguageFromDevice` — `/app.js` contains `navigator.language`.
     (Note: don't assert on `preferredLanguage` alone — the profile branch already ships that
     token three times, so it proves nothing about speech wiring. `navigator.language` is the
     genuinely new token. If a stable assignment idiom is used, additionally assert the source
     contains `.lang =` to bind the language *to recognition*; the heuristic still can't prove
     the runtime value — that's AC4's manual half.)
   *Verify:* `dotnet test Agent42.slnx` (all green); `dotnet format --verify-no-changes`.

5. **Docs (same MR).** Run `/decide` → `.agentic/decisions/0006-speech-input-web-speech-api.md`
   capturing: Web Speech API chosen over Azure server-side transcription and Azure Speech SDK;
   the third-party-audio (Google) privacy trade-off vs the app's otherwise tenant-resident
   data (a de-facto expectation, not an existing ADR — do not attribute it to ADR 0002);
   Firefox-hidden degradation; and why it's frontend-only. Add a `/gotcha` entry **only if** a
   real non-obvious trap surfaced (secure-context requirement; Chrome continuous+interim
   auto-stop). Run `/audit-docs` mentally: no code fact in CLAUDE.md/gotchas is invalidated;
   optionally add a one-line repo-note to CLAUDE.md that voice dictation exists. Then `/sync-docs`
   if any doc claim drifted.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test / proof | File |
|---|----------------------|--------------|------|
| AC1 | A mic button is present in the composer | `MicButton_IsPresent` | `tests/Agent42.Tests/SpeechInputAssetsTests.cs` |
| AC2 | App feature-detects the Web Speech API (and hides the button when absent) | `AppJs_FeatureDetectsSpeechRecognition` + manual (Firefox: hidden; Chrome: shown) | same |
| AC3 | Interim results are enabled (live partial text) | `AppJs_EnablesInterimResults` + manual (text streams while speaking) | same |
| AC4 | Recognition language comes from the saved profile, falling back to device language | `AppJs_DrivesRecognitionLanguageFromDevice` (asserts `navigator.language`, and `.lang =` if that idiom is used) + manual (set `nl-NL`, dictate Dutch) — the profile→lang runtime binding is manual-only | same |
| AC5 | Transcript fills the input for review and does **not** auto-send; a send stops recognition cleanly | manual (dictate → edit → Enter sends; mid-dictation Enter captures then resets) | — (runtime-only; no JS runner) |
| AC6 | Decision + privacy trade-off recorded | ADR 0006 exists and is referenced | `.agentic/decisions/0006-*.md` |

Runtime-only criteria (AC5, and the manual halves above) have no automated test because the
Web Speech API is a browser runtime the repo can't drive headlessly — this matches the
existing frontend-test convention (heuristics + manual), not a coverage gap to paper over.

## Quality gate checklist (copied from `.agentic/quality-gates.yaml`)

- `new_issues: 0`
- `new_security_issues: 0`
- `duplication_pct_max: 3` — the new test class duplicates ~15 lines of `PwaAssetsTests`
  bootstrap; if that trips duplication, extract a shared `StaticAssetsServerFixture` instead.
- `coverage_new_code_pct_min: 80` — the **only new C# is the test class itself**; there is no
  new production C# to cover, and coverlet does not measure JS. Coverage of new code is
  therefore trivially satisfied (document this in the MR so the self-reported gate isn't
  misread as a miss).
- `cognitive_complexity_max: 15` — keep the `onresult` handler flat (a single results-loop
  building final/interim); no nested branching.
- Branch: `feature/speech-input` off the post-merge main; MR required (hard rule 1). Tests
  written alongside (Step 4), never deferred (hard rule 3).

## Out of scope

- Server-side / Azure transcription, Azure Speech SDK, any new Azure resource or model
  deployment, any backend endpoint or grain.
- Greyed interim-text overlay (plain-textarea limitation; see Context).
- Auto-send on end of speech (explicitly review-before-send).
- Text-to-speech / reading answers aloud (this is input only).
- Translating the UI or the prompt; `preferredLanguage` only selects the recognition locale.
- Push-to-talk (hold) control; a tap-to-toggle button is used.

## Review

A fresh-context `critic` reviewed this spec. Verdict: **sound, no blocking findings.** Cleared:
prerequisite claims, ADR-0006 numbering, quality-gate thresholds (verbatim), no active-ADR
contradiction, correct `PwaAssetsTests` exemplar shape, no gotcha tripped, no-build vanilla-JS
rule honored, out-of-scope list consistent. Findings and resolutions:

1. **Line-number drift (should-fix) — FIXED.** Every precise line ref was anchored to the
   pre-merge tree; the prerequisite grows `app.js` 754→846 lines, shifting all refs below the
   profile insertions. Removed raw line numbers throughout in favor of stable quoted anchor
   strings, and added an explicit note that the executor locates code by anchor, not line.
2. **AC4 guardrail half no-op (should-fix) — FIXED.** `preferredLanguage` already ships on the
   profile branch, so asserting it proves nothing about speech wiring. Retargeted the test to
   `navigator.language` (the genuinely new token) plus the `.lang =` idiom; AC4 table updated
   and the profile→lang runtime binding is marked manual-only.
3. **ADR 0002 mischaracterized as data-residency (should-fix) — FIXED.** ADR 0002 governs
   Entra auth gating only. Reworded both mentions: the tenant-residency expectation is
   de-facto/unwritten and must **not** be attributed to ADR 0002 (the new ADR 0006 states it).
4. **`PwaAssetsTests.cs:96` off-by-one (nit) — FIXED.** Dropped the line number from the citation.

## Implementation notes (divergence from the plan)

Executed on `feature/speech-input` off main @ 9e6fc8f (no drift; prerequisite `profile.preferredLanguage`
confirmed present). All 6 ACs met (runtime halves manual, per convention). Divergences:

1. **Shared test fixture extracted (was conditional in the plan).** The plan said extract a
   `StaticAssetsServerFixture` *only if* duplication tripped the gate. The execution `critic` measured
   the real duplication at ~45 lines (not the estimated ~15), so I extracted
   `tests/Agent42.Tests/StaticAssetsServerFixture.cs` — an xUnit `[CollectionDefinition("StaticAssets")]`
   collection fixture — and refactored **both** `SpeechInputAssetsTests` and the existing `PwaAssetsTests`
   onto it. The server is now built once for both classes (test time dropped 45s→15s). This expanded the
   diff into `PwaAssetsTests.cs` (not in the original affected-areas table), a deliberate debt-paydown the
   plan pre-named.
2. **Error-placeholder restore (critic finding 3).** The `onerror` "mic blocked" hint originally never
   restored the `"Transmit a prompt…"` placeholder. Fixed: the original placeholder is snapshotted
   (`defaultPlaceholder`) and restored when a new dictation starts, so the hint is effectively one-shot.
3. **No `/gotcha` added.** No candidate trap cleared the gotchas admission bar ("invisible in the code"):
   the secure-context and Chrome-auto-stop facts live in ADR 0006 + code comments, and the
   `[hidden]`-vs-`display:grid` trap is already documented by an existing global CSS rule. Matches the
   plan's conditional guidance.
4. **No CLAUDE.md repo-note added.** The plan marked it optional; voice dictation is frontend-only with no
   infra surface, and the residency caveat's proper home is ADR 0006 (where "knowledge lives" points).
5. **ADR 0006** written: `.agentic/decisions/0006-speech-input-web-speech-api.md` (AC6). Marked `accepted`,
   references ADR 0005 (the preferred-language prerequisite) and explicitly disclaims ADR 0002 as a
   data-residency authority.
