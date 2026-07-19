# Plan — Move default-tools selection into its own Preferences tab

planned-against: main @ 937c792

## Overview
The Preferences dialog (`#profileDialog`) currently has two tabs — **General** and **Model
providers** — and the "default tools" checkbox group lives at the bottom of the **General**
panel. Move that group into a **new dedicated "Tools" tab** so General holds only language /
theme / location. This is a pure frontend reorganization (vanilla JS + HTML + one asset test);
no backend, endpoint, grain state, or persistence change. The `/api/profile` PUT with
`enabledTools` and the `renderToolChecks`/`readToolChecks` logic are untouched.

## Affected areas
- `src/Agent42.Backend/wwwroot/index.html` — Preferences dialog markup (tablist lines 254–259;
  General panel lines 261–296; default-tools `<section>` lines 291–295; leading comment line 247–249).
- `src/Agent42.Backend/wwwroot/app.js` — the `profileTabs = wireTabs([...])` array (lines 54–57).
- `tests/Agent42.Tests/PwaAssetsTests.cs` — `Preferences_HasGeneralAndProvidersTabs` (lines 276–290).

## Context & constraints
- **`wireTabs` is generic and does all the work.** `wireTabs(tabPairs)` (app.js 1324–1350) takes
  `[tab, panel]` pairs *in tablist order*, wires click + arrow/Home/End keyboard nav, roving
  tabindex, `aria-selected`, and panel `hidden`. Adding a tab = add one HTML button + one panel +
  one array entry. Do **not** hand-roll switching. Exemplar: the agent dialog's 3-tab `agentTabs`
  (app.js 58–62; ADR 0022 API-key tab).
- **The tools container id must not change.** `openProfileDialog` calls
  `renderToolChecks(profileToolChecks, profile.enabledTools ?? allToolKeys())` (app.js 1356) and
  `saveProfile` calls `readToolChecks(profileToolChecks)` (app.js ~1367), both keyed on the element
  `id="profileToolChecks"`. Moving that `<div>` into a different panel keeps the id, so **no JS
  populate/save change is needed** — only the `profileTabs` array grows.
- **`profileTabs.select(0)` still opens on General** (app.js 1359) — keep it; General stays tab 0.
- **Tab order:** place Tools **between** General and Model providers → `General · Tools · Model
  providers`. This mirrors the agent dialog's `General · Tools · API key` order (consistency call;
  not user-specified). The tablist array order in app.js must match the DOM order exactly (indices
  are positional).
- **ARIA shape to copy** (from the existing panels): tab button `class="agent-form__tab"
  type="button" role="tab" id="tabTools" aria-selected="false" aria-controls="panelTools"
  tabindex="-1"`; panel `class="agent-form__panel" role="tabpanel" id="panelTools"
  aria-labelledby="tabTools" hidden`. Every button inside `#profileForm` MUST carry
  `type="button"` or it submits+closes the dialog (index.html comment 247–249).
- **Governing docs:** ADR 0021 (`.agentic/decisions/0021-selectable-tools-three-layer.md`) owns the
  default-tools feature but only the three-layer *model*, not UI placement — this change does not
  touch its semantics, so **no `/decide` is required**. No gotcha covers the Preferences dialog
  layout. The "no JS test runner" reality means the asset tests are **source-text heuristics**
  (see `AgentEditorAssetsTests` header) — they prove the markup/strings exist, not that a browser
  renders them; real tab switching is verified by driving DevHost.
- **CSS:** `.agent-form__tab` / `.agent-form__panel` / `.tools-checks` already exist and are shared;
  no new CSS expected. Confirm during implementation (grep `styles.css`), add none if reused.

## Steps
1. **index.html — add the Tools tab button.** In the Preferences tablist (lines 254–259), insert a
   `tabTools` button between `tabGeneral` and `tabProviders`, copying the ARIA shape above.
   Update the leading comment (247–249) from "(General / Model providers)" to
   "(General / Tools / Model providers)".
   *Verify:* `dotnet build Agent42.slnx` (static assets are embedded/served); visual check in step 4.
2. **index.html — move the default-tools section into a new panel.** Cut the
   `<section>…// default tools…#profileToolChecks…</section>` block (291–295) out of `panelGeneral`
   and place it inside a new `<div … id="panelTools" … hidden>` panel positioned **between**
   `panelGeneral` and `panelProviders`. Keep the `id="profileToolChecks"` div and the note text
   verbatim. (Depends on step 1 for tab/panel id agreement.)
   *Verify:* build; DevHost check in step 4.
3. **app.js — register the third tab.** Add
   `[document.getElementById("tabTools"), document.getElementById("panelTools")]` to the
   `profileTabs` array (54–57) in the position matching DOM order (after General, before Providers).
   No other JS change. (Depends on steps 1–2 for the ids.)
   *Verify:* `dotnet build`; DevHost check in step 4.
4. **Update/rename the asset test.** In `PwaAssetsTests.cs`, extend
   `Preferences_HasGeneralAndProvidersTabs` (rename to `Preferences_HasGeneralToolsAndProvidersTabs`)
   to also assert `id="tabTools"`, `id="panelTools"`, and that the default-tools container
   `id="profileToolChecks"` is present. Add a **source-text ordering assertion** that pins the move:
   `IndexOf("id=\"panelTools\"") < IndexOf("id=\"profileToolChecks\"") < IndexOf("id=\"panelProviders\"")`
   — this fails if the checkbox div is accidentally left in `panelGeneral` (it would sort before
   `panelTools`), giving AC2 real regression coverage instead of relying on manual step 5 alone.
   Update the comment to name three tabs.
   *Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~PwaAssetsTests`.
5. **Manual DevHost verification (not automated — no JS runner).** `dotnet run --project
   tools/Agent42.DevHost`, open Preferences: confirm three tabs, that Tools shows the checkboxes and
   General no longer does, keyboard arrow nav cycles all three, toggling a tool + Save persists
   (reopen → Tools tab reflects it), and the dialog opens on General.

Steps 1→2→3 are sequential (shared ids). Step 4 can be written alongside step 1. Step 5 is last.

## Acceptance criteria → test mapping
| AC | Proof | Test file |
|----|-------|-----------|
| AC1 — Preferences has a dedicated Tools tab + panel | asserts `id="tabTools"`, `id="panelTools"` present | `tests/Agent42.Tests/PwaAssetsTests.cs` (`Preferences_HasGeneralToolsAndProvidersTabs`) |
| AC2 — default-tools checkboxes live in the Tools panel, not General | source-text ordering assertion `panelTools < profileToolChecks < panelProviders` (fails if left in General); browser rendering confirmed manually | same test + step 5 DevHost check |
| AC3 — the third tab is wired via `wireTabs` | app.js `profileTabs` array carries the `tabTools`/`panelTools` pair; existing `Preferences_HasGeneralAndProvidersTabs`→renamed still green, full suite passes | `dotnet test Agent42.slnx` |
| AC4 — save/populate of default tools still works (no regression) | existing profile round-trip unchanged (`id` untouched); DevHost toggle+save+reopen | step 5 manual |

## Quality gate checklist (from `.agentic/quality-gates.yaml` @ plan time)
- new_issues: 0 · new_security_issues: 0
- duplication_pct_max: 3
- coverage_new_code_pct_min: 80 (this change is markup + one array entry + a test; little executable
  new code — the asset test covers the added markup)
- cognitive_complexity_max: 15
- vulnerable_packages: 0 · no new dependencies
- Lint: `dotnet format Agent42.slnx --verify-no-changes` · Build: `dotnet build Agent42.slnx` ·
  Test: `dotnet test Agent42.slnx`
- Branch: `feature/*` + MR (never commit to `main`).

## Out of scope
- The **per-conversation** tools popover (composer "Tools", ADR 0021) and the **per-agent** Tools tab
  (agent dialog) — unchanged.
- Any change to `ToolCatalog`, `/api/tools`, `/api/profile`, `EnabledTools` semantics, or the
  `null == all` sentinel.
- Adding/removing selectable tools, or new CSS beyond reusing existing tab classes.
- Automated browser/JS testing of tab switching (no JS runner in repo by design).

## Review
Critic verdict: **clean pass**, verified against `main @ 937c792` (exact `planned-against` ref). It
confirmed all cited line numbers, that `profileToolChecks` is referenced by id only (re-parenting is
inert to JS), that `wireTabs` is fully positional/generic, that no ADR/gotcha is tripped, and that CSS
(class-based), `sw.js` (`SHELL`/`CACHE` unchanged), and other tests/docs need no change.
- **Adopted:** its one suggestion — a source-text ordering assertion for AC2 (folded into step 4 and
  the AC table) so the checkbox-div move has automated regression coverage, not just manual step 5.
- **Noted (immaterial):** the `readToolChecks(profileToolChecks)` call is at app.js:1372, not "~1367"
  (the `~` already made this approximate).

## Implementation notes (divergence from plan)

Executed on branch `feature/plan-preferences-tools-tab` from `main @ 937c792` (zero drift — HEAD
matched `planned-against` exactly). **The plan was followed verbatim; no material divergence.**

- Steps 1–4 landed exactly as written. Final diff: `index.html` (+6/−1: comment, tab button, panel
  reparent), `app.js` (+1: one `profileTabs` entry), `PwaAssetsTests.cs` (+21/−4: renamed test with
  new-id + ordering assertions). No CSS change was needed (`.agent-form__tab`/`__panel`/`.tools-checks`
  reused, as predicted).
- **Validation evidence:** `dotnet build` 0 errors; full suite **366 passed / 0 failed**; PwaAssetsTests
  filtered 19/19; `dotnet format --verify-no-changes` clean. Quality gate: PASS (coverage N/A — no new
  C# product code; duplication SKIPPED — no tool in repo).
- **Critic:** clean pass. It noted one *pre-existing, out-of-scope* stale comment in the AgentEditor
  test (`PwaAssetsTests.cs:316`, "Two panels…" for the now-3-tab agent dialog) — untouched by this MR,
  left as-is.
- **DevHost smoke check:** the real backend served `index.html` with the correct DOM order
  (`tabGeneral → tabTools → tabProviders`; `panelGeneral → panelTools[profileToolChecks] →
  panelProviders`). **Interactive browser checks** from plan step 5 — click switching, arrow/Home/End
  keyboard nav across all three tabs, and toggle-tool→Save→reopen round-trip — remain a **manual human
  step** (no JS runner in repo; curl cannot exercise them). The generic, already-proven `wireTabs` +
  the identical `agentTabs` 3-tab exemplar make these low-risk.
- No `/decide` (ADR 0021 semantics untouched) and no `/gotcha` (no new trap) were warranted, matching
  the plan's governing-docs analysis.
